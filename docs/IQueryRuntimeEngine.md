# IQueryRuntimeEngine 详细设计文档

> **命名空间**: `CodexFlow.Core.Runtime`  
> **当前实现状态**: Phase 0B/1/4A 已完成；Gateway 三条 LLM 路径已接入；Recovery 执行链路尚未接入；Context governance 为最小可用实现  
> **适用分支**: 当前主干代码  

---

## 1. 概述

`IQueryRuntimeEngine` 是 CodexFlow 平台的**统一 Query/Tool Loop 执行引擎**。它将分散在 4 个入口（`CodexController`、`GatewayMessageProcessor`、`DefaultCodexKernel`、`SimpleCodexController`）中的 query/tool loop 主执行语义收口为一个统一的、可复用的状态机。

### 1.1 为什么需要它

| 问题 | 现状 | 统一 Runtime 解决 |
|------|------|------------------|
| 多套 loop 并存 | 历史上 4 个入口各自实现 round loop、tool 执行与终止判断 | 统一执行语义 |
| 行为漂移 | 各入口对 max rounds、tool 调用、事件产出曾存在差异 | 统一的终止判断、事件模型与工具协调 |
| 修复无法横向覆盖 | 一个入口的 bug fix 无法自动惠及其它入口 | 一次修复，处处生效 |
| 职责混乱 | Controller/Gateway 既是入口适配层又承担 loop 实现职责 | 清晰的分层边界 |

### 1.2 设计原则

1. **以 loop 收口为中心** — 先统一执行语义，不急于推进 prompt builder 和 context compaction 大抽象
2. **兼容为前提** — 支持同内核、不同 event adapter 的多入口输出
3. **入口层与 runtime 解耦** — 入口层负责认证、request 解析、role prompt 组装、adapter 选择；runtime 负责 round loop、tool execution、termination、event 产出；recovery 与完整 context governance 通过扩展点预留

---

## 2. 核心接口

### 2.1 IQueryRuntimeEngine

```csharp
public interface IQueryRuntimeEngine
{
    /// <summary>
    /// 执行一次完整的 query turn（包含多轮 tool loop）
    /// </summary>
    /// <param name="request">执行请求，包含初始 messages、tools、maxRounds 等</param>
    /// <param name="eventSink">内部的事件消费端，用于 SSE adapter 或 telemetry 消费</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>终止结果，包含 termination reason、total rounds、tool call 统计等</returns>
    Task<QueryRuntimeResult> ExecuteAsync(
        QueryRuntimeRequest request,
        IQueryRuntimeEventSink eventSink,
        CancellationToken ct = default);
}
```

**方法语义**：`ExecuteAsync` 是 runtime 的唯一公开入口。它接受一个不可变的 `QueryRuntimeRequest` 和一个可插拔的 `IQueryRuntimeEventSink`，返回一个最终的 `QueryRuntimeResult`。整个执行过程是一个同步-流式循环：先流式消费 LLM 输出，再批量执行工具调用，循环往复直到终止条件触发。

### 2.2 依赖注入

```csharp
// Program.cs
builder.Services.AddScoped<IQueryRuntimeEngine, QueryRuntimeEngine>();
builder.Services.AddScoped<IToolExecutionCoordinator, DefaultToolExecutionCoordinator>();
builder.Services.AddScoped<IQueryRecoveryPolicy, DefaultQueryRecoveryPolicy>();
builder.Services.AddScoped<IContextWindowManager, DefaultContextWindowManager>();
```

所有依赖通过 DI 注入，`QueryRuntimeEngine` 的构造函数接受以下依赖：

| 依赖 | 类型 | 必须 | 职责 |
|------|------|------|------|
| `ILLMExecutor` | `ILLMExecutor` | ✅ | LLM 流式/非流式调用 |
| `IContextWindowManager` | `IContextWindowManager?` | ❌ | query turn 完成后的统一上下文回写与压缩挂点 |
| `IToolExecutionCoordinator` | `IToolExecutionCoordinator?` | ❌ | 工具去重、执行、结果归一 |
| `IQueryRecoveryPolicy` | `IQueryRecoveryPolicy?` | ❌ | 恢复策略扩展点，当前已注入但主执行路径尚未调用 |
| `IQueryLoopTelemetry` | `IQueryLoopTelemetry?` | ❌ | 遥测记录 |
| `ILogger<QueryRuntimeEngine>` | `ILogger` | ✅ | 日志记录 |

---

## 3. 数据模型

### 3.1 QueryRuntimeRequest

一次 query 执行的完整输入，由入口层构造、runtime 只读消费：

| 字段 | 类型 | 说明 |
|------|------|------|
| `SessionId` | `string` | 会话 ID，用于 telemetry 和 persistence |
| `EntryPoint` | `QueryLoopEntryPoint` | 入口点标识（`DefaultCodexKernel` / `CodexController` / `GatewayMessageProcessor` / `SimpleCodexController`） |
| `InitialMessages` | `IReadOnlyList<ChatMessage>` | 已组装好的初始消息（含 system prompt + history + user message） |
| `Options` | `ChatOptions?` | ChatOptions（temperature、tools、thinking 等） |
| `Scenario` | `MemoryInjectionScenario` | 记忆注入场景，默认为 `Chat` |
| `Session` | `CodexSession?` | 会话对象，用于 memory context assembler |
| `MaxRounds` | `int` | 最大轮次，默认 20 |
| `EnableTools` | `bool` | 是否启用工具调用 |
| `AllowStreaming` | `bool` | 是否允许流式输出 |
| `AvailableTools` | `IReadOnlyList<AIFunction>?` | 可用工具列表 |
| `AdapterHints` | `AdapterHints?` | 入口特化配置（tool dedupe、recovery 策略等） |
| `InterventionHook` | `IQueryRuntimeInterventionHook?` | Phase 4A.1 干预钩子（guardrail + critique） |
| `ConversationCapture` | `QueryRuntimeConversationCapture?` | 完成后回写到短期上下文窗口的消息 |
| `PromptMetadata` | `PromptMetadata?` | 审计/调试用元数据 |

### 3.2 QueryRuntimeState

Runtime 内部可变状态，由 engine 维护、入口层不直接访问：

| 字段 | 类型 | 说明 |
|------|------|------|
| `Messages` | `List<ChatMessage>` | 当前消息列表（每轮增长） |
| `Round` | `int` | 当前轮次 |
| `MaxRounds` | `int` | 最大轮次（来自 request） |
| `LastAssistantText` | `StringBuilder` | 本轮 LLM 输出的文本内容 |
| `LastThinkingText` | `StringBuilder` | 本轮思维链内容 |
| `LastToolCalls` | `List<FunctionCallContent>` | 本轮收集到的工具调用 |
| `TotalToolCalls` | `int` | 累计工具调用总数 |
| `ZeroToolCallRounds` | `int` | 无工具调用轮次计数 |
| `EmptyResponseCount` | `int` | 空响应计数 |
| `MalformedProtocolCount` | `int` | 异常协议计数（malformed tool-call） |
| `RecoveryCount` | `int` | 恢复尝试计数 |
| `TotalPromptTokens` | `int` | 累计 prompt tokens |
| `TotalCompletionTokens` | `int` | 累计 completion tokens |
| `TerminationReason` | `QueryTerminationReason` | 终止原因 |
| `LastContinueReason` | `string?` | 最后一轮继续原因 |
| `Flags` | `RuntimeState` | 状态标志位（位图） |
| `TotalContextChars` | `long` | 累计上下文字符数 |
| `ExecutedToolSignatures` | `HashSet<string>` | 已执行工具签名缓存（去重用） |
| `ConsecutiveSameToolCount` | `int` | 连续相同工具调用计数 |
| `LastToolSignature` | `string?` | 最后一次工具调用签名 |
| `StartedAt` | `DateTimeOffset` | 执行启动时间 |
| `Stopwatch` | `Stopwatch` | 执行计时器 |
| `IsThinking` | `bool` | 当前是否处于 thinking 状态 |
| `RoundStartedSent` | `bool` | 当前轮是否已发送 RoundStarted 事件 |
| `ThinkingStartedSent` | `bool` | 当前轮是否已发送 ThinkingStarted 事件 |
| `EnableToolDeduplication` | `bool` | 是否启用工具去重 |

### 3.3 QueryRuntimeResult

执行终止结果，返回给入口层：

| 字段 | 类型 | 说明 |
|------|------|------|
| `TerminationReason` | `QueryTerminationReason` | 终止原因 |
| `TotalRounds` | `int` | 实际执行轮次 |
| `TotalToolCalls` | `int` | 工具调用总数 |
| `ZeroToolCallRounds` | `int` | 无工具调用轮次计数 |
| `EmptyResponseCount` | `int` | 空响应计数 |
| `RecoveryCount` | `int` | 恢复尝试计数 |
| `MalformedProtocolCount` | `int` | 异常协议计数 |
| `FinalText` | `string` | 最终 assistant 文本 |
| `FinalThinking` | `string?` | 最终思维链内容 |
| `TotalPromptTokens` | `int?` | 累计 prompt tokens |
| `TotalCompletionTokens` | `int?` | 累计 completion tokens |
| `TotalDurationMs` | `long` | 总耗时（毫秒） |
| `Flags` | `RuntimeState` | 状态标志位 |
| `TerminalDetailCode` | `string?` | 终止详情码 |
| `LastFunctionCall` | `string?` | 最后一次函数调用 |
| `FinalMessages` | `IReadOnlyList<ChatMessage>?` | 最终消息列表 |
| `QueryId` | `Guid` | 用于 telemetry 关联 |

### 3.4 RuntimeState（位图标志）

```csharp
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
    ToolDeduplicationApplied = 512
}
```

---

## 4. 执行流程

### 4.1 主循环

```
ExecuteAsync(request, eventSink)
    │
    ├── InitializeState(request)          ← 初始化状态
    ├── Telemetry: RecordStart            ← 记录开始
    ├── Emit RoundStarted (round 0)       ← 发射第 0 轮开始事件
    │
    ├── Main Loop (while round < maxRounds)
    │   │
    │   ├── ExecuteRoundAsync()           ← 执行单轮
    │   │   │
    │   │   ├── Build LLM request
    │   │   ├── Stream LLM response       ← 流式消费 LLM 输出
    │   │   │   ├── Handle thinking chunks    → ThinkingStarted/ThinkingDelta
    │   │   │   ├── Handle text deltas       → AssistantDelta
    │   │   │   ├── Handle tool calls        → ToolCallRequested
    │   │   │   └── Handle usage stats       → 更新 token 统计
    │   │   │
    │   │   ├── Execute tool calls        ← 批量执行工具
    │   │   │   ├── Pre-execution guardrail  ← 干预钩子（可选）
    │   │   │   ├── Execute via coordinator
    │   │   │   ├── Post-execution critique   ← 批判钩子（可选）
    │   │   │   └── Append result to messages
    │   │   │
    │   │   ├── Emit RoundCompleted
    │   │   ├── Telemetry: RecordRound
    │   │   └── Determine termination
    │   │       ├── No tool calls → terminate
    │   │       └── Max rounds reached → terminate
    │   │
    │   ├── Check: should terminate? → break
    │   ├── Emit RoundStarted (next)
    │   └── round++
    │
    ├── OnTurnCompletedAsync            ← turn 完成后的上下文回写挂点
    ├── Telemetry: RecordTermination
    ├── Emit Terminated
    └── Return QueryRuntimeResult
```

### 4.2 终止条件

| 原因 | 当前状态 | 枚举值 |
|------|----------|--------|
| 正常结束 | 初始状态，等待被覆盖 | `Normal` |
| 无工具调用 | 当前主循环已实现 | `NoToolCalls` |
| 达到最大轮次 | 当前主循环已实现 | `MaxRoundsReached` |
| 上下文硬限制 | 预留枚举与 adapter hint，当前主循环未统一触发 | `ContextHardLimit` |
| 检测停滞 | 预留枚举与 adapter hint，当前主循环未统一触发 | `StallDetected` |
| 等待用户确认 | 干预钩子可返回该终态 | `AwaitingUserConfirmation` |
| 异常 | 当前主循环已实现 | `Exception` |
| 恢复耗尽 | 恢复框架预留，当前主循环未统一触发 | `RecoveryExhausted` |
| 空响应回退 | 恢复框架预留，当前主循环未统一触发 | `EmptyResponseFallback` |
| 自动分发 | adapter hint / 终态预留，当前主循环未统一触发 | `AutoDispatched` |

### 4.3 事件序列

每轮的事件发射遵循固定序列号模式（`seqBase = round * 1000`）：

| 事件 | Seq 偏移 | 说明 |
|------|----------|------|
| `RoundStartedEvent` | 0 | 轮次开始（含上下文字符数） |
| `ThinkingStartedEvent` | 10 | 思维链开始 |
| `ThinkingDeltaEvent` | 11 + length | 思维链增量 |
| `ThinkingEndedEvent` | 99 | 思维链结束 |
| `AssistantDeltaEvent` | 100 + length | 文本增量 |
| `ToolCallRequestedEvent` | 200 + count | 工具调用请求 |
| `ToolExecutionStartedEvent` | 300 | 工具执行开始 |
| `ToolExecutionCompletedEvent` | 400 | 工具执行完成 |
| `RoundCompletedEvent` | 500 | 轮次完成 |
| `TerminatedEvent` | `(round+1)*1000+999` | 整个 query 终止 |

---

## 5. 关键机制

### 5.1 工具执行协调器（IToolExecutionCoordinator）

负责工具调用的去重、执行和结果归一化：

```csharp
public interface IToolExecutionCoordinator
{
    ToolDedupResult? CheckDuplicate(
        FunctionCallContent toolCall,
        QueryRuntimeState state);

    Task<ToolExecutionResult> ExecuteAsync(
        FunctionCallContent toolCall,
        IReadOnlyList<AIFunction>? availableTools,
        QueryRuntimeState state,
        CancellationToken ct = default);

    IAsyncEnumerable<ToolExecutionResult> ExecuteBatchAsync(
        IReadOnlyList<FunctionCallContent> toolCalls,
        IReadOnlyList<AIFunction>? availableTools,
        QueryRuntimeState state,
        CancellationToken ct = default);

    string ComputeSignature(FunctionCallContent toolCall);
}
```

**去重机制**：
- 通过 `toolName + args` 生成签名
- 维护 `ExecutedToolSignatures` 缓存
- 当前默认实现对重复调用返回 `ShouldSkip`，结果使用占位文本而不是复用上次真实输出

### 5.2 恢复策略（IQueryRecoveryPolicy）

处理各种异常场景的恢复。

当前代码状态：`IQueryRecoveryPolicy` 已注册并注入 `QueryRuntimeEngine`，但主循环尚未调用该策略；下表描述的是设计目标，而不是当前已生效行为。

| 场景 | 策略 |
|------|------|
| 空响应 | 注入重试提示，要求 LLM 重新生成 |
| 无工具调用 | 根据入口策略决定是否重试或直接结束 |
| Malformed protocol | 检测错误的工具调用格式，注入修复提示 |
| 传输失败 | 网络/LLM 超时后的重试 |
| 停滞检测 | 连续相同工具调用超过阈值时干预 |

### 5.3 干预钩子（IQueryRuntimeInterventionHook）

Phase 4A.1 引入，主要用于 DefaultCodexKernel 的 guardrail + critique 闭环：

```csharp
public interface IQueryRuntimeInterventionHook
{
    Task<InterventionResult> OnToolCallRequestedAsync(
        string toolName,
        Dictionary<string, object?> arguments,
        CodexSession? session,
        CancellationToken ct);

    Task<CritiqueResult> OnToolExecutionCompletedAsync(
        string toolName,
        string result,
        bool success,
        CodexSession? session,
        CancellationToken ct);
}
```

**Guardrail（执行前）**：
- 检查工具调用是否合法
- 可注入自定义消息
- 可跳过工具执行

**Critique（执行后）**：
- 检查结果质量
- 可拒绝注入工具结果
- 可注入反馈消息替代原始结果

### 5.4 上下文窗口治理（IContextWindowManager）

当前为稳定运行时挂点：

```csharp
public interface IContextWindowManager
{
    Task OnTurnStartedAsync(
        QueryRuntimeRequest request,
        CancellationToken ct = default);

    Task OnTurnCompletedAsync(
        QueryRuntimeRequest request,
        QueryRuntimeResult result,
        CancellationToken ct = default);
}
```

当前默认实现 `DefaultContextWindowManager` 已负责两件事：

- 在 query turn 开始前，对 `ConversationCapture` 输入做 pre-flight 持久化。
- 在 query turn 完成后，把 runtime 最终 assistant 文本统一回写到 `CodexSessionManager`。

`CodexSessionManager` 会在每次写回后执行固定的最小治理顺序：

1. 轻量裁剪：当 `RecentTurns` 超过软上限时，把最老的溢出 turn 汇总进 `HistorySummary`
2. 单消息压缩：超长 turn 在进入热上下文窗口前做 deterministic compaction，但原始 turn 仍保留在 audit/history store
3. 全局压缩：在 runtime warn/hard limit 或自动压缩阈值触发时，把当前 `RecentTurns` 全量汇总进 `HistorySummary`

当前仍未完成的是更细粒度的 budget 策略（tool result budget、recent trajectory window 等）与全量 telemetry 产品化。

---

## 6. 入口层集成

### 6.1 各入口使用方式

| 入口 | 接入状态 | Event Adapter | 特点 |
|------|----------|---------------|------|
| `GatewayMessageProcessor` | ✅ Phase 3 完成 | `GatewayRuntimeEventAdapter` | UserChat / TaskCompleted / TaskFailed 三条路径已收口 |
| `SimpleCodexController` | ✅ Phase 1 完成 | `SimpleCodexSseEventAdapter` | 最小 loop，API 原型验证 |
| `CodexController` | ✅ Phase 2A 完成 | `CodexChatSseEventAdapter` | 旧 `/api/Codex/Chat` 路径 |
| `DefaultCodexKernel` | ✅ Phase 4A 完成 | `KernelRuntimeEventAdapter` | Guardrail + Critique 闭环 |

### 6.2 典型调用模式

```csharp
// Gateway 入口示例
var request = new QueryRuntimeRequest
{
    SessionId = sessionId,
    EntryPoint = QueryLoopEntryPoint.GatewayMessageProcessor,
    InitialMessages = assembledMessages,
    Options = new ChatOptions { Tools = availableTools, Temperature = 0.7 },
    MaxRounds = maxRounds,
    ConversationCapture = new QueryRuntimeConversationCapture(
        inputTurns, AssistantRole: "assistant")
};

var result = await _queryRuntimeEngine.ExecuteAsync(request, eventSink, ct);

// 根据终止原因决定后续行为
if (result.TerminationReason == QueryTerminationReason.NoToolCalls)
{
    // LLM 完成回复，发送最终结果
}
```

---

## 7. 边界定义

### 7.1 Runtime 负责

- Round loop（多轮迭代）
- Messages 增长管理
- Tool call 收集、执行、结果 append
- Continue / terminate 判断
- 事件发射与 telemetry 记录
- turn 完成后的上下文回写挂点调用

当前尚未统一落地：
- Recovery 执行
- 完整 context budget / trimming / trajectory 治理

### 7.2 入口层负责

- 身份认证
- Request DTO / session 解析
- Role prompt / system prompt 初始组装
- 选择使用哪个 adapter 输出事件
- runtime request 构造（包括 `ConversationCapture`）
- 非 runtime fallback 路径上的 persistence / audit / UI 专用副作用

### 7.3 Orchestrator 负责

- Stage 决策
- 任务规划与执行编排
- Checkpoint / background job 生命周期
- 业务级成功 / 失败 / retry 语义

---

## 8. 相关文档

- [统一 Query Runtime 升级蓝图](./archived-blueprints/query-runtime-upgrade.md)
- [内核语义差异 ADR](./adr/ADR-001-kernel-runtime-semantics.md)
- [上下文压缩修复实施蓝图](./上下文压缩修复实施蓝图.md)
