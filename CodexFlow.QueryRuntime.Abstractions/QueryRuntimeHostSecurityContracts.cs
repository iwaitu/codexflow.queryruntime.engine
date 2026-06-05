using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Abstractions;

/// <summary>
/// Host-provided policy hook that can inspect QRE tool calls before execution and
/// observe tool results after execution.
/// </summary>
public interface IQueryRuntimeToolIntervention
{
    /// <summary>
    /// Called before QRE invokes a tool selected by the model.
    /// </summary>
    ValueTask<QueryRuntimeToolInterventionDecision> BeforeToolCallAsync(
        QueryRuntimeToolCallContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Called after QRE has produced a tool result or captured a tool execution failure.
    /// </summary>
    ValueTask AfterToolExecutionAsync(
        QueryRuntimeToolExecutionResultContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Host-provided gate that can accept, continue, or fail a terminal candidate before
/// QRE returns a final assistant answer.
/// </summary>
public interface IQueryRuntimeStopGate
{
    ValueTask<QueryRuntimeStopDecision> BeforeStopAsync(
        QueryRuntimeBeforeStopContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Immutable context passed to a host before a model-selected tool is executed.
/// </summary>
public sealed record QueryRuntimeToolCallContext(
    string RunId,
    string SessionId,
    string? WorkspacePath,
    int Round,
    string ToolName,
    string CallId,
    IReadOnlyDictionary<string, object?> Arguments,
    IReadOnlyList<string> AvailableToolNames,
    string? RequiredToolName,
    IReadOnlyList<ChatMessage> Messages);

/// <summary>
/// Immutable context passed to a host after QRE has a tool result or captured
/// tool execution failure.
/// </summary>
public sealed record QueryRuntimeToolExecutionResultContext(
    string RunId,
    string SessionId,
    string? WorkspacePath,
    int Round,
    string ToolName,
    string CallId,
    bool Success,
    int ResultLength,
    string ResultSummary,
    string? ExceptionType,
    string? ExceptionMessage);

/// <summary>
/// Decision returned by a host tool-intervention hook.
/// </summary>
public sealed record QueryRuntimeToolInterventionDecision(
    QueryRuntimeToolInterventionDecisionKind Kind,
    string? Feedback = null,
    string? Reason = null,
    string? DetailCode = null)
{
    public static QueryRuntimeToolInterventionDecision Allow(string? reason = null)
        => new(QueryRuntimeToolInterventionDecisionKind.Allow, Reason: reason);

    public static QueryRuntimeToolInterventionDecision BlockWithFeedback(
        string feedback,
        string? reason = null,
        string? detailCode = null)
        => new(
            QueryRuntimeToolInterventionDecisionKind.BlockWithFeedback,
            feedback,
            reason,
            detailCode);

    public static QueryRuntimeToolInterventionDecision FailClosed(
        string? reason = null,
        string? detailCode = null)
        => new(QueryRuntimeToolInterventionDecisionKind.FailClosed, Reason: reason, DetailCode: detailCode);
}

/// <summary>
/// Supported pre-tool policy decisions. Argument rewrite is intentionally not
/// part of this first contract version.
/// </summary>
public enum QueryRuntimeToolInterventionDecisionKind
{
    /// <summary>Allow QRE to execute the selected tool.</summary>
    Allow = 0,

    /// <summary>Skip tool execution and return policy feedback to the model.</summary>
    BlockWithFeedback = 1,

    /// <summary>Terminate the run instead of continuing without host approval.</summary>
    FailClosed = 2
}

/// <summary>
/// Immutable context passed to a host before QRE accepts a terminal answer.
/// </summary>
public sealed record QueryRuntimeBeforeStopContext(
    string RunId,
    string SessionId,
    string? WorkspacePath,
    int Round,
    int MaxRounds,
    string AssistantText,
    IReadOnlyList<string> ExecutedToolNames,
    IReadOnlyList<string> SuccessfulToolNames,
    IReadOnlyList<string> ToolResultSummaries,
    int TotalToolCalls,
    int ZeroToolCallRounds,
    int ContinuationCount,
    int MaxContinuations,
    string? RequiredToolName,
    bool RequiredToolSatisfied,
    bool CanContinue,
    IReadOnlyList<ChatMessage> Messages);

/// <summary>
/// Decision returned by a host stop gate for a terminal-answer candidate.
/// </summary>
public sealed record QueryRuntimeStopDecision(
    QueryRuntimeStopDecisionKind Kind,
    string? Feedback = null,
    string? RequiredToolName = null,
    string? Reason = null,
    string? DetailCode = null)
{
    public static QueryRuntimeStopDecision Accept(string? reason = null)
        => new(QueryRuntimeStopDecisionKind.Accept, Reason: reason);

    public static QueryRuntimeStopDecision Continue(
        string feedback,
        string? reason = null,
        string? detailCode = null)
        => new(QueryRuntimeStopDecisionKind.Continue, feedback, Reason: reason, DetailCode: detailCode);

    public static QueryRuntimeStopDecision RequireTool(
        string toolName,
        string feedback,
        string? reason = null,
        string? detailCode = null)
        => new(QueryRuntimeStopDecisionKind.RequireTool, feedback, toolName, reason, detailCode);

    public static QueryRuntimeStopDecision FailClosed(
        string? reason = null,
        string? detailCode = null)
        => new(QueryRuntimeStopDecisionKind.FailClosed, Reason: reason, DetailCode: detailCode);
}

/// <summary>
/// Supported stop-gate decisions.
/// </summary>
public enum QueryRuntimeStopDecisionKind
{
    /// <summary>Accept the terminal candidate as the final answer.</summary>
    Accept = 0,

    /// <summary>Append host feedback and run another model round.</summary>
    Continue = 1,

    /// <summary>Append host feedback and require a named tool in the next round.</summary>
    RequireTool = 2,

    /// <summary>Terminate the run because the terminal candidate is not acceptable.</summary>
    FailClosed = 3
}
