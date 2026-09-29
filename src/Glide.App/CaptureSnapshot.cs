using System.Runtime.InteropServices;

namespace Glide.App;

internal sealed record CaptureSnapshot(byte[] Pixels, int Width, int Height)
{
    [DllImport("Glide.Capture.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int glide_snapshot(nuint window, nuint monitor, uint maxWidth, uint maxHeight, [Out] byte[] pixels, uint capacity, out uint width, out uint height);
    public static CaptureSnapshot Take(CaptureTarget source, bool fullSize = false)
    {
        uint maxWidth = fullSize ? 7680u : 800u, maxHeight = fullSize ? 4320u : 450u;
        byte[] buffer = new byte[checked((int)(maxWidth * maxHeight * 4))];
        Marshal.ThrowExceptionForHR(glide_snapshot(source.IsMonitor ? 0 : (nuint)source.Handle, source.IsMonitor ? (nuint)source.Handle : 0,
            maxWidth, maxHeight, buffer, (uint)buffer.Length, out var width, out var height));
        Array.Resize(ref buffer, checked((int)(width * height * 4)));
        return new(buffer, (int)width, (int)height);
    }
}
