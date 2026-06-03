using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Hashline.Abstractions;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Phase 1: 默认工具执行协调器实现
/// </summary>
public sealed class DefaultToolExecutionCoordinator : IToolExecutionCoordinator
{
    public const string DuplicateHashlineReadReasonCode = "duplicate_hashline_read";

    private static readonly char[] InlineWhitespaceSeparators = ['\r', '\n', '\t'];
    private readonly ILogger<DefaultToolExecutionCoordinator> _logger;
    private readonly ITextNormalizer? _hashlineTextNormalizer;
    private readonly IFileFingerprintProvider? _hashlineFingerprintProvider;
    private readonly IEncodingDetector? _hashlineEncodingDetector;

    public DefaultToolExecutionCoordinator(
        ILogger<DefaultToolExecutionCoordinator> logger,
        ITextNormalizer? hashlineTextNormalizer = null,
        IFileFingerprintProvider? hashlineFingerprintProvider = null,
        IEncodingDetector? hashlineEncodingDetector = null)
    {
        _logger = logger;
        _hashlineTextNormalizer = hashlineTextNormalizer;
        _hashlineFingerprintProvider = hashlineFingerprintProvider;
        _hashlineEncodingDetector = hashlineEncodingDetector;
    }

    /// <inheritdoc/>
    public ToolDedupResult? CheckDuplicate(
        FunctionCallContent toolCall,
        QueryRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(state);

        lock (state.ToolExecutionSync)
        {
            return CheckDuplicateCore(toolCall, state, request: null);
        }
    }

    private ToolDedupResult? CheckDuplicateCore(
        FunctionCallContent toolCall,
        QueryRuntimeState state,
        QueryRuntimeRequest? request)
    {
        var toolName = toolCall.Name;
        var signatureArguments = BuildSignatureArguments(toolCall.Arguments, request);
        if (ToolCallSyntaxRecovery.TryNormalizeInlineInvocation(toolCall.Name, toolCall.Arguments, out var recoveredToolName, out _))
        {
            toolName = recoveredToolName;
        }

        // Skip deduplication if not enabled
        if (!state.EnableToolDeduplication || !ShouldDeduplicate(toolName))
        {
            return null;
        }

        var signature = ComputeSignature(toolName ?? "unknown", signatureArguments);

        if (state.ExecutedToolSignatures.Contains(signature))
        {
            _logger.LogDebug("Duplicate tool call detected: {Signature}", signature);
            state.Flags |= RuntimeState.ToolDeduplicationApplied;
            return new ToolDedupResult(
                ShouldSkip: true,
                CachedResult: null,
                WasFailed: false);
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<ToolExecutionResult> ExecuteAsync(
        FunctionCallContent toolCall,
        IReadOnlyList<AIFunction>? availableTools,
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(state);

        var rawToolName = toolCall.Name ?? "unknown";
        var toolName = rawToolName;
        var callId = toolCall.CallId ?? string.Empty;
        var args = ToolCallSyntaxRecovery.CloneArguments(toolCall.Arguments);
        var recoveredMalformedSyntax = false;

        // Check for duplicate
        ToolDedupResult? dedupResult;
        lock (state.ToolExecutionSync)
        {
            dedupResult = CheckDuplicateCore(toolCall, state, request);
        }
        if (dedupResult?.ShouldSkip == true)
        {
            return new ToolExecutionResult(
                ToolName: toolName,
                CallId: callId,
                Result: dedupResult.CachedResult ?? "[Skipped: duplicate tool call]",
                Success: !dedupResult.WasFailed,
                ResultLength: dedupResult.CachedResult?.Length,
                Summary: "Skipped duplicate tool call");
        }

        // Find the tool
        var currentTools = ResolveAvailableTools(request, availableTools);
        var tool = currentTools?.FirstOrDefault(t => t.Name == toolName);
            if (tool == null &&
            ToolCallSyntaxRecovery.TryNormalizeInlineInvocation(rawToolName, toolCall.Arguments, out var recoveredToolName, out var recoveredArgs))
        {
            toolName = recoveredToolName;
            args = recoveredArgs;
            recoveredMalformedSyntax = !string.Equals(rawToolName, recoveredToolName, StringComparison.Ordinal);
            tool = currentTools?.FirstOrDefault(t => t.Name == toolName);
            if (tool != null)
            {
                _logger.LogWarning("Recovered malformed inline tool call syntax. Raw={RawToolName} Normalized={ToolName}", rawToolName, toolName);
            }
        }

        var codexTool = ResolveAvailableCodexTools(request)?.FirstOrDefault(t => t.Name == toolName);

        if (tool == null && codexTool == null)
        {
            _logger.LogWarning("Tool not found: {ToolName}", rawToolName);
            var message = ToolResultTextFormatter.FormatToolNotFound(
                rawToolName,
                currentTools?.Select(static tool => tool.Name));
            return new ToolExecutionResult(
                ToolName: rawToolName,
                CallId: callId,
                Result: message,
                Success: false,
                ResultLength: message.Length,
                Summary: ToolResultTextFormatter.SummarizeText(message));
        }

        // Execute the tool
        try
        {
            _logger.LogDebug("Executing tool: {ToolName}", toolName);
            InjectTrustedRuntimeArguments(args, request.Session);
            BackfillHashlineWriteArguments(toolName, args, request, state);
            var duplicateHashlineReadResult = await TryRejectUnchangedHashlineReadAsync(
                    toolName,
                    callId,
                    args,
                    request,
                    state,
                    ct)
                .ConfigureAwait(false);
            if (duplicateHashlineReadResult != null)
            {
                return duplicateHashlineReadResult;
            }

            if (codexTool != null)
            {
                var validation = await codexTool.ValidateInputAsync(args, ct).ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    _logger.LogWarning("Tool {ToolName} validation failed: {Message}", toolName, validation.Message);
                    return BuildToolExecutionResult(
                        toolName,
                        callId,
                        new CodexToolResult
                        {
                            Status = ToolResultStatus.ValidationRequired,
                            Output = validation.Message ?? $"Tool '{toolName}' validation failed.",
                            Summary = validation.Message ?? $"Tool '{toolName}' validation failed.",
                            Metadata = validation.Metadata,
                            SystemHint = validation.SystemHint
                        });
                }

                var permission = await codexTool.CheckPermissionsAsync(args, request, state, ct).ConfigureAwait(false);
                if (!permission.IsAllowed)
                {
                    _logger.LogWarning("Tool {ToolName} permission denied: {Message}", toolName, permission.Message);
                    return BuildToolExecutionResult(
                        toolName,
                        callId,
                        new CodexToolResult
                        {
                            Status = ToolResultStatus.BlockedByGuardrail,
                            Output = permission.Message ?? $"Tool '{toolName}' was blocked by guardrail.",
                            Summary = permission.Message ?? $"Tool '{toolName}' was blocked by guardrail.",
                            Metadata = permission.Metadata,
                            SystemHint = permission.SystemHint
                        });
                }
            }

            ToolExecutionResult executionResult;
            if (codexTool != null)
            {
                var codexResult = await codexTool.ExecuteAsync(args, ct).ConfigureAwait(false);
                executionResult = BuildToolExecutionResult(toolName, callId, codexResult);
            }
            else
            {
                // Tools registered via AIFunctionFactory.Create have varying signatures:
                //   - Some expect raw args directly: (string url, CancellationToken ct)
                //   - Some expect a wrapped dict: (Dictionary<string, object?> input_params, CancellationToken ct)
                // Strategy: try direct args first, fall back to wrapped input_params on parameter mismatch.
                object? result;
                try
                {
                    result = await tool!.InvokeAsync(new AIFunctionArguments(args), ct);
                }
                catch (Exception ex) when (IsMissingRequiredParameter(ex, "input_params") || IsMissingRequiredParameter(ex, "rawArgs"))
                {
                    // Tool expects wrapped arguments (kernel-style delegate)
                    var wrappedArgs = new Dictionary<string, object?> { ["input_params"] = args };
                    result = await tool!.InvokeAsync(new AIFunctionArguments(wrappedArgs), ct);
                }

                executionResult = BuildToolExecutionResult(
                    toolName,
                    callId,
                    CodexToolResult.Succeeded(result?.ToString() ?? string.Empty));
            }

            // Record execution for deduplication only if enabled and the tool completed successfully
            if (executionResult.Success && state.EnableToolDeduplication && ShouldDeduplicate(toolName))
            {
                var signature = ComputeSignature(toolName, BuildSignatureArguments(args, request));
                lock (state.ToolExecutionSync)
                {
                    state.ExecutedToolSignatures.Add(signature);
                }
            }

            _logger.LogDebug(
                "Tool {ToolName} completed. Success={Success}, result length: {Length}",
                toolName,
                executionResult.Success,
                executionResult.ResultLength ?? executionResult.Result.Length);

            return executionResult;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {ToolName} execution failed", toolName);
            var message = ToolResultTextFormatter.FormatException(ex, toolName);
            var systemHintDetail = TryExtractMissingRequiredParameter(ex, out var missingParameter)
                ? BuildMissingRequiredParameterHint(toolName, missingParameter, currentTools)
                : null;
            return new ToolExecutionResult(
                ToolName: toolName,
                CallId: callId,
                Result: message,
                Success: false,
                ResultLength: message.Length,
                Exception: ex,
                Summary: ToolResultTextFormatter.SummarizeText(message),
                SystemHint: systemHintDetail?.Message,
                SystemHintDetail: systemHintDetail);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ToolExecutionResult> ExecuteBatchAsync(
        IReadOnlyList<FunctionCallContent> toolCalls,
        IReadOnlyList<AIFunction>? availableTools,
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toolCalls);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(state);

        var currentTools = ResolveAvailableTools(request, availableTools);
        foreach (var batch in PartitionToolCalls(toolCalls, request))
        {
            ct.ThrowIfCancellationRequested();
            var preparedBatch = batch.IsConcurrencySafe
                ? PrepareConcurrencySafeBatch(batch.Calls, request, state)
                : batch.Calls
                    .Select(static call => new PreparedToolExecution(call, null))
                    .ToList();

            if (batch.IsConcurrencySafe && preparedBatch.Count(prepared => prepared.ToolCall != null) > 1)
            {
                var tasks = preparedBatch
                    .Select((prepared, index) => (Prepared: prepared, Index: index))
                    .Where(entry => entry.Prepared.ToolCall != null)
                    .Select(async entry => (entry.Index, Result: await ExecuteAsync(
                        entry.Prepared.ToolCall!,
                        currentTools,
                        request,
                        state,
                        ct).ConfigureAwait(false)))
                    .ToArray();
                var completed = await Task.WhenAll(tasks).ConfigureAwait(false);
                var orderedResults = new ToolExecutionResult[preparedBatch.Count];
                foreach (var prepared in preparedBatch.Select((item, index) => (Item: item, Index: index)))
                {
                    if (prepared.Item.PrecomputedResult != null)
                    {
                        orderedResults[prepared.Index] = prepared.Item.PrecomputedResult;
                    }
                }

                foreach (var completedResult in completed)
                {
                    orderedResults[completedResult.Index] = completedResult.Result;
                }

                foreach (var result in orderedResults)
                {
                    yield return result;
                }

                continue;
            }

            foreach (var prepared in preparedBatch)
            {
                ct.ThrowIfCancellationRequested();
                if (prepared.PrecomputedResult != null)
                {
                    yield return prepared.PrecomputedResult;
                    continue;
                }

                yield return await ExecuteAsync(prepared.ToolCall!, currentTools, request, state, ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public string ComputeSignature(FunctionCallContent toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (ToolCallSyntaxRecovery.TryNormalizeInlineInvocation(toolCall.Name, toolCall.Arguments, out var recoveredToolName, out var recoveredArgs))
        {
            return ComputeSignature(recoveredToolName, BuildSignatureArguments(recoveredArgs, request: null));
        }

        return ComputeSignature(toolCall.Name ?? "unknown", BuildSignatureArguments(toolCall.Arguments, request: null));
    }

    /// <summary>
    /// Check if an exception indicates a missing required parameter with the given name.
    /// This is used to detect when a tool expects wrapped arguments (input_params/rawArgs).
    /// </summary>
    private static bool IsMissingRequiredParameter(Exception ex, string paramName)
    {
        var message = ex.Message;
        return message.Contains($"missing a value for the required parameter '{paramName}'", StringComparison.OrdinalIgnoreCase)
            || message.Contains($"missing a value for the required parameter \"{paramName}\"", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractMissingRequiredParameter(Exception ex, out string parameterName)
    {
        parameterName = string.Empty;
        var message = ex.Message;
        const string singleQuoteMarker = "missing a value for the required parameter '";
        var index = message.IndexOf(singleQuoteMarker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var start = index + singleQuoteMarker.Length;
            var end = message.IndexOf('\'', start);
            if (end > start)
            {
                parameterName = message[start..end];
                return true;
            }
        }

        const string doubleQuoteMarker = "missing a value for the required parameter \"";
        index = message.IndexOf(doubleQuoteMarker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var start = index + doubleQuoteMarker.Length;
            var end = message.IndexOf('"', start);
            if (end > start)
            {
                parameterName = message[start..end];
                return true;
            }
        }

        return false;
    }

    private static ToolSystemHint BuildMissingRequiredParameterHint(
        string toolName,
        string missingParameter,
        IReadOnlyList<AIFunction>? currentTools)
    {
        var baseHint = $"工具 `{toolName}` 缺少必需参数 `{missingParameter}`。";
        if (currentTools?.Any(static tool => string.Equals(tool.Name, "start_next_task", StringComparison.OrdinalIgnoreCase)) == true)
        {
            return new ToolSystemHint(
                baseHint + " 如果当前计划已有待执行任务，不要继续读取或搜索代码，下一步调用 `start_next_task`。否则按工具 schema 补齐必需参数后重试。",
                RequiredToolName: "start_next_task",
                ToolCallRequired: true);
        }

        return new ToolSystemHint(baseHint + " 请按工具 schema 补齐必需参数后重试。");
    }

    private static string ComputeSignature(string toolName, Dictionary<string, object?>? arguments)
    {
        if (arguments == null || arguments.Count == 0)
        {
            return toolName;
        }

        var argString = string.Join("|",
            arguments
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));

        return $"{toolName}:{argString.GetHashCode():X}";
    }

    private static bool ShouldDeduplicate(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        return toolName.Equals("search_file_index", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("search_in_files", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("ivilson_read", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("hs_read", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("ivilson_ls", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("list_workspace", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("fetch_webpage", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("analyze_code", StringComparison.OrdinalIgnoreCase);
    }

    private static List<PreparedToolExecution> PrepareConcurrencySafeBatch(
        List<FunctionCallContent> toolCalls,
        QueryRuntimeRequest request,
        QueryRuntimeState state)
    {
        if (!state.EnableToolDeduplication || toolCalls.Count == 0)
        {
            return toolCalls
                .Select(static call => new PreparedToolExecution(call, null))
                .ToList();
        }

        var seenSignatures = new HashSet<string>(StringComparer.Ordinal);
        var prepared = new List<PreparedToolExecution>(toolCalls.Count);
        foreach (var toolCall in toolCalls)
        {
            var toolName = toolCall.Name;
            var signatureArguments = BuildSignatureArguments(toolCall.Arguments, request);
            if (ToolCallSyntaxRecovery.TryNormalizeInlineInvocation(toolCall.Name, toolCall.Arguments, out var recoveredToolName, out var recoveredArgs))
            {
                toolName = recoveredToolName;
                signatureArguments = BuildSignatureArguments(recoveredArgs, request);
            }

            if (!ShouldDeduplicate(toolName))
            {
                prepared.Add(new PreparedToolExecution(toolCall, null));
                continue;
            }

            var signature = ComputeSignature(toolName ?? "unknown", signatureArguments);
            if (!seenSignatures.Add(signature))
            {
                state.Flags |= RuntimeState.ToolDeduplicationApplied;
                prepared.Add(new PreparedToolExecution(
                    ToolCall: null,
                    PrecomputedResult: new ToolExecutionResult(
                        ToolName: toolName ?? toolCall.Name ?? "unknown",
                        CallId: toolCall.CallId ?? string.Empty,
                        Result: "[Skipped: duplicate tool call in concurrent batch]",
                        Success: true,
                        ResultLength: "[Skipped: duplicate tool call in concurrent batch]".Length,
                        Summary: "Skipped duplicate tool call in concurrent batch")));
                continue;
            }

            prepared.Add(new PreparedToolExecution(toolCall, null));
        }

        return prepared;
    }

    private static List<ToolExecutionBatch> PartitionToolCalls(
        IReadOnlyList<FunctionCallContent> toolCalls,
        QueryRuntimeRequest request)
    {
        if (toolCalls.Count == 0)
        {
            return [];
        }

        var batches = new List<ToolExecutionBatch>();
        ToolExecutionBatch? currentBatch = null;

        foreach (var call in toolCalls)
        {
            var isConcurrencySafe = IsConcurrencySafeBatchableTool(call, request);
            if (currentBatch == null || currentBatch.IsConcurrencySafe != isConcurrencySafe)
            {
                currentBatch = new ToolExecutionBatch(isConcurrencySafe, []);
                batches.Add(currentBatch);
            }

            currentBatch.Calls.Add(call);
        }

        return batches;
    }

    private static bool IsConcurrencySafeBatchableTool(
        FunctionCallContent toolCall,
        QueryRuntimeRequest request)
    {
        var metadata = ResolveToolMetadata(toolCall, request);
        return metadata is
        {
            IsConcurrencySafe: true,
            IsReadOnly: true,
            IsDestructive: false
        };
    }

    private static ToolExecutionMetadata? ResolveToolMetadata(
        FunctionCallContent toolCall,
        QueryRuntimeRequest request)
    {
        var toolName = toolCall.Name;
        if (ToolCallSyntaxRecovery.TryNormalizeInlineInvocation(toolCall.Name, toolCall.Arguments, out var recoveredToolName, out _))
        {
            toolName = recoveredToolName;
        }

        return ResolveAvailableCodexTools(request)?
            .FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase))?
            .Metadata;
    }

    private static IReadOnlyList<AIFunction>? ResolveAvailableTools(
        QueryRuntimeRequest request,
        IReadOnlyList<AIFunction>? fallback)
    {
        if (request.AvailableToolsProvider != null)
        {
            return request.AvailableToolsProvider();
        }

        return fallback ?? request.AvailableTools;
    }

    private static IReadOnlyList<ICodexTool>? ResolveAvailableCodexTools(QueryRuntimeRequest request)
    {
        if (request.AvailableCodexToolsProvider != null)
        {
            return request.AvailableCodexToolsProvider();
        }

        return request.AvailableCodexTools;
    }

    private static ToolExecutionResult BuildToolExecutionResult(
        string toolName,
        string callId,
        CodexToolResult result)
    {
        var output = ToolResultTextFormatter.FormatCodexToolResult(result, toolName);
        var summary = string.IsNullOrWhiteSpace(result.Summary)
            ? ToolResultTextFormatter.SummarizeText(output)
            : ToolResultTextFormatter.SummarizeText(result.Summary);

        return new ToolExecutionResult(
            ToolName: toolName,
            CallId: callId,
            Result: output,
            Success: IsSuccessfulStatus(result.Status),
            ResultLength: output.Length,
            Summary: summary,
            IsOutputTruncated: result.IsOutputTruncated,
            Metadata: result.Metadata,
            SystemHint: result.SystemHint,
            SystemHintDetail: result.SystemHintDetail);
    }

    private static bool IsSuccessfulStatus(ToolResultStatus status)
        => status is ToolResultStatus.Success or ToolResultStatus.PartialSuccess;

    private static string? SummarizePlainToolResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return null;
        }

        var normalized = string.Join(
            " ",
            result
                .Split(InlineWhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();
        if (normalized.Length <= 140)
        {
            return normalized;
        }

        return normalized[..137] + "...";
    }

    private static void InjectTrustedRuntimeArguments(Dictionary<string, object?> args, Core.Models.CodexSession? session)
    {
        ToolArgumentNormalizer.NormalizeInPlace(args);

        if (session == null)
        {
            return;
        }

        args["session_id"] = session.Id;
        args["workspace_path"] = session.WorkspacePath;
        args["project_root"] = ToolPathResolver.ResolveProjectRoot(
            session.WorkspacePath,
            null,
            session.ProjectUrl,
            session.Metadata);
    }

    private static void BackfillHashlineWriteArguments(
        string? toolName,
        Dictionary<string, object?> args,
        QueryRuntimeRequest request,
        QueryRuntimeState state)
    {
        if (!string.Equals(toolName, "hs_write", StringComparison.OrdinalIgnoreCase) ||
            state.EvidenceLedger.Files.Count == 0)
        {
            return;
        }

        var filePath = ToolArgumentNormalizer.CoerceLooseStringScalarValue(args.GetValueOrDefault("filePath"))
                       ?? ToolArgumentNormalizer.CoerceLooseStringScalarValue(args.GetValueOrDefault("path"));
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var evidence = FindReadEvidenceForPath(state, filePath, request);
        if (evidence == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ToolArgumentNormalizer.CoerceLooseStringScalarValue(args.GetValueOrDefault("snapshotId"))) &&
            !string.IsNullOrWhiteSpace(evidence.SnapshotId))
        {
            args["snapshotId"] = evidence.SnapshotId;
        }

        if (string.IsNullOrWhiteSpace(ToolArgumentNormalizer.CoerceLooseStringScalarValue(args.GetValueOrDefault("fileFingerprint"))) &&
            !string.IsNullOrWhiteSpace(evidence.FileFingerprint))
        {
            args["fileFingerprint"] = evidence.FileFingerprint;
        }
    }

    private async Task<ToolExecutionResult?> TryRejectUnchangedHashlineReadAsync(
        string toolName,
        string callId,
        Dictionary<string, object?> args,
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        CancellationToken ct)
    {
        if (!state.EnableToolDeduplication ||
            !IsHashlineSnapshotReadRequest(toolName, args) ||
            _hashlineTextNormalizer == null ||
            _hashlineFingerprintProvider == null)
        {
            return null;
        }

        var requestedPath = ToolArgumentNormalizer.CoerceLooseStringScalarValue(args.GetValueOrDefault("path"));
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return null;
        }

        var previousEvidence = FindReadEvidenceForPath(state, requestedPath, request);
        if (previousEvidence == null || string.IsNullOrWhiteSpace(previousEvidence.FileFingerprint))
        {
            return null;
        }

        if (!TryResolveReadableFilePath(args, requestedPath, request, out var fullPath, out var normalizedPath))
        {
            return null;
        }

        var currentFingerprint = await TryComputeCurrentHashlineFingerprintAsync(fullPath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(currentFingerprint) ||
            !string.Equals(currentFingerprint, previousEvidence.FileFingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        var displayPath = string.IsNullOrWhiteSpace(previousEvidence.FilePath)
            ? normalizedPath
            : previousEvidence.FilePath;
        var requiredTool = ResolvePreferredWriteToolName(request);
        var message =
            $"工具 `{toolName}` 执行失败。\n" +
            $"Runtime 已拒绝重复 Hashline 读取：`{displayPath}` 的当前 fileFingerprint 仍为 `{currentFingerprint}`，与已有快照一致。\n" +
            $"请复用已有 snapshotId `{previousEvidence.SnapshotId ?? "(unknown)"}` 和 fileFingerprint `{previousEvidence.FileFingerprint}` 继续编辑；只有在 FILE_FINGERPRINT_MISMATCH、ANCHOR_MISMATCH、LINE_OUT_OF_RANGE 或确认文件已变化后才重新读取。";
        var hintMessage = string.IsNullOrWhiteSpace(requiredTool)
            ? $"已有 `{displayPath}` 的未变化 Hashline 快照，停止重复读取并基于已有 snapshotId/fileFingerprint 继续。"
            : $"已有 `{displayPath}` 的未变化 Hashline 快照，下一步必须调用 `{requiredTool}`，不要再次读取该文件。";

        _logger.LogWarning(
            "Rejected duplicate unchanged Hashline read. Tool={ToolName} Path={Path} Fingerprint={Fingerprint} SessionId={SessionId}",
            toolName,
            displayPath,
            currentFingerprint,
            request.SessionId);

        state.Flags |= RuntimeState.ToolDeduplicationApplied;
        return new ToolExecutionResult(
            ToolName: toolName,
            CallId: callId,
            Result: message,
            Success: false,
            ResultLength: message.Length,
            Summary: $"Rejected duplicate unchanged Hashline read for {displayPath}",
            Metadata: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ReasonCode"] = DuplicateHashlineReadReasonCode,
                ["FilePath"] = displayPath,
                ["SnapshotId"] = previousEvidence.SnapshotId,
                ["FileFingerprint"] = previousEvidence.FileFingerprint,
                ["CurrentFingerprint"] = currentFingerprint
            },
            SystemHint: hintMessage,
            SystemHintDetail: new ToolSystemHint(
                hintMessage,
                RequiredToolName: requiredTool,
                ToolCallRequired: !string.IsNullOrWhiteSpace(requiredTool)));
    }

    private static bool IsHashlineSnapshotReadRequest(
        string? toolName,
        IReadOnlyDictionary<string, object?> args)
    {
        if (string.Equals(toolName, "hs_read", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(toolName, "ivilson_read", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var mode = ToolArgumentNormalizer.CoerceLooseStringScalarValue(args.GetValueOrDefault("mode"));
        return string.Equals(mode, "hashline", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> TryComputeCurrentHashlineFingerprintAsync(
        string fullPath,
        CancellationToken ct)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
            string rawText;
            if (_hashlineEncodingDetector != null)
            {
                var encodingInfo = _hashlineEncodingDetector.DetectEncoding(bytes);
                rawText = encodingInfo.Encoding.GetString(bytes);
                if (encodingInfo.HasBom &&
                    encodingInfo.Encoding.CodePage == Encoding.UTF8.CodePage &&
                    rawText.Length > 0 &&
                    rawText[0] == '\uFEFF')
                {
                    rawText = rawText[1..];
                }
            }
            else
            {
                rawText = Encoding.UTF8.GetString(bytes);
                if (rawText.Length > 0 && rawText[0] == '\uFEFF')
                {
                    rawText = rawText[1..];
                }
            }

            var normalized = _hashlineTextNormalizer!.Normalize(rawText);
            return _hashlineFingerprintProvider!.ComputeFingerprint(normalized.NormalizedText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to compute current Hashline fingerprint for duplicate-read guard. Path={Path}", fullPath);
            return null;
        }
    }

    private static bool TryResolveReadableFilePath(
        Dictionary<string, object?> args,
        string requestedPath,
        QueryRuntimeRequest request,
        out string fullPath,
        out string normalizedPath)
    {
        fullPath = string.Empty;
        normalizedPath = requestedPath;
        var baseRoot = ResolveSignatureBaseRoot(args, request);
        if (string.IsNullOrWhiteSpace(baseRoot))
        {
            return false;
        }

        normalizedPath = ToolPathResolver.NormalizeDuplicateRepoPrefix(requestedPath, baseRoot);
        fullPath = Path.GetFullPath(Path.IsPathRooted(normalizedPath)
            ? normalizedPath
            : Path.Combine(baseRoot, normalizedPath));

        if (!File.Exists(fullPath) &&
            !string.Equals(normalizedPath, requestedPath, StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = requestedPath;
            fullPath = Path.GetFullPath(Path.IsPathRooted(requestedPath)
                ? requestedPath
                : Path.Combine(baseRoot, requestedPath));
        }

        return ToolPathResolver.IsWithinRoot(fullPath, baseRoot) && File.Exists(fullPath);
    }

    private static string? ResolvePreferredWriteToolName(QueryRuntimeRequest request)
    {
        var availableTools = request.AvailableToolsProvider?.Invoke() ?? request.AvailableTools;
        var contract = request.RequiredToolContract ?? request.WorkerContext?.RequiredToolContract;
        var contractTool = contract?.ResolveRequiredToolName(availableTools);
        if (!string.IsNullOrWhiteSpace(contractTool))
        {
            return contractTool;
        }

        var availableNames = new HashSet<string>(
            availableTools?.Select(static tool => tool.Name) ?? [],
            StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[]
                 {
                     "hs_write",
                     "ivilson_smart_patch",
                     "apply_patch",
                     "edit_file",
                     "write_file"
                 })
        {
            if (availableNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static RuntimeReadEvidence? FindReadEvidenceForPath(
        QueryRuntimeState state,
        string filePath,
        QueryRuntimeRequest request)
    {
        var candidates = BuildComparablePathCandidates(filePath, request);
        foreach (var evidence in state.EvidenceLedger.Files)
        {
            var evidenceCandidates = BuildComparablePathCandidates(evidence.FilePath, request);
            if (candidates.Any(candidate => evidenceCandidates.Contains(candidate)))
            {
                return new RuntimeReadEvidence(
                    string.IsNullOrWhiteSpace(evidence.ToolName) ? "hs_read" : evidence.ToolName,
                    evidence.FilePath,
                    evidence.SnapshotId,
                    evidence.FileFingerprint,
                    evidence.WindowStartLine,
                    evidence.WindowEndLine,
                    evidence.TotalLineCount);
            }
        }

        return null;
    }

    private static HashSet<string> BuildComparablePathCandidates(string path, QueryRuntimeRequest request)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(path))
        {
            return result;
        }

        var normalized = path.Replace('\\', '/').Trim();
        result.Add(normalized);

        var baseRoot = ResolveSignatureBaseRoot(new Dictionary<string, object?>(), request);
        if (!string.IsNullOrWhiteSpace(baseRoot))
        {
            try
            {
                var fullPath = Path.IsPathRooted(path)
                    ? Path.GetFullPath(path)
                    : Path.GetFullPath(Path.Combine(baseRoot, path));
                result.Add(fullPath.Replace('\\', '/'));
                result.Add(Path.GetRelativePath(baseRoot, fullPath).Replace('\\', '/'));
            }
            catch
            {
                // Path comparison is best-effort; leave the original candidate.
            }
        }

        return result;
    }

    private static Dictionary<string, object?>? BuildSignatureArguments(
        IDictionary<string, object?>? arguments,
        QueryRuntimeRequest? request)
    {
        if (arguments == null)
        {
            return null;
        }

        var normalized = ToolArgumentNormalizer.NormalizeCopy(arguments);
        NormalizeSignaturePathArguments(normalized, request);
        return normalized;
    }

    private static void NormalizeSignaturePathArguments(
        Dictionary<string, object?> arguments,
        QueryRuntimeRequest? request)
    {
        if (arguments.Count == 0)
        {
            return;
        }

        var baseRoot = ResolveSignatureBaseRoot(arguments, request);
        if (string.IsNullOrWhiteSpace(baseRoot))
        {
            return;
        }

        NormalizeSignaturePathArgument(arguments, "path", baseRoot);
        NormalizeSignaturePathArgument(arguments, "file", baseRoot);
        NormalizeSignaturePathArgument(arguments, "dir", baseRoot);
        NormalizeSignaturePathArgument(arguments, "root", baseRoot);
    }

    private static void NormalizeSignaturePathArgument(
        Dictionary<string, object?> arguments,
        string key,
        string baseRoot)
    {
        if (!arguments.TryGetValue(key, out var rawValue))
        {
            return;
        }

        var path = ToolArgumentNormalizer.CoerceLooseStringScalarValue(rawValue);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        arguments[key] = ToolPathResolver.NormalizeDuplicateRepoPrefix(path, baseRoot);
    }

    private static string? ResolveSignatureBaseRoot(
        Dictionary<string, object?> arguments,
        QueryRuntimeRequest? request)
    {
        arguments.TryGetValue("workspace_path", out var workspaceValue);
        arguments.TryGetValue("project_root", out var projectRootValue);
        var workspacePath = ToolArgumentNormalizer.CoerceLooseStringScalarValue(workspaceValue);
        var projectRoot = ToolArgumentNormalizer.CoerceLooseStringScalarValue(projectRootValue);

        if (string.IsNullOrWhiteSpace(projectRoot) && request?.Session != null)
        {
            projectRoot = ToolPathResolver.ResolveProjectRoot(
                request.Session.WorkspacePath,
                null,
                request.Session.ProjectUrl,
                request.Session.Metadata);
        }

        workspacePath ??= request?.Session?.WorkspacePath;

        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        return string.IsNullOrWhiteSpace(baseRoot) ? null : baseRoot;
    }

    private sealed record ToolExecutionBatch(bool IsConcurrencySafe, List<FunctionCallContent> Calls);
    private sealed record PreparedToolExecution(FunctionCallContent? ToolCall, ToolExecutionResult? PrecomputedResult);
}
