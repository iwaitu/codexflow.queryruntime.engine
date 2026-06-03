using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Qre = CodexFlow.QueryRuntime.Engine;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class StandaloneQueryRuntimeEngineTests
{
    [Fact]
    public async Task ExecuteAsync_ReportsActualRounds_WhenRoundHasMultipleToolCalls()
    {
        var firstTool = AIFunctionFactory.Create(
            () => "first-result",
            new AIFunctionFactoryOptions { Name = "first_tool" });
        var secondTool = AIFunctionFactory.Create(
            () => "second-result",
            new AIFunctionFactoryOptions { Name = "second_tool" });
        var model = new ScriptedModelClient(
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [
                    new FunctionCallContent("call-1", "first_tool", new Dictionary<string, object?>()),
                    new FunctionCallContent("call-2", "second_tool", new Dictionary<string, object?>())
                ]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")]));
        var sink = new CapturingEventSink();
        var engine = new Qre.QueryRuntimeEngine(model);

        var result = await engine.ExecuteAsync(
            new Qre.QueryRuntimeRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                MaxRounds = 3,
                EnableTools = true,
                AvailableTools = [firstTool, secondTool]
            },
            sink,
            "run-1",
            "/tmp/qre-test/events.jsonl",
            workspacePath: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(Qre.QueryTerminationReason.NoToolCalls, result.TerminationReason);
        Assert.Equal(2, result.TotalRounds);
        Assert.Equal(2, result.TotalToolCalls);
        Assert.Contains(sink.Events, evt => evt is Qre.TerminatedEvent terminated && terminated.TotalRounds == 2);
    }

    private sealed class ScriptedModelClient(params ChatResponseUpdate[] responses) : Qre.IQueryRuntimeModelClient
    {
        private readonly Queue<ChatResponseUpdate> _responses = new(responses);

        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            Qre.QueryRuntimeModelRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return _responses.Dequeue();
            await Task.CompletedTask;
        }
    }

    private sealed class CapturingEventSink : Qre.IQueryRuntimeEventSink
    {
        private readonly List<Qre.QueryRuntimeEvent> _events = [];

        public IReadOnlyList<Qre.QueryRuntimeEvent> Events => _events;

        public bool IsEnabled(Qre.QueryRuntimeEventType eventType) => true;

        public ValueTask OnEventAsync(Qre.QueryRuntimeEvent runtimeEvent)
        {
            _events.Add(runtimeEvent);
            return ValueTask.CompletedTask;
        }
    }
}
