using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Globalization;
using System.Text;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 基于内存索引的快速文件搜索工具。
/// </summary>
public class SearchFileIndexTool(
    CodexSessionManager sessionManager,
    ILogger<SearchFileIndexTool> logger) : ICodexTool
{
    public string Name => "search_file_index";
    public string Description => "快速查找项目中的文件路径。当你不确定文件位置时，优先使用此工具而不是盲目猜测 `src`/`app`/`lib` 或全盘 `ivilson_ls`。优先使用已有项目索引；如果索引尚未建立，会自动回退到当前工作区做受限实时扫描。支持模糊匹配。Few-shot: search_file_index({\"query\":\"FileService\"})。";
    public ToolCategory Category => ToolCategory.Analysis;
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4, 5];

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", ".next",
        "dist", "build", "__pycache__", ".venv", "venv", ".mypy_cache",
        "target", ".gradle", ".cargo", "packages", "TestResults", "logs",
        "artifacts", ".tmp-build", "workspaces"
    };

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        var query = arguments.GetValueOrDefault("query")?.ToString();
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();

        if (string.IsNullOrEmpty(query)) return CodexToolResult.Error("Missing query.");

        var triedIndex = false;
        try
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                triedIndex = true;
                var session = await sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
                var indexFact = session.ActiveFacts.FirstOrDefault(f => f.Key == "ProjectFileIndex");
                if (indexFact != null && !string.IsNullOrEmpty(indexFact.Value))
                {
                    var index = JsonConvert.DeserializeObject<List<FileIndexEntry>>(indexFact.Value);
                    if (index is { Count: > 0 })
                    {
                        var indexedMatches = index
                            .Where(e => e.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(e => e.Path.Length)
                            .Take(20)
                            .ToList();

                        if (indexedMatches.Count > 0)
                        {
                            return FormatMatches(
                                query,
                                indexedMatches,
                                "session index",
                                systemHint: "优先根据这些真实路径继续读取或搜索，不要改回去猜测 `src`/`app`/`lib`。");
                        }
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            StructuredLog.Error(logger, ex, "SearchFileIndex index decode failed, falling back to workspace scan");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(logger, ex, "SearchFileIndex session lookup failed, falling back to workspace scan");
        }

        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (!string.IsNullOrWhiteSpace(baseRoot) && Directory.Exists(baseRoot))
        {
            var filesystemMatches = await SearchWorkspaceAsync(baseRoot, query, ct).ConfigureAwait(false);
            if (filesystemMatches.Count > 0)
            {
                return FormatMatches(
                    query,
                    filesystemMatches,
                    "live workspace scan",
                    systemHint: "结果来自实时工作区扫描。继续时请从这些已确认存在的真实路径出发，不要先假设 `src`/`app`/`lib` 存在。");
            }

            return CodexToolResult.Succeeded(
                $"未找到包含 '{query}' 的文件。建议尝试更短的关键词，或先从 `.` / 已确认存在的目录继续探索。",
                summary: $"No file path match for {query}",
                systemHint: "进入陌生仓库时，先依据真实目录结构继续探索，不要机械假设 `src`/`app`/`lib`。");
        }

        if (triedIndex)
        {
            return CodexToolResult.Error(
                "项目索引尚未建立。请先运行 analyze_project 或等待系统自动构建索引。",
                summary: $"Project file index unavailable for {query}",
                systemHint: "如果当前轮已经绑定工作区，也可以直接依赖 `workspace_path/project_root` 走实时扫描。");
        }

        return CodexToolResult.Error("Missing session_id or workspace_path/project_root.");
    }

    private static CodexToolResult FormatMatches(
        string query,
        List<FileIndexEntry> matches,
        string sourceLabel,
        string? systemHint = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FormattableString.Invariant($"🔍 Found {matches.Count} matches for '{query}' ({sourceLabel}):"));
        sb.AppendLine();
        foreach (var m in matches)
        {
            sb.AppendLine(FormattableString.Invariant($"- `{m.Path}` ({m.Type}, {FormatSize(m.Size)})"));
        }

        if (matches.Count >= 20)
        {
            sb.AppendLine();
            sb.AppendLine("⚠️ 结果已截断 (Top 20)。请提供更精确的关键词。");
        }

        sb.AppendLine();
        sb.AppendLine("🔄 建议：优先直接读取这些真实路径中的高信号文件，不要继续把当前关键词扩写成未确认的同义词、架构术语或猜测命名。");

        return CodexToolResult.Succeeded(
            sb.ToString(),
            metadata: new
            {
                MatchCount = matches.Count,
                Query = query,
                Source = sourceLabel
            },
            summary: BuildMatchSummary(query, matches),
            systemHint: string.IsNullOrWhiteSpace(systemHint)
                ? "已经拿到真实文件路径。下一步优先直接读取这些文件，不要继续猜测目录名或未确认的类型命名。"
                : systemHint);
    }

    private static string BuildMatchSummary(string query, List<FileIndexEntry> matches)
    {
        var preview = string.Join("; ", matches.Take(3).Select(static match => match.Path));
        if (string.IsNullOrWhiteSpace(preview))
        {
            return $"{matches.Count} file path match(es) for {query}";
        }

        return $"{matches.Count} file path match(es) for {query}: {preview}";
    }

    private static Task<List<FileIndexEntry>> SearchWorkspaceAsync(
        string baseRoot,
        string query,
        CancellationToken ct)
    {
        var matches = new List<FileIndexEntry>();
        var pending = new Queue<string>();
        pending.Enqueue(baseRoot);

        while (pending.Count > 0 && matches.Count < 20)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Dequeue();

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                var name = Path.GetFileName(directory);
                if (ExcludedDirs.Contains(name) || (name.Length > 0 && name[0] == '.'))
                {
                    continue;
                }

                pending.Enqueue(directory);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(baseRoot, file);
                if (!relativePath.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var info = new FileInfo(file);
                matches.Add(new FileIndexEntry
                {
                    Path = relativePath.Replace('\\', '/'),
                    Type = ClassifyFileType(relativePath),
                    Size = info.Exists ? info.Length : 0
                });

                if (matches.Count >= 20)
                {
                    break;
                }
            }
        }

        return Task.FromResult(matches
            .OrderBy(entry => entry.Path.Length)
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static string ClassifyFileType(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        return extension.ToLowerInvariant() switch
        {
            ".cs" => "C# Source",
            ".csproj" => "C# Project",
            ".sln" or ".slnx" => "Solution",
            ".json" => "JSON",
            ".md" => "Markdown",
            ".ts" or ".tsx" => "TypeScript",
            ".js" or ".jsx" => "JavaScript",
            ".yml" or ".yaml" => "YAML",
            _ => "File"
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB";
        return (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";
    }
}

