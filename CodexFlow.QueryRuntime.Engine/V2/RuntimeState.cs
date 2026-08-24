using System.Collections.ObjectModel;
using CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.QueryRuntime.Engine.V2;

public enum RuntimeTurnStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3
}

public enum RuntimeStepPhase
{
    Preparing = 0,
    Sampling = 1,
    ResolvingTools = 2,
    ExecutingTools = 3,
    CommittingObservation = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7
}

public enum RuntimeToolInvocationStatus
{
    Requested = 0,
    AwaitingApproval = 1,
    Approved = 2,
    Denied = 3,
    Executing = 4,
    Succeeded = 5,
    Failed = 6,
    Cancelled = 7
}

public sealed record RuntimeSessionState(
    RuntimeSessionId SessionId,
    long HistoryVersion,
    RuntimeTurnState? ActiveTurn,
    IReadOnlyList<RuntimeTurnState> TerminalTurns)
{
    public static RuntimeSessionState Create(RuntimeSessionId sessionId, long historyVersion = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId.Value);
        if (historyVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(historyVersion));
        }
        return new RuntimeSessionState(
            sessionId,
            historyVersion,
            null,
            Array.Empty<RuntimeTurnState>());
    }
}

public sealed record RuntimeTurnState(
    RuntimeTurnContext Context,
    RuntimeTurnStatus Status,
    IReadOnlyList<RuntimeStepState> Steps,
    RuntimeTurnProgress Progress,
    RuntimeTerminationReason? TerminationReason = null,
    RuntimeError? Error = null);

public sealed record RuntimeStepState(
    RuntimeStepContext Context,
    RuntimeStepPhase Phase,
    int ModelAttempts = 0,
    RuntimeModelOutput? Output = null,
    IReadOnlyList<RuntimeToolInvocationState>? ToolInvocations = null,
    RuntimeError? Error = null);

public sealed record RuntimeTurnContext(
    RuntimeSessionId SessionId,
    RuntimeTurnId TurnId,
    string Objective,
    DateTimeOffset CreatedAt,
    string? RequiredToolName = null);

public sealed record RuntimeTurnProgress(
    RuntimeUsageTotals Usage,
    string? RequiredToolName,
    bool RequiredToolSatisfied,
    int ContinuationCount,
    int ToolCallCount,
    RuntimeModelStopReason? LastModelStopReason)
{
    public static RuntimeTurnProgress Create(string? requiredToolName)
        => new(
            RuntimeUsageTotals.Empty,
            NormalizeOptional(requiredToolName),
            false,
            0,
            0,
            null);

    internal static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record RuntimeUsageTotals(
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    IReadOnlyDictionary<string, long> Additional)
{
    public static RuntimeUsageTotals Empty { get; } = new(
        0,
        0,
        0,
        new ReadOnlyDictionary<string, long>(
            new Dictionary<string, long>(StringComparer.Ordinal)));
}

public sealed record RuntimeModelOutput(
    IReadOnlyList<RuntimeItem> Items,
    RuntimeUsageTotals Usage,
    IReadOnlyList<RuntimeWarning> Warnings,
    RuntimeModelStopReason StopReason)
{
    public string Text => string.Concat(Items.OfType<RuntimeTextItem>().Select(static item => item.Text));

    public string Reasoning => string.Concat(Items.OfType<RuntimeReasoningItem>().Select(static item => item.Text));

    public IReadOnlyList<RuntimeToolCall> ToolCalls => Array.AsReadOnly(Items
        .OfType<RuntimeToolCallItem>()
        .Select(static item => item.Call)
        .ToArray());
}

public sealed record RuntimeToolInvocationState(
    RuntimeToolCall Call,
    RuntimeToolInvocationStatus Status,
    RuntimeToolResult? Result = null);

public sealed record RuntimeStepContext
{
    private RuntimeStepContext(
        RuntimeStepId stepId,
        int index,
        RuntimeModelRequest modelRequest,
        RuntimePolicySnapshot policy,
        RuntimeEnvironmentSnapshot environment,
        RuntimeBudgetSnapshot budget,
        long historyVersion,
        DateTimeOffset createdAt,
        PreparedRuntimeContext? preparedContext)
    {
        StepId = stepId;
        Index = index;
        ModelRequest = modelRequest;
        Policy = policy;
        Environment = environment;
        Budget = budget;
        HistoryVersion = historyVersion;
        CreatedAt = createdAt;
        PreparedContext = preparedContext;
    }

    public RuntimeStepId StepId { get; }

    public int Index { get; }

    public RuntimeModelRequest ModelRequest { get; }

    public RuntimePolicySnapshot Policy { get; }

    public RuntimeEnvironmentSnapshot Environment { get; }

    public RuntimeBudgetSnapshot Budget { get; }

    public long HistoryVersion { get; }

    public DateTimeOffset CreatedAt { get; }

    public PreparedRuntimeContext? PreparedContext { get; }

    public static RuntimeStepContext Create(
        RuntimeStepId stepId,
        int index,
        RuntimeModelRequest modelRequest,
        RuntimePolicySnapshot policy,
        RuntimeEnvironmentSnapshot environment,
        RuntimeBudgetSnapshot budget,
        long historyVersion,
        DateTimeOffset createdAt,
        PreparedRuntimeContext? preparedContext = null)
    {
        ArgumentNullException.ThrowIfNull(modelRequest);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRequest.SessionId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRequest.TurnId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRequest.StepId.Value);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        if (historyVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(historyVersion));
        }

        return new RuntimeStepContext(
            stepId,
            index,
            Snapshot(modelRequest),
            policy,
            environment,
            budget,
            historyVersion,
            createdAt,
            preparedContext == null ? null : Snapshot(preparedContext));
    }

    private static RuntimeModelRequest Snapshot(RuntimeModelRequest request)
        => request with
        {
            Messages = Array.AsReadOnly(request.Messages
                .Select(static message => message with
                {
                    Items = Array.AsReadOnly(message.Items.Select(SnapshotItem).ToArray())
                })
                .ToArray()),
            Tools = Array.AsReadOnly(request.Tools
                .Select(static tool => tool with { InputSchema = tool.InputSchema.Clone() })
                .ToArray())
        };

    private static PreparedRuntimeContext Snapshot(PreparedRuntimeContext context)
        => context with
        {
            Messages = Array.AsReadOnly(context.Messages.Select(static message => message with
            {
                Items = Array.AsReadOnly(message.Items.Select(SnapshotItem).ToArray())
            }).ToArray()),
            IncludedItemIds = Array.AsReadOnly(context.IncludedItemIds.ToArray()),
            OmittedItemIds = Array.AsReadOnly(context.OmittedItemIds.ToArray()),
            ReplacedItemIds = Array.AsReadOnly(context.ReplacedItemIds.ToArray()),
            Partitions = Array.AsReadOnly(context.Partitions.ToArray()),
            Events = Array.AsReadOnly(context.Events.ToArray())
        };

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
}

public sealed record RuntimePolicySnapshot
{
    public RuntimePolicySnapshot(string version, string profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        Version = version;
        Profile = profile;
    }

    public string Version { get; }

    public string Profile { get; }
}

public sealed record RuntimeEnvironmentSnapshot
{
    public RuntimeEnvironmentSnapshot(
        string executionMode,
        string? workspaceIdentity,
        string capabilityDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityDigest);
        ExecutionMode = executionMode;
        WorkspaceIdentity = workspaceIdentity;
        CapabilityDigest = capabilityDigest;
    }

    public string ExecutionMode { get; }

    public string? WorkspaceIdentity { get; }

    public string CapabilityDigest { get; }
}

public sealed record RuntimeBudgetSnapshot
{
    public RuntimeBudgetSnapshot(
        int maxSteps,
        int maxToolCalls,
        long? maxInputTokens = null,
        long? maxOutputTokens = null,
        int maxModelRetries = 0,
        int maxContinuations = 1)
    {
        if (maxSteps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSteps));
        }
        if (maxToolCalls < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxToolCalls));
        }
        if (maxInputTokens <= 0 && maxInputTokens.HasValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInputTokens));
        }
        if (maxOutputTokens <= 0 && maxOutputTokens.HasValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        }
        if (maxModelRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxModelRetries));
        }
        if (maxContinuations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContinuations));
        }

        MaxSteps = maxSteps;
        MaxToolCalls = maxToolCalls;
        MaxInputTokens = maxInputTokens;
        MaxOutputTokens = maxOutputTokens;
        MaxModelRetries = maxModelRetries;
        MaxContinuations = maxContinuations;
    }

    public int MaxSteps { get; }

    public int MaxToolCalls { get; }

    public long? MaxInputTokens { get; }

    public long? MaxOutputTokens { get; }

    public int MaxModelRetries { get; }

    public int MaxContinuations { get; }
}
