> **English** | [简体中文](IQueryRuntimeEngine.zh-CN.md)

# IQueryRuntimeEngine

This document describes the current runtime engine contract in this repository.
It is intentionally narrower than the older CodexFlow platform design notes.

## Runtime Interface

The injectable runtime interface lives in `CodexFlow.QueryRuntime.Engine`:

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

`QueryRuntimeEngine` implements this interface. Applications that embed QRE in a
.NET service should depend on `IQueryRuntimeEngine`, not the concrete class.

```csharp
builder.Services.AddScoped<IQueryRuntimeModelClient, MyModelClient>();
builder.Services.AddScoped<IQueryRuntimeEngine, QueryRuntimeEngine>();
builder.Services.AddScoped<IQueryRuntimeEventSink, MyEventSink>();
```

The model client is still supplied by the host application. The built-in CLI
uses the experimental harness to adapt provider settings into this lower-level
runtime contract.

## Request Shape

`QueryRuntimeRequest` is the engine-level input. The important fields are:

| Field | Type | Description |
| --- | --- | --- |
| `SessionId` | `string` | Stable session identifier for events and telemetry. |
| `InitialMessages` | `IReadOnlyList<ChatMessage>` | Pre-assembled messages, usually system prompt + conversation history + current user message. |
| `Options` | `ChatOptions?` | Provider options such as response format, tools, and tool mode. |
| `MaxRounds` | `int` | Maximum model/tool loop rounds. Defaults to `3`. |
| `EnableTools` | `bool` | Enables tool execution when `AvailableTools` is non-empty. |
| `AvailableTools` | `IReadOnlyList<AIFunction>` | Tools exposed to the model for this run. |
| `RequiredToolName` | `string?` | Optional tool that must be requested before normal tool mode is restored. |

The runtime does not assemble prompts from a single string. Entry layers are
responsible for building the final `InitialMessages` list.

## Multi-Turn Usage

For application integration, pass multi-turn conversation history as the
`InitialMessages` list:

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

This is the preferred shape for .NET integrations. Avoid flattening prior turns
into one prompt string unless the caller is using the CLI or another facade that
only accepts a prompt.

## Execution Semantics

On each round, the engine:

1. Emits a `RoundStartedEvent`.
2. Emits a `PromptAssemblySnapshotEvent` with the current message count and tool names.
3. Streams model updates through `IQueryRuntimeModelClient`.
4. Collects assistant text and structured `FunctionCallContent` values.
5. If no tool calls are present, terminates with `NoToolCalls`.
6. If tool calls are present and tools are enabled, appends the assistant tool-call message, executes matching `AIFunction` tools, appends tool result messages, and continues.
7. Stops at `MaxRounds` if the loop does not terminate earlier.

The message list grows inside the runtime during one execution. The original
`InitialMessages` list is the entry-layer responsibility.

## Trace Parameters

`runId`, `traceFilePath`, and `workspacePath` are explicit parameters on
`ExecuteAsync` because the current engine is trace-aware but does not own run-id
generation or workspace selection.

Typical hosts should create these values before calling the engine:

```csharp
var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
var traceFilePath = Path.Combine(workspacePath, ".qre", "runs", runId, "events.jsonl");
```

## Facade Interface

There are two facade contracts in `CodexFlow.QueryRuntime.Abstractions`.

`IQueryRuntimeHostEngine` is the library-facing contract for applications that
want QRE to replace an existing in-process runtime. It accepts pre-assembled
`ChatMessage` history, custom `AIFunction` tools, required-tool steering,
provider `ChatOptions`, trace location, workspace path, and streaming text
deltas:

```csharp
public interface IQueryRuntimeHostEngine : IQueryRuntimeEngine
{
    Task<QueryRuntimeResult> RunAsync(
        QueryRuntimeHostRequest request,
        CancellationToken ct = default);
}
```

Example:

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

`IQueryRuntimeEngine` remains the smaller CLI-style prompt facade:

```csharp
Task<QueryRuntimeResult> RunAsync(QueryRuntimeRequest request, CancellationToken ct = default);
```

Use `IQueryRuntimeHostEngine` for CodexFlow-style in-process replacement work.
Use `CodexFlow.QueryRuntime.Engine.IQueryRuntimeEngine` only when the host needs
the lower-level event-sink and trace-file control surface directly.
