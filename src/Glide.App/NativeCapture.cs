using System.Runtime.InteropServices;

namespace Glide.App;

internal sealed class NativeCapture : IDisposable
{
    private long session;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Stats
    {
        public uint Size, State;
        public ulong Frames, Dropped;
        public long DurationUs;
        public uint Width, Height;
        public int Error;
    }

    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint glide_capture_abi_version();
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int glide_capture_start(nuint window, nuint monitor, string directory, uint fps, out ulong id);
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int glide_capture_start_with_audio(nuint window, nuint monitor, string directory, uint fps, in NativeAudio.Options audio, out ulong id);
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int glide_capture_stats(ulong id, ref Stats stats);
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int glide_capture_stop(ulong id);
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int glide_capture_pause(ulong id, uint paused);
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void glide_capture_release(ulong id);
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int glide_verify_video(string path, out ulong frames, out long durationUs);

    private NativeCapture(ulong id) => session = checked((long)id);
    internal static NativeCapture Attach(ulong id) => new(id);

    public static NativeCapture Start(CaptureTarget target, string directory, uint fps, AudioSelection? audio = null)
    {
        if (glide_capture_abi_version() != 1) throw new InvalidOperationException("Incompatible capture engine.");
        using var options = new NativeAudio.Lease(audio ?? new());
        Marshal.ThrowExceptionForHR(glide_capture_start_with_audio(target.IsMonitor ? 0 : (nuint)target.Handle,
            target.IsMonitor ? (nuint)target.Handle : 0, directory, fps, in options.Value, out ulong id));
        return new(id);
    }

    public Stats GetStats()
    {
        ObjectDisposedException.ThrowIf(session == 0, this);
        var stats = new Stats { Size = (uint)Marshal.SizeOf<Stats>() };
        Marshal.ThrowExceptionForHR(glide_capture_stats((ulong)session, ref stats));
        return stats;
    }

    // Finalized media may still be recoverable when the returned HRESULT indicates interruption.
    public int Stop() => session == 0 ? 0 : glide_capture_stop((ulong)session);
    public void Pause(bool paused) => Marshal.ThrowExceptionForHR(glide_capture_pause((ulong)session, paused ? 1u : 0u));

    public static void VerifyVideo(string path)
    {
        Marshal.ThrowExceptionForHR(glide_verify_video(path, out var frames, out _));
        if (frames == 0) throw new InvalidDataException("The saved video has no decodable frames.");
    }

    public void Dispose()
    {
        long id = Interlocked.Exchange(ref session, 0);
        if (id != 0) glide_capture_release((ulong)id);
    }
}
