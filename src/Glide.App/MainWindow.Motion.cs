using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Glide.App;

public sealed partial class MainWindow
{
    private UiPreferences preferences = UiPreferences.Parse("{}");
    private MotionPolicy? motionPolicy;
    private MotionCoordinator? uiMotion;
    private FrameworkElement? currentSurface;
    private CountdownRing? countdownRing;
    private bool awaitingEditorFrame, motionDisposed;
    private string? savedFile;

    private void InitializeMotion()
    {
        motionPolicy = new(DispatcherQueue, preferences.Motion); uiMotion = new(motionPolicy);
        currentSurface = HomePanel;
        Root.Loaded += (_, _) => AttachMotionButtons(Root);
        AppWindow.Changed += (_, _) =>
        {
            if (motionDisposed) return;
            bool visible = AppWindow.IsVisible && (AppWindow.Presenter is not OverlappedPresenter p || p.State != OverlappedPresenterState.Minimized);
            uiMotion.SetVisible(visible);
            if (!visible) { countdownRing?.Dispose(); countdownRing = null; }
        };
        motionPolicy.Changed += UpdateMotionHint;
        UpdateMotionHint();
    }
    private void AttachMotionButtons(DependencyObject parent)
    {
        if (parent is Button button) uiMotion?.AttachButton(button, button == RecordingModeButton || button == PresentationModeButton);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) AttachMotionButtons(VisualTreeHelper.GetChild(parent, i));
    }
    private void UpdateMotionHint()
    {
        if (MotionSettingHint is null) return;
        MotionSettingHint.Text = motionPolicy?.Reduced == true
            ? "모션 감소 적용 중 · 영상의 확대 효과는 그대로 유지됩니다."
            : "Windows에서 애니메이션을 끄면 감소 설정을 따릅니다. 영상의 확대 효과는 그대로 유지됩니다.";
    }
    private void UiMotionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        preferences.Motion = UiMotionChoice.SelectedIndex == 1 ? UiMotion.Reduced : UiMotion.Standard;
        motionPolicy?.SetPreference(preferences.Motion); SavePreferences();
    }
    private void SavePreferences()
    {
        try { preferences.Save(PreferencesPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Show("화면 설정을 저장하지 못했습니다.", InfoBarSeverity.Warning); }
    }
    private void SwitchSurface(FrameworkElement next, bool backward = false)
    {
        var previous = currentSurface;
        uiMotion?.CancelAll();
        // Accessibility and input follow logical state immediately, even while the old visual fades.
        foreach (var surface in new FrameworkElement[] { HomePanel, LibraryPanel, SetupSurface, LivePanel, EditorSurface })
        {
            bool active = surface == next;
            surface.IsHitTestVisible = active;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(surface,
                active ? Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content : Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            if (surface is Control control) control.IsEnabled = active;
            surface.Visibility = active || surface == previous ? Visibility.Visible : Visibility.Collapsed;
        }
        // Grid containers must also disable descendants in the tab order during their exit.
        Editor.IsEnabled = next == EditorSurface && renderer is not null;
        Inspector.IsHitTestVisible = next == EditorSurface;
        InspectorControls.IsEnabled = next == EditorSurface && !busy && !loadingProject;
        currentSurface = next;
        if (previous != next && previous is not null)
            uiMotion?.Exit(previous, () => { if (currentSurface != previous) previous.Visibility = Visibility.Collapsed; });
        next.Visibility = Visibility.Visible;
        if (next == SetupSurface)
        {
            uiMotion?.Enter(SetupHeading, 256, x: backward ? -16 : 16, y: 0);
            uiMotion?.Enter(SetupCard, 256, y: 16, delay: 32);
            uiMotion?.Enter(SetupFooter, 256, y: 12, delay: 64);
        }
        else if (next == EditorSurface)
        {
            awaitingEditorFrame = true;
            uiMotion?.Enter(PreviewTitle, MotionTokens.Page, y: 12);
            // The frame itself remains pixel-exact. Chrome follows when the first decoded frame is ready.
        }
        else uiMotion?.Enter(next, MotionTokens.Page, x: backward ? -16 : 16, y: 0);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (motionDisposed || currentSurface != next) return;
            Control focus = next == HomePanel ? RecordingModeButton : next == LibraryPanel ? Projects : next == SetupSurface ? Sources : next == EditorSurface ? PlayButton : capture is null ? BeginLiveButton : StopButton;
            focus.Focus(FocusState.Programmatic);
        });
    }
    private void EditorFrameReady()
    {
        if (!awaitingEditorFrame || currentSurface != EditorSurface) return;
        awaitingEditorFrame = false;
        uiMotion?.Enter(TimelinePanel, 256, y: 12, delay: 32);
        uiMotion?.Enter(Inspector, 256, x: 12, y: 0, delay: 64);
    }
    private void ShowCountdown(int number)
    {
        CountdownOverlay.Visibility = Visibility.Visible; CountdownNumber.Text = number.ToString();
        uiMotion?.Enter(CountdownNumber, MotionTokens.Preview, y: 0, scale: .92f);
        if (motionPolicy is not null && countdownRing is null && Root.XamlRoot is not null)
        {
            var brush = (SolidColorBrush)CountdownNumber.Foreground;
            countdownRing = new(CountdownRingHost, motionPolicy, brush.Color);
        }
        countdownRing?.Step(number);
    }
    private void HideCountdown()
    {
        countdownRing?.Dispose(); countdownRing = null;
        uiMotion?.Cancel(CountdownNumber); CountdownOverlay.Visibility = Visibility.Collapsed;
    }
    private void ShowSaved(string message, string? path)
    {
        savedFile = path; SavedMessage.Text = message;
        OpenSavedButton.Visibility = path is null ? Visibility.Collapsed : Visibility.Visible;
        SavedFeedback.Visibility = Visibility.Visible; Notice.IsOpen = false;
        uiMotion?.Enter(SavedFeedback, MotionTokens.Panel, y: 8);
        uiMotion?.Feedback(SavedIcon);
    }
    private void OpenSavedFile(object sender, RoutedEventArgs e)
    {
        if (savedFile is null) return;
        try { Process.Start(new ProcessStartInfo { FileName = savedFile, UseShellExecute = true }); }
        catch (Exception ex) { Show($"저장한 파일을 열지 못했습니다. ({ex.HResult:X8})", InfoBarSeverity.Error); }
    }
    private void DismissSaved(object sender, RoutedEventArgs e)
    {
        uiMotion?.Exit(SavedFeedback, () => SavedFeedback.Visibility = Visibility.Collapsed);
    }
    private void DisposeMotion()
    {
        motionDisposed = true; countdownRing?.Dispose(); countdownRing = null;
        uiMotion?.Dispose(); motionPolicy?.Dispose();
    }
}
