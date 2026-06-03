using System.Collections.ObjectModel;
using System.Text.Json;

namespace CodexFlow.Core.Models;

/// <summary>
/// Phase 1: Scope values for <see cref="MemoryEntryMetadata.Scope"/>.
/// Distinguishes the lifetime and visibility boundary of a memory fact.
/// </summary>
public static class MemoryFactScope
{
    /// <summary>Fact is bound to a single conversation session.</summary>
    public const string Session = "session";

    /// <summary>Fact belongs to a specific user across sessions.</summary>
    public const string User = "user";

    /// <summary>Fact is scoped to a workspace (repository / project directory).</summary>
    public const string Workspace = "workspace";

    /// <summary>Fact is project-wide and persists beyond individual sessions.</summary>
    public const string Project = "project";
}

/// <summary>
/// Phase 1: Confidence levels for <see cref="MemoryEntryMetadata.Confidence"/>.
/// Priority order for recall injection: high &gt; medium &gt; low.
/// </summary>
public static class MemoryFactConfidence
{
    /// <summary>Fact was produced by a deterministic tool (e.g. file analysis, test runner).</summary>
    public const string High = "high";

    /// <summary>Fact was confirmed by the user or derived from a high-confidence source.</summary>
    public const string Medium = "medium";

    /// <summary>Fact is model-inferred or speculative; deprioritised during recall injection.</summary>
    public const string Low = "low";
}

/// <summary>
/// Phase 1: Typed metadata envelope carried alongside a <see cref="MemoryEntry"/>.
/// Serialised as JSON into <see cref="MemoryEntry.Metadata"/> — no Mongo schema migration required.
/// </summary>
/// <param name="Scope">Lifetime/visibility boundary — see <see cref="MemoryFactScope"/>.</param>
/// <param name="Source">Write origin, e.g. <c>analyze_project</c>, <c>manual_summary</c>, <c>execution_closeout</c>.</param>
/// <param name="Confidence">Trustworthiness level — see <see cref="MemoryFactConfidence"/>.</param>
/// <param name="FreshnessState">Whether the fact is considered <c>fresh</c>, <c>stale</c>, or <c>unknown</c>.</param>
/// <param name="SourceEventId">Optional reference to the audit event that produced this fact.</param>
/// <param name="VectorSyncState">Whether the fact has been projected into Qdrant (<c>synced</c>, <c>pending</c>, <c>skipped</c>).</param>
public sealed record MemoryEntryMetadata(
    string? Scope = null,
    string? Source = null,
    string? Confidence = null,
    string? FreshnessState = null,
    string? SourceEventId = null,
    string? VectorSyncState = null)
{
    /// <summary>Serialise to compact JSON for storage in <see cref="MemoryEntry.Metadata"/>.</summary>
    public string ToJson() => JsonSerializer.Serialize(this);
}

/// <summary>
/// A single memory fact stored in the session or user fact library.
/// <para>
/// Phase 1: The optional <see cref="Metadata"/> field carries a JSON-serialised
/// <see cref="MemoryEntryMetadata"/> envelope. Callers that do not populate it remain
/// fully backward-compatible — the Mongo schema is unchanged.
/// </para>
/// </summary>
public record MemoryEntry(string Key, string Value, string Category, DateTime CreatedAt, string? Metadata = null);

public record ChatTurn(string Role, string Content, DateTime Timestamp);

/// <summary>
/// Defines the runtime durability contract for a recorded session message.
/// </summary>
public enum SessionMessageKind
{
    /// <summary>
    /// User-authored turn. Must be durably appended and kept in the hot context window.
    /// </summary>
    User,

    /// <summary>
    /// Assistant-authored turn. Must be durably appended and kept in the hot context window.
    /// </summary>
    Assistant,

    /// <summary>
    /// Progress/status update. Durably appended for audit/recovery, but excluded from the hot context window.
    /// </summary>
    Progress,

    /// <summary>
    /// System boundary event such as worker completion/failure handoff. Durably appended and retained in the hot window.
    /// </summary>
    SystemBoundary,
}

/// <summary>
/// Normalised input envelope for session-message persistence.
/// </summary>
public sealed record SessionTurnWrite(string Role, string Content, SessionMessageKind Kind)
{
    public static SessionTurnWrite User(string content, string role = "user")
        => new(role, content, SessionMessageKind.User);

    public static SessionTurnWrite Assistant(string content, string role = "assistant")
        => new(role, content, SessionMessageKind.Assistant);

    public static SessionTurnWrite Progress(string content, string role = "progress")
        => new(role, content, SessionMessageKind.Progress);

    public static SessionTurnWrite SystemBoundary(string content, string role = "system_boundary")
        => new(role, content, SessionMessageKind.SystemBoundary);
}

/// <summary>
/// Phase 4: Policy applied when a new value conflicts with an existing fact
/// that already has evidence or a higher-confidence version.
/// </summary>
public enum FactConflictPolicy
{
    /// <summary>Silently overwrite the existing value (legacy default behaviour).</summary>
    SilentOverwrite,

    /// <summary>Keep the version with the higher confidence level.</summary>
    KeepHighestConfidence,

    /// <summary>Always keep the most recently written value regardless of confidence.</summary>
    KeepLatest,

    /// <summary>Reject the incoming write and surface a <see cref="FactConflictRecord"/> for review.</summary>
    EnqueueReview,
}

/// <summary>
/// Phase 4: Represents a detected conflict between an incoming fact write
/// and the currently stored version.
/// </summary>
public sealed record FactConflictRecord(
    string SessionId,
    string Key,
    string ExistingValue,
    string ExistingConfidence,
    string? ExistingSourceEventId,
    string IncomingValue,
    string IncomingConfidence,
    string? IncomingSource,
    FactConflictPolicy AppliedPolicy,
    DateTime DetectedAtUtc);

public partial class CodexSession
{
    // 现有属性已在 CodexModels.cs 中定义

    /// <summary>
    /// Phase 3: ConversationSummary — 面向对话连续性的滚动摘要。
    /// 由 <see cref="IMemoryService.SummarizeHistoryAsync"/> 生成。
    /// 触发条件：recent turns 超阈值、阶段切换。
    /// </summary>
    public string HistorySummary { get; set; } = string.Empty;
    public DateTime? LastCompressedAt { get; set; }

    /// <summary>
    /// Phase 3: ExecutionSummary — 面向工程执行追踪的独立摘要。
    /// 由 <see cref="IMemoryService.SummarizeExecutionAsync"/> 生成，
    /// 在任务批次完成时由 <c>FlushExecutionSummaryAsync</c> 更新。
    /// 与 <see cref="HistorySummary"/> 独立存储、独立注入，
    /// 高价值内容同时提取为结构化 facts。
    /// </summary>
    public string ExecutionSummary { get; set; } = string.Empty;

    /// <summary>
    /// 最近的对话记录
    /// </summary>
    public Collection<ChatTurn> RecentTurns { get; } = new();

    /// <summary>
    /// 项目核心事实（长期记忆快照）
    /// </summary>
    public Collection<MemoryEntry> ActiveFacts { get; } = new();

    /// <summary>
    /// 用户级全局偏好事实
    /// </summary>
    public Collection<MemoryEntry> UserFacts { get; } = new();

    public void ReplaceRecentTurns(IEnumerable<ChatTurn>? recentTurns) => ReplaceSessionCollection(RecentTurns, recentTurns);
    public void ReplaceActiveFacts(IEnumerable<MemoryEntry>? activeFacts) => ReplaceSessionCollection(ActiveFacts, activeFacts);
    public void ReplaceUserFacts(IEnumerable<MemoryEntry>? userFacts) => ReplaceSessionCollection(UserFacts, userFacts);

    private static void ReplaceSessionCollection<T>(Collection<T> target, IEnumerable<T>? source)
    {
        target.Clear();
        if (source == null)
        {
            return;
        }

        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
