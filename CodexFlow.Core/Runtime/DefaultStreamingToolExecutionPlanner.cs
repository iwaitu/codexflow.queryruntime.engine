using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.Runtime;

public sealed class DefaultStreamingToolExecutionPlanner : IStreamingToolExecutionPlanner
{
    private readonly StreamingToolExecutionOptions _options;

    public DefaultStreamingToolExecutionPlanner(IOptions<StreamingToolExecutionOptions>? options = null)
    {
        _options = options?.Value ?? new StreamingToolExecutionOptions();
    }

    public StreamingToolExecutionDecision Decide(StreamingToolExecutionPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RuntimeRequest);
        ArgumentNullException.ThrowIfNull(request.State);
        ArgumentNullException.ThrowIfNull(request.ToolCall);

        var toolName = ResolveToolName(request.ToolCall);
        if (!_options.Enabled)
        {
            return StreamingToolExecutionDecision.Skip(
                StreamingToolExecutionDecisionReasons.Disabled,
                toolName);
        }

        if (request.ToolCoordinator == null)
        {
            return StreamingToolExecutionDecision.Skip(
                StreamingToolExecutionDecisionReasons.ToolExecutionUnavailable,
                toolName);
        }

        if (!request.RuntimeRequest.EnableTools)
        {
            return StreamingToolExecutionDecision.Skip(
                StreamingToolExecutionDecisionReasons.ToolsDisabledForRequest,
                toolName);
        }

        if (!request.AllowToolCallsThisRound)
        {
            return StreamingToolExecutionDecision.Skip(
                StreamingToolExecutionDecisionReasons.ToolCallsDisabledForRound,
                toolName);
        }

        if (request.RuntimeRequest.InterventionHook != null)
        {
            return StreamingToolExecutionDecision.Skip(
                StreamingToolExecutionDecisionReasons.InterventionHookActive,
                toolName);
        }

        var maxConcurrent = Math.Max(0, _options.MaxConcurrentStreamingTools);
        if (maxConcurrent == 0 || request.ActiveStreamingCount >= maxConcurrent)
        {
            return StreamingToolExecutionDecision.Skip(
                StreamingToolExecutionDecisionReasons.ConcurrencyLimitReached,
                toolName,
                $"limit={maxConcurrent}");
        }

        if (Matches(_options.DenyToolNames, toolName))
        {
            return StreamingToolExecutionDecision.Skip(
                StreamingToolExecutionDecisionReasons.DeniedByName,
                toolName);
        }

        if (_options.AllowToolNames.Count > 0 && !Matches(_options.AllowToolNames, toolName))
        {
            return StreamingToolExecutionDecision.Skip(
                StreamingToolExecutionDecisionReasons.NotAllowedByName,
                toolName);
        }

        var metadata = ResolveCodexToolMetadata(request.RuntimeRequest, request.ToolCall);
        if (metadata == null)
        {
            return StreamingToolExecutionDecision.Skip(
                StreamingToolExecutionDecisionReasons.MissingToolMetadata,
                toolName);
        }

        if (!metadata.IsConcurrencySafe)
        {
            return StreamingToolExecutionDecision.Skip(
                StreamingToolExecutionDecisionReasons.NotConcurrencySafe,
                toolName);
        }

        if (_options.ReadOnlyOnly && !metadata.IsReadOnly)
        {
            return StreamingToolExecutionDecision.Skip(
                StreamingToolExecutionDecisionReasons.NotReadOnly,
                toolName);
        }

        if (metadata.IsDestructive)
        {
            return StreamingToolExecutionDecision.Skip(
                StreamingToolExecutionDecisionReasons.DestructiveTool,
                toolName);
        }

        if (metadata.InterruptBehavior != ToolInterruptBehavior.CancelSafe)
        {
            return StreamingToolExecutionDecision.Skip(
                StreamingToolExecutionDecisionReasons.NotCancelSafe,
                toolName);
        }

        var signature = request.ToolCoordinator.ComputeSignature(request.ToolCall);
        if (request.State.EnableToolDeduplication && request.ActiveStreamingSignatures.Contains(signature))
        {
            return StreamingToolExecutionDecision.Skip(
                StreamingToolExecutionDecisionReasons.DuplicateInStreamingRound,
                toolName);
        }

        return StreamingToolExecutionDecision.Start(toolName, signature);
    }

    private static string ResolveToolName(FunctionCallContent call)
    {
        if (ToolCallSyntaxRecovery.TryNormalizeInlineInvocation(call.Name, call.Arguments, out var recoveredToolName, out _))
        {
            return recoveredToolName;
        }

        return call.Name ?? "unknown";
    }

    private static ToolExecutionMetadata? ResolveCodexToolMetadata(
        QueryRuntimeRequest request,
        FunctionCallContent call)
    {
        var toolName = ResolveToolName(call);
        return ResolveAvailableCodexTools(request)?
            .FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase))?
            .Metadata;
    }

    private static IReadOnlyList<ICodexTool>? ResolveAvailableCodexTools(QueryRuntimeRequest request)
    {
        if (request.AvailableCodexToolsProvider != null)
        {
            return request.AvailableCodexToolsProvider();
        }

        return request.AvailableCodexTools;
    }

    private static bool Matches(IEnumerable<string> names, string toolName)
        => names.Any(name => string.Equals(name, toolName, StringComparison.OrdinalIgnoreCase));
}
