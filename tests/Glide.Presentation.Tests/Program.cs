using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Glide.Core;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]

if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || args.Length != 2)
{ Console.Error.WriteLine("Requires an unlocked Windows x64 interactive desktop. Usage: Glide.Presentation.Tests <probe.exe> <new-evidence-directory>"); return 2; }
string probe = Path.GetFullPath(args[0]), root = Path.GetFullPath(args[1]);
if (!File.Exists(probe) || Directory.Exists(root)) return 2;
Directory.CreateDirectory(root);
List<object> results = [];
int failed = 0;
void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
void Hr(int value) => Marshal.ThrowExceptionForHR(value);
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
async Task Until(Func<bool> condition)
{
    var elapsed = Stopwatch.StartNew();
    while (!condition()) { if (elapsed.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Live state did not settle in 12 seconds."); await Task.Delay(25); }
}
Native.LiveStats Live(ulong id) { Native.LiveStats s = new() { Size = (uint)Marshal.SizeOf<Native.LiveStats>() }; Hr(Native.Stats(id, ref s)); return s; }
Native.CaptureStats Recorded(ulong id) { Native.CaptureStats s = new() { Size = (uint)Marshal.SizeOf<Native.CaptureStats>() }; Hr(Native.CaptureStatus(id, ref s)); return s; }
async Task Run(string name, Func<nuint, Task<object>> test)
{
    var watch = Stopwatch.StartNew(); Process? fixture = null;
    try
    {
        var start = new ProcessStartInfo(probe) { UseShellExecute = false }; start.ArgumentList.Add("--fixture-only");
        fixture = Process.Start(start) ?? throw new InvalidOperationException("Could not create fixture.");
        await Until(() => { fixture.Refresh(); return fixture.MainWindowHandle != 0; });
        object evidence = await test((nuint)fixture.MainWindowHandle);
        results.Add(new { name, passed = true, elapsedMs = watch.Elapsed.TotalMilliseconds, evidence }); Console.WriteLine($"PASS {name}");
    }
    catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.ToString() }); Console.WriteLine($"FAIL {name}: {e.Message}"); }
    finally
    {
        if (fixture is not null && !fixture.HasExited)
        {
            fixture.CloseMainWindow();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await fixture.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { fixture.Kill(entireProcessTree: true); await fixture.WaitForExitAsync(); }
        }
        fixture?.Dispose();
    }
}
byte[] Snapshot(nuint hwnd)
{
    byte[] data = new byte[800 * 450 * 4];
    Hr(Native.Snapshot(hwnd, 0, 800, 450, data, (uint)data.Length, out uint w, out uint h));
    Check(w > 100 && h > 100, "Audience snapshot has invalid dimensions."); return data[..checked((int)(w * h * 4))];
}

Check(Native.Abi() == 1 && Marshal.SizeOf<Native.LiveStats>() == 64 && Marshal.SizeOf<Native.Pose>() == 56, "Live ABI mismatch.");
await Run("ready/live/covered audience output and live-only storage", async hwnd =>
{
    ulong id = 0; string folder = Path.Combine(root, "live-only"); Directory.CreateDirectory(folder);
    string previous = Environment.CurrentDirectory; Environment.CurrentDirectory = folder;
    try
    {
        Hr(Native.Create(hwnd, 0, 0, out id)); var ready = Live(id);
        Check(ready.State == 1 && ready.Frames == 0 && ready.Width == 0 && ready.OutputWindow != 0, "Ready started capture or omitted audience window.");
        Hr(Native.Command(id, 1)); await Until(() => Live(id).Frames >= 8);
        byte[] live = Snapshot(Live(id).OutputWindow);
        Hr(Native.Command(id, 2)); await Task.Delay(300); var covered = Live(id);
        byte[] neutral = Snapshot(covered.OutputWindow); await Task.Delay(250);
        Check(covered.State == 3 && covered.Width == 0 && Live(id).Frames == covered.Frames, "Covered state kept reading or displaying source frames.");
        Check(!SHA256.HashData(live).SequenceEqual(SHA256.HashData(neutral)), "Covered audience pixels match live content.");
        // Bottom-quarter interior must be the neutral black background, away from title bar and placeholder text.
        int black = 0, sampled = 0;
        for (int i = neutral.Length * 3 / 4 / 4 * 4; i < neutral.Length - 4; i += 4) { sampled++; if (neutral[i] < 8 && neutral[i+1] < 8 && neutral[i+2] < 8) black++; }
        Check(black > sampled * .85, "Covered audience retained visible source content.");
        Check(Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length == 0, "Live-only path wrote files.");
        return new { readyFrames = ready.Frames, liveFrames = covered.Frames, coveredFramesStable = true, liveOnlyFiles = 0, audiencePixelsChecked = true };
    }
    finally { if (id != 0) Native.Release(id); Environment.CurrentDirectory = previous; }
});
await Run("optional recording pauses, survives cover, stops independently, and retains camera path", async hwnd =>
{
    ulong live = 0, record = 0;
    try
    {
        Hr(Native.Create(hwnd, 0, 0, out live)); Hr(Native.Command(live, 1)); await Until(() => Live(live).Frames >= 5);
        Native.Pose pose = new() { X = .5, Y = .5, Scale = 1.6, Background = 0xFF141820, ShowClicks = 1, Padding = .06, CursorScale = 1.5 };
        Hr(Native.SetPose(live, ref pose)); string path = Path.Combine(root, "optional-recording");
        Hr(Native.Record(live, path, out record)); await Task.Delay(2200);
        Hr(Native.Pause(record, 1)); var paused = Recorded(record);
        Hr(Native.Command(live, 2)); await Task.Delay(1200);
        Hr(Native.Command(live, 1)); await Until(() => Live(live).Width > 0); await Task.Delay(400);
        Check(Recorded(record).Frames == paused.Frames, "Uncover resumed a manually paused recording.");
        Hr(Native.Pause(record, 0)); await Task.Delay(3400); Hr(Native.Stop(record)); var done = Recorded(record);
        ulong audienceFrames = Live(live).Frames; await Task.Delay(250);
        Check(Live(live).State == 2 && Live(live).Frames > audienceFrames, "Stopping recording stopped presentation output.");
        Check(done.Frames > paused.Frames && done.DurationUs < 6_800_000, "Pause was included in recorded media time.");
        var store = new ProjectStore(Path.Combine(root, "projects")); string projectId = Guid.NewGuid().ToString("N");
        var document = CaptureImport.Import(store, projectId, path, "Presentation fixture", file => { Hr(Native.Verify(file, out ulong frames, out _)); Check(frames > 0, "Recorded segment is empty."); });
        var camera = CaptureImport.ReadPresentation(path, document.DurationUs);
        Check(document.Origin == "presentation" && camera is not null && Math.Abs(camera.At(document.Ranges[0].StartUs).Scale - 1.6) < 1e-6, "Recorded presentation camera path was not preserved.");
        Hr(Native.Command(live, 3)); await Task.Delay(150);
        Check(Live(live).State == 4 && Live(live).Width == 0, "End retained capture source.");
        return new { recordedFrames = done.Frames, durationUs = done.DurationUs, pausedFrames = paused.Frames, projectId, presentationContinued = true, cameraScale = 1.6 };
    }
    finally { if (record != 0) Native.CaptureRelease(record); if (live != 0) Native.Release(live); }
});
await Run("invalid recursive source and invalid pose are rejected", async hwnd =>
{
    ulong live = 0, recursive = 0;
    try
    {
        Hr(Native.Create(hwnd, 0, 0, out live)); var output = Live(live).OutputWindow;
        int recursiveResult = Native.Create(output, 0, 0, out recursive);
        Check(recursiveResult < 0 && recursive == 0, "Own audience window accepted as input.");
        Native.Pose invalid = new() { X = double.NaN, Y = .5, Scale = 1.6, Background = 0xFF141820, CursorScale = 1.5 };
        Check(Native.SetPose(live, ref invalid) == unchecked((int)0x80070057), "Nonfinite pose accepted.");
        await Task.CompletedTask; return new { recursiveResult, invalidPoseRejected = true };
    }
    finally { if (recursive != 0) Native.Release(recursive); if (live != 0) Native.Release(live); }
});
await Run("repeated capture callbacks survive cover, resume and session disposal", async hwnd =>
{
    const int cycles = 5;
    for (int cycle = 0; cycle < cycles; cycle++)
    {
        ulong live = 0;
        try
        {
            Hr(Native.Create(hwnd, 0, 0, out live)); Hr(Native.Command(live, 1));
            await Until(() => Live(live).Frames >= 3);
            Hr(Native.Command(live, 2));
            Check(Live(live).Width == 0, "Covered source remained readable.");
            ulong before = Live(live).Frames;
            Hr(Native.Command(live, 1)); await Until(() => Live(live).Frames > before + 2);
            Hr(Native.Command(live, 2));
            Check(Live(live).Width == 0, "Second cover retained a stale frame.");
        }
        finally { if (live != 0) Native.Release(live); }
    }
    return new { completedCycles = cycles, coverResumeCycles = cycles, scope = "short callback lifetime regression, not a leak or reliability certification" };
});
await Run("monitor camera produces a capturable output outside its own input", async hwnd =>
{
    ulong live = 0;
    try
    {
        nuint monitor = Native.MonitorFromWindow(hwnd, 2);
        Hr(Native.Create(0, monitor, 0, out live)); Hr(Native.Command(live, 1));
        await Until(() => Live(live).Frames >= 8);
        Native.Pose pose = new() { X = .5, Y = .5, Scale = 1.5, Background = 0xFF141820, CursorScale = 1.5 };
        Hr(Native.SetPose(live, ref pose)); await Task.Delay(200);
        var stats = Live(live); Check(Native.GetWindowRect(stats.OutputWindow, out var rect), "Missing output bounds");
        int right = Native.GetSystemMetrics(76) + Native.GetSystemMetrics(78);
        Check(rect.Left >= right, "Monitor output is recursively visible in desktop input");
        var pixels = Snapshot(stats.OutputWindow);
        int bright = 0, total = 0;
        // Ignore title bar/frame pixels; inspect the central output content.
        for (int i = pixels.Length / 3 / 4 * 4; i < pixels.Length * 2 / 3 / 4 * 4; i += 4)
        { ++total; if (pixels[i] > 40 || pixels[i + 1] > 40 || pixels[i + 2] > 40) ++bright; }
        Check(bright > total * .05, "Window sharing sees a black output interior");
        File.WriteAllBytes(Path.Combine(root, "monitor-output.bgra"), pixels);
        return new { outputLeft = rect.Left, desktopRight = right, frames = stats.Frames, scale = 1.5, windowCaptureBytes = pixels.Length };
    }
    finally { if (live != 0) Native.Release(live); }
});
await Run("audio meters reject invalid endpoints and measure a real speaker test tone", async hwnd =>
{
    Check(Native.MeterStart("missing-device", 2, out var missing) < 0 && missing == 0, "Unknown endpoint accepted");
    Check(Native.MeterStart("", 7, out var invalid) < 0 && invalid == 0, "Invalid meter role accepted");
    string? speaker = null;
    Native.DeviceCallback callback = (id, name, role, primary) => { if (role == 2 && (speaker is null || primary != 0)) speaker = Marshal.PtrToStringUni(id); return 0; };
    Hr(Native.Devices(callback)); GC.KeepAlive(callback); Check(speaker is not null, "No speaker endpoint");
    ulong meter = 0;
    try
    {
        Hr(Native.MeterStart(speaker!, 2, out meter));
        string wave = Path.Combine(root, "meter-tone.wav");
        using (var writer = new BinaryWriter(File.Create(wave)))
        {
            writer.Write("RIFF"u8); writer.Write(36 + 48000 * 2); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(96000);
            for (int i = 0; i < 48000; ++i) writer.Write((short)(Math.Sin(i * 440 * 2 * Math.PI / 48000) * 1638));
        }
        Check(Native.PlaySound(wave, 0, 0x20009), "Speaker tone did not start");
        List<float> levels = [];
        for (int i = 0; i < 30; ++i) { await Task.Delay(50); Hr(Native.MeterRead(meter, out var peak)); Check(float.IsFinite(peak) && peak >= 0 && peak <= 1, "Invalid measured level"); levels.Add(peak); }
        Native.PlaySound(null, 0, 0); await Task.Delay(300); Hr(Native.MeterRead(meter, out var stopped));
        Check(levels.Max() > .001f, "Real tone was not detected");
        Native.MeterRelease(meter); ulong released = meter; meter = 0;
        Check(Native.MeterRead(released, out _) < 0, "Released meter remains readable");
        return new { peak = levels.Max(), stoppedPeak = stopped, samples = levels, scope = "Selected speaker endpoint and actual tone; no microphone speech or acoustic quality claim" };
    }
    finally { Native.PlaySound(null, 0, 0); if (meter != 0) Native.MeterRelease(meter); }
});
File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(), engineSha256 = Hash(Path.Combine(AppContext.BaseDirectory, "Glide.Capture.dll")), probeSha256 = Hash(probe),
    tests = results.Count, failed, scope = "Short functional regressions. Does not certify latency, real meeting shares, hardware, audio, or production readiness.", results }, new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;

static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct LiveStats { public uint Size, State, Width, Height; public ulong Frames; public double X, Y; public uint Visible, Buttons; public int Error; public uint Closed; public nuint OutputWindow; }
    [StructLayout(LayoutKind.Sequential)] internal struct Pose { public double X, Y, Scale; public uint Background, ShowClicks; public double Padding, CursorScale, Corners; }
    [StructLayout(LayoutKind.Sequential)] internal struct CaptureStats { public uint Size, State; public ulong Frames, Dropped; public long DurationUs; public uint Width, Height; public int Error; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] internal static extern nuint MonitorFromWindow(nuint window, uint flags);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nuint window, out Rect rect);
    [DllImport("winmm.dll", EntryPoint="PlaySoundW", CharSet=CharSet.Unicode)] internal static extern bool PlaySound(string? path, nint module, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int DeviceCallback(nint id, nint name, uint role, uint primary);
    [DllImport(Dll, EntryPoint="glide_audio_devices", CallingConvention=CallingConvention.Cdecl)] internal static extern int Devices(DeviceCallback callback);
    [DllImport(Dll, EntryPoint="glide_audio_meter_start", CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Unicode)] internal static extern int MeterStart(string device, uint role, out ulong id);
    [DllImport(Dll, EntryPoint="glide_audio_meter_read", CallingConvention=CallingConvention.Cdecl)] internal static extern int MeterRead(ulong id, out float peak);
    [DllImport(Dll, EntryPoint="glide_audio_meter_release", CallingConvention=CallingConvention.Cdecl)] internal static extern void MeterRelease(ulong id);
    private const string Dll = "Glide.Capture.dll";
    [DllImport(Dll, EntryPoint="glide_live_abi_version", CallingConvention=CallingConvention.Cdecl)] internal static extern uint Abi();
    [DllImport(Dll, EntryPoint="glide_live_create", CallingConvention=CallingConvention.Cdecl)] internal static extern int Create(nuint window,nuint monitor,nuint output,out ulong id);
    [DllImport(Dll, EntryPoint="glide_live_command", CallingConvention=CallingConvention.Cdecl)] internal static extern int Command(ulong id,uint command);
    [DllImport(Dll, EntryPoint="glide_live_stats", CallingConvention=CallingConvention.Cdecl)] internal static extern int Stats(ulong id,ref LiveStats stats);
    [DllImport(Dll, EntryPoint="glide_live_pose", CallingConvention=CallingConvention.Cdecl)] internal static extern int SetPose(ulong id,ref Pose pose);
    [DllImport(Dll, EntryPoint="glide_live_record", CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)] internal static extern int Record(ulong id,string directory,out ulong record);
    [DllImport(Dll, EntryPoint="glide_live_release", CallingConvention=CallingConvention.Cdecl)] internal static extern void Release(ulong id);
    [DllImport(Dll, EntryPoint="glide_capture_pause", CallingConvention=CallingConvention.Cdecl)] internal static extern int Pause(ulong id,uint paused);
    [DllImport(Dll, EntryPoint="glide_capture_stop", CallingConvention=CallingConvention.Cdecl)] internal static extern int Stop(ulong id);
    [DllImport(Dll, EntryPoint="glide_capture_stats", CallingConvention=CallingConvention.Cdecl)] internal static extern int CaptureStatus(ulong id,ref CaptureStats stats);
    [DllImport(Dll, EntryPoint="glide_capture_release", CallingConvention=CallingConvention.Cdecl)] internal static extern void CaptureRelease(ulong id);
    [DllImport(Dll, EntryPoint="glide_verify_video", CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)] internal static extern int Verify(string path,out ulong frames,out long duration);
    [DllImport(Dll, EntryPoint="glide_snapshot", CallingConvention=CallingConvention.Cdecl)] internal static extern int Snapshot(nuint window,nuint monitor,uint maxWidth,uint maxHeight,[Out] byte[] data,uint capacity,out uint width,out uint height);
}
