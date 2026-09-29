using System.Runtime.InteropServices;

namespace Glide.App;

internal sealed record AudioDevice(string? Id, string Name, uint Role)
{
    public override string ToString() => Name;
}
internal sealed record AudioSelection(string? Microphone = null, string? System = null);

internal static class NativeAudio
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Options { public uint Size, Reserved; public nint Microphone, System; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeviceCallback(nint id, nint name, uint role, uint isDefault);
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint glide_audio_abi_version();
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int glide_audio_devices(DeviceCallback callback);
    internal static void CheckAbi()
    {
        if (glide_audio_abi_version() != 1 || Marshal.SizeOf<Options>() != 24) throw new InvalidOperationException("Unsupported audio capture ABI.");
    }
    internal static AudioDevice[] Devices()
    {
        CheckAbi(); var devices = new List<AudioDevice>(); Exception? failure = null;
        DeviceCallback callback = (id, name, role, isDefault) =>
        {
            try
            {
                if (role is not (1 or 2)) throw new InvalidDataException("Invalid audio device role.");
                string key = Marshal.PtrToStringUni(id) ?? throw new InvalidDataException("Missing audio device identifier.");
                string label = Marshal.PtrToStringUni(name) ?? "오디오 장치";
                devices.Add(new(key, label + (isDefault != 0 ? " · 기본 장치" : ""), role)); return 0;
            }
            catch (Exception e) { failure = e; return e.HResult; }
        };
        int result = glide_audio_devices(callback); GC.KeepAlive(callback);
        if (failure is not null) throw failure;
        Marshal.ThrowExceptionForHR(result); return devices.ToArray();
    }
    internal sealed class Lease : IDisposable
    {
        internal Options Value;
        internal Lease(AudioSelection selection)
        {
            CheckAbi(); Value.Size = (uint)Marshal.SizeOf<Options>();
            try
            {
                if (selection.Microphone is not null) Value.Microphone = Marshal.StringToCoTaskMemUni(selection.Microphone);
                if (selection.System is not null) Value.System = Marshal.StringToCoTaskMemUni(selection.System);
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (Value.Microphone != 0) Marshal.FreeCoTaskMem(Value.Microphone);
            if (Value.System != 0) Marshal.FreeCoTaskMem(Value.System);
            Value.Microphone = Value.System = 0;
        }
    }
}
