using System;
using System.Threading;
using System.Threading.Tasks;

namespace CodexFlow.Core.Abstractions;

/// <summary>
/// Represents a persisted snapshot of a session's compression state.
/// </summary>
public sealed record SessionCompressionState(
    string HistorySummary,
    DateTime LastCompressedAtUtc);

/// <summary>
/// Abstraction for persisting and retrieving session compression projections.
/// Used by <see cref="CodexFlow.Core.Agents.CodexSessionManager"/> to survive
/// Redis TTL expiry without losing the conversation summary.
/// </summary>
public interface ISessionCompressionStore
{
    /// <summary>
    /// Loads the compression projection for the given session from durable storage.
    /// Returns null if no projection exists or if the session has never been compressed.
    /// </summary>
    Task<SessionCompressionState?> LoadAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Persists (idempotent overwrite) the compression projection for the given session.
    /// </summary>
    Task SaveAsync(string sessionId, string historySummary, DateTime lastCompressedAtUtc, CancellationToken ct = default);

    /// <summary>
    /// Clears the compression projection for the given session (e.g. on workspace reset).
    /// </summary>
    Task ClearAsync(string sessionId, CancellationToken ct = default);
}
