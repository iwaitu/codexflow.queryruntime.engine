namespace CodexFlow.QueryRuntime.Abstractions;

public sealed record QueryRuntimeProviderOptions
{
    public string? StaticResponse { get; set; }

    public string? ApiUrl { get; set; }

    public string? ApiKey { get; set; }

    public string? Model { get; set; }

    public string? ApiMode { get; set; }
}

public sealed record QueryRuntimeToolProfile(string Name)
{
    public static QueryRuntimeToolProfile None { get; } = new("none");

    public static QueryRuntimeToolProfile ReadOnly { get; } = new("readonly");

    public static QueryRuntimeToolProfile Verify { get; } = new("verify");

    public static QueryRuntimeToolProfile Repair { get; } = new("repair");

    public bool IsNone => string.Equals(Name, None.Name, StringComparison.OrdinalIgnoreCase);
}

public sealed record QueryRuntimeModelPolicyOptions
{
    public QreThinkingPolicy ThinkingPolicy { get; set; } = QreThinkingPolicy.Auto;
}

public sealed record QueryRuntimeOutputOptions
{
    /// <summary>
    /// Requests provider-level JSON response format when supported.
    /// </summary>
    public bool RequestJson { get; set; }

    /// <summary>
    /// CLI output formatting flag. Host integrations should inspect
    /// <see cref="QueryRuntimeResult"/> instead.
    /// </summary>
    public bool Json { get; set; }

    /// <summary>
    /// CLI stdout streaming flag. Host integrations should use
    /// <see cref="QueryRuntimeHostRequest.TextDeltaSink"/> for text deltas.
    /// </summary>
    public bool Stream { get; set; }
}

public sealed record QueryRuntimeExecutionOptions
{
    public int MaxRounds { get; set; } = 3;

    /// <summary>
    /// Maximum number of host-requested continuation rounds allowed after a terminal
    /// candidate. QRE fails closed when the stop gate still requires more work after
    /// this limit or after <see cref="MaxRounds"/> is reached.
    /// </summary>
    public int MaxStopGateContinuations { get; set; } = 1;
}

/// <summary>
/// Controls which potentially sensitive values are persisted in trace artifacts.
/// Public redacted traces are the safe default. The other modes require an explicit opt-in.
/// </summary>
public enum QueryRuntimeTraceDataMode
{
    PublicRedacted = 0,
    PrivateDiagnostic = 1,
    SanitizedFixture = 2
}

public enum QueryRuntimeReplayCapability
{
    SummaryOnly = 0,
    FullFidelity = 1
}

public sealed record QueryRuntimeTraceOptions
{
    public QueryRuntimeTraceDataMode DataMode { get; init; } = QueryRuntimeTraceDataMode.PublicRedacted;

    /// <summary>
    /// Maximum age for private diagnostic run directories. Private traces may
    /// contain prompts and tool payloads, so the harness prunes expired runs
    /// whenever a new private trace starts.
    /// </summary>
    public TimeSpan PrivateDiagnosticRetention { get; init; } = TimeSpan.FromDays(7);

    public QueryRuntimeReplayCapability ReplayCapability
        => DataMode == QueryRuntimeTraceDataMode.PublicRedacted
            ? QueryRuntimeReplayCapability.SummaryOnly
            : QueryRuntimeReplayCapability.FullFidelity;

    public void Validate()
    {
        if (DataMode == QueryRuntimeTraceDataMode.PrivateDiagnostic &&
            (PrivateDiagnosticRetention <= TimeSpan.Zero || PrivateDiagnosticRetention > TimeSpan.FromDays(30)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(PrivateDiagnosticRetention),
                "Private diagnostic trace retention must be between zero and 30 days.");
        }
    }
}

public enum QreThinkingPolicy
{
    Auto = 0,
    Preserve = 1,
    ForceDisabled = 2,
    ForceEnabled = 3
}
