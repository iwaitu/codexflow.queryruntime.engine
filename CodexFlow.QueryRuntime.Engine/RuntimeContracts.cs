using Microsoft.Extensions.AI;
using HostContracts = CodexFlow.QueryRuntime.Abstractions;

namespace CodexFlow.QueryRuntime.Engine;

public interface IQueryRuntimeModelClient
{
    IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        QueryRuntimeModelRequest request,
        CancellationToken ct = default);
}

public interface IQueryRuntimeEngine
{
    Task<QueryRuntimeResult> ExecuteAsync(
        QueryRuntimeRequest request,
        IQueryRuntimeEventSink eventSink,
        string runId,
        string traceFilePath,
        string? workspacePath,
        CancellationToken ct = default);
}

public sealed record QueryRuntimeModelRequest(
    IReadOnlyList<ChatMessage> Messages,
    ChatOptions? Options,
    string RunId,
    string? WorkspacePath);

public sealed record QueryRuntimeRequest
{
    public required string SessionId { get; init; }

    public required IReadOnlyList<ChatMessage> InitialMessages { get; init; }

    public ChatOptions? Options { get; init; }

    public Func<ChatOptions, ChatOptions>? OptionsCloneFactory { get; init; }

    public int MaxRounds { get; init; } = 3;

    public bool EnableTools { get; init; }

    public IReadOnlyList<AIFunction> AvailableTools { get; init; } = [];

    public Func<QueryRuntimeToolResolutionContext, IReadOnlyList<AIFunction>>? ToolProvider { get; init; }

    public string? RequiredToolName { get; init; }

    public IReadOnlySet<string> WriteToolNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HostContracts.IQueryRuntimeToolIntervention? ToolIntervention { get; init; }

    public HostContracts.IQueryRuntimeStopGate? StopGate { get; init; }

    public int MaxStopGateContinuations { get; init; } = 1;

    public Func<string, CancellationToken, ValueTask>? TextDeltaSink { get; init; }
}

public sealed record QueryRuntimeToolResolutionContext(
    int Round,
    bool RequiredToolSatisfied);

public sealed record QueryRuntimeResult(
    string RunId,
    string SessionId,
    string TraceFilePath,
    string FinalText,
    QueryTerminationReason TerminationReason,
    int TotalRounds,
    int TotalToolCalls,
    long TotalDurationMs)
{
    public string? TerminalDetailCode { get; init; }

    public int ZeroToolCallRounds { get; init; }

    public int ContinuationCount { get; init; }

    public string? LastFunctionCall { get; init; }

    public int WriteToolCalls { get; init; }

    public string? RunDirectory { get; init; }

    public string? RequiredToolName { get; init; }

    public bool RequiredToolSatisfied { get; init; }

    public IReadOnlyList<string> ExecutedToolNames { get; init; } = [];

    public IReadOnlyList<string> SuccessfulToolNames { get; init; } = [];

    public IReadOnlyList<ChatMessage> FinalMessages { get; init; } = [];
}

public enum QueryTerminationReason
{
    NoToolCalls = 0,
    MaxRounds = 1,
    Error = 2,
    FailClosed = 3
}

public interface IQueryRuntimeEventSink
{
    bool IsEnabled(QueryRuntimeEventType eventType);

    ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent);
}

public enum QueryRuntimeEventType
{
    PromptAssemblySnapshot = 0,
    ModelResponseSampled = 1,
    ToolCallRequested = 2,
    ToolExecutionStarted = 3,
    ToolExecutionCompleted = 4,
    RoundStarted = 5,
    RoundCompleted = 6,
    Terminated = 7,
    Error = 8,
    PolicyInterventionDecision = 9,
    StopGateDecision = 10
}

public abstract record QueryRuntimeEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    DateTimeOffset Timestamp);

public sealed record PromptAssemblySnapshotEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    DateTimeOffset Timestamp,
    int Round,
    int MessageCount,
    bool ToolCallsAllowed,
    IReadOnlyList<string> ToolNames,
    string? RequiredToolName,
    bool RequiredToolSatisfied)
    : QueryRuntimeEvent(Seq, QueryId, SessionId, Timestamp);

public sealed record ModelResponseSampledEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    DateTimeOffset Timestamp,
    int Round,
    int AssistantTextLength,
    int StructuredToolCallCount,
    string AssistantText,
    IReadOnlyList<QueryRuntimeFunctionCallSnapshot> ToolCalls)
    : QueryRuntimeEvent(Seq, QueryId, SessionId, Timestamp);

public sealed record QueryRuntimeFunctionCallSnapshot(
    string CallId,
    string Name,
    IReadOnlyDictionary<string, object?> Arguments);

public sealed record ToolCallRequestedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    DateTimeOffset Timestamp,
    int Round,
    string ToolName,
    string CallId,
    IReadOnlyDictionary<string, object?> Arguments)
    : QueryRuntimeEvent(Seq, QueryId, SessionId, Timestamp);

public sealed record ToolExecutionStartedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    DateTimeOffset Timestamp,
    int Round,
    string ToolName,
    string CallId)
    : QueryRuntimeEvent(Seq, QueryId, SessionId, Timestamp);

public sealed record ToolExecutionCompletedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    DateTimeOffset Timestamp,
    int Round,
    string ToolName,
    string CallId,
    bool Success,
    int ResultLength,
    string Result)
    : QueryRuntimeEvent(Seq, QueryId, SessionId, Timestamp);

public sealed record PolicyInterventionDecisionEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    DateTimeOffset Timestamp,
    int Round,
    string ToolName,
    string CallId,
    string Decision,
    string? Reason,
    string? DetailCode,
    string? Feedback)
    : QueryRuntimeEvent(Seq, QueryId, SessionId, Timestamp);

public sealed record StopGateDecisionEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    DateTimeOffset Timestamp,
    int Round,
    string Decision,
    string? RequiredToolName,
    string? Reason,
    string? DetailCode,
    string? Feedback,
    int ContinuationCount)
    : QueryRuntimeEvent(Seq, QueryId, SessionId, Timestamp);

public sealed record RoundStartedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    DateTimeOffset Timestamp,
    int Round,
    int MaxRounds)
    : QueryRuntimeEvent(Seq, QueryId, SessionId, Timestamp);

public sealed record RoundCompletedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    DateTimeOffset Timestamp,
    int Round,
    int ToolCallCount,
    bool HasText,
    int TextLength,
    string? ContinueReason)
    : QueryRuntimeEvent(Seq, QueryId, SessionId, Timestamp);

public sealed record TerminatedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    DateTimeOffset Timestamp,
    QueryTerminationReason Reason,
    int TotalRounds,
    int TotalToolCalls,
    long TotalDurationMs,
    string? DetailCode,
    int ZeroToolCallRounds,
    int ContinuationCount,
    int WriteToolCalls,
    string? LastFunctionCall,
    string? RequiredToolName,
    bool RequiredToolSatisfied)
    : QueryRuntimeEvent(Seq, QueryId, SessionId, Timestamp);

public sealed record ErrorEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    DateTimeOffset Timestamp,
    string ErrorType,
    string Message,
    Exception? Exception)
    : QueryRuntimeEvent(Seq, QueryId, SessionId, Timestamp);
