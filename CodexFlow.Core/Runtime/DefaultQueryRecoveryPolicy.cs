using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Phase 1: 默认查询恢复策略实现
/// </summary>
public sealed class DefaultQueryRecoveryPolicy : IQueryRecoveryPolicy
{
    private readonly ILogger<DefaultQueryRecoveryPolicy> _logger;

    public DefaultQueryRecoveryPolicy(ILogger<DefaultQueryRecoveryPolicy> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public RecoveryDecision DetectRecoveryNeeded(
        QueryRuntimeState state,
        QueryRuntimeRequest request,
        RecoveryContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // Check for transport failures
        if (context.LastException is not null)
        {
            return new RecoveryDecision(
                Type: RecoveryType.TransportFailure,
                NeedsRecovery: state.TransportFailureCount <= GetMaxRecoveryAttempts(request),
                Reason: "Transient transport failure detected",
                CurrentAttempt: state.TransportFailureCount);
        }

        // Check for empty response
        if (string.IsNullOrWhiteSpace(context.LastResponseText) &&
            (context.LastToolCalls == null || context.LastToolCalls.Count == 0))
        {
            state.EmptyResponseCount++;
            _logger.LogDebug("Empty response detected, count: {Count}", state.EmptyResponseCount);

            return new RecoveryDecision(
                Type: RecoveryType.EmptyResponse,
                NeedsRecovery: state.EmptyResponseCount <= GetMaxRecoveryAttempts(request),
                Reason: "LLM returned empty response",
                CurrentAttempt: state.EmptyResponseCount);
        }

        // Check for malformed protocol
        if (context.LastToolCalls != null && context.LastToolCalls.Any(call => string.IsNullOrWhiteSpace(call.Name)))
        {
            return new RecoveryDecision(
                Type: RecoveryType.MalformedProtocol,
                NeedsRecovery: state.MalformedProtocolCount <= GetMaxRecoveryAttempts(request),
                Reason: "Malformed tool-call protocol detected",
                CurrentAttempt: state.MalformedProtocolCount);
        }

        // Check for zero tool call (if this is considered a recovery scenario)
        if (context.LastToolCalls == null || context.LastToolCalls.Count == 0)
        {
            return new RecoveryDecision(
                Type: RecoveryType.ZeroToolCall,
                NeedsRecovery: false, // Zero tool call is normal termination, not recovery
                Reason: "No tool calls in response",
                CurrentAttempt: state.ZeroToolCallRounds);
        }

        // Check for stall (consecutive same tool calls)
        if (request.AdapterHints?.EnableStallDetection == true &&
            state.ConsecutiveSameToolCount >= request.AdapterHints.MaxConsecutiveSameTool)
        {
            return new RecoveryDecision(
                Type: RecoveryType.StallDetected,
                NeedsRecovery: true,
                Reason: $"Detected stall: same tool called {state.ConsecutiveSameToolCount} times consecutively",
                CurrentAttempt: state.ConsecutiveSameToolCount);
        }

        // Check for context limit
        if (request.AdapterHints?.EnableContextLimitCheck == true &&
            request.AdapterHints.ContextHardLimit.HasValue &&
            context.ContextChars > request.AdapterHints.ContextHardLimit.Value)
        {
            return new RecoveryDecision(
                Type: RecoveryType.ContextHardLimit,
                NeedsRecovery: false, // Cannot recover from context limit without compaction
                Reason: $"Context size {context.ContextChars} exceeds hard limit {request.AdapterHints.ContextHardLimit}",
                CurrentAttempt: 0);
        }

        // No recovery needed
        return new RecoveryDecision(
            Type: RecoveryType.None,
            NeedsRecovery: false,
            Reason: "Normal operation",
            CurrentAttempt: 0);
    }

    /// <inheritdoc/>
    public RecoveryAction GetRecoveryAction(
        RecoveryDecision decision,
        QueryRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(state);

        return decision.Type switch
        {
            RecoveryType.EmptyResponse => new RecoveryAction(
                Type: RecoveryActionType.InjectMessageAndRetry,
                PromptInjection: "Please provide a response. If you need more information, please ask.",
                RetryDelayMs: 100),

            RecoveryType.MalformedProtocol => new RecoveryAction(
                Type: RecoveryActionType.InjectMessageAndRetry,
                PromptInjection: "There was an issue with the tool call format. Please try again with properly formatted tool calls.",
                RetryDelayMs: 100),

            RecoveryType.StallDetected => new RecoveryAction(
                Type: RecoveryActionType.InjectUrgencyPrompt,
                PromptInjection: "You seem to be stuck calling the same tool repeatedly. Please consider a different approach or provide a summary of what you've learned.",
                RetryDelayMs: 100),

            RecoveryType.ContextHardLimit => new RecoveryAction(
                Type: RecoveryActionType.Terminate,
                PromptInjection: null,
                RetryDelayMs: null),

            _ => new RecoveryAction(Type: RecoveryActionType.Continue)
        };
    }

    /// <inheritdoc/>
    public string? GetRecoveryPrompt(
        RecoveryAction action,
        QueryRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(state);

        return action.PromptInjection;
    }

    /// <inheritdoc/>
    public bool ShouldTerminate(
        QueryRuntimeState state,
        RecoveryType recoveryType)
    {
        ArgumentNullException.ThrowIfNull(state);

        return recoveryType switch
        {
            RecoveryType.ContextHardLimit => true,
            _ => false
        };
    }

    private static int GetMaxRecoveryAttempts(QueryRuntimeRequest request)
    {
        return request.AdapterHints?.MaxRecoveryAttempts ?? 3;
    }
}
