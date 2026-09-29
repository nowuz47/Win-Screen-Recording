using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Glide.Core;

public sealed record AudioTrack(string Role, bool Enabled = true, double Gain = 1)
{
    public void Validate()
    {
        if (Role is not ("microphone" or "system") || !double.IsFinite(Gain) || Gain < 0 || Gain > 4)
            throw new InvalidDataException("Invalid audio track settings.");
    }
}
public sealed record AudioPart(string Track, string File, long StartFrame, long Frames)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeRange Range => new((StartFrame * 1_000_000 + 47_999) / 48_000, (StartFrame + Frames) * 1_000_000 / 48_000);
}
public sealed record AudioSegment(AudioPart Part, long Bytes, string Sha256);
public sealed record AudioManifest(int SchemaVersion, AudioTrack[] Tracks, AudioSegment[] Segments);
public sealed record AudioRecovery(AudioManifest Manifest, string[] Issues);

// Separate source tracks remain immutable. Segment copies and their final manifest
// can be retried after interruption, including a crash between copy and publication.
public static class AudioStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record CaptureAudioInfo(int SchemaVersion, int SampleRate, int Channels, string Format, bool Microphone, bool System);

    public static long VerifyWave(Stream stream)
    {
        if (!stream.CanSeek || !stream.CanRead) throw new InvalidDataException("Audio verification requires a seekable stream.");
        stream.Position = 0;
        Span<byte> header = stackalloc byte[44]; stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header.Slice(8,8).SequenceEqual("WAVEfmt "u8) || !header.Slice(36,4).SequenceEqual("data"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) != 16
            || BinaryPrimitives.ReadUInt16LittleEndian(header[20..]) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(header[22..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) != 48_000
            || BinaryPrimitives.ReadUInt32LittleEndian(header[28..]) != 192_000
            || BinaryPrimitives.ReadUInt16LittleEndian(header[32..]) != 4
            || BinaryPrimitives.ReadUInt16LittleEndian(header[34..]) != 16)
            throw new InvalidDataException("Unsupported audio source format.");
        long bytes = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);
        if (bytes == 0 || bytes > 960_000 || bytes % 4 != 0 || stream.Length != bytes + 44
            || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != bytes + 36)
            throw new InvalidDataException("Invalid audio source length.");
        // Fixed PCM contains no compressed payload to parse; every four bytes form
        // one complete stereo sample frame. SHA-256 covers the full payload below.
        stream.Position = 0; return bytes / 4;
    }

    private static AudioSegment Inspect(string directory, AudioPart part)
    {
        ValidatePart(part);
        string path = Path.Combine(directory, part.File); CaptureImport.CheckRegularPath(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (VerifyWave(input) != part.Frames) throw new InvalidDataException("Audio journal and WAV frame counts differ.");
        return new(part, input.Length, Convert.ToHexString(SHA256.HashData(input)));
    }

    private static void ValidatePart(AudioPart part)
    {
        if (part.Track is not ("microphone" or "system") || part.StartFrame < 0 || part.Frames <= 0 || part.Frames > 240_000
            || part.StartFrame > 86_400_000 - part.Frames || part.File is null)
            throw new InvalidDataException("Invalid audio segment.");
        string prefix = part.Track + "-";
        if (part.File.Length != prefix.Length + 10 || !part.File.StartsWith(prefix, StringComparison.Ordinal)
            || !part.File.EndsWith(".wav", StringComparison.Ordinal)
            || !int.TryParse(part.File.AsSpan(prefix.Length, 6), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int sequence)
            || part.File != $"{part.Track}-{sequence:D6}.wav")
            throw new InvalidDataException("Unsafe audio filename.");
    }

    public static AudioRecovery ReadCapture(string captureDirectory, CaptureInfo capture)
    {
        var tracks = new List<AudioTrack>();
        if (capture.Microphone) tracks.Add(new("microphone"));
        if (capture.SystemAudio) tracks.Add(new("system"));
        if (tracks.Count == 0) return new(new(1, [], []), []);
        string directory = Path.Combine(captureDirectory, "audio");
        var info = JsonSerializer.Deserialize<CaptureAudioInfo>(CaptureImport.ReadLimited(Path.Combine(directory, "audio-info.json"), 65_536), Json)
            ?? throw new InvalidDataException("Missing audio info.");
        if (info != new CaptureAudioInfo(1, 48_000, 2, "pcm_s16le", capture.Microphone, capture.SystemAudio))
            throw new InvalidDataException("Audio selection changed.");
        var lines = CaptureImport.ReadLimited(Path.Combine(directory, "audio-journal.jsonl"), 1_048_576).Split('\n');
        if (lines.Length > 4096) throw new InvalidDataException("Audio journal exceeds record limit.");
        var segments = new List<AudioSegment>(); var issues = new List<string>();
        var next = tracks.ToDictionary(t => t.Role, _ => 0); var previous = tracks.ToDictionary(t => t.Role, _ => 0L);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i == lines.Length - 1 && lines[i].Length == 0) continue;
            try
            {
                if (i == lines.Length - 1) throw new InvalidDataException("Uncommitted audio tail.");
                var part = JsonSerializer.Deserialize<AudioPart>(lines[i], Json) ?? throw new InvalidDataException("Empty audio record.");
                ValidatePart(part);
                if (!next.ContainsKey(part.Track) || part.File != $"{part.Track}-{next[part.Track]:D6}.wav" || part.StartFrame != previous[part.Track])
                    throw new InvalidDataException("Audio sequence or selected track mismatch.");
                next[part.Track]++; previous[part.Track] = part.StartFrame + part.Frames;
                segments.Add(Inspect(directory, part));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or ArgumentException)
            { issues.Add($"audio record {i}: {e.GetType().Name}"); }
        }
        return new(new(1, tracks.ToArray(), segments.ToArray()), issues.ToArray());
    }

    public static void Import(ProjectStore store, string id, string captureDirectory, AudioManifest manifest)
    {
        if (manifest.Tracks.Length == 0) return;
        string project = store.ProjectPath(id); Directory.CreateDirectory(project);
        CaptureImport.CheckRegularPath(Path.Combine(project, ".audio-lock"));
        using var lease = new FileStream(Path.Combine(project, ".audio-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string folder = Path.Combine(project, "audio"); CaptureImport.CheckRegularPath(folder); Directory.CreateDirectory(folder);
        string path = Path.Combine(project, "audio.json");
        if (File.Exists(path))
        {
            var old = Load(store, id);
            if (old.Issues.Length > 0 || !old.Manifest.Tracks.SequenceEqual(manifest.Tracks)
                || old.Manifest.Segments.Any(s => !manifest.Segments.Contains(s)))
                throw new InvalidDataException("Previously imported audio changed.");
        }
        foreach (var segment in manifest.Segments)
        {
            ValidatePart(segment.Part);
            string source = Path.Combine(captureDirectory, "audio", segment.Part.File);
            CaptureImport.CheckRegularPath(source);
            string destination = Path.Combine(folder, segment.Part.File); CaptureImport.CheckRegularPath(destination);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (VerifyWave(input) != segment.Part.Frames || Convert.ToHexString(SHA256.HashData(input)) != segment.Sha256 || input.Length != segment.Bytes)
                throw new InvalidDataException("Audio changed before import.");
            if (File.Exists(destination))
            {
                if (Inspect(folder, segment.Part) != segment) throw new InvalidDataException("Existing audio copy differs.");
                continue;
            }
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".pending";
            try
            {
                input.Position = 0;
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, FileOptions.WriteThrough))
                { input.CopyTo(output, 65_536); output.Flush(flushToDisk: true); }
                File.Move(temporary, destination, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        DurableFile.Replace(path, JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
    }

    public static AudioRecovery Load(ProjectStore store, string id)
    {
        string project = store.ProjectPath(id);
        var manifest = JsonSerializer.Deserialize<AudioManifest>(CaptureImport.ReadLimited(Path.Combine(project, "audio.json"), 1_048_576), Json)
            ?? throw new InvalidDataException("Missing audio manifest.");
        if (manifest.SchemaVersion != 1 || manifest.Tracks is null || manifest.Segments is null || manifest.Tracks.Length is < 1 or > 2 || manifest.Segments.Length > 4096
            || manifest.Tracks.Any(t => t is null) || manifest.Segments.Any(s => s is null || s.Part is null)
            || manifest.Tracks.Select(t => t.Role).Distinct().Count() != manifest.Tracks.Length)
            throw new InvalidDataException("Invalid audio manifest.");
        foreach (var track in manifest.Tracks) track.Validate();
        var good = new List<AudioSegment>(); var issues = new List<string>();
        var ends = manifest.Tracks.ToDictionary(t => t.Role, _ => 0L); var names = new HashSet<string>();
        foreach (var segment in manifest.Segments)
        {
            try
            {
                ValidatePart(segment.Part);
                if (!ends.ContainsKey(segment.Part.Track) || !names.Add(segment.Part.File) || segment.Part.StartFrame < ends[segment.Part.Track])
                    throw new InvalidDataException("Audio records overlap or refer to an undeclared track.");
                ends[segment.Part.Track] = segment.Part.StartFrame + segment.Part.Frames;
                if (Inspect(Path.Combine(project, "audio"), segment.Part) != segment) throw new InvalidDataException("Audio content changed.");
                good.Add(segment);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
            { issues.Add($"audio source: {e.GetType().Name}"); }
        }
        return new(manifest with { Segments = good.ToArray() }, issues.ToArray());
    }

    public static TimeRange[] CommonRanges(IEnumerable<TimeRange> video, AudioManifest audio)
    {
        var ranges = video.ToArray();
        foreach (var track in audio.Tracks)
        {
            // Merge on the exact sample grid before rounding inward to whole
            // microseconds. Rounding outward can expose a nonexistent tail sample;
            // rounding each adjacent part separately can invent a tiny hole.
            var spans = new List<(long Start, long End)>();
            foreach (var part in audio.Segments.Where(s => s.Part.Track == track.Role).Select(s => s.Part).OrderBy(p => p.StartFrame))
            {
                long end = part.StartFrame + part.Frames;
                if (spans.Count > 0 && part.StartFrame <= spans[^1].End) spans[^1] = (spans[^1].Start, Math.Max(spans[^1].End, end));
                else spans.Add((part.StartFrame, end));
            }
            var coverage = spans.Select(s => new TimeRange((s.Start * 1_000_000 + 47_999) / 48_000, s.End * 1_000_000 / 48_000)).ToArray();
            var kept = new List<TimeRange>();
            foreach (var range in ranges)
            foreach (var available in coverage)
            {
                long start = Math.Max(range.StartUs, available.StartUs), end = Math.Min(range.EndUs, available.EndUs);
                if (end <= start) continue;
                if (kept.Count > 0 && start <= kept[^1].EndUs) kept[^1] = new(kept[^1].StartUs, Math.Max(end, kept[^1].EndUs));
                else kept.Add(new(start, end));
            }
            ranges = kept.ToArray();
        }
        return ranges;
    }
}
