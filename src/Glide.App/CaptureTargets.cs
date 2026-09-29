using System.Runtime.InteropServices;
using System.Text;

namespace Glide.App;

internal sealed record CaptureTarget(nint Handle, bool IsMonitor, string Label)
{
    public override string ToString() => Label;
}

internal static class CaptureTargets
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    private delegate bool WindowEnum(nint window, nint parameter);
    private delegate bool MonitorEnum(nint monitor, nint dc, ref Rect rectangle, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowEnum callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnum callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, uint size);

    internal static IReadOnlyList<CaptureTarget> List()
    {
        List<CaptureTarget> result = [];
        MonitorEnum monitors = (nint monitor, nint _, ref Rect rectangle, nint parameter) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfoW(monitor, ref info)) result.Add(new(monitor, true, $"화면 {result.Count + 1} · {rectangle.Right - rectangle.Left} × {rectangle.Bottom - rectangle.Top}"));
            return true;
        };
        EnumDisplayMonitors(0, 0, monitors, 0);
        WindowEnum windows = (window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window) || GetWindow(window, 4) != 0) return true;
            GetWindowThreadProcessId(window, out uint process);
            if (process == Environment.ProcessId) return true;
            if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var title = new StringBuilder(512); GetWindowTextW(window, title, title.Capacity);
            if (title.Length > 0) result.Add(new(window, false, title.ToString()));
            return true;
        };
        EnumWindows(windows, 0);
        GC.KeepAlive(monitors); GC.KeepAlive(windows);
        return result;
    }
}

internal sealed class RecordingHotkey : IDisposable
{
    private readonly uint HotkeyId;
    private readonly nint window;
    private readonly SubclassProc callback;
    public bool Available { get; }
    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    public RecordingHotkey(nint window, Action stop, uint key = 0x52, uint id = 0x474C)
    {
        this.window = window; HotkeyId = id;
        callback = (hwnd, message, wParam, lParam, _, _) =>
        {
            if (message == 0x0312 && wParam == HotkeyId) { stop(); return 0; }
            return DefSubclassProc(hwnd, message, wParam, lParam);
        };
        if (!SetWindowSubclass(window, callback, HotkeyId, 0)) return;
        Available = RegisterHotKey(window, (int)HotkeyId, 0x0002 | 0x0004 | 0x4000, key);
    }
    public void Dispose() { UnregisterHotKey(window, (int)HotkeyId); RemoveWindowSubclass(window, callback, HotkeyId); }
}
