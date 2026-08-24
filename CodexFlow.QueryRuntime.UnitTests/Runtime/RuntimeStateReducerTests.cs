using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class RuntimeStateReducerTests
{
    [Fact]
    public void StateMachine_ProducesOrderedImmutableSessionTurnStepTrajectory()
    {
        var originalMessages = new List<RuntimeMessage>
        {
            new(RuntimeMessageRole.User, [new RuntimeTextItem("inspect")])
        };
        var session = RuntimeSessionState.Create(new RuntimeSessionId("session-1"), historyVersion: 7);
        var turnContext = new RuntimeTurnContext(
            session.SessionId,
            new RuntimeTurnId("turn-1"),
            "inspect workspace",
            DateTimeOffset.UnixEpoch);
        session = RuntimeStateReducer.StartTurn(session, turnContext);
        var step = CreateStep(session, originalMessages, index: 0);
        session = RuntimeStateReducer.PrepareStep(session, step);
        originalMessages.Clear();

        session = RuntimeStateReducer.TransitionStep(session, step.StepId, RuntimeStepPhase.Sampling);
        session = RuntimeStateReducer.RecordModelAttempt(session, step.StepId);
        session = RuntimeStateReducer.CommitModelOutput(
            session,
            step.StepId,
            new RuntimeModelOutput(
                [new RuntimeTextItem("done")],
                RuntimeUsageTotals.Empty,
                [],
                RuntimeModelStopReason.EndTurn));
        session = RuntimeStateReducer.TransitionStep(session, step.StepId, RuntimeStepPhase.Completed);
        session = RuntimeStateReducer.FinishTurn(
            session,
            RuntimeTurnStatus.Completed,
            RuntimeTerminationReason.Completed);

        Assert.Null(session.ActiveTurn);
        var terminal = Assert.Single(session.TerminalTurns);
        Assert.Equal(RuntimeTurnStatus.Completed, terminal.Status);
        Assert.Equal(RuntimeStepPhase.Completed, Assert.Single(terminal.Steps).Phase);
        Assert.Single(terminal.Steps[0].Context.ModelRequest.Messages);
    }

    [Fact]
    public void StepSnapshot_OwnsJsonAndCollectionData()
    {
        var artifacts = new List<RuntimeArtifactReference>
        {
            new("result.txt", "text/plain")
        };
        RuntimeStepContext step;
        using (var json = JsonDocument.Parse("{\"value\":1}"))
        {
            var session = RuntimeSessionState.Create(new RuntimeSessionId("session-1"));
            session = RuntimeStateReducer.StartTurn(
                session,
                new RuntimeTurnContext(
                    session.SessionId,
                    new RuntimeTurnId("turn-1"),
                    "inspect",
                    DateTimeOffset.UnixEpoch));
            var stepId = new RuntimeStepId("step-0");
            var request = new RuntimeModelRequest(
                session.SessionId,
                session.ActiveTurn!.Context.TurnId,
                stepId,
                [new RuntimeMessage(RuntimeMessageRole.Assistant,
                [
                    new RuntimeToolCallItem(new RuntimeToolCall(
                        new RuntimeInvocationId("call-1"),
                        "read_file",
                        json.RootElement)),
                    new RuntimeToolResultItem(new RuntimeToolResult(
                        new RuntimeInvocationId("call-1"),
                        "ok",
                        true,
                        Artifacts: artifacts))
                ])],
                [new RuntimeToolDescriptor(
                    "read_file",
                    "1",
                    "Read a file.",
                    json.RootElement,
                    RuntimeToolSideEffect.ReadOnly,
                    RuntimeToolIdempotency.Idempotent)],
                new RuntimeModelParameters(),
                session.HistoryVersion);
            step = RuntimeStepContext.Create(
                stepId,
                0,
                request,
                new RuntimePolicySnapshot("policy-v1", "readonly"),
                new RuntimeEnvironmentSnapshot("local", "workspace", "sha256:capabilities"),
                new RuntimeBudgetSnapshot(25, 25),
                session.HistoryVersion,
                DateTimeOffset.UnixEpoch);
        }

        artifacts.Clear();

        Assert.Equal(1, step.ModelRequest.Tools[0].InputSchema.GetProperty("value").GetInt32());
        var call = Assert.IsType<RuntimeToolCallItem>(step.ModelRequest.Messages[0].Items[0]);
        Assert.Equal(1, call.Call.Arguments.GetProperty("value").GetInt32());
        var result = Assert.IsType<RuntimeToolResultItem>(step.ModelRequest.Messages[0].Items[1]);
        Assert.Single(result.Result.Artifacts!);
    }

    [Fact]
    public void PublicStateBoundaries_RejectDefaultIdentifiers()
    {
        Assert.Throws<ArgumentNullException>(() => RuntimeSessionState.Create(default));

        var session = RuntimeSessionState.Create(new RuntimeSessionId("session-1"));
        var error = Assert.Throws<RuntimeStateTransitionException>(() =>
            RuntimeStateReducer.StartTurn(
                session,
                new RuntimeTurnContext(
                    session.SessionId,
                    default,
                    "inspect",
                    DateTimeOffset.UnixEpoch)));

        Assert.Equal("invalid_turn_id", error.Error.Code);
    }

    [Fact]
    public void StateMachine_FailsClosedOnIllegalTransitionAndIdentityMismatch()
    {
        var session = RuntimeSessionState.Create(new RuntimeSessionId("session-1"));
        session = RuntimeStateReducer.StartTurn(
            session,
            new RuntimeTurnContext(
                session.SessionId,
                new RuntimeTurnId("turn-1"),
                "inspect",
                DateTimeOffset.UnixEpoch));
        var template = CreateStep(session, [new RuntimeMessage(RuntimeMessageRole.User, [])], 0);
        var invalidStep = RuntimeStepContext.Create(
            template.StepId,
            template.Index,
            template.ModelRequest with { TurnId = new RuntimeTurnId("other-turn") },
            template.Policy,
            template.Environment,
            template.Budget,
            template.HistoryVersion,
            template.CreatedAt);
        Assert.Equal(
            "step_identity_mismatch",
            Assert.Throws<RuntimeStateTransitionException>(() =>
                RuntimeStateReducer.PrepareStep(session, invalidStep)).Error.Code);

        var step = CreateStep(session, [], 0);
        session = RuntimeStateReducer.PrepareStep(session, step);
        Assert.Equal(
            "illegal_step_transition",
            Assert.Throws<RuntimeStateTransitionException>(() =>
                RuntimeStateReducer.TransitionStep(
                    session,
                    step.StepId,
                    RuntimeStepPhase.ExecutingTools)).Error.Code);
    }

    [Fact]
    public void SameInputsAndTransitions_ProduceEquivalentTerminalState()
    {
        var first = RunDeterministicTrajectory();
        var second = RunDeterministicTrajectory();

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(first.HistoryVersion, second.HistoryVersion);
        Assert.Equal(first.TerminalTurns[0].Context, second.TerminalTurns[0].Context);
        Assert.Equal(first.TerminalTurns[0].Status, second.TerminalTurns[0].Status);
        AssertStepEquivalent(
            first.TerminalTurns[0].Steps[0].Context,
            second.TerminalTurns[0].Steps[0].Context);
        Assert.Equal(first.TerminalTurns[0].Steps[0].Phase, second.TerminalTurns[0].Steps[0].Phase);
    }

    private static void AssertStepEquivalent(RuntimeStepContext first, RuntimeStepContext second)
    {
        Assert.Equal(first.StepId, second.StepId);
        Assert.Equal(first.Index, second.Index);
        Assert.Equal(first.HistoryVersion, second.HistoryVersion);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal(first.Policy, second.Policy);
        Assert.Equal(first.Environment, second.Environment);
        Assert.Equal(first.Budget, second.Budget);
        Assert.Equal(first.ModelRequest.SessionId, second.ModelRequest.SessionId);
        Assert.Equal(first.ModelRequest.TurnId, second.ModelRequest.TurnId);
        Assert.Equal(first.ModelRequest.StepId, second.ModelRequest.StepId);
        Assert.Equal(first.ModelRequest.Parameters, second.ModelRequest.Parameters);
        Assert.Equal(
            Assert.IsType<RuntimeTextItem>(first.ModelRequest.Messages[0].Items[0]).Text,
            Assert.IsType<RuntimeTextItem>(second.ModelRequest.Messages[0].Items[0]).Text);
        Assert.Equal(first.ModelRequest.Tools[0].CanonicalName, second.ModelRequest.Tools[0].CanonicalName);
        Assert.True(JsonElement.DeepEquals(
            first.ModelRequest.Tools[0].InputSchema,
            second.ModelRequest.Tools[0].InputSchema));
    }

    private static RuntimeSessionState RunDeterministicTrajectory()
    {
        var session = RuntimeSessionState.Create(new RuntimeSessionId("session-1"));
        session = RuntimeStateReducer.StartTurn(
            session,
            new RuntimeTurnContext(
                session.SessionId,
                new RuntimeTurnId("turn-1"),
                "inspect",
                DateTimeOffset.UnixEpoch));
        var step = CreateStep(session, [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("inspect")])], 0);
        session = RuntimeStateReducer.PrepareStep(session, step);
        session = RuntimeStateReducer.TransitionStep(session, step.StepId, RuntimeStepPhase.Sampling);
        session = RuntimeStateReducer.RecordModelAttempt(session, step.StepId);
        session = RuntimeStateReducer.CommitModelOutput(
            session,
            step.StepId,
            new RuntimeModelOutput(
                [new RuntimeTextItem("done")],
                RuntimeUsageTotals.Empty,
                [],
                RuntimeModelStopReason.EndTurn));
        session = RuntimeStateReducer.TransitionStep(session, step.StepId, RuntimeStepPhase.Completed);
        return RuntimeStateReducer.FinishTurn(session, RuntimeTurnStatus.Completed, RuntimeTerminationReason.Completed);
    }

    private static RuntimeStepContext CreateStep(
        RuntimeSessionState session,
        IReadOnlyList<RuntimeMessage> messages,
        int index)
    {
        var turn = session.ActiveTurn!;
        var stepId = new RuntimeStepId($"step-{index}");
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var request = new RuntimeModelRequest(
            session.SessionId,
            turn.Context.TurnId,
            stepId,
            messages,
            [new RuntimeToolDescriptor(
                "read_file",
                "1",
                "Read a file.",
                schema.RootElement.Clone(),
                RuntimeToolSideEffect.ReadOnly,
                RuntimeToolIdempotency.Idempotent)],
            new RuntimeModelParameters(),
            session.HistoryVersion);
        return RuntimeStepContext.Create(
            stepId,
            index,
            request,
            new RuntimePolicySnapshot("policy-v1", "readonly"),
            new RuntimeEnvironmentSnapshot("local", "workspace", "sha256:capabilities"),
            new RuntimeBudgetSnapshot(25, 25),
            session.HistoryVersion,
            DateTimeOffset.UnixEpoch);
    }
}
