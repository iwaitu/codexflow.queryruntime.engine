using CodexFlow.Core.Abstractions;
using CodexFlow.Core.LanguageServices;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 删除工作区文件（替代 MCP delete_file）。
/// </summary>
public class DeleteFileTool : ICodexTool
{
    private readonly ILogger<DeleteFileTool> _logger;
    private readonly ILanguageServiceRefreshNotifier? _refreshNotifier;

    public DeleteFileTool(ILogger<DeleteFileTool> logger, ILanguageServiceRefreshNotifier? refreshNotifier = null)
    {
        _logger = logger;
        _refreshNotifier = refreshNotifier;
    }

    public string Name => "delete_file";
    public string Description => "删除工作区中的单个文件（不可恢复操作，请谨慎使用）。\n" +
        "参数（JSON object）：\n" +
        "  - path (string, 必填): 相对于工作区根目录的文件路径\n" +
        "返回：文件删除成功的确认信息。文件不存在时返回错误。\n" +
        "调用示例：\n" +
        "  delete_file({\"path\":\"temp/debug.log\"})\n" +
        "  delete_file({\"path\":\"src/Obsolete/OldService.cs\"})";
    public ToolCategory Category => ToolCategory.Forge;
    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: true,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 4_096);
    public IReadOnlyList<int> AllowedStages => [3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var relativePath = arguments.GetValueOrDefault("path")?.ToString();

        if ((string.IsNullOrEmpty(workspacePath) && string.IsNullOrEmpty(projectRoot)) || string.IsNullOrEmpty(relativePath))
            return CodexToolResult.Error("Missing workspace_path or path.");

        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(baseRoot) || !Directory.Exists(baseRoot))
            return CodexToolResult.Error("Workspace root does not exist.");
        var normalizedPath = ToolPathResolver.NormalizeDuplicateRepoPrefix(relativePath, baseRoot);
        var fullPath = Path.GetFullPath(Path.Combine(baseRoot, normalizedPath));
        if (!ToolPathResolver.IsWithinRoot(fullPath, baseRoot))
            return CodexToolResult.Error("Path traversal not allowed.");

        if (!File.Exists(fullPath))
            return CodexToolResult.Error($"File not found: {relativePath}");

        try
        {
            File.Delete(fullPath);
            await NotifyRefreshAsync(baseRoot, arguments, normalizedPath, ct).ConfigureAwait(false);
            StructuredLog.Information(_logger, "delete_file: deleted {Path}", normalizedPath);
            return CodexToolResult.Succeeded($"✅ 文件已删除: {normalizedPath}");
        }
        catch (IOException ex)
        {
            StructuredLog.Error(_logger, ex, "delete_file failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(_logger, ex, "delete_file failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (ArgumentException ex)
        {
            StructuredLog.Error(_logger, ex, "delete_file failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            StructuredLog.Error(_logger, ex, "delete_file failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
    }

    private async Task NotifyRefreshAsync(
        string workspaceRoot,
        Dictionary<string, object?> arguments,
        string normalizedPath,
        CancellationToken ct)
    {
        if (_refreshNotifier == null)
        {
            return;
        }

        var workerId = arguments.GetValueOrDefault("worker_id")?.ToString()
            ?? arguments.GetValueOrDefault("session_id")?.ToString()
            ?? "default";

        await _refreshNotifier.NotifyFilesChangedAsync(new LanguageServiceRefreshRequest
        {
            WorkspacePath = workspaceRoot,
            WorkerId = workerId,
            RelativePaths = [normalizedPath]
        }, ct).ConfigureAwait(false);
    }
}
