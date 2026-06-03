namespace CodexFlow.Core.Models;

public static class MemoryFactCategories
{
    public const string General = "general";
    public const string Project = "project";
    public const string Analysis = "analysis";
    public const string Decision = "decision";
    public const string Execution = "execution";
    public const string Verification = "verification";
    public const string Preference = "preference";
}

public static class ProjectMemoryFactKeys
{
    public const string ProjectFingerprint = "ProjectFingerprint";
    public const string ProjectLanguage = "ProjectLanguage";
    public const string ProjectFileIndex = "ProjectFileIndex";
    public const string ArchitectureAudit = "ArchitectureAudit";
    public const string ProjectSummaryVersion = "ProjectSummaryVersion";
    public const string RepoSnapshot = "RepoSnapshot";
    public const string MemoryFreshness = "MemoryFreshness";
    public const string LastExecutionOutcome = "LastExecutionOutcome";
    public const string VerificationBaseline = "VerificationBaseline";
    public const string SemanticRecallTrace = "SemanticRecallTrace";
    public const string SemanticRecallContext = "SemanticRecallContext";
    // Phase 3: execution summary stored as a fact for cross-session recall
    public const string ExecutionSummary = "ExecutionSummary";
}

/// <summary>
/// Phase 1: Explicit set of fact keys that must never be projected into the Qdrant vector store.
/// <para>
/// Replacing scattered if/else guards with a single named policy — any new exclusion must be
/// added here rather than buried inside service methods.
/// </para>
/// <list type="bullet">
///   <item><term><see cref="ProjectMemoryFactKeys.ProjectFileIndex"/></term><description>High-volume index — too large and too noisy for semantic retrieval.</description></item>
///   <item><term><see cref="ProjectMemoryFactKeys.SemanticRecallTrace"/></term><description>Runtime-derived recall audit record — not a source-of-truth fact.</description></item>
///   <item><term><see cref="ProjectMemoryFactKeys.SemanticRecallContext"/></term><description>Injected prompt context — derived from Qdrant, must not be fed back.</description></item>
///   <item><term><see cref="ProjectMemoryFactKeys.RepoSnapshot"/></term><description>Raw git metadata JSON — low semantic density, already captured via ProjectSummary.</description></item>
/// </list>
/// </summary>
public static class NonVectorizableFactKeys
{
    private static readonly HashSet<string> _keys = new(StringComparer.Ordinal)
    {
        ProjectMemoryFactKeys.ProjectFileIndex,
        ProjectMemoryFactKeys.SemanticRecallTrace,
        ProjectMemoryFactKeys.SemanticRecallContext,
        ProjectMemoryFactKeys.RepoSnapshot,
    };

    /// <summary>Returns <see langword="true"/> if the given key should be excluded from Qdrant projection.</summary>
    public static bool Contains(string key) => _keys.Contains(key);
}

/// <summary>
/// Phase 4: Fact keys that carry high-risk, long-lived project knowledge and must
/// not be silently overwritten. Writes to these keys are subject to conflict detection
/// and the <see cref="FactConflictPolicy"/> configured on <c>IFactGuardService</c>.
/// <para>
/// Keys listed here represent the "source-of-truth" constraints defined in the blueprint:
/// architecture decisions, security gates, and verified test baselines.
/// </para>
/// </summary>
public static class ProtectedFactKeys
{
    private static readonly HashSet<string> _keys = new(StringComparer.Ordinal)
    {
        ProjectMemoryFactKeys.ArchitectureAudit,
        ProjectMemoryFactKeys.VerificationBaseline,
        ProjectMemoryFactKeys.ProjectFingerprint,
        ProjectMemoryFactKeys.ProjectSummaryVersion,
    };

    /// <summary>Returns <see langword="true"/> if the key is protected from silent overwrites.</summary>
    public static bool Contains(string key) => _keys.Contains(key);

    /// <summary>All protected keys, for enumeration or logging.</summary>
    public static IReadOnlyCollection<string> All => _keys;
}

// ─── Phase 5: Memory Drift Governance ────────────────────────────────────────

/// <summary>
/// Phase 5: The three independent dimensions of memory drift that the system tracks.
/// </summary>
public enum MemoryDriftType
{
    /// <summary>
    /// The repository file structure has changed since the last <c>analyze_project</c> run.
    /// Indicates that code-level facts (fingerprint, file index, architecture) may be stale.
    /// </summary>
    CodeStructure,

    /// <summary>
    /// <c>PROJECT_SUMMARY.md</c> content is inconsistent with the stored structured facts
    /// (e.g. version mismatch, key section absent).
    /// </summary>
    ProjectSummary,

    /// <summary>
    /// The Qdrant vector collection is out of date relative to the current facts and summaries
    /// (e.g. facts were updated but Qdrant sync has not been re-run).
    /// </summary>
    VectorProjection,
}

/// <summary>
/// Phase 5: Result of a drift check for a single <see cref="MemoryDriftType"/>.
/// </summary>
public sealed record MemoryDriftCheckResult(
    MemoryDriftType DriftType,
    bool IsDrifted,
    string Reason,
    DateTime CheckedAtUtc);

/// <summary>
/// Phase 5: Composite drift report combining results across all three drift dimensions.
/// </summary>
public sealed record MemoryDriftReport(
    string SessionId,
    bool HasAnyDrift,
    IReadOnlyList<MemoryDriftCheckResult> Results,
    DateTime GeneratedAtUtc)
{
    /// <summary>Returns the result for a specific drift type, or <see langword="null"/> if not checked.</summary>
    public MemoryDriftCheckResult? For(MemoryDriftType type)
        => Results.FirstOrDefault(r => r.DriftType == type);

    /// <summary>True when the code-structure dimension is drifted.</summary>
    public bool IsCodeStructureDrifted => For(MemoryDriftType.CodeStructure)?.IsDrifted ?? false;

    /// <summary>True when the project-summary dimension is drifted.</summary>
    public bool IsProjectSummaryDrifted => For(MemoryDriftType.ProjectSummary)?.IsDrifted ?? false;

    /// <summary>True when the vector-projection dimension is drifted.</summary>
    public bool IsVectorProjectionDrifted => For(MemoryDriftType.VectorProjection)?.IsDrifted ?? false;
}

/// <summary>
/// Phase 5: Named events that can trigger an automatic memory refresh action.
/// Used by the auto-refresh trigger matrix inside <c>IDriftGovernor</c>.
/// </summary>
public enum AutoRefreshTrigger
{
    /// <summary><c>analyze_project</c> completed — rebuild related vectors.</summary>
    AnalyzeProjectCompleted,

    /// <summary>A plan execution window closed — update execution memory.</summary>
    PlanExecutionClosed,

    /// <summary>A manual project summary was saved — sync corresponding facts.</summary>
    ManualSummarySaved,

    /// <summary>A background job completed — write result facts and notify main session.</summary>
    BackgroundJobCompleted,
}

public sealed record RepoSnapshotMemory(
    string ProjectRoot,
    string? Branch,
    string? HeadCommit,
    bool? IsDirty,
    DateTime CheckedAtUtc);

public sealed record MemoryFreshnessState(
    bool IsStale,
    string Reason,
    DateTime CheckedAtUtc);

/// <summary>
/// Phase 2: Well-known values for <see cref="SemanticRecallTraceItem.RejectedReason"/>.
/// Populated when a recall candidate is scored but not injected into the prompt context.
/// </summary>
public static class RecallRejectedReasons
{
    /// <summary>Candidate score was below the configured injection threshold.</summary>
    public const string ScoreBelowThreshold = "score_below_threshold";

    /// <summary>Injection slot quota was already filled by higher-scoring candidates.</summary>
    public const string QuotaExhausted = "quota_exhausted";

    /// <summary>Candidate was marked stale and freshness-downgrade policy excluded it.</summary>
    public const string StaleFact = "stale_fact";

    /// <summary>Candidate confidence level was too low for the current recall scenario.</summary>
    public const string LowConfidence = "low_confidence";

    /// <summary>Candidate content is a known non-vectorizable key that should not be re-injected.</summary>
    public const string NonVectorizableKey = "non_vectorizable_key";

    /// <summary>Candidate is a derived/projected document (e.g. SemanticRecallContext itself).</summary>
    public const string DerivedProjection = "derived_projection";
}

public sealed record SemanticRecallTraceItem(
    string Id,
    string SourceType,
    double Score,
    string Freshness,
    string ContentPreview,
    DateTime? UpdatedAtUtc,
    string? Category,
    string? Metadata,
    // Phase 2: rejection reason — use constants from RecallRejectedReasons
    string? RejectedReason = null);

public sealed record SemanticRecallTrace(
    string Query,
    string Source,
    bool Enabled,
    bool Succeeded,
    bool TimedOut,
    string? Error,
    int CandidateCount,
    int InjectedCount,
    DateTime GeneratedAtUtc,
    IReadOnlyList<SemanticRecallTraceItem> Items);

public sealed class ProjectMemoryDocument
{
    public int Version { get; set; } = 2;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string ProjectOverview { get; set; } = string.Empty;
    public string TechStackAndConstraints { get; set; } = string.Empty;
    public string ConfirmedDecisions { get; set; } = string.Empty;
    public string KnownRisksAndDebt { get; set; } = string.Empty;
    public string VerificationBaseline { get; set; } = string.Empty;
    public string LatestExecutionResult { get; set; } = string.Empty;
    public string LegacyAnalysisSnapshot { get; set; } = string.Empty;
}

public sealed record ProjectAnalysisMemoryInput(
    string WorkspacePath,
    string? ProjectRoot,
    string? SessionId,
    Uri? ProjectUrl,
    IReadOnlyDictionary<string, string>? Metadata,
    string DetectedLanguage,
    string ProjectFingerprint,
    IReadOnlyList<string> RiskItems,
    string? ReadmeSummary,
    string RawAnalysisReport);

public sealed record ProjectManualSummaryInput(
    string WorkspacePath,
    string? ProjectRoot,
    string? SessionId,
    Uri? ProjectUrl,
    IReadOnlyDictionary<string, string>? Metadata,
    string Summary);

public sealed record ProjectExecutionMemoryInput(
    string WorkspacePath,
    string? ProjectRoot,
    string? SessionId,
    Uri? ProjectUrl,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyList<CodexTask> Plan,
    string? VerificationSummary);

public sealed record ProjectMemoryWriteResult(
    string FilePath,
    string Content,
    ProjectMemoryDocument Document);
