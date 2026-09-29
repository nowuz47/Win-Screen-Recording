using System.Runtime.InteropServices;

namespace Glide.App;

internal sealed class AudioLevelTest : IDisposable
{
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int glide_audio_meter_start(string device, uint role, out ulong id);
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int glide_audio_meter_read(ulong id, out float peak);
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void glide_audio_meter_release(ulong id);
    private ulong microphone, system;
    public AudioLevelTest(AudioSelection selection)
    {
        try
        {
            if (selection.Microphone is not null) Marshal.ThrowExceptionForHR(glide_audio_meter_start(selection.Microphone, 1, out microphone));
            if (selection.System is not null) Marshal.ThrowExceptionForHR(glide_audio_meter_start(selection.System, 2, out system));
        }
        catch { Dispose(); throw; }
    }
    private static float? Read(ulong id)
    {
        if (id == 0) return null;
        Marshal.ThrowExceptionForHR(glide_audio_meter_read(id, out var peak)); return peak;
    }
    public (float? Microphone, float? System) Read() => (Read(microphone), Read(system));
    public void Dispose()
    {
        if (microphone != 0) glide_audio_meter_release(microphone);
        if (system != 0) glide_audio_meter_release(system);
        microphone = system = 0;
    }
}
