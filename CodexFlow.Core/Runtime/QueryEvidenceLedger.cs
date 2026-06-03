namespace CodexFlow.Core.Runtime;

/// <summary>
/// Runtime working-set evidence that should survive prompt projection decisions.
/// It is intentionally compact and stores handles/summaries instead of raw tool output.
/// </summary>
public sealed class QueryEvidenceLedger
{
    public List<FileEvidence> Files { get; } = [];

    public List<ToolEvidence> ToolResults { get; } = [];

    public List<PendingModificationEvidence> PendingModifications { get; } = [];

    public string? LastToolBatchSummary { get; set; }

    public List<RuntimeFailureEvidence> Failures { get; } = [];

    public List<string> RepeatedEvidenceKeys { get; } = [];

    public Dictionary<string, int> SeenReadEvidenceKeys { get; } = new(StringComparer.Ordinal);
}

public sealed record FileEvidence
{
    public required string FilePath { get; init; }
    public string? ToolName { get; init; }
    public string? SnapshotId { get; init; }
    public string? FileFingerprint { get; init; }
    public int? WindowStartLine { get; init; }
    public int? WindowEndLine { get; init; }
    public int? TotalLineCount { get; init; }
    public string? Summary { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ToolEvidence
{
    public required string ToolName { get; init; }
    public required string CallId { get; init; }
    public required bool Success { get; init; }
    public string? Summary { get; init; }
    public int? ResultLength { get; init; }
    public bool IsOutputTruncated { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PendingModificationEvidence
{
    public required string Source { get; init; }
    public string? RequiredToolName { get; init; }
    public string? AssistantPlanSummary { get; init; }
    public IReadOnlyList<string> CandidateFiles { get; init; } = [];
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RuntimeFailureEvidence
{
    public required string Source { get; init; }
    public required string Message { get; init; }
    public string? ToolName { get; init; }
    public string? CallId { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RuntimeRecoveryHint
{
    public required string Source { get; init; }
    public int Attempt { get; init; }
    public string? RequiredToolName { get; init; }
    public bool ToolCallRequired { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string> CandidateFiles { get; init; } = [];
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}
