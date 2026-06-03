using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class QueryRuntimeEngineSmokeTests
{
    [Fact]
    public async Task ExecuteAsync_NoToolRound_CompletesAndEmitsTerminalEvent()
    {
        var sink = new CapturingRuntimeEventSink();
        var executor = new ScriptedLlmExecutor(ScriptedLlmExecutor.TextStep("done"));
        var engine = CreateEngine(executor);

        var result = await engine.ExecuteAsync(
            CreateRequest(enableTools: false),
            sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(QueryTerminationReason.NoToolCalls, result.TerminationReason);
        Assert.Equal("done", result.FinalText);
        Assert.Single(executor.Requests);
        Assert.Contains(sink.Events, evt => evt is PromptAssemblySnapshotEvent);
        Assert.Contains(sink.Events, evt => evt is TerminatedEvent terminated &&
            terminated.Reason == QueryTerminationReason.NoToolCalls);
    }

    [Fact]
    public async Task ExecuteAsync_ToolRound_ExecutesToolAndContinuesToFinalText()
    {
        var executedCommands = new List<string>();
        var runCommand = AIFunctionFactory.Create(
            (string command) =>
            {
                executedCommands.Add(command);
                return $"ran:{command}";
            },
            new AIFunctionFactoryOptions { Name = "run_command", Description = "run command" });
        var sink = new CapturingRuntimeEventSink();
        var executor = new ScriptedLlmExecutor(
            ScriptedLlmExecutor.FunctionCallStep("call-1", "run_command", new Dictionary<string, object?> { ["command"] = "pwd" }),
            ScriptedLlmExecutor.TextStep("verified"));
        var engine = CreateEngine(executor);

        var result = await engine.ExecuteAsync(
            CreateRequest(enableTools: true, availableTools: [runCommand]),
            sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(QueryTerminationReason.NoToolCalls, result.TerminationReason);
        Assert.Equal("verified", result.FinalText);
        Assert.Equal(["pwd"], executedCommands);
        Assert.Equal(2, executor.Requests.Count);
        Assert.Contains(sink.Events, evt => evt is ToolExecutionCompletedEvent completed &&
            completed.ToolName == "run_command" &&
            completed.Success);
    }

    private static QueryRuntimeEngine CreateEngine(ScriptedLlmExecutor executor)
        => new(
            executor,
            contextWindowManager: null,
            new DefaultToolExecutionCoordinator(NullLogger<DefaultToolExecutionCoordinator>.Instance),
            new DefaultQueryRecoveryPolicy(NullLogger<DefaultQueryRecoveryPolicy>.Instance),
            telemetry: null,
            logger: NullLogger<QueryRuntimeEngine>.Instance);

    private static QueryRuntimeRequest CreateRequest(
        bool enableTools,
        IReadOnlyList<AIFunction>? availableTools = null)
        => new()
        {
            SessionId = Guid.NewGuid().ToString("N"),
            EntryPoint = QueryLoopEntryPoint.DefaultCodexKernel,
            InitialMessages = [new ChatMessage(ChatRole.User, "test")],
            MaxRounds = 3,
            EnableTools = enableTools,
            Options = new ChatOptions(),
            AvailableTools = availableTools,
            AvailableToolsProvider = availableTools != null ? () => availableTools : null
        };

    private sealed class ScriptedLlmExecutor(
        params Func<LLMExecutionRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>[] steps) : ILLMExecutor
    {
        private readonly Queue<Func<LLMExecutionRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>> _steps = new(steps);

        public List<LLMExecutionRequest> Requests { get; } = [];

        public Task<ChatResponse> ExecuteAsync(LLMExecutionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("QueryRuntime tests use StreamAsync only.");

        public IAsyncEnumerable<ChatResponseUpdate> StreamAsync(LLMExecutionRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("No scripted LLM step remains.");
            }

            return _steps.Dequeue()(request, ct);
        }

        public static Func<LLMExecutionRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> TextStep(string text)
            => (_, _) => StreamUpdates(new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(text)]));

        public static Func<LLMExecutionRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> FunctionCallStep(
            string callId,
            string toolName,
            IReadOnlyDictionary<string, object?> arguments)
            => (_, _) => StreamUpdates(new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent(callId, toolName, new Dictionary<string, object?>(arguments, StringComparer.OrdinalIgnoreCase))]));

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamUpdates(
            params ChatResponseUpdate[] updates)
        {
            foreach (var update in updates)
            {
                yield return update;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class CapturingRuntimeEventSink : IQueryRuntimeEventSink
    {
        private readonly List<QueryRuntimeEvent> _events = [];

        public IReadOnlyList<QueryRuntimeEvent> Events => _events;

        public bool IsEnabled(QueryRuntimeEventType eventType) => true;

        public ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent)
        {
            _events.Add(runtimeEvent);
            return ValueTask.CompletedTask;
        }
    }
}
