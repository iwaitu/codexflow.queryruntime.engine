# EmbeddedV2

A minimal, production-grade integration example demonstrating how to directly embed the **0.23.0 stable, v2-only** QRE runtime (`CodexFlow.QueryRuntime.Engine.V2.IAgentRuntime`) inside a .NET 10 application.

This approach requires no CLI subprocess, no external database, and no CodexFlow web platform dependency.

---

## How to Run

From the repository root:

```bash
# Build and run with default prompt
dotnet run --project examples/EmbeddedV2

# Or pass a custom objective
dotnet run --project examples/EmbeddedV2 -- "Explain the host/runtime boundary."
```

Output:
```text
The host supplies the request; QRE owns the model loop and runtime state.
status: Completed; steps: 1
```

---

## Architectural Concept

In this embedding model:
- **The Host Application owns**:
  - Process lifecycle and user interface;
  - Task objective and initial user prompt;
  - Policy, environment, and budget snapshots;
  - Presentation event subscription (`IRuntimeEventSink`).
- **QRE owns**:
  - The deterministic Agent model loop;
  - Step transitions and state reducer;
  - Context preparation and token compaction;
  - Tool execution pipeline and fail-closed policy enforcement;
  - Terminal condition evaluation.

---

## Code Walkthrough

### 1. Initialize Runtime with Model Adapter
```csharp
// Offline static model (used in this example)
IAgentRuntime runtime = new AgentRuntime(new StaticRuntimeModelClient(
    "The host supplies the request; QRE owns the model loop and runtime state."));

// For live OpenAI / vLLM / Claude providers, wrap Microsoft.Extensions.AI:
// var meaiClient = new OpenAIClient(...).AsChatClient();
// IAgentRuntime runtime = new AgentRuntime(new MeaiRuntimeModelClient(meaiClient, QreModelApiMode.ChatCompletions));
```

### 2. Assemble Immutable V2 Request
```csharp
var request = new RuntimeAgentLoopRequest(
    new RuntimeSessionId(Guid.NewGuid().ToString("N")),
    new RuntimeTurnId(Guid.NewGuid().ToString("N")),
    objective,
    [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(objective)])],
    ToolDefinitions: [],
    ModelParameters: new RuntimeModelParameters(),
    Policy: new RuntimePolicySnapshot("embedded-v2", "none"),
    Environment: new RuntimeEnvironmentSnapshot("local", Path.GetFullPath("."), "embedded-v2"),
    Budget: new RuntimeBudgetSnapshot(MaxRounds: 3, MaxStepsPerRound: 4));
```

### 3. Stream Presentation Events
Implement `IRuntimeEventSink` to receive fine-grained, real-time events:
```csharp
file sealed class ConsoleEvents : IRuntimeEventSink
{
    public ValueTask OnEventAsync(RuntimePresentationEvent runtimeEvent, CancellationToken ct)
    {
        switch (runtimeEvent.Type)
        {
            case RuntimePresentationEventType.TextDelta:
                Console.Write(runtimeEvent.Text);
                break;
            case RuntimePresentationEventType.ReasoningDelta:
                // Streaming CoT reasoning tokens
                Console.Write($"[Thinking: {runtimeEvent.Text}]");
                break;
            case RuntimePresentationEventType.ToolCallRequested:
                Console.WriteLine($"\n[Tool Requested]: {runtimeEvent.ToolName}");
                break;
            case RuntimePresentationEventType.Warning:
                Console.WriteLine($"\n[Warning]: {runtimeEvent.Warning?.Message}");
                break;
        }
        return ValueTask.CompletedTask;
    }
}
```

### 4. Execute Turn
```csharp
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(300));
var result = await runtime.RunAsync(new RuntimeRunRequest(request), new ConsoleEvents(), cancellation.Token);
```

For fault-resilient checkpointing and process recovery, see **[H1CrashResume](../H1CrashResume)**.
