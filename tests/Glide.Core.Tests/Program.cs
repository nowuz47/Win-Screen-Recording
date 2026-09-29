using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Glide.Core;

var suite = args.FirstOrDefault() ?? "all";
List<(string Suite, string Name, Action Run)> tests = [];
void Test(string group, string name, Action run) => tests.Add((group, name, run));
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void Near(double expected, double actual, double tolerance = 1e-9) { if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected}, got {actual}"); }
void Throws<T>(Action run) where T : Exception { try { run(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

Test("timeline", "half-open cut boundaries map audio, video, and cursor together", () =>
{
    var timeline = new Timeline([new(0, 2_000_000), new(5_000_000, 8_000_000)]);
    Equal(5_000_000L, timeline.DurationUs); Equal(5_000_000L, timeline.ToSource(2_000_000));
    Equal<long?>(null, timeline.ToOutput(3_000_000)); Equal<long?>(2_000_000, timeline.ToOutput(5_000_000));
    Throws<ArgumentOutOfRangeException>(() => timeline.ToSource(5_000_000));
});
Test("timeline", "delete spanning a prior cut retains exact source ranges", () =>
{
    var timeline = new Timeline([new(0, 2_000_000), new(5_000_000, 8_000_000)]).Remove(new(1_000_000, 3_000_000));
    Equal(new TimeRange(0, 1_000_000), timeline.Ranges[0]); Equal(new TimeRange(6_000_000, 8_000_000), timeline.Ranges[1]);
    Equal(3_000_000L, timeline.DurationUs);
});
Test("timeline", "invalid overlapping, empty, and overflowing timelines are rejected", () =>
{
    Throws<ArgumentException>(() => new Timeline([])); Throws<ArgumentException>(() => new Timeline([new(0, 100), new(99, 200)]));
    Throws<ArgumentOutOfRangeException>(() => new Timeline([new(-1, 20)]));
    Throws<ArgumentException>(() => new Timeline([new(0, 100)]).Remove(new(0, 100)));
});
Test("timeline", "ten thousand seeded source/output round trips", () =>
{
    var random = new Random(9102026);
    for (int n = 0; n < 1000; n++)
    {
        long at = 0; List<TimeRange> ranges = [];
        for (int j = 0; j < 10; j++) { at += random.Next(1, 10000); var end = at + random.Next(1, 10000); ranges.Add(new(at, end)); at = end; }
        var timeline = new Timeline(ranges);
        for (int j = 0; j < 10; j++) { long output = random.NextInt64(timeline.DurationUs); Equal<long?>(output, timeline.ToOutput(timeline.ToSource(output))); }
    }
});
Test("motion", "camera bounds never expose outside pixels across aspect ratios", () =>
{
    foreach (double x in new[] { 0, .05, .5, .95, 1 })
    foreach (double y in new[] { 0, .05, .5, .95, 1 })
    foreach (double aspect in new[] { 16.0 / 9, 1, 9.0 / 16 })
    {
        ZoomSegment[] zoom = [new("z", new(0, 2_000_000), new(x, y), 2)];
        for (long t = 0; t < 2_000_000; t += 11_111)
        {
            var pose = Motion.CameraAt(zoom, t, 16.0 / 9, aspect);
            double halfW = Math.Min(1, aspect / (16.0 / 9)) / pose.Scale / 2;
            double halfH = Math.Min(1, (16.0 / 9) / aspect) / pose.Scale / 2;
            Check(pose.Center.X >= halfW - 1e-9 && pose.Center.X <= 1 - halfW + 1e-9 && pose.Center.Y >= halfH - 1e-9 && pose.Center.Y <= 1 - halfH + 1e-9, "Camera crossed source edge");
        }
    }
});
Test("motion", "seek order cannot change camera pose", () =>
{
    ZoomSegment[] zoom = [new("z", new(0, 2_000_000), new(.8, .2), 1.6)];
    var first = Motion.CameraAt(zoom, 650_000); _ = Motion.CameraAt(zoom, 1_850_000); Equal(first, Motion.CameraAt(zoom, 650_000));
    Equal(CameraPose.Full, Motion.CameraAt(zoom, 2_000_000)); Near(1, Motion.CameraAt(zoom, 0).Scale);
});
Test("motion", "locked manual zooms survive re-analysis", () =>
{
    var manual = new ZoomSegment("manual", new(500_000, 3_000_000), new(.4, .7), 2, true);
    var result = Motion.Plan([new(1_000_000, new(.8, .8)), new(4_000_000, new(.2, .2))], 8_000_000, [manual]);
    Equal(manual, result.First(x => x.Locked)); Equal(2, result.Count);
});
Test("motion", "nearby clicks merge and drag does not create automatic zoom", () =>
{
    var result = Motion.Plan([new(1_000_000, new(.5, .5)), new(1_200_000, new(.51, .5)), new(4_000_000, new(.3, .2), true)], 6_000_000);
    Equal(1, result.Count); Equal(3_000_000L, result[0].Range.EndUs);
});
Test("motion", "invalid nonfinite parameters are rejected", () =>
{
    Throws<ArgumentException>(() => Motion.ValidateZoom(new("z", new(0, 100), new(double.NaN, .5), 2)));
    Throws<ArgumentOutOfRangeException>(() => Motion.CameraAt([], 1, double.PositiveInfinity));
    Throws<ArgumentException>(() => Motion.Plan([new(-1, new(.5, .5))], 100));
});
Test("motion", "click anchor stays exact with smoothing enabled", () =>
{
    var track = new CursorTrack([new(0, new(.1, .1)), new(10_000, new(.2, .2)), new(20_000, new(.8, .6))], [new(15_000, new(.75, .6))]);
    Equal(new PointD(.75, .6), track.At(15_000, true)!.Position);
});
Test("motion", "missing and hidden cursor intervals never create phantom pointers", () =>
{
    var track = new CursorTrack([new(10_000, new(.2, .2)), new(20_000, new(.2, .2), false), new(1_000_000, new(.8, .8))]);
    Equal(false, track.At(0, true)!.Visible); Equal(false, track.At(500_000, true)!.Visible);
    Equal(false, track.At(1_300_000, true)!.Visible);
    Throws<ArgumentException>(() => new CursorTrack([new(10, new(.1, .1)), new(10, new(.2, .2))]));
});
Test("session", "pause duration is removed once and Stop is idempotent", () =>
{
    var session = new RecordingSession(); session.Prepare(); session.Ready(); session.Start(100); session.Pause(1_100); session.Resume(5_100);
    Equal(2_000L, session.ElapsedUs(6_100)); Equal(true, session.Stop(6_100)); Equal(false, session.Stop(6_100));
    Equal(2_000L, session.ElapsedUs(9_100)); session.Complete(); Equal(RecordingState.Editing, session.State);
});
Test("session", "stopping during pause excludes the ongoing pause", () =>
{
    var session = new RecordingSession(); session.Prepare(); session.Ready(); session.Start(0); session.Pause(10); session.Stop(1000); Equal(10L, session.ElapsedUs(1000));
});
Test("session", "concurrent Stop finalizes exactly once", () =>
{
    var session = new RecordingSession(); session.Prepare(); session.Ready(); session.Start(0);
    int winners = 0; Parallel.For(0, 100, _ => { if (session.Stop(100)) Interlocked.Increment(ref winners); }); Equal(1, winners);
});
Test("session", "invalid transitions and negative time fail explicitly", () =>
{
    var session = new RecordingSession(); Throws<InvalidOperationException>(() => session.Start(0)); session.Prepare(); session.Cancel(); Equal(RecordingState.Idle, session.State);
    session.Prepare(); session.Ready(); Throws<ArgumentOutOfRangeException>(() => session.Start(-1));
});
Test("session", "a stale timestamp after resume cannot create negative active time", () =>
{
    var session = new RecordingSession(); session.Prepare(); session.Ready(); session.Start(100); session.Pause(200); session.Resume(1000);
    Throws<ArgumentOutOfRangeException>(() => session.Pause(300)); Throws<ArgumentOutOfRangeException>(() => session.Stop(300));
    Equal(200L, session.ElapsedUs(1100));
});

void InStore(Action<ProjectStore, string, string> test)
{
    // Keep test data under this checkout; macOS /tmp is a symlink and deliberately disallowed by the store.
    var root = Path.GetFullPath(Path.Combine(".artifacts", "storage-tests", Guid.NewGuid().ToString("N")));
    var id = Guid.NewGuid().ToString("N"); Directory.CreateDirectory(root);
    try { test(new(root), id, root); } finally { Directory.Delete(root, recursive: true); }
}
ProjectDocument Document(string id, long revision = 0) => new(1, id, "데모 프로젝트 ✨", 10_000_000, 1920, 1080, [new(0, 10_000_000)], [], revision);
Test("storage", "project atomically roundtrips Unicode and rejects stale saves", () => InStore((store, id, _) =>
{
    store.Save(Document(id)); Equal("데모 프로젝트 ✨", store.Load(id).Name);
    Throws<InvalidOperationException>(() => store.Save(Document(id))); store.Save(Document(id, 1)); Equal(1L, store.Load(id).Revision);
}));
Test("storage", "unsupported schema and source-escaping edits are rejected", () => InStore((store, id, _) =>
{
    Throws<InvalidDataException>(() => store.Save(Document(id) with { SchemaVersion = 999 }));
    Throws<InvalidDataException>(() => store.Save(Document(id) with { Ranges = [new(0, 11_000_000)] }));
    Throws<ArgumentException>(() => store.Load("../../outside"));
}));
Test("storage", "committed segments survive malformed journal tail", () => InStore((store, id, root) =>
{
    var first = store.CommitSegment(id, Encoding.UTF8.GetBytes("test-media-one"), 0, 5_000_000);
    var second = store.CommitSegment(id, Encoding.UTF8.GetBytes("test-media-two"), 5_000_000, 10_000_000);
    File.AppendAllText(Path.Combine(root, id, "segments.jsonl"), "{\"sequence\":2");
    var recovered = store.Recover(id); Equal(2, recovered.Segments.Count); Equal(1, recovered.Issues.Count);
    Equal(first.Sha256, recovered.Segments[0].Sha256); Equal(second.Sha256, recovered.Segments[1].Sha256);
    Throws<InvalidDataException>(() => store.CommitSegment(id, [1], 10_000_000, 15_000_000));
}));
Test("storage", "corrupt media is detected without discarding later valid media", () => InStore((store, id, root) =>
{
    store.CommitSegment(id, [1, 2, 3], 0, 5_000_000); store.CommitSegment(id, [4, 5, 6], 5_000_000, 10_000_000);
    File.WriteAllBytes(Path.Combine(root, id, "media", "screen-000000.mp4"), [9, 9, 9]);
    var result = store.Recover(id); Equal(1, result.Segments.Count); Equal(1, result.Segments[0].Sequence); Equal(1, result.Issues.Count);
}));
Test("storage", "orphan media is never overwritten on retry", () => InStore((store, id, root) =>
{
    var directory = Path.Combine(root, id, "media"); Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "screen-000000.mp4"); File.WriteAllBytes(path, [7, 8, 9]);
    Throws<IOException>(() => store.CommitSegment(id, [1, 2, 3], 0, 5_000_000)); Equal((byte)7, File.ReadAllBytes(path)[0]);
}));
Test("storage", "twenty independent recovery trials preserve all committed bytes", () =>
{
    Parallel.For(0, 20, trial => InStore((store, id, root) =>
    {
        var random = new Random(trial); var media = new byte[4096]; random.NextBytes(media);
        var record = store.CommitSegment(id, media, 0, 5_000_000);
        File.AppendAllText(Path.Combine(root, id, "segments.jsonl"), new string('{', trial + 1));
        Equal(record.Sha256, store.Recover(id).Segments.Single().Sha256);
    }));
});

Test("storage", "large nonseekable media imports with bounded reads", () => InStore((store, id, _) =>
{
    using var source = new GeneratedStream(7 * 1024 * 1024, failAfter: long.MaxValue);
    var record = store.CommitSegment(id, source, 0, 5_000_000);
    Equal(7L * 1024 * 1024, record.Bytes); Equal(record, store.Recover(id).Segments.Single());
}));
Test("storage", "interrupted source copy never commits incomplete media", () => InStore((store, id, root) =>
{
    using var source = new GeneratedStream(1024 * 1024, failAfter: 200_000);
    Throws<IOException>(() => store.CommitSegment(id, source, 0, 5_000_000));
    Equal(0, store.Recover(id).Segments.Count);
    Equal(0, Directory.GetFiles(Path.Combine(root, id, "media")).Length);
    store.CommitSegment(id, [1, 2, 3], 0, 5_000_000);
    Equal(1, store.Recover(id).Segments.Count);
}));

string CaptureFixture(string root)
{
    string capture = Path.Combine(root, "capture"); Directory.CreateDirectory(capture);
    File.WriteAllText(Path.Combine(capture, "capture-info.json"), "{\"schemaVersion\":1,\"width\":1920,\"height\":1080,\"fps\":30}");
    File.WriteAllBytes(Path.Combine(capture, "screen-000000.mp4"), [1, 2, 3]);
    File.WriteAllBytes(Path.Combine(capture, "screen-000001.mp4"), [4, 5, 6]);
    File.WriteAllText(Path.Combine(capture, "capture-journal.jsonl"), "{\"file\":\"screen-000000.mp4\",\"startUs\":0,\"endUs\":5000000}\n{\"file\":\"screen-000001.mp4\",\"startUs\":5000000,\"endUs\":10000000}\n");
    return capture;
}
Test("import", "partial native journal retains committed ranges", () => InStore((store, id, root) =>
{
    string capture = CaptureFixture(root);
    File.AppendAllText(Path.Combine(capture, "capture-journal.jsonl"), "{\"file\":\"screen-000002");
    var manifest = CaptureImport.ReadManifest(capture);
    Equal(2, manifest.Parts.Length); Equal(1, manifest.Issues.Length);
    int verified = 0;
    // Synthetic byte fixtures test import orchestration only; Windows probe tests actual media decoding.
    var imported = CaptureImport.Import(store, id, capture, "Recovered", _ => verified++);
    Equal(2, verified); Equal(10_000_000L, new Timeline(imported.Ranges).DurationUs);
    Equal(2, store.Recover(id).Segments.Count);
}));
Test("import", "decoder failure cannot produce a valid project", () => InStore((store, id, root) =>
{
    string capture = CaptureFixture(root);
    Throws<InvalidDataException>(() => CaptureImport.Import(store, id, capture, "Bad", _ => throw new InvalidDataException("Decode failed")));
    Equal(0, store.List().Count); Equal(0, store.Recover(id).Segments.Count);
    Equal(true, File.Exists(Path.Combine(capture, "screen-000000.mp4")));
}));
Test("import", "an explicit capture failure stays visible even when committed ranges are complete", () =>
{
    foreach(string? marker in new string?[] { null, "capture-error.json", "audio/audio-error.json" })
        InStore((store,id,root)=>
        {
            string capture=CaptureFixture(root);
            if(marker is not null) {
                string path=Path.Combine(capture,marker);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path,"{\"hresult\":-2147024785}");
            }
            // Only metadata orchestration uses synthetic bytes here; the actual
            // Windows queue-fault tests verify decodable originals and reopen.
            var project=CaptureImport.Import(store,id,capture,"Interrupted metadata",_=>{});
            Equal(marker is not null,project.Recovered);Equal(marker is not null,store.Load(id).Recovered);
            Equal(10_000_000L,new Timeline(project.Ranges).DurationUs);
        });
});
Test("import", "live journal exposes only completed records while its writer remains open", () => InStore((_, _, root) =>
{
    string capture = CaptureFixture(root);
    using var writer = new FileStream(Path.Combine(capture, "capture-journal.jsonl"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    writer.Write(Encoding.UTF8.GetBytes("{\"file\":\"screen-000002")); writer.Flush(true);
    var pending = CaptureImport.ReadManifest(capture);
    Equal(2, pending.Parts.Length); Equal(1, pending.Issues.Length);
    File.WriteAllBytes(Path.Combine(capture, "screen-000002.mp4"), [7, 8, 9]);
    writer.Write(Encoding.UTF8.GetBytes(".mp4\",\"startUs\":10000000,\"endUs\":15000000}\n")); writer.Flush(true);
    var complete = CaptureImport.ReadManifest(capture);
    Equal(3, complete.Parts.Length); Equal(0, complete.Issues.Length);
}));
Test("import", "untrusted capture journal cannot reference another directory", () => InStore((_, _, root) =>
{
    string capture = CaptureFixture(root);
    File.WriteAllText(Path.Combine(capture, "capture-journal.jsonl"), "{\"file\":\"../screen-000000.mp4\",\"startUs\":0,\"endUs\":5000000}\n");
    var manifest = CaptureImport.ReadManifest(capture); Equal(0, manifest.Parts.Length); Equal(1, manifest.Issues.Length);
}));
Test("import", "pointer button transitions distinguish a click from a drag", () => InStore((_, _, root) =>
{
    string capture = CaptureFixture(root);
    var events = new CapturedPointer[] {
        new(0,.2,.2,true,false,false), new(10000,.2,.2,true,true,false), new(20000,.2,.2,true,false,false),
        new(30000,.2,.2,true,true,false), new(40000,.5,.5,true,true,false), new(50000,.7,.7,true,false,false)
    };
    File.WriteAllLines(Path.Combine(capture, "cursor.jsonl"), events.Select(x => JsonSerializer.Serialize(x)));
    var pointers = CaptureImport.ReadPointers(capture, 10_000_000);
    Equal(6, pointers.Samples.Length); Equal(2, pointers.Clicks.Length);
    Equal(false, pointers.Clicks[0].IsDrag); Equal(true, pointers.Clicks[1].IsDrag); Equal(10000L, pointers.Clicks[0].TimeUs);
}));

Test("render", "frame schedule never accumulates fractional 60fps clock drift", () =>
{
    var p = Document(Guid.NewGuid().ToString("N")) with { DurationUs = 1_800_000_000, Ranges = [new(0, 1_800_000_000)] };
    var plan = new RenderPlan(p, new CursorTrack([]), 1920, 1080, 60);
    Equal(108_000L, plan.FrameCount); Equal(1_799_983_333L, plan.AtFrame(107_999).OutputUs);
    Throws<ArgumentOutOfRangeException>(() => plan.AtFrame(108_000));
});
Test("render", "a cut maps camera and cursor to the same source instant", () =>
{
    var p = Document(Guid.NewGuid().ToString("N")) with { Ranges = [new(0, 1_000_000), new(5_000_000, 10_000_000)], Zooms = [new("z", new(4_000_000, 7_000_000), new(.75,.25), 2)] };
    var plan = new RenderPlan(p, new CursorTrack([new(5_000_000, new(.75,.25))]), 1920, 1080, 30);
    var frame = plan.AtTime(1_000_000);
    Equal(5_000_000L, frame.SourceUs); Near(.5, frame.SourceCrop.Width);
    Near(960, frame.CursorPixel.X); Near(540, frame.CursorPixel.Y); Equal(true, frame.CursorVisible);
});
Test("render", "render snapshot survives edits and source array mutation", () =>
{
    var p = Document(Guid.NewGuid().ToString("N"));
    var plan = new RenderPlan(p, new CursorTrack([]), 1080, 1920, 30);
    p.Ranges[0] = new(3_000_000, 5_000_000);
    Equal(10_000_000L, plan.DurationUs); Equal(0L, plan.AtFrame(0).SourceUs);
    var frame = plan.AtFrame(0);
    Near(16.0/9, frame.Destination.Width / frame.Destination.Height);
    Check(frame.Destination.X >= 0 && frame.Destination.Y >= 0 && frame.Destination.X + frame.Destination.Width <= 1080, "Letterboxing crossed canvas");
});
Test("edit", "undo and redo preserve content while revisions remain monotonic", () =>
{
    var history = new EditHistory(Document(Guid.NewGuid().ToString("N")));
    history.Delete(new(2_000_000, 4_000_000)); Equal(8_000_000L, new Timeline(history.Current.Ranges).DurationUs);
    history.Undo(); Equal(10_000_000L, new Timeline(history.Current.Ranges).DurationUs); Equal(2L, history.Current.Revision);
    history.Redo(); Equal(8_000_000L, new Timeline(history.Current.Ranges).DurationUs); Equal(3L, history.Current.Revision);
    history.Undo(); history.Delete(new(0, 1_000_000)); Equal(false, history.CanRedo);
});
Test("edit", "invalid edits do not mutate history or its source", () =>
{
    var history = new EditHistory(Document(Guid.NewGuid().ToString("N")));
    Throws<ArgumentException>(() => history.Delete(new(0, 10_000_000)));
    Throws<ArgumentException>(() => history.Apply(p => p with { Width = 640 }));
    history.Current.Ranges[0] = new(1,2);
    Equal(10_000_000L, new Timeline(history.Current.Ranges).DurationUs); Equal(false, history.CanUndo);
});

Test("edit", "failed persistence preserves edits and both history stacks", () =>
{
    var history = new EditHistory(Document(Guid.NewGuid().ToString("N")));
    Action<ProjectDocument> fail = _ => throw new IOException("injected disk failure");
    Throws<IOException>(() => history.Apply(p => p with { Name = "changed" }, fail));
    Equal(0L, history.Current.Revision); Equal(false, history.CanUndo);
    history.Apply(p => p with { Name = "saved" });
    Throws<IOException>(() => history.Undo(fail));
    Equal("saved", history.Current.Name); Equal(1L, history.Current.Revision); Equal(false, history.CanRedo);
    history.Undo();
    Throws<IOException>(() => history.Redo(fail));
    Equal(2L, history.Current.Revision); Equal(true, history.CanRedo); Equal(false, history.CanUndo);
    history.Redo(); Equal("saved", history.Current.Name); Equal(3L, history.Current.Revision);
});

Test("presentation", "cover and recording pause retain separate ownership", () =>
{
    var p = new PresentationSession(); p.Prepare();
    Throws<InvalidOperationException>(p.StartRecording); p.Begin(); p.StartRecording();
    Equal(true, p.ShouldWriteRecording); p.PauseRecording(true); p.Cover(); p.Resume();
    Equal(false, p.ShouldWriteRecording); p.PauseRecording(false); Equal(true, p.ShouldWriteRecording);
    p.Cover(); Equal(false, p.ShouldWriteRecording); p.Resume(); Equal(true, p.ShouldWriteRecording);
    p.StopRecording(); Equal(PresentationState.Live, p.State); Equal(false, p.Recording);
});
Test("presentation", "fault recovery stays covered and never restarts stopped recording", () =>
{
    var p = new PresentationSession(); p.Prepare(); p.Begin(); p.StartRecording();
    p.Fail("source removed"); Equal(false, p.ShouldWriteRecording);
    Throws<InvalidOperationException>(p.Resume); p.StopRecording(); p.Recover();
    Equal(PresentationState.Covered, p.State); p.Resume(); Equal(false, p.ShouldWriteRecording);
    p.End(); p.End(); Equal(PresentationState.Ended, p.State); p.Prepare(); Equal(PresentationState.Ready, p.State);
});
Test("presentation", "source changes require cover and finalized recording", () =>
{
    var p = new PresentationSession(); p.Prepare(); Equal(true, p.CanChangeSource);
    p.Begin(); Equal(false, p.CanChangeSource); p.StartRecording(); p.Cover(); Equal(false, p.CanChangeSource);
    p.StopRecording(); Equal(true, p.CanChangeSource);
});
Test("presentation", "camera reaches 1.5x in 420ms and clamps all screen edges", () =>
{
    foreach (var target in new[] { new PointD(0, 0), new PointD(1, 1), new PointD(.5, .5) })
    {
        var camera = new LiveCamera(); camera.ToggleZoom(0, target);
        for (long t = 0; t <= 420_000; t += 1_000)
        {
            var pose = camera.Step(t, target, true); double half = .5 / pose.Scale;
            Check(pose.Scale >= 1 && pose.Scale <= 1.5, "Overshoot");
            Check(pose.Center.X >= half && pose.Center.X <= 1 - half && pose.Center.Y >= half && pose.Center.Y <= 1 - half, "Outside crop");
        }
        Near(1.5, camera.Pose.Scale); camera.ToggleZoom(500_000, target); camera.Step(920_000, target, true);
        Equal(CameraPose.Full, camera.Pose);
    }
});
Test("presentation", "camera ignores safe-area jitter and hidden pointer, and respects lock", () =>
{
    var c = new LiveCamera(); c.ToggleZoom(0, new(.5, .5)); c.Step(420_000, new(.5, .5), true);
    var still = c.Pose;
    for (long t = 430_000; t < 700_000; t += 10_000) Equal(still, c.Step(t, new(.52, .48), true));
    Equal(still, c.Step(700_000, new(1, 1), false));
    c.ToggleLock(); Equal(still, c.Step(800_000, new(1, 1), true)); c.ToggleLock();
    Check(c.Step(900_000, new(1, 1), true).Center.X > .5, "Follow did not resume");
    Throws<ArgumentOutOfRangeException>(() => c.Step(899_999, new(.5, .5), true));
    Throws<ArgumentOutOfRangeException>(() => c.SetMagnification(double.NaN));
});
Test("presentation", "live magnification retargets mid-animation without a jump", () =>
{
    var c = new LiveCamera(); var pointer = new PointD(.8, .2);
    c.ToggleZoom(0, pointer); var before = c.Step(180_000, pointer, true);
    c.AdjustMagnification(180_000, 2, pointer); Equal(before, c.Pose);
    Equal(before, c.Step(180_000, pointer, true));
    var middle = c.Step(350_000, pointer, true);
    Check(middle.Scale > before.Scale && middle.Scale < 2, "No smooth ramp");
    c.AdjustMagnification(350_000, 1.2, pointer); Equal(middle, c.Pose);
    Near(1.2, c.Step(770_000, pointer, true).Scale);
    Throws<ArgumentOutOfRangeException>(() => c.AdjustMagnification(800_000, double.NaN, pointer));
    Throws<ArgumentOutOfRangeException>(() => c.AdjustMagnification(800_000, 2.1, pointer));
});
Test("presentation", "recorded poses are deterministic across seeks and manual overrides", () =>
{
    var track = new PresentationTrack([new(0, .5, .5, 1), new(1_000_000, .6, .4, 1.6)], 10_000_000);
    var p = Document(Guid.NewGuid().ToString("N")) with { Origin = "presentation" };
    var plan = new RenderPlan(p, new([]), 1920, 1080, 30, presentation: track);
    var frame = plan.AtTime(2_000_000); _ = plan.AtTime(5_000_000); _ = plan.AtTime(0);
    Equal(frame, plan.AtTime(2_000_000)); Near(1 / 1.6, frame.SourceCrop.Width);
    p = p with { Zooms = [new("manual", new(1_000_000, 4_000_000), new(.5, .5), 2, true)] };
    Near(.5, new RenderPlan(p, new([]), 1920, 1080, 30, presentation: track).AtTime(2_000_000).SourceCrop.Width);
    Throws<InvalidDataException>(() => new PresentationTrack([new(0, .1, .1, 1)], 1_000_000));
});
Test("storage", "v1 upgrades preserve a durable backup and style settings", () => InStore((store, id, root) =>
{
    Directory.CreateDirectory(store.ProjectPath(id));
    string path = Path.Combine(root, id, "project.json");
    File.WriteAllText(path, JsonSerializer.Serialize(Document(id), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    byte[] old = File.ReadAllBytes(path);
    var document = store.Load(id) with { Revision = 1, Style = new(.12, 2, false, true, 0xFF123456, 24), Aspect = "9:16" };
    store.Save(document); var next = store.Load(id);
    Equal(ProjectDocument.CurrentSchema, next.SchemaVersion); Equal(document.Style, next.Style); Equal("9:16", next.Aspect);
    Check(old.SequenceEqual(File.ReadAllBytes(Path.Combine(root, id, "project-v1.backup.json"))), "Migration backup changed");
    var frame = new RenderPlan(next, new([]), 1080, 1920, 30).AtTime(0);
    Equal(0xFF123456u, frame.Background); Near(24 * 1920 / 1080.0, frame.CornerRadius);
}));

void WriteAudioFixture(string directory, bool microphone = true, bool system = true)
{
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory,"audio-info.json"),JsonSerializer.Serialize(new { schemaVersion=1,sampleRate=48000,channels=2,format="pcm_s16le",microphone,system }));
    List<string> journal=[];
    foreach (var role in new[]{"microphone","system"}.Where(r=>r=="microphone"?microphone:system))
    for (int i=0;i<3;i++)
    {
        string file=$"{role}-{i:D6}.wav";
        using (var output=new BinaryWriter(File.Create(Path.Combine(directory,file))))
        {
            output.Write("RIFF"u8);output.Write(960036);output.Write("WAVEfmt "u8);output.Write(16);
            output.Write((short)1);output.Write((short)2);output.Write(48000);output.Write(192000);output.Write((short)4);output.Write((short)16);
            output.Write("data"u8);output.Write(960000);
            for(int sample=0;sample<240000;sample++){output.Write((short)(sample%100));output.Write((short)(-sample%100));}
        }
        journal.Add(JsonSerializer.Serialize(new{track=role,file,startFrame=i*240000,frames=240000}));
    }
    File.WriteAllText(Path.Combine(directory,"audio-journal.jsonl"),string.Join('\n',journal)+"\n");
}
Test("audio", "separate source tracks import durably, reopen, and retry without replacing media",()=>InStore((store,id,root)=>
{
    string capture=Path.Combine(root,"capture");WriteAudioFixture(Path.Combine(capture,"audio"));
    var read=AudioStore.ReadCapture(capture,new(2,640,360,30,true,true));
    Equal(0,read.Issues.Length);Equal(6,read.Manifest.Segments.Length);
    AudioStore.Import(store,id,capture,read.Manifest);var saved=AudioStore.Load(store,id);
    Equal(0,saved.Issues.Length);Check(saved.Manifest.Segments.SequenceEqual(read.Manifest.Segments),"Audio changed during copy");
    File.Delete(Path.Combine(store.ProjectPath(id),"audio.json")); // Crash after durable copies, before manifest publication.
    AudioStore.Import(store,id,capture,read.Manifest);Equal(0,AudioStore.Load(store,id).Issues.Length);
    Check(!Directory.EnumerateFiles(Path.Combine(store.ProjectPath(id),"audio")).Any(p=>p.EndsWith(".pending")),"Pending file leaked");
}));
Test("audio", "corrupt selected microphone chunk removes only the unavailable common interval",()=>InStore((store,id,root)=>
{
    string capture=Path.Combine(root,"capture");WriteAudioFixture(Path.Combine(capture,"audio"));
    using(var file=File.OpenWrite(Path.Combine(capture,"audio","microphone-000001.wav")))file.SetLength(100);
    var read=AudioStore.ReadCapture(capture,new(2,640,360,30,true,true));
    Equal(1,read.Issues.Length);Equal(5,read.Manifest.Segments.Length);
    var common=AudioStore.CommonRanges([new(0,15_000_000)],read.Manifest);
    Check(common.SequenceEqual(new[]{new TimeRange(0,5_000_000),new TimeRange(10_000_000,15_000_000)}),"Recovery bridged missing microphone data");
    AudioStore.Import(store,id,capture,read.Manifest);Equal(0,AudioStore.Load(store,id).Issues.Length);
}));
Test("audio", "partial audiovisual recovery is marked and preserves common intervals",()=>InStore((store,id,root)=>
{
    string capture=Path.Combine(root,"capture");WriteAudioFixture(Path.Combine(capture,"audio"));
    using(var file=File.OpenWrite(Path.Combine(capture,"audio","microphone-000001.wav")))file.SetLength(100);
    File.WriteAllText(Path.Combine(capture,"capture-info.json"),"{\"schemaVersion\":2,\"width\":640,\"height\":360,\"fps\":30,\"microphone\":true,\"systemAudio\":true}");
    List<string> journal=[];
    for(int i=0;i<3;i++)
    {
        string file=$"screen-{i:D6}.mp4";File.WriteAllBytes(Path.Combine(capture,file),[1,2,3]); // Storage-only fixture; real decode is a separate Windows test.
        journal.Add(JsonSerializer.Serialize(new{file,startUs=i*5_000_000,endUs=(i+1)*5_000_000}));
    }
    File.WriteAllText(Path.Combine(capture,"capture-journal.jsonl"),string.Join('\n',journal)+"\n");
    var project=CaptureImport.Import(store,id,capture,"부분 복구",_=>{});
    Equal(true,store.Load(id).Recovered);Equal(10_000_000L,new Timeline(project.Ranges).DurationUs);
    Equal(5,AudioStore.Load(store,id).Manifest.Segments.Length);
}));
Test("audio", "copied audio mutation is detected and cannot be silently replaced on retry",()=>InStore((store,id,root)=>
{
    string capture=Path.Combine(root,"capture");WriteAudioFixture(Path.Combine(capture,"audio"),false,true);
    var read=AudioStore.ReadCapture(capture,new(2,640,360,30,false,true));AudioStore.Import(store,id,capture,read.Manifest);
    using(var file=File.OpenWrite(Path.Combine(store.ProjectPath(id),"audio","system-000000.wav"))){file.Position=100;file.WriteByte(255);}
    Equal(1,AudioStore.Load(store,id).Issues.Length);
    Throws<InvalidDataException>(()=>AudioStore.Import(store,id,capture,read.Manifest));
}));
Test("audio", "unsafe journal paths and unterminated records never enter the project",()=>InStore((_,_,root)=>
{
    string capture=Path.Combine(root,"capture");string folder=Path.Combine(capture,"audio");WriteAudioFixture(folder,false,true);
    File.WriteAllText(Path.Combine(folder,"audio-journal.jsonl"),"{\"track\":\"system\",\"file\":\"../outside.wav\",\"startFrame\":0,\"frames\":240000}\n{\"track\":\"system\"");
    var read=AudioStore.ReadCapture(capture,new(2,640,360,30,false,true));Equal(2,read.Issues.Length);Equal(0,read.Manifest.Segments.Length);
    Equal(0,AudioStore.CommonRanges([new(0,5_000_000)],read.Manifest).Length);
}));
Test("audio", "audio selection and WAV format mismatches are rejected",()=>InStore((_,_,root)=>
{
    string capture=Path.Combine(root,"capture");WriteAudioFixture(Path.Combine(capture,"audio"),false,true);
    Throws<InvalidDataException>(()=>AudioStore.ReadCapture(capture,new(2,640,360,30,true,true)));
    using var wav=new MemoryStream(File.ReadAllBytes(Path.Combine(capture,"audio","system-000000.wav")));
    wav.Position=24;wav.WriteByte(0);Throws<InvalidDataException>(()=>AudioStore.VerifyWave(wav));
}));
Test("audio", "audio settings undo and defensive copies preserve the source tracks",()=>
{
    var document=Document(Guid.NewGuid().ToString("N")) with{SchemaVersion=3,Audio=[new("microphone"),new("system")]};
    var history=new EditHistory(document);document.Audio[0]=new("microphone",false);
    Equal(true,history.Current.Audio![0].Enabled);
    history.Apply(p=>p with{Audio=[p.Audio![0] with{Gain=.5},p.Audio[1] with{Enabled=false}]});
    Near(.5,history.Current.Audio![0].Gain);history.Undo();Near(1,history.Current.Audio![0].Gain);history.Redo();Equal(false,history.Current.Audio![1].Enabled);
    Throws<InvalidDataException>(()=>(document with{Audio=[new("system",Gain:double.NaN)]}).Validate());
    Throws<InvalidDataException>(()=>(document with{Audio=[new("system"),new("system")]}).Validate());
});
Test("audio", "v2 project migration keeps its original bytes and starts without audio",()=>InStore((store,id,root)=>
{
    Directory.CreateDirectory(store.ProjectPath(id));string path=Path.Combine(root,id,"project.json");
    File.WriteAllText(path,JsonSerializer.Serialize(Document(id) with{SchemaVersion=2},new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
    byte[] previous=File.ReadAllBytes(path);var read=store.Load(id);Equal<AudioTrack[]?>(null,read.Audio);
    store.Save(read with{Revision=1});Equal(3,store.Load(id).SchemaVersion);
    Check(previous.SequenceEqual(File.ReadAllBytes(Path.Combine(root,id,"project-v2.backup.json"))),"v2 backup changed");
}));

ProjectDocument MixerFixture(ProjectStore store, string id, string root)
{
    string capture=Path.Combine(root,"capture");WriteAudioFixture(Path.Combine(capture,"audio"));
    var recovery=AudioStore.ReadCapture(capture,new(2,640,360,30,true,true));
    AudioStore.Import(store,id,capture,recovery.Manifest);
    return new(3,id,"PCM mixer",15_000_000,640,360,[new(0,15_000_000)],[],Audio:[new("microphone",true,.5),new("system",true,.25)]);
}
Test("audio", "PCM mixing crosses segment and cut boundaries with exact stereo gain",()=>InStore((store,id,root)=>
{
    var p=MixerFixture(store,id,root) with {Ranges=[new(4_000_000,6_000_000),new(12_000_000,13_000_000)]};
    using var mix=new AudioMixer(store,p);Equal(144_000L,mix.FrameCount);
    short[] pcm=new short[16];var stats=mix.Read(47_997,pcm);
    for(int i=0;i<8;i++)
    {
        short expected=(short)Math.Round((97+i)%100*.75,MidpointRounding.AwayFromZero);
        Equal(expected,pcm[i*2]);Equal((short)-expected,pcm[i*2+1]);
    }
    mix.Read(95_997,pcm);Equal((short)73,pcm[0]);Equal((short)0,pcm[6]);Equal((short)1,pcm[8]);
    Equal(0L,stats.ClippedSamples);
}));
Test("audio", "fractional edits use one output sample grid and never interpolate across deleted audio",()=>InStore((store,id,root)=>
{
    var p=MixerFixture(store,id,root) with {Ranges=[new(10,1012),new(5_000_014,5_001_213)],Audio=[new("microphone"),new("system",false)]};
    using var mix=new AudioMixer(store,p);short[] all=new short[mix.FrameCount*2];mix.Read(0,all);
    for(int n=0;n<mix.FrameCount;n++)
    {
        decimal time=n*1_000_000m/48_000;var range=time<1002?p.Ranges[0]:p.Ranges[1];
        decimal source=(range.StartUs+time-(time<1002?0:1002))*48_000/1_000_000;
        long frame=(long)decimal.Floor(source);decimal fraction=source-frame;
        long following=Math.Min(frame+1,(long)decimal.Ceiling(range.EndUs*48_000m/1_000_000)-1);
        short expected=(short)decimal.Round(frame%100+(following%100-frame%100)*fraction,0,MidpointRounding.AwayFromZero);
        Equal(expected,all[n*2]);Equal((short)-expected,all[n*2+1]);
    }
    for(int first=0;first<mix.FrameCount;first+=7)
    {
        short[] chunk=new short[Math.Min(7,mix.FrameCount-first)*2];mix.Read(first,chunk);
        Check(chunk.SequenceEqual(all.Skip(first*2).Take(chunk.Length)),"Chunking changed PCM");
    }
    short[] repeat=new short[12];mix.Read(3,repeat);Check(repeat.SequenceEqual(all.Skip(6).Take(12)),"Seeking changed PCM");
}));
Test("audio", "mute and gain are immutable and zero gain produces explicit silence",()=>InStore((store,id,root)=>
{
    var p=MixerFixture(store,id,root);using var mix=new AudioMixer(store,p);
    p.Audio![0]=new("microphone",false);p.Audio[1]=new("system",false);
    short[] sample=new short[2];mix.Read(50,sample);Equal((short)38,sample[0]);Equal(true,mix.IsAudible);
    using var muted=new AudioMixer(store,p);muted.Read(50,sample);Equal((short)0,sample[0]);Equal(false,muted.IsAudible);
    using var zero=new AudioMixer(store,p with {Audio=[new("microphone",true,0),new("system",true,0)]});
    zero.Read(50,sample);Equal((short)0,sample[0]);Equal(false,zero.IsAudible);
}));
Test("audio", "missing or changed audio is rejected even when a track is muted",()=>InStore((store,id,root)=>
{
    var p=MixerFixture(store,id,root) with {Audio=[new("microphone",false),new("system",false)]};
    using(var file=File.OpenWrite(Path.Combine(store.ProjectPath(id),"audio","microphone-000001.wav"))){file.Position=44;file.WriteByte(255);}
    Throws<InvalidDataException>(()=>new AudioMixer(store,p));
    Throws<InvalidDataException>(()=>new AudioMixer(store,p with {Audio=[new("system")]}));
}));
Test("audio", "PCM bounds and disposed readers reject invalid requests",()=>InStore((store,id,root)=>
{
    var mix=new AudioMixer(store,MixerFixture(store,id,root));
    Throws<ArgumentOutOfRangeException>(()=>mix.Read(-1,new short[2]));
    Throws<ArgumentOutOfRangeException>(()=>mix.Read(mix.FrameCount,new short[2]));
    Throws<ArgumentOutOfRangeException>(()=>mix.Read(0,new short[3]));
    mix.Read(mix.FrameCount,[]);mix.Dispose();mix.Dispose();
    Throws<ObjectDisposedException>(()=>mix.Read(0,new short[2]));
}));
Test("audio", "loud source sum saturates without wrapping and reports clipping",()=>InStore((store,id,root)=>
{
    string capture=Path.Combine(root,"capture");WriteAudioFixture(Path.Combine(capture,"audio"));
    foreach(var role in new[]{"microphone","system"})
    {
        using var writer=new BinaryWriter(File.OpenWrite(Path.Combine(capture,"audio",role+"-000000.wav")));
        writer.BaseStream.Position=44;writer.Write((short)20_000);writer.Write((short)-20_000);
    }
    AudioStore.Import(store,id,capture,AudioStore.ReadCapture(capture,new(2,640,360,30,true,true)).Manifest);
    var p=new ProjectDocument(3,id,"loud",15_000_000,640,360,[new(0,1_000_000)],[],Audio:[new("microphone"),new("system")]);
    using var mix=new AudioMixer(store,p);short[] sample=new short[2];var stats=mix.Read(0,sample);
    Equal(short.MaxValue,sample[0]);Equal(short.MinValue,sample[1]);Equal(2L,stats.ClippedSamples);Near(40000.0/32768,stats.PeakBeforeClipping);
}));

AudioManifest ShortPcmFixture(string capture, params (long Start, int Frames)[] parts)
{
    string folder=Path.Combine(capture,"audio");Directory.CreateDirectory(folder);List<AudioSegment> records=[];
    for(int i=0;i<parts.Length;i++)
    {
        var part=parts[i];string file=$"system-{i:D6}.wav",path=Path.Combine(folder,file);
        using(var writer=new BinaryWriter(File.Create(path)))
        {
            writer.Write("RIFF"u8);writer.Write(part.Frames*4+36);writer.Write("WAVEfmt "u8);writer.Write(16);
            writer.Write((short)1);writer.Write((short)2);writer.Write(48000);writer.Write(192000);writer.Write((short)4);writer.Write((short)16);writer.Write("data"u8);writer.Write(part.Frames*4);
            for(int f=0;f<part.Frames;f++){writer.Write((short)123);writer.Write((short)-123);}
        }
        records.Add(new(new("system",file,part.Start,part.Frames),new FileInfo(path).Length,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))));
    }
    return new(1,[new("system")],records.ToArray());
}
Test("audio", "fractional PCM tail never promises an extra unavailable sample",()=>
{
    for(int count=1;count<=12;count++)InStore((store,id,root)=>
    {
        string capture=Path.Combine(root,"capture");var manifest=ShortPcmFixture(capture,(0,count));AudioStore.Import(store,id,capture,manifest);
        var ranges=AudioStore.CommonRanges([new(0,1000)],manifest);
        var p=new ProjectDocument(3,id,"short PCM",1000,640,360,ranges,[],Audio:[new("system")]);
        using var mixer=new AudioMixer(store,p);Equal((long)count,mixer.FrameCount);
        short[] pcm=new short[count*2];mixer.Read(0,pcm);Equal((short)123,pcm[^2]);Equal((short)-123,pcm[^1]);
    });
});
Test("audio", "recovered PCM start and adjacent fractional segments form exact safe coverage",()=>InStore((store,id,root)=>
{
    string capture=Path.Combine(root,"capture");var manifest=ShortPcmFixture(capture,(1,2),(3,7));AudioStore.Import(store,id,capture,manifest);
    var ranges=AudioStore.CommonRanges([new(0,1000)],manifest);Equal(1,ranges.Length);Equal(new TimeRange(21,208),ranges[0]);
    var p=new ProjectDocument(3,id,"recovered PCM",1000,640,360,ranges,[],Audio:[new("system")],Recovered:true);
    using var mixer=new AudioMixer(store,p);Equal(9L,mixer.FrameCount);short[] pcm=new short[18];mixer.Read(0,pcm);
    for(int i=0;i<9;i++){Equal((short)123,pcm[i*2]);Equal((short)-123,pcm[i*2+1]);}
}));

var selected = tests.Where(t => suite == "all" || t.Suite == suite).ToArray();
if (selected.Length == 0) { Console.Error.WriteLine($"Unknown suite: {suite}"); return 2; }
var results = new List<object>(); int failed = 0;
foreach (var test in selected)
{
    var watch = Stopwatch.StartNew(); string? error = null;
    try { test.Run(); } catch (Exception ex) { error = ex.ToString(); failed++; }
    Console.WriteLine($"{(error is null ? "PASS" : "FAIL")} {test.Suite}: {test.Name}{(error is null ? "" : "\n" + error)}");
    results.Add(new { suite = test.Suite, name = test.Name, passed = error is null, milliseconds = watch.Elapsed.TotalMilliseconds, error });
}
Directory.CreateDirectory(".artifacts");
File.WriteAllText(Path.Combine(".artifacts", $"core-tests-{suite}.json"), JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), tests = selected.Length, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{selected.Length - failed}/{selected.Length} passed; evidence .artifacts/core-tests-{suite}.json");
return failed == 0 ? 0 : 1;

sealed class GeneratedStream(long length, long failAfter) : Stream
{
    private long position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count > 128 * 1024) throw new InvalidOperationException("Unbounded read request");
        if (position >= failAfter) throw new IOException("Injected source read failure");
        int read = (int)Math.Min(count, Math.Min(length - position, failAfter - position));
        for (int i = 0; i < read; i++) buffer[offset + i] = (byte)((position + i) % 251);
        position += read; return read;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
