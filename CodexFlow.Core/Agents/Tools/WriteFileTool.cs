using CodexFlow.Core.Abstractions;
using CodexFlow.Core.LanguageServices;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 在工作区写入/创建文件（替代 MCP write_file）。
/// </summary>
public class WriteFileTool : ICodexTool
{
    private readonly ILogger<WriteFileTool> _logger;
    private readonly ILanguageServiceRefreshNotifier? _refreshNotifier;

    public WriteFileTool(ILogger<WriteFileTool> logger, ILanguageServiceRefreshNotifier? refreshNotifier = null)
    {
        _logger = logger;
        _refreshNotifier = refreshNotifier;
    }

    // [Bug-04 Fix] High-risk files that should never be blindly overwritten with full-file write.
    // Patch failures on these files must abort instead of falling back to write_file.
    private static readonly HashSet<string> HighRiskFilePatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program.cs",
        "appsettings.json",
        "appsettings.Development.json",
        "appsettings.Production.json",
        "Startup.cs",
        "GlobalUsings.cs",
        "*.csproj",
        "*.sln",
        "Directory.Build.props",
        "Directory.Packages.props",
        ".env",
        ".env.*",
        "appsettings.local.json",
        "settings.local.json",
        "secrets.json",
        "launchSettings.json",
        "launchsettings.json",
        "Controllers/AuthController.cs",
        "Controllers/AccountController.cs",
        "Middleware/*.cs",
        "Services/AuthService.cs",
        "Services/IdentityService.cs",
        "Program.*.cs",
    };

    public string Name => "write_file";
    public string Description => "在工作区目录下写入或创建文本文件。\n" +
        "参数（JSON object）：\n" +
        "  - path (string, 必填): 相对于工作区根目录的文件路径，如 \"src/Models/User.cs\"\n" +
        "  - content (string, 必填): 要写入的完整文件内容\n" +
        "返回：写入成功的确认信息，包含文件路径和字符数。\n" +
        "注意：如果文件已存在则覆盖；高风险文件（Program.cs、*.csproj等）禁止覆盖，请改用 hs_read + hs_write，或 ivilson_smart_patch / apply_patch。\n" +
        "调用示例：\n" +
        "  write_file({\"path\":\"src/Abstractions/IFileRepository.cs\",\"content\":\"namespace MyApp.Abstractions;\\n\\npublic interface IFileRepository\\n{\\n    Task<string> ReadAsync(string path);\\n}\"})\n" +
        "  write_file({\"path\":\"tests/FileRepoTests.cs\",\"content\":\"using Xunit;\\n\\npublic class FileRepoTests\\n{\\n    [Fact]\\n    public void Should_Read() { }\\n}\"})";

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
        var relativePath = arguments.GetValueOrDefault("path")?.ToString()?.TrimStart('/', '\\');
        var content = arguments.GetValueOrDefault("content")?.ToString();

        if ((string.IsNullOrEmpty(workspacePath) && string.IsNullOrEmpty(projectRoot)) || string.IsNullOrEmpty(relativePath))
            return CodexToolResult.Error("Missing workspace_path or path.");
        if (content is null)
            return CodexToolResult.Error("Missing content.");

        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(baseRoot) || !Directory.Exists(baseRoot))
            return CodexToolResult.Error("Workspace root does not exist.");

        var normalizedPath = ToolPathResolver.NormalizeDuplicateRepoPrefix(relativePath, baseRoot).TrimStart('/', '\\');
        var fullPath = Path.GetFullPath(Path.Combine(baseRoot, normalizedPath));
        if (!ToolPathResolver.IsWithinRoot(fullPath, baseRoot))
            return CodexToolResult.Error("Path traversal not allowed.");

        var existed = File.Exists(fullPath);

        // [Bug-04 Fix] Only block full-file overwrite for EXISTING high-risk files.
        // New file creation remains allowed; the safeguard is specifically against replacing
        // the full contents of sensitive entrypoints/config/auth files via write_file fallback.
        if (existed && IsHighRiskFile(normalizedPath))
        {
            StructuredLog.Warning(_logger,
                "write_file blocked: existing high-risk file {Path} cannot be overwritten with full-file write. " +
                "Caller must use hs_read/hs_write or ivilson_smart_patch/apply_patch instead.", normalizedPath);
            return CodexToolResult.Error(
                $"⛔ 高风险既有文件 [{normalizedPath}] 禁止使用 write_file 整文件覆盖。" +
                "请先用 hs_read 读取最新快照，再用 hs_write 进行精准修改；也可改用 ivilson_smart_patch 或 apply_patch。");
        }

        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(fullPath, content, ct).ConfigureAwait(false);
            await NotifyRefreshAsync(baseRoot, arguments, normalizedPath, ct).ConfigureAwait(false);
            var action = existed ? "updated" : "created";

            StructuredLog.Information(_logger, "write_file: {Action} {Path}", action, normalizedPath);
            return CodexToolResult.Succeeded(
                $"✅ 文件已{action}: {normalizedPath} ({content.Length} chars)",
                new { Action = action, FilePath = normalizedPath, Size = content.Length });
        }
        catch (IOException ex)
        {
            StructuredLog.Error(_logger, ex, "write_file failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(_logger, ex, "write_file failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (ArgumentException ex)
        {
            StructuredLog.Error(_logger, ex, "write_file failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            StructuredLog.Error(_logger, ex, "write_file failed: {Path}", normalizedPath);
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

    private static bool IsHighRiskFile(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath))
            return false;

        var fileName = Path.GetFileName(normalizedPath);
        var dirName = Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/') ?? "";

        foreach (var pattern in HighRiskFilePatterns)
        {
            if (pattern.Contains('*'))
            {
                // Glob-style matching
                if (pattern.StartsWith("*."))
                {
                    // *.csproj, *.sln, etc.
                    var suffix = pattern[1..]; // includes the dot
                    if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (pattern.EndsWith("/*.cs"))
                {
                    // Middleware/*.cs, Program/*.cs, etc.
                    var dirPattern = pattern[..^5]; // remove "/*.cs"
                    if (dirName.EndsWith(dirPattern, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (pattern.EndsWith(".*"))
                {
                    // .env.*, launchSettings.json (no wildcard here actually)
                    var prefix = pattern[..^2];
                    if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        fileName.Length > prefix.Length + 1)
                        return true;
                }
            }
            else
            {
                // Exact match
                if (string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalizedPath.Replace('\\', '/'), pattern.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
