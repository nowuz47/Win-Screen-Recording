using Glide.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;

namespace Glide.App;

public sealed partial class MainWindow
{
    private WorkspaceMode selectedMode = WorkspaceMode.Recording;
    private readonly Stack<WorkspaceMode> backHistory = [], forwardHistory = [];
    private void InitializeShell()
    {
        if (AppWindow.Presenter is OverlappedPresenter p) p.SetBorderAndTitleBar(true, false);
        WindowDrag.Attach(this, TitleDrag);
        Root.Loaded += (_, _) => SelectMode(selectedMode, false);
        Root.ActualThemeChanged += (_, _) => SelectMode(selectedMode, false);
        SelectMode(selectedMode, false);
    }
    private static void SetCommand(Button button, string label, string glyph)
    {
        CommandContent.Set(button, glyph, CommandContent.Caption(label));
        AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label);
    }
    private void SelectMode(WorkspaceMode next, bool animate = true)
    {
        double old = selectedMode == WorkspaceMode.Recording ? 0 : 112;
        selectedMode = next; double offset = next == WorkspaceMode.Recording ? 0 : 112;
        ModeSelection.Margin = new Thickness(offset, 0, 0, 0);
        if (animate) uiMotion?.Move(ModeSelection, (float)(old - offset), 0);
        var theme = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[Root.ActualTheme == ElementTheme.Light ? "Light" : "Dark"];
        var onAccent = (Brush)theme["GlideOnAccent"];
        Brush text = new SolidColorBrush(Root.ActualTheme == ElementTheme.Light ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            var settings = new Windows.UI.ViewManagement.UISettings();
            onAccent = new SolidColorBrush(settings.UIElementColor(Windows.UI.ViewManagement.UIElementType.HighlightText));
            text = new SolidColorBrush(settings.UIElementColor(Windows.UI.ViewManagement.UIElementType.WindowText));
        }
        RecordingModeButton.Foreground = next == WorkspaceMode.Recording ? onAccent : text;
        PresentationModeButton.Foreground = next == WorkspaceMode.Presentation ? onAccent : text;
        AutomationProperties.SetItemStatus(RecordingModeButton, next == WorkspaceMode.Recording ? "선택됨" : "선택 안 됨");
        AutomationProperties.SetItemStatus(PresentationModeButton, next == WorkspaceMode.Presentation ? "선택됨" : "선택 안 됨");
        ToolTipService.SetToolTip(HomeStartButton, next == WorkspaceMode.Recording ? "녹화 설정 시작" : "발표 설정 시작");
    }
    private bool CanNavigate => !busy && !loadingProject && capture is null && !IsPresenting;
    private void StartSelectedMode(object sender, RoutedEventArgs e) { if (CanNavigate) Navigate(selectedMode); }
    private void OpenLibrary(object sender, RoutedEventArgs e) { if (CanNavigate) { Projects.SelectedItem = null; Navigate(WorkspaceMode.Library); _ = LoadLibrary(); } }
    private void GoBack(object sender, RoutedEventArgs e)
    {
        if (!CanNavigate || backHistory.Count == 0) return;
        forwardHistory.Push(mode); Navigate(backHistory.Pop(), false);
    }
    private void GoForward(object sender, RoutedEventArgs e)
    {
        if (!CanNavigate || forwardHistory.Count == 0) return;
        backHistory.Push(mode); Navigate(forwardHistory.Pop(), false);
    }
    private void MinimizeWindow(object sender, RoutedEventArgs e) { if (AppWindow.Presenter is OverlappedPresenter p) p.Minimize(); }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
