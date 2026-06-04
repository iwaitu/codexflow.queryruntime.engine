namespace CodexFlow.QueryRuntime.Experimental;

/// <summary>
/// A fully deterministic <see cref="TimeProvider"/> used by strict replay. Both the
/// wall-clock (<see cref="GetUtcNow"/>) and the high-resolution timestamp source
/// (<see cref="GetTimestamp"/>) advance by fixed steps per call, so a replay that
/// executes the identical code path against the same recorded trace produces
/// byte-identical timestamps and durations every time.
/// </summary>
public sealed class DeterministicReplayClock : TimeProvider
{
    private const long FixedTimestampFrequency = 1_000_000; // 1 tick == 1 microsecond.

    private readonly DateTimeOffset _baseUtc;
    private readonly long _utcStepTicks;
    private readonly long _timestampStep;
    private long _utcCounter;
    private long _timestamp;

    public DeterministicReplayClock(DateTimeOffset baseUtc, TimeSpan? utcStep = null)
    {
        _baseUtc = baseUtc.ToUniversalTime();
        _utcStepTicks = Math.Max(1, (utcStep ?? TimeSpan.FromMilliseconds(1)).Ticks);
        // One GetTimestamp step equals 1ms of deterministic elapsed time.
        _timestampStep = FixedTimestampFrequency / 1000;
    }

    public override long TimestampFrequency => FixedTimestampFrequency;

    public override DateTimeOffset GetUtcNow()
    {
        var current = _baseUtc.AddTicks(_utcStepTicks * _utcCounter);
        _utcCounter++;
        return current;
    }

    public override long GetTimestamp()
    {
        var current = _timestamp;
        _timestamp += _timestampStep;
        return current;
    }
}
