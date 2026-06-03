using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Runtime;

public interface IRecoveryDecisionApplier
{
    Task<QueryRecoveryApplicationResult> ApplyAsync(
        QueryRecoveryApplicationRequest request,
        CancellationToken ct = default);
}

public sealed record QueryRecoveryApplicationRequest
{
    public required QueryRuntimeRequest RuntimeRequest { get; init; }

    public required QueryRuntimeState State { get; init; }

    public required IQueryRuntimeEventSink EventSink { get; init; }

    public required Guid QueryId { get; init; }

    public required long SeqBase { get; init; }

    public required RecoveryDecision Decision { get; init; }

    public required string RecoveryType { get; init; }

    public required RuntimeState RecoveryFlag { get; init; }

    public required string ContinueReason { get; init; }

    public required string ExhaustedMessage { get; init; }

    public string? PromptOverride { get; init; }

    public required bool AllowToolCallsOnNextRound { get; init; }
}

public sealed record QueryRecoveryApplicationResult
{
    public required bool Handled { get; init; }

    public required bool Continued { get; init; }

    public required bool Terminal { get; init; }

    public RecoveryAction? Action { get; init; }

    public string? PromptInjection { get; init; }
}

public sealed class DefaultRecoveryDecisionApplier : IRecoveryDecisionApplier
{
    private readonly IQueryRecoveryPolicy? _recoveryPolicy;
    private readonly IQueryLoopTelemetry? _telemetry;

    public DefaultRecoveryDecisionApplier(
        IQueryRecoveryPolicy? recoveryPolicy,
        IQueryLoopTelemetry? telemetry)
    {
        _recoveryPolicy = recoveryPolicy;
        _telemetry = telemetry;
    }

    public async Task<QueryRecoveryApplicationResult> ApplyAsync(
        QueryRecoveryApplicationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RuntimeRequest);
        ArgumentNullException.ThrowIfNull(request.State);
        ArgumentNullException.ThrowIfNull(request.EventSink);
        ArgumentNullException.ThrowIfNull(request.Decision);

        var runtimeRequest = request.RuntimeRequest;
        var state = request.State;
        var decision = request.Decision;
        var action = _recoveryPolicy?.GetRecoveryAction(decision, state);
        var shouldTerminate = !decision.NeedsRecovery ||
            (_recoveryPolicy?.ShouldTerminate(state, decision.Type) ?? false);

        state.RecoveryCount++;
        state.Flags |= request.RecoveryFlag;

        _telemetry?.RecordRecovery(new QueryLoopRecovery(
            request.QueryId,
            runtimeRequest.SessionId,
            runtimeRequest.EntryPoint,
            state.Round,
            request.RecoveryType,
            decision.CurrentAttempt,
            Continued: !shouldTerminate,
            Terminal: shouldTerminate));

        await EmitEventAsync(request.EventSink, QueryRuntimeEventType.RecoveryTriggered, new RecoveryTriggeredEvent(
            Seq: request.SeqBase + 900 + decision.CurrentAttempt,
            QueryId: request.QueryId,
            SessionId: runtimeRequest.SessionId,
            EntryPoint: runtimeRequest.EntryPoint,
            Round: state.Round,
            RecoveryType: request.RecoveryType,
            Attempt: decision.CurrentAttempt,
            Reason: decision.Reason)).ConfigureAwait(false);

        if (shouldTerminate)
        {
            state.TerminationReason = QueryTerminationReason.RecoveryExhausted;
            state.LastAssistantText.Clear();
            state.LastAssistantText.Append(request.ExhaustedMessage);
            state.LastNonEmptyAssistantText.Clear();
            state.LastNonEmptyAssistantText.Append(request.ExhaustedMessage);

            await EmitEventAsync(request.EventSink, QueryRuntimeEventType.Error, new ErrorEvent(
                Seq: request.SeqBase + 980,
                QueryId: request.QueryId,
                SessionId: runtimeRequest.SessionId,
                EntryPoint: runtimeRequest.EntryPoint,
                ErrorType: nameof(QueryTerminationReason.RecoveryExhausted),
                Message: request.ExhaustedMessage)).ConfigureAwait(false);

            return new QueryRecoveryApplicationResult
            {
                Handled = true,
                Continued = false,
                Terminal = true,
                Action = action
            };
        }

        if (request.AllowToolCallsOnNextRound)
        {
            state.ForceAllowToolCallsNextRound = true;
        }
        else
        {
            state.ForceDisableToolCallsNextRound = true;
        }

        if (action?.ReducedOptions is { Count: > 0 })
        {
            state.NextRoundOptionOverrides = action.ReducedOptions;
        }

        var promptInjection = !string.IsNullOrWhiteSpace(request.PromptOverride)
            ? request.PromptOverride
            : _recoveryPolicy?.GetRecoveryPrompt(action ?? new RecoveryAction(RecoveryActionType.Continue), state);
        if (!string.IsNullOrWhiteSpace(promptInjection))
        {
            state.Messages.Add(new ChatMessage(ChatRole.User, promptInjection));
            await EmitEventAsync(request.EventSink, QueryRuntimeEventType.SystemNotice, new SystemNoticeEvent(
                Seq: request.SeqBase + 950 + decision.CurrentAttempt,
                QueryId: request.QueryId,
                SessionId: runtimeRequest.SessionId,
                EntryPoint: runtimeRequest.EntryPoint,
                NoticeType: request.RecoveryType,
                Content: promptInjection)).ConfigureAwait(false);
        }

        if (action?.RetryDelayMs is > 0)
        {
            await Task.Delay(action.RetryDelayMs.Value, ct).ConfigureAwait(false);
        }

        state.LastContinueReason = request.ContinueReason;
        return new QueryRecoveryApplicationResult
        {
            Handled = true,
            Continued = true,
            Terminal = false,
            Action = action,
            PromptInjection = promptInjection
        };
    }

    private static async ValueTask EmitEventAsync(
        IQueryRuntimeEventSink sink,
        QueryRuntimeEventType eventType,
        QueryRuntimeEvent runtimeEvent)
    {
        if (sink.IsEnabled(eventType))
        {
            await sink.OnEventAsync(runtimeEvent).ConfigureAwait(false);
        }
    }
}
