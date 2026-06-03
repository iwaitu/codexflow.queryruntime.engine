using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.LanguageServices;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// Safely edits Jupyter notebook cells by parsing the .ipynb JSON structure.
/// </summary>
public sealed class NotebookEditTool(
    ILogger<NotebookEditTool> logger,
    ILanguageServiceRefreshNotifier? refreshNotifier = null) : ICodexTool
{
    public string Name => "notebook_edit";

    public string Description => "使用 JSON parser 安全修改 .ipynb notebook cell，避免整文件字符串替换破坏结构。\n" +
        "参数（JSON object）：\n" +
        "  - path (string, 必填): 相对于工作区根目录的 .ipynb 文件路径\n" +
        "  - operation (string, 可选): replace_source / insert_cell / delete_cell，默认 replace_source\n" +
        "  - cell_index (int, 必填): 目标 cell 下标；insert_cell 表示插入位置\n" +
        "  - source (string, replace_source/insert_cell 必填): cell source 新内容\n" +
        "  - cell_type (string, insert_cell 可选): code / markdown，默认 code\n" +
        "  - dry_run (bool, 可选): true 仅返回 diff preview，不写入；默认 true\n" +
        "返回：目标 cell 的结构化变更摘要和 diff preview。";

    public ToolCategory Category => ToolCategory.Forge;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: true,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 12_288);

    public IReadOnlyList<int> AllowedStages => [3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var relativePath = arguments.GetValueOrDefault("path")?.ToString()?.TrimStart('/', '\\');
        var operation = arguments.GetValueOrDefault("operation")?.ToString() ?? "replace_source";
        var source = arguments.GetValueOrDefault("source")?.ToString();
        var cellType = arguments.GetValueOrDefault("cell_type")?.ToString() ?? "code";
        var dryRun = arguments.GetValueOrDefault("dry_run")?.ToString()?.Equals("false", StringComparison.OrdinalIgnoreCase) != true;

        if ((string.IsNullOrWhiteSpace(workspacePath) && string.IsNullOrWhiteSpace(projectRoot)) ||
            string.IsNullOrWhiteSpace(relativePath))
        {
            return CodexToolResult.Error("Missing workspace_path or path.");
        }

        if (!int.TryParse(arguments.GetValueOrDefault("cell_index")?.ToString(), out var cellIndex))
        {
            return CodexToolResult.Error("Missing or invalid cell_index.");
        }

        if (!relativePath.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase))
        {
            return CodexToolResult.Error("notebook_edit only supports .ipynb files.");
        }

        var normalizedOperation = operation.Trim().ToLowerInvariant();
        if (normalizedOperation is not ("replace_source" or "insert_cell" or "delete_cell"))
        {
            return CodexToolResult.Error("operation must be replace_source, insert_cell, or delete_cell.");
        }

        if (normalizedOperation is "replace_source" or "insert_cell" && source == null)
        {
            return CodexToolResult.Error("Missing source.");
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
            return CodexToolResult.Error("Notebook file does not exist.");
        }

        try
        {
            var originalJson = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
            var notebook = JsonNode.Parse(originalJson, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) as JsonObject;
            if (notebook == null || notebook["cells"] is not JsonArray cells)
            {
                return CodexToolResult.Error("Invalid notebook: root object with cells array is required.");
            }

            var beforeSource = cellIndex >= 0 && cellIndex < cells.Count
                ? ExtractSource(cells[cellIndex])
                : string.Empty;

            ApplyOperation(cells, normalizedOperation, cellIndex, source ?? string.Empty, cellType);

            var afterSource = normalizedOperation == "delete_cell"
                ? string.Empty
                : ExtractSource(cells[Math.Clamp(cellIndex, 0, Math.Max(0, cells.Count - 1))]);
            var preview = BuildPreview(normalizedOperation, cellIndex, beforeSource, afterSource);
            var updatedJson = notebook.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

            if (!dryRun)
            {
                await File.WriteAllTextAsync(fullPath, updatedJson, ct).ConfigureAwait(false);
                await NotifyRefreshAsync(baseRoot, arguments, normalizedPath, ct).ConfigureAwait(false);
            }

            StructuredLog.Information(logger, "notebook_edit: {Operation} {Path} cell {CellIndex} dryRun={DryRun}", normalizedOperation, normalizedPath, cellIndex, dryRun);
            return CodexToolResult.Succeeded(
                $"{(dryRun ? "DRY RUN: " : "✅ ")}notebook_edit {normalizedOperation} {normalizedPath} cell {cellIndex}\n{preview}",
                new
                {
                    FilePath = normalizedPath,
                    Operation = normalizedOperation,
                    CellIndex = cellIndex,
                    CellCount = cells.Count,
                    DryRun = dryRun
                },
                summary: $"notebook_edit {normalizedPath}: {normalizedOperation} cell {cellIndex}{(dryRun ? " dry-run" : "")}");
        }
        catch (JsonException ex)
        {
            return CodexToolResult.Error($"Invalid notebook JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "notebook_edit failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(logger, ex, "notebook_edit failed: {Path}", normalizedPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return CodexToolResult.Error(ex.Message);
        }
    }

    private static void ApplyOperation(JsonArray cells, string operation, int cellIndex, string source, string cellType)
    {
        switch (operation)
        {
            case "replace_source":
                if (cellIndex < 0 || cellIndex >= cells.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(cellIndex), "cell_index is outside notebook cells range.");
                }

                if (cells[cellIndex] is not JsonObject cell)
                {
                    throw new ArgumentOutOfRangeException(nameof(cellIndex), "target cell is not a JSON object.");
                }

                cell["source"] = BuildSourceArray(source);
                break;

            case "insert_cell":
                if (cellIndex < 0 || cellIndex > cells.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(cellIndex), "cell_index is outside notebook insertion range.");
                }

                cells.Insert(cellIndex, BuildCell(cellType, source));
                break;

            case "delete_cell":
                if (cellIndex < 0 || cellIndex >= cells.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(cellIndex), "cell_index is outside notebook cells range.");
                }

                cells.RemoveAt(cellIndex);
                break;
        }
    }

    private static JsonObject BuildCell(string cellType, string source)
    {
        var normalizedCellType = cellType.Equals("markdown", StringComparison.OrdinalIgnoreCase)
            ? "markdown"
            : "code";

        var cell = new JsonObject
        {
            ["cell_type"] = normalizedCellType,
            ["metadata"] = new JsonObject(),
            ["source"] = BuildSourceArray(source)
        };

        if (normalizedCellType == "code")
        {
            cell["execution_count"] = null;
            cell["outputs"] = new JsonArray();
        }

        return cell;
    }

    private static JsonArray BuildSourceArray(string source)
    {
        var array = new JsonArray();
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            array.Add(i < lines.Length - 1 ? line + "\n" : line);
        }

        return array;
    }

    private static string ExtractSource(JsonNode? cell)
    {
        if (cell is not JsonObject obj || obj["source"] is not { } source)
        {
            return string.Empty;
        }

        if (source is JsonArray array)
        {
            return string.Concat(array.Select(item => item?.GetValue<string>() ?? string.Empty));
        }

        return source.GetValue<string>();
    }

    private static string BuildPreview(string operation, int cellIndex, string beforeSource, string afterSource)
    {
        var builder = new StringBuilder();
        builder.AppendLine("```diff");
        builder.AppendLine($"# {operation} cell {cellIndex}");
        foreach (var line in beforeSource.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length > 0)
            {
                builder.Append("- ").AppendLine(line);
            }
        }

        foreach (var line in afterSource.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length > 0)
            {
                builder.Append("+ ").AppendLine(line);
            }
        }

        builder.AppendLine("```");
        return builder.ToString();
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
}
