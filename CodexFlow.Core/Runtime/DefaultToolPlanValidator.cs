using CodexFlow.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Runtime;

public interface IToolPlanValidator
{
    ValueTask<ToolPlanValidationOutput> ValidateAsync(
        ToolPlanValidationRequest request,
        CancellationToken ct = default);
}

public sealed class DefaultToolPlanValidator(
    IToolExecutionCoordinator? toolCoordinator,
    ILogger<DefaultToolPlanValidator> logger) : IToolPlanValidator
{
    private const int MaxExplorationToolCallsPerRound = 8;
    private const int MaxSameExplorationToolSignaturePerRound = 3;

    public async ValueTask<ToolPlanValidationOutput> ValidateAsync(
        ToolPlanValidationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prestartedCalls = new HashSet<FunctionCallContent>(
            request.PrestartedStreamingCalls,
            ReferenceEqualityComparer.Instance);
        var preBlockedToolResults = BuildExplorationToolCallLimitResults(request.ToolPlan.Calls, prestartedCalls);
        var executableToolCalls = new List<FunctionCallContent>(request.ToolPlan.Calls.Count);
        var blockedToolResults = new List<BlockedToolResult>();
        var rejectedToolCalls = new List<RejectedToolCall>();
        var injectedPreExecutionMessages = new List<ChatMessage>();
        var requiredToolViolations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var call in request.ToolPlan.Calls)
        {
            var toolName = ResolveToolCallName(call);
            if (preBlockedToolResults.TryGetValue(call, out var preBlockedTranscript))
            {
                blockedToolResults.Add(new BlockedToolResult
                {
                    Call = call,
                    Transcript = preBlockedTranscript
                });
                rejectedToolCalls.Add(new RejectedToolCall
                {
                    ToolName = toolName,
                    CallId = call.CallId ?? string.Empty,
                    ReasonCode = ToolPlanRejectionReasons.ExplorationLimitExceeded,
                    Detail = "exploration tool-call limit rejected this call before execution"
                });
                continue;
            }

            if (IsNonRequiredToolCall(call, request.RequiredToolNameForRound))
            {
                requiredToolViolations.Add(toolName);
                var detail = $"required-tool recovery round only permits `{request.RequiredToolNameForRound}`";
                blockedToolResults.Add(new BlockedToolResult
                {
                    Call = call,
                    Transcript = BuildSyntheticToolResultTranscript(
                        toolName,
                        $"{detail}; this tool call was rejected before execution",
                        "tool call rejected by required-tool runtime guard before execution")
                });
                rejectedToolCalls.Add(new RejectedToolCall
                {
                    ToolName = toolName,
                    CallId = call.CallId ?? string.Empty,
                    ReasonCode = ToolPlanRejectionReasons.NonRequiredToolInRequiredRound,
                    Detail = detail
                });
                continue;
            }

            if (request.RuntimeRequest.InterventionHook != null)
            {
                var guardrailResult = await request.RuntimeRequest.InterventionHook.OnToolCallRequestedAsync(
                    toolName,
                    call.Arguments ?? new Dictionary<string, object?>(),
                    request.RuntimeRequest.Session,
                    ct).ConfigureAwait(false);

                if (guardrailResult.ShouldBlock)
                {
                    var guardrailReason = string.IsNullOrWhiteSpace(guardrailResult.Reason)
                        ? "runtime intervention blocked this tool call"
                        : guardrailResult.Reason;
                    logger.LogWarning(
                        "Tool {ToolName} blocked by intervention hook. Reason: {Reason}",
                        toolName,
                        guardrailReason);

                    if (guardrailResult.InjectedMessage != null)
                    {
                        injectedPreExecutionMessages.Add(guardrailResult.InjectedMessage);
                    }

                    blockedToolResults.Add(new BlockedToolResult
                    {
                        Call = call,
                        Transcript = BuildSyntheticToolResultTranscript(
                            toolName,
                            guardrailReason,
                            "tool call blocked by runtime intervention before execution")
                    });
                    rejectedToolCalls.Add(new RejectedToolCall
                    {
                        ToolName = toolName,
                        CallId = call.CallId ?? string.Empty,
                        ReasonCode = ToolPlanRejectionReasons.RuntimeInterventionBlocked,
                        Detail = guardrailReason
                    });
                    continue;
                }
            }

            executableToolCalls.Add(call);
        }

        var validationResult = new ToolPlanValidationResult
        {
            AcceptedCalls = executableToolCalls.ToArray(),
            RejectedCalls = rejectedToolCalls.ToArray(),
            RequiresRecovery = requiredToolViolations.Count > 0,
            RecoveryReason = requiredToolViolations.Count > 0
                ? $"required_tool_violation:{request.RequiredToolNameForRound}"
                : null
        };

        return new ToolPlanValidationOutput
        {
            ValidationResult = validationResult,
            ExecutableToolCalls = executableToolCalls,
            BlockedToolResults = blockedToolResults,
            InjectedPreExecutionMessages = injectedPreExecutionMessages,
            RequiredToolViolations = requiredToolViolations
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private Dictionary<FunctionCallContent, string> BuildExplorationToolCallLimitResults(
        IReadOnlyList<FunctionCallContent> toolCalls,
        HashSet<FunctionCallContent> prestartedCalls)
    {
        var blocked = new Dictionary<FunctionCallContent, string>(ReferenceEqualityComparer.Instance);
        if (toolCalls.Count == 0)
        {
            return blocked;
        }

        var explorationCount = 0;
        var signatureCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var call in toolCalls)
        {
            var toolName = ResolveToolCallName(call);
            if (!IsExplorationToolName(toolName))
            {
                continue;
            }

            explorationCount++;
            var signature = toolCoordinator?.ComputeSignature(call) ?? BuildFallbackToolCallSignature(call);
            signatureCounts.TryGetValue(signature, out var signatureCount);
            signatureCount++;
            signatureCounts[signature] = signatureCount;

            if (prestartedCalls.Contains(call))
            {
                continue;
            }

            if (explorationCount <= MaxExplorationToolCallsPerRound &&
                signatureCount <= MaxSameExplorationToolSignaturePerRound)
            {
                continue;
            }

            var reason =
                $"本轮只读/搜索工具调用过多，runtime 已跳过该调用。上限为每轮 {MaxExplorationToolCallsPerRound} 个探索调用、同一目标 {MaxSameExplorationToolSignaturePerRound} 次。请基于已执行结果总结，必要时下一轮再读取明确的单个文件。";
            blocked[call] = BuildSyntheticToolResultTranscript(
                toolName,
                reason,
                "tool call skipped by runtime exploration limit");
        }

        if (blocked.Count > 0)
        {
            logger.LogWarning(
                "Runtime skipped {SkippedCount} excessive exploration tool call(s) in one round. TotalToolCalls={TotalToolCalls}",
                blocked.Count,
                toolCalls.Count);
        }

        return blocked;
    }

    private static bool IsNonRequiredToolCall(FunctionCallContent call, string? requiredToolName)
        => !string.IsNullOrWhiteSpace(requiredToolName) &&
           !string.Equals(call.Name, requiredToolName, StringComparison.OrdinalIgnoreCase);

    private static string BuildSyntheticToolResultTranscript(
        string toolName,
        string? reason,
        string fallbackReason)
    {
        var message = string.IsNullOrWhiteSpace(reason) ? fallbackReason : reason.Trim();
        return $"[runtime synthetic tool_result] {toolName}: {message}";
    }

    private static string ResolveToolCallName(FunctionCallContent call)
    {
        if (ToolCallSyntaxRecovery.TryNormalizeInlineInvocation(call.Name, call.Arguments, out var recoveredToolName, out _))
        {
            return recoveredToolName;
        }

        return call.Name ?? "unknown";
    }

    private static string BuildFallbackToolCallSignature(FunctionCallContent call)
    {
        var toolName = ResolveToolCallName(call);
        if (call.Arguments == null || call.Arguments.Count == 0)
        {
            return toolName;
        }

        var args = string.Join(
            "|",
            call.Arguments
                .OrderBy(argument => argument.Key, StringComparer.Ordinal)
                .Select(argument => $"{argument.Key}={argument.Value}"));
        return $"{toolName}:{args.GetHashCode(StringComparison.Ordinal):X}";
    }

    private static bool IsExplorationToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        return toolName.Contains("read", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("search", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("grep", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("find", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("show", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("analy", StringComparison.OrdinalIgnoreCase) ||
               toolName.EndsWith("_ls", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(toolName, "ls", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(toolName, "dir", StringComparison.OrdinalIgnoreCase);
    }
}
