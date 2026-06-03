using CodexFlow.Core.Telemetry;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Stable terminal detail codes for runtime results, events, and telemetry.
/// Keep this list small and product-facing so UI/ops can build on it safely.
/// </summary>
public static class QueryTerminalDetailCodes
{
    public const string Completed = "completed";
    public const string NoToolCalls = "no_tool_calls";
    public const string MaxRoundsReached = "max_rounds_reached";
    public const string ContextHardLimitReached = "context_hard_limit_reached";
    public const string StallDetected = "stall_detected";
    public const string AwaitingUserConfirmation = "awaiting_user_confirmation";
    public const string UnhandledException = "unhandled_exception";
    public const string RecoveryExhausted = "recovery_exhausted";
    public const string RecoveryExhaustedEmptyResponse = "recovery_exhausted_empty_response";
    public const string RecoveryExhaustedMalformedProtocol = "recovery_exhausted_malformed_protocol";
    public const string RecoveryExhaustedZeroToolCall = "recovery_exhausted_zero_tool_call";
    public const string RecoveryExhaustedUnexecutedWriteIntent = "recovery_exhausted_unexecuted_write_intent";
    public const string RequiredToolContractViolation = "required_tool_contract_violation";
    public const string RecoveryExhaustedTransportFailure = "recovery_exhausted_transport_failure";
    public const string RecoveryExhaustedStall = "recovery_exhausted_stall_detected";
    public const string EmptyResponseFallback = "empty_response_fallback";
    public const string AutoDispatched = "auto_dispatched";
    public const string WrapUpToolCallsSuppressed = "wrap_up_tool_calls_suppressed";
    public const string WrapUpContinuationUsed = "wrap_up_continuation_used";

    public static string Resolve(QueryRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!string.IsNullOrWhiteSpace(state.TerminalDetailCode))
        {
            return state.TerminalDetailCode;
        }

        if (state.Flags.HasFlag(RuntimeState.WrapUpToolCallsSuppressed))
        {
            return WrapUpToolCallsSuppressed;
        }

        if (state.Flags.HasFlag(RuntimeState.WrapUpToolContinuationUsed)
            && state.TerminationReason == QueryTerminationReason.NoToolCalls)
        {
            return WrapUpContinuationUsed;
        }

        return state.TerminationReason switch
        {
            QueryTerminationReason.Normal => Completed,
            QueryTerminationReason.NoToolCalls => NoToolCalls,
            QueryTerminationReason.MaxRoundsReached => MaxRoundsReached,
            QueryTerminationReason.ContextHardLimit => ContextHardLimitReached,
            QueryTerminationReason.StallDetected => StallDetected,
            QueryTerminationReason.AwaitingUserConfirmation => AwaitingUserConfirmation,
            QueryTerminationReason.Exception => UnhandledException,
            QueryTerminationReason.EmptyResponseFallback => EmptyResponseFallback,
            QueryTerminationReason.AutoDispatched => AutoDispatched,
            QueryTerminationReason.RecoveryExhausted => ResolveRecoveryExhaustedCode(state),
            _ => Resolve(state.TerminationReason)
        };
    }

    public static string Resolve(QueryTerminationReason terminationReason)
    {
        return terminationReason switch
        {
            QueryTerminationReason.Normal => Completed,
            QueryTerminationReason.NoToolCalls => NoToolCalls,
            QueryTerminationReason.MaxRoundsReached => MaxRoundsReached,
            QueryTerminationReason.ContextHardLimit => ContextHardLimitReached,
            QueryTerminationReason.StallDetected => StallDetected,
            QueryTerminationReason.AwaitingUserConfirmation => AwaitingUserConfirmation,
            QueryTerminationReason.Exception => UnhandledException,
            QueryTerminationReason.EmptyResponseFallback => EmptyResponseFallback,
            QueryTerminationReason.AutoDispatched => AutoDispatched,
            QueryTerminationReason.RecoveryExhausted => RecoveryExhausted,
            _ => RecoveryExhausted
        };
    }

    private static string ResolveRecoveryExhaustedCode(QueryRuntimeState state)
    {
        if (state.Flags.HasFlag(RuntimeState.StallDetected))
        {
            return RecoveryExhaustedStall;
        }

        if (state.TransportFailureCount > 0 || state.Flags.HasFlag(RuntimeState.TransportFailureRecoveryUsed))
        {
            return RecoveryExhaustedTransportFailure;
        }

        if (state.MalformedProtocolCount > 0 || state.Flags.HasFlag(RuntimeState.MalformedProtocolRecoveryUsed))
        {
            return RecoveryExhaustedMalformedProtocol;
        }

        if (state.EmptyResponseCount > 0 || state.Flags.HasFlag(RuntimeState.EmptyResponseRecoveryUsed))
        {
            return RecoveryExhaustedEmptyResponse;
        }

        if (state.Flags.HasFlag(RuntimeState.UnexecutedWriteIntentRecoveryUsed))
        {
            return RecoveryExhaustedUnexecutedWriteIntent;
        }

        if (state.Flags.HasFlag(RuntimeState.RequiredToolContractRecoveryUsed))
        {
            return RequiredToolContractViolation;
        }

        if (state.ZeroToolCallRounds > 0 || state.Flags.HasFlag(RuntimeState.ZeroToolCallRecoveryUsed))
        {
            return RecoveryExhaustedZeroToolCall;
        }

        return RecoveryExhausted;
    }
}
