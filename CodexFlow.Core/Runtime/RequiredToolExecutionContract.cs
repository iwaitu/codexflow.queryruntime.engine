using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Runtime-level contract requiring at least one matching tool execution before a query may stop.
/// </summary>
public sealed record RequiredToolExecutionContract
{
    /// <summary>Stable contract name for diagnostics and telemetry.</summary>
    public required string Name { get; init; }

    /// <summary>Any of these tool names satisfies the contract.</summary>
    public required IReadOnlyList<string> AnyOfToolNames { get; init; }

    /// <summary>When true, only successful tool results satisfy the contract.</summary>
    public bool RequireSuccessfulResult { get; init; } = true;

    /// <summary>Maximum stop-hook continuations before the runtime fails the query.</summary>
    public int MaxContinuationAttempts { get; init; } = 2;

    /// <summary>Preferred tool to require during the recovery round, when it is available.</summary>
    public string? PreferredRecoveryToolName { get; init; }

    /// <summary>Optional feedback injected when the model attempts to stop before satisfying the contract.</summary>
    public string? Feedback { get; init; }

    /// <summary>Optional failure message when the contract remains unsatisfied after all continuations.</summary>
    public string? ExhaustionMessage { get; init; }

    public bool IsSatisfiedBy(
        IReadOnlySet<string> executedToolNames,
        IReadOnlySet<string> successfulToolNames)
    {
        ArgumentNullException.ThrowIfNull(executedToolNames);
        ArgumentNullException.ThrowIfNull(successfulToolNames);

        var observed = RequireSuccessfulResult ? successfulToolNames : executedToolNames;
        return AnyOfToolNames.Any(observed.Contains);
    }

    public string? ResolveRequiredToolName(IEnumerable<AIFunction>? availableTools)
        => ResolveRequiredToolName(availableTools?.Select(tool => tool.Name));

    public string? ResolveRequiredToolName(IEnumerable<string>? availableToolNames)
    {
        var available = new HashSet<string>(
            availableToolNames ?? [],
            StringComparer.OrdinalIgnoreCase);

        if (available.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(PreferredRecoveryToolName) &&
            available.Contains(PreferredRecoveryToolName))
        {
            return PreferredRecoveryToolName;
        }

        return AnyOfToolNames.FirstOrDefault(available.Contains);
    }

    public string FormatToolList()
        => string.Join(", ", AnyOfToolNames.Select(static name => $"`{name}`"));
}
