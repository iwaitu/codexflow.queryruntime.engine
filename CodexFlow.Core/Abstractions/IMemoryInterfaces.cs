using CodexFlow.Core.Models;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Abstractions;

// ─── Phase 4: Fact Guard ──────────────────────────────────────────────────────

/// <summary>
/// Phase 4: Guards fact writes against silent overwrites and surfaces conflicts
/// for protected or high-confidence keys.
/// <para>
/// This service sits <em>in front of</em> <see cref="IMemoryService.LearnFactAsync"/>.
/// Callers that want conflict detection must use <see cref="GuardedLearnFactAsync"/>
/// instead of writing directly to <see cref="IMemoryService"/>.
/// </para>
/// </summary>
public interface IFactGuardService
{
    /// <summary>
    /// Attempts to write a fact, applying the appropriate <see cref="FactConflictPolicy"/>.
    /// </summary>
    /// <param name="sessionId">Target session.</param>
    /// <param name="key">Fact key.</param>
    /// <param name="value">New value.</param>
    /// <param name="category">Fact category (see <see cref="MemoryFactCategories"/>).</param>
    /// <param name="metadata">Optional <see cref="MemoryEntryMetadata"/> JSON envelope.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="FactWriteOutcome"/> describing whether the write was applied,
    /// skipped, or queued for review.
    /// </returns>
    Task<FactWriteOutcome> GuardedLearnFactAsync(
        string sessionId,
        string key,
        string value,
        string category = "general",
        string? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns pending conflict records for the given session (EnqueueReview policy).
    /// </summary>
    Task<IReadOnlyList<FactConflictRecord>> GetPendingConflictsAsync(
        string sessionId,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves a queued conflict by accepting the incoming value or retaining the existing one.
    /// </summary>
    Task ResolveConflictAsync(
        string sessionId,
        string key,
        bool acceptIncoming,
        CancellationToken ct = default);
}

/// <summary>
/// Phase 4: Result of a guarded fact write attempt.
/// </summary>
public enum FactWriteOutcome
{
    /// <summary>Value was written (new or updated).</summary>
    Written,

    /// <summary>Existing value was kept because it had higher or equal confidence.</summary>
    Skipped,

    /// <summary>Conflict was enqueued for review; no write occurred yet.</summary>
    ConflictEnqueued,
}

// ─── Phase 5: Drift Governor ──────────────────────────────────────────────────

/// <summary>
/// Phase 5: Detects memory drift across code-structure, project-summary,
/// and vector-projection dimensions and coordinates auto-refresh actions.
/// </summary>
public interface IDriftGovernor
{
    /// <summary>
    /// Runs all three drift checks for the session and returns a composite report.
    /// This is a read-only operation — it does not mutate any state.
    /// </summary>
    Task<MemoryDriftReport> CheckDriftAsync(
        CodexSession session,
        CancellationToken ct = default);

    /// <summary>
    /// Applies the auto-refresh actions appropriate for the given trigger event.
    /// For example, <see cref="AutoRefreshTrigger.AnalyzeProjectCompleted"/> will
    /// re-queue a Qdrant sync and update the freshness fact.
    /// </summary>
    Task ApplyAutoRefreshAsync(
        CodexSession session,
        AutoRefreshTrigger trigger,
        CancellationToken ct = default);

    /// <summary>
    /// Marks the vector-projection dimension as stale for the session,
    /// so the next recall pass will lower-weight its candidates.
    /// Called when facts are updated but Qdrant sync has not yet run.
    /// </summary>
    Task MarkVectorStalAsync(string sessionId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Marks the project-summary dimension as stale (e.g. PROJECT_SUMMARY.md
    /// version does not match the stored <c>ProjectSummaryVersion</c> fact).
    /// </summary>
    Task MarkSummaryStalAsync(string sessionId, string reason, CancellationToken ct = default);
}

// ─── Phase 6: Memory Orchestrator ────────────────────────────────────────────

// ─── Phase 5/6: Vector sync adapter (Core-side abstraction) ──────────────────

/// <summary>
/// Thin adapter that Core services use to trigger a Qdrant vector sync
/// without taking a direct dependency on <c>CodexFlow.Application</c>.
/// Implemented by <c>WorkspaceVectorSyncAdapter</c> in the web-API project,
/// which delegates to <c>IWorkspaceVectorStoreService</c>.
/// </summary>
public interface IVectorSyncAdapter
{
    /// <summary>
    /// Synchronises the session's vector collection.
    /// Returns <c>true</c> when sync succeeded; <c>false</c> when it failed or was skipped.
    /// Implementations must not throw — all failures are surfaced via the return value and logs.
    /// </summary>
    Task<bool> TrySyncAsync(CodexSession session, CancellationToken ct = default);
}

/// <summary>
/// Phase 6: Unified entry point for all memory write operations.
/// <para>
/// Callers invoke the appropriate method here instead of scatter-calling
/// <see cref="IMemoryService"/>, <see cref="IWorkspaceVectorStoreService"/>, and
/// <see cref="IDriftGovernor"/> independently. The orchestrator ensures:
/// <list type="bullet">
///   <item>Primary-storage writes fail fast (exception propagated to caller).</item>
///   <item>Vector-projection writes degrade gracefully (logged, not thrown).</item>
///   <item>Freshness and drift state are updated consistently after every write.</item>
/// </list>
/// </para>
/// </summary>
public interface IMemoryOrchestrator
{
    /// <summary>
    /// Writes a fact to the primary store (Mongo) using the fact guard,
    /// then asynchronously marks the vector projection as pending sync.
    /// </summary>
    Task WriteFactAsync(
        CodexSession session,
        string key,
        string value,
        string category = "general",
        string? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the project summary section in PROJECT_SUMMARY.md
    /// and writes/updates the corresponding structured facts.
    /// </summary>
    Task UpdateProjectSummaryAsync(
        CodexSession session,
        ProjectMemoryWriteResult writeResult,
        CancellationToken ct = default);

    /// <summary>
    /// Appends an audit event to the event log.
    /// </summary>
    Task AppendEventAsync(
        string sessionId,
        string eventType,
        string data,
        CancellationToken ct = default);

    /// <summary>
    /// Triggers a full Qdrant sync for the session.
    /// Failures are caught and logged — the caller is never thrown.
    /// </summary>
    Task TriggerVectorSyncAsync(
        CodexSession session,
        CancellationToken ct = default);

    /// <summary>
    /// Updates memory freshness metadata on the session and persists the change.
    /// </summary>
    Task UpdateFreshnessAsync(
        CodexSession session,
        bool isStale,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Convenience method: applies an <see cref="AutoRefreshTrigger"/> through
    /// <see cref="IDriftGovernor"/> and handles any resulting sync actions.
    /// </summary>
    Task RunAutoRefreshAsync(
        CodexSession session,
        AutoRefreshTrigger trigger,
        CancellationToken ct = default);
}

// ─── Phase 7: LLM Facade ──────────────────────────────────────────────────────

/// <summary>
/// Phase 7: Thin facade over <see cref="IChatClient"/> that optionally enriches
/// the system prompt with scenario-appropriate memory context before dispatching
/// the call.
/// <para>
/// Agents that already embed all required context inline should pass
/// <c>Session = null</c> in <see cref="LLMExecutionRequest"/> to skip enrichment.
/// </para>
/// </summary>
public interface ILLMExecutor
{
    /// <summary>
    /// Executes a non-streaming chat completion, optionally injecting memory context.
    /// </summary>
    Task<ChatResponse> ExecuteAsync(LLMExecutionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Executes a streaming chat completion, optionally injecting memory context.
    /// </summary>
    IAsyncEnumerable<ChatResponseUpdate> StreamAsync(LLMExecutionRequest request, CancellationToken ct = default);
}

/// <summary>
/// Phase 7: Assembles scenario-aware memory sections from a <see cref="CodexSession"/>.
/// Used by <see cref="ILLMExecutor"/> to enrich prompts before LLM dispatch.
/// </summary>
public interface IMemoryContextAssembler
{
    /// <summary>
    /// Builds a <see cref="MemoryContextEnvelope"/> containing the memory sections
    /// appropriate for the given scenario. Never throws — returns empty envelope on error.
    /// </summary>
    Task<MemoryContextEnvelope> AssembleAsync(MemoryContextRequest request, CancellationToken ct = default);
}

public interface ISessionStore
{
    Task<CodexSession?> GetSessionAsync(string sessionId, CancellationToken ct = default);
    Task SaveSessionAsync(CodexSession session, CancellationToken ct = default);
    Task DeleteSessionAsync(string sessionId, CancellationToken ct = default);
}

public interface IMemoryService
{
    // --- 长期记忆：事实库 ---
    /// <param name="metadata">
    /// Phase 1: optional JSON-serialised <see cref="MemoryEntryMetadata"/> envelope.
    /// Pass <see langword="null"/> from existing callers — fully backward-compatible.
    /// </param>
    Task LearnFactAsync(string sessionId, string key, string value, string category = "general", string? metadata = null, CancellationToken ct = default);
#pragma warning disable CA1002 // Preserve legacy list-based API for compatibility.
    Task<List<MemoryEntry>> RecallFactsAsync(string sessionId, string query = "", CancellationToken ct = default);
#pragma warning restore CA1002
    Task ForgetFactAsync(string sessionId, string key, CancellationToken ct = default);

    // --- 全局记忆：用户事实与偏好 ---
    Task LearnUserFactAsync(string userId, string key, string value, string category = "preference", string? metadata = null, CancellationToken ct = default);
#pragma warning disable CA1002 // Preserve legacy list-based API for compatibility.
    Task<List<MemoryEntry>> RecallUserFactsAsync(string userId, string query = "", CancellationToken ct = default);
#pragma warning restore CA1002
    Task ForgetUserFactAsync(string userId, string key, CancellationToken ct = default);

    // --- 短期记忆：对话上下文管理 ---
    Task AppendTurnAsync(string sessionId, ChatTurn turn, CancellationToken ct = default);
#pragma warning disable CA1002 // Preserve legacy list-based API for compatibility.
    Task<List<ChatTurn>> GetRecentTurnsAsync(string sessionId, int count = 10, CancellationToken ct = default);
#pragma warning restore CA1002

    // --- 自动摘要与压缩 ---
    /// <summary>
    /// Produces a <b>conversation</b> summary — optimised for dialogue continuity.
    /// Called when the recent-turns buffer exceeds the configured threshold.
    /// </summary>
    Task<string> SummarizeHistoryAsync(string sessionId, string existingSummary, IReadOnlyList<ChatTurn> newTurns, CancellationToken ct = default);

    /// <summary>
    /// Phase 3: Produces a structured <b>execution</b> summary — focused on engineering outcomes
    /// (tasks completed, decisions made, verification results). Distinct from
    /// <see cref="SummarizeHistoryAsync"/> which targets dialogue continuity.
    /// </summary>
    /// <param name="sessionId">Session whose execution context is being summarised.</param>
    /// <param name="completedTasks">Ordered list of tasks that finished in this execution window.</param>
    /// <param name="verificationSummary">Optional verification or test-run output to include.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A concise, structured execution summary string (no hallucinated conclusions).</returns>
    Task<string> SummarizeExecutionAsync(
        string sessionId,
        IReadOnlyList<string> completedTasks,
        string? verificationSummary = null,
        CancellationToken ct = default);

    /// <summary>
    /// Phase 3: Parses a free-text execution summary and extracts structured facts
    /// (decisions, verification outcomes, key constraints) as typed key-value pairs.
    /// <para>
    /// The returned list represents (key, value, category) triples ready for
    /// <see cref="LearnFactAsync"/>. This is how "摘要转结构化 facts" is implemented:
    /// the raw summary is the source of truth, but structured items are derived
    /// and written as independent, queryable facts.
    /// </para>
    /// </summary>
    /// <param name="executionSummary">The full text produced by <see cref="SummarizeExecutionAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Zero or more (key, value, category) triples. Returns an empty list if
    /// no extractable structured facts are found or on error — never throws.
    /// </returns>
    Task<IReadOnlyList<(string Key, string Value, string Category)>> ExtractStructuredFactsAsync(
        string executionSummary,
        CancellationToken ct = default);

    // --- 审计与追踪 ---
    Task LogEventAsync(string sessionId, string eventType, string data, CancellationToken ct = default);

    // --- 彻底清空 Session 记忆 (Stage, Facts, History) ---
    Task ClearSessionMemoryAsync(string sessionId, CancellationToken ct = default);
}
