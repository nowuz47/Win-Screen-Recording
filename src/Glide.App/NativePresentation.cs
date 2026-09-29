using System.Runtime.InteropServices;
using Glide.Core;

namespace Glide.App;

internal sealed class NativePresentation : IDisposable
{
    private long session;
    [StructLayout(LayoutKind.Sequential)]
    internal struct Stats
    {
        public uint Size, State, Width, Height;
        public ulong Frames;
        public double CursorX, CursorY;
        public uint CursorVisible, Buttons;
        public int Error;
        public uint Closed;
        public nuint OutputWindow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Pose
    {
        public double X, Y, Scale;
        public uint Background, ShowClicks;
        public double Padding, CursorScale, CornerRadius;
    }
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)] private static extern uint glide_live_abi_version();
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)] private static extern int glide_live_create(nuint window, nuint monitor, nuint outputMonitor, out ulong id);
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)] private static extern int glide_live_command(ulong id, uint command);
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)] private static extern int glide_live_pose(ulong id, in Pose pose);
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)] private static extern int glide_live_stats(ulong id, ref Stats stats);
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] private static extern int glide_live_record(ulong id, string directory, out ulong recording);
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)] private static extern int glide_live_record_with_audio(ulong id, string directory, in NativeAudio.Options audio, out ulong recording);
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)] private static extern void glide_live_release(ulong id);
    private NativePresentation(ulong id) => session = checked((long)id);
    public static NativePresentation Create(CaptureTarget source, CaptureTarget? destination)
    {
        if (glide_live_abi_version() != 1) throw new InvalidOperationException("Unsupported presentation engine.");
        Marshal.ThrowExceptionForHR(glide_live_create(source.IsMonitor ? 0 : (nuint)source.Handle, source.IsMonitor ? (nuint)source.Handle : 0,
            destination is null ? 0 : (nuint)destination.Handle, out var id));
        return new(id);
    }
    public void Command(uint command)
    {
        ObjectDisposedException.ThrowIf(session == 0, this);
        Marshal.ThrowExceptionForHR(glide_live_command((ulong)session, command));
    }
    public void SetPose(CameraPose camera, RenderStyle style)
    {
        if (session == 0) return;
        var p = new Pose { X = camera.Center.X, Y = camera.Center.Y, Scale = camera.Scale, Background = style.Background,
            ShowClicks = style.ShowClicks ? 1u : 0u, Padding = style.Padding, CursorScale = style.CursorScale, CornerRadius = style.CornerRadius };
        Marshal.ThrowExceptionForHR(glide_live_pose((ulong)session, in p));
    }
    public Stats GetStats()
    {
        ObjectDisposedException.ThrowIf(session == 0, this);
        var stats = new Stats { Size = (uint)Marshal.SizeOf<Stats>() };
        Marshal.ThrowExceptionForHR(glide_live_stats((ulong)session, ref stats)); return stats;
    }
    public NativeCapture Record(string directory, AudioSelection? audio = null)
    {
        ObjectDisposedException.ThrowIf(session == 0, this);
        using var options = new NativeAudio.Lease(audio ?? new());
        Marshal.ThrowExceptionForHR(glide_live_record_with_audio((ulong)session, directory, in options.Value, out var id)); return NativeCapture.Attach(id);
    }
    public void Dispose() { long id = Interlocked.Exchange(ref session, 0); if (id != 0) glide_live_release((ulong)id); }
}
