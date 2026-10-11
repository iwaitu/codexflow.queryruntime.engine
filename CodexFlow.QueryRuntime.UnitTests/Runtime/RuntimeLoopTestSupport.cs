using System.Runtime.CompilerServices;
using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

/// <summary>Shared scripted model/tool doubles for agent-loop feature tests.</summary>
internal static class RuntimeLoopTestSupport
{
    public static RuntimeAgentLoopRequest CreateRequest(
        IReadOnlyList<RuntimeToolDescriptor>? tools = null,
        IRuntimeToolExecutor? executor = null,
        RuntimeModelParameters? parameters = null,
        RuntimeBudgetSnapshot? budget = null,
        IRuntimeTerminationPolicy? terminationPolicy = null)
        => new(
            new RuntimeSessionId("session-1"),
            new RuntimeTurnId("turn-1"),
            "test objective",
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("start")])],
            tools ?? [],
            parameters ?? new RuntimeModelParameters(),
            new RuntimePolicySnapshot("policy-v1", "test"),
            new RuntimeEnvironmentSnapshot("local", "workspace", "sha256:test"),
            budget ?? new RuntimeBudgetSnapshot(3, 3),
            CreatedAt: DateTimeOffset.UnixEpoch)
        {
            ToolExecutor = executor ?? (tools is { Count: > 0 } ? new RecordingToolExecutor() : null),
            TerminationPolicy = terminationPolicy
        };

    public static Task<RuntimeAgentLoopResult> RunLoopAsync(
        RuntimeAgentLoop loop,
        RuntimeAgentLoopRequest request)
        => loop.RunAsync(request, ct: TestContext.Current.CancellationToken);

    public static RuntimeToolDescriptor ToolDescriptor(string name)
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        return new RuntimeToolDescriptor(
            name,
            "1",
            $"Execute {name}.",
            schema.RootElement.Clone(),
            RuntimeToolSideEffect.ReadOnly,
            RuntimeToolIdempotency.Idempotent);
    }

    public static RuntimeToolCall ToolCall(string invocationId, string name, string arguments = "{}")
    {
        using var document = JsonDocument.Parse(arguments);
        return new RuntimeToolCall(
            new RuntimeInvocationId(invocationId),
            name,
            document.RootElement.Clone());
    }

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static async IAsyncEnumerable<RuntimeModelStreamEvent> Events(
        params RuntimeModelStreamEvent[] events)
    {
        foreach (var runtimeEvent in events)
        {
            await Task.Yield();
            yield return runtimeEvent;
        }
    }

    public static IAsyncEnumerable<RuntimeModelStreamEvent> Text(string text)
        => Events(
            new RuntimeTextDeltaEvent(text),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn));

    public static IAsyncEnumerable<RuntimeModelStreamEvent> Tool(params RuntimeToolCall[] calls)
        => Events(
        [
            .. calls.Select(static call => (RuntimeModelStreamEvent)new RuntimeToolCallEvent(call)),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)
        ]);

    public static async IAsyncEnumerable<RuntimeModelStreamEvent> Throw(
        Exception exception,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        throw exception;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    internal sealed class ScriptedModelClient(
        params Func<RuntimeModelRequest, IAsyncEnumerable<RuntimeModelStreamEvent>>[] scripts)
        : IRuntimeModelClient
    {
        private int _index;

        public List<RuntimeModelRequest> Requests { get; } = [];

        public IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
            RuntimeModelRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            var index = Interlocked.Increment(ref _index) - 1;
            if (index >= scripts.Length)
            {
                throw new InvalidOperationException("No scripted model response remains.");
            }
            return WithCancellation(scripts[index](request), ct);
        }

        private static async IAsyncEnumerable<RuntimeModelStreamEvent> WithCancellation(
            IAsyncEnumerable<RuntimeModelStreamEvent> source,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var runtimeEvent in source.WithCancellation(ct))
            {
                yield return runtimeEvent;
            }
        }
    }

    internal sealed class RecordingToolExecutor(Action<RuntimeToolCall>? onExecute = null) : IRuntimeToolExecutor
    {
        public List<RuntimeToolCall> Calls { get; } = [];

        public ValueTask<RuntimeToolResult> ExecuteAsync(
            RuntimeToolDescriptor descriptor,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(call);
            onExecute?.Invoke(call);
            return ValueTask.FromResult(new RuntimeToolResult(call.InvocationId, $"{descriptor.CanonicalName}:ok", true));
        }
    }

    internal sealed class FixedToolSelector(Func<int, IReadOnlyList<RuntimeToolDescriptor>, IReadOnlyList<RuntimeToolDescriptor>> select)
        : IRuntimeToolCatalogSelector
    {
        public IReadOnlyList<RuntimeToolDescriptor> SelectTools(
            PreparedRuntimeContext context,
            IReadOnlyList<RuntimeToolDescriptor> frozenCatalog,
            int stepIndex)
            => select(stepIndex, frozenCatalog);

        public void Observe(RuntimeToolCall call, RuntimeToolResult result)
        {
        }
    }
}
