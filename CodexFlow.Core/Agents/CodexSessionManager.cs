using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.ObjectModel;
using System.Globalization;

namespace CodexFlow.Core.Agents;

public class CodexSessionManager
{
    private const string ContextEstimatedCharsMetadataKey = "ContextWindowEstimatedChars";
    private const string ContextEstimatedTokensMetadataKey = "ContextWindowEstimatedTokens";
    private const string ContextRecentTurnsMetadataKey = "ContextWindowRecentTurns";
    private const string ContextSummaryCharsMetadataKey = "ContextWindowSummaryChars";
    private const string ContextUpdatedAtUtcMetadataKey = "ContextWindowUpdatedAtUtc";
    private const string SessionPromptTokensMetadataKey = "SessionTotalPromptTokens";
    private const string SessionCompletionTokensMetadataKey = "SessionTotalCompletionTokens";
    private const string PendingExecutionTasksMetadataKey = "PendingExecutionTasks";
    private const string RecentExecutionUpdatesMetadataKey = "RecentExecutionUpdates";
    private const int MaxRecentExecutionUpdates = 6;
    private const int MaxExecutionUpdateChars = 240;
    private const int MaxRuntimeCheckpointFiles = 16;
    private const int MaxRuntimeCheckpointToolResults = 16;
    private const int MaxRuntimeCheckpointPendingModifications = 8;
    private const int MaxRuntimeCheckpointFailures = 8;
    private static readonly char[] LineBreakSeparators = ['\r', '\n'];

    private readonly ISessionStore _sessionStore;
    private readonly IMemoryService _memoryService;
    public IMemoryService Memory => _memoryService;
    private readonly ILogger<CodexSessionManager> _logger;
    private readonly ISessionCompressionStore? _compressionStore;
    private readonly ContextCompressionTriggerOptions _compressionTriggerOptions;

    public CodexSessionManager(
        ISessionStore sessionStore,
        IMemoryService memoryService,
        ILogger<CodexSessionManager> logger)
        : this(sessionStore, memoryService, logger, null, null)
    {
    }

    public CodexSessionManager(
        ISessionStore sessionStore,
        IMemoryService memoryService,
        ILogger<CodexSessionManager> logger,
        ISessionCompressionStore? compressionStore = null,
        ContextCompressionTriggerOptions? compressionTriggerOptions = null)
    {
        _sessionStore = sessionStore;
        _memoryService = memoryService;
        _logger = logger;
        _compressionStore = compressionStore;
        _compressionTriggerOptions = compressionTriggerOptions ?? new ContextCompressionTriggerOptions();
    }

    public Task<CodexSession> GetOrCreateSessionAsync(string sessionId, string userId = "", string workspacePath = "", string projectUrl = "")
    {
        return GetOrCreateSessionAsync(sessionId, userId, workspacePath, CodexSession.CreateProjectUri(projectUrl));
    }

    public async Task<CodexSession> GetOrCreateSessionAsync(string sessionId, string userId, string workspacePath, Uri? projectUrl)
    {
        var session = await _sessionStore.GetSessionAsync(sessionId).ConfigureAwait(false);
        if (session == null)
        {
            session = new CodexSession
            {
                Id = sessionId,
                UserId = userId,
                WorkspacePath = workspacePath,
                ProjectUrl = projectUrl,
                CurrentStage = 1
            };

            // Redis miss: try to hydrate compression state from PG projection
            if (_compressionStore != null)
            {
                var compression = await _compressionStore.LoadAsync(sessionId).ConfigureAwait(false);
                if (compression != null)
                {
                    session.HistorySummary = compression.HistorySummary;
                    session.LastCompressedAt = compression.LastCompressedAtUtc;
                }
            }

            await _sessionStore.SaveSessionAsync(session).ConfigureAwait(false);
        }

        return await EnrichAndOptionallyPersistSessionAsync(session, userId, workspacePath, projectUrl).ConfigureAwait(false);
    }

    public async Task<CodexSession?> GetExistingSessionAsync(string sessionId, string userId = "", string workspacePath = "", Uri? projectUrl = null)
    {
        var session = await _sessionStore.GetSessionAsync(sessionId).ConfigureAwait(false);
        if (session == null)
        {
            return null;
        }

        return await EnrichAndOptionallyPersistSessionAsync(session, userId, workspacePath, projectUrl).ConfigureAwait(false);
    }

    private async Task<CodexSession> EnrichAndOptionallyPersistSessionAsync(CodexSession session, string userId, string workspacePath, Uri? projectUrl)
    {
        var sessionUpdated = false;
        if (!string.IsNullOrWhiteSpace(userId) && !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            session.UserId = userId;
            sessionUpdated = true;
        }

        if (!string.IsNullOrWhiteSpace(workspacePath) && !string.Equals(session.WorkspacePath, workspacePath, StringComparison.Ordinal))
        {
            session.WorkspacePath = workspacePath;
            sessionUpdated = true;
        }

        // If projectUrl provided but session has empty one, update it
        if (projectUrl != null && session.ProjectUrl == null)
        {
            session.ProjectUrl = projectUrl;
            sessionUpdated = true;
        }

        if (sessionUpdated)
        {
            await _sessionStore.SaveSessionAsync(session).ConfigureAwait(false);
        }

        // 加载 Session 级事实
        session.ReplaceActiveFacts(await _memoryService.RecallFactsAsync(session.Id).ConfigureAwait(false));

        // 加载 User 级全局事实 (偏好)
        if (!string.IsNullOrEmpty(session.UserId))
        {
            session.ReplaceUserFacts(await _memoryService.RecallUserFactsAsync(session.UserId).ConfigureAwait(false));
        }
        else if (session.UserFacts.Count > 0)
        {
            session.ReplaceUserFacts(null);
        }

        return session;
    }

    public async Task UpdateSessionAsync(CodexSession session)
    {
        await _sessionStore.SaveSessionAsync(session).ConfigureAwait(false);
    }

    /// <summary>
    /// 记录新的对话并自动处理摘要压缩（短期记忆管理）
    /// </summary>
    public async Task RecordMessageAsync(string sessionId, string role, string content)
    {
        await RecordTurnsAsync(sessionId, [CreateDefaultTurnWrite(role, content)]).ConfigureAwait(false);
    }

    /// <summary>
    /// Records one or more conversation turns into short-term memory and triggers
    /// auto-compression when the estimated conversation-context size reaches the
    /// configured model-budget threshold.
    /// </summary>
    public async Task RecordMessagesAsync(string sessionId, params (string Role, string Content)[] turns)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var normalizedTurns = turns
            .Select(turn => CreateDefaultTurnWrite(turn.Role, turn.Content))
            .ToArray();
        await RecordTurnsAsync(sessionId, normalizedTurns).ConfigureAwait(false);
    }

    /// <summary>
    /// Records one or more typed session turns according to the durability policy:
    /// user / assistant / system boundary enter the hot context window, while
    /// progress updates are audit-only and do not pollute the active prompt buffer.
    /// </summary>
    public async Task RecordTurnsAsync(string sessionId, params SessionTurnWrite[] turns)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var session = await GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
        foreach (var turn in turns)
        {
            var turnRole = turn.Role;
            var turnContent = turn.Content;
            if (string.IsNullOrWhiteSpace(turnContent))
            {
                continue;
            }

            var timestamp = DateTime.UtcNow;
            var rawTurn = new ChatTurn(turnRole, turnContent, timestamp);
            await _memoryService.AppendTurnAsync(sessionId, rawTurn).ConfigureAwait(false);

            if (ShouldEnterContextWindow(turn.Kind))
            {
                session.RecentTurns.Add(CompactTurnForContextWindow(rawTurn));
            }
        }

        if (session.RecentTurns.Count == 0)
        {
            return;
        }

        await ApplyContextGovernanceCoreAsync(sessionId, session).ConfigureAwait(false);
        RefreshContextWindowSnapshot(session);
        await UpdateSessionAsync(session).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies context-window governance to an existing session after external callers
    /// have appended new turns. The governance order is fixed:
    /// single-turn compaction → light trim of oldest turns → full compression.
    /// </summary>
    public async Task ApplyContextGovernanceAsync(
        string sessionId,
        long? warnLimitChars = null,
        long? hardLimitChars = null,
        CancellationToken ct = default)
    {
        var session = await GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
        if (session.RecentTurns.Count == 0)
        {
            return;
        }

        var changed = await ApplyContextGovernanceCoreAsync(sessionId, session, warnLimitChars, hardLimitChars).ConfigureAwait(false);
        changed |= RefreshContextWindowSnapshot(session);
        if (changed)
        {
            await UpdateSessionAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records per-session runtime token totals and refreshes the derived context snapshot metadata.
    /// This is called after a query turn completes so session diagnostics stay aligned with
    /// the hot context window that drives auto-compression.
    /// </summary>
    public async Task RecordRuntimeUsageAsync(
        string sessionId,
        int promptTokens,
        int completionTokens,
        CancellationToken ct = default)
    {
        var session = await GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);

        var changed = AccumulateLongMetadata(session.Metadata, SessionPromptTokensMetadataKey, promptTokens);
        changed |= AccumulateLongMetadata(session.Metadata, SessionCompletionTokensMetadataKey, completionTokens);
        changed |= RefreshContextWindowSnapshot(session);

        if (changed)
        {
            await UpdateSessionAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Persists the latest runtime working set as compact session metadata.
    /// This is intentionally separate from chat turns so tool evidence can survive
    /// compaction without polluting the user-visible transcript.
    /// </summary>
    public async Task RecordRuntimeCheckpointAsync(
        string sessionId,
        QueryRuntimeCheckpoint checkpoint,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(checkpoint);

        var session = await GetOrCreateSessionAsync(sessionId).ConfigureAwait(false);
        var serialized = JsonConvert.SerializeObject(ToCheckpointProjection(checkpoint));
        if (SetMetadataValue(session.Metadata, QueryRuntimeCheckpoint.MetadataKey, serialized))
        {
            await UpdateSessionAsync(session).ConfigureAwait(false);
        }
    }

    public async Task LearnFactAsync(string sessionId, string key, string fact, string category = MemoryFactCategories.General, string? metadata = null)
    {
        await _memoryService.LearnFactAsync(sessionId, key, fact, category, metadata).ConfigureAwait(false);
    }

    public async Task LearnUserFactAsync(string userId, string key, string fact, string category = MemoryFactCategories.Preference, string? metadata = null)
    {
        await _memoryService.LearnUserFactAsync(userId, key, fact, category, metadata).ConfigureAwait(false);
    }

    /// <summary>
    /// Phase 3: Records that a task has completed and optionally triggers an execution summary
    /// when the completed-task buffer reaches the configured threshold.
    /// Call this from <c>CodexOrchestrator</c> / <c>DefaultCodexKernel</c> after each successful task.
    /// </summary>
    /// <param name="sessionId">Owning session.</param>
    /// <param name="taskTitle">Human-readable title of the completed task.</param>
    /// <param name="verificationSummary">Optional test/validation output for this task.</param>
    public async Task RecordTaskCompletedAsync(string sessionId, string taskTitle, string? verificationSummary = null)
    {
        var session = await GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);

        // Buffer completed task titles in session metadata for batch summarisation
        var existing = session.Metadata.TryGetValue(PendingExecutionTasksMetadataKey, out var raw) ? raw : string.Empty;
        var updated = string.IsNullOrWhiteSpace(existing)
            ? taskTitle
            : existing + "\n" + taskTitle;
        session.Metadata[PendingExecutionTasksMetadataKey] = updated;
        await UpdateSessionAsync(session).ConfigureAwait(false);

        // Trigger execution summary when 5 or more tasks are buffered
        var bufferedTasks = updated.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (bufferedTasks.Length >= 5)
        {
            await FlushExecutionSummaryAsync(sessionId, session, bufferedTasks, verificationSummary).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Phase 3: Triggers an execution summary immediately — e.g. on stage transition or session close.
    /// Clears the pending task buffer after summarisation.
    /// </summary>
    public async Task FlushExecutionSummaryAsync(
        string sessionId,
        CodexFlow.Core.Models.CodexSession? session = null,
        string[]? bufferedTasks = null,
        string? verificationSummary = null)
    {
        session ??= await GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);

        if (bufferedTasks == null)
        {
            var raw = session.Metadata.TryGetValue(PendingExecutionTasksMetadataKey, out var r) ? r : string.Empty;
            bufferedTasks = string.IsNullOrWhiteSpace(raw)
                ? []
                : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        if (bufferedTasks.Length == 0 && string.IsNullOrWhiteSpace(verificationSummary))
        {
            return;
        }

        StructuredLog.Information(_logger, "Generating execution summary for session {SessionId}. Tasks={Count}", sessionId, bufferedTasks.Length);

        var executionSummary = await _memoryService.SummarizeExecutionAsync(
            sessionId,
            bufferedTasks,
            verificationSummary).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(executionSummary))
        {
            var execMeta = new MemoryEntryMetadata(
                Scope: MemoryFactScope.Session,
                Source: "execution_summary_flush",
                Confidence: MemoryFactConfidence.High,
                FreshnessState: "fresh").ToJson();

            // Phase 3: write execution summary to both the session field and as a persistent fact
            session.ExecutionSummary = executionSummary;

            await LearnFactAsync(
                sessionId,
                ProjectMemoryFactKeys.LastExecutionOutcome,
                executionSummary,
                MemoryFactCategories.Execution,
                execMeta).ConfigureAwait(false);

            // Phase 3: "摘要转结构化 facts" — extract typed decision/verification items from the summary
            await ExtractAndPersistStructuredFactsAsync(sessionId, executionSummary, execMeta).ConfigureAwait(false);
        }

        // Clear the pending buffer
        session.Metadata.Remove(PendingExecutionTasksMetadataKey);
        await UpdateSessionAsync(session).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a compact execution-status update outside the hot conversation turn buffer.
    /// These updates are injected into runtime context as a transient summary section instead of
    /// being replayed as <c>system_boundary</c> chat history.
    /// </summary>
    public async Task RecordExecutionUpdateAsync(string sessionId, string updateText)
    {
        var normalized = NormalizeExecutionUpdateText(updateText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var session = await GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
        var updates = GetRecentExecutionUpdates(session);
        updates.RemoveAll(existing => string.Equals(existing, normalized, StringComparison.Ordinal));
        updates.Insert(0, normalized);

        if (updates.Count > MaxRecentExecutionUpdates)
        {
            updates.RemoveRange(MaxRecentExecutionUpdates, updates.Count - MaxRecentExecutionUpdates);
        }

        var serialized = JsonConvert.SerializeObject(updates);
        if (session.Metadata.TryGetValue(RecentExecutionUpdatesMetadataKey, out var existingSerialized) &&
            string.Equals(existingSerialized, serialized, StringComparison.Ordinal))
        {
            return;
        }

        session.Metadata[RecentExecutionUpdatesMetadataKey] = serialized;
        await UpdateSessionAsync(session).ConfigureAwait(false);
    }

    /// <summary>
    /// Phase 3: Extracts structured facts (decisions, verification conclusions) from a free-text
    /// execution summary and persists them as independent, queryable <see cref="MemoryEntry"/> items.
    /// </summary>
    private async Task ExtractAndPersistStructuredFactsAsync(string sessionId, string executionSummary, string execMeta)
    {
        IReadOnlyList<(string Key, string Value, string Category)> extracted;
        try
        {
            extracted = await _memoryService.ExtractStructuredFactsAsync(executionSummary).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Warning(_logger, "ExtractStructuredFactsAsync threw unexpectedly for session {SessionId}: {Msg}", sessionId, ex.Message);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        foreach (var (key, value, category) in extracted)
        {
            await LearnFactAsync(sessionId, key, value, category, execMeta).ConfigureAwait(false);
            StructuredLog.Information(_logger, "Persisted structured fact from execution summary. Session={SessionId}, Key={Key}, Category={Category}", sessionId, key, category);
        }
    }

    /// <summary>
    /// Phase 3: Called on stage transitions (e.g. Stage 3 → Stage 4).
    /// Flushes any pending execution summary <b>and</b> compresses the conversation history
    /// (ConversationSummary trigger #2 — stage change), so the new stage starts with a clean context.
    /// </summary>
    public async Task OnStageChangedAsync(string sessionId, int newStage, string? verificationSummary = null)
    {
        StructuredLog.Information(_logger, "Stage changed to {Stage} for session {SessionId}. Flushing summaries.", newStage, sessionId);

        // Flush execution summary for the stage we're leaving
        await FlushExecutionSummaryAsync(sessionId, verificationSummary: verificationSummary).ConfigureAwait(false);

        // Compress conversation history (trigger #2: stage change)
        var session = await GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
        if (session.RecentTurns.Count > 0)
        {
            StructuredLog.Information(_logger, "Compressing conversation history on stage change. SessionId={SessionId}, TurnCount={Count}", sessionId, session.RecentTurns.Count);
            var newConversationSummary = await _memoryService.SummarizeHistoryAsync(sessionId, session.HistorySummary, session.RecentTurns).ConfigureAwait(false);
            session.HistorySummary = newConversationSummary;
            session.LastCompressedAt = DateTime.UtcNow;
            session.RecentTurns.Clear();
            RefreshContextWindowSnapshot(session);
            await PersistCompressionProjectionAsync(session).ConfigureAwait(false);
            await UpdateSessionAsync(session).ConfigureAwait(false);
        }
    }

    public async Task ClearSessionAsync(string sessionId)
    {
        // 1. 清空 Redis 缓存
        await _sessionStore.DeleteSessionAsync(sessionId).ConfigureAwait(false);

        // 2. 清空 MongoDB 记忆与事实
        await _memoryService.ClearSessionMemoryAsync(sessionId).ConfigureAwait(false);

        // 3. 清空 PG 压缩投影（best-effort）
        if (_compressionStore != null)
        {
            try { await _compressionStore.ClearAsync(sessionId).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to clear compression projection. SessionId={SessionId}", sessionId); }
        }

        StructuredLog.Information(_logger, "Orchestration session {SessionId} fully cleared in Memory/Store.", sessionId);
    }

    public async Task<string> GetFullContextAsync(string sessionId)
    {
        var session = await GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
        var sb = new System.Text.StringBuilder();

        // 1. 注入用户级全局偏好 (User Facts)
        if (session.UserFacts is { Count: > 0 })
        {
            sb.AppendLine("# 用户全局偏好与习惯");
            foreach (var fact in session.UserFacts)
            {
                sb.AppendLine("- " + fact.Key + ": " + fact.Value);
            }
            sb.AppendLine();
        }

        // 2. 项目摘要
        if (session.Metadata.TryGetValue("MemoryFreshnessState", out var freshnessState) &&
            string.Equals(freshnessState, "stale", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("# 记忆新鲜度警告");
            if (session.Metadata.TryGetValue("MemoryFreshnessReason", out var staleReason) && !string.IsNullOrWhiteSpace(staleReason))
            {
                sb.AppendLine("- 当前项目记忆已过期: " + staleReason);
            }
            else
            {
                sb.AppendLine("- 当前项目记忆已过期，需要先重新分析项目状态。");
            }
            sb.AppendLine("- 当前轮应优先重新扫描/重新规划，而不是复用旧计划。");
            sb.AppendLine();
        }

        sb.AppendLine("# 项目摘要\n" + session.ProjectSummary + "\n");

        // 3. ConversationSummary — 对话连续性摘要
        if (!string.IsNullOrEmpty(session.HistorySummary))
        {
            sb.AppendLine("# 历史进展总结\n" + session.HistorySummary + "\n");
        }

        var recentExecutionUpdates = GetRecentExecutionUpdates(session);
        if (recentExecutionUpdates.Count > 0)
        {
            sb.AppendLine("# 最近后台执行更新");
            foreach (var update in recentExecutionUpdates)
            {
                sb.AppendLine("- " + update);
            }

            sb.AppendLine();
        }

        // Phase 3: ExecutionSummary — 独立工程执行摘要节（区别于对话摘要）
        if (!string.IsNullOrWhiteSpace(session.ExecutionSummary))
        {
            sb.AppendLine("# 执行进展摘要\n" + session.ExecutionSummary + "\n");
        }

        var semanticRecallContext = TryGetSemanticRecallContext(session.ActiveFacts);
        if (!string.IsNullOrWhiteSpace(semanticRecallContext))
        {
            sb.AppendLine("# 语义召回上下文");
            sb.AppendLine(semanticRecallContext);
            sb.AppendLine();
        }

        // 4. Session 级事实与决策 (去重：跳过与 ProjectSummary 内容高度重叠的大型事实)
        var summaryText = session.ProjectSummary ?? string.Empty;
        var filteredFacts = session.ActiveFacts?
            .Where(f =>
            {
                if (string.Equals(f.Key, ProjectMemoryFactKeys.SemanticRecallContext, StringComparison.Ordinal) ||
                    string.Equals(f.Key, ProjectMemoryFactKeys.SemanticRecallTrace, StringComparison.Ordinal))
                {
                    return false;
                }

                // 当 ProjectSummary 存在且非空时, 检查事实内容是否已被摘要覆盖
                if (!string.IsNullOrEmpty(summaryText) && !string.IsNullOrEmpty(f.Value))
                {
                    // 完全相同 → 去重
                    if (f.Value.Equals(summaryText, StringComparison.Ordinal))
                        return false;
                    // 事实内容是摘要子串(>80%长度) → 高度重叠, 去重
                    if (f.Value.Length > 200 && summaryText.Contains(f.Value, StringComparison.Ordinal))
                        return false;
                }
                return true;
            })
            .ToList();
        if (filteredFacts is { Count: > 0 })
        {
            sb.AppendLine("# 已知项目事实与决策");
            foreach (var fact in filteredFacts)
            {
                // 对于 ProjectFileIndex 这样的大型 JSON，截断以减少上下文膨胀
                var value = fact.Value;
                if (fact.Key == "ProjectFileIndex" && value != null && value.Length > 2000)
                {
                    value = value[..2000] + "\n... (已截断，使用 search_file_index 工具查询完整文件索引)";
                }
                sb.AppendLine("- " + fact.Key + ": " + value);
            }
            sb.AppendLine();
        }

        // 5. 当前执行计划 (Current Plan) [Crucial to prevent regeneration loop]
        if (session.Plan is { Count: > 0 } && !CodexPlanStateGuards.IsPlanFullyCompleted(session.Plan))
        {
            sb.AppendLine("# 当前执行计划 (Current Plan)");
            sb.AppendLine("注意：你已经有一个正在执行的计划，请不要重新生成计划，而是继续执行下一个 Pending 任务。");
            foreach (var task in session.Plan)
            {
                var check = task.Status == CodexTaskStatus.Success ? "x" : " ";
                var statusNote = task.Status == CodexTaskStatus.Executing ? " (Running)" :
                                 task.Status == CodexTaskStatus.Failed ? " (Failed)" : "";

                sb.AppendLine("- [" + check + "] #" + task.Id + " " + task.Title + statusNote);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private bool ShouldAutoCompressConversation(CodexSession session, out int estimatedTokens, out int thresholdTokens)
    {
        estimatedTokens = EstimateConversationContextTokens(session);
        thresholdTokens = _compressionTriggerOptions.ResolveTriggerThresholdTokens();

        if (session.RecentTurns.Count < Math.Max(1, _compressionTriggerOptions.MinRecentTurnsBeforeCompression))
        {
            return false;
        }

        return estimatedTokens >= thresholdTokens;
    }

    private async Task<bool> ApplyContextGovernanceCoreAsync(
        string sessionId,
        CodexSession session,
        long? warnLimitChars = null,
        long? hardLimitChars = null)
    {
        var changed = false;

        var softLimit = _compressionTriggerOptions.ResolveRecentTurnsSoftLimit();
        while (session.RecentTurns.Count > softLimit)
        {
            var overflowCount = session.RecentTurns.Count - softLimit;
            var overflowTurns = session.RecentTurns.Take(overflowCount).ToList();
            if (overflowTurns.Count == 0)
            {
                break;
            }

            session.HistorySummary = await _memoryService
                .SummarizeHistoryAsync(sessionId, session.HistorySummary, overflowTurns)
                .ConfigureAwait(false);
            session.LastCompressedAt = DateTime.UtcNow;
            RemoveOldestTurns(session.RecentTurns, overflowCount);
            changed = true;

            StructuredLog.Information(
                _logger,
                "Context governance light-trim applied. SessionId={SessionId}, OverflowTurns={OverflowTurns}, SoftLimit={SoftLimit}",
                sessionId,
                overflowCount,
                softLimit);
        }

        var estimatedChars = EstimateConversationContextChars(session);
        var forceFullCompression = ShouldForceFullCompression(session, warnLimitChars, hardLimitChars, estimatedChars, out var governanceReason);
        var autoCompressionTriggered = ShouldAutoCompressConversation(session, out var estimatedTokens, out var thresholdTokens);

        if (forceFullCompression || autoCompressionTriggered)
        {
            StructuredLog.Information(
                _logger,
                "Context governance full compression applied. SessionId={SessionId}, Reason={Reason}, EstimatedChars={EstimatedChars}, EstimatedTokens={EstimatedTokens}, ThresholdTokens={ThresholdTokens}, RecentTurns={RecentTurns}",
                sessionId,
                governanceReason ?? "auto_threshold",
                estimatedChars,
                estimatedTokens,
                thresholdTokens,
                session.RecentTurns.Count);

            session.HistorySummary = await _memoryService
                .SummarizeHistoryAsync(sessionId, session.HistorySummary, session.RecentTurns.ToList())
                .ConfigureAwait(false);
            session.LastCompressedAt = DateTime.UtcNow;
            session.RecentTurns.Clear();
            changed = true;
        }

        if (changed)
        {
            await PersistCompressionProjectionAsync(session).ConfigureAwait(false);
        }

        return changed;
    }

    private bool ShouldForceFullCompression(
        CodexSession session,
        long? warnLimitChars,
        long? hardLimitChars,
        int estimatedChars,
        out string? reason)
    {
        reason = null;
        if (session.RecentTurns.Count == 0)
        {
            return false;
        }

        if (hardLimitChars.HasValue && estimatedChars >= hardLimitChars.Value)
        {
            reason = "runtime_hard_limit";
            return true;
        }

        if (warnLimitChars.HasValue &&
            estimatedChars >= warnLimitChars.Value &&
            session.RecentTurns.Count >= Math.Max(1, _compressionTriggerOptions.MinRecentTurnsBeforeCompression))
        {
            reason = "runtime_warn_limit";
            return true;
        }

        return false;
    }

    private int EstimateConversationContextTokens(CodexSession session)
    {
        var totalChars = EstimateConversationContextChars(session);
        var charsPerToken = _compressionTriggerOptions.ResolveEstimatedCharsPerToken();
        return (int)Math.Ceiling(totalChars / charsPerToken);
    }

    private int EstimateConversationContextChars(CodexSession session)
    {
        long totalChars = 0;

        if (!string.IsNullOrWhiteSpace(session.HistorySummary))
        {
            totalChars += session.HistorySummary.Length + 32;
        }

        foreach (var turn in session.RecentTurns)
        {
            totalChars += turn.Role.Length + turn.Content.Length + 8;
        }

        return totalChars >= int.MaxValue ? int.MaxValue : (int)totalChars;
    }

    private ChatTurn CompactTurnForContextWindow(ChatTurn turn)
    {
        var compactedContent = CompactTurnContentForContextWindow(turn.Content);
        return compactedContent == turn.Content
            ? turn
            : turn with { Content = compactedContent };
    }

    private string CompactTurnContentForContextWindow(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        var softLimit = _compressionTriggerOptions.ResolveSingleTurnSoftLimitChars();
        if (content.Length <= softLimit)
        {
            return content;
        }

        var headChars = _compressionTriggerOptions.ResolveSingleTurnPreserveHeadChars();
        var tailChars = _compressionTriggerOptions.ResolveSingleTurnPreserveTailChars();
        var preserveChars = Math.Min(content.Length, headChars + tailChars);
        if (preserveChars >= content.Length)
        {
            return content;
        }

        var actualHead = Math.Min(headChars, content.Length);
        var actualTail = Math.Min(tailChars, content.Length - actualHead);
        if (actualTail <= 0)
        {
            return content[..softLimit];
        }

        var omittedChars = content.Length - actualHead - actualTail;
        return string.Concat(
            content[..actualHead],
            "\n\n[... ",
            omittedChars.ToString(CultureInfo.InvariantCulture),
            " chars omitted for context window ...]\n\n",
            content[(content.Length - actualTail)..]);
    }

    private static void RemoveOldestTurns(Collection<ChatTurn> turns, int count)
    {
        for (var i = 0; i < count && turns.Count > 0; i++)
        {
            turns.Remove(turns.First());
        }
    }

    private static SessionTurnWrite CreateDefaultTurnWrite(string role, string content)
    {
        return InferMessageKind(role) switch
        {
            SessionMessageKind.User => SessionTurnWrite.User(content, role),
            SessionMessageKind.Progress => SessionTurnWrite.Progress(content, role),
            SessionMessageKind.SystemBoundary => SessionTurnWrite.SystemBoundary(content, role),
            _ => SessionTurnWrite.Assistant(content, role)
        };
    }

    private static SessionMessageKind InferMessageKind(string? role)
    {
        return role?.Trim().ToLowerInvariant() switch
        {
            "user" => SessionMessageKind.User,
            "assistant" => SessionMessageKind.Assistant,
            "progress" => SessionMessageKind.Progress,
            "system" => SessionMessageKind.SystemBoundary,
            "system_boundary" => SessionMessageKind.SystemBoundary,
            _ => SessionMessageKind.Assistant
        };
    }

    private static bool ShouldEnterContextWindow(SessionMessageKind kind)
        => kind is SessionMessageKind.User or SessionMessageKind.Assistant or SessionMessageKind.SystemBoundary;

    private async Task PersistCompressionProjectionAsync(CodexSession session)
    {
        try
        {
            if (_compressionStore != null && !string.IsNullOrWhiteSpace(session.HistorySummary) && session.LastCompressedAt is { } ca)
            {
                await _compressionStore.SaveAsync(session.Id, session.HistorySummary, ca).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist compression projection. SessionId={SessionId}", session.Id);
        }
    }

    private bool RefreshContextWindowSnapshot(CodexSession session)
    {
        var estimatedChars = EstimateConversationContextChars(session);
        var estimatedTokens = EstimateConversationContextTokens(session);
        var summaryChars = session.HistorySummary?.Length ?? 0;

        var changed = false;
        changed |= SetMetadataValue(session.Metadata, ContextEstimatedCharsMetadataKey, estimatedChars.ToString(CultureInfo.InvariantCulture));
        changed |= SetMetadataValue(session.Metadata, ContextEstimatedTokensMetadataKey, estimatedTokens.ToString(CultureInfo.InvariantCulture));
        changed |= SetMetadataValue(session.Metadata, ContextRecentTurnsMetadataKey, session.RecentTurns.Count.ToString(CultureInfo.InvariantCulture));
        changed |= SetMetadataValue(session.Metadata, ContextSummaryCharsMetadataKey, summaryChars.ToString(CultureInfo.InvariantCulture));
        changed |= SetMetadataValue(session.Metadata, ContextUpdatedAtUtcMetadataKey, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return changed;
    }

    private static bool AccumulateLongMetadata(Dictionary<string, string> metadata, string key, int delta)
    {
        if (delta == 0)
        {
            return false;
        }

        var current = 0L;
        if (metadata.TryGetValue(key, out var raw) &&
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            current = parsed;
        }

        var updated = checked(current + delta);
        return SetMetadataValue(metadata, key, updated.ToString(CultureInfo.InvariantCulture));
    }

    private static bool SetMetadataValue(Dictionary<string, string> metadata, string key, string value)
    {
        if (metadata.TryGetValue(key, out var existing) &&
            string.Equals(existing, value, StringComparison.Ordinal))
        {
            return false;
        }

        metadata[key] = value;
        return true;
    }

    private static object ToCheckpointProjection(QueryRuntimeCheckpoint checkpoint)
        => new
        {
            checkpoint.QueryId,
            checkpoint.SessionId,
            checkpoint.EntryPoint,
            checkpoint.Round,
            checkpoint.CurrentPhase,
            checkpoint.CreatedAtUtc,
            checkpoint.ActiveTaskSummary,
            checkpoint.WorkerType,
            checkpoint.WorkerDisplayName,
            checkpoint.WorkerIsolationMode,
            checkpoint.RequiredToolContractName,
            checkpoint.RequiredToolContractToolNames,
            checkpoint.RequiredToolNameForNextRound,
            checkpoint.RequiredToolContractSatisfied,
            checkpoint.TotalToolCalls,
            checkpoint.WriteToolCalls,
            checkpoint.RecoveryCount,
            checkpoint.LastContinueReason,
            LastPromptSnapshot = checkpoint.LastPromptAssemblySnapshot == null
                ? null
                : new
                {
                    checkpoint.LastPromptAssemblySnapshot.Round,
                    checkpoint.LastPromptAssemblySnapshot.ToolNames,
                    checkpoint.LastPromptAssemblySnapshot.ToolChoice,
                    checkpoint.LastPromptAssemblySnapshot.RequiredToolName,
                    checkpoint.LastPromptAssemblySnapshot.DroppedFrames,
                    checkpoint.LastPromptAssemblySnapshot.BudgetDecisions
                },
            EvidenceLedger = new
            {
                Files = checkpoint.EvidenceLedger.Files.TakeLast(MaxRuntimeCheckpointFiles),
                ToolResults = checkpoint.EvidenceLedger.ToolResults.TakeLast(MaxRuntimeCheckpointToolResults),
                PendingModifications = checkpoint.EvidenceLedger.PendingModifications.TakeLast(MaxRuntimeCheckpointPendingModifications),
                checkpoint.EvidenceLedger.LastToolBatchSummary,
                Failures = checkpoint.EvidenceLedger.Failures.TakeLast(MaxRuntimeCheckpointFailures),
                checkpoint.EvidenceLedger.RepeatedEvidenceKeys
            }
        };

    private static List<string> GetRecentExecutionUpdates(CodexSession session)
    {
        if (!session.Metadata.TryGetValue(RecentExecutionUpdatesMetadataKey, out var raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonConvert.DeserializeObject<List<string>>(raw)?
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(MaxRecentExecutionUpdates)
                .ToList()
                ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string NormalizeExecutionUpdateText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            " ",
            value.Split(LineBreakSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => segment.Trim())
                .Where(segment => segment.Length > 0));

        if (normalized.Length <= MaxExecutionUpdateChars)
        {
            return normalized;
        }

        return normalized[..(MaxExecutionUpdateChars - 3)].TrimEnd() + "...";
    }

    private static string? TryGetSemanticRecallContext(IEnumerable<MemoryEntry> facts)
    {
        var factList = facts as IReadOnlyList<MemoryEntry> ?? facts.ToList();
        var context = factList.FirstOrDefault(f => string.Equals(f.Key, ProjectMemoryFactKeys.SemanticRecallContext, StringComparison.Ordinal))?.Value;
        if (!string.IsNullOrWhiteSpace(context))
        {
            return context;
        }

        var traceRaw = factList.FirstOrDefault(f => string.Equals(f.Key, ProjectMemoryFactKeys.SemanticRecallTrace, StringComparison.Ordinal))?.Value;
        if (string.IsNullOrWhiteSpace(traceRaw))
        {
            return null;
        }

        try
        {
            var trace = JsonConvert.DeserializeObject<SemanticRecallTrace>(traceRaw);
            if (trace == null)
            {
                return null;
            }

            return BuildSemanticRecallContext(trace);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildSemanticRecallContext(SemanticRecallTrace trace)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("- Query: " + trace.Query);
        sb.AppendLine("- Source: " + trace.Source);
        sb.AppendLine("- Enabled: " + trace.Enabled);
        sb.AppendLine("- Succeeded: " + trace.Succeeded);
        sb.AppendLine("- Injected: " + trace.InjectedCount + "/" + trace.CandidateCount);
        if (!string.IsNullOrWhiteSpace(trace.Error))
        {
            sb.AppendLine("- Error: " + trace.Error);
        }

        foreach (var item in trace.Items.Take(5))
        {
            var updatedAt = item.UpdatedAtUtc.HasValue
                ? item.UpdatedAtUtc.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "n/a";
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "- [{0}] score={1:F2}, freshness={2}, updated_at={3}",
                item.SourceType,
                item.Score,
                item.Freshness,
                updatedAt));
            sb.AppendLine("  " + item.ContentPreview);
        }

        return sb.ToString().TrimEnd();
    }
}
