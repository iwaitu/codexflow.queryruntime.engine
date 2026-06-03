using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Phase 0B: 恢复检测上下文 — 传递给 IQueryRecoveryPolicy 的检测上下文
/// </summary>
public sealed record RecoveryContext(
    Exception? LastException = null,
    string? LastResponseText = null,
    IReadOnlyList<FunctionCallContent>? LastToolCalls = null,
    long? ContextChars = null,
    int? ConsecutiveSameToolCount = null);

/// <summary>
/// 恢复决策 — 由 IQueryRecoveryPolicy.DetectRecoveryNeeded 返回
/// </summary>
public sealed record RecoveryDecision(
    RecoveryType Type,
    bool NeedsRecovery,
    string Reason,
    int CurrentAttempt);

/// <summary>
/// 恢复动作 — 由 IQueryRecoveryPolicy.GetRecoveryAction 返回
/// </summary>
public sealed record RecoveryAction(
    RecoveryActionType Type,
    string? PromptInjection = null,
    int? RetryDelayMs = null,
    bool SilentRetry = false,
    IReadOnlyDictionary<string, object?>? ReducedOptions = null);

/// <summary>
/// 终止条件检查结果
/// </summary>
public sealed record TerminationCheckResult(
    bool ShouldTerminate,
    QueryTerminationReason? Reason,
    string? DetailCode = null);

/// <summary>
/// 继续原因常量 — 用于 telemetry 和状态记录
/// </summary>
public static class ContinueReasons
{
    public const string NextToolRound = "next_tool_round";
    public const string EmptyResponseRecovery = "empty_response_recovery";
    public const string MalformedProtocolRecovery = "malformed_protocol_recovery";
    public const string ZeroToolCallRecovery = "zero_tool_call_recovery";
    public const string ToolResultAppended = "tool_result_appended";
    public const string AutoDispatchContinuation = "autodispatch_continuation";
    public const string ContextCompactedRetry = "context_compacted_retry";
    public const string TransportFailureRecovery = "transport_failure_recovery";
    public const string UrgencyPromptInjected = "urgency_prompt_injected";
    public const string StopHookContinuation = "stop_hook_continuation";
    public const string RequiredToolContractRecovery = "required_tool_contract_recovery";
}
