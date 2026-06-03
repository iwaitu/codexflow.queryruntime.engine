using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Hashline.Constants;
using CodexFlow.Core.Hashline.Infrastructure;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.LanguageServices;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using CodexFlow.Core.Agents.Tools;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexFlow.Core.Agents;

/// <summary>
/// Level 6 核心工具：智能补丁写入器 (Smart Patch)
/// 遵循白皮书影子路径原则，实现原子化的 Diff 注入与自动快照。
/// 支持与 Hashline 能力集成，提供更严格的文件级校验与落盘。
/// </summary>
public class SmartPatchTool : ICodexTool
{
    private readonly IGitService _gitService;
    private readonly IHashlineFileService? _hashlineService;
    private readonly HashlineOptions? _hashlineOptions;
    private readonly ILanguageServiceRefreshNotifier? _refreshNotifier;
    private readonly ILogger<SmartPatchTool> _logger;

    public string Name => "ivilson_smart_patch";
    public string Description => "在影子工作区应用 Unified Diff 格式补丁。执行前自动创建 Git 快照，失败时自动回滚，确保工作区安全。\n" +
        "\n" +
        "【参数说明】\n" +
        "  - patch_content (string, 必填): 标准 Unified Diff 格式的补丁内容，必须包含 --- 和 +++ 头部以及 @@ 行号标记\n" +
        "  - reason (string, 可选): 本次修改的简短说明，将记录在 Git 快照中\n" +
        "  - edit_mode (string, 可选): \"hashline\" 启用 Hashline 编辑模式\n" +
        "  - request (object, 可选): Hashline 编辑请求对象（详见 apply_patch 说明）\n" +
        "    - request.operations 必须是 JSON 数组，数组元素必须是操作对象；禁止传 {}、禁止空数组、禁止多层嵌套数组\n" +
        "\n" +
        "【适用场景】\n" +
        "修改已有文件（特别是 Program.cs、*.csproj 等高风险文件）时必须使用此工具。\n" +
        "\n" +
        "【高风险文件列表】\n" +
        "以下文件禁止使用 write_file 整文件覆盖，必须使用 ivilson_smart_patch：\n" +
        "  - Program.cs, Program.*.cs, Startup.cs\n" +
        "  - *.csproj, *.sln, Directory.Build.props, Directory.Packages.props\n" +
        "  - appsettings.json, appsettings.*.json, launchSettings.json\n" +
        "  - Controllers/AuthController.cs, Controllers/AccountController.cs\n" +
        "  - Services/AuthService.cs, Services/IdentityService.cs\n" +
        "  - Middleware/*.cs\n" +
        "  - .env, .env.*, secrets.json\n" +
        "\n" +
        "【高风险文件 Hashline 策略】\n" +
        "对上述高风险文件：\n" +
        "  1. 先调用 ivilson_read({\"path\":\"Program.cs\", \"mode\":\"hashline\"})\n" +
        "  2. 解析返回的 renderedText 获取每行的 lineNumber 和 anchorId\n" +
        "  3. 使用 ivilson_smart_patch({\"edit_mode\":\"hashline\", ...}) 提交精准操作\n" +
        "  4. 禁止猜测 anchorId，必须从快照中提取\n" +
        "\n" +
        "【返回】\n" +
        "补丁应用成功或失败的确认信息。失败时工作区自动回滚到补丁前状态。\n" +
        "\n" +
        "【Few-shot 示例】\n" +
        "传统模式：\n" +
        "  ivilson_smart_patch({\n" +
        "    \"patch_content\":\"diff --git a/src/Service.cs b/src/Service.cs\\n--- a/src/Service.cs\\n+++ b/src/Service.cs\\n@@ -10,6 +10,7 @@\\n     public class Service\\n     {\\n+        private readonly ILogger _logger;\\n         public void Run() { }\\n     }\",\n" +
        "    \"reason\":\"添加日志依赖\"\n" +
        "  })\n" +
        "\n" +
        "Hashline 模式（高风险文件精准编辑）：\n" +
        "  ivilson_smart_patch({\n" +
        "    \"reason\":\"调整 Program.cs 中间件与路由\",\n" +
        "    \"edit_mode\":\"hashline\",\n" +
        "    \"request\":{\n" +
        "      \"filePath\":\"Program.cs\",\n" +
        "      \"snapshotId\":\"snap_xxx\",\n" +
        "      \"fileFingerprint\":\"fp_xxx\",\n" +
        "      \"dryRun\":false,\n" +
        "      \"operations\":[\n" +
        "        {\"type\":\"insert_after\",\"targetLine\":15,\"targetAnchorId\":\"AA11BB22\",\"newLines\":[\"app.UseMiddleware<CustomMiddleware>();\"]},\n" +
        "        {\"type\":\"replace_range\",\"startLine\":22,\"startAnchorId\":\"CC33DD44\",\"endLine\":25,\"endAnchorId\":\"EE55FF66\",\"newLines\":[\"app.MapGet(\\\"/api/health\\\", HealthCheck);\",\"app.MapGet(\\\"/api/status\\\", StatusCheck);\"]},\n" +
        "        {\"type\":\"rewrite_whole_file\",\"newContent\":\"<Project Sdk=\\\"Microsoft.NET.Sdk\\\">\\n  <PropertyGroup>\\n    <TargetFramework>net10.0</TargetFramework>\\n  </PropertyGroup>\\n</Project>\"}\n" +
        "      ]\n" +
        "    }\n" +
        "  })\n" +
        "\n" +
        "Hashline 模式（修改 .csproj 引用/包）：\n" +
        "  ivilson_smart_patch({\n" +
        "    \"reason\":\"为 Infrastructure 项目添加 Core 引用和 MongoDB.Driver 包\",\n" +
        "    \"edit_mode\":\"hashline\",\n" +
        "    \"request\":{\n" +
        "      \"filePath\":\"src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj\",\n" +
        "      \"snapshotId\":\"snap_infra\",\n" +
        "      \"fileFingerprint\":\"fp_infra\",\n" +
        "      \"operations\":[\n" +
        "        {\"type\":\"replace_range\",\"startLine\":9,\"startAnchorId\":\"P1\",\"endLine\":13,\"endAnchorId\":\"P2\",\"newLines\":[\"  <ItemGroup>\",\"    <ProjectReference Include=\\\"..\\\\CleanApp.Core\\\\CleanApp.Core.csproj\\\" />\",\"    <PackageReference Include=\\\"MongoDB.Driver\\\" Version=\\\"3.2.1\\\" />\",\"  </ItemGroup>\"]}\n" +
        "      ]\n" +
        "    }\n" +
        "  })\n" +
        "\n" +
        "反例（禁止空 operations）：\n" +
        "  ivilson_smart_patch({\"edit_mode\":\"hashline\",\"request\":{\"filePath\":\"src/CleanApp/Program.cs\",\"operations\":{}}})  ❌\n" +
        "  ivilson_smart_patch({\"edit_mode\":\"hashline\",\"request\":{\"filePath\":\"src/CleanApp/Program.cs\",\"operations\":[]}})  ❌";
    public ToolCategory Category => ToolCategory.Forge;
    public IReadOnlyList<int> AllowedStages => new[] { 3, 4 }; // 仅允许在执行与自愈阶段使用

    public SmartPatchTool(IGitService gitService, ILogger<SmartPatchTool> logger)
        : this(gitService, null, null, null, logger)
    {
    }

    public SmartPatchTool(IGitService gitService, IHashlineFileService? hashlineService, ILogger<SmartPatchTool> logger)
        : this(gitService, hashlineService, null, null, logger)
    {
    }

    public SmartPatchTool(
        IGitService gitService,
        IHashlineFileService? hashlineService,
        HashlineOptions? hashlineOptions,
        ILogger<SmartPatchTool> logger)
        : this(gitService, hashlineService, hashlineOptions, null, logger)
    {
    }

    public SmartPatchTool(
        IGitService gitService,
        IHashlineFileService? hashlineService,
        HashlineOptions? hashlineOptions,
        ILanguageServiceRefreshNotifier? refreshNotifier,
        ILogger<SmartPatchTool> logger)
    {
        _gitService = gitService;
        _hashlineService = hashlineService;
        _hashlineOptions = hashlineOptions;
        _refreshNotifier = refreshNotifier;
        _logger = logger;
    }

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var editMode = arguments.TryGetValue("edit_mode", out var editModeValue) ? editModeValue?.ToString() : null;
        var hasRequest = arguments.ContainsKey("request") && arguments["request"] != null;

        // 检查是否应该使用 Hashline 模式
        var shouldUseHashline = ShouldUseHashlineMode(editMode, hasRequest);

        if (shouldUseHashline && _hashlineService != null)
        {
            return await ExecuteHashlineModeAsync(arguments, ct).ConfigureAwait(false);
        }

        // 传统 unified diff 模式
        var workspacePath = arguments.TryGetValue("workspace_path", out var workspaceValue) ? workspaceValue?.ToString() : null;
        var projectRoot = arguments.TryGetValue("project_root", out var projectRootValue) ? projectRootValue?.ToString() : null;
        var patchContent = arguments.TryGetValue("patch_content", out var patchValue) ? patchValue?.ToString() : null;
        var reason = arguments.TryGetValue("reason", out var reasonValue) ? reasonValue?.ToString() : "Applying smart patch";
        var baseRoot = Tools.ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);

        if (string.IsNullOrEmpty(baseRoot))
            return CodexToolResult.Error("Missing workspace_path. SmartPatch must run in a shadow directory.");

        if (string.IsNullOrEmpty(patchContent))
            return CodexToolResult.Error("patch_content cannot be empty.");

        // 检查高风险文件是否需要 Hashline
        if (_hashlineOptions?.ShouldRequireHashlineForHighRiskFiles() == true)
        {
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
            // 1. 执行前原子快照 (Checkpoint)
            // 在影子路径的分支上记录当前状态，确保万一 patch 导致环境崩溃可以瞬间通过 git reset 恢复
            await _gitService.CommitAsync(baseRoot, $"[PRE-PATCH] {reason}").ConfigureAwait(false);

            // 2. 应用补丁 (Apply Patch)
            var normalizedPatch = Tools.ToolPathResolver.NormalizeDuplicateRepoPrefixInPatch(patchContent, baseRoot);
            if (!string.Equals(normalizedPatch, patchContent, StringComparison.Ordinal))
            {
                StructuredLog.Information(_logger, "Normalized duplicated repo prefix in smart patch content for root {Path}", baseRoot);
            }

            var cleanedPatch = PatchPayloadNormalizer.NormalizeTraditionalPatch(normalizedPatch, out var removedDuplicateEndPatchCount);
            if (removedDuplicateEndPatchCount > 0)
            {
                StructuredLog.Warning(_logger, "smart_patch: removed {Count} duplicate '*** End Patch' marker(s) before validation", removedDuplicateEndPatchCount);
            }

            if (PatchPayloadNormalizer.LooksLikeCodexPatchEnvelope(cleanedPatch))
            {
                StructuredLog.Warning(_logger, "smart_patch: codex patch envelope detected in unified diff mode for {Path}", baseRoot);
                return CodexToolResult.Error("❌ ivilson_smart_patch 的传统模式只接受 unified diff。当前输入看起来是 Codex Patch envelope，请改用 apply_patch 或改成标准 unified diff。");
            }

            if (!PatchPayloadNormalizer.TryValidateUnifiedDiff(cleanedPatch, out var validationError))
            {
                StructuredLog.Warning(_logger, "smart_patch: unrecognized patch format in {Path}; does not look like unified diff", baseRoot);
                return CodexToolResult.Error($"❌ patch_content 不是有效的 unified diff：{validationError}");
            }

            StructuredLog.Information(_logger, "Applying smart patch to {Path}", baseRoot);
            var success = await _gitService.ApplyPatchAsync(baseRoot, cleanedPatch).ConfigureAwait(false);

            if (success)
            {
                await NotifyRefreshAsync(baseRoot, arguments, ExtractChangedPathsFromPatch(cleanedPatch), ct).ConfigureAwait(false);
                return CodexToolResult.Succeeded("✅ 补丁应用成功。修改已注入影子工作区，准备进行后续验证。", new
                {
                    Action = "SmartPatch",
                    Status = "Applied",
                    Checkpoint = "Created"
                });
            }
            else
            {
                // 3. 失败回滚 (Auto-Rollback)
                StructuredLog.Warning(_logger, "Patch failed to apply. Rolling back to pre-patch checkpoint.");
                await _gitService.RevertToLastCommitAsync(baseRoot).ConfigureAwait(false);

                return CodexToolResult.Error("❌ 补丁应用失败：检测到内容冲突或格式错误。工作区已自动回滚到 Patch 前的快照状态，未造成任何污染。");
            }
        }
        catch (IOException ex)
        {
            StructuredLog.Error(_logger, ex, "SmartPatch execution failed");
            return CodexToolResult.Error($"SmartPatch 内部异常: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(_logger, ex, "SmartPatch execution failed");
            return CodexToolResult.Error($"SmartPatch 内部异常: {ex.Message}");
        }
        catch (TimeoutException ex)
        {
            StructuredLog.Error(_logger, ex, "SmartPatch execution failed");
            return CodexToolResult.Error($"SmartPatch 内部异常: {ex.Message}");
        }
    }

    private async Task<CodexToolResult> ExecuteHashlineModeAsync(Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var reason = arguments.TryGetValue("reason", out var reasonValue) ? reasonValue?.ToString() : "Applying smart patch with Hashline";
        var workspacePath = arguments.TryGetValue("workspace_path", out var workspaceValue) ? workspaceValue?.ToString() : null;
        var projectRoot = arguments.TryGetValue("project_root", out var projectRootValue) ? projectRootValue?.ToString() : null;
        var baseRoot = Tools.ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);

        if (string.IsNullOrEmpty(baseRoot))
            return CodexToolResult.Error("Missing workspace_path. SmartPatch must run in a shadow directory.");

        var requestObj = arguments.TryGetValue("request", out var req) ? req : null;
        if (requestObj == null)
        {
            return CodexToolResult.Error("Hashline 模式需要提供 request 参数。");
        }

        try
        {
            // 1. 解析 Hashline 请求
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

            // 2. 执行前原子快照 (Checkpoint)
            await _gitService.CommitAsync(baseRoot, $"[PRE-PATCH-HASHLINE] {reason}").ConfigureAwait(false);

            // 3. 执行 Hashline 编辑
            var result = await _hashlineService!.EditAsync(parseResult.Request, baseRoot, ct).ConfigureAwait(false);

            if (result.Success)
            {
                if (!result.DryRun)
                {
                    var normalizedPath = Path.GetRelativePath(baseRoot, parseResult.Request.FilePath).Replace('\\', '/');
                    await NotifyRefreshAsync(baseRoot, arguments, [normalizedPath], ct).ConfigureAwait(false);
                }

                var message = result.DryRun
                    ? "✅ Hashline SmartPatch 验证成功（DryRun）。"
                    : "✅ Hashline SmartPatch 应用成功。修改已注入影子工作区，准备进行后续验证。";

                if (!string.IsNullOrEmpty(result.UnifiedDiff))
                {
                    message += $"\n\nDiff:\n{result.UnifiedDiff}";
                }

                return CodexToolResult.Succeeded(message, new
                {
                    Action = "SmartPatch",
                    Mode = "Hashline",
                    Status = "Applied",
                    Checkpoint = "Created",
                    OldFingerprint = result.OldFingerprint,
                    NewFingerprint = result.NewFingerprint
                });
            }
            else
            {
                // 4. 失败回滚
                StructuredLog.Warning(_logger, "Hashline SmartPatch failed. Rolling back to pre-patch checkpoint.");
                await _gitService.RevertToLastCommitAsync(baseRoot).ConfigureAwait(false);

                // 检查是否为 fingerprint/anchor mismatch 类型错误，需要添加特定前缀供 Orchestrator 检测
                var isMismatchError = result.ErrorCode == HashlineErrorCodes.FileFingerprintMismatch ||
                                      result.ErrorCode == HashlineErrorCodes.AnchorMismatch ||
                                      result.ErrorCode == HashlineErrorCodes.LineOutOfRange ||
                                      result.ErrorCode == HashlineErrorCodes.AnchorNotFound;

                var errorPrefix = isMismatchError ? "[HASHLINE_MISMATCH_FAILURE] " : "";
                return CodexToolResult.Error($"{errorPrefix}❌ Hashline SmartPatch 失败: {result.ErrorCode} - {result.ErrorMessage}\n" +
                    "你必须重新 ivilson_read({\"path\":\"...\", \"mode\":\"hashline\"}) 获取最新快照，禁止猜测 anchorId。");
            }
        }
        catch (Exception ex)
        {
            StructuredLog.Error(_logger, ex, "Hashline SmartPatch execution failed");
            await _gitService.RevertToLastCommitAsync(baseRoot).ConfigureAwait(false);
            return CodexToolResult.Error($"Hashline SmartPatch 内部异常: {ex.Message}");
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

        var workspacePath = arguments.TryGetValue("workspace_path", out var ws) ? ws?.ToString() : null;
        var projectRoot = arguments.TryGetValue("project_root", out var pr) ? pr?.ToString() : null;
        var baseRoot = Tools.ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);

        var filePath = dict.TryGetValue("filePath", out var fp) ? fp?.ToString() : null;
        if (string.IsNullOrEmpty(filePath))
        {
            result.Errors.Add("request.filePath 缺失或为空。");
            return result;
        }

        // 如果是相对路径，转换为绝对路径
        if (!Path.IsPathRooted(filePath) && !string.IsNullOrEmpty(baseRoot))
        {
            filePath = Path.GetFullPath(Path.Combine(baseRoot, filePath));
        }

        var request = new HashlineEditRequest
        {
            FilePath = filePath!,
            SnapshotId = dict.TryGetValue("snapshotId", out var sid) ? sid?.ToString() ?? string.Empty : string.Empty,
            FileFingerprint = dict.TryGetValue("fileFingerprint", out var ff) ? ff?.ToString() ?? string.Empty : string.Empty,
            DryRun = dict.TryGetValue("dryRun", out var dr) && dr is bool b && b
        };

        // 解析操作
        if (!TryGetOperationCandidates(dict.TryGetValue("operations", out var ops) ? ops : null, out var operationCandidates, out var operationsError))
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
            errors.Add($"operation[{index}] 不是合法的操作对象，或无法从 input_params/arguments 包装中解出参数字典。");
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
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList<object?>(),
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
        var type = opDict.TryGetValue("type", out var t) ? t?.ToString() : null;
        if (string.IsNullOrEmpty(type))
        {
            errors.Add($"operation[{index}] 缺少必填字段 type。");
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

    private static ReplaceRangeOperation? CreateReplaceRangeOperation(
        Dictionary<string, object?> opDict,
        string type,
        int index,
        List<string> errors)
    {
        var newLines = ParseStringList(opDict.TryGetValue("newLines", out var nl) ? nl : null);
        if (newLines.Count == 0)
        {
            errors.Add($"operation[{index}] type={type} 需要提供非空数组 newLines。");
            return null;
        }

        return new ReplaceRangeOperation
        {
            Type = type,
            StartLine = TryGetInt(opDict.TryGetValue("startLine", out var sl) ? sl : null) ?? 0,
            StartAnchorId = opDict.TryGetValue("startAnchorId", out var sai) ? sai?.ToString() ?? string.Empty : string.Empty,
            EndLine = TryGetInt(opDict.TryGetValue("endLine", out var el) ? el : null) ?? 0,
            EndAnchorId = opDict.TryGetValue("endAnchorId", out var eai) ? eai?.ToString() ?? string.Empty : string.Empty,
            NewLines = newLines
        };
    }

    private static InsertAfterOperation? CreateInsertAfterOperation(
        Dictionary<string, object?> opDict,
        string type,
        int index,
        List<string> errors)
    {
        var newLines = ParseStringList(opDict.TryGetValue("newLines", out var nl) ? nl : null);
        if (newLines.Count == 0)
        {
            errors.Add($"operation[{index}] type={type} 需要提供非空数组 newLines。");
            return null;
        }

        return new InsertAfterOperation
        {
            Type = type,
            TargetLine = TryGetInt(opDict.TryGetValue("targetLine", out var tl) ? tl : null) ?? 0,
            TargetAnchorId = opDict.TryGetValue("targetAnchorId", out var tai) ? tai?.ToString() ?? string.Empty : string.Empty,
            NewLines = newLines
        };
    }

    private static InsertBeforeOperation? CreateInsertBeforeOperation(
        Dictionary<string, object?> opDict,
        string type,
        int index,
        List<string> errors)
    {
        var newLines = ParseStringList(opDict.TryGetValue("newLines", out var nl) ? nl : null);
        if (newLines.Count == 0)
        {
            errors.Add($"operation[{index}] type={type} 需要提供非空数组 newLines。");
            return null;
        }

        return new InsertBeforeOperation
        {
            Type = type,
            TargetLine = TryGetInt(opDict.TryGetValue("targetLine", out var tl) ? tl : null) ?? 0,
            TargetAnchorId = opDict.TryGetValue("targetAnchorId", out var tai) ? tai?.ToString() ?? string.Empty : string.Empty,
            NewLines = newLines
        };
    }

    private static DeleteRangeOperation CreateDeleteRangeOperation(Dictionary<string, object?> opDict, string type)
    {
        return new DeleteRangeOperation
        {
            Type = type,
            StartLine = TryGetInt(opDict.TryGetValue("startLine", out var sl) ? sl : null) ?? 0,
            StartAnchorId = opDict.TryGetValue("startAnchorId", out var sai) ? sai?.ToString() ?? string.Empty : string.Empty,
            EndLine = TryGetInt(opDict.TryGetValue("endLine", out var el) ? el : null) ?? 0,
            EndAnchorId = opDict.TryGetValue("endAnchorId", out var eai) ? eai?.ToString() ?? string.Empty : string.Empty
        };
    }

    private static RewriteWholeFileOperation? CreateRewriteWholeFileOperation(
        Dictionary<string, object?> opDict,
        string type,
        int index,
        List<string> errors)
    {
        var newContent = opDict.TryGetValue("newContent", out var nc) ? nc?.ToString() : null;
        if (string.IsNullOrEmpty(newContent))
        {
            errors.Add($"operation[{index}] type={type} 需要提供非空字段 newContent。");
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
        errors.Add($"operation[{index}] 包含不支持的 type='{type}'。");
        return null;
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
           推荐优先改用更扁平的 `hs_write({...})`。若仍使用 ivilson_smart_patch，最小正确格式示例：
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

    private sealed class ParseHashlineResult
    {
        public HashlineEditRequest? Request { get; set; }
        public List<string> Errors { get; } = new();
    }

    /// <summary>
    /// 判断是否应该使用 Hashline 模式。
    /// </summary>
    private bool ShouldUseHashlineMode(string? explicitEditMode, bool hasRequest)
    {
        // 显式指定 edit_mode
        if (explicitEditMode?.ToLowerInvariant() == "hashline")
            return true;
        if (explicitEditMode?.ToLowerInvariant() == "plain")
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
