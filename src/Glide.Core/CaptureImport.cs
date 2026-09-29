using System.Text.Json;

namespace Glide.Core;

public sealed record CaptureInfo(int SchemaVersion, int Width, int Height, int Fps, bool Microphone = false, bool SystemAudio = false);
public sealed record CapturePart(string File, long StartUs, long EndUs);
public sealed record CapturedPointer(long TimeUs, double X, double Y, bool Visible, bool Left, bool Right);
public sealed record CaptureManifest(CaptureInfo Info, CapturePart[] Parts, string[] Issues);

public static class CaptureImport
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static CaptureManifest ReadManifest(string directory)
    {
        var info = JsonSerializer.Deserialize<CaptureInfo>(ReadLimited(Path.Combine(directory, "capture-info.json"), 65536), Json)
            ?? throw new InvalidDataException("Missing capture info.");
        if (info.SchemaVersion is not (1 or 2) || info.Width < 2 || info.Height < 2 || info.Width > 7680 || info.Height > 4320 || info.Width % 2 != 0 || info.Height % 2 != 0 || info.Fps is not (30 or 60)
            || (info.SchemaVersion == 1 && (info.Microphone || info.SystemAudio)) || (info.SchemaVersion == 2 && !info.Microphone && !info.SystemAudio))
            throw new InvalidDataException("Invalid capture geometry.");
        string journal = ReadLimited(Path.Combine(directory, "capture-journal.jsonl"), 1024 * 1024);
        string[] lines = journal.Split('\n');
        List<CapturePart> parts = [];
        List<string> issues = [];
        long previousEnd = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (i == lines.Length - 1 && lines[i].Length == 0) continue;
            try
            {
                if (i == lines.Length - 1) throw new InvalidDataException("Uncommitted capture tail.");
                var part = JsonSerializer.Deserialize<CapturePart>(lines[i], Json) ?? throw new InvalidDataException("Empty capture record.");
                if (part.File != $"screen-{i:D6}.mp4" || part.StartUs < previousEnd || part.EndUs <= part.StartUs || part.EndUs > 1_800_100_000)
                    throw new InvalidDataException("Invalid capture segment.");
                previousEnd = part.EndUs;
                string path = Path.Combine(directory, part.File);
                CheckRegularPath(path);
                var length = new FileInfo(path).Length;
                if (length == 0 || length > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Invalid media size.");
                parts.Add(part);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or ArgumentException)
            { issues.Add($"segment {i}: {e.GetType().Name}"); }
        }
        return new(info, parts.ToArray(), issues.ToArray());
    }

    // Every accepted segment is decoded by the caller before it becomes a saved project.
    // Original capture files stay in place, including uncommitted tails, for later recovery.
    public static ProjectDocument Import(ProjectStore store, string id, string directory, string name, Action<string> verifyVideo)
    {
        ArgumentNullException.ThrowIfNull(verifyVideo);
        var manifest = ReadManifest(directory);
        if (manifest.Parts.Length == 0) throw new InvalidDataException("No committed media available.");
        var recovery = store.Recover(id);
        if (recovery.Issues.Count > 0) throw new InvalidDataException("Project media requires recovery.");
        if (recovery.Segments.Count > manifest.Parts.Length) throw new InvalidDataException("Capture identity mismatch.");
        for (int i = 0; i < manifest.Parts.Length; i++)
        {
            var part = manifest.Parts[i];
            var path = Path.Combine(directory, part.File);
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            verifyVideo(path);
            if (i < recovery.Segments.Count)
            {
                var saved = recovery.Segments[i];
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source));
                if (saved.StartUs != part.StartUs || saved.EndUs != part.EndUs || saved.Sha256 != hash)
                    throw new InvalidDataException("Previously imported segment changed.");
                continue;
            }
            store.CommitSegment(id, source, part.StartUs, part.EndUs);
        }
        long duration = manifest.Parts[^1].EndUs;
        var audio = AudioStore.ReadCapture(directory, manifest.Info);
        var ranges = AudioStore.CommonRanges(manifest.Parts.Select(p => new TimeRange(p.StartUs, p.EndUs)), audio.Manifest);
        if (ranges.Length == 0) throw new InvalidDataException("No jointly committed video and selected audio are available.");
        AudioStore.Import(store, id, directory, audio.Manifest);
        var clicks = ReadPointers(directory, duration).Clicks;
        bool presentation = File.Exists(Path.Combine(directory, "presentation.jsonl"));
        if (presentation) _ = ReadPresentation(directory, duration);
        bool interrupted = new[] { Path.Combine(directory, "capture-error.json"), Path.Combine(directory, "audio", "audio-error.json") }
            .Any(path => { CheckRegularPath(path); return File.Exists(path); });
        var document = new ProjectDocument(ProjectDocument.CurrentSchema, id, name, duration, manifest.Info.Width, manifest.Info.Height,
            ranges, presentation ? [] : Motion.Plan(clicks, duration).ToArray(),
            Origin: presentation ? "presentation" : "recording", Audio: audio.Manifest.Tracks,
            Recovered: interrupted || manifest.Issues.Length > 0 || audio.Issues.Length > 0 || ranges.Sum(r => r.DurationUs) < manifest.Parts.Sum(p => p.EndUs - p.StartUs));
        store.Save(document);
        return document;
    }

    public static (CursorSample[] Samples, ClickEvent[] Clicks) ReadPointers(string directory, long durationUs)
    {
        var path = Path.Combine(directory, "cursor.jsonl");
        if (!File.Exists(path)) return ([], []);
        CheckRegularPath(path);
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Pointer metadata exceeds limit.");
        List<CursorSample> samples = [];
        List<ClickEvent> clicks = [];
        CapturedPointer? down = null;
        bool dragged = false, held = false;
        foreach (var line in File.ReadLines(path))
        {
            // Incomplete input tails may accompany a recoverable recording interruption.
            CapturedPointer? p;
            try { p = JsonSerializer.Deserialize<CapturedPointer>(line, Json); } catch (JsonException) { break; }
            if (p is null || p.TimeUs < 0 || p.TimeUs >= durationUs || !new PointD(p.X, p.Y).IsValid || (samples.Count > 0 && p.TimeUs <= samples[^1].TimeUs)) break;
            samples.Add(new(p.TimeUs, new(p.X, p.Y), p.Visible));
            bool button = p.Left || p.Right;
            if (button && !held && p.Visible) { down = p; dragged = false; }
            if (down is not null)
            {
                dragged |= Math.Abs(p.X - down.X) + Math.Abs(p.Y - down.Y) > .006;
                if (!button)
                {
                    clicks.Add(new(down.TimeUs, new(down.X, down.Y), dragged));
                    down = null;
                }
            }
            held = button;
        }
        return (samples.ToArray(), clicks.ToArray());
    }

    public static PresentationTrack? ReadPresentation(string directory, long durationUs)
    {
        string path = Path.Combine(directory, "presentation.jsonl");
        if (!File.Exists(path)) return null;
        string data = ReadLimited(path, 16 * 1024 * 1024);
        var lines = data.Split('\n');
        List<PresentationSample> samples = [];
        // Like the capture journal, a final unterminated record is an uncommitted tail.
        foreach (var line in lines.Take(lines.Length - 1))
        {
            var sample = JsonSerializer.Deserialize<PresentationSample>(line, Json) ?? throw new InvalidDataException("Empty presentation pose.");
            if (sample.TimeUs < durationUs) samples.Add(sample);
        }
        return new(samples, durationUs);
    }

    internal static string ReadLimited(string path, long maximum)
    {
        CheckRegularPath(path);
        // A finalized segment can be inspected while the recorder still has its journal open.
        // Snapshot a bounded byte count; a concurrent append is considered on the next read.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length;
        if (length > maximum) throw new InvalidDataException("Capture metadata exceeds limit.");
        byte[] bytes = new byte[checked((int)length)];
        stream.ReadExactly(bytes);
        return System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }

    internal static void CheckRegularPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Capture path contains a symbolic link or junction.");
    }
}
