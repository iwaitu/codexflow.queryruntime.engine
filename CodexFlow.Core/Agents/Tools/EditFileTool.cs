using System.Text;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.LanguageServices;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// Performs exact string replacement in an existing workspace text file.
/// </summary>
public sealed class EditFileTool(
    ILogger<EditFileTool> logger,
    ILanguageServiceRefreshNotifier? refreshNotifier = null) : ICodexTool
{
    public string Name => "edit_file";

    public string Description => "对既有文本文件执行精确字符串替换，适合小范围编辑。\n" +
        "参数（JSON object）：\n" +
        "  - path (string, 必填): 相对于工作区根目录的文件路径\n" +
        "  - old_string (string, 必填): 要替换的原始文本，必须与文件内容精确匹配\n" +
        "  - new_string (string, 必填): 替换后的文本\n" +
        "  - replace_all (bool, 可选): 是否替换所有匹配，默认 false；多处匹配时必须显式设为 true\n" +
        "  - dry_run (bool, 可选): true 仅返回预览，不写入文件\n" +
        "返回：替换次数、预览和写入状态。";

    public ToolCategory Category => ToolCategory.Forge;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: true,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 8_192);

    public IReadOnlyList<int> AllowedStages => [3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var relativePath = arguments.GetValueOrDefault("path")?.ToString()?.TrimStart('/', '\\');
        var oldString = arguments.GetValueOrDefault("old_string")?.ToString();
        var newString = arguments.GetValueOrDefault("new_string")?.ToString();
        var replaceAll = arguments.GetValueOrDefault("replace_all")?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
        var dryRun = arguments.GetValueOrDefault("dry_run")?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

        if ((string.IsNullOrWhiteSpace(workspacePath) && string.IsNullOrWhiteSpace(projectRoot)) || string.IsNullOrWhiteSpace(relativePath))
        {
            return CodexToolResult.Error("Missing workspace_path or path.");
        }

        if (oldString is null)
        {
            return CodexToolResult.Error("Missing old_string.");
        }

        if (oldString.Length == 0)
        {
            return CodexToolResult.Error("old_string cannot be empty.");
        }

        if (newString is null)
        {
            return CodexToolResult.Error("Missing new_string.");
        }

        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(baseRoot) || !Directory.Exists(baseRoot))
        {
            return CodexToolResult.Error("Workspace root does not exist.");
        }

        var normalizedPath = ToolPathResolver.NormalizeDuplicateRepoPrefix(relativePath, baseRoot).TrimStart('/', '\\');
        var fullPath = Path.GetFullPath(Path.Combine(baseRoot, normalizedPath));
        if (!ToolPathResolver.IsWithinRoot(fullPath, baseRoot))
        {
            return CodexToolResult.Error("Path traversal not allowed.");
        }

        if (!File.Exists(fullPath))
        {
            return CodexToolResult.Error("File does not exist. Use write_file to create new files.");
        }

        try
        {
            var content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
            var matchCount = CountOccurrences(content, oldString);
            if (matchCount == 0)
            {
                return CodexToolResult.Error(
                    $"old_string was not found in {normalizedPath}.",
                    systemHint: "请先重新读取目标文件片段，复制最新的精确文本后再调用 edit_file。");
            }

            if (matchCount > 1 && !replaceAll)
            {
                return CodexToolResult.Error(
                    $"old_string matched {matchCount} locations in {normalizedPath}; set replace_all=true to replace all matches, or provide a more specific old_string.",
                    systemHint: "多处匹配时不要猜测目标位置；扩大 old_string 上下文或显式 replace_all=true。");
            }

            var updated = replaceAll
                ? content.Replace(oldString, newString, StringComparison.Ordinal)
                : ReplaceFirst(content, oldString, newString);
            var preview = BuildPreview(content, updated, oldString, newString, replaceAll);

            if (dryRun)
            {
                return CodexToolResult.Succeeded(
                    $"DRY RUN: edit_file would replace {ReplacementCount(matchCount, replaceAll)} occurrence(s) in {normalizedPath}.\n{preview}",
                    new
                    {
                        FilePath = normalizedPath,
                        MatchCount = matchCount,
                        ReplacementCount = ReplacementCount(matchCount, replaceAll),
                        DryRun = true
                    },
                    summary: $"Dry run edit_file {normalizedPath}: {ReplacementCount(matchCount, replaceAll)} replacement(s).");
            }

            await File.WriteAllTextAsync(fullPath, updated, ct).ConfigureAwait(false);
            await NotifyRefreshAsync(baseRoot, arguments, normalizedPath, ct).ConfigureAwait(false);

            StructuredLog.Information(logger, "edit_file: updated {Path} with {Count} replacement(s)", normalizedPath, ReplacementCount(matchCount, replaceAll));
            return CodexToolResult.Succeeded(
                $"✅ edit_file updated {normalizedPath}: {ReplacementCount(matchCount, replaceAll)} replacement(s).\n{preview}",
                new
                {
                    FilePath = normalizedPath,
                    MatchCount = matchCount,
                    ReplacementCount = ReplacementCount(matchCount, replaceAll),
                    DryRun = false
                },
                summary: $"edit_file {normalizedPath}: {ReplacementCount(matchCount, replaceAll)} replacement(s).");
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "edit_file failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(logger, ex, "edit_file failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (ArgumentException ex)
        {
            StructuredLog.Error(logger, ex, "edit_file failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            StructuredLog.Error(logger, ex, "edit_file failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
    }

    private async Task NotifyRefreshAsync(
        string workspaceRoot,
        Dictionary<string, object?> arguments,
        string normalizedPath,
        CancellationToken ct)
    {
        if (refreshNotifier == null)
        {
            return;
        }

        var workerId = arguments.GetValueOrDefault("worker_id")?.ToString()
            ?? arguments.GetValueOrDefault("session_id")?.ToString()
            ?? "default";

        await refreshNotifier.NotifyFilesChangedAsync(new LanguageServiceRefreshRequest
        {
            WorkspacePath = workspaceRoot,
            WorkerId = workerId,
            RelativePaths = [normalizedPath]
        }, ct).ConfigureAwait(false);
    }

    private static int CountOccurrences(string content, string oldString)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(oldString, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += oldString.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string content, string oldString, string newString)
    {
        var index = content.IndexOf(oldString, StringComparison.Ordinal);
        return index < 0
            ? content
            : content[..index] + newString + content[(index + oldString.Length)..];
    }

    private static int ReplacementCount(int matchCount, bool replaceAll)
        => replaceAll ? matchCount : 1;

    private static string BuildPreview(string before, string after, string oldString, string newString, bool replaceAll)
    {
        var firstIndex = before.IndexOf(oldString, StringComparison.Ordinal);
        var afterIndex = after.IndexOf(newString, StringComparison.Ordinal);
        var contextStart = Math.Max(0, Math.Min(firstIndex < 0 ? 0 : firstIndex, afterIndex < 0 ? 0 : afterIndex) - 120);
        var contextEnd = Math.Min(after.Length, Math.Max(afterIndex < 0 ? 0 : afterIndex + newString.Length, contextStart) + 240);
        var preview = after[contextStart..contextEnd];

        var sb = new StringBuilder();
        sb.AppendLine(replaceAll ? "Preview after replace_all:" : "Preview after replacement:");
        sb.AppendLine("```");
        sb.AppendLine(preview);
        sb.AppendLine("```");
        return sb.ToString();
    }
}
