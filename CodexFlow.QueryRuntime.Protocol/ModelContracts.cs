using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexFlow.QueryRuntime.Protocol;

public interface IRuntimeModelClient
{
    IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
        RuntimeModelRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Optional capability for model clients that accept the Runtime's authoritative
/// model-attempt information. The Runtime calls this overload only after it has
/// recorded the attempt in reducer state; clients that do not implement it keep
/// using <see cref="IRuntimeModelClient.StreamAsync"/> and must treat the attempt
/// ordinal as unavailable rather than inventing one.
/// </summary>
public interface IRuntimeModelAttemptClient : IRuntimeModelClient
{
    IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
        RuntimeModelRequest request,
        RuntimeModelAttemptContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Provider-free attempt metadata for one Runtime model sampling call.
/// </summary>
/// <param name="RuntimeModelAttemptOrdinal">
/// The Step's <c>ModelAttempts</c> value after the Runtime recorded this attempt
/// (1-based). After recovery it reflects only the recovered state and can repeat
/// an ordinal that was used before a crash.
/// </param>
/// <param name="RunAttemptId">
/// The host run/recovery attempt identity when known. It is unrelated to model
/// retries and HTTP retries.
/// </param>
public sealed record RuntimeModelAttemptContext(
    int RuntimeModelAttemptOrdinal,
    string? RunAttemptId = null)
{
    public int RuntimeModelAttemptOrdinal { get; } = RuntimeModelAttemptOrdinal >= 1
        ? RuntimeModelAttemptOrdinal
        : throw new ArgumentOutOfRangeException(nameof(RuntimeModelAttemptOrdinal));
}

public sealed record RuntimeModelRequest(
    RuntimeSessionId SessionId,
    RuntimeTurnId TurnId,
    RuntimeStepId StepId,
    IReadOnlyList<RuntimeMessage> Messages,
    IReadOnlyList<RuntimeToolDescriptor> Tools,
    RuntimeModelParameters Parameters,
    long HistoryVersion);

/// <param name="RequireJsonObject">
/// Legacy JSON-object switch interpreted by the host options factory. Prefer
/// <see cref="RuntimeModelParameters.ResponseFormat"/> with
/// <see cref="RuntimeResponseFormatKind.JsonObject"/>; the two cannot be combined.
/// </param>
public sealed record RuntimeModelParameters(
    string? Model = null,
    double? Temperature = null,
    int? MaxOutputTokens = null,
    bool RequireJsonObject = false,
    string? RequiredToolName = null)
{
    /// <summary>
    /// Provider-free structured output declaration. Null keeps the legacy
    /// behavior where the host options factory owns the provider format. It
    /// cannot be combined with <see cref="RequireJsonObject"/>; on a Step
    /// request it is the format actually in effect for that Step.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeResponseFormat? ResponseFormat { get; init; }
}

public enum RuntimeResponseFormatKind
{
    Text = 0,
    JsonObject = 1,
    JsonSchema = 2
}

public enum RuntimeResponseFormatScope
{
    /// <summary>The format applies to every Step.</summary>
    AllSteps = 0,

    /// <summary>The format applies only to Steps that expose no tools.</summary>
    ToolFreeStepsOnly = 1
}

/// <summary>
/// Requested model output format. Equality compares <see cref="Schema"/>
/// structurally, so two formats with the same schema document are equal.
/// </summary>
public sealed record RuntimeResponseFormat(
    RuntimeResponseFormatKind Kind,
    string? SchemaName = null,
    string? SchemaDescription = null,
    JsonElement? Schema = null,
    RuntimeResponseFormatScope Scope = RuntimeResponseFormatScope.AllSteps)
{
    public static RuntimeResponseFormat JsonObject(
        RuntimeResponseFormatScope scope = RuntimeResponseFormatScope.AllSteps)
        => new(RuntimeResponseFormatKind.JsonObject, Scope: scope);

    public static RuntimeResponseFormat ForJsonSchema(
        JsonElement schema,
        string? schemaName = null,
        string? schemaDescription = null,
        RuntimeResponseFormatScope scope = RuntimeResponseFormatScope.AllSteps)
        => new(RuntimeResponseFormatKind.JsonSchema, schemaName, schemaDescription, schema.Clone(), scope);

    /// <summary>
    /// Lowercase hex SHA-256 of the schema's compact UTF-8 JSON (property order
    /// preserved, no insignificant whitespace), or null when no schema is set.
    /// </summary>
    public string? ComputeSchemaSha256()
    {
        if (Schema is not { } schema || schema.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            schema.WriteTo(writer);
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    public bool Equals(RuntimeResponseFormat? other)
        => other is not null &&
           Kind == other.Kind &&
           Scope == other.Scope &&
           string.Equals(SchemaName, other.SchemaName, StringComparison.Ordinal) &&
           string.Equals(SchemaDescription, other.SchemaDescription, StringComparison.Ordinal) &&
           (Schema, other.Schema) switch
           {
               (null, null) => true,
               ({ } left, { } right) => JsonElement.DeepEquals(left, right),
               _ => false
           };

    public override int GetHashCode()
        => HashCode.Combine(Kind, Scope, SchemaName, SchemaDescription, ComputeSchemaSha256());
}

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
    bool Retryable = false)
{
    /// <summary>Provider-requested wait before retrying, when the response carried one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? RetryAfter { get; init; }
}

public sealed class RuntimeModelClientException(RuntimeError error, Exception? innerException = null)
    : Exception(error.Message, innerException)
{
    public RuntimeError Error { get; } = error;
}
