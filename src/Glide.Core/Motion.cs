namespace Glide.Core;

public readonly record struct PointD(double X, double Y)
{
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && X >= 0 && X <= 1 && Y >= 0 && Y <= 1;
    public static PointD Lerp(PointD a, PointD b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}
public record CursorSample(long TimeUs, PointD Position, bool Visible = true, string Shape = "arrow");
public record ClickEvent(long TimeUs, PointD Position, bool IsDrag = false);
public record ZoomSegment(string Id, TimeRange Range, PointD Focus, double Scale, bool Locked = false);
public readonly record struct CameraPose(PointD Center, double Scale)
{
    public static CameraPose Full => new(new(.5, .5), 1);
}

public static class Motion
{
    public static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }

    public static CameraPose CameraAt(IReadOnlyList<ZoomSegment> zooms, long timeUs, double sourceAspect = 16.0 / 9, double outputAspect = 16.0 / 9)
    {
        if (!double.IsFinite(sourceAspect) || !double.IsFinite(outputAspect) || sourceAspect <= 0 || outputAspect <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceAspect));
        // A locked manual segment wins over an automatically generated one.
        var z = zooms.FirstOrDefault(x => x.Locked && x.Range.Contains(timeUs)) ?? zooms.FirstOrDefault(x => x.Range.Contains(timeUs));
        if (z is null) return CameraPose.Full;
        ValidateZoom(z);
        double ramp = Math.Min(650_000, z.Range.DurationUs / 2.0);
        double amount = Math.Min(Ease((timeUs - z.Range.StartUs) / ramp), Ease((z.Range.EndUs - timeUs) / ramp));
        double scale = 1 + (z.Scale - 1) * amount;
        double cropWidth = Math.Min(1, outputAspect / sourceAspect) / scale;
        double cropHeight = Math.Min(1, sourceAspect / outputAspect) / scale;
        var center = PointD.Lerp(new(.5, .5), z.Focus, amount);
        return new(new(Math.Clamp(center.X, cropWidth / 2, 1 - cropWidth / 2), Math.Clamp(center.Y, cropHeight / 2, 1 - cropHeight / 2)), scale);
    }

    public static void ValidateZoom(ZoomSegment z)
    {
        z.Range.Validate();
        if (!z.Focus.IsValid || !double.IsFinite(z.Scale) || z.Scale < 1 || z.Scale > 4 || string.IsNullOrWhiteSpace(z.Id))
            throw new ArgumentException("Invalid zoom segment.");
    }

    public static IReadOnlyList<ZoomSegment> Plan(IEnumerable<ClickEvent> clicks, long durationUs, IEnumerable<ZoomSegment>? existing = null)
    {
        if (durationUs <= 0) throw new ArgumentOutOfRangeException(nameof(durationUs));
        var locked = (existing ?? []).Where(z => z.Locked).ToList();
        foreach (var z in locked) { ValidateZoom(z); if (z.Range.EndUs > durationUs) throw new ArgumentException("Zoom exceeds source duration."); }
        List<ZoomSegment> planned = [];
        foreach (var click in clicks.OrderBy(c => c.TimeUs))
        {
            if (click.TimeUs < 0 || click.TimeUs >= durationUs || !click.Position.IsValid) throw new ArgumentException("Invalid click.");
            if (click.IsDrag) continue;
            var range = new TimeRange(Math.Max(0, click.TimeUs - 250_000), Math.Min(durationUs, click.TimeUs + 1_800_000));
            if (range.DurationUs < 900_000 || locked.Any(z => Overlaps(z.Range, range))) continue;
            if (planned.Count > 0 && Overlaps(planned[^1].Range, range))
            {
                var previous = planned[^1];
                var distance = Math.Abs(previous.Focus.X - click.Position.X) + Math.Abs(previous.Focus.Y - click.Position.Y);
                if (distance < .25)
                {
                    planned[^1] = previous with { Range = new(previous.Range.StartUs, range.EndUs) };
                    continue;
                }
                // A distant action needs its own context; don't compound scales or snap between focuses.
                continue;
            }
            planned.Add(new($"auto-{click.TimeUs}", range, click.Position, 1.6));
        }
        return planned.Concat(locked).OrderBy(z => z.Range.StartUs).ToArray();
    }

    private static bool Overlaps(TimeRange a, TimeRange b) => a.StartUs < b.EndUs && b.StartUs < a.EndUs;
}

public sealed class CursorTrack
{
    private readonly CursorSample[] samples;
    private readonly ClickEvent[] anchors;
    public CursorTrack(IEnumerable<CursorSample> samples, IEnumerable<ClickEvent>? anchors = null)
    {
        this.samples = samples.ToArray();
        this.anchors = (anchors ?? []).OrderBy(x => x.TimeUs).ToArray();
        long previous = -1;
        foreach (var sample in this.samples)
        {
            if (sample.TimeUs <= previous || !sample.Position.IsValid || string.IsNullOrEmpty(sample.Shape)) throw new ArgumentException("Cursor samples must have valid positions and strictly increasing times.");
            previous = sample.TimeUs;
        }
        if (this.anchors.Any(x => x.TimeUs < 0 || !x.Position.IsValid)) throw new ArgumentException("Invalid cursor anchor.");
    }

    public CursorSample? At(long timeUs, bool smooth)
    {
        if (timeUs < 0) throw new ArgumentOutOfRangeException(nameof(timeUs));
        if (samples.Length == 0) return null;
        int left = 0, right = samples.Length - 1;
        while (left < right) { int mid = (left + right + 1) / 2; if (samples[mid].TimeUs <= timeUs) left = mid; else right = mid - 1; }
        var a = samples[left];
        var b = samples[Math.Min(left + 1, samples.Length - 1)];
        // No inferred visible cursor before the first sample or across a missing interval.
        if (timeUs < a.TimeUs || !a.Visible || timeUs - a.TimeUs > 250_000) return a with { TimeUs = timeUs, Visible = false };
        double t = b.TimeUs == a.TimeUs ? 0 : Math.Clamp((double)(timeUs - a.TimeUs) / (b.TimeUs - a.TimeUs), 0, 1);
        PointD position = !b.Visible || b.TimeUs - a.TimeUs > 250_000 ? a.Position : PointD.Lerp(a.Position, b.Position, t);
        if (smooth && a.Visible)
        {
            double x = 0, y = 0, sum = 0;
            for (int i = left; i >= 0 && timeUs - samples[i].TimeUs <= 80_000; i--)
            {
                var point = samples[i];
                if (!point.Visible || point.Shape != a.Shape) break;
                double weight = Math.Exp(-(timeUs - point.TimeUs) / 30_000.0);
                x += point.Position.X * weight; y += point.Position.Y * weight; sum += weight;
            }
            if (sum > 0) position = PointD.Lerp(position, new(x / sum, y / sum), .5);
        }
        // Preserve click timing/coordinates; taper correction nearby to prevent a single-frame jump.
        var anchor = anchors.MinBy(c => Math.Abs(c.TimeUs - timeUs));
        if (anchor is not null && Math.Abs(anchor.TimeUs - timeUs) < 60_000)
            position = PointD.Lerp(position, anchor.Position, Motion.Ease(1 - Math.Abs(anchor.TimeUs - timeUs) / 60_000.0));
        return a with { TimeUs = timeUs, Position = position };
    }

    public double ClickAmountAt(long timeUs)
    {
        var click = LastClickAt(timeUs);
        return click is null ? 0 : Math.Clamp(1 - (timeUs - click.TimeUs) / 250_000.0, 0, 1);
    }
    public ClickEvent? LastClickAt(long timeUs) => anchors.LastOrDefault(c => c.TimeUs <= timeUs && !c.IsDrag);
}
