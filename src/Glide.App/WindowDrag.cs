using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;

namespace Glide.App;

internal static class WindowDrag
{
    private const uint WmNcLeftButtonDown = 0x00A1;
    private const int HitCaption = 2, LeftMouseButton = 0x01;
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }

    public static void Attach(Window window, Control handle, Action? moved = null)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        bool moving = false, closed = false;
        window.Closed += (_, _) => closed = true;
        handle.PointerPressed += (_, e) =>
        {
            if (closed || moving || !handle.IsEnabled ||
                !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            // An event can reach the UI thread after the button was released.
            // Never enter the native modal move loop for an obsolete press.
            if ((GetAsyncKeyState(LeftMouseButton) & 0x8000) == 0 || !GetCursorPos(out var cursor)) return;
            e.Handled = true;
            handle.Focus(FocusState.Pointer);
            moving = true;
            try
            {
                Trace($"native-start {cursor.X},{cursor.Y} window {window.AppWindow.Position.X},{window.AppWindow.Position.Y}");
                // XAML coordinates belong to a moving surface. Combining an old
                // event with the current window origin creates positive feedback.
                // Hand off once to Windows; it owns capture, DPI changes, Escape
                // and button release for the entire move. Do not call Move here.
                ReleaseCapture();
                var screenPoint = unchecked((int)((ushort)cursor.X | ((uint)(ushort)cursor.Y << 16)));
                SendMessage(hwnd, WmNcLeftButtonDown, HitCaption, screenPoint);
            }
            finally { moving = false; }
            if (closed) return;
            Trace($"native-end {window.AppWindow.Position.X},{window.AppWindow.Position.Y}");
            moved?.Invoke();
        };
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
