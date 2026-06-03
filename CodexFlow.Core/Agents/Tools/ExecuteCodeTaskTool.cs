using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.IO;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 执行一个特定的代码任务。
/// </summary>
public class ExecuteCodeTaskTool(CodexOrchestrator orchestrator, ILogger<ExecuteCodeTaskTool> logger) : ICodexTool
{
    public string Name => "execute_code_task";
    public string Description => "执行规划中 type 为 code 的特定原子任务。启动 TDD 闭环（影子工作区、测试生成、代码实现、质量验证）。参数：task_id (任务 ID)。Few-shot: execute_code_task({\"task_id\":\"TASK_001\"})。";
    public ToolCategory Category => ToolCategory.Forge;
    public IReadOnlyList<int> AllowedStages => [1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        var taskId = arguments.GetValueOrDefault("task_id")?.ToString();
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(taskId))
            return CodexToolResult.Error("Missing session_id or task_id.");

        try
        {
            // [Path Normalization Fix] 使用 ToolPathResolver 解析正确的工作区根目录并传递给 Session
            var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
            var effectiveWorkspace = string.IsNullOrEmpty(baseRoot) ? (workspacePath ?? "") : baseRoot;

            var session = await orchestrator.SessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, effectiveWorkspace, (Uri?)null).ConfigureAwait(false);
            var task = session.Plan?.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.Ordinal));

            if (task == null)
            {
                // Bug-01: surface available task IDs so the model can self-correct instead of looping.
                var available = session.Plan?.Where(t => t != null).Select(t => t.Id).ToList() ?? [];
                var hint = available.Count > 0
                    ? $"当前计划（版本 {session.PlanVersion}）包含以下任务：{string.Join(", ", available)}。请使用正确的 task_id 重新调用。"
                    : $"当前计划为空，请先调用 {PlanningToolNames.Primary} 生成计划。";
                return CodexToolResult.Error($"[TASK_NOT_FOUND] 任务不存在：{taskId}。{hint}");
            }

            if (task.Status is CodexTaskStatus.Success or CodexTaskStatus.CompletedWithWarnings or CodexTaskStatus.Skipped)
            {
                if (IsPlanFullyCompleted(session.Plan))
                {
                    var summary = BuildPlanSummary(session.Plan);
                    return CodexToolResult.Succeeded($"任务 {taskId} 已处于完成态，且全计划已完成。{summary} 请输出最终执行总结，不要重复执行任务。", new Dictionary<string, object?>
                    {
                        ["taskId"] = taskId,
                        ["alreadyCompleted"] = true,
                        ["planCompleted"] = true
                    });
                }

                return CodexToolResult.Succeeded($"任务 {taskId} 已处于完成态，无需重复执行。请继续执行其他未完成任务。", new Dictionary<string, object?>
                {
                    ["taskId"] = taskId,
                    ["alreadyCompleted"] = true,
                    ["planCompleted"] = false
                });
            }

            var result = await orchestrator.ExecuteCodeTaskAsync(sessionId, taskId, workspacePath: effectiveWorkspace, ct: ct).ConfigureAwait(false);

            var response = new
            {
                result.Success,
                result.Message,
                TaskId = taskId
            };

            return result.Success
                ? CodexToolResult.Succeeded(JsonConvert.SerializeObject(response))
                : CodexToolResult.Error(result.Message);
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "execute_code_task failed");
            return CodexToolResult.Error($"任务执行异常：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(logger, ex, "execute_code_task failed");
            return CodexToolResult.Error($"任务执行异常：{ex.Message}");
        }
        catch (TimeoutException ex)
        {
            StructuredLog.Error(logger, ex, "execute_code_task failed");
            return CodexToolResult.Error($"任务执行异常：{ex.Message}");
        }
    }

    private static bool IsPlanFullyCompleted(IEnumerable<CodexTask>? plan)
    {
        return CodexPlanStateGuards.IsPlanFullyCompleted(plan);
    }

    private static string BuildPlanSummary(IEnumerable<CodexTask>? plan)
    {
        return CodexPlanSummaryFormatter.BuildExecutionSummary(plan);
    }
}
