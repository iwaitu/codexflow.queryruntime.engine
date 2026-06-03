using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Runtime;

public interface IStreamingToolExecutionPlanner
{
    StreamingToolExecutionDecision Decide(StreamingToolExecutionPlanRequest request);
}

public sealed record StreamingToolExecutionPlanRequest(
    QueryRuntimeRequest RuntimeRequest,
    QueryRuntimeState State,
    FunctionCallContent ToolCall,
    bool AllowToolCallsThisRound,
    IToolExecutionCoordinator? ToolCoordinator,
    IReadOnlySet<string> ActiveStreamingSignatures,
    int ActiveStreamingCount);

public sealed record StreamingToolExecutionDecision(
    bool ShouldStart,
    string Reason,
    string ToolName,
    string Signature,
    string? Detail = null)
{
    public static StreamingToolExecutionDecision Start(string toolName, string signature)
        => new(true, StreamingToolExecutionDecisionReasons.Started, toolName, signature);

    public static StreamingToolExecutionDecision Skip(string reason, string toolName, string? detail = null)
        => new(false, reason, toolName, string.Empty, detail);
}

public static class StreamingToolExecutionDecisionReasons
{
    public const string Started = "started";
    public const string Disabled = "disabled";
    public const string ToolExecutionUnavailable = "tool_execution_unavailable";
    public const string ToolsDisabledForRequest = "tools_disabled_for_request";
    public const string ToolCallsDisabledForRound = "tool_calls_disabled_for_round";
    public const string InterventionHookActive = "intervention_hook_active";
    public const string ConcurrencyLimitReached = "concurrency_limit_reached";
    public const string DeniedByName = "denied_by_name";
    public const string NotAllowedByName = "not_allowed_by_name";
    public const string MissingToolMetadata = "missing_tool_metadata";
    public const string NotConcurrencySafe = "not_concurrency_safe";
    public const string NotReadOnly = "not_read_only";
    public const string DestructiveTool = "destructive_tool";
    public const string NotCancelSafe = "not_cancel_safe";
    public const string DuplicateInStreamingRound = "duplicate_in_streaming_round";
}
