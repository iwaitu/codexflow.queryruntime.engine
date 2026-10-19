using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.QueryRuntime.Engine.V2;

public static class RuntimeAuditSchema
{
    /// <summary>Legacy schema, written whenever no enhanced feature is enabled.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Written for every event of a Turn that enables Q6 tool-call repair.</summary>
    public const int EnhancedVersion = 2;

    public const int MaxEventsPerTurn = 100_000;

    public static bool IsSupported(int version)
        => version is CurrentVersion or EnhancedVersion;

    internal static int ForRequest(RuntimeToolCallRepairPolicy? toolCallRepair)
        => toolCallRepair == null ? CurrentVersion : EnhancedVersion;
}

public readonly record struct RuntimeAuditEventId(string Value);

public enum RuntimeAuditEventKind
{
    TurnStarted = 0,
    ContextPrepared = 1,
    ModelRequestPrepared = 2,
    ModelResponseCommitted = 3,
    ToolObservationCommitted = 4,
    TurnTerminal = 5,

    /// <summary>Budget-driven tool finalization triggered (opt-in; absent from legacy recordings).</summary>
    ToolFinalizationTriggered = 6,

    /// <summary>
    /// A termination decision that changes the next Step's shape. Only decisions
    /// introduced after audit v1 (continue_without_tools) are recorded, so
    /// recordings of legacy decisions stay unchanged.
    /// </summary>
    TerminationDecided = 7,

    /// <summary>Q6 tool-call repair reservation or outcome (audit schema 2 only).</summary>
    ToolCallRepairAttempted = 8
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
[JsonDerivedType(typeof(RuntimeToolFinalizationAuditPayload), "tool_finalization")]
[JsonDerivedType(typeof(RuntimeTerminationDecisionAuditPayload), "termination_decision")]
[JsonDerivedType(typeof(RuntimeToolCallRepairAuditPayload), "tool_call_repair")]
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

public sealed record RuntimeModelRequestAuditPayload(RuntimeModelRequest Request) : RuntimeAuditPayload
{
    /// <summary>Effective response format kind for this Step; null when none is in effect.</summary>
    public string? ResponseFormatKind { get; init; }

    /// <summary>SHA-256 of the effective response schema; null when no schema is in effect.</summary>
    public string? ResponseFormatSchemaSha256 { get; init; }

    public static RuntimeModelRequestAuditPayload Create(RuntimeModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var format = request.Parameters.ResponseFormat;
        return new RuntimeModelRequestAuditPayload(request)
        {
            ResponseFormatKind = format == null ? null : RuntimeResponseFormats.KindName(format.Kind),
            ResponseFormatSchemaSha256 = format?.ComputeSchemaSha256()
        };
    }
}

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

/// <param name="Reason">tool_call_budget, deadline, or step_reserve.</param>
/// <param name="ElapsedMs">Turn time elapsed at the decision, from the Runtime TimeProvider.</param>
/// <param name="AfterStepId">The tool Step after whose batch finalization triggered.</param>
public sealed record RuntimeToolFinalizationAuditPayload(
    string Reason,
    int ToolCallCount,
    long ElapsedMs,
    RuntimeStepId AfterStepId) : RuntimeAuditPayload;

/// <param name="Decision">Currently always continue_without_tools.</param>
public sealed record RuntimeTerminationDecisionAuditPayload(
    RuntimeStepId AfterStepId,
    string Decision,
    string? Feedback) : RuntimeAuditPayload;

/// <summary>
/// Q6 repair record. The reservation phase is written before the repair request
/// and carries the exact repair request (sensitive); the outcome phase is written
/// before the Step commits and carries the raw repair response (sensitive) plus
/// the recovered tool identity and argument digest.
/// </summary>
/// <param name="Phase">reservation or outcome.</param>
/// <param name="Trigger">malformed_tool_call or invalid_tool_call_identity.</param>
/// <param name="ReservationIndex">One-based repair ordinal within the Turn.</param>
/// <param name="MaxPerTurn">The frozen shared repair budget.</param>
public sealed record RuntimeToolCallRepairAuditPayload(
    RuntimeStepId StepId,
    string Phase,
    string Trigger,
    int ReservationIndex,
    int MaxPerTurn) : RuntimeAuditPayload
{
    public string? Outcome { get; init; }

    public RuntimeModelRequest? RepairRequest { get; init; }

    public string? ResponseText { get; init; }

    public string? ResponseSha256 { get; init; }

    public string? RecoveredToolName { get; init; }

    public string? ArgumentsSha256 { get; init; }

    public RuntimeUsageTotals? Usage { get; init; }

    public string? ErrorCode { get; init; }
}

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
    long HistoryVersion = 0) : RuntimeAuditPayload
{
    /// <summary>Effective response format kind of a model request; never the schema text.</summary>
    public string? ResponseFormatKind { get; init; }

    /// <summary>Tool finalization reason or termination decision name.</summary>
    public string? Decision { get; init; }

    public long? ElapsedMs { get; init; }
}

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
        var executedObservationCount = 0;
        var committedUsage = RuntimeUsageTotals.Empty;
        string? lastCommittedText = null;
        var finalized = false;
        var toolFreeNext = false;
        RuntimeTurnTerminalAuditPayload? terminal = null;
        var recordingVersion = recording.Events.Count > 0 ? recording.Events[0].SchemaVersion : 0;
        var continueWithoutToolsCount = 0;
        var repairReservations = new Dictionary<string, RuntimeToolCallRepairAuditPayload>(StringComparer.Ordinal);
        var repairOutcomes = new Dictionary<string, RuntimeToolCallRepairAuditPayload>(StringComparer.Ordinal);
        int? repairBudget = null;

        for (var index = 0; index < recording.Events.Count; index++)
        {
            var current = recording.Events[index];
            if (!RuntimeAuditSchema.IsSupported(current.SchemaVersion))
            {
                throw Corrupt(
                    "audit_schema_incompatible",
                    $"Audit schema {current.SchemaVersion} is not supported; expected {RuntimeAuditSchema.CurrentVersion} or {RuntimeAuditSchema.EnhancedVersion}.",
                    RuntimeErrorCategory.SchemaIncompatible);
            }
            if (current.SchemaVersion != recordingVersion)
            {
                throw Corrupt(
                    "audit_schema_incompatible",
                    "All events of one recording must use the same audit schema.",
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
                    if ((finalized || toolFreeNext) && request.Request.Tools.Count != 0)
                    {
                        throw Corrupt("audit_tool_free_step_exposed_tools", "A Step after finalization or continue_without_tools exposed tools.");
                    }
                    toolFreeNext = false;
                    var expectedFormat = request.Request.Parameters.ResponseFormat;
                    if (!string.Equals(
                            request.ResponseFormatKind,
                            expectedFormat == null ? null : RuntimeResponseFormats.KindName(expectedFormat.Kind),
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            request.ResponseFormatSchemaSha256,
                            expectedFormat?.ComputeSchemaSha256(),
                            StringComparison.Ordinal))
                    {
                        throw Corrupt("audit_response_format_mismatch", "The audited response format does not match the recorded model request.");
                    }
                    break;
                case RuntimeModelResponseAuditPayload response:
                    if (!requests.Contains(response.StepId.Value) || !responses.Add(response.StepId.Value))
                    {
                        throw Corrupt("audit_model_response_without_request", "A model response has no unique preceding request.");
                    }
                    if (repairReservations.ContainsKey(response.StepId.Value))
                    {
                        ValidateRepairedResponse(response, repairOutcomes.GetValueOrDefault(response.StepId.Value));
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
                    if (observation.Result.Details?.Outcome is not (
                            RuntimeToolOutcome.Denied or RuntimeToolOutcome.Cancelled))
                    {
                        executedObservationCount++;
                    }
                    break;
                case RuntimeToolFinalizationAuditPayload value:
                    if (finalized || observations.Count == 0 ||
                        value.Reason is not ("tool_call_budget" or "deadline" or "step_reserve") ||
                        value.ToolCallCount < 0 || value.ElapsedMs < 0 ||
                        !responses.Contains(value.AfterStepId.Value))
                    {
                        throw Corrupt("audit_tool_finalization_invalid", "Tool finalization must follow a committed tool batch and occur at most once.");
                    }
                    finalized = true;
                    break;
                case RuntimeTerminationDecisionAuditPayload value:
                    if (value.Decision != "continue_without_tools" || !responses.Contains(value.AfterStepId.Value))
                    {
                        throw Corrupt("audit_termination_decision_invalid", "The recorded termination decision is unknown or orphaned.");
                    }
                    toolFreeNext = true;
                    continueWithoutToolsCount++;
                    if (repairBudget is { } sharedBudget &&
                        repairReservations.Count + continueWithoutToolsCount > sharedBudget)
                    {
                        throw Corrupt("audit_tool_call_repair_budget_exceeded", "Repairs and continue_without_tools exceed the shared budget.");
                    }
                    break;
                case RuntimeToolCallRepairAuditPayload repair:
                    if (recordingVersion != RuntimeAuditSchema.EnhancedVersion)
                    {
                        throw Corrupt(
                            "audit_tool_call_repair_schema_invalid",
                            "Tool-call repair events require audit schema 2.",
                            RuntimeErrorCategory.SchemaIncompatible);
                    }
                    var repairStep = repair.StepId.Value;
                    if (!requests.Contains(repairStep) || responses.Contains(repairStep) ||
                        !RuntimeToolCallRepairCodes.IsTrigger(repair.Trigger) ||
                        repair.MaxPerTurn is < 1 or > RuntimeToolCallRepairPolicy.MaxAllowedPerTurn ||
                        repairBudget is { } frozenBudget && frozenBudget != repair.MaxPerTurn)
                    {
                        throw Corrupt("audit_tool_call_repair_invalid", "A tool-call repair is orphaned, late, or inconsistent.");
                    }
                    repairBudget = repair.MaxPerTurn;
                    if (repair.Phase == RuntimeToolCallRepairCodes.PhaseReservation)
                    {
                        if (repair.Outcome != null || repair.RepairRequest == null ||
                            repair.RepairRequest.StepId != repair.StepId ||
                            repair.RepairRequest.Tools.Count != 0 ||
                            !repairReservations.TryAdd(repairStep, repair) ||
                            repair.ReservationIndex != repairReservations.Count ||
                            repairReservations.Count + continueWithoutToolsCount > repair.MaxPerTurn)
                        {
                            throw Corrupt("audit_tool_call_repair_reservation_invalid", "A Step may reserve one repair within the shared Turn budget.");
                        }
                    }
                    else if (repair.Phase == RuntimeToolCallRepairCodes.PhaseOutcome)
                    {
                        if (!repairReservations.TryGetValue(repairStep, out var reservation) ||
                            reservation.ReservationIndex != repair.ReservationIndex ||
                            !string.Equals(reservation.Trigger, repair.Trigger, StringComparison.Ordinal) ||
                            !RuntimeToolCallRepairCodes.IsKnownOutcome(repair.Outcome) ||
                            !repairOutcomes.TryAdd(repairStep, repair) ||
                            repair.Outcome == RuntimeToolCallRepairCodes.OutcomeToolCallRecovered &&
                            (string.IsNullOrWhiteSpace(repair.RecoveredToolName) || string.IsNullOrWhiteSpace(repair.ArgumentsSha256)) ||
                            repair.Outcome == RuntimeToolCallRepairCodes.OutcomeFinalPayloadReturned &&
                            string.IsNullOrWhiteSpace(repair.ResponseSha256))
                        {
                            throw Corrupt("audit_tool_call_repair_outcome_invalid", "A repair outcome must follow its own reservation exactly once.");
                        }
                    }
                    else
                    {
                        throw Corrupt("audit_tool_call_repair_phase_invalid", "Unknown tool-call repair phase.");
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
        if (completed && repairReservations.Keys.Any(step =>
                !repairOutcomes.TryGetValue(step, out var outcome) ||
                !RuntimeToolCallRepairCodes.IsSuccess(outcome.Outcome) ||
                !responses.Contains(step)))
        {
            throw Corrupt(
                "audit_tool_call_repair_interrupted",
                "A completed Turn cannot contain an interrupted or rejected tool-call repair.");
        }
        var trajectoryCountsInvalid = completed
            ? requests.Count != responses.Count ||
              terminal.TotalSteps != responses.Count ||
              terminal.TotalToolCalls != executedObservationCount
            : responses.Count > requests.Count ||
              terminal.TotalSteps < responses.Count ||
              executedObservationCount > terminal.TotalToolCalls;
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

    private static void ValidateRepairedResponse(
        RuntimeModelResponseAuditPayload response,
        RuntimeToolCallRepairAuditPayload? outcome)
    {
        if (outcome == null || !RuntimeToolCallRepairCodes.IsSuccess(outcome.Outcome))
        {
            throw Corrupt(
                "audit_tool_call_repair_commit_invalid",
                "A Step with a repair reservation committed output without a successful repair outcome.");
        }
        var calls = response.Output.ToolCalls;
        if (outcome.Outcome == RuntimeToolCallRepairCodes.OutcomeToolCallRecovered)
        {
            if (calls.Count != 1 ||
                !string.Equals(calls[0].Name, outcome.RecoveredToolName, StringComparison.Ordinal) ||
                !string.Equals(
                    RuntimeToolCallRepair.ArgumentsDigest(calls[0].Arguments),
                    outcome.ArgumentsSha256,
                    StringComparison.Ordinal))
            {
                throw Corrupt(
                    "audit_tool_call_repair_commit_mismatch",
                    "The committed tool call does not match the recorded repair outcome.");
            }
            return;
        }
        if (calls.Count != 0 ||
            !string.Equals(
                RuntimeToolCallRepair.TextDigest(response.Output.Text),
                outcome.ResponseSha256,
                StringComparison.Ordinal))
        {
            throw Corrupt(
                "audit_tool_call_repair_commit_mismatch",
                "The committed final payload does not match the recorded repair response.");
        }
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
            RuntimeAuditEventKind.ToolFinalizationTriggered =>
                value.Payload is RuntimeToolFinalizationAuditPayload finalization &&
                value.StepId == finalization.AfterStepId && value.InvocationId == null,
            RuntimeAuditEventKind.TerminationDecided =>
                value.Payload is RuntimeTerminationDecisionAuditPayload decision &&
                value.StepId == decision.AfterStepId && value.InvocationId == null,
            RuntimeAuditEventKind.ToolCallRepairAttempted =>
                value.Payload is RuntimeToolCallRepairAuditPayload repair &&
                value.StepId == repair.StepId && value.InvocationId == null,
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
[JsonSerializable(typeof(RuntimeToolFinalizationAuditPayload))]
[JsonSerializable(typeof(RuntimeTerminationDecisionAuditPayload))]
[JsonSerializable(typeof(RuntimeToolCallRepairAuditPayload))]
public partial class RuntimeAuditJsonContext : JsonSerializerContext;
