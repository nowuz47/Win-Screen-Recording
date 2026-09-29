namespace Glide.Core;

public enum RecordingState { Idle, Preparing, Countdown, Recording, Paused, Finalizing, Editing, Faulted }

public sealed class RecordingSession
{
    private readonly object sync = new();
    private long? startUs, pauseUs, stopUs;
    private long pausedUs;
    private long lastTransitionUs;
    private RecordingState state = RecordingState.Idle;
    public RecordingState State { get { lock (sync) return state; } }
    public string? Error { get; private set; }

    public void Prepare() { lock (sync) { Require(RecordingState.Idle, RecordingState.Editing, RecordingState.Faulted); state = RecordingState.Preparing; startUs = pauseUs = stopUs = null; pausedUs = 0; lastTransitionUs = 0; Error = null; } }
    public void Ready() { lock (sync) { Require(RecordingState.Preparing); state = RecordingState.Countdown; } }
    public void Cancel() { lock (sync) { Require(RecordingState.Preparing, RecordingState.Countdown); state = RecordingState.Idle; } }
    public void Start(long clockUs) { lock (sync) { Require(RecordingState.Countdown); CheckTime(clockUs); lastTransitionUs = clockUs; startUs = clockUs; state = RecordingState.Recording; } }
    public void Pause(long clockUs) { lock (sync) { Require(RecordingState.Recording); CheckTime(clockUs); lastTransitionUs = clockUs; pauseUs = clockUs; state = RecordingState.Paused; } }
    public void Resume(long clockUs) { lock (sync) { Require(RecordingState.Paused); CheckTime(clockUs); pausedUs = checked(pausedUs + clockUs - pauseUs!.Value); lastTransitionUs = clockUs; pauseUs = null; state = RecordingState.Recording; } }
    public bool Stop(long clockUs)
    {
        lock (sync)
        {
            if (state is RecordingState.Finalizing or RecordingState.Editing) return false;
            Require(RecordingState.Recording, RecordingState.Paused); CheckTime(clockUs);
            lastTransitionUs = clockUs; stopUs = pauseUs ?? clockUs; state = RecordingState.Finalizing; return true;
        }
    }
    public void Complete() { lock (sync) { Require(RecordingState.Finalizing); state = RecordingState.Editing; } }
    public void Fail(string error) { lock (sync) { Error = string.IsNullOrWhiteSpace(error) ? "Unknown recording failure" : error; state = RecordingState.Faulted; } }
    public long ElapsedUs(long clockUs)
    {
        lock (sync)
        {
            CheckTime(clockUs);
            if (startUs is null) return 0;
            return checked((stopUs ?? pauseUs ?? clockUs) - startUs.Value - pausedUs);
        }
    }
    private void Require(params RecordingState[] allowed) { if (!allowed.Contains(state)) throw new InvalidOperationException($"Invalid transition from {state}."); }
    private void CheckTime(long clockUs) { if (clockUs < lastTransitionUs) throw new ArgumentOutOfRangeException(nameof(clockUs)); }
}
