namespace Glide.Core;

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public bool Contains(PointD point) => point.X >= X && point.Y >= Y && point.X <= X + Width && point.Y <= Y + Height;
}

public sealed record RenderStyle(double Padding = .06, double CursorScale = 1.5, bool SmoothCursor = true, bool ShowCursor = true,
    uint Background = 0xFF141820, double CornerRadius = 0, bool ShowClicks = true)
{
    public void Validate()
    {
        if (!double.IsFinite(Padding) || Padding < 0 || Padding > .25 || !double.IsFinite(CursorScale) || CursorScale < .5 || CursorScale > 4 ||
            !double.IsFinite(CornerRadius) || CornerRadius < 0 || CornerRadius > 80 || Background >> 24 != 255)
            throw new ArgumentException("Invalid render style.");
    }
}
public sealed record RenderFrame(long Revision, long OutputUs, long SourceUs, RectD SourceCrop, RectD Destination, PointD CursorPixel, bool CursorVisible,
    double CursorScale, uint Background = 0xFF141820, double CornerRadius = 0, double ClickAmount = 0, PointD ClickPixel = default);

// Renderer-independent immutable plan. Preview and offline export must consume this same mapping.
// Pixel composition and video encoding are deliberately separate from timeline/motion decisions.
public sealed class RenderPlan
{
    private readonly ProjectDocument project;
    private readonly Timeline timeline;
    private readonly CursorTrack cursors;
    private readonly RenderStyle style;
    private readonly PresentationTrack? presentation;
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public long DurationUs => timeline.DurationUs;
    public long FrameCount { get; }

    public RenderPlan(ProjectDocument project, CursorTrack cursors, int width, int height, int fps, RenderStyle? style = null, PresentationTrack? presentation = null)
    {
        project.Validate();
        if (width < 2 || height < 2 || width > 3840 || height > 3840 || (long)width * height > 8_294_400 || width % 2 != 0 || height % 2 != 0 || fps is not (30 or 60))
            throw new ArgumentOutOfRangeException(nameof(width));
        this.style = style ?? project.Style ?? new(); this.style.Validate();
        this.presentation = project.Origin == "presentation" && project.UsePresentationCamera ? presentation : null;
        this.project = project with { Ranges = project.Ranges.ToArray(), Zooms = project.Zooms.ToArray(), Audio = project.Audio?.ToArray() };
        this.cursors = cursors;
        timeline = new(this.project.Ranges);
        if (DurationUs > 1_800_100_000) throw new ArgumentException("Render duration exceeds 30-minute capture limit.");
        Width = width; Height = height; Fps = fps;
        FrameCount = checked((DurationUs * fps + 999_999) / 1_000_000);
    }

    public RenderFrame AtFrame(long frameIndex)
    {
        if (frameIndex < 0 || frameIndex >= FrameCount) throw new ArgumentOutOfRangeException(nameof(frameIndex));
        return AtTime(frameIndex * 1_000_000 / Fps);
    }

    public RenderFrame AtTime(long outputUs)
    {
        long sourceUs = timeline.ToSource(outputUs);
        double sourceAspect = (double)project.Width / project.Height;
        double padding = Math.Min(Width, Height) * style.Padding;
        double availableW = Width - 2 * padding, availableH = Height - 2 * padding;
        double factor = Math.Min(availableW / project.Width, availableH / project.Height);
        double videoW = factor * project.Width, videoH = factor * project.Height;
        var destination = new RectD((Width - videoW) / 2, (Height - videoH) / 2, videoW, videoH);
        var manual = project.Zooms.Any(z => z.Locked && z.Range.Contains(sourceUs));
        var camera = presentation is not null && !manual ? presentation.At(sourceUs) : Motion.CameraAt(project.Zooms, sourceUs, sourceAspect, sourceAspect);
        double cropSize = 1 / camera.Scale;
        var crop = new RectD(camera.Center.X - cropSize / 2, camera.Center.Y - cropSize / 2, cropSize, cropSize);
        var cursor = cursors.At(sourceUs, style.SmoothCursor);
        bool visible = style.ShowCursor && cursor is { Visible: true } && crop.Contains(cursor.Position);
        PointD pixel = cursor is null ? default : new(destination.X + (cursor.Position.X - crop.X) / crop.Width * destination.Width,
            destination.Y + (cursor.Position.Y - crop.Y) / crop.Height * destination.Height);
        var click = cursors.LastClickAt(sourceUs);
        double clickAmount = style.ShowClicks && click is not null && crop.Contains(click.Position) ? cursors.ClickAmountAt(sourceUs) : 0;
        var clickPixel = click is null ? default : new PointD(destination.X + (click.Position.X - crop.X) / crop.Width * destination.Width,
            destination.Y + (click.Position.Y - crop.Y) / crop.Height * destination.Height);
        return new(project.Revision, outputUs, sourceUs, crop, destination, pixel, visible, style.CursorScale,
            style.Background, style.CornerRadius * Height / 1080.0, clickAmount, clickPixel);
    }
}

public sealed class EditHistory
{
    private readonly List<ProjectDocument> undo = [], redo = [];
    private ProjectDocument current;
    public ProjectDocument Current => Snapshot(current);
    public bool CanUndo => undo.Count != 0;
    public bool CanRedo => redo.Count != 0;
    public EditHistory(ProjectDocument document) { document.Validate(); current = Snapshot(document); }
    public void Apply(Func<ProjectDocument, ProjectDocument> edit, Action<ProjectDocument>? persist = null)
    {
        var next = edit(Snapshot(current)); next.Validate();
        if (next.Id != current.Id || next.DurationUs != current.DurationUs || next.Width != current.Width || next.Height != current.Height || next.Recovered != current.Recovered
            || !(next.Audio ?? []).Select(t => t.Role).SequenceEqual((current.Audio ?? []).Select(t => t.Role)))
            throw new ArgumentException("An edit cannot replace the source media.");
        next = next with { Revision = checked(current.Revision + 1) };
        persist?.Invoke(Snapshot(next));
        undo.Add(current); if (undo.Count > 100) undo.RemoveAt(0);
        redo.Clear(); current = Snapshot(next);
    }
    public void Delete(TimeRange outputRange) => Apply(p => p with { Ranges = new Timeline(p.Ranges).Remove(outputRange).Ranges.ToArray() });
    public void Undo(Action<ProjectDocument>? persist = null) => Restore(undo, redo, persist);
    public void Redo(Action<ProjectDocument>? persist = null) => Restore(redo, undo, persist);
    private void Restore(List<ProjectDocument> from, List<ProjectDocument> to, Action<ProjectDocument>? persist)
    {
        if (from.Count == 0) throw new InvalidOperationException("No edit to restore.");
        var next = from[^1] with { Revision = checked(current.Revision + 1) };
        persist?.Invoke(Snapshot(next));
        from.RemoveAt(from.Count - 1); to.Add(current); current = next;
    }
    private static ProjectDocument Snapshot(ProjectDocument p) => p with { Ranges = p.Ranges.ToArray(), Zooms = p.Zooms.ToArray(), Audio = p.Audio?.ToArray() };
}
