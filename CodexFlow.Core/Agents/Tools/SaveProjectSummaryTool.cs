using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 保存项目摘要到本地文件并同步到 Session。
/// </summary>
public class SaveProjectSummaryTool(
    ILogger<SaveProjectSummaryTool> logger,
    CodexSessionManager sessionManager,
    IProjectMemoryService projectMemoryService) : ICodexTool
{
    public string Name => "save_project_summary";
    public string Description => "将经用户确认的任务摘要保存到项目目录。参数：summary (摘要文本内容)。Few-shot: save_project_summary({\"summary\":\"已完成登录模块安全加固\"})。";
    public ToolCategory Category => ToolCategory.Forge;
    public IReadOnlyList<int> AllowedStages => [0, 1]; // 仅允许在编排前的准备阶段调用

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        var summary = arguments.GetValueOrDefault("summary")?.ToString();

        if (string.IsNullOrEmpty(workspacePath) || string.IsNullOrEmpty(summary))
            return CodexToolResult.Error("Missing workspace_path or summary content.");

        try
        {
            Uri? projectUrl = null;
            IReadOnlyDictionary<string, string>? metadata = null;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                var session = await sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
                projectUrl = session.ProjectUrl;
                metadata = session.Metadata;
            }

            var result = await projectMemoryService.SaveManualSummaryAsync(
                new ProjectManualSummaryInput(
                    workspacePath,
                    projectRoot,
                    sessionId,
                    projectUrl,
                    metadata,
                    summary),
                ct).ConfigureAwait(false);

            StructuredLog.Information(logger, "Project summary saved to {Path}", result.FilePath);
            return CodexToolResult.Succeeded("✅ 项目摘要已成功持久化至工作区根目录：PROJECT_SUMMARY.md");
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "save_project_summary failed");
            return CodexToolResult.Error($"保存摘要失败：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(logger, ex, "save_project_summary failed");
            return CodexToolResult.Error($"保存摘要失败：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(logger, ex, "save_project_summary failed");
            return CodexToolResult.Error($"保存摘要失败：{ex.Message}");
        }
    }
}

