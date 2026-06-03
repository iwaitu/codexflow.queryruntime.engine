using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;
using System.Diagnostics;
using System.Text;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Phase 0B: Query Runtime 内部可变状态 — runtime 维护，入口层不直接访问
/// </summary>
public sealed class QueryRuntimeState
{
    /// <summary>当前消息列表（每轮增长）</summary>
    public List<ChatMessage> Messages { get; } = new();

    /// <summary>当前轮次</summary>
    public int Round { get; set; }

    /// <summary>最大轮次（来自 request，可在恢复/保留总结轮时由 runtime 动态延长）</summary>
    public int MaxRounds { get; set; }

    /// <summary>本轮 LLM 输出的文本内容</summary>
    public StringBuilder LastAssistantText { get; } = new();

    /// <summary>最后一条非空的可见 assistant 文本，用于空尾轮时保留用户已看到的结果</summary>
    public StringBuilder LastNonEmptyAssistantText { get; } = new();

    /// <summary>本轮思维链内容</summary>
    public StringBuilder LastThinkingText { get; } = new();

    /// <summary>本轮工具调用列表</summary>
    public List<FunctionCallContent> LastToolCalls { get; } = new();

    /// <summary>最近一次模型响应抽取出的工具计划。</summary>
    public ToolPlan? LastToolPlan { get; set; }

    /// <summary>最近一次工具计划校验结果。</summary>
    public ToolPlanValidationResult? LastToolPlanValidation { get; set; }

    /// <summary>累计工具调用总数</summary>
    public int TotalToolCalls { get; set; }

    /// <summary>当前 query 中实际执行过的工具名集合。</summary>
    public HashSet<string> ExecutedToolNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前 query 中成功执行过的工具名集合。</summary>
    public HashSet<string> SuccessfulToolNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>累计写操作工具调用总数（write_file / apply_patch / ivilson_smart_patch / ivilson_write）</summary>
    public int TotalWriteToolCalls { get; set; }

    /// <summary>无工具调用轮次计数</summary>
    public int ZeroToolCallRounds { get; set; }

    /// <summary>空响应计数</summary>
    public int EmptyResponseCount { get; set; }

    /// <summary>异常协议计数（malformed tool-call）</summary>
    public int MalformedProtocolCount { get; set; }

    /// <summary>恢复尝试计数</summary>
    public int RecoveryCount { get; set; }

    /// <summary>瞬时传输失败连续计数</summary>
    public int TransportFailureCount { get; set; }

    /// <summary>
    /// “口头表示将执行 build/test 但未实际发起工具调用”的自动纠偏计数
    /// </summary>
    public int UnexecutedCommandIntentRecoveryCount { get; set; }

    /// <summary>
    /// “口头表示将继续读取/搜索/查看证据但未实际发起工具调用”的自动纠偏计数
    /// </summary>
    public int UnexecutedReadIntentRecoveryCount { get; set; }

    /// <summary>
    /// “口头表示将调用 planning 工具但未实际发起工具调用”的自动纠偏计数
    /// </summary>
    public int UnexecutedPlanningIntentRecoveryCount { get; set; }

    /// <summary>
    /// “口头表示将修改代码/文件但未实际发起写工具调用”的自动纠偏计数
    /// </summary>
    public int UnexecutedWriteIntentRecoveryCount { get; set; }

    /// <summary>
    /// wrap-up 轮仍然发出工具调用时，runtime 为保留总结轮而动态扩展的次数。
    /// </summary>
    public int WrapUpToolContinuationCount { get; set; }

    /// <summary>
    /// Stop hook 要求 runtime 不要结束而继续下一轮的次数。
    /// </summary>
    public int StopHookContinuationCount { get; set; }

    /// <summary>
    /// 可见正文虽然非空，但明显只是半句/导语而非最终答案时的自动纠偏计数。
    /// </summary>
    public int InsufficientVisibleAnswerRecoveryCount { get; set; }

    /// <summary>
    /// 当前 runtime 执行中已成功调用会话计划工具的次数。
    /// 用于避免在工具已实际执行后，后续总结文本被误判为“未执行 planning intent”。
    /// </summary>
    public int ExecutedPlanningToolCount { get; set; }

    /// <summary>累计 prompt tokens</summary>
    public int TotalPromptTokens { get; set; }

    /// <summary>累计 completion tokens</summary>
    public int TotalCompletionTokens { get; set; }

    /// <summary>终止原因（复用现有 QueryTerminationReason enum）</summary>
    public QueryTerminationReason TerminationReason { get; set; } = QueryTerminationReason.Normal;

    /// <summary>终止详情码（用于 UI / telemetry / 运营映射）</summary>
    public string? TerminalDetailCode { get; set; }

    /// <summary>最后一轮继续原因</summary>
    public string? LastContinueReason { get; set; }

    /// <summary>状态标志位</summary>
    public RuntimeState Flags { get; set; } = RuntimeState.None;

    /// <summary>累计上下文字符数</summary>
    public long TotalContextChars { get; set; }

    /// <summary>已执行工具签名缓存（用于去重）</summary>
    public HashSet<string> ExecutedToolSignatures { get; } = new(StringComparer.Ordinal);

    /// <summary>连续相同工具调用计数（stall detection）</summary>
    public int ConsecutiveSameToolCount { get; set; }

    /// <summary>连续“仅工具、无可见正文”轮次计数，用于更强的收尾提醒。</summary>
    public int ConsecutiveToolOnlyRounds { get; set; }

    /// <summary>连续“重复读取未变化证据”轮次计数，用于更强的综合提醒。</summary>
    public int ConsecutiveRepeatedReadRounds { get; set; }

    /// <summary>最后工具签名</summary>
    public string? LastToolSignature { get; set; }

    /// <summary>当前 query 的结构化工作现场证据账本。</summary>
    public QueryEvidenceLedger EvidenceLedger { get; } = new();

    /// <summary>恢复策略为下一轮留下的结构化提示，供 prompt 组装层显式投影。</summary>
    public List<RuntimeRecoveryHint> RecoveryHints { get; } = new();

    /// <summary>启动时间戳</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>启动 stopwatch</summary>
    public Stopwatch Stopwatch { get; init; } = Stopwatch.StartNew();

    /// <summary>当前是否处于 thinking 状态</summary>
    public bool IsThinking { get; set; }

    /// <summary>是否已发送 RoundStarted 事件</summary>
    public bool RoundStartedSent { get; set; }

    /// <summary>是否已发送 ThinkingStarted 事件</summary>
    public bool ThinkingStartedSent { get; set; }

    /// <summary>是否启用工具去重（由 AdapterHints 控制）</summary>
    public bool EnableToolDeduplication { get; set; }

    /// <summary>恢复逻辑可要求下一轮即使位于 wrap-up round 也允许继续调用工具。</summary>
    public bool ForceAllowToolCallsNextRound { get; set; }

    /// <summary>恢复逻辑可要求下一轮强制禁用工具，仅基于现有证据收尾。</summary>
    public bool ForceDisableToolCallsNextRound { get; set; }

    /// <summary>恢复逻辑可要求下一轮只暴露一个指定工具，降低模型在恢复轮继续叙述的空间。</summary>
    public string? RequiredToolNameForNextRound { get; set; }

    /// <summary>动态上下文当前强制的工具名，用于限制每次外部状态投影的连续强制次数。</summary>
    public string? DynamicRequiredToolName { get; set; }

    /// <summary>同一个动态 required tool 已连续强制的次数。</summary>
    public int DynamicRequiredToolAttempts { get; set; }

    /// <summary>恢复策略为下一轮附加的临时 ChatOptions 覆盖项。</summary>
    public IReadOnlyDictionary<string, object?>? NextRoundOptionOverrides { get; set; }

    /// <summary>下一轮请求前临时注入的工具批次摘要提醒，不写回持久消息列表。</summary>
    public string? PendingToolBatchSummaryPrompt { get; set; }

    /// <summary>最近一次生成的工具批次摘要，用于 recovery 重试时重放简要证据。</summary>
    public string? LastToolBatchSummaryPrompt { get; set; }

    /// <summary>最近一次模型请求前生成的 prompt/context 组装快照。</summary>
    public PromptAssemblySnapshot? LastPromptAssemblySnapshot { get; set; }

    /// <summary>当前 query loop 阶段，用于诊断和阶段化测试。</summary>
    public QueryRuntimeLoopPhase CurrentPhase { get; set; } = QueryRuntimeLoopPhase.PromptAssembly;

    /// <summary>工具执行协调器的轻量级同步锁，用于保护去重状态。</summary>
    public object ToolExecutionSync { get; } = new();
}

public sealed record RuntimeReadEvidence(
    string ToolName,
    string FilePath,
    string? SnapshotId,
    string? FileFingerprint,
    int? WindowStartLine,
    int? WindowEndLine,
    int? TotalLineCount);

/// <summary>
/// Runtime 状态标志位 — 用于记录特定行为是否已触发
/// </summary>
[Flags]
public enum RuntimeState
{
    None = 0,
    EmptyResponseRecoveryUsed = 1,
    ZeroToolCallRecoveryUsed = 2,
    ContextCompactionUsed = 4,
    AutoDispatchUsed = 8,
    StallDetected = 16,
    ContextHardLimitReached = 32,
    MalformedProtocolRecoveryUsed = 64,
    TransportFailureRecoveryUsed = 128,
    MaxRoundsWarningIssued = 256,
    ToolDeduplicationApplied = 512,
    StreamingToolExecutionUsed = 1024,
    UnexecutedWriteIntentRecoveryUsed = 2048,
    RequiredToolContractRecoveryUsed = 4096,
    WrapUpToolContinuationUsed = 8192,
    WrapUpToolCallsSuppressed = 16384
}
