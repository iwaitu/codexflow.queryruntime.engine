using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Qre = CodexFlow.QueryRuntime.Engine;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class StandaloneQueryRuntimeEngineTests
{
    [Fact]
    public async Task ExecuteAsync_UsesInitialMessagesForMultiTurnConversationHistory()
    {
        var initialMessages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are QRE."),
            new(ChatRole.User, "Summarize the repository."),
            new(ChatRole.Assistant, "It is a QueryRuntime engine repository."),
            new(ChatRole.User, "Now explain how history is passed to the runtime.")
        };
        var model = new ScriptedModelClient(
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Pass history through InitialMessages.")]));
        var sink = new CapturingEventSink();
        Qre.IQueryRuntimeEngine engine = new Qre.QueryRuntimeEngine(model);

        var result = await engine.ExecuteAsync(
            new Qre.QueryRuntimeRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                InitialMessages = initialMessages,
                MaxRounds = 1,
                EnableTools = false
            },
            sink,
            "run-history",
            "/tmp/qre-test/events.jsonl",
            workspacePath: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(Qre.QueryTerminationReason.NoToolCalls, result.TerminationReason);
        Assert.Equal("Pass history through InitialMessages.", result.FinalText);

        var modelRequest = Assert.Single(model.Requests);
        Assert.Equal(4, modelRequest.Messages.Count);
        Assert.Equal(ChatRole.System, modelRequest.Messages[0].Role);
        Assert.Equal("You are QRE.", ReadText(modelRequest.Messages[0]));
        Assert.Equal(ChatRole.User, modelRequest.Messages[1].Role);
        Assert.Equal("Summarize the repository.", ReadText(modelRequest.Messages[1]));
        Assert.Equal(ChatRole.Assistant, modelRequest.Messages[2].Role);
        Assert.Equal("It is a QueryRuntime engine repository.", ReadText(modelRequest.Messages[2]));
        Assert.Equal(ChatRole.User, modelRequest.Messages[3].Role);
        Assert.Equal("Now explain how history is passed to the runtime.", ReadText(modelRequest.Messages[3]));

        Assert.Contains(
            sink.Events,
            evt => evt is Qre.PromptAssemblySnapshotEvent snapshot &&
                   snapshot.Round == 0 &&
                   snapshot.MessageCount == initialMessages.Count);
    }

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
        Qre.IQueryRuntimeEngine engine = new Qre.QueryRuntimeEngine(model);

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
        Assert.Single(model.Requests[0].Messages);
        Assert.Equal(3, model.Requests[1].Messages.Count);
        Assert.Contains(sink.Events, evt => evt is Qre.TerminatedEvent terminated && terminated.TotalRounds == 2);
    }

    [Fact]
    public async Task ExecuteAsync_StreamsTextDeltasBeforeFinalResult()
    {
        var model = new ScriptedModelClient(
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [
                    new TextContent("first "),
                    new TextContent("second")
                ]));
        var sink = new CapturingEventSink();
        var deltas = new List<string>();
        Qre.IQueryRuntimeEngine engine = new Qre.QueryRuntimeEngine(model);

        var result = await engine.ExecuteAsync(
            new Qre.QueryRuntimeRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                MaxRounds = 1,
                EnableTools = false,
                TextDeltaSink = (delta, _) =>
                {
                    deltas.Add(delta);
                    return ValueTask.CompletedTask;
                }
            },
            sink,
            "run-stream",
            "/tmp/qre-test/events.jsonl",
            workspacePath: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(["first ", "second"], deltas);
        Assert.Equal("first second", result.FinalText);
    }

    [Fact]
    public async Task ExecuteAsync_CopiesDerivedChatOptionsWithoutMutatingHostInstance()
    {
        var tool = AIFunctionFactory.Create(
            () => "tool-result",
            new AIFunctionFactoryOptions { Name = "custom_tool" });
        var hostOptions = new DerivedChatOptions
        {
            Marker = "provider-specific",
            Temperature = 0.2f
        };
        var model = new ScriptedModelClient(
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")]));
        var sink = new CapturingEventSink();
        Qre.IQueryRuntimeEngine engine = new Qre.QueryRuntimeEngine(model);

        await engine.ExecuteAsync(
            new Qre.QueryRuntimeRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                Options = hostOptions,
                OptionsCloneFactory = static options =>
                {
                    var source = Assert.IsType<DerivedChatOptions>(options);
                    return new DerivedChatOptions
                    {
                        Marker = source.Marker,
                        Temperature = source.Temperature
                    };
                },
                MaxRounds = 1,
                EnableTools = true,
                AvailableTools = [tool]
            },
            sink,
            "run-derived-options",
            "/tmp/qre-test/events.jsonl",
            workspacePath: null,
            TestContext.Current.CancellationToken);

        Assert.Null(hostOptions.Tools);
        Assert.Null(hostOptions.ToolMode);
        var runtimeOptions = Assert.IsType<DerivedChatOptions>(Assert.Single(model.Requests).Options);
        Assert.NotSame(hostOptions, runtimeOptions);
        Assert.Equal("provider-specific", runtimeOptions.Marker);
        Assert.Single(runtimeOptions.Tools ?? []);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailureToolResultForUnavailableToolCall()
    {
        var model = new ScriptedModelClient(
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call-missing", "missing_tool", new Dictionary<string, object?>())]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")]));
        var sink = new CapturingEventSink();
        Qre.IQueryRuntimeEngine engine = new Qre.QueryRuntimeEngine(model);

        var result = await engine.ExecuteAsync(
            new Qre.QueryRuntimeRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                MaxRounds = 2,
                EnableTools = true
            },
            sink,
            "run-missing-tool",
            "/tmp/qre-test/events.jsonl",
            workspacePath: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("done", result.FinalText);
        Assert.Equal(0, result.TotalToolCalls);
        Assert.Contains(model.Requests[1].Messages, message => message.Role == ChatRole.Tool && message.Contents.Count == 1);
        Assert.Contains(
            sink.Events,
            evt => evt is Qre.ToolExecutionCompletedEvent completed &&
                   completed.ToolName == "missing_tool" &&
                   !completed.Success &&
                   completed.Result.Contains("not currently available", StringComparison.Ordinal));
    }

    private sealed class ScriptedModelClient(params ChatResponseUpdate[] responses) : Qre.IQueryRuntimeModelClient
    {
        private readonly Queue<ChatResponseUpdate> _responses = new(responses);

        public List<Qre.QueryRuntimeModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            Qre.QueryRuntimeModelRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            yield return _responses.Dequeue();
            await Task.CompletedTask;
        }
    }

    private static string ReadText(ChatMessage message)
        => string.Concat(message.Contents.OfType<TextContent>().Select(static content => content.Text));

    private sealed class DerivedChatOptions : ChatOptions
    {
        public string? Marker { get; set; }
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
