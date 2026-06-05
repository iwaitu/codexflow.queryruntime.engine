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
5. 如果没有 tool call，以 `NoToolCalls` 终止。
6. 如果存在 tool call 且工具已启用，追加 assistant tool-call message，执行匹配的 `AIFunction`，追加 tool result message，然后进入下一轮。
7. 如果没有提前终止，则在 `MaxRounds` 达到后停止。

runtime 会在一次执行内部增长 message list。初始的 `InitialMessages` 由入口层负责。

## Trace 参数

`runId`、`traceFilePath`、`workspacePath` 是 `ExecuteAsync` 的显式参数，因为当前
engine 会产出 trace，但不负责生成 run id 或选择 workspace。

典型宿主可以在调用前生成这些值：

```csharp
var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
var traceFilePath = Path.Combine(workspacePath, ".qre", "runs", runId, "events.jsonl");
```

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
