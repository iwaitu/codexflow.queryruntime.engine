using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;

// Minimal .NET 10 integration using Engine.V2.IAgentRuntime directly (v2-only architecture).
var objective = args.Length == 0 ? "Explain the host/runtime boundary." : string.Join(' ', args);

// 1. Initialize the model client (Static offline client or online MeaiRuntimeModelClient)
IAgentRuntime runtime = new AgentRuntime(new StaticRuntimeModelClient(
    "The host supplies the request; QRE owns the model loop and runtime state."));

// 2. Build the typed loop request with session/turn IDs and policy
var request = new RuntimeAgentLoopRequest(
    new RuntimeSessionId(Guid.NewGuid().ToString("N")),
    new RuntimeTurnId(Guid.NewGuid().ToString("N")),
    objective,
    [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(objective)])],
    [],
    new RuntimeModelParameters(),
    new RuntimePolicySnapshot("embedded-v2", "none"),
    new RuntimeEnvironmentSnapshot("local", Path.GetFullPath("."), "embedded-v2"),
    new RuntimeBudgetSnapshot(maxSteps: 3, maxToolCalls: 4));

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(300));

// 3. Execute the turn with event streaming
var result = await runtime.RunAsync(new RuntimeRunRequest(request), new ConsoleEvents(), cancellation.Token);

Console.WriteLine();
Console.WriteLine($"status: {result.Status}; steps: {result.Turn.Steps.Count}");
return result.Status == RuntimeTurnStatus.Completed ? 0 : 1;

/// <summary>
/// Subscribes to V2 presentation events (text tokens, reasoning tokens, tool requests, warnings).
/// </summary>
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
                // Reasoning / CoT token stream (for models with thinking enabled)
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
