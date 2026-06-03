using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Hashline.Constants;
using CodexFlow.Core.Hashline.Infrastructure;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.LanguageServices;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 在工作区应用补丁（替代 MCP apply_patch）。
/// 支持 unified diff 格式和 Hashline 编辑模式。
/// </summary>
public class ApplyPatchTool : ICodexTool
{
    private readonly IGitService _gitService;
    private readonly IHashlineFileService? _hashlineService;
    private readonly HashlineOptions? _hashlineOptions;
    private readonly ILanguageServiceRefreshNotifier? _refreshNotifier;
    private readonly ILogger<ApplyPatchTool> _logger;

    public ApplyPatchTool(
        IGitService gitService,
        IHashlineFileService? hashlineService,
        HashlineOptions? hashlineOptions,
        ILanguageServiceRefreshNotifier? refreshNotifier,
        ILogger<ApplyPatchTool> logger)
    {
        _gitService = gitService;
        _hashlineService = hashlineService;
        _hashlineOptions = hashlineOptions;
        _refreshNotifier = refreshNotifier;
        _logger = logger;
    }

    // Backward-compatible constructor
    public ApplyPatchTool(IGitService gitService, IHashlineFileService? hashlineService, ILogger<ApplyPatchTool> logger)
        : this(gitService, hashlineService, null, null, logger)
    {
    }

    public ApplyPatchTool(
        IGitService gitService,
        IHashlineFileService? hashlineService,
        HashlineOptions? hashlineOptions,
        ILogger<ApplyPatchTool> logger)
        : this(gitService, hashlineService, hashlineOptions, null, logger)
    {
    }

    private sealed record CodexPatchSection(string Kind, string Path, List<string> BodyLines);

    public string Name => "apply_patch";
    public string Description => "对工作区文件应用 unified diff 补丁或 Hashline 结构化编辑。\n" +
        "\n" +
        "【参数说明】\n" +
        "  - patch (string): unified diff 格式的补丁内容（传统模式）\n" +
        "  - edit_mode (string, 可选): \"hashline\" 启用 Hashline 编辑模式\n" +
        "  - request (object, 可选): Hashline 编辑请求对象，包含：\n" +
        "    - filePath: 文件路径\n" +
        "    - snapshotId: 快照 ID\n" +
        "    - fileFingerprint: 文件指纹\n" +
        "    - dryRun: 是否仅验证不落盘\n" +
        "    - operations: 编辑操作列表（必须是 JSON 数组，数组元素必须是操作对象；禁止传 {}、禁止空数组、禁止多层嵌套数组）\n" +
        "\n" +
        "【Hashline 模式 - 无 Fallback 规则】\n" +
        "若你已通过 ivilson_read(..., mode=\"hashline\") 读取文件，并需要对既有文件做精准修改，\n" +
        "必须使用 Hashline 模式提交 operations，不得回退到 unified diff。\n" +
        "\n" +
        "【错误码与修复指引】\n" +
        "当 Hashline 编辑失败，错误码含义如下：\n" +
        "  - FILE_FINGERPRINT_MISMATCH: 文件被并发修改 → 必须重新 ivilson_read({\"path\":\"...\", \"mode\":\"hashline\"})\n" +
        "  - ANCHOR_MISMATCH: 行锚点不匹配 → 必须重新读取快照，禁止猜测旧内容\n" +
        "  - ANCHOR_NOT_FOUND: 锚点未找到 → 检查 anchorId 是否正确，重新读取获取\n" +
        "  - LINE_OUT_OF_RANGE: 行号超出范围 → 重新读取获取正确行号\n" +
        "  - INVALID_OPERATION_TYPE: 操作类型非法 → 使用 replace_range/insert_after/insert_before/delete_range\n" +
        "  - OVERLAPPING_OPERATIONS: 操作区间重叠 → 按行号顺序排列，确保不重叠\n" +
        "\n" +
        "【禁止行为】\n" +
        "  - ❌ 不得复述旧文本，不得猜测旧上下文\n" +
        "  - ❌ 不得在 fingerprint/anchor 失败后继续尝试猜测\n" +
        "  - ❌ 必须重新读取快照获取最新 anchorId\n" +
        "\n" +
        "【Few-shot 示例】\n" +
        "传统模式：\n" +
        "  apply_patch({\"patch\":\"diff --git a/a.txt b/a.txt\\n--- a/a.txt\\n+++ b/a.txt\\n@@ -1 +1 @@\\n-old\\n+new\"})\n" +
        "\n" +
        "Hashline 模式（推荐用于既有文件精准编辑）：\n" +
        "  apply_patch({\n" +
        "    \"edit_mode\":\"hashline\",\n" +
        "    \"request\":{\n" +
        "      \"filePath\":\"src/Program.cs\",\n" +
        "      \"snapshotId\":\"snap_xxx\",\n" +
        "      \"fileFingerprint\":\"fp_xxx\",\n" +
        "      \"dryRun\":false,\n" +
        "      \"operations\":[\n" +
        "        {\"type\":\"insert_after\",\"targetLine\":2,\"targetAnchorId\":\"CC22DD11\",\"newLines\":[\"app.MapGet(\\\"/health\\\", () => Results.Ok());\"]}\n" +
        "      ]\n" +
        "    }\n" +
        "  })\n" +
        "\n" +
        "Hashline 模式（修改 .csproj 引用/包）：\n" +
        "  apply_patch({\n" +
        "    \"edit_mode\":\"hashline\",\n" +
        "    \"request\":{\n" +
        "      \"filePath\":\"src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj\",\n" +
        "      \"snapshotId\":\"snap_infra\",\n" +
        "      \"fileFingerprint\":\"fp_infra\",\n" +
        "      \"operations\":[\n" +
        "        {\"type\":\"replace_range\",\"startLine\":9,\"startAnchorId\":\"AA11\",\"endLine\":13,\"endAnchorId\":\"BB22\",\"newLines\":[\"  <ItemGroup>\",\"    <ProjectReference Include=\\\"..\\\\CleanApp.Core\\\\CleanApp.Core.csproj\\\" />\",\"    <PackageReference Include=\\\"MongoDB.Driver\\\" Version=\\\"3.2.1\\\" />\",\"  </ItemGroup>\"]}\n" +
        "      ]\n" +
        "    }\n" +
        "  })\n" +
        "\n" +
        "反例（禁止空 operations）：\n" +
        "  apply_patch({\"edit_mode\":\"hashline\",\"request\":{\"filePath\":\"src/CleanApp/Program.cs\",\"operations\":{}}})  ❌\n" +
        "  apply_patch({\"edit_mode\":\"hashline\",\"request\":{\"filePath\":\"src/CleanApp/Program.cs\",\"operations\":[]}})  ❌";
    public ToolCategory Category => ToolCategory.Forge;
    public IReadOnlyList<int> AllowedStages => [3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var editMode = arguments.GetValueOrDefault("edit_mode")?.ToString()?.ToLowerInvariant();
        var hasRequest = arguments.ContainsKey("request") && arguments["request"] != null;

        // 检查是否应该使用 Hashline 模式
        var shouldUseHashline = ShouldUseHashlineMode(editMode, hasRequest, arguments);

        if (shouldUseHashline && _hashlineService != null)
        {
            return await ExecuteHashlineModeAsync(arguments, ct).ConfigureAwait(false);
        }

        // 传统 unified diff 模式
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var patchContent = arguments.GetValueOrDefault("patch")?.ToString()
                        ?? arguments.GetValueOrDefault("patch_content")?.ToString();

        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(baseRoot))
            return CodexToolResult.Error("Missing workspace_path.");
        if (string.IsNullOrEmpty(patchContent))
            return CodexToolResult.Error("Missing patch content.");

        // 检查高风险文件是否需要 Hashline
        if (_hashlineOptions?.ShouldRequireHashlineForHighRiskFiles() == true)
        {
            // 从 patch 内容中提取目标文件路径
            var targetFile = ExtractTargetFileFromPatch(patchContent);
            if (!string.IsNullOrEmpty(targetFile) && HighRiskFileDetector.IsHighRiskFile(targetFile))
            {
                return CodexToolResult.Error(
                    $"[HIGH_RISK_FILE_REQUIRES_HASHLINE] 高风险文件 '{targetFile}' 必须使用 Hashline 模式编辑。\n" +
                    $"请先 ivilson_read({{\"path\":\"{targetFile}\", \"mode\":\"hashline\"}}) 获取快照，再使用 edit_mode=\"hashline\" 编辑。");
            }
        }

        try
        {
            var normalizedPatch = ToolPathResolver.NormalizeDuplicateRepoPrefixInPatch(patchContent, baseRoot);
            if (!string.Equals(normalizedPatch, patchContent, StringComparison.Ordinal))
            {
                StructuredLog.Information(_logger, "apply_patch: normalized duplicated repo prefix in patch content for root {Path}", baseRoot);
            }

            var cleanedPatch = PatchPayloadNormalizer.NormalizeTraditionalPatch(normalizedPatch, out var removedDuplicateEndPatchCount);
            if (removedDuplicateEndPatchCount > 0)
            {
                StructuredLog.Warning(_logger, "apply_patch: removed {Count} duplicate '*** End Patch' marker(s) before validation", removedDuplicateEndPatchCount);
            }

            if (PatchPayloadNormalizer.LooksLikeCodexPatchEnvelope(cleanedPatch))
            {
                var codexPatchResult = await TryApplyCodexPatchAsync(baseRoot, cleanedPatch, ct).ConfigureAwait(false);
                if (codexPatchResult is not null)
                {
                    return codexPatchResult;
                }

                StructuredLog.Warning(_logger, "apply_patch: unsupported Codex patch envelope detected in {Path}; instruct caller to use smart patch", baseRoot);
                return CodexToolResult.Error("❌ apply_patch 检测到 Codex Patch 格式，但无法可靠解析应用。请改用 ivilson_smart_patch 或 write_file。");
            }

            if (!PatchPayloadNormalizer.TryValidateUnifiedDiff(cleanedPatch, out var validationError))
            {
                StructuredLog.Warning(_logger, "apply_patch: unrecognized patch format in {Path}; does not look like unified diff", baseRoot);
                return CodexToolResult.Error($"❌ apply_patch 收到的补丁不是有效的 unified diff：{validationError} 请改用 ivilson_smart_patch 或修正 hunk 结构。");
            }

            var success = await _gitService.ApplyPatchAsync(baseRoot, cleanedPatch).ConfigureAwait(false);
            if (success)
            {
                await NotifyRefreshAsync(baseRoot, arguments, ExtractChangedPathsFromPatch(cleanedPatch), ct).ConfigureAwait(false);
                StructuredLog.Information(_logger, "apply_patch: succeeded in {Path}", baseRoot);
                return CodexToolResult.Succeeded("✅ 补丁应用成功。");
            }
            else
            {
                StructuredLog.Warning(_logger, "apply_patch: failed in {Path}", baseRoot);
                return CodexToolResult.Error("❌ 补丁应用失败：检测到内容冲突或格式错误。");
            }
        }
        catch (IOException ex)
        {
            StructuredLog.Error(_logger, ex, "apply_patch failed");
            return CodexToolResult.Error($"apply_patch 异常: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(_logger, ex, "apply_patch failed");
            return CodexToolResult.Error($"apply_patch 异常: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            StructuredLog.Error(_logger, ex, "apply_patch failed");
            return CodexToolResult.Error($"apply_patch 异常: {ex.Message}");
        }
    }

    private async Task<CodexToolResult> ExecuteHashlineModeAsync(Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var requestObj = arguments.GetValueOrDefault("request");
        if (requestObj == null)
        {
            return CodexToolResult.Error("Hashline 模式需要提供 request 参数。");
        }

        // 获取 workspace root 用于路径安全检查
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var workspaceRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);

        try
        {
            // 解析请求
            var parseResult = ParseHashlineEditRequest(requestObj, arguments);
            if (parseResult.Request == null)
            {
                var errorText = parseResult.Errors.Count > 0
                    ? string.Join("\n", parseResult.Errors)
                    : "无法解析 Hashline 编辑请求。";
                return CodexToolResult.Error($"[HASHLINE_REQUEST_INVALID] 无法解析 Hashline 编辑请求。\n{errorText}\n\n{BuildHashlineRequestFormatGuidance()}");
            }

            if (parseResult.Request.Operations.Count == 0)
            {
                var errorText = parseResult.Errors.Count > 0
                    ? string.Join("\n", parseResult.Errors)
                    : "request.operations 中没有任何合法 operation。";
                return CodexToolResult.Error($"[HASHLINE_REQUEST_INVALID] Hashline 编辑请求未包含合法操作。\n{errorText}\n\n{BuildHashlineRequestFormatGuidance()}");
            }

            // 执行编辑
            var result = await _hashlineService!.EditAsync(parseResult.Request, workspaceRoot, ct).ConfigureAwait(false);

            if (result.Success)
            {
                if (!result.DryRun)
                {
                    var normalizedPath = workspaceRoot == null
                        ? parseResult.Request.FilePath.Replace('\\', '/')
                        : Path.GetRelativePath(workspaceRoot, parseResult.Request.FilePath).Replace('\\', '/');
                    await NotifyRefreshAsync(workspaceRoot ?? string.Empty, arguments, [normalizedPath], ct).ConfigureAwait(false);
                }

                var message = result.DryRun
                    ? "✅ Hashline 编辑验证成功（DryRun）。"
                    : "✅ Hashline 编辑应用成功。";

                if (!string.IsNullOrEmpty(result.UnifiedDiff))
                {
                    message += $"\n\nDiff:\n{result.UnifiedDiff}";
                }

                return CodexToolResult.Succeeded(message, new
                {
                    Success = true,
                    OldFingerprint = result.OldFingerprint,
                    NewFingerprint = result.NewFingerprint,
                    Hunks = result.Hunks.Count
                });
            }
            else
            {
                // 检查是否为 fingerprint/anchor mismatch 类型错误，需要添加特定前缀供 Orchestrator 检测
                var isMismatchError = result.ErrorCode == HashlineErrorCodes.FileFingerprintMismatch ||
                                      result.ErrorCode == HashlineErrorCodes.AnchorMismatch ||
                                      result.ErrorCode == HashlineErrorCodes.LineOutOfRange ||
                                      result.ErrorCode == HashlineErrorCodes.AnchorNotFound;

                var errorPrefix = isMismatchError ? "[HASHLINE_MISMATCH_FAILURE] " : "";
                return CodexToolResult.Error($"{errorPrefix}❌ Hashline 编辑失败: {result.ErrorCode} - {result.ErrorMessage}\n" +
                    "你必须重新 ivilson_read({\"path\":\"...\", \"mode\":\"hashline\"}) 获取最新快照，禁止猜测 anchorId。");
            }
        }
        catch (Exception ex)
        {
            StructuredLog.Error(_logger, ex, "Hashline edit failed");
            return CodexToolResult.Error($"Hashline 编辑异常: {ex.Message}");
        }
    }

    private static ParseHashlineResult ParseHashlineEditRequest(object? requestObj, Dictionary<string, object?> arguments)
    {
        var result = new ParseHashlineResult();
        var dict = UnwrapParameterDictionary(requestObj, "filePath", "operations", "snapshotId", "fileFingerprint");
        if (dict == null)
        {
            result.Errors.Add("request 不是合法的对象，或无法从 input_params/arguments 包装中解出参数字典。");
            return result;
        }

        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);

        var filePath = dict.GetValueOrDefault("filePath")?.ToString();
        if (string.IsNullOrEmpty(filePath))
        {
            result.Errors.Add("request.filePath 缺失或为空。");
            return result;
        }

        // 如果是相对路径，转换为绝对路径
        if (!Path.IsPathRooted(filePath) && !string.IsNullOrEmpty(baseRoot))
        {
            filePath = ToolPathResolver.NormalizeDuplicateRepoPrefix(filePath, baseRoot);
            filePath = Path.GetFullPath(Path.Combine(baseRoot, filePath));
        }

        var dryRun = TryGetBool(dict.GetValueOrDefault("dryRun")) ?? false;
        var request = new HashlineEditRequest
        {
            FilePath = filePath!,
            SnapshotId = dict.GetValueOrDefault("snapshotId")?.ToString() ?? string.Empty,
            FileFingerprint = dict.GetValueOrDefault("fileFingerprint")?.ToString() ?? string.Empty,
            DryRun = dryRun
        };

        var hasOperationsField = dict.ContainsKey("operations");
        if (!hasOperationsField)
        {
            result.Errors.Add("request.operations 缺失，必须提供非空数组。");
            return result;
        }

        if (!TryGetOperationCandidates(dict.GetValueOrDefault("operations"), out var operationCandidates, out var operationsError))
        {
            result.Errors.Add(operationsError ?? "request.operations 不是合法的非空数组。");
            return result;
        }

        for (var operationIndex = 0; operationIndex < operationCandidates.Count; operationIndex++)
        {
            var op = ParseEditOperationFromObject(operationCandidates[operationIndex], operationIndex, result.Errors);
            if (op != null)
            {
                request.Operations.Add(op);
            }
        }

        if (request.Operations.Count == 0)
        {
            if (result.Errors.Count == 0)
            {
                result.Errors.Add("request.operations 存在，但没有解析出任何合法 operation。");
            }

            return result;
        }

        result.Request = request;
        return result;
    }

    private static EditOperation? ParseEditOperationFromObject(object? opObj, int index, List<string> errors)
    {
        var opDict = UnwrapParameterDictionary(opObj, "type");
        if (opDict == null)
        {
            errors.Add($"operations[{index}] 不是合法的操作对象，或无法从 input_params/arguments 包装中解出参数字典。");
            return null;
        }

        return ParseEditOperation(opDict, index, errors);
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

    private static Dictionary<string, object?> JTokenToDictionary(JObject jsonObject)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in jsonObject.Properties())
        {
            dict[property.Name] = JTokenToObject(property.Value);
        }

        return dict;
    }

    private static object? JTokenToObject(JToken token)
    {
        return token switch
        {
            JObject jsonObject => JTokenToDictionary(jsonObject),
            JArray jsonArray => jsonArray.Select(JTokenToObject).ToList(),
            JValue jsonValue => jsonValue.Value,
            _ => token.ToString()
        };
    }

    private static bool TryParseRequestString(string requestText, out Dictionary<string, object?> dictionary)
    {
        dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(requestText))
        {
            return false;
        }

        var stripped = PatchPayloadNormalizer.StripMarkdownCodeFences(requestText).Trim();
        try
        {
            using var doc = JsonDocument.Parse(stripped);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            dictionary = JsonElementToDictionary(doc.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static EditOperation? ParseEditOperation(Dictionary<string, object?> opDict, int index, List<string> errors)
    {
        var type = opDict.GetValueOrDefault("type")?.ToString();
        if (string.IsNullOrEmpty(type))
        {
            errors.Add($"operations[{index}] 缺少必填字段 type。");
            return null;
        }

        var normalizedType = type.Trim();
        return normalizedType switch
        {
            "replace_range" => CreateReplaceRangeOperation(opDict, normalizedType, index, errors),
            "insert_after" => CreateInsertAfterOperation(opDict, normalizedType, index, errors),
            "insert_before" => CreateInsertBeforeOperation(opDict, normalizedType, index, errors),
            "delete_range" => CreateDeleteRangeOperation(opDict, normalizedType),
            "rewrite_whole_file" => CreateRewriteWholeFileOperation(opDict, normalizedType, index, errors),
            _ => RecordInvalidOperationType(normalizedType, index, errors)
        };
    }

    private static List<string> ParseStringList(object? obj)
    {
        if (obj is null)
        {
            return new List<string>();
        }

        if (obj is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
        {
            return jsonElement.EnumerateArray()
                .Select(item => item.ToString())
                .ToList();
        }

        if (obj is JArray jsonArray)
        {
            return jsonArray.Select(static item => item.ToString()).ToList();
        }

        if (obj is IEnumerable<object?> list)
        {
            return list
                .Select(item => item?.ToString())
                .Where(static item => item != null)
                .Cast<string>()
                .ToList();
        }

        return new List<string>();
    }

    private static int? TryGetInt(object? value)
    {
        return value switch
        {
            null => null,
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            double doubleValue when doubleValue is >= int.MinValue and <= int.MaxValue => (int)doubleValue,
            decimal decimalValue when decimalValue is >= int.MinValue and <= int.MaxValue => (int)decimalValue,
            JsonElement { ValueKind: JsonValueKind.Number } numberElement when numberElement.TryGetInt32(out var jsonIntValue) => jsonIntValue,
            JsonElement { ValueKind: JsonValueKind.String } stringElement when int.TryParse(stringElement.GetString(), out var parsed) => parsed,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => null
        };
    }

    private static bool TryGetOperationCandidates(object? operationsObj, out List<object?> candidates, out string? error)
    {
        candidates = new List<object?>();
        error = null;

        if (operationsObj is null)
        {
            error = "request.operations 缺失，必须提供非空数组。";
            return false;
        }

        if (operationsObj is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind != JsonValueKind.Array)
            {
                error = $"request.operations 必须是 JSON 数组，当前为 {jsonElement.ValueKind}。";
                return false;
            }

            candidates = jsonElement.EnumerateArray().Select(static child => (object?)child).ToList();
        }
        else if (operationsObj is JArray jsonArray)
        {
            candidates = jsonArray.Select(JTokenToObject).ToList();
        }
        else if (operationsObj is IEnumerable<object?> enumerable &&
                 operationsObj is not string &&
                 operationsObj is not Dictionary<string, object?> &&
                 operationsObj is not IDictionary<string, object?>)
        {
            candidates = enumerable.ToList();
        }
        else if (operationsObj is IEnumerable nonGenericEnumerable &&
                 operationsObj is not string &&
                 operationsObj is not IDictionary)
        {
            candidates = nonGenericEnumerable.Cast<object?>().ToList();
        }
        else
        {
            error = "request.operations 必须是非空数组，禁止传对象或其他类型。";
            return false;
        }

        if (candidates.Count == 0)
        {
            error = "request.operations 不能为空，必须至少包含一个操作对象。";
            return false;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (candidate is JsonElement childElement && childElement.ValueKind == JsonValueKind.Array)
            {
                error = $"operations[{i}] 不能是嵌套数组。";
                return false;
            }
            if (candidate is IEnumerable<object?> &&
                candidate is not string &&
                candidate is not Dictionary<string, object?> &&
                candidate is not IDictionary<string, object?> &&
                candidate is not JsonElement)
            {
                error = $"operations[{i}] 不能是嵌套数组。";
                return false;
            }
        }

        return true;
    }

    private static string BuildHashlineRequestFormatGuidance()
        => """
           推荐优先改用更扁平的 `hs_write({...})`。若仍使用 apply_patch，最小正确格式示例：
           {
             "edit_mode": "hashline",
             "request": {
               "filePath": "path/from/hashline/read",
               "snapshotId": "snap_xxx",
               "fileFingerprint": "fp_xxx",
               "operations": [
                 {
                   "type": "replace_range",
                   "startLine": 12,
                   "startAnchorId": "ANCHOR_START",
                   "endLine": 12,
                   "endAnchorId": "ANCHOR_END",
                   "newLines": ["replacement line"]
                 }
               ]
             }
           }
           不要传 `operations:{}`、不要传空数组、不要把 operations 再包成字符串或嵌套数组。
           先从 `hs_read({"path":"..."})` 或 `ivilson_read({"path":"...", "mode":"hashline"})` 复制真实的 line/anchor，再提交编辑请求。
           """;

    private static ReplaceRangeOperation? CreateReplaceRangeOperation(
        Dictionary<string, object?> opDict,
        string type,
        int index,
        List<string> errors)
    {
        var newLines = ParseStringList(opDict.GetValueOrDefault("newLines"));
        if (newLines.Count == 0)
        {
            errors.Add($"operations[{index}] type={type} 需要提供非空数组 newLines。");
            return null;
        }

        return new ReplaceRangeOperation
        {
            Type = type,
            StartLine = TryGetInt(opDict.GetValueOrDefault("startLine")) ?? 0,
            StartAnchorId = opDict.GetValueOrDefault("startAnchorId")?.ToString() ?? string.Empty,
            EndLine = TryGetInt(opDict.GetValueOrDefault("endLine")) ?? 0,
            EndAnchorId = opDict.GetValueOrDefault("endAnchorId")?.ToString() ?? string.Empty,
            NewLines = newLines
        };
    }

    private static InsertAfterOperation? CreateInsertAfterOperation(
        Dictionary<string, object?> opDict,
        string type,
        int index,
        List<string> errors)
    {
        var newLines = ParseStringList(opDict.GetValueOrDefault("newLines"));
        if (newLines.Count == 0)
        {
            errors.Add($"operations[{index}] type={type} 需要提供非空数组 newLines。");
            return null;
        }

        return new InsertAfterOperation
        {
            Type = type,
            TargetLine = TryGetInt(opDict.GetValueOrDefault("targetLine")) ?? 0,
            TargetAnchorId = opDict.GetValueOrDefault("targetAnchorId")?.ToString() ?? string.Empty,
            NewLines = newLines
        };
    }

    private static InsertBeforeOperation? CreateInsertBeforeOperation(
        Dictionary<string, object?> opDict,
        string type,
        int index,
        List<string> errors)
    {
        var newLines = ParseStringList(opDict.GetValueOrDefault("newLines"));
        if (newLines.Count == 0)
        {
            errors.Add($"operations[{index}] type={type} 需要提供非空数组 newLines。");
            return null;
        }

        return new InsertBeforeOperation
        {
            Type = type,
            TargetLine = TryGetInt(opDict.GetValueOrDefault("targetLine")) ?? 0,
            TargetAnchorId = opDict.GetValueOrDefault("targetAnchorId")?.ToString() ?? string.Empty,
            NewLines = newLines
        };
    }

    private static DeleteRangeOperation CreateDeleteRangeOperation(Dictionary<string, object?> opDict, string type)
    {
        return new DeleteRangeOperation
        {
            Type = type,
            StartLine = TryGetInt(opDict.GetValueOrDefault("startLine")) ?? 0,
            StartAnchorId = opDict.GetValueOrDefault("startAnchorId")?.ToString() ?? string.Empty,
            EndLine = TryGetInt(opDict.GetValueOrDefault("endLine")) ?? 0,
            EndAnchorId = opDict.GetValueOrDefault("endAnchorId")?.ToString() ?? string.Empty
        };
    }

    private static RewriteWholeFileOperation? CreateRewriteWholeFileOperation(
        Dictionary<string, object?> opDict,
        string type,
        int index,
        List<string> errors)
    {
        var newContent = opDict.GetValueOrDefault("newContent")?.ToString();
        if (string.IsNullOrEmpty(newContent))
        {
            errors.Add($"operations[{index}] type={type} 需要提供非空字段 newContent。");
            return null;
        }

        return new RewriteWholeFileOperation
        {
            Type = type,
            NewContent = newContent
        };
    }

    private static EditOperation? RecordInvalidOperationType(string type, int index, List<string> errors)
    {
        errors.Add($"operations[{index}] 包含不支持的 type='{type}'。");
        return null;
    }

    private static bool? TryGetBool(object? value)
    {
        return value switch
        {
            null => null,
            bool boolValue => boolValue,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } stringElement when bool.TryParse(stringElement.GetString(), out var parsed) => parsed,
            string stringValue when bool.TryParse(stringValue, out var parsed) => parsed,
            _ => null
        };
    }

    private static Dictionary<string, object?>? UnwrapParameterDictionary(object? source, params string[] expectedKeys)
    {
        var dict = ToDictionary(source);
        if (dict == null)
        {
            return null;
        }

        if (expectedKeys.Length == 0 || expectedKeys.Any(dict.ContainsKey))
        {
            return dict;
        }

        foreach (var wrapperKey in new[] { "input_params", "arguments" })
        {
            if (!dict.TryGetValue(wrapperKey, out var wrapped))
            {
                continue;
            }

            var unwrapped = UnwrapParameterDictionary(wrapped, expectedKeys);
            if (unwrapped != null)
            {
                return unwrapped;
            }
        }

        return dict;
    }

    private static Dictionary<string, object?>? ToDictionary(object? source)
    {
        if (source is Dictionary<string, object?> typedDict)
        {
            return new Dictionary<string, object?>(typedDict, StringComparer.OrdinalIgnoreCase);
        }

        if (source is IDictionary<string, object?> dictionary)
        {
            return new Dictionary<string, object?>(dictionary, StringComparer.OrdinalIgnoreCase);
        }

        if (source is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Object)
        {
            return JsonElementToDictionary(jsonElement);
        }

        if (source is JObject jsonObject)
        {
            return JTokenToDictionary(jsonObject);
        }

        if (source is string requestText &&
            TryParseRequestString(requestText, out var parsedDict))
        {
            return parsedDict;
        }

        return null;
    }

    private sealed class ParseHashlineResult
    {
        public HashlineEditRequest? Request { get; set; }
        public List<string> Errors { get; } = new();
    }

    private async Task<CodexToolResult?> TryApplyCodexPatchAsync(string baseRoot, string patchContent, CancellationToken ct)
    {
        try
        {
            var sections = ParseCodexPatchSections(patchContent);
            if (sections.Count == 0)
            {
                StructuredLog.Warning(_logger, "apply_patch: Codex patch envelope detected but no sections parsed");
                return CodexToolResult.Error("❌ Codex Patch 未包含可解析的 Update/Add/Delete File 段。");
            }

            var changedCount = 0;
            foreach (var section in sections)
            {
                ct.ThrowIfCancellationRequested();

                var normalizedRelPath = ToolPathResolver.NormalizeDuplicateRepoPrefix(section.Path, baseRoot)
                    .Replace('/', Path.DirectorySeparatorChar);
                var targetPath = Path.GetFullPath(Path.Combine(baseRoot, normalizedRelPath));
                if (!ToolPathResolver.IsWithinRoot(targetPath, baseRoot))
                {
                    StructuredLog.Warning(_logger, "apply_patch: Codex patch target escapes root. baseRoot={BaseRoot}, path={Path}", baseRoot, section.Path);
                    return CodexToolResult.Error($"❌ 补丁目标路径越界: {section.Path}");
                }

                switch (section.Kind)
                {
                    case "Update":
                        await ApplyCodexUpdateSectionAsync(targetPath, section).ConfigureAwait(false);
                        changedCount++;
                        break;
                    case "Add":
                        await ApplyCodexAddSectionAsync(targetPath, section).ConfigureAwait(false);
                        changedCount++;
                        break;
                    case "Delete":
                        ApplyCodexDeleteSection(targetPath);
                        changedCount++;
                        break;
                    default:
                        StructuredLog.Warning(_logger, "apply_patch: unsupported Codex patch section kind {Kind}", section.Kind);
                        return CodexToolResult.Error($"❌ 不支持的 Codex Patch 段类型: {section.Kind}");
                }
            }

            StructuredLog.Information(_logger, "apply_patch: applied Codex patch envelope directly in {Path}, sections={Count}", baseRoot, sections.Count);
            return CodexToolResult.Succeeded($"✅ Codex Patch 应用成功（{changedCount} 个文件段）。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex)
        {
            StructuredLog.Warning(_logger, ex, "apply_patch: failed to apply Codex patch envelope directly in {Path}", baseRoot);
            return CodexToolResult.Error($"❌ Codex Patch 解析/应用失败：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Warning(_logger, ex, "apply_patch: failed to apply Codex patch envelope directly in {Path}", baseRoot);
            return CodexToolResult.Error($"❌ Codex Patch 解析/应用失败：{ex.Message}");
        }
        catch (ArgumentException ex)
        {
            StructuredLog.Warning(_logger, ex, "apply_patch: failed to apply Codex patch envelope directly in {Path}", baseRoot);
            return CodexToolResult.Error($"❌ Codex Patch 解析/应用失败：{ex.Message}");
        }
    }

    private static List<CodexPatchSection> ParseCodexPatchSections(string patchContent)
    {
        var lines = patchContent.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var sections = new List<CodexPatchSection>();
        CodexPatchSection? current = null;

        foreach (var raw in lines)
        {
            var line = raw;
            if (line.StartsWith("*** Begin Patch", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("*** End Patch", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    sections.Add(current);
                    current = null;
                }
                continue;
            }

            if (TryParseSectionHeader(line, out var kind, out var path))
            {
                if (current is not null)
                    sections.Add(current);
                current = new CodexPatchSection(kind, path, new List<string>());
                continue;
            }

            if (line.StartsWith("*** End of File", StringComparison.Ordinal))
                continue;

            if (current is not null)
                current.BodyLines.Add(line);
        }

        if (current is not null)
            sections.Add(current);

        return sections;
    }

    private static bool TryParseSectionHeader(string line, out string kind, out string path)
    {
        const string updatePrefix = "*** Update File: ";
        const string addPrefix = "*** Add File: ";
        const string deletePrefix = "*** Delete File: ";

        if (line.StartsWith(updatePrefix, StringComparison.Ordinal))
        {
            kind = "Update";
            path = line[updatePrefix.Length..].Trim();
            return true;
        }
        if (line.StartsWith(addPrefix, StringComparison.Ordinal))
        {
            kind = "Add";
            path = line[addPrefix.Length..].Trim();
            return true;
        }
        if (line.StartsWith(deletePrefix, StringComparison.Ordinal))
        {
            kind = "Delete";
            path = line[deletePrefix.Length..].Trim();
            return true;
        }

        kind = string.Empty;
        path = string.Empty;
        return false;
    }

    private static async Task ApplyCodexUpdateSectionAsync(string targetPath, CodexPatchSection section)
    {
        if (!File.Exists(targetPath))
            throw new FileNotFoundException($"Update target file not found: {section.Path}", targetPath);

        var originalText = await File.ReadAllTextAsync(targetPath).ConfigureAwait(false);
        var usesCrlf = originalText.Contains("\r\n", StringComparison.Ordinal);
        var text = originalText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        var hunkLines = SplitCodexHunks(section.BodyLines);
        if (hunkLines.Count == 0)
            throw new InvalidOperationException($"Codex Update section contains no hunks: {section.Path}");

        foreach (var hunk in hunkLines)
        {
            var (oldChunk, newChunk) = BuildOldNewChunksFromCodexHunk(hunk);
            if (string.IsNullOrEmpty(oldChunk))
                throw new InvalidOperationException($"Codex Patch hunk lacks removable/context lines for {section.Path}");

            text = ReplaceFirstOrThrow(text, oldChunk, newChunk, section.Path);
        }

        var finalText = usesCrlf ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text;
        if (!string.Equals(originalText, finalText, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(targetPath, finalText).ConfigureAwait(false);
        }
    }

    private static async Task ApplyCodexAddSectionAsync(string targetPath, CodexPatchSection section)
    {
        var content = BuildAddFileContent(section.BodyLines);
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(targetPath, content).ConfigureAwait(false);
    }

    private static void ApplyCodexDeleteSection(string targetPath)
    {
        if (File.Exists(targetPath))
            File.Delete(targetPath);
    }

    private static List<List<string>> SplitCodexHunks(List<string> bodyLines)
    {
        var hunks = new List<List<string>>();
        List<string>? current = null;
        var sawHunkHeader = false;

        foreach (var line in bodyLines)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                sawHunkHeader = true;
                if (current is not null && current.Count > 0)
                    hunks.Add(current);
                current = new List<string>();
                continue;
            }

            if (current is null)
                current = new List<string>();

            current.Add(line);
        }

        if (current is not null && current.Count > 0)
            hunks.Add(current);

        // Add-file sections often have no @@, return one synthetic hunk.
        if (!sawHunkHeader && hunks.Count == 0 && bodyLines.Count > 0)
            hunks.Add(new List<string>(bodyLines));

        return hunks;
    }

    private static (string OldChunk, string NewChunk) BuildOldNewChunksFromCodexHunk(List<string> hunkLines)
    {
        var oldSb = new StringBuilder();
        var newSb = new StringBuilder();

        foreach (var line in hunkLines)
        {
            if (line.StartsWith("\\ No newline at end of file", StringComparison.Ordinal))
                continue;
            if (line.Length == 0)
            {
                // Empty line without prefix appears occasionally in malformed output; treat as context blank line.
                oldSb.Append('\n');
                newSb.Append('\n');
                continue;
            }

            var prefix = line[0];
            var content = line.Length > 1 ? line[1..] : string.Empty;
            switch (prefix)
            {
                case ' ':
                    oldSb.Append(content).Append('\n');
                    newSb.Append(content).Append('\n');
                    break;
                case '-':
                    oldSb.Append(content).Append('\n');
                    break;
                case '+':
                    newSb.Append(content).Append('\n');
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported Codex patch hunk line prefix '{prefix}'.");
            }
        }

        return (oldSb.ToString(), newSb.ToString());
    }

    private static string BuildAddFileContent(List<string> bodyLines)
    {
        var sb = new StringBuilder();
        foreach (var line in bodyLines)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal) || line.StartsWith("*** End of File", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("\\ No newline at end of file", StringComparison.Ordinal))
                continue;

            if (line.Length == 0)
            {
                sb.Append('\n');
                continue;
            }

            var prefix = line[0];
            var content = line.Length > 1 ? line[1..] : string.Empty;
            if (prefix is '+' or ' ')
            {
                sb.Append(content).Append('\n');
                continue;
            }

            if (prefix == '-')
            {
                // Add-file sections should not contain removals; ignore malformed line to stay lenient.
                continue;
            }

            throw new InvalidOperationException($"Unsupported Codex add-file line prefix '{prefix}'.");
        }
        return sb.ToString();
    }

    private static string ReplaceFirstOrThrow(string text, string oldChunk, string newChunk, string displayPath)
    {
        var index = text.IndexOf(oldChunk, StringComparison.Ordinal);
        if (index >= 0)
            return text[..index] + newChunk + text[(index + oldChunk.Length)..];

        // Fallback: some patches omit final newline in the hunk chunk.
        var oldNoTrailingLf = oldChunk.TrimEnd('\n');
        if (!string.Equals(oldNoTrailingLf, oldChunk, StringComparison.Ordinal))
        {
            index = text.IndexOf(oldNoTrailingLf, StringComparison.Ordinal);
            if (index >= 0)
            {
                var newNoTrailingLf = newChunk.EndsWith('\n')
                    ? newChunk[..^1]
                    : newChunk;
                return text[..index] + newNoTrailingLf + text[(index + oldNoTrailingLf.Length)..];
            }
        }

        throw new InvalidOperationException($"Codex Patch hunk context not found in target file: {displayPath}");
    }

    /// <summary>
    /// 判断是否应该使用 Hashline 模式。
    /// </summary>
    private bool ShouldUseHashlineMode(string? explicitEditMode, bool hasRequest, Dictionary<string, object?> arguments)
    {
        // 显式指定 edit_mode
        if (explicitEditMode == "hashline")
            return true;
        if (explicitEditMode == "plain")
            return false;

        // 如果提供了 request 对象，检查配置是否启用
        if (hasRequest && _hashlineOptions?.IsHashlinePipelineEnabled() == true)
            return true;

        return false;
    }

    /// <summary>
    /// 从 patch 内容中提取目标文件路径。
    /// </summary>
    private static string? ExtractTargetFileFromPatch(string patchContent)
    {
        if (string.IsNullOrEmpty(patchContent))
            return null;

        // 查找 --- 或 +++ 行
        var lines = patchContent.Split('\n');
        foreach (var line in lines)
        {
            if (line.StartsWith("--- a/", StringComparison.Ordinal) ||
                line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var path = line.StartsWith("--- a/") ? line[6..] : line[4..];
                // 移除时间戳等后缀
                var tabIdx = path.IndexOf('\t');
                if (tabIdx > 0)
                    path = path[..tabIdx];
                return path.Trim();
            }
        }

        return null;
    }

    private async Task NotifyRefreshAsync(
        string workspaceRoot,
        Dictionary<string, object?> arguments,
        List<string> relativePaths,
        CancellationToken ct)
    {
        if (_refreshNotifier == null || string.IsNullOrWhiteSpace(workspaceRoot) || relativePaths.Count == 0)
        {
            return;
        }

        var workerId = arguments.GetValueOrDefault("worker_id")?.ToString()
            ?? arguments.GetValueOrDefault("session_id")?.ToString()
            ?? "default";

        await _refreshNotifier.NotifyFilesChangedAsync(new LanguageServiceRefreshRequest
        {
            WorkspacePath = workspaceRoot,
            WorkerId = workerId,
            RelativePaths = relativePaths.Select(path => path.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        }, ct).ConfigureAwait(false);
    }

    private static List<string> ExtractChangedPathsFromPatch(string patchContent)
    {
        if (string.IsNullOrWhiteSpace(patchContent))
        {
            return [];
        }

        var paths = new List<string>();
        foreach (var line in patchContent.Split('\n'))
        {
            if (!line.StartsWith("+++ ", StringComparison.Ordinal) || line.StartsWith("+++ /dev/null", StringComparison.Ordinal))
            {
                continue;
            }

            var path = line[4..].Trim();
            if (path.StartsWith("b/", StringComparison.Ordinal))
            {
                path = path[2..];
            }

            var tabIdx = path.IndexOf('\t');
            if (tabIdx > 0)
            {
                path = path[..tabIdx];
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path.Replace('\\', '/'));
            }
        }

        return paths;
    }
}
