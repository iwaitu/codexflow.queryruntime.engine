using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 列出工作区目录下的文件和文件夹（替代 MCP list_workspace）。
/// </summary>
public class ListWorkspaceTool(ILogger<ListWorkspaceTool> logger) : ICodexTool
{
    public string Name => "list_workspace";
    public string Description => "列出工作区目录下的文件和文件夹结构。\n" +
        "参数（JSON object）：\n" +
        "  - path (string, 可选): 相对于工作区根目录的子目录路径，默认 \".\"\n" +
        "  - recursive (bool, 可选): 是否递归列出所有子目录，默认 false\n" +
        "返回：目录内容列表，包含类型（DIR/FILE）、名称和大小。\n" +
        "调用示例：\n" +
        "  list_workspace({\"path\":\".\"})\n" +
        "  list_workspace({\"path\":\"CodexFlow.Core/Abstractions\",\"recursive\":false})\n" +
        "  list_workspace({\"path\":\"src\",\"recursive\":true})";
    public ToolCategory Category => ToolCategory.Read;
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var workspacePath = arguments?.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments?.GetValueOrDefault("project_root")?.ToString();

        // 健壮性处理：如果 path 传了空对象、空数组或非字符串，统统回退到 "."
        var pathObj = arguments?.GetValueOrDefault("path");
        var subPath = ToolArgumentNormalizer.CoerceLooseStringScalarValue(pathObj) ?? ".";

        var recursiveObj = arguments?.GetValueOrDefault("recursive");
        var recursive = false;
        if (recursiveObj != null)
        {
            var val = recursiveObj.ToString();
            recursive = val?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
        }

        if (string.IsNullOrEmpty(workspacePath) && string.IsNullOrEmpty(projectRoot))
            return CodexToolResult.Error("Missing workspace_path.");

        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(baseRoot) || !Directory.Exists(baseRoot))
            return CodexToolResult.Error("Workspace root does not exist.");

        var targetDir = Path.GetFullPath(Path.Combine(baseRoot, subPath));

        // 如果 subPath 解析后依然找不到目录，尝试回退到根目录，而不是直接报错
        if (!Directory.Exists(targetDir))
        {
            var normalizedSubPath = ToolPathResolver.NormalizeDuplicateRepoPrefix(subPath, baseRoot);
            if (!string.Equals(normalizedSubPath, subPath, StringComparison.OrdinalIgnoreCase))
            {
                var retriedTarget = Path.GetFullPath(Path.Combine(baseRoot, normalizedSubPath));
                if (Directory.Exists(retriedTarget))
                {
                    StructuredLog.Information(logger, 
                        "Normalized list_workspace path by stripping duplicated repo prefix: {OriginalPath} -> {NormalizedPath}",
                        subPath,
                        normalizedSubPath);
                    subPath = normalizedSubPath;
                    targetDir = retriedTarget;
                }
            }
        }

        if (!Directory.Exists(targetDir))
        {
            StructuredLog.Warning(logger, "Directory not found: {SubPath}. Falling back to root {BaseRoot}.", subPath, baseRoot);
            targetDir = Path.GetFullPath(baseRoot);
            subPath = ".";
        }

        try
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var entries = Directory.GetFileSystemEntries(targetDir, "*", option);
            var sb = new StringBuilder();
            sb.AppendLine(FormattableString.Invariant($"Contents of {subPath}:"));

            foreach (var entry in entries)
            {
                var relativePath = Path.GetRelativePath(targetDir, entry);
                var isDir = Directory.Exists(entry);
                var info = new FileInfo(entry);
                var typeStr = isDir ? "[DIR]" : "[FILE]";
                var sizeStr = isDir ? "---" : FormatSize(info.Length);
                sb.AppendLine(FormattableString.Invariant($"{typeStr} {relativePath,-40} | Size: {sizeStr,-10}"));
            }

            return CodexToolResult.Succeeded(sb.ToString());
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "list_workspace failed");
            return CodexToolResult.Error(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(logger, ex, "list_workspace failed");
            return CodexToolResult.Error(ex.Message);
        }
        catch (ArgumentException ex)
        {
            StructuredLog.Error(logger, ex, "list_workspace failed");
            return CodexToolResult.Error(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            StructuredLog.Error(logger, ex, "list_workspace failed");
            return CodexToolResult.Error(ex.Message);
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        int i = 0;
        double size = bytes;
        while (i < units.Length - 1 && size >= 1024) { i++; size /= 1024; }
        return string.Create(CultureInfo.InvariantCulture, $"{size:F1} {units[i]}");
    }
}

