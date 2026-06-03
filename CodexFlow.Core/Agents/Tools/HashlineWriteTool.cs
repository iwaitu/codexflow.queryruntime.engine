using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Hashline.Abstractions;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Models;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

public sealed class HashlineWriteTool : ICodexTool
{
    private readonly ApplyPatchTool _inner;
    private readonly IHashlineFileService? _hashlineService;

    public HashlineWriteTool(ApplyPatchTool inner, IHashlineFileService? hashlineService = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _hashlineService = hashlineService;
    }

    public string Name => "hs_write";

    public string Description => "Hashline 专用精准写入工具。固定使用 Hashline 编辑模式修改既有文件，避免再手动拼 `edit_mode` / `request` 外层结构。\n" +
        "参数（JSON object）：\n" +
        "  - filePath (string, 必填): 目标文件路径，必须与最近一次 hs_read/ivilson_read(hashline) 对应\n" +
        "  - snapshotId (string, 必填): Hashline 快照 ID\n" +
        "  - fileFingerprint (string, 必填): 文件指纹\n" +
        "  - operations (array, 必填): Hashline 操作数组，至少包含 1 个操作对象\n" +
        "  - oldString/newString (string, 可选): Claude Edit 风格的简化替换入口；当 operations 缺失时，runtime 会基于文件快照自动生成 replace_range 操作\n" +
        "  - replaceAll (bool, 可选): 与 oldString/newString 配合，替换所有匹配项；默认 false，默认要求 oldString 唯一\n" +
        "  - dryRun (bool, 可选): true 表示仅验证不落盘\n" +
        "返回：Hashline 编辑结果。\n" +
        "Few-shot:\n" +
        "  hs_write({\"filePath\":\"src/CleanApp/Program.cs\",\"oldString\":\"Pending\",\"newString\":\"Ready\"})\n" +
        "  hs_write({\"filePath\":\"src/CleanApp/Program.cs\",\"snapshotId\":\"snap_xxx\",\"fileFingerprint\":\"fp_xxx\",\"operations\":[{\"type\":\"insert_after\",\"targetLine\":22,\"targetAnchorId\":\"CC33DD44\",\"newLines\":[\"app.UseAuthorization();\"]}]})";

    public ToolCategory Category => ToolCategory.Forge;

    public IReadOnlyList<int> AllowedStages => _inner.AllowedStages;

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var delegated = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        CopyIfPresent(arguments, delegated, "worker_id", "session_id", "workspace_path", "project_root");
        delegated["edit_mode"] = "hashline";

        if (arguments.TryGetValue("request", out var request) && request is not null)
        {
            delegated["request"] = request;
            return await _inner.ExecuteAsync(delegated, ct).ConfigureAwait(false);
        }

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        payload["filePath"] = NormalizeHashlineFilePathArgument(
            arguments,
            arguments.GetValueOrDefault("filePath") ?? arguments.GetValueOrDefault("path"));
        payload["snapshotId"] = arguments.GetValueOrDefault("snapshotId");
        payload["fileFingerprint"] = arguments.GetValueOrDefault("fileFingerprint");

        if (arguments.TryGetValue("dryRun", out var dryRun))
        {
            payload["dryRun"] = dryRun;
        }

        if (arguments.TryGetValue("operations", out var operations))
        {
            payload["operations"] = NormalizeJsonLikeValue(operations);
        }
        else if (TryGetString(arguments, "oldString", "old_string", out var oldString) &&
                 TryGetString(arguments, "newString", "new_string", out var newString))
        {
            var simplePayload = await TryBuildSimpleReplacementPayloadAsync(arguments, payload, oldString, newString, ct)
                .ConfigureAwait(false);
            if (simplePayload.Error != null)
            {
                return CodexToolResult.Error(simplePayload.Error);
            }

            payload = simplePayload.Payload!;
        }

        delegated["request"] = payload;
        return await _inner.ExecuteAsync(delegated, ct).ConfigureAwait(false);
    }

    private async Task<(Dictionary<string, object?>? Payload, string? Error)> TryBuildSimpleReplacementPayloadAsync(
        Dictionary<string, object?> arguments,
        Dictionary<string, object?> payload,
        string oldString,
        string newString,
        CancellationToken ct)
    {
        if (_hashlineService == null)
        {
            return (null, "[HASHLINE_SIMPLE_REPLACE_UNAVAILABLE] hs_write oldString/newString 简化入口需要 Hashline 服务；请改用 operations。");
        }

        if (string.IsNullOrEmpty(oldString))
        {
            return (null, "[HASHLINE_SIMPLE_REPLACE_INVALID] oldString 不能为空。若要插入内容，请使用 operations 的 insert_before/insert_after。");
        }

        var filePath = NormalizeHashlineFilePathArgument(
            arguments,
            payload.GetValueOrDefault("filePath") ?? arguments.GetValueOrDefault("path"))?.ToString();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return (null, "[HASHLINE_SIMPLE_REPLACE_INVALID] filePath 不能为空。");
        }

        var workspaceRoot = ToolPathResolver.ResolveBaseRoot(
            arguments.GetValueOrDefault("workspace_path")?.ToString(),
            arguments.GetValueOrDefault("project_root")?.ToString());
        var fullPath = !Path.IsPathRooted(filePath) && !string.IsNullOrWhiteSpace(workspaceRoot)
            ? Path.GetFullPath(Path.Combine(workspaceRoot, filePath))
            : filePath;

        var snapshot = await _hashlineService.ReadAsync(fullPath, workspaceRoot, ct: ct).ConfigureAwait(false);
        var replaceAll = TryGetBool(arguments.GetValueOrDefault("replaceAll")) ??
                         TryGetBool(arguments.GetValueOrDefault("replace_all")) ??
                         false;
        var operations = BuildSimpleReplacementOperations(snapshot, oldString, newString, replaceAll, out var error);
        if (operations == null)
        {
            return (null, error);
        }

        var result = new Dictionary<string, object?>(payload, StringComparer.OrdinalIgnoreCase)
        {
            ["filePath"] = filePath,
            ["snapshotId"] = string.IsNullOrWhiteSpace(payload.GetValueOrDefault("snapshotId")?.ToString())
                ? snapshot.SnapshotId
                : payload["snapshotId"],
            ["fileFingerprint"] = string.IsNullOrWhiteSpace(payload.GetValueOrDefault("fileFingerprint")?.ToString())
                ? snapshot.FileFingerprint
                : payload["fileFingerprint"],
            ["operations"] = operations
        };

        return (result, null);
    }

    private static object? NormalizeHashlineFilePathArgument(Dictionary<string, object?> arguments, object? pathValue)
    {
        var filePath = pathValue?.ToString();
        if (string.IsNullOrWhiteSpace(filePath) || Path.IsPathRooted(filePath))
        {
            return pathValue;
        }

        var workspaceRoot = ToolPathResolver.ResolveBaseRoot(
            arguments.GetValueOrDefault("workspace_path")?.ToString(),
            arguments.GetValueOrDefault("project_root")?.ToString());
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return pathValue;
        }

        var normalized = ToolPathResolver.NormalizeDuplicateRepoPrefix(filePath, workspaceRoot);
        return string.IsNullOrWhiteSpace(normalized) ? pathValue : normalized;
    }

    private static List<Dictionary<string, object?>>? BuildSimpleReplacementOperations(
        FileSnapshot snapshot,
        string oldString,
        string newString,
        bool replaceAll,
        out string? error)
    {
        error = null;
        var oldLines = SplitEditText(oldString);
        var newLines = SplitEditText(newString);
        if (oldLines.Count == 0)
        {
            error = "[HASHLINE_SIMPLE_REPLACE_INVALID] oldString 不能为空。";
            return null;
        }

        if (oldLines.Count == 1)
        {
            var matches = new List<(int LineIndex, string NewLine)>();
            for (var i = 0; i < snapshot.Lines.Count; i++)
            {
                var raw = snapshot.Lines[i].RawText;
                if (!raw.Contains(oldString, StringComparison.Ordinal))
                {
                    continue;
                }

                var occurrenceCount = CountOccurrences(raw, oldString);
                if (!replaceAll && occurrenceCount > 1)
                {
                    error = $"[HASHLINE_SIMPLE_REPLACE_AMBIGUOUS] oldString 在第 {i + 1} 行出现 {occurrenceCount} 次；请提供更长的 oldString 或设置 replaceAll=true。";
                    return null;
                }

                var replaced = replaceAll
                    ? raw.Replace(oldString, newString, StringComparison.Ordinal)
                    : ReplaceFirst(raw, oldString, newString);
                matches.Add((i, replaced));
            }

            if (matches.Count == 0)
            {
                error = "[HASHLINE_SIMPLE_REPLACE_NOT_FOUND] oldString 未在文件中找到。请先用 hs_read 确认最新内容。";
                return null;
            }

            if (!replaceAll && matches.Count > 1)
            {
                error = $"[HASHLINE_SIMPLE_REPLACE_AMBIGUOUS] oldString 在 {matches.Count} 行中出现；请提供更长的 oldString 或设置 replaceAll=true。";
                return null;
            }

            return matches
                .Select(match =>
                {
                    var line = snapshot.Lines[match.LineIndex];
                    return new Dictionary<string, object?>
                    {
                        ["type"] = "replace_range",
                        ["startLine"] = line.LineNumber,
                        ["startAnchorId"] = line.AnchorId,
                        ["endLine"] = line.LineNumber,
                        ["endAnchorId"] = line.AnchorId,
                        ["newLines"] = new[] { match.NewLine }
                    };
                })
                .ToList();
        }

        var rangeMatches = new List<(int StartIndex, int EndIndex)>();
        for (var i = 0; i <= snapshot.Lines.Count - oldLines.Count; i++)
        {
            var matched = true;
            for (var j = 0; j < oldLines.Count; j++)
            {
                if (!string.Equals(snapshot.Lines[i + j].RawText, oldLines[j], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                rangeMatches.Add((i, i + oldLines.Count - 1));
            }
        }

        if (rangeMatches.Count == 0)
        {
            error = "[HASHLINE_SIMPLE_REPLACE_NOT_FOUND] oldString 多行片段未在文件中找到。请先用 hs_read 确认最新内容。";
            return null;
        }

        if (!replaceAll && rangeMatches.Count > 1)
        {
            error = $"[HASHLINE_SIMPLE_REPLACE_AMBIGUOUS] oldString 多行片段出现 {rangeMatches.Count} 次；请提供更长上下文或设置 replaceAll=true。";
            return null;
        }

        return rangeMatches
            .Select(match =>
            {
                var start = snapshot.Lines[match.StartIndex];
                var end = snapshot.Lines[match.EndIndex];
                if (newLines.Count == 0)
                {
                    return new Dictionary<string, object?>
                    {
                        ["type"] = "delete_range",
                        ["startLine"] = start.LineNumber,
                        ["startAnchorId"] = start.AnchorId,
                        ["endLine"] = end.LineNumber,
                        ["endAnchorId"] = end.AnchorId
                    };
                }

                return new Dictionary<string, object?>
                {
                    ["type"] = "replace_range",
                    ["startLine"] = start.LineNumber,
                    ["startAnchorId"] = start.AnchorId,
                    ["endLine"] = end.LineNumber,
                    ["endAnchorId"] = end.AnchorId,
                    ["newLines"] = newLines
                };
            })
            .ToList();
    }

    private static List<string> SplitEditText(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.Length == 0
            ? []
            : normalized.Split('\n').ToList();
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0
            ? text
            : text[..index] + newValue + text[(index + oldValue.Length)..];
    }

    private static bool TryGetString(
        Dictionary<string, object?> arguments,
        string primaryKey,
        string alternateKey,
        out string value)
    {
        value = string.Empty;
        if (!arguments.TryGetValue(primaryKey, out var raw) &&
            !arguments.TryGetValue(alternateKey, out raw))
        {
            return false;
        }

        value = raw?.ToString() ?? string.Empty;
        return !string.IsNullOrEmpty(value);
    }

    private static bool? TryGetBool(object? value)
    {
        return value switch
        {
            null => null,
            bool boolValue => boolValue,
            string text when bool.TryParse(text, out var parsed) => parsed,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            _ => null
        };
    }

    private static object? NormalizeJsonLikeValue(object? value)
    {
        return value switch
        {
            null => null,
            JsonElement jsonElement => JsonElementToObject(jsonElement),
            JObject jsonObject => jsonObject.Properties().ToDictionary(
                static property => property.Name,
                static property => NormalizeJsonLikeValue(property.Value),
                StringComparer.OrdinalIgnoreCase),
            JArray jsonArray => jsonArray.Select(NormalizeJsonLikeValue).ToList(),
            JValue jsonValue => jsonValue.Value,
            IDictionary<string, object?> typedDictionary => typedDictionary.ToDictionary(
                static pair => pair.Key,
                static pair => NormalizeJsonLikeValue(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            IDictionary dictionary => NormalizeDictionary(dictionary),
            string => value,
            IEnumerable enumerable => NormalizeEnumerable(enumerable),
            _ => value
        };
    }

    private static Dictionary<string, object?> NormalizeDictionary(IDictionary dictionary)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is null)
            {
                continue;
            }

            result[entry.Key.ToString() ?? string.Empty] = NormalizeJsonLikeValue(entry.Value);
        }

        return result;
    }

    private static List<object?> NormalizeEnumerable(IEnumerable enumerable)
    {
        var result = new List<object?>();
        foreach (var item in enumerable)
        {
            result.Add(NormalizeJsonLikeValue(item));
        }

        return result;
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt32(out var intVal) ? intVal : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.Object => JsonElementToDictionary(element),
            _ => element.ToString()
        };
    }

    private static Dictionary<string, object?> JsonElementToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            dict[property.Name] = JsonElementToObject(property.Value);
        }

        return dict;
    }

    private static void CopyIfPresent(
        Dictionary<string, object?> source,
        Dictionary<string, object?> destination,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (source.TryGetValue(key, out var value) && value is not null)
            {
                destination[key] = value;
            }
        }
    }
}
