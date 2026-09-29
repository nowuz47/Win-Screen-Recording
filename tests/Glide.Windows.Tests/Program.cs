using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Glide.Core;

if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
{ Console.Error.WriteLine("This integration suite requires the x64 Windows process and interactive test desktop."); return 2; }
if (args.Length != 2) { Console.Error.WriteLine("Usage: Glide.Windows.Tests <probe.exe> <new-evidence-directory>"); return 2; }
string probe = Path.GetFullPath(args[0]), root = Path.GetFullPath(args[1]);
if (!File.Exists(probe) || Directory.Exists(root)) { Console.Error.WriteLine("Probe is missing or evidence path already exists."); return 2; }
Directory.CreateDirectory(root);
List<object> results = [];
int failures = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
async Task Run(string name, Func<Task<object>> test)
{
    var elapsed = Stopwatch.StartNew();
    try { object evidence = await test(); results.Add(new { name, passed = true, milliseconds = elapsed.Elapsed.TotalMilliseconds, evidence }); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures++; results.Add(new { name, passed = false, milliseconds = elapsed.Elapsed.TotalMilliseconds, error = e.ToString() }); Console.WriteLine($"FAIL {name}: {e.Message}"); }
}
string Digest(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
Process StartProbe(string directory, bool crash = false, string? fault = null)
{
    var info = new ProcessStartInfo(probe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add(directory); if (crash) info.ArgumentList.Add("--crash-after-8");
    else if (fault is not null) info.ArgumentList.Add(fault);
    return Process.Start(info) ?? throw new InvalidOperationException("Probe did not start.");
}
async Task WaitForProbe(Process process, string log)
{
    var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException)
    {
        // Only the disposable child created by this test is terminated, never another app instance.
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        throw new TimeoutException("Capture probe exceeded 45 seconds.");
    }
    finally { await File.WriteAllTextAsync(log, await output + await errors); }
}

await Run("C ABI rejects invalid capture sources without creating data", () =>
{
    string directory = Path.Combine(root, "invalid-capture");
    int result = Native.Start(0, 0, directory, 30, out ulong id);
    Check(result == unchecked((int)0x80070057) && id == 0 && !Directory.Exists(directory), "Invalid source entered capture.");
    return Task.FromResult<object>(new { hresult = result, directoryCreated = false });
});

await Run("real capture decodes, imports, and reopens with verified media", async () =>
{
    string directory = Path.Combine(root, "normal-capture");
    using var process = StartProbe(directory, crash: false);
    await WaitForProbe(process, Path.Combine(root, "normal-probe.log"));
    Check(process.ExitCode == 0, $"Normal probe exited {process.ExitCode}.");
    var manifest = CaptureImport.ReadManifest(directory);
    Check(manifest.Issues.Length == 0 && manifest.Parts.Length >= 2, "Invalid native capture journal.");
    ulong decoded = 0;
    foreach (var part in manifest.Parts) decoded += Native.Verify(Path.Combine(directory, part.File));
    using var probeResult = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "probe-result.json")));
    Check(decoded == probeResult.RootElement.GetProperty("encodedFrames").GetUInt64(), "Encoded/decoded frame counts disagree.");
    var store = new ProjectStore(Path.Combine(root, "normal-projects")); string id = Guid.NewGuid().ToString("N");
    var project = CaptureImport.Import(store, id, directory, "실제 녹화 테스트 ✨", path => Native.Verify(path));
    var reopened = store.Load(id); var media = store.Recover(id);
    Check(reopened.Id == project.Id && media.Issues.Count == 0 && media.Segments.Count == manifest.Parts.Length, "Reopened project lost media.");
    foreach (var part in media.Segments) Native.Verify(Path.Combine(store.ProjectPath(id), "media", part.FileName));
    return new { decodedFrames = decoded, segments = media.Segments.Count, outputDurationUs = new Timeline(reopened.Ranges).DurationUs, projectId = id };
});

await Run("forced process termination preserves every observed committed segment", async () =>
{
    string directory = Path.Combine(root, "crash-capture");
    using var process = StartProbe(directory, crash: true);
    var observed = new Dictionary<string, string>();
    var watch = Stopwatch.StartNew();
    while (!process.HasExited && watch.Elapsed < TimeSpan.FromSeconds(20))
    {
        string info = Path.Combine(directory, "capture-info.json"), journal = Path.Combine(directory, "capture-journal.jsonl");
        if (File.Exists(info) && File.Exists(journal))
        {
            try
            {
                foreach (var part in CaptureImport.ReadManifest(directory).Parts)
                    observed.TryAdd(part.File, Digest(Path.Combine(directory, part.File)));
            }
            catch (IOException) { /* Retry while the recorder is flushing its commit. */ }
            catch (JsonException) { }
        }
        await Task.Delay(50);
    }
    await WaitForProbe(process, Path.Combine(root, "crash-probe.log"));
    Check(process.ExitCode == 86, $"Expected injected termination, got {process.ExitCode}.");
    Check(observed.Count > 0, "No committed segment was observed before termination; trial is inconclusive.");
    var recovered = CaptureImport.ReadManifest(directory);
    Check(recovered.Parts.Length >= observed.Count, "A committed segment disappeared.");
    foreach (var part in observed)
    {
        Check(recovered.Parts.Any(p => p.File == part.Key), "Confirmed segment missing from recovery.");
        Check(Digest(Path.Combine(directory, part.Key)) == part.Value, "Confirmed media changed after process termination.");
        Native.Verify(Path.Combine(directory, part.Key));
    }
    using var crash = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "probe-crash.json")));
    long wallUs = crash.RootElement.GetProperty("wallUsSinceCaptureReady").GetInt64();
    long tailUpperBoundUs = Math.Max(0, wallUs - recovered.Parts[^1].EndUs);
    Check(tailUpperBoundUs <= 10_000_000, "Lost tail exceeds 10 seconds.");
    var store = new ProjectStore(Path.Combine(root, "recovered-projects")); string id = Guid.NewGuid().ToString("N");
    var project = CaptureImport.Import(store, id, directory, "강제 종료 복구", path => Native.Verify(path));
    Check(store.Recover(id).Issues.Count == 0, "Recovered project checksum failed.");
    return new { observedCommitted = observed, recoveredSegments = recovered.Parts.Length, tailUpperBoundUs, projectId = project.Id, scope = "one ARM VM fault trial; not G03 certification" };
});

foreach (string fault in new[] { "close", "resize" })
{
    await Run($"target {fault} automatically stops capture and preserves decodable media", async () =>
    {
        string directory = Path.Combine(root, $"{fault}-capture");
        using var process = StartProbe(directory, fault: $"--{fault}-after-8");
        await WaitForProbe(process, Path.Combine(root, $"{fault}-probe.log"));
        Check(process.ExitCode == 0, $"{fault} probe exited {process.ExitCode}.");
        using var outcome = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "probe-result.json")));
        Check(outcome.RootElement.GetProperty("automaticStop").GetBoolean(), "Capture required a manual stop after target loss.");
        var manifest = CaptureImport.ReadManifest(directory);
        Check(manifest.Issues.Length == 0 && manifest.Parts.Length >= 2, "Interrupted capture journal is invalid.");
        var store = new ProjectStore(Path.Combine(root, $"{fault}-projects")); string id = Guid.NewGuid().ToString("N");
        var project = CaptureImport.Import(store, id, directory, $"Target {fault} recovery", path => Native.Verify(path));
        var recovered = store.Recover(id);
        Check(recovered.Issues.Count == 0 && recovered.Segments.Count == manifest.Parts.Length, "Interrupted capture lost media during import.");
        return new { automaticStop = true, segments = recovered.Segments.Count, projectId = project.Id, captureHresult = outcome.RootElement.GetProperty("captureHresult").GetInt32() };
    });
}

await Run("real system audio survives pause, separate source import and reopen", async () =>
{
    string directory = Path.Combine(root, "audio-capture");
    using var process = StartProbe(directory, fault: "--system-audio");
    await WaitForProbe(process, Path.Combine(root, "audio-probe.log"));
    Check(process.ExitCode == 0, $"Audio probe exited {process.ExitCode}.");
    var capture = CaptureImport.ReadManifest(directory);
    Check(capture.Info.SchemaVersion == 2 && capture.Info.SystemAudio && !capture.Info.Microphone, "Selected audio profile changed.");
    var source = AudioStore.ReadCapture(directory, capture.Info);
    Check(source.Issues.Length == 0 && source.Manifest.Segments.Length >= 2, "Audio source journal failed validation.");
    var store = new ProjectStore(Path.Combine(root, "audio-projects")); string id = Guid.NewGuid().ToString("N");
    var project = CaptureImport.Import(store, id, directory, "실제 시스템 소리 · 일시정지", path => Native.Verify(path));
    var reopened = store.Load(id); var audio = AudioStore.Load(store, id);
    Check(reopened.Audio?.Length == 1 && reopened.Audio[0].Role == "system", "Audio selection disappeared on reopen.");
    Check(audio.Issues.Length == 0 && audio.Manifest.Segments.SequenceEqual(source.Manifest.Segments), "Audio original bytes changed during import.");
    long duration = new Timeline(reopened.Ranges).DurationUs;
    long audioFrames = audio.Manifest.Segments.Sum(s => s.Part.Frames);
    Check(duration > 9_500_000 && duration < 10_500_000, "Two-second pause was not removed from recording time.");
    Check(Math.Abs(audioFrames * 1_000_000 / 48_000 - project.DurationUs) <= 50_000, "Audio/video committed tail lengths disagree.");
    return new { projectId = id, audioSegments = audio.Manifest.Segments.Length, audioFrames, outputDurationUs = duration,
        scope = "Actual WASAPI loopback and data preservation only. Microphone, tone purity, fine A/V sync, long drift and GA quality are not certified by this test." };
});

foreach(string fault in new[] { "writer-stall", "write-failure" })
{
    await Run($"audio {fault} fails explicitly and preserves pre-fault committed WAVs",async()=>
    {
        string directory=Path.Combine(root,$"audio-{fault}-capture");
        using var process=StartProbe(directory,fault:$"--audio-{fault}");
        var observed=new Dictionary<string,string>();var watch=Stopwatch.StartNew();
        string checkpoint=Path.Combine(directory,"audio","audio-test-checkpoint.json");
        while(!process.HasExited && watch.Elapsed<TimeSpan.FromSeconds(20))
        {
            if(File.Exists(checkpoint))
            {
                var capture=CaptureImport.ReadManifest(directory);
                var before=AudioStore.ReadCapture(directory,capture.Info);
                Check(before.Issues.Length==0 && before.Manifest.Segments.Length>0,"No valid committed audio before fault; trial is inconclusive.");
                foreach(var segment in before.Manifest.Segments)observed.Add(segment.Part.File,Digest(Path.Combine(directory,"audio",segment.Part.File)));
                await File.WriteAllTextAsync(Path.Combine(directory,"audio","audio-test-continue"),"Committed WAV hashes observed before fault.");
                break;
            }
            await Task.Delay(20);
        }
        await WaitForProbe(process,Path.Combine(root,$"audio-{fault}-probe.log"));
        Check(process.ExitCode==0,$"Audio fault probe exited {process.ExitCode}.");
        Check(observed.Count>0,"The test did not observe a pre-fault checkpoint.");
        var source=CaptureImport.ReadManifest(directory);var audio=AudioStore.ReadCapture(directory,source.Info);
        Check(audio.Issues.Length==0,"Committed audio failed verification after fault.");
        foreach(var item in observed)Check(audio.Manifest.Segments.Any(s=>s.Part.File==item.Key) && Digest(Path.Combine(directory,"audio",item.Key))==item.Value,"A pre-fault audio segment changed or disappeared.");
        using var metrics=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory,"audio","audio-performance.json")));
        var queue=metrics.RootElement.GetProperty("writerQueue");
        Check(queue.GetProperty("faultInjected").GetBoolean(),"Storage fault was not reached.");
        Check(queue.GetProperty("peakItems").GetInt32()<=512 && queue.GetProperty("peakPcmBytes").GetInt32()<=768000,"Audio queue exceeded its fixed bounds.");
        if(fault=="writer-stall")Check(queue.GetProperty("rejected").GetInt64()>0 && queue.GetProperty("discarded").GetInt64()==0,"Overflow did not drain accepted data.");
        var store=new ProjectStore(Path.Combine(root,$"audio-{fault}-projects"));string id=Guid.NewGuid().ToString("N");
        var project=CaptureImport.Import(store,id,directory,$"오디오 장애 복구 {fault}",path=>Native.Verify(path));
        Check(project.Recovered && store.Load(id).Recovered,"The recording failure disappeared from persisted recovery status.");
        Check(AudioStore.Load(store,id).Issues.Length==0 && store.Recover(id).Issues.Count==0,"Recovered project failed checksum verification.");
        Check(store.Load(id).Audio?.Length==1,"Recovered audio track was lost.");
        return new { observedCommitted=observed,queue=queue.Clone(),projectId=project.Id,outputDurationUs=new Timeline(project.Ranges).DurationUs,
            scope="Actual Windows capture plus deliberate storage-boundary stall/write-error injection; not physical disk-full or unplug certification." };
    });
}

var report = new { timestamp = DateTimeOffset.UtcNow, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription,
    probeSha256 = Digest(probe), engineSha256 = Digest(Path.Combine(AppContext.BaseDirectory, "Glide.Capture.dll")), tests = results.Count, failed = failures, results };
File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return failures == 0 ? 0 : 1;

static class Native
{
    [DllImport("Glide.Capture.dll", EntryPoint = "glide_capture_start", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int Start(nuint window, nuint monitor, string directory, uint fps, out ulong id);
    [DllImport("Glide.Capture.dll", EntryPoint = "glide_verify_video", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int VerifyVideo(string path, out ulong frames, out long durationUs);
    internal static ulong Verify(string path)
    {
        Marshal.ThrowExceptionForHR(VerifyVideo(path, out ulong frames, out _));
        if (frames == 0) throw new InvalidDataException("Decoded media contains zero frames.");
        return frames;
    }
}
