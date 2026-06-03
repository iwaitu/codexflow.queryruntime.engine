namespace CodexFlow.Core.Runtime;

/// <summary>
/// Records the prompt-facing context assembled for one runtime round.
/// This is diagnostic metadata; it is not sent to the model by itself.
/// </summary>
public sealed record PromptAssemblySnapshot
{
    public required Guid QueryId { get; init; }
    public required string SessionId { get; init; }
    public required int Round { get; init; }
    public required string EntryPoint { get; init; }
    public required IReadOnlyList<PromptAssemblyFrameRecord> Frames { get; init; }
    public required IReadOnlyList<string> ToolNames { get; init; }
    public string? ToolChoice { get; init; }
    public string? RequiredToolName { get; init; }
    public bool ToolsEnabled { get; init; }
    public bool ToolCallsAllowed { get; init; }
    public int MessageCount { get; init; }
    public int EstimatedContextChars { get; init; }
    public int EstimatedPromptTokens { get; init; }
    public IReadOnlyList<string> DroppedFrames { get; init; } = [];
    public IReadOnlyList<string> BudgetDecisions { get; init; } = [];
}

public sealed record PromptAssemblyFrameRecord
{
    public required string Name { get; init; }
    public required PromptAssemblyFrameKind Kind { get; init; }
    public int Priority { get; init; }
    public int EstimatedChars { get; init; }
    public int EstimatedTokens { get; init; }
    public bool StableAcrossRounds { get; init; }
    public bool Compressible { get; init; } = true;
    public string? Source { get; init; }
    public string? Summary { get; init; }
}

public enum PromptAssemblyFrameKind
{
    StableSystem,
    UserMemory,
    ProjectMemory,
    ConversationSummary,
    RecentTranscript,
    ToolSurface,
    WorkerCapsule,
    EvidenceLedger,
    RecoveryHint,
    CompactBoundary,
    DebugMetadata
}
