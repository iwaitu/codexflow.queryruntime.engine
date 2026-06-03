using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents;

/// <summary>
/// Phase 4A.1: 默认 Guardrail 实现 — 基于 ICodeAnalysisService 的熔断检查
/// </summary>
/// <remarks>
/// 实现 Kernel Forge 角色的安全策略：
/// - 检查危险工具（write_file, ivilson_smart_patch, delete_file, ApplyPatchTool）
/// - 使用 ICodeAnalysisService.BuildGraphAsync + CheckGuardrailAsync
/// - 评估目标文件是否在熔断保护范围内
/// </remarks>
public sealed class DefaultCodexGuardrail : ICodexGuardrail
{
    private readonly ICodeAnalysisService _analysisService;
    private readonly ILogger<DefaultCodexGuardrail> _logger;

    /// <summary>
    /// 危险工具列表 — 这些工具可能修改文件系统，需要熔断检查
    /// </summary>
    private static readonly HashSet<string> DangerousTools = new()
    {
        "write_file",
        "edit_file",
        "ivilson_smart_patch",
        "delete_file",
        "apply_patch",
        "ApplyPatchTool"
    };

    public DefaultCodexGuardrail(
        ICodeAnalysisService analysisService,
        ILogger<DefaultCodexGuardrail> logger)
    {
        _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async ValueTask<GuardrailCheckResult> CheckAsync(
        CodexSession session,
        string toolName,
        IDictionary<string, object?> arguments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(arguments);

        // 只检查危险工具
        if (!DangerousTools.Contains(toolName))
        {
            return GuardrailCheckResult.Allowed;
        }

        // 提取目标文件路径
        var targetPath = ExtractTargetPath(arguments);
        if (string.IsNullOrEmpty(targetPath))
        {
            // 无法提取路径，允许执行（保守策略：不阻止）
            _logger.LogDebug("Could not extract target path from tool {ToolName} arguments", toolName);
            return GuardrailCheckResult.Allowed;
        }

        try
        {
            // 构建依赖图
            var graph = await _analysisService.BuildGraphAsync(session.WorkspacePath, ct).ConfigureAwait(false);
            if (graph == null)
            {
                _logger.LogWarning("Failed to build dependency graph for workspace {Workspace}", session.WorkspacePath);
                return GuardrailCheckResult.Allowed;
            }

            // 获取当前任务的风险等级
            var currentTask = session.Plan?.FirstOrDefault(t => t != null && t.Id == session.ActiveTaskId);
            var taskRisk = currentTask?.RiskLevel ?? "Medium";

            // 执行熔断检查
            var guardResult = await _analysisService.CheckGuardrailAsync(graph, targetPath, taskRisk).ConfigureAwait(false);

            if (guardResult != null && guardResult.IsBlocked)
            {
                _logger.LogWarning(
                    "Guardrail blocked tool {ToolName} for path {Path}. Reason: {Reason}",
                    toolName, targetPath, guardResult.Reason);

                return GuardrailCheckResult.Blocked(
                    guardResult.Reason ?? "目标文件被熔断机制锁定",
                    targetPath);
            }

            return GuardrailCheckResult.Allowed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Guardrail check failed for tool {ToolName}", toolName);
            // 异常时不阻止（保守策略）
            return GuardrailCheckResult.Allowed;
        }
    }

    /// <summary>
    /// 从工具参数中提取目标文件路径
    /// </summary>
    private static string? ExtractTargetPath(IDictionary<string, object?> arguments)
    {
        // 尝试多个常见的路径参数名
        if (arguments.TryGetValue("path", out var path) && path is string pathStr)
            return pathStr;

        if (arguments.TryGetValue("file_path", out var filePath) && filePath is string filePathStr)
            return filePathStr;

        if (arguments.TryGetValue("target_file", out var targetFile) && targetFile is string targetFileStr)
            return targetFileStr;

        if (arguments.TryGetValue("target_path", out var targetPath) && targetPath is string targetPathStr)
            return targetPathStr;

        return null;
    }
}
