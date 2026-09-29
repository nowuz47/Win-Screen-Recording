using Glide.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glide.App;

public sealed partial class MainWindow
{
    private bool loadingAudioDevices;
    private AudioPreviewSource? audioPreview;
    private AudioLevelTest? audioLevelTest;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? audioLevelTimer;
    private int audioTestVersion;
    private bool startingAudioTest;
    private AudioSelection SelectedAudio => new((MicrophoneDevice.SelectedItem as AudioDevice)?.Id, (SystemAudioDevice.SelectedItem as AudioDevice)?.Id);

    private void StopAudioTest()
    {
        ++audioTestVersion; audioLevelTimer?.Stop(); audioLevelTest?.Dispose(); audioLevelTest = null;
        if (AudioTestButton is null) return;
        MicrophoneDevice.IsEnabled = SystemAudioDevice.IsEnabled = true;
        MicrophoneMeter.Value = SystemMeter.Value = 0; MicrophoneLevel.Text = SystemLevel.Text = "—";
        AudioTestStatus.Text = "장치를 선택하고 소리 테스트를 시작하세요.";
        SetCommand(AudioTestButton, "소리 테스트", "\uE767");
    }
    private async void ToggleAudioTest(object sender, RoutedEventArgs e)
    {
        if (startingAudioTest || busy || capture is not null || IsPresenting) return;
        if (audioLevelTest is not null) { StopAudioTest(); return; }
        var selection = SelectedAudio;
        if (selection.Microphone is null && selection.System is null) { AudioTestStatus.Text = "테스트할 마이크 또는 시스템 장치를 선택하세요."; return; }
        int version = ++audioTestVersion; startingAudioTest = true; AudioTestButton.IsEnabled = false;
        MicrophoneDevice.IsEnabled = SystemAudioDevice.IsEnabled = false;
        try
        {
            var test = await Task.Run(() => new AudioLevelTest(selection));
            if (version != audioTestVersion) { test.Dispose(); return; }
            audioLevelTest = test;
            audioLevelTimer ??= DispatcherQueue.CreateTimer(); audioLevelTimer.Interval = TimeSpan.FromMilliseconds(50);
            audioLevelTimer.Tick -= AudioLevelTick; audioLevelTimer.Tick += AudioLevelTick; audioLevelTimer.Start();
            AudioTestStatus.Text = "테스트 중 · 말하거나 시스템 소리를 재생하세요. 파일은 저장하지 않습니다.";
            SetCommand(AudioTestButton, "소리 테스트 종료", "\uE71A");
        }
        catch (Exception ex) { if (version == audioTestVersion) { StopAudioTest(); AudioTestStatus.Text = $"장치에 접근하지 못했습니다. 연결과 마이크 권한을 확인하세요. ({ex.HResult:X8})"; } }
        finally { startingAudioTest = false; AudioTestButton.IsEnabled = true; }
    }
    private void AudioLevelTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (audioLevelTest is null) return;
            var (mic, system) = audioLevelTest.Read();
            static double Level(float? value) => value is > 0 ? Math.Clamp((20 * Math.Log10(value.Value) + 60) / 60 * 100, 0, 100) : 0;
            static string Label(float? value) => value is null ? "끔" : value is > 0.001f ? $"{20 * Math.Log10(value.Value):0} dB" : "무음";
            MicrophoneMeter.Value = Level(mic); SystemMeter.Value = Level(system);
            MicrophoneLevel.Text = Label(mic); SystemLevel.Text = Label(system);
        }
        catch (Exception ex) { StopAudioTest(); AudioTestStatus.Text = $"소리 테스트가 중단됐습니다. 장치를 다시 선택하세요. ({ex.HResult:X8})"; }
    }

    private void PrepareAudioPlayer(CancellationTokenSource request)
    {
        // Events belong to one playback attempt. A delayed event from a disposed
        // player must not stop a newer preview or display an obsolete error.
        var previous = player;
        var current = player = new() { AutoPlay = false };
        Preview.SetMediaPlayer(current); previous.Dispose();
        current.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
        { if (player == current && playback == request && !request.IsCancellationRequested) StopPlayback(); });
        current.MediaFailed += (_, e) => DispatcherQueue.TryEnqueue(() =>
        {
            if (player != current || playback != request || request.IsCancellationRequested) return;
            StopPlayback(); Show($"미리보기를 재생하지 못했습니다. 저장된 원본은 보존됩니다. ({e.ExtendedErrorCode.HResult:X8})", InfoBarSeverity.Error);
        });
    }

    private async Task LoadAudioDevices()
    {
        if (loadingAudioDevices || capture is not null || IsPresenting) return;
        StopAudioTest();
        loadingAudioDevices = true;
        var previous = SelectedAudio;
        try
        {
            var devices = await Task.Run(NativeAudio.Devices);
            var microphones = new[] { new AudioDevice(null, "마이크 끔", 1) }.Concat(devices.Where(d => d.Role == 1)).ToArray();
            var systems = new[] { new AudioDevice(null, "시스템 소리 끔", 2) }.Concat(devices.Where(d => d.Role == 2)).ToArray();
            MicrophoneDevice.ItemsSource = microphones; SystemAudioDevice.ItemsSource = systems;
            MicrophoneDevice.SelectedItem = microphones.FirstOrDefault(d => d.Id == previous.Microphone) ?? microphones[0];
            SystemAudioDevice.SelectedItem = systems.FirstOrDefault(d => d.Id == previous.System) ?? systems[0];
            if (SelectedAudio != previous) Show("선택한 오디오 장치를 찾지 못해 해당 트랙을 껐습니다. 녹화할 장치를 다시 선택해 주세요.", InfoBarSeverity.Warning);
        }
        catch (Exception e) { Show($"오디오 장치 목록을 읽지 못했습니다. ({e.HResult:X8})", InfoBarSeverity.Error); }
        finally { loadingAudioDevices = false; UpdateControls(); }
    }
    private void RefreshAudioInspector(ProjectDocument p)
    {
        var microphone = p.Audio?.FirstOrDefault(t => t.Role == "microphone");
        var system = p.Audio?.FirstOrDefault(t => t.Role == "system");
        AudioEditControls.Visibility = microphone is not null || system is not null ? Visibility.Visible : Visibility.Collapsed;
        AudioEditEmpty.Visibility = AudioEditControls.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        MicrophoneEdit.Visibility = microphone is null ? Visibility.Collapsed : Visibility.Visible;
        SystemAudioEdit.Visibility = system is null ? Visibility.Collapsed : Visibility.Visible;
        MicrophoneEnabled.IsOn = microphone?.Enabled == true; MicrophoneGain.Value = (microphone?.Gain ?? 1) * 100;
        SystemAudioEnabled.IsOn = system?.Enabled == true; SystemAudioGain.Value = (system?.Gain ?? 1) * 100;
    }
    private async void ApplyAudio(object sender, RoutedEventArgs e)
    {
        if (edits is null || busy) return;
        bool microphone = MicrophoneEnabled.IsOn, system = SystemAudioEnabled.IsOn;
        var tracks = edits.Current.Audio ?? [];
        double? microphoneGain = tracks.Any(t => t.Role == "microphone") ? NumberBoxInput.Read(MicrophoneGain) / 100 : 0;
        double? systemGain = tracks.Any(t => t.Role == "system") ? NumberBoxInput.Read(SystemAudioGain) / 100 : 0;
        if (microphoneGain is null || systemGain is null)
        { Show("볼륨은 0~400 사이의 숫자로 입력해 주세요.", InfoBarSeverity.Warning); return; }
        await ChangeEdit(h => h.Apply(p => p with { Audio = p.Audio?.Select(t => t.Role == "microphone"
            ? t with { Enabled = microphone, Gain = microphoneGain.Value } : t with { Enabled = system, Gain = systemGain.Value }).ToArray() }, store.Save));
    }
}
