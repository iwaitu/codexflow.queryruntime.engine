using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Hashline.Services;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 读取工作区文件内容（替代 MCP read_file），支持普通模式和 Hashline 模式。
/// </summary>
public class ReadFileTool : ICodexTool
{
    private const int MaxAutoReadLines = 500; // 默认读取前 500 行，防止撑爆上下文
    private readonly ILogger<ReadFileTool> _logger;
    private readonly IHashlineFileService? _hashlineService;
    private readonly HashlineOptions? _hashlineOptions;

    public ReadFileTool(
        ILogger<ReadFileTool> logger,
        IHashlineFileService? hashlineService = null,
        HashlineOptions? hashlineOptions = null)
    {
        _logger = logger;
        _hashlineService = hashlineService;
        _hashlineOptions = hashlineOptions;
    }

    public string Name => "ivilson_read";
    public string Description => "读取工作区文件内容，支持普通模式和 Hashline 模式。\n" +
        "参数（JSON object）：\n" +
        "  - path (string, 必填): 相对于工作区根目录的文件路径\n" +
        "  - mode (string, 可选): 读取模式，\"plain\"（默认）或 \"hashline\"（用于精准编辑）\n" +
        "  - start_line (int, 可选): 起始行号（从1开始），不指定则从文件开头读取\n" +
        "  - end_line (int, 可选): 结束行号，不指定则读取到文件末尾（普通模式）\n" +
        "  - window_start_line (int, 可选): Hashline 分段读取起始行（1-based）\n" +
        "  - window_line_count (int, 可选): Hashline 分段读取返回的最大行数\n" +
        "\n" +
        "【Hashline 模式说明】\n" +
        "当你准备修改既有文件且担心 patch 上下文不稳定、目标片段重复、或文件可能并发变化时，\n" +
        "优先使用 mode=\"hashline\" 获取带锚点快照，再基于该快照发起编辑。\n" +
        "\n" +
        "【高风险文件策略】\n" +
        "对以下高风险文件，修改前必须使用 mode=\"hashline\" 读取：\n" +
        "  - Program.cs, Program.*.cs, Startup.cs\n" +
        "  - *.csproj, *.sln, Directory.Build.props\n" +
        "  - appsettings.json, appsettings.*.json\n" +
        "  - Auth/Identity 相关文件\n" +
        "\n" +
        "Hashline 模式返回：\n" +
        "  - snapshotId: 快照标识\n" +
        "  - fileFingerprint: 文件指纹（用于乐观并发控制）\n" +
        "  - renderedText: 带行锚点的文本，格式如 \"1#A1B2C3|using System;\"\n" +
        "  - encodingName: 文件编码\n" +
        "  - hasBom: 是否有 BOM\n" +
        "  - newLineStyle: 换行风格\n" +
        "\n" +
        "【后续编辑错误恢复】\n" +
        "如果后续 apply_patch/ivilson_smart_patch 报错：\n" +
        "  - FILE_FINGERPRINT_MISMATCH: 文件已被并发修改，必须重新 ivilson_read({\"path\":\"...\", \"mode\":\"hashline\"}) 获取最新快照\n" +
        "  - ANCHOR_MISMATCH: 行内容与锚点不匹配，必须重新读取快照，禁止猜测旧上下文\n" +
        "  - LINE_OUT_OF_RANGE: 行号超出范围，重新读取获取当前行数\n" +
        "\n" +
        "后续编辑必须引用刚读取到的 snapshotId 与 fileFingerprint。\n" +
        "\n" +
        "如果文件路径不确定，先用 `search_file_index` 或 `ivilson_ls({\"path\":\".\"})` 定位真实路径，不要猜测 `src`/`app`/`lib`。\n" +
        "\n" +
        "返回：文件内容文本，包含文件路径、行数和大小。\n" +
        "调用示例：\n" +
        "  ivilson_read({\"path\":\"Program.cs\"})\n" +
        "  ivilson_read({\"path\":\"CodexFlow.Core/Runtime/QueryRuntimeEngine.cs\", \"start_line\":1, \"end_line\":100})\n" +
        "  ivilson_read({\"path\":\"Program.cs\", \"mode\":\"hashline\"})";
    public ToolCategory Category => ToolCategory.Read;
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var relativePath = arguments.GetValueOrDefault("path")?.ToString();
        var explicitMode = arguments.GetValueOrDefault("mode")?.ToString()?.ToLowerInvariant();
        var startLine = TryGetInt(arguments.GetValueOrDefault("start_line"));
        var endLine = TryGetInt(arguments.GetValueOrDefault("end_line"));
        var (windowStartLine, windowLineCount) = ParseWindowArguments(arguments);

        if ((string.IsNullOrEmpty(workspacePath) && string.IsNullOrEmpty(projectRoot)) || string.IsNullOrEmpty(relativePath))
            return CodexToolResult.Error("Missing workspace_path or path.");

        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(baseRoot) || !Directory.Exists(baseRoot))
            return CodexToolResult.Error("Workspace root does not exist.");

        var normalizedPath = relativePath;
        var fullPath = Path.GetFullPath(Path.Combine(baseRoot, normalizedPath));

        if (!File.Exists(fullPath))
        {
            var fallbackPath = ToolPathResolver.NormalizeDuplicateRepoPrefix(relativePath, baseRoot);
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

        if (!ToolPathResolver.IsWithinRoot(fullPath, baseRoot))
            return CodexToolResult.Error("Path traversal not allowed.");

        if (!File.Exists(fullPath))
            return CodexToolResult.Error(
                $"File not found: {relativePath}",
                systemHint: "路径不确定时，先用 `search_file_index` 或 `ivilson_ls({\"path\":\".\"})` 定位真实文件，不要假设 `src`/`app`/`lib` 存在。");

        // 决定使用哪种模式：显式指定 > 配置默认值
        var useHashlineMode = explicitMode switch
        {
            "hashline" => true,
            "plain" => false,
            _ => _hashlineOptions?.IsHashlinePipelineEnabled() ?? false
        };

        // Hashline 模式
        if (useHashlineMode && _hashlineService != null)
        {
            return await ExecuteHashlineModeAsync(
                fullPath,
                normalizedPath,
                baseRoot,
                windowStartLine,
                windowLineCount,
                ct).ConfigureAwait(false);
        }

        // 普通模式
        return await ExecutePlainModeAsync(fullPath, normalizedPath, startLine, endLine, ct).ConfigureAwait(false);
    }

    private async Task<CodexToolResult> ExecutePlainModeAsync(
        string fullPath, string normalizedPath,
        int? startLine, int? endLine,
        CancellationToken ct)
    {
        try
        {
            var allLines = await File.ReadAllLinesAsync(fullPath, ct).ConfigureAwait(false);
            var totalLines = allLines.Length;
            var info = new FileInfo(fullPath);

            int selStart = startLine.HasValue ? Math.Max(1, startLine.Value) : 1;
            int selEnd = endLine.HasValue ? Math.Min(totalLines, endLine.Value) : totalLines;

            // If no range specified and file is very large, auto-truncate to first 500 lines
            bool isAutoTruncated = false;
            if (!startLine.HasValue && !endLine.HasValue && totalLines > MaxAutoReadLines)
            {
                selEnd = MaxAutoReadLines;
                isAutoTruncated = true;
            }

            var selectedLines = allLines.Skip(selStart - 1).Take(selEnd - selStart + 1);
            var content = string.Join(Environment.NewLine, selectedLines);

            var output = $"--- File: {normalizedPath} ({selStart}-{selEnd}/{totalLines} lines, {FormatSize(info.Length)}) ---\n{content}";
            if (isAutoTruncated || selEnd < totalLines)
            {
                output += $"\n[NOTE] Content truncated. Total lines: {totalLines}. Use 'start_line' and 'end_line' to read more.";
            }

            return CodexToolResult.Succeeded(output,
                new { FilePath = normalizedPath, Lines = totalLines, Size = info.Length, StartLine = selStart, EndLine = selEnd, IsTruncated = isAutoTruncated || selEnd < totalLines });
        }
        catch (IOException ex)
        {
            StructuredLog.Error(_logger, ex, "ivilson_read failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(_logger, ex, "ivilson_read failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (ArgumentException ex)
        {
            StructuredLog.Error(_logger, ex, "ivilson_read failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            StructuredLog.Error(_logger, ex, "ivilson_read failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
    }

    private async Task<CodexToolResult> ExecuteHashlineModeAsync(
        string fullPath,
        string normalizedPath,
        string workspaceRoot,
        int? windowStartLine,
        int? windowLineCount,
        CancellationToken ct)
    {
        try
        {
            var snapshot = await _hashlineService!
                .ReadAsync(fullPath, workspaceRoot, windowStartLine, windowLineCount, ct)
                .ConfigureAwait(false);

            var resultContent = $"--- File (Hashline): {normalizedPath} ---\n" +
                $"SnapshotId: {snapshot.SnapshotId}\n" +
                $"Fingerprint: {snapshot.FileFingerprint}\n" +
                $"Lines: {snapshot.Lines.Count}\n" +
                $"TotalLines: {snapshot.TotalLineCount}\n" +
                $"Window: {snapshot.WindowStartLine}-{snapshot.WindowEndLine}\n" +
                $"Encoding: {snapshot.EncodingName}\n" +
                $"NewLine: {snapshot.NewLineStyle.Replace("\n", "\\n").Replace("\r\n", "\\r\\n")}\n" +
                $"BOM: {snapshot.HasBom}\n" +
                $"\n" +
                $"{snapshot.RenderedText}";

            return CodexToolResult.Succeeded(resultContent, new
            {
                FilePath = normalizedPath,
                SnapshotId = snapshot.SnapshotId,
                FileFingerprint = snapshot.FileFingerprint,
                Lines = snapshot.Lines.Count,
                TotalLineCount = snapshot.TotalLineCount,
                WindowStartLine = snapshot.WindowStartLine,
                WindowEndLine = snapshot.WindowEndLine,
                IsPartialWindow = snapshot.IsPartialWindow,
                EncodingName = snapshot.EncodingName,
                HasBom = snapshot.HasBom,
                NewLineStyle = snapshot.NewLineStyle,
                RenderedText = snapshot.RenderedText
            });
        }
        catch (HashlineException ex)
        {
            StructuredLog.Error(_logger, ex, "ivilson_read hashline mode failed: {Path}", normalizedPath);
            return CodexToolResult.Error($"Hashline error: {ex.ErrorCode} - {ex.Message}");
        }
        catch (Exception ex)
        {
            StructuredLog.Error(_logger, ex, "ivilson_read hashline mode failed: {Path}", normalizedPath);
            return CodexToolResult.Error($"Hashline error: {ex.Message}");
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        int i = 0;
        double size = bytes;
        while (i < units.Length - 1 && size >= 1024) { i++; size /= 1024; }
        return $"{size:F1} {units[i]}";
    }

    private static (int? WindowStartLine, int? WindowLineCount) ParseWindowArguments(Dictionary<string, object?> arguments)
    {
        var startLine = TryGetInt(arguments.GetValueOrDefault("window_start_line"));
        var lineCount = TryGetInt(arguments.GetValueOrDefault("window_line_count"));

        if (arguments.GetValueOrDefault("window") is Dictionary<string, object?> windowDict)
        {
            startLine ??= TryGetInt(windowDict.GetValueOrDefault("startLine"));
            lineCount ??= TryGetInt(windowDict.GetValueOrDefault("lineCount"));
        }
        else if (arguments.GetValueOrDefault("window") is JsonElement windowElement && windowElement.ValueKind == JsonValueKind.Object)
        {
            if (windowElement.TryGetProperty("startLine", out var startElement))
            {
                startLine ??= TryGetInt(startElement);
            }
            if (windowElement.TryGetProperty("lineCount", out var countElement))
            {
                lineCount ??= TryGetInt(countElement);
            }
        }

        return (startLine, lineCount);
    }

    private static int? TryGetInt(object? value)
    {
        return value switch
        {
            null => null,
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            double doubleValue when doubleValue is >= int.MinValue and <= int.MaxValue => (int)doubleValue,
            JsonElement { ValueKind: JsonValueKind.Number } numberElement when numberElement.TryGetInt32(out var jsonIntValue) => jsonIntValue,
            JsonElement { ValueKind: JsonValueKind.String } stringElement when int.TryParse(stringElement.GetString(), out var parsed) => parsed,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => null
        };
    }
}
