> **简体中文** | [English](IQueryRuntimeEngine.md)

# IQueryRuntimeEngine

本文档描述当前仓库里的 runtime engine contract。它已经按当前独立
QueryRuntime 仓库的代码收窄，不再沿用旧 CodexFlow 平台设计稿里的
Gateway、`CodexFlow.Core.Runtime`、`ILLMExecutor` 等描述。

## Runtime 接口

可注入的 runtime 接口位于 `CodexFlow.QueryRuntime.Engine`：

```csharp
public interface IQueryRuntimeEngine
{
    Task<QueryRuntimeResult> ExecuteAsync(
        QueryRuntimeRequest request,
        IQueryRuntimeEventSink eventSink,
        string runId,
        string traceFilePath,
        string? workspacePath,
        CancellationToken ct = default);
}
```

`QueryRuntimeEngine` 已实现这个接口。应用在 .NET 服务中集成 QRE 时，应依赖
`IQueryRuntimeEngine`，而不是直接依赖 concrete class。

```csharp
builder.Services.AddScoped<IQueryRuntimeModelClient, MyModelClient>();
builder.Services.AddScoped<IQueryRuntimeEngine, QueryRuntimeEngine>();
builder.Services.AddScoped<IQueryRuntimeEventSink, MyEventSink>();
```

模型客户端仍由宿主应用提供。内置 CLI 通过 experimental harness 把 provider
配置适配到这个更底层的 runtime contract。

## Request 结构

`QueryRuntimeRequest` 是 engine 层输入，关键字段如下：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `SessionId` | `string` | 会话 ID，用于事件和 telemetry。 |
| `InitialMessages` | `IReadOnlyList<ChatMessage>` | 已组装好的消息，通常是 system prompt + 历史对话 + 当前 user message。 |
| `Options` | `ChatOptions?` | Provider 选项，例如 response format、tools、tool mode。 |
| `MaxRounds` | `int` | 最大 model/tool loop 轮次，默认 `3`。 |
| `EnableTools` | `bool` | 当 `AvailableTools` 非空时是否启用工具执行。 |
| `AvailableTools` | `IReadOnlyList<AIFunction>` | 本次运行暴露给模型的工具。 |
| `RequiredToolName` | `string?` | 可选的必调工具名，满足后恢复普通 tool mode。 |
| `WriteToolNames` | `IReadOnlySet<string>` | 可选的宿主提供写工具名集合，用于 result metadata 中的 workspace-write 调用计数。内置 profile 会从 `write_fs` capability 推导。 |
| `ToolIntervention` | `IQueryRuntimeToolIntervention?` | 可选的宿主策略 hook，可在工具执行前 allow、block-with-feedback 或 fail-closed，并在工具执行后观察结果。 |
| `StopGate` | `IQueryRuntimeStopGate?` | 可选的宿主验证 gate，在 no-tool-call 终止候选被接受前调用。 |
| `MaxStopGateContinuations` | `int` | stop gate 可要求的最大 continuation 轮数，默认 `1`。 |

runtime 不负责把单个 prompt 字符串组装成上下文。入口层需要自行构造最终的
`InitialMessages`。

## 多轮对话用法

应用集成时，应把多轮历史作为 `InitialMessages` 传入：

```csharp
var messages = new List<ChatMessage>
{
    new(ChatRole.System, "You are running inside QRE."),
    new(ChatRole.User, "Summarize this repository."),
    new(ChatRole.Assistant, "It is a QueryRuntime engine repository."),
    new(ChatRole.User, "Now explain how history is passed to the runtime.")
};

var result = await queryRuntimeEngine.ExecuteAsync(
    new QueryRuntimeRequest
    {
        SessionId = sessionId,
        InitialMessages = messages,
        MaxRounds = 3,
        EnableTools = tools.Count > 0,
        AvailableTools = tools,
        Options = chatOptions
    },
    eventSink,
    runId,
    traceFilePath,
    workspacePath,
    ct);
```

这是 .NET 集成的推荐形态。除非调用的是 CLI 或只接受 prompt 的 facade，否则不要把历史对话压平成一个 prompt 字符串。

## 执行语义

每一轮中，engine 会：

1. 发出 `RoundStartedEvent`。
2. 发出 `PromptAssemblySnapshotEvent`，记录当前 message count 和 tool names。
3. 通过 `IQueryRuntimeModelClient` 流式读取模型输出。
4. 收集 assistant text 和结构化 `FunctionCallContent`。
5. 如果没有 tool call，且提供了 `StopGate`，先调用 stop gate。gate 可以 accept、追加宿主 feedback 后 continue、要求下一轮调用指定工具，或 fail closed。
6. 如果存在 tool call 且工具已启用，追加 assistant tool-call message，并在每个匹配的 `AIFunction` 执行前调用 `ToolIntervention`；被允许的工具才会执行，随后追加 tool result message 并进入下一轮。
7. 如果没有提前终止，则在 `MaxRounds` 达到后停止。若 stop gate 要求 continuation 但已无法继续，QRE 会以 terminal detail code fail closed，而不是报告普通成功结束。

runtime 会在一次执行内部增长 message list。初始的 `InitialMessages` 由入口层负责。

## Host 安全 Hook

`CodexFlow.QueryRuntime.Abstractions` 定义了一组 host-neutral hook，用于承载较大宿主已有的策略、guardrail、critique 和验证语义。这些 contract 只依赖 QRE 自有 DTO、`Microsoft.Extensions.AI` 和 BCL 类型，不需要平台专有 runtime 类型。

`IQueryRuntimeToolIntervention.BeforeToolCallAsync` 会收到 tool name、call id、arguments、round、available tool names、required tool name、workspace path 和当前 messages。它必须返回：

| Decision | 行为 |
| --- | --- |
| `Allow` | 执行模型选择的 `AIFunction`。 |
| `BlockWithFeedback` | 不执行工具。QRE 会追加包含策略反馈的 tool-result message，让模型在宿主策略约束下继续。 |
| `FailClosed` | 以 `QueryTerminationReason.FailClosed` 和 terminal detail code 终止本次运行。 |

第一版刻意不支持在执行前静默改写工具参数。后续如果需要参数 rewrite，必须引入单独的 typed decision，并配套 schema validation 和审计事件。

`IQueryRuntimeToolIntervention.AfterToolExecutionAsync` 可以观察 success、result length、result summary，以及失败时的 exception type/message。如果 after-tool hook 自身失败，QRE 会 fail closed，而不是静默忽略宿主 hook 失败。

`IQueryRuntimeStopGate.BeforeStopAsync` 会在 QRE 接受 no-tool-call assistant response 为终态之前运行。它可以返回：

| Decision | 行为 |
| --- | --- |
| `Accept` | 接受 assistant text 作为最终答案。 |
| `Continue` | 把宿主 feedback 作为 user message 追加，然后再跑一轮模型。 |
| `RequireTool` | 追加宿主 feedback，并在下一轮要求调用指定工具（当该工具可用时）。 |
| `FailClosed` | 使用提供的 detail code fail closed。 |

Continuation 同时受 `MaxRounds` 和 `MaxStopGateContinuations` 限制。当 gate 在达到任一限制后仍要求继续，QRE 会返回 `FailClosed`，并给出 `verification_incomplete` 或 `verification_timed_out` 等验证 detail code。

QRE 会发出 `PolicyInterventionDecisionEvent` 和 `StopGateDecisionEvent`。Prompt assembly snapshot 会记录当前 required-tool 名称以及是否已经满足。terminal event 会记录 terminal detail code、zero-tool-call 轮数、continuation 次数、write-tool 调用次数、最后一次 function call 和 required-tool 状态。

`QueryRuntimeResult` 暴露同一组 host-facing metadata：

| 字段 | 说明 |
| --- | --- |
| `TerminalDetailCode` | 机器可读终止细节，例如 `verification_incomplete`、`verification_timed_out` 或 hook failure code。 |
| `ZeroToolCallRounds` | 模型没有返回结构化 tool call 的轮数。 |
| `ContinuationCount` | stop gate 要求追加 continuation 的次数。 |
| `WriteToolCalls` | 被分类为 workspace-write tool 且成功执行的工具调用数。 |
| `LastFunctionCall` | 模型最后一次请求的 function/tool 名称。 |
| `RunDirectory` | 本次运行的 `events.jsonl`、`manifest.json`、`run.json` 和 artifacts 所在目录。 |
| `RequiredToolName` / `RequiredToolSatisfied` | 终止时的 active required-tool 状态。 |
| `ExecutedToolNames` / `SuccessfulToolNames` | 有序工具执行元数据，供 adapter 做策略检查。 |
| `FinalMessages` | QRE 在本次运行中追加 assistant/tool messages 后的最终内存消息历史。 |

## Trace 参数

`runId`、`traceFilePath`、`workspacePath` 是 `ExecuteAsync` 的显式参数，因为当前
engine 会产出 trace，但不负责生成 run id 或选择 workspace。

典型宿主可以在调用前生成这些值：

```csharp
var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
var traceFilePath = Path.Combine(workspacePath, ".qre", "runs", runId, "events.jsonl");
```

experimental host facade 会把 `TraceRoot` 规范化为绝对路径，并把 run artifacts 限定在
`<trace-root>/runs/<run-id>/` 下；`runId` 必须是单个安全路径段。打开
`events.jsonl` 前，它会使用与 workspace file tools 相同的逐段 symlink escape 检查，
确认目标路径仍位于授权 trace root 内。trace root 位于 `.git` 或 secret-looking
路径段下也会被拒绝。

如果宿主希望自己负责 trace 持久化，应使用更底层的 engine contract，并提供自己的
`IQueryRuntimeEventSink`；此时宿主需要在写 artifact 前应用等价的 path containment。
`CodexFlow.QueryRuntime.Abstractions.QueryRuntimePathSafety` 已暴露同一套 root
规范化、containment、symlink escape 和 protected-path helper，供类库消费方复用。

## Facade 接口

`CodexFlow.QueryRuntime.Abstractions` 中现在有两个 facade contract。

`IQueryRuntimeHostEngine` 是面向“作为类库嵌入”的宿主接口，用于让 QRE 替代已有
in-process runtime。它可以接收已组装好的 `ChatMessage` 历史、自定义
`AIFunction` 工具、required tool、provider `ChatOptions`、trace 位置、workspace
路径，以及流式文本回调：

```csharp
public interface IQueryRuntimeHostEngine : IQueryRuntimeEngine
{
    Task<QueryRuntimeResult> RunAsync(
        QueryRuntimeHostRequest request,
        CancellationToken ct = default);
}
```

示例：

```csharp
using CodexFlow.QueryRuntime.Experimental;
using Qre = CodexFlow.QueryRuntime.Abstractions;

Qre.IQueryRuntimeHostEngine runtime =
    new ExperimentalQueryRuntimeHarness(
        new ChatClientExperimentalModelClient(chatClient));

var result = await runtime.RunAsync(
    new Qre.QueryRuntimeHostRequest
    {
        InitialMessages = history,
        WorkspacePath = workspacePath,
        RunId = runId,
        SessionId = sessionId,
        Tools = customTools,
        RequiredToolName = "repo_context",
        Execution = new Qre.QueryRuntimeExecutionOptions { MaxRounds = 4 },
        Options = chatOptions,
        TextDeltaSink = (delta, ct) => StreamToClientAsync(delta, ct)
    },
    ct);
```

`IQueryRuntimeEngine` 仍然保留为更小的 CLI-style prompt facade：

```csharp
Task<QueryRuntimeResult> RunAsync(QueryRuntimeRequest request, CancellationToken ct = default);
```

CodexFlow 这类 in-process 替换场景应优先使用 `IQueryRuntimeHostEngine`。只有在
宿主需要直接控制 event sink 和 trace file 参数时，才使用更底层的
`CodexFlow.QueryRuntime.Engine.IQueryRuntimeEngine`。

## Host Adapter Contract Tests

downstream adapter 不能只用 happy-path tool call 证明 QRE 集成可用。已编译的
`CodexFlow.QueryRuntime.UnitTests/Contracts/HostAdapterContractTestKit.cs`
保留了最小 host-adapter contract 检查：

- pre-tool policy hook 能阻断写工具且底层工具不会执行。
- stop gate 能在接受 terminal answer 前强制 continuation。
- required-tool decision 能触发下一轮并要求指定工具。
- result metadata 能保留 executed/successful/write tool names 和最终 chat history。
- 不安全 run id / trace root 会在打开 trace artifacts 前失败。

`HostAdapterContractTestKitTests` 会在本仓库用 `ExperimentalQueryRuntimeHarness`
执行这些检查。CodexFlow adapter 在把 QRE 从实验 backend 推进到更大范围前，应使用
自己的 `IQueryRuntimeHostEngine` factory 和 scripted model provider 实现等价测试。
