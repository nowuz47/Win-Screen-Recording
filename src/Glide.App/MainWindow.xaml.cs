using System.Diagnostics;
using System.Runtime.InteropServices;
using Glide.Core;
using Glide.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Glide.App;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public nint Window; public uint Flags, Count, Timeout; }
    private readonly string projectRoot;
    private readonly ProjectStore store;
    private readonly DispatcherQueueTimer timer;
    private readonly RecordingHotkey hotkey;
    private MediaPlayer player = new() { AutoPlay = false };
    private MediaRenderer? renderer;
    private EditHistory? edits;
    private CursorTrack cursors = new([]);
    private CancellationTokenSource? previewRequest, exportCancellation;
    private readonly SemaphoreSlim previewGate = new(1, 1);
    private bool loadingProject;
    private long previewVersion;
    private string? exportedPath;
    private NativeCapture? capture;
    private bool busy, closeRequested, canClose;
    private string? currentId;
    private Task? finishingRecording;

    internal bool ActivateExisting()
    {
        if (closeRequested || canClose) return false;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
            presenter.Restore();
        Activate();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!SetForegroundWindow(handle))
        {
            // Windows may deny foreground changes. Use its taskbar attention cue,
            // never topmost toggles or cross-thread input attachment to bypass it.
            var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = handle, Flags = 3, Count = 3, Timeout = 0 };
            FlashWindowEx(ref flash);
        }
        return true;
    }

    public MainWindow()
    {
        InitializeComponent();
        InitializeRange();
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double dpiScale = Math.Max(1, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);
        int width = Math.Min((int)(1120 * dpiScale), workArea.Width - 32);
        int height = Math.Min((int)(820 * dpiScale), workArea.Height - 32);
        AppWindow.MoveAndResize(new(workArea.X + (workArea.Width - width) / 2, workArea.Y + (workArea.Height - height) / 2, width, height));
        projectRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glide", "projects");
        store = new(projectRoot);
        Preview.SetMediaPlayer(player);
        hotkey = new(WinRT.Interop.WindowNative.GetWindowHandle(this), () => DispatcherQueue.TryEnqueue(async () => await FinishRecording()));
        if (!hotkey.Available) HotkeyHint.Text = "단축키가 사용 중입니다. 이 창의 종료 버튼을 사용하세요.";
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(250); timer.Tick += Tick;
        AppWindow.Closing += async (_, e) =>
        {
            if (canClose) return;
            closeRequested = true;
            if (busy || loadingProject || capture is not null)
            {
                e.Cancel = true; closeRequested = true; exportCancellation?.Cancel();
                if (!busy) await FinishRecording();
            }
        };
        Closed += (_, _) => { previewRequest?.Cancel(); exportCancellation?.Cancel(); timer.Stop(); DisposeModes(); hotkey.Dispose(); player.Dispose(); capture?.Dispose(); var previous = renderer; renderer = null; _ = Task.Run(() => previous?.Dispose()); };
        InitializeModes();
        LoadSources();
        _ = LoadAudioDevices();
        _ = LoadLibrary();
    }

    private void Show(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    { Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true; }

    private void CloseWhenIdle()
    {
        if (!canClose && closeRequested && !busy && !loadingProject && capture is null)
        { canClose = true; Close(); }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        UpdateControls();
    }

    private void UpdateControls()
    {
        bool blocked = busy || loadingProject;
        if (blocked || renderer is null) { startRangeDrag?.Cancel(); endRangeDrag?.Cancel(); }
        BusyIndicator.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = !blocked && capture is null && Sources.SelectedItem is not null;
        StopButton.IsEnabled = !blocked && capture is not null;
        Sources.IsEnabled = Refresh.IsEnabled = Projects.IsEnabled = !blocked && capture is null && !IsPresenting;
        AudioCaptureSettings.IsEnabled = !blocked && !loadingAudioDevices && capture is null && !IsPresenting;
        HomeButton.IsEnabled = HomeStartButton.IsEnabled = LibraryButton.IsEnabled = !blocked && capture is null && !IsPresenting;
        BackButton.IsEnabled = CanNavigate && backHistory.Count > 0;
        ForwardButton.IsEnabled = CanNavigate && forwardHistory.Count > 0;
        if (initialized) UpdatePresentationControls();
        Editor.IsEnabled = renderer is not null && capture is null;
        InspectorControls.IsEnabled = !blocked && renderer is not null;
        Playhead.IsEnabled = CutControls.IsEnabled = MotionControls.IsEnabled = ExportButton.IsEnabled = !blocked;
        RangeTrack.IsHitTestVisible = !blocked && renderer is not null;
        RangeStart.IsEnabled = RangeEnd.IsEnabled = !blocked && renderer is not null;
        UndoButton.IsEnabled = edits?.CanUndo == true;
        RedoButton.IsEnabled = edits?.CanRedo == true;
    }

    private void LoadSources()
    {
        var selected = Sources.SelectedItem as CaptureTarget;
        var targets = CaptureTargets.List(); Sources.ItemsSource = targets; RefreshOutputs(targets);
        Sources.SelectedItem = selected is null ? null : targets.FirstOrDefault(x => x.Handle == selected.Handle && x.IsMonitor == selected.IsMonitor);
        StartButton.IsEnabled = !busy && !loadingProject && capture is null && Sources.SelectedItem is not null;
    }
    private async void RefreshSources(object sender, RoutedEventArgs e) { LoadSources(); await LoadAudioDevices(); }
    private void SourceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (StartButton is null) return;
        StartButton.IsEnabled = !busy && !loadingProject && capture is null && Sources.SelectedItem is CaptureTarget;
        if (initialized) _ = RefreshSourcePreview();
    }

    private async Task LoadLibrary(string? selectedId = null)
    {
        try
        {
            var projects = await Task.Run(() => store.List().OrderByDescending(p => Directory.GetLastWriteTimeUtc(store.ProjectPath(p.Id))).ToArray());
            Projects.ItemsSource = projects; EmptyLibrary.Visibility = projects.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (selectedId is not null) Projects.SelectedItem = projects.FirstOrDefault(p => p.Id == selectedId);
        }
        catch (Exception e) { Show($"저장된 녹화 목록을 읽지 못했습니다. ({e.HResult:X8})", InfoBarSeverity.Error); }
    }

    private async void StartRecording(object sender, RoutedEventArgs e)
    {
        if (busy || loadingProject || capture is not null || Sources.SelectedItem is not CaptureTarget target) return;
        StopAudioTest(); SetBusy(true); StopPlayback(); Elapsed.Text = "00:00"; recordingPaused = false;
        var selectedAudio = SelectedAudio;
        using var cancellation = countdown = new CancellationTokenSource();
        CancelCountdownButton.Visibility = Visibility.Visible;
        currentId = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(store.ProjectPath(currentId), "capture");
        try
        {
            for (int i = 3; i > 0; i--)
            {
                ShowCountdown(i);
                await Task.Delay(1000, cancellation.Token);
                if (closeRequested) return;
            }
            HideCountdown(); Show("녹화를 준비하고 있습니다.");
            var previous = presentation; presentation = null;
            if (previous is not null) await Task.Run(previous.Dispose);
            presentation = await Task.Run(() => NativePresentation.Create(target, null));
            presentationState.End(); presentationState.Prepare(); presentation.Command(1); presentationState.Begin();
            StartCamera(); RegisterPresentationHotkeys(); liveTimer?.Start();
            var live = presentation;
            capture = await Task.Run(() => live.Record(directory, selectedAudio));
            presentationState.StartRecording();
            timer.Start(); SetBusy(false);
            SwitchSurface(LivePanel);
            BeginLiveButton.Visibility = LiveRecordButton.Visibility = Visibility.Collapsed;
            ZoomLiveButton.Visibility = LockLiveButton.Visibility = CoverLiveButton.Visibility = EndLiveButton.Visibility = Visibility.Visible;
            LiveStatus.Text = "녹화 중"; LiveHint.Text = "종료하면 원본과 커서가 저장되고 편집기로 이동합니다.";
            OpenToolbar(true);
            Show("녹화 중입니다. 종료하면 이 PC에 저장됩니다.", InfoBarSeverity.Success);
            if (closeRequested) { await FinishRecording(); return; }
            if (hotkey.Available && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
        }
        catch (OperationCanceledException) { Show("녹화 시작을 취소했습니다."); }
        catch (Exception ex)
        {
            capture?.Dispose(); capture = null;
            ReleaseCamera();
            Show($"녹화를 시작하지 못했습니다. 대상을 다시 선택해 주세요. ({ex.HResult:X8})", InfoBarSeverity.Error);
        }
        finally
        {
            countdown = null; HideCountdown(); CancelCountdownButton.Visibility = Visibility.Collapsed;
            SetBusy(false);
            CloseWhenIdle();
        }
    }

    private async void StopRecording(object sender, RoutedEventArgs e) => await FinishRecording();

    private async Task FinishRecording()
    {
        if (finishingRecording is not null) { await finishingRecording; return; }
        if (busy || capture is null || currentId is null) return;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        finishingRecording = completed.Task;
        try { await FinishRecordingCore(); }
        finally { finishingRecording = null; completed.TrySetResult(); }
    }

    private async Task FinishRecordingCore()
    {
        bool wasPresenting = IsPresenting && mode == WorkspaceMode.Presentation;
        if (!wasPresenting) liveTimer?.Stop();
        SetBusy(true); timer.Stop();
        if (!wasPresenting && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        if (!wasPresenting)
        {
            toolbar?.Dispose(); toolbar = null; ActivateExisting();
            LiveStatus.Text = "녹화를 저장하고 있습니다.";
            LiveHint.Text = "파일 확인을 마치면 편집기로 이동합니다.";
        }
        Show("영상을 저장하고 파일을 확인하고 있습니다.");
        var recording = capture!; var id = currentId!;
        try
        {
            int result = await Task.Run(recording.Stop);
            recording.Dispose(); capture = null;
            if (!wasPresenting) ReleaseCamera();
            string directory = Path.Combine(store.ProjectPath(id), "capture");
            string name = $"녹화 {DateTime.Now:yyyy-MM-dd HH.mm.ss}";
            var document = await Task.Run(() => CaptureImport.Import(store, id, directory, name, NativeCapture.VerifyVideo));
            if (!closeRequested)
            {
                if (wasPresenting) { lastPresentationProject = document.Id; await LoadLibrary(); }
                else { await LoadLibrary(document.Id); LoadSources(); }
                bool recovered = result < 0 || document.Recovered;
                Show(recovered ? "녹화가 중단되거나 일부 원본이 손상되어 저장 가능한 구간을 복구했습니다." : "녹화를 저장했습니다. 편집기에서 확인해 보세요.", recovered ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            }
        }
        catch (Exception e)
        {
            Show($"저장을 완료하지 못했습니다. 원본 파일은 보존되어 있습니다. 저장 폴더를 확인해 주세요. ({e.HResult:X8})", InfoBarSeverity.Error);
        }
        finally
        {
            recording.Dispose(); capture = null; recordingPaused = false;
            if (wasPresenting) presentationState.StopRecording(); else ReleaseCamera();
            SetBusy(false);
            CloseWhenIdle();
        }
    }

    private void ReleaseCamera()
    {
        liveTimer?.Stop(); presentationState.End(); liveCamera.Reset();
        foreach (var key in presentationHotkeys) key.Dispose(); presentationHotkeys.Clear();
        presentation?.Dispose(); presentation = null;
    }

    private void Tick(DispatcherQueueTimer sender, object args)
    {
        if (capture is null || busy) return;
        try
        {
            var stats = capture.GetStats();
            Elapsed.Text = TimeSpan.FromMicroseconds(stats.DurationUs).ToString(@"mm\:ss");
            LiveRecordingStatus.Text = $"● 녹화 {(recordingPaused || (IsPresenting && presentationState.State != PresentationState.Live) ? "일시정지" : "중")} · {Elapsed.Text}";
            if (!IsPresenting) toolbar?.Status($"녹화 {(recordingPaused ? "일시정지 · " : "")}{Elapsed.Text}");
            if (stats.State != 1) _ = FinishRecording();
        }
        catch (Exception e) { Show($"녹화 상태를 확인하지 못했습니다. ({e.HResult:X8})", InfoBarSeverity.Error); _ = FinishRecording(); }
    }

    private async void ProjectSelected(object sender, SelectionChangedEventArgs e)
    {
        if (closeRequested || Projects.SelectedItem is not ProjectDocument selected || capture is not null || IsPresenting) return;
        loadingProject = true; previewRequest?.Cancel(); StopPlayback(); player.Source = null;
        var previous = renderer; renderer = null; edits = null; EditorPreview.Source = null;
        _ = Task.Run(() => previous?.Dispose()); UpdateControls();
        try
        {
            var opened = await Task.Run(() =>
            {
                var document = store.Load(selected.Id);
                var pointers = CaptureImport.ReadPointers(Path.Combine(store.ProjectPath(document.Id), "capture"), document.DurationUs);
                var track = new CursorTrack(pointers.Samples, pointers.Clicks);
                return (Document: document, Track: track, Renderer: CreateRenderer(document, track));
            });
            var old = renderer; renderer = opened.Renderer; cursors = opened.Track; edits = new(opened.Document);
            _ = Task.Run(() => old?.Dispose());
            Navigate(WorkspaceMode.Editing); ResetEditor();
        }
        catch (Exception ex) { Show($"편집기를 열지 못했습니다. 원본은 보존되어 있습니다. ({ex.HResult:X8})", InfoBarSeverity.Error); }
        finally
        {
            loadingProject = false; UpdateControls();
            CloseWhenIdle();
        }
    }

    private void ResetEditor()
    {
        if (renderer is null || edits is null) return;
        var document = edits.Current;
        PreviewTitle.Text = document.Name;
        PreviewDetails.Text = $"{TimeSpan.FromMicroseconds(renderer.Plan.DurationUs):mm\\:ss} · 확대 {document.Zooms.Length}개 · {(document.Recovered ? "복구된 녹화 · " : "")}변경 사항 저장됨";
        Playhead.Maximum = Math.Max(0, (renderer.Plan.DurationUs - 1) / 1_000_000.0);
        Playhead.Value = Math.Min(Playhead.Value, Playhead.Maximum);
        double durationSeconds = renderer.Plan.DurationUs / 1_000_000.0;
        // Raise bounds before assigning values: a previous shorter project can otherwise clamp the new selection.
        CutStart.Maximum = CutEnd.Maximum = durationSeconds;
        CutStart.Value = 0; CutEnd.Value = durationSeconds;
        exportedPath = null; Preview.Visibility = Visibility.Collapsed; EditorPreview.Visibility = Visibility.Visible;
        string sound = edits.Current.Audio?.Any(t => t.Enabled && t.Gain > 0) == true ? "소리 포함 MP4" : "무음 MP4";
        ExportStatus.Text = $"{renderer.Plan.Width} × {renderer.Plan.Height} · 30fps · {sound}";
        RefreshInspector(); RebuildZoomBlocks(); DrawRange();
        UpdateControls(); QueuePreview();
    }

    private void SeekEdited(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (renderer is null || busy || advancingPlayback) return;
        StopPlayback(); Preview.Visibility = Visibility.Collapsed; EditorPreview.Visibility = Visibility.Visible;
        QueuePreview();
    }

    private async void QueuePreview()
    {
        if (renderer is null) return;
        previewRequest?.Cancel(); previewRequest?.Dispose();
        var request = previewRequest = new CancellationTokenSource(); var token = request.Token;
        long version = ++previewVersion;
        var snapshot = renderer;
        long timeUs = Math.Clamp((long)Math.Round(Playhead.Value * 1_000_000), 0, snapshot.Plan.DurationUs - 1);
        PlayheadText.Text = TimeSpan.FromMicroseconds(timeUs).ToString(@"mm\:ss\.ff");
        bool entered = false;
        try
        {
            await Task.Delay(150, token);
            await previewGate.WaitAsync(token); entered = true;
            var pixels = await Task.Run(() => snapshot.Preview(timeUs, compareOriginal), token);
            if (token.IsCancellationRequested || version != previewVersion || renderer != snapshot) return;
            var bitmap = new WriteableBitmap(snapshot.Plan.Width, snapshot.Plan.Height);
            using (var stream = bitmap.PixelBuffer.AsStream()) await stream.WriteAsync(pixels, token);
            if (token.IsCancellationRequested || version != previewVersion) return;
            bitmap.Invalidate(); EditorPreview.Source = bitmap; EditorFrameReady();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) Show($"편집 프레임을 표시하지 못했습니다. ({ex.HResult:X8})", InfoBarSeverity.Error); }
        finally { if (entered) previewGate.Release(); }
    }

    private TimeRange Selection()
    {
        if (renderer is null || !double.IsFinite(CutStart.Value) || !double.IsFinite(CutEnd.Value)) throw new ArgumentException();
        var range = new TimeRange((long)Math.Round(CutStart.Value * 1_000_000), (long)Math.Round(CutEnd.Value * 1_000_000));
        range.Validate();
        if (range.EndUs > renderer.Plan.DurationUs) throw new ArgumentException();
        return range;
    }

    private async Task ChangeEdit(Action<EditHistory> change)
    {
        if (busy || loadingProject || edits is null || renderer is null) return;
        SetBusy(true); previewRequest?.Cancel(); StopPlayback();
        try
        {
            await Task.Run(() => change(edits));
            var next = await Task.Run(() => CreateRenderer(edits.Current, cursors));
            var old = renderer; renderer = next; _ = Task.Run(old.Dispose); ResetEditor();
            Notice.IsOpen = false;
        }
        catch (ArgumentException) { Show("시작과 끝을 확인해 주세요. 최소 한 구간은 남겨야 합니다.", InfoBarSeverity.Warning); }
        catch (Exception ex)
        {
            if (renderer is not null && edits.Current.Revision != renderer.Plan.AtTime(0).Revision)
            {
                var invalid = renderer; renderer = null; _ = Task.Run(invalid.Dispose);
            }
            Show($"편집을 완료하지 못했습니다. 프로젝트를 다시 열어 확인해 주세요. ({ex.HResult:X8})", InfoBarSeverity.Error);
        }
        finally { SetBusy(false); CloseWhenIdle(); }
    }

    private async void DeleteSelection(object sender, RoutedEventArgs e)
    {
        try { var range = Selection(); await ChangeEdit(h => h.Apply(p => p with { Ranges = new Timeline(p.Ranges).Remove(range).Ranges.ToArray() }, store.Save)); }
        catch (ArgumentException) { Show("삭제할 시작과 끝을 확인해 주세요.", InfoBarSeverity.Warning); }
    }
    private async void KeepSelection(object sender, RoutedEventArgs e)
    {
        try
        {
            var range = Selection();
            await ChangeEdit(h => h.Apply(p =>
            {
                var timeline = new Timeline(p.Ranges);
                if (range.EndUs < timeline.DurationUs) timeline = timeline.Remove(new(range.EndUs, timeline.DurationUs));
                if (range.StartUs > 0) timeline = timeline.Remove(new(0, range.StartUs));
                return p with { Ranges = timeline.Ranges.ToArray() };
            }, store.Save));
        }
        catch (ArgumentException) { Show("남길 구간의 시작과 끝을 확인해 주세요.", InfoBarSeverity.Warning); }
    }
    private async void AddZoom(object sender, RoutedEventArgs e)
    {
        if (renderer is null || edits is null) return;
        long sourceUs = renderer.Plan.AtTime(Math.Clamp((long)(Playhead.Value * 1_000_000), 0, renderer.Plan.DurationUs - 1)).SourceUs;
        var focus = cursors.At(sourceUs, true) is { Visible: true } pointer ? pointer.Position : new PointD(.5, .5);
        var range = new TimeRange(Math.Max(0, sourceUs - 300_000), Math.Min(edits.Current.DurationUs, sourceUs + 1_800_000));
        await ChangeEdit(h => h.Apply(p => p with { Zooms = p.Zooms.Where(z => z.Range.EndUs <= range.StartUs || z.Range.StartUs >= range.EndUs)
            .Append(new ZoomSegment(Guid.NewGuid().ToString("N"), range, focus, 1.8, true)).OrderBy(z => z.Range.StartUs).ToArray() }, store.Save));
    }
    private async void ResetZoom(object sender, RoutedEventArgs e) => await ChangeEdit(h => h.Apply(p => p with { Zooms = [], UsePresentationCamera = false }, store.Save));
    private async void UndoEdit(object sender, RoutedEventArgs e) => await ChangeEdit(h => h.Undo(store.Save));
    private async void RedoEdit(object sender, RoutedEventArgs e) => await ChangeEdit(h => h.Redo(store.Save));
    private void CancelExport(object sender, RoutedEventArgs e) => exportCancellation?.Cancel();

    private async void ExportVideo(object sender, RoutedEventArgs e)
    {
        if (busy || loadingProject || renderer is null || edits is null) return;
        SetBusy(true); previewRequest?.Cancel(); StopPlayback();
        using var cancellation = exportCancellation = new CancellationTokenSource();
        CancelExportButton.Visibility = Visibility.Visible;
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker { CommitButtonText = "여기에 저장", SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var destination = await picker.PickSingleFolderAsync();
            if (destination is null) { ExportStatus.Text = "저장 위치 선택을 취소했습니다."; return; }
            cancellation.Token.ThrowIfCancellationRequested();
            string output = Path.Combine(destination.Path, $"Glide-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.mp4");
            var snapshot = renderer; int lastPercent = -1;
            var result = await Task.Run(() => snapshot.Export(output, cancellation.Token, p =>
            {
                int percent = (int)(p.Completed * 100 / p.Total);
                if (percent == lastPercent) return; lastPercent = percent;
                DispatcherQueue.TryEnqueue(() => ExportStatus.Text = percent == 100 ? "저장 파일 확인 중…" : $"MP4 만드는 중 · {percent}%");
            }));
            exportedPath = result.Path;
            Preview.Visibility = Visibility.Collapsed; EditorPreview.Visibility = Visibility.Visible;
            ExportStatus.Text = "저장 완료 · 선택한 폴더에 MP4를 저장했습니다.";
            ShowSaved("MP4를 저장했습니다.", result.Path);
            if (result.ClippedAudioSamples > 0) Show("소리가 너무 커 일부 음이 잘렸습니다. 소리 볼륨을 낮춘 뒤 다시 저장해 주세요.", InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException) { ExportStatus.Text = "저장을 취소했습니다."; Show("MP4 저장을 취소했습니다. 편집과 원본은 보존됩니다."); }
        catch (Exception ex) { ExportStatus.Text = "MP4 저장 실패"; Show($"MP4를 저장하지 못했습니다. ({ex.HResult:X8})", InfoBarSeverity.Error); }
        finally
        {
            exportCancellation = null; CancelExportButton.Visibility = Visibility.Collapsed; SetBusy(false);
            CloseWhenIdle();
        }
    }

    private void OpenFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = exportedPath is null ? projectRoot : Path.GetDirectoryName(exportedPath)!, UseShellExecute = true }); }
        catch (Exception ex) { Show($"저장 폴더를 열지 못했습니다. ({ex.HResult:X8})", InfoBarSeverity.Error); }
    }
}
