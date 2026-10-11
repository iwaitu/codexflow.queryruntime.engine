# QRE 面向 ia-engine 嵌入集成的增强计划

日期：2026-10-11
目标版本：`0.24.0`（开发期间发布 `0.24.0-preview.N`）
状态：Q1–Q5、Q7、Q8 已完成；Q9 版本定为 `0.24.0`，PR 合并后由 `release.yml` 自动发布；Q6 推迟到 0.24.x

> 本文档是一份**可直接执行的工作计划**。执行会话请从“0. 执行须知”开始，
> 按“5. 执行顺序”逐项完成，并在“7. 状态跟踪”中更新进度。

---

## 0. 执行须知

1. **所有新能力必须 opt-in**。不传新参数时，QRE 的现有行为（包括 fail-closed 语义、
   重试行为、上下文裁剪）必须保持 byte-identical；现有单元测试、集成测试、
   `scripts/queryruntime-baseline-gate.sh`、`scripts/qre-aot-gate.sh` 必须继续通过。
2. **分层约束**：
   - `CodexFlow.QueryRuntime.Protocol` 与 `CodexFlow.QueryRuntime.Engine/V2` 是 provider-free 的，
     **不得引用 `Microsoft.Extensions.AI`**（目前 `Engine/V2/*.cs` 中没有任何 MEAI using，保持这一点）。
   - 依赖 MEAI 的代码放在 `CodexFlow.QueryRuntime.Models`。
   - Protocol 与 Models 是 `IsAotCompatible=true`。新增可序列化类型必须加入对应的
     `JsonSerializerContext`（如 `QueryRuntimeProtocolJsonContext`、`RuntimeAudit.cs`、
     `RuntimeCheckpoint.cs` 中的 context），不得引入反射序列化。
3. **回放确定性**：任何由时钟、外部状态决定的分支（例如按耗时收尾），必须作为审计事件记录，
   严格回放（strict replay）时读取事件而不是读取时钟。
4. **Schema 版本**：`RuntimeAuditSchema.CurrentVersion`、`RuntimeCheckpointSchema.CurrentVersion`、
   `QueryRuntimeProtocolSchema.CurrentVersion` 目前都是 `1`。如果新增字段/事件类型导致旧读取器
   无法读取新数据，或新读取器需要区分新旧数据，按 ADR-004 / ADR-008 的约定处理：
   优先做“新增可选字段 + 读取端容忍缺省”，确实需要时再升版本，且必须保留 v1 读取能力。
   每项工作结束时在 PR 描述里写明是否改动了 schema 版本以及原因。
5. **兼容性**：codexflow 主项目目前固定在旧 preview 包上，本计划不要求其升级，但 0.24.0 的
   破坏性改动（如果有）必须写入 `CHANGELOG` 与迁移说明。
   优先使用 `init` 属性而不是新增 record 位置参数，避免二进制破坏。
6. **本机环境提示**：本机 `git` 命令可能因 Xcode license 未同意而只输出警告、不执行。
   如遇到，请提醒用户先在终端执行 `sudo xcodebuild -license`。
7. 每完成一项：补测试 → 跑全量测试 → 更新本文件“7. 状态跟踪” → 再开始下一项。

---

## 1. 背景

ia-engine（`/Users/iwaitu/github/ia-engine`）是规划审查引擎。其材料包审查（bundler）对每条规则
运行一个“带只读工具的结构化抽取 / 评分”LLM 会话：

- 入口：`ia-engine/src/IA.Engine.Core/Services/BundleReview/BundleLlmRuleExecutor.cs` 的 `ExecuteChatAsync`
- 循环实现：`ia-engine/src/IA.Engine.Core/Services/LlmChatService.cs`（手写 agent loop）
- 工具：`BundleDocumentToolset`（10 个只读文档工具）+ `PlanningIndicatorLibrary`（4 个）
- 并发：多条规则并行（默认 3），每条规则是独立会话
- 输出：最终一段 JSON 文本，随后由 executor 做 schema / 证据校验

计划把 `LlmChatService` 的工具循环替换为 QRE V2（`AgentRuntime` + `MeaiRuntimeModelClient`），
以获得审计轨迹、严格回放、出站诊断与统一的工具管线。
对照 ia-engine 现有行为，QRE 目前存在以下缺口，本计划逐项补齐。

---

## 2. 差距清单（附代码位置）

| # | ia-engine 现有行为 | QRE 现状 | 代码位置 | 对应工作项 |
|---|---|---|---|---|
| G1 | 宿主以 `AIFunction` 提供工具 | `AIFunction → IRuntimeTool` 适配器是 Experimental 中的 private 类 | `Experimental/ExperimentalV2ToolComposition.cs:234` `AIFunctionRuntimeTool` | Q1 |
| G2 | JSON Schema 结构化输出；工具轮不带 `response_format`，无工具轮再带上 | `RuntimeModelParameters` 只有 `RequireJsonObject` | `Protocol/ModelContracts.cs` `RuntimeModelParameters` | Q2 |
| G3 | 工具调用达到 12 次或耗时达到超时的 85% 时，追加一轮“禁止工具”的收尾调用 | 无此能力；Step 预算耗尽于工具观测之后时直接 `MaxSteps` 失败（无最终答案） | `Engine/V2/RuntimeAgentLoop.cs:404` `step_budget_exhausted` | Q3 |
| G4 | 最终 JSON 不合法时，以“禁止工具 + 带格式”追加一次恢复调用 | `TerminationPolicy.Continue` 只能追加反馈，下一步仍带工具、无法切换格式 | `Engine/V2/RuntimeAgentLoopContracts.cs` `RuntimeTerminationDecision` | Q3（Q3b） |
| G5 | 带抖动的指数退避重试；401 视为致命错误 | 只重试 `Retryable` 的 `RuntimeModelClientException`，且**无退避**（立即 `continue`）；`MeaiRuntimeModelClient` 不做异常分类，一般异常落为不可重试的 `model_stream_failed` | `Engine/V2/RuntimeAgentLoop.cs:573-604`；`Models/MeaiRuntimeModelClient.cs` | Q4 |
| G6 | 可配置流式/非流式；流式返回为空时改用非流式重试同一轮 | `MeaiRuntimeModelClient` 只走 `GetStreamingResponseAsync` | `Models/MeaiRuntimeModelClient.cs:79` | Q5 |
| G7 | 模型返回空函数名时，让模型以 JSON 重述工具调用后继续 | 校验器直接 fail-closed：`invalid_tool_call_identity` | `Protocol/RuntimeModelStreamValidator.cs:63` | Q6 |
| G8 | 首条 user 消息是很大的 JSON（含文档片段）；`OpenDocument` 单次最多返回 40000 字符 | 默认 `MaxContextTokens=32k`、`MaxItemTokens=8k`、`MaxToolResultTokens=4k`，会静默裁剪证据 | `Engine/V2/RuntimeContext.cs:79-99` | Q7 |
| G9 | — | 缺少“宿主工具 + 结构化抽取 + 收尾”的嵌入示例 | `examples/` | Q8 |

---

## 3. 设计约束与原则

- **opt-in**：新参数缺省时等价于旧行为。
- **审计可见**：所有影响模型请求的决策（实际生效的 response format、收尾触发、重试原因、
  工具调用修复）都要进入审计，严格回放能复现。
- **决策放在 Engine，映射放在 Models**：例如“这一步是否应用 response format”由 Engine
  在构造 `RuntimeModelRequest` 时决定（审计记录的是实际生效的请求），Models 只负责把它映射为
  `ChatResponseFormat`。
- **时间源**：统一使用 `AgentRuntime` 构造函数已有的 `TimeProvider`（`AgentRuntime.cs:111`），
  便于测试中用 `FakeTimeProvider` 驱动截止时间和退避。

---

## 4. 工作项详情

### Q1（P0）公开 `AIFunction` 工具适配器

**目标**：宿主可以直接把 `AIFunction` 注册为 V2 工具，并声明副作用、幂等性、并发与限额。

**位置**：新增 `CodexFlow.QueryRuntime.Models/Tools/RuntimeAIFunctionTool.cs`（依赖 MEAI，所以放在 Models）。

**API 草案**：

```csharp
public sealed record RuntimeAIFunctionToolOptions
{
    public string Version { get; init; } = "1.0.0";
    public RuntimeToolSideEffect SideEffect { get; init; } = RuntimeToolSideEffect.ReadOnly;
    public RuntimeToolIdempotency Idempotency { get; init; } = RuntimeToolIdempotency.Idempotent;
    public RuntimeToolConcurrency Concurrency { get; init; } = RuntimeToolConcurrency.ParallelSafe;
    public RuntimeToolLimits Limits { get; init; } = RuntimeToolLimits.Default;
    public IReadOnlySet<string> Capabilities { get; init; } = ...;
    public RuntimeSandboxRequirements Sandbox { get; init; } = RuntimeSandboxRequirements.None;
    public string? CanonicalName { get; init; }   // 默认用 function.Name
}

public sealed class RuntimeAIFunctionTool : IRuntimeTool
{
    public RuntimeAIFunctionTool(AIFunction function, RuntimeAIFunctionToolOptions? options = null);
    public static IReadOnlyList<IRuntimeTool> FromFunctions(IEnumerable<AIFunction> functions, RuntimeAIFunctionToolOptions? options = null);
}
```

**行为**：
- 参数：`invocation.NormalizedArguments` 转成 `AIFunctionArguments`（复用现有 `ToObjectDictionary` 逻辑）。
- 返回值：`string` 原样透传；`JsonElement` 输出 raw text；其他对象使用 `function.JsonSerializerOptions`
  序列化（保持 AOT 安全，不要自己 new 反射版 options）。中文不得被转义为 `\uXXXX`。
- 异常：调用方取消时重新抛出；其他异常转成 `RuntimeToolResult(success:false, Category=ToolFailed)`，
  模型能看到错误并继续（与 ia-engine 现状一致）。
- `ExperimentalV2ToolComposition` 中的 private `AIFunctionRuntimeTool` 改为复用该类型（保持其 30 分钟 / 800000 字节限额与
  `output truncated` 判定不变）。

**测试**：string / 对象 / JsonElement 返回值；中文不转义；异常转为失败结果；取消传播；
Experimental 现有测试全部通过。

---

### Q2（P0）结构化输出成为一等参数

**目标**：Runtime 层可以声明 JSON Schema 输出，并可以指定“只在不带工具的 Step 上生效”。

**Protocol 新增**（`Protocol/ModelContracts.cs`）：

```csharp
public enum RuntimeResponseFormatKind { Text = 0, JsonObject = 1, JsonSchema = 2 }
public enum RuntimeResponseFormatScope { AllSteps = 0, ToolFreeStepsOnly = 1 }

public sealed record RuntimeResponseFormat(
    RuntimeResponseFormatKind Kind,
    string? SchemaName = null,
    string? SchemaDescription = null,
    JsonElement? Schema = null,
    RuntimeResponseFormatScope Scope = RuntimeResponseFormatScope.AllSteps);

// RuntimeModelParameters 增加 init 属性（不新增位置参数）：
public RuntimeResponseFormat? ResponseFormat { get; init; }
```

**规则**：
- `RequireJsonObject = true` 与 `ResponseFormat` 同时设置 → 请求校验失败（明确错误码）。
  `RequireJsonObject` 保留，语义等价于 `Kind = JsonObject, Scope = AllSteps`，可标 `[Obsolete]`（注意不要让 warning-as-error 打断构建）。
- `Kind = JsonSchema` 时 `Schema` 必须是 object，且通过现有 `RuntimeToolArgumentNormalizer.ValidateSchema` 同等级别的基本校验。
- **生效决策在 Engine**：`RuntimeAgentLoop` 构造每个 Step 的 `RuntimeModelRequest` 时，若
  `Scope = ToolFreeStepsOnly` 且该 Step 暴露的工具数 > 0，则把请求中的 `ResponseFormat` 置空。
  审计 `model_request` 记录的是实际生效值。
- record 相等性：`JsonElement` 按引用比较，若有代码依赖 `RuntimeModelParameters` 相等性（checkpoint 兼容性校验等），
  需改为比较 schema 的规范化 SHA-256 摘要。
- **Models 映射**（`MeaiRuntimeModelClient.Prepare`）：`JsonObject → ChatResponseFormat.Json`；
  `JsonSchema → ChatResponseFormat.ForJsonSchema(schema, name, description)`。
  Runtime 参数非空时覆盖 optionsFactory 返回的 `ResponseFormat`；为空时保留 factory 的值（兼容现有宿主）。
- **审计**：`model_request` 载荷增加 `responseFormatKind` 与 `responseFormatSchemaSha256`；
  PublicRedacted 模式下不落 schema 原文。
- **Checkpoint**：`RuntimeCheckpoint.cs:96` 已序列化 `ModelParameters`，确认新字段能写入、
  旧 checkpoint（无该字段）能读取。
- **出站诊断**：更新 `Models/Diagnostics/QreSemanticProjector.cs:99` 附近对 response format 的投影，
  使 `json_schema` 能被识别；`response_format_dropped` 检测覆盖 json_schema；
  CLI 的 `QreDiagnosticsRebuilder` / `QreDiagnosticsAnalyzer` 相应更新。
- 更新 `examples/StructuredOutputsJsonSchema` 使用新参数。

**测试**：两种 Scope 下各 Step 的实际请求；冲突校验；序列化 / checkpoint 往返；旧轨迹严格回放；
诊断识别 json_schema 与 dropped。

---

### Q3（P0）预算驱动的无工具收尾；Q3b `ContinueWithoutTools`

**目标**：在工具调用次数、截止时间或 Step 预算即将用尽时，自动进入一个“禁止工具、要求给出最终答案”的收尾 Step，
而不是以 `MaxSteps` 失败。

**API 草案**（`Engine/V2/RuntimeAgentLoopContracts.cs`）：

```csharp
public enum RuntimeFinalizationToolCallHandling { IgnoreAndUseText = 0, FailClosed = 1 }

public sealed record RuntimeToolFinalizationPolicy
{
    public int? AfterToolCalls { get; init; }           // 累计工具调用数达到即触发
    public TimeSpan? Deadline { get; init; }            // 相对 Turn 开始
    public double DeadlineFraction { get; init; } = 0.85;
    public bool ReserveFinalStep { get; init; } = true; // 只剩最后一个 Step 时强制收尾
    public required string Instruction { get; init; }   // 追加为 user 消息
    public RuntimeFinalizationToolCallHandling ToolCallHandling { get; init; }
        = RuntimeFinalizationToolCallHandling.IgnoreAndUseText;
}

// RuntimeAgentLoopRequest 新增：
public RuntimeToolFinalizationPolicy? ToolFinalization { get; init; }
```

**行为**：
- 检查时机：每个工具批次提交（`ToolBatchCommitted`）之后、开始下一 Step 之前。
- 触发条件（任一满足）：`ToolCallCount >= AfterToolCalls`；`elapsed >= Deadline * DeadlineFraction`（用 `TimeProvider`）；
  `ReserveFinalStep && 下一 Step 是最后一个`。
- 触发后：向历史追加 `Instruction`（User 消息）；收尾 Step **不暴露任何工具**（覆盖 `ToolCatalogSelector`），
  并按 Q2 规则应用 response format（因为是无工具 Step）。
- 收尾 Step 中模型仍返回工具调用：`IgnoreAndUseText` → 丢弃这些调用、记录 warning `finalization_tool_calls_ignored`，
  取正文；正文为空则 fail-closed（`finalization_empty`）。`FailClosed` → 直接 fail-closed。
- 每个 Turn 最多触发一次收尾。
- **状态与审计**：在 reducer 的 Turn progress 中记录 `FinalizationTriggered` 及原因，写入 checkpoint；
  新增审计事件 `ToolFinalizationTriggered { reason: tool_call_budget | deadline | step_reserve, toolCallCount, elapsedMs }`。
  严格回放读取该事件决定是否进入收尾，不读时钟。
- 恢复（resume）：从 checkpoint 恢复时已触发的收尾不得重复触发。

**Q3b `ContinueWithoutTools`**：

```csharp
public static RuntimeTerminationDecision ContinueWithoutTools(string feedback);
```

- 与 `Continue` 相同，但下一 Step 不暴露工具，并按 Q2 规则应用 response format。
- 用途：宿主在 TerminationPolicy 中发现最终 JSON 不合法时，要求模型“禁止工具、按格式重新输出”。
- 受 `MaxContinuations` 与 Step 预算约束；审计中的终止决策要区分 `continue` 与 `continue_without_tools`。

**测试**：三种触发条件分别触发；收尾 Step 无工具且带 response format；收尾中模型仍调用工具的两种处理；
带截止时间触发的轨迹严格回放一致；checkpoint 后恢复不重复触发；`ContinueWithoutTools` 端到端；
未配置时行为与现状完全一致（`step_budget_exhausted` 仍按原样出现）。

---

### Q4（P0）模型错误分类与退避重试

**目标**：可重试的传输错误按退避策略重试，认证错误明确归类。

**Models：错误分类器**（新增 `Models/QreModelErrorClassifier.cs`，在 `MeaiRuntimeModelClient`
的 `StreamCoreAsync` / `StreamObservedAsync` 中包装 SDK 抛出的异常，转为 `RuntimeModelClientException`）：

| 异常 | Category | Retryable |
|---|---|---|
| HTTP 429 | ProviderRateLimit | 是 |
| HTTP 408 / 500 / 502 / 503 / 504 | ProviderTransport | 是 |
| HTTP 401 / 403，或消息含 `Unauthorized` / `Authentication failed` | ProviderAuthentication | 否 |
| HTTP 400 / 404 / 422 | ProviderProtocol | 否 |
| 无状态码的 `HttpRequestException`、`IOException`、`SocketException` | ProviderTransport | 是 |
| `TaskCanceledException` / `OperationCanceledException` 且调用方 token **未**取消（HTTP 超时） | ProviderTransport（code `provider_timeout`） | 是 |
| 调用方取消 | 原样抛出，不分类 | — |
| 其他 | ProviderTransport（code `model_stream_failed`） | 否（保持现状） |

- 需要先确认 `VllmChatClient 2.0.25` 实际抛出的异常类型与状态码载体，必要时为其单独分支。
- `RuntimeError` 增加可选 `RetryAfter`（`TimeSpan?`，init 属性）；能从响应头取到时填充。

**Engine：退避策略**：

```csharp
public sealed record RuntimeRetryPolicy
{
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    public double JitterRatio { get; init; } = 0.2;
    public bool RespectRetryAfter { get; init; } = true;
}
// RuntimeAgentLoopRequest 新增：
public RuntimeRetryPolicy? ModelRetryPolicy { get; init; }   // null = 现状（立即重试）
```

- 位置：`RuntimeAgentLoop.cs:573` 的 retry `catch` 分支，`continue` 前按策略等待（`TimeProvider` 驱动，响应取消）。
- 保持“只在本次尝试尚未产出任何事件时重试”的约束不变。
- 审计：每次重试记录 warning（错误码、第几次尝试、等待时长）。
- 抖动随机源可注入，测试中固定。

**测试**：分类表逐项；重试延迟计算（含上限、Retry-After）；已产出事件后不重试；
认证错误不重试并以 ProviderAuthentication 终止；未配置策略时与现状一致。

---

### Q5（P1）非流式模式与空流降级

**位置**：`Models/MeaiRuntimeModelClient.cs`，新增构造重载接收 `MeaiRuntimeModelClientOptions`。

```csharp
public enum MeaiStreamingMode { Streaming = 0, NonStreaming = 1, StreamingWithNonStreamingFallbackOnEmpty = 2 }
public sealed record MeaiRuntimeModelClientOptions
{
    public MeaiStreamingMode StreamingMode { get; init; } = MeaiStreamingMode.Streaming;
}
```

- `NonStreaming`：调用 `GetResponseAsync`，用 `response.ToChatResponseUpdates()` 再走现有
  `MeaiRuntimeProtocolAdapter.ToProtocolEvents`，保证 finish reason、usage 正确传递。
- `StreamingWithNonStreamingFallbackOnEmpty`：在出现第一个实质内容（text / reasoning / tool call）之前，
  先缓冲 usage / warning 事件；若流结束仍无实质内容，丢弃缓冲，改用非流式重发一次，并输出 warning `empty_stream_fallback`。
  一旦已输出实质内容，不再降级。
- 诊断路径（`StreamObservedAsync`）同样支持三种模式，诊断记录能区分 streaming / non-streaming 调用。

**测试**：三种模式的事件序列；空流降级；有内容时不降级；诊断记录。

---

### Q6（P2，可推迟到 0.24.x）畸形工具调用修复

**背景**：ia-engine 遇到 provider 返回空函数名（VllmChatClient 抛出含 `empty function.name` 的
`InvalidOperationException`）时，会让模型以 JSON `{"tool_calls":[{"name":...,"arguments":{}}]}`
重述一次工具调用，校验工具名已注册后再执行。QRE 目前会 fail-closed。

**设计方向**：
- 触发点：(a) Q4 分类器把上述异常归为 `ProviderProtocol / malformed_tool_call`；
  (b) 校验器 `invalid_tool_call_identity`。两者都在“本次尝试尚未产生可执行工具调用”时进入修复流程。
- 新增 `RuntimeToolCallRepairPolicy`（opt-in）：包含修复提示词模板、每 Step 最多修复次数（默认 1）。
  修复请求通过同一个 `IRuntimeModelClient` 发出，使用 `JsonObject` 格式、不暴露工具；
  解析结果只能是“恰好一个已注册工具调用”或“最终答案 JSON”，否则 fail-closed。
- 修复得到的工具调用照常进入工具管线（策略、审批、审计不绕过）。
- 审计新增 `ToolCallRepairAttempted { trigger, outcome }`；严格回放可复现。

> 若本项推迟，ia-engine 会在 `IChatClient` 层用 `DelegatingChatClient` 做过渡修复，QRE 侧无需改动。

---

### Q7（P0）上下文预算预设与截断可观测性

**目标**：大上下文宿主可以方便地放宽预算，并能在证据被截断时得知或拒绝。

- 文档：在 `docs/queryruntime-technical-guide.zh-CN.md` 中写清 `RuntimeContextOptions` 各字段含义、
  默认值的适用场景、与模型上下文窗口的关系。
- 预设：`RuntimeContextOptions.ForModelWindow(int maxContextTokens)`，按比例放大
  `MaxItemTokens`、`MaxToolResultTokens`、`LargeToolResultTokens`（比例在实现时根据现有压缩逻辑确定，并写入文档）。
- 截断策略：

  ```csharp
  public enum RuntimeContextTruncationPolicy { Allow = 0, FailClosedOnInitialMessages = 1, FailClosedOnAny = 2 }
  // RuntimeContextOptions 新增 init 属性 TruncationPolicy，默认 Allow
  ```

  当首条消息（`InitialMessages`）或任意条目被替换、摘要、省略时，按策略 fail-closed（错误码 `context_truncation_rejected`），
  并确保无论哪种策略都会发出带条目 id 的 `RuntimeContextEvent`（`ItemReplaced` / `ContextCompacted`）。
- 核对 `RuntimeTokenEstimator.Estimate`（`RuntimeContext.cs:631`，UTF-8 字节数 / 4）对中文的估算偏差：
  中文 1 字 = 3 字节，约 0.75 token/字，可能低估部分 tokenizer 的实际值。在文档中说明，
  可选地允许宿主注入估算系数；不改默认估算（避免影响回放）。

**测试**：预设数值；三种截断策略；截断事件包含条目 id；默认行为不变。

---

### Q8（P1）嵌入式结构化抽取示例

- 新增 `examples/EmbeddedStructuredExtraction`：模拟 ia-engine 用法。
  - 2–3 个只读 `AIFunction` 工具（如“列出文档 / 打开文档 / 查找关键词”，数据为内置样例），用 `RuntimeAIFunctionTool` 注册。
  - `ResponseFormat = JsonSchema, Scope = ToolFreeStepsOnly`。
  - `ToolFinalization`（次数 + 截止时间）。
  - 一个 TerminationPolicy：JSON 不合法时 `ContinueWithoutTools` 一次。
  - `ModelRetryPolicy`、`StreamingWithNonStreamingFallbackOnEmpty`。
  - 审计写到宿主指定目录（`RuntimeJsonlAuditStore`），演示 trace-data 级别选择。
  - 无 `--live` 时使用脚本化的离线模型客户端完成整条链路（包含收尾与 JSON 恢复），并纳入 `scripts/test-examples.py`。
- 更新 `examples/README.md` 与 README（中英文）的“嵌入 .NET 应用”章节。

---

### Q9（P0）发布

1. 开发期间：`scripts/qre-pack-nuget.sh` 打包到本地目录（例如 `~/local-nuget`），供 ia-engine 先行集成，
   版本 `0.24.0-preview.N`。
2. 门禁：全量单元/集成测试、`queryruntime-baseline-gate.sh`、`qre-aot-gate.sh`、`qre-aot-smoke.sh`、
   `qre-dependency-gate.sh`、`qre-package-smoke.sh`、`scripts/test-examples.py`。
3. 文档：`CHANGELOG.md`（仓库目前没有，需新建）、迁移说明（Protocol 新字段、`RequireJsonObject` 过时、schema 版本是否变化）、
   新 ADR：`docs/adr/ADR-010-embedded-structured-extraction.md`（记录 Q2/Q3/Q4 的设计决策与回放确定性约定）。
4. `Directory.Build.props` 中 `VersionPrefix` 改为 `0.24.0`；preview 稳定后发布正式版到 nuget.org。

---

## 5. 执行顺序

```
第 1 批（相互独立，可任意顺序）：Q1  Q2  Q4  Q7
第 2 批：Q3（依赖 Q2）  Q5
第 3 批：Q8（依赖 Q1–Q5、Q7）
第 4 批：Q9 发布 0.24.0-preview.1
可选：  Q6 → 0.24.x
```

建议的实际顺序：**Q1 → Q2 → Q3 → Q4 → Q7 → Q5 → Q8 → Q9**（Q6 视时间）。
Q1、Q2、Q3 完成后即可先打一个本地 preview 包，ia-engine 可以开始接入主流程。

## 6. 不在本计划范围内

- ia-engine 侧的改动（`IBundleLlmConversationRunner`、`QreBundleConversationRunner`、对照验证等）在 ia-engine 仓库单独进行。
- codexflow 主项目升级到 0.24.0。
- 多进程 / 分布式恢复（ADR-001 / ADR-008 的范围不变）。

## 7. 状态跟踪

| 项 | 优先级 | 状态 | 是否改动 schema 版本 | 备注 |
|---|---|---|---|---|
| Q1 公开 AIFunction 工具适配器 | P0 | 已完成 | 否 | `Models/Tools/RuntimeAIFunctionTool.cs`；额外增加 `Description`、`FailureCode`、`TruncationDetector` 选项以保持 Experimental 行为不变（`compatibility_tool_failed`、`output truncated` 判定）；Experimental 新增对 Models 的项目引用；`JsonElement` 字符串值按原值输出（与旧 `ToString()` 一致），对象/数组保留 raw text，仅含 `\uXXXX` 转义时以宽松编码重写；全量单测 584 通过，AOT gate 通过 |
| Q2 结构化输出一等参数 | P0 | 已完成 | 否 | `RuntimeModelParameters.ResponseFormat`（init，`WhenWritingNull` 省略，未设置时请求/审计/checkpoint 字节不变）；`RuntimeResponseFormat` 结构化相等 + `ComputeSchemaSha256()`（紧凑 UTF-8 JSON）；校验失败抛 `RuntimeRequestValidationException : ArgumentException`（`response_format_conflict` / `response_format_invalid`），schema 校验在工具 schema 基础上放宽为允许类型联合、`$defs`、`anyOf` 等；`model_request` 审计新增 `responseFormatKind` / `responseFormatSchemaSha256`，公开审计只记 kind；回放校验二者与请求一致（`audit_response_format_mismatch`）；诊断投影/分析/重建识别 `json_schema` 与降级；`RequireJsonObject` 未加 `[Obsolete]`（避免全仓告警），改为文档说明；示例已切换 |
| Q3 无工具收尾 + ContinueWithoutTools | P0 | 已完成 | 否（新增可选字段与审计事件类型，v1 读取端兼容） | `RuntimeAgentLoopRequest.ToolFinalization`；触发后收尾为“粘性”：本 Turn 之后所有 Step 都不暴露工具；截止时间相对 Turn `CreatedAt`；`RuntimeTurnProgress` 新增 `FinalizationReason`（null 省略）与 `NextStepWithoutTools`（`WhenWritingDefault` 省略），未启用时 checkpoint 字节不变；收尾决策在保存 `ToolBatchCommitted` checkpoint 之前写入状态，恢复不重复触发；`RuntimeCheckpointRequestSnapshot` 增加 `ToolFinalization`（参与指纹）；新审计事件 `ToolFinalizationTriggered`（`tool_finalization`）与 `TerminationDecided`（`termination_decision`，仅记录 `continue_without_tools`，保持旧决策的审计不变）；`RuntimeRecordedReplay` 校验收尾/无工具 Step 不再暴露工具（`audit_tool_free_step_exposed_tools`）；无工具 Step 中的工具调用：`IgnoreAndUseText` 丢弃并告警（`finalization_tool_calls_ignored`，`ContinueWithoutTools` Step 为 `tool_free_step_*`），无正文 `finalization_empty`，`FailClosed` 为 `finalization_tool_calls_rejected`；`RecordContinuation` 新增重载而非可选参数（避免二进制破坏） |
| Q4 错误分类与退避重试 | P0 | 已完成 | 否 | 实测 `VllmChatClient 2.0.25` 对所有 HTTP 错误状态都抛 `InvalidOperationException("Vllm error: <body>")`，不带状态码；因此新增 `QreModelHttpErrorCaptureHandler`（DelegatingHandler，经 AsyncLocal 记录本次模型调用的状态码与 Retry-After / retry-after-ms），分类器优先用异常自带状态码、其次捕获的状态码、再次解析 `EnsureSuccessStatusCode` 消息；网络错误为 `HttpRequestException(SocketException)`，HttpClient 超时为 `TaskCanceledException(TimeoutException)`。为满足“opt-in”，分类通过 `MeaiRuntimeModelClientOptions.ClassifyProviderErrors`（默认 false）开启，新增构造重载；错误码：`provider_rate_limited` / `provider_server_error` / `provider_timeout` / `provider_authentication_failed` / `provider_request_rejected` / `provider_transport_failed` / `malformed_tool_call` / `model_stream_failed`；`RuntimeError.RetryAfter`（init，null 省略）；`RuntimeRetryPolicy.ComputeDelay` 指数退避 + 可注入抖动 + Retry-After（上限 MaxDelay）；重试 warning `model_retry` 写入成功 Step 输出（仅配置策略时）；等待使用 `TimeProvider` 且可取消 |
| Q5 非流式与空流降级 | P1 | 已完成 | 否 | `MeaiRuntimeModelClientOptions.StreamingMode`（与 Q4 的 `ClassifyProviderErrors` 同一个 options 记录）；非流式通过内部 `IChatClient` 视图把 `GetResponseAsync` 结果经 `ToChatResponseUpdates()` 复用流式事件映射；降级模式缓冲 usage/warning/completed 直到首个实质内容，空流则丢弃缓冲并以非流式重发一次，先输出 warning `empty_stream_fallback`；诊断路径下降级会产生两次独立 model call，`adapter_prepared` 的 `stream` 分别为 `true`（`GetStreamingResponseAsync`）与 `false`（`GetResponseAsync`）；默认 `Streaming` 路径行为不变 |
| Q6 畸形工具调用修复 | P2 | 推迟到 0.24.x | — | 本轮未实现。Q4 分类器已把 `empty function.name` / `function name is empty` 归类为 `ProviderProtocol / malformed_tool_call`（不可重试），可作为后续修复流程的触发点；ia-engine 可先按计划在 `IChatClient` 层用 `DelegatingChatClient` 过渡 |
| Q7 上下文预算预设与截断策略 | P0 | 已完成 | 否 | `RuntimeContextOptions.ForModelWindow(W)` 沿用默认比例（item 1/4、tool result 1/8、large tool result 1/16、summary 1/16，`ForModelWindow(32000) == Default`）；`TruncationPolicy`（默认 `Allow`）在每个 Step 投影准备后、调用模型前检查上一 Step 以来的上下文事件，`context_truncation_rejected`（`ContextOverflow`，FailClosed）；计入截断的是预算驱动的硬上限、大结果替换与压缩，不含空文本/重复/孤立项等协议归一化；未改变既有事件 kind（硬上限仍为 `HistoryNormalized`，均已带条目 id），以保持默认输出不变；文档见技术指南 §5.11（含中文估算偏差说明）；未实现估算系数注入（计划中为可选项，避免影响回放） |
| Q8 嵌入式结构化抽取示例 | P1 | 已完成 | 否 | `examples/EmbeddedStructuredExtraction`：离线模式用挂在真实 `MeaiRuntimeModelClient` 后的脚本化 `IChatClient`，覆盖 503 分类重试、空流降级、预算收尾、schema 仅在无工具 Step、`ContinueWithoutTools` JSON 修复、宿主目录审计（`--trace-data public/sanitized/private`）与纯数据回放；已纳入 `scripts/test-examples.py`（11 项全部通过）；更新 `examples/README.md`、README 中英文“嵌入 .NET 应用”章节与示例列表 |
| Q9 发布 0.24.0 | P0 | 发布准备完成，合并后自动发布 | 否（三个 schema 版本均仍为 1） | 按维护者决定，`Directory.Build.props` 的 `VersionPrefix` 直接设为 `0.24.0`（跳过 nuget.org 预览版）：`release.yml` 会在 main 的 CI 成功后自动发布该版本。开发期间曾以 `0.24.0-preview.1` 打包到 `~/local-nuget` 供 ia-engine 先行集成。门禁：`queryruntime-baseline-gate.sh --full`（652 单测、AOT publish + native smoke）、`qre-dependency-gate.sh`、`qre-package-smoke.sh`、`scripts/test-examples.py`（11 项）全部通过；集成测试 15 项均为环境门控跳过（Docker / 真实 provider 未启用）。新增 `CHANGELOG.md`（含迁移说明）与 ADR-010；README 包版本与 `QreCliSmokeTests` 版本断言更新为 `0.24.0` |
