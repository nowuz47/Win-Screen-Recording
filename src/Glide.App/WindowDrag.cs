using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Glide.App;

internal static class WindowDrag
{
    public static void Attach(Window window, Control handle, Action? moved = null)
    {
        Point origin = default; Windows.Graphics.PointInt32 position = default;
        _ = new PointerDrag(handle, e =>
        {
            var point = e.GetCurrentPoint((UIElement)window.Content).Position;
            var current = window.AppWindow.Position;
            double scale = handle.XamlRoot.RasterizationScale;
            return new Point(current.X + point.X * scale, current.Y + point.Y * scale);
        }, point =>
        {
            origin = point; position = window.AppWindow.Position;
            Trace($"start {point.X},{point.Y} window {position.X},{position.Y}"); return true;
        }, point =>
        {
            window.AppWindow.Move(new(position.X + (int)Math.Round(point.X - origin.X), position.Y + (int)Math.Round(point.Y - origin.Y)));
            Trace($"event {point.X},{point.Y} window {window.AppWindow.Position.X},{window.AppWindow.Position.Y}");
        }, canceled =>
        {
            if (canceled) { Trace("canceled"); return; }
            Trace($"end {window.AppWindow.Position.X},{window.AppWindow.Position.Y}"); moved?.Invoke();
        });
        handle.KeyDown += (_, e) =>
        {
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
