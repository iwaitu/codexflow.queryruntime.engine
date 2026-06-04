namespace CodexFlow.QueryRuntime.Abstractions;

/// <summary>
/// Durable, public description of the QueryRuntime JSONL trace / replay format.
/// These values are part of the public contract: once a trace is written with a
/// given <see cref="CurrentVersion"/>, that version's record shape must remain
/// readable by future runtimes (via migration) or be rejected with a precise reason.
/// </summary>
public static class QueryRuntimeTraceSchema
{
    /// <summary>
    /// Schema version stamped on the <c>run.started</c> record and the run manifest.
    /// Version 1 is the first public, deterministically-replayable trace format:
    /// it carries deterministic per-run query ids, injected clock timestamps, and a
    /// stable record envelope.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Lowest schema version a <c>--strict</c> replay will accept. Pre-public traces
    /// without a recorded <c>SchemaVersion</c> are treated as version 0 and are not
    /// strict-replayable, because their determinism guarantees were never frozen.
    /// </summary>
    public const int MinimumStrictReplayVersion = 1;

    /// <summary>Version assigned to traces that predate explicit schema versioning.</summary>
    public const int LegacyUnversioned = 0;

    /// <summary>JSON property name carrying the schema version on the run-started record and manifest.</summary>
    public const string VersionField = "SchemaVersion";

    /// <summary>
    /// Returns whether a trace recorded at <paramref name="traceVersion"/> can be
    /// replayed under the current runtime in <paramref name="strict"/> mode, and if
    /// not, why.
    /// </summary>
    public static QueryRuntimeTraceCompatibility GetReplayCompatibility(int traceVersion, bool strict)
    {
        if (traceVersion > CurrentVersion)
        {
            return new QueryRuntimeTraceCompatibility(
                false,
                $"unsupported trace schema version {traceVersion} (runtime supports up to {CurrentVersion}); upgrade the runtime to replay this trace");
        }

        if (strict && traceVersion < MinimumStrictReplayVersion)
        {
            var detail = traceVersion <= LegacyUnversioned
                ? "trace has no recorded schema version (pre-public legacy trace)"
                : $"trace schema version {traceVersion} predates the strict-replay baseline {MinimumStrictReplayVersion}";
            return new QueryRuntimeTraceCompatibility(
                false,
                $"strict replay requires schema version >= {MinimumStrictReplayVersion}; {detail}. Use non-strict recorded replay instead.");
        }

        return new QueryRuntimeTraceCompatibility(true, null);
    }
}

/// <summary>Result of a trace schema compatibility check.</summary>
public sealed record QueryRuntimeTraceCompatibility(bool Compatible, string? Reason);

/// <summary>Replay execution modes exposed by the runtime.</summary>
public enum QueryRuntimeReplayMode
{
    /// <summary>Read-only summary of the recorded trace; the runtime is not executed.</summary>
    Summary = 0,

    /// <summary>
    /// Recorded replay: recorded model responses and tool outputs are replayed through
    /// the engine without calling any provider or executing any original tool. Timestamps
    /// and query ids use the live runtime clock, so they differ from the source run.
    /// </summary>
    Recorded = 1,

    /// <summary>
    /// Strict replay: like <see cref="Recorded"/>, but with deterministic clock and query
    /// id injection seeded from the source trace, producing a byte-identical canonical
    /// event projection across repeated replays of the same trace and runtime version.
    /// </summary>
    Strict = 2
}
