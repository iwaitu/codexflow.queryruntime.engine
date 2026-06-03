using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Committee;
using CodexFlow.Core.Services;
using CodexFlow.Core.TDD;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Agents;

public class CodexOrchestrator
{
    private const string GitPushConsentOnceKey = "GitPushConsentOnce";
    private const string DocumentationSyncPendingMetadataKey = "DocumentationSyncPending";
    private const string DocumentationSyncStatusMetadataKey = "DocumentationSyncStatus";
    private const int ExistingSessionRecoveryMaxAttempts = 3;
    private static readonly TimeSpan ExistingSessionRecoveryRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 标识上次 attempt 失败的原因，用于决定下次重试时跳过哪些环节。
    /// </summary>
    private enum RetryReason
    {
        None,
        SecurityAuditFailure,
        ValidationFailure,
        ZeroToolCalls,
        HashlineMismatch,
        BuildVerificationFailure
    }

    private readonly CodexSessionManager _sessionManager;
    public CodexSessionManager SessionManager => _sessionManager;
    private readonly ICodexAgentKernel _kernel;
    private readonly ICodexArchitect _architect;
    private readonly ICodexSecurityAuditor _securityAuditor;
    private readonly ICodexValidator _validator;
    private readonly IGitService _gitService;
    private readonly ProjectScanner _scanner;
    private readonly ICodeAnalysisService _semanticScanner;
    private readonly IArchitectureService _archService;
    private readonly ICodexTestDesigner _testDesigner;
    private readonly IPolicyValidator _policyValidator;
    private readonly ISemanticDiffService _semanticDiff;
    private readonly IEnumerable<ICodexEventSink> _sinks;
    private readonly ITaskListService _taskListService;
    private readonly IProjectMemoryService? _projectMemoryService;
    private readonly IComplexityClassifier? _complexityClassifier;
    private readonly ICommitteePlanningService? _committeePlanningService;
    private readonly ICommitteeRoleChatClientFactory? _committeeClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CodexOrchestrator> _logger;

#pragma warning disable CA1003 // Preserve legacy public event shape for compatibility.
    public event Action<CodexEvent>? OnEvent;
#pragma warning restore CA1003

    public CodexOrchestrator(
        CodexSessionManager sessionManager,
        ICodexAgentKernel kernel,
        ICodexArchitect architect,
        ICodexSecurityAuditor securityAuditor,
        ICodexValidator validator,
        IGitService gitService,
        ProjectScanner scanner,
        ICodeAnalysisService semanticScanner,
        IArchitectureService archService,
        ICodexTestDesigner testDesigner,
        IPolicyValidator policyValidator,
        ISemanticDiffService semanticDiff,
        ITaskListService taskListService,
        IProjectMemoryService? projectMemoryService,
        IComplexityClassifier? complexityClassifier,
        ICommitteePlanningService? committeePlanningService,
        ICommitteeRoleChatClientFactory? committeeClientFactory,
        IConfiguration configuration,
        ILogger<CodexOrchestrator> logger,
        IEnumerable<ICodexEventSink>? sinks = null)
    {
        _sessionManager = sessionManager;
        _kernel = kernel;
        _architect = architect;
        _securityAuditor = securityAuditor;
        _validator = validator;
        _gitService = gitService;
        _scanner = scanner;
        _semanticScanner = semanticScanner;
        _archService = archService;
        _testDesigner = testDesigner;
        _policyValidator = policyValidator;
        _semanticDiff = semanticDiff;
        _taskListService = taskListService;
        _projectMemoryService = projectMemoryService;
        _complexityClassifier = complexityClassifier;
        _committeePlanningService = committeePlanningService;
        _committeeClientFactory = committeeClientFactory;
        _configuration = configuration;
        _logger = logger;
        _sinks = sinks ?? Enumerable.Empty<ICodexEventSink>();

        _kernel.OnEvent += e => OnEvent?.Invoke(e);
    }

    public CodexOrchestrator(
        CodexSessionManager sessionManager,
        ICodexAgentKernel kernel,
        ICodexArchitect architect,
        ICodexSecurityAuditor securityAuditor,
        ICodexValidator validator,
        IGitService gitService,
        ProjectScanner scanner,
        ICodeAnalysisService semanticScanner,
        IArchitectureService archService,
        ICodexTestDesigner testDesigner,
        IPolicyValidator policyValidator,
        ISemanticDiffService semanticDiff,
        ITaskListService taskListService,
        IConfiguration configuration,
        ILogger<CodexOrchestrator> logger,
        IEnumerable<ICodexEventSink>? sinks = null)
        : this(
            sessionManager,
            kernel,
            architect,
            securityAuditor,
            validator,
            gitService,
            scanner,
            semanticScanner,
            archService,
            testDesigner,
            policyValidator,
            semanticDiff,
            taskListService,
            projectMemoryService: null,
            complexityClassifier: null,
            committeePlanningService: null,
            committeeClientFactory: null,
            configuration,
            logger,
            sinks)
    {
    }

    private async Task ReportProgressAsync(string message, CodexSession session, CodexEventType type = CodexEventType.General, string? taskId = null, object? payload = null)
    {
        StructuredLog.Information(_logger, "Progress: {Message}", message);
        var e = new CodexEvent
        {
            SessionId = session.Id,
            Type = type,
            Message = message,
            TaskId = taskId,
            Payload = payload,
            Timestamp = DateTime.UtcNow
        };

        OnEvent?.Invoke(e);

        foreach (var sink in _sinks)
        {
            await sink.PublishAsync(e).ConfigureAwait(false);
        }

        // [Level 8 Enhancement] Journaling every progress event for systemic thinking
        try
        {
            var data = payload != null ? JsonConvert.SerializeObject(payload) : "";
            await _sessionManager.Memory.LogEventAsync(session.Id, type.ToString(), $"{message} | Data: {data}").ConfigureAwait(false);
            await _sessionManager
                .RecordTurnsAsync(session.Id, SessionMessageDurabilityPolicy.FromEvent(type, message))
                .ConfigureAwait(false);
        }
        catch (IOException) { /* 忽略日誌記錄異常 */ }
        catch (InvalidOperationException) { /* 忽略日誌記錄異常 */ }
        catch (TimeoutException) { /* 忽略日誌記錄異常 */ }
    }

    /// <summary>
    /// [Level 7] 原子任务执行器：仅处理代码实现类任务，包含影子隔离和质量闭环。
    /// </summary>
    public async Task<OrchestratorResult> ExecuteCodeTaskAsync(string sessionId, string taskId, string userId = "", string workspacePath = "", CancellationToken ct = default)
    {
        var session = await LoadExistingSessionWithRetryAsync(sessionId, userId, workspacePath, ct).ConfigureAwait(false);
        if (session == null)
        {
            StructuredLog.Error(_logger,
                "ExecuteCodeTaskAsync failed to load existing session {SessionId} after {AttemptCount} attempts. Refusing to fabricate an empty session for task {TaskId}.",
                sessionId, ExistingSessionRecoveryMaxAttempts, taskId);
            return new OrchestratorResult(
                false,
                $"会话 [{sessionId}] 的执行状态未能从存储中恢复，无法继续执行任务 [{taskId}]。请先刷新会话状态或重新生成计划后再试。",
                new CodexSession
                {
                    Id = sessionId,
                    UserId = userId,
                    WorkspacePath = workspacePath,
                    CurrentStage = 1
                });
        }

        if (session.Metadata.TryGetValue(PlanGuardMetadataKeys.PlanResetRequired, out var resetRequiredRaw) &&
            bool.TryParse(resetRequiredRaw, out var resetRequired) &&
            resetRequired)
        {
            session.Metadata.TryGetValue(PlanGuardMetadataKeys.PlanResetReason, out var resetReason);
            var suffix = string.IsNullOrWhiteSpace(resetReason)
                ? string.Empty
                : $" 当前失效原因：{resetReason}";
            return new OrchestratorResult(false,
                $"当前计划已失效，不能继续执行旧的 task_id。请先重新调用 {PlanningToolNames.Primary} 生成新计划。" + suffix,
                session);
        }

        await AutoSkipNonCodeTasksAsync(session).ConfigureAwait(false);
        var mainRoot = ToolPathResolver.ResolveProjectRoot(session.WorkspacePath, null, session.ProjectUrl, session.Metadata);
        if (string.IsNullOrWhiteSpace(mainRoot))
        {
            mainRoot = ResolveMainRoot(session.WorkspacePath, session.ProjectUrl);
        }

        // 基础环境保障：确保仓库已初始化
        if (!Directory.Exists(mainRoot))
        {
            await _gitService.InitRepositoryAsync(mainRoot).ConfigureAwait(false);
        }

        var targetTask = session.Plan.FirstOrDefault(t => t.Id == taskId);
        if (targetTask == null)
        {
            _logger.LogError(
                "ExecuteCodeTaskAsync could not find task in current plan. SessionId={SessionId}, TaskId={TaskId}, PlanCount={PlanCount}, PlanVersion={PlanVersion}, PlanGeneratedAtUtc={PlanGeneratedAtUtc}, ActiveTaskId={ActiveTaskId}, PlanTaskIds=[{PlanTaskIds}]",
                session.Id,
                taskId,
                session.Plan.Count,
                session.PlanVersion ?? "<null>",
                session.PlanGeneratedAtUtc,
                session.ActiveTaskId ?? "<null>",
                string.Join(", ", session.Plan.Select(t => t.Id)));
            return new OrchestratorResult(false, $"任务 [{taskId}] 未在当前计划中找到。", session);
        }

        if (!CodexTaskClassifier.IsCodeExecutionTask(targetTask))
        {
            await MarkTaskSkippedAsync(session, targetTask, "分析/只读任务不进入 execute_code_task，已自动跳过。").ConfigureAwait(false);
            var nextTask = FindNextExecutableTask(session.Plan);
            if (nextTask != null)
            {
                return await ExecuteCodeTaskAsync(sessionId, nextTask.Id, userId, workspacePath, ct).ConfigureAwait(false);
            }

            return new OrchestratorResult(true, $"任务 [{taskId}] 属于 analysis 类型，已跳过。当前没有待执行的代码任务。", session);
        }

        StructuredLog.Information(_logger, "Atomic execution started for Task: {TaskId}", taskId);

        // [Level 8] 构建/刷新项目文件索引
        try
        {
            var index = await _scanner.GenerateFileIndexAsync(mainRoot).ConfigureAwait(false) ?? new List<FileIndexEntry>();

            if (index.Count <= 2 &&
                !string.IsNullOrWhiteSpace(mainRoot) &&
                !string.IsNullOrWhiteSpace(session.WorkspacePath) &&
                !Path.GetFullPath(session.WorkspacePath).Equals(Path.GetFullPath(mainRoot), StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(session.WorkspacePath))
            {
                var fallbackIndex = await _scanner.GenerateFileIndexAsync(session.WorkspacePath).ConfigureAwait(false) ?? new List<FileIndexEntry>();
                if (fallbackIndex.Count > index.Count)
                {
                    StructuredLog.Warning(_logger, "Project index appears too small at {MainRoot} ({MainCount}). Fallback to workspace root {Workspace} ({FallbackCount}).",
                        mainRoot, index.Count, session.WorkspacePath, fallbackIndex.Count);
                    index = fallbackIndex;
                }
            }

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(index);
            var indexMeta = new MemoryEntryMetadata(
                Scope: MemoryFactScope.Session,
                Source: "orchestrator_index_refresh",
                Confidence: MemoryFactConfidence.High).ToJson();
            await _sessionManager.LearnFactAsync(sessionId, ProjectMemoryFactKeys.ProjectFileIndex, json, MemoryFactCategories.Project, indexMeta).ConfigureAwait(false);
            StructuredLog.Information(_logger, "Project Index refreshed. Total files: {Count}", index.Count);
        }
        catch (IOException ex)
        {
            StructuredLog.Warning(_logger, "Failed to build project index: {Message}", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Warning(_logger, "Failed to build project index: {Message}", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Warning(_logger, "Failed to build project index: {Message}", ex.Message);
        }

        var context = await _sessionManager.GetFullContextAsync(sessionId).ConfigureAwait(false);

        return await ExecuteTaskInShadowPathAsync(session, targetTask, mainRoot, "main", context, ct).ConfigureAwait(false);
    }

    private async Task<CodexSession?> LoadExistingSessionWithRetryAsync(string sessionId, string userId, string workspacePath, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= ExistingSessionRecoveryMaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var session = await _sessionManager
                .GetExistingSessionAsync(sessionId, userId, workspacePath, (Uri?)null)
                .ConfigureAwait(false);

            if (session != null)
            {
                if (attempt > 1)
                {
                    StructuredLog.Warning(
                        _logger,
                        "Existing session recovery succeeded on retry. SessionId={SessionId}, Attempt={Attempt}, WorkspacePath={WorkspacePath}, PlanCount={PlanCount}, Stage={Stage}",
                        sessionId,
                        attempt,
                        string.IsNullOrWhiteSpace(workspacePath) ? "<empty>" : workspacePath,
                        session.Plan.Count,
                        session.CurrentStage);
                }

                return session;
            }

            if (attempt == ExistingSessionRecoveryMaxAttempts)
            {
                StructuredLog.Error(
                    _logger,
                    "Existing session recovery exhausted retries. SessionId={SessionId}, Attempts={AttemptCount}, UserId={UserId}, WorkspacePath={WorkspacePath}",
                    sessionId,
                    ExistingSessionRecoveryMaxAttempts,
                    string.IsNullOrWhiteSpace(userId) ? "<empty>" : userId,
                    string.IsNullOrWhiteSpace(workspacePath) ? "<empty>" : workspacePath);
                break;
            }

            StructuredLog.Warning(
                _logger,
                "Existing session load returned null. SessionId={SessionId}, Attempt={Attempt}/{AttemptCount}, UserId={UserId}, WorkspacePath={WorkspacePath}. Retrying after {RetryDelayMs}ms.",
                sessionId,
                attempt,
                ExistingSessionRecoveryMaxAttempts,
                string.IsNullOrWhiteSpace(userId) ? "<empty>" : userId,
                string.IsNullOrWhiteSpace(workspacePath) ? "<empty>" : workspacePath,
                (int)ExistingSessionRecoveryRetryDelay.TotalMilliseconds);

            await Task.Delay(ExistingSessionRecoveryRetryDelay, ct).ConfigureAwait(false);
        }

        return null;
    }

    private string ResolveMainRoot(string workspacePath, Uri? projectUrl)
    {
        if (string.IsNullOrWhiteSpace(workspacePath)) return Directory.GetCurrentDirectory();

        var normalized = Path.GetFullPath(workspacePath);
        var isRepoRoot = Directory.Exists(Path.Combine(normalized, ".git"));
        if (isRepoRoot) return normalized;

        var candidateRepos = Directory.Exists(normalized)
            ? Directory.GetDirectories(normalized)
                .Where(d => !string.Equals(Path.GetFileName(d), "shadows", StringComparison.OrdinalIgnoreCase))
                .Where(d => Directory.Exists(Path.Combine(d, ".git")))
                .ToList()
            : new List<string>();

        if (projectUrl != null)
        {
            var preferredName = TryGetRepoNameFromUrl(projectUrl);
            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                var preferredPath = candidateRepos.FirstOrDefault(d => string.Equals(Path.GetFileName(d), preferredName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(preferredPath))
                {
                    return preferredPath;
                }
            }
        }

        if (candidateRepos.Count == 1)
        {
            return candidateRepos[0];
        }

        if (candidateRepos.Count > 1)
        {
            var first = candidateRepos.OrderBy(x => Path.GetFileName(x)).First();
            StructuredLog.Warning(_logger, "Multiple git repositories detected under workspace {Workspace}. Fallback selecting {Repo}. ProjectUrl={ProjectUrl}", normalized, first, projectUrl?.ToString() ?? "<empty>");
            return first;
        }

        return normalized;
    }

    private static string? TryGetRepoNameFromUrl(Uri? projectUrl)
    {
        if (projectUrl == null) return null;

        var trimmed = projectUrl.ToString().Trim();
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        var slashIndex = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        if (slashIndex < 0 || slashIndex == trimmed.Length - 1) return null;

        var name = trimmed[(slashIndex + 1)..].Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string BuildIcodexBranchName(string sessionId, string taskId)
    {
        static string Sanitize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "task";
            var sanitized = raw.Trim().Replace(' ', '-');
            var invalidChars = new[] { '~', '^', ':', '?', '*', '[', '\\' };
            foreach (var ch in invalidChars)
            {
                sanitized = sanitized.Replace(ch, '-');
            }
            sanitized = sanitized.Replace("..", "-", StringComparison.Ordinal);
            sanitized = sanitized.Trim('.', '/', '-');
            return string.IsNullOrWhiteSpace(sanitized) ? "task" : sanitized;
        }

        var sessionPart = Sanitize(sessionId);
        var taskPart = Sanitize(taskId);
        return $"icodex-{sessionPart}-{taskPart}";
    }

    /// <summary>
    /// 检测用户输入是否包含系统通知防循环标记
    /// </summary>
    private static bool IsNotificationSuppressed(string userPrompt)
    {
        if (string.IsNullOrEmpty(userPrompt)) return false;
        return userPrompt.Contains("SYSTEM_NOTIFICATION", StringComparison.OrdinalIgnoreCase)
            || userPrompt.Contains("NO_AUTOPLAN", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 读取当前配置的委员会模式。
    /// </summary>
    private CommitteeMode GetCommitteeMode()
    {
        var modeStr = _configuration["Planning:CommitteeModeEnabled"];
        return modeStr?.Trim() switch
        {
            "On" or "on" or "ON" => CommitteeMode.On,
            "Shadow" or "shadow" or "SHADOW" => CommitteeMode.Shadow,
            _ => CommitteeMode.Off
        };
    }

    /// <summary>
    /// 处理用户对委员会确认的回复。
    /// </summary>
    private async Task<OrchestratorResult> HandleCommitteeConfirmationAsync(
        CodexSession session, string userPrompt, CancellationToken ct)
    {
        // 清除 pending 标记
        session.Metadata.Remove(CommitteeConstants.CommitteeConfirmationPendingKey);

        var trimmed = userPrompt.Trim();

        // 先检查拒绝关键词（防止"不同意"被子串匹配为"同意"）
        var rejected = CommitteeConstants.RejectKeywords
            .Any(k => trimmed.Contains(k, StringComparison.OrdinalIgnoreCase));

        var accepted = !rejected && CommitteeConstants.AcceptKeywords
            .Any(k => trimmed.Contains(k, StringComparison.OrdinalIgnoreCase));

        var goal = session.Metadata.TryGetValue("OriginalGoal", out var g) ? g : "";

        if (!accepted)
        {
            _logger.LogInformation("用户拒绝召开委员会: SessionId={SessionId}", session.Id);
            session.Metadata[CommitteeConstants.CommitteeConfirmationResultKey] = "rejected";
            await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
            return await FallbackToRegularPlanningAsync(session, goal, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("用户同意召开委员会: SessionId={SessionId}", session.Id);
        session.Metadata[CommitteeConstants.CommitteeConfirmationResultKey] = "accepted";
        await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);

        var committeeMode = GetCommitteeMode();

        if (committeeMode == CommitteeMode.On && _committeePlanningService != null)
        {
            return await RunCommitteeOnModeAsync(session, goal, ct).ConfigureAwait(false);
        }

        if (committeeMode == CommitteeMode.Shadow && _committeePlanningService != null)
        {
            return await RunCommitteeShadowModeAsync(session, goal, ct).ConfigureAwait(false);
        }

        // 无委员会服务可用，回退
        return await FallbackToRegularPlanningAsync(session, goal, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// On 模式：委员会结果直接驱动执行。
    /// </summary>
    private async Task<OrchestratorResult> RunCommitteeOnModeAsync(
        CodexSession session, string goal, CancellationToken ct)
    {
        await ReportProgressAsync("专家委员会评审会议已启动...", session, CodexEventType.General).ConfigureAwait(false);

        var request = new CommitteePlanningRequest
        {
            Goal = goal,
            SessionId = session.Id,
            WorkspacePath = session.WorkspacePath,
            ProjectSummary = session.ProjectSummary
        };

        var result = await _committeePlanningService!.RunCommitteePlanningAsync(request, ct).ConfigureAwait(false);

        if (!result.Success || result.Status != CommitteeMeetingStatus.Completed || string.IsNullOrWhiteSpace(result.FinalPlan))
        {
            var statusOrReason = !string.IsNullOrWhiteSpace(result.FailureReason)
                ? result.FailureReason
                : result.Status.ToString();
            _logger.LogWarning("委员会规划未达成可执行定稿: {StatusOrReason}，回退到常规规划", statusOrReason);
            await ReportProgressAsync(
                $"委员会规划未达成可执行定稿 ({statusOrReason})，回退到常规规划。",
                session, CodexEventType.General).ConfigureAwait(false);
            return await FallbackToRegularPlanningAsync(session, goal, ct).ConfigureAwait(false);
        }

        await ReportProgressAsync(
            $"委员会经过 {result.TotalRounds} 轮评审达成{(result.Status == CommitteeMeetingStatus.Completed ? "共识" : "最大轮次")}，正在投影为任务计划...",
            session, CodexEventType.General).ConfigureAwait(false);

        // 投影蓝图为 CodexTask
        var projectionService = new PlanProjectionService(_logger);
        var pmClient = _committeeClientFactory?.GetClientForRole("ProjectManager");

        List<ProjectedTask>? projected = null;
        if (pmClient != null)
        {
            projected = await projectionService.ProjectAsync(result.FinalPlan, pmClient, ct).ConfigureAwait(false);
        }

        if (projected == null || projected.Count == 0)
        {
            _logger.LogWarning("蓝图投影失败，回退到常规规划");
            await ReportProgressAsync("蓝图投影为任务计划失败，回退到常规规划。", session, CodexEventType.General).ConfigureAwait(false);
            return await FallbackToRegularPlanningAsync(session, goal, ct).ConfigureAwait(false);
        }

        // Schema 校验
        var validation = projectionService.Validate(projected);
        if (!validation.IsValid)
        {
            _logger.LogWarning("投影结果校验失败: {Errors}", string.Join("; ", validation.Errors));
            await ReportProgressAsync(
                $"投影结果校验失败 ({validation.Errors.Count} 个错误)，回退到常规规划。",
                session, CodexEventType.General).ConfigureAwait(false);
            return await FallbackToRegularPlanningAsync(session, goal, ct).ConfigureAwait(false);
        }

        // 转换并覆盖 session.Plan
        var tasks = PlanProjectionService.ToCodexTasks(projected);
        session.ReplacePlan(tasks);
        await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);

        if (session.Plan is { Count: > 0 })
        {
            await PublishTaskListAsync(session).ConfigureAwait(false);
        }

        await ReportProgressAsync(
            $"委员会规划完成，已生成 {tasks.Count} 个任务。",
            session, CodexEventType.General).ConfigureAwait(false);

        return new OrchestratorResult(true, $"委员会规划完成，共 {tasks.Count} 个任务。", session);
    }

    /// <summary>
    /// Shadow 模式：主路径走常规规划，旁路运行委员会并输出 diff。
    /// </summary>
    private async Task<OrchestratorResult> RunCommitteeShadowModeAsync(
        CodexSession session, string goal, CancellationToken ct)
    {
        // 主路径：常规规划
        var regularResult = await FallbackToRegularPlanningAsync(session, goal, ct).ConfigureAwait(false);
        var baselinePlan = session.Plan.ToList();

        // 旁路：委员会规划（不驱动执行）
        try
        {
            await ReportProgressAsync("Shadow 模式：旁路运行委员会评审...", session, CodexEventType.General).ConfigureAwait(false);

            var request = new CommitteePlanningRequest
            {
                Goal = goal,
                SessionId = session.Id,
                WorkspacePath = session.WorkspacePath,
                ProjectSummary = session.ProjectSummary
            };

            var committeeResult = await _committeePlanningService!.RunCommitteePlanningAsync(request, ct).ConfigureAwait(false);

            if (committeeResult.Success && !string.IsNullOrWhiteSpace(committeeResult.FinalPlan))
            {
                var projectionService = new PlanProjectionService(_logger);
                var pmClient = _committeeClientFactory?.GetClientForRole("ProjectManager");

                // 尝试将委员会蓝图投影为 CodexTask 列表
                List<CodexTask>? committeeTasks = null;
                if (pmClient != null)
                {
                    var projected = await projectionService.ProjectAsync(committeeResult.FinalPlan, pmClient, ct).ConfigureAwait(false);
                    if (projected is { Count: > 0 })
                    {
                        var validation = projectionService.Validate(projected);
                        if (validation.IsValid)
                        {
                            committeeTasks = PlanProjectionService.ToCodexTasks(projected);
                        }
                        else
                        {
                            _logger.LogWarning("Shadow 模式投影校验失败: {Errors}", string.Join("; ", validation.Errors));
                        }
                    }
                }

                // 生成结构化 diff
                object diffData;
                if (committeeTasks is { Count: > 0 })
                {
                    diffData = PlanProjectionService.GenerateShadowDiff(baselinePlan, committeeTasks);
                }
                else
                {
                    // 投影失败时仍记录基础对比信息
                    diffData = new
                    {
                        baseline_plan_source = "DefaultCodexPlanner",
                        committee_plan_source = "CommitteePlanning",
                        baseline_task_count = baselinePlan.Count,
                        committee_projection_failed = true,
                        committee_status = committeeResult.Status.ToString(),
                        committee_total_rounds = committeeResult.TotalRounds,
                        committee_unresolved_items = committeeResult.UnresolvedItems,
                        summary = $"Shadow 模式：基线 {baselinePlan.Count} 任务，委员会蓝图投影失败",
                        generated_at = DateTime.UtcNow
                    };
                }

                // 写入 diff 和 metrics 工件
                if (!string.IsNullOrEmpty(committeeResult.MeetingDirectory) && Directory.Exists(committeeResult.MeetingDirectory))
                {
                    var artifacts = new MeetingArtifactService(_logger);
                    var state = new CommitteeMeetingState
                    {
                        MeetingId = committeeResult.MeetingId ?? string.Empty,
                        MeetingDirectory = committeeResult.MeetingDirectory
                    };
                    artifacts.WriteShadowPlanDiff(state, diffData);
                    artifacts.WriteShadowMetrics(state, baselinePlan, committeeTasks, committeeResult);
                }

                _logger.LogInformation("Shadow 模式 diff 已生成");
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Shadow 模式委员会旁路执行失败，不影响主路径");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Shadow 模式委员会旁路执行失败，不影响主路径");
        }

        return regularResult;
    }

    /// <summary>
    /// 回退到常规规划流程。
    /// </summary>
    private async Task<OrchestratorResult> FallbackToRegularPlanningAsync(
        CodexSession session, string goal, CancellationToken ct)
    {
        var plan = await _architect.PlanAsync(session, goal, ct).ConfigureAwait(false);
        if (plan is { Count: > 0 })
        {
            session.ReplacePlan(plan);
        }

        await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);

        if (session.Plan is { Count: > 0 })
        {
            await PublishTaskListAsync(session).ConfigureAwait(false);
        }

        return new OrchestratorResult(true, "已生成任务计划。", session);
    }

    private static bool IsPlanFullyCompletedForPush(CodexSession session)
    {
        if (session.Plan.Count == 0)
        {
            return false;
        }

        return session.Plan.All(t => t.Status is CodexTaskStatus.Success
            or CodexTaskStatus.CompletedWithWarnings
            or CodexTaskStatus.Skipped);
    }

    /// <summary>
    /// [Legacy] 向后兼容接口：现在仅作为“执行下一个待办任务”的简化入口。
    /// 核心逻辑控制已移交给 Chat LLM。
    /// </summary>
    public Task<OrchestratorResult> RunNextStepAsync(string sessionId, string userPrompt, string userId = "", string workspacePath = "", string? taskId = null, string? repoUrl = null, string? baseBranch = null, CancellationToken ct = default)
    {
        return RunNextStepAsync(sessionId, userPrompt, userId, workspacePath, taskId, CodexSession.CreateProjectUri(repoUrl), baseBranch, ct);
    }

    public async Task<OrchestratorResult> RunNextStepAsync(string sessionId, string userPrompt, string userId, string workspacePath, string? taskId, Uri? repoUrl, string? baseBranch, CancellationToken ct = default)
    {
        // 防循环守卫：系统通知消息不触发自动规划
        if (IsNotificationSuppressed(userPrompt))
        {
            _logger.LogInformation("通知消息检测到 NO_AUTOPLAN 标记，跳过自动规划: SessionId={SessionId}", sessionId);
            var stubSession = await _sessionManager.GetOrCreateSessionAsync(sessionId, userId, workspacePath, repoUrl).ConfigureAwait(false);
            return new OrchestratorResult(true, "系统通知已送达，等待用户指令。", stubSession);
        }

        var session = await _sessionManager.GetOrCreateSessionAsync(sessionId, userId, workspacePath, repoUrl).ConfigureAwait(false);

        // [Committee] 处理用户对委员会确认的回复
        if (session.Metadata.TryGetValue(CommitteeConstants.CommitteeConfirmationPendingKey, out var pending)
            && string.Equals(pending, "true", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleCommitteeConfirmationAsync(session, userPrompt, ct).ConfigureAwait(false);
        }

        // Compatibility fix for old test behavior: if prompt is initial inquiry (Stage 1),
        // we should run Stage 1 analysis/planning instead of failing on empty plan.
        if (session.CurrentStage == 1 && session.Plan.Count == 0)
        {
            // Auto-trigger analysis (Stage 1) -> This restores legacy test behavior
            // where RunNextStepAsync handles the full flow.

            // 1. Run Architect (Analysis)
            session.ProjectSummary = await _architect.AnalyzeAsync(session, userPrompt, ct).ConfigureAwait(false);

            // [Fix] Save analysis to file and trigger CodePreview
            var planPath = Path.Combine(session.WorkspacePath, "implementation_plan.md");
            if (session.ProjectSummary == null) session.ProjectSummary = "";
            await File.WriteAllTextAsync(planPath, session.ProjectSummary, ct).ConfigureAwait(false);

            await ReportProgressAsync("已生成实施计划 (implementation_plan.md)，正在推送到 UI...", session, CodexEventType.General).ConfigureAwait(false);

            await ReportProgressAsync("Previewing Plan...", session, CodexEventType.CodePreview, null, new
            {
                filePath = "implementation_plan.md",
                code = session.ProjectSummary,
                language = "markdown"
            }).ConfigureAwait(false);

            // Auto-scan (Stage 1.5) - REQUIRED for tests like Stage1_5PerceptionTests
            var scanReport = await _scanner.ScanAndSummarizeAsync(session.WorkspacePath).ConfigureAwait(false);
            var scanMeta = new MemoryEntryMetadata(
                Scope: MemoryFactScope.Session,
                Source: "orchestrator_auto_scan",
                Confidence: MemoryFactConfidence.High).ToJson();
            await _sessionManager.LearnFactAsync(sessionId, ProjectMemoryFactKeys.ProjectFingerprint, scanReport, MemoryFactCategories.Project, scanMeta).ConfigureAwait(false);

            session.CurrentStage = 2; // Move to Planning
            await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);

            // [Committee] Stage 2 拦截：复杂度判定与用户确认
            var committeeMode = GetCommitteeMode();
            if (committeeMode != CommitteeMode.Off && _complexityClassifier != null)
            {
                var classification = await _complexityClassifier.ClassifyAsync(userPrompt, session.ProjectSummary, ct).ConfigureAwait(false);
                if (classification.IsComplex)
                {
                    _logger.LogInformation("委员会模式: 检测到复杂需求, Reason={Reason}, SessionId={SessionId}",
                        classification.Reason, sessionId);

                    // 保存复杂度判定结果到 session metadata，等待用户确认
                    session.Metadata[CommitteeConstants.CommitteeConfirmationPendingKey] = "true";
                    session.Metadata[CommitteeConstants.CommitteeComplexityReasonKey] = classification.Reason;
                    session.Metadata["OriginalGoal"] = userPrompt;
                    await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);

                    // 向用户发送确认消息
                    var confirmMessage = CommitteeConstants.CommitteeConfirmationMessage
                        .Replace("{0}", classification.Reason, StringComparison.Ordinal);

                    await ReportProgressAsync(confirmMessage, session, CodexEventType.General).ConfigureAwait(false);

                    return new OrchestratorResult(true, confirmMessage, session);
                }
                else
                {
                    _logger.LogInformation("委员会模式: 需求判定为简单, Reason={Reason}, 继续常规规划",
                        classification.Reason);
                }
            }

            // 2. Run Planner (Stage 2)
            var plan = await _architect.PlanAsync(session, userPrompt, ct).ConfigureAwait(false);

            // If the planner returns null or empty, and we don't have a plan yet, initialize empty
            if (plan is { Count: > 0 })
            {
                session.ReplacePlan(plan);
            }

            await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);

            // 计划生成后发布任务清单快照
            if (session.Plan is { Count: > 0 })
            {
                await PublishTaskListAsync(session).ConfigureAwait(false);
            }

            // If we generated a plan, fall through to execute the first task
            // This mimics the "auto-drive" behavior of the original Orchestrator
        }

        // Ensure session plan is loaded if not already (for mocks that update session state)
        if (session.Plan.Count == 0)
        {
            var freshSession = await _sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
            if (freshSession.Plan is { Count: > 0 })
            {
                session.ReplacePlan(freshSession.Plan);
            }
        }

        // 1. 如果指定了 TaskId，直接执行
        if (!string.IsNullOrEmpty(taskId))
        {
            return await ExecuteCodeTaskAsync(sessionId, taskId, userId, workspacePath, ct).ConfigureAwait(false);
        }

        // 2. 如果没指定 TaskId，尝试寻找第一个 Pending 的 Code 任务
        if (session.Plan.Count == 0)
        {
            // Try reload session from store as it might have been updated by other components/mocks
            var refreshed = await _sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
            if (refreshed.Plan.Count > 0)
            {
                session.ReplacePlan(refreshed.Plan);
            }
        }

        await AutoSkipNonCodeTasksAsync(session).ConfigureAwait(false);

        var nextTask = FindNextExecutableTask(session.Plan);
        if (nextTask != null)
        {
            // Transition to Execution Stage if not already
            if (session.CurrentStage < 3)
            {
                session.CurrentStage = 3;
                await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
            }
            return await ExecuteCodeTaskAsync(sessionId, nextTask.Id, userId, workspacePath, ct).ConfigureAwait(false);
        }

        // 3. 区分"计划已全部完成"和"从未生成计划"
        if (session.Plan.Count > 0)
        {
            var total = session.Plan.Count;
            var succeeded = session.Plan.Count(t => t.Status == CodexTaskStatus.Success);
            var succeededWithWarnings = session.Plan.Count(t => t.Status == CodexTaskStatus.CompletedWithWarnings);
            var skipped = session.Plan.Count(t => t.Status == CodexTaskStatus.Skipped);
            var failed = session.Plan.Count(t => t.Status == CodexTaskStatus.Failed);
            var blocked = session.Plan.Count(t => t.Status == CodexTaskStatus.BlockedByDependency);

            // Phase 3: flush any remaining buffered tasks into a structured execution summary
            await _sessionManager.FlushExecutionSummaryAsync(sessionId).ConfigureAwait(false);

            return new OrchestratorResult(true, $"所有任务已处理完毕。成功: {succeeded}, 警告通过: {succeededWithWarnings}, 跳过: {skipped}, 失败: {failed}, 依赖阻塞: {blocked}, 总计: {total}", session);
        }

        return new OrchestratorResult(false, $"当前计划为空，请先调用 {PlanningToolNames.Primary} 工具生成计划。", session);
    }

    private static CodexTask? FindNextExecutableTask(IEnumerable<CodexTask>? plan)
    {
        if (plan == null) return null;
        var taskList = plan.Where(t => t != null).ToList();

        return taskList.FirstOrDefault(t =>
            CodexTaskClassifier.IsCodeExecutionTask(t) &&
            (t.Status == CodexTaskStatus.Pending ||
             t.Status == CodexTaskStatus.Failed ||
             t.Status == CodexTaskStatus.BlockedByDependency) &&
            AreDependenciesSatisfied(t, taskList));
    }

    /// <summary>
    /// BUG-002 fix: Check whether all declared dependencies of a task are satisfied
    /// before allowing it to be scheduled for execution.
    /// </summary>
    private static bool AreDependenciesSatisfied(CodexTask task, List<CodexTask> allTasks)
    {
        return CodexPlanStateGuards.AreDependenciesSatisfied(task, allTasks, out _);
    }

    private async Task AutoSkipNonCodeTasksAsync(CodexSession session)
    {
        if (session.Plan.Count == 0)
        {
            return;
        }

        var skippedAny = false;
        foreach (var task in session.Plan.Where(t =>
                     t != null &&
                     !CodexTaskClassifier.IsCodeExecutionTask(t) &&
                     (t.Status == CodexTaskStatus.Pending || t.Status == CodexTaskStatus.Failed)))
        {
            task.Status = CodexTaskStatus.Skipped;
            task.FinishedAt = DateTime.UtcNow;
            task.ResultNotes = "分析/只读任务不进入 execute_code_task，已由调度器自动跳过。";
            skippedAny = true;
        }

        if (!skippedAny)
        {
            return;
        }

        await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
        await PublishTaskListAsync(session).ConfigureAwait(false);
    }

    private async Task MarkTaskSkippedAsync(CodexSession session, CodexTask task, string reason)
    {
        task.Status = CodexTaskStatus.Skipped;
        task.FinishedAt = DateTime.UtcNow;
        task.ResultNotes = reason;
        await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
        await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Skipped, reason).ConfigureAwait(false);
    }

    private async Task<OrchestratorResult> ExecuteTaskInShadowPathAsync(CodexSession session, CodexTask task, string mainRoot, string baseBranch, string context, CancellationToken ct)
    {
        var enableShadowWorkspace = _configuration.GetValue<bool>("Workspace:EnableShadowWorkspace", true);
        var keepShadowOnFailure = _configuration.GetValue<bool>("Workspace:KeepShadowOnFailure", false);
        var keepShadowOnInfrastructureError = _configuration.GetValue<bool>("Workspace:KeepShadowOnInfrastructureError", true);
        var enableTaskFileScopeGuard = _configuration.GetValue<bool>("Workspace:EnableTaskFileScopeGuard", true);
        var enableTdd = _configuration.GetValue<bool>("Workspace:EnableTdd", true);
        var startMessage = enableShadowWorkspace
            ? $"🚀 [Orchestrator] 正在创建影子工作区：[{task.Title}]..."
            : $"🚀 [Orchestrator] 影子工作区已禁用，直接在主工作区执行：[{task.Title}]...";
        await ReportProgressAsync(startMessage, session, CodexEventType.TaskStarted, task.Id, task).ConfigureAwait(false);

        // [Fix] Save base commit hash before task execution for non-shadow file diff
        var baseCommitHash = await _gitService.GetHeadHashAsync(mainRoot).ConfigureAwait(false);

        string? shadowPath = null;
        if (enableShadowWorkspace)
        {
            shadowPath = await _gitService.CreateShadowWorktreeAsync(mainRoot, task.Id, baseBranch).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(shadowPath))
            {
                await ReportProgressAsync("⚠️ [Orchestrator] 影子工作区创建失败，已回退为主工作区执行。", session, CodexEventType.TaskProgress, task.Id).ConfigureAwait(false);
            }
        }

        var usingShadow = !string.IsNullOrEmpty(shadowPath);
        var workPath = usingShadow ? shadowPath! : mainRoot;
        var shouldCleanupShadow = true;

        CodexTaskClassifier.NormalizeTask(task);
        var contractLintWarnings = CodexTaskClassifier.GetContractLintWarnings(task);
        if (contractLintWarnings.Count > 0)
        {
            StructuredLog.Warning(
                _logger,
                "Task contract lint warnings detected for {TaskId}: {Warnings}",
                task.Id,
                string.Join(" | ", contractLintWarnings));
        }

        var taskFileScope = BuildTaskFileScope(task);
        var taskFileScopeDescriptor = TaskFileScopeGuard.BuildTaskFileScope(task);
        if (enableTaskFileScopeGuard && taskFileScope.HasConstraints)
        {
            await ReportProgressAsync(
                $"🧭 [Scope] 当前任务限制修改文件范围：{string.Join(", ", taskFileScope.AllowedFiles.Take(8))}{(taskFileScope.AllowedFiles.Count > 8 ? " ..." : "")}",
                session,
                CodexEventType.TaskProgress,
                task.Id).ConfigureAwait(false);
        }

        session.ActiveTaskId = task.Id;
        task.Status = CodexTaskStatus.Executing;
        task.StartedAt = DateTime.UtcNow;

        // [Fix] Enforce Execution Stage (3) to hide Planning tools from the Agent
        if (session.CurrentStage != 3)
        {
            session.CurrentStage = 3;
        }

        await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
        await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Executing).ConfigureAwait(false);

        var originalWorkspacePath = session.WorkspacePath;

        try
        {
            session.WorkspacePath = workPath;

            // --- TDD 环节 ---
            if (enableTdd && task.ComplexityLevel >= 2)
            {
                await ReportProgressAsync($"🧪 [TDD] 任务复杂度为 {task.ComplexityLevel}，触发 TDD 流程...", session, CodexEventType.TaskProgress, task.Id).ConfigureAwait(false);
                var testPlan = await _testDesigner.DesignTestsAsync(task, session, ct).ConfigureAwait(false);

                if (testPlan?.TestFiles is { Count: > 0 })
                {
                    await ReportProgressAsync($"🧪 [TDD] 生成了 {testPlan.TestFiles.Count} 个测试文件。", session, CodexEventType.TaskProgress, task.Id).ConfigureAwait(false);
                    foreach (var file in testPlan.TestFiles)
                    {
                        if (file == null) continue;
                        var fullPath = Path.Combine(workPath, file.FilePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                        await File.WriteAllTextAsync(fullPath, file.Content, ct).ConfigureAwait(false);
                    }
                    await _gitService.CommitAsync(workPath, $"test: red tests for {task.Id}").ConfigureAwait(false);
                }
                else
                {
                    await ReportProgressAsync($"⚠️ [TDD] 未生成任何測試文件，跳過測試注入。", session, CodexEventType.TaskProgress, task.Id).ConfigureAwait(false);
                }
            }
            else if (!enableTdd && task.ComplexityLevel >= 2)
            {
                await ReportProgressAsync("🧪 [TDD] 已在配置中禁用，跳过 TDD 流程。", session, CodexEventType.TaskProgress, task.Id).ConfigureAwait(false);
            }

            // --- Self-Healing Loop (Max 3 Attempts: 1 Initial + 2 Repair Rounds) ---
            // Retry policy:
            //   - Security Audit failure  → retry Forge with security feedback, then full pipeline
            //   - Validation failure      → retry Forge with validation feedback, then full pipeline
            //   - Zero Tool Calls         → retry Forge immediately (skip Semantic Diff on retry)
            //   - Hashline Mismatch       → retry Forge with hashline guidance
            //   - Build Verification      → retry Forge with build error feedback
            //   - Kernel Infra Error      → no retry, fail immediately
            const int MaxAttempts = 3;
            ValidationResult? valResult = null;
            var previousRetryReason = RetryReason.None;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (attempt > 1 && previousRetryReason != RetryReason.None)
                {
                    _logger.LogInformation(
                        "Retry decision: session={SessionId} task={TaskId} attempt={Attempt}/{Max} reason={Reason}",
                        session.Id, task.Id, attempt, MaxAttempts, previousRetryReason);
                }

                context = await _sessionManager.GetFullContextAsync(session.Id).ConfigureAwait(false);

                // --- Forge 环节 ---
                var execPrompt = $@"基于背景执行原子任务：\n{context}\n\n[任务] {task.Title}: {task.Description}

# 施工准则
1. **直接修改**：不要浪费时间在反复阅读代码上。如果你已经定位了问题，立即调用 `write_file` 或 `ivilson_smart_patch` 进行修改。
2. **遵守架构**：严格遵守 Clean Architecture 规范。
3. **一次性完成**：尽可能在这一轮内完成所有相关的代码修改。
4. **提交结果**：修改完成后，务必确认修改已保存。";

                if (enableTaskFileScopeGuard && taskFileScope.HasConstraints)
                {
                    execPrompt += $"\n\n# 文件范围约束\n" +
                                  $"允许修改以下已有文件（或其直接对应测试文件）：\n- {string.Join("\n- ", taskFileScope.AllowedFiles)}\n" +
                                  (taskFileScope.AllowedDirectories.Count > 0
                                      ? $"允许在以下目录创建新文件：\n- {string.Join("\n- ", taskFileScope.AllowedDirectories)}\n"
                                      : "") +
                                  (taskFileScope.AllowProjectFileEdits
                                      ? "允许修改相关的 .csproj 项目文件。\n"
                                      : "") +
                                  "若你发现需要改动范围外文件，请停止并在结果中说明，不要擅自扩散修改。";
                }

                if (contractLintWarnings.Count > 0)
                {
                    execPrompt += "\n\n# 任务契约预检提醒\n" +
                                  string.Join("\n", contractLintWarnings.Select((warning, index) => $"{index + 1}. {warning}"));
                }

                // [Fix] Inject previous failure feedback with prefix-based dispatch
                if (!string.IsNullOrEmpty(task.ResultNotes))
                {
                    if (task.ResultNotes.StartsWith("[SECURITY_AUDIT_FAILURE]", StringComparison.Ordinal))
                    {
                        execPrompt += BuildSecurityRepairPrompt(task.ResultNotes);
                    }
                    else if (task.ResultNotes.StartsWith("[ZERO_TOOL_CALLS]", StringComparison.Ordinal))
                    {
                        execPrompt += BuildZeroToolCallsRepairPrompt(task, task.ResultNotes);
                    }
                    else if (task.ResultNotes.StartsWith("[VALIDATION_FAILURE]", StringComparison.Ordinal) ||
                             task.ResultNotes.StartsWith("[VALIDATION FAILURE]", StringComparison.Ordinal) ||
                             task.ResultNotes.StartsWith("[FALLBACK_VALIDATION_FAILED]", StringComparison.Ordinal))
                    {
                        execPrompt += BuildValidationRepairPrompt(task, task.ResultNotes);
                    }
                    else if (task.ResultNotes.StartsWith("[HASHLINE_MISMATCH_FAILURE]", StringComparison.Ordinal))
                    {
                        execPrompt += BuildHashlineMismatchRepairPrompt(task.ResultNotes);
                        // [Fix GPT-Architect-02] HashlineMismatch was the only RetryReason
                        // that never had its previousRetryReason set in any continue path.
                        // This ensures it's tracked for logging purposes.
                        previousRetryReason = RetryReason.HashlineMismatch;
                    }
                    else if (task.ResultNotes.StartsWith("[BUILD_VERIFICATION_FAILURE]", StringComparison.Ordinal))
                    {
                        execPrompt += BuildBuildVerificationRepairPrompt(task, task.ResultNotes);
                    }
                    // Other prefixes (e.g. [KernelInfraError]) are handled by !IsComplete hard-gate
                    // and never reach this point, so no prompt injection needed.
                }

                if (attempt == 1)
                    await ReportProgressAsync($"🛠️ Ivilson-Forge 正在施工（第 {attempt}/{MaxAttempts} 次）...", session, CodexEventType.TaskProgress, task.Id).ConfigureAwait(false);
                else
                    await ReportProgressAsync($"🚑 Ivilson-Forge 正在进行精准修复（第 {attempt}/{MaxAttempts} 次）...", session, CodexEventType.TaskProgress, task.Id).ConfigureAwait(false);

                var execResponse = await RunForgeKernelAsync(session, execPrompt, taskFileScopeDescriptor, ct).ConfigureAwait(false);
                if (execResponse == null)
                {
                    task.RetryCount++;
                    task.ResultNotes = "[KernelInfraError] Forge kernel returned null response.";
                    await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Failed, task.ResultNotes).ConfigureAwait(false);
                    return new OrchestratorResult(false, task.ResultNotes, session);
                }

                // Keep the last response text unless overwritten by failure notes
                if (attempt == 1) task.ResultNotes = execResponse.Text;

                // [Bug-Fix] Zero tool calls detection — must be BEFORE !IsComplete hard-gate
                // When Forge returns pure text with no tool calls, Kernel marks IsComplete=true.
                // We intercept here to enter self-healing loop instead of proceeding to validation.
                if (execResponse.IsComplete && execResponse.TotalToolCalls == 0)
                {
                    _logger.LogWarning(
                        "Forge returned zero tool calls. session={SessionId} task={TaskId} attempt={Attempt}/{MaxRetries}",
                        session.Id, task.Id, attempt, MaxAttempts);

                    // Structured summary for zero-tool-call path (no validator reached)
                    _logger.LogInformation(
                        "Forge attempt summary: session={SessionId} task={TaskId} attempt={Attempt}/{MaxRetries} " +
                        "kernelComplete={IsComplete} totalToolCalls={TotalToolCalls} writeToolCalls={WriteToolCalls} " +
                        "exitReason=ZeroToolCalls",
                        session.Id, task.Id, attempt, MaxAttempts,
                        execResponse.IsComplete, execResponse.TotalToolCalls, execResponse.WriteToolCalls);

                    if (attempt < MaxAttempts)
                    {
                        task.ResultNotes = "[ZERO_TOOL_CALLS] Forge 未执行任何工具调用。你必须使用结构化工具调用（write_file / ivilson_smart_patch）修改代码，纯文本响应不被接受。";
                        await ReportProgressAsync(
                            $"⚠️ Forge 未执行工具调用（第 {attempt}/{MaxAttempts} 次），触发强制修复...",
                            session, CodexEventType.TaskProgress, task.Id).ConfigureAwait(false);
                        previousRetryReason = RetryReason.ZeroToolCalls;
                        continue; // Enter next self-healing iteration
                    }
                    else
                    {
                        task.Status = CodexTaskStatus.Failed;
                        task.FinishedAt = DateTime.UtcNow;
                        task.ResultNotes = "[ZERO_TOOL_CALLS] Forge 连续多次未执行任何工具调用。";
                        await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                        await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Failed, task.ResultNotes).ConfigureAwait(false);
                        return new OrchestratorResult(false, task.ResultNotes, session);
                    }
                }

                // Hard gate: kernel reported incomplete/failed execution (e.g. empty tool calls, transport collapse)
                if (!execResponse.IsComplete)
                {
                    var isInfraKernelFailure = IsInfrastructureKernelFailure(execResponse.Text);
                    task.RetryCount++;
                    task.FinishedAt = DateTime.UtcNow;
                    task.ResultNotes = $"[KernelInfraError] {execResponse.Text}";

                    if (isInfraKernelFailure)
                    {
                        task.Status = CodexTaskStatus.Failed;
                        if (usingShadow && keepShadowOnInfrastructureError)
                        {
                            shouldCleanupShadow = false;
                            await ReportProgressAsync($"🧷 [Infra] 保留影子工作区用于复盘：{workPath}", session, CodexEventType.General, task.Id).ConfigureAwait(false);
                        }

                        await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                        await ReportProgressAsync($"⚠️ 任务 [{task.Title}] 遇到平台级执行异常，已标记为 Failed，避免继续停留在 Pending。", session, CodexEventType.TaskFailed, task.Id).ConfigureAwait(false);
                        await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Failed, "Kernel infrastructure failure").ConfigureAwait(false);
                        return new OrchestratorResult(false, $"执行基础设施异常：{execResponse.Text}", session);
                    }

                    task.Status = CodexTaskStatus.Failed;
                    if (usingShadow && keepShadowOnFailure)
                    {
                        shouldCleanupShadow = false;
                        await ReportProgressAsync($"🧷 [Debug] 保留失败任务影子工作区：{workPath}", session, CodexEventType.General, task.Id).ConfigureAwait(false);
                    }

                    await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                    await ReportProgressAsync($"❌ 任务 [{task.Title}] 内核执行失败，已中止当前任务。", session, CodexEventType.TaskFailed, task.Id).ConfigureAwait(false);
                    await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Failed, "Kernel execution incomplete").ConfigureAwait(false);

                    return new OrchestratorResult(false, $"内核执行失败：{execResponse.Text}", session);
                }

                // --- Semantic Diff 环节 (Level 7) ---
                await ReportProgressAsync($"👁️ Ivilson-Vision 正在分析語義變更...", session, CodexEventType.TaskProgress, task.Id).ConfigureAwait(false);

                // [Fix GPT-Architect-01] Use actual git-changed files for security audit,
                // NOT semantic spillover files. ImpactedFiles includes untouched files that
                // are merely affected by the change — those should NOT block the audit gate.
                IEnumerable<string> changedFilesForAudit = Enumerable.Empty<string>();
                try
                {
                    // Primary: working-tree diff against base commit (includes Forge's uncommitted changes).
                    // GetWorkingTreeChangedFilesAsync uses `git diff --name-status {base}` which compares
                    // the working tree (staged + unstaged) against the base, so files modified by Forge
                    // but not yet committed are correctly included.
                    if (!string.IsNullOrEmpty(baseCommitHash))
                    {
                        var gitChanges = await _gitService.GetWorkingTreeChangedFilesAsync(workPath, baseCommitHash).ConfigureAwait(false);
                        changedFilesForAudit = gitChanges?.Select(c => c.FilePath).ToList() ?? Enumerable.Empty<string>();
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
                {
                    StructuredLog.Warning(_logger, ex, "获取 git 变更文件失败，审计将使用全量扫描。");
                }

                // Secondary: still run semantic diff for impact awareness (logging only)
                try
                {
                    var diffResult = await _semanticDiff.AnalyzeDiffAsync(mainRoot, workPath, ct).ConfigureAwait(false);
                    if (diffResult != null && diffResult.HasChanges)
                    {
                        await ReportProgressAsync($"🔍 檢測到語義變更：{string.Join(", ", diffResult.ChangedSymbols.Take(3))}...", session, CodexEventType.TaskProgress, task.Id, diffResult).ConfigureAwait(false);
                        if (diffResult.ImpactedFiles is { Count: > 0 })
                        {
                            await ReportProgressAsync($"⚠️ 變更波及了 {diffResult.ImpactedFiles.Count} 個外部文件。", session, CodexEventType.TaskProgress, task.Id).ConfigureAwait(false);
                        }
                    }
                }
                catch (IOException ex)
                {
                    StructuredLog.Warning(_logger, ex, "語義差異分析失敗，跳過。");
                }
                catch (InvalidOperationException ex)
                {
                    StructuredLog.Warning(_logger, ex, "語義差異分析失敗，跳過。");
                }
                catch (HttpRequestException ex)
                {
                    StructuredLog.Warning(_logger, ex, "語義差異分析失敗，跳過。");
                }

                // --- Level 8: Security Audit ---
                var enableAuditor = _configuration.GetValue<bool>("Workspace:EnableAuditor", true);

                SecurityAuditResult auditResult;
                if (enableAuditor)
                {
                    await ReportProgressAsync($"🛡️ Ivilson-Guard 正在进行全域安全扫描...", session, CodexEventType.SecurityAudit, task.Id).ConfigureAwait(false);
                    // [Fix] Incremental Audit: specificially check only changed files to avoid legacy debt blocking
                    auditResult = await _securityAuditor.AuditAsync(session, workPath, ct, changedFilesForAudit).ConfigureAwait(false);
                    if (auditResult == null)
                    {
                        StructuredLog.Error(_logger, "Security auditor returned null result. Treating as failure (fail-closed).");
                        auditResult = new SecurityAuditResult(false, "Security auditor returned null result", new List<string> { "[System] Security auditor returned null result." }, "");
                    }
                }
                else
                {
                    await ReportProgressAsync($"🛡️ [Config] Security Audit is DISABLED. Skipping...", session, CodexEventType.General, task.Id).ConfigureAwait(false);
                    auditResult = new SecurityAuditResult(true, "Security Audit Disabled by Configuration", new List<string>(), "");
                }

                // 结构化审计归属：
                // - Risks: 当前任务阻塞项
                // - DeferredRisks: 归属后续步骤，当前任务非阻塞
                // - LegacyRisks: 存量问题，当前任务非阻塞
                var hasBlockingRisks = auditResult.Risks != null && auditResult.Risks.Any();
                var hasDeferredRisks = auditResult.DeferredRisks != null && auditResult.DeferredRisks.Any();
                var hasLegacyRisks = auditResult.LegacyRisks != null && auditResult.LegacyRisks.Any();

                // 容错归一化：若模型给出 IsPassed=false 但没有阻塞项，仅有 deferred/legacy，则按当前步骤通过处理。
                if (!auditResult.IsPassed && !hasBlockingRisks && (hasDeferredRisks || hasLegacyRisks))
                {
                    StructuredLog.Warning(_logger, "Security audit returned IsPassed=false without blocking risks; normalizing to pass for current-step gate. Task={TaskId}", task.Id);
                    auditResult = auditResult with
                    {
                        IsPassed = true,
                        Summary = string.IsNullOrWhiteSpace(auditResult.Summary)
                            ? "Step-scoped audit passed with deferred/legacy findings."
                            : $"{auditResult.Summary} (normalized to step-scoped pass: no blocking risks in current step)"
                    };
                }

                // [Fix] Handle Deferred Risks: Record but do not block
                if (auditResult.DeferredRisks != null && auditResult.DeferredRisks.Any())
                {
                    var deferredMsg = string.Join("\n", auditResult.DeferredRisks.Select(r => $"- {r}"));
                    session.ProjectSummary += $"\n\n## ⏭️ Deferred Security Findings (Recorded {DateTime.UtcNow:yyyy-MM-dd})\n{deferredMsg}";
                    await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                    await ReportProgressAsync($"📝 [Note] 记录了 {auditResult.DeferredRisks.Count} 个后续步骤安全项（当前步骤不阻塞）。", session, CodexEventType.General, task.Id).ConfigureAwait(false);

                    if (auditResult.IsPassed)
                    {
                        task.Status = CodexTaskStatus.CompletedWithWarnings;
                    }
                }

                // [Fix] Handle Legacy Debt: Record but do not block
                if (auditResult.LegacyRisks != null && auditResult.LegacyRisks.Any())
                {
                    var legacyMsg = string.Join("\n", auditResult.LegacyRisks.Select(r => $"- {r}"));
                    session.ProjectSummary += $"\n\n## 🛡️ Known Security Debt (Recorded {DateTime.UtcNow:yyyy-MM-dd})\n{legacyMsg}";
                    await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                    await ReportProgressAsync($"📝 [Note] 记录了 {auditResult.LegacyRisks.Count} 个存量安全债务到项目摘要中。", session, CodexEventType.General, task.Id).ConfigureAwait(false);

                    // [Feature] Visualizing Legacy Debt
                    // If task passed but has legacy risks, mark as CompletedWithWarnings
                    if (auditResult.IsPassed)
                    {
                        task.Status = CodexTaskStatus.CompletedWithWarnings;
                    }
                }

                if (!auditResult.IsPassed)
                {
                    // Detect infrastructure failures (e.g. parse failure) — risks marked with [System]
                    // must NOT trigger the self-healing Forge loop; they indicate auditor/platform issues,
                    // not actual code vulnerabilities.
                    var isInfraFailure = auditResult.Risks != null &&
                        auditResult.Risks.Any(r => r.StartsWith("[System]", StringComparison.OrdinalIgnoreCase));

                    // Failed
                    var failureMsg = isInfraFailure
                        ? $"[SECURITY_AUDITOR_INFRA_FAILURE] Security Audit Failed: {auditResult.Summary}"
                        : $"[SECURITY_AUDIT_FAILURE] Security Audit Failed: {auditResult.Summary}";

                    // [Fix] Check if we have detailed risks to provide precise feedback
                    if (auditResult.Risks != null && auditResult.Risks.Any())
                    {
                        var details = string.Join("\n- ", auditResult.Risks);
                        failureMsg += $"\n\nDetails:\n- {details}";
                    }

                    task.ResultNotes = failureMsg;
                    await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);

                    // Structured summary for security audit failure path (no validator reached)
                    _logger.LogInformation(
                        "Forge attempt summary: session={SessionId} task={TaskId} attempt={Attempt}/{MaxRetries} " +
                        "kernelComplete={IsComplete} totalToolCalls={TotalToolCalls} writeToolCalls={WriteToolCalls} " +
                        "exitReason=SecurityAuditFailure auditPassed={AuditPassed} isInfraFailure={IsInfraFailure}",
                        session.Id, task.Id, attempt, MaxAttempts,
                        execResponse.IsComplete, execResponse.TotalToolCalls, execResponse.WriteToolCalls,
                        auditResult.IsPassed, isInfraFailure);

                    await ReportProgressAsync($"🚫 [Security] 安全审计未通过：{auditResult.Summary}", session, CodexEventType.GuardrailBlocked, task.Id, auditResult).ConfigureAwait(false);

                    // Infra failures bail immediately — no self-healing loop for platform issues.
                    if (!isInfraFailure && attempt < MaxAttempts)
                    {
                        await ReportProgressAsync($"🔄 触发自愈流程：正在将漏洞定位信息发送给 Agent 进行修复...", session, CodexEventType.TaskProgress, task.Id).ConfigureAwait(false);
                        previousRetryReason = RetryReason.SecurityAuditFailure;
                        continue; // Retry loop
                    }
                    else
                    {
                        // Either infra failure (bail immediately) or exhausted retries
                        task.Status = CodexTaskStatus.Failed;
                        task.FinishedAt = DateTime.UtcNow;
                        if (usingShadow && keepShadowOnFailure)
                        {
                            shouldCleanupShadow = false;
                            await ReportProgressAsync($"🧷 [Debug] 安全审计失败，保留影子工作区：{workPath}", session, CodexEventType.General, task.Id).ConfigureAwait(false);
                        }
                        await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                        await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Failed, auditResult.Summary).ConfigureAwait(false);
                        var finalMsg = isInfraFailure
                            ? $"Security Audit Failed: auditor infrastructure failure — {auditResult.Summary}"
                            : $"Security Audit Failed after repair attempt: {auditResult.Summary}";
                        return new OrchestratorResult(false, finalMsg, session);
                    }
                }

                // Seed structured evidence BEFORE validation so deterministic fallback
                // does not rely solely on log slicing to detect real writes/tests.
                try
                {
                    var preValidationChanges = Array.Empty<CodexFlow.Core.Abstractions.GitFileChange>();
                    if (!string.IsNullOrEmpty(baseCommitHash))
                    {
                        var changeSource = usingShadow ? workPath : mainRoot;
                        preValidationChanges = (await _gitService.GetWorkingTreeChangedFilesAsync(changeSource, baseCommitHash).ConfigureAwait(false))
                            ?.ToArray()
                            ?? Array.Empty<CodexFlow.Core.Abstractions.GitFileChange>();
                    }

                    task.ExecutionEvidence = new TaskExecutionEvidenceResult(
                        ChangedFiles: preValidationChanges.Where(c => c.Status == "M").Select(c => c.FilePath).ToList(),
                        CreatedFiles: preValidationChanges.Where(c => c.Status == "A").Select(c => c.FilePath).ToList(),
                        DeletedFiles: preValidationChanges.Where(c => c.Status == "D").Select(c => c.FilePath).ToList(),
                        HasSuccessfulBuildEvidence: false,
                        HasSuccessfulTestEvidence: false,
                        AssertionResults: task.ExecutionEvidence?.AssertionResults ?? Array.Empty<string>(),
                        TotalToolCalls: execResponse.TotalToolCalls,
                        WriteToolCalls: execResponse.WriteToolCalls);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
                {
                    StructuredLog.Warning(_logger, ex, "Failed to seed pre-validation execution evidence for task {TaskId}", task.Id);
                }

                // --- Sentry 环节 (Moved into Loop) ---
                var buildVerificationResult = await RunBuildVerificationAsync(session, task, workPath, ct).ConfigureAwait(false);
                if (buildVerificationResult.IsRequired)
                {
                    task.ExecutionEvidence = (task.ExecutionEvidence ?? new TaskExecutionEvidenceResult(
                        ChangedFiles: Array.Empty<string>(),
                        CreatedFiles: Array.Empty<string>(),
                        DeletedFiles: Array.Empty<string>(),
                        HasSuccessfulBuildEvidence: false,
                        HasSuccessfulTestEvidence: false,
                        AssertionResults: Array.Empty<string>(),
                        TotalToolCalls: execResponse.TotalToolCalls,
                        WriteToolCalls: execResponse.WriteToolCalls)) with
                    {
                        HasSuccessfulBuildEvidence = buildVerificationResult.Success
                    };
                }

                if (buildVerificationResult.IsRequired && !buildVerificationResult.Success)
                {
                    var buildFailureMessage =
                        $"[BUILD_VERIFICATION_FAILURE] dotnet build 验证失败：{buildVerificationResult.Summary}";
                    if (!string.IsNullOrWhiteSpace(buildVerificationResult.OutputSummary))
                    {
                        buildFailureMessage += $"\n\n构建输出摘要：\n{buildVerificationResult.OutputSummary}";
                    }

                    task.ResultNotes = buildFailureMessage;
                    await ReportProgressAsync(
                        $"⚠️ 任务 [{task.Title}] 在验证前编译检查失败：{buildVerificationResult.Summary}",
                        session,
                        CodexEventType.TaskProgress,
                        task.Id).ConfigureAwait(false);

                    if (attempt < MaxAttempts)
                    {
                        previousRetryReason = RetryReason.BuildVerificationFailure;
                        continue;
                    }

                    task.Status = CodexTaskStatus.Failed;
                    task.FinishedAt = DateTime.UtcNow;
                    await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                    await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Failed, buildVerificationResult.Summary).ConfigureAwait(false);
                    return new OrchestratorResult(false, $"编译验证失败：{buildVerificationResult.Summary}", session);
                }

                await ReportProgressAsync($"🧪 Ivilson-Sentry 正在验证...", session, CodexEventType.TaskProgress, task.Id).ConfigureAwait(false);
                if (_validator == null) throw new InvalidOperationException("Validator service is not initialized.");
                var specPrecheckIssues = CodexTaskClassifier.EvaluateExecutionSpecConformance(workPath, task);
                if (specPrecheckIssues.Count > 0)
                {
                    StructuredLog.Warning(
                        _logger,
                        "Execution spec precheck failed for task {TaskId}: {Issues}",
                        task.Id,
                        string.Join(" | ", specPrecheckIssues));

                    valResult = DefaultCodexValidator.AttachChecklistEvaluation(
                        task,
                        workPath,
                        new ValidationResult(
                            false,
                            "执行期契约预检未通过。",
                            specPrecheckIssues));
                }
                else
                {
                    valResult = await _validator.ValidateAsync(session, task, ct).ConfigureAwait(false);
                }

                // Phase 3: orchestrator owns checklist state. Always merge the latest
                // validator evaluation before any retry/finalization logic decides next steps.
                CodexTaskProgressMerger.MergeChecklistEvaluation(task, valResult?.ChecklistEvaluation);

                // Structured per-attempt summary log
                _logger.LogInformation(
                    "Forge attempt summary: session={SessionId} task={TaskId} attempt={Attempt}/{MaxRetries} " +
                    "kernelComplete={IsComplete} totalToolCalls={TotalToolCalls} writeToolCalls={WriteToolCalls} " +
                    "validatorFallback={IsFallback} validationPassed={ValidationPassed}",
                    session.Id, task.Id, attempt, MaxAttempts,
                    execResponse.IsComplete, execResponse.TotalToolCalls, execResponse.WriteToolCalls,
                    valResult?.IsFallback ?? false, valResult?.Success ?? false);

                if (valResult is { IsInfrastructureError: true })
                {
                    task.Status = CodexTaskStatus.Failed;
                    task.RetryCount++;
                    task.ResultNotes = $"[ValidatorInfraError] {valResult.Summary}";
                    if (usingShadow && keepShadowOnInfrastructureError)
                    {
                        shouldCleanupShadow = false;
                        await ReportProgressAsync($"🧷 [Infra] 验证器异常，保留影子工作区用于复盘：{workPath}", session, CodexEventType.General, task.Id).ConfigureAwait(false);
                    }

                    await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                    await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Failed, valResult.Summary).ConfigureAwait(false);
                    return new OrchestratorResult(false, $"验证基础设施异常：{valResult.Summary}", session);
                }

                if (valResult != null && valResult.Success)
                {
                    // [Bug-03 Fix] Fallback validation
                    if (valResult.IsFallback || valResult.HasQualityWarnings)
                    {
                        task.Status = CodexTaskStatus.CompletedWithWarnings;
                        if (valResult.WarningDetails is { Count: > 0 })
                        {
                            task.ResultNotes = $"[QualityWarnings] {string.Join(" | ", valResult.WarningDetails.Take(3))}";
                        }
                        if (valResult.IsFallback)
                        {
                            task.ResultNotes = string.IsNullOrWhiteSpace(task.ResultNotes)
                                ? "[FallbackValidation] LLM verifier did not return a valid verdict. Passed via deterministic fallback rules."
                                : $"[FallbackValidation] {task.ResultNotes}";
                            // BUG-002 fix: Explicit fallback warning for downstream dependency tracking
                            StructuredLog.Warning(_logger,
                                "Task {TaskId} passed via FALLBACK validation. Downstream dependency satisfaction may be unreliable. FallbackReason: {Summary}",
                                task.Id, valResult.Summary);
                        }

                        await ReportProgressAsync(
                            $"⚠️ 任务 [{task.Title}] 验证通过，但质量门禁检测到 {valResult.WarningDetails?.Count ?? 0} 条 warning。" +
                            (valResult.IsFallback ? " [验证器降级通过]" : ""),
                            session,
                            CodexEventType.General,
                            task.Id,
                            new { warnings = valResult.WarningDetails, isFallback = valResult.IsFallback }).ConfigureAwait(false);
                    }
                    else if (task.Status != CodexTaskStatus.CompletedWithWarnings)
                    {
                        task.Status = CodexTaskStatus.Success;
                    }
                    
                    // Success!
                    break;
                }
                else
                {
                    var failReason = valResult?.Summary ?? "Unknown validation failure";
                    // [Bug-002 fix] Include detailed Issues list so Forge can target specific failures
                    var issuesList = valResult?.Issues != null && valResult.Issues.Count > 0
                        ? "\n\n具体失败项：\n" + string.Join("\n", valResult.Issues.Select((s, i) => $"{i + 1}. {s}"))
                        : "";
                    
                    // --- Causal Healing Logic (Level 7) ---
                    var healingHint = "";
                    try
                    {
                        var diff = await _semanticDiff.AnalyzeDiffAsync(mainRoot, workPath, ct).ConfigureAwait(false);
                        IEnumerable<string> impactedFiles = diff == null ? Array.Empty<string>() : diff.ImpactedFiles;
                        if (impactedFiles.Any())
                        {
                            healingHint = $"\n\n【語義自愈線索】檢測到你的修改影響了以下文件，這可能是導致報錯的原因：\n" +
                                          string.Join("\n", impactedFiles.Select(f => $"- {f}"));
                        }
                    }
                    catch (IOException) { }
                    catch (InvalidOperationException) { }
                    catch (HttpRequestException) { }

                    var fullFailureMsg = $"❌ 任务 [{task.Title}] 验证失败：{failReason}{issuesList}{healingHint}";
                    await ReportProgressAsync(fullFailureMsg, session, CodexEventType.TaskFailed, task.Id).ConfigureAwait(false);
                    
                    if (attempt < MaxAttempts)
                    {
                        task.ResultNotes = $"[VALIDATION FAILURE]\n{failReason}{issuesList}{healingHint}";
                        await ReportProgressAsync($"🔄 触发自愈流程：正在将验证失败信息反馈给 Agent 进行修正...", session, CodexEventType.TaskProgress, task.Id).ConfigureAwait(false);
                        previousRetryReason = RetryReason.ValidationFailure;
                        // Continue to next attempt
                    }
                    else
                    {
                        task.Status = CodexTaskStatus.Failed;
                        task.FinishedAt = DateTime.UtcNow;
                        task.ResultNotes = $"{failReason}{issuesList}";
                        if (usingShadow && keepShadowOnFailure)
                        {
                            shouldCleanupShadow = false;
                        }
                        await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                        await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Failed, failReason).ConfigureAwait(false);
                        return new OrchestratorResult(false, $"验证失败：{failReason}{healingHint}", session);
                    }
                }
            }

            // --- Post-Loop Finalization ---
            session.WorkspacePath = originalWorkspacePath;
            task.FinishedAt = DateTime.UtcNow;

                // BUG-002 fix: Collect structured execution evidence for dependency checking
                try
                {
                    CodexTaskClassifier.NormalizeTask(task);
                    var assertionResults = new List<string>();
                    var allAssertions = task.RequiredArtifacts.Concat(task.ForbiddenStates).ToList();
                    if (allAssertions.Count > 0 && !string.IsNullOrEmpty(mainRoot))
                    {
                        var assertionIssues = DefaultCodexValidator.EvaluateArtifactAssertions(mainRoot, allAssertions);
                        assertionResults.AddRange(assertionIssues.Count == 0
                            ? new[] { $"All {allAssertions.Count} artifact assertions passed." }
                            : assertionIssues);
                    }
                    task.ExecutionEvidence = (task.ExecutionEvidence ?? new TaskExecutionEvidenceResult(
                        ChangedFiles: Array.Empty<string>(),
                        CreatedFiles: Array.Empty<string>(),
                        DeletedFiles: Array.Empty<string>(),
                        HasSuccessfulBuildEvidence: false,
                        HasSuccessfulTestEvidence: false,
                        AssertionResults: Array.Empty<string>(),
                        TotalToolCalls: 0,
                        WriteToolCalls: 0)) with
                    {
                        HasSuccessfulBuildEvidence = task.ExecutionEvidence?.HasSuccessfulBuildEvidence == true ||
                                                     (valResult?.Success == true && task.ResultNotes?.Contains("build", StringComparison.OrdinalIgnoreCase) == true),
                        HasSuccessfulTestEvidence = task.ExecutionEvidence?.HasSuccessfulTestEvidence == true ||
                                                    (valResult?.Success == true && task.ResultNotes?.Contains("test", StringComparison.OrdinalIgnoreCase) == true),
                        AssertionResults = assertionResults
                    };
                }
                catch (Exception ex)
                {
                    StructuredLog.Warning(_logger, ex, "Failed to collect execution evidence for task {TaskId}", task.Id);
                }

                // [Fix] Non-shadow mode: run scope guard BEFORE commit to prevent
                // irreversible commits landing in main when scope is violated.
                IReadOnlyList<CodexFlow.Core.Abstractions.GitFileChange> mergedFileChanges =
                    Array.Empty<CodexFlow.Core.Abstractions.GitFileChange>();

                if (!usingShadow && enableTaskFileScopeGuard && taskFileScope.HasConstraints && !string.IsNullOrEmpty(baseCommitHash))
                {
                    try
                    {
                        mergedFileChanges = await _gitService.GetChangedFilesAsync(mainRoot, baseCommitHash, "HEAD").ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
                    {
                        StructuredLog.Warning(_logger, ex, "非影子模式 scope guard 前置检查：获取变更文件列表失败: {TaskId}", task.Id);
                    }

                    if (mergedFileChanges.Count > 0)
                    {
                        var outOfScope = GetOutOfScopeChanges(mergedFileChanges, taskFileScope);
                        if (outOfScope.Count > 0)
                        {
                            var reason = $"检测到超出当前任务范围的文件改动：{string.Join(", ", outOfScope.Take(8))}{(outOfScope.Count > 8 ? " ..." : "")}";

                            // Roll back all agent commits to the base commit before marking failure
                            var resetOk = await _gitService.ResetToCommitAsync(mainRoot, baseCommitHash).ConfigureAwait(false);
                            if (resetOk)
                            {
                                await ReportProgressAsync($"⏪ [Scope Guard] 已回滚主工作区到任务执行前状态 ({baseCommitHash[..8]})。", session, CodexEventType.TaskProgress, task.Id).ConfigureAwait(false);
                            }
                            else
                            {
                                reason += " (警告：回滚主工作区失败，请手动检查)";
                                StructuredLog.Warning(_logger, "非影子模式 scope guard 回滚失败: {TaskId}, baseCommit={BaseCommit}", task.Id, baseCommitHash);
                            }

                            task.Status = CodexTaskStatus.Failed;
                            task.RetryCount++;
                            task.ResultNotes = reason;
                            await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                            await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Failed, reason).ConfigureAwait(false);
                            return new OrchestratorResult(false, reason, session);
                        }
                    }
                }

                await _gitService.CommitAsync(workPath, $"feat: complete {task.Id}").ConfigureAwait(false);

                if (usingShadow)
                {
                    // 在合并前先获取影子分支相对于主分支的变更文件列表
                    try
                    {
                        mergedFileChanges = await _gitService.GetChangedFilesAsync(mainRoot, baseBranch, $"task-{task.Id}").ConfigureAwait(false);
                    }
                    catch (IOException ex)
                    {
                        StructuredLog.Warning(_logger, ex, "获取变更文件列表失败: {TaskId}", task.Id);
                    }
                    catch (InvalidOperationException ex)
                    {
                        StructuredLog.Warning(_logger, ex, "获取变更文件列表失败: {TaskId}", task.Id);
                    }
                    catch (TimeoutException ex)
                    {
                        StructuredLog.Warning(_logger, ex, "获取变更文件列表失败: {TaskId}", task.Id);
                    }

                    if (enableTaskFileScopeGuard && taskFileScope.HasConstraints && mergedFileChanges.Count > 0)
                    {
                        var outOfScope = GetOutOfScopeChanges(mergedFileChanges, taskFileScope);
                        if (outOfScope.Count > 0)
                        {
                            var reason = $"检测到超出当前任务范围的文件改动：{string.Join(", ", outOfScope.Take(8))}{(outOfScope.Count > 8 ? " ..." : "")}";
                            task.Status = CodexTaskStatus.Failed;
                            task.RetryCount++;
                            task.ResultNotes = reason;
                            if (keepShadowOnFailure)
                            {
                                shouldCleanupShadow = false;
                                await ReportProgressAsync($"🧷 [Debug] 已保留影子工作区：{workPath}", session, CodexEventType.General, task.Id).ConfigureAwait(false);
                            }

                            await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                            await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Failed, reason).ConfigureAwait(false);
                            return new OrchestratorResult(false, reason, session);
                        }
                    }

                    // [Level 8] Precise Diff Anchoring: Capture state exactly before and after merge
                    var preMergeHash = await _gitService.GetHeadHashAsync(mainRoot).ConfigureAwait(false);

                    var syncOk = await _gitService.MergeAsync(mainRoot, $"task-{task.Id}", baseBranch).ConfigureAwait(false);
                    if (!syncOk)
                    {
                        task.Status = CodexTaskStatus.Failed;
                        task.RetryCount++;
                        task.ResultNotes = $"将影子分支 task-{task.Id} 同步到 {baseBranch} 失败。";
                        if (keepShadowOnFailure)
                        {
                            shouldCleanupShadow = false;
                            await ReportProgressAsync($"🧷 [Debug] 合并失败，保留影子工作区：{workPath}", session, CodexEventType.General, task.Id).ConfigureAwait(false);
                        }
                        await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                        await ReportProgressAsync($"❌ 任务 [{task.Title}] 同步主分支失败，已中止。", session, CodexEventType.TaskFailed, task.Id).ConfigureAwait(false);
                        await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Failed, task.ResultNotes).ConfigureAwait(false);
                        return new OrchestratorResult(false, task.ResultNotes, session);
                    }

                    var postMergeHash = await _gitService.GetHeadHashAsync(mainRoot).ConfigureAwait(false);

                    // Collect changes introduced ONLY by this merge
                    if (!string.IsNullOrEmpty(preMergeHash) && !string.IsNullOrEmpty(postMergeHash) && preMergeHash != postMergeHash)
                    {
                        try
                        {
                            mergedFileChanges = await _gitService.GetChangedFilesAsync(mainRoot, preMergeHash, postMergeHash).ConfigureAwait(false);
                            StructuredLog.Information(_logger, "Precise diff collected {Count} files between {Pre} and {Post}.", mergedFileChanges.Count, preMergeHash, postMergeHash);
                        }
                        catch (IOException ex)
                        {
                            StructuredLog.Warning(_logger, ex, "基于精确 Hash 区间获取变更失败，准备回退策略。");
                        }
                        catch (InvalidOperationException ex)
                        {
                            StructuredLog.Warning(_logger, ex, "基于精确 Hash 区间获取变更失败，准备回退策略。");
                        }
                        catch (TimeoutException ex)
                        {
                            StructuredLog.Warning(_logger, ex, "基于精确 Hash 区间获取变更失败，准备回退策略。");
                        }
                    }

                    // [Fallback] If precise diff failed or returned empty (e.g. fast-forward with no changes), use base commit snapshot.
                    if (mergedFileChanges.Count == 0 && !string.IsNullOrEmpty(baseCommitHash))
                    {
                        try
                        {
                            var fallbackChanges = await _gitService.GetChangedFilesAsync(mainRoot, baseCommitHash, "HEAD").ConfigureAwait(false);
                            if (fallbackChanges.Count > 0)
                            {
                                mergedFileChanges = fallbackChanges;
                            }
                        }
                        catch (IOException ex)
                        {
                            StructuredLog.Warning(_logger, ex, "基于基线提交回退获取变更文件失败: {TaskId}", task.Id);
                        }
                        catch (InvalidOperationException ex)
                        {
                            StructuredLog.Warning(_logger, ex, "基于基线提交回退获取变更文件失败: {TaskId}", task.Id);
                        }
                        catch (TimeoutException ex)
                        {
                            StructuredLog.Warning(_logger, ex, "基于基线提交回退获取变更文件失败: {TaskId}", task.Id);
                        }
                    }
                }

                // Phase 3: buffer this task for execution summary generation
                // NOTE: Must be AFTER scope guard and merge succeed to avoid polluting
                // execution summary with tasks that will be marked Failed below.

                // BUG-002 fix: Finalize execution evidence with actual file changes
                try
                {
                    if (task.ExecutionEvidence != null && mergedFileChanges.Count > 0)
                    {
                        task.ExecutionEvidence = task.ExecutionEvidence with
                        {
                            ChangedFiles = mergedFileChanges.Where(c => c.Status == "M").Select(c => c.FilePath).ToList(),
                            CreatedFiles = mergedFileChanges.Where(c => c.Status == "A").Select(c => c.FilePath).ToList(),
                            DeletedFiles = mergedFileChanges.Where(c => c.Status == "D").Select(c => c.FilePath).ToList()
                        };
                    }
                }
                catch (Exception ex)
                {
                    StructuredLog.Warning(_logger, ex, "Failed to finalize execution evidence with file changes for task {TaskId}", task.Id);
                }

                await _sessionManager.RecordTaskCompletedAsync(
                    session.Id,
                    $"[{task.Id}] {task.Title}").ConfigureAwait(false);

                // [Level 8] Auto-Push Policy Enforcement (as per MEMORY.md)
                // MANDATORY: Every time a problem is solved and tests pass, commit and push.
                var autoPushEnabled = _configuration.GetValue<bool>("Git:AutoPush", true);
                var planFullyCompleted = IsPlanFullyCompletedForPush(session);

                if (autoPushEnabled && planFullyCompleted)
                {
                    await ReportProgressAsync($"📤 [Policy] 检测到计划已完成，正在执行强制远程推送...", session, CodexEventType.General, task.Id).ConfigureAwait(false);
                    var pushBranch = BuildIcodexBranchName(session.Id, task.Id);
                    var pushOk = await _gitService.PushAsync(mainRoot, pushBranch).ConfigureAwait(false);
                    if (pushOk)
                    {
                        await ReportProgressAsync($"✅ 代码已成功推送到远程分支：{pushBranch}", session, CodexEventType.General, task.Id).ConfigureAwait(false);
                    }
                    else
                    {
                        StructuredLog.Warning(_logger, "Auto-push failed for branch {Branch}.", pushBranch);
                    }
                }
                else if (planFullyCompleted)
                {
                    // Fallback to manual consent logic if auto-push is disabled in config
                    var pushApproved = session.Metadata.TryGetValue(GitPushConsentOnceKey, out var consent)
                        && string.Equals(consent, "true", StringComparison.OrdinalIgnoreCase);

                    if (pushApproved)
                    {
                        var pushBranch = BuildIcodexBranchName(session.Id, task.Id);
                        await _gitService.PushAsync(mainRoot, pushBranch).ConfigureAwait(false);
                        session.Metadata[GitPushConsentOnceKey] = "false";
                        await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                    }
                }

                if (planFullyCompleted &&
                    !usingShadow &&
                    mergedFileChanges.Count == 0 &&
                    !string.IsNullOrEmpty(baseCommitHash))
                {
                    try
                    {
                        var finalChanges = await _gitService.GetChangedFilesAsync(mainRoot, baseCommitHash, "HEAD").ConfigureAwait(false);
                        if (finalChanges.Count > 0)
                        {
                            mergedFileChanges = finalChanges;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
                    {
                        StructuredLog.Warning(_logger, ex, "非影子模式文档同步差异采集失败: {TaskId}", task.Id);
                    }
                }

                if (planFullyCompleted)
                {
                    await UpdateDocumentationSyncStatus(session, mergedFileChanges).ConfigureAwait(false);
                }

                // [Bug-03 Fix] Distinguish fallback-pass from real LLM validation pass in the audit trail.
                var statusLabel = task.Status == CodexTaskStatus.CompletedWithWarnings ? "⚠️" : "✅";
                if (valResult?.IsFallback == true)
                {
                    StructuredLog.Warning(_logger,
                        "⚠️ [Validator-Fallback] Task [{TaskId}] completed via deterministic fallback validation " +
                        "(LLM validator did not return a verdict). Summary: {Summary}. " +
                        "This is recorded as IsFallback=true — treat as PassedWithFallback, not a normal pass.",
                        task.Id, valResult.Summary);
                    statusLabel = "⚠️[降级验证]";
                }

                var mergePayload = mergedFileChanges.Count > 0
                    ? (object)new
                    {
                        mergedFiles = mergedFileChanges.Select(f => new { path = Path.GetRelativePath(session.WorkspacePath, Path.Combine(mainRoot, f.FilePath)).Replace('\\', '/'), status = f.Status, additions = f.Additions, deletions = f.Deletions }),
                        taskTitle = task.Title,
                        taskId = task.Id
                    }
                    : null;
                await ReportProgressAsync($"{statusLabel} 任务 [{task.Title}] 已完成并合入主分支。", session, CodexEventType.TaskCompleted, task.Id, mergePayload).ConfigureAwait(false);
                // [Fix] Persist session BEFORE publishing snapshot, so next task loads correct state
                await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                await PublishTaskListAsync(session, task.Id, task.Status).ConfigureAwait(false);
                return new OrchestratorResult(true, "执行并验证成功", session, task.Status);
        }
        catch (IOException ex)
        {
            task.Status = CodexTaskStatus.Failed;
            if (usingShadow && keepShadowOnFailure)
            {
                shouldCleanupShadow = false;
                await ReportProgressAsync($"🧷 [Debug] 异常失败，保留影子工作区：{workPath}", session, CodexEventType.General, task.Id).ConfigureAwait(false);
            }
            StructuredLog.Error(_logger, ex, "Execution failed");
            await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Failed, ex.Message).ConfigureAwait(false);
            return new OrchestratorResult(false, $"异常：{ex.Message}", session);
        }
        catch (InvalidOperationException ex)
        {
            task.Status = CodexTaskStatus.Failed;
            if (usingShadow && keepShadowOnFailure)
            {
                shouldCleanupShadow = false;
                await ReportProgressAsync($"🧷 [Debug] 异常失败，保留影子工作区：{workPath}", session, CodexEventType.General, task.Id).ConfigureAwait(false);
            }
            StructuredLog.Error(_logger, ex, "Execution failed");
            await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Failed, ex.Message).ConfigureAwait(false);
            return new OrchestratorResult(false, $"异常：{ex.Message}", session);
        }
        catch (HttpRequestException ex)
        {
            task.Status = CodexTaskStatus.Failed;
            if (usingShadow && keepShadowOnFailure)
            {
                shouldCleanupShadow = false;
                await ReportProgressAsync($"🧷 [Debug] 异常失败，保留影子工作区：{workPath}", session, CodexEventType.General, task.Id).ConfigureAwait(false);
            }
            StructuredLog.Error(_logger, ex, "Execution failed");
            await PublishTaskListAsync(session, task.Id, CodexTaskStatus.Failed, ex.Message).ConfigureAwait(false);
            return new OrchestratorResult(false, $"异常：{ex.Message}", session);
        }
        finally
        {
            session.WorkspacePath = originalWorkspacePath;
            if (usingShadow && shouldCleanupShadow)
            {
                try
                {
                    // Level 7 優化：無論成功與否，任務結束後都嘗試回收影子路徑，避免殘留進程佔用
                    await _gitService.RemoveShadowWorktreeAsync(mainRoot, task.Id).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    StructuredLog.Warning(_logger, ex, "影子工作區回收失敗: {TaskId}", task.Id);
                }
                catch (InvalidOperationException ex)
                {
                    StructuredLog.Warning(_logger, ex, "影子工作區回收失敗: {TaskId}", task.Id);
                }
            }
            else if (usingShadow && !shouldCleanupShadow)
            {
                StructuredLog.Information(_logger, "Shadow workspace retained for debugging: {Path}", workPath);
            }

            session.ActiveTaskId = null;
            await _sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);

            if (ShouldRefreshProjectSummaryAfterTask(session, task))
            {
                await RefreshProjectSummaryAsync(session, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<CodexResponse?> RunForgeKernelAsync(CodexSession session, string prompt, TaskFileScopeDescriptor? taskFileScope, CancellationToken ct)
    {
        var streamingResponse = await _kernel
            .RunLoopStreamingAsync(session, prompt, CodexAgentRole.Forge, ct, enableTools: true, taskFileScope: taskFileScope)
            .ConfigureAwait(false);
        if (streamingResponse != null)
        {
            return streamingResponse;
        }

        StructuredLog.Warning(
            _logger,
            "Forge streaming kernel returned null. Falling back to non-streaming kernel. SessionId={SessionId}, ActiveTaskId={TaskId}",
            session.Id,
            session.ActiveTaskId ?? "<none>");

        return await _kernel
            .RunLoopAsync(session, prompt, CodexAgentRole.Forge, ct, enableTools: true, taskFileScope: taskFileScope)
            .ConfigureAwait(false);
    }

    private static bool ShouldRefreshProjectSummaryAfterTask(CodexSession session, CodexTask task)
    {
        return session.Plan.Count > 0 &&
               task.Status is CodexTaskStatus.Success
                   or CodexTaskStatus.CompletedWithWarnings
                   or CodexTaskStatus.Failed
                   or CodexTaskStatus.Skipped;
    }

    private async Task RefreshProjectSummaryAsync(CodexSession session, CancellationToken ct)
    {
        try
        {
            if (_projectMemoryService == null)
            {
                StructuredLog.Warning(_logger, "Skipping project summary refresh because project memory service is unavailable. Session={SessionId}", session.Id);
                return;
            }

            var result = await _projectMemoryService.SaveExecutionResultAsync(
                new ProjectExecutionMemoryInput(
                    session.WorkspacePath,
                    null,
                    session.Id,
                    session.ProjectUrl,
                    session.Metadata,
                    session.Plan.ToList(),
                    BuildVerificationSummary(session)),
                ct).ConfigureAwait(false);

            session.ProjectSummary = result.Content;
            if (IsPlanFullyCompletedForPush(session))
            {
                StructuredLog.Information(_logger, "Project summary refreshed after plan completion: {Path}", result.FilePath);
            }
            else
            {
                StructuredLog.Information(_logger, "Project summary refreshed after task update: {Path}", result.FilePath);
            }
        }
        catch (IOException ex)
        {
            StructuredLog.Warning(_logger, ex, "Failed to refresh project summary after task state update for session {SessionId}", session.Id);
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Warning(_logger, ex, "Failed to refresh project summary after task state update for session {SessionId}", session.Id);
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Warning(_logger, ex, "Failed to refresh project summary after task state update for session {SessionId}", session.Id);
        }
    }

    private static string BuildPlanExecutionSummary(IEnumerable<CodexTask>? plan)
    {
        return CodexPlanSummaryFormatter.BuildExecutionSummary(plan);
    }

    private static string BuildVerificationSummary(CodexSession session)
    {
        var plan = session.Plan;
        var tasks = (plan ?? Enumerable.Empty<CodexTask>()).Where(task => task != null).ToList();
        var warnings = tasks.Count(task => task.Status == CodexTaskStatus.CompletedWithWarnings);
        var summary = CodexPlanSummaryFormatter.BuildExecutionSummary(tasks);
        var documentationSyncSummary = BuildDocumentationSyncSummary(session.Metadata);
        if (warnings == 0)
        {
            return summary + Environment.NewLine + "质量门禁：未检测到带警告完成的任务。" + documentationSyncSummary;
        }

        return summary + Environment.NewLine +
               $"质量门禁：检测到 {warnings} 个带警告完成的任务，warning scan 已触发 CompletedWithWarnings。请在收尾时复查编译告警、存量安全债或 deferred 风险。" +
               documentationSyncSummary;
    }

    private async Task UpdateDocumentationSyncStatus(
        CodexSession session,
        IReadOnlyList<CodexFlow.Core.Abstractions.GitFileChange> mergedFileChanges)
    {
        var pendingDocs = DetectDocumentationSyncTargets(mergedFileChanges);
        if (pendingDocs.Count == 0)
        {
            session.Metadata[DocumentationSyncStatusMetadataKey] = "synced";
            session.Metadata.Remove(DocumentationSyncPendingMetadataKey);
            return;
        }

        session.Metadata[DocumentationSyncStatusMetadataKey] = "pending";
        session.Metadata[DocumentationSyncPendingMetadataKey] = string.Join(";", pendingDocs);
        await ReportProgressAsync(
            $"📝 检测到架构/工程变更，待同步文档：{string.Join(", ", pendingDocs)}",
            session,
            CodexEventType.General,
            payload: new { pendingDocs }).ConfigureAwait(false);
    }

    private static List<string> DetectDocumentationSyncTargets(
        IReadOnlyList<CodexFlow.Core.Abstractions.GitFileChange> changedFiles)
    {
        if (changedFiles == null || changedFiles.Count == 0)
        {
            return [];
        }

        var normalizedPaths = changedFiles
            .Select(change => NormalizePathLike(change.FilePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var changedSet = normalizedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var architectureChangeDetected = normalizedPaths.Any(IsArchitectureOrStructureChange);
        if (!architectureChangeDetected)
        {
            return [];
        }

        var pending = new List<string>();
        foreach (var docPath in GetTrackedDocumentationPaths())
        {
            if (!changedSet.Contains(docPath))
            {
                pending.Add(docPath);
            }
        }

        return pending;
    }

    private static bool IsArchitectureOrStructureChange(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/src/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("CodexFlow.Core/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("CodexFlow.Application/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("CodexFlow.Infrastructure/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("CodexFlow.Domain/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("CodexFlow/", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetTrackedDocumentationPaths()
    {
        return
        [
            "README.md",
            ".github/copilot-instructions.md",
            "PROJECT_SUMMARY.md"
        ];
    }

    private static string BuildDocumentationSyncSummary(Dictionary<string, string> metadata)
    {
        if (metadata.TryGetValue(DocumentationSyncPendingMetadataKey, out var pendingRaw) &&
            !string.IsNullOrWhiteSpace(pendingRaw))
        {
            var pendingDocs = pendingRaw
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (pendingDocs.Count > 0)
            {
                return Environment.NewLine + $"文档同步：待同步 {string.Join(", ", pendingDocs)}。";
            }
        }

        if (metadata.TryGetValue(DocumentationSyncStatusMetadataKey, out var status) &&
            string.Equals(status, "synced", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.NewLine + "文档同步：未检测到待同步文档。";
        }

        return string.Empty;
    }

    // --- Repair Prompt Builders (prefix-dispatched) ---

    private static string BuildSecurityRepairPrompt(string resultNotes) =>
        $"\n\n🚨 [SECURITY REPAIR REQUIRED]\nThe previous attempt failed security audit. You must fix the following specific issues:\n\n{resultNotes}\n\n" +
        "👉 REPAIR PROTOCOL (follow in order):\n" +
        "1. Extract the vulnerability keywords from each risk item above (e.g. SQL Injection, XSS, Path Traversal).\n" +
        "2. Call `web_search` to find the standard fix for each vulnerability type in the project's language.\n" +
        "   Example: web_search({ \"query\": \"<language> <vulnerability> prevention best practice OWASP\" })\n" +
        "3. If the search snippet is insufficient, call `fetch_webpage` on the most relevant URL to get full details.\n" +
        "4. Apply the researched fix to ONLY the affected files. Do not change unrelated logic.\n" +
        "5. Run build/test to verify the fix compiles and existing tests still pass.\n" +
        "⚠️ Do NOT guess fixes without researching first. Do NOT delete functionality to pass the audit.";

    private static string BuildZeroToolCallsRepairPrompt(CodexTask task, string resultNotes) =>
        $"\n\n🚨 [ZERO TOOL CALLS REPAIR REQUIRED]\n你上一轮没有执行任何工具调用。你必须使用结构化工具来修改代码，纯文本回复会被视为失败。\n\n{resultNotes}\n\n" +
        "执行要求：\n" +
        "1. 第一步：调用 `ivilson_read` 或 `list_workspace` 确认目标文件位置。\n" +
        "2. 第二步：调用 `write_file` / `ivilson_smart_patch` / `run_command` 执行代码修改。\n" +
        "3. 第三步：若任务描述或失败原因涉及编译/测试、`dotnet build`、`dotnet test`、构建成功证据或测试成功证据，你必须显式调用 `run_command`（或 `run_tests`，若该工具可用）实际执行这些命令。\n" +
        $"4. 当前任务包含的验证要求：{SummarizeBuildAndTestRequirements(task)}\n" +
        "5. 只用文字说明“我将执行 dotnet build/dotnet test”不会被接受，必须产生真实工具调用和成功输出证据。\n" +
        "6. 纯文本再次返回会直接标记任务失败。";

    private static string BuildValidationRepairPrompt(CodexTask task, string resultNotes) =>
        $"\n\n⚠️ [VALIDATION REPAIR REQUIRED]\n你上一次已经执行了代码任务，但验证失败。这不是安全审计任务，不要进行漏洞研究或 web_search。\n\n必须修复以下问题：\n{resultNotes}\n" +
        BuildChecklistRepairPrompt(task) + "\n" +
        "执行要求：\n" +
        "1. 使用结构化工具调用，不要只返回分析文字。\n" +
        "2. 若失败原因提到“缺少代码变更证据”，你必须实际修改至少一个目标文件，不能只读文件或只写说明。\n" +
        "3. 若需要新建文件，请直接创建。\n" +
        $"4. 当前任务的构建/测试要求：{SummarizeBuildAndTestRequirements(task)}\n" +
        "5. 若失败原因提到“缺少构建成功证据”“缺少测试成功证据”，或任务描述包含 dotnet build / dotnet test / 编译 / 测试，你必须显式调用 `run_command`（或 `run_tests`，若该工具可用）实际执行对应命令，并保留成功输出证据。\n" +
        "6. 只用文字说明“我将执行 dotnet build/dotnet test”会直接视为失败，不算完成验证。\n" +
        "7. 对 *.csproj / Program.cs / appsettings.json 这类高风险既有文件，简单改动可用 unified diff：`apply_patch({\"patch\":\"...\"})` 或 `ivilson_smart_patch({\"patch_content\":\"...\"})`；一旦需要 Hashline，优先切到 `hs_read` / `hs_write`，不要再手写 `edit_mode/request` 外层壳。\n" +
        "8. 高风险文件进入 Hashline 流程时，优先使用 `hs_read({\"path\":\"...\"})` 获取 snapshotId/fileFingerprint/anchorId，再用 `hs_write({\"filePath\":\"...\",\"snapshotId\":\"...\",\"fileFingerprint\":\"...\",\"operations\":[...]})` 提交编辑。\n" +
        "9. 若必须使用旧接口，`ivilson_smart_patch` 的传统参数名是 `patch_content`，不是 `patch`；Hashline 模式参数名是 `request`，其内部字段是 `filePath/snapshotId/fileFingerprint/operations`。\n" +
        "10. 若受文件范围约束阻止，必须明确说明被哪条约束阻止。";

    private static string SummarizeBuildAndTestRequirements(CodexTask task)
    {
        var text = string.Join("\n", new[]
        {
            task.Title ?? string.Empty,
            task.Description ?? string.Empty,
            string.Join("\n", task.ChecklistItems.Select(item => item.Text))
        });

        var needsBuild =
            text.Contains("dotnet build", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("编译", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("build", StringComparison.OrdinalIgnoreCase);
        var needsTest =
            text.Contains("dotnet test", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("测试", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(text, @"\btest(s|ing)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return (needsBuild, needsTest) switch
        {
            (true, true) => "必须实际执行 `dotnet build` 和 `dotnet test`。",
            (true, false) => "必须实际执行 `dotnet build`。",
            (false, true) => "必须实际执行 `dotnet test`。",
            _ => "如失败原因要求构建或测试证据，必须实际执行对应命令。"
        };
    }

    private static string BuildChecklistRepairPrompt(CodexTask task)
    {
        if (task.ChecklistItems.Count == 0)
        {
            return "\n";
        }

        var completed = task.ChecklistItems
            .Where(item => item.Status == TaskChecklistItemStatus.Completed)
            .ToList();
        var failed = task.ChecklistItems
            .Where(item => item.Status == TaskChecklistItemStatus.Failed)
            .ToList();
        var pending = task.ChecklistItems
            .Where(item => item.Status is TaskChecklistItemStatus.Pending or TaskChecklistItemStatus.Blocked)
            .ToList();

        if (completed.Count == 0 && failed.Count == 0 && pending.Count == 0)
        {
            return "\n";
        }

        var builder = new StringBuilder();
        builder.AppendLine();

        if (completed.Count > 0)
        {
            builder.AppendLine("以下子步骤已通过验证，通常不需要再次修改，除非你判断它们是当前失败根因：");
            AppendChecklistLines(builder, completed);
            builder.AppendLine();
        }

        if (failed.Count > 0)
        {
            builder.AppendLine("以下子步骤当前验证失败，本轮必须优先修复：");
            AppendChecklistLines(builder, failed);
            builder.AppendLine();
        }

        if (pending.Count > 0)
        {
            builder.AppendLine("以下子步骤仍未完成，本轮必须继续推进：");
            AppendChecklistLines(builder, pending);
            builder.AppendLine();
        }

        var focusItems = failed.Concat(pending).Select(item => item.Text.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (focusItems.Count > 0)
        {
            builder.AppendLine("本轮重点目标：");
            foreach (var focusItem in focusItems)
            {
                builder.Append("- ").AppendLine(focusItem);
            }
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void AppendChecklistLines(StringBuilder builder, IEnumerable<TaskChecklistItem> items)
    {
        foreach (var item in items)
        {
            builder.Append("- ").Append(item.Text.Trim());
            if (item.Evidence.Count > 0)
            {
                builder.Append(" (证据: ").Append(string.Join("; ", item.Evidence)).Append(')');
            }

            builder.AppendLine();
        }
    }

    private static string BuildHashlineMismatchRepairPrompt(string resultNotes) =>
        $"\n\n🚨 [HASHLINE MISMATCH REPAIR REQUIRED]\n你的 Hashline 编辑失败，文件指纹或锚点不匹配。这通常意味着文件被并发修改或你的快照已过期。\n\n{resultNotes}\n\n" +
        "👉 强制修复流程（必须按顺序执行，禁止跳步）：\n" +
        "1. **重新读取快照**：优先调用 `hs_read({{\"path\":\"<目标文件>\"}})` 获取最新快照。\n" +
        "2. **解析新锚点**：从返回的 renderedText 中提取目标行的 lineNumber 和 anchorId。\n" +
        "   格式：`行号#锚点ID|内容`，例如 `22#CC33DD44|app.UseAuthentication();`\n" +
        "3. **重新提交编辑**：使用新的 snapshotId、fileFingerprint 和 anchorId 重新调用 `hs_write`；仅在兼容旧接口时才回退到 `apply_patch` 或 `ivilson_smart_patch`。\n" +
        "4. **禁止猜测**：不得编造 anchorId，不得复述旧文本，不得在失败后继续尝试猜测。\n\n" +
        "⚠️ 重复使用过期快照将直接标记任务失败，不会进入第三次修复循环。";

    private static string BuildBuildVerificationRepairPrompt(CodexTask task, string resultNotes) =>
        $"\n\n🚨 [BUILD VERIFICATION REPAIR REQUIRED]\n你上一轮代码修改已经落盘，但编排器在 Validator 前执行 `dotnet build` 失败。\n\n{resultNotes}\n\n" +
        BuildChecklistRepairPrompt(task) + "\n" +
        "执行要求：\n" +
        "1. 优先修复导致编译失败的代码或项目配置，不要绕过构建检查。\n" +
        "2. 使用结构化工具修改代码，不能只回复分析文字。\n" +
        "3. 修复后必须显式调用 `run_command({\"command\":[\"dotnet\",\"build\"]})` 或等价构建命令，保留成功输出证据。\n" +
        "4. 不要把 build 失败泛化为普通 validation failure；先让项目恢复可编译，再进入后续验证。";

    private async Task<BuildVerificationResult> RunBuildVerificationAsync(
        CodexSession session,
        CodexTask task,
        string workspacePath,
        CancellationToken ct)
    {
        if (!ShouldRequireBuildVerification(task, workspacePath))
        {
            return BuildVerificationResult.NotRequired;
        }

        await ReportProgressAsync("🏗️ Orchestrator 正在执行编译验证（dotnet build）...", session, CodexEventType.TaskProgress, task.Id).ConfigureAwait(false);

        var tool = new RunCommandTool(NullLogger<RunCommandTool>.Instance);
        var commandArgs = new Dictionary<string, object?>
        {
            ["workspace_path"] = workspacePath,
            ["command"] = new[] { "dotnet", "build" }
        };

        CodexToolResult toolResult;
        try
        {
            toolResult = await tool.ExecuteAsync(commandArgs, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            StructuredLog.Warning(_logger, ex, "Build verification execution failed unexpectedly for task {TaskId}", task.Id);
            return new BuildVerificationResult(
                IsRequired: true,
                Success: false,
                Summary: $"dotnet build 执行异常：{ex.Message}",
                OutputSummary: ex.Message);
        }

        var (exitCode, stdout, stderr) = ExtractRunCommandResult(toolResult);
        var outputSummary = SummarizeCommandOutput(stdout, stderr, toolResult.Output);

        if (toolResult.Status == ToolResultStatus.Failed)
        {
            return new BuildVerificationResult(
                IsRequired: true,
                Success: false,
                Summary: toolResult.Output,
                OutputSummary: outputSummary);
        }

        if (exitCode == 0)
        {
            return new BuildVerificationResult(
                IsRequired: true,
                Success: true,
                Summary: "dotnet build 成功。",
                OutputSummary: outputSummary);
        }

        return new BuildVerificationResult(
            IsRequired: true,
            Success: false,
            Summary: $"dotnet build 退出码 {exitCode}。",
            OutputSummary: outputSummary);
    }

    private static bool ShouldRequireBuildVerification(CodexTask task, string workspacePath)
    {
        var taskText = string.Join("\n", new[]
        {
            task.Title ?? string.Empty,
            task.Description ?? string.Empty,
            string.Join("\n", task.ChecklistItems.Select(item => item.Text))
        });

        if (taskText.Contains("dotnet build", StringComparison.OrdinalIgnoreCase) ||
            taskText.Contains("编译", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(taskText, @"\bbuild\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return false;
        }

        return Directory.EnumerateFiles(workspacePath, "*.sln", SearchOption.AllDirectories).Any() ||
               Directory.EnumerateFiles(workspacePath, "*.csproj", SearchOption.AllDirectories).Any();
    }

    private static (int ExitCode, string Stdout, string Stderr) ExtractRunCommandResult(CodexToolResult toolResult)
    {
        if (toolResult.Metadata == null)
        {
            return (-1, string.Empty, string.Empty);
        }

        var metadataType = toolResult.Metadata.GetType();
        var exitCode = metadataType.GetProperty("ExitCode")?.GetValue(toolResult.Metadata) as int? ?? -1;
        var stdout = metadataType.GetProperty("Stdout")?.GetValue(toolResult.Metadata)?.ToString() ?? string.Empty;
        var stderr = metadataType.GetProperty("Stderr")?.GetValue(toolResult.Metadata)?.ToString() ?? string.Empty;
        return (exitCode, stdout, stderr);
    }

    private static string SummarizeCommandOutput(string stdout, string stderr, string fallbackOutput)
    {
        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            sections.Add(TrimForSummary(stdout));
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            sections.Add(TrimForSummary(stderr));
        }

        if (sections.Count == 0 && !string.IsNullOrWhiteSpace(fallbackOutput))
        {
            sections.Add(TrimForSummary(fallbackOutput));
        }

        return string.Join("\n", sections.Where(section => !string.IsNullOrWhiteSpace(section)));
    }

    private static string TrimForSummary(string text)
    {
        var trimmed = text.Trim();
        const int maxLength = 3000;
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "\n... (truncated)";
    }

    private sealed class TaskFileScope
    {
        public HashSet<string> AllowedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AllowedTestFileNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AllowedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DisallowedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool AllowProjectFileEdits { get; set; }
        public bool AllowDiRegistrationFiles { get; set; }
        public bool HasConstraints => AllowedFiles.Count > 0;
    }

    private sealed record BuildVerificationResult(
        bool IsRequired,
        bool Success,
        string Summary,
        string OutputSummary)
    {
        public static BuildVerificationResult NotRequired { get; } =
            new(false, true, "Build verification not required.", string.Empty);
    }

    private static TaskFileScope BuildTaskFileScope(CodexTask task)
    {
        var sharedScope = TaskFileScopeGuard.BuildTaskFileScope(task);
        var scope = new TaskFileScope
        {
            AllowProjectFileEdits = sharedScope.AllowProjectFileEdits,
            AllowDiRegistrationFiles = sharedScope.AllowDiRegistrationFiles
        };

        foreach (var file in sharedScope.AllowedFiles)
        {
            scope.AllowedFiles.Add(file);
        }

        foreach (var file in sharedScope.AllowedTestFileNames)
        {
            scope.AllowedTestFileNames.Add(file);
        }

        foreach (var directory in sharedScope.AllowedDirectories)
        {
            scope.AllowedDirectories.Add(directory);
        }

        foreach (var directory in sharedScope.DisallowedDirectories)
        {
            scope.DisallowedDirectories.Add(directory);
        }

        return scope;
    }

    private static string? ResolveProjectDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var current = directory.Replace('\\', '/').TrimEnd('/');
        while (!string.IsNullOrWhiteSpace(current))
        {
            var segment = Path.GetFileName(current);
            if (segment.Contains('.', StringComparison.Ordinal) &&
                !segment.EndsWith('.'))
            {
                return current;
            }

            var parent = Path.GetDirectoryName(current)?.Replace('\\', '/');
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent ?? string.Empty;
        }

        return null;
    }

    private static IReadOnlyList<string> GetOutOfScopeChanges(
        IReadOnlyList<CodexFlow.Core.Abstractions.GitFileChange> changes,
        TaskFileScope scope)
    {
        var sharedScope = new TaskFileScopeDescriptor
        {
            AllowProjectFileEdits = scope.AllowProjectFileEdits,
            AllowDiRegistrationFiles = scope.AllowDiRegistrationFiles
        };

        foreach (var file in scope.AllowedFiles)
        {
            sharedScope.AllowedFiles.Add(file);
        }

        foreach (var file in scope.AllowedTestFileNames)
        {
            sharedScope.AllowedTestFileNames.Add(file);
        }

        foreach (var directory in scope.AllowedDirectories)
        {
            sharedScope.AllowedDirectories.Add(directory);
        }

        foreach (var directory in scope.DisallowedDirectories)
        {
            sharedScope.DisallowedDirectories.Add(directory);
        }

        return TaskFileScopeGuard.GetOutOfScopeChanges(changes, sharedScope);
    }

    private static bool IsSystemManagedTaskSideEffect(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        var fileName = Path.GetFileName(normalizedPath);
        return string.Equals(fileName, "PROJECT_SUMMARY.md", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathLike(string path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        // 去掉仓库名前缀，统一成 repo 内相对路径
        var firstSlash = normalized.IndexOf('/', StringComparison.Ordinal);
        if (firstSlash > 0 && normalized.Contains("/src/", StringComparison.OrdinalIgnoreCase))
        {
            var srcIndex = normalized.IndexOf("/src/", StringComparison.OrdinalIgnoreCase);
            normalized = normalized[(srcIndex + 1)..];
        }
        else if (firstSlash > 0 && normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase))
        {
            var testIndex = normalized.IndexOf("/test/", StringComparison.OrdinalIgnoreCase);
            normalized = normalized[(testIndex + 1)..];
        }

        return normalized.TrimStart('/');
    }

    private static bool IsInfrastructureKernelFailure(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("no_tool_calls_with_pending_task", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Malformed tool-call protocol", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("transport", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("function.name", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 将 Session.Plan 快照写入 Redis 并通过 OnEvent 触发 SignalR 推送。
    /// </summary>
    public async Task PublishTaskListAsync(CodexSession session, string? taskId = null, CodexTaskStatus? status = null, string? errorMessage = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        try
        {
            string snapshotJson;
            if (taskId != null && status.HasValue)
            {
                snapshotJson = await _taskListService.UpdateTaskStatusAsync(session, taskId, status.Value, errorMessage).ConfigureAwait(false);
            }
            else
            {
                snapshotJson = await _taskListService.SaveSnapshotAsync(session).ConfigureAwait(false);
            }

            await ReportProgressAsync("任务清单已更新", session, CodexEventType.TaskListUpdated, taskId, snapshotJson).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            StructuredLog.Warning(_logger, ex, "发布任务清单快照失败");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Warning(_logger, ex, "发布任务清单快照失败");
        }
    }
}

public record OrchestratorResult(bool Success, string Message, CodexSession Session, CodexTaskStatus? FinalTaskStatus = null);
