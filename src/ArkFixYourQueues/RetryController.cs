namespace ArkFixYourQueues;

public enum RetryState
{
    Stopped,
    Arming,
    Ready,
    Cooldown,
    PausedForLoading
}

public sealed class RetryController
{
    public const int MinimumRetrySeconds = 2;
    private const int ChangedFramesToPause = 2;
    private const int BaselineFramesToResume = 3;

    private int _changedFrames;
    private int _baselineFrames;

    public RetryState State { get; private set; } = RetryState.Stopped;
    public DateTimeOffset NextAttemptAt { get; private set; }
    public int RetrySeconds { get; private set; } = 10;

    public void Start(int requestedRetrySeconds, DateTimeOffset now)
    {
        RetrySeconds = Math.Max(MinimumRetrySeconds, requestedRetrySeconds);
        State = RetryState.Arming;
        NextAttemptAt = now.AddSeconds(3);
        ResetEvidence();
    }

    public void FinishArming(DateTimeOffset now)
    {
        if (State != RetryState.Arming) return;
        State = RetryState.Ready;
        NextAttemptAt = now;
    }

    public bool ShouldAttempt(DateTimeOffset now, bool gameIsForeground) =>
        State == RetryState.Ready && gameIsForeground && now >= NextAttemptAt;

    public void Attempted(DateTimeOffset now)
    {
        if (State != RetryState.Ready) return;
        State = RetryState.Cooldown;
        NextAttemptAt = now.AddSeconds(RetrySeconds);
        ResetEvidence();
    }

    public void ObserveScreen(bool resemblesBaseline, DateTimeOffset now)
    {
        if (State is RetryState.Stopped or RetryState.Arming) return;

        if (resemblesBaseline)
        {
            _baselineFrames++;
            _changedFrames = 0;
        }
        else
        {
            _changedFrames++;
            _baselineFrames = 0;
        }

        if (State == RetryState.Cooldown && _changedFrames >= ChangedFramesToPause)
        {
            State = RetryState.PausedForLoading;
        }
        else if (State == RetryState.PausedForLoading && _baselineFrames >= BaselineFramesToResume)
        {
            State = RetryState.Ready;
            NextAttemptAt = now;
        }
        else if (State == RetryState.Cooldown && now >= NextAttemptAt && resemblesBaseline)
        {
            State = RetryState.Ready;
        }
    }

    public void Stop()
    {
        State = RetryState.Stopped;
        ResetEvidence();
    }

    private void ResetEvidence()
    {
        _changedFrames = 0;
        _baselineFrames = 0;
    }
}
