namespace CodexFlow.Core.Models;

public enum PlannerSummaryKind
{
    Started,
    InProgress,
    Fallback,
    Completed,
    Failed
}

public sealed record PlannerSummaryUpdate
{
    public required PlannerSummaryKind Kind { get; init; }
    public required string Message { get; init; }
    public string? Phase { get; init; }
    public int? Round { get; init; }
    public int? MaxRounds { get; init; }
    public int? ThinkingLength { get; init; }
    public int? TextLength { get; init; }
    public int? TaskCount { get; init; }
    public long? DurationMs { get; init; }
    public string? TerminationReason { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
}
