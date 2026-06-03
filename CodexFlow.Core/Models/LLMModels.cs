using Microsoft.Extensions.AI;
using System.Text;

namespace CodexFlow.Core.Models;

// ─── Phase 7: LLM Call Facade Models ──────────────────────────────────────────

/// <summary>
/// Identifies the call-site scenario so <see cref="IMemoryContextAssembler"/>
/// can select the right memory sections for injection.
/// </summary>
public enum MemoryInjectionScenario
{
    /// <summary>Interactive chat turn from the user.</summary>
    Chat,

    /// <summary>Task-plan generation pass.</summary>
    Planning,

    /// <summary>Task execution (kernel reasoning cycle).</summary>
    Execution,

    /// <summary>Post-execution validation pass.</summary>
    Validation,

    /// <summary>Background job resumption after a pause or restart.</summary>
    BackgroundResume,

    /// <summary>Generating a digest / notification summary.</summary>
    NotificationSummary,
}

/// <summary>
/// Wraps an LLM call made through <see cref="ILLMExecutor"/>.
/// </summary>
/// <param name="Messages">The full message list to send to the LLM.</param>
/// <param name="Options">Sampling options, or <see langword="null"/> to use defaults.</param>
/// <param name="Scenario">The scenario that governs memory context selection.</param>
/// <param name="Session">
/// If provided, <see cref="IMemoryContextAssembler"/> enriches the system prompt
/// with relevant memory sections before the call is dispatched.
/// Pass <see langword="null"/> when the caller already embeds all required context inline.
/// </param>
/// <param name="Caller">Optional diagnostic label (class or method name) for observability.</param>
public sealed record LLMExecutionRequest(
    IReadOnlyList<ChatMessage> Messages,
    ChatOptions? Options,
    MemoryInjectionScenario Scenario,
    CodexSession? Session = null,
    string? Caller = null);

/// <summary>
/// Specifies what memory sections to assemble for a given scenario and session.
/// </summary>
/// <param name="Session">The session whose memory should be assembled.</param>
/// <param name="Scenario">Scenario controlling section selection.</param>
/// <param name="IncludeFacts">Whether scenario-specific active facts are included.</param>
/// <param name="IncludeSummary">Whether the project summary is included.</param>
public sealed record MemoryContextRequest(
    CodexSession Session,
    MemoryInjectionScenario Scenario,
    bool IncludeFacts = true,
    bool IncludeSummary = true);

/// <summary>
/// A single named block of memory content to be injected into a prompt.
/// </summary>
public sealed record MemoryContextSection(string Label, string Content);

/// <summary>
/// The assembled set of memory sections ready to be prepended to a system prompt.
/// </summary>
public sealed record MemoryContextEnvelope(IReadOnlyList<MemoryContextSection> Sections)
{
    /// <summary>Singleton empty envelope — use when no enrichment is needed.</summary>
    public static readonly MemoryContextEnvelope Empty =
        new(Array.Empty<MemoryContextSection>());

    /// <summary>Returns <see langword="true"/> when all sections are empty.</summary>
    public bool IsEmpty => Sections.Count == 0 ||
                           Sections.All(s => string.IsNullOrWhiteSpace(s.Content));

    /// <summary>Renders all non-empty sections as a single fenced block.</summary>
    public string AsSystemPromptBlock()
    {
        if (IsEmpty) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("=== Memory Context ===");
        foreach (var s in Sections.Where(s => !string.IsNullOrWhiteSpace(s.Content)))
        {
            sb.AppendLine($"[{s.Label}]");
            sb.AppendLine(s.Content);
        }
        sb.AppendLine("=== End Memory Context ===");
        return sb.ToString();
    }
}
