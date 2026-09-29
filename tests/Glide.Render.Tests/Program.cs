using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Glide.Core;
using Glide.Media;

if (!OperatingSystem.IsWindows() || args.Length != 2) { Console.Error.WriteLine("Usage: Glide.Render.Tests <actual-fixture-capture> <new-output-directory>"); return 2; }
string input = Path.GetFullPath(args[0]), root = Path.GetFullPath(args[1]);
if (Directory.Exists(root)) return 2;
Directory.CreateDirectory(root);
var results = new List<object>(); int failed = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
string Digest(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
void Run(string name, Func<object> action)
{
    var watch = Stopwatch.StartNew();
    try { var evidence = action(); results.Add(new { name, passed = true, milliseconds = watch.Elapsed.TotalMilliseconds, evidence }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; results.Add(new { name, passed = false, milliseconds = watch.Elapsed.TotalMilliseconds, error = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
var store = new ProjectStore(Path.Combine(root, "projects"));
string id = Guid.NewGuid().ToString("N");
var source = CaptureImport.Import(store, id, input, "Actual fixture render", path => { Marshal.ThrowExceptionForHR(Verify(path, out _, out _)); });
var retained = new[] { new TimeRange(1_000_000, 2_000_000), new TimeRange(4_500_000, 6_500_000) }
    .SelectMany(r => source.Ranges.Select(s => new TimeRange(Math.Max(r.StartUs, s.StartUs), Math.Min(r.EndUs, s.EndUs))).Where(s => s.EndUs > s.StartUs)).ToArray();
var project = source with { Ranges = retained,
    Zooms = [new("test-manual", new(4_500_000, 6_500_000), new(.75, .55), 1.8, true)], Revision = 1 };
var cursor = new CursorTrack(Enumerable.Range(0, 211).Select(i => new CursorSample(i * 33_333L, new(.7, .55))), [new(5_200_000, new(.7, .55))]);
var before = store.Recover(id).Segments.ToDictionary(s => s.FileName, s => s.Sha256);
var previews = new List<object>();
Run("actual media cuts, crosses segments, composites zoom and cursor, and exports exact 30fps frames", () =>
{
    using var renderer = new MediaRenderer(store, project, cursor, 640, 360);
    foreach (int index in new[] { 0, 30, 51, 89 })
    {
        var frame = renderer.Plan.AtFrame(index); byte[] image = renderer.Preview(frame.OutputUs);
        string path = Path.Combine(root, $"preview-{index}.bgra"); File.WriteAllBytes(path, image);
        Check(image[0] == 32 && image[1] == 24 && image[2] == 20 && image[3] == 255, "Wrong background pixel/stride.");
        previews.Add(new { index, frame, file = Path.GetFileName(path), sha256 = Digest(path) });
    }
    var progress = new List<long>();
    var result = renderer.Export(Path.Combine(root, "edited-30.mp4"), progress: p => progress.Add(p.Completed));
    Check(result.Frames == 90 && Math.Abs(result.DurationUs - 3_000_000) < 100, "Output timing/count mismatch.");
    Check(progress[0] == 0 && progress[^1] == 90 && progress.SequenceEqual(progress.Order()), "Invalid progress sequence.");
    return new { result, previews, width = 640, height = 360, sourceRanges = project.Ranges, syntheticPointerMetadata = true };
});
Run("60fps fractional final frame and cursor visibility use the same composition", () =>
{
    var p = project with { Ranges = [new(4_500_000, 5_000_123)] };
    using var visible = new MediaRenderer(store, p, cursor, 640, 360, 60);
    using var hidden = new MediaRenderer(store, p, cursor, 640, 360, 60, new(ShowCursor: false));
    byte[] a = visible.Preview(200_000), b = hidden.Preview(200_000);
    int changed = Enumerable.Range(0, a.Length / 4).Count(i => !a.AsSpan(i * 4, 4).SequenceEqual(b.AsSpan(i * 4, 4)));
    Check(changed > 0 && changed < 600, "Cursor changed pixels outside its small footprint.");
    var result = visible.Export(Path.Combine(root, "edited-60.mp4"));
    Check(result.Frames == 31 && Math.Abs(result.DurationUs - 500_123) < 100, "Fractional last-frame duration lost.");
    return new { result, cursorChangedPixels = changed };
});
Run("cancelled rendering removes partial output and retains every original hash", () =>
{
    using var renderer = new MediaRenderer(store, project, cursor, 640, 360);
    using var cancel = new CancellationTokenSource(); string output = Path.Combine(root, "cancelled.mp4");
    bool observed = false;
    try { renderer.Export(output, cancel.Token, p => { if (p.Completed >= 3) cancel.Cancel(); }); }
    catch (OperationCanceledException) { observed = true; }
    Check(observed && !File.Exists(output) && !Directory.EnumerateFiles(root, ".glide-render-*").Any(), "Cancellation published or leaked partial output.");
    foreach (var pair in before) Check(Digest(Path.Combine(store.ProjectPath(id), "media", pair.Key)) == pair.Value, "Original media changed.");
    return new { cancelledDuringFrames = true, originals = before.Count, noPartialOutput = true };
});
Run("existing destination and concurrent media writes are rejected without damage", () =>
{
    using var renderer = new MediaRenderer(store, project, cursor, 640, 360);
    string output = Path.Combine(root, "existing.mp4"); File.WriteAllText(output, "sentinel"); string hash = Digest(output);
    bool rejected = false; try { renderer.Export(output); } catch (IOException) { rejected = true; }
    Check(rejected && Digest(output) == hash, "Existing destination changed.");
    bool locked = false;
    try { using var write = new FileStream(Path.Combine(store.ProjectPath(id), "media", before.Keys.First()), FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
    catch (IOException) { locked = true; }
    Check(locked, "Render does not hold media read leases.");
    return new { rejectedExisting = rejected, sourceWriteDenied = locked };
});
Run("callback failure stays managed and cleans up native output", () =>
{
    using var renderer = new MediaRenderer(store, project, cursor, 640, 360);
    bool observed = false; string output = Path.Combine(root, "callback-failure.mp4");
    try { renderer.Export(output, progress: p => { if (p.Completed >= 2) throw new InvalidOperationException("test callback"); }); }
    catch (InvalidOperationException e) when (e.Message == "test callback") { observed = true; }
    Check(observed && !File.Exists(output) && !Directory.EnumerateFiles(root, ".glide-render-*").Any(), "Callback crossed ABI or leaked output.");
    return new { managedException = true, noPartialOutput = true };
});
Run("decoder failure preserves source and never publishes a partial MP4", () =>
{
    string brokenId = Guid.NewGuid().ToString("N");
    var broken = new ProjectDocument(1, brokenId, "Invalid media fixture", 1_000_000, 640, 360, [new(0, 1_000_000)], []);
    store.Save(broken);
    var saved = store.CommitSegment(brokenId, new byte[128], 0, 1_000_000);
    using var renderer = new MediaRenderer(store, broken, new CursorTrack([]), 640, 360);
    bool rejected = false; string output = Path.Combine(root, "broken.mp4");
    try { renderer.Export(output); } catch (COMException) { rejected = true; }
    Check(rejected && !File.Exists(output) && !Directory.EnumerateFiles(root, ".glide-render-*").Any(), "Decoder failure published or leaked output.");
    Check(Digest(Path.Combine(store.ProjectPath(brokenId), "media", saved.FileName)) == saved.Sha256, "Invalid original was modified.");
    return new { rejectedInvalidContainer = true, originalPreserved = true };
});
Run("a retained interval cannot silently bridge missing source media", () =>
{
    string gapId = Guid.NewGuid().ToString("N");
    var missing = new ProjectDocument(1, gapId, "Gap fixture", 3_000_000, 640, 360, [new(0, 3_000_000)], []);
    store.Save(missing);
    store.CommitSegment(gapId, new byte[128], 0, 1_000_000);
    store.CommitSegment(gapId, new byte[128], 2_000_000, 3_000_000);
    bool rejected = false;
    try { using var renderer = new MediaRenderer(store, missing, new CursorTrack([]), 640, 360); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Missing media was silently filled.");
    return new { rejectedGap = true };
});
// Controlled PCM on actual WGC video isolates editing/encoding from the VM's
// audio-device clock. Actual WASAPI capture has separate integration/quality tests.
ProjectDocument AudioProject()
{
    string aid=Guid.NewGuid().ToString("N");
    var imported=CaptureImport.Import(store,aid,input,"Controlled audio on actual video",path=>{Marshal.ThrowExceptionForHR(Verify(path,out _,out _));});
    string capture=Path.Combine(root,"synthetic-audio-source"),folder=Path.Combine(capture,"audio");Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder,"audio-info.json"),JsonSerializer.Serialize(new{schemaVersion=1,sampleRate=48000,channels=2,format="pcm_s16le",microphone=true,system=true}));
    long total=(imported.DurationUs*48000+999999)/1000000;List<string> journal=[];
    foreach(var role in new[]{"microphone","system"})
    for(long first=0,sequence=0;first<total;first+=240000,sequence++)
    {
        int count=(int)Math.Min(240000,total-first);string file=$"{role}-{sequence:D6}.wav";
        using(var writer=new BinaryWriter(File.Create(Path.Combine(folder,file))))
        {
            writer.Write("RIFF"u8);writer.Write(count*4+36);writer.Write("WAVEfmt "u8);writer.Write(16);
            writer.Write((short)1);writer.Write((short)2);writer.Write(48000);writer.Write(192000);writer.Write((short)4);writer.Write((short)16);writer.Write("data"u8);writer.Write(count*4);
            for(int i=0;i<count;i++)
            {
                double time=(first+i)/48000.0;double frequency=time<2?440:time<4.5?880:660;
                if(role=="system")frequency*=2;
                foreach(double channel in new[]{1.0,1.25})writer.Write((short)Math.Round(6000*Math.Sin(2*Math.PI*frequency*channel*time)));
            }
        }
        journal.Add(JsonSerializer.Serialize(new{track=role,file,startFrame=first,frames=count}));
    }
    File.WriteAllText(Path.Combine(folder,"audio-journal.jsonl"),string.Join('\n',journal)+"\n");
    AudioStore.Import(store,aid,capture,AudioStore.ReadCapture(capture,new(2,imported.Width,imported.Height,30,true,true)).Manifest);
    var p=imported with {SchemaVersion=3,Audio=[new("microphone",true,.5),new("system",true,.25)],Ranges=project.Ranges,Revision=1};
    store.Save(p);return p;
}
ProjectDocument? audioProject=null;
Run("two controlled PCM tracks on actual video export as AAC with shared cuts",()=>
{
    audioProject=AudioProject();using var renderer=new MediaRenderer(store,audioProject,cursor,640,360);
    var result=renderer.Export(Path.Combine(root,"audio-mixed.mp4"));
    Check(result.HasAudio&&result.AudioFrames>0&&Math.Abs(result.AudioDurationUs-3_000_000)<=50_000&&result.ClippedAudioSamples==0,"Missing/misaligned audio output");
    using var mixer=new AudioMixer(store,audioProject);using(var output=new BinaryWriter(File.Create(Path.Combine(root,"audio-mixed-reference.s16le"))))
    {
        short[] block=new short[3200];
        for(long frame=0;frame<mixer.FrameCount;frame+=1600)
        {int count=(int)Math.Min(1600,mixer.FrameCount-frame);mixer.Read(frame,block.AsSpan(0,count*2));for(int n=0;n<count*2;n++)output.Write(block[n]);}
    }
    return new{result,sourceRanges=audioProject.Ranges,syntheticPcm=true,actualWgcVideo=true};
});
Run("muting microphone keeps system audio and does not change source samples",()=>
{
    Check(audioProject is not null,"Audio setup failed");
    var p=audioProject! with {Audio=[new("microphone",false),new("system",true,.25)]};
    using var renderer=new MediaRenderer(store,p,cursor,640,360);
    var result=renderer.Export(Path.Combine(root,"audio-system-only.mp4"));Check(result.HasAudio,"Selected system audio missing");
    Check(AudioStore.Load(store,p.Id).Issues.Length==0,"Original audio changed");return new{result,syntheticPcm=true};
});
Run("explicitly muting every track creates a video without an audio stream",()=>
{
    Check(audioProject is not null,"Audio setup failed");
    var p=audioProject! with {Audio=[new("microphone",false),new("system",false)],Ranges=[new(1_000_000,1_500_123)]};
    using var renderer=new MediaRenderer(store,p,cursor,640,360,60);
    var result=renderer.Export(Path.Combine(root,"audio-all-muted.mp4"));Check(!result.HasAudio,"Muted audio unexpectedly enabled");
    Check(VerifyAudio(result.Path,out _,out _)<0,"Unexpected audio stream");return new{result};
});
Run("AAC fractional final sample and 60fps final video frame remain within 50ms",()=>
{
    Check(audioProject is not null,"Audio setup failed");var p=audioProject! with {Ranges=[new(4_500_007,5_000_130)]};
    using var renderer=new MediaRenderer(store,p,cursor,640,360,60);
    var result=renderer.Export(Path.Combine(root,"audio-fractional.mp4"));
    Check(result.HasAudio&&result.Frames==31&&Math.Abs(result.AudioDurationUs-500_123)<=50_000,"Fractional AV tail mismatch");return new{result};
});
Run("audio export cancellation removes the muxed partial file and keeps original hashes",()=>
{
    Check(audioProject is not null,"Audio setup failed");using var renderer=new MediaRenderer(store,audioProject!,cursor,640,360);
    using var cancel=new CancellationTokenSource();string path=Path.Combine(root,"audio-cancelled.mp4");bool cancelled=false;
    try{renderer.Export(path,cancel.Token,p=>{if(p.Completed>=45)cancel.Cancel();});}catch(OperationCanceledException){cancelled=true;}
    Check(cancelled&&!File.Exists(path)&&!Directory.EnumerateFiles(root,".glide-render-*").Any(),"Partial audio output leaked");
    Check(AudioStore.Load(store,audioProject!.Id).Issues.Length==0,"Audio originals changed");return new{cancelled,noPartialOutput=true};
});
Run("verified audio read leases reject concurrent source writes",()=>
{
    Check(audioProject is not null,"Audio setup failed");using var renderer=new MediaRenderer(store,audioProject!,cursor,640,360);
    string path=Path.Combine(store.ProjectPath(audioProject!.Id),"audio","microphone-000000.wav");bool blocked=false;
    try{using var output=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);}catch(IOException){blocked=true;}
    Check(blocked,"Concurrent writer acquired audio source");return new{blocked};
});
File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(), engineSha256 = Digest(Path.Combine(AppContext.BaseDirectory, "Glide.Capture.dll")),
    mediaAssemblySha256 = Digest(typeof(MediaRenderer).Assembly.Location), tests = results.Count, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;

[DllImport("Glide.Capture.dll", EntryPoint = "glide_verify_video", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
[DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
static extern int Verify(string path, out ulong frames, out long durationUs);

[DllImport("Glide.Capture.dll", EntryPoint="glide_verify_audio", ExactSpelling=true, CharSet=CharSet.Unicode, CallingConvention=CallingConvention.Cdecl)]
[DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32)]
static extern int VerifyAudio(string path,out ulong frames,out long durationUs);
