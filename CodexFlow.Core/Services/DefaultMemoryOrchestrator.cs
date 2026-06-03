using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Services;

/// <summary>
/// Phase 6: Default implementation of <see cref="IMemoryOrchestrator"/>.
/// <para>
/// Single entry point for all memory write operations. Callers no longer scatter
/// writes across <see cref="IMemoryService"/>, <see cref="IWorkspaceVectorStoreService"/>,
/// and <see cref="IDriftGovernor"/> independently.
/// </para>
/// <para>
/// Error-handling contract:
/// <list type="bullet">
///   <item>Primary-storage writes (Mongo) propagate exceptions — callers must handle them.</item>
///   <item>Vector-projection writes (Qdrant) are caught and logged — callers are never thrown.</item>
///   <item>Freshness / drift state updates are best-effort — failures are logged, not thrown.</item>
/// </list>
/// </para>
/// </summary>
public sealed class DefaultMemoryOrchestrator : IMemoryOrchestrator
{
    // Metadata key tracking the last fact write timestamp (used by DriftGovernor).
    private const string MetaKeyLastFactWriteAt = "LastFactWriteAt";
    private const string MetaKeyLastVectorSyncAt = "LastVectorSyncAt";

    private readonly IMemoryService _memoryService;
    private readonly IFactGuardService _factGuard;
    private readonly IDriftGovernor _driftGovernor;
    private readonly ISessionStore _sessionStore;
    private readonly IVectorSyncAdapter? _vectorSync;
    private readonly ILogger<DefaultMemoryOrchestrator> _logger;

    public DefaultMemoryOrchestrator(
        IMemoryService memoryService,
        IFactGuardService factGuard,
        IDriftGovernor driftGovernor,
        ISessionStore sessionStore,
        ILogger<DefaultMemoryOrchestrator> logger,
        IVectorSyncAdapter? vectorSync = null)
    {
        _memoryService = memoryService;
        _factGuard = factGuard;
        _driftGovernor = driftGovernor;
        _sessionStore = sessionStore;
        _logger = logger;
        _vectorSync = vectorSync;
    }

    /// <inheritdoc/>
    public async Task WriteFactAsync(
        CodexSession session,
        string key,
        string value,
        string category = "general",
        string? metadata = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        // 1. Primary store write — via fact guard (may detect conflicts for protected keys).
        var outcome = await _factGuard.GuardedLearnFactAsync(
            session.Id, key, value, category, metadata, ct).ConfigureAwait(false);

        _logger.LogDebug(
            "MemoryOrchestrator: WriteFact key={Key} session={SessionId} outcome={Outcome}.",
            key, session.Id, outcome);

        // 2. Update the LastFactWriteAt timestamp so DriftGovernor can detect vector staleness.
        if (outcome == FactWriteOutcome.Written)
        {
            await BestEffortUpdateTimestampAsync(session, MetaKeyLastFactWriteAt, ct).ConfigureAwait(false);

            // 3. Mark vector projection as pending sync (non-blocking).
            await BestEffortAsync(
                () => _driftGovernor.MarkVectorStalAsync(session.Id, $"fact '{key}' updated", ct),
                "mark vector stale after fact write").ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task UpdateProjectSummaryAsync(
        CodexSession session,
        ProjectMemoryWriteResult writeResult,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(writeResult);

        // 1. Update only the summary-related fields on the latest session snapshot.
        session.ProjectSummary = writeResult.Content;
        await UpdateLatestSessionAsync(
            session.Id,
            latest => latest.ProjectSummary = writeResult.Content,
            ct).ConfigureAwait(false);

        // 2. Write ProjectSummaryVersion fact (primary store — must succeed).
        await _factGuard.GuardedLearnFactAsync(
            session.Id,
            ProjectMemoryFactKeys.ProjectSummaryVersion,
            writeResult.Document.UpdatedAtUtc.ToString("O"),
            MemoryFactCategories.Project,
            new MemoryEntryMetadata(
                Scope: MemoryFactScope.Project,
                Source: "UpdateProjectSummary",
                Confidence: MemoryFactConfidence.High).ToJson(),
            ct).ConfigureAwait(false);

        // 3. Clear summary stale flag (best-effort).
        await BestEffortAsync(
            async () =>
            {
                await UpdateLatestSessionAsync(
                    session.Id,
                    latest =>
                    {
                        latest.Metadata.Remove("SummaryDriftState");
                        latest.Metadata.Remove("SummaryDriftReason");
                    },
                    ct).ConfigureAwait(false);
            },
            "clear summary stale flag").ConfigureAwait(false);

        // 4. Mark vector projection as pending sync (non-blocking).
        await BestEffortAsync(
            () => _driftGovernor.MarkVectorStalAsync(
                session.Id, "project summary updated — Qdrant rebuild required", ct),
            "mark vector stale after summary update").ConfigureAwait(false);

        _logger.LogInformation(
            "MemoryOrchestrator: ProjectSummary updated for session {SessionId}.", session.Id);
    }

    /// <inheritdoc/>
    public async Task AppendEventAsync(
        string sessionId,
        string eventType,
        string data,
        CancellationToken ct = default)
    {
        // Events go directly to the primary store (Mongo).
        // Failures must propagate — event logging is a reliability contract.
        await _memoryService.LogEventAsync(sessionId, eventType, data, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task TriggerVectorSyncAsync(
        CodexSession session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (_vectorSync == null)
        {
            _logger.LogDebug(
                "MemoryOrchestrator: No IVectorSyncAdapter registered — skipping Qdrant sync for session {SessionId}.",
                session.Id);
            return;
        }

        // Vector sync is always best-effort — failures must not propagate to callers.
        await BestEffortAsync(async () =>
        {
            var succeeded = await _vectorSync.TrySyncAsync(session, ct).ConfigureAwait(false);

            if (succeeded)
            {
                _logger.LogInformation(
                    "MemoryOrchestrator: Qdrant sync completed for session {SessionId}.",
                    session.Id);

                session.Metadata[MetaKeyLastVectorSyncAt] = DateTime.UtcNow.ToString("O");
                session.Metadata.Remove("VectorDriftState");
                session.Metadata.Remove("VectorDriftReason");
                await _sessionStore.SaveSessionAsync(session, ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning(
                    "MemoryOrchestrator: Qdrant sync returned failure for session {SessionId}.",
                    session.Id);
            }
        }, "Qdrant vector sync").ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpdateFreshnessAsync(
        CodexSession session,
        bool isStale,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Update only freshness-related metadata on the latest session snapshot.
        session.Metadata["MemoryFreshnessState"] = isStale ? "stale" : "fresh";
        if (isStale)
        {
            session.Metadata["MemoryFreshnessReason"] = reason;
        }
        else
        {
            session.Metadata.Remove("MemoryFreshnessReason");
        }

        await UpdateLatestSessionAsync(
            session.Id,
            latest =>
            {
                latest.Metadata["MemoryFreshnessState"] = isStale ? "stale" : "fresh";
                if (isStale)
                {
                    latest.Metadata["MemoryFreshnessReason"] = reason;
                }
                else
                {
                    latest.Metadata.Remove("MemoryFreshnessReason");
                }
            },
            ct).ConfigureAwait(false);

        // Persist a typed fact so future sessions can deserialise the freshness state.
        var state = new MemoryFreshnessState(isStale, reason, DateTime.UtcNow);
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        await BestEffortAsync(
            () => _memoryService.LearnFactAsync(
                session.Id,
                ProjectMemoryFactKeys.MemoryFreshness,
                json,
                MemoryFactCategories.Analysis,
                ct: ct),
            "persist freshness fact").ConfigureAwait(false);

        _logger.LogInformation(
            "MemoryOrchestrator: Freshness updated for session {SessionId}. IsStale={IsStale}, Reason={Reason}.",
            session.Id, isStale, reason);
    }

    /// <inheritdoc/>
    public async Task RunAutoRefreshAsync(
        CodexSession session,
        AutoRefreshTrigger trigger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await BestEffortAsync(
            () => _driftGovernor.ApplyAutoRefreshAsync(session, trigger, ct),
            $"auto-refresh trigger {trigger}").ConfigureAwait(false);

        // After the drift governor runs its refresh actions, run a vector sync pass.
        await TriggerVectorSyncAsync(session, ct).ConfigureAwait(false);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Executes an async action in a best-effort manner: exceptions are caught and
    /// logged with the given <paramref name="operationName"/> label, never rethrown.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "BestEffortAsync is intentionally a degradation boundary — all failures must be swallowed and logged.")]
    private async Task BestEffortAsync(Func<Task> action, string operationName)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MemoryOrchestrator: Non-critical operation '{Operation}' failed — degrading gracefully.",
                operationName);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Timestamp update is a best-effort metadata operation; failure must not block the caller.")]
    private async Task BestEffortUpdateTimestampAsync(
        CodexSession session,
        string metaKey,
        CancellationToken ct)
    {
        try
        {
            session.Metadata[metaKey] = DateTime.UtcNow.ToString("O");
            await UpdateLatestSessionAsync(
                session.Id,
                latest => latest.Metadata[metaKey] = session.Metadata[metaKey],
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "MemoryOrchestrator: Failed to update timestamp key '{Key}' — non-critical.",
                metaKey);
        }
    }

    private async Task UpdateLatestSessionAsync(
        string sessionId,
        Action<CodexSession> mutate,
        CancellationToken ct)
    {
        var latest = await _sessionStore.GetSessionAsync(sessionId, ct).ConfigureAwait(false)
            ?? new CodexSession { Id = sessionId };

        mutate(latest);
        await _sessionStore.SaveSessionAsync(latest, ct).ConfigureAwait(false);
    }
}
