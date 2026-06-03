namespace CodexFlow.Core.Telemetry;

public enum QueryLoopEntryPoint
{
    DefaultCodexKernel,
    CodexController,
    GatewayMessageProcessor,
    SimpleCodexController,
    ExploreWorker,
    ForgeWorker,
    PlanWorker,
    VerifyWorker
}

public enum QueryTerminationReason
{
    Normal,
    NoToolCalls,
    MaxRoundsReached,
    ContextHardLimit,
    StallDetected,
    AwaitingUserConfirmation,
    Exception,
    RecoveryExhausted,
    EmptyResponseFallback,
    AutoDispatched
}

public sealed record QueryLoopStarted(
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    DateTimeOffset StartedAt,
    int MaxRounds,
    int InitialContextChars);

public sealed record QueryLoopRoundCompleted(
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    int ToolCallCount,
    bool HasText,
    int TextLength,
    int ThinkingLength,
    int ContextChars,
    long DurationMs,
    string? ContinueReason = null,
    int? PromptTokens = null,
    int? CompletionTokens = null);

public sealed record QueryLoopTerminated(
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    QueryTerminationReason TerminationReason,
    string? DetailCode,
    int TotalRounds,
    int TotalToolCalls,
    int ZeroToolCallRounds,
    int MalformedProtocolCount,
    int EmptyResponseCount,
    int RecoveryCount,
    long TotalDurationMs,
    int? TotalPromptTokens = null,
    int? TotalCompletionTokens = null);

public sealed record QueryLoopRecovery(
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    string RecoveryType,
    int Attempt,
    bool Continued,
    bool Terminal);
