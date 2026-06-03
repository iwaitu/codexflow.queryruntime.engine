using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodexFlow.Core.Agents;

public class StructuralLsTool : ICodexTool
{
    private readonly ICodeAnalysisService _analysisService;
    private readonly ILogger<StructuralLsTool> _logger;

    public string Name => "ivilson_ls";
    public string Description => "列出工作区目录内容，支持扁平列表和递归目录树两种模式。\n" +
        "参数（JSON object）：\n" +
        "  - path (string, 可选): 相对于工作区根目录的子目录路径，默认 \".\"\n" +
        "  - recursive (bool, 可选): 是否递归列出完整目录树，默认 false\n" +
        "  - max_depth (int, 可选): 递归模式的最大深度（1-10），默认 5\n" +
        "返回：目录内容列表。扁平模式显示类型/名称/大小/修改时间；递归模式显示树形结构。自动过滤 .git/bin/obj/node_modules 等目录。\n" +
        "进入陌生仓库时，优先从 `path\":\".\"` 或已经确认存在的真实目录开始；不要先假设存在 `src`/`app`/`lib`。\n" +
        "调用示例：\n" +
        "  ivilson_ls({\"path\":\".\"})\n" +
        "  ivilson_ls({\"path\":\"CodexFlow.Core\",\"recursive\":true,\"max_depth\":3})\n" +
        "  ivilson_ls({\"path\":\"CodexFlow.Core/Runtime\",\"recursive\":false})";
    public ToolCategory Category => ToolCategory.Read;
    public IReadOnlyList<int> AllowedStages => new[] { 0, 1, 2, 3, 4 };

    // Directories to skip in recursive mode to avoid noise
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", ".next",
        "dist", "build", "__pycache__", ".venv", "venv", ".mypy_cache",
        "target", ".gradle", ".cargo", "packages", "TestResults"
    };

    public StructuralLsTool(ICodeAnalysisService analysisService, ILogger<StructuralLsTool> logger)
    {
        _analysisService = analysisService;
        _logger = logger;
    }

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var workspacePath = arguments.TryGetValue("workspace_path", out var workspaceValue) ? workspaceValue?.ToString() : null;
        var projectRoot = arguments.TryGetValue("project_root", out var projectRootValue) ? projectRootValue?.ToString() : null;

        // 健壮性处理：如果 path 传了空对象、空数组或非字符串，回退到 "."
        var pathObj = arguments.TryGetValue("path", out var pathValue) ? pathValue : null;
        var subDir = ToolArgumentNormalizer.CoerceLooseStringScalarValue(pathObj) ?? ".";
        var requestedSubDir = subDir;

        // Parse recursive flag
        var recursive = false;
        if (arguments.TryGetValue("recursive", out var recVal))
        {
            if (recVal is bool b) recursive = b;
            else if (recVal != null && bool.TryParse(recVal.ToString(), out var parsedRecursive)) recursive = parsedRecursive;
        }

        // Parse max_depth
        var maxDepth = 5;
        if (arguments.TryGetValue("max_depth", out var depthVal))
        {
            if (depthVal is int d) maxDepth = d;
            else if (depthVal != null && int.TryParse(depthVal.ToString(), out var parsedDepth)) maxDepth = parsedDepth;
        }
        maxDepth = Math.Clamp(maxDepth, 1, 10);

        if (string.IsNullOrEmpty(workspacePath) && string.IsNullOrEmpty(projectRoot))
        {
            return CodexToolResult.Error("Missing workspace_path.");
        }

        var baseRoot = Tools.ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(baseRoot) || !Directory.Exists(baseRoot))
        {
            return CodexToolResult.Error("Workspace root does not exist.");
        }

        var targetDir = Path.GetFullPath(Path.Combine(baseRoot, subDir));

        // 自动降级：如果子路径不存在，回退到根目录而不是直接报错
        if (!Directory.Exists(targetDir))
        {
            var normalizedSubDir = Tools.ToolPathResolver.NormalizeDuplicateRepoPrefix(subDir, baseRoot);
            if (!string.Equals(normalizedSubDir, subDir, StringComparison.OrdinalIgnoreCase))
            {
                var retriedTarget = Path.GetFullPath(Path.Combine(baseRoot, normalizedSubDir));
                if (Directory.Exists(retriedTarget))
                {
                    StructuredLog.Information(_logger, 
                        "Normalized ivilson_ls path by stripping duplicated repo prefix: {OriginalPath} -> {NormalizedPath}",
                        subDir,
                        normalizedSubDir);
                    subDir = normalizedSubDir;
                    targetDir = retriedTarget;
                }
            }
        }

        if (!Directory.Exists(targetDir))
        {
            StructuredLog.Warning(_logger, "Directory not found: {SubDir}. Falling back to workspace root.", subDir);
            targetDir = Path.GetFullPath(baseRoot);
            subDir = ".";
        }

        try
        {
            CodexToolResult result;
            if (recursive)
            {
                result = BuildRecursiveTree(targetDir, subDir, maxDepth);
            }
            else
            {
                result = BuildFlatList(targetDir, subDir);
            }

            if (string.Equals(subDir, ".", StringComparison.Ordinal) && !string.Equals(requestedSubDir, ".", StringComparison.Ordinal))
            {
                result.SystemHint = "如果当前目录不是你原先猜测的路径，后续请基于这里展示的真实目录继续探索，不要继续假设 `src`/`app`/`lib`。";
            }

            return result;
        }
        catch (IOException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to list directory");
            return CodexToolResult.Error(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to list directory");
            return CodexToolResult.Error(ex.Message);
        }
        catch (ArgumentException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to list directory");
            return CodexToolResult.Error(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to list directory");
            return CodexToolResult.Error(ex.Message);
        }
    }

    /// <summary>
    /// 原始模式：只列出当前级别
    /// </summary>
    private static CodexToolResult BuildFlatList(string targetDir, string subDir)
    {
        var entries = Directory.GetFileSystemEntries(targetDir);
        var resultList = new List<FileEntryMetadata>();
        var outputSb = new StringBuilder();
        outputSb.AppendLine("Contents of " + subDir + ":");

        foreach (var entry in entries)
        {
            var info = new FileInfo(entry);
            var isDir = (info.Attributes & FileAttributes.Directory) == FileAttributes.Directory;

            var metadata = new FileEntryMetadata
            {
                Name = info.Name,
                IsDirectory = isDir,
                Size = isDir ? 0 : info.Length,
                LastModified = info.LastWriteTimeUtc
            };

            resultList.Add(metadata);

            var typeStr = isDir ? "[DIR]" : "[FILE]";
            var sizeStr = isDir ? "---" : FormatSize(info.Length);
            var modified = info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            outputSb.AppendLine(FormattableString.Invariant($"{typeStr} {info.Name,-30} | Size: {sizeStr,-10} | Modified: {modified}"));
        }

        return CodexToolResult.Succeeded(outputSb.ToString(), resultList);
    }

    /// <summary>
    /// 递归模式：输出完整的目录树（tree 风格缩进）
    /// </summary>
    private static CodexToolResult BuildRecursiveTree(string rootDir, string displayRoot, int maxDepth)
    {
        var sb = new StringBuilder();
        sb.AppendLine("📁 " + displayRoot + "/");

        int fileCount = 0;
        int dirCount = 0;
        const int maxFiles = 500; // Safety limit to prevent context explosion

        BuildTreeRecursive(sb, rootDir, "", 0, maxDepth, ref fileCount, ref dirCount, maxFiles);

        sb.AppendLine();
        sb.AppendLine(FormattableString.Invariant($"--- Summary: {dirCount} directories, {fileCount} files ---"));
        if (fileCount >= maxFiles)
        {
            sb.AppendLine(FormattableString.Invariant($"⚠️ Output truncated at {maxFiles} files. Use non-recursive mode for specific subdirectories."));
        }

        return CodexToolResult.Succeeded(sb.ToString(), null);
    }

    private static void BuildTreeRecursive(StringBuilder sb, string currentDir, string indent, int depth, int maxDepth, ref int fileCount, ref int dirCount, int maxFiles)
    {
        if (depth >= maxDepth || fileCount >= maxFiles) return;

        DirectoryInfo dirInfo;
        try { dirInfo = new DirectoryInfo(currentDir); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        catch (ArgumentException) { return; }

        // Get subdirectories first, then files
        DirectoryInfo[] subDirs;
        FileInfo[] files;
        try
        {
            subDirs = dirInfo.GetDirectories()
                .Where(d => !SkipDirs.Contains(d.Name) && !d.Name.StartsWith('.'))
                .OrderBy(d => d.Name)
                .ToArray();
            files = dirInfo.GetFiles()
                .OrderBy(f => f.Name)
                .ToArray();
        }
        catch (IOException) { return; } // Skip directories we can't access
        catch (UnauthorizedAccessException) { return; } // Skip directories we can't access

        var allItems = subDirs.Length + files.Length;
        var index = 0;

        // Directories first
        foreach (var dir in subDirs)
        {
            if (fileCount >= maxFiles) return;
            index++;
            var isLast = (index == allItems);
            var connector = isLast ? "└── " : "├── ";
            var childIndent = indent + (isLast ? "    " : "│   ");

            sb.AppendLine(FormattableString.Invariant($"{indent}{connector}📁 {dir.Name}/"));
            dirCount++;
            BuildTreeRecursive(sb, dir.FullName, childIndent, depth + 1, maxDepth, ref fileCount, ref dirCount, maxFiles);
        }

        // Then files
        foreach (var file in files)
        {
            if (fileCount >= maxFiles) return;
            index++;
            var isLast = (index == allItems);
            var connector = isLast ? "└── " : "├── ";

            sb.AppendLine(FormattableString.Invariant($"{indent}{connector}{file.Name} ({FormatSize(file.Length)})"));
            fileCount++;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        int i = 0;
        double dblSise = bytes;
        while (i < units.Length - 1 && bytes >= 1024)
        {
            i++;
            bytes /= 1024;
            dblSise /= 1024;
        }
        return $"{dblSise:F2} {units[i]}";
    }

    private sealed class FileEntryMetadata
    {
        public string Name { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
    }
}

