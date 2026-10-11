using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Runtime.RuntimeLoopTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class RuntimeToolFinalizationTests
{
    private const string Instruction = "Stop calling tools and return the final JSON now.";

    private static readonly RuntimeResponseFormat ToolFreeJson =
        RuntimeResponseFormat.JsonObject(RuntimeResponseFormatScope.ToolFreeStepsOnly);

    private static RuntimeToolFinalizationPolicy Policy(
        int? afterToolCalls = null,
        TimeSpan? deadline = null,
        double fraction = 0.85,
        bool reserveFinalStep = false,
        RuntimeFinalizationToolCallHandling handling = RuntimeFinalizationToolCallHandling.IgnoreAndUseText)
        => new()
        {
            AfterToolCalls = afterToolCalls,
            Deadline = deadline,
            DeadlineFraction = fraction,
            ReserveFinalStep = reserveFinalStep,
            Instruction = Instruction,
            ToolCallHandling = handling
        };

    private static RuntimeAgentLoopRequest Request(
        RuntimeToolFinalizationPolicy? policy,
        int maxSteps = 10,
        IRuntimeToolExecutor? executor = null,
        IRuntimeTerminationPolicy? termination = null)
        => CreateRequest(
            tools: [ToolDescriptor("open_document")],
            executor: executor,
            parameters: new RuntimeModelParameters { ResponseFormat = ToolFreeJson },
            budget: new RuntimeBudgetSnapshot(maxSteps, 20, maxContinuations: 3),
            terminationPolicy: termination) with
        {
            ToolFinalization = policy
        };

    private static Func<RuntimeModelRequest, IAsyncEnumerable<RuntimeModelStreamEvent>> CallTool(string id)
        => _ => Tool(ToolCall(id, "open_document"));

    [Fact]
    public async Task ToolCallBudget_TriggersToolFreeFinalStepWithResponseFormat()
    {
        var model = new ScriptedModelClient(CallTool("c1"), CallTool("c2"), _ => Text("{\"ok\":true}"));
        var audit = new InMemoryRuntimeAuditSink();

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(Policy(afterToolCalls: 2)) with { AuditSink = audit });

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("{\"ok\":true}", result.FinalText);
        Assert.Equal(RuntimeToolFinalizationReason.ToolCallBudget, result.Turn.Progress.FinalizationReason);
        Assert.NotEmpty(model.Requests[1].Tools);
        Assert.Null(model.Requests[1].Parameters.ResponseFormat);
        var final = model.Requests[2];
        Assert.Empty(final.Tools);
        Assert.Equal(ToolFreeJson, final.Parameters.ResponseFormat);
        var instruction = final.Messages[^1];
        Assert.Equal(RuntimeMessageRole.User, instruction.Role);
        Assert.Equal(Instruction, Assert.IsType<RuntimeTextItem>(Assert.Single(instruction.Items)).Text);
        var triggered = Assert.Single(audit.Events, static e => e.Kind == RuntimeAuditEventKind.ToolFinalizationTriggered);
        var payload = Assert.IsType<RuntimeToolFinalizationAuditPayload>(triggered.Payload);
        Assert.Equal("tool_call_budget", payload.Reason);
        Assert.Equal(2, payload.ToolCallCount);
        Assert.Equal("turn-1:step:1", payload.AfterStepId.Value);
        var replay = RuntimeRecordedReplay.Replay(Recording(audit.Events));
        Assert.Equal(RuntimeTurnStatus.Completed, replay.Status);
    }

    [Fact]
    public async Task Deadline_UsesTimeProviderAndRecordsElapsedTime()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var executor = new RecordingToolExecutor(_ => clock.Advance(TimeSpan.FromSeconds(6)));
        var model = new ScriptedModelClient(CallTool("c1"), _ => Text("{}"));
        var audit = new InMemoryRuntimeAuditSink();

        var result = await RunLoopAsync(
            new RuntimeAgentLoop(model, clock),
            Request(Policy(deadline: TimeSpan.FromSeconds(10), fraction: 0.5), executor: executor) with { AuditSink = audit });

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(RuntimeToolFinalizationReason.Deadline, result.Turn.Progress.FinalizationReason);
        Assert.Empty(model.Requests[1].Tools);
        var payload = Assert.IsType<RuntimeToolFinalizationAuditPayload>(
            Assert.Single(audit.Events, static e => e.Kind == RuntimeAuditEventKind.ToolFinalizationTriggered).Payload);
        Assert.Equal("deadline", payload.Reason);
        Assert.Equal(6000, payload.ElapsedMs);
    }

    [Fact]
    public async Task Deadline_NotReachedKeepsTools()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var executor = new RecordingToolExecutor(_ => clock.Advance(TimeSpan.FromSeconds(4)));
        var model = new ScriptedModelClient(CallTool("c1"), _ => Text("{}"));

        var result = await RunLoopAsync(
            new RuntimeAgentLoop(model, clock),
            Request(Policy(deadline: TimeSpan.FromSeconds(10), fraction: 0.5), executor: executor));

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Null(result.Turn.Progress.FinalizationReason);
        Assert.NotEmpty(model.Requests[1].Tools);
    }

    [Fact]
    public async Task ReserveFinalStep_ReplacesStepBudgetFailureWithFinalAnswer()
    {
        var model = new ScriptedModelClient(CallTool("c1"), CallTool("c2"), _ => Text("{\"done\":1}"));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(Policy(reserveFinalStep: true), maxSteps: 3));

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(RuntimeToolFinalizationReason.StepReserve, result.Turn.Progress.FinalizationReason);
        Assert.Equal(3, model.Requests.Count);
        Assert.Empty(model.Requests[2].Tools);
    }

    [Fact]
    public async Task Unconfigured_KeepsStepBudgetExhaustedAndLegacyAudit()
    {
        var model = new ScriptedModelClient(CallTool("c1"), CallTool("c2"), CallTool("c3"));
        var audit = new InMemoryRuntimeAuditSink();

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(policy: null, maxSteps: 3) with { AuditSink = audit });

        Assert.Equal(RuntimeTerminationReason.MaxSteps, result.TerminationReason);
        Assert.Equal("step_budget_exhausted", result.Error!.Code);
        Assert.All(model.Requests, static request => Assert.NotEmpty(request.Tools));
        Assert.DoesNotContain(audit.Events, static e =>
            e.Kind is RuntimeAuditEventKind.ToolFinalizationTriggered or RuntimeAuditEventKind.TerminationDecided);
        Assert.Null(result.Turn.Progress.FinalizationReason);
    }

    [Fact]
    public async Task FinalizationToolCalls_AreIgnoredWithWarningWhenTextExists()
    {
        var executor = new RecordingToolExecutor();
        var model = new ScriptedModelClient(
            CallTool("c1"),
            _ => Events(
                new RuntimeTextDeltaEvent("{\"partial\":true}"),
                new RuntimeToolCallEvent(ToolCall("c2", "open_document")),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)));
        var audit = new InMemoryRuntimeAuditSink();

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(Policy(afterToolCalls: 1), executor: executor) with { AuditSink = audit });

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("{\"partial\":true}", result.FinalText);
        Assert.Single(executor.Calls);
        var output = result.Turn.Steps[^1].Output!;
        Assert.Empty(output.ToolCalls);
        Assert.Equal(RuntimeModelStopReason.EndTurn, output.StopReason);
        Assert.Contains(output.Warnings, static warning => warning.Code == "finalization_tool_calls_ignored");
        Assert.Equal(RuntimeTurnStatus.Completed, RuntimeRecordedReplay.Replay(Recording(audit.Events)).Status);
    }

    [Fact]
    public async Task FinalizationToolCalls_WithoutTextFailClosed()
    {
        var model = new ScriptedModelClient(CallTool("c1"), CallTool("c2"));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(Policy(afterToolCalls: 1)));

        Assert.Equal(RuntimeTerminationReason.FailClosed, result.TerminationReason);
        Assert.Equal("finalization_empty", result.Error!.Code);
    }

    [Fact]
    public async Task FinalizationToolCalls_FailClosedHandlingRejects()
    {
        var model = new ScriptedModelClient(
            CallTool("c1"),
            _ => Events(
                new RuntimeTextDeltaEvent("text"),
                new RuntimeToolCallEvent(ToolCall("c2", "open_document")),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)));

        var result = await RunLoopAsync(
            new RuntimeAgentLoop(model),
            Request(Policy(afterToolCalls: 1, handling: RuntimeFinalizationToolCallHandling.FailClosed)));

        Assert.Equal(RuntimeTerminationReason.FailClosed, result.TerminationReason);
        Assert.Equal("finalization_tool_calls_rejected", result.Error!.Code);
    }

    [Fact]
    public async Task Finalization_TriggersAtMostOncePerTurn()
    {
        // After finalization the Turn stays tool-free; a later continuation does not re-trigger.
        var termination = new SequencePolicy(
            RuntimeTerminationDecision.Continue("try again"),
            RuntimeTerminationDecision.Accept());
        var model = new ScriptedModelClient(CallTool("c1"), _ => Text("first"), _ => Text("second"));
        var audit = new InMemoryRuntimeAuditSink();

        var result = await RunLoopAsync(
            new RuntimeAgentLoop(model),
            Request(Policy(afterToolCalls: 1), termination: termination) with { AuditSink = audit });

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Empty(model.Requests[1].Tools);
        Assert.Empty(model.Requests[2].Tools);
        Assert.Single(audit.Events, static e => e.Kind == RuntimeAuditEventKind.ToolFinalizationTriggered);
        Assert.Single(result.History, static message => message.Items.OfType<RuntimeTextItem>().Any(static item => item.Text == Instruction));
    }

    [Fact]
    public async Task Resume_AfterTriggerDoesNotTriggerAgain()
    {
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var crashing = new ScriptedModelClient(
            CallTool("c1"),
            _ => Throw(new InvalidOperationException("process crashed")));
        await RunLoopAsync(new RuntimeAgentLoop(crashing), Request(Policy(afterToolCalls: 1)) with
        {
            CheckpointSink = checkpoints,
            RecoveryCompatibilityId = "finalization-test"
        });
        var checkpoint = checkpoints.Checkpoints.Last(static value => value.Kind == RuntimeCheckpointKind.ToolBatchCommitted);
        Assert.Equal(RuntimeToolFinalizationReason.ToolCallBudget, checkpoint.Session.ActiveTurn!.Progress.FinalizationReason);
        Assert.NotNull(checkpoint.Request.ToolFinalization);

        var resumedModel = new ScriptedModelClient(_ => Text("{\"resumed\":true}"));
        var audit = new InMemoryRuntimeAuditSink();
        var request = checkpoint.Request.ToLoopRequest() with
        {
            Attempt = RuntimeRunAttempt.Resume(checkpoint),
            CheckpointSink = new InMemoryRuntimeCheckpointSink(),
            ToolExecutor = new RecordingToolExecutor(),
            AuditSink = audit
        };
        var result = await new RuntimeAgentLoop(resumedModel).ResumeAsync(
            request,
            checkpoint,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Empty(Assert.Single(resumedModel.Requests).Tools);
        Assert.DoesNotContain(audit.Events, static e => e.Kind == RuntimeAuditEventKind.ToolFinalizationTriggered);
        Assert.Single(result.History, static message => message.Items.OfType<RuntimeTextItem>().Any(static item => item.Text == Instruction));
    }

    [Fact]
    public async Task DurableStores_PersistFinalizationStateAndKeepLegacyShape()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qre-finalization-" + Guid.NewGuid().ToString("N"));
        try
        {
            var checkpoints = new RuntimeJsonCheckpointStore(Path.Combine(directory, "checkpoint"));
            await using (var audit = RuntimeJsonlAuditStore.Create(directory, "public-run"))
            {
                await RunLoopAsync(
                    new RuntimeAgentLoop(new ScriptedModelClient(CallTool("c1"), _ => Text("{}"))),
                    Request(Policy(afterToolCalls: 1)) with
                    {
                        CheckpointSink = checkpoints,
                        RecoveryCompatibilityId = "finalization-store",
                        AuditSink = audit
                    });
            }
            var restored = RuntimeJsonCheckpointStore.Read(checkpoints.CheckpointPath);
            Assert.Equal(RuntimeToolFinalizationReason.ToolCallBudget, restored.Session.TerminalTurns[^1].Progress.FinalizationReason);
            Assert.Equal(Instruction, restored.Request.ToolFinalization!.Instruction);

            var publicText = string.Join('\n', Directory.EnumerateFiles(Path.Combine(directory, ".qre"), "*.jsonl", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
            Assert.Contains("\"decision\":\"tool_call_budget\"", publicText, StringComparison.Ordinal);
            Assert.DoesNotContain(Instruction, publicText, StringComparison.Ordinal);

            var legacy = RuntimeSessionState.Create(new RuntimeSessionId("s"));
            legacy = RuntimeStateReducer.StartTurn(legacy, new RuntimeTurnContext(legacy.SessionId, new RuntimeTurnId("t"), "o", DateTimeOffset.UnixEpoch));
            var legacyJson = System.Text.Json.JsonSerializer.Serialize(legacy, RuntimeCheckpointJsonContext.Default.RuntimeSessionState);
            Assert.DoesNotContain("finalizationReason", legacyJson, StringComparison.Ordinal);
            Assert.DoesNotContain("nextStepWithoutTools", legacyJson, StringComparison.Ordinal);
            Assert.DoesNotContain("toolFreeStepRequired", legacyJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ContinueWithoutTools_RunsOneToolFreeStepWithFormat()
    {
        var termination = new SequencePolicy(
            RuntimeTerminationDecision.ContinueWithoutTools("The JSON was invalid; output it again."),
            RuntimeTerminationDecision.Continue("Gather more evidence."),
            RuntimeTerminationDecision.Accept());
        var model = new ScriptedModelClient(
            CallTool("c1"),
            _ => Text("not json"),
            _ => Text("{\"fixed\":true}"),
            _ => Text("{\"final\":true}"));
        var audit = new InMemoryRuntimeAuditSink();

        var result = await RunLoopAsync(
            new RuntimeAgentLoop(model),
            Request(policy: null, termination: termination) with { AuditSink = audit });

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.NotEmpty(model.Requests[1].Tools);
        Assert.Empty(model.Requests[2].Tools);
        Assert.Equal(ToolFreeJson, model.Requests[2].Parameters.ResponseFormat);
        Assert.NotEmpty(model.Requests[3].Tools);
        Assert.Equal(2, result.Turn.Progress.ContinuationCount);
        var decision = Assert.IsType<RuntimeTerminationDecisionAuditPayload>(
            Assert.Single(audit.Events, static e => e.Kind == RuntimeAuditEventKind.TerminationDecided).Payload);
        Assert.Equal("continue_without_tools", decision.Decision);
        Assert.Equal(RuntimeTurnStatus.Completed, RuntimeRecordedReplay.Replay(Recording(audit.Events)).Status);
    }

    [Fact]
    public async Task ContinueWithoutTools_RespectsContinuationBudget()
    {
        var termination = new SequencePolicy(RuntimeTerminationDecision.ContinueWithoutTools("again"));
        var model = new ScriptedModelClient(_ => Text("x"));

        var result = await RunLoopAsync(
            new RuntimeAgentLoop(model),
            CreateRequest(budget: new RuntimeBudgetSnapshot(1, 1), terminationPolicy: termination));

        Assert.Equal(RuntimeTerminationReason.MaxSteps, result.TerminationReason);
        Assert.Equal("continuation_budget_exhausted", result.Error!.Code);
    }

    [Fact]
    public async Task Replay_RejectsToolsAfterFinalization()
    {
        var audit = new InMemoryRuntimeAuditSink();
        await RunLoopAsync(
            new RuntimeAgentLoop(new ScriptedModelClient(CallTool("c1"), _ => Text("{}"))),
            Request(Policy(afterToolCalls: 1)) with { AuditSink = audit });
        var events = audit.Events.ToArray();
        var index = Array.FindLastIndex(events, static e => e.Payload is RuntimeModelRequestAuditPayload);
        var payload = (RuntimeModelRequestAuditPayload)events[index].Payload;
        events[index] = events[index] with
        {
            Payload = payload with { Request = payload.Request with { Tools = [ToolDescriptor("open_document")] } }
        };

        var error = Assert.Throws<RuntimeAuditReplayException>(() => RuntimeRecordedReplay.Replay(Recording(events)));

        Assert.Equal("audit_tool_free_step_exposed_tools", error.Error.Code);
    }

    [Fact]
    public async Task Policy_ValidationRejectsInvalidValues()
    {
        var loop = new RuntimeAgentLoop(new ScriptedModelClient());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => RunLoopAsync(loop, Request(Policy(afterToolCalls: 0))));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => RunLoopAsync(loop, Request(Policy(deadline: TimeSpan.Zero))));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => RunLoopAsync(loop, Request(Policy(deadline: TimeSpan.FromSeconds(1), fraction: 1.5))));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => RunLoopAsync(loop, Request(Policy(afterToolCalls: 1) with { Instruction = " " })));
    }

    private static RuntimeAuditRecording Recording(IReadOnlyList<RuntimeAuditEnvelope> events)
        => new(RuntimeAuditDataMode.PrivateDiagnostic, RuntimeAuditReplayCapability.Recorded, events);

    private sealed class SequencePolicy(params RuntimeTerminationDecision[] decisions) : IRuntimeTerminationPolicy
    {
        private int _index;

        public ValueTask<RuntimeTerminationDecision> DecideAsync(RuntimeTerminationContext context, CancellationToken ct)
            => ValueTask.FromResult(decisions[Math.Min(_index++, decisions.Length - 1)]);
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
