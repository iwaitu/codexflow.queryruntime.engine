using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodexFlow.Core.Agents;

public class StructuralReadTool : ICodexTool
{
    private readonly ICodeAnalysisService _analysisService;
    private readonly ILogger<StructuralReadTool> _logger;
    private const int MaxAutoReadLines = 500; // 默认读取前 500 行，防止撑爆上下文

    public string Name => "ivilson_read";
    public string Description => "读取工作区文件内容，支持行号范围读取和大文件智能截断（超过500行自动截断）。\n" +
        "参数（JSON object）：\n" +
        "  - path (string, 必填): 相对于工作区根目录的文件路径\n" +
        "  - start_line (int, 可选): 起始行号（从1开始），不指定则从文件开头读取\n" +
        "  - end_line (int, 可选): 结束行号，不指定则读取到文件末尾\n" +
        "返回：文件内容文本，包含行号范围、总行数信息。大文件未指定范围时自动截断到前500行。\n" +
        "调用示例：\n" +
        "  ivilson_read({\"path\":\"README.md\"})\n" +
        "  ivilson_read({\"path\":\"CodexFlow.Core/Agents/CodexOrchestrator.cs\",\"start_line\":600,\"end_line\":700})\n" +
        "  ivilson_read({\"path\":\"src/Program.cs\",\"start_line\":1,\"end_line\":50})";
    public ToolCategory Category => ToolCategory.Read;
    public IReadOnlyList<int> AllowedStages => new[] { 0, 1, 2, 3, 4 };

    public StructuralReadTool(ICodeAnalysisService analysisService, ILogger<StructuralReadTool> logger)
    {
        _analysisService = analysisService;
        _logger = logger;
    }

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var workspacePath = arguments.TryGetValue("workspace_path", out var workspaceValue) ? workspaceValue?.ToString() : null;
        var projectRoot = arguments.TryGetValue("project_root", out var projectRootValue) ? projectRootValue?.ToString() : null;
        var relativePath = arguments.TryGetValue("path", out var pathValue) ? pathValue?.ToString() : null;

        if ((string.IsNullOrEmpty(workspacePath) && string.IsNullOrEmpty(projectRoot)) || string.IsNullOrEmpty(relativePath))
        {
            return CodexToolResult.Error("Missing path or workspace_path.");
        }

        var baseRoot = Tools.ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(baseRoot) || !Directory.Exists(baseRoot))
        {
            return CodexToolResult.Error("Workspace root does not exist.");
        }

        var normalizedPath = relativePath;
        var fullPath = Path.GetFullPath(Path.Combine(baseRoot, normalizedPath));

        if (!File.Exists(fullPath))
        {
            var fallbackPath = Tools.ToolPathResolver.NormalizeDuplicateRepoPrefix(relativePath, baseRoot);
            if (!string.Equals(fallbackPath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                var retriedFullPath = Path.GetFullPath(Path.Combine(baseRoot, fallbackPath));
                if (File.Exists(retriedFullPath))
                {
                    StructuredLog.Information(_logger, 
                        "Normalized ivilson_read path by stripping duplicated repo prefix: {OriginalPath} -> {NormalizedPath}",
                        relativePath,
                        fallbackPath);
                    normalizedPath = fallbackPath;
                    fullPath = retriedFullPath;
                }
            }
        }

        if (!Tools.ToolPathResolver.IsWithinRoot(fullPath, baseRoot))
        {
            return CodexToolResult.Error("Path traversal not allowed.");
        }

        if (!File.Exists(fullPath))
        {
            return CodexToolResult.Error($"File not found: {relativePath}");
        }

        try
        {
            var allLines = await File.ReadAllLinesAsync(fullPath, ct).ConfigureAwait(false);
            var totalLines = allLines.Length;
            var fileInfo = new FileInfo(fullPath);

            int startLine = 1;
            int endLine = totalLines;

            if (arguments.TryGetValue("start_line", out var sObj) && int.TryParse(sObj?.ToString(), out var s)) startLine = Math.Max(1, s);
            if (arguments.TryGetValue("end_line", out var eObj) && int.TryParse(eObj?.ToString(), out var e)) endLine = Math.Min(totalLines, e);

            // 如果没指定范围且文件过大，执行自动截断
            bool isAutoTruncated = false;
            if (!arguments.TryGetValue("start_line", out _) && !arguments.TryGetValue("end_line", out _) && totalLines > MaxAutoReadLines)
            {
                endLine = MaxAutoReadLines;
                isAutoTruncated = true;
            }

            var selectedLines = allLines.Skip(startLine - 1).Take(endLine - startLine + 1).ToList();
            var content = string.Join(Environment.NewLine, selectedLines);

            var metadata = new FileReadMetadata
            {
                FilePath = normalizedPath,
                TotalLines = totalLines,
                Size = fileInfo.Length,
                StartLine = startLine,
                EndLine = endLine,
                IsTruncated = isAutoTruncated || endLine < totalLines
            };

            var outputSb = new StringBuilder();
            outputSb.AppendLine(FormattableString.Invariant($"--- File: {normalizedPath} ({startLine}-{endLine}/{totalLines} lines) ---"));
            outputSb.AppendLine(content);
            if (metadata.IsTruncated)
            {
                outputSb.AppendLine(FormattableString.Invariant($"\n[NOTE] Content truncated. Total lines: {totalLines}. Use 'start_line' and 'end_line' to read more."));
            }

            return CodexToolResult.Succeeded(outputSb.ToString(), metadata);
        }
        catch (IOException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to read file: {Path}", relativePath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to read file: {Path}", relativePath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (ArgumentException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to read file: {Path}", relativePath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to read file: {Path}", relativePath);
            return CodexToolResult.Error(ex.Message);
        }
    }

    private sealed class FileReadMetadata
    {
        public string FilePath { get; set; } = string.Empty;
        public int TotalLines { get; set; }
        public long Size { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public bool IsTruncated { get; set; }
    }
}

