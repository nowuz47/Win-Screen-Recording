using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;

namespace Glide.App;

internal static class WindowDrag
{
    public static void Attach(Window window, Control handle, Action? moved = null)
    {
        // Register before the press: Windows performs hit testing and owns the
        // complete move loop. No XAML capture handoff, cursor sampling or Move
        // calls are involved in mouse dragging.
        var source = InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
        CaptionBounds? previous = null;
        bool closed = false;

        void UpdateRegion(object? sender, object args)
        {
            if (closed) return;
            CaptionBounds bounds = default;
            if (handle.IsLoaded && handle.IsEnabled && handle.Visibility == Visibility.Visible &&
                handle.XamlRoot is { } root && window.Content is FrameworkElement content)
            {
                var area = handle.TransformToVisual(content).TransformBounds(new Rect(0, 0, handle.ActualWidth, handle.ActualHeight));
                bounds = CaptionBounds.FromDips(area.X, area.Y, area.Width, area.Height,
                    content.ActualWidth, content.ActualHeight, root.RasterizationScale);
            }
            if (previous == bounds) return;
            if (bounds.Width == 0 || bounds.Height == 0) source.ClearRegionRects(NonClientRegionKind.Caption);
            else source.SetRegionRects(NonClientRegionKind.Caption,
                [new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height)]);
            previous = bounds;
            Trace($"caption-region {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}");
        }

        void ExitedMoveSize(InputNonClientPointerSource sender, ExitedMoveSizeEventArgs args)
        {
            if (closed) return;
            Trace($"caption-end {window.AppWindow.Position.X},{window.AppWindow.Position.Y}");
            moved?.Invoke();
        }
        // LayoutUpdated also covers a changed DPI, origin or sibling width, not
        // just the handle's size. Only changed physical rectangles reach Win32.
        handle.LayoutUpdated += UpdateRegion;
        handle.Unloaded += UpdateRegion;
        source.ExitedMoveSize += ExitedMoveSize;
        window.Closed += (_, _) =>
        {
            closed = true;
            handle.LayoutUpdated -= UpdateRegion;
            handle.Unloaded -= UpdateRegion;
            source.ExitedMoveSize -= ExitedMoveSize;
        };
        handle.KeyDown += (_, e) =>
        {
            if (closed) return;
            var p = window.AppWindow.Position;
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Left: p.X -= 12; break;
                case Windows.System.VirtualKey.Right: p.X += 12; break;
                case Windows.System.VirtualKey.Up: p.Y -= 12; break;
                case Windows.System.VirtualKey.Down: p.Y += 12; break;
                default: return;
            }
            window.AppWindow.Move(p); e.Handled = true; moved?.Invoke();
        };
    }
    private static void Trace(string text)
    {
        if (Environment.GetEnvironmentVariable("GLIDE_DEV_DIAGNOSTICS") != "1") return;
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glide", "diagnostics");
            Directory.CreateDirectory(folder); File.AppendAllText(Path.Combine(folder, "window-drag.log"), $"{DateTimeOffset.UtcNow:O} {text}\n");
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
