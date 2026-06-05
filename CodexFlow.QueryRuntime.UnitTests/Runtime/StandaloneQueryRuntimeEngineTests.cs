using System.Runtime.CompilerServices;
using HostContracts = CodexFlow.QueryRuntime.Abstractions;
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
        Assert.Contains(
            result.FinalMessages,
            message => message.Role == ChatRole.Assistant &&
                       ReadText(message) == "Pass history through InitialMessages.");

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

    [Fact]
    public async Task ExecuteAsync_BlockedToolDoesNotExecuteAndFeedbackReachesNextRound()
    {
        var toolCalls = 0;
        var tool = AIFunctionFactory.Create(
            () =>
            {
                toolCalls++;
                return "write-complete";
            },
            new AIFunctionFactoryOptions { Name = "write_file" });
        var intervention = new RecordingToolIntervention(
            static _ => HostContracts.QueryRuntimeToolInterventionDecision.BlockWithFeedback(
                "Policy blocked write_file until verification completes.",
                "writes require verification",
                "tool_blocked"));
        var model = new ScriptedModelClient(
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call-write", "write_file", new Dictionary<string, object?>())]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("blocked handled")]));
        var sink = new CapturingEventSink();
        Qre.IQueryRuntimeEngine engine = new Qre.QueryRuntimeEngine(model);

        var result = await engine.ExecuteAsync(
            new Qre.QueryRuntimeRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                MaxRounds = 2,
                EnableTools = true,
                AvailableTools = [tool],
                ToolIntervention = intervention
            },
            sink,
            "run-blocked-tool",
            "/tmp/qre-test/events.jsonl",
            workspacePath: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("blocked handled", result.FinalText);
        Assert.Equal(0, toolCalls);
        Assert.Single(intervention.BeforeCalls);
        Assert.Empty(intervention.AfterCalls);
        Assert.Equal(0, result.TotalToolCalls);
        Assert.Contains(
            model.Requests[1].Messages,
            message => message.Role == ChatRole.Tool &&
                       ReadText(message).Contains("Policy blocked write_file", StringComparison.Ordinal));
        Assert.Contains(
            sink.Events,
            evt => evt is Qre.PolicyInterventionDecisionEvent decision &&
                   decision.ToolName == "write_file" &&
                   decision.Decision == HostContracts.QueryRuntimeToolInterventionDecisionKind.BlockWithFeedback.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_AfterToolHookObservesSuccessAndFailure()
    {
        var successTool = AIFunctionFactory.Create(
            () => "success-result",
            new AIFunctionFactoryOptions { Name = "success_tool" });
        var successIntervention = new RecordingToolIntervention();
        var successModel = new ScriptedModelClient(
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call-success", "success_tool", new Dictionary<string, object?>())]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")]));
        Qre.IQueryRuntimeEngine successEngine = new Qre.QueryRuntimeEngine(successModel);

        await successEngine.ExecuteAsync(
            new Qre.QueryRuntimeRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                MaxRounds = 2,
                EnableTools = true,
                AvailableTools = [successTool],
                ToolIntervention = successIntervention
            },
            new CapturingEventSink(),
            "run-success-hook",
            "/tmp/qre-test/events.jsonl",
            workspacePath: null,
            TestContext.Current.CancellationToken);

        var success = Assert.Single(successIntervention.AfterCalls);
        Assert.True(success.Success);
        Assert.Equal("success_tool", success.ToolName);
        Assert.Equal("success-result", success.ResultSummary);

        var failingTool = AIFunctionFactory.Create(
            ThrowToolFailure,
            new AIFunctionFactoryOptions { Name = "failing_tool" });
        var failureIntervention = new RecordingToolIntervention();
        var failureModel = new ScriptedModelClient(
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call-failure", "failing_tool", new Dictionary<string, object?>())]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("recovered")]));
        Qre.IQueryRuntimeEngine failureEngine = new Qre.QueryRuntimeEngine(failureModel);

        var failureResult = await failureEngine.ExecuteAsync(
            new Qre.QueryRuntimeRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                MaxRounds = 2,
                EnableTools = true,
                AvailableTools = [failingTool],
                ToolIntervention = failureIntervention
            },
            new CapturingEventSink(),
            "run-failure-hook",
            "/tmp/qre-test/events.jsonl",
            workspacePath: null,
            TestContext.Current.CancellationToken);

        var failure = Assert.Single(failureIntervention.AfterCalls);
        Assert.False(failure.Success);
        Assert.Equal("InvalidOperationException", failure.ExceptionType);
        Assert.Equal(Qre.QueryTerminationReason.NoToolCalls, failureResult.TerminationReason);
        Assert.Equal("recovered", failureResult.FinalText);
        Assert.Equal(1, failureResult.TotalToolCalls);
        Assert.Equal(["failing_tool"], failureResult.ExecutedToolNames);
        Assert.Empty(failureResult.SuccessfulToolNames);
        Assert.Null(failureResult.TerminalDetailCode);
        Assert.Contains(
            failureModel.Requests[1].Messages,
            message => message.Role == ChatRole.Tool &&
                       ReadText(message).Contains("tool failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_PreToolHookRunsForEveryParallelToolCall()
    {
        var firstTool = AIFunctionFactory.Create(
            () => "first",
            new AIFunctionFactoryOptions { Name = "first_tool" });
        var secondTool = AIFunctionFactory.Create(
            () => "second",
            new AIFunctionFactoryOptions { Name = "second_tool" });
        var intervention = new RecordingToolIntervention();
        var model = new ScriptedModelClient(
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [
                    new FunctionCallContent("call-1", "first_tool", new Dictionary<string, object?>()),
                    new FunctionCallContent("call-2", "second_tool", new Dictionary<string, object?>())
                ]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")]));
        Qre.IQueryRuntimeEngine engine = new Qre.QueryRuntimeEngine(model);

        await engine.ExecuteAsync(
            new Qre.QueryRuntimeRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                MaxRounds = 2,
                EnableTools = true,
                AvailableTools = [firstTool, secondTool],
                ToolIntervention = intervention
            },
            new CapturingEventSink(),
            "run-multi-hook",
            "/tmp/qre-test/events.jsonl",
            workspacePath: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(["first_tool", "second_tool"], intervention.BeforeCalls.Select(static call => call.ToolName));
    }

    [Fact]
    public async Task ExecuteAsync_ResultMetadataTracksExecutedSuccessfulAndWriteTools()
    {
        var readTool = AIFunctionFactory.Create(
            () => "read-result",
            new AIFunctionFactoryOptions { Name = "read_file" });
        var writeTool = AIFunctionFactory.Create(
            () => "write-result",
            new AIFunctionFactoryOptions { Name = "write_file" });
        var model = new ScriptedModelClient(
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [
                    new FunctionCallContent("call-read", "read_file", new Dictionary<string, object?>()),
                    new FunctionCallContent("call-write", "write_file", new Dictionary<string, object?>())
                ]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")]));
        var sink = new CapturingEventSink();
        Qre.IQueryRuntimeEngine engine = new Qre.QueryRuntimeEngine(model);

        var result = await engine.ExecuteAsync(
            new Qre.QueryRuntimeRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                MaxRounds = 2,
                EnableTools = true,
                AvailableTools = [readTool, writeTool],
                WriteToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "write_file" }
            },
            sink,
            "run-metadata",
            "/tmp/qre-test/events.jsonl",
            workspacePath: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalToolCalls);
        Assert.Equal(1, result.WriteToolCalls);
        Assert.Equal(["read_file", "write_file"], result.ExecutedToolNames);
        Assert.Equal(["read_file", "write_file"], result.SuccessfulToolNames);
        Assert.Equal("write_file", result.LastFunctionCall);
        Assert.Equal("/tmp/qre-test", result.RunDirectory);
        Assert.Contains(
            sink.Events,
            evt => evt is Qre.TerminatedEvent terminated &&
                   terminated.WriteToolCalls == 1 &&
                   terminated.LastFunctionCall == "write_file");
    }

    [Fact]
    public async Task ExecuteAsync_StopGateCanContinueBeforeAcceptingFinalAnswer()
    {
        var stopGate = new ScriptedStopGate(
            HostContracts.QueryRuntimeStopDecision.Continue("Run verification before final answer."),
            HostContracts.QueryRuntimeStopDecision.Accept());
        var model = new ScriptedModelClient(
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("draft")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("verified")]));
        var sink = new CapturingEventSink();
        Qre.IQueryRuntimeEngine engine = new Qre.QueryRuntimeEngine(model);

        var result = await engine.ExecuteAsync(
            new Qre.QueryRuntimeRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                MaxRounds = 2,
                EnableTools = false,
                StopGate = stopGate,
                MaxStopGateContinuations = 1
            },
            sink,
            "run-stop-continue",
            "/tmp/qre-test/events.jsonl",
            workspacePath: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("verified", result.FinalText);
        Assert.Equal(2, result.TotalRounds);
        Assert.Equal(1, result.ContinuationCount);
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains(
            model.Requests[1].Messages,
            message => message.Role == ChatRole.User &&
                       ReadText(message).Contains("Run verification", StringComparison.Ordinal));
        Assert.Contains(sink.Events, evt => evt is Qre.StopGateDecisionEvent { Decision: "Continue" });
    }

    [Fact]
    public async Task ExecuteAsync_StopGateRequireToolForcesNextRoundToolCall()
    {
        var toolCalls = 0;
        var verifyTool = AIFunctionFactory.Create(
            () =>
            {
                toolCalls++;
                return "verified";
            },
            new AIFunctionFactoryOptions { Name = "verify_state" });
        var stopGate = new ScriptedStopGate(
            HostContracts.QueryRuntimeStopDecision.RequireTool(
                "verify_state",
                "Call verify_state before final answer."),
            HostContracts.QueryRuntimeStopDecision.Accept());
        var model = new ScriptedModelClient(
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("draft")]),
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call-verify", "verify_state", new Dictionary<string, object?>())]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")]));
        Qre.IQueryRuntimeEngine engine = new Qre.QueryRuntimeEngine(model);

        var result = await engine.ExecuteAsync(
            new Qre.QueryRuntimeRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                MaxRounds = 3,
                EnableTools = true,
                AvailableTools = [verifyTool],
                StopGate = stopGate,
                MaxStopGateContinuations = 1
            },
            new CapturingEventSink(),
            "run-require-tool",
            "/tmp/qre-test/events.jsonl",
            workspacePath: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("done", result.FinalText);
        Assert.Equal(1, toolCalls);
        Assert.Equal(1, result.TotalToolCalls);
        Assert.Equal(1, result.ContinuationCount);
        Assert.Equal("verify_state", result.LastFunctionCall);
    }

    [Fact]
    public async Task ExecuteAsync_StopGateRequireToolRequiresNewExecutionEvenWhenToolPreviouslySucceeded()
    {
        var toolCalls = 0;
        var verifyTool = AIFunctionFactory.Create(
            () =>
            {
                toolCalls++;
                return $"verified-{toolCalls}";
            },
            new AIFunctionFactoryOptions { Name = "verify_state" });
        var stopGate = new ScriptedStopGate(
            HostContracts.QueryRuntimeStopDecision.RequireTool(
                "verify_state",
                "Re-run verify_state for the final answer."),
            HostContracts.QueryRuntimeStopDecision.Accept());
        var model = new ScriptedModelClient(
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call-verify-1", "verify_state", new Dictionary<string, object?>())]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("draft after first verification")]),
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call-verify-2", "verify_state", new Dictionary<string, object?>())]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")]));
        Qre.IQueryRuntimeEngine engine = new Qre.QueryRuntimeEngine(model);

        var result = await engine.ExecuteAsync(
            new Qre.QueryRuntimeRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                MaxRounds = 4,
                EnableTools = true,
                AvailableTools = [verifyTool],
                StopGate = stopGate,
                MaxStopGateContinuations = 1
            },
            new CapturingEventSink(),
            "run-require-tool-again",
            "/tmp/qre-test/events.jsonl",
            workspacePath: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("done", result.FinalText);
        Assert.Equal(2, toolCalls);
        Assert.Equal(2, result.TotalToolCalls);
        Assert.Equal(1, result.ContinuationCount);
        Assert.Equal(ChatToolMode.RequireSpecific("verify_state"), model.Requests[2].Options?.ToolMode);
    }

    [Fact]
    public async Task ExecuteAsync_StopGateFailsClosedWhenContinuationBudgetIsExhausted()
    {
        var stopGate = new ScriptedStopGate(
            HostContracts.QueryRuntimeStopDecision.Continue(
                "Need another verification pass.",
                detailCode: "verification_incomplete"));
        var model = new ScriptedModelClient(
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("draft")]));
        Qre.IQueryRuntimeEngine engine = new Qre.QueryRuntimeEngine(model);

        var result = await engine.ExecuteAsync(
            new Qre.QueryRuntimeRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                MaxRounds = 2,
                EnableTools = false,
                StopGate = stopGate,
                MaxStopGateContinuations = 0
            },
            new CapturingEventSink(),
            "run-stop-fail",
            "/tmp/qre-test/events.jsonl",
            workspacePath: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(Qre.QueryTerminationReason.FailClosed, result.TerminationReason);
        Assert.Equal("verification_incomplete", result.TerminalDetailCode);
        Assert.Equal(1, result.TotalRounds);
        Assert.Contains(
            result.FinalMessages,
            message => message.Role == ChatRole.Assistant &&
                       ReadText(message) == "draft");
    }

    [Fact]
    public async Task ExecuteAsync_AfterToolHookFailureFailsClosedAndEmitsError()
    {
        var tool = AIFunctionFactory.Create(
            () => "success-result",
            new AIFunctionFactoryOptions { Name = "success_tool" });
        var intervention = new RecordingToolIntervention(
            after: _ => throw new InvalidOperationException("after hook failed"));
        var model = new ScriptedModelClient(
            new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call-success", "success_tool", new Dictionary<string, object?>())]));
        var sink = new CapturingEventSink();
        Qre.IQueryRuntimeEngine engine = new Qre.QueryRuntimeEngine(model);

        var result = await engine.ExecuteAsync(
            new Qre.QueryRuntimeRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                MaxRounds = 1,
                EnableTools = true,
                AvailableTools = [tool],
                ToolIntervention = intervention
            },
            sink,
            "run-after-hook-failure",
            "/tmp/qre-test/events.jsonl",
            workspacePath: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(Qre.QueryTerminationReason.FailClosed, result.TerminationReason);
        Assert.Equal("tool_intervention_after_failed", result.TerminalDetailCode);
        Assert.Contains(
            sink.Events,
            evt => evt is Qre.ErrorEvent error &&
                   error.ErrorType == "InvalidOperationException" &&
                   error.Message == "after hook failed");
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
        => string.Concat(message.Contents.Select(static content => content switch
        {
            TextContent text => text.Text,
            FunctionResultContent result => result.Result?.ToString() ?? string.Empty,
            _ => string.Empty
        }));

    private static string ThrowToolFailure()
        => throw new InvalidOperationException("tool failed");

    private sealed class RecordingToolIntervention(
        Func<HostContracts.QueryRuntimeToolCallContext, HostContracts.QueryRuntimeToolInterventionDecision>? before = null,
        Func<HostContracts.QueryRuntimeToolExecutionResultContext, ValueTask>? after = null)
        : HostContracts.IQueryRuntimeToolIntervention
    {
        public List<HostContracts.QueryRuntimeToolCallContext> BeforeCalls { get; } = [];

        public List<HostContracts.QueryRuntimeToolExecutionResultContext> AfterCalls { get; } = [];

        public ValueTask<HostContracts.QueryRuntimeToolInterventionDecision> BeforeToolCallAsync(
            HostContracts.QueryRuntimeToolCallContext context,
            CancellationToken ct = default)
        {
            BeforeCalls.Add(context);
            return ValueTask.FromResult(before?.Invoke(context) ?? HostContracts.QueryRuntimeToolInterventionDecision.Allow());
        }

        public ValueTask AfterToolExecutionAsync(
            HostContracts.QueryRuntimeToolExecutionResultContext context,
            CancellationToken ct = default)
        {
            AfterCalls.Add(context);
            return after?.Invoke(context) ?? ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedStopGate(params HostContracts.QueryRuntimeStopDecision[] decisions)
        : HostContracts.IQueryRuntimeStopGate
    {
        private readonly Queue<HostContracts.QueryRuntimeStopDecision> _decisions = new(decisions);

        public List<HostContracts.QueryRuntimeBeforeStopContext> Calls { get; } = [];

        public ValueTask<HostContracts.QueryRuntimeStopDecision> BeforeStopAsync(
            HostContracts.QueryRuntimeBeforeStopContext context,
            CancellationToken ct = default)
        {
            Calls.Add(context);
            return ValueTask.FromResult(_decisions.Count == 0
                ? HostContracts.QueryRuntimeStopDecision.Accept()
                : _decisions.Dequeue());
        }
    }

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
