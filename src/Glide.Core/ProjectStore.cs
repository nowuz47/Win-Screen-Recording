using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Glide.Core;

public sealed record ProjectDocument(int SchemaVersion, string Id, string Name, long DurationUs, int Width, int Height, TimeRange[] Ranges, ZoomSegment[] Zooms, long Revision = 0,
    RenderStyle? Style = null, string Origin = "recording", string Aspect = "16:9", bool UsePresentationCamera = true, AudioTrack[]? Audio = null, bool Recovered = false)
{
    public const int CurrentSchema = 3;
    public void Validate()
    {
        if (SchemaVersion is not (1 or 2 or CurrentSchema)) throw new InvalidDataException($"Unsupported project schema {SchemaVersion}.");
        if (!Guid.TryParseExact(Id, "N", out _) || string.IsNullOrWhiteSpace(Name) || Name.Length > 200 || DurationUs <= 0 || Width <= 0 || Height <= 0 || Width > 16384 || Height > 16384 || Revision < 0)
            throw new InvalidDataException("Invalid project metadata.");
        if (Ranges is null || Zooms is null) throw new InvalidDataException("Missing project tracks.");
        if (Origin is not ("recording" or "presentation") || Aspect is not ("16:9" or "1:1" or "9:16")) throw new InvalidDataException("Unsupported project profile.");
        (Style ?? new()).Validate();
        if (Audio is not null)
        {
            if (Audio.Length > 2 || Audio.Any(t => t is null) || Audio.Select(t => t.Role).Distinct().Count() != Audio.Length
                || (SchemaVersion < 3 && Audio.Length != 0)) throw new InvalidDataException("Invalid project audio tracks.");
            foreach (var track in Audio) track.Validate();
        }
        var timeline = new Timeline(Ranges);
        if (timeline.Ranges.Any(x => x.EndUs > DurationUs)) throw new InvalidDataException("Edit exceeds source duration.");
        foreach (var z in Zooms) { Motion.ValidateZoom(z); if (z.Range.EndUs > DurationUs) throw new InvalidDataException("Zoom exceeds duration."); }
    }
}

public sealed record SegmentRecord(int Sequence, string FileName, long StartUs, long EndUs, long Bytes, string Sha256);
public sealed record RecoveryResult(IReadOnlyList<SegmentRecord> Segments, IReadOnlyList<string> Issues);

public static class DurableFile
{
    public static void WriteNew(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
    }

    public static void Replace(string path, ReadOnlySpan<byte> bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { WriteNew(temporary, bytes); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class ProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
    private readonly string root;

    public ProjectStore(string root)
    {
        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
        AssertNoLinks(this.root);
    }

    public string ProjectPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Project ID must be a canonical UUID.");
        return Resolve(id);
    }

    public void Save(ProjectDocument project)
    {
        project.Validate();
        string directory = ProjectPath(project.Id);
        Directory.CreateDirectory(directory);
        // Lock covers compare-and-write across ProjectStore instances and processes.
        using var lease = new FileStream(Path.Combine(directory, ".write-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string path = Resolve(project.Id, "project.json");
        if (File.Exists(path))
        {
            var previous = Load(project.Id);
            if (project.Revision <= previous.Revision) throw new InvalidOperationException("Stale project revision.");
            string backup = $"project-v{previous.SchemaVersion}.backup.json";
            if (previous.SchemaVersion < ProjectDocument.CurrentSchema && !File.Exists(Resolve(project.Id, backup)))
                DurableFile.WriteNew(Resolve(project.Id, backup), File.ReadAllBytes(path));
        }
        DurableFile.Replace(path, JsonSerializer.SerializeToUtf8Bytes(project with { SchemaVersion = ProjectDocument.CurrentSchema }, JsonOptions));
    }

    public ProjectDocument Load(string id)
    {
        _ = ProjectPath(id);
        string path = Resolve(id, "project.json");
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Project metadata exceeds size limit.");
        var project = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("Empty project.");
        project.Validate();
        if (project.Id != id) throw new InvalidDataException("Project identity mismatch.");
        return project;
    }

    public IReadOnlyList<ProjectDocument> List()
    {
        List<ProjectDocument> result = [];
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _) || !File.Exists(Path.Combine(directory, "project.json"))) continue;
            result.Add(Load(id));
        }
        return result;
    }

    public SegmentRecord CommitSegment(string id, ReadOnlySpan<byte> encodedMedia, long startUs, long endUs)
    {
        if (encodedMedia.IsEmpty) throw new ArgumentException("Empty segment.");
        // In-memory convenience is intended for small inputs; capture import uses a bounded stream.
        using var source = new MemoryStream(encodedMedia.ToArray(), writable: false);
        return CommitSegment(id, source, startUs, endUs);
    }

    public SegmentRecord CommitSegment(string id, Stream encodedMedia, long startUs, long endUs)
    {
        _ = ProjectPath(id);
        new TimeRange(startUs, endUs).Validate();
        if (!encodedMedia.CanRead) throw new ArgumentException("Unreadable segment.");
        string directory = Resolve(id);
        Directory.CreateDirectory(directory);
        using var lease = new FileStream(Resolve(id, ".segment-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string journal = Resolve(id, "segments.jsonl");
        var recovery = Recover(id);
        if (recovery.Issues.Count > 0) throw new InvalidDataException("Journal needs recovery before adding segments.");
        if (recovery.Segments.Count > 0 && startUs < recovery.Segments[^1].EndUs) throw new ArgumentException("Overlapping segment.");
        int sequence = recovery.Segments.Count;
        string fileName = $"screen-{sequence:D6}.mp4";
        string mediaDirectory = Resolve(id, "media");
        Directory.CreateDirectory(mediaDirectory);
        string mediaPath = Resolve(id, "media", fileName);
        // If a previous crash left an orphan, preserve it for explicit recovery instead of overwriting it.
        string temporary = mediaPath + "." + Guid.NewGuid().ToString("N") + ".pending";
        long bytes = 0;
        string digest;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                byte[] buffer = new byte[65536];
                int read;
                while ((read = encodedMedia.Read(buffer)) > 0)
                {
                    bytes = checked(bytes + read);
                    if (bytes > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Segment exceeds 2 GiB limit.");
                    output.Write(buffer, 0, read); hash.AppendData(buffer, 0, read);
                }
                if (bytes == 0) throw new ArgumentException("Empty segment.");
                output.Flush(flushToDisk: true);
            }
            digest = Convert.ToHexString(hash.GetHashAndReset());
            File.Move(temporary, mediaPath, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        var record = new SegmentRecord(sequence, fileName, startUs, endUs, bytes, digest);
        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, JsonOptions) + "\n");
        using var stream = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(line); stream.Flush(flushToDisk: true);
        return record;
    }

    public RecoveryResult Recover(string id)
    {
        _ = ProjectPath(id);
        var journal = Resolve(id, "segments.jsonl");
        if (!File.Exists(journal)) return new([], []);
        if (new FileInfo(journal).Length > 16 * 1024 * 1024) throw new InvalidDataException("Journal exceeds size limit.");
        var text = File.ReadAllText(journal);
        string[] lines = text.Split('\n');
        List<SegmentRecord> good = [];
        List<string> issues = [];
        long previousEnd = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (i == lines.Length - 1 && lines[i].Length == 0) continue;
            try
            {
                // A record without a terminating newline was never committed.
                if (i == lines.Length - 1) throw new InvalidDataException("Uncommitted journal tail.");
                var record = JsonSerializer.Deserialize<SegmentRecord>(lines[i], JsonOptions) ?? throw new InvalidDataException("Empty record.");
                if (record.Sequence != i || record.FileName != $"screen-{i:D6}.mp4" || record.StartUs < previousEnd || record.EndUs <= record.StartUs || record.Bytes <= 0 || record.Sha256 is null)
                    throw new InvalidDataException("Invalid segment record.");
                previousEnd = record.EndUs;
                string path = Resolve(id, "media", record.FileName);
                if (!File.Exists(path) || new FileInfo(path).Length != record.Bytes) throw new InvalidDataException("Missing or truncated media.");
                using var media = File.OpenRead(path);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(media)), record.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("Media checksum mismatch.");
                good.Add(record);
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or IOException or ArgumentException)
            {
                issues.Add($"segment {i}: {e.Message}");
            }
        }
        return new(good, issues);
    }

    private string Resolve(params string[] parts)
    {
        foreach (var part in parts)
            if (string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || Path.IsPathRooted(part))
                throw new ArgumentException("Unsafe project path.");
        var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        AssertNoLinks(path);
        return path;
    }

    private static void AssertNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symbolic links and junctions are not allowed in a project storage path.");
        }
    }
}
