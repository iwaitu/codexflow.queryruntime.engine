using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Runtime;

public sealed record ToolPlan
{
    public required IReadOnlyList<FunctionCallContent> Calls { get; init; }

    public required string AssistantText { get; init; }

    public string? ThinkingText { get; init; }

    public bool FromLegacyTextFallback { get; init; }
}

public sealed record ToolPlanValidationResult
{
    public required IReadOnlyList<FunctionCallContent> AcceptedCalls { get; init; }

    public required IReadOnlyList<RejectedToolCall> RejectedCalls { get; init; }

    public bool RequiresRecovery { get; init; }

    public string? RecoveryReason { get; init; }
}

public sealed record ToolPlanValidationRequest
{
    public required ToolPlan ToolPlan { get; init; }

    public required QueryRuntimeRequest RuntimeRequest { get; init; }

    public string? RequiredToolNameForRound { get; init; }

    public IReadOnlyCollection<FunctionCallContent> PrestartedStreamingCalls { get; init; } = [];
}

public sealed record ToolPlanValidationOutput
{
    public required ToolPlanValidationResult ValidationResult { get; init; }

    public required IReadOnlyList<FunctionCallContent> ExecutableToolCalls { get; init; }

    public required IReadOnlyList<BlockedToolResult> BlockedToolResults { get; init; }

    public required IReadOnlyList<ChatMessage> InjectedPreExecutionMessages { get; init; }

    public required IReadOnlyList<string> RequiredToolViolations { get; init; }
}

public sealed record BlockedToolResult
{
    public required FunctionCallContent Call { get; init; }

    public required string Transcript { get; init; }
}

public sealed record ToolArgumentNormalizationRequest
{
    public required IReadOnlyList<FunctionCallContent> Calls { get; init; }

    public required QueryRuntimeRequest RuntimeRequest { get; init; }

    public required QueryRuntimeState State { get; init; }

    public IReadOnlyCollection<FunctionCallContent> PrestartedStreamingCalls { get; init; } = [];
}

public sealed record ToolArgumentNormalizationResult
{
    public required IReadOnlyList<FunctionCallContent> Calls { get; init; }

    public int NormalizedCallCount { get; init; }
}

public sealed record RejectedToolCall
{
    public required string ToolName { get; init; }

    public required string CallId { get; init; }

    public required string ReasonCode { get; init; }

    public required string Detail { get; init; }
}

public static class ToolPlanRejectionReasons
{
    public const string ExplorationLimitExceeded = "exploration_limit_exceeded";
    public const string NonRequiredToolInRequiredRound = "non_required_tool_in_required_round";
    public const string RuntimeInterventionBlocked = "runtime_intervention_blocked";
}
