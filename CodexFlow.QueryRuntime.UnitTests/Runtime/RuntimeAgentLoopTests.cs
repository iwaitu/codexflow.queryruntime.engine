using System.Runtime.CompilerServices;
using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class RuntimeAgentLoopTests
{
    [Fact]
    public async Task PlainText_CompletesWithExplicitUsageAndStopReason()
    {
        var model = new ScriptedModelClient(
            _ => Events(
                new RuntimeTextDeltaEvent("hello"),
                new RuntimeUsageEvent(new RuntimeUsage(10, 3, 13)),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), CreateRequest());

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(RuntimeTerminationReason.Completed, result.TerminationReason);
        Assert.Equal("hello", result.FinalText);
        Assert.Equal(10, result.Usage.InputTokens);
        Assert.Equal(3, result.Usage.OutputTokens);
        Assert.Equal(RuntimeModelStopReason.EndTurn, result.Turn.Progress.LastModelStopReason);
        Assert.Equal(1, result.Session.HistoryVersion);
    }

    [Fact]
    public async Task MixedTextAndMultipleTools_PreservesOrderAndCommitsEveryObservation()
    {
        var firstCall = ToolCall("call-1", "read_file", "{\"path\":\"a.txt\"}");
        var secondCall = ToolCall("call-2", "read_file", "{\"path\":\"b.txt\"}");
        var model = new ScriptedModelClient(
            _ => Events(
                new RuntimeTextDeltaEvent("checking "),
                new RuntimeToolCallEvent(firstCall),
                new RuntimeReasoningDeltaEvent("between"),
                new RuntimeToolCallEvent(secondCall),
                new RuntimeUsageEvent(new RuntimeUsage(20, 5, 25)),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)),
            _ => Events(
                new RuntimeTextDeltaEvent("done"),
                new RuntimeUsageEvent(new RuntimeUsage(30, 2, 32)),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)));
        var executor = new RecordingToolExecutor();

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), CreateRequest(
            tools: [ToolDescriptor("read_file")],
            executor: executor,
            budget: new RuntimeBudgetSnapshot(3, 5)));

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(["call-1", "call-2"], executor.Calls.Select(static call => call.InvocationId.Value));
        Assert.Equal(2, result.Turn.Progress.ToolCallCount);
        Assert.Equal(50, result.Usage.InputTokens);
        Assert.Equal(7, result.Usage.OutputTokens);
        var firstAssistant = Assert.Single(result.History, message =>
            message.Role == RuntimeMessageRole.Assistant &&
            message.Items.OfType<RuntimeToolCallItem>().Any());
        Assert.Collection(
            firstAssistant.Items,
            item => Assert.IsType<RuntimeTextItem>(item),
            item => Assert.Equal(firstCall.InvocationId, Assert.IsType<RuntimeToolCallItem>(item).Call.InvocationId),
            item => Assert.IsType<RuntimeReasoningItem>(item),
            item => Assert.Equal(secondCall.InvocationId, Assert.IsType<RuntimeToolCallItem>(item).Call.InvocationId));
        var observations = result.History
            .Where(static message => message.Role == RuntimeMessageRole.Tool)
            .SelectMany(static message => message.Items)
            .OfType<RuntimeToolResultItem>()
            .ToArray();
        Assert.Equal(2, observations.Length);
        Assert.All(observations, static observation => Assert.True(observation.Result.Success));
    }

    [Fact]
    public async Task RequiredTool_ContinuesUntilSuccessfulToolObservation()
    {
        var call = ToolCall("required-1", "inspect", "{}");
        var model = new ScriptedModelClient(
            _ => Events(
                new RuntimeTextDeltaEvent("I am finished."),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)),
            request =>
            {
                Assert.Equal("inspect", request.Parameters.RequiredToolName);
                return Events(
                    new RuntimeToolCallEvent(call),
                    new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall));
            },
            request =>
            {
                Assert.Null(request.Parameters.RequiredToolName);
                return Events(
                    new RuntimeTextDeltaEvent("verified"),
                    new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn));
            });

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), CreateRequest(
            tools: [ToolDescriptor("inspect")],
            executor: new RecordingToolExecutor(),
            parameters: new RuntimeModelParameters(RequiredToolName: "inspect"),
            budget: new RuntimeBudgetSnapshot(3, 3, maxContinuations: 2)));

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.True(result.Turn.Progress.RequiredToolSatisfied);
        Assert.Equal("inspect", result.Turn.Progress.RequiredToolName);
        Assert.Equal(1, result.Turn.Progress.ContinuationCount);
        Assert.Equal(3, result.Turn.Steps.Count);
    }

    [Fact]
    public async Task StopPolicyAndMaxOutputTokens_CreateNewSemanticSteps()
    {
        var model = new ScriptedModelClient(
            _ => Events(
                new RuntimeTextDeltaEvent("partial"),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.MaxOutputTokens)),
            _ => Events(
                new RuntimeTextDeltaEvent("candidate"),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)),
            _ => Events(
                new RuntimeTextDeltaEvent("accepted"),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)));
        var policy = new OneContinuationPolicy();

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), CreateRequest(
            budget: new RuntimeBudgetSnapshot(3, 0, maxContinuations: 2),
            terminationPolicy: policy));

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("accepted", result.FinalText);
        Assert.Equal(2, result.Turn.Progress.ContinuationCount);
        Assert.Equal(2, policy.Calls);
        Assert.Equal(3, model.Requests.Count);
    }

    [Fact]
    public async Task RetryableFailureBeforeFirstEvent_RetriesWithinSameStep()
    {
        var model = new ScriptedModelClient(
            _ => ThrowModel(new RuntimeError(
                RuntimeErrorCategory.ProviderTransport,
                "temporary",
                "temporary",
                Retryable: true)),
            _ => Events(
                new RuntimeTextDeltaEvent("recovered"),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), CreateRequest(
            budget: new RuntimeBudgetSnapshot(1, 0, maxModelRetries: 1)));

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(2, Assert.Single(result.Turn.Steps).ModelAttempts);
        Assert.Equal(result.Turn.Steps[0].Context.StepId, model.Requests[0].StepId);
        Assert.Equal(model.Requests[0].StepId, model.Requests[1].StepId);
    }

    [Fact]
    public async Task RetryableFailureAfterPartialOutput_DoesNotReplayTheStep()
    {
        var model = new ScriptedModelClient(
            _ => PartialThenThrow(new RuntimeError(
                RuntimeErrorCategory.ProviderTransport,
                "interrupted",
                "interrupted",
                Retryable: true)));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), CreateRequest(
            budget: new RuntimeBudgetSnapshot(1, 0, maxModelRetries: 2)));

        Assert.Equal(RuntimeTurnStatus.Failed, result.Status);
        Assert.Equal("interrupted", result.Error!.Code);
        Assert.Single(model.Requests);
        Assert.Equal(1, Assert.Single(result.Turn.Steps).ModelAttempts);
    }

    [Fact]
    public async Task EmptyOrMalformedStream_FailsClosed()
    {
        var empty = await RunLoopAsync(
            new RuntimeAgentLoop(new ScriptedModelClient(
                _ => Events(new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)))),
            CreateRequest());
        var malformed = await RunLoopAsync(
            new RuntimeAgentLoop(new ScriptedModelClient(
                _ => Events(new RuntimeTextDeltaEvent("unterminated")))),
            CreateRequest());

        Assert.Equal(RuntimeTerminationReason.FailClosed, empty.TerminationReason);
        Assert.Equal("empty_model_response", empty.Error!.Code);
        Assert.Equal(RuntimeTerminationReason.FailClosed, malformed.TerminationReason);
        Assert.Equal("missing_model_completion", malformed.Error!.Code);
    }

    [Fact]
    public async Task ToolBudget_DeniesOverflowCallAndPreservesObservationBeforeFailure()
    {
        var model = new ScriptedModelClient(_ => Events(
            new RuntimeToolCallEvent(ToolCall("call-1", "read_file", "{}")),
            new RuntimeToolCallEvent(ToolCall("call-2", "read_file", "{}")),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)));
        var executor = new RecordingToolExecutor();

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), CreateRequest(
            tools: [ToolDescriptor("read_file")],
            executor: executor,
            budget: new RuntimeBudgetSnapshot(2, 1)));

        Assert.Equal(RuntimeTurnStatus.Failed, result.Status);
        Assert.Equal("tool_call_budget_exhausted", result.Error!.Code);
        Assert.Single(executor.Calls);
        var invocations = Assert.Single(result.Turn.Steps).ToolInvocations!;
        Assert.Equal(RuntimeToolInvocationStatus.Succeeded, invocations[0].Status);
        Assert.Equal(RuntimeToolInvocationStatus.Denied, invocations[1].Status);
        Assert.NotNull(invocations[1].Result);
    }

    [Fact]
    public async Task StepSnapshotAndExecutionCatalog_RemainConsistentWhenCallerMutatesTools()
    {
        var tools = new List<RuntimeToolDescriptor> { ToolDescriptor("read_file") };
        var call = ToolCall("snapshot-call", "read_file", "{}");
        var model = new ScriptedModelClient(
            request =>
            {
                Assert.Single(request.Tools);
                tools.Clear();
                return Events(
                    new RuntimeToolCallEvent(call),
                    new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall));
            },
            _ => Events(
                new RuntimeTextDeltaEvent("done"),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)));
        var executor = new RecordingToolExecutor();

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), CreateRequest(
            tools: tools,
            executor: executor,
            budget: new RuntimeBudgetSnapshot(2, 1)));

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Single(executor.Calls);
        Assert.Single(result.Turn.Steps[0].Context.ModelRequest.Tools);
        Assert.Equal("read_file", result.Turn.Steps[0].Context.ModelRequest.Tools[0].CanonicalName);
    }

    [Fact]
    public async Task TokenBudget_DeniesAllEmittedToolCallsBeforeExecution()
    {
        var model = new ScriptedModelClient(_ => Events(
            new RuntimeToolCallEvent(ToolCall("call-1", "read_file", "{}")),
            new RuntimeUsageEvent(new RuntimeUsage(101, 1, 102)),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)));
        var executor = new RecordingToolExecutor();

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), CreateRequest(
            tools: [ToolDescriptor("read_file")],
            executor: executor,
            budget: new RuntimeBudgetSnapshot(2, 2, maxInputTokens: 100)));

        Assert.Equal(RuntimeTurnStatus.Failed, result.Status);
        Assert.Equal("token_budget_exhausted", result.Error!.Code);
        Assert.Empty(executor.Calls);
        var invocation = Assert.Single(Assert.Single(result.Turn.Steps).ToolInvocations!);
        Assert.Equal(RuntimeToolInvocationStatus.Denied, invocation.Status);
        Assert.Equal("token_budget_exhausted", invocation.Result!.Error!.Code);
    }

    [Fact]
    public async Task ToolCatalogThatConsumesContextBudget_FailsClosedBeforeSampling()
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            description = new string('s', 1_000)
        });
        var tool = new RuntimeToolDescriptor(
            "oversized_tool",
            "1",
            new string('d', 1_000),
            schema,
            RuntimeToolSideEffect.ReadOnly,
            RuntimeToolIdempotency.Idempotent);
        var model = new ScriptedModelClient();
        var request = CreateRequest(tools: [tool], executor: new RecordingToolExecutor()) with
        {
            ContextManager = new RuntimeContextManager(new RuntimeContextOptions
            {
                MaxContextTokens = 220,
                MaxItemTokens = 100,
                MaxToolResultTokens = 80,
                LargeToolResultTokens = 60,
                SummaryTokens = 50,
                RecentTrajectoryMessages = 6
            })
        };

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), request);

        Assert.Equal(RuntimeTurnStatus.Failed, result.Status);
        Assert.Equal(RuntimeTerminationReason.FailClosed, result.TerminationReason);
        Assert.Equal("tool_catalog_context_budget_exhausted", result.Error!.Code);
        Assert.Empty(model.Requests);
        Assert.Empty(result.PreparedContexts);
    }

    [Fact]
    public async Task ProvisionalHandle_SupportsApprovalAndSteering()
    {
        var handle = new RuntimeTurnHandle();
        var call = ToolCall("approval-1", "write_file", "{}");
        var model = new ScriptedModelClient(
            _ => Events(
                new RuntimeToolCallEvent(call),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)),
            request =>
            {
                Assert.Contains(request.Messages, message => message.Items
                    .OfType<RuntimeTextItem>()
                    .Any(item => item.Text == "host steering"));
                return Events(
                    new RuntimeTextDeltaEvent("done"),
                    new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn));
            });
        var executor = new RecordingToolExecutor(onExecute: _ =>
        {
            handle.Steer(new RuntimeMessage(
                RuntimeMessageRole.User,
                [new RuntimeTextItem("host steering")]));
        });
        handle.Approve(call.InvocationId);

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), CreateRequest(
            tools: [ToolDescriptor("write_file")],
            executor: executor,
            authorization: new RequireApprovalAuthorization(),
            budget: new RuntimeBudgetSnapshot(3, 2, maxContinuations: 1)), handle);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(RuntimeToolInvocationStatus.Succeeded,
            result.Turn.Steps[0].ToolInvocations![0].Status);
        Assert.Contains(result.History, message => message.Items
            .OfType<RuntimeTextItem>()
            .Any(item => item.Text == "host steering"));
    }

    [Fact]
    public async Task CancellationDuringModelApprovalAndTool_IsNeverReportedAsSuccess()
    {
        using var modelHandle = new RuntimeTurnHandle();
        var blockingModel = new BlockingModelClient();
        var modelTask = RunLoopAsync(new RuntimeAgentLoop(blockingModel), CreateRequest(), modelHandle);
        await WaitForSignalAsync(blockingModel.Started.Task);
        modelHandle.Cancel();
        var modelResult = await modelTask;

        using var approvalHandle = new RuntimeTurnHandle();
        var approval = new SignallingApprovalAuthorization();
        var approvalCall = ToolCall("approval-cancel", "write_file", "{}");
        var approvalTask = RunLoopAsync(
            new RuntimeAgentLoop(new ScriptedModelClient(_ => Events(
                new RuntimeToolCallEvent(approvalCall),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)))),
            CreateRequest(
                tools: [ToolDescriptor("write_file")],
                executor: new RecordingToolExecutor(),
                authorization: approval,
                budget: new RuntimeBudgetSnapshot(2, 2)),
            approvalHandle);
        await WaitForSignalAsync(approval.Evaluated.Task);
        approvalHandle.Cancel();
        var approvalResult = await approvalTask;

        using var toolHandle = new RuntimeTurnHandle();
        var blockingTool = new BlockingToolExecutor();
        var toolCall = ToolCall("tool-cancel", "write_file", "{}");
        var toolTask = RunLoopAsync(
            new RuntimeAgentLoop(new ScriptedModelClient(_ => Events(
                new RuntimeToolCallEvent(toolCall),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)))),
            CreateRequest(
                tools: [ToolDescriptor("write_file")],
                executor: blockingTool,
                budget: new RuntimeBudgetSnapshot(2, 2)),
            toolHandle);
        await WaitForSignalAsync(blockingTool.Started.Task);
        toolHandle.Cancel();
        var toolResult = await toolTask;

        Assert.All([modelResult, approvalResult, toolResult], result =>
        {
            Assert.Equal(RuntimeTurnStatus.Cancelled, result.Status);
            Assert.Equal(RuntimeTerminationReason.Cancelled, result.TerminationReason);
        });
        Assert.Equal(1, modelResult.Turn.Steps[0].ModelAttempts);
        Assert.Equal(RuntimeToolInvocationStatus.Cancelled,
            approvalResult.Turn.Steps[0].ToolInvocations![0].Status);
        Assert.Equal(RuntimeToolInvocationStatus.Cancelled,
            toolResult.Turn.Steps[0].ToolInvocations![0].Status);
    }

    [Fact]
    public async Task SameScript_ProducesEquivalentTerminalState()
    {
        static RuntimeAgentLoop CreateLoop() => new(new ScriptedModelClient(_ => Events(
            new RuntimeTextDeltaEvent("same"),
            new RuntimeUsageEvent(new RuntimeUsage(1, 1, 2)),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn))));
        var request = CreateRequest(createdAt: DateTimeOffset.UnixEpoch);

        var first = await RunLoopAsync(CreateLoop(), request);
        var second = await RunLoopAsync(CreateLoop(), request);

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.TerminationReason, second.TerminationReason);
        Assert.Equal(first.Turn.Context, second.Turn.Context);
        Assert.Equal(first.Turn.Progress.RequiredToolName, second.Turn.Progress.RequiredToolName);
        Assert.Equal(first.Turn.Progress.RequiredToolSatisfied, second.Turn.Progress.RequiredToolSatisfied);
        Assert.Equal(first.Turn.Progress.ContinuationCount, second.Turn.Progress.ContinuationCount);
        Assert.Equal(first.Turn.Progress.ToolCallCount, second.Turn.Progress.ToolCallCount);
        Assert.Equal(first.Turn.Progress.LastModelStopReason, second.Turn.Progress.LastModelStopReason);
        Assert.Equal(first.Turn.Progress.Usage.InputTokens, second.Turn.Progress.Usage.InputTokens);
        Assert.Equal(first.Turn.Progress.Usage.OutputTokens, second.Turn.Progress.Usage.OutputTokens);
        Assert.Equal(first.Turn.Progress.Usage.TotalTokens, second.Turn.Progress.Usage.TotalTokens);
        Assert.Equal(first.Turn.Progress.Usage.Additional, second.Turn.Progress.Usage.Additional);
        Assert.Equal(first.Turn.Steps[0].Context.StepId, second.Turn.Steps[0].Context.StepId);
        Assert.Equal(first.Turn.Steps[0].Phase, second.Turn.Steps[0].Phase);
        Assert.Equal(first.FinalText, second.FinalText);
    }

    private static RuntimeAgentLoopRequest CreateRequest(
        IReadOnlyList<RuntimeToolDescriptor>? tools = null,
        IRuntimeToolExecutor? executor = null,
        RuntimeModelParameters? parameters = null,
        RuntimeBudgetSnapshot? budget = null,
        IRuntimeToolAuthorization? authorization = null,
        IRuntimeTerminationPolicy? terminationPolicy = null,
        DateTimeOffset? createdAt = null)
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
            CreatedAt: createdAt ?? DateTimeOffset.UnixEpoch)
        {
            ToolExecutor = executor,
            ToolAuthorization = authorization,
            TerminationPolicy = terminationPolicy
        };

    private static Task<RuntimeAgentLoopResult> RunLoopAsync(
        RuntimeAgentLoop loop,
        RuntimeAgentLoopRequest request,
        RuntimeTurnHandle? handle = null)
        => loop.RunAsync(request, handle, TestContext.Current.CancellationToken);

    private static Task WaitForSignalAsync(Task signal)
        => signal.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private static RuntimeToolDescriptor ToolDescriptor(string name)
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

    private static RuntimeToolCall ToolCall(string invocationId, string name, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new RuntimeToolCall(
            new RuntimeInvocationId(invocationId),
            name,
            document.RootElement.Clone());
    }

    private static async IAsyncEnumerable<RuntimeModelStreamEvent> Events(
        params RuntimeModelStreamEvent[] events)
    {
        foreach (var runtimeEvent in events)
        {
            await Task.Yield();
            yield return runtimeEvent;
        }
    }

    private static async IAsyncEnumerable<RuntimeModelStreamEvent> ThrowModel(
        RuntimeError error,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        throw new RuntimeModelClientException(error);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<RuntimeModelStreamEvent> PartialThenThrow(
        RuntimeError error)
    {
        yield return new RuntimeTextDeltaEvent("partial");
        await Task.Yield();
        throw new RuntimeModelClientException(error);
    }

    private sealed class ScriptedModelClient(
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
            var stream = scripts[index](request);
            return WithCancellation(stream, ct);
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

    private sealed class RecordingToolExecutor(Action<RuntimeToolCall>? onExecute = null) : IRuntimeToolExecutor
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

    private sealed class BlockingModelClient : IRuntimeModelClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
            RuntimeModelRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }
    }

    private sealed class BlockingToolExecutor : IRuntimeToolExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<RuntimeToolResult> ExecuteAsync(
            RuntimeToolDescriptor descriptor,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new RuntimeToolResult(call.InvocationId, "unreachable", true);
        }
    }

    private sealed class RequireApprovalAuthorization : IRuntimeToolAuthorization
    {
        public ValueTask<RuntimeToolAuthorizationDecision> AuthorizeAsync(
            RuntimeToolDescriptor descriptor,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
            => ValueTask.FromResult(RuntimeToolAuthorizationDecision.RequireApproval());
    }

    private sealed class SignallingApprovalAuthorization : IRuntimeToolAuthorization
    {
        public TaskCompletionSource Evaluated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<RuntimeToolAuthorizationDecision> AuthorizeAsync(
            RuntimeToolDescriptor descriptor,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
        {
            Evaluated.TrySetResult();
            return ValueTask.FromResult(RuntimeToolAuthorizationDecision.RequireApproval());
        }
    }

    private sealed class OneContinuationPolicy : IRuntimeTerminationPolicy
    {
        public int Calls { get; private set; }

        public ValueTask<RuntimeTerminationDecision> DecideAsync(
            RuntimeTerminationContext context,
            CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(Calls == 1
                ? RuntimeTerminationDecision.Continue("verify again")
                : RuntimeTerminationDecision.Accept());
        }
    }
}
