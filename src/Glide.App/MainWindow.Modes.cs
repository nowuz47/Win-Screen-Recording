using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Glide.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Glide.App;

public sealed partial class MainWindow
{
    private WorkspaceMode mode = WorkspaceMode.Home;
    private readonly PresentationSession presentationState = new();
    private readonly LiveCamera liveCamera = new();
    private readonly List<RecordingHotkey> presentationHotkeys = [];
    private readonly SemaphoreSlim snapshotGate = new(1, 1);
    private NativePresentation? presentation;
    private SessionToolbar? toolbar;
    private DispatcherQueueTimer? liveTimer;
    private CancellationTokenSource? countdown;
    private bool initialized, recordingPaused, presentationOperation;
    private bool sharingOutputOffscreen;
    private long sourcePreviewVersion;
    private string? lastPresentationProject;
    private List<CaptureTarget?> outputTargets = [null];
    private static long NowUs => (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));
    private static string PreferencesPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glide", "preferences.json");

    private void InitializeModes()
    {
        initialized = false;
        foreach (var scale in new[] { LiveMagnification, ZoomScale, CursorScaleValue })
            scale.NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
            {
                IntegerDigits = 1, FractionDigits = 1,
                NumberRounder = new Windows.Globalization.NumberFormatting.IncrementNumberRounder { Increment = 0.1 }
            };
        try
        {
            if (File.Exists(PreferencesPath))
            {
                preferences = UiPreferences.Load(PreferencesPath);
                ThemeChoice.SelectedIndex = preferences.Theme;
                UiMotionChoice.SelectedIndex = (int)preferences.Motion;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Show("화면 설정을 기본값으로 열었습니다."); }
        InitializeMotion(); InitializeShell(); initialized = true;
        foreach (var seconds in new[] { CutStart, CutEnd, ZoomStart, ZoomEnd })
            seconds.NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
            { IntegerDigits = 1, FractionDigits = 2, NumberRounder = new Windows.Globalization.NumberFormatting.IncrementNumberRounder { Increment = .01 } };
        liveTimer = DispatcherQueue.CreateTimer(); liveTimer.Interval = TimeSpan.FromMilliseconds(33); liveTimer.Tick += LiveTick;
        Root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && countdown is not null) { countdown.Cancel(); e.Handled = true; }
        };
        Navigate(WorkspaceMode.Home);
    }
    private void RegisterPresentationHotkeys()
    {
        foreach (var key in presentationHotkeys) key.Dispose(); presentationHotkeys.Clear();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        presentationHotkeys.Add(new(hwnd, () => ToggleLiveZoom(this, new()), 0x78, 0x4801));
        presentationHotkeys.Add(new(hwnd, () => ToggleLiveLock(this, new()), 0x79, 0x4802));
        presentationHotkeys.Add(new(hwnd, () => ToggleLiveCover(this, new()), 0x7A, 0x4803));
        presentationHotkeys.Add(new(hwnd, () => EndPresentation(this, new()), 0x7B, 0x4804));
        presentationHotkeys.Add(new(hwnd, () => AdjustLiveMagnification(.1), 0x26, 0x4805));
        presentationHotkeys.Add(new(hwnd, () => AdjustLiveMagnification(-.1), 0x28, 0x4806));
    }
    private void DisposeModes()
    {
        countdown?.Cancel(); liveTimer?.Stop(); StopAudioTest(); StopPlayback();
        foreach (var key in presentationHotkeys) key.Dispose();
        toolbar?.Dispose(); presentation?.Dispose(); DisposeMotion();
    }
    private void OpenToolbar(bool presenting)
    {
        toolbar?.Dispose();
        toolbar=new(motionPolicy!, presenting,()=>ActivateExisting(),
            ()=>ToggleLiveZoom(this,new()),()=>ToggleLiveLock(this,new()),()=>ToggleLiveCover(this,new()),
            ()=>StartLiveRecording(this,new()),()=>PauseRecording(this,new()),()=>_ = FinishRecording(),()=>_ = FinishPresentation());
        toolbar.Activate();
        UpdatePresentationControls();
        if(!toolbar.ExcludedFromCapture)Show("컨트롤 바를 캡처에서 숨기지 못했습니다. 발표에는 Glide Presentation 창을 선택해 주세요.",InfoBarSeverity.Warning);
    }
    private void Navigate(WorkspaceMode next, bool remember = true)
    {
        if (next != mode && remember) { backHistory.Push(mode); forwardHistory.Clear(); }
        StopAudioTest(); StopPlayback();
        mode = next; Notice.IsOpen = false;
        SavedFeedback.Visibility = Visibility.Collapsed;
        SwitchSurface(next == WorkspaceMode.Home ? HomePanel : next == WorkspaceMode.Library ? LibraryPanel : next == WorkspaceMode.Editing ? EditorSurface : SetupSurface, next == WorkspaceMode.Home);
        PresentationSetup.Visibility = Visibility.Visible;
        OutputChoice.Visibility = RecordWithPresentation.Visibility = next == WorkspaceMode.Presentation ? Visibility.Visible : Visibility.Collapsed;
        SetupModeIcon.Glyph = next == WorkspaceMode.Presentation ? "\uE7F4" : "\uE714";
        SetupTitle.Text = next == WorkspaceMode.Presentation ? "지금 보여줄 화면을 선택하세요." : "녹화할 화면을 선택하세요.";
        SetupDescription.Text = next == WorkspaceMode.Presentation ? "원본을 조작하면 별도 발표 창에 확대된 화면이 표시됩니다." : "원본은 이 PC에 저장되고, 확대는 나중에 수정할 수 있습니다.";
        SetCommand(StartButton, next == WorkspaceMode.Presentation ? "발표 화면 열기" : "녹화 시작", next == WorkspaceMode.Presentation ? "\uE768" : "\uE714");
        if (next is WorkspaceMode.Presentation or WorkspaceMode.Recording) LoadSources();
        UpdateControls();
    }
    private void GoHome(object sender, RoutedEventArgs e)
    {
        if (busy || loadingProject || capture is not null || IsPresenting) return;
        StopPlayback(); Navigate(WorkspaceMode.Home);
    }
    private bool IsPresenting => presentation is not null && presentationState.State is not (PresentationState.Idle or PresentationState.Ended);
    private void OpenRecordingMode(object sender, RoutedEventArgs e) { if (!busy && !IsPresenting) SelectMode(WorkspaceMode.Recording); }
    private void OpenPresentationMode(object sender, RoutedEventArgs e) { if (!busy && !IsPresenting) SelectMode(WorkspaceMode.Presentation); }
    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Root is null || ThemeChoice is null) return;
        Root.RequestedTheme = ThemeChoice.SelectedIndex switch { 1 => ElementTheme.Light, 2 => ElementTheme.Default, _ => ElementTheme.Dark };
        if (!initialized) return;
        preferences.Theme = ThemeChoice.SelectedIndex;
        SavePreferences();
    }
    private void LayoutChanged(object sender, SizeChangedEventArgs e)
    {
        if (InspectorColumn is null) return;
        uiMotion?.CancelAll();
        // Bound the setup before measuring source labels inside the scrolling view.
        // Long window titles must never expand the panel beyond the viewport.
        double contentWidth = Math.Min(900, Math.Max(0, e.NewSize.Width - Root.Padding.Left - Root.Padding.Right));
        SetupContent.Width = LiveContent.Width = contentWidth;
        // Keep the preview usable on a high-DPI work area; every inspector control remains scrollable.
        InspectorColumn.Width = new GridLength(e.NewSize.Width < 1000 ? 230 : 270);
    }
    private void StartMode(object sender, RoutedEventArgs e)
    {
        var magnification = NumberBoxInput.Read(LiveMagnification);
        if (magnification is null || magnification < 1.2 || magnification > 2)
        { Show("확대 배율은 1.2~2.0 사이의 숫자로 입력하세요.", InfoBarSeverity.Warning); return; }
        LiveMagnification.Value = magnification.Value;
        if (mode == WorkspaceMode.Presentation) PreparePresentation(sender, e); else StartRecording(sender, e);
    }
    private void CancelCountdown(object sender, RoutedEventArgs e) => countdown?.Cancel();

    private async Task RefreshSourcePreview()
    {
        long version = ++sourcePreviewVersion;
        uiMotion?.Cancel(SourcePreview); SourcePreview.Source = null; SourcePreviewHint.Visibility = Visibility.Visible;
        if (Sources.SelectedItem is not CaptureTarget source) { SourcePreviewHint.Text = "대상을 선택하면 미리보기가 표시됩니다."; return; }
        SourcePreviewHint.Text = "미리보기 준비 중…";
        await snapshotGate.WaitAsync();
        try
        {
            if (version != sourcePreviewVersion) return;
            var snapshot = await Task.Run(() => CaptureSnapshot.Take(source));
            if (version != sourcePreviewVersion) return;
            SourcePreview.Source = await Bitmap(snapshot); SourcePreviewHint.Visibility = Visibility.Collapsed;
            if (currentSurface == SetupSurface) uiMotion?.Enter(SourcePreview, MotionTokens.Preview, y: 0, scale: .985f);
        }
        catch (Exception) { if (version == sourcePreviewVersion) SourcePreviewHint.Text = "미리보기를 표시하지 못했습니다. 대상 창이 열려 있는지 확인하세요."; }
        finally { snapshotGate.Release(); }
    }
    private static async Task<WriteableBitmap> Bitmap(CaptureSnapshot snapshot)
    {
        var bitmap = new WriteableBitmap(snapshot.Width, snapshot.Height);
        using var stream = bitmap.PixelBuffer.AsStream(); await stream.WriteAsync(snapshot.Pixels); bitmap.Invalidate(); return bitmap;
    }
    private void RefreshOutputs(IReadOnlyList<CaptureTarget> targets)
    {
        var selected = OutputChoice.SelectedIndex >= 0 && OutputChoice.SelectedIndex < outputTargets.Count ? outputTargets[OutputChoice.SelectedIndex] : null;
        outputTargets = [null, .. targets.Where(t => t.IsMonitor)];
        OutputChoice.ItemsSource = outputTargets.Select(t => t is null ? "회의 앱에서 발표 창 공유" : $"{t.Label}에 전체 화면").ToArray();
        OutputChoice.SelectedIndex = Math.Max(0, outputTargets.FindIndex(t => t?.Handle == selected?.Handle));
    }
    private async void PreparePresentation(object sender, RoutedEventArgs e)
    {
        if (busy || capture is not null || Sources.SelectedItem is not CaptureTarget target) return;
        var destination = OutputChoice.SelectedIndex >= 0 ? outputTargets[OutputChoice.SelectedIndex] : null;
        if (target.IsMonitor && destination?.Handle == target.Handle)
        { Show("전체 화면 출력은 입력 화면과 다른 모니터를 선택하세요. 회의 앱 공유용 발표 창도 사용할 수 있습니다.", InfoBarSeverity.Warning); return; }
        StopAudioTest(); SetBusy(true);
        try
        {
            var previous = presentation; presentation = null; if (previous is not null) await Task.Run(previous.Dispose);
            presentation = await Task.Run(() => NativePresentation.Create(target, destination));
            presentationState.End(); presentationState.Prepare(); liveCamera.Reset(); liveCamera.SetMagnification(LiveMagnification.Value);
            lastPresentationProject = null; RegisterPresentationHotkeys();
            SwitchSurface(LivePanel);
            sharingOutputOffscreen = target.IsMonitor && destination is null;
            ZoomLiveButton.Visibility=LockLiveButton.Visibility=CoverLiveButton.Visibility=EndLiveButton.Visibility=LiveRecordButton.Visibility=Visibility.Visible;
            liveTimer?.Start(); UpdatePresentationControls();
            if (presentationHotkeys.Any(k => !k.Available)) Show("일부 발표 단축키가 다른 앱에서 사용 중입니다. 컨트롤 바 버튼으로 조작하세요.", InfoBarSeverity.Warning);
        }
        catch (Exception ex) { Show($"발표 화면을 준비하지 못했습니다. 대상과 출력 모니터를 확인하세요. ({ex.HResult:X8})", InfoBarSeverity.Error); }
        finally { SetBusy(false); }
    }
    private async void BeginPresentation(object sender, RoutedEventArgs e)
    {
        if (presentationOperation || presentation is null || presentationState.State != PresentationState.Ready) return;
        presentationOperation = true;
        try
        {
            presentation.Command(1); presentationState.Begin(); StartCamera(); OpenToolbar(true);
            if (RecordWithPresentation.IsOn) await BeginLiveRecording();
        }
        catch (Exception ex) { Show($"발표를 시작하지 못했습니다. ({ex.HResult:X8})", InfoBarSeverity.Error); }
        finally { presentationOperation = false; UpdatePresentationControls(); }
    }
    private void StartCamera()
    {
        liveCamera.Reset(); liveCamera.SetMagnification(LiveMagnification.Value);
        var stats = presentation!.GetStats();
        liveCamera.ToggleZoom(NowUs, stats.CursorVisible != 0 ? new(stats.CursorX, stats.CursorY) : new(.5, .5));
    }
    private void AdjustLiveMagnification(double step)
    {
        if (presentation is null || presentationState.State != PresentationState.Live) return;
        var stats = presentation.GetStats();
        double value = Math.Clamp(Math.Round(liveCamera.Magnification + step, 1), 1.2, 2);
        liveCamera.AdjustMagnification(NowUs, value, stats.CursorVisible != 0 ? new(stats.CursorX, stats.CursorY) : liveCamera.Pose.Center);
        LiveMagnification.Value = value; UpdatePresentationControls();
    }
    private void ToggleLiveZoom(object sender, RoutedEventArgs e)
    {
        if (presentation is null || presentationState.State != PresentationState.Live) return;
        try
        {
            var stats = presentation.GetStats(); var p = stats.CursorVisible != 0 ? new PointD(stats.CursorX, stats.CursorY) : liveCamera.Pose.Center;
            liveCamera.ToggleZoom(NowUs, p); UpdatePresentationControls();
        }
        catch (Exception ex) { Show($"확대를 변경하지 못했습니다. ({ex.HResult:X8})", InfoBarSeverity.Error); }
    }
    private void ToggleLiveLock(object sender, RoutedEventArgs e) { if (presentationState.State == PresentationState.Live) { liveCamera.ToggleLock(); UpdatePresentationControls(); } }
    private async void ToggleLiveCover(object sender, RoutedEventArgs e)
    {
        if (presentationOperation || presentation is null || presentationState.State is not (PresentationState.Live or PresentationState.Covered)) return;
        presentationOperation = true;
        try
        {
            if (presentationState.State == PresentationState.Live)
            {
                presentationState.Cover(); if (capture is not null) await Task.Run(() => capture.Pause(true)); presentation.Command(2);
            }
            else
            {
                presentation.Command(1); presentationState.Resume();
                // A resumed WGC session must deliver a new frame before the recording clock resumes.
                var deadline = Stopwatch.StartNew();
                while (presentation.GetStats().Width == 0)
                {
                    if (presentation.GetStats().Error < 0 || deadline.Elapsed > TimeSpan.FromSeconds(10))
                        throw new InvalidOperationException("발표 프레임을 받지 못했습니다.");
                    await Task.Delay(20);
                }
                if (capture is not null && presentationState.ShouldWriteRecording) await Task.Run(() => capture.Pause(false));
            }
        }
        catch (Exception ex) { presentationState.Fail("출력 중단"); Show($"발표 상태를 바꾸지 못했습니다. ({ex.HResult:X8})", InfoBarSeverity.Error); }
        finally { presentationOperation = false; UpdatePresentationControls(); }
    }
    private async void StartLiveRecording(object sender, RoutedEventArgs e)
    {
        if (presentationOperation) return; presentationOperation = true;
        try { await BeginLiveRecording(); }
        catch (Exception ex) { Show($"녹화를 시작하지 못했습니다. 발표 출력은 계속됩니다. ({ex.HResult:X8})", InfoBarSeverity.Error); }
        finally { presentationOperation = false; UpdatePresentationControls(); }
    }
    private async Task BeginLiveRecording()
    {
        if (presentation is null || presentationState.State != PresentationState.Live || capture is not null) return;
        currentId = Guid.NewGuid().ToString("N"); string path = Path.Combine(store.ProjectPath(currentId), "capture");
        var live = presentation; var selectedAudio = SelectedAudio;
        capture = await Task.Run(() => live.Record(path, selectedAudio));
        presentationState.StartRecording(); recordingPaused = false; timer.Start(); UpdateControls();
    }
    private async void PauseRecording(object sender, RoutedEventArgs e)
    {
        if (capture is null || busy || presentationOperation) return;
        presentationOperation = true;
        try
        {
            recordingPaused = !recordingPaused;
            if (IsPresenting) presentationState.PauseRecording(recordingPaused);
            bool pause = recordingPaused || (IsPresenting && !presentationState.ShouldWriteRecording);
            await Task.Run(() => capture.Pause(pause));
            SetCommand(PauseButton, recordingPaused ? "녹화 재개" : "녹화 일시정지", recordingPaused ? "\uE768" : "\uE769");
        }
        catch (Exception ex) { Show($"녹화 일시정지를 변경하지 못했습니다. ({ex.HResult:X8})", InfoBarSeverity.Error); }
        finally { presentationOperation = false; UpdatePresentationControls(); }
    }
    private async void EndPresentation(object sender, RoutedEventArgs e) => await FinishPresentation();
    private async Task FinishPresentation()
    {
        if (mode == WorkspaceMode.Recording && capture is not null) { await FinishRecording(); return; }
        if (presentationOperation || presentation is null || !IsPresenting || (busy && finishingRecording is null)) return;
        presentationOperation = true;
        try
        {
            presentation.Command(3); if (capture is not null) await FinishRecording();
            presentationState.End(); liveTimer?.Stop(); liveCamera.Reset();
            foreach (var key in presentationHotkeys) key.Dispose(); presentationHotkeys.Clear();
            toolbar?.Dispose();toolbar=null;
            if (!closeRequested)
            {
                if (AppWindow.Presenter is OverlappedPresenter mainPresenter) mainPresenter.Restore();
                Activate();
            }
            Navigate(WorkspaceMode.Home);
            if (lastPresentationProject is not null) await LoadLibrary(lastPresentationProject);
            Show("발표를 종료했습니다. 회의 앱에서도 화면 공유를 종료해 주세요.");
        }
        catch (Exception ex) { Show($"발표 종료를 완료하지 못했습니다. ({ex.HResult:X8})", InfoBarSeverity.Error); }
        finally { presentationOperation = false; UpdateControls(); }
    }
    private void LiveTick(DispatcherQueueTimer sender, object args)
    {
        if (presentation is null || presentationOperation) return;
        try
        {
            var stats = presentation.GetStats();
            var previousState = presentationState.State;
            if (stats.Closed != 0) { _ = FinishPresentation(); return; }
            if (stats.State == 5 && presentationState.State != PresentationState.Faulted)
            { presentationState.Fail("발표 대상을 확인해 주세요."); Show("발표 출력을 중단했습니다. 대상 창과 모니터를 확인해 주세요.", InfoBarSeverity.Error); }
            if (stats.State == 3 && presentationState.State == PresentationState.Live)
            { presentationState.Cover(); Show("대상 창이 최소화되어 화면을 가렸습니다. 창을 복원한 뒤 발표를 재개하세요.", InfoBarSeverity.Warning); }
            if (presentationState.State == PresentationState.Live)
            {
                var camera = liveCamera.Step(NowUs, new(stats.CursorX, stats.CursorY), stats.CursorVisible != 0);
                presentation.SetPose(camera, new());
            }
            if (previousState != presentationState.State) UpdatePresentationControls();
            else UpdateToolbarStatus();
        }
        catch (Exception ex) { presentationState.Fail("출력 오류"); UpdatePresentationControls(); Show($"발표 상태를 확인하지 못했습니다. ({ex.HResult:X8})", InfoBarSeverity.Error); }
    }
    private void UpdatePresentationControls()
    {
        SetCommand(PauseButton, recordingPaused ? "녹화 재개" : "녹화 일시정지", recordingPaused ? "\uE768" : "\uE769");
        toolbar?.Recording(capture is not null, recordingPaused, busy || presentationOperation, presentationState.State == PresentationState.Live);
        if (!IsPresenting)
        {
            PauseButton.IsEnabled = capture is not null && !busy && !presentationOperation;
            return;
        }
        bool active = presentationState.State == PresentationState.Live, covered = presentationState.State == PresentationState.Covered;
        toolbar?.Presentation(active, liveCamera.Zoomed, liveCamera.Locked, covered, busy || presentationOperation);
        BeginLiveButton.Visibility = presentationState.State == PresentationState.Ready ? Visibility.Visible : Visibility.Collapsed;
        LiveStatus.Text = presentationState.State switch { PresentationState.Live => mode == WorkspaceMode.Recording ? "녹화 중" : "발표 출력 중", PresentationState.Covered => "화면 가림", PresentationState.Faulted => "출력 중단", PresentationState.Ended => "발표 종료", _ => "발표 화면 준비 완료" };
        LiveHint.Text = presentationState.State switch
        {
            PresentationState.Live => "원본 창에서 작업하세요. 확대를 켜면 커서를 따라 화면이 움직입니다.",
            PresentationState.Covered => "화면을 가리고 녹화를 일시정지했습니다. 준비되면 발표를 재개하세요.",
            PresentationState.Faulted => "발표를 종료한 뒤 대상 창과 출력 위치를 다시 선택해 주세요.",
            _ => sharingOutputOffscreen
                ? "회의 앱에서 Glide Presentation 창을 선택하세요. 공유용 창은 반복 캡처를 막기 위해 화면 밖에 배치됩니다."
                : "회의 앱에서 Glide Presentation 창을 공유한 뒤 발표를 시작하세요."
        };
        ZoomLiveButton.IsEnabled = active && !presentationOperation;
        LockLiveButton.IsEnabled = active && liveCamera.Zoomed && !presentationOperation;
        CoverLiveButton.IsEnabled = (active || covered) && !presentationOperation;
        EndLiveButton.IsEnabled = !presentationOperation;
        SetCommand(ZoomLiveButton, liveCamera.Zoomed ? "전체 보기 · Ctrl+Shift+F9" : "확대 · Ctrl+Shift+F9", liveCamera.Zoomed ? "\uE71F" : "\uE8A3");
        SetCommand(LockLiveButton, liveCamera.Locked ? "추적 재개 · Ctrl+Shift+F10" : "위치 고정 · Ctrl+Shift+F10", "\uE718");
        SetCommand(CoverLiveButton, covered ? "발표 재개 · Ctrl+Shift+F11" : "화면 가리기 · Ctrl+Shift+F11", covered ? "\uE768" : "\uE890");
        LiveRecordButton.IsEnabled = active && capture is null && !presentationOperation;
        PauseButton.IsEnabled = capture is not null && !busy && !presentationOperation;
        StopButton.IsEnabled = capture is not null && !busy;
        LiveRecordingStatus.Text = capture is null ? "녹화 꺼짐 · 파일을 저장하지 않습니다." : $"● 녹화 {(recordingPaused || covered ? "일시정지" : "중")} · {Elapsed.Text}";
        UpdateToolbarStatus();
    }
    private void UpdateToolbarStatus()
    {
        if(IsPresenting)toolbar?.Status($"{LiveStatus.Text} · {(liveCamera.Zoomed ? $"{liveCamera.Pose.Scale:0.0}×" : "전체 보기")} · {(capture is null?"녹화 꺼짐":$"녹화 {(recordingPaused || presentationState.State == PresentationState.Covered ? "일시정지 · " : "")}{Elapsed.Text}")}",
            capture is null ? $"{(presentationState.State == PresentationState.Covered ? "발표 가림" : "발표 중")} · {liveCamera.Pose.Scale:0.0}×" : $"{(recordingPaused || presentationState.State == PresentationState.Covered ? "일시정지" : mode == WorkspaceMode.Recording ? "녹화" : "발표 녹화")} · {Elapsed.Text}");
    }

}
