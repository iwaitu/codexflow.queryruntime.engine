using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Workers;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using System.Text;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Projects runtime state into the prompt-facing context for one model round.
/// </summary>
public interface IQueryContextAssembler
{
    QueryContextAssemblyResult Assemble(QueryContextAssemblyRequest request);
}

public sealed record QueryContextAssemblyRequest
{
    public required QueryRuntimeRequest RuntimeRequest { get; init; }

    public required QueryRuntimeState State { get; init; }

    public required Guid QueryId { get; init; }

    public required VllmChatOptions Options { get; init; }

    public required IReadOnlyList<AIFunction> CurrentTools { get; init; }

    public string? PendingToolBatchSummaryPrompt { get; init; }

    public string? RequiredToolNameForRound { get; init; }

    public bool AllowToolCalls { get; init; }

    public IReadOnlyList<ChatMessage> DynamicContextMessages { get; init; } = [];
}

public sealed record QueryContextAssemblyResult
{
    public required List<ChatMessage> Messages { get; init; }

    public required PromptAssemblySnapshot Snapshot { get; init; }
}

public sealed class DefaultQueryContextAssembler : IQueryContextAssembler
{
    private const int EstimatedCharsPerToken = 4;
    private const int ToolSummaryMaxChars = 220;
    private const int ToolResultProjectionThresholdChars = 4_000;
    private const int ToolResultProjectionSummaryMaxChars = 1_200;
    private const int EvidenceLedgerPromptMaxChars = 1_600;
    private const int RecoveryHintPromptMaxChars = 1_200;

    public static DefaultQueryContextAssembler Instance { get; } = new();

    public QueryContextAssemblyResult Assemble(QueryContextAssemblyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rawMessages = BuildRoundMessages(request);
        var projection = ApplyContextProjection(request, rawMessages);
        var snapshot = BuildPromptAssemblySnapshot(
            request,
            projection.Messages,
            projection.BudgetDecisions,
            projection.DroppedFrames);
        return new QueryContextAssemblyResult
        {
            Messages = projection.Messages,
            Snapshot = snapshot
        };
    }

    private static List<ChatMessage> BuildRoundMessages(
        QueryContextAssemblyRequest request)
    {
        var persistedMessages = request.State.Messages;
        var pendingToolBatchSummaryPrompt = request.PendingToolBatchSummaryPrompt;
        var workerCapsulePrompt = BuildWorkerCapsulePrompt(request.RuntimeRequest, request.State);
        var runtimeCheckpointPrompt = BuildRuntimeCheckpointPrompt(request.RuntimeRequest);
        var toolSurfacePrompt = BuildToolSurfacePrompt(request);
        var recoveryHintsPrompt = BuildRecoveryHintsPrompt(request.State.RecoveryHints);
        var evidenceLedgerPrompt = BuildEvidenceLedgerPrompt(request.State.EvidenceLedger);
        var dynamicContextMessages = request.DynamicContextMessages
            .Where(static message => !string.IsNullOrWhiteSpace(message.Text))
            .ToArray();
        if (string.IsNullOrWhiteSpace(workerCapsulePrompt) &&
            string.IsNullOrWhiteSpace(runtimeCheckpointPrompt) &&
            string.IsNullOrWhiteSpace(toolSurfacePrompt) &&
            string.IsNullOrWhiteSpace(recoveryHintsPrompt) &&
            string.IsNullOrWhiteSpace(pendingToolBatchSummaryPrompt) &&
            string.IsNullOrWhiteSpace(evidenceLedgerPrompt) &&
            dynamicContextMessages.Length == 0)
        {
            return persistedMessages;
        }

        var messages = new List<ChatMessage>(persistedMessages.Count + 5 + dynamicContextMessages.Length);
        messages.AddRange(persistedMessages);
        var prefixMessages = new List<ChatMessage>(2 + dynamicContextMessages.Length);
        if (!string.IsNullOrWhiteSpace(workerCapsulePrompt))
        {
            prefixMessages.Add(new ChatMessage(ChatRole.User, workerCapsulePrompt));
        }

        if (dynamicContextMessages.Length > 0)
        {
            prefixMessages.AddRange(dynamicContextMessages);
        }

        if (!string.IsNullOrWhiteSpace(runtimeCheckpointPrompt))
        {
            prefixMessages.Add(new ChatMessage(ChatRole.User, runtimeCheckpointPrompt));
        }

        if (!string.IsNullOrWhiteSpace(toolSurfacePrompt))
        {
            prefixMessages.Add(new ChatMessage(ChatRole.User, toolSurfacePrompt));
        }

        if (prefixMessages.Count > 0)
        {
            InsertAfterLeadingSystemMessages(messages, prefixMessages);
        }

        if (!string.IsNullOrWhiteSpace(evidenceLedgerPrompt))
        {
            var ledgerMessage = new ChatMessage(ChatRole.User, evidenceLedgerPrompt);
            if (string.IsNullOrWhiteSpace(pendingToolBatchSummaryPrompt) &&
                messages.Count > 0 &&
                IsTrailingRuntimeInstruction(messages[^1]))
            {
                messages.Insert(messages.Count - 1, ledgerMessage);
            }
            else
            {
                messages.Add(ledgerMessage);
            }
        }

        if (!string.IsNullOrWhiteSpace(recoveryHintsPrompt))
        {
            InsertBeforeTrailingRuntimeInstruction(
                messages,
                new ChatMessage(ChatRole.User, recoveryHintsPrompt));
        }

        if (!string.IsNullOrWhiteSpace(pendingToolBatchSummaryPrompt))
        {
            messages.Add(new ChatMessage(ChatRole.User, pendingToolBatchSummaryPrompt));
        }

        return messages;
    }

    private static PromptAssemblySnapshot BuildPromptAssemblySnapshot(
        QueryContextAssemblyRequest assemblyRequest,
        List<ChatMessage> messagesForRound,
        IReadOnlyList<string> projectionBudgetDecisions,
        IReadOnlyList<string> droppedFrames)
    {
        var runtimeRequest = assemblyRequest.RuntimeRequest;
        var state = assemblyRequest.State;
        var frames = BuildPromptAssemblyFrames(assemblyRequest, messagesForRound);
        var rawToolNames = assemblyRequest.Options.Tools?
            .Select(static tool => tool.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var toolNames = ProjectRequiredToolNames(rawToolNames, assemblyRequest.RequiredToolNameForRound);
        var estimatedContextChars = messagesForRound.Sum(EstimateChatMessageChars);
        var budgetDecisions = BuildPromptAssemblyBudgetDecisions(
            assemblyRequest,
            messagesForRound,
            frames,
            projectionBudgetDecisions);

        return new PromptAssemblySnapshot
        {
            QueryId = assemblyRequest.QueryId,
            SessionId = runtimeRequest.SessionId,
            Round = state.Round,
            EntryPoint = runtimeRequest.EntryPoint.ToString(),
            Frames = frames,
            ToolNames = toolNames,
            ToolChoice = FormatToolChoice(assemblyRequest.Options),
            RequiredToolName = string.IsNullOrWhiteSpace(assemblyRequest.RequiredToolNameForRound)
                ? null
                : assemblyRequest.RequiredToolNameForRound,
            ToolsEnabled = runtimeRequest.EnableTools,
            ToolCallsAllowed = assemblyRequest.AllowToolCalls,
            MessageCount = messagesForRound.Count,
            EstimatedContextChars = estimatedContextChars,
            EstimatedPromptTokens = EstimateTokens(estimatedContextChars),
            DroppedFrames = droppedFrames,
            BudgetDecisions = budgetDecisions
        };
    }

    private static PromptAssemblyFrameRecord[] BuildPromptAssemblyFrames(
        QueryContextAssemblyRequest assemblyRequest,
        List<ChatMessage> messagesForRound)
    {
        var runtimeRequest = assemblyRequest.RuntimeRequest;
        var state = assemblyRequest.State;
        var frames = new List<PromptAssemblyFrameRecord>();
        var systemMessages = messagesForRound
            .Where(static message => message.Role == ChatRole.System)
            .ToArray();
        if (systemMessages.Length > 0)
        {
            AddPromptAssemblyFrame(
                frames,
                "stable_system_messages",
                PromptAssemblyFrameKind.StableSystem,
                priority: 1000,
                estimatedChars: systemMessages.Sum(EstimateChatMessageChars),
                stableAcrossRounds: true,
                compressible: false,
                source: "InitialMessages",
                summary: $"{systemMessages.Length} system message(s)");
        }

        var nonSystemMessages = messagesForRound.Count - systemMessages.Length;
        AddPromptAssemblyFrame(
            frames,
            "recent_transcript",
            PromptAssemblyFrameKind.RecentTranscript,
            priority: 800,
            estimatedChars: messagesForRound.Sum(EstimateChatMessageChars),
            stableAcrossRounds: false,
            compressible: true,
            source: "QueryRuntimeState.Messages",
            summary: $"{nonSystemMessages} non-system message(s), {messagesForRound.Count} total message(s)");

        if (assemblyRequest.DynamicContextMessages.Count > 0)
        {
            var estimatedChars = assemblyRequest.DynamicContextMessages.Sum(EstimateChatMessageChars);
            AddPromptAssemblyFrame(
                frames,
                "dynamic_context",
                PromptAssemblyFrameKind.WorkerCapsule,
                priority: 960,
                estimatedChars: estimatedChars,
                stableAcrossRounds: false,
                compressible: false,
                source: nameof(QueryRuntimeRequest.DynamicContextProvider),
                summary: $"{assemblyRequest.DynamicContextMessages.Count} dynamic context message(s)");
        }

        if (!string.IsNullOrWhiteSpace(assemblyRequest.PendingToolBatchSummaryPrompt))
        {
            AddPromptAssemblyFrame(
                frames,
                "pending_tool_batch_summary",
                PromptAssemblyFrameKind.RecoveryHint,
                priority: 930,
                estimatedChars: assemblyRequest.PendingToolBatchSummaryPrompt!.Length,
                stableAcrossRounds: false,
                compressible: true,
                source: nameof(QueryRuntimeState.PendingToolBatchSummaryPrompt),
                summary: TruncateForPrompt(NormalizePromptSummary(assemblyRequest.PendingToolBatchSummaryPrompt), ToolSummaryMaxChars));
        }

        var runtimeDirective = messagesForRound.LastOrDefault(IsTrailingRuntimeInstruction);
        if (runtimeDirective != null)
        {
            AddPromptAssemblyFrame(
                frames,
                "runtime_action_directive",
                PromptAssemblyFrameKind.RecoveryHint,
                priority: 990,
                estimatedChars: EstimateChatMessageChars(runtimeDirective),
                stableAcrossRounds: false,
                compressible: false,
                source: "ProjectedMessages",
                summary: TruncateForPrompt(NormalizePromptSummary(runtimeDirective.Text ?? string.Empty), ToolSummaryMaxChars));
        }

        if (runtimeRequest.WorkerContext != null)
        {
            var workerSummary =
                $"{runtimeRequest.WorkerContext.DisplayName} ({runtimeRequest.WorkerContext.WorkerType}), isolation={runtimeRequest.WorkerContext.IsolationMode}, output={runtimeRequest.WorkerContext.OutputContract}";
            AddPromptAssemblyFrame(
                frames,
                "worker_capsule",
                PromptAssemblyFrameKind.WorkerCapsule,
                priority: 950,
                estimatedChars: workerSummary.Length,
                stableAcrossRounds: true,
                compressible: false,
                source: nameof(QueryRuntimeRequest.WorkerContext),
                summary: workerSummary);
        }

        if (TryGetRuntimeCheckpointMetadata(runtimeRequest, out var checkpoint))
        {
            AddPromptAssemblyFrame(
                frames,
                "runtime_checkpoint",
                PromptAssemblyFrameKind.CompactBoundary,
                priority: 945,
                estimatedChars: checkpoint.ToString(Newtonsoft.Json.Formatting.None).Length,
                stableAcrossRounds: true,
                compressible: true,
                source: $"{nameof(QueryRuntimeRequest.Session)}.Metadata[{QueryRuntimeCheckpoint.MetadataKey}]",
                summary: BuildRuntimeCheckpointFrameSummary(checkpoint));
        }

        var toolSummary = assemblyRequest.CurrentTools.Count == 0
            ? "no available tools"
            : string.Join(", ", assemblyRequest.CurrentTools.Select(static tool => tool.Name).OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).Take(12));
        AddPromptAssemblyFrame(
            frames,
            "tool_surface",
            PromptAssemblyFrameKind.ToolSurface,
            priority: 900,
            estimatedChars: toolSummary.Length,
            stableAcrossRounds: false,
            compressible: false,
            source: "AvailableToolsProvider",
            summary: assemblyRequest.CurrentTools.Count > 12
                ? $"{toolSummary}, ... ({assemblyRequest.CurrentTools.Count} total)"
                : $"{toolSummary} ({assemblyRequest.CurrentTools.Count} total)");

        if (!string.IsNullOrWhiteSpace(assemblyRequest.RequiredToolNameForRound))
        {
            AddPromptAssemblyFrame(
                frames,
                "required_tool_recovery",
                PromptAssemblyFrameKind.RecoveryHint,
                priority: 980,
                estimatedChars: assemblyRequest.RequiredToolNameForRound!.Length,
                stableAcrossRounds: false,
                compressible: false,
                source: nameof(QueryRuntimeState.RequiredToolNameForNextRound),
                summary: $"required tool: {assemblyRequest.RequiredToolNameForRound}");
        }

        if (state.RecoveryHints.Count > 0)
        {
            var recoveryHintSummary = string.Join(
                "; ",
                state.RecoveryHints
                    .TakeLast(4)
                    .Select(FormatRecoveryHintSummary));
            AddPromptAssemblyFrame(
                frames,
                "runtime_recovery_hints",
                PromptAssemblyFrameKind.RecoveryHint,
                priority: 985,
                estimatedChars: recoveryHintSummary.Length,
                stableAcrossRounds: false,
                compressible: true,
                source: nameof(QueryRuntimeState.RecoveryHints),
                summary: TruncateForPrompt(recoveryHintSummary, ToolSummaryMaxChars));
        }

        AddEvidenceLedgerFrames(frames, state);

        if (!assemblyRequest.AllowToolCalls)
        {
            AddPromptAssemblyFrame(
                frames,
                "tool_call_suppression",
                PromptAssemblyFrameKind.RecoveryHint,
                priority: 970,
                estimatedChars: 32,
                stableAcrossRounds: false,
                compressible: false,
                source: "ShouldAllowToolCallsThisRound",
                summary: "tool calls disabled for this round");
        }

        return frames
            .OrderByDescending(static frame => frame.Priority)
            .ThenBy(static frame => frame.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] ProjectRequiredToolNames(string[] toolNames, string? requiredToolName)
    {
        if (string.IsNullOrWhiteSpace(requiredToolName) || toolNames.Length == 0)
        {
            return toolNames;
        }

        var projected = toolNames
            .Where(name => string.Equals(name, requiredToolName.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return projected.Length == 0 ? toolNames : projected;
    }

    private static void AddEvidenceLedgerFrames(
        List<PromptAssemblyFrameRecord> frames,
        QueryRuntimeState state)
    {
        if (state.EvidenceLedger.Files.Count > 0)
        {
            var evidenceSummary = string.Join(
                "; ",
                state.EvidenceLedger.Files
                    .OrderBy(static evidence => evidence.FilePath, StringComparer.OrdinalIgnoreCase)
                    .Take(6)
                    .Select(FormatFileEvidenceSummary));
            AddPromptAssemblyFrame(
                frames,
                "read_evidence_ledger",
                PromptAssemblyFrameKind.EvidenceLedger,
                priority: 960,
                estimatedChars: evidenceSummary.Length,
                stableAcrossRounds: false,
                compressible: true,
                source: nameof(QueryRuntimeState.EvidenceLedger),
                summary: state.EvidenceLedger.Files.Count > 6
                    ? $"{evidenceSummary}; ... ({state.EvidenceLedger.Files.Count} file(s) total)"
                    : evidenceSummary);
        }

        if (state.EvidenceLedger.ToolResults.Count > 0)
        {
            var toolEvidenceSummary = string.Join(
                "; ",
                state.EvidenceLedger.ToolResults
                    .TakeLast(8)
                    .Select(static evidence => $"{evidence.ToolName}:{(evidence.Success ? "ok" : "failed")}"));
            AddPromptAssemblyFrame(
                frames,
                "tool_result_evidence",
                PromptAssemblyFrameKind.EvidenceLedger,
                priority: 920,
                estimatedChars: toolEvidenceSummary.Length,
                stableAcrossRounds: false,
                compressible: true,
                source: nameof(QueryRuntimeState.EvidenceLedger),
                summary: toolEvidenceSummary);
        }

        if (state.EvidenceLedger.PendingModifications.Count > 0)
        {
            var pendingSummary = string.Join(
                "; ",
                state.EvidenceLedger.PendingModifications
                    .TakeLast(4)
                    .Select(FormatPendingModificationEvidenceSummary));
            AddPromptAssemblyFrame(
                frames,
                "pending_modification_evidence",
                PromptAssemblyFrameKind.EvidenceLedger,
                priority: 950,
                estimatedChars: pendingSummary.Length,
                stableAcrossRounds: false,
                compressible: true,
                source: nameof(QueryRuntimeState.EvidenceLedger),
                summary: TruncateForPrompt(pendingSummary, ToolSummaryMaxChars));
        }

        if (!string.IsNullOrWhiteSpace(state.EvidenceLedger.LastToolBatchSummary))
        {
            AddPromptAssemblyFrame(
                frames,
                "tool_batch_summary_evidence",
                PromptAssemblyFrameKind.EvidenceLedger,
                priority: 930,
                estimatedChars: state.EvidenceLedger.LastToolBatchSummary.Length,
                stableAcrossRounds: false,
                compressible: true,
                source: nameof(QueryRuntimeState.EvidenceLedger),
                summary: TruncateForPrompt(
                    NormalizePromptSummary(state.EvidenceLedger.LastToolBatchSummary),
                    ToolSummaryMaxChars));
        }

        if (state.EvidenceLedger.RepeatedEvidenceKeys.Count > 0)
        {
            var repeatedSummary = string.Join(", ", state.EvidenceLedger.RepeatedEvidenceKeys.Take(8));
            AddPromptAssemblyFrame(
                frames,
                "repeated_read_evidence",
                PromptAssemblyFrameKind.EvidenceLedger,
                priority: 940,
                estimatedChars: repeatedSummary.Length,
                stableAcrossRounds: false,
                compressible: true,
                source: nameof(QueryRuntimeState.EvidenceLedger),
                summary: repeatedSummary);
        }

        if (state.EvidenceLedger.Failures.Count > 0)
        {
            var failureSummary = string.Join(
                "; ",
                state.EvidenceLedger.Failures
                    .TakeLast(4)
                    .Select(static evidence => string.IsNullOrWhiteSpace(evidence.ToolName)
                        ? evidence.Message
                        : $"{evidence.ToolName}:{evidence.Message}"));
            AddPromptAssemblyFrame(
                frames,
                "runtime_failure_evidence",
                PromptAssemblyFrameKind.EvidenceLedger,
                priority: 910,
                estimatedChars: failureSummary.Length,
                stableAcrossRounds: false,
                compressible: true,
                source: nameof(QueryRuntimeState.EvidenceLedger),
                summary: TruncateForPrompt(failureSummary, ToolSummaryMaxChars));
        }
    }

    private static List<string> BuildPromptAssemblyBudgetDecisions(
        QueryContextAssemblyRequest assemblyRequest,
        List<ChatMessage> messagesForRound,
        IReadOnlyList<PromptAssemblyFrameRecord> frames,
        IReadOnlyList<string> projectionBudgetDecisions)
    {
        var runtimeRequest = assemblyRequest.RuntimeRequest;
        var state = assemblyRequest.State;
        var estimatedContextChars = messagesForRound.Sum(EstimateChatMessageChars);
        var decisions = new List<string>
        {
            $"messages={messagesForRound.Count}",
            $"estimated_context_chars={estimatedContextChars}"
        };
        decisions.AddRange(projectionBudgetDecisions);

        if (!runtimeRequest.EnableTools)
        {
            decisions.Add("tools_disabled_by_request");
        }
        else if (!assemblyRequest.AllowToolCalls)
        {
            decisions.Add("tools_disabled_by_round_policy");
        }
        else if (!string.IsNullOrWhiteSpace(assemblyRequest.RequiredToolNameForRound))
        {
            decisions.Add($"required_tool_filter={assemblyRequest.RequiredToolNameForRound}");
        }

        var sentToolCount = ProjectRequiredToolNames(
            assemblyRequest.Options.Tools?
                .Select(static tool => tool.Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!)
                .ToArray() ?? [],
            assemblyRequest.RequiredToolNameForRound).Length;
        if (assemblyRequest.CurrentTools.Count != sentToolCount)
        {
            decisions.Add($"tool_surface_projected={assemblyRequest.CurrentTools.Count}->{sentToolCount}");
        }

        if (!string.IsNullOrWhiteSpace(assemblyRequest.PendingToolBatchSummaryPrompt))
        {
            decisions.Add("pending_tool_batch_summary_injected");
        }

        if (state.RecoveryHints.Count > 0)
        {
            decisions.Add($"runtime_recovery_hints={state.RecoveryHints.Count}");
            decisions.Add("runtime_recovery_hint_prompt_injected");
        }

        if (messagesForRound.Any(IsTrailingRuntimeInstruction))
        {
            decisions.Add("runtime_action_directive_present");
        }

        if (runtimeRequest.WorkerContext != null)
        {
            decisions.Add("worker_capsule_prompt_injected");
        }

        if (TryGetRuntimeCheckpointMetadata(runtimeRequest, out _))
        {
            decisions.Add("runtime_checkpoint_prompt_injected");
        }

        if (runtimeRequest.EnableTools)
        {
            decisions.Add("tool_surface_prompt_injected");
        }

        if (state.EvidenceLedger.Files.Count > 0)
        {
            decisions.Add($"read_evidence_files={state.EvidenceLedger.Files.Count}");
            decisions.Add("evidence_ledger_prompt_injected");
        }

        if (state.EvidenceLedger.ToolResults.Count > 0)
        {
            decisions.Add($"tool_evidence_results={state.EvidenceLedger.ToolResults.Count}");
            if (!decisions.Contains("evidence_ledger_prompt_injected", StringComparer.Ordinal))
            {
                decisions.Add("evidence_ledger_prompt_injected");
            }
        }

        if (state.EvidenceLedger.PendingModifications.Count > 0)
        {
            decisions.Add($"pending_modification_evidence={state.EvidenceLedger.PendingModifications.Count}");
            if (!decisions.Contains("evidence_ledger_prompt_injected", StringComparer.Ordinal))
            {
                decisions.Add("evidence_ledger_prompt_injected");
            }
        }

        if (!string.IsNullOrWhiteSpace(state.EvidenceLedger.LastToolBatchSummary))
        {
            decisions.Add("tool_batch_summary_evidence_present");
            if (!decisions.Contains("evidence_ledger_prompt_injected", StringComparer.Ordinal))
            {
                decisions.Add("evidence_ledger_prompt_injected");
            }
        }

        if (state.EvidenceLedger.RepeatedEvidenceKeys.Count > 0)
        {
            decisions.Add($"repeated_read_targets={state.EvidenceLedger.RepeatedEvidenceKeys.Count}");
        }

        if (state.EvidenceLedger.Failures.Count > 0)
        {
            decisions.Add($"runtime_failures={state.EvidenceLedger.Failures.Count}");
        }

        AddContextBudgetDecisions(decisions, runtimeRequest, estimatedContextChars, frames);
        return decisions;
    }

    private static ContextProjectionResult ApplyContextProjection(
        QueryContextAssemblyRequest request,
        List<ChatMessage> rawMessages)
    {
        var toolResultProjection = ApplyToolResultBudgetProjection(request, rawMessages);
        var messages = toolResultProjection.Messages;
        var budgetDecisions = new List<string>(toolResultProjection.BudgetDecisions);
        var droppedFrames = new List<string>(toolResultProjection.DroppedFrames);
        var hardLimit = request.RuntimeRequest.AdapterHints?.ContextHardLimit;
        if (hardLimit is not > 0)
        {
            return new ContextProjectionResult(messages, budgetDecisions, droppedFrames);
        }

        var originalChars = messages.Sum(EstimateChatMessageChars);
        if (originalChars <= hardLimit.Value)
        {
            return new ContextProjectionResult(messages, budgetDecisions, droppedFrames);
        }

        if (messages.Any(ContainsStructuredToolContent))
        {
            budgetDecisions.Add("context_projection_skipped=tool_pairing_present");
            budgetDecisions.Add($"context_projection_original_chars={originalChars}");
            return new ContextProjectionResult(messages, budgetDecisions, droppedFrames);
        }

        var systemMessages = messages
            .Select(static (message, index) => (Message: message, Index: index))
            .Where(static item => item.Message.Role == ChatRole.System)
            .ToArray();
        var nonSystemMessages = messages
            .Select(static (message, index) => (Message: message, Index: index))
            .Where(static item => item.Message.Role != ChatRole.System)
            .ToArray();
        if (nonSystemMessages.Length <= 4)
        {
            budgetDecisions.Add("context_projection_skipped=insufficient_tail");
            budgetDecisions.Add($"context_projection_original_chars={originalChars}");
            return new ContextProjectionResult(messages, budgetDecisions, droppedFrames);
        }

        var tailStartIndex = nonSystemMessages[^4].Index;
        var droppedMessages = nonSystemMessages
            .Where(item => item.Index < tailStartIndex)
            .Select(static item => item.Message)
            .ToArray();
        if (droppedMessages.Length == 0)
        {
            return new ContextProjectionResult(messages, budgetDecisions, droppedFrames);
        }

        var projectedMessages = new List<ChatMessage>(systemMessages.Length + 5);
        var boundaryInserted = false;
        foreach (var item in messages.Select(static (message, index) => (Message: message, Index: index)))
        {
            if (item.Message.Role == ChatRole.System)
            {
                projectedMessages.Add(item.Message);
                continue;
            }

            if (item.Index < tailStartIndex)
            {
                if (!boundaryInserted)
                {
                    projectedMessages.Add(BuildCompactBoundaryMessage(droppedMessages, originalChars));
                    boundaryInserted = true;
                }

                continue;
            }

            projectedMessages.Add(item.Message);
        }

        var projectedChars = projectedMessages.Sum(EstimateChatMessageChars);
        budgetDecisions.Add("context_projection_applied=recent_transcript");
        budgetDecisions.Add($"context_projection_original_chars={originalChars}");
        budgetDecisions.Add($"context_projection_projected_chars={projectedChars}");
        budgetDecisions.Add($"context_projection_dropped_messages={droppedMessages.Length}");
        droppedFrames.Add("recent_transcript");
        return new ContextProjectionResult(
            projectedMessages,
            budgetDecisions,
            droppedFrames);
    }

    private static ContextProjectionResult ApplyToolResultBudgetProjection(
        QueryContextAssemblyRequest request,
        List<ChatMessage> rawMessages)
    {
        List<ChatMessage>? projectedMessages = null;
        var projectedCount = 0;
        var originalChars = 0;
        var projectedChars = 0;

        for (var messageIndex = 0; messageIndex < rawMessages.Count; messageIndex++)
        {
            var message = rawMessages[messageIndex];
            if (message.Contents == null || message.Contents.Count == 0)
            {
                projectedMessages?.Add(message);
                continue;
            }

            List<AIContent>? projectedContents = null;
            for (var contentIndex = 0; contentIndex < message.Contents.Count; contentIndex++)
            {
                var content = message.Contents[contentIndex];
                if (content is FunctionResultContent resultContent &&
                    TryProjectToolResultContent(request.State.EvidenceLedger, resultContent, out var projectedContent, out var rawLength, out var projectionLength))
                {
                    projectedContents ??= message.Contents.Take(contentIndex).ToList();
                    projectedContents.Add(projectedContent);
                    projectedCount++;
                    originalChars += rawLength;
                    projectedChars += projectionLength;
                    continue;
                }

                projectedContents?.Add(content);
            }

            if (projectedContents == null)
            {
                projectedMessages?.Add(message);
                continue;
            }

            if (projectedMessages == null)
            {
                projectedMessages = rawMessages.Take(messageIndex).ToList();
            }

            projectedMessages.Add(new ChatMessage(message.Role, projectedContents));
        }

        if (projectedMessages == null)
        {
            return new ContextProjectionResult(rawMessages, [], []);
        }

        return new ContextProjectionResult(
            projectedMessages,
            [
                "tool_result_budget_projection_applied",
                $"tool_result_budget_projected_results={projectedCount}",
                $"tool_result_budget_original_chars={originalChars}",
                $"tool_result_budget_projected_chars={projectedChars}"
            ],
            ["tool_result_raw_output"]);
    }

    private static bool TryProjectToolResultContent(
        QueryEvidenceLedger ledger,
        FunctionResultContent resultContent,
        out FunctionResultContent projectedContent,
        out int rawLength,
        out int projectedLength)
    {
        var rawResult = resultContent.Result?.ToString() ?? string.Empty;
        rawLength = rawResult.Length;
        projectedLength = 0;
        projectedContent = resultContent;
        if (rawLength <= ToolResultProjectionThresholdChars)
        {
            return false;
        }

        var summary = ledger.ToolResults
            .LastOrDefault(item => string.Equals(item.CallId, resultContent.CallId, StringComparison.Ordinal))
            ?.Summary;
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = TruncateForPrompt(NormalizePromptSummary(rawResult), ToolResultProjectionSummaryMaxChars);
        }
        else
        {
            summary = TruncateForPrompt(NormalizePromptSummary(summary), ToolResultProjectionSummaryMaxChars);
        }

        var projectedText =
            $"[SYSTEM] Tool result budget projection applied. callId={resultContent.CallId}, rawChars={rawLength}. Full raw output was omitted from this model round; use the preserved handles/evidence or request a narrower follow-up if exact omitted content is required. Summary: {summary}";
        projectedLength = projectedText.Length;
        projectedContent = new FunctionResultContent(resultContent.CallId, projectedText);
        return true;
    }

    private static bool ContainsStructuredToolContent(ChatMessage message)
        => message.Contents?.Any(static content =>
            content is FunctionCallContent ||
            content is FunctionResultContent) == true;

    private static ChatMessage BuildCompactBoundaryMessage(
        ChatMessage[] droppedMessages,
        int originalChars)
    {
        var summary = SummarizeDroppedMessages(droppedMessages);
        var text = string.IsNullOrWhiteSpace(summary)
            ? $"[SYSTEM] Context compaction applied before this model round. Omitted {droppedMessages.Length} older plain-text transcript message(s); original estimate was {originalChars} chars."
            : $"[SYSTEM] Context compaction applied before this model round. Omitted {droppedMessages.Length} older plain-text transcript message(s); original estimate was {originalChars} chars. Omitted preview: {summary}";
        return new ChatMessage(ChatRole.User, text);
    }

    private static string SummarizeDroppedMessages(ChatMessage[] droppedMessages)
        => string.Join(
            " | ",
            droppedMessages
                .Take(4)
                .Select(static message =>
                    $"{message.Role}: {TruncateForPrompt(NormalizePromptSummary(message.Text ?? string.Empty), 80)}")
                .Where(static value => !string.IsNullOrWhiteSpace(value)));

    private static void AddContextBudgetDecisions(
        List<string> decisions,
        QueryRuntimeRequest runtimeRequest,
        int estimatedContextChars,
        IReadOnlyList<PromptAssemblyFrameRecord> frames)
    {
        var warnLimit = runtimeRequest.AdapterHints?.ContextWarnLimit;
        var hardLimit = runtimeRequest.AdapterHints?.ContextHardLimit;
        var warnExceeded = warnLimit is > 0 && estimatedContextChars > warnLimit.Value;
        var hardExceeded = hardLimit is > 0 && estimatedContextChars > hardLimit.Value;

        if (!warnExceeded && !hardExceeded)
        {
            return;
        }

        if (warnExceeded)
        {
            decisions.Add($"context_warn_limit_exceeded={estimatedContextChars}>{warnLimit}");
        }

        if (hardExceeded)
        {
            decisions.Add($"context_hard_limit_exceeded={estimatedContextChars}>{hardLimit}");
        }

        var compressibleFrames = frames
            .Where(static frame => frame.Compressible)
            .OrderBy(static frame => frame.Priority)
            .ThenBy(static frame => frame.Name, StringComparer.Ordinal)
            .ToArray();
        var compressibleChars = compressibleFrames.Sum(static frame => frame.EstimatedChars);
        if (compressibleChars <= 0)
        {
            decisions.Add("context_budget_no_compressible_frames");
            return;
        }

        decisions.Add($"compressible_context_chars={compressibleChars}");
        decisions.Add("compact_candidate_frames=" + string.Join(",", compressibleFrames.Take(4).Select(static frame => frame.Name)));
    }

    private static void AddPromptAssemblyFrame(
        List<PromptAssemblyFrameRecord> frames,
        string name,
        PromptAssemblyFrameKind kind,
        int priority,
        int estimatedChars,
        bool stableAcrossRounds,
        bool compressible,
        string source,
        string? summary)
    {
        frames.Add(new PromptAssemblyFrameRecord
        {
            Name = name,
            Kind = kind,
            Priority = priority,
            EstimatedChars = Math.Max(0, estimatedChars),
            EstimatedTokens = EstimateTokens(estimatedChars),
            StableAcrossRounds = stableAcrossRounds,
            Compressible = compressible,
            Source = source,
            Summary = string.IsNullOrWhiteSpace(summary) ? null : summary
        });
    }

    private static int EstimateChatMessageChars(ChatMessage message)
    {
        var text = message.Text;
        if (!string.IsNullOrEmpty(text))
        {
            return text.Length + message.Role.ToString().Length + 8;
        }

        return message.Role.ToString().Length +
            (message.Contents?.Sum(static content => content.ToString()?.Length ?? 0) ?? 0) +
            8;
    }

    private static int EstimateTokens(int chars)
        => (int)Math.Ceiling(Math.Max(0, chars) / (double)EstimatedCharsPerToken);

    private static string? FormatToolChoice(VllmChatOptions options)
    {
        var toolMode = options.ToolMode;
        if (toolMode != null)
        {
            return toolMode.ToString();
        }

        if (options.AdditionalProperties != null &&
            options.AdditionalProperties.TryGetValue("tool_choice", out var toolChoice) &&
            toolChoice != null)
        {
            return toolChoice.ToString();
        }

        return null;
    }

    private static string FormatFileEvidenceSummary(FileEvidence evidence)
    {
        var builder = new StringBuilder();
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

    private static string? BuildEvidenceLedgerPrompt(QueryEvidenceLedger ledger)
    {
        if (ledger.Files.Count == 0 &&
            ledger.ToolResults.Count == 0 &&
            ledger.PendingModifications.Count == 0 &&
            string.IsNullOrWhiteSpace(ledger.LastToolBatchSummary) &&
            ledger.RepeatedEvidenceKeys.Count == 0 &&
            ledger.Failures.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("[SYSTEM] Runtime evidence ledger (compact working set):");
        if (ledger.Files.Count > 0)
        {
            builder.AppendLine("Files:");
            foreach (var file in ledger.Files.TakeLast(6))
            {
                builder.Append("- ");
                builder.AppendLine(FormatFileEvidenceSummary(file));
            }
        }

        if (ledger.ToolResults.Count > 0)
        {
            builder.AppendLine("Tool results:");
            foreach (var tool in ledger.ToolResults.TakeLast(6))
            {
                builder.Append("- ");
                builder.Append(tool.ToolName);
                if (!string.IsNullOrWhiteSpace(tool.CallId))
                {
                    builder.Append(" callId=");
                    builder.Append(tool.CallId);
                }

                builder.Append(tool.Success ? " ok" : " failed");
                if (tool.ResultLength.HasValue)
                {
                    builder.Append(" length=");
                    builder.Append(tool.ResultLength.Value);
                }

                if (!string.IsNullOrWhiteSpace(tool.Summary))
                {
                    builder.Append(" summary=");
                    builder.Append(TruncateForPrompt(NormalizePromptSummary(tool.Summary), ToolSummaryMaxChars));
                }

                builder.AppendLine();
            }
        }

        if (ledger.PendingModifications.Count > 0)
        {
            builder.AppendLine("Pending modifications:");
            foreach (var pending in ledger.PendingModifications.TakeLast(4))
            {
                builder.Append("- ");
                builder.AppendLine(FormatPendingModificationEvidenceSummary(pending));
            }
        }

        if (!string.IsNullOrWhiteSpace(ledger.LastToolBatchSummary))
        {
            builder.AppendLine("Last tool batch summary:");
            builder.Append("- ");
            builder.AppendLine(TruncateForPrompt(
                NormalizePromptSummary(ledger.LastToolBatchSummary),
                ToolSummaryMaxChars));
        }

        if (ledger.RepeatedEvidenceKeys.Count > 0)
        {
            builder.Append("Repeated read targets: ");
            builder.AppendLine(string.Join(", ", ledger.RepeatedEvidenceKeys.Take(8)));
        }

        if (ledger.Failures.Count > 0)
        {
            builder.AppendLine("Recent failures:");
            foreach (var failure in ledger.Failures.TakeLast(4))
            {
                builder.Append("- ");
                if (!string.IsNullOrWhiteSpace(failure.ToolName))
                {
                    builder.Append(failure.ToolName);
                    builder.Append(": ");
                }

                builder.AppendLine(TruncateForPrompt(NormalizePromptSummary(failure.Message), ToolSummaryMaxChars));
            }
        }

        return TruncateForPrompt(builder.ToString().TrimEnd(), EvidenceLedgerPromptMaxChars);
    }

    private static string? BuildRecoveryHintsPrompt(List<RuntimeRecoveryHint> recoveryHints)
    {
        if (recoveryHints.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("[SYSTEM] Runtime recovery hints (must be honored this round):");
        foreach (var hint in recoveryHints.TakeLast(4))
        {
            builder.Append("- source=");
            builder.Append(hint.Source);
            if (hint.Attempt > 0)
            {
                builder.Append(" attempt=");
                builder.Append(hint.Attempt);
            }

            if (!string.IsNullOrWhiteSpace(hint.RequiredToolName))
            {
                builder.Append(" requiredTool=");
                builder.Append(hint.RequiredToolName);
            }

            builder.Append(" toolCallRequired=");
            builder.Append(hint.ToolCallRequired ? "true" : "false");
            if (hint.CandidateFiles.Count > 0)
            {
                builder.Append(" files=");
                builder.Append(string.Join(", ", hint.CandidateFiles.Take(6)));
            }

            if (!string.IsNullOrWhiteSpace(hint.Message))
            {
                builder.Append(" hint=");
                builder.Append(TruncateForPrompt(NormalizePromptSummary(hint.Message), ToolSummaryMaxChars));
            }

            builder.AppendLine();
        }

        builder.AppendLine("When toolCallRequired=true and a requiredTool is present, emit that tool call next instead of plain text.");
        return TruncateForPrompt(builder.ToString().TrimEnd(), RecoveryHintPromptMaxChars);
    }

    private static string FormatRecoveryHintSummary(RuntimeRecoveryHint hint)
    {
        var builder = new StringBuilder();
        builder.Append(hint.Source);
        if (hint.Attempt > 0)
        {
            builder.Append(" attempt=");
            builder.Append(hint.Attempt);
        }

        if (!string.IsNullOrWhiteSpace(hint.RequiredToolName))
        {
            builder.Append(" requiredTool=");
            builder.Append(hint.RequiredToolName);
        }

        builder.Append(hint.ToolCallRequired ? " toolCallRequired" : " synthesisAllowed");
        if (hint.CandidateFiles.Count > 0)
        {
            builder.Append(" files=");
            builder.Append(string.Join(", ", hint.CandidateFiles.Take(4)));
        }

        if (!string.IsNullOrWhiteSpace(hint.Message))
        {
            builder.Append(" hint=");
            builder.Append(TruncateForPrompt(NormalizePromptSummary(hint.Message), ToolSummaryMaxChars));
        }

        return builder.ToString();
    }

    private static string FormatPendingModificationEvidenceSummary(PendingModificationEvidence evidence)
    {
        var builder = new StringBuilder();
        builder.Append(evidence.Source);
        if (!string.IsNullOrWhiteSpace(evidence.RequiredToolName))
        {
            builder.Append(" requiredTool=");
            builder.Append(evidence.RequiredToolName);
        }

        if (evidence.CandidateFiles.Count > 0)
        {
            builder.Append(" files=");
            builder.Append(string.Join(", ", evidence.CandidateFiles.Take(6)));
        }

        if (!string.IsNullOrWhiteSpace(evidence.AssistantPlanSummary))
        {
            builder.Append(" plan=");
            builder.Append(TruncateForPrompt(
                NormalizePromptSummary(evidence.AssistantPlanSummary),
                ToolSummaryMaxChars));
        }

        return builder.ToString();
    }

    private static string? BuildWorkerCapsulePrompt(
        QueryRuntimeRequest request,
        QueryRuntimeState state)
    {
        var worker = request.WorkerContext;
        if (worker == null)
        {
            return null;
        }

        var contract = request.RequiredToolContract ?? worker.RequiredToolContract;
        var builder = new StringBuilder();
        builder.AppendLine("[SYSTEM] Worker capsule (stable runtime context):");
        builder.AppendLine($"- worker: {worker.DisplayName} ({worker.WorkerType})");
        builder.AppendLine($"- isolation: {worker.IsolationMode}");
        builder.AppendLine($"- output contract: {worker.OutputContract}");
        builder.AppendLine($"- language service: {worker.LanguageServiceAccess}");
        builder.AppendLine($"- allowed categories: {string.Join(", ", worker.AllowedToolCategories)}");
        if (worker.AllowedToolNames.Count > 0)
        {
            var allowedTools = string.Join(", ", worker.AllowedToolNames.Take(12));
            builder.AppendLine(worker.AllowedToolNames.Count > 12
                ? $"- allowed tools: {allowedTools}, ... ({worker.AllowedToolNames.Count} total)"
                : $"- allowed tools: {allowedTools}");
        }

        if (worker.AutoActivateToolNames.Count > 0)
        {
            builder.AppendLine($"- auto-activated tools: {string.Join(", ", worker.AutoActivateToolNames.Take(12))}");
        }

        if (contract != null)
        {
            var satisfied = contract.IsSatisfiedBy(state.ExecutedToolNames, state.SuccessfulToolNames)
                ? "satisfied"
                : "unsatisfied";
            builder.AppendLine($"- required tool contract: {contract.Name} ({satisfied})");
            builder.AppendLine($"- acceptable contract tools: {contract.FormatToolList()}");
            if (!string.IsNullOrWhiteSpace(contract.PreferredRecoveryToolName))
            {
                builder.AppendLine($"- preferred recovery tool: {contract.PreferredRecoveryToolName}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string? BuildRuntimeCheckpointPrompt(QueryRuntimeRequest request)
    {
        if (!TryGetRuntimeCheckpointMetadata(request, out var checkpoint))
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("[SYSTEM] Runtime checkpoint restored from previous turn:");
        AppendJObjectString(builder, checkpoint, "ActiveTaskSummary", "- active task: ");
        AppendJObjectString(builder, checkpoint, "WorkerDisplayName", "- previous worker: ");
        AppendJObjectString(builder, checkpoint, "RequiredToolContractName", "- required contract: ");
        if (checkpoint.TryGetValue("RequiredToolContractSatisfied", StringComparison.OrdinalIgnoreCase, out var satisfied))
        {
            builder.Append("- required contract satisfied: ");
            builder.AppendLine(satisfied.Type == JTokenType.Boolean ? satisfied.Value<bool>().ToString() : satisfied.ToString());
        }

        if (checkpoint.TryGetValue("RequiredToolNameForNextRound", StringComparison.OrdinalIgnoreCase, out var requiredTool) &&
            requiredTool.Type != JTokenType.Null &&
            !string.IsNullOrWhiteSpace(requiredTool.ToString()))
        {
            builder.Append("- pending required tool: ");
            builder.AppendLine(requiredTool.ToString());
        }

        if (checkpoint.TryGetValue("EvidenceLedger", StringComparison.OrdinalIgnoreCase, out var ledger) &&
            ledger is JObject ledgerObject)
        {
            AppendCheckpointFiles(builder, ledgerObject);
            AppendCheckpointToolResults(builder, ledgerObject);
            AppendCheckpointPendingModifications(builder, ledgerObject);
        }

        return TruncateForPrompt(builder.ToString().TrimEnd(), EvidenceLedgerPromptMaxChars);
    }

    private static void AppendCheckpointFiles(StringBuilder builder, JObject ledgerObject)
    {
        if (ledgerObject["Files"] is not JArray files || files.Count == 0)
        {
            return;
        }

        builder.AppendLine("Files from previous working set:");
        foreach (var file in files.OfType<JObject>().TakeLast(6))
        {
            builder.Append("- ");
            builder.Append(GetJObjectString(file, "FilePath") ?? "(unknown)");
            var snapshotId = GetJObjectString(file, "SnapshotId");
            var fingerprint = GetJObjectString(file, "FileFingerprint");
            if (!string.IsNullOrWhiteSpace(snapshotId))
            {
                builder.Append(" snapshot=");
                builder.Append(snapshotId);
            }

            if (!string.IsNullOrWhiteSpace(fingerprint))
            {
                builder.Append(" fp=");
                builder.Append(fingerprint);
            }

            builder.AppendLine();
        }
    }

    private static void AppendCheckpointToolResults(StringBuilder builder, JObject ledgerObject)
    {
        if (ledgerObject["ToolResults"] is not JArray tools || tools.Count == 0)
        {
            return;
        }

        builder.AppendLine("Tool results from previous working set:");
        foreach (var tool in tools.OfType<JObject>().TakeLast(6))
        {
            builder.Append("- ");
            builder.Append(GetJObjectString(tool, "ToolName") ?? "(unknown)");
            builder.Append(" success=");
            builder.Append(GetJObjectString(tool, "Success") ?? "(unknown)");
            var summary = GetJObjectString(tool, "Summary");
            if (!string.IsNullOrWhiteSpace(summary))
            {
                builder.Append(" summary=");
                builder.Append(TruncateForPrompt(NormalizePromptSummary(summary), ToolSummaryMaxChars));
            }

            builder.AppendLine();
        }
    }

    private static void AppendCheckpointPendingModifications(StringBuilder builder, JObject ledgerObject)
    {
        if (ledgerObject["PendingModifications"] is not JArray pending || pending.Count == 0)
        {
            return;
        }

        builder.AppendLine("Pending modifications from previous working set:");
        foreach (var modification in pending.OfType<JObject>().TakeLast(4))
        {
            builder.Append("- ");
            builder.Append(GetJObjectString(modification, "Source") ?? "(unknown)");
            var requiredTool = GetJObjectString(modification, "RequiredToolName");
            if (!string.IsNullOrWhiteSpace(requiredTool))
            {
                builder.Append(" requiredTool=");
                builder.Append(requiredTool);
            }

            if (modification["CandidateFiles"] is JArray files && files.Count > 0)
            {
                builder.Append(" files=");
                builder.Append(string.Join(", ", files.Select(static item => item.ToString()).Take(6)));
            }

            var plan = GetJObjectString(modification, "AssistantPlanSummary");
            if (!string.IsNullOrWhiteSpace(plan))
            {
                builder.Append(" plan=");
                builder.Append(TruncateForPrompt(NormalizePromptSummary(plan), ToolSummaryMaxChars));
            }

            builder.AppendLine();
        }
    }

    private static bool TryGetRuntimeCheckpointMetadata(
        QueryRuntimeRequest request,
        out JObject checkpoint)
    {
        checkpoint = null!;
        var metadata = request.Session?.Metadata;
        if (metadata == null ||
            !metadata.TryGetValue(QueryRuntimeCheckpoint.MetadataKey, out var rawCheckpoint) ||
            string.IsNullOrWhiteSpace(rawCheckpoint))
        {
            return false;
        }

        try
        {
            checkpoint = JObject.Parse(rawCheckpoint);
            return true;
        }
        catch
        {
            checkpoint = null!;
            return false;
        }
    }

    private static string BuildRuntimeCheckpointFrameSummary(JObject checkpoint)
    {
        var parts = new List<string>();
        AddSummaryPart(parts, "task", GetJObjectString(checkpoint, "ActiveTaskSummary"));
        AddSummaryPart(parts, "worker", GetJObjectString(checkpoint, "WorkerDisplayName"));
        AddSummaryPart(parts, "contract", GetJObjectString(checkpoint, "RequiredToolContractName"));
        AddSummaryPart(parts, "satisfied", GetJObjectString(checkpoint, "RequiredToolContractSatisfied"));
        if (checkpoint["EvidenceLedger"] is JObject ledger)
        {
            AddSummaryPart(parts, "files", (ledger["Files"] as JArray)?.Count.ToString());
            AddSummaryPart(parts, "tools", (ledger["ToolResults"] as JArray)?.Count.ToString());
            AddSummaryPart(parts, "pending", (ledger["PendingModifications"] as JArray)?.Count.ToString());
        }

        return TruncateForPrompt(string.Join(", ", parts), ToolSummaryMaxChars);
    }

    private static void AddSummaryPart(List<string> parts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{label}={NormalizePromptSummary(value)}");
        }
    }

    private static void AppendJObjectString(
        StringBuilder builder,
        JObject json,
        string key,
        string prefix)
    {
        var value = GetJObjectString(json, key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append(prefix);
            builder.AppendLine(value);
        }
    }

    private static string? GetJObjectString(JObject json, string key)
    {
        if (!json.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) ||
            token.Type == JTokenType.Null)
        {
            return null;
        }

        return token.Type == JTokenType.String
            ? token.Value<string>()
            : token.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static string? BuildToolSurfacePrompt(QueryContextAssemblyRequest request)
    {
        if (!request.RuntimeRequest.EnableTools)
        {
            return null;
        }

        var sentToolNames = request.Options.Tools?
            .Select(static tool => tool.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var availableToolNames = request.CurrentTools
            .Select(static tool => tool.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine("[SYSTEM] Tool surface for this model round:");
        builder.AppendLine($"- tool calls allowed: {request.AllowToolCalls}");
        builder.AppendLine($"- available tools before projection: {availableToolNames.Length}");
        builder.AppendLine($"- tools sent to model: {sentToolNames.Length}");
        if (sentToolNames.Length > 0)
        {
            builder.AppendLine($"- sent tool names: {FormatToolNameList(sentToolNames)}");
        }

        var toolChoice = FormatToolChoice(request.Options);
        if (!string.IsNullOrWhiteSpace(toolChoice))
        {
            builder.AppendLine($"- tool choice: {toolChoice}");
        }

        if (!string.IsNullOrWhiteSpace(request.RequiredToolNameForRound))
        {
            builder.AppendLine($"- required tool this round: {request.RequiredToolNameForRound}");
            builder.AppendLine("- instruction: do not call any other tool in this required-tool round.");
        }
        else if (!request.AllowToolCalls)
        {
            builder.AppendLine("- instruction: synthesize from existing context; do not call tools this round.");
        }

        return builder.ToString().TrimEnd();
    }

    private static void InsertAfterLeadingSystemMessages(
        List<ChatMessage> messages,
        IReadOnlyList<ChatMessage> insertedMessages)
    {
        var insertionIndex = 0;
        while (insertionIndex < messages.Count && messages[insertionIndex].Role == ChatRole.System)
        {
            insertionIndex++;
        }

        messages.InsertRange(insertionIndex, insertedMessages);
    }

    private static void InsertBeforeTrailingRuntimeInstruction(
        List<ChatMessage> messages,
        ChatMessage insertedMessage)
    {
        if (messages.Count > 0 && IsTrailingRuntimeInstruction(messages[^1]))
        {
            messages.Insert(messages.Count - 1, insertedMessage);
            return;
        }

        messages.Add(insertedMessage);
    }

    private static string FormatToolNameList(string[] toolNames)
    {
        var preview = string.Join(", ", toolNames.Take(16));
        return toolNames.Length > 16
            ? $"{preview}, ... ({toolNames.Length} total)"
            : preview;
    }

    private static bool IsTrailingRuntimeInstruction(ChatMessage message)
    {
        if (message.Role != ChatRole.User || string.IsNullOrWhiteSpace(message.Text))
        {
            return false;
        }

        var text = message.Text.TrimStart();
        return text.StartsWith("[SYSTEM]", StringComparison.Ordinal) ||
               text.Contains("下一轮", StringComparison.Ordinal) ||
               text.Contains("下一条消息", StringComparison.Ordinal) ||
               text.Contains("最终收尾阶段", StringComparison.Ordinal);
    }

    private static string NormalizePromptSummary(string text)
        => string.Join(" ", text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string TruncateForPrompt(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars] + "...";
    }

    private sealed record ContextProjectionResult(
        List<ChatMessage> Messages,
        IReadOnlyList<string> BudgetDecisions,
        IReadOnlyList<string> DroppedFrames);
}
