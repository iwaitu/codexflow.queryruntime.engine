using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Runtime;

public interface IToolObservationProcessor
{
    ToolObservationResult Observe(ToolObservationRequest request);
}

public sealed class DefaultToolObservationProcessor : IToolObservationProcessor
{
    private const int EvidenceLedgerMaxFiles = 32;
    private const int EvidenceLedgerMaxToolResults = 24;
    private const int EvidenceLedgerMaxFailures = 12;
    private const int ToolSummaryMaxChars = 220;
    private const int ReadToolSummaryMaxChars = 4_000;
    private const int HashlineToolSummaryMaxChars = 8_000;
    private static readonly char[] InlineWhitespaceSeparators = ['\r', '\n', '\t'];

    public static DefaultToolObservationProcessor Instance { get; } = new();

    public ToolObservationResult Observe(ToolObservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var repeatedReadTargets = UpdateRepeatedReadEvidenceState(request.State, request.ToolResults);
        UpdateEvidenceLedgerState(request.State, request.ToolResults, repeatedReadTargets);

        return new ToolObservationResult
        {
            ToolResults = request.ToolResults,
            UpdatedLedger = request.State.EvidenceLedger,
            RequiredToolContractSatisfied = IsRequiredToolContractSatisfied(request.RuntimeRequest, request.State),
            HasWriteEvidence = request.ToolResults.Any(result => ToolClassification.IsWriteTool(result.ToolName)),
            HasRepeatedReadEvidence = repeatedReadTargets.Length > 0,
            RepeatedReadTargets = repeatedReadTargets
        };
    }

    private static bool IsRequiredToolContractSatisfied(
        QueryRuntimeRequest request,
        QueryRuntimeState state)
    {
        var contract = request.RequiredToolContract ?? request.WorkerContext?.RequiredToolContract;
        return contract?.IsSatisfiedBy(state.ExecutedToolNames, state.SuccessfulToolNames) == true;
    }

    private static string[] UpdateRepeatedReadEvidenceState(
        QueryRuntimeState state,
        IReadOnlyList<ToolExecutionResult> executionResults)
    {
        var repeatedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roundEvidenceKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var result in executionResults)
        {
            if (!TryExtractRepeatableReadEvidence(result, out var evidenceKey, out var displayTarget, out var readEvidence))
            {
                continue;
            }

            UpsertFileEvidence(state.EvidenceLedger.Files, ToFileEvidence(readEvidence));

            if (!roundEvidenceKeys.Add(evidenceKey))
            {
                continue;
            }

            if (state.EvidenceLedger.SeenReadEvidenceKeys.ContainsKey(evidenceKey))
            {
                repeatedTargets.Add(displayTarget);
            }

            state.EvidenceLedger.SeenReadEvidenceKeys[evidenceKey] = state.EvidenceLedger.SeenReadEvidenceKeys.TryGetValue(evidenceKey, out var count)
                ? count + 1
                : 1;
        }

        state.EvidenceLedger.RepeatedEvidenceKeys.Clear();
        foreach (var target in repeatedTargets.OrderBy(static target => target, StringComparer.OrdinalIgnoreCase))
        {
            state.EvidenceLedger.RepeatedEvidenceKeys.Add(target);
        }

        state.ConsecutiveRepeatedReadRounds = repeatedTargets.Count > 0
            ? state.ConsecutiveRepeatedReadRounds + 1
            : 0;

        return state.EvidenceLedger.RepeatedEvidenceKeys.ToArray();
    }

    private static void UpdateEvidenceLedgerState(
        QueryRuntimeState state,
        IReadOnlyList<ToolExecutionResult> executionResults,
        IReadOnlyList<string> repeatedReadTargets)
    {
        foreach (var result in executionResults)
        {
            UpsertToolEvidence(
                state.EvidenceLedger.ToolResults,
                new ToolEvidence
                {
                    ToolName = result.ToolName,
                    CallId = result.CallId,
                    Success = result.Success,
                    Summary = SummarizeToolExecutionResultForBatchPrompt(result),
                    ResultLength = result.ResultLength,
                    IsOutputTruncated = result.IsOutputTruncated
                });

            if (!result.Success)
            {
                AppendBounded(
                    state.EvidenceLedger.Failures,
                    new RuntimeFailureEvidence
                    {
                        Source = "tool_execution",
                        ToolName = result.ToolName,
                        CallId = result.CallId,
                        Message = string.IsNullOrWhiteSpace(result.Exception?.Message)
                            ? SummarizeToolExecutionResult(result)
                            : result.Exception.Message
                    },
                    EvidenceLedgerMaxFailures);
            }
        }

        if (executionResults.Any(static result => result.Success))
        {
            state.RecoveryHints.RemoveAll(static hint => hint.ToolCallRequired);
        }

        if (!string.IsNullOrWhiteSpace(state.DynamicRequiredToolName) &&
            executionResults.Any(result =>
                result.Success &&
                string.Equals(result.ToolName, state.DynamicRequiredToolName, StringComparison.OrdinalIgnoreCase)))
        {
            state.DynamicRequiredToolName = null;
            state.DynamicRequiredToolAttempts = 0;
        }

        foreach (var result in executionResults.Where(static result => result.SystemHintDetail != null || !string.IsNullOrWhiteSpace(result.SystemHint)))
        {
            var systemHint = ResolveSystemHint(result);
            if (systemHint == null)
            {
                continue;
            }

            AppendBounded(
                state.RecoveryHints,
                new RuntimeRecoveryHint
                {
                    Source = $"tool:{result.ToolName}",
                    RequiredToolName = systemHint.RequiredToolName,
                    ToolCallRequired = systemHint.ToolCallRequired,
                    Message = systemHint.Message
                },
                8);

            if (ShouldForceRequiredToolFromSystemHint(result, systemHint))
            {
                state.RequiredToolNameForNextRound = systemHint.RequiredToolName;
                state.ForceAllowToolCallsNextRound = true;
            }
        }

        if (executionResults.Any(static result => result.Success && ToolClassification.IsWriteTool(result.ToolName)))
        {
            state.EvidenceLedger.PendingModifications.Clear();
        }

        state.EvidenceLedger.RepeatedEvidenceKeys.Clear();
        foreach (var target in repeatedReadTargets.Take(16))
        {
            state.EvidenceLedger.RepeatedEvidenceKeys.Add(target);
        }

        TrimHead(state.EvidenceLedger.Files, EvidenceLedgerMaxFiles);
        TrimHead(state.EvidenceLedger.ToolResults, EvidenceLedgerMaxToolResults);
    }

    private static FileEvidence ToFileEvidence(RuntimeReadEvidence evidence)
        => new()
        {
            ToolName = evidence.ToolName,
            FilePath = evidence.FilePath,
            SnapshotId = evidence.SnapshotId,
            FileFingerprint = evidence.FileFingerprint,
            WindowStartLine = evidence.WindowStartLine,
            WindowEndLine = evidence.WindowEndLine,
            TotalLineCount = evidence.TotalLineCount,
            Summary = FormatReadEvidenceSummary(evidence)
        };

    private static void UpsertToolEvidence(List<ToolEvidence> toolEvidence, ToolEvidence evidence)
    {
        var index = toolEvidence.FindIndex(item =>
            !string.IsNullOrWhiteSpace(evidence.CallId) &&
            string.Equals(item.CallId, evidence.CallId, StringComparison.Ordinal));
        if (index >= 0)
        {
            toolEvidence[index] = evidence;
            return;
        }

        toolEvidence.Add(evidence);
        TrimHead(toolEvidence, EvidenceLedgerMaxToolResults);
    }

    private static void UpsertFileEvidence(List<FileEvidence> fileEvidence, FileEvidence evidence)
    {
        var index = fileEvidence.FindIndex(item =>
            string.Equals(item.FilePath, evidence.FilePath, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            fileEvidence[index] = evidence;
            return;
        }

        fileEvidence.Add(evidence);
        TrimHead(fileEvidence, EvidenceLedgerMaxFiles);
    }

    private static void AppendBounded<T>(List<T> items, T item, int maxCount)
    {
        items.Add(item);
        TrimHead(items, maxCount);
    }

    private static void TrimHead<T>(List<T> items, int maxCount)
    {
        if (items.Count <= maxCount)
        {
            return;
        }

        items.RemoveRange(0, items.Count - maxCount);
    }

    private static ToolSystemHint? ResolveSystemHint(ToolExecutionResult result)
    {
        if (result.SystemHintDetail != null)
        {
            return result.SystemHintDetail;
        }

        return string.IsNullOrWhiteSpace(result.SystemHint)
            ? null
            : new ToolSystemHint(result.SystemHint.Trim());
    }

    private static bool ShouldForceRequiredToolFromSystemHint(
        ToolExecutionResult result,
        ToolSystemHint systemHint)
        => systemHint.ToolCallRequired &&
           !string.IsNullOrWhiteSpace(systemHint.RequiredToolName) &&
           ((result.Success &&
             (string.Equals(result.ToolName, "approve_plan", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(result.ToolName, "project_plan_to_tasks", StringComparison.OrdinalIgnoreCase))) ||
            IsDuplicateHashlineReadGuardResult(result));

    private static bool IsDuplicateHashlineReadGuardResult(ToolExecutionResult result)
    {
        if (!TryGetMetadataObject(result.Metadata, out var metadata))
        {
            return false;
        }

        var reasonCode = GetMetadataString(metadata, "ReasonCode", "reasonCode");
        return string.Equals(
            reasonCode,
            DefaultToolExecutionCoordinator.DuplicateHashlineReadReasonCode,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractRepeatableReadEvidence(
        ToolExecutionResult result,
        out string evidenceKey,
        out string displayTarget,
        out RuntimeReadEvidence readEvidence)
    {
        evidenceKey = string.Empty;
        displayTarget = string.Empty;
        readEvidence = null!;

        if (!IsRepeatableReadTool(result.ToolName) || !TryGetMetadataObject(result.Metadata, out var metadata))
        {
            return false;
        }

        var filePath = GetMetadataString(metadata, "FilePath", "filePath");
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        displayTarget = filePath.Replace('\\', '/');
        var fingerprint = GetMetadataString(metadata, "FileFingerprint", "fileFingerprint", "Fingerprint", "fingerprint");
        var snapshotId = GetMetadataString(metadata, "SnapshotId", "snapshotId");
        var rangeStart = GetMetadataInt(metadata, "WindowStartLine", "windowStartLine", "StartLine", "startLine");
        var rangeEnd = GetMetadataInt(metadata, "WindowEndLine", "windowEndLine", "EndLine", "endLine");
        var totalLineCount = GetMetadataInt(metadata, "TotalLineCount", "totalLineCount", "Lines", "lines");

        readEvidence = new RuntimeReadEvidence(
            result.ToolName,
            displayTarget,
            snapshotId,
            fingerprint,
            rangeStart,
            rangeEnd,
            totalLineCount);

        if (!string.IsNullOrWhiteSpace(fingerprint))
        {
            evidenceKey = $"{result.ToolName}|{displayTarget}|fp:{fingerprint}";
            return true;
        }

        if (rangeStart.HasValue || rangeEnd.HasValue)
        {
            evidenceKey = $"{result.ToolName}|{displayTarget}|range:{rangeStart?.ToString() ?? "?"}-{rangeEnd?.ToString() ?? "?"}";
            return true;
        }

        evidenceKey = $"{result.ToolName}|{displayTarget}";
        return true;
    }

    private static bool IsRepeatableReadTool(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        return toolName.Equals("ivilson_read", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("hs_read", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetMetadataObject(object? metadata, out JObject json)
    {
        switch (metadata)
        {
            case null:
                json = null!;
                return false;
            case JObject jobject:
                json = jobject;
                return true;
            default:
                try
                {
                    json = JObject.FromObject(metadata);
                    return true;
                }
                catch
                {
                    json = null!;
                    return false;
                }
        }
    }

    private static string? GetMetadataString(JObject metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token))
            {
                var value = token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static int? GetMetadataInt(JObject metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!metadata.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token))
            {
                continue;
            }

            if (token.Type == JTokenType.Integer && token.Value<int?>() is { } intValue)
            {
                return intValue;
            }

            if (token.Type == JTokenType.String && int.TryParse(token.Value<string>(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string FormatReadEvidenceSummary(RuntimeReadEvidence evidence)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(evidence.FilePath);
        if (!string.IsNullOrWhiteSpace(evidence.SnapshotId))
        {
            builder.Append(" snapshot=");
            builder.Append(evidence.SnapshotId);
        }

        if (!string.IsNullOrWhiteSpace(evidence.FileFingerprint))
        {
            builder.Append(" fp=");
            builder.Append(evidence.FileFingerprint);
        }

        if (evidence.WindowStartLine.HasValue || evidence.WindowEndLine.HasValue)
        {
            builder.Append(" lines=");
            builder.Append(evidence.WindowStartLine?.ToString() ?? "?");
            builder.Append('-');
            builder.Append(evidence.WindowEndLine?.ToString() ?? "?");
        }

        return builder.ToString();
    }

    private static string SummarizeToolExecutionResult(ToolExecutionResult result)
        => SummarizeToolExecutionResult(result, ToolSummaryMaxChars);

    private static string SummarizeToolExecutionResultForBatchPrompt(ToolExecutionResult result)
        => SummarizeToolExecutionResult(result, GetToolSummaryMaxChars(result));

    private static string SummarizeToolExecutionResult(ToolExecutionResult result, int maxChars)
    {
        var preferred = string.IsNullOrWhiteSpace(result.Summary)
            ? NormalizeToolResultText(result.Result)
            : NormalizeToolResultText(result.Summary);
        if (string.IsNullOrWhiteSpace(preferred))
        {
            preferred = result.Success ? "Success" : "Error";
        }

        if (preferred.Length > maxChars)
        {
            preferred = preferred[..(maxChars - 3)] + "...";
        }

        if (result.IsOutputTruncated && !preferred.Contains("truncated", StringComparison.OrdinalIgnoreCase))
        {
            preferred += " [tool already truncated output]";
        }

        return preferred;
    }

    private static int GetToolSummaryMaxChars(ToolExecutionResult result)
    {
        if (IsHashlineReadResult(result))
        {
            return HashlineToolSummaryMaxChars;
        }

        if (IsReadFileTool(result.ToolName))
        {
            return ReadToolSummaryMaxChars;
        }

        return ToolSummaryMaxChars;
    }

    private static bool IsHashlineReadResult(ToolExecutionResult result)
        => IsReadFileTool(result.ToolName) &&
           (ContainsHashlineSnapshotHeader(result.Result) ||
            (TryGetMetadataObject(result.Metadata, out var metadata) &&
             !string.IsNullOrWhiteSpace(GetMetadataString(metadata, "FileFingerprint", "fileFingerprint", "Fingerprint", "fingerprint"))));

    private static bool IsReadFileTool(string? toolName)
        => string.Equals(toolName, "ivilson_read", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(toolName, "hs_read", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsHashlineSnapshotHeader(string? text)
        => !string.IsNullOrWhiteSpace(text) &&
           text.Contains("--- File (Hashline):", StringComparison.OrdinalIgnoreCase) &&
           text.Contains("SnapshotId:", StringComparison.OrdinalIgnoreCase) &&
           text.Contains("Fingerprint:", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeToolResultText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            text
                .Split(InlineWhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();
    }
}

public sealed record ToolObservationRequest
{
    public required IReadOnlyList<FunctionCallContent> ToolCalls { get; init; }

    public required IReadOnlyList<ToolExecutionResult> ToolResults { get; init; }

    public required QueryRuntimeRequest RuntimeRequest { get; init; }

    public required QueryRuntimeState State { get; init; }
}

public sealed record ToolObservationResult
{
    public required IReadOnlyList<ToolExecutionResult> ToolResults { get; init; }

    public required QueryEvidenceLedger UpdatedLedger { get; init; }

    public required bool RequiredToolContractSatisfied { get; init; }

    public required bool HasWriteEvidence { get; init; }

    public required bool HasRepeatedReadEvidence { get; init; }

    public required IReadOnlyList<string> RepeatedReadTargets { get; init; }
}
