using System.Collections.Concurrent;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Runtime.RuntimeLoopTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

/// <summary>
/// Boundary between the hard tool-call cap and opt-in tool finalization: when the Turn
/// reaches both AfterToolCalls and MaxToolCalls in the same batch, the accounted budget
/// denial hands over to the existing tool-free final Step instead of failing the Turn.
/// </summary>
public sealed class RuntimeToolFinalizationBoundaryTests
{
    private const string Instruction = "Stop calling tools and return the final JSON now.";
    private const string FinalJson = "{\"ok\":true}";

    // Real 12-37-01 r2 shape: 13 requested calls in batches 1,1,1,1,1,2,2,1,1,2 against a cap of 12.
    private static readonly int[] IncidentBatches = [1, 1, 1, 1, 1, 2, 2, 1, 1, 2];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThirteenRequestedTwelveExecuted_FinalizesWithoutTools(bool pipeline)
    {
        var tools = new ToolRecorder();
        var audit = new InMemoryRuntimeAuditSink();
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var model = new ScriptedModelClient([.. Batches(IncidentBatches), _ => Text(FinalJson)]);
        var request = Request(tools, pipeline, Policy(12)) with
        {
            AuditSink = audit,
            CheckpointSink = checkpoints,
            RecoveryCompatibilityId = "finalization-boundary"
        };

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), request);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(FinalJson, result.FinalText);
        Assert.Equal(RuntimeToolFinalizationReason.ToolCallBudget, result.Turn.Progress.FinalizationReason);
        Assert.Equal(12, result.Turn.Progress.ToolCallCount);
        Assert.Equal(12, tools.Ids.Count);
        Assert.Equal(12, tools.Ids.Distinct().Count());
        Assert.DoesNotContain("b10-c2", tools.Ids);

        var lastToolStep = result.Turn.Steps[^2].ToolInvocations!;
        Assert.Equal(RuntimeToolInvocationStatus.Succeeded, lastToolStep[0].Status);
        Assert.Equal(RuntimeToolInvocationStatus.Denied, lastToolStep[1].Status);
        var denial = lastToolStep[1].Result!.Error!;
        Assert.Equal("tool_call_budget_exhausted", denial.Code);
        // The denial speaks about the budget only; it must not suggest the material is absent.
        Assert.DoesNotContain("not found", denial.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("不存在", denial.Message, StringComparison.Ordinal);

        AssertEveryToolCallHasOneResult(result.History, expectedCalls: 13);
        Assert.Equal(IncidentBatches.Length + 1, model.Requests.Count);
        Assert.All(model.Requests.Take(IncidentBatches.Length), static sent => Assert.NotEmpty(sent.Tools));
        var final = model.Requests[^1];
        Assert.Empty(final.Tools);
        Assert.Equal(Instruction, Assert.IsType<RuntimeTextItem>(Assert.Single(final.Messages[^1].Items)).Text);

        var payload = Assert.IsType<RuntimeToolFinalizationAuditPayload>(
            Assert.Single(audit.Events, static e => e.Kind == RuntimeAuditEventKind.ToolFinalizationTriggered).Payload);
        Assert.Equal("tool_call_budget", payload.Reason);
        Assert.Equal(12, payload.ToolCallCount);
        Assert.Contains(checkpoints.Checkpoints, static value => value.Kind == RuntimeCheckpointKind.ToolBatchCommitted
            && value.Session.ActiveTurn!.Progress.FinalizationReason == RuntimeToolFinalizationReason.ToolCallBudget);

        var replay = RuntimeRecordedReplay.Replay(Recording(audit.Events));
        Assert.Equal(RuntimeTurnStatus.Completed, replay.Status);
        Assert.Equal(FinalJson, replay.FinalText);
        Assert.False(replay.ProviderCalls);
    }

    [Fact]
    public async Task DurableOverflowCheckpoint_WriteReadResume_SendsOnlyToolFreeFinalRequest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qre-finalization-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var checkpoint = await WriteDurableOverflowCheckpointAsync(directory);

            Assert.Equal(RuntimeCheckpointKind.ToolBatchCommitted, checkpoint.Kind);
            var turn = checkpoint.Session.ActiveTurn!;
            Assert.Equal(RuntimeToolFinalizationReason.ToolCallBudget, turn.Progress.FinalizationReason);
            Assert.Equal(12, turn.Progress.ToolCallCount);
            Assert.Equal("tool_call_budget_exhausted", turn.Steps[^1].ToolInvocations![1].Result!.Error!.Code);

            var resumedTools = new ToolRecorder();
            var resumedModel = new ScriptedModelClient(_ => Text(FinalJson));
            var request = checkpoint.Request.ToLoopRequest() with
            {
                Attempt = RuntimeRunAttempt.Resume(checkpoint),
                CheckpointSink = new RuntimeJsonCheckpointStore(Path.Combine(directory, "resumed")),
                ToolExecutor = resumedTools
            };
            var result = await new RuntimeAgentLoop(resumedModel).ResumeAsync(request, checkpoint, ct: TestContext.Current.CancellationToken);

            Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
            Assert.Equal(FinalJson, result.FinalText);
            Assert.Empty(Assert.Single(resumedModel.Requests).Tools);
            Assert.Empty(resumedTools.Ids);
            Assert.Equal(12, result.Turn.Progress.ToolCallCount);
            AssertEveryToolCallHasOneResult(result.History, expectedCalls: 13);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DurableOverflowCheckpoint_RejectsForgedOrIncompleteFinalizationState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qre-finalization-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var valid = await WriteDurableOverflowCheckpointAsync(directory);
            var turn = valid.Session.ActiveTurn!;
            var last = turn.Steps[^1];
            var denied = last.ToolInvocations![1];

            RuntimeCheckpointDocument WithTurn(RuntimeTurnState changed)
                => valid with { Session = valid.Session with { ActiveTurn = changed } };
            RuntimeCheckpointDocument WithDenied(RuntimeToolInvocationState changed)
                => WithTurn(turn with
                {
                    Steps = [.. turn.Steps.Take(turn.Steps.Count - 1), last with { ToolInvocations = [last.ToolInvocations[0], changed] }]
                });

            var forged = new Dictionary<string, RuntimeCheckpointDocument>
            {
                ["no_finalization_reason"] = WithTurn(turn with { Progress = turn.Progress with { FinalizationReason = null } }),
                ["deadline_reason"] = WithTurn(turn with { Progress = turn.Progress with { FinalizationReason = RuntimeToolFinalizationReason.Deadline } }),
                ["below_cap"] = WithTurn(turn with { Progress = turn.Progress with { ToolCallCount = 11 } }),
                ["no_policy"] = valid with { Request = valid.Request with { ToolFinalization = null } },
                ["no_call_threshold"] = valid with
                {
                    Request = valid.Request with { ToolFinalization = valid.Request.ToolFinalization! with { AfterToolCalls = null } }
                },
                ["wrong_category"] = WithDenied(denied with
                {
                    Result = denied.Result! with { Error = denied.Result.Error! with { Category = RuntimeErrorCategory.ProviderProtocol } }
                }),
                ["not_denied"] = WithDenied(denied with { Status = RuntimeToolInvocationStatus.Failed }),
                ["successful_result"] = WithDenied(denied with { Result = denied.Result! with { Success = true } }),
                ["missing_pair"] = WithDenied(denied with { Result = null })
            };

            // Control: the genuine document is accepted by a fresh durable store.
            await new RuntimeJsonCheckpointStore(Path.Combine(directory, "control")).SaveAsync(valid, TestContext.Current.CancellationToken);
            foreach (var (name, document) in forged)
            {
                var store = new RuntimeJsonCheckpointStore(Path.Combine(directory, "forged-" + name));
                var error = await Assert.ThrowsAsync<RuntimeResumeException>(
                    () => store.SaveAsync(document, TestContext.Current.CancellationToken).AsTask());
                // A ToolCallBudget finalization without its policy threshold is already refused by the
                // existing shape guard; every other forgery reaches the tool-batch boundary guard.
                var expected = name is "no_policy" or "no_call_threshold"
                    ? "checkpoint_shape_invalid"
                    : "checkpoint_stable_boundary_invalid";
                Assert.True(error.Error.Code == expected, $"{name}: {error.Error.Code}");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Runs the incident batches against a durable store and simulates a process stop right after
    // the overflow batch is committed: the store keeps that checkpoint, which is read back from disk.
    private static async Task<RuntimeCheckpointDocument> WriteDurableOverflowCheckpointAsync(string directory)
    {
        var store = new RuntimeJsonCheckpointStore(Path.Combine(directory, "checkpoint"));
        var sink = new StopAfterFinalizationSink(store);
        var model = new ScriptedModelClient([.. Batches(IncidentBatches), _ => Text(FinalJson)]);
        var request = Request(new ToolRecorder(), pipeline: false, Policy(12)) with
        {
            CheckpointSink = sink,
            RecoveryCompatibilityId = "finalization-boundary"
        };
        try
        {
            await RunLoopAsync(new RuntimeAgentLoop(model), request);
        }
        catch (ProcessStoppedException)
        {
        }
        Assert.True(sink.Stopped);
        Assert.Equal(IncidentBatches.Length, model.Requests.Count);
        return RuntimeJsonCheckpointStore.Read(store.CheckpointPath);
    }

    private sealed class ProcessStoppedException : Exception;

    private sealed class StopAfterFinalizationSink(RuntimeJsonCheckpointStore store) : IRuntimeCheckpointSink
    {
        public bool Stopped { get; private set; }

        public async ValueTask SaveAsync(RuntimeCheckpointDocument checkpoint, CancellationToken ct)
        {
            if (Stopped)
            {
                throw new ProcessStoppedException();
            }
            await store.SaveAsync(checkpoint, ct);
            if (checkpoint.Kind == RuntimeCheckpointKind.ToolBatchCommitted &&
                checkpoint.Session.ActiveTurn?.Progress.FinalizationReason == RuntimeToolFinalizationReason.ToolCallBudget)
            {
                Stopped = true;
                throw new ProcessStoppedException();
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithoutToolFinalization_RealOverflowStillFails(bool pipeline)
    {
        var tools = new ToolRecorder();
        var audit = new InMemoryRuntimeAuditSink();
        var model = new ScriptedModelClient([.. Batches(IncidentBatches)]);

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(tools, pipeline, policy: null) with { AuditSink = audit });

        Assert.Equal(RuntimeTurnStatus.Failed, result.Status);
        Assert.Equal("tool_call_budget_exhausted", result.Error!.Code);
        Assert.Equal(12, tools.Ids.Distinct().Count());
        Assert.Equal(IncidentBatches.Length, model.Requests.Count);
        Assert.DoesNotContain(audit.Events, static e => e.Kind == RuntimeAuditEventKind.ToolFinalizationTriggered);
        Assert.Equal("tool_call_budget_exhausted", RuntimeRecordedReplay.Replay(Recording(audit.Events)).Error!.Code);
    }

    [Fact]
    public async Task FinalizationWithoutCallThreshold_KeepsOverflowFailure()
    {
        // Finalization is enabled only by deadline; the hard cap alone never converts the failure.
        var tools = new ToolRecorder();
        var model = new ScriptedModelClient([.. Batches(IncidentBatches)]);
        var policy = new RuntimeToolFinalizationPolicy { Deadline = TimeSpan.FromHours(1), Instruction = Instruction };
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);

        var result = await RunLoopAsync(new RuntimeAgentLoop(model, clock), Request(tools, pipeline: true, policy));

        Assert.Equal(RuntimeTurnStatus.Failed, result.Status);
        Assert.Equal("tool_call_budget_exhausted", result.Error!.Code);
        Assert.Null(result.Turn.Progress.FinalizationReason);
    }

    [Fact]
    public async Task OtherTerminalToolError_IsNotSwallowedByFinalization()
    {
        // A terminal approval failure in the overflowing batch keeps the Turn failed with its own code.
        var tools = new ToolRecorder();
        var model = new ScriptedModelClient(
        [
            .. Batches(1, 1, 1, 1, 1, 2, 2, 1, 1),
            _ => Tool(ToolCall("boom", "open_document"), ToolCall("b10-c1", "open_document"), ToolCall("b10-c2", "open_document")),
            _ => Text(FinalJson)
        ]);
        var request = Request(tools, pipeline: true, Policy(12), new ApprovalRequiredPolicy("boom"));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), request);

        Assert.Equal(RuntimeTurnStatus.Failed, result.Status);
        Assert.Equal(RuntimeTerminationReason.FailClosed, result.TerminationReason);
        Assert.Equal("bound_approval_unavailable", result.Error!.Code);
        Assert.Equal("tool_call_budget_exhausted", result.Turn.Steps[^1].ToolInvocations![2].Result!.Error!.Code);
        Assert.Null(result.Turn.Progress.FinalizationReason);
        Assert.DoesNotContain("boom", tools.Ids);
    }

    [Fact]
    public async Task UnknownToolInOverflowBatch_StaysDeniedAndPaired()
    {
        var tools = new ToolRecorder();
        var model = new ScriptedModelClient(
        [
            .. Batches(1, 1, 1, 1, 1, 2, 2, 1, 1),
            _ => Tool(ToolCall("ghost", "missing_tool"), ToolCall("b10-c1", "open_document"), ToolCall("b10-c2", "open_document")),
            _ => Text(FinalJson)
        ]);

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(tools, pipeline: true, Policy(12)));

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        var batch = result.Turn.Steps[^2].ToolInvocations!;
        Assert.Equal("tool_not_exposed_in_context", batch[0].Result!.Error!.Code);
        Assert.Equal(RuntimeToolInvocationStatus.Succeeded, batch[1].Status);
        Assert.Equal("tool_call_budget_exhausted", batch[2].Result!.Error!.Code);
        Assert.Equal(12, tools.Ids.Count);
        AssertEveryToolCallHasOneResult(result.History, expectedCalls: 14);
    }

    [Fact]
    public async Task ToolFreeFinalStepReturningTools_StillFails()
    {
        var tools = new ToolRecorder();
        var model = new ScriptedModelClient([.. Batches(IncidentBatches), _ => Tool(ToolCall("again", "open_document"))]);

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(tools, pipeline: true, Policy(12)));

        Assert.Equal(RuntimeTerminationReason.FailClosed, result.TerminationReason);
        Assert.Equal("finalization_empty", result.Error!.Code);
        Assert.Equal(12, tools.Ids.Count);
        Assert.DoesNotContain("again", tools.Ids);
        Assert.Empty(model.Requests[^1].Tools);
    }

    [Fact]
    public async Task NoStepLeftAfterOverflowBatch_KeepsOverflowFailureWithoutExtraStep()
    {
        var tools = new ToolRecorder();
        var model = new ScriptedModelClient([.. Batches(IncidentBatches), _ => Text(FinalJson)]);

        var result = await RunLoopAsync(
            new RuntimeAgentLoop(model),
            Request(tools, pipeline: true, Policy(12) with { ReserveFinalStep = false }, maxSteps: IncidentBatches.Length));

        // No Step remains for a final answer, so the accounted overflow keeps its original failure.
        Assert.Equal("tool_call_budget_exhausted", result.Error!.Code);
        Assert.Equal(RuntimeTerminationReason.Error, result.TerminationReason);
        Assert.Null(result.Turn.Progress.FinalizationReason);
        Assert.Equal(IncidentBatches.Length, model.Requests.Count);
        Assert.Equal(12, tools.Ids.Count);
    }

    [Fact]
    public async Task DeadlineAlreadyFinalized_NeverReachesTheCap()
    {
        // A deadline that fires first makes the Turn tool-free before the cap; no extra calls run.
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var tools = new ToolRecorder(() => clock.Advance(TimeSpan.FromSeconds(1)));
        var model = new ScriptedModelClient([.. Batches(IncidentBatches), _ => Text(FinalJson)]);
        var policy = Policy(12) with { Deadline = TimeSpan.FromSeconds(10), DeadlineFraction = 0.5 };

        var result = await RunLoopAsync(new RuntimeAgentLoop(model, clock), Request(tools, pipeline: true, policy));

        Assert.Equal(RuntimeTurnStatus.Failed, result.Status);
        Assert.Equal(RuntimeToolFinalizationReason.Deadline, result.Turn.Progress.FinalizationReason);
        Assert.True(tools.Ids.Count < 12);
        Assert.Empty(model.Requests[^1].Tools);
    }

    private static RuntimeToolFinalizationPolicy Policy(int afterToolCalls)
        => new() { AfterToolCalls = afterToolCalls, Instruction = Instruction };

    private static RuntimeAgentLoopRequest Request(
        ToolRecorder tools,
        bool pipeline,
        RuntimeToolFinalizationPolicy? policy,
        IRuntimeToolPolicyEvaluator? toolPolicy = null,
        int maxSteps = 20)
    {
        var budget = new RuntimeBudgetSnapshot(maxSteps, 12);
        RuntimeAgentLoopRequest request;
        if (pipeline)
        {
            var toolPipeline = new RuntimeToolExecutionPipeline(new RuntimeToolRegistry([tools]), toolPolicy ?? new RuntimeAllowToolPolicy());
            request = CreateRequest(tools: toolPipeline.Descriptors, budget: budget) with { ToolExecutor = null, ToolPipeline = toolPipeline };
        }
        else
        {
            request = CreateRequest(tools: [ToolDescriptor("open_document")], executor: tools, budget: budget);
        }
        return request with { ToolFinalization = policy };
    }

    private static Func<RuntimeModelRequest, IAsyncEnumerable<RuntimeModelStreamEvent>>[] Batches(params int[] sizes)
        => sizes.Select((size, batch) => (Func<RuntimeModelRequest, IAsyncEnumerable<RuntimeModelStreamEvent>>)(_ => Tool(
                Enumerable.Range(1, size)
                    .Select(call => ToolCall($"b{batch + 1}-c{call}", "open_document"))
                    .ToArray())))
            .ToArray();

    private static void AssertEveryToolCallHasOneResult(IReadOnlyList<RuntimeMessage> history, int expectedCalls)
    {
        var calls = history.SelectMany(static message => message.Items).OfType<RuntimeToolCallItem>()
            .Select(static item => item.Call.InvocationId.Value).ToList();
        var results = history.SelectMany(static message => message.Items).OfType<RuntimeToolResultItem>()
            .Select(static item => item.Result.InvocationId.Value).ToList();
        Assert.Equal(expectedCalls, calls.Count);
        Assert.Equal(calls, results);
    }

    private static RuntimeAuditRecording Recording(IReadOnlyList<RuntimeAuditEnvelope> events)
        => new(RuntimeAuditDataMode.PrivateDiagnostic, RuntimeAuditReplayCapability.Recorded, events);

    private sealed class ApprovalRequiredPolicy(string invocationId) : IRuntimeToolPolicyEvaluator
    {
        public ValueTask<RuntimeToolPolicyDecision> EvaluateAsync(RuntimeToolPolicyContext context, CancellationToken ct)
            => ValueTask.FromResult(context.Call.InvocationId.Value == invocationId
                ? RuntimeToolPolicyDecision.RequireApproval("needs approval")
                : RuntimeToolPolicyDecision.Allow());
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>Records each executed invocation id; serves as both a pipeline tool and a plain executor.</summary>
    private sealed class ToolRecorder(Action? onExecute = null) : IRuntimeTool, IRuntimeToolExecutor
    {
        private readonly ConcurrentQueue<string> _ids = new();

        public IReadOnlyList<string> Ids => [.. _ids];

        public RuntimeToolDefinition Definition { get; } = new(
            ToolDescriptor("open_document") with { Version = "1.0.0" },
            new HashSet<string>(StringComparer.Ordinal),
            RuntimeToolConcurrency.Serial,
            RuntimeSandboxRequirements.None,
            RuntimeToolLimits.Default);

        public ValueTask<RuntimeToolResult> InvokeAsync(
            RuntimeToolInvocation invocation,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
            => Record(invocation.OriginalCall);

        public ValueTask<RuntimeToolResult> ExecuteAsync(
            RuntimeToolDescriptor descriptor,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
            => Record(call);

        private ValueTask<RuntimeToolResult> Record(RuntimeToolCall call)
        {
            _ids.Enqueue(call.InvocationId.Value);
            onExecute?.Invoke();
            return ValueTask.FromResult(new RuntimeToolResult(call.InvocationId, "ok", true,
                Details: new RuntimeToolResultDetails(RuntimeToolOutcome.Succeeded)));
        }
    }
}
