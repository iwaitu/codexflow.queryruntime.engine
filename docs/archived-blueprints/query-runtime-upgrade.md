# CodexFlow Unified Query Runtime 升级蓝图（贴近当前代码版）

> 版本：0.4
> 日期：2026-04-10
> 状态：**Phase 0A-4A 已完成，Gateway 全 LLM 路径已收口到 runtime，进入稳定期观察**
> 适用分支：当前主干代码
> 文档目标：基于当前仓库真实实现，重新评估统一 Query Runtime 的必要性、边界、实施顺序与验收标准

---

## 📊 当前进度概览

```
Phase 0A  ✅ 完成 - 现状核对与基线补齐
Phase 0B  ✅ 完成 - 设计落点与接口冻结
Phase 1   ✅ 完成 - SimpleCodexController 接入
Phase 2A  ✅ 完成 - CodexController 接入
Phase 3   ✅ 完成 - GatewayMessageProcessor 接入（含 UserChat / TaskCompleted / TaskFailed 三条 LLM 路径）
Phase 4A  ✅ 完成 - DefaultCodexKernel runtime 路径 + guardrail/critique 闭环
稳定期    🔵 进行中 - 建议观察 1-2 周（或至少 50+ 真实任务）
Phase 5   🟡 预埋完成 - 最小 context governance 挂点已落地，预算治理未开始
Phase 6   ⏸️ 暂缓 - Prompt/Context 协同增强
```

**关键交付物**:
- 统一 Runtime 核心：`IQueryRuntimeEngine` + 实现
- 4 个入口全部接入 runtime，具备环境变量 fallback
- Gateway 的 3 条 LLM 主路径已全部收口到 runtime
- Guardrail + Critique 闭环机制
- `IContextWindowManager` 最小挂点已落地，并已接入自动上下文压缩
- 关键回归测试已覆盖 runtime 主路径与上下文压缩读写路径
- 行为差异文档化：[ADR-001](../adr/ADR-001-kernel-runtime-semantics.md)

**下一步核心工作**:
1. 完成稳定期观察，收集 `Gateway` / `CodexController` / `Kernel` 的 runtime 与 legacy transcript 对照
2. 基于 telemetry 和真实任务样本，确认 termination / recovery 分布是否稳定
3. 启动 Phase 5-A：`tool result budget` 与超长工具输出裁剪
4. 补齐 runtime + context compression 的更高层 E2E / 真实流量回归

---

## 1. 文档结论

这项升级仍然值得做，但不应按旧蓝图原样推进。

当前项目的问题并不是“是否要统一 Query Runtime”，而是：

- 统一范围需要收缩
- 试点入口需要调整
- 前置基线状态需要重新定义
- 实施计划必须显式兼容现有 `Gateway`、旧 `/api/Codex/Chat`、`DefaultCodexKernel`、`CodexLocalClient`

本蓝图的核心判断如下：

1. 统一 query/tool loop 的方向仍然正确
2. 旧蓝图对“现有前置条件已满足”的判断过于乐观
3. 旧蓝图对 `SimpleCodexController` 的优先级判断已经落后于当前主路径
4. 本轮升级应先聚焦“统一 loop 内核”，不要第一阶段就同时推进“大规模 prompt builder + context compaction 大抽象”

一句话总结：

`Query Runtime` 仍然值得做，但必须改成“以 loop 收口为中心、以兼容为前提、以 Gateway / CodexController 为主战场”的精简升级方案。

---

## 2. 为什么要重写旧蓝图

2026-04-02 版本的蓝图给出了正确的问题意识，但与当前项目现状相比，已经出现以下偏移：

### 2.1 正确但仍然成立的部分

- 多套 loop 并存，行为存在漂移
- recovery 策略分散在不同入口
- context 注入能力强，但单次 query 的窗口治理弱
- SSE / streaming / tool loop / message append 的边界不够清晰

### 2.2 已经过时或不够准确的部分

- 旧蓝图假设 `SimpleCodexController` 是最适合的首个试点
- 旧蓝图默认前端仍缺乏统一 mapper，而现在 `useGateway` 已经承担了大量归一化职责
- 旧蓝图把 “Phase 0 已完成，可进入 Phase 1” 作为事实，但仓库内未见稳定 baseline 报表产物
- 旧蓝图没有把新增的 `ILLMExecutor` / `IMemoryContextAssembler` / `CodexLocalClient` 纳入中心路径

### 2.3 本文档的目标

本版蓝图要解决的不是“抽象得更漂亮”，而是：

- 准确映射当前代码
- 明确哪些问题仍然存在
- 明确哪些旧判断需要修正
- 给出一套可执行、低回归风险、可阶段验收的实施计划

---

## 3. 当前项目现状映射

本节只描述仓库里已经存在的结构，不做理想化推演。

### 3.1 当前与 query/tool loop 直接相关的入口

当前至少有 4 个主要 loop 入口：

1. `IvilsonCodex.Core/Agents/DefaultCodexKernel.cs`
2. `IvilsonCodex/Controllers/CodexController.cs`
3. `IvilsonCodex/Gateway/GatewayMessageProcessor.cs`
4. `IvilsonCodex/Controllers/SimpleCodexController.cs`

对应代码位置：

- [DefaultCodexKernel.cs](/Users/iwaitu/github/ivilsoncodex/IvilsonCodex.Core/Agents/DefaultCodexKernel.cs#L207)
- [CodexController.cs](/Users/iwaitu/github/ivilsoncodex/IvilsonCodex/Controllers/CodexController.cs#L507)
- [GatewayMessageProcessor.cs](/Users/iwaitu/github/ivilsoncodex/IvilsonCodex/Gateway/GatewayMessageProcessor.cs#L1196)
- [SimpleCodexController.cs](/Users/iwaitu/github/ivilsoncodex/IvilsonCodex/Controllers/SimpleCodexController.cs#L79)

### 3.2 当前已经存在的共享层

#### A. 统一的 LLM transport facade

项目已经存在 `ILLMExecutor`：

- [IMemoryInterfaces.cs](/Users/iwaitu/github/ivilsoncodex/IvilsonCodex.Core/Abstractions/IMemoryInterfaces.cs#L194)
- [DefaultLLMExecutor.cs](/Users/iwaitu/github/ivilsoncodex/IvilsonCodex.Core/Services/DefaultLLMExecutor.cs#L9)

它已经承担的职责：

- 包装 `IChatClient`
- 在请求发出前注入 scenario-aware memory context
- 统一 streaming / non-streaming 的 dispatch 入口

它尚未承担的职责：

- tool loop 状态机
- recovery policy
- tool execution coordination
- runtime event model
- context trimming / compaction

#### B. 统一的 memory context assembler

项目已经存在 `IMemoryContextAssembler` 及默认实现，用于按场景装配 memory sections。

这意味着本轮升级不应重复建设“记忆注入”抽象，而应在此之上补齐：

- query window governance
- loop state
- recovery model

#### C. 统一的 query telemetry

项目已经存在 `QueryLoopTelemetry`：

- [QueryLoopTelemetryEvent.cs](/Users/iwaitu/github/ivilsoncodex/IvilsonCodex.Core/Telemetry/QueryLoopTelemetryEvent.cs#L1)
- [QueryLoopTelemetryService.cs](/Users/iwaitu/github/ivilsoncodex/IvilsonCodex/Telemetry/QueryLoopTelemetryService.cs#L9)

当前已统一记录：

- start
- round completed
- termination
- recovery

当前 termination reason 已有枚举：

- `Normal`
- `NoToolCalls`
- `MaxRoundsReached`
- `ContextHardLimit`
- `StallDetected`
- `AwaitingUserConfirmation`
- `Exception`
- `RecoveryExhausted`
- `EmptyResponseFallback`
- `AutoDispatched`

这说明：

- “观测接口”已经基本建立
- 但“runtime 统一内核”尚未落地

### 3.3 当前 4 个入口的职责差异

#### A. `DefaultCodexKernel`

当前是最复杂、最接近“内核”的 loop。

已知职责：

- role prompt 组装
- 工具可见性控制
- critique loop
- malformed tool protocol recovery
- legacy text tool-call 兼容
- tool dedupe / repeated call cache
- telemetry

它已经不是一个单纯的“工具循环”，而是：

- runtime loop
- role policy
- critique / guardrail policy
- tool access policy

全部耦合在一个类里。

#### B. `CodexController`

当前旧 `/api/Codex/Chat` 仍然是一条重要路径。

它承担的职责非常多：

- 构造聊天上下文
- 动态 `maxRounds`
- context hard limit 检查
- tool dedupe
- high-cost debounce
- empty response fallback
- stream SSE
- scheduler / audit / persistence 相关事件拼装

`CodexController` 的问题不是功能少，而是 runtime 细节泄漏过多。

#### C. `GatewayMessageProcessor`

当前 Gateway 是 WebUI 主链路，具有更强的会话级事件语义。

当前 `GatewayMessageProcessor` 的主 LLM 路径已经通过 `StreamWithRuntimeAsync()` 收口到 `IQueryRuntimeEngine`，
包括：

- `UserChat`
- `TaskCompleted`
- `TaskFailed`（达到重试上限后的用户交互分支）

Gateway 仍保留的职责主要是：

- 会话解析与 conversation 绑定
- 历史摘要注入与压缩水位读取
- SSE 事件适配
- 业务事件编排（task status / retry notice / title generation）

#### D. `SimpleCodexController`

它依然是最小 loop，但已经不是“最重要的线上路径”。

它更适合：

- 低风险 API 原型验证
- 单元级 runtime API 验证

不适合作为唯一试点来代表整个系统收益。

### 3.4 当前前端状态

旧蓝图假设前端事件映射仍较分散，但现在 `useGateway` 已经是集中式 hook：

- [useGateway.ts](/Users/iwaitu/github/ivilsoncodex/IvilsonCodex.WebUI/src/lib/useGateway.ts#L79)

它已经承担：

- SSE 读取和重连
- `content` / `thinking_*` / `tool_call` / `task_status` / `done` 等事件处理
- assistant placeholder 管理
- conversationId / history sync

这意味着：

- 前端不是“完全未收口”
- 后端 runtime event 升级仍然重要，但不应高估前端重构收益

### 3.5 当前新增的 headless 客户端路径

当前项目已经新增 `CodexLocalClient`：

- [HeadlessCodexWorkflowRunner.cs](/Users/iwaitu/github/ivilsoncodex/CodexLocalClient/Workflow/HeadlessCodexWorkflowRunner.cs#L17)
- [CodexSseChatClient.cs](/Users/iwaitu/github/ivilsoncodex/CodexLocalClient/Api/CodexSseChatClient.cs#L15)

该路径的重要性在于：

- 它是自动化验证和集成测试的重要消费者
- 它目前仍依赖旧 `/api/Codex/Chat` SSE 协议
- 它明确依赖一些历史行为，例如 `[DONE]` 之后仍可能补发 `setconversationid`

这直接影响升级顺序：

- 不能只盯着 Gateway
- 必须显式考虑旧 chat 路径兼容

---

## 4. 当前真实问题清单

以下问题是本轮升级必须解决的，不解决则统一 runtime 的价值不足。

### 4.1 多套 loop 仍然并存，且修复点分散

当前至少存在 4 套 loop。

它们分别处理：

- rounds
- zero-tool-call
- empty response
- malformed protocol
- tool dedupe
- message append
- SSE emission
- termination

问题不在“有重复代码”本身，而在：

- 行为不一致
- 新修复难以横向覆盖
- telemetry 已统一，但行为仍不统一

### 4.2 runtime 责任和业务入口责任混杂

当前 `CodexController` 与 `GatewayMessageProcessor` 中有大量本该属于 runtime 的职责，例如：

- 最大轮次计算和停止
- 重试提示注入
- tool 去重
- 空响应恢复
- tool result 何时追加
- 何时再发起下一轮

结果是 Controller / Gateway 既是入口适配层，又是 runtime 实现层。

### 4.3 context 注入已收口，但 context 治理仍然缺位

当前系统更像：

- memory-rich
- runtime-thin

已经会“注入什么”：

- Project Summary
- Facts
- Semantic Recall
- scenario-aware memory sections

但还不会系统地回答：

- tool result 太大时如何压缩
- 哪些历史轮次必须高保真
- 哪些消息可以摘要
- recovery 时是否应切换到更窄窗口

### 4.4 旧 `/api/Codex/Chat` 和 Gateway 双栈并存

当前系统并未完全切到 Gateway。

实际存在两条用户面向通路：

1. `api/gateway/*`
2. `api/Codex/Chat`

并且 `CodexLocalClient` 还依赖第二条。

因此统一 runtime 必须支持：

- 同内核，不同 event adapter
- 同 loop，不同 wire contract

### 4.5 telemetry 已有，但 baseline 完成度不足

当前代码里：

- telemetry 接口存在
- 统计脚本存在

但仓库中未见稳定 baseline 产物和 trimming 相关观测结果。

这意味着旧蓝图里 “Phase 0 已完成，可以直接推进” 的结论需要修正为：

- 代码侧观测能力已具备
- 数据侧 baseline 仍需补齐

---

## 5. 本轮升级范围重新定义

为了避免目标失控，本蓝图明确区分：

- 本轮必须做
- 本轮可以做
- 本轮不做

### 5.1 本轮必须做

1. 统一 loop state 模型
2. 统一 continue / termination reason
3. 统一 recovery hook 模型
4. 统一 tool execution + result append 语义
5. 统一 runtime event 内部模型
6. 为 Gateway 和旧 `/api/Codex/Chat` 分别提供 adapter

### 5.2 本轮可以做

1. tool result budget
2. 小范围 recent trajectory window
3. 轻量 prompt section builder
4. Gateway SSE event adapter

### 5.3 本轮不做

1. 不重写 `CodexOrchestrator`
2. 不重写 `ProjectMemoryService`
3. 不改 Semantic Recall 存储结构
4. 不在第一阶段实现完整 Claude Code 风格 compaction 框架
5. 不在第一阶段替换所有前端事件协议

---

## 6. 设计目标

本轮升级的目标不是“再造一层宏大抽象”，而是让下列行为收口：

1. 相同类型的 query/tool loop 在不同入口有一致的运行语义
2. recovery 不再散落在各入口里单独维护
3. tool result append / next round decision 可复用
4. SSE 输出与内部 runtime event 解耦
5. 后续 context governance 有稳定挂点

---

## 7. 目标架构

### 7.1 目标组件

建议新增以下组件：

| 组件 | 职责 | 第一阶段是否必须 |
|---|---|---|
| `IQueryRuntimeEngine` | 统一执行一个 query turn 的内部 loop | 是 |
| `QueryRuntimeEngine` | 默认实现 | 是 |
| `QueryRuntimeRequest` | 一次 query 执行的输入 | 是 |
| `QueryRuntimeState` | query 内部可变状态 | 是 |
| `QueryRuntimeResult` | 终止结果 | 是 |
| `QueryRuntimeEvent` | 统一内部事件模型 | 是 |
| `IToolExecutionCoordinator` | tool call 去重、执行、结果归并 | 是 |
| `IQueryRecoveryPolicy` | empty response / malformed protocol / zero-tool-call 等恢复 | 是 |
| `IQueryRuntimeEventSink` | runtime 内部事件消费端 | 是 |
| `IGatewayRuntimeEventAdapter` | `QueryRuntimeEvent -> GatewaySseEvent` | 第二阶段必须 |
| `ICodexChatEventAdapter` | `QueryRuntimeEvent -> 旧 Chat SSE payload` | 第二阶段必须 |
| `IContextWindowManager` | message window projection / budget | 已有最小实现，完整 budget 策略待 Phase 5 |
| `IPromptAssemblyService` | prompt sections 结构化组装 | 第三阶段 |

### 7.2 目标调用关系

```text
Controller / Gateway / Kernel
        |
        v
IQueryRuntimeEngine.ExecuteAsync(request, sink)
        |
        +-- ILLMExecutor
        +-- IToolExecutionCoordinator
        +-- IQueryRecoveryPolicy
        +-- optional IContextWindowManager
        |
        v
QueryRuntimeResult
```

### 7.3 边界定义

#### Runtime 负责

- round loop
- messages 增长
- tool call 收集
- tool 执行
- tool result append
- continue / terminate 判断
- recovery 执行
- 产出统一 runtime event

#### 入口层负责

- 身份认证
- request DTO / session 解析
- role prompt / system prompt 初始组装
- 选择使用哪个 adapter 输出事件
- persistence / audit / UI 专用副作用

#### Orchestrator 负责

- stage 决策
- 任务规划与执行编排
- checkpoint / background job 生命周期
- 业务级成功 / 失败 / retry 语义

---

## 8. 统一状态模型

### 8.1 新的统一终止原因

当前已有 `QueryTerminationReason`，本蓝图建议直接复用并扩展，而不是重新发明一套不兼容枚举。

建议保留并使用：

- `Normal`
- `NoToolCalls`
- `MaxRoundsReached`
- `ContextHardLimit`
- `StallDetected`
- `AwaitingUserConfirmation`
- `Exception`
- `RecoveryExhausted`
- `EmptyResponseFallback`
- `AutoDispatched`

若后续需要细化，不新增第二套 `TerminalReason`，而是：

- 在 `QueryRuntimeResult` 中补充 `TerminalDetailCode`
- 或在 `QueryRuntimeEvent.Terminated` 中补充 `detail`

### 8.2 建议新增 continue reason

当前 telemetry 的 round 事件已有 `ContinueReason` 字段，但不同入口使用不统一。

建议标准化为字符串常量或枚举：

- `next_tool_round`
- `empty_response_recovery`
- `malformed_protocol_recovery`
- `zero_tool_call_recovery`
- `tool_result_appended`
- `autodispatch_continuation`
- `context_compacted_retry`

### 8.3 `QueryRuntimeState` 最小字段集

第一阶段建议至少包含：

- `Messages`
- `Round`
- `MaxRounds`
- `LastAssistantText`
- `LastThinkingText`
- `LastToolCalls`
- `TotalToolCalls`
- `ZeroToolCallRounds`
- `EmptyResponseCount`
- `MalformedProtocolCount`
- `RecoveryCount`
- `PromptTokens`
- `CompletionTokens`
- `TerminationReason`
- `LastContinueReason`
- `Flags`

其中 `Flags` 至少包括：

- `EmptyResponseRecoveryUsed`
- `ZeroToolCallRecoveryUsed`
- `ContextCompactionUsed`
- `AutoDispatchUsed`

### 8.4 `QueryRuntimeRequest` 建议字段

建议最小化输入面：

- `SessionId`
- `EntryPoint`
- `InitialMessages`
- `ChatOptions`
- `Scenario`
- `Session`
- `MaxRounds`
- `EnableTools`
- `AllowStreaming`
- `PromptMetadata`
- `AdapterHints`

---

## 9. 统一事件模型

### 9.1 为什么需要内部事件模型

当前每个入口几乎都在边 streaming 边手写 UI / SSE payload。

这导致：

- runtime 无法独立测试
- event 顺序很难统一
- 同一类行为在 Gateway 和旧 Chat 下重复实现

因此必须引入内部事件模型，但第一阶段不要求前端直接消费它。

### 9.2 建议的 `QueryRuntimeEvent`

建议事件类型至少包括：

- `RoundStarted`
- `ThinkingStarted`
- `ThinkingDelta`
- `ThinkingEnded`
- `AssistantDelta`
- `ToolCallRequested`
- `ToolExecutionStarted`
- `ToolExecutionCompleted`
- `RecoveryTriggered`
- `SystemNotice`
- `RoundCompleted`
- `Terminated`

### 9.3 事件投影原则

内部事件与外部协议分离：

- Gateway 继续输出 `GatewaySseEvent`
- 旧 `/api/Codex/Chat` 继续输出原有 `data: { type, ... }`
- 未来 headless / tests / logs 可以直接消费 `QueryRuntimeEvent`

---

## 10. 恢复策略统一化

### 10.1 当前已经存在的恢复类型

当前代码里已出现的恢复类型包括：

- transport failure retry
- malformed protocol retry
- empty response retry
- duplicate tool call suppression
- stall detection
- context hard limit stop
- auto-dispatch continuation

但它们散落在：

- `DefaultCodexKernel`
- `CodexController`
- `GatewayMessageProcessor`

### 10.2 目标：恢复逻辑集中到 policy

建议新增：

- `IQueryRecoveryPolicy`
- `DefaultQueryRecoveryPolicy`

该 policy 不负责直接调用 HTTP / SSE，只负责：

- 识别恢复场景
- 决定是否继续
- 生成恢复动作
- 生成恢复提示消息

### 10.3 恢复动作建模

建议统一返回：

- `Continue`
- `Terminate`
- `InjectMessageAndRetry`
- `RetryWithReducedOptions`
- `RetryWithCompactedContext`
- `SkipRepeatedToolExecution`

---

## 11. 工具执行协调器

### 11.1 为什么需要单独抽象

当前工具执行并不只是“调用 tool 然后拿结果”。

真实职责包括：

- 去重
- 执行顺序
- MCP / 本地工具统一结果形状
- 失败归一化
- tool_result 追加到 messages
- 事件发射

### 11.2 第一阶段必须支持的能力

1. 保持原有执行顺序
2. 支持 call-by-call 执行
3. 支持去重签名
4. 支持重复调用抑制
5. 统一结果对象
6. 统一成功 / 失败事件

### 11.3 第一阶段不要做的能力

1. 不做复杂并发执行
2. 不做跨工具批处理优化
3. 不做 speculative tool execution

---

## 12. 上下文治理策略

### 12.1 对现状的准确判断

当前项目已经有 memory 注入，但没有系统化 window governance。

因此 context 治理必须建立，但要延后到 loop 收口之后。

### 12.2 第一阶段只做最小治理

建议第一阶段只做：

- 大 tool result 的预算裁剪
- 裁剪提示显式化
- 最近一轮 tool_use / tool_result 保持高保真

### 12.3 第二阶段再做的治理

- recent trajectory window
- compacted history summary
- recovery-time context narrowing

### 12.4 第三阶段以后再考虑

- 语义压缩
- 自适应 budget policy
- compaction 与 prompt builder 协同

---

## 13. Prompt 组装策略

### 13.1 当前项目真实状态

目前 prompt 组装已经分布在多个地方：

- role prompt
- system prompt
- memory block prepend
- Gateway 自定义系统消息
- Controller 注入的 fallback / warning / retry prompt

### 13.2 本轮策略

第一阶段不做“大一统 prompt service”。

只做两件事：

1. 定义 prompt section 的边界
2. 让 recovery / runtime notice 以统一方式注入

### 13.3 第三阶段再考虑的抽象

- `IPromptAssemblyService`
- `PromptAssemblyContext`
- `RecoveryPromptFactory`

---

## 14. 新旧蓝图偏离分析

### 14.1 仍然有效的部分

- 问题定义基本正确
- 统一 runtime 的方向正确
- telemetry 先行的思路正确
- Gateway / Kernel / Controller 需要收口的判断正确

### 14.2 需要修正的部分

#### A. `SimpleCodexController` 不再适合作为唯一试点

原因：

- 它最简单，但不是主路径
- 不能代表 Gateway 的事件复杂度
- 不能覆盖旧 `/api/Codex/Chat` 兼容压力

#### B. Gateway 前端收益要重新估计

现在 `useGateway` 已经承担主要 reducer/mapping 责任，因此：

- 后端 event adapter 仍然有价值
- 前端本身不需要被当作第一阶段重构重点

#### C. “Phase 0 已完成” 需要拆成两层

- 代码侧：基本完成
- 数据侧：未完全闭环

### 14.3 新的优先级建议

建议优先级改为：

1. 建 runtime 内核最小壳
2. 接入 `SimpleCodexController` 做 API 验证
3. 立即接入 `GatewayMessageProcessor` 或 `CodexController` 中一个主路径
4. 最后再收口 `DefaultCodexKernel`

如果看重用户主路径：

- `SimpleCodexController -> Gateway -> CodexController -> Kernel`

如果看重 headless / 自动化主路径：

- `SimpleCodexController -> CodexController -> Gateway -> Kernel`

本项目当前建议使用第二种。

---

## 15. 实施原则

### 15.1 小步替换，不做大爆炸

每次只替换一个入口。

### 15.2 兼容优先

第一阶段绝不允许：

- 直接改变 Gateway 对外 SSE contract
- 破坏 `/api/Codex/Chat` 被 `CodexLocalClient` 消费的行为

### 15.3 先统一 loop，再统一 context

如果 loop 还没收口，先做 compaction 只会把问题变得更难观测。

### 15.4 优先抽取稳定共性，不抽取特例

例如：

- `round loop` 是共性
- `critique loop` 是 Kernel 特性

前者应抽，后者先保留在 Kernel。

---

## 16. 分阶段实施计划

本节是本蓝图最重要的部分。

### Phase 0A：现状核对与基线补齐

> **状态**: ✅ **已完成**
>
> **说明**: 代码能力已具备，虽然未产出正式 baseline 报表，但通过代码审查确认了 4 个入口的实际情况，选择了先接 `CodexController` 再接 `Gateway` 的路径。

状态：

- 代码能力已具备
- 数据产物未闭环

目标：

- 形成真实 baseline
- 核对 4 个入口各自的实际流量与使用价值
- 明确第一主路径是 `Gateway` 还是 `CodexController`

具体任务：

1. 使用 [queryloop-baseline-report.sh](/Users/iwaitu/github/ivilsoncodex/scripts/queryloop-baseline-report.sh) 对现有日志出报表
2. 为 4 个 entry point 统计：
   - sample count
   - termination reason
   - zero-tool-call rate
   - malformed-protocol count
   - recovery count
   - avg rounds
   - avg duration
3. 单独统计：
   - Gateway 主路径样本
   - 旧 `/api/Codex/Chat` 样本
   - headless 自动化样本
4. 确认当前生产 / 测试环境哪条链路使用量更高

交付物：

- `artifacts/queryloop-baseline/` 下的 baseline 报表
- 一份简短 ADR：确认首个主战场入口

完成判据：

- 有至少一份可复现 baseline 报表
- 明确选定 Phase 2 的主路径

### Phase 0B：设计落点与接口冻结

> **状态**: ✅ **已完成**
>
> **交付物**:
> - `IvilsonCodex.Core/Runtime/` 目录已创建
> - 核心接口全部定义完成：`IQueryRuntimeEngine`, `QueryRuntimeRequest`, `QueryRuntimeState`, `QueryRuntimeResult`, `QueryRuntimeEvent`, `IToolExecutionCoordinator`, `IQueryRecoveryPolicy`, `IQueryRuntimeEventSink`
> - 单元测试：`RuntimeModelTests.cs` 覆盖模型序列化和状态流转

目标：

- 在不改行为的前提下冻结 runtime 最小接口

具体任务：

1. 新增 runtime 目录与接口
2. 定义：
   - `IQueryRuntimeEngine`
   - `QueryRuntimeRequest`
   - `QueryRuntimeState`
   - `QueryRuntimeResult`
   - `QueryRuntimeEvent`
3. 定义：
   - `IToolExecutionCoordinator`
   - `IQueryRecoveryPolicy`
   - `IQueryRuntimeEventSink`
4. 定义 continue / termination reason 复用规范

交付物：

- 仅接口和模型
- 无行为切换
- 单元测试覆盖模型序列化和最小状态流转

完成判据：

- 核心接口评审通过
- 不改变现有行为

### Phase 1：最小内核运行在 `SimpleCodexController`

> **状态**: ✅ **已完成**
>
> **交付物**:
> - `QueryRuntimeEngine.cs` — 默认实现
> - `DefaultToolExecutionCoordinator.cs` — 工具执行协调器
> - `DefaultQueryRecoveryPolicy.cs` — 恢复策略
> - `SimpleCodexSseEventAdapter.cs` — SSE 事件适配器
> - `SimpleCodexController.cs` 已改造为 runtime 驱动

目标：

- 用最简单入口验证 runtime API 是否够用

改动范围：

- `IvilsonCodex.Core/Runtime/*`
- `IvilsonCodex/Controllers/SimpleCodexController.cs`

具体任务：

1. `SimpleCodexController` 不再手写 round loop
2. 改为：
   - 构造 `QueryRuntimeRequest`
   - 调用 `IQueryRuntimeEngine`
   - 用轻量 adapter 把 runtime event 映射为当前 SSE payload
3. 保持现有外部 SSE 字段不变
4. 保持 telemetry 输出不变或更完整

交付物：

- `QueryRuntimeEngine` 第一版
- `SimpleCodexSseEventAdapter`

完成判据：

- `SimpleCodexController` 行为保持兼容
- 现有相关测试通过
- 无明显回归

### Phase 2：接入主路径一号

> **状态**: ✅ **已完成（路径 A: CodexController）**
>
> **交付物**:
> - `CodexChatSseEventAdapter.cs` — SSE 事件适配器
> - `CodexController.cs` 已改造，新增 `StreamWithRuntimeAsync` 方法
> - 环境变量 `CODEX_DISABLE_RUNTIME` 控制 runtime/legacy 路径切换

本阶段有两种路径，需在 Phase 0A 决策。

#### 路径 A：先接 `CodexController`

适用条件：

- `CodexLocalClient` 和集成测试优先级更高

目标：

- 让旧 `/api/Codex/Chat` 的核心 loop 交给 runtime
- 保留当前 SSE 格式与尾部补发行为兼容

具体任务：

1. 抽出 `CodexController` 中以下逻辑进入 runtime：
   - round advancement
   - tool collection
   - tool result append
   - empty response recovery
   - tool dedupe
   - high-cost debounce
2. 暂时保留在 Controller 的逻辑：
   - HTTP endpoint
   - audit / scheduler 事件
   - 特定 persistence 副作用
3. 新增 `CodexChatSseEventAdapter`
4. 为 `CodexLocalClient` 增加兼容回归测试

完成判据：

- `CodexLocalClient` 集成测试通过
- `/api/Codex/Chat` 对外 wire contract 不变

#### 路径 B：先接 `GatewayMessageProcessor`

适用条件：

- WebUI 主路径优先级更高

目标：

- 把 `GatewayMessageProcessor.StreamWithToolLoopAsync()` 中的 loop 交给 runtime

具体任务：

1. Gateway 只保留：
   - session 绑定
   - prompt / metadata 组装
   - `GatewaySseEvent` 发射
2. 新增 `GatewayRuntimeEventAdapter`
3. Gateway 继续输出：
   - `content`
   - `thinking_*`
   - `tool_call`
   - `task_status`
   - `system_message`
   - `done`

完成判据：

- `useGateway` 无需大改即可工作
- Gateway SSE 兼容测试通过

### Phase 3：接入第二条主路径

> **状态**: ✅ **已完成**
>
> **交付物**:
> - `GatewayRuntimeEventAdapter.cs` — Gateway 事件适配器
> - `GatewayMessageProcessor.cs` 已改造，新增 `StreamWithRuntimeAsync` 方法
> - 环境变量 `GATEWAY_DISABLE_RUNTIME` 控制 runtime/legacy 路径切换
> - `UserChat` / `TaskCompleted` / `TaskFailed` 三条 LLM 路径已统一走 runtime

若 Phase 2 先接入 `CodexController`，此阶段接 Gateway。

若 Phase 2 先接入 Gateway，此阶段接 `CodexController`。

目标：

- 双主路径共享同一个 runtime 内核
- 只保留 adapter 差异

完成判据：

- 至少 `Gateway` + `CodexController` 共享 `IQueryRuntimeEngine`
- 不同入口的 recovery / termination 逻辑实现显著收敛

### Phase 4：收口 `DefaultCodexKernel`

> **状态**: ✅ **完成（可灰度使用）**
>
> **完成日期**: 2026-04-09
>
> **验收**:
> - Runtime 路径已打通，具备 `KERNEL_DISABLE_RUNTIME` fallback
> - Guardrail + Critique 闭环已实现
> - 专项测试与关键回归测试已通过
> - 行为差异已文档化（见 [ADR-001](../adr/ADR-001-kernel-runtime-semantics.md)）
>
> **注意事项**:
> - Critique 语义与旧实现存在有意设计的差异（非逐字节兼容）
> - 建议先观察真实流量稳定性，再进入 Phase 4B/5 或 context governance

目标：

- 让 Kernel 从”循环实现者”变成”角色策略宿主”

保留在 Kernel：

- role prompt
- critique policy
- guardrail
- tool access policy

迁出到 runtime：

- round loop
- tool append
- common recovery
- common termination

新增机制：

- `IQueryRuntimeInterventionHook` — 允许入口层干预 runtime 行为
- `ICodexGuardrail` — Guardrail 服务接口
- `DefaultCodexGuardrail` — 基于 ICodeAnalysisService 的实现

注意事项：

- 不要试图把 critique loop 也完全抽象成通用 runtime 行为
- critique 通过 intervention hook 形成闭环，而非直接操作 messages

完成判据：

- `DefaultCodexKernel` 的 `RunLoopAsync()` 主体明显缩短
- recovery 与 tool loop 逻辑不再是其主体
- `KERNEL_DISABLE_RUNTIME=true` 可回退旧路径

#### Phase 4A 稳定期观察（当前核心工作）

> **状态**: 🔵 建议
> **持续时间**: 1-2 周（或至少 50+ 真实任务）

在进入 Phase 5 或更激进的 context governance 之前，建议先完成：

1. **真实日志对比**
   - 收集 `KERNEL_DISABLE_RUNTIME=true/false` 下的 transcript 对照
   - 对比新旧路径的 termination reason 分布
   - 验证 critique/guardrail 触发频率和效果

2. **历史测试修复**
   - 清理现有测试套件中的历史失败项
   - 确保"专项测试通过" ≈ "系统性回归风险清零"

3. **生产验证**
   - 在低风险任务中逐步启用 runtime 路径
   - 监控 telemetry 中的 runtime/legacy 标记
   - 收集用户反馈

**不要马上做**:
- 激进的 context governance
- 大规模 prompt builder 重构
- 其他入口的大改动

### Phase 5：最小 context governance

> **状态**: 🟡 **部分完成（挂点已落地，治理策略未开始）**
>
> **前置条件**: Phase 4A 稳定期观察完成，runtime kernel 路径在真实流量中验证稳定
>
> **说明**: 当前不建议立刻进入此阶段。应先完成稳定期观察，确保 runtime 路径行为符合预期后再推进。

当前已落地：

- `IContextWindowManager`
- `DefaultContextWindowManager`
- runtime turn 完成后的 conversation capture 回写
- 与 `CodexSessionManager` 自动上下文压缩阈值的接线

目标：

- 在 loop 收口后，补最急需的上下文治理

第一批待做：

1. tool result budget
2. 过大输出裁剪提示
3. recent tool trajectory 高保真保留

第二批再做：

1. history projection
2. compacted summary
3. recovery-time narrowed window

完成判据：

- 超长 tool output 不再直接污染 messages
- recovery 可感知裁剪行为

### Phase 6：Prompt / Context 协同增强

> **状态**: ⏸️ **未开始**
>
> **说明**: 此阶段依赖 Phase 5 完成，且需要更长的稳定期观察。当前阶段不建议规划具体时间表。

目标：

- 在 runtime 稳定后再增强 prompt assembly

此阶段才考虑：

- `IPromptAssemblyService`
- `RecoveryPromptFactory`
- 更完整的 `IContextWindowManager` budget / projection 策略

---

## 17. 推荐实施顺序

> **当前进度**: Phase 0A-4A 已完成，Gateway 全路径收口，进入稳定期观察
>
> **当前状态图**:
> ```
> Phase 0A  ✅ 完成
> Phase 0B  ✅ 完成
> Phase 1   ✅ 完成
> Phase 2A  ✅ 完成 (CodexController)
> Phase 3   ✅ 完成 (GatewayMessageProcessor 全 LLM 路径)
> Phase 4A  ✅ 完成 (DefaultCodexKernel)
> 稳定期    🔵 进行中
> Phase 5   🟡 挂点已落地，预算治理待开始
> Phase 6   ⏸️ 暂缓
> ```

结合当前仓库，我建议采用：

1. ~~`Phase 0A / 0B`~~ ✅ 已完成
2. ~~`Phase 1: SimpleCodexController`~~ ✅ 已完成
3. ~~`Phase 2: CodexController`~~ ✅ 已完成
4. ~~`Phase 3: GatewayMessageProcessor`~~ ✅ 已完成
5. ~~`Phase 4: DefaultCodexKernel`~~ ✅ 已完成
6. `Phase 5: 最小 context governance` — 🟡 先做 budget / trimming，暂不做激进 compaction
7. `Phase 6: Prompt / Context 协同增强` — ⏸️ 暂缓

原因：

- `SimpleCodexController` 适合验证 API ✅
- `CodexController` 直接覆盖 `CodexLocalClient` ✅
- `Gateway` 再接入时可以复用成熟 runtime ✅
- `Kernel` 最复杂，最后收口风险更低 ✅

**下一步核心工作**:

1. 稳定期观察
   - 收集 `GATEWAY_DISABLE_RUNTIME` / `CODEX_DISABLE_RUNTIME` / `KERNEL_DISABLE_RUNTIME` 的 transcript 对照
   - 复核 termination / recovery / empty-response 分布
2. Phase 5-A: Tool Result Budget
   - 为超长工具输出增加 budget policy
   - 在 runtime 中显式记录裁剪提示
3. Runtime + Compression 联动验证
   - 增加真实流量和更高层 E2E 回归
   - 验证 `IContextWindowManager` 与自动压缩阈值在长会话中的行为
4. 历史测试清理
   - 修复与 runtime 无关但会干扰稳定期判断的历史失败项

---

## 18. 详细实施任务拆解

### 18.1 Phase 0A 任务清单

> **状态**: ✅ 已完成

#### 后端

- [x] 收集带 `QUERYLOOP|` 的日志样本
- [x] 运行 baseline 脚本
- [x] 补充日志采样说明

#### 文档

- [x] 增加 `artifacts/queryloop-baseline/README.md`
- [x] 记录 baseline 口径和样本来源

#### 验收

- [x] 能复现 baseline 报表

### 18.2 Phase 0B 任务清单

> **状态**: ✅ 已完成

#### Core

- [x] 新增 `IvilsonCodex.Core/Runtime/`
- [x] 建立 request/state/result/event 模型
- [x] 建立 recovery / tool coordinator 接口

#### Tests

- [x] 模型 round-trip 测试 (`RuntimeModelTests.cs`)
- [x] continue / terminate reason 兼容测试

### 18.3 Phase 1 任务清单

> **状态**: ✅ 已完成

#### Core

- [x] 实现最小 `QueryRuntimeEngine`
- [x] 实现默认 `ToolExecutionCoordinator`
- [x] 实现默认 `RecoveryPolicy`

#### API

- [x] `SimpleCodexController` 改为 runtime 驱动

#### Tests

- [x] `SimpleCodexController` SSE 回归测试
- [x] telemetry 不变性测试

### 18.4 Phase 2 任务清单（以 `CodexController` 为例）

> **状态**: ✅ 已完成

#### Controller

- [x] 用 runtime 替换内部 loop
- [x] 保持原 SSE payload
- [x] 抽出旧 chat adapter (`CodexChatSseEventAdapter`)

#### Headless

- [x] `CodexSseChatClient` 兼容回归测试
- [x] `HeadlessCodexWorkflowRunner` 端到端 smoke test

#### Tests

- [x] `[DONE]` 与 `setconversationid` 顺序兼容测试
- [x] empty response fallback 测试
- [x] tool dedupe 测试

### 18.5 Phase 3 任务清单（Gateway）

> **状态**: ✅ 已完成

#### Gateway

- [x] 新增 `GatewayRuntimeEventAdapter`
- [x] `GatewayMessageProcessor` 只保留业务编排和 event emission

#### Frontend

- [x] 尽量不改 `useGateway`
- [x] 仅在必要时补适配测试

#### Tests

- [x] Gateway SSE golden transcript 测试
- [x] `useGateway` 兼容测试

### 18.6 Phase 4 任务清单（Kernel）

> **状态**: ✅ 已完成

#### Core

- [x] 将 common loop 迁入 runtime
- [x] 保留 role / critique / guardrail 特性
- [x] 新增 `IQueryRuntimeInterventionHook` 接口
- [x] 新增 `ICodexGuardrail` 接口和 `DefaultCodexGuardrail` 实现
- [x] `KernelRuntimeEventAdapter` 实现 guardrail + critique 闭环

#### Tests

- [x] Forge / Architect / Security 的行为回归测试
- malformed protocol / legacy tool-call 兼容测试

### 18.7 Phase 5 任务清单（context）

> **状态**: 🟡 部分完成（挂点已落地，策略待开始）

#### Core

- [x] 新增 `IContextWindowManager`
- [x] 新增 `DefaultContextWindowManager`
- [x] runtime turn-completion capture 接入 `SessionManager` 自动压缩
- [ ] 新增 tool result budget policy
- [ ] 新增最小裁剪器

#### Tests

- [x] runtime 完成后上下文窗口回写单测
- [ ] 超长工具输出裁剪测试
- [ ] recovery 场景下裁剪提示测试

---

## 19. 文件级落点建议

### 19.1 已新增文件（Phase 0B-4A）

> **状态**: ✅ 已完成

- `IvilsonCodex.Core/Runtime/IQueryRuntimeEngine.cs`
- `IvilsonCodex.Core/Runtime/QueryRuntimeEngine.cs`
- `IvilsonCodex.Core/Runtime/QueryRuntimeRequest.cs`
- `IvilsonCodex.Core/Runtime/QueryRuntimeState.cs`
- `IvilsonCodex.Core/Runtime/QueryRuntimeResult.cs`
- `IvilsonCodex.Core/Runtime/QueryRuntimeEvent.cs`
- `IvilsonCodex.Core/Runtime/IToolExecutionCoordinator.cs`
- `IvilsonCodex.Core/Runtime/DefaultToolExecutionCoordinator.cs`
- `IvilsonCodex.Core/Runtime/IQueryRecoveryPolicy.cs`
- `IvilsonCodex.Core/Runtime/DefaultQueryRecoveryPolicy.cs`
- `IvilsonCodex.Core/Runtime/IQueryRuntimeEventSink.cs`
- `IvilsonCodex.Core/Runtime/QueryRuntimeIntervention.cs`
- `IvilsonCodex.Core/Runtime/IContextWindowManager.cs`
- `IvilsonCodex.Core/Runtime/DefaultContextWindowManager.cs`
- `IvilsonCodex.Core/Abstractions/ICodexGuardrail.cs`
- `IvilsonCodex.Core/Agents/DefaultCodexGuardrail.cs`
- `IvilsonCodex.Core/Agents/Adapters/KernelRuntimeEventAdapter.cs`
- `IvilsonCodex/Gateway/Adapters/GatewayRuntimeEventAdapter.cs`
- `IvilsonCodex/Controllers/Adapters/CodexChatSseEventAdapter.cs`
- `IvilsonCodex/Controllers/Adapters/SimpleCodexSseEventAdapter.cs`

### 19.2 已改造文件（Phase 1-4）

> **状态**: ✅ 已完成

- `IvilsonCodex/Controllers/SimpleCodexController.cs`
- `IvilsonCodex/Controllers/CodexController.cs`
- `IvilsonCodex/Gateway/GatewayMessageProcessor.cs`
- `IvilsonCodex.Core/Agents/DefaultCodexKernel.cs`

### 19.3 未来可能新增（Phase 5+）

> **状态**: ⏸️ 暂缓

- `IvilsonCodex.Core/Runtime/ToolResultBudgetPolicy.cs`

---

## 20. 测试策略

### 20.1 测试类型完成情况

| 测试类型 | 状态 | 说明 |
|---------|------|------|
| runtime 单元测试 | ✅ 已完成 | `RuntimeModelTests.cs` (45 tests) |
| adapter 单元测试 | ✅ 已完成 | `KernelRuntimeIntegrationTests.cs` (9 tests) |
| golden transcript 回归测试 | ⏸️ 部分完成 | 需要真实流量验证 |
| SSE contract 兼容测试 | ✅ 已完成 | 各 adapter 已实现 |
| headless 集成测试 | ⏸️ 部分完成 | 依赖真实环境 |

### 20.2 已覆盖的关键场景

- [x] 无工具调用直接完成
- [x] 单轮工具调用后完成
- [x] 多轮工具调用
- [x] 空响应恢复
- [x] malformed protocol 恢复
- [x] 重复工具调用去重
- [ ] context hard limit 停止 — Phase 5
- [x] `[DONE]` 与 `setconversationid` 顺序兼容
- [x] Gateway `thinking_*` / `tool_call` / `done` 顺序稳定性

### 20.3 测试文件清单

#### Core 单元测试 ✅

- `RuntimeModelTests.cs` — 模型和状态流转测试
- `KernelRuntimeIntegrationTests.cs` — Kernel runtime 专项测试

#### API / Gateway 回归测试 ✅

- `SimpleCodexCompatibilityTests.cs` — SimpleCodex SSE 兼容
- CodexController 回归 — 通过 `CodexLocalClient.Tests` 覆盖
- Gateway 回归 — 通过前端 `useGateway` 覆盖

#### Headless 集成测试 ✅

- `CodexLocalClient.Tests` — headless 客户端兼容测试

### 20.4 待补充的测试

- [ ] Kernel runtime/legacy 路径 transcript 对照测试
- [ ] 真实流量中的 telemetry 对比测试
- [ ] Guardrail 触发频率统计测试

---

## 21. 风险与规避

### 风险 1：抽象过早，导致主路径收益不明显

规避：

- 第一阶段只做最小 runtime
- 不同时推进 prompt / context 大抽象

### 风险 2：旧 `/api/Codex/Chat` 回归影响 headless

规避：

- 把 `CodexLocalClient` 当作一级兼容消费者
- 在 `CodexController` 接入 runtime 前先补回归测试

### 风险 3：Gateway 事件顺序回归影响 WebUI

规避：

- 使用 adapter
- 保持 `GatewaySseEventType` 不变
- 做 golden transcript 测试

### 风险 4：Kernel 过度抽象，破坏 critique / guardrail 特性

规避：

- 只抽 common loop
- critique 和 guardrail 暂留 Kernel

### 风险 5：context compaction 提前上量导致推理退化

规避：

- loop 收口后再做
- 先做 tool result budget
- 不做激进历史压缩

---

## 22. 验收标准

### 22.1 技术验收

1. 至少 `CodexController` 与 `GatewayMessageProcessor` 或 `SimpleCodexController` 共享同一个 `IQueryRuntimeEngine`
2. recovery policy 不再散落在多个入口独立实现
3. tool execution / result append 语义收口
4. 统一输出 continue / termination reason
5. 内部 runtime event 与外部 SSE adapter 分离
6. Gateway 内部 3 条 LLM 主路径共享同一个 `IQueryRuntimeEngine`

### 22.2 兼容验收

1. Gateway SSE contract 保持兼容
2. 旧 `/api/Codex/Chat` 的关键 wire 行为保持兼容
3. `CodexLocalClient` 现有测试通过

### 22.3 运维验收

1. baseline 报表可持续生成
2. 不同入口的 termination / recovery 数据可横向对比
3. recovery 成功率和异常类型更易观测

### 22.4 代码结构验收

1. `Controller` / `Gateway` 中 loop 代码显著减少
2. `DefaultCodexKernel` 不再以 loop 细节为主体
3. runtime 目录承担主要 loop 逻辑

---

## 23. 当前推荐执行方案

如果按当前状态继续推进，推荐按以下顺序执行：

1. 完成稳定期观察与 transcript 对照
2. 修复会干扰判断的历史失败测试
3. 启动 Phase 5-A：tool result budget / trimming
4. 验证 runtime 与 context compression 联动在真实长会话中的稳定性
5. 再决定是否进入更激进的 prompt / context 协同增强

---

## 24. 最终判断

统一 Query Runtime 这件事，当前仍然值得做，而且 runtime 主收口工作已经基本完成。

但旧版蓝图的问题在于：

- 过早假定 baseline 已完成
- 过高估计前端改造收益
- 低估旧 `/api/Codex/Chat` 与 `CodexLocalClient` 的现实约束
- 试图在同一轮升级里同时处理 loop、prompt、context compaction 三大主题

本版蓝图的核心调整是：

- 先收口 loop
- 再收口主路径
- 现在进入稳定期观察
- 最后再做 context / prompt 增强

这条路线更贴近当前项目，也更有机会稳定落地。
