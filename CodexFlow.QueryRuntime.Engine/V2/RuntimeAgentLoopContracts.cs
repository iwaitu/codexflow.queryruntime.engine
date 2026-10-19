using System.ComponentModel;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.QueryRuntime.Engine.V2;

public sealed record RuntimeAgentLoopRequest(
    RuntimeSessionId SessionId,
    RuntimeTurnId TurnId,
    string Objective,
    IReadOnlyList<RuntimeMessage> InitialMessages,
    IReadOnlyList<RuntimeToolDescriptor> Tools,
    RuntimeModelParameters ModelParameters,
    RuntimePolicySnapshot Policy,
    RuntimeEnvironmentSnapshot Environment,
    RuntimeBudgetSnapshot Budget,
    long HistoryVersion = 0,
    DateTimeOffset? CreatedAt = null)
{
    public IRuntimeToolExecutionPipeline? ToolPipeline { get; init; }

    /// <summary>
    /// Approves or declines the exact immutable execution plan produced by the
    /// C4 tool pipeline. The approval port is deliberately separate from the
    /// provisional Turn handle so an invocation id alone cannot authorize a
    /// re-evaluated plan.
    /// </summary>
    public IRuntimeToolApproval? ToolApproval { get; init; }

    public IRuntimeContextManager? ContextManager { get; init; }

    public IRuntimeContextEventSink? ContextEventSink { get; init; }

    public IRuntimeToolCatalogSelector? ToolCatalogSelector { get; init; }

    public IRuntimeAuditSink? AuditSink { get; init; }

    public RuntimeAuditFailureMode AuditFailureMode { get; init; } = RuntimeAuditFailureMode.FailClosed;

    public RuntimeRunAttempt? Attempt { get; init; }

    public IRuntimeCheckpointSink? CheckpointSink { get; init; }

    public RuntimeCheckpointFailureMode CheckpointFailureMode { get; init; } = RuntimeCheckpointFailureMode.FailClosed;

    /// <summary>
    /// Host-owned stable identifier for every non-serializable recovery
    /// dependency (context, policy, approval, tool composition, and hooks).
    /// It is mandatory whenever durable checkpoints are enabled.
    /// </summary>
    public string? RecoveryCompatibilityId { get; init; }

    public IRuntimeToolExecutor? ToolExecutor { get; init; }

    public IRuntimeToolAuthorization? ToolAuthorization { get; init; }

    public IRuntimeTerminationPolicy? TerminationPolicy { get; init; }

    /// <summary>
    /// Opt-in budget-driven finalization. Null keeps the legacy behavior where
    /// the Turn fails with <c>step_budget_exhausted</c> when tools consume the
    /// last Step.
    /// </summary>
    public RuntimeToolFinalizationPolicy? ToolFinalization { get; init; }

    /// <summary>
    /// Backoff between model retries. Null keeps the legacy behavior: a retryable
    /// failure is retried immediately within <see cref="RuntimeBudgetSnapshot.MaxModelRetries"/>.
    /// </summary>
    public RuntimeRetryPolicy? ModelRetryPolicy { get; init; }

    /// <summary>
    /// Opt-in bounded repair of a malformed tool-call response. Null keeps the
    /// legacy behavior where <c>malformed_tool_call</c> and
    /// <c>invalid_tool_call_identity</c> end the Turn; setting it switches the
    /// audit and checkpoint schema to <see cref="RuntimeAuditSchema.EnhancedVersion"/>.
    /// </summary>
    public RuntimeToolCallRepairPolicy? ToolCallRepair { get; init; }
}

/// <summary>
/// Bounded tool-call repair (Q6). A Step whose sample failed with a tool-protocol
/// error, and which accepted no tool call, may send one tool-free JSON repair
/// request. Repairs and <c>continue_without_tools</c> continuations share
/// <see cref="MaxPerTurn"/>. The reservation is durable before the request is sent.
/// </summary>
public sealed record RuntimeToolCallRepairPolicy
{
    public const int MaxPerStep = 1;

    public const int MaxAllowedPerTurn = 2;

    /// <summary>Host instruction placed before the exposed tool definitions.</summary>
    public required string Instruction { get; init; }

    /// <summary>Repairs plus continuations allowed per Turn; 1 or 2.</summary>
    public int MaxPerTurn { get; init; } = MaxAllowedPerTurn;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Instruction))
        {
            throw new ArgumentException("A tool-call repair instruction is required.", nameof(Instruction));
        }
        if (MaxPerTurn is < 1 or > MaxAllowedPerTurn)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPerTurn), "MaxPerTurn must be 1 or 2.");
        }
    }
}

/// <summary>Stable identifiers of the Q6 repair contract.</summary>
public static class RuntimeToolCallRepairCodes
{
    public const string TriggerMalformedToolCall = "malformed_tool_call";
    public const string TriggerInvalidToolCallIdentity = "invalid_tool_call_identity";

    public const string PhaseReservation = "reservation";
    public const string PhaseOutcome = "outcome";

    public const string OutcomeToolCallRecovered = "tool_call_recovered";
    public const string OutcomeFinalPayloadReturned = "final_payload_returned";
    public const string OutcomeInvalidJson = "invalid_json";
    public const string OutcomeInvalidShape = "invalid_shape";
    public const string OutcomeMultipleToolCalls = "multiple_tool_calls";
    public const string OutcomeUnknownTool = "unknown_tool";
    public const string OutcomeSideEffectNotReadOnly = "side_effect_not_read_only";
    public const string OutcomeInvalidArguments = "invalid_arguments";
    public const string OutcomeRequestFailed = "repair_request_failed";
    public const string OutcomeRequestCancelled = "repair_request_cancelled";

    public const string RejectedErrorCode = "tool_call_repair_rejected";
    public const string ContextBudgetExceededErrorCode = "context_budget_exceeded";

    internal static bool IsSuccess(string? outcome)
        => outcome is OutcomeToolCallRecovered or OutcomeFinalPayloadReturned;

    internal static bool IsKnownOutcome(string? outcome)
        => outcome is OutcomeToolCallRecovered or OutcomeFinalPayloadReturned or OutcomeInvalidJson or
            OutcomeInvalidShape or OutcomeMultipleToolCalls or OutcomeUnknownTool or
            OutcomeSideEffectNotReadOnly or OutcomeInvalidArguments or OutcomeRequestFailed or
            OutcomeRequestCancelled;

    internal static bool IsTrigger(string? code)
        => code is TriggerMalformedToolCall or TriggerInvalidToolCallIdentity;
}

/// <summary>
/// Exponential backoff with jitter for retryable model failures. Retries still
/// happen only while the failed attempt has produced no stream events and the
/// Step's MaxModelRetries budget allows another attempt. Waits use the
/// Runtime's <see cref="TimeProvider"/> and honor cancellation.
/// </summary>
public sealed record RuntimeRetryPolicy
{
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Symmetric jitter as a fraction of the computed delay, in [0, 1].</summary>
    public double JitterRatio { get; init; } = 0.2;

    /// <summary>Wait at least the provider's Retry-After (still capped at <see cref="MaxDelay"/>).</summary>
    public bool RespectRetryAfter { get; init; } = true;

    /// <summary>Uniform random source in [0, 1) for jitter; defaults to <see cref="Random.Shared"/>.</summary>
    public Func<double>? RandomSource { get; init; }

    /// <summary>
    /// Delay before the retry that follows failed attempt <paramref name="failedAttempt"/> (1-based):
    /// <c>min(BaseDelay * 2^(n-1), MaxDelay)</c> with jitter, raised to Retry-After when respected.
    /// </summary>
    public TimeSpan ComputeDelay(int failedAttempt, TimeSpan? retryAfter = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempt, 1);
        var maxMs = MaxDelay.TotalMilliseconds;
        var exponent = Math.Min(failedAttempt - 1, 62);
        var delayMs = Math.Min(BaseDelay.TotalMilliseconds * Math.Pow(2, exponent), maxMs);
        if (JitterRatio > 0 && delayMs > 0)
        {
            var sample = RandomSource?.Invoke() ?? Random.Shared.NextDouble();
            delayMs += delayMs * JitterRatio * (2 * Math.Clamp(sample, 0, 1) - 1);
        }
        if (RespectRetryAfter && retryAfter is { } requested && requested.TotalMilliseconds > delayMs)
        {
            delayMs = requested.TotalMilliseconds;
        }
        return TimeSpan.FromMilliseconds(Math.Clamp(delayMs, 0, maxMs));
    }

    internal void Validate()
    {
        if (BaseDelay < TimeSpan.Zero || MaxDelay < BaseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDelay), "Retry delays must satisfy 0 <= BaseDelay <= MaxDelay.");
        }
        if (!(JitterRatio >= 0 && JitterRatio <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(JitterRatio), "JitterRatio must be in [0, 1].");
        }
    }
}

public enum RuntimeFinalizationToolCallHandling
{
    /// <summary>Discard tool calls, record a warning, and keep the text answer.</summary>
    IgnoreAndUseText = 0,

    /// <summary>Fail the Turn closed when the model still calls tools.</summary>
    FailClosed = 1
}

public enum RuntimeToolFinalizationReason
{
    ToolCallBudget = 0,
    Deadline = 1,
    StepReserve = 2
}

/// <summary>
/// Moves a Turn into a tool-free finalization Step once a tool budget is nearly
/// spent. The check runs after each committed tool batch; when it triggers, the
/// instruction is appended as a user message and every later Step of the Turn
/// exposes no tools. It triggers at most once per Turn and the decision is
/// recorded in reducer state, checkpoints and audit rather than re-read from
/// the clock.
/// </summary>
public sealed record RuntimeToolFinalizationPolicy
{
    /// <summary>Trigger once the Turn's tool-call count reaches this value.</summary>
    public int? AfterToolCalls { get; init; }

    /// <summary>Turn deadline, relative to the Turn's <c>CreatedAt</c>.</summary>
    public TimeSpan? Deadline { get; init; }

    /// <summary>Fraction of <see cref="Deadline"/> after which finalization triggers.</summary>
    public double DeadlineFraction { get; init; } = 0.85;

    /// <summary>Trigger when only the final Step of the Step budget remains.</summary>
    public bool ReserveFinalStep { get; init; } = true;

    /// <summary>User message appended when finalization triggers.</summary>
    public required string Instruction { get; init; }

    public RuntimeFinalizationToolCallHandling ToolCallHandling { get; init; }
        = RuntimeFinalizationToolCallHandling.IgnoreAndUseText;

    internal void Validate()
    {
        if (AfterToolCalls is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AfterToolCalls), "AfterToolCalls must be positive.");
        }
        if (Deadline is { } deadline && deadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Deadline), "Deadline must be positive.");
        }
        if (!(DeadlineFraction > 0 && DeadlineFraction <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(DeadlineFraction), "DeadlineFraction must be in (0, 1].");
        }
        if (string.IsNullOrWhiteSpace(Instruction))
        {
            throw new ArgumentException("A finalization instruction is required.", nameof(Instruction));
        }
        if (!Enum.IsDefined(ToolCallHandling))
        {
            throw new ArgumentOutOfRangeException(nameof(ToolCallHandling));
        }
    }
}

public sealed record RuntimeAgentLoopResult(
    RuntimeSessionState Session,
    RuntimeTurnState Turn,
    IReadOnlyList<RuntimeMessage> History,
    string FinalText)
{
    public IReadOnlyList<PreparedRuntimeContext> PreparedContexts { get; init; } = [];

    public IReadOnlyList<RuntimeContextEvent> ContextEvents { get; init; } = [];

    public IReadOnlyList<RuntimeAuditEnvelope> AuditEvents { get; init; } = [];

    public IReadOnlyList<RuntimeWarning> AuditWarnings { get; init; } = [];

    public IReadOnlyList<RuntimeWarning> CheckpointWarnings { get; init; } = [];

    public RuntimeRunAttempt? Attempt { get; init; }

    public IReadOnlyDictionary<string, RuntimeHistoryBlob> HistoryBlobs { get; init; } =
        new ReadOnlyDictionary<string, RuntimeHistoryBlob>(
            new Dictionary<string, RuntimeHistoryBlob>(StringComparer.Ordinal));

    public RuntimeTurnStatus Status => Turn.Status;

    public RuntimeTerminationReason TerminationReason =>
        Turn.TerminationReason ?? RuntimeTerminationReason.Error;

    public RuntimeError? Error => Turn.Error;

    public RuntimeUsageTotals Usage => Turn.Progress.Usage;
}

public interface IRuntimeToolExecutor
{
    ValueTask<RuntimeToolResult> ExecuteAsync(
        RuntimeToolDescriptor descriptor,
        RuntimeToolCall call,
        RuntimeToolExecutionContext context,
        CancellationToken ct);
}

public interface IRuntimeToolApproval
{
    ValueTask<RuntimeToolApprovalDecision> DecideAsync(
        ResolvedExecutionPlan plan,
        RuntimeToolCall call,
        RuntimeToolExecutionContext context,
        CancellationToken ct);
}

public sealed record RuntimeToolApprovalDecision(bool Approved, string? Reason = null)
{
    public static RuntimeToolApprovalDecision Approve(string? reason = null) => new(true, reason);

    public static RuntimeToolApprovalDecision Decline(string? reason = null) => new(false, reason);
}

public sealed record RuntimeToolExecutionContext(
    RuntimeSessionId SessionId,
    RuntimeTurnId TurnId,
    RuntimeStepId StepId,
    RuntimePolicySnapshot Policy,
    RuntimeEnvironmentSnapshot Environment,
    RuntimeBudgetSnapshot Budget)
{
    public ResolvedExecutionPlan? Plan { get; init; }

    public IRuntimeSandbox? Sandbox { get; init; }
}

public interface IRuntimeToolAuthorization
{
    ValueTask<RuntimeToolAuthorizationDecision> AuthorizeAsync(
        RuntimeToolDescriptor descriptor,
        RuntimeToolCall call,
        RuntimeToolExecutionContext context,
        CancellationToken ct);
}

public enum RuntimeToolAuthorizationKind
{
    Allow = 0,
    Deny = 1,
    RequireApproval = 2
}

public sealed record RuntimeToolAuthorizationDecision(
    RuntimeToolAuthorizationKind Kind,
    string? Reason = null)
{
    public static RuntimeToolAuthorizationDecision Allow() => new(RuntimeToolAuthorizationKind.Allow);

    public static RuntimeToolAuthorizationDecision Deny(string? reason = null)
        => new(RuntimeToolAuthorizationKind.Deny, reason);

    public static RuntimeToolAuthorizationDecision RequireApproval(string? reason = null)
        => new(RuntimeToolAuthorizationKind.RequireApproval, reason);
}

public interface IRuntimeTerminationPolicy
{
    ValueTask<RuntimeTerminationDecision> DecideAsync(
        RuntimeTerminationContext context,
        CancellationToken ct);
}

public sealed record RuntimeTerminationContext(
    RuntimeSessionState Session,
    RuntimeStepState Step,
    IReadOnlyList<RuntimeMessage> History,
    bool CanContinue);

public enum RuntimeTerminationDecisionKind
{
    Accept = 0,
    Continue = 1,
    RequireTool = 2,
    FailClosed = 3,

    /// <summary>Continue, but expose no tools on the next Step only.</summary>
    ContinueWithoutTools = 4
}

public sealed record RuntimeTerminationDecision(
    RuntimeTerminationDecisionKind Kind,
    string? Feedback = null,
    string? RequiredToolName = null,
    RuntimeError? Error = null)
{
    public static RuntimeTerminationDecision Accept() => new(RuntimeTerminationDecisionKind.Accept);

    public static RuntimeTerminationDecision Continue(string feedback)
        => new(RuntimeTerminationDecisionKind.Continue, feedback);

    /// <summary>
    /// Like <see cref="Continue"/>, but the next Step exposes no tools, so a
    /// ToolFreeStepsOnly response format applies to it. Counts against the
    /// continuation and Step budgets.
    /// </summary>
    public static RuntimeTerminationDecision ContinueWithoutTools(string feedback)
        => new(RuntimeTerminationDecisionKind.ContinueWithoutTools, feedback);

    public static RuntimeTerminationDecision RequireTool(string toolName, string? feedback = null)
        => new(RuntimeTerminationDecisionKind.RequireTool, feedback, toolName);

    public static RuntimeTerminationDecision FailClosed(RuntimeError error)
        => new(RuntimeTerminationDecisionKind.FailClosed, Error: error);
}

/// <summary>
/// Provisional single-process interaction handle. This API intentionally has no
/// resume, durable cursor, ownership, or cross-process semantics.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class RuntimeTurnHandle : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentQueue<RuntimeMessage> _steering = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _approvals =
        new(StringComparer.Ordinal);
    private int _disposed;

    internal CancellationToken CancellationToken => _cancellation.Token;

    internal bool HasPendingSteering => !_steering.IsEmpty;

    public void Cancel()
    {
        ThrowIfDisposed();
        _cancellation.Cancel();
    }

    public void Steer(RuntimeMessage message)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);
        _steering.Enqueue(message with
        {
            Items = Array.AsReadOnly(message.Items.Select(SnapshotItem).ToArray())
        });
    }

    public bool Approve(RuntimeInvocationId invocationId)
        => ResolveApproval(invocationId, approved: true);

    public bool Decline(RuntimeInvocationId invocationId)
        => ResolveApproval(invocationId, approved: false);

    internal IReadOnlyList<RuntimeMessage> DrainSteering()
    {
        var messages = new List<RuntimeMessage>();
        while (_steering.TryDequeue(out var message))
        {
            messages.Add(message);
        }
        return messages;
    }

    internal async ValueTask<bool> WaitForApprovalAsync(
        RuntimeInvocationId invocationId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId.Value);
        var completion = _approvals.GetOrAdd(
            invocationId.Value,
            static _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _approvals.TryRemove(invocationId.Value, out _);
        }
    }

    private bool ResolveApproval(RuntimeInvocationId invocationId, bool approved)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId.Value);
        var completion = _approvals.GetOrAdd(
            invocationId.Value,
            static _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        return completion.TrySetResult(approved);
    }

    private static RuntimeItem SnapshotItem(RuntimeItem item)
        => item switch
        {
            RuntimeTextItem text => text,
            RuntimeReasoningItem reasoning => reasoning,
            RuntimeToolCallItem toolCall => toolCall with
            {
                Call = toolCall.Call with { Arguments = toolCall.Call.Arguments.Clone() }
            },
            RuntimeToolResultItem toolResult => toolResult with
            {
                Result = toolResult.Result with
                {
                    Artifacts = toolResult.Result.Artifacts == null
                        ? null
                        : Array.AsReadOnly(toolResult.Result.Artifacts.ToArray())
                }
            },
            RuntimeArtifactItem artifact => artifact,
            _ => throw new ArgumentException(
                $"Unsupported Runtime item type '{item.GetType().FullName}'.",
                nameof(item))
        };

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _cancellation.Cancel();
        _cancellation.Dispose();
        foreach (var completion in _approvals.Values)
        {
            completion.TrySetCanceled();
        }
        _approvals.Clear();
    }
}
