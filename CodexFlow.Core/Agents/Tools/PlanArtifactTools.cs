using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Agents.Tools;

internal static class PlanArtifactToolSupport
{
    private static readonly Regex ExplicitTaskLineRegex = new(
        @"^\s{0,6}(?:#{1,6}\s*)?(?:[-*]\s*)?(?:\[[ xX]\]\s*)?(?:\*\*)?(?:Task|Step|任务|步骤)\s*(?:[#：:\-]?\s*[A-Za-z0-9_\-.]+)?\s*(?:[.、:：\-—]\s*)?(?<title>.+?)\s*(?:\*\*)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HeadingRegex = new(
        @"^\s{0,3}#{1,6}\s+(?<title>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumberedLineRegex = new(
        @"^\s{0,8}(?:\d+|[一二三四五六七八九十]+)[\.、)]\s+(?<title>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BulletLineRegex = new(
        @"^\s{0,8}[-*]\s+(?:\[[ xX]\]\s*)?(?<title>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] TaskSectionKeywords =
    [
        "execution steps",
        "implementation steps",
        "tasks",
        "task list",
        "steps",
        "执行步骤",
        "实施步骤",
        "任务",
        "任务清单",
        "修复步骤"
    ];

    private static readonly string[] ActionKeywords =
    [
        "修改", "修复", "实现", "新增", "更新", "重构", "迁移", "验证", "测试", "检查", "定位", "调整",
        "fix", "implement", "add", "update", "refactor", "migrate", "verify", "test", "inspect", "adjust"
    ];

    public static string? ReadString(Dictionary<string, object?> arguments, string name)
        => arguments.GetValueOrDefault(name)?.ToString();

    public static async Task<(CodexSession Session, PlanArtifact Artifact)> LoadCurrentAsync(
        CodexSessionManager sessionManager,
        IPlanArtifactStore store,
        Dictionary<string, object?> arguments,
        CancellationToken ct)
    {
        var sessionId = ReadString(arguments, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("缺少必填参数 session_id。");
        }

        var session = await sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
        var planId = ReadString(arguments, "plan_artifact_id") ?? session.CurrentPlanArtifactId;
        var artifact = string.IsNullOrWhiteSpace(planId)
            ? await store.GetCurrentAsync(session.Id, ct).ConfigureAwait(false)
            : await store.GetAsync(planId, ct).ConfigureAwait(false);

        return artifact == null
            ? throw new InvalidOperationException("当前 session 没有可用 PlanArtifact。")
            : (session, artifact);
    }

    public static List<CodexTask> BuildFallbackPreviewTasks(string? markdown, string? goal)
    {
        var titles = ExtractTaskTitles(markdown).Take(10).ToList();
        if (titles.Count == 0 && !string.IsNullOrWhiteSpace(goal))
        {
            titles.Add(CleanTaskTitle(goal) ?? goal.Trim());
        }

        var tasks = new List<CodexTask>(titles.Count);
        for (var i = 0; i < titles.Count; i++)
        {
            var id = $"TASK-PREVIEW-{i + 1:000}";
            var title = titles[i];
            var task = new CodexTask
            {
                Id = id,
                Title = title,
                Description =
                    "从 Markdown 计划确定性提取的预览任务。\n\n" +
                    "## Execution Steps\n" +
                    $"1. {title}\n" +
                    "2. Run the smallest relevant verification after the change.",
                Status = CodexTaskStatus.Planning,
                StageId = i + 1,
                TaskType = CodexTaskClassifier.NormalizeTaskType(null, title, title),
                ComplexityLevel = 2,
                RiskLevel = "Low",
                StartedAt = null,
                FinishedAt = null,
                ResultNotes = null
            };
            task.ReplaceChecklistItems(
            [
                new TaskChecklistItem
                {
                    Id = "CHK-001",
                    Text = title,
                    Status = TaskChecklistItemStatus.Pending
                },
                new TaskChecklistItem
                {
                    Id = "CHK-002",
                    Text = "Run the smallest relevant verification after the change.",
                    Status = TaskChecklistItemStatus.Pending
                }
            ]);
            if (i > 0)
            {
                task.Dependencies.Add(tasks[i - 1].Id);
            }

            tasks.Add(task);
        }

        CodexTaskClassifier.NormalizePlan(tasks);
        return tasks;
    }

    private static IEnumerable<string> ExtractTaskTitles(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            yield break;
        }

        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var explicitTitles = new List<string>();
        foreach (var line in lines)
        {
            var match = ExplicitTaskLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var title = CleanTaskTitle(match.Groups["title"].Value);
            if (title != null)
            {
                explicitTitles.Add(title);
            }
        }

        if (explicitTitles.Count > 0)
        {
            foreach (var title in DeduplicateTitles(explicitTitles))
            {
                yield return title;
            }

            yield break;
        }

        var inTaskSection = false;
        var sectionTitles = new List<string>();
        foreach (var line in lines)
        {
            var heading = HeadingRegex.Match(line);
            if (heading.Success)
            {
                inTaskSection = IsTaskSection(heading.Groups["title"].Value);
                continue;
            }

            var numbered = NumberedLineRegex.Match(line);
            var bullet = numbered.Success ? Match.Empty : BulletLineRegex.Match(line);
            var candidate = numbered.Success
                ? numbered.Groups["title"].Value
                : bullet.Success
                    ? bullet.Groups["title"].Value
                    : null;
            if (candidate == null)
            {
                continue;
            }

            if (!inTaskSection && !LooksActionable(candidate))
            {
                continue;
            }

            var title = CleanTaskTitle(candidate);
            if (title != null)
            {
                sectionTitles.Add(title);
            }
        }

        foreach (var title in DeduplicateTitles(sectionTitles))
        {
            yield return title;
        }
    }

    private static bool IsTaskSection(string title)
        => TaskSectionKeywords.Any(keyword => title.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static bool LooksActionable(string text)
        => ActionKeywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> DeduplicateTitles(IEnumerable<string> titles)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in titles)
        {
            if (seen.Add(title))
            {
                yield return title;
            }
        }
    }

    private static string? CleanTaskTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var title = raw.Trim()
            .TrimStart('-', '*')
            .Trim()
            .Trim('*')
            .Trim();
        title = Regex.Replace(title, @"^\[[ xX]\]\s*", string.Empty, RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"^`(?<text>.+)`$", "${text}", RegexOptions.CultureInvariant);
        title = title.Trim(' ', '\t', '.', '。', ';', '；');

        if (title.Length < 3 ||
            title.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("n/a", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return title.Length <= 140 ? title : title[..140].TrimEnd() + "...";
    }
}

public sealed class WritePlanFileTool(
    CodexSessionManager sessionManager,
    IPlanArtifactStore store,
    IPlanFileService planFileService,
    CodexOrchestrator? orchestrator = null,
    ICodexPlanner? planner = null,
    Microsoft.Extensions.Logging.ILogger<WritePlanFileTool>? logger = null) : ICodexTool
{
    public string Name => "write_plan_file";
    public string Description => "写入当前 PlanArtifact 对应的 Markdown plan file。参数: session_id, markdown, plan_artifact_id(可选)。只允许写当前计划文件。";
    public ToolCategory Category => ToolCategory.Planning;
    public ToolExecutionMetadata Metadata => new(false, false, false, ToolInterruptBehavior.CancelSafe, 16_384);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        try
        {
            var markdown = PlanArtifactToolSupport.ReadString(arguments, "markdown");
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return CodexToolResult.Error("缺少必填参数 markdown。");
            }

            var (session, artifact) = await PlanArtifactToolSupport.LoadCurrentAsync(sessionManager, store, arguments, ct).ConfigureAwait(false);
            var previousMarkdownHash = PlanArtifactSupport.ComputeMarkdownHash(artifact.Markdown);
            await planFileService.WriteAsync(session, artifact, markdown, ct).ConfigureAwait(false);
            var currentMarkdownHash = PlanArtifactSupport.ComputeMarkdownHash(artifact.Markdown);
            var markdownChanged = !string.Equals(previousMarkdownHash, currentMarkdownHash, StringComparison.Ordinal);

            if (markdownChanged &&
                planner != null &&
                orchestrator != null &&
                string.Equals(session.CurrentPlanArtifactId, artifact.Id, StringComparison.Ordinal))
            {
                await TryPreviewProjectWrittenPlanAsync(
                    session,
                    artifact,
                    planner,
                    orchestrator,
                    logger,
                    ct).ConfigureAwait(false);
            }

            await store.SaveAsync(artifact, ct).ConfigureAwait(false);
            if (orchestrator != null &&
                markdownChanged &&
                string.Equals(session.CurrentPlanArtifactId, artifact.Id, StringComparison.Ordinal))
            {
                session.ReplacePlan(artifact.Tasks);
                await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                await orchestrator.PublishTaskListAsync(session).ConfigureAwait(false);
            }

            return CodexToolResult.Succeeded(
                $"PLAN_FILE_WRITTEN\n{artifact.PlanFilePath}",
                metadata: new { plan_artifact_id = artifact.Id, status = artifact.Status.ToString(), plan_file_path = artifact.PlanFilePath });
        }
        catch (InvalidOperationException ex)
        {
            return CodexToolResult.Error(ex.Message);
        }
    }

    private async Task TryPreviewProjectWrittenPlanAsync(
        CodexSession session,
        PlanArtifact artifact,
        ICodexPlanner planner,
        CodexOrchestrator orchestrator,
        Microsoft.Extensions.Logging.ILogger<WritePlanFileTool>? logger,
        CancellationToken ct)
    {
        try
        {
            var context = await sessionManager.GetFullContextAsync(session.Id).ConfigureAwait(false);
            var previewPrompt =
                $"参考项目背景和历史记录：\n{context}\n\n" +
                "请为以下已写入的待审批 Markdown 计划生成可展示的 CodexTask[] 预投影。任务必须忠于 Markdown 计划，不要新增范围。\n\n" +
                $"需求：{artifact.Goal}\n\nMarkdown 计划：\n{artifact.Markdown}";
            var previewTasks = await planner.GeneratePlanAsync(session, previewPrompt, ct).ConfigureAwait(false);
            if (previewTasks is not { Count: > 0 })
            {
                logger?.LogWarning(
                    "Plan file write preview projection returned no tasks. PlanArtifactId={PlanArtifactId}",
                    artifact.Id);
                previewTasks = PlanArtifactToolSupport.BuildFallbackPreviewTasks(artifact.Markdown, artifact.Goal);
                if (previewTasks.Count == 0)
                {
                    return;
                }
            }

            CodexTaskClassifier.NormalizePlan(previewTasks);
            foreach (var task in previewTasks)
            {
                task.Status = CodexTaskStatus.Planning;
                task.StartedAt = null;
                task.FinishedAt = null;
                task.ResultNotes = null;
            }

            artifact.ReplaceTasks(previewTasks);
            artifact.PreviewProjectedMarkdownHash = PlanArtifactSupport.ComputeMarkdownHash(artifact.Markdown);
            session.ReplacePlan(previewTasks);
            session.CurrentPlanArtifactId = artifact.Id;
            await orchestrator.PublishTaskListAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(
                ex,
                "Plan file write preview projection failed. PlanArtifactId={PlanArtifactId}",
                artifact.Id);
            var fallbackTasks = PlanArtifactToolSupport.BuildFallbackPreviewTasks(artifact.Markdown, artifact.Goal);
            if (fallbackTasks.Count > 0)
            {
                artifact.ReplaceTasks(fallbackTasks);
                artifact.PreviewProjectedMarkdownHash = PlanArtifactSupport.ComputeMarkdownHash(artifact.Markdown);
                session.ReplacePlan(fallbackTasks);
                session.CurrentPlanArtifactId = artifact.Id;
                await orchestrator.PublishTaskListAsync(session).ConfigureAwait(false);
                return;
            }

            artifact.PreviewProjectedMarkdownHash = null;
            artifact.ReplaceTasks(null);
            session.ReplacePlan([]);
        }
    }
}

public sealed class ReadPlanFileTool(
    CodexSessionManager sessionManager,
    IPlanArtifactStore store,
    IPlanFileService planFileService) : ICodexTool
{
    public string Name => "read_plan_file";
    public string Description => "读取当前 PlanArtifact 的 Markdown plan file。参数: session_id, plan_artifact_id(可选)。";
    public ToolCategory Category => ToolCategory.Read;
    public ToolExecutionMetadata Metadata => new(true, true, false, ToolInterruptBehavior.CancelSafe, 16_384);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        try
        {
            var (session, artifact) = await PlanArtifactToolSupport.LoadCurrentAsync(sessionManager, store, arguments, ct).ConfigureAwait(false);
            var markdown = await planFileService.ReadAsync(session, artifact, ct).ConfigureAwait(false) ?? artifact.Markdown;
            return CodexToolResult.Succeeded(
                markdown,
                metadata: new { plan_artifact_id = artifact.Id, status = artifact.Status.ToString(), plan_file_path = artifact.PlanFilePath });
        }
        catch (InvalidOperationException ex)
        {
            return CodexToolResult.Error(ex.Message);
        }
    }
}

public sealed class RequestPlanApprovalTool(
    CodexSessionManager sessionManager,
    IPlanArtifactStore store,
    IPlanApprovalService approvalService) : ICodexTool
{
    public string Name => "request_plan_approval";
    public string Description => "将当前 PlanArtifact 标记为 ReadyForApproval，等待用户审批。参数: session_id, plan_artifact_id(可选)。";
    public ToolCategory Category => ToolCategory.Planning;
    public ToolExecutionMetadata Metadata => new(false, false, false, ToolInterruptBehavior.CancelSafe, 4_096);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        try
        {
            var (_, artifact) = await PlanArtifactToolSupport.LoadCurrentAsync(sessionManager, store, arguments, ct).ConfigureAwait(false);
            artifact = await approvalService.RequestApprovalAsync(artifact.Id, ct).ConfigureAwait(false);
            return CodexToolResult.Succeeded(
                "PLAN_APPROVAL_REQUESTED",
                metadata: new { plan_artifact_id = artifact.Id, status = artifact.Status.ToString(), markdown = artifact.Markdown });
        }
        catch (InvalidOperationException ex)
        {
            return CodexToolResult.Error(ex.Message);
        }
    }
}

public sealed class ApprovePlanTool(
    CodexSessionManager sessionManager,
    IPlanArtifactStore store,
    IPlanApprovalService approvalService,
    IPlanProjectionService projectionService,
    CodexOrchestrator orchestrator) : ICodexTool
{
    public string Name => "approve_plan";
    public string Description => "批准 ReadyForApproval 状态的 PlanArtifact。参数: session_id, user_id(可选), feedback(可选), plan_artifact_id(可选)。若存在有效预投影会自动投影到任务列表。";
    public ToolCategory Category => ToolCategory.Planning;
    public ToolExecutionMetadata Metadata => new(false, false, false, ToolInterruptBehavior.CancelSafe, 4_096);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        try
        {
            var (session, artifact) = await PlanArtifactToolSupport.LoadCurrentAsync(sessionManager, store, arguments, ct).ConfigureAwait(false);
            var userId = PlanArtifactToolSupport.ReadString(arguments, "user_id") ?? string.Empty;
            var feedback = PlanArtifactToolSupport.ReadString(arguments, "feedback");
            artifact = await approvalService.ApproveAsync(artifact.Id, userId, feedback, ct).ConfigureAwait(false);
            var projection = await projectionService.ProjectAsync(artifact, session, ct).ConfigureAwait(false);
            if (projection.Success)
            {
                session.ReplacePlan(projection.Tasks);
                session.CurrentPlanArtifactId = projection.Artifact.Id;
                session.CurrentStage = 2;
                session.PlanVersion = projection.Artifact.Version;
                session.PlanGeneratedAtUtc = DateTime.UtcNow;
                await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                await orchestrator.PublishTaskListAsync(session).ConfigureAwait(false);

                return CodexToolResult.Succeeded(
                    "PLAN_APPROVED_AND_PROJECTED",
                    metadata: new
                    {
                        plan_artifact_id = projection.Artifact.Id,
                        status = projection.Artifact.Status.ToString(),
                        markdown_hash = projection.Artifact.Approval?.ApprovedMarkdownHash,
                        task_count = projection.Tasks.Count
                    },
                    systemHint: "计划已批准并投影为待执行任务。下一步必须调用 start_next_task，不要在主会话中直接读取、搜索或修改代码。",
                    requiredToolName: "start_next_task",
                    toolCallRequired: true);
            }

            return CodexToolResult.Succeeded(
                "PLAN_APPROVED",
                metadata: new
                {
                    plan_artifact_id = projection.Artifact.Id,
                    status = projection.Artifact.Status.ToString(),
                    markdown_hash = projection.Artifact.Approval?.ApprovedMarkdownHash,
                    projection_errors = projection.Errors
                });
        }
        catch (InvalidOperationException ex)
        {
            return CodexToolResult.Error(ex.Message);
        }
    }
}

public sealed class RejectPlanTool(
    CodexSessionManager sessionManager,
    IPlanArtifactStore store,
    IPlanApprovalService approvalService,
    CodexOrchestrator orchestrator) : ICodexTool
{
    public string Name => "reject_plan";
    public string Description => "拒绝当前 PlanArtifact 并记录反馈。参数: session_id, user_id(可选), feedback(可选), plan_artifact_id(可选)。";
    public ToolCategory Category => ToolCategory.Planning;
    public ToolExecutionMetadata Metadata => new(false, false, false, ToolInterruptBehavior.CancelSafe, 4_096);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        try
        {
            var (session, artifact) = await PlanArtifactToolSupport.LoadCurrentAsync(sessionManager, store, arguments, ct).ConfigureAwait(false);
            var userId = PlanArtifactToolSupport.ReadString(arguments, "user_id") ?? string.Empty;
            var feedback = PlanArtifactToolSupport.ReadString(arguments, "feedback");
            artifact = await approvalService.RejectAsync(artifact.Id, userId, feedback, ct).ConfigureAwait(false);
            if (string.Equals(session.CurrentPlanArtifactId, artifact.Id, StringComparison.Ordinal))
            {
                session.ReplacePlan([]);
                await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                await orchestrator.PublishTaskListAsync(session).ConfigureAwait(false);
            }

            return CodexToolResult.Succeeded(
                "PLAN_REJECTED",
                metadata: new { plan_artifact_id = artifact.Id, status = artifact.Status.ToString(), feedback });
        }
        catch (InvalidOperationException ex)
        {
            return CodexToolResult.Error(ex.Message);
        }
    }
}

public sealed class ProjectPlanToTasksTool(
    CodexSessionManager sessionManager,
    IPlanArtifactStore store,
    IPlanProjectionService projectionService,
    CodexOrchestrator orchestrator,
    ITaskListService? taskListService = null) : ICodexTool
{
    public string Name => "project_plan_to_tasks";
    public string Description => "将 Approved PlanArtifact 投影为 CodexTask[]，通过校验后写入 session.Plan 并进入 ReadyToExecute。参数: session_id, plan_artifact_id(可选)。";
    public ToolCategory Category => ToolCategory.Planning;
    public ToolExecutionMetadata Metadata => new(false, false, false, ToolInterruptBehavior.CancelSafe, 16_384);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        try
        {
            var (session, artifact) = await PlanArtifactToolSupport.LoadCurrentAsync(sessionManager, store, arguments, ct).ConfigureAwait(false);
            var result = await projectionService.ProjectAsync(artifact, session, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return CodexToolResult.Error("计划投影失败：" + string.Join("; ", result.Errors));
            }

            session.ReplacePlan(result.Tasks);
            session.CurrentPlanArtifactId = result.Artifact.Id;
            session.CurrentStage = 2;
            session.PlanVersion = result.Artifact.Version;
            session.PlanGeneratedAtUtc = DateTime.UtcNow;
            await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
            await orchestrator.PublishTaskListAsync(session).ConfigureAwait(false);

            if (taskListService != null)
            {
                _ = await taskListService.GetSnapshotAsync(session.Id, ct).ConfigureAwait(false);
            }

            return CodexToolResult.Succeeded(
                JsonConvert.SerializeObject(result.Tasks, Formatting.Indented),
                metadata: new { plan_artifact_id = result.Artifact.Id, status = result.Artifact.Status.ToString(), task_count = result.Tasks.Count },
                systemHint: "计划已投影为待执行任务。下一步必须调用 start_next_task，不要在主会话中直接读取、搜索或修改代码。",
                requiredToolName: "start_next_task",
                toolCallRequired: true);
        }
        catch (InvalidOperationException ex)
        {
            return CodexToolResult.Error(ex.Message);
        }
    }
}

public sealed class PlanDiffTool(IPlanDiffService diffService) : ICodexTool
{
    public string Name => "plan_diff";
    public string Description => "比较两个 PlanArtifact 版本。参数: to_plan_artifact_id, from_plan_artifact_id(可选)。";
    public ToolCategory Category => ToolCategory.Analysis;
    public ToolExecutionMetadata Metadata => new(true, true, false, ToolInterruptBehavior.CancelSafe, 16_384);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        try
        {
            var to = PlanArtifactToolSupport.ReadString(arguments, "to_plan_artifact_id");
            if (string.IsNullOrWhiteSpace(to))
            {
                return CodexToolResult.Error("缺少必填参数 to_plan_artifact_id。");
            }

            var from = PlanArtifactToolSupport.ReadString(arguments, "from_plan_artifact_id");
            var diff = await diffService.DiffAsync(from, to, ct).ConfigureAwait(false);
            return CodexToolResult.Succeeded(JsonConvert.SerializeObject(diff, Formatting.Indented));
        }
        catch (InvalidOperationException ex)
        {
            return CodexToolResult.Error(ex.Message);
        }
    }
}
