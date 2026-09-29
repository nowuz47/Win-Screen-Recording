namespace Glide.Core;

// All persisted times are integer microseconds on the recording's monotonic clock.
public readonly record struct TimeRange(long StartUs, long EndUs)
{
    public long DurationUs => checked(EndUs - StartUs);
    public bool Contains(long timeUs) => timeUs >= StartUs && timeUs < EndUs;
    public void Validate()
    {
        if (StartUs < 0 || EndUs <= StartUs) throw new ArgumentOutOfRangeException(nameof(StartUs));
    }
}

public sealed class Timeline
{
    private readonly TimeRange[] ranges;
    public IReadOnlyList<TimeRange> Ranges => Array.AsReadOnly(ranges);
    public long DurationUs { get; }

    public Timeline(IEnumerable<TimeRange> ranges)
    {
        this.ranges = ranges.ToArray();
        if (this.ranges.Length == 0) throw new ArgumentException("The timeline must retain at least one range.");
        long previousEnd = 0;
        foreach (var range in this.ranges)
        {
            range.Validate();
            if (range.StartUs < previousEnd) throw new ArgumentException("Source ranges must be ordered and disjoint.");
            previousEnd = range.EndUs;
            DurationUs = checked(DurationUs + range.DurationUs);
        }
    }

    public long ToSource(long outputUs)
    {
        if (outputUs < 0 || outputUs >= DurationUs) throw new ArgumentOutOfRangeException(nameof(outputUs));
        foreach (var range in ranges)
        {
            if (outputUs < range.DurationUs) return checked(range.StartUs + outputUs);
            outputUs -= range.DurationUs;
        }
        throw new InvalidOperationException("Invalid timeline state.");
    }

    public long? ToOutput(long sourceUs)
    {
        long offset = 0;
        foreach (var range in ranges)
        {
            if (range.Contains(sourceUs)) return checked(offset + sourceUs - range.StartUs);
            offset += range.DurationUs;
        }
        return null;
    }

    public Timeline Remove(TimeRange outputRange)
    {
        outputRange.Validate();
        if (outputRange.EndUs > DurationUs) throw new ArgumentOutOfRangeException(nameof(outputRange));
        List<TimeRange> kept = [];
        long offset = 0;
        foreach (var source in ranges)
        {
            var end = checked(offset + source.DurationUs);
            var cutStart = Math.Max(offset, outputRange.StartUs);
            var cutEnd = Math.Min(end, outputRange.EndUs);
            if (cutStart >= cutEnd) kept.Add(source);
            else
            {
                if (cutStart > offset) kept.Add(new(source.StartUs, source.StartUs + cutStart - offset));
                if (cutEnd < end) kept.Add(new(source.StartUs + cutEnd - offset, source.EndUs));
            }
            offset = end;
        }
        return new Timeline(kept);
    }
}
