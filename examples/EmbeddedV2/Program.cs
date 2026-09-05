using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;

var objective = args.Length == 0 ? "Explain the host/runtime boundary." : string.Join(' ', args);
IAgentRuntime runtime = new AgentRuntime(new StaticRuntimeModelClient(
    "The host supplies the request; QRE owns the model loop and runtime state."));
var request = new RuntimeAgentLoopRequest(
    new RuntimeSessionId(Guid.NewGuid().ToString("N")),
    new RuntimeTurnId(Guid.NewGuid().ToString("N")),
    objective,
    [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(objective)])],
    [],
    new RuntimeModelParameters(),
    new RuntimePolicySnapshot("example-v1", "none"),
    new RuntimeEnvironmentSnapshot("local", Path.GetFullPath("."), "embedded-v2"),
    new RuntimeBudgetSnapshot(3, 4));
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(300));
var result = await runtime.RunAsync(new RuntimeRunRequest(request), new ConsoleEvents(), cancellation.Token);
Console.WriteLine();
Console.WriteLine($"status: {result.Status}; steps: {result.Turn.Steps.Count}");
return result.Status == RuntimeTurnStatus.Completed ? 0 : 1;

file sealed class ConsoleEvents : IRuntimeEventSink
{
    public ValueTask OnEventAsync(RuntimePresentationEvent runtimeEvent, CancellationToken ct)
    {
        if (runtimeEvent.Type == RuntimePresentationEventType.TextDelta)
            Console.Write(runtimeEvent.Text);
        return ValueTask.CompletedTask;
    }
}
