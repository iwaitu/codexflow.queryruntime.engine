using CodexFlow.Core.Models;
using CodexFlow.Core.Telemetry;
using CodexFlow.Core.Workers;
using CodexFlow.Core.Abstractions;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Phase 0B: Query Runtime 执行请求 — 入口层构造，runtime 只读消费
/// </summary>
public sealed record QueryRuntimeRequest
{
    /// <summary>会话 ID，用于 telemetry 和 persistence</summary>
    public required string SessionId { get; init; }

    /// <summary>入口点标识，复用现有 QueryLoopEntryPoint enum</summary>
    public required QueryLoopEntryPoint EntryPoint { get; init; }

    /// <summary>初始消息列表（已包含 system prompt / history / user message）</summary>
    public required IReadOnlyList<ChatMessage> InitialMessages { get; init; }

    /// <summary>ChatOptions（包含 temperature、tools、thinkingEnabled 等）</summary>
    public ChatOptions? Options { get; init; }

    /// <summary>Memory injection scenario，用于 ILLMExecutor 场景选择</summary>
    public MemoryInjectionScenario Scenario { get; init; } = MemoryInjectionScenario.Chat;

    /// <summary>CodexSession（可选，用于 memory context assembler）</summary>
    public CodexSession? Session { get; init; }

    /// <summary>最大轮次（不同入口可配置不同值）</summary>
    public int MaxRounds { get; init; } = 20;

    /// <summary>是否启用工具（某些场景如 Stage 1 空项目需要禁用）</summary>
    public bool EnableTools { get; init; } = true;

    /// <summary>是否允许流式输出</summary>
    public bool AllowStreaming { get; init; } = true;

    /// <summary>Prompt 元数据（用于 audit / debug）</summary>
    public PromptMetadata? PromptMetadata { get; init; }

    /// <summary>Adapter 提示（传递给 recovery policy 做入口特化决策）</summary>
    public AdapterHints? AdapterHints { get; init; }

    /// <summary>可用工具列表（由入口层提供）</summary>
    public IReadOnlyList<AIFunction>? AvailableTools { get; init; }

    /// <summary>
    /// 可用工具动态提供器。
    /// 当运行时存在 tool_search 激活 deferred 工具等场景时，
    /// 应优先使用该 provider 在每一轮重新获取当前工具集，而不是依赖启动时快照。
    /// </summary>
    public Func<IReadOnlyList<AIFunction>>? AvailableToolsProvider { get; init; }

    /// <summary>
    /// 当前请求对应的原始 Codex 工具列表。
    /// 用于 coordinator 在执行前统一执行 validateInput / checkPermissions。
    /// </summary>
    public IReadOnlyList<ICodexTool>? AvailableCodexTools { get; init; }

    /// <summary>
    /// 原始 Codex 工具动态提供器。
    /// 与 AvailableToolsProvider 保持一致，用于 deferred tool 激活后的每轮重建。
    /// </summary>
    public Func<IReadOnlyList<ICodexTool>>? AvailableCodexToolsProvider { get; init; }

    /// <summary>
    /// 每轮动态上下文提供器。
    /// 入口层可在每个模型轮次前重新读取最新外部状态，注入短提示，并可要求本轮只调用指定工具。
    /// </summary>
    public Func<QueryRuntimeDynamicContextRequest, CancellationToken, ValueTask<QueryRuntimeDynamicContextResult?>>? DynamicContextProvider { get; init; }

    /// <summary>
    /// 当前 query turn 需要由 runtime 负责持久化到短期上下文窗口的输入消息。
    /// 入口层只提供 capture 数据；pre-flight 持久化与 completion 回写均由 runtime 统一触发。
    /// </summary>
    public QueryRuntimeConversationCapture? ConversationCapture { get; init; }

    /// <summary>
    /// Phase 4A.1: 干预 hook（用于 Kernel 的 guardrail + critique）
    /// 允许入口层在工具执行前后干预 runtime 行为
    /// </summary>
    public IQueryRuntimeInterventionHook? InterventionHook { get; init; }

    /// <summary>
    /// 当前请求对应的 worker 定义上下文。
    /// 使 runtime 可以明确感知 worker 类型、允许工具面、隔离模式与输出契约。
    /// </summary>
    public WorkerRuntimeContext? WorkerContext { get; init; }

    /// <summary>
    /// 当前请求在停止前必须满足的工具执行契约。
    /// 未满足时，runtime stop hook 会阻止无工具候选结束并触发恢复轮。
    /// </summary>
    public RequiredToolExecutionContract? RequiredToolContract { get; init; }
}

/// <summary>
/// Prompt 元数据 — 用于 audit / debug / telemetry enrichment
/// </summary>
public sealed record PromptMetadata(
    string? RolePrompt,
    string? WorkspacePath,
    int? PlanSize,
    int? InitialStage);

/// <summary>
/// 当前 query turn 需要持久化的输入消息快照。
/// assistant 最终回复由 runtime 在执行完成后补上。
/// </summary>
public sealed record QueryRuntimeConversationCapture(
    IReadOnlyList<QueryRuntimeConversationMessage> InputTurns,
    string AssistantRole = "assistant");

/// <summary>
/// 会话窗口回写消息
/// </summary>
public sealed record QueryRuntimeConversationMessage(
    string Role,
    string Content);

public sealed record QueryRuntimeDynamicContextRequest(
    QueryRuntimeRequest RuntimeRequest,
    QueryRuntimeState State,
    int Round,
    bool ToolCallsAllowed,
    string? RequiredToolNameForRound);

public sealed record QueryRuntimeDynamicContextResult
{
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];

    public string? RequiredToolName { get; init; }

    public bool ForceAllowToolCalls { get; init; }

    public IReadOnlyList<RuntimeRecoveryHint> RecoveryHints { get; init; } = [];
}

/// <summary>
/// Adapter 提示 — 传递入口特化配置给 runtime 和 recovery policy
/// </summary>
public sealed record AdapterHints(
    bool EnableAutoDispatch = false,
    bool EnableStallDetection = false,
    bool EnableContextLimitCheck = false,
    long? ContextHardLimit = null,
    long? ContextWarnLimit = null,
    bool EnableEmptyResponseRecovery = false,
    bool EnableMalformedProtocolRecovery = false,
    bool EnableTransportFailureRecovery = false,
    bool EnableToolDeduplication = false,
    int MaxRecoveryAttempts = 3,
    int MaxConsecutiveSameTool = 3,
    HashSet<string>? AlwaysOnTools = null);
