# QueryRuntime 上下文组装与主循环对齐改造蓝图

## 1. 文档目标

本文定义 CodexFlow QueryRuntime 的下一步改造方案：在现有 tool-use contract、worker surface、wire logging 和 TASK_BUG_001 修复基础上，引入更明确的上下文组装管线，并把 QueryRuntime 主循环对齐到 Claude Code 风格的 agentic loop：

```text
prompt
  -> model
  -> tool plan
  -> validate
  -> normalize
  -> execute
  -> observe
  -> recover
  -> compact
  -> continue
  -> stop
```

目标不是照搬 Claude Code 的实现，而是吸收其关键工程原则：

- 上下文是每轮重新投影的结构化产物，而不是一次性拼接的字符串。
- 工具调用必须经过计划、校验、归一、执行、观测、恢复的完整闭环。
- 压缩不能只生成一个历史摘要，还要保留当前工作现场。
- recovery 不能只靠元指令，要把模型继续执行所需的证据、约束和半成品参数带回上下文。
- 每轮最终发送给模型的 messages、tools、tool_choice 和上下文 section 必须可审计。

---

## 2. 背景与问题

### 2.1 TASK_BUG_001 暴露的问题

TASK_BUG_001 的失败链路说明 QueryRuntime 不能只依赖“模型看到提示后自觉调用工具”：

1. Forge worker 已读取目标文件和 Hashline 快照。
2. 模型在文本中表达写入意图，但没有发出真实写工具调用。
3. recovery prompt 早期只强调“必须调用写工具”，缺少可直接构造 `hs_write` 参数的上下文。
4. 客户端 SDK / provider 兼容层曾导致 `ToolMode`、工具过滤和 wire body 不一致。
5. 即使 runtime 端设置了 required tool，也需要出站 wire、工具表和执行守卫三层共同保证。

当前修复已经补强了 `hs_write` 参数兼容、snapshot/fingerprint 回填、工具参数归一和 live LLM 集成测试。但从架构上看，仍需要把“上下文管理”和“loop 阶段”一等化。

### 2.2 CodexFlow 当前状态

现有能力：

- `QueryRuntimeEngine` 已统一 Gateway / worker 的多轮 tool loop。
- `QueryRuntimeState` 已记录 tool calls、write calls、recovery counts、recent read evidence、repeated read evidence。
- `RequiredToolExecutionContract` 已能阻止 worker 在缺少最低工具证据时停止。
- `ToolCatalogPromptComposer` 已按当前工具面注入工具使用准则。
- `CodexSessionManager` 已有 RecentTurns soft limit、HistorySummary 和自动压缩。
- `VllmWireLoggingHandler` 已能检查出站 tools / tool_choice / response tool use。

主要缺口：

- prompt 组装责任分散在 Gateway、worker prompt、memory assembler、runtime recovery。
- runtime 每轮只有 `BuildRoundMessages`，尚未形成完整的 `ContextProjectionPipeline`。
- 压缩粒度偏会话历史，不够关注工具结果、文件快照、当前任务和 worker 状态。
- recovery 上下文仍是 ad-hoc 字符串，而不是来自统一的 Evidence Ledger。
- 缺少一份可持久化、可调试的 Prompt Assembly Snapshot。

---

## 3. Claude Code 可借鉴点

### 3.1 上下文分层

Claude Code 将上下文拆成：

- stable system prompt
- user context
- system context
- session messages after compact boundary
- tool result budget projection
- compact summary
- post-compact attachments

CodexFlow 应对应拆成：

| Claude Code 概念 | CodexFlow 目标概念 |
|---|---|
| `systemPrompt` | StableSystemPromptFrame |
| `userContext` | UserMemoryFrame / CurrentDateFrame |
| `systemContext` | WorkspaceStateFrame / GitStateFrame / WorkerSurfaceFrame |
| post compact messages | CompactSummaryFrame + PreservedTailFrame |
| file attachments after compact | EvidenceLedgerFrame |
| deferred tool / agent delta attachments | ToolSurfaceFrame / WorkerCapsuleFrame |

### 3.2 每轮上下文投影

Claude Code 每轮调用模型前会重新生成 `messagesForQuery`，包括工具结果预算、snip/microcompact/autocompact、上下文 collapse 和 post-compact messages。

CodexFlow 也应在每轮 `ILLMExecutor.StreamAsync` 前统一执行：

```text
PersistedMessages
  -> trim/summarize old transcript if needed
  -> apply tool result budget
  -> inject EvidenceLedger
  -> inject WorkerCapsule
  -> inject recovery hints
  -> assemble final ChatMessage[]
```

### 3.3 压缩后恢复工作现场

Claude Code compact 后不仅保留摘要，还恢复最近文件、计划、工具列表、agent 信息和被调用过的 skill。

CodexFlow 应避免压缩后只剩 `HistorySummary`。对 worker 来说，真正关键的是：

- 当前任务目标
- 已读取文件路径
- snapshotId / fingerprint / line window
- 已执行工具结果摘要
- 待写文件和候选修改点
- 最近失败的工具调用和失败原因
- worker contract 是否已满足

### 3.4 请求级可审计性

Claude Code 保存 last API request 和 post-compaction messages 用于调试。CodexFlow 已有 wire log，但还缺少“组装阶段的解释性快照”。

应新增 `PromptAssemblySnapshot`，记录本轮为何包含或排除了某段上下文。

---

## 4. 目标态架构

### 4.1 新增 Context Assembly 层

新增核心抽象：

```csharp
public interface IQueryContextAssembler
{
    Task<QueryContextFrameSet> AssembleAsync(
        QueryContextAssemblyRequest request,
        CancellationToken ct = default);
}
```

`QueryContextFrameSet` 表示一轮模型调用前的结构化上下文：

```csharp
public sealed record QueryContextFrameSet
{
    public required IReadOnlyList<QueryContextFrame> Frames { get; init; }
    public required PromptAssemblySnapshot Snapshot { get; init; }
}

public sealed record QueryContextFrame
{
    public required string Name { get; init; }
    public required QueryContextFrameKind Kind { get; init; }
    public required string Content { get; init; }
    public int Priority { get; init; }
    public bool StableAcrossRounds { get; init; }
    public bool Compressible { get; init; } = true;
    public bool IsUntrustedData { get; init; } = true;
    public int EstimatedTokens { get; init; }
    public string? Source { get; init; }
}
```

建议 frame kind：

```csharp
public enum QueryContextFrameKind
{
    StableSystem,
    UserMemory,
    ProjectMemory,
    ConversationSummary,
    RecentTranscript,
    ToolSurface,
    WorkerCapsule,
    EvidenceLedger,
    RecoveryHint,
    CompactBoundary,
    DebugMetadata
}
```

### 4.2 Evidence Ledger

将 `QueryRuntimeState.RecentReadEvidence`、`LastToolBatchSummaryPrompt`、重复读取状态、写入 recovery 信息统一提升为 Evidence Ledger：

```csharp
public sealed record QueryEvidenceLedger
{
    public IReadOnlyList<FileEvidence> Files { get; init; } = [];
    public IReadOnlyList<ToolEvidence> ToolResults { get; init; } = [];
    public IReadOnlyList<PendingModificationEvidence> PendingModifications { get; init; } = [];
    public IReadOnlyList<RuntimeFailureEvidence> Failures { get; init; } = [];
    public IReadOnlyList<string> RepeatedEvidenceKeys { get; init; } = [];
}
```

文件证据：

```csharp
public sealed record FileEvidence
{
    public required string FilePath { get; init; }
    public string? SnapshotId { get; init; }
    public string? FileFingerprint { get; init; }
    public int? WindowStartLine { get; init; }
    public int? WindowEndLine { get; init; }
    public string? Summary { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
}
```

这份 ledger 是 recovery、compact、worker resume 和最终回答的共同事实来源。它不替代原始 transcript，而是为模型提供“当前工作现场”。

### 4.3 Worker Capsule

每个 worker 每轮应注入一个小型、稳定、结构化的 capsule：

```text
## Worker Capsule
- worker: Ivilson-Forge
- isolation: ShadowWorktree
- output contract: TaskNotificationEnvelope
- required tool contract: forge_worker_write_evidence
- contract status: unsatisfied / satisfied
- allowed tool categories: Read, Analysis, Forge, System
- preferred next action: write / verify / synthesize
- current task: TASK_BUG_001 ...
```

Worker capsule 应替代部分散落在 system prompt 和 recovery prompt 中的 worker 状态说明。

### 4.4 Prompt Assembly Snapshot

每轮模型调用前生成快照：

```csharp
public sealed record PromptAssemblySnapshot
{
    public required Guid QueryId { get; init; }
    public required string SessionId { get; init; }
    public required int Round { get; init; }
    public required QueryLoopEntryPoint EntryPoint { get; init; }
    public required IReadOnlyList<PromptAssemblyFrameRecord> Frames { get; init; }
    public required IReadOnlyList<string> ToolNames { get; init; }
    public string? ToolChoice { get; init; }
    public int EstimatedPromptTokens { get; init; }
    public int EstimatedContextTokens { get; init; }
    public IReadOnlyList<string> DroppedFrames { get; init; } = [];
    public IReadOnlyList<string> BudgetDecisions { get; init; } = [];
}
```

用途：

- wire log 之外的 prompt 组装审计。
- 集成测试中打印 worker 详细上下文。
- 复盘模型为什么没有调用工具。
- 判断 compaction 是否丢失关键证据。

---

## 5. 主循环目标状态

### 5.1 标准循环

QueryRuntime 主循环应明确拆成以下阶段：

```text
while (!stop)
{
    prompt = AssemblePrompt(state)
    response = CallModel(prompt)
    toolPlan = ExtractToolPlan(response)
    validation = ValidateToolPlan(toolPlan)
    normalizedPlan = NormalizeToolPlan(validation.AcceptedCalls)
    results = ExecuteTools(normalizedPlan)
    observations = Observe(results)
    recovery = DecideRecovery(response, toolPlan, observations)
    compacted = CompactIfNeeded(state, observations)
    stop = DecideStop(response, toolPlan, recovery, compacted)
}
```

### 5.2 阶段语义

| 阶段 | 输入 | 输出 | 责任 |
|---|---|---|---|
| `prompt` | state, session, worker, ledger, tools | final messages/options/snapshot | 上下文投影、预算、工具面 |
| `model` | messages/options/tools | streamed response | 收集 text/thinking/tool calls/usage |
| `tool plan` | response | `ToolPlan` | 抽取真实 `FunctionCallContent`，兼容 legacy text fallback |
| `validate` | tool plan, worker contract, policy | accepted/rejected calls | 权限、required tool、工具面、危险操作、重复调用 |
| `normalize` | accepted calls | normalized calls | 参数归一、Newtonsoft/System.Text.Json 兼容、schema 修复 |
| `execute` | normalized calls | raw results | 并发/串行执行、streaming tool execution、超时 |
| `observe` | results | evidence updates | 更新 ledger、tool counters、contract status、summaries |
| `recover` | state + response + observations | recovery action | 空响应、未执行写意图、错误工具、重复读取、contract 未满足 |
| `compact` | transcript + ledger | compacted transcript/ledger | 工具结果瘦身、历史摘要、保留尾部和证据 |
| `continue` | action | next state | 追加 tool results / recovery prompt / batch summary |
| `stop` | state | result | 最终文本、终止原因、detail code |

### 5.3 不变量

1. 只有真实 `FunctionCallContent` 才进入工具执行。
2. `tool plan` 阶段不执行工具，只抽取候选调用。
3. `validate` 阶段可以拒绝工具，但必须产生 synthetic tool_result 或 recovery feedback，避免 tool_use/tool_result 失配。
4. `normalize` 阶段必须发生在权限校验之后、实际执行之前。
5. `observe` 阶段是唯一更新 Evidence Ledger 的地方。
6. `recover` 阶段不直接改 transcript 大段历史，只产生下一轮临时 frame 或短 feedback message。
7. `compact` 不能丢失未满足 contract、最近文件快照和 pending write evidence。
8. `stop` 必须经过 required tool contract、wrap-up tool continuation、empty response、insufficient visible answer 的最终判定。

---

## 6. 关键组件设计

### 6.1 QueryRuntimeLoopPhase

新增阶段枚举，用于日志、telemetry、测试断言：

```csharp
public enum QueryRuntimeLoopPhase
{
    PromptAssembly,
    ModelSampling,
    ToolPlanExtraction,
    ToolPlanValidation,
    ToolArgumentNormalization,
    ToolExecution,
    Observation,
    RecoveryDecision,
    ContextCompaction,
    ContinuationDecision,
    StopDecision
}
```

### 6.2 ToolPlan

```csharp
public sealed record ToolPlan
{
    public required IReadOnlyList<FunctionCallContent> Calls { get; init; }
    public required string AssistantText { get; init; }
    public string? ThinkingText { get; init; }
    public bool FromLegacyTextFallback { get; init; }
}
```

### 6.3 ToolPlanValidationResult

```csharp
public sealed record ToolPlanValidationResult
{
    public IReadOnlyList<FunctionCallContent> AcceptedCalls { get; init; } = [];
    public IReadOnlyList<RejectedToolCall> RejectedCalls { get; init; } = [];
    public bool RequiresRecovery { get; init; }
    public string? RecoveryReason { get; init; }
}
```

拒绝原因建议标准化：

- `tool_not_available`
- `non_required_tool_in_required_round`
- `permission_denied`
- `duplicate_or_stalled_call`
- `exploration_limit_exceeded`
- `worker_surface_violation`
- `malformed_arguments`

### 6.4 ObservationResult

```csharp
public sealed record ObservationResult
{
    public required IReadOnlyList<ToolExecutionResult> ToolResults { get; init; }
    public required QueryEvidenceLedger UpdatedLedger { get; init; }
    public bool RequiredToolContractSatisfied { get; init; }
    public bool HasWriteEvidence { get; init; }
    public bool HasRepeatedReadEvidence { get; init; }
    public string? ToolBatchSummary { get; init; }
}
```

---

## 7. 上下文预算与压缩策略

### 7.1 分层预算

每轮 prompt 的预算建议分层管理：

| 层 | 默认策略 |
|---|---|
| Stable system | 不压缩，尽量稳定，利于 provider cache |
| Tool schemas | 由真实工具面决定，不靠 prompt catalog 替代 |
| Worker capsule | 小而稳定，最多 1-2KB |
| Evidence ledger | 高优先级，保留结构摘要和 handles |
| Recent transcript | 保留最近 N 个 API round |
| Tool raw output | 低优先级，超限后摘要 + handle |
| History summary | 可压缩，可替换 |
| Debug metadata | 只在诊断模式注入 |

### 7.2 工具结果预算

参考 Claude Code 的 `applyToolResultBudget` 思路：

- 每个工具结果进入 transcript 前先生成 `summary`。
- 对大结果保存：
  - preview
  - summary
  - lossless handle，如 file path、snapshotId、artifact id、log file path
- prompt 中默认注入 summary，不反复注入完整 raw output。

对 CodexFlow 工具：

| 工具类型 | 保留内容 |
|---|---|
| `hs_read` / `ivilson_read` | path, snapshotId, fingerprint, line window, excerpt preview |
| search tools | query, top paths, match counts, first few hits |
| `exec_cmd` / `run_tests` | command, exit code, tail, failure markers |
| write tools | changed files, operation count, checkpoint id |
| LSP tools | diagnostic counts, affected files, top diagnostics |

### 7.3 Compact 后必须保留

compact 后第一轮必须恢复：

- compact boundary
- HistorySummary
- WorkerCapsule
- EvidenceLedger 当前 working set
- 当前 active task / user request
- 未完成 recovery action
- required tool contract 状态
- 当前工具面 delta

---

## 8. Recovery 与上下文对齐

### 8.1 Recovery Action 类型

```csharp
public enum QueryRecoveryActionKind
{
    None,
    RetryWithSameTools,
    RetryWithRequiredTool,
    RetrySynthesisOnly,
    RetryAfterToolSurfaceCorrection,
    CompactAndRetry,
    FailTerminal
}
```

### 8.2 Recovery 不再只拼元指令

每种 recovery 都应由 Context Assembly 生成结构化 frame：

```text
## Recovery Hint
- reason: unexecuted_write_intent
- required tool: hs_write
- previous assistant plan: ...
- available file snapshots:
  - CodexFlow.Core/Runtime/QueryRuntimeEngine.cs snapshotId=... fingerprint=...
- recommended minimal call:
  hs_write({ "filePath": "...", "oldString": "...", "newString": "..." })
```

### 8.3 Required Tool 三层保证

required tool 轮必须同时具备：

1. `ChatToolMode.RequireSpecific(toolName)` 或 provider 等价字段。
2. 出站 tools 表只包含 required tool。
3. runtime execution guard 拒绝非 required tool，并产生 recovery feedback。

如果任意一层不可用，`PromptAssemblySnapshot` 和 wire log 必须能看出来。

---

## 9. 与现有代码的落点

### 9.1 QueryRuntimeEngine

目标改造：

- 将 `ExecuteRoundAsync` 内部拆成阶段方法。
- 用 `IQueryContextAssembler` 替代直接 `BuildRoundMessages`。
- 将 `BuildToolBatchSummaryPrompt` 产物写入 Evidence Ledger，而不是只存 `PendingToolBatchSummaryPrompt`。
- 将 `UpdateRepeatedReadEvidenceState` 改为 Observation 阶段的一部分。
- 将 unexecuted intent recovery 产物改为 Recovery Frame。

保留现有行为：

- streaming tool execution
- required tool guard
- wrap-up continuation
- tool deduplication
- legacy tool call fallback
- current integration tests

### 9.2 QueryRuntimeState

新增：

```csharp
public QueryEvidenceLedger EvidenceLedger { get; set; } = QueryEvidenceLedger.Empty;
public PromptAssemblySnapshot? LastPromptAssemblySnapshot { get; set; }
public QueryRuntimeLoopPhase CurrentPhase { get; set; }
```

逐步收敛：

- `RecentReadEvidence` 迁移到 `EvidenceLedger.Files`
- `LastToolBatchSummaryPrompt` 迁移到 `EvidenceLedger.ToolResults`
- `LastRepeatedReadTargets` 迁移到 `EvidenceLedger.RepeatedEvidenceKeys`

### 9.3 DefaultContextWindowManager / CodexSessionManager

现有上下文治理偏持久会话记忆。新增 runtime loop compact 时，应避免直接污染 `RecentTurns`：

- 会话级历史压缩仍由 `CodexSessionManager` 管理。
- loop 内 prompt projection / tool result budget 由 `IQueryContextAssembler` 管理。
- worker resume 所需 ledger 可以进入专门的 runtime checkpoint / task artifact，而不是普通 chat turns。

### 9.4 ToolCatalogPromptComposer

继续保留当前工具使用准则，但应被纳入 `ToolSurfaceFrame`：

- frame 记录真实 tool names。
- frame 记录 auto-activated deferred tools。
- frame 记录 required tool / tool_choice。
- frame 记录 worker allowed categories。

### 9.5 VllmWireLoggingHandler

继续承担 wire 级验证，同时关联 `PromptAssemblySnapshot`：

- snapshot 认为 tools=1，wire tools 也应为 1。
- snapshot 认为 tool_choice=hs_write，wire body 也应出现等价字段。
- 如果 mismatch，输出 structured warning。

---

## 10. 实施阶段

### Phase 1: 可观测上下文快照

目标：不改变行为，先把每轮上下文组装可视化。

- 新增 `PromptAssemblySnapshot` 模型。
- 在 `QueryRuntimeEngine` 调用 `ILLMExecutor.StreamAsync` 前生成 snapshot。
- snapshot 包含 messages count、tool names、tool choice、estimated chars/tokens、pending recovery。
- 集成测试输出 snapshot。
- wire log 增加 snapshot/wire mismatch 提示。

验收：

- TASK_BUG_001 集成测试日志中能看到每轮 final tools、tool_choice、recovery frame、evidence summary。
- 如果 SDK 丢失 `tool_choice`，snapshot/wire mismatch 能直接暴露。

### Phase 2: Evidence Ledger

目标：把当前工作现场从 prompt 字符串提升为结构状态。

- 新增 `QueryEvidenceLedger`。
- Observation 阶段更新 file evidence、tool evidence、failure evidence。
- `BuildWriteRecoveryContext` 改为从 ledger 渲染。
- compact 后恢复 ledger frame。
- worker notification 可附带 ledger 摘要。

验收：

- 重复读取同 fingerprint 时 ledger 能标记 repeated evidence。
- 写入 recovery frame 包含可直接构造 `hs_write` 的 filePath / snapshotId / fingerprint。
- compact 后 TASK_BUG_001 仍能继续写入，不丢 snapshot。

### Phase 3: Context Projection Pipeline

目标：每轮 prompt 由结构化 frame 投影生成。

- 新增 `IQueryContextAssembler`。
- 实现 `DefaultQueryContextAssembler`。
- 将 `BuildRoundMessages` 替换为 `AssembleRoundMessages`。
- 支持 frame priority、budget、drop reason。
- 将 WorkerCapsule、ToolSurface、EvidenceLedger、RecoveryHint 都作为 frame 注入。

验收：

- runtime 每轮 prompt 组装路径单一。
- Gateway / worker / integration tests 能打印 frame 列表。
- recovery prompt 不再散落多个字符串拼接点。

### Phase 4: Loop Phase 拆分

目标：把大方法拆成可测试阶段。

- 拆出 `ExtractToolPlan`
- 拆出 `ValidateToolPlan`
- 拆出 `NormalizeToolArguments`
- 拆出 `ExecuteToolPlan`
- 拆出 `ObserveToolResults`
- 拆出 `DecideRecovery`
- 拆出 `DecideContinuationOrStop`

验收：

- 每个阶段有单元测试。
- malformed arguments、non-required tool、duplicate read、write intent recovery 都能阶段化断言。
- `ExecuteRoundAsync` 只保留 orchestration，不再承担全部细节。

### Phase 5: Multi-stage Compact

目标：压缩从会话历史拓展到 agentic loop 上下文。

- 工具结果 budget projection。
- recent transcript tail preservation。
- compact boundary frame。
- post-compact ledger restoration。
- compact circuit breaker，避免连续失败。

验收：

- 长工具输出不会无限膨胀 prompt。
- compact 后仍保留 active task、required contract、file evidence。
- compact 不会破坏 tool_use/tool_result pairing。

---

## 11. 测试计划

### 11.1 单元测试

- `PromptAssemblySnapshotTests`
  - frame 顺序稳定
  - required tool 轮 tools/tool_choice 正确
  - budget drop reason 正确
- `EvidenceLedgerTests`
  - hs_read metadata 提取
  - repeated fingerprint 检测
  - write recovery context 渲染
- `ToolPlanValidationTests`
  - 非 required tool 被拒绝
  - worker surface violation 被拒绝
  - synthetic tool_result 生成
- `ContextProjectionPipelineTests`
  - compact 后 ledger frame 仍在
  - recovery frame 覆盖普通 synthesis hint

### 11.2 集成测试

- TASK_BUG_001 live LLM test
  - 至少两次稳定通过
  - 日志包含 prompt snapshot、wire tools、tool_choice、tool calls、tool results
- Forge write recovery test
  - 模型先文本描述写入
  - runtime recovery 后必须调用 `hs_write`
- Repeated read pivot test
  - 连续读取同 fingerprint 后注入 synthesis/write guidance
- Long tool output compact test
  - 大命令输出被摘要，不破坏最终回答

### 11.3 诊断验收

失败报告必须能回答：

- 本轮模型实际看到了哪些上下文 frame？
- tools 表最终是什么？
- API 层是否真的带了 tool_choice？
- 哪些工具调用被 validate 拒绝，为什么？
- 哪些工具结果进入 Evidence Ledger？
- compact 是否丢弃了关键证据？
- stop 前 required tool contract 是否满足？

---

## 12. 风险与约束

### 12.1 不要一次性重写 runtime

`QueryRuntimeEngine` 已包含大量修复逻辑。改造应以 snapshot 和 ledger 为入口，先增强观测，再逐步搬迁逻辑。

### 12.2 不要把所有上下文都持久化到会话历史

工具证据、wire mismatch、recovery action 是 runtime 工作现场，不一定适合作为用户对话历史。应区分：

- user-visible transcript
- runtime prompt projection
- audit log
- worker checkpoint

### 12.3 不要让 prompt catalog 替代真实 tools schema

工具目录可以指导模型，但 runtime 的真实执行能力必须来自 `ChatOptions.Tools` 和 wire body。所有 required tool 行为都要用 snapshot + wire 双重验证。

### 12.4 不要压缩掉 tool pairing

任何 compact / trim 都必须保留合法的 assistant tool_use 与 tool_result 配对，或生成明确 synthetic tool_result。

---

## 13. 验收标准

1. 每轮 QueryRuntime 调用模型前都有 `PromptAssemblySnapshot`。
2. TASK_BUG_001 集成测试能输出 prompt frame、tools、tool_choice、tool call、tool result、ledger 更新。
3. Evidence Ledger 能保留最近文件快照、工具结果摘要、重复读取证据和 pending write context。
4. Required tool recovery 同时满足 prompt guidance、options tool mode、tools filtering、runtime execution guard。
5. compact 后 active task、worker capsule、required contract、file evidence 不丢失。
6. `ExecuteRoundAsync` 的核心路径能被映射到 `prompt -> model -> tool plan -> validate -> normalize -> execute -> observe -> recover -> compact -> continue -> stop`。
7. 旧 Gateway / worker 行为不回退，现有 tool-use contract 测试继续通过。

---

## 14. 当前实现状态

截至本轮改造，蓝图的核心链路已落地：

- `PromptAssemblySnapshot` 已在每轮模型请求前生成并通过 `PromptAssemblySnapshotEvent` 发出。
- `DefaultQueryContextAssembler` 已统一投影 recent transcript、worker capsule、tool surface、evidence ledger、recovery hints、runtime checkpoint 和 compact boundary。
- `QueryEvidenceLedger` 已记录文件快照、工具结果、pending modification、重复读取和失败证据。
- `RuntimeRecoveryHint` 已把 required-tool、未执行 read/command/planning/write intent 等恢复动作从纯文本提示提升为结构化上下文 frame。
- 主循环已按 `prompt -> model -> tool plan -> validate -> normalize -> execute -> observe -> recover -> compact -> continue -> stop` 拆出阶段事件和关键阶段组件。
- required-tool recovery 已具备三层保证：`ToolMode.RequireSpecific`、工具表裁剪、runtime execution guard；`VllmWireLoggingHandler` 还会在 wire 层强制补齐/裁剪并记录 mismatch。
- wire 日志已关联 prompt snapshot 的 `queryId/sessionId/round`，并记录 expected tools、actual wire tools、tool_choice、required-tool mismatch 和 expected-tool mismatch。
- 工具结果预算投影已覆盖长工具输出，保留 tool_use/tool_result pairing，同时用摘要替换 raw output。
- runtime checkpoint 已保存 evidence ledger 和最后一次 prompt snapshot，供 compact/resume/debug 使用。
- 文件快照、重复读取 guidance、observation 输出、write recovery candidate、`hs_write` snapshot/fingerprint 回填已统一从 `EvidenceLedger` 读取；`RecentReadEvidence`、`LastRepeatedReadTargets`、旧 `SeenReadEvidenceKeys` 已从 `QueryRuntimeState` 移除。

已固定的回归覆盖：

- QueryRuntime prompt snapshot、context governance、recovery、required-tool contract、event ordering、tool plan executor、argument normalization、observation processor、checkpoint。
- `VllmWireLoggingHandler` required-tool 注入、expected tools mismatch、snapshot correlation。
- TASK_BUG_001 live LLM 集成测试稳定通过。

后续如果继续演进，主要是质量收敛而不是 TASK_BUG_001 阻断修复：

- 逐步把旧的短 recovery correction 文本完全收敛到结构化 recovery frame。
- 继续把旧的短 recovery correction 文案收敛到结构化 recovery frame，并拆薄 `QueryRuntimeEngine` 的 recovery 分支。
- 针对更多 provider/API mode 增加 wire-level contract tests。
