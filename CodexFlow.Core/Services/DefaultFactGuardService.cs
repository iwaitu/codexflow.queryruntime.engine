using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Services;

/// <summary>
/// Phase 4: Default implementation of <see cref="IFactGuardService"/>.
/// <para>
/// Applies conflict detection for protected and high-confidence facts before
/// delegating to <see cref="IMemoryService.LearnFactAsync"/>.
/// Pending conflicts (EnqueueReview policy) are stored in-memory for the lifetime
/// of the service instance; a persistent store can be swapped in later.
/// </para>
/// </summary>
public sealed class DefaultFactGuardService : IFactGuardService
{
    private readonly IMemoryService _memoryService;
    private readonly ILogger<DefaultFactGuardService> _logger;

    // In-process review queue keyed by (sessionId, key).
    // Replace with a Redis/Mongo-backed store when persistence is required.
    private readonly Dictionary<(string SessionId, string Key), FactConflictRecord> _pendingConflicts = new();

    public DefaultFactGuardService(IMemoryService memoryService, ILogger<DefaultFactGuardService> logger)
    {
        _memoryService = memoryService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<FactWriteOutcome> GuardedLearnFactAsync(
        string sessionId,
        string key,
        string value,
        string category = "general",
        string? metadata = null,
        CancellationToken ct = default)
    {
        // Determine the incoming confidence from the metadata envelope.
        var incomingMeta = TryDeserializeMetadata(metadata);
        var incomingConfidence = incomingMeta?.Confidence ?? MemoryFactConfidence.Low;
        var incomingSource = incomingMeta?.Source;

        // Non-protected keys: apply SilentOverwrite (legacy default).
        if (!ProtectedFactKeys.Contains(key))
        {
            await _memoryService.LearnFactAsync(sessionId, key, value, category, metadata, ct).ConfigureAwait(false);
            return FactWriteOutcome.Written;
        }

        // Protected key: look up existing fact to evaluate the conflict policy.
        var existingFacts = await _memoryService.RecallFactsAsync(sessionId, key, ct).ConfigureAwait(false);
        var existing = existingFacts.FirstOrDefault(f =>
            string.Equals(f.Key, key, StringComparison.Ordinal));

        if (existing == null)
        {
            // First write of a protected key — allow it.
            await _memoryService.LearnFactAsync(sessionId, key, value, category, metadata, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "FactGuard: First write of protected key {Key} in session {SessionId}.",
                key, sessionId);
            return FactWriteOutcome.Written;
        }

        var existingMeta = TryDeserializeMetadata(existing.Metadata);
        var existingConfidence = existingMeta?.Confidence ?? MemoryFactConfidence.Low;

        // Choose policy based on confidence levels.
        var policy = DeterminePolicy(existingConfidence, incomingConfidence);

        switch (policy)
        {
            case FactConflictPolicy.KeepHighestConfidence:
                _logger.LogInformation(
                    "FactGuard: Skipping write to protected key {Key} (existing confidence={ExistingConf} >= incoming={IncomingConf}).",
                    key, existingConfidence, incomingConfidence);
                return FactWriteOutcome.Skipped;

            case FactConflictPolicy.EnqueueReview:
                var conflict = new FactConflictRecord(
                    SessionId: sessionId,
                    Key: key,
                    ExistingValue: existing.Value,
                    ExistingConfidence: existingConfidence,
                    ExistingSourceEventId: existingMeta?.SourceEventId,
                    IncomingValue: value,
                    IncomingConfidence: incomingConfidence,
                    IncomingSource: incomingSource,
                    AppliedPolicy: FactConflictPolicy.EnqueueReview,
                    DetectedAtUtc: DateTime.UtcNow);
                _pendingConflicts[(sessionId, key)] = conflict;
                _logger.LogWarning(
                    "FactGuard: Conflict enqueued for protected key {Key} in session {SessionId}. Existing={ExistingConf}, Incoming={IncomingConf}.",
                    key, sessionId, existingConfidence, incomingConfidence);
                return FactWriteOutcome.ConflictEnqueued;

            case FactConflictPolicy.KeepLatest:
            case FactConflictPolicy.SilentOverwrite:
            default:
                await _memoryService.LearnFactAsync(sessionId, key, value, category, metadata, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "FactGuard: Overwrote protected key {Key} in session {SessionId} (policy={Policy}).",
                    key, sessionId, policy);
                return FactWriteOutcome.Written;
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FactConflictRecord>> GetPendingConflictsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        var results = _pendingConflicts
            .Where(kv => string.Equals(kv.Key.SessionId, sessionId, StringComparison.Ordinal))
            .Select(kv => kv.Value)
            .ToList();

        return Task.FromResult<IReadOnlyList<FactConflictRecord>>(results);
    }

    /// <inheritdoc/>
    public async Task ResolveConflictAsync(
        string sessionId,
        string key,
        bool acceptIncoming,
        CancellationToken ct = default)
    {
        var conflictKey = (sessionId, key);
        if (!_pendingConflicts.TryGetValue(conflictKey, out var conflict))
        {
            return;
        }

        _pendingConflicts.Remove(conflictKey);

        if (acceptIncoming)
        {
            await _memoryService.LearnFactAsync(
                sessionId, key, conflict.IncomingValue,
                ct: ct).ConfigureAwait(false);
            _logger.LogInformation(
                "FactGuard: Conflict resolved — accepted incoming value for key {Key} in session {SessionId}.",
                key, sessionId);
        }
        else
        {
            _logger.LogInformation(
                "FactGuard: Conflict resolved — retained existing value for key {Key} in session {SessionId}.",
                key, sessionId);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Determines which <see cref="FactConflictPolicy"/> applies given the two
    /// confidence levels.
    /// <list type="bullet">
    ///   <item>Incoming higher than existing → KeepLatest (allow overwrite).</item>
    ///   <item>Existing is high-confidence and incoming is lower → KeepHighestConfidence (skip).</item>
    ///   <item>Both high-confidence and different → EnqueueReview.</item>
    /// </list>
    /// </summary>
    private static FactConflictPolicy DeterminePolicy(string existingConfidence, string incomingConfidence)
    {
        var existingRank = ConfidenceRank(existingConfidence);
        var incomingRank = ConfidenceRank(incomingConfidence);

        if (incomingRank > existingRank)
        {
            return FactConflictPolicy.KeepLatest;
        }

        if (existingRank >= ConfidenceRank(MemoryFactConfidence.High) &&
            incomingRank >= ConfidenceRank(MemoryFactConfidence.High))
        {
            return FactConflictPolicy.EnqueueReview;
        }

        return FactConflictPolicy.KeepHighestConfidence;
    }

    private static int ConfidenceRank(string confidence) => confidence switch
    {
        MemoryFactConfidence.High => 3,
        MemoryFactConfidence.Medium => 2,
        MemoryFactConfidence.Low => 1,
        _ => 0,
    };

    private static MemoryEntryMetadata? TryDeserializeMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MemoryEntryMetadata>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
