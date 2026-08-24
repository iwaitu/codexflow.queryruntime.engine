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

    public IRuntimeToolExecutor? ToolExecutor { get; init; }

    public IRuntimeToolAuthorization? ToolAuthorization { get; init; }

    public IRuntimeTerminationPolicy? TerminationPolicy { get; init; }
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
    FailClosed = 3
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
