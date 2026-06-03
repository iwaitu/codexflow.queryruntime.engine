using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Composes multiple IQueryRuntimeInterventionHook instances with first-block semantics.
/// OnToolCallRequestedAsync: if any hook returns ShouldBlock, the composite blocks
/// with the first blocking result. OnToolExecutionCompletedAsync: if any hook returns
/// ShouldSkipToolResult, the composite skips with the first skipping result.
/// </summary>
public sealed class CompositeInterventionHook : IQueryRuntimeInterventionHook
{
    private readonly IReadOnlyList<IQueryRuntimeInterventionHook> _hooks;
    private readonly ILogger? _logger;

    public CompositeInterventionHook(
        IReadOnlyList<IQueryRuntimeInterventionHook> hooks,
        ILogger? logger = null)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _logger = logger;
    }

    public async ValueTask<QueryRuntimeIntervention> OnToolCallRequestedAsync(
        string toolName,
        IDictionary<string, object?> arguments,
        object? session,
        CancellationToken ct = default)
    {
        foreach (var hook in _hooks)
        {
            var result = await hook.OnToolCallRequestedAsync(toolName, arguments, session, ct).ConfigureAwait(false);
            if (result.ShouldBlock)
            {
                _logger?.LogDebug(
                    "CompositeInterventionHook: blocked by {HookType}. Reason: {Reason}",
                    hook.GetType().Name, result.Reason);
                return result;
            }
        }

        return QueryRuntimeIntervention.None;
    }

    public async ValueTask<QueryRuntimeIntervention> OnToolExecutionCompletedAsync(
        string toolName,
        string result,
        bool success,
        object? session,
        CancellationToken ct = default)
    {
        foreach (var hook in _hooks)
        {
            var intervention = await hook.OnToolExecutionCompletedAsync(toolName, result, success, session, ct).ConfigureAwait(false);
            if (intervention.ShouldSkipToolResult)
            {
                _logger?.LogDebug(
                    "CompositeInterventionHook: tool result skipped by {HookType}. Reason: {Reason}",
                    hook.GetType().Name, intervention.Reason);
                return intervention;
            }
        }

        return QueryRuntimeIntervention.None;
    }
}