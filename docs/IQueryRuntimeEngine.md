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
| `WriteToolNames` | `IReadOnlySet<string>` | Optional host-supplied tool names counted as workspace-write calls in result metadata. Built-in profiles derive this from `write_fs` tool capabilities. |
| `ToolIntervention` | `IQueryRuntimeToolIntervention?` | Optional host policy hook that can allow, block with feedback, or fail closed before a tool executes, then observe the result after execution. |
| `StopGate` | `IQueryRuntimeStopGate?` | Optional host verification gate called before a no-tool-call terminal answer is accepted. |
| `MaxStopGateContinuations` | `int` | Maximum number of stop-gate requested continuation rounds. Defaults to `1`. |

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
5. If no tool calls are present, calls `StopGate` when supplied. The gate can accept, continue with host feedback, require a specific tool in the next round, or fail closed.
6. If tool calls are present and tools are enabled, appends the assistant tool-call message, calls `ToolIntervention` before each matching `AIFunction`, executes allowed tools, appends tool result messages, and continues.
7. Stops at `MaxRounds` if the loop does not terminate earlier. A stop-gate continuation request that cannot be honored fails closed with a terminal detail code instead of being reported as a normal successful stop.

The message list grows inside the runtime during one execution. The original
`InitialMessages` list is the entry-layer responsibility.

## Host Security Hooks

`CodexFlow.QueryRuntime.Abstractions` defines host-neutral hooks that map to the
kind of policy, guardrail, critique, and verification behavior a larger host may
already own. The contracts depend only on QRE DTOs, `Microsoft.Extensions.AI`,
and BCL types; they do not require platform-specific runtime types.

`IQueryRuntimeToolIntervention.BeforeToolCallAsync` receives the tool name, call
id, arguments, round, available tool names, required tool name, workspace path,
and current messages. It must return one of:

| Decision | Behavior |
| --- | --- |
| `Allow` | Execute the selected `AIFunction`. |
| `BlockWithFeedback` | Do not execute the tool. QRE appends a tool-result message containing the policy feedback so the model can continue under the host policy. |
| `FailClosed` | Terminate the run with `QueryTerminationReason.FailClosed` and a terminal detail code. |

The first version intentionally does not support silently rewriting tool
arguments before execution. If argument rewrite is added later, it must be a
separate typed decision with schema validation and audit events.

`IQueryRuntimeToolIntervention.AfterToolExecutionAsync` observes success,
result length, result summary, and exception type/message when execution fails.
If the after-tool hook itself fails, QRE fails closed instead of silently
ignoring the host hook failure.

`IQueryRuntimeStopGate.BeforeStopAsync` runs before QRE accepts a no-tool-call
assistant response as terminal. It can:

| Decision | Behavior |
| --- | --- |
| `Accept` | Return the assistant text as the final answer. |
| `Continue` | Append host feedback as a user message and run another model round. |
| `RequireTool` | Append host feedback and require the named tool in the next round when available. |
| `FailClosed` | Terminate with `FailClosed` and the supplied detail code. |

Continuation attempts are bounded by both `MaxRounds` and
`MaxStopGateContinuations`. When the gate still requires work after either limit
is reached, QRE returns `FailClosed` with a verification detail code such as
`verification_incomplete` or `verification_timed_out`.

QRE emits `PolicyInterventionDecisionEvent` and `StopGateDecisionEvent` records.
Prompt assembly snapshots include the active required-tool name and whether it
has already been satisfied. Terminal events include the terminal detail code,
zero-tool-call round count, continuation count, write-tool call count,
last function call, and required-tool state.

`QueryRuntimeResult` exposes the same host-facing metadata:

| Field | Description |
| --- | --- |
| `TerminalDetailCode` | Machine-readable terminal detail such as `verification_incomplete`, `verification_timed_out`, or hook failure codes. |
| `ZeroToolCallRounds` | Number of rounds where the model returned no structured tool calls. |
| `ContinuationCount` | Number of stop-gate requested continuation rounds. |
| `WriteToolCalls` | Number of successful executed tools classified as workspace-write tools. |
| `LastFunctionCall` | Last model-requested function/tool name. |
| `RunDirectory` | Directory containing `events.jsonl`, `manifest.json`, `run.json`, and artifacts for the run. |
| `RequiredToolName` / `RequiredToolSatisfied` | Active required-tool state at termination. |
| `ExecutedToolNames` / `SuccessfulToolNames` | Ordered tool execution metadata for adapter policy checks. |
| `FinalMessages` | Final in-memory message history after QRE appended assistant and tool messages during the run. |

## Trace Parameters

`runId`, `traceFilePath`, and `workspacePath` are explicit parameters on
`ExecuteAsync` because the current engine is trace-aware but does not own run-id
generation or workspace selection.

Typical hosts should create these values before calling the engine:

```csharp
var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
var traceFilePath = Path.Combine(workspacePath, ".qre", "runs", runId, "events.jsonl");
```

The experimental host facade normalizes `TraceRoot` to an absolute path, keeps
run artifacts under `<trace-root>/runs/<run-id>/`, and rejects run ids that are
not a single safe path segment. Before opening `events.jsonl`, it verifies the
target path is contained by the authorized trace root using the same
segment-by-segment symlink escape checks used by workspace file tools. It also
rejects trace roots under `.git` or secret-looking path segments.

If a host wants to own trace persistence itself, use the lower-level engine
contract with a host-provided `IQueryRuntimeEventSink`; in that mode the host is
responsible for applying equivalent path containment before writing artifacts.
`CodexFlow.QueryRuntime.Abstractions.QueryRuntimePathSafety` exposes the same
root normalization, containment, symlink-escape, and protected-path helpers for
library consumers.

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

## Host Adapter Contract Tests

Downstream adapters should not prove QRE integration with only a happy-path tool
call. The compiled test helper
`CodexFlow.QueryRuntime.UnitTests/Contracts/HostAdapterContractTestKit.cs`
captures the minimum host-adapter contract checks:

- pre-tool policy hooks can block a write tool without executing it
- stop gates can force continuation before accepting a terminal answer
- required-tool decisions trigger another round and require the named tool
- result metadata preserves executed/successful/write tool names and final chat history
- unsafe run ids and trace roots fail before trace artifacts are opened

`HostAdapterContractTestKitTests` runs those checks against
`ExperimentalQueryRuntimeHarness` in this repository. A downstream CodexFlow
adapter should implement equivalent tests against its own `IQueryRuntimeHostEngine`
factory and scripted model provider before enabling QRE beyond an experimental
backend.
