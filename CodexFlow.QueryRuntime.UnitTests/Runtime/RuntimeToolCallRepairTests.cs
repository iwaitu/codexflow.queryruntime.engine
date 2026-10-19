using System.Runtime.CompilerServices;
using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Runtime.RuntimeLoopTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

/// <summary>E2 / Q6 bounded tool-call repair.</summary>
public sealed class RuntimeToolCallRepairTests
{
    private const string ToolName = "open_document";
    private const string RepairedCall = "{\"tool_calls\":[{\"name\":\"open_document\",\"arguments\":{\"path\":\"a.md\"}}]}";

    [Fact]
    public void TriggerCodes_MatchTheModelsClassifier()
        => Assert.Equal(QreModelErrorClassifier.MalformedToolCallCode, RuntimeToolCallRepairCodes.TriggerMalformedToolCall);

    [Theory]
    [InlineData("", 2)]
    [InlineData("repair", 0)]
    [InlineData("repair", 3)]
    public void Policy_ValidatesInstructionAndRange(string instruction, int maxPerTurn)
    {
        var policy = new RuntimeToolCallRepairPolicy { Instruction = instruction, MaxPerTurn = maxPerTurn };
        Assert.ThrowsAny<ArgumentException>(policy.Validate);
    }

    [Fact]
    public async Task Policy_RequiresAStepAfterARecoveredCall()
    {
        var model = new ScriptedModelClient(_ => Text("unused"));
        await Assert.ThrowsAsync<ArgumentException>(() => RunLoopAsync(
            new RuntimeAgentLoop(model),
            Request(new RecordingToolExecutor(), budget: new RuntimeBudgetSnapshot(1, 1))));
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task MalformedToolCall_RecoversOneToolCallThroughTheNormalPipeline()
    {
        var executor = new RecordingToolExecutor();
        var audit = new InMemoryRuntimeAuditSink();
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var model = new ScriptedModelClient(_ => Malformed(), _ => JsonText(RepairedCall), _ => Text("final answer"));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(executor) with
        {
            AuditSink = audit,
            CheckpointSink = checkpoints,
            RecoveryCompatibilityId = "repair-test:v1"
        });

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("final answer", result.FinalText);
        var call = Assert.Single(executor.Calls);
        Assert.Equal(ToolName, call.Name);
        Assert.Equal("turn-1:step:0:repair:0", call.InvocationId.Value);
        Assert.Equal("{\"path\":\"a.md\"}", call.Arguments.GetRawText());
        Assert.Equal(3, model.Requests.Count);

        var repairRequest = model.Requests[1];
        Assert.Empty(repairRequest.Tools);
        Assert.Null(repairRequest.Parameters.RequiredToolName);
        Assert.Equal(RuntimeResponseFormatKind.JsonObject, repairRequest.Parameters.ResponseFormat!.Kind);
        Assert.Equal(model.Requests[0].Messages.Count + 1, repairRequest.Messages.Count);
        var instruction = ((RuntimeTextItem)repairRequest.Messages[^1].Items[0]).Text;
        Assert.StartsWith("Restate the call as JSON.", instruction, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"open_document\"", instruction, StringComparison.Ordinal);
        Assert.Contains("\"required\":[\"path\"]", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("Execute open_document.", instruction, StringComparison.Ordinal);

        Assert.Contains(result.Turn.Steps[0].Output!.Warnings, static w => w.Code == "tool_call_repaired");
        Assert.Equal(["turn-1:step:0"], result.Turn.Progress.ToolCallRepair!.AttemptedStepIds);

        Assert.All(audit.Events, static e => Assert.Equal(RuntimeAuditSchema.EnhancedVersion, e.SchemaVersion));
        var repairEvents = audit.Events.Where(static e => e.Kind == RuntimeAuditEventKind.ToolCallRepairAttempted)
            .Select(static e => (RuntimeToolCallRepairAuditPayload)e.Payload).ToArray();
        Assert.Equal(
            [RuntimeToolCallRepairCodes.PhaseReservation, RuntimeToolCallRepairCodes.PhaseOutcome],
            repairEvents.Select(static e => e.Phase));
        Assert.Equal(RuntimeToolCallRepairCodes.OutcomeToolCallRecovered, repairEvents[1].Outcome);
        Assert.Equal(ToolName, repairEvents[1].RecoveredToolName);
        var kinds = audit.Events.Select(static e => e.Kind).ToArray();
        Assert.True(Array.IndexOf(kinds, RuntimeAuditEventKind.ModelRequestPrepared) <
                    Array.IndexOf(kinds, RuntimeAuditEventKind.ToolCallRepairAttempted));
        Assert.True(Array.LastIndexOf(kinds, RuntimeAuditEventKind.ToolCallRepairAttempted) <
                    Array.IndexOf(kinds, RuntimeAuditEventKind.ModelResponseCommitted));

        var replay = RuntimeRecordedReplay.Replay(Recording(audit));
        Assert.Equal(RuntimeTurnStatus.Completed, replay.Status);

        var prepared = checkpoints.Checkpoints.Where(static c => c.Kind == RuntimeCheckpointKind.StepPrepared).ToArray();
        Assert.True(prepared.Length >= 2);
        Assert.Null(prepared[0].Session.ActiveTurn!.Progress.ToolCallRepair);
        Assert.Equal(["turn-1:step:0"], prepared[1].Session.ActiveTurn!.Progress.ToolCallRepair!.AttemptedStepIds);
        Assert.All(checkpoints.Checkpoints, static c => Assert.Equal(RuntimeCheckpointSchema.EnhancedVersion, c.SchemaVersion));
    }

    [Fact]
    public async Task InvalidToolCallIdentity_IsRepaired()
    {
        var executor = new RecordingToolExecutor();
        var model = new ScriptedModelClient(
            _ => Events(new RuntimeToolCallEvent(ToolCall("bad", "")), new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)),
            _ => JsonText(RepairedCall),
            _ => Text("done"));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(executor));

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Single(executor.Calls);
    }

    [Fact]
    public async Task FinalPayload_IsCommittedAsTextForTheTerminationPolicy()
    {
        var executor = new RecordingToolExecutor();
        var audit = new InMemoryRuntimeAuditSink();
        var model = new ScriptedModelClient(_ => Malformed(), _ => JsonText("{\"status\":\"partial\",\"value\":null}"));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(executor) with { AuditSink = audit });

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("{\"status\":\"partial\",\"value\":null}", result.FinalText);
        Assert.Empty(executor.Calls);
        Assert.Equal(RuntimeTurnStatus.Completed, RuntimeRecordedReplay.Replay(Recording(audit)).Status);
    }

    public static TheoryData<string, string> RejectedResponses => new()
    {
        { "not json", RuntimeToolCallRepairCodes.OutcomeInvalidJson },
        { "[1]", RuntimeToolCallRepairCodes.OutcomeInvalidShape },
        { "{\"tool_calls\":[{\"name\":\"open_document\",\"arguments\":{\"path\":\"a\"}}],\"answer\":1}", RuntimeToolCallRepairCodes.OutcomeInvalidShape },
        { "{\"tool_calls\":[]}", RuntimeToolCallRepairCodes.OutcomeInvalidShape },
        { "{\"tool_calls\":[{\"name\":\"open_document\",\"arguments\":{\"path\":\"a\"},\"id\":\"x\"}]}", RuntimeToolCallRepairCodes.OutcomeInvalidShape },
        { "{\"tool_calls\":[{\"name\":\"open_document\",\"arguments\":{\"path\":\"a\"}},{\"name\":\"open_document\",\"arguments\":{\"path\":\"b\"}}]}", RuntimeToolCallRepairCodes.OutcomeMultipleToolCalls },
        { "{\"tool_calls\":[{\"name\":\"delete_everything\",\"arguments\":{}}]}", RuntimeToolCallRepairCodes.OutcomeUnknownTool },
        { "{\"tool_calls\":[{\"name\":\"Open_Document\",\"arguments\":{\"path\":\"a\"}}]}", RuntimeToolCallRepairCodes.OutcomeUnknownTool },
        { "{\"tool_calls\":[{\"name\":\"open_document\",\"arguments\":{}}]}", RuntimeToolCallRepairCodes.OutcomeInvalidArguments },
        { "{\"tool_calls\":[{\"name\":\"open_document\",\"arguments\":{\"path\":\"a\",\"extra\":1}}]}", RuntimeToolCallRepairCodes.OutcomeInvalidArguments },
        { "{\"tool_calls\":[{\"name\":\"open_document\",\"arguments\":{\"path\":3}}]}", RuntimeToolCallRepairCodes.OutcomeInvalidArguments }
    };

    [Theory]
    [MemberData(nameof(RejectedResponses))]
    public async Task InvalidRepairResponses_FailClosedWithoutExecution(string response, string outcome)
    {
        var executor = new RecordingToolExecutor();
        var audit = new InMemoryRuntimeAuditSink();
        var model = new ScriptedModelClient(_ => Malformed(), _ => JsonText(response));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(executor) with { AuditSink = audit });

        AssertRejected(result, outcome, audit);
        Assert.Empty(executor.Calls);
        Assert.Equal(2, model.Requests.Count);
    }

    [Theory]
    [InlineData(RuntimeToolSideEffect.None)]
    [InlineData(RuntimeToolSideEffect.External)]
    [InlineData(RuntimeToolSideEffect.WorkspaceWrite)]
    public async Task NonReadOnlyTools_AreNeverRecovered(RuntimeToolSideEffect sideEffect)
    {
        var executor = new RecordingToolExecutor();
        var audit = new InMemoryRuntimeAuditSink();
        var model = new ScriptedModelClient(_ => Malformed(), _ => JsonText(RepairedCall));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(executor, Descriptor() with { SideEffect = sideEffect }) with
        {
            AuditSink = audit
        });

        AssertRejected(result, RuntimeToolCallRepairCodes.OutcomeSideEffectNotReadOnly, audit);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task RepairResponseWithStructuredToolCall_IsRejected()
    {
        var executor = new RecordingToolExecutor();
        var model = new ScriptedModelClient(_ => Malformed(), _ => Tool(ToolCall("x", ToolName, "{\"path\":\"a\"}")));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(executor));

        Assert.Equal(RuntimeToolCallRepairCodes.RejectedErrorCode, result.Error!.Code);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task PartiallyAcceptedBatch_IsNotRepaired()
    {
        var executor = new RecordingToolExecutor();
        var model = new ScriptedModelClient(_ => Events(
            new RuntimeToolCallEvent(ToolCall("ok", ToolName, "{\"path\":\"a\"}")),
            new RuntimeToolCallEvent(ToolCall("bad", "")),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(executor));

        Assert.Equal(RuntimeTerminationReason.FailClosed, result.TerminationReason);
        Assert.Equal(RuntimeToolCallRepairCodes.TriggerInvalidToolCallIdentity, result.Error!.Code);
        Assert.Single(model.Requests);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task WithoutPolicy_LegacyFailureAndSchemaOneAreUnchanged()
    {
        var audit = new InMemoryRuntimeAuditSink();
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var model = new ScriptedModelClient(_ => Malformed());

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(new RecordingToolExecutor()) with
        {
            ToolCallRepair = null,
            AuditSink = audit,
            CheckpointSink = checkpoints,
            RecoveryCompatibilityId = "legacy:v1"
        });

        Assert.Equal(RuntimeTerminationReason.Error, result.TerminationReason);
        Assert.Equal(RuntimeToolCallRepairCodes.TriggerMalformedToolCall, result.Error!.Code);
        Assert.Single(model.Requests);
        Assert.All(audit.Events, static e => Assert.Equal(RuntimeAuditSchema.CurrentVersion, e.SchemaVersion));
        Assert.DoesNotContain(audit.Events, static e => e.Kind == RuntimeAuditEventKind.ToolCallRepairAttempted);
        Assert.All(checkpoints.Checkpoints, static c =>
        {
            Assert.Equal(RuntimeCheckpointSchema.CurrentVersion, c.SchemaVersion);
            var json = JsonSerializer.Serialize(c, RuntimeCheckpointJsonContext.Default.RuntimeCheckpointDocument);
            Assert.DoesNotContain("toolCallRepair", json, StringComparison.Ordinal);
        });
        var auditJson = JsonSerializer.Serialize(audit.Events.ToArray(), RuntimeAuditJsonContext.Default.RuntimeAuditEnvelopeArray);
        Assert.DoesNotContain("toolCallRepair", auditJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StepWithoutTools_IsNotRepaired()
    {
        var model = new ScriptedModelClient(_ => Malformed());

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(new RecordingToolExecutor()) with
        {
            Tools = [],
            ToolExecutor = null
        });

        Assert.Equal(RuntimeToolCallRepairCodes.TriggerMalformedToolCall, result.Error!.Code);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task TurnBudget_AllowsTwoStepsButNeverTwoRepairsInOneStep()
    {
        var executor = new RecordingToolExecutor();
        var twice = new ScriptedModelClient(
            _ => Malformed(), _ => JsonText(RepairedCall),
            _ => Malformed(), _ => JsonText(RepairedCall),
            _ => Text("done"));
        var both = await RunLoopAsync(new RuntimeAgentLoop(twice), Request(executor, budget: new RuntimeBudgetSnapshot(5, 5)));
        Assert.Equal(RuntimeTurnStatus.Completed, both.Status);
        Assert.Equal(2, executor.Calls.Count);
        Assert.Equal(2, both.Turn.Progress.ToolCallRepairCount);

        var limited = new ScriptedModelClient(
            _ => Malformed(), _ => JsonText(RepairedCall),
            _ => Malformed());
        var once = await RunLoopAsync(new RuntimeAgentLoop(limited), Request(
            new RecordingToolExecutor(),
            budget: new RuntimeBudgetSnapshot(5, 5),
            maxPerTurn: 1));
        Assert.Equal(RuntimeToolCallRepairCodes.TriggerMalformedToolCall, once.Error!.Code);
        Assert.Equal(3, limited.Requests.Count);
    }

    [Fact]
    public async Task RepairsShareTheBudgetWithContinueWithoutTools()
    {
        // A repair spends the only shared slot, so a JSON-repair continuation cannot follow.
        var model = new ScriptedModelClient(
            _ => Malformed(), _ => JsonText(RepairedCall),
            _ => Text("not json"));
        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(
            new RecordingToolExecutor(),
            budget: new RuntimeBudgetSnapshot(5, 5, maxContinuations: 2),
            maxPerTurn: 1,
            termination: new JsonRepairTermination()));
        Assert.Equal("continuation_budget_exhausted", result.Error!.Code);

        // A continuation spends the slot first, so a later repair on a tool Step is refused.
        var reverse = new ScriptedModelClient(
            _ => Text("not json"),
            _ => Malformed());
        var refused = await RunLoopAsync(new RuntimeAgentLoop(reverse), Request(
            new RecordingToolExecutor(),
            budget: new RuntimeBudgetSnapshot(5, 5, maxContinuations: 2),
            maxPerTurn: 1,
            termination: new JsonRepairTermination(withoutTools: false)));
        Assert.Equal(1, refused.Turn.Progress.ContinuationCount);
        Assert.Equal(RuntimeToolCallRepairCodes.TriggerMalformedToolCall, refused.Error!.Code);
        Assert.Null(refused.Turn.Progress.ToolCallRepair);
        Assert.Equal(2, reverse.Requests.Count);
        Assert.NotEmpty(reverse.Requests[1].Tools);
    }

    [Fact]
    public async Task RepairBudgetOverflow_FailsClosedBeforeAnyReservationOrRequest()
    {
        var audit = new InMemoryRuntimeAuditSink();
        var model = new ScriptedModelClient(_ => Malformed(), _ => JsonText(RepairedCall));
        var options = new RuntimeContextOptions
        {
            MaxContextTokens = 1_100,
            MaxItemTokens = 1_100,
            MaxToolResultTokens = 1_000,
            LargeToolResultTokens = 1_000,
            SummaryTokens = 200
        };

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(new RecordingToolExecutor()) with
        {
            InitialMessages = [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(new string('q', 4_000))])],
            ContextManager = new RuntimeContextManager(options),
            AuditSink = audit,
            // The restated instruction and schema are re-estimated, not assumed to fit
            // inside the reserved tool tokens of the original Step.
            ToolCallRepair = new RuntimeToolCallRepairPolicy { Instruction = "Restate. " + new string('i', 800) }
        });

        Assert.True(result.Error!.Category == RuntimeErrorCategory.ContextOverflow, result.Error.Code + ": " + result.Error.Message);
        Assert.Equal(RuntimeToolCallRepairCodes.ContextBudgetExceededErrorCode, result.Error.Code);
        Assert.Single(model.Requests);
        Assert.Null(result.Turn.Progress.ToolCallRepair);
        Assert.DoesNotContain(audit.Events, static e => e.Kind == RuntimeAuditEventKind.ToolCallRepairAttempted);
    }

    [Fact]
    public async Task RepairRequestFailure_IsNotRetried()
    {
        var audit = new InMemoryRuntimeAuditSink();
        var model = new ScriptedModelClient(
            _ => Malformed(),
            _ => Throw(new RuntimeModelClientException(new RuntimeError(
                RuntimeErrorCategory.ProviderTransport, "transport_failed", "boom", Retryable: true))));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(new RecordingToolExecutor()) with
        {
            AuditSink = audit,
            Budget = new RuntimeBudgetSnapshot(3, 3, maxModelRetries: 2)
        });

        Assert.Equal(RuntimeTerminationReason.Error, result.TerminationReason);
        Assert.Equal("transport_failed", result.Error!.Code);
        Assert.False(result.Error.Retryable);
        Assert.Equal(2, model.Requests.Count);
        var outcome = (RuntimeToolCallRepairAuditPayload)audit.Events.Last(static e => e.Kind == RuntimeAuditEventKind.ToolCallRepairAttempted).Payload;
        Assert.Equal(RuntimeToolCallRepairCodes.OutcomeRequestFailed, outcome.Outcome);
        Assert.Equal(RuntimeTurnStatus.Failed, RuntimeRecordedReplay.Replay(Recording(audit)).Status);
    }

    [Fact]
    public async Task CancellationDuringRepair_CancelsTheTurnAndRecordsTheOutcome()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var audit = new InMemoryRuntimeAuditSink();
        var model = new ScriptedModelClient(_ => Malformed(), _ => CancelThenWait(cancellation));

        var result = await new RuntimeAgentLoop(model).RunAsync(
            Request(new RecordingToolExecutor()) with { AuditSink = audit },
            ct: cancellation.Token);

        Assert.Equal(RuntimeTurnStatus.Cancelled, result.Status);
        var outcome = (RuntimeToolCallRepairAuditPayload)audit.Events.Last(static e => e.Kind == RuntimeAuditEventKind.ToolCallRepairAttempted).Payload;
        Assert.Equal(RuntimeToolCallRepairCodes.OutcomeRequestCancelled, outcome.Outcome);
    }

    [Fact]
    public async Task ResumeAfterReservation_NeverRepairsTheSameStepAgain()
    {
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var source = new ScriptedModelClient(
            _ => Malformed(),
            _ => Throw(new IOException("process crashed during the repair request")));
        var request = Request(new RecordingToolExecutor()) with
        {
            CheckpointSink = checkpoints,
            RecoveryCompatibilityId = "repair-resume:v1"
        };
        await RunLoopAsync(new RuntimeAgentLoop(source), request);
        var prepared = checkpoints.Checkpoints.Where(static c => c.Kind == RuntimeCheckpointKind.StepPrepared).ToArray();
        Assert.Equal(2, prepared.Length);
        var reserved = prepared[1];
        Assert.Equal(["turn-1:step:0"], reserved.Session.ActiveTurn!.Progress.ToolCallRepair!.AttemptedStepIds);

        // Crash after the durable reservation: the resumed Step samples once and may not repair.
        var afterReservation = new ScriptedModelClient(_ => Malformed(), _ => JsonText(RepairedCall));
        var refused = await ResumeAsync(afterReservation, request, reserved);
        Assert.Equal(RuntimeToolCallRepairCodes.TriggerMalformedToolCall, refused.Error!.Code);
        Assert.Single(afterReservation.Requests);

        // Crash after the reservation but before commit, then a clean resample commits normally.
        var executor = new RecordingToolExecutor();
        var clean = new ScriptedModelClient(_ => Tool(ToolCall("c1", ToolName, "{\"path\":\"a\"}")), _ => Text("done"));
        var completed = await ResumeAsync(clean, request with { ToolExecutor = executor }, reserved);
        Assert.Equal(RuntimeTurnStatus.Completed, completed.Status);
        Assert.Equal(["turn-1:step:0"], completed.Turn.Progress.ToolCallRepair!.AttemptedStepIds);
        Assert.Single(executor.Calls);

        // Crash before the reservation: the earlier checkpoint still allows exactly one repair.
        var beforeReservation = new ScriptedModelClient(_ => Malformed(), _ => JsonText(RepairedCall), _ => Text("done"));
        var repaired = await ResumeAsync(beforeReservation, request, prepared[0]);
        Assert.Equal(RuntimeTurnStatus.Completed, repaired.Status);
        Assert.Equal(3, beforeReservation.Requests.Count);
    }

    [Fact]
    public async Task CheckpointStore_RoundTripsSchemaTwoAndRejectsInconsistentRepairState()
    {
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        await RunLoopAsync(
            new RuntimeAgentLoop(new ScriptedModelClient(_ => Malformed(), _ => JsonText(RepairedCall), _ => Text("done"))),
            Request(new RecordingToolExecutor()) with { CheckpointSink = checkpoints, RecoveryCompatibilityId = "store:v1" });
        var reserved = checkpoints.Checkpoints.Where(static c => c.Kind == RuntimeCheckpointKind.StepPrepared).ElementAt(1);

        var directory = Path.Combine(Path.GetTempPath(), "qre-repair-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RuntimeJsonCheckpointStore(directory);
            await store.SaveAsync(reserved, TestContext.Current.CancellationToken);
            var reread = RuntimeJsonCheckpointStore.Read(store.CheckpointPath);
            Assert.Equal(RuntimeCheckpointSchema.EnhancedVersion, reread.SchemaVersion);
            Assert.Equal(["turn-1:step:0"], reread.Session.ActiveTurn!.Progress.ToolCallRepair!.AttemptedStepIds);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        var turn = reserved.Session.ActiveTurn!;
        var tooMany = reserved with
        {
            Session = reserved.Session with
            {
                ActiveTurn = turn with
                {
                    Progress = turn.Progress with
                    {
                        ToolCallRepair = new RuntimeToolCallRepairProgress(["turn-1:step:0", "turn-1:step:9"])
                    }
                }
            }
        };
        Assert.Equal(
            "checkpoint_tool_call_repair_invalid",
            (await ValidateViaStoreAsync(tooMany)).Error.Code);
        // After a StepPrepared recovery removed the reserving Step, the reservation
        // still belongs to the next Step ID; any other missing Step is rejected.
        var awaitingRebuild = reserved with
        {
            Kind = RuntimeCheckpointKind.TurnStarted,
            Session = reserved.Session with { ActiveTurn = turn with { Steps = [] } }
        };
        await SaveViaStoreAsync(awaitingRebuild);
        var foreign = awaitingRebuild with
        {
            Session = awaitingRebuild.Session with
            {
                ActiveTurn = awaitingRebuild.Session.ActiveTurn! with
                {
                    Progress = turn.Progress with
                    {
                        ToolCallRepair = new RuntimeToolCallRepairProgress(["turn-1:step:5"])
                    }
                }
            }
        };
        Assert.Equal(
            "checkpoint_tool_call_repair_invalid",
            (await ValidateViaStoreAsync(foreign)).Error.Code);
        var downgraded = reserved with { SchemaVersion = RuntimeCheckpointSchema.CurrentVersion };
        Assert.Equal(
            "checkpoint_runtime_incompatible",
            (await ValidateViaStoreAsync(downgraded)).Error.Code);
        var unknown = reserved with { SchemaVersion = 3 };
        Assert.Equal(
            "checkpoint_runtime_incompatible",
            (await ValidateViaStoreAsync(unknown)).Error.Code);
    }

    [Fact]
    public async Task RecordedReplay_RejectsTamperedOrInterruptedRepairs()
    {
        var audit = new InMemoryRuntimeAuditSink();
        await RunLoopAsync(
            new RuntimeAgentLoop(new ScriptedModelClient(_ => Malformed(), _ => JsonText(RepairedCall), _ => Text("done"))),
            Request(new RecordingToolExecutor()) with { AuditSink = audit });
        var events = audit.Events.ToArray();
        var outcomeIndex = Array.FindLastIndex(events, static e => e.Kind == RuntimeAuditEventKind.ToolCallRepairAttempted);
        var outcome = (RuntimeToolCallRepairAuditPayload)events[outcomeIndex].Payload;

        AssertReplayRejected(Replace(events, outcomeIndex, outcome with { RecoveredToolName = "other_tool" }), "audit_tool_call_repair_commit_mismatch");
        AssertReplayRejected(Replace(events, outcomeIndex, outcome with { ArgumentsSha256 = new string('0', 64) }), "audit_tool_call_repair_commit_mismatch");
        AssertReplayRejected(Replace(events, outcomeIndex, outcome with { Outcome = "made_up" }), "audit_tool_call_repair_outcome_invalid");
        AssertReplayRejected(Replace(events, outcomeIndex, outcome with { Phase = RuntimeToolCallRepairCodes.PhaseReservation }), "audit_tool_call_repair_reservation_invalid");
        AssertReplayRejected(Relink(events.Where((_, index) => index != outcomeIndex).ToArray()), "audit_tool_call_repair_commit_invalid");
        AssertReplayRejected(Relink(events.Select(static e => e with { SchemaVersion = RuntimeAuditSchema.CurrentVersion }).ToArray()), "audit_tool_call_repair_schema_invalid");
        var mixed = events.ToArray();
        mixed[^1] = mixed[^1] with { SchemaVersion = RuntimeAuditSchema.CurrentVersion };
        AssertReplayRejected(mixed, "audit_schema_incompatible");
    }

    [Fact]
    public async Task ReplayRejectsACompletedTurnWithAnInterruptedReservation()
    {
        var audit = new InMemoryRuntimeAuditSink();
        await RunLoopAsync(
            new RuntimeAgentLoop(new ScriptedModelClient(_ => Malformed(), _ => JsonText("{\"answer\":1}"))),
            Request(new RecordingToolExecutor()) with { AuditSink = audit });
        var events = audit.Events.ToArray();
        var outcomeIndex = Array.FindLastIndex(events, static e => e.Kind == RuntimeAuditEventKind.ToolCallRepairAttempted);
        var responseIndex = Array.FindIndex(events, static e => e.Kind == RuntimeAuditEventKind.ModelResponseCommitted);
        var interrupted = Relink(events.Where((_, index) => index != outcomeIndex && index != responseIndex).ToArray());

        var failure = Assert.Throws<RuntimeAuditReplayException>(() => RuntimeRecordedReplay.Replay(Recording(interrupted)));
        Assert.True(failure.Error.Code is "audit_tool_call_repair_interrupted" or "audit_terminal_counts_mismatch" or "audit_terminal_text_mismatch",
            failure.Error.Code);
    }

    [Fact]
    public async Task JsonlStore_WritesSchemaTwoManifestAndPublicProjectionHidesRepairContent()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "qre-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            string auditFile;
            string manifestFile;
            await using (var store = RuntimeJsonlAuditStore.Create(workspace, "repair-run", new RuntimeAuditStoreOptions
                         {
                             DataMode = RuntimeAuditDataMode.SanitizedFixture
                         }))
            {
                await RunLoopAsync(
                    new RuntimeAgentLoop(new ScriptedModelClient(_ => Malformed(), _ => JsonText(RepairedCall), _ => Text("done"))),
                    Request(new RecordingToolExecutor()) with { AuditSink = store });
                auditFile = store.AuditFilePath;
                manifestFile = store.ManifestPath;
            }
            var recording = RuntimeJsonlAuditStore.Read(auditFile, ct: TestContext.Current.CancellationToken);
            Assert.All(recording.Events, static e => Assert.Equal(RuntimeAuditSchema.EnhancedVersion, e.SchemaVersion));
            Assert.Equal(RuntimeTurnStatus.Completed, RuntimeRecordedReplay.Replay(recording).Status);
            Assert.Contains("\"schemaVersion\":2", File.ReadAllText(manifestFile), StringComparison.Ordinal);

            string publicFile;
            await using (var publicStore = RuntimeJsonlAuditStore.Create(workspace, "public-run"))
            {
                await RunLoopAsync(
                    new RuntimeAgentLoop(new ScriptedModelClient(_ => Malformed(), _ => JsonText(RepairedCall), _ => Text("done"))),
                    Request(new RecordingToolExecutor()) with { AuditSink = publicStore });
                publicFile = publicStore.AuditFilePath;
            }
            var publicText = File.ReadAllText(publicFile);
            Assert.Contains("outcome:tool_call_recovered:1", publicText, StringComparison.Ordinal);
            Assert.DoesNotContain("a.md", publicText, StringComparison.Ordinal);
            Assert.DoesNotContain("Restate the call", publicText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private static async Task SaveViaStoreAsync(RuntimeCheckpointDocument checkpoint)
    {
        var directory = Path.Combine(Path.GetTempPath(), "qre-repair-valid-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new RuntimeJsonCheckpointStore(directory).SaveAsync(checkpoint, TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<RuntimeResumeException> ValidateViaStoreAsync(RuntimeCheckpointDocument checkpoint)
    {
        var directory = Path.Combine(Path.GetTempPath(), "qre-repair-invalid-" + Guid.NewGuid().ToString("N"));
        try
        {
            return await Assert.ThrowsAsync<RuntimeResumeException>(async () =>
                await new RuntimeJsonCheckpointStore(directory).SaveAsync(checkpoint, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void AssertRejected(RuntimeAgentLoopResult result, string outcome, InMemoryRuntimeAuditSink? audit = null)
    {
        Assert.Equal(RuntimeTerminationReason.FailClosed, result.TerminationReason);
        Assert.Equal(RuntimeToolCallRepairCodes.RejectedErrorCode, result.Error!.Code);
        Assert.Contains(outcome, result.Error.Message, StringComparison.Ordinal);
        if (audit != null)
        {
            var recorded = (RuntimeToolCallRepairAuditPayload)audit.Events
                .Last(static e => e.Kind == RuntimeAuditEventKind.ToolCallRepairAttempted).Payload;
            Assert.Equal(outcome, recorded.Outcome);
            Assert.Equal(RuntimeTurnStatus.Failed, RuntimeRecordedReplay.Replay(Recording(audit)).Status);
        }
    }

    private static void AssertReplayRejected(RuntimeAuditEnvelope[] events, string code)
    {
        var failure = Assert.Throws<RuntimeAuditReplayException>(() => RuntimeRecordedReplay.Replay(Recording(events)));
        Assert.Equal(code, failure.Error.Code);
    }

    private static RuntimeAuditEnvelope[] Replace(RuntimeAuditEnvelope[] events, int index, RuntimeAuditPayload payload)
    {
        var copy = events.ToArray();
        copy[index] = copy[index] with { Payload = payload };
        return copy;
    }

    private static RuntimeAuditEnvelope[] Relink(RuntimeAuditEnvelope[] events)
    {
        var copy = new RuntimeAuditEnvelope[events.Length];
        for (var index = 0; index < events.Length; index++)
        {
            copy[index] = events[index] with
            {
                Sequence = index + 1,
                CausationId = index == 0 ? null : copy[index - 1].EventId
            };
        }
        return copy;
    }

    private static RuntimeAuditRecording Recording(InMemoryRuntimeAuditSink audit)
        => Recording(audit.Events.ToArray());

    private static RuntimeAuditRecording Recording(IReadOnlyList<RuntimeAuditEnvelope> events)
        => new(RuntimeAuditDataMode.SanitizedFixture, RuntimeAuditReplayCapability.Recorded, events);

    private static Task<RuntimeAgentLoopResult> ResumeAsync(
        IRuntimeModelClient model,
        RuntimeAgentLoopRequest request,
        RuntimeCheckpointDocument checkpoint)
        => new RuntimeAgentLoop(model).ResumeAsync(
            request with
            {
                Attempt = RuntimeRunAttempt.Resume(checkpoint, "attempt-" + Guid.NewGuid().ToString("N")),
                CheckpointSink = new InMemoryRuntimeCheckpointSink()
            },
            checkpoint,
            ct: TestContext.Current.CancellationToken);

    private static RuntimeToolDescriptor Descriptor()
        => new(
            ToolName,
            "1.0.0",
            "Execute open_document.",
            Json("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}"),
            RuntimeToolSideEffect.ReadOnly,
            RuntimeToolIdempotency.Idempotent);

    private static RuntimeAgentLoopRequest Request(
        IRuntimeToolExecutor executor,
        RuntimeToolDescriptor? descriptor = null,
        RuntimeBudgetSnapshot? budget = null,
        int maxPerTurn = 2,
        IRuntimeTerminationPolicy? termination = null)
        => CreateRequest(
            [descriptor ?? Descriptor()],
            executor,
            budget: budget ?? new RuntimeBudgetSnapshot(3, 3),
            terminationPolicy: termination) with
        {
            ToolCallRepair = new RuntimeToolCallRepairPolicy
            {
                Instruction = "Restate the call as JSON.",
                MaxPerTurn = maxPerTurn
            }
        };

    private static IAsyncEnumerable<RuntimeModelStreamEvent> Malformed()
        => Throw(new RuntimeModelClientException(new RuntimeError(
            RuntimeErrorCategory.ProviderProtocol,
            RuntimeToolCallRepairCodes.TriggerMalformedToolCall,
            "Provider returned 1 tool_calls with empty function.name in non-stream response.")));

    private static IAsyncEnumerable<RuntimeModelStreamEvent> JsonText(string json)
        => Events(
            new RuntimeTextDeltaEvent(json),
            new RuntimeUsageEvent(new RuntimeUsage(10, 5, 15)),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn));

    private static async IAsyncEnumerable<RuntimeModelStreamEvent> CancelThenWait(
        CancellationTokenSource source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await source.CancelAsync();
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }

    /// <summary>Requests one tool-free JSON repair whenever the final text is not JSON.</summary>
    private sealed class JsonRepairTermination(bool withoutTools = true) : IRuntimeTerminationPolicy
    {
        public ValueTask<RuntimeTerminationDecision> DecideAsync(RuntimeTerminationContext context, CancellationToken ct)
        {
            var text = context.Session.ActiveTurn!.Steps[^1].Output!.Text;
            try
            {
                using var _ = JsonDocument.Parse(text);
                return ValueTask.FromResult(RuntimeTerminationDecision.Accept());
            }
            catch (JsonException)
            {
                return ValueTask.FromResult(withoutTools
                    ? RuntimeTerminationDecision.ContinueWithoutTools("Return valid JSON.")
                    : RuntimeTerminationDecision.Continue("Return valid JSON."));
            }
        }
    }
}
