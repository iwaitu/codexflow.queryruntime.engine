using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Services;

/// <summary>
/// Phase 7: Assembles scenario-aware memory sections from a <see cref="CodexSession"/>
/// and any persisted facts available via <see cref="IMemoryService"/>.
/// <para>
/// This implementation reads from the in-memory <see cref="CodexSession.ActiveFacts"/>
/// collection (already loaded by <see cref="CodexSessionManager"/>) to avoid a
/// redundant round-trip to MongoDB on every LLM call. Persisted-only facts are
/// loaded on-demand only for the <see cref="MemoryInjectionScenario.BackgroundResume"/>
/// and <see cref="MemoryInjectionScenario.NotificationSummary"/> scenarios where the
/// in-process session state may be incomplete.
/// </para>
/// </summary>
public sealed class DefaultMemoryContextAssembler(
    IMemoryService memoryService,
    ILogger<DefaultMemoryContextAssembler> logger) : IMemoryContextAssembler
{
    /// <inheritdoc/>
    public async Task<MemoryContextEnvelope> AssembleAsync(
        MemoryContextRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return request.Scenario switch
            {
                MemoryInjectionScenario.Planning => AssemblePlanning(request),
                MemoryInjectionScenario.Execution => AssembleExecution(request),
                MemoryInjectionScenario.Validation => AssembleValidation(request),
                MemoryInjectionScenario.Chat => AssembleChat(request),
                MemoryInjectionScenario.BackgroundResume => await AssembleBackgroundResumeAsync(request, ct).ConfigureAwait(false),
                MemoryInjectionScenario.NotificationSummary => await AssembleNotificationSummaryAsync(request, ct).ConfigureAwait(false),
                _ => MemoryContextEnvelope.Empty,
            };
        }
#pragma warning disable CA1031 // Assembler must not throw regardless of upstream failures.
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MemoryContextAssembler: Failed to assemble context for scenario {Scenario}. Returning empty envelope.", request.Scenario);
            return MemoryContextEnvelope.Empty;
        }
#pragma warning restore CA1031
    }

    // ── Scenario-specific builders ────────────────────────────────────────────

    private static MemoryContextEnvelope AssemblePlanning(MemoryContextRequest request)
    {
        // Planning agents already inject ProjectSummary, DependencyGraphSummary, and
        // ArchitectureAudit inline in their system prompt. Return empty to avoid duplication.
        // Downstream: DefaultCodexPlanner passes Session=null to skip enrichment entirely.
        return MemoryContextEnvelope.Empty;
    }

    private static MemoryContextEnvelope AssembleExecution(MemoryContextRequest request)
    {
        var sections = new List<MemoryContextSection>();

        if (request.IncludeSummary)
            AddSummary(sections, request.Session);

        if (request.IncludeFacts)
            AddFactIfPresent(sections, request.Session, ProjectMemoryFactKeys.ProjectFingerprint, "ProjectFingerprint");

        return new MemoryContextEnvelope(sections);
    }

    private static MemoryContextEnvelope AssembleValidation(MemoryContextRequest request)
    {
        // Validator already builds full context inline.
        return MemoryContextEnvelope.Empty;
    }

    private static MemoryContextEnvelope AssembleChat(MemoryContextRequest request)
    {
        var sections = new List<MemoryContextSection>();

        if (request.IncludeSummary)
            AddSummary(sections, request.Session);

        if (request.IncludeFacts)
        {
            AddFactIfPresent(sections, request.Session, ProjectMemoryFactKeys.ArchitectureAudit, "ArchitectureAudit");
            AddFactIfPresent(sections, request.Session, "DependencyGraphSummary", "DependencyGraph");
        }

        return new MemoryContextEnvelope(sections);
    }

    private async Task<MemoryContextEnvelope> AssembleBackgroundResumeAsync(
        MemoryContextRequest request,
        CancellationToken ct)
    {
        var sections = new List<MemoryContextSection>();
        AddSummary(sections, request.Session);

        // Load persisted facts for background jobs — session may be freshly loaded.
        var facts = await memoryService.RecallFactsAsync(request.Session.Id, ct: ct).ConfigureAwait(false);
        foreach (var fact in facts.Take(8))
        {
            if (!string.IsNullOrWhiteSpace(fact.Value))
                sections.Add(new MemoryContextSection(fact.Key, fact.Value));
        }

        return new MemoryContextEnvelope(sections);
    }

    private async Task<MemoryContextEnvelope> AssembleNotificationSummaryAsync(
        MemoryContextRequest request,
        CancellationToken ct)
    {
        var sections = new List<MemoryContextSection>();
        AddSummary(sections, request.Session);

        var facts = await memoryService.RecallFactsAsync(request.Session.Id, ct: ct).ConfigureAwait(false);
        var recentFacts = facts
            .OrderByDescending(f => f.CreatedAt)
            .Take(5)
            .ToList();

        foreach (var fact in recentFacts)
        {
            if (!string.IsNullOrWhiteSpace(fact.Value))
                sections.Add(new MemoryContextSection(fact.Key, fact.Value));
        }

        return new MemoryContextEnvelope(sections);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AddSummary(List<MemoryContextSection> sections, CodexSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.ProjectSummary))
            sections.Add(new MemoryContextSection("ProjectSummary", session.ProjectSummary));
    }

    private static void AddFactIfPresent(
        List<MemoryContextSection> sections,
        CodexSession session,
        string factKey,
        string label)
    {
        var value = session.ActiveFacts.FirstOrDefault(f => f.Key == factKey)?.Value;
        if (!string.IsNullOrWhiteSpace(value))
            sections.Add(new MemoryContextSection(label, value));
    }
}
