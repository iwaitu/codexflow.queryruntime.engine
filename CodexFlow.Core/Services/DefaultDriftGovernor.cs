using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Services;

/// <summary>
/// Phase 5: Default implementation of <see cref="IDriftGovernor"/>.
/// <para>
/// Detects drift across three independent dimensions:
/// <list type="bullet">
///   <item><b>CodeStructure</b> — repo head/fingerprint has changed since last analysis.</item>
///   <item><b>ProjectSummary</b> — PROJECT_SUMMARY.md version mismatches stored fact.</item>
///   <item><b>VectorProjection</b> — facts or summaries were updated after last Qdrant sync.</item>
/// </list>
/// </para>
/// </summary>
public sealed class DefaultDriftGovernor : IDriftGovernor
{
    // Session-metadata keys used to track per-dimension stale state.
    private const string MetaKeyVectorStale = "VectorDriftState";
    private const string MetaKeySummaryStale = "SummaryDriftState";
    private const string MetaKeyVectorStaleReason = "VectorDriftReason";
    private const string MetaKeySummaryStalReason = "SummaryDriftReason";
    private const string MetaKeyLastVectorSyncAt = "LastVectorSyncAt";
    private const string MetaKeyLastFactWriteAt = "LastFactWriteAt";

    private readonly IMemoryService _memoryService;
    private readonly IVectorSyncAdapter? _vectorSync;
    private readonly ISessionStore _sessionStore;
    private readonly ILogger<DefaultDriftGovernor> _logger;

    public DefaultDriftGovernor(
        IMemoryService memoryService,
        ISessionStore sessionStore,
        ILogger<DefaultDriftGovernor> logger,
        IVectorSyncAdapter? vectorSync = null)
    {
        _memoryService = memoryService;
        _sessionStore = sessionStore;
        _logger = logger;
        _vectorSync = vectorSync;
    }

    /// <inheritdoc/>
    public async Task<MemoryDriftReport> CheckDriftAsync(
        CodexSession session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var results = new List<MemoryDriftCheckResult>
        {
            await CheckCodeStructureDriftAsync(session, ct).ConfigureAwait(false),
            await CheckProjectSummaryDriftAsync(session, ct).ConfigureAwait(false),
            CheckVectorProjectionDrift(session),
        };

        var hasAny = results.Any(r => r.IsDrifted);
        var report = new MemoryDriftReport(
            SessionId: session.Id,
            HasAnyDrift: hasAny,
            Results: results,
            GeneratedAtUtc: DateTime.UtcNow);

        if (hasAny)
        {
            _logger.LogInformation(
                "DriftGovernor: Drift detected for session {SessionId}. Dimensions={Dimensions}",
                session.Id,
                string.Join(", ", results.Where(r => r.IsDrifted).Select(r => r.DriftType)));
        }

        return report;
    }

    /// <inheritdoc/>
    public async Task ApplyAutoRefreshAsync(
        CodexSession session,
        AutoRefreshTrigger trigger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        _logger.LogInformation(
            "DriftGovernor: AutoRefresh triggered for session {SessionId}, trigger={Trigger}.",
            session.Id, trigger);

        // Each case only manages state (stale/fresh markers).
        // Actual Qdrant vector sync is always delegated to DefaultMemoryOrchestrator.RunAutoRefreshAsync
        // to avoid double-sync when the orchestrator calls both ApplyAutoRefreshAsync and TriggerVectorSyncAsync.
        switch (trigger)
        {
            case AutoRefreshTrigger.AnalyzeProjectCompleted:
                await ClearCodeStructureStalenessAsync(session, ct).ConfigureAwait(false);
                break;

            case AutoRefreshTrigger.PlanExecutionClosed:
                await MarkVectorStalAsync(session.Id, "plan execution closed — facts updated", ct).ConfigureAwait(false);
                break;

            case AutoRefreshTrigger.ManualSummarySaved:
                await MarkSummaryStalAsync(session.Id, "manual summary saved — version may differ from facts", ct).ConfigureAwait(false);
                break;

            case AutoRefreshTrigger.BackgroundJobCompleted:
                await MarkVectorStalAsync(session.Id, "background job completed — result facts pending sync", ct).ConfigureAwait(false);
                break;
        }
    }

    /// <inheritdoc/>
    public async Task MarkVectorStalAsync(string sessionId, string reason, CancellationToken ct = default)
    {
        var session = await _sessionStore.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (session == null)
        {
            return;
        }

        session.Metadata[MetaKeyVectorStale] = "stale";
        session.Metadata[MetaKeyVectorStaleReason] = reason;
        await _sessionStore.SaveSessionAsync(session, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "DriftGovernor: Vector projection marked stale for session {SessionId}. Reason={Reason}",
            sessionId, reason);
    }

    /// <inheritdoc/>
    public async Task MarkSummaryStalAsync(string sessionId, string reason, CancellationToken ct = default)
    {
        var session = await _sessionStore.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (session == null)
        {
            return;
        }

        session.Metadata[MetaKeySummaryStale] = "stale";
        session.Metadata[MetaKeySummaryStalReason] = reason;
        await _sessionStore.SaveSessionAsync(session, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "DriftGovernor: Project summary marked stale for session {SessionId}. Reason={Reason}",
            sessionId, reason);
    }

    // ── Private drift check helpers ───────────────────────────────────────────

    private async Task<MemoryDriftCheckResult> CheckCodeStructureDriftAsync(
        CodexSession session,
        CancellationToken ct)
    {
        // Compare stored RepoSnapshot head-commit with current MemoryFreshness state.
        var facts = await _memoryService.RecallFactsAsync(session.Id, ct: ct).ConfigureAwait(false);

        var freshnessFact = facts.FirstOrDefault(f =>
            string.Equals(f.Key, ProjectMemoryFactKeys.MemoryFreshness, StringComparison.Ordinal));

        if (freshnessFact == null)
        {
            return new MemoryDriftCheckResult(
                MemoryDriftType.CodeStructure,
                IsDrifted: false,
                Reason: "No freshness fact stored — assuming fresh.",
                CheckedAtUtc: DateTime.UtcNow);
        }

        MemoryFreshnessState? freshnessState = null;
        try
        {
            freshnessState = JsonSerializer.Deserialize<MemoryFreshnessState>(freshnessFact.Value);
        }
        catch (System.Text.Json.JsonException)
        {
            // Unparseable fact — treat as not drifted to avoid false positive resets.
        }

        if (freshnessState?.IsStale == true)
        {
            return new MemoryDriftCheckResult(
                MemoryDriftType.CodeStructure,
                IsDrifted: true,
                Reason: freshnessState.Reason,
                CheckedAtUtc: DateTime.UtcNow);
        }

        return new MemoryDriftCheckResult(
            MemoryDriftType.CodeStructure,
            IsDrifted: false,
            Reason: "Code structure is current.",
            CheckedAtUtc: DateTime.UtcNow);
    }

    private async Task<MemoryDriftCheckResult> CheckProjectSummaryDriftAsync(
        CodexSession session,
        CancellationToken ct)
    {
        // Detect mismatch: session.Metadata["SummaryDriftState"] == "stale" OR
        // ProjectSummary is empty but ProjectSummaryVersion fact exists (version > 0).
        if (session.Metadata.TryGetValue(MetaKeySummaryStale, out var summaryDrift) &&
            string.Equals(summaryDrift, "stale", StringComparison.OrdinalIgnoreCase))
        {
            var reason = session.Metadata.TryGetValue(MetaKeySummaryStalReason, out var r) ? r : "summary drift detected";
            return new MemoryDriftCheckResult(
                MemoryDriftType.ProjectSummary,
                IsDrifted: true,
                Reason: reason,
                CheckedAtUtc: DateTime.UtcNow);
        }

        // Cross-check: if ProjectSummary is blank but a ProjectSummaryVersion fact exists,
        // the in-memory session state is out of sync.
        if (string.IsNullOrWhiteSpace(session.ProjectSummary))
        {
            var facts = await _memoryService.RecallFactsAsync(session.Id, ct: ct).ConfigureAwait(false);
            var versionFact = facts.FirstOrDefault(f =>
                string.Equals(f.Key, ProjectMemoryFactKeys.ProjectSummaryVersion, StringComparison.Ordinal));

            if (versionFact != null && !string.IsNullOrWhiteSpace(versionFact.Value))
            {
                return new MemoryDriftCheckResult(
                    MemoryDriftType.ProjectSummary,
                    IsDrifted: true,
                    Reason: "Session ProjectSummary is empty but a version fact exists — summary not loaded.",
                    CheckedAtUtc: DateTime.UtcNow);
            }
        }

        return new MemoryDriftCheckResult(
            MemoryDriftType.ProjectSummary,
            IsDrifted: false,
            Reason: "Project summary is consistent.",
            CheckedAtUtc: DateTime.UtcNow);
    }

    private static MemoryDriftCheckResult CheckVectorProjectionDrift(CodexSession session)
    {
        // Check session metadata set by MarkVectorStalAsync or fact-write paths.
        if (session.Metadata.TryGetValue(MetaKeyVectorStale, out var vectorDrift) &&
            string.Equals(vectorDrift, "stale", StringComparison.OrdinalIgnoreCase))
        {
            var reason = session.Metadata.TryGetValue(MetaKeyVectorStaleReason, out var r)
                ? r
                : "vector projection is stale";
            return new MemoryDriftCheckResult(
                MemoryDriftType.VectorProjection,
                IsDrifted: true,
                Reason: reason,
                CheckedAtUtc: DateTime.UtcNow);
        }

        // Check timestamp-based staleness: if LastFactWriteAt > LastVectorSyncAt.
        if (session.Metadata.TryGetValue(MetaKeyLastFactWriteAt, out var lastWriteStr) &&
            session.Metadata.TryGetValue(MetaKeyLastVectorSyncAt, out var lastSyncStr))
        {
            if (DateTime.TryParse(lastWriteStr, out var lastWrite) &&
                DateTime.TryParse(lastSyncStr, out var lastSync) &&
                lastWrite > lastSync)
            {
                return new MemoryDriftCheckResult(
                    MemoryDriftType.VectorProjection,
                    IsDrifted: true,
                    Reason: $"Facts were updated at {lastWrite:u} but Qdrant was last synced at {lastSync:u}.",
                    CheckedAtUtc: DateTime.UtcNow);
            }
        }

        return new MemoryDriftCheckResult(
            MemoryDriftType.VectorProjection,
            IsDrifted: false,
            Reason: "Vector projection is current.",
            CheckedAtUtc: DateTime.UtcNow);
    }

    // ── Private action helpers ────────────────────────────────────────────────

    private async Task ClearCodeStructureStalenessAsync(CodexSession session, CancellationToken ct)
    {
        // Write a fresh MemoryFreshnessState fact.
        var freshState = new MemoryFreshnessState(
            IsStale: false,
            Reason: "analyze_project completed",
            CheckedAtUtc: DateTime.UtcNow);
        var json = JsonSerializer.Serialize(freshState);
        await _memoryService.LearnFactAsync(
            session.Id,
            ProjectMemoryFactKeys.MemoryFreshness,
            json,
            MemoryFactCategories.Analysis,
            ct: ct).ConfigureAwait(false);

        // Also clear the session metadata freshness flags.
        session.Metadata["MemoryFreshnessState"] = "fresh";
        session.Metadata.Remove("MemoryFreshnessReason");
        await _sessionStore.SaveSessionAsync(session, ct).ConfigureAwait(false);
    }

    private async Task TriggerVectorRebuildAsync(CodexSession session, CancellationToken ct)
    {
        if (_vectorSync == null)
        {
            _logger.LogDebug(
                "DriftGovernor: No IVectorSyncAdapter registered — skipping vector rebuild for session {SessionId}.",
                session.Id);
            return;
        }

        var succeeded = await _vectorSync.TrySyncAsync(session, ct).ConfigureAwait(false);

        if (succeeded)
        {
            _logger.LogInformation(
                "DriftGovernor: Vector rebuild completed for session {SessionId}.",
                session.Id);

            session.Metadata.Remove(MetaKeyVectorStale);
            session.Metadata.Remove(MetaKeyVectorStaleReason);
            session.Metadata[MetaKeyLastVectorSyncAt] = DateTime.UtcNow.ToString("O");
            await _sessionStore.SaveSessionAsync(session, ct).ConfigureAwait(false);
        }
        else
        {
            _logger.LogWarning(
                "DriftGovernor: Vector rebuild failed for session {SessionId} — degrading gracefully.",
                session.Id);
        }
    }
}
