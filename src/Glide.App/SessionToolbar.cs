using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Glide.App;

internal sealed class SessionToolbar : Window, IDisposable
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    private const double IslandHeight = 64, ButtonSize = 44, CommandWidth = 104;
    private readonly TextBlock status = new() { Text = "준비 중", FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button pauseButton, stopButton;
    private readonly Button? recordButton, zoomButton, lockButton, coverButton, endButton;
    private readonly Border body;
    private readonly nint hwnd;
    private bool disposing;
    private int shapedWidth, shapedHeight;
    private readonly MotionCoordinator motion;
    public bool ExcludedFromCapture { get; }

    public SessionToolbar(MotionPolicy policy, bool presentation, Action showMain, Action zoom, Action locked, Action cover, Action record, Action pause, Action stop, Action end)
    {
        motion = new(policy);
        Title = presentation ? "Glide · 발표 제어" : "Glide · 녹화 제어";
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        }
        bool acrylic = DesktopAcrylicController.IsSupported();
        if (acrylic) SystemBackdrop = new ToolbarBackdrop();
        body = new Border
        {
            RequestedTheme = ElementTheme.Dark, CornerRadius = new CornerRadius(IslandHeight / 2),
            Padding = new Thickness(12, 9, 12, 9), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            Background = new SolidColorBrush(acrylic ? Color.FromArgb(140, 18, 22, 29) : Color.FromArgb(255, 29, 34, 41))
        };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dragArea = new Grid { Width = presentation ? 144 : 158, ColumnSpacing = 6, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        dragArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dragArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var gripMark = new FontIcon { Glyph = "\uE784", FontSize = 16, Width = 28 };
        dragArea.Children.Add(gripMark); Grid.SetColumn(status, 1); dragArea.Children.Add(status);
        var grip = new ContentControl
        {
            IsTabStop = true, UseSystemFocusVisuals = true, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Stretch,
            Template = (ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ContentControl'><Grid Background='Transparent'/></ControlTemplate>")
        };
        Grid.SetColumnSpan(grip, 2); dragArea.Children.Add(grip);
        AutomationProperties.SetName(grip, "제어 패널 이동");
        ToolTipService.SetToolTip(grip, "끌어서 이동 · 방향키로 이동");
        WindowDrag.Attach(this, grip, () => TraceAction($"패널 이동 {AppWindow.Position.X},{AppWindow.Position.Y}"));
        row.Children.Add(dragArea);

        Button Command(string label, string glyph, Action action, string? shortcut = null)
        {
            var button = new Button { Width = CommandWidth, Height = ButtonSize, MinWidth = CommandWidth, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(ButtonSize / 2) };
            SetLabel(button, label, glyph, shortcut);
            button.Click += (_, _) => { TraceAction(AutomationProperties.GetName(button)); action(); };
            motion.AttachButton(button); return button;
        }
        var secondary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (presentation)
        {
            secondary.Children.Add(zoomButton = Command("확대", "\uE8A3", zoom, "Ctrl+Shift+F9"));
            secondary.Children.Add(lockButton = Command("고정", "\uE718", locked, "Ctrl+Shift+F10"));
            secondary.Children.Add(coverButton = Command("가리기", "\uE890", cover, "Ctrl+Shift+F11"));
            secondary.Children.Add(recordButton = Command("녹화 시작", "\uE714", record));
            secondary.Children.Add(endButton = Command("발표 종료", "\uE8BB", end, "Ctrl+Shift+F12"));
        }
        // At unusually narrow work areas only the secondary actions scroll.
        // Pause, stop and return-to-workspace always remain visible in one row.
        var secondaryScroll = new ScrollViewer { Content = secondary, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(secondaryScroll, 1); row.Children.Add(secondaryScroll);
        var essentials = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        essentials.Children.Add(pauseButton = Command("녹화 일시정지", "\uE769", pause));
        essentials.Children.Add(stopButton = Command("녹화 종료", "\uE71A", stop, "Ctrl+Shift+R"));
        stopButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 151, 156));
        essentials.Children.Add(new Border { Width = 1, Height = 22, Margin = new Thickness(3, 0, 3, 0), Background = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)) });
        essentials.Children.Add(Command("작업 화면", "\uE80F", showMain));
        Grid.SetColumn(essentials, 2); row.Children.Add(essentials);
        body.Child = row; Content = body;
        body.Loaded += (_, _) => motion.Enter(row, MotionTokens.Preview, y: 0);
        AppWindow.Changed += (_, args) =>
        {
            if (disposing) return;
            motion.SetVisible(AppWindow.IsVisible);
            if (args.DidSizeChange) { ApplyShape(); motion.CancelAll(); }
        };
        ExcludedFromCapture = SetWindowDisplayAffinity(hwnd, 0x11);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double dpi = Math.Max(1, GetDpiForWindow(hwnd) / 96.0);
        int width = Math.Min(area.Width - 24, (int)((presentation ? 1104 : 560) * dpi));
        if (width / dpi < 500 && presentation) dragArea.Width = 108;
        int height = (int)(IslandHeight * dpi);
        AppWindow.MoveAndResize(new(area.X + (area.Width - width) / 2, area.Y + area.Height - height - 20, width, height));
        ApplyShape();
        AppWindow.Closing += (_, e) => { if (!disposing) { e.Cancel = true; showMain(); } };
    }

    private void ApplyShape()
    {
        var size = AppWindow.Size;
        if (size.Width <= 0 || size.Height <= 0 || (shapedWidth == size.Width && shapedHeight == size.Height)) return;
        nint region = CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, size.Height, size.Height);
        if (region == 0) return;
        shapedWidth = size.Width; shapedHeight = size.Height;
        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            DeleteObject(region); shapedWidth = shapedHeight = 0;
        } // On success Windows owns the region.
    }
    public void Status(string text, string? compact = null)
    {
        string label = compact ?? text;
        if (status.Text != label) { status.Text = label; ToolTipService.SetToolTip(status, text); }
    }
    private static void TraceAction(string action)
    {
        if (Environment.GetEnvironmentVariable("GLIDE_DEV_DIAGNOSTICS") != "1") return;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glide", "diagnostics");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "toolbar-actions.log"), $"{DateTimeOffset.UtcNow:O} {action}\n");
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static void SetLabel(Button button, string text, string glyph, string? shortcut = null)
    {
        CommandContent.Set(button, glyph, CommandContent.Caption(text), 12);
        AutomationProperties.SetName(button, text);
        if (shortcut is not null) AutomationProperties.SetAcceleratorKey(button, shortcut);
        ToolTipService.SetToolTip(button, shortcut is null ? text : $"{text} · {shortcut}");
    }
    private void Label(Button button, string text, string glyph, string? shortcut = null)
    {
        if (AutomationProperties.GetName(button) == text) return;
        SetLabel(button, text, glyph, shortcut);
        motion.Enter((FrameworkElement)button.Content, MotionTokens.Press, y: 0);
    }
    private static void Emphasize(Button button, bool active)
    {
        if (active) button.Style = (Style)Application.Current.Resources["GlidePrimaryButton"];
        else button.ClearValue(FrameworkElement.StyleProperty);
    }
    public void Recording(bool recording, bool manuallyPaused, bool blocked, bool canStart)
    {
        Label(pauseButton, manuallyPaused ? "녹화 재개" : "녹화 일시정지", manuallyPaused ? "\uE768" : "\uE769");
        Emphasize(pauseButton, recording && manuallyPaused);
        pauseButton.IsEnabled = stopButton.IsEnabled = recording && !blocked;
        if (recordButton is not null) recordButton.IsEnabled = !recording && canStart && !blocked;
    }
    public void Presentation(bool live, bool zoomed, bool locked, bool covered, bool blocked)
    {
        if (zoomButton is null || lockButton is null || coverButton is null || endButton is null) return;
        Label(zoomButton, zoomed ? "전체 보기" : "확대", zoomed ? "\uE71F" : "\uE8A3", "Ctrl+Shift+F9");
        Label(lockButton, locked ? "추적 재개" : "고정", locked ? "\uE77A" : "\uE718", "Ctrl+Shift+F10");
        Label(coverButton, covered ? "발표 재개" : "가리기", covered ? "\uE768" : "\uE890", "Ctrl+Shift+F11");
        Emphasize(zoomButton, zoomed); Emphasize(lockButton, locked); Emphasize(coverButton, covered);
        zoomButton.IsEnabled = live && !blocked; lockButton.IsEnabled = live && zoomed && !blocked;
        coverButton.IsEnabled = (live || covered) && !blocked; endButton.IsEnabled = !blocked;
    }
    public void Dispose() { if (disposing) return; disposing = true; motion.Dispose(); SystemBackdrop = null; Close(); }
}
