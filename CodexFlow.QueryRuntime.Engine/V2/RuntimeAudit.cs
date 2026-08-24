using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.QueryRuntime.Engine.V2;

public static class RuntimeAuditSchema
{
    public const int CurrentVersion = 1;

    public const int MaxEventsPerTurn = 100_000;
}

public readonly record struct RuntimeAuditEventId(string Value);

public enum RuntimeAuditEventKind
{
    TurnStarted = 0,
    ContextPrepared = 1,
    ModelRequestPrepared = 2,
    ModelResponseCommitted = 3,
    ToolObservationCommitted = 4,
    TurnTerminal = 5
}

public enum RuntimeAuditSensitivity
{
    Public = 0,
    Internal = 1,
    Sensitive = 2
}

public enum RuntimeAuditDataMode
{
    PublicRedacted = 0,
    PrivateDiagnostic = 1,
    SanitizedFixture = 2
}

public enum RuntimeAuditReplayCapability
{
    SummaryOnly = 0,
    Recorded = 1
}

public enum RuntimeAuditFailureMode
{
    FailClosed = 0,
    BestEffort = 1
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "payloadType")]
[JsonDerivedType(typeof(RuntimeTurnStartedAuditPayload), "turn_started")]
[JsonDerivedType(typeof(RuntimeContextPreparedAuditPayload), "context_prepared")]
[JsonDerivedType(typeof(RuntimeModelRequestAuditPayload), "model_request")]
[JsonDerivedType(typeof(RuntimeModelResponseAuditPayload), "model_response")]
[JsonDerivedType(typeof(RuntimeToolObservationAuditPayload), "tool_observation")]
[JsonDerivedType(typeof(RuntimeTurnTerminalAuditPayload), "turn_terminal")]
[JsonDerivedType(typeof(RuntimePublicAuditPayload), "public_summary")]
public abstract record RuntimeAuditPayload;

public sealed record RuntimeTurnStartedAuditPayload(
    string Objective,
    long InitialHistoryVersion,
    IReadOnlyList<RuntimeMessage> InitialMessages,
    RuntimePolicySnapshot Policy,
    RuntimeEnvironmentSnapshot Environment,
    RuntimeBudgetSnapshot Budget) : RuntimeAuditPayload;

public sealed record RuntimeContextPreparedAuditPayload(
    long HistoryVersion,
    int EstimatedTokens,
    int ReservedToolTokens,
    bool Compacted,
    IReadOnlyList<RuntimeHistoryItemId> IncludedItemIds,
    IReadOnlyList<RuntimeHistoryItemId> OmittedItemIds,
    IReadOnlyList<RuntimeHistoryItemId> ReplacedItemIds,
    IReadOnlyList<RuntimeContextEvent> Events) : RuntimeAuditPayload;

public sealed record RuntimeModelRequestAuditPayload(RuntimeModelRequest Request) : RuntimeAuditPayload;

public sealed record RuntimeModelResponseAuditPayload(
    RuntimeStepId StepId,
    RuntimeModelOutput Output) : RuntimeAuditPayload;

public sealed record RuntimeToolObservationAuditPayload(
    RuntimeStepId StepId,
    RuntimeToolCall Call,
    RuntimeToolResult Result) : RuntimeAuditPayload;

public sealed record RuntimeTurnTerminalAuditPayload(
    RuntimeTurnStatus Status,
    RuntimeTerminationReason TerminationReason,
    RuntimeError? Error,
    string FinalText,
    RuntimeUsageTotals Usage,
    int TotalSteps,
    int TotalToolCalls,
    int ContinuationCount,
    long HistoryVersion,
    IReadOnlyList<RuntimeMessage> CanonicalHistory) : RuntimeAuditPayload;

/// <summary>
/// Explicit allow-list projection used by public durable audit. It contains
/// counts and terminal classifications only, never prompts, model text,
/// reasoning, tool names, arguments, results, paths, or host identifiers.
/// </summary>
public sealed record RuntimePublicAuditPayload(
    int MessageCount = 0,
    int ItemCount = 0,
    int ToolCount = 0,
    int ToolCallCount = 0,
    int TextLength = 0,
    int ReasoningLength = 0,
    int IncludedItemCount = 0,
    int OmittedItemCount = 0,
    int ReplacedItemCount = 0,
    int EstimatedTokens = 0,
    int ReservedToolTokens = 0,
    bool Compacted = false,
    bool? ToolSuccess = null,
    RuntimeModelStopReason? StopReason = null,
    RuntimeTurnStatus? TurnStatus = null,
    RuntimeTerminationReason? TerminationReason = null,
    string? ErrorCode = null,
    long InputTokens = 0,
    long OutputTokens = 0,
    long TotalTokens = 0,
    int TotalSteps = 0,
    int ContinuationCount = 0,
    long HistoryVersion = 0) : RuntimeAuditPayload;

public sealed record RuntimeAuditEnvelope(
    int SchemaVersion,
    long Sequence,
    RuntimeAuditEventId EventId,
    DateTimeOffset Timestamp,
    RuntimeAuditEventKind Kind,
    RuntimeSessionId SessionId,
    RuntimeTurnId TurnId,
    RuntimeStepId? StepId,
    RuntimeInvocationId? InvocationId,
    RuntimeAuditEventId? CausationId,
    string CorrelationId,
    RuntimeAuditSensitivity Sensitivity,
    RuntimeAuditPayload Payload);

public sealed record RuntimeAuditBlobReference(
    string Algorithm,
    string Digest,
    long SizeBytes,
    string Path);

public sealed record RuntimePersistedAuditRecord(
    int SchemaVersion,
    long Sequence,
    string EventId,
    DateTimeOffset Timestamp,
    RuntimeAuditEventKind Kind,
    string SessionId,
    string TurnId,
    string? StepId,
    string? InvocationId,
    string? CausationId,
    string CorrelationId,
    RuntimeAuditSensitivity Sensitivity,
    RuntimeAuditDataMode DataMode,
    RuntimeAuditReplayCapability ReplayCapability,
    RuntimeAuditPayload? Payload,
    RuntimeAuditBlobReference? PayloadBlob);

public sealed record RuntimeAuditManifest(
    int SchemaVersion,
    string Type,
    string RunId,
    RuntimeAuditDataMode DataMode,
    RuntimeAuditReplayCapability ReplayCapability,
    string Status,
    long EventCount,
    long EventBytes,
    long BlobBytes,
    string? TerminationReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RuntimeAuditRecording(
    RuntimeAuditDataMode DataMode,
    RuntimeAuditReplayCapability ReplayCapability,
    IReadOnlyList<RuntimeAuditEnvelope> Events,
    string? SourcePath = null);

public interface IRuntimeAuditSink
{
    ValueTask OnEventAsync(RuntimeAuditEnvelope auditEvent, CancellationToken ct);
}

public sealed class InMemoryRuntimeAuditSink : IRuntimeAuditSink
{
    private readonly object _sync = new();
    private readonly List<RuntimeAuditEnvelope> _events = [];
    private readonly int _maxEvents;

    public InMemoryRuntimeAuditSink(int maxEvents = RuntimeAuditSchema.MaxEventsPerTurn)
    {
        if (maxEvents <= 0 || maxEvents > RuntimeAuditSchema.MaxEventsPerTurn)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents));
        }
        _maxEvents = maxEvents;
    }

    public IReadOnlyList<RuntimeAuditEnvelope> Events
    {
        get
        {
            lock (_sync)
            {
                return Array.AsReadOnly(_events.ToArray());
            }
        }
    }

    public ValueTask OnEventAsync(RuntimeAuditEnvelope auditEvent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(auditEvent);
        lock (_sync)
        {
            if (_events.Count >= _maxEvents)
            {
                throw new InvalidDataException($"C6 in-memory audit exceeds the {_maxEvents} event quota.");
            }
            _events.Add(auditEvent);
        }
        return ValueTask.CompletedTask;
    }
}

public sealed record RuntimeRecordedReplayOptions
{
    public int MaxEvents { get; init; } = 100_000;

    internal void Validate()
    {
        if (MaxEvents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEvents));
        }
    }
}

public sealed record RuntimeRecordedReplayResult(
    string FinalText,
    RuntimeTurnStatus Status,
    RuntimeTerminationReason TerminationReason,
    RuntimeError? Error,
    RuntimeUsageTotals Usage,
    IReadOnlyList<RuntimeMessage> CanonicalHistory,
    int TotalSteps,
    int TotalToolCalls,
    int ContinuationCount,
    int EventCount,
    string ReplayDigest,
    bool ProviderCalls,
    bool ToolExecutions);

/// <summary>
/// Replays a sanitized/private recording as data through a strict validation
/// reducer. It never accepts a provider or tool executor and therefore cannot
/// perform external calls or side effects.
/// </summary>
public static class RuntimeRecordedReplay
{
    public static RuntimeRecordedReplayResult Replay(
        RuntimeAuditRecording recording,
        RuntimeRecordedReplayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(recording);
        options ??= new RuntimeRecordedReplayOptions();
        options.Validate();
        if (recording.ReplayCapability != RuntimeAuditReplayCapability.Recorded ||
            recording.DataMode == RuntimeAuditDataMode.PublicRedacted)
        {
            throw Corrupt("audit_summary_only", "Public redacted audit is inspectable but not replayable.");
        }
        if (recording.Events.Count == 0 || recording.Events.Count > options.MaxEvents)
        {
            throw Corrupt("audit_event_count_invalid", "The audit event count is empty or exceeds the replay limit.");
        }

        RuntimeAuditEnvelope? previous = null;
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        RuntimeSessionId? sessionId = null;
        RuntimeTurnId? turnId = null;
        var requests = new HashSet<string>(StringComparer.Ordinal);
        var responses = new HashSet<string>(StringComparer.Ordinal);
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var observations = new HashSet<string>(StringComparer.Ordinal);
        var expectedObservationOrder = new Queue<string>();
        var committedUsage = RuntimeUsageTotals.Empty;
        string? lastCommittedText = null;
        RuntimeTurnTerminalAuditPayload? terminal = null;

        for (var index = 0; index < recording.Events.Count; index++)
        {
            var current = recording.Events[index];
            if (current.SchemaVersion != RuntimeAuditSchema.CurrentVersion)
            {
                throw Corrupt(
                    "audit_schema_incompatible",
                    $"Audit schema {current.SchemaVersion} is not supported; expected {RuntimeAuditSchema.CurrentVersion}.",
                    RuntimeErrorCategory.SchemaIncompatible);
            }
            if (current.Sequence != index + 1)
            {
                throw Corrupt("audit_sequence_invalid", "Audit sequence must be contiguous and start at one.");
            }
            if (string.IsNullOrWhiteSpace(current.EventId.Value) || !eventIds.Add(current.EventId.Value))
            {
                throw Corrupt("audit_event_id_invalid", "Audit event IDs must be non-empty and unique.");
            }
            if (index == 0)
            {
                if (current.Kind != RuntimeAuditEventKind.TurnStarted || current.CausationId != null)
                {
                    throw Corrupt("audit_root_invalid", "The recording must start with an uncaused TurnStarted event.");
                }
                sessionId = current.SessionId;
                turnId = current.TurnId;
            }
            else if (current.CausationId?.Value != previous!.EventId.Value)
            {
                throw Corrupt("audit_causation_invalid", "Each audit event must be caused by the preceding event.");
            }
            if (current.SessionId != sessionId || current.TurnId != turnId ||
                !string.Equals(current.CorrelationId, turnId!.Value.Value, StringComparison.Ordinal))
            {
                throw Corrupt("audit_correlation_invalid", "Audit identity or correlation changed within one recording.");
            }
            ValidateEnvelopeShape(current);

            switch (current.Payload)
            {
                case RuntimeModelRequestAuditPayload request:
                    if (!requests.Add(request.Request.StepId.Value))
                    {
                        throw Corrupt("audit_duplicate_model_request", "A Step contains more than one committed model request.");
                    }
                    break;
                case RuntimeModelResponseAuditPayload response:
                    if (!requests.Contains(response.StepId.Value) || !responses.Add(response.StepId.Value))
                    {
                        throw Corrupt("audit_model_response_without_request", "A model response has no unique preceding request.");
                    }
                    foreach (var call in response.Output.ToolCalls)
                    {
                        if (!calls.Add(call.InvocationId.Value))
                        {
                            throw Corrupt("audit_duplicate_tool_call", "A tool invocation ID was emitted more than once.");
                        }
                        expectedObservationOrder.Enqueue(call.InvocationId.Value);
                    }
                    committedUsage = AddUsage(committedUsage, response.Output.Usage);
                    lastCommittedText = response.Output.Text;
                    break;
                case RuntimeToolObservationAuditPayload observation:
                    if (!calls.Contains(observation.Call.InvocationId.Value) ||
                        observation.Result.InvocationId != observation.Call.InvocationId ||
                        !observations.Add(observation.Call.InvocationId.Value) ||
                        expectedObservationOrder.Count == 0 ||
                        !string.Equals(
                            expectedObservationOrder.Dequeue(),
                            observation.Call.InvocationId.Value,
                            StringComparison.Ordinal))
                    {
                        throw Corrupt("audit_tool_observation_invalid", "A tool observation is orphaned, duplicated, or has mismatched identity.");
                    }
                    break;
                case RuntimeTurnTerminalAuditPayload value:
                    if (index != recording.Events.Count - 1 || terminal != null)
                    {
                        throw Corrupt("audit_terminal_invalid", "The recording must contain exactly one final terminal event.");
                    }
                    terminal = value;
                    break;
            }
            previous = current;
        }

        if (terminal == null)
        {
            throw Corrupt("audit_terminal_missing", "The recording has no terminal event.");
        }
        var completed = terminal.Status == RuntimeTurnStatus.Completed;
        var trajectoryCountsInvalid = completed
            ? requests.Count != responses.Count ||
              terminal.TotalSteps != responses.Count ||
              terminal.TotalToolCalls != observations.Count
            : responses.Count > requests.Count ||
              terminal.TotalSteps < responses.Count ||
              observations.Count > terminal.TotalToolCalls;
        if (trajectoryCountsInvalid)
        {
            throw Corrupt("audit_terminal_counts_mismatch", "Terminal counts do not match the replayed trajectory.");
        }
        if (responses.Count > 0 && !string.Equals(terminal.FinalText, lastCommittedText, StringComparison.Ordinal))
        {
            throw Corrupt("audit_terminal_text_mismatch", "Terminal final text does not match the last committed model response.");
        }
        if (terminal.Usage.InputTokens != committedUsage.InputTokens ||
            terminal.Usage.OutputTokens != committedUsage.OutputTokens ||
            terminal.Usage.TotalTokens != committedUsage.TotalTokens)
        {
            throw Corrupt("audit_terminal_usage_mismatch", "Terminal usage does not match committed model responses.");
        }
        var historyObservationIds = terminal.CanonicalHistory
            .SelectMany(static message => message.Items)
            .OfType<RuntimeToolResultItem>()
            .Select(static item => item.Result.InvocationId.Value)
            .ToHashSet(StringComparer.Ordinal);
        if (observations.Any(value => !historyObservationIds.Contains(value)))
        {
            throw Corrupt("audit_terminal_history_mismatch", "Terminal canonical history omits a committed tool observation.");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            recording.Events.ToArray(),
            RuntimeAuditJsonContext.Default.RuntimeAuditEnvelopeArray);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new RuntimeRecordedReplayResult(
            terminal.FinalText,
            terminal.Status,
            terminal.TerminationReason,
            terminal.Error,
            terminal.Usage,
            terminal.CanonicalHistory,
            terminal.TotalSteps,
            terminal.TotalToolCalls,
            terminal.ContinuationCount,
            recording.Events.Count,
            digest,
            ProviderCalls: false,
            ToolExecutions: false);
    }

    private static RuntimeAuditReplayException Corrupt(
        string code,
        string message,
        RuntimeErrorCategory category = RuntimeErrorCategory.TraceCorrupt)
        => new(new RuntimeError(category, code, message));

    private static void ValidateEnvelopeShape(RuntimeAuditEnvelope value)
    {
        var valid = value.Kind switch
        {
            RuntimeAuditEventKind.TurnStarted =>
                value.Payload is RuntimeTurnStartedAuditPayload && value.StepId == null && value.InvocationId == null,
            RuntimeAuditEventKind.ContextPrepared =>
                value.Payload is RuntimeContextPreparedAuditPayload && value.StepId != null && value.InvocationId == null,
            RuntimeAuditEventKind.ModelRequestPrepared =>
                value.Payload is RuntimeModelRequestAuditPayload request &&
                value.StepId == request.Request.StepId && value.InvocationId == null,
            RuntimeAuditEventKind.ModelResponseCommitted =>
                value.Payload is RuntimeModelResponseAuditPayload response &&
                value.StepId == response.StepId && value.InvocationId == null,
            RuntimeAuditEventKind.ToolObservationCommitted =>
                value.Payload is RuntimeToolObservationAuditPayload observation &&
                value.StepId == observation.StepId &&
                value.InvocationId == observation.Call.InvocationId &&
                observation.Result.InvocationId == observation.Call.InvocationId,
            RuntimeAuditEventKind.TurnTerminal =>
                value.Payload is RuntimeTurnTerminalAuditPayload && value.StepId == null && value.InvocationId == null,
            _ => false
        };
        if (!valid)
        {
            throw Corrupt("audit_envelope_shape_invalid", "Audit kind, payload, Step ID, or invocation ID is inconsistent.");
        }
    }

    private static RuntimeUsageTotals AddUsage(RuntimeUsageTotals left, RuntimeUsageTotals right)
    {
        var additional = new Dictionary<string, long>(left.Additional, StringComparer.Ordinal);
        foreach (var pair in right.Additional)
        {
            additional[pair.Key] = checked(additional.GetValueOrDefault(pair.Key) + pair.Value);
        }
        return new RuntimeUsageTotals(
            checked(left.InputTokens + right.InputTokens),
            checked(left.OutputTokens + right.OutputTokens),
            checked(left.TotalTokens + right.TotalTokens),
            additional);
    }
}

public sealed class RuntimeAuditReplayException(RuntimeError error, Exception? innerException = null)
    : Exception(error.Message, innerException)
{
    public RuntimeError Error { get; } = error;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(RuntimeAuditEnvelope))]
[JsonSerializable(typeof(RuntimeAuditEnvelope[]))]
[JsonSerializable(typeof(RuntimePersistedAuditRecord))]
[JsonSerializable(typeof(RuntimeAuditManifest))]
[JsonSerializable(typeof(RuntimeAuditPayload))]
[JsonSerializable(typeof(RuntimeTurnStartedAuditPayload))]
[JsonSerializable(typeof(RuntimeContextPreparedAuditPayload))]
[JsonSerializable(typeof(RuntimeModelRequestAuditPayload))]
[JsonSerializable(typeof(RuntimeModelResponseAuditPayload))]
[JsonSerializable(typeof(RuntimeToolObservationAuditPayload))]
[JsonSerializable(typeof(RuntimeTurnTerminalAuditPayload))]
[JsonSerializable(typeof(RuntimePublicAuditPayload))]
public partial class RuntimeAuditJsonContext : JsonSerializerContext;
