using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 在工作区文件中搜索内容，类似 grep（替代 MCP search_in_files）。
/// </summary>
public class SearchInFilesTool(ILogger<SearchInFilesTool> logger) : ICodexTool
{
    public string Name => "search_in_files";
    public string Description => "在工作区文件中搜索文本内容（类似 grep），返回匹配行及其文件路径和行号。\n" +
        "参数（JSON object）：\n" +
        "  - pattern (string, 必填): 要搜索的文本或正则表达式模式\n" +
        "  - path (string, 可选): 搜索起始目录，相对于工作区根目录，默认 \".\"\n" +
        "  - file_extensions (string[], 可选): 限制搜索的文件扩展名，如 [\".cs\",\".json\"]\n" +
        "  - ignore_case (bool, 可选): 是否忽略大小写，默认 true\n" +
        "  - max_results (int, 可选): 最大返回结果数，默认 50\n" +
        "返回：匹配结果列表，格式为 \"文件路径:行号: 匹配行内容\"。\n" +
        "路径不确定时，先从 `path\":\".\"` 或已确认存在的真实目录开始；不要预设仓库一定存在 `src`/`app`/`lib`。\n" +
        "调用示例：\n" +
        "  search_in_files({\"pattern\":\"IFileRepository\",\"path\":\".\",\"max_results\":30})\n" +
        "  search_in_files({\"pattern\":\"class.*Service\",\"path\":\"CodexFlow.Core\",\"file_extensions\":[\".cs\"]})\n" +
        "  search_in_files({\"pattern\":\"TODO\",\"path\":\".\",\"ignore_case\":true})";
    public ToolCategory Category => ToolCategory.Read;
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", "__pycache__", ".venv", "dist", "build", ".idea",
        "logs", "artifacts", ".tmp-build", "TestResults", "workspaces"
    };

    private static readonly HashSet<string> ExcludedArtifactExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".binlog", ".trx", ".coverage", ".sarif"
    };

    private static readonly HashSet<string> CommonGuessedRootSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "src", "source", "app", "apps", "lib", "code", "server", "client",
        "backend", "frontend", "service", "services", "test", "tests"
    };

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var pattern = arguments.GetValueOrDefault("pattern")?.ToString();
        var subPath = ToolArgumentNormalizer.CoerceLooseStringScalarValue(arguments.GetValueOrDefault("path")) ?? ".";
        var ignoreCase = arguments.GetValueOrDefault("ignore_case")?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? true;
        var maxResults = 50;
        if (arguments.GetValueOrDefault("max_results") is { } mr && int.TryParse(mr.ToString(), out var mrVal))
            maxResults = mrVal;

        if ((string.IsNullOrEmpty(workspacePath) && string.IsNullOrEmpty(projectRoot)) || string.IsNullOrEmpty(pattern))
            return CodexToolResult.Error("Missing workspace_path or pattern.");

        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(baseRoot) || !Directory.Exists(baseRoot))
            return CodexToolResult.Error("Workspace root does not exist.");

        var normalizedSubPath = ToolPathResolver.NormalizeDuplicateRepoPrefix(subPath, baseRoot);
        var targetDir = Path.GetFullPath(Path.Combine(baseRoot, normalizedSubPath));
        if (!Directory.Exists(targetDir) && !string.Equals(normalizedSubPath, subPath, StringComparison.OrdinalIgnoreCase))
        {
            targetDir = Path.GetFullPath(Path.Combine(baseRoot, subPath));
        }

        var fellBackToRoot = false;
        var requestedSubPath = subPath;
        if (!Directory.Exists(targetDir))
        {
            if (ShouldFallbackToWorkspaceRoot(subPath))
            {
                fellBackToRoot = true;
                normalizedSubPath = ".";
                targetDir = Path.GetFullPath(baseRoot);
            }
            else
            {
                return CodexToolResult.Error(
                    $"Directory not found: {subPath}",
                    systemHint: "路径不确定时，先用 `search_file_index`、`ivilson_ls({\"path\":\".\"})`，或直接从 `.` 开始搜索；不要假设 `src`/`app`/`lib` 一定存在。");
            }
        }

        var fallbackHeader = fellBackToRoot
            ? $"Requested directory `{requestedSubPath}` not found. Searched from `.` instead.{Environment.NewLine}"
            : string.Empty;
        var fallbackHint = fellBackToRoot
            ? "请求的目录不存在，已回退到工作区根目录。后续请基于真实存在的目录继续，不要重复假设 `src`/`app`/`lib`。"
            : null;

        // Parse file extensions filter
        HashSet<string>? extensions = null;
        if (arguments.GetValueOrDefault("file_extensions") is IEnumerable<object> exts)
        {
            extensions = new HashSet<string>(exts.Select(e => e.ToString()!.StartsWith('.') ? e.ToString()! : "." + e.ToString()), StringComparer.OrdinalIgnoreCase);
        }

        var includeGeneratedArtifacts = ShouldIncludeGeneratedArtifacts(normalizedSubPath, extensions);

        try
        {
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var sb = new StringBuilder();
            int matchCount = 0;
            var matchedFiles = new List<string>();
            var matchedFileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in EnumerateFiles(targetDir, extensions, includeGeneratedArtifacts))
            {
                if (ct.IsCancellationRequested) break;
                if (matchCount >= maxResults) break;

                try
                {
                    var lines = await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false);
                    for (int i = 0; i < lines.Length && matchCount < maxResults; i++)
                    {
                        if (lines[i].Contains(pattern, comparison))
                        {
                            var relativePath = Path.GetRelativePath(baseRoot, file).Replace('\\', '/');
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{relativePath}:{i + 1}: {lines[i].TrimEnd()}");
                            if (matchedFileSet.Add(relativePath))
                            {
                                matchedFiles.Add(relativePath);
                            }

                            matchCount++;
                        }
                    }
                }
                catch (IOException)
                {
                    /* skip binary/unreadable files */
                }
                catch (UnauthorizedAccessException)
                {
                    /* skip inaccessible files */
                }
            }

            if (matchCount == 0)
            {
                return CodexToolResult.Succeeded(
                    fallbackHeader + $"No matches found for \"{pattern}\" in {normalizedSubPath}.",
                    summary: fellBackToRoot
                        ? $"Requested path {requestedSubPath} missing; searched from root instead."
                        : null,
                    systemHint: fallbackHint);
            }

            var header = matchCount >= maxResults
                ? $"Found {matchCount}+ matches (truncated at {maxResults}):\n"
                : $"Found {matchCount} match(es):\n";
            var followUpHint = "🔄 建议：这些真实文件已经命中。下一步优先直接读取相关文件片段，不要继续把问题扩写成未确认的同义词或架构术语。\n";

            return CodexToolResult.Succeeded(
                fallbackHeader + header + sb.ToString() + followUpHint,
                new
                {
                    MatchCount = matchCount,
                    Pattern = pattern,
                    RequestedPath = requestedSubPath,
                    EffectivePath = normalizedSubPath,
                    FellBackToRoot = fellBackToRoot
                },
                summary: BuildMatchSummary(pattern, matchedFiles, requestedSubPath, normalizedSubPath, fellBackToRoot),
                systemHint: fallbackHint);
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "search_in_files failed");
            return CodexToolResult.Error(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(logger, ex, "search_in_files failed");
            return CodexToolResult.Error(ex.Message);
        }
        catch (ArgumentException ex)
        {
            StructuredLog.Error(logger, ex, "search_in_files failed");
            return CodexToolResult.Error(ex.Message);
        }
    }

    private static bool ShouldFallbackToWorkspaceRoot(string? subPath)
    {
        if (string.IsNullOrWhiteSpace(subPath))
        {
            return false;
        }

        var normalized = subPath.Replace('\\', '/').Trim();
        if (normalized is "." or "./")
        {
            return false;
        }

        var firstSegment = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return firstSegment != null && CommonGuessedRootSegments.Contains(firstSegment);
    }

    private static IEnumerable<string> EnumerateFiles(
        string dir,
        HashSet<string>? extensions,
        bool includeGeneratedArtifacts)
    {
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(dir);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (PathTooLongException)
        {
            yield break;
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (var d in dirs)
        {
            if (!includeGeneratedArtifacts && ExcludedDirs.Contains(Path.GetFileName(d))) continue;
            foreach (var f in EnumerateFiles(d, extensions, includeGeneratedArtifacts))
                yield return f;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (PathTooLongException)
        {
            yield break;
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (var f in files)
        {
            if (!includeGeneratedArtifacts && ShouldSkipArtifactFile(f))
            {
                continue;
            }

            if (extensions is null || extensions.Contains(Path.GetExtension(f)))
                yield return f;
        }
    }

    private static bool ShouldIncludeGeneratedArtifacts(string normalizedSubPath, HashSet<string>? extensions)
    {
        if (extensions is not null && extensions.Overlaps(ExcludedArtifactExtensions))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(normalizedSubPath) || normalizedSubPath == ".")
        {
            return false;
        }

        var segments = normalizedSubPath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments.Any(segment => ExcludedDirs.Contains(segment));
    }

    private static bool ShouldSkipArtifactFile(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith(".tmp-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ExcludedArtifactExtensions.Contains(Path.GetExtension(fileName));
    }

    private static string BuildMatchSummary(
        string pattern,
        IReadOnlyList<string> matchedFiles,
        string requestedSubPath,
        string effectiveSubPath,
        bool fellBackToRoot)
    {
        var filePreview = string.Join("; ", matchedFiles.Take(3));
        var prefix = fellBackToRoot
            ? $"Requested path {requestedSubPath} missing; searched from {effectiveSubPath} instead. "
            : string.Empty;

        if (string.IsNullOrWhiteSpace(filePreview))
        {
            return $"{prefix}Matches for {pattern}";
        }

        return $"{prefix}Matches for {pattern} in {filePreview}";
    }
}

