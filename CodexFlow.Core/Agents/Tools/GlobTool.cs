using System.Text;
using System.Text.RegularExpressions;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// Finds workspace paths by glob pattern.
/// </summary>
public sealed class GlobTool(ILogger<GlobTool> logger) : ICodexTool
{
    public string Name => "glob";

    public string Description => "按 glob 模式查找工作区文件路径，适用于先定位文件再读取或编辑。\n" +
        "参数（JSON object）：\n" +
        "  - pattern (string, 必填): glob 模式，支持 *、?、**，例如 \"**/*.cs\"、\"docs/**/*.md\"\n" +
        "  - path (string, 可选): 搜索起始目录，相对于工作区根目录，默认 \".\"\n" +
        "  - max_results (int, 可选): 最大返回结果数，默认 100，最大 1000\n" +
        "  - include_directories (bool, 可选): 是否包含目录结果，默认 false\n" +
        "返回：匹配到的工作区相对路径列表，按最近修改时间优先。";

    public ToolCategory Category => ToolCategory.Read;

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", "__pycache__", ".venv", "dist", "build", ".idea",
        "logs", "artifacts", ".tmp-build", "TestResults", "workspaces"
    };

    public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var pattern = ToolArgumentNormalizer.CoerceLooseStringScalarValue(arguments.GetValueOrDefault("pattern"));
        var subPath = ToolArgumentNormalizer.CoerceLooseStringScalarValue(arguments.GetValueOrDefault("path")) ?? ".";
        var includeDirectories = arguments.GetValueOrDefault("include_directories")?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
        var maxResults = 100;
        if (arguments.GetValueOrDefault("max_results") is { } mr && int.TryParse(mr.ToString(), out var mrVal))
        {
            maxResults = Math.Clamp(mrVal, 1, 1000);
        }

        if (string.IsNullOrWhiteSpace(workspacePath) && string.IsNullOrWhiteSpace(projectRoot))
        {
            return Task.FromResult(CodexToolResult.Error("Missing workspace_path."));
        }

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Task.FromResult(CodexToolResult.Error("Missing pattern."));
        }

        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(baseRoot) || !Directory.Exists(baseRoot))
        {
            return Task.FromResult(CodexToolResult.Error("Workspace root does not exist."));
        }

        var normalizedSubPath = ToolPathResolver.NormalizeDuplicateRepoPrefix(subPath, baseRoot);
        var targetDir = Path.GetFullPath(Path.Combine(baseRoot, normalizedSubPath));
        if (!Directory.Exists(targetDir) || !ToolPathResolver.IsWithinRoot(targetDir, baseRoot))
        {
            return Task.FromResult(CodexToolResult.Error(
                $"Directory not found: {subPath}",
                systemHint: "路径不确定时，先用 `list_workspace({\"path\":\".\"})` 或从 `glob({\"pattern\":\"**/*\",\"path\":\".\"})` 开始。"));
        }

        var normalizedPattern = NormalizePattern(pattern);
        var regex = BuildGlobRegex(normalizedPattern);
        var matches = new List<GlobMatch>();

        try
        {
            foreach (var entry in EnumerateEntries(targetDir, includeDirectories, ct))
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var relativePath = Path.GetRelativePath(baseRoot, entry).Replace('\\', '/');
                var scopedRelativePath = Path.GetRelativePath(targetDir, entry).Replace('\\', '/');

                if (regex.IsMatch(relativePath) || regex.IsMatch(scopedRelativePath))
                {
                    var lastWriteUtc = Directory.Exists(entry)
                        ? Directory.GetLastWriteTimeUtc(entry)
                        : File.GetLastWriteTimeUtc(entry);
                    matches.Add(new GlobMatch(relativePath, lastWriteUtc, Directory.Exists(entry)));
                }
            }

            var orderedMatches = matches
                .OrderByDescending(match => match.LastWriteUtc)
                .ThenBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .ToArray();

            if (orderedMatches.Length == 0)
            {
                return Task.FromResult(CodexToolResult.Succeeded(
                    $"No paths matched glob `{pattern}` in {normalizedSubPath}.",
                    new
                    {
                        Pattern = pattern,
                        EffectivePath = normalizedSubPath,
                        MatchCount = 0
                    },
                    summary: $"No glob matches for {pattern}."));
            }

            var sb = new StringBuilder();
            var truncated = matches.Count > orderedMatches.Length;
            sb.AppendLine(truncated
                ? $"Found {matches.Count} path(s), showing first {orderedMatches.Length}:"
                : $"Found {orderedMatches.Length} path(s):");

            foreach (var match in orderedMatches)
            {
                sb.AppendLine(match.IsDirectory ? match.Path + "/" : match.Path);
            }

            return Task.FromResult(CodexToolResult.Succeeded(
                sb.ToString(),
                new
                {
                    Pattern = pattern,
                    EffectivePath = normalizedSubPath,
                    MatchCount = matches.Count,
                    ReturnedCount = orderedMatches.Length,
                    Truncated = truncated
                },
                summary: BuildSummary(pattern, orderedMatches, truncated)));
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "glob failed");
            return Task.FromResult(CodexToolResult.Error(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(logger, ex, "glob failed");
            return Task.FromResult(CodexToolResult.Error(ex.Message));
        }
        catch (ArgumentException ex)
        {
            StructuredLog.Error(logger, ex, "glob failed");
            return Task.FromResult(CodexToolResult.Error(ex.Message));
        }
    }

    private static IEnumerable<string> EnumerateEntries(string dir, bool includeDirectories, CancellationToken ct)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }
        catch (PathTooLongException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested)
            {
                yield break;
            }

            yield return file;
        }

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(dir);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }
        catch (PathTooLongException)
        {
            yield break;
        }

        foreach (var child in dirs)
        {
            if (ct.IsCancellationRequested)
            {
                yield break;
            }

            if (ExcludedDirs.Contains(Path.GetFileName(child)))
            {
                continue;
            }

            if (includeDirectories)
            {
                yield return child;
            }

            foreach (var nested in EnumerateEntries(child, includeDirectories, ct))
            {
                yield return nested;
            }
        }
    }

    private static string NormalizePattern(string pattern)
        => pattern.Replace('\\', '/').Trim().TrimStart('/');

    private static Regex BuildGlobRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '*')
            {
                var isDoubleStar = i + 1 < pattern.Length && pattern[i + 1] == '*';
                if (isDoubleStar)
                {
                    var slashAfterDoubleStar = i + 2 < pattern.Length && pattern[i + 2] == '/';
                    sb.Append(slashAfterDoubleStar ? "(?:.*/)?" : ".*");
                    i += slashAfterDoubleStar ? 2 : 1;
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (ch == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(ch.ToString()));
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string BuildSummary(string pattern, IReadOnlyList<GlobMatch> matches, bool truncated)
    {
        var preview = string.Join("; ", matches.Take(5).Select(match => match.IsDirectory ? match.Path + "/" : match.Path));
        var suffix = truncated ? " (truncated)" : string.Empty;
        return $"Glob {pattern}: {preview}{suffix}";
    }

    private sealed record GlobMatch(string Path, DateTime LastWriteUtc, bool IsDirectory);
}
