using System.Collections.ObjectModel;
using CodexFlow.QueryRuntime.Protocol;
using System.Diagnostics.CodeAnalysis;

namespace CodexFlow.QueryRuntime.Engine.V2;

public static class RuntimeStateReducer
{
    public static RuntimeSessionState StartTurn(
        RuntimeSessionState session,
        RuntimeTurnContext context)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);
        if (session.ActiveTurn != null)
        {
            ThrowInvariant("active_turn_exists", "A Session can have at most one active Turn.");
        }
        if (session.SessionId != context.SessionId)
        {
            ThrowInvariant("turn_session_mismatch", "The Turn context belongs to a different Session.");
        }
        if (string.IsNullOrWhiteSpace(context.TurnId.Value))
        {
            ThrowInvariant("invalid_turn_id", "The Turn ID must not be empty.");
        }
        if (string.IsNullOrWhiteSpace(context.Objective))
        {
            ThrowInvariant("invalid_turn_objective", "The Turn objective must not be empty.");
        }

        return session with
        {
            ActiveTurn = new RuntimeTurnState(
                context,
                RuntimeTurnStatus.Running,
                Array.Empty<RuntimeStepState>(),
                RuntimeTurnProgress.Create(context.RequiredToolName))
        };
    }

    public static RuntimeSessionState PrepareStep(
        RuntimeSessionState session,
        RuntimeStepContext context)
    {
        var turn = RequireRunningTurn(session);
        ArgumentNullException.ThrowIfNull(context);
        if (turn.Steps.LastOrDefault() is { } last && !IsTerminal(last.Phase))
        {
            ThrowInvariant("active_step_exists", "A Turn can have at most one active Step.");
        }
        if (context.Index != turn.Steps.Count)
        {
            ThrowInvariant("step_index_out_of_order", "Step indexes must be contiguous and ordered.");
        }
        if (context.ModelRequest.SessionId != session.SessionId ||
            context.ModelRequest.TurnId != turn.Context.TurnId ||
            context.ModelRequest.StepId != context.StepId)
        {
            ThrowInvariant("step_identity_mismatch", "The Step snapshot IDs do not match active Runtime state.");
        }
        if (context.HistoryVersion != session.HistoryVersion ||
            context.ModelRequest.HistoryVersion != session.HistoryVersion)
        {
            ThrowInvariant("history_version_mismatch", "The Step snapshot must use the Session history version.");
        }
        if (turn.Steps.Count >= context.Budget.MaxSteps)
        {
            ThrowInvariant("step_budget_exhausted", "The Turn has exhausted its Step budget.");
        }

        return ReplaceActiveTurn(
            session,
            turn with
            {
                Steps = Array.AsReadOnly(turn.Steps
                    .Append(new RuntimeStepState(context, RuntimeStepPhase.Preparing))
                    .ToArray())
            });
    }

    public static RuntimeSessionState RecordModelAttempt(RuntimeSessionState session, RuntimeStepId stepId)
    {
        var (turn, step) = RequireActiveStep(session, stepId, RuntimeStepPhase.Sampling);
        var maxAttempts = checked(step.Context.Budget.MaxModelRetries + 1);
        if (step.ModelAttempts >= maxAttempts)
        {
            ThrowInvariant("model_retry_budget_exhausted", "The Step exhausted its model retry budget.");
        }

        return ReplaceActiveStep(session, turn, step with { ModelAttempts = step.ModelAttempts + 1 });
    }

    public static RuntimeSessionState CommitModelOutput(
        RuntimeSessionState session,
        RuntimeStepId stepId,
        RuntimeModelOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var (turn, step) = RequireActiveStep(session, stepId, RuntimeStepPhase.Sampling);
        if (step.ModelAttempts == 0)
        {
            ThrowInvariant("model_not_sampled", "Model output cannot be committed before a model attempt.");
        }
        if (step.Output != null)
        {
            ThrowInvariant("model_output_already_committed", "A Step can commit model output only once.");
        }
        if (output.Items == null || output.Usage == null || output.Warnings == null)
        {
            ThrowInvariant("invalid_model_output", "Model output collections and usage must not be null.");
        }
        if (output.Usage.InputTokens < 0 ||
            output.Usage.OutputTokens < 0 ||
            output.Usage.TotalTokens < 0 ||
            output.Usage.Additional == null ||
            output.Usage.Additional.Values.Any(static value => value < 0))
        {
            ThrowInvariant("invalid_model_usage", "Committed model usage must be non-negative.");
        }

        var snapshot = SnapshotOutput(output);
        var invocationIds = new HashSet<RuntimeInvocationId>();
        foreach (var call in snapshot.ToolCalls)
        {
            if (string.IsNullOrWhiteSpace(call.InvocationId.Value) ||
                string.IsNullOrWhiteSpace(call.Name) ||
                call.Arguments.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                ThrowInvariant("invalid_tool_call", "Committed tool calls require identity, name, and object arguments.");
            }
            if (!invocationIds.Add(call.InvocationId))
            {
                ThrowInvariant("duplicate_tool_invocation_id", "Tool invocation IDs must be unique within a Step.");
            }
        }

        var usage = AddUsage(turn.Progress.Usage, snapshot.Usage);
        var toolStates = snapshot.ToolCalls
            .Select(static call => new RuntimeToolInvocationState(call, RuntimeToolInvocationStatus.Requested))
            .ToArray();
        var targetPhase = toolStates.Length == 0
            ? RuntimeStepPhase.CommittingObservation
            : RuntimeStepPhase.ResolvingTools;
        var updatedStep = step with
        {
            Phase = targetPhase,
            Output = snapshot,
            ToolInvocations = Array.AsReadOnly(toolStates)
        };
        var updatedTurn = turn with
        {
            Progress = turn.Progress with
            {
                Usage = usage,
                LastModelStopReason = snapshot.StopReason
            }
        };
        return ReplaceActiveStep(session, updatedTurn, updatedStep);
    }

    public static RuntimeSessionState TransitionTool(
        RuntimeSessionState session,
        RuntimeStepId stepId,
        RuntimeInvocationId invocationId,
        RuntimeToolInvocationStatus target,
        RuntimeToolResult? result = null)
    {
        var turn = RequireRunningTurn(session);
        if (turn.Steps.Count == 0 || turn.Steps[^1].Context.StepId != stepId)
        {
            ThrowInvariant("step_not_active", "Only the active Step can transition a tool invocation.");
        }

        var step = turn.Steps[^1];
        if (step.Phase is not (RuntimeStepPhase.ResolvingTools or RuntimeStepPhase.ExecutingTools))
        {
            ThrowInvariant("tool_transition_outside_execution", "Tool lifecycle transitions require a resolving or executing Step.");
        }
        var invocations = step.ToolInvocations?.ToArray() ?? [];
        var index = Array.FindIndex(invocations, item => item.Call.InvocationId == invocationId);
        if (index < 0)
        {
            ThrowInvariant("unknown_tool_invocation", "The tool invocation does not belong to the active Step.");
        }

        var current = invocations[index];
        if (!CanTransitionTool(current.Status, target))
        {
            ThrowInvariant(
                "illegal_tool_transition",
                $"Tool transition {current.Status} -> {target} is not allowed.");
        }
        var terminal = IsTerminal(target);
        if (terminal && result == null)
        {
            ThrowInvariant("missing_tool_observation", "A terminal tool state requires an observation.");
        }
        if (!terminal && result != null)
        {
            ThrowInvariant("unexpected_tool_observation", "Only a terminal tool state can carry an observation.");
        }
        if (result != null && result.InvocationId != invocationId)
        {
            ThrowInvariant("tool_observation_mismatch", "The tool observation belongs to another invocation.");
        }
        if (target == RuntimeToolInvocationStatus.Succeeded && result?.Success != true)
        {
            ThrowInvariant("invalid_success_observation", "A succeeded tool requires a successful observation.");
        }
        if (target != RuntimeToolInvocationStatus.Succeeded && result?.Success == true)
        {
            ThrowInvariant("invalid_failure_observation", "A denied, failed, or cancelled tool cannot carry a successful observation.");
        }
        if (target == RuntimeToolInvocationStatus.Succeeded && result?.Error != null)
        {
            ThrowInvariant("unexpected_tool_error", "A successful tool observation cannot carry an error.");
        }
        if (terminal && target != RuntimeToolInvocationStatus.Succeeded && result?.Error == null)
        {
            ThrowInvariant("missing_tool_error", "An unsuccessful terminal tool observation requires a typed error.");
        }

        invocations[index] = current with { Status = target, Result = SnapshotResult(result) };
        var updatedStep = step with
        {
            Phase = RuntimeStepPhase.ExecutingTools,
            ToolInvocations = Array.AsReadOnly(invocations)
        };
        var toolCallCount = turn.Progress.ToolCallCount +
            (target == RuntimeToolInvocationStatus.Executing ? 1 : 0);
        var requiredSatisfied = turn.Progress.RequiredToolSatisfied ||
            target == RuntimeToolInvocationStatus.Succeeded &&
            string.Equals(
                current.Call.Name,
                turn.Progress.RequiredToolName,
                StringComparison.OrdinalIgnoreCase);
        var updatedTurn = turn with
        {
            Progress = turn.Progress with
            {
                ToolCallCount = toolCallCount,
                RequiredToolSatisfied = requiredSatisfied
            }
        };
        return ReplaceActiveStep(session, updatedTurn, updatedStep);
    }

    public static RuntimeSessionState RecordContinuation(
        RuntimeSessionState session,
        string? requiredToolName = null)
    {
        var turn = RequireRunningTurn(session);
        var step = turn.Steps.LastOrDefault();
        if (step == null || step.Phase != RuntimeStepPhase.Completed)
        {
            ThrowInvariant("continuation_before_step_completion", "A continuation requires a completed Step.");
        }
        if (turn.Progress.ContinuationCount >= step.Context.Budget.MaxContinuations)
        {
            ThrowInvariant("continuation_budget_exhausted", "The Turn exhausted its continuation budget.");
        }

        var normalized = RuntimeTurnProgress.NormalizeOptional(requiredToolName);
        return ReplaceActiveTurn(
            session,
            turn with
            {
                Progress = turn.Progress with
                {
                    ContinuationCount = turn.Progress.ContinuationCount + 1,
                    RequiredToolName = normalized ?? turn.Progress.RequiredToolName,
                    RequiredToolSatisfied = normalized == null && turn.Progress.RequiredToolSatisfied
                }
            });
    }

    public static RuntimeSessionState AdvanceHistory(RuntimeSessionState session, int committedBatches = 1)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (committedBatches <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(committedBatches));
        }
        if (session.ActiveTurn?.Steps.LastOrDefault() is { } step && !IsTerminal(step.Phase))
        {
            ThrowInvariant("history_commit_during_active_step", "History can advance only at a Step boundary.");
        }
        return session with { HistoryVersion = checked(session.HistoryVersion + committedBatches) };
    }

    public static RuntimeSessionState TransitionStep(
        RuntimeSessionState session,
        RuntimeStepId stepId,
        RuntimeStepPhase target,
        RuntimeError? error = null)
    {
        var turn = RequireRunningTurn(session);
        if (turn.Steps.Count == 0 || turn.Steps[^1].Context.StepId != stepId)
        {
            ThrowInvariant("step_not_active", "Only the active Step can transition.");
        }

        var current = turn.Steps[^1];
        if (!CanTransition(current.Phase, target))
        {
            ThrowInvariant(
                "illegal_step_transition",
                $"Step transition {current.Phase} -> {target} is not allowed.");
        }
        if (target == RuntimeStepPhase.Failed && error == null)
        {
            ThrowInvariant("missing_step_error", "A failed Step requires a typed error.");
        }
        if (target != RuntimeStepPhase.Failed && error != null)
        {
            ThrowInvariant("unexpected_step_error", "Only a failed Step can carry a typed error.");
        }
        if (current.Phase == RuntimeStepPhase.Sampling &&
            target is RuntimeStepPhase.ResolvingTools or RuntimeStepPhase.CommittingObservation)
        {
            ThrowInvariant(
                "model_output_not_committed",
                "Sampling can advance only by committing validated model output.");
        }
        if (current.Phase == RuntimeStepPhase.ResolvingTools && target == RuntimeStepPhase.ExecutingTools)
        {
            ThrowInvariant(
                "tool_lifecycle_not_started",
                "Tool execution can start only through a tool lifecycle transition.");
        }
        if (current.Phase == RuntimeStepPhase.ExecutingTools &&
            target == RuntimeStepPhase.CommittingObservation &&
            current.ToolInvocations?.Any(invocation => !IsTerminal(invocation.Status)) == true)
        {
            ThrowInvariant(
                "tool_observation_missing",
                "Every tool invocation must reach an observed terminal state before observation commit.");
        }
        if (current.Phase == RuntimeStepPhase.CommittingObservation &&
            target == RuntimeStepPhase.Completed &&
            current.Output == null)
        {
            ThrowInvariant("model_output_missing", "A completed Step requires committed model output.");
        }

        var updatedSteps = turn.Steps.ToArray();
        updatedSteps[^1] = current with { Phase = target, Error = error };
        return ReplaceActiveTurn(session, turn with { Steps = updatedSteps });
    }

    public static RuntimeSessionState FinishTurn(
        RuntimeSessionState session,
        RuntimeTurnStatus status,
        RuntimeTerminationReason terminationReason,
        RuntimeError? error = null)
    {
        var turn = RequireRunningTurn(session);
        if (status == RuntimeTurnStatus.Running)
        {
            ThrowInvariant("turn_not_terminal", "FinishTurn requires a terminal Turn status.");
        }
        var last = turn.Steps.LastOrDefault();
        if (last != null && !IsTerminal(last.Phase))
        {
            ThrowInvariant("step_not_terminal", "A Turn cannot finish while its Step is active.");
        }
        if (status == RuntimeTurnStatus.Completed && last?.Phase != RuntimeStepPhase.Completed)
        {
            ThrowInvariant("completed_turn_without_completed_step", "A completed Turn requires a completed final Step.");
        }
        if (status == RuntimeTurnStatus.Failed && error == null)
        {
            ThrowInvariant("missing_turn_error", "A failed Turn requires a typed error.");
        }
        if (status != RuntimeTurnStatus.Failed && error != null)
        {
            ThrowInvariant("unexpected_turn_error", "Only a failed Turn can carry a typed error.");
        }

        var terminal = turn with
        {
            Status = status,
            TerminationReason = terminationReason,
            Error = error
        };
        return session with
        {
            ActiveTurn = null,
            TerminalTurns = Array.AsReadOnly(session.TerminalTurns.Append(terminal).ToArray())
        };
    }

    private static RuntimeTurnState RequireRunningTurn(RuntimeSessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var turn = session.ActiveTurn;
        if (turn == null)
        {
            ThrowInvariant("no_active_turn", "The Session has no running Turn.");
        }
        if (turn.Status != RuntimeTurnStatus.Running)
        {
            ThrowInvariant("turn_not_running", "The active Turn is not running.");
        }
        return turn;
    }

    private static RuntimeSessionState ReplaceActiveTurn(
        RuntimeSessionState session,
        RuntimeTurnState turn)
        => session with { ActiveTurn = turn };

    private static RuntimeSessionState ReplaceActiveStep(
        RuntimeSessionState session,
        RuntimeTurnState turn,
        RuntimeStepState step)
    {
        var steps = turn.Steps.ToArray();
        steps[^1] = step;
        return ReplaceActiveTurn(session, turn with { Steps = Array.AsReadOnly(steps) });
    }

    private static (RuntimeTurnState Turn, RuntimeStepState Step) RequireActiveStep(
        RuntimeSessionState session,
        RuntimeStepId stepId,
        RuntimeStepPhase phase)
    {
        var turn = RequireRunningTurn(session);
        if (turn.Steps.Count == 0 || turn.Steps[^1].Context.StepId != stepId)
        {
            ThrowInvariant("step_not_active", "Only the active Step can be updated.");
        }
        var step = turn.Steps[^1];
        if (step.Phase != phase)
        {
            ThrowInvariant("unexpected_step_phase", $"The active Step must be in {phase}, but was {step.Phase}.");
        }
        return (turn, step);
    }

    private static bool IsTerminal(RuntimeStepPhase phase)
        => phase is RuntimeStepPhase.Completed or RuntimeStepPhase.Failed or RuntimeStepPhase.Cancelled;

    private static bool IsTerminal(RuntimeToolInvocationStatus status)
        => status is RuntimeToolInvocationStatus.Denied or
            RuntimeToolInvocationStatus.Succeeded or
            RuntimeToolInvocationStatus.Failed or
            RuntimeToolInvocationStatus.Cancelled;

    private static bool CanTransitionTool(RuntimeToolInvocationStatus current, RuntimeToolInvocationStatus target)
        => (current, target) switch
        {
            (RuntimeToolInvocationStatus.Requested, RuntimeToolInvocationStatus.AwaitingApproval) => true,
            (RuntimeToolInvocationStatus.Requested, RuntimeToolInvocationStatus.Denied) => true,
            (RuntimeToolInvocationStatus.Requested, RuntimeToolInvocationStatus.Cancelled) => true,
            (RuntimeToolInvocationStatus.Requested, RuntimeToolInvocationStatus.Executing) => true,
            (RuntimeToolInvocationStatus.AwaitingApproval, RuntimeToolInvocationStatus.Approved) => true,
            (RuntimeToolInvocationStatus.AwaitingApproval, RuntimeToolInvocationStatus.Denied) => true,
            (RuntimeToolInvocationStatus.AwaitingApproval, RuntimeToolInvocationStatus.Cancelled) => true,
            (RuntimeToolInvocationStatus.Approved, RuntimeToolInvocationStatus.Executing) => true,
            (RuntimeToolInvocationStatus.Approved, RuntimeToolInvocationStatus.Denied) => true,
            (RuntimeToolInvocationStatus.Approved, RuntimeToolInvocationStatus.Cancelled) => true,
            (RuntimeToolInvocationStatus.Executing, RuntimeToolInvocationStatus.Succeeded) => true,
            (RuntimeToolInvocationStatus.Executing, RuntimeToolInvocationStatus.Failed) => true,
            (RuntimeToolInvocationStatus.Executing, RuntimeToolInvocationStatus.Cancelled) => true,
            _ => false
        };

    private static RuntimeModelOutput SnapshotOutput(RuntimeModelOutput output)
        => output with
        {
            Items = Array.AsReadOnly(output.Items
                .Select(SnapshotItem)
                .ToArray()),
            Usage = output.Usage with
            {
                Additional = new ReadOnlyDictionary<string, long>(
                    new Dictionary<string, long>(
                        output.Usage.Additional,
                        StringComparer.Ordinal))
            },
            Warnings = Array.AsReadOnly(output.Warnings.ToArray())
        };

    private static RuntimeItem SnapshotItem(RuntimeItem item)
        => item switch
        {
            RuntimeTextItem text => text,
            RuntimeReasoningItem reasoning => reasoning,
            RuntimeToolCallItem toolCall => toolCall with
            {
                Call = toolCall.Call with { Arguments = toolCall.Call.Arguments.Clone() }
            },
            RuntimeToolResultItem toolResult => toolResult with
            {
                Result = toolResult.Result with
                {
                    Artifacts = toolResult.Result.Artifacts == null
                        ? null
                        : Array.AsReadOnly(toolResult.Result.Artifacts.ToArray())
                }
            },
            RuntimeArtifactItem artifact => artifact,
            _ => throw new ArgumentException(
                $"Unsupported Runtime item type '{item.GetType().FullName}'.",
                nameof(item))
        };

    private static RuntimeToolResult? SnapshotResult(RuntimeToolResult? result)
        => result == null
            ? null
            : result with
            {
                Artifacts = result.Artifacts == null
                    ? null
                    : Array.AsReadOnly(result.Artifacts.ToArray())
            };

    private static RuntimeUsageTotals AddUsage(RuntimeUsageTotals current, RuntimeUsageTotals added)
    {
        var additional = new Dictionary<string, long>(current.Additional, StringComparer.Ordinal);
        foreach (var pair in added.Additional)
        {
            additional[pair.Key] = checked(additional.GetValueOrDefault(pair.Key) + pair.Value);
        }
        return new RuntimeUsageTotals(
            checked(current.InputTokens + added.InputTokens),
            checked(current.OutputTokens + added.OutputTokens),
            checked(current.TotalTokens + added.TotalTokens),
            new ReadOnlyDictionary<string, long>(additional));
    }

    private static bool CanTransition(RuntimeStepPhase current, RuntimeStepPhase target)
    {
        if (!IsTerminal(current) && target is RuntimeStepPhase.Failed or RuntimeStepPhase.Cancelled)
        {
            return true;
        }

        return (current, target) switch
        {
            (RuntimeStepPhase.Preparing, RuntimeStepPhase.Sampling) => true,
            (RuntimeStepPhase.Sampling, RuntimeStepPhase.ResolvingTools) => true,
            (RuntimeStepPhase.Sampling, RuntimeStepPhase.CommittingObservation) => true,
            (RuntimeStepPhase.ResolvingTools, RuntimeStepPhase.ExecutingTools) => true,
            (RuntimeStepPhase.ResolvingTools, RuntimeStepPhase.CommittingObservation) => true,
            (RuntimeStepPhase.ExecutingTools, RuntimeStepPhase.CommittingObservation) => true,
            (RuntimeStepPhase.CommittingObservation, RuntimeStepPhase.Completed) => true,
            _ => false
        };
    }

    [DoesNotReturn]
    private static void ThrowInvariant(string code, string message)
        => throw new RuntimeStateTransitionException(new RuntimeError(
            RuntimeErrorCategory.RuntimeInvariantViolation,
            code,
            message));
}

public sealed class RuntimeStateTransitionException(RuntimeError error) : Exception(error.Message)
{
    public RuntimeError Error { get; } = error;
}
