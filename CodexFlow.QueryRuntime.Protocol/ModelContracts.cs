using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexFlow.QueryRuntime.Protocol;

public interface IRuntimeModelClient
{
    IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
        RuntimeModelRequest request,
        CancellationToken ct = default);
}

public sealed record RuntimeModelRequest(
    RuntimeSessionId SessionId,
    RuntimeTurnId TurnId,
    RuntimeStepId StepId,
    IReadOnlyList<RuntimeMessage> Messages,
    IReadOnlyList<RuntimeToolDescriptor> Tools,
    RuntimeModelParameters Parameters,
    long HistoryVersion);

public sealed record RuntimeModelParameters(
    string? Model = null,
    double? Temperature = null,
    int? MaxOutputTokens = null,
    bool RequireJsonObject = false,
    string? RequiredToolName = null);

public sealed record RuntimeToolDescriptor(
    string CanonicalName,
    string Version,
    string Description,
    JsonElement InputSchema,
    RuntimeToolSideEffect SideEffect,
    RuntimeToolIdempotency Idempotency);

public enum RuntimeToolSideEffect
{
    None = 0,
    ReadOnly = 1,
    WorkspaceWrite = 2,
    External = 3
}

public enum RuntimeToolIdempotency
{
    Unknown = 0,
    Idempotent = 1,
    NonIdempotent = 2
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RuntimeTextDeltaEvent), "text_delta")]
[JsonDerivedType(typeof(RuntimeReasoningDeltaEvent), "reasoning_delta")]
[JsonDerivedType(typeof(RuntimeToolCallEvent), "tool_call")]
[JsonDerivedType(typeof(RuntimeUsageEvent), "usage")]
[JsonDerivedType(typeof(RuntimeWarningEvent), "warning")]
[JsonDerivedType(typeof(RuntimeModelCompletedEvent), "completed")]
public abstract record RuntimeModelStreamEvent;

public sealed record RuntimeTextDeltaEvent(string Text) : RuntimeModelStreamEvent;

public sealed record RuntimeReasoningDeltaEvent(
    string Text,
    string? ProtectedData = null) : RuntimeModelStreamEvent;

public sealed record RuntimeToolCallEvent(RuntimeToolCall Call) : RuntimeModelStreamEvent;

public sealed record RuntimeUsageEvent(RuntimeUsage Usage) : RuntimeModelStreamEvent;

public sealed record RuntimeWarningEvent(RuntimeWarning Warning) : RuntimeModelStreamEvent;

public sealed record RuntimeModelCompletedEvent(RuntimeModelStopReason StopReason) : RuntimeModelStreamEvent;

public sealed record RuntimeUsage(
    long? InputTokens = null,
    long? OutputTokens = null,
    long? TotalTokens = null,
    IReadOnlyDictionary<string, long>? Additional = null);

public sealed record RuntimeWarning(string Code, string Message);

public enum RuntimeModelStopReason
{
    Unknown = 0,
    EndTurn = 1,
    ToolCall = 2,
    MaxOutputTokens = 3,
    ContentFilter = 4,
    Cancelled = 5,
    Error = 6
}

public enum RuntimeTerminationReason
{
    Completed = 0,
    MaxSteps = 1,
    RequiredToolMissing = 2,
    StopGateRejected = 3,
    Cancelled = 4,
    Error = 5,
    FailClosed = 6
}

public enum RuntimeErrorCategory
{
    ProviderTransport = 0,
    ProviderRateLimit = 1,
    ProviderAuthentication = 2,
    ProviderProtocol = 3,
    ContextOverflow = 4,
    MalformedToolArguments = 5,
    UnknownTool = 6,
    UnsupportedItem = 7,
    PolicyDenied = 8,
    ApprovalDeclined = 9,
    ApprovalTimeout = 10,
    SandboxDenied = 11,
    SandboxTimeout = 12,
    ResourceExhausted = 13,
    ToolFailed = 14,
    Cancelled = 15,
    UncertainSideEffect = 16,
    TraceCorrupt = 17,
    SchemaIncompatible = 18,
    RuntimeInvariantViolation = 19
}

public sealed record RuntimeError(
    RuntimeErrorCategory Category,
    string Code,
    string Message,
    bool Retryable = false);

public sealed class RuntimeModelClientException(RuntimeError error, Exception? innerException = null)
    : Exception(error.Message, innerException)
{
    public RuntimeError Error { get; } = error;
}
