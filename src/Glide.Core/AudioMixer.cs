using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Glide.Core;

public readonly record struct AudioMixStats(double PeakBeforeClipping, long ClippedSamples);

// An immutable edit, verified read leases and at most one five-second PCM cache per
// source track. Preview and export request the same 48 kHz output sample positions.
public sealed class AudioMixer : IDisposable
{
    private readonly object gate = new();
    private readonly List<TrackReader> tracks = [];
    private readonly (TimeRange Source, long OutputStartUs, long OutputEndUs)[] ranges;
    private bool disposed;
    public long FrameCount { get; }
    public long DurationUs { get; }
    public bool IsAudible { get; }

    public AudioMixer(ProjectStore store, ProjectDocument project)
    {
        project.Validate();
        var timeline = new Timeline(project.Ranges);
        DurationUs = timeline.DurationUs;
        if (DurationUs > 1_800_100_000 || project.DurationUs > 1_800_100_000)
            throw new InvalidDataException("Audio exceeds the recording limit.");
        FrameCount = (DurationUs * 48_000 + 999_999) / 1_000_000;
        long offset = 0;
        ranges = timeline.Ranges.Select(r => { long start = offset; offset += r.DurationUs; return (r, start, offset); }).ToArray();
        var settings = project.Audio?.ToArray() ?? [];
        IsAudible = settings.Any(t => t.Enabled && t.Gain > 0);
        if (settings.Length == 0) return;
        var recovery = AudioStore.Load(store, project.Id);
        if (recovery.Issues.Length != 0 || !settings.Select(t => t.Role).Order().SequenceEqual(recovery.Manifest.Tracks.Select(t => t.Role).Order()))
            throw new InvalidDataException("Audio source requires recovery before rendering.");
        // Check each retained interval separately; adjacent edits need not have been merged.
        foreach (var range in timeline.Ranges)
            if (!AudioStore.CommonRanges([range], recovery.Manifest).SequenceEqual(new[] { range }))
                throw new InvalidDataException("Edit references missing audio.");
        try
        {
            foreach (var setting in settings)
                tracks.Add(new TrackReader(Path.Combine(store.ProjectPath(project.Id), "audio"), setting,
                    recovery.Manifest.Segments.Where(s => s.Part.Track == setting.Role).OrderBy(s => s.Part.StartFrame).ToArray()));
        }
        catch { Dispose(); throw; }
    }

    // Values are interleaved signed 16-bit stereo. Callers must surface clipping;
    // saturation prevents integer wrap but is not a loudness normalizer/limiter.
    public AudioMixStats Read(long firstFrame, Span<short> destination)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int count = destination.Length / 2;
            if (destination.Length % 2 != 0 || count > 48_000 || firstFrame < 0 || firstFrame > FrameCount - count)
                throw new ArgumentOutOfRangeException(nameof(firstFrame));
            int rangeIndex = 0;
            double peak = 0; long clipped = 0;
            for (int i = 0; i < count; ++i)
            {
                long outputNumerator = (firstFrame + i) * 1_000_000;
                while (rangeIndex + 1 < ranges.Length && outputNumerator >= ranges[rangeIndex].OutputEndUs * 48_000) rangeIndex++;
                var range = ranges[rangeIndex];
                long sourceNumerator = outputNumerator + (range.Source.StartUs - range.OutputStartUs) * 48_000;
                long sourceFrame = sourceNumerator / 1_000_000;
                double fraction = sourceNumerator % 1_000_000 / 1_000_000.0;
                // Never interpolate across a removed interval, or beyond the last
                // sample at the end of a retained source range.
                long following = Math.Min(sourceFrame + 1, (range.Source.EndUs * 48_000 + 999_999) / 1_000_000 - 1);
                for (int channel = 0; channel < 2; ++channel)
                {
                    double sum = 0;
                    foreach (var track in tracks)
                    {
                        if (!track.Setting.Enabled || track.Setting.Gain == 0) continue;
                        double a = track.At(sourceFrame, channel);
                        sum += (a + (fraction == 0 ? 0 : (track.At(following, channel) - a) * fraction)) * track.Setting.Gain;
                    }
                    peak = Math.Max(peak, Math.Abs(sum) / 32768.0);
                    if (sum < short.MinValue || sum > short.MaxValue) clipped++;
                    destination[i * 2 + channel] = (short)Math.Round(Math.Clamp(sum, short.MinValue, short.MaxValue), MidpointRounding.AwayFromZero);
                }
            }
            return new(peak, clipped);
        }
    }

    private sealed class TrackReader : IDisposable
    {
        internal AudioTrack Setting { get; }
        private readonly AudioSegment[] segments;
        private readonly List<FileStream> leases = [];
        private readonly byte[] cache = new byte[960_000];
        private int cached = -1;
        internal TrackReader(string directory, AudioTrack setting, AudioSegment[] segments)
        {
            Setting = setting; this.segments = segments;
            try
            {
                foreach (var segment in segments)
                {
                    string path = Path.Combine(directory, segment.Part.File);
                    CaptureImport.CheckRegularPath(path);
                    var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    leases.Add(file);
                    if (AudioStore.VerifyWave(file) != segment.Part.Frames || file.Length != segment.Bytes || Convert.ToHexString(SHA256.HashData(file)) != segment.Sha256)
                        throw new InvalidDataException("Audio changed while preparing render.");
                }
            }
            catch { Dispose(); throw; }
        }
        internal short At(long frame, int channel)
        {
            bool Contains(int i) => i >= 0 && segments[i].Part.StartFrame <= frame && frame < segments[i].Part.StartFrame + segments[i].Part.Frames;
            if (!Contains(cached))
            {
                int lo = 0, hi = segments.Length - 1;
                while (lo <= hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (Contains(mid)) { lo = mid; break; }
                    if (segments[mid].Part.StartFrame > frame) hi = mid - 1; else lo = mid + 1;
                }
                if (lo >= segments.Length || !Contains(lo)) throw new InvalidDataException("Missing PCM sample in retained audio.");
                var file = leases[lo]; file.Position = 44;
                file.ReadExactly(cache.AsSpan(0, checked((int)segments[lo].Part.Frames * 4)));
                cached = lo;
            }
            int offset = checked((int)(frame - segments[cached].Part.StartFrame) * 4 + channel * 2);
            return BinaryPrimitives.ReadInt16LittleEndian(cache.AsSpan(offset, 2));
        }
        public void Dispose() { foreach (var lease in leases) lease.Dispose(); leases.Clear(); }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; foreach (var track in tracks) track.Dispose(); tracks.Clear(); }
    }
}
