using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 制定详细的开发计划。
/// </summary>
public class GenerateDevPlanTool(
    ICodexPlanner planner,
    CodexSessionManager sessionManager,
    CodexOrchestrator orchestrator,
    ILogger<GenerateDevPlanTool> logger,
    ITaskListService? taskListService = null,
    IOptions<PlanningOptions>? planningOptions = null,
    IPlanArtifactStore? planArtifactStore = null,
    IPlanFileService? planFileService = null,
    IPlanBlueprintGenerator? planBlueprintGenerator = null,
    IPlanApprovalService? planApprovalService = null) : ICodexTool
{
    public string Name => PlanningToolNames.Primary;
    public string Description => $"根据用户需求和当前项目上下文，为当前主会话生成结构化任务清单并写入 task list。参数: requirement (需求描述)。Few-shot: {PlanningToolNames.Primary}({{\"requirement\":\"为文件上传增加扩展名白名单和大小限制\"}})。兼容别名：{PlanningToolNames.LegacyAlias}。";
    public ToolCategory Category => ToolCategory.Planning;
    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 16_384);
    // [Fix] Explicitly restricted to Analysis (1) and Planning (2). NOT allowed in Execution (3).
    public IReadOnlyList<int> AllowedStages => [0, 1];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        var requirement = arguments.GetValueOrDefault("requirement")?.ToString();

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(requirement))
            return CodexToolResult.Error("Missing session_id or requirement.");

        try
        {
            var session = await sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
            var options = planningOptions?.Value ?? new PlanningOptions();

            // [Bug-01 Fix] Guard: if a plan was previously generated (PlanGeneratedAtUtc is set) but the
            // current session.Plan is empty, the plan has been LOST (not completed). This indicates a
            // session state persistence/recovery failure. Block re-planning and surface the error explicitly
            // so the system does not silently overwrite the user's confirmed plan with a new shorter plan.
            if (session.PlanGeneratedAtUtc.HasValue && session.Plan.Count == 0)
            {
                StructuredLog.Error(logger,
                    "Plan state loss detected for session {SessionId}: PlanGeneratedAtUtc={At} but Plan.Count=0. " +
                    "Blocking {PlanToolName} to prevent silent overwrite of previously generated plan.",
                    sessionId, session.PlanGeneratedAtUtc, PlanningToolNames.Primary);
                return CodexToolResult.Error(
                    $"⛔ [系统错误] 检测到计划状态丢失：本会话于 {session.PlanGeneratedAtUtc:u} 已生成计划（版本 {session.PlanVersion}），" +
                    "但当前计划列表为空，说明计划未能从存储中恢复。禁止自动重新规划以保护原计划完整性。" +
                    "请联系管理员检查 Session 持久化链路，或创建新会话重新生成计划。");
            }

            // [Fix] Only block re-planning when there are actively pending/executing/planning tasks (mid-execution).
            // Allow re-planning when all tasks are completed — user may want to iterate on remaining issues.
            if (CodexPlanStateGuards.HasPendingOrExecutingTasks(session.Plan))
            {
                return CodexToolResult.Succeeded("⚠️ 当前计划中仍有待执行、执行中或规划中的任务。请先完成当前计划再重新规划。");
            }

            // [Level 8] Transactional Re-planning: Save old plan in case generation fails
            var oldPlan = session.Plan.ToList();
            var oldStage = session.CurrentStage;
            var oldPlanVersion = session.PlanVersion;
            var oldPlanGeneratedAtUtc = session.PlanGeneratedAtUtc;
            var oldMetadata = session.Metadata.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            var context = await sessionManager.GetFullContextAsync(sessionId).ConfigureAwait(false);

            if (options.PlanArtifactMode != PlanningMode.Off &&
                planArtifactStore != null &&
                planFileService != null &&
                planBlueprintGenerator != null &&
                planApprovalService != null)
            {
                var artifact = await CreateReadyForApprovalArtifactAsync(
                    session,
                    requirement,
                    context,
                    planArtifactStore,
                    planFileService,
                    planBlueprintGenerator,
                    planApprovalService,
                    planner,
                    orchestrator,
                    logger,
                    ct).ConfigureAwait(false);
                await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);

                if (options.PlanArtifactMode == PlanningMode.On)
                {
                    return CodexToolResult.Succeeded(
                        $"PLAN_ARTIFACT_READY_FOR_APPROVAL\n{artifact.Markdown}",
                        metadata: new
                        {
                            plan_artifact_id = artifact.Id,
                            status = artifact.Status.ToString(),
                            plan_file_path = artifact.PlanFilePath,
                            task_count = artifact.Tasks.Count
                        },
                        summary: "plan artifact ready for approval");
                }
            }

            // Inject placeholder "PLANNING" task for immediate UI feedback
            session.ReplacePlan(
            [
                new CodexTask
                {
                    Id = "TASK-PLANNING",
                    Title = "正在进行全局方案与任务拆解设计...",
                    Description = "Architect 正在结合您的需求和项目上下文进行深度思考",
                    Status = CodexTaskStatus.Planning,
                    StartedAt = DateTime.UtcNow,
                    ComplexityLevel = 2,
                    RiskLevel = "Low"
                }
            ]);

            try
            {
                await orchestrator.PublishTaskListAsync(session).ConfigureAwait(false);

                var planPrompt = $"参考项目背景和历史记录：\n{context}\n\n请为以下需求生成详细的任务规划：{requirement}";
                var newPlan = await planner.GeneratePlanAsync(session, planPrompt, ct).ConfigureAwait(false);

                if (newPlan is { Count: > 0 })
                {
                    StructuredLog.Information(logger, "New plan generated successfully with {Count} tasks.", newPlan.Count);
                    session.ReplacePlan(newPlan);
                    session.CurrentStage = 2; // 标记为已规划
                    // [Bug-01 Fix] Stamp plan lifecycle metadata so future requests can detect plan loss vs. no-plan.
                    session.PlanVersion = Guid.NewGuid().ToString("N")[..8];
                    session.PlanGeneratedAtUtc = DateTime.UtcNow;
                    session.Metadata.Remove(PlanGuardMetadataKeys.PlanResetRequired);
                    session.Metadata.Remove(PlanGuardMetadataKeys.PlanResetReason);
                    session.Metadata.Remove(PlanGuardMetadataKeys.PlanResetAtUtc);
                    // [Bug-Fix-F] Clear all per-task external retry counters: new plan = new task IDs,
                    // old counters are stale and would prematurely block execution of new tasks.
                    var staleKeys = session.Metadata.Keys
                        .Where(k => k.StartsWith(ExecutionGuardMetadataKeys.ExternalRetryCountPrefix, StringComparison.Ordinal))
                        .ToList();
                    foreach (var k in staleKeys) session.Metadata.Remove(k);
                    await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                    await orchestrator.PublishTaskListAsync(session).ConfigureAwait(false);

                    if (!await HasPersistedTaskListSnapshotAsync(session, taskListService, ct).ConfigureAwait(false))
                    {
                        StructuredLog.Error(
                            logger,
                            "Task list snapshot was not persisted after {PlanToolName} for session {SessionId}. Rolling back to previous plan.",
                            PlanningToolNames.Primary,
                            sessionId);
                        session.ReplacePlan(oldPlan);
                        session.CurrentStage = oldStage;
                        session.PlanVersion = oldPlanVersion;
                        session.PlanGeneratedAtUtc = oldPlanGeneratedAtUtc;
                        session.ReplaceMetadata(oldMetadata);
                        await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                        await orchestrator.PublishTaskListAsync(session).ConfigureAwait(false);
                        return CodexToolResult.Error("规划已生成但 task list snapshot 未成功持久化，系统已回滚到旧计划状态。请检查任务清单存储链路后重试。");
                    }

                    var planJson = JsonConvert.SerializeObject(newPlan, Formatting.Indented);
                    return CodexToolResult.Succeeded($"✅ 任务规划已生成，共计 {newPlan.Count} 个任务：\n\n{planJson}\n\n请审查以上计划，然后调用 start_next_task 开始执行第一个待执行任务。analysis 任务为只读分析任务，不应进入代码执行。");
                }
                else
                {
                    // Fallback to old plan if generation returned empty
                    StructuredLog.Warning(logger, "Architect returned empty plan. Rolling back to previous plan.");
                    session.ReplacePlan(oldPlan);
                    await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                    await orchestrator.PublishTaskListAsync(session).ConfigureAwait(false);
                    return CodexToolResult.Error("规划服务未能生成有效任务列表，已恢复原有进度。");
                }
            }
            catch (HttpRequestException ex)
            {
                StructuredLog.Error(logger, ex, "Failed during dev plan generation. Rolling back plan state.");
                session.ReplacePlan(oldPlan);
                await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                await orchestrator.PublishTaskListAsync(session).ConfigureAwait(false);
                return CodexToolResult.Error($"规划生成异常: {ex.Message}。已恢复原有进度。");
            }
            catch (IOException ex)
            {
                StructuredLog.Error(logger, ex, "Failed during dev plan generation. Rolling back plan state.");
                session.ReplacePlan(oldPlan);
                await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                await orchestrator.PublishTaskListAsync(session).ConfigureAwait(false);
                return CodexToolResult.Error($"规划生成异常: {ex.Message}。已恢复原有进度。");
            }
            catch (InvalidOperationException ex)
            {
                StructuredLog.Error(logger, ex, "Failed during dev plan generation. Rolling back plan state.");
                session.ReplacePlan(oldPlan);
                await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
                await orchestrator.PublishTaskListAsync(session).ConfigureAwait(false);
                return CodexToolResult.Error($"规划生成异常: {ex.Message}。已恢复原有进度。");
            }
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "{PlanToolName} outer error", PlanningToolNames.Primary);
            return CodexToolResult.Error($"规划任务启动失败: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(logger, ex, "{PlanToolName} outer error", PlanningToolNames.Primary);
            return CodexToolResult.Error($"规划任务启动失败: {ex.Message}");
        }
    }

    private static async Task<PlanArtifact> CreateReadyForApprovalArtifactAsync(
        CodexSession session,
        string requirement,
        string context,
        IPlanArtifactStore store,
        IPlanFileService fileService,
        IPlanBlueprintGenerator blueprintGenerator,
        IPlanApprovalService approvalService,
        ICodexPlanner planner,
        CodexOrchestrator orchestrator,
        ILogger<GenerateDevPlanTool> logger,
        CancellationToken ct)
    {
        var artifact = new PlanArtifact
        {
            SessionId = session.Id,
            Goal = requirement,
            Source = PlanArtifactSource.DefaultPlanner,
            Status = PlanArtifactStatus.Draft
        };

        artifact.Markdown = await blueprintGenerator.GenerateAsync(session, requirement, context, ct).ConfigureAwait(false);
        artifact.PlanFilePath = Path.GetRelativePath(
                session.WorkspacePath,
                fileService.GetPlanFilePath(session, artifact.Id))
            .Replace('\\', '/');
        await fileService.WriteAsync(session, artifact, artifact.Markdown, ct).ConfigureAwait(false);
        await TryPreviewProjectTasksAsync(
            session,
            artifact,
            requirement,
            context,
            planner,
            orchestrator,
            logger,
            ct).ConfigureAwait(false);
        await store.SaveAsync(artifact, ct).ConfigureAwait(false);
        await store.SetCurrentAsync(session.Id, artifact.Id, ct).ConfigureAwait(false);
        session.CurrentPlanArtifactId = artifact.Id;
        await approvalService.RequestApprovalAsync(artifact.Id, ct).ConfigureAwait(false);
        return await store.GetAsync(artifact.Id, ct).ConfigureAwait(false) ?? artifact;
    }

    private static async Task TryPreviewProjectTasksAsync(
        CodexSession session,
        PlanArtifact artifact,
        string requirement,
        string context,
        ICodexPlanner planner,
        CodexOrchestrator orchestrator,
        ILogger<GenerateDevPlanTool> logger,
        CancellationToken ct)
    {
        try
        {
            var previewPrompt =
                $"参考项目背景和历史记录：\n{context}\n\n" +
                $"请为以下待审批计划生成可展示的 CodexTask[] 预投影。任务应忠于需求，不要新增范围。\n\n需求：{requirement}\n\nMarkdown 计划：\n{artifact.Markdown}";
            var previewTasks = await planner.GeneratePlanAsync(session, previewPrompt, ct).ConfigureAwait(false);
            if (previewTasks is not { Count: > 0 })
            {
                StructuredLog.Warning(logger, "Plan artifact preview projection returned no tasks. PlanArtifactId={PlanArtifactId}", artifact.Id);
                previewTasks = PlanArtifactToolSupport.BuildFallbackPreviewTasks(artifact.Markdown, requirement);
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
            StructuredLog.Warning(logger, ex, "Plan artifact preview projection failed. PlanArtifactId={PlanArtifactId}", artifact.Id);
            var fallbackTasks = PlanArtifactToolSupport.BuildFallbackPreviewTasks(artifact.Markdown, requirement);
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

    private static async Task<bool> HasPersistedTaskListSnapshotAsync(
        CodexSession session,
        ITaskListService? taskListService,
        CancellationToken ct)
    {
        if (taskListService == null)
        {
            return true;
        }

        var snapshotJson = await taskListService.GetSnapshotAsync(session.Id, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(snapshotJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            if (!document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var persistedTaskIds = items.EnumerateArray()
                .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return session.Plan.Count > 0 &&
                   persistedTaskIds.Count == session.Plan.Count &&
                   session.Plan.All(task => persistedTaskIds.Contains(task.Id));
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
