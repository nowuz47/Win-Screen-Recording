namespace Glide.Core;

public enum WorkspaceMode { Home, Recording, Presentation, Editing, Library }
public enum PresentationState { Idle, Ready, Live, Covered, Faulted, Ended }

// Presentation and recording are independent. Covering may pause a recording, but must
// never undo a user's own pause, start a recording, or resume a failed session.
public sealed class PresentationSession
{
    public PresentationState State { get; private set; }
    public bool Recording { get; private set; }
    public bool UserPausedRecording { get; private set; }
    public bool ShouldWriteRecording => Recording && !UserPausedRecording && State == PresentationState.Live;
    public bool CanChangeSource => !Recording && State is PresentationState.Ready or PresentationState.Covered;
    public string? Error { get; private set; }

    public void Prepare()
    {
        Require(PresentationState.Idle, PresentationState.Ended);
        Recording = UserPausedRecording = false; Error = null; State = PresentationState.Ready;
    }
    public void Begin() { Require(PresentationState.Ready); State = PresentationState.Live; }
    public void Cover() { Require(PresentationState.Live); State = PresentationState.Covered; }
    public void Resume() { Require(PresentationState.Covered); State = PresentationState.Live; }
    public void StartRecording()
    {
        Require(PresentationState.Live);
        if (Recording) throw new InvalidOperationException("Recording already active.");
        Recording = true; UserPausedRecording = false;
    }
    public void PauseRecording(bool paused)
    {
        if (!Recording) throw new InvalidOperationException("No recording.");
        UserPausedRecording = paused;
    }
    public void StopRecording() { Recording = UserPausedRecording = false; }
    public void Fail(string reason)
    {
        if (State is PresentationState.Idle or PresentationState.Ended) return;
        Error = string.IsNullOrWhiteSpace(reason) ? "Presentation interrupted." : reason;
        State = PresentationState.Faulted;
    }
    public void Recover() { Require(PresentationState.Faulted); Error = null; State = PresentationState.Covered; }
    public void End() { StopRecording(); State = PresentationState.Ended; }
    private void Require(params PresentationState[] states)
    {
        if (!states.Contains(State)) throw new InvalidOperationException($"Invalid presentation transition from {State}.");
    }
}

// Causal camera: only current/past input is used. The exact poses sent to the live
// compositor are recorded, so later playback does not run this stateful filter again.
public sealed class LiveCamera
{
    private CameraPose pose = CameraPose.Full, transitionFrom = CameraPose.Full;
    private PointD focus = new(.5, .5);
    private long previousUs = -1, transitionUs;
    private bool transitioning;
    public bool Zoomed { get; private set; }
    public bool Locked { get; private set; }
    public double Magnification { get; private set; } = 1.5;
    public CameraPose Pose => pose;

    public void SetMagnification(double value)
    {
        if (!double.IsFinite(value) || value < 1.2 || value > 2) throw new ArgumentOutOfRangeException(nameof(value));
        Magnification = value;
    }
    public void AdjustMagnification(long timeUs, double value, PointD pointer)
    {
        if (!double.IsFinite(value) || value < 1.2 || value > 2) throw new ArgumentOutOfRangeException(nameof(value));
        Step(timeUs, pointer, pointer.IsValid);
        SetMagnification(value); Zoomed = true;
        if (!Locked) focus = pointer.IsValid ? pointer : pose.Center;
        transitionFrom = pose; transitionUs = timeUs; transitioning = true;
    }
    public void ToggleZoom(long timeUs, PointD pointer)
    {
        Step(timeUs, pointer, pointer.IsValid);
        Zoomed = !Zoomed; Locked = false;
        focus = pointer.IsValid ? pointer : pose.Center;
        transitionFrom = pose; transitionUs = timeUs; transitioning = true;
    }
    public void ToggleLock() { if (Zoomed) { Locked = !Locked; focus = pose.Center; } }
    public void Reset() { pose = transitionFrom = CameraPose.Full; focus = new(.5, .5); previousUs = -1; Zoomed = Locked = transitioning = false; }

    public CameraPose Step(long timeUs, PointD pointer, bool visible)
    {
        if (timeUs < 0 || timeUs < previousUs) throw new ArgumentOutOfRangeException(nameof(timeUs));
        double dt = previousUs < 0 ? 0 : Math.Min(.1, (timeUs - previousUs) / 1_000_000.0);
        previousUs = timeUs;
        if (Zoomed && !Locked && !transitioning && visible && pointer.IsValid)
        {
            // Only pan enough to bring the pointer into the inner 12% of the crop.
            double margin = .06 / pose.Scale;
            double x = Math.Clamp(focus.X, pointer.X - margin, pointer.X + margin);
            double y = Math.Clamp(focus.Y, pointer.Y - margin, pointer.Y + margin);
            focus = new(x, y);
        }
        double scale = Zoomed ? Magnification : 1;
        PointD target = Zoomed ? focus : new(.5, .5);
        if (transitioning)
        {
            double t = Math.Clamp((timeUs - transitionUs) / 420_000.0, 0, 1);
            double eased = Motion.Ease(t);
            scale = transitionFrom.Scale + (scale - transitionFrom.Scale) * eased;
            target = PointD.Lerp(transitionFrom.Center, target, eased);
            if (t >= 1) transitioning = false;
        }
        else if (Zoomed && !Locked) target = PointD.Lerp(pose.Center, target, 1 - Math.Exp(-dt / .08));
        else if (Locked) target = pose.Center;
        double half = .5 / scale;
        pose = new(new(Math.Clamp(target.X, half, 1 - half), Math.Clamp(target.Y, half, 1 - half)), scale);
        return pose;
    }
}

public sealed record PresentationSample(long TimeUs, double X, double Y, double Scale);
public sealed class PresentationTrack
{
    private readonly PresentationSample[] samples;
    public PresentationTrack(IEnumerable<PresentationSample> samples, long durationUs)
    {
        this.samples = samples.ToArray();
        if (this.samples.Length > 110_000) throw new InvalidDataException("Presentation track exceeds limit.");
        long previous = -1;
        foreach (var s in this.samples)
        {
            if (s.TimeUs <= previous || s.TimeUs >= durationUs || !new PointD(s.X, s.Y).IsValid || !double.IsFinite(s.Scale) || s.Scale < 1 || s.Scale > 4)
                throw new InvalidDataException("Invalid presentation pose.");
            double half = .5 / s.Scale;
            if (s.X < half - 1e-7 || s.X > 1 - half + 1e-7 || s.Y < half - 1e-7 || s.Y > 1 - half + 1e-7)
                throw new InvalidDataException("Presentation pose crosses source bounds.");
            previous = s.TimeUs;
        }
    }
    public CameraPose At(long sourceUs)
    {
        if (samples.Length == 0 || sourceUs < samples[0].TimeUs) return CameraPose.Full;
        int lo = 0, hi = samples.Length - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (samples[mid].TimeUs <= sourceUs) lo = mid; else hi = mid - 1; }
        var s = samples[lo]; return new(new(s.X, s.Y), s.Scale);
    }
}
