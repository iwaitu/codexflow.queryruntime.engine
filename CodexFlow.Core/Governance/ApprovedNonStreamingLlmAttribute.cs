namespace CodexFlow.Core.Governance;

/// <summary>
/// Marks intentionally approved non-streaming LLM entry points so future analyzers
/// can distinguish governed facades from ad-hoc direct calls.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ApprovedNonStreamingLlmAttribute(string reason) : Attribute
{
    public string Reason { get; } = string.IsNullOrWhiteSpace(reason)
        ? throw new ArgumentException("Reason is required.", nameof(reason))
        : reason;

    public string? Ticket { get; init; }

    public string? ReviewBy { get; init; }

    public ApprovedNonStreamingLlmScope Scope { get; init; } = ApprovedNonStreamingLlmScope.Exception;
}

public enum ApprovedNonStreamingLlmScope
{
    Exception,
    Facade
}
