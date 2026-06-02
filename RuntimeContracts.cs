using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Engine;

public interface IQueryRuntimeModelClient
{
    IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        QueryRuntimeModelRequest request,
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

    public int MaxRounds { get; init; } = 3;

    public bool EnableTools { get; init; }

    public IReadOnlyList<AIFunction> AvailableTools { get; init; } = [];

    public string? RequiredToolName { get; init; }
}

public sealed record QueryRuntimeResult(
    string RunId,
    string SessionId,
    string TraceFilePath,
    string FinalText,
    QueryTerminationReason TerminationReason,
    int TotalRounds,
    int TotalToolCalls,
    long TotalDurationMs);

public enum QueryTerminationReason
{
    NoToolCalls = 0,
    MaxRounds = 1,
    Error = 2
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
    Error = 8
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
    string? RequiredToolName)
    : QueryRuntimeEvent(Seq, QueryId, SessionId, Timestamp);

public sealed record ModelResponseSampledEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    DateTimeOffset Timestamp,
    int Round,
    int AssistantTextLength,
    int StructuredToolCallCount)
    : QueryRuntimeEvent(Seq, QueryId, SessionId, Timestamp);

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
    string? DetailCode)
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
