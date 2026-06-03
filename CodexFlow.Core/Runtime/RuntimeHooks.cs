using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Runtime hook executed after the model response has been fully collected for a round.
/// </summary>
public interface IRuntimeHook
{
    /// <summary>
    /// Allows a hook to inspect or rewrite the round response before the runtime proceeds.
    /// </summary>
    ValueTask<AfterModelResponseHookResult> OnAfterModelResponseAsync(
        AfterModelResponseContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Allows a hook to inspect the final no-tool response and request one more runtime round.
    /// </summary>
    ValueTask<BeforeStopHookResult> OnBeforeStopAsync(
        BeforeStopContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Dispatches registered runtime hooks in a fixed order.
/// </summary>
public interface IRuntimeHookDispatcher
{
    /// <summary>
    /// Executes all registered hooks with fail-log-and-continue semantics.
    /// </summary>
    ValueTask<AfterModelResponseContext> DispatchAfterModelResponseAsync(
        AfterModelResponseContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Executes stop hooks before the runtime accepts a no-tool response as terminal.
    /// </summary>
    ValueTask<BeforeStopHookResult> DispatchBeforeStopAsync(
        BeforeStopContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Strongly typed model-response payload passed to runtime hooks.
/// </summary>
public sealed record AfterModelResponseContext
{
    /// <summary>The query request that produced this response.</summary>
    public required QueryRuntimeRequest Request { get; init; }

    /// <summary>The zero-based round index.</summary>
    public required int Round { get; init; }

    /// <summary>The accumulated assistant text for this round.</summary>
    public required string ResponseText { get; init; }

    /// <summary>The accumulated thinking content for this round.</summary>
    public required string ThinkingText { get; init; }

    /// <summary>The tool calls produced in this round, if any.</summary>
    public required IReadOnlyList<FunctionCallContent> ToolCalls { get; init; }
}

/// <summary>
/// Optional runtime-hook mutation result.
/// </summary>
public sealed record AfterModelResponseHookResult
{
    /// <summary>Replacement assistant text. Null means keep the current text.</summary>
    public string? ResponseText { get; init; }

    /// <summary>Replacement thinking text. Null means keep the current thinking text.</summary>
    public string? ThinkingText { get; init; }

    /// <summary>Represents a no-op hook result.</summary>
    public static AfterModelResponseHookResult None { get; } = new();
}

/// <summary>
/// Strongly typed payload passed to runtime stop hooks.
/// </summary>
public sealed record BeforeStopContext
{
    /// <summary>The query request that is about to stop.</summary>
    public required QueryRuntimeRequest Request { get; init; }

    /// <summary>The zero-based round index.</summary>
    public required int Round { get; init; }

    /// <summary>The assistant text that would otherwise become the final response.</summary>
    public required string LastAssistantMessage { get; init; }

    /// <summary>The accumulated thinking content for the final candidate round.</summary>
    public required string ThinkingText { get; init; }

    /// <summary>True when this stop-hook pass is itself a continuation caused by a prior stop hook.</summary>
    public required bool StopHookActive { get; init; }

    /// <summary>The current number of stop-hook continuation attempts.</summary>
    public required int ContinuationCount { get; init; }

    /// <summary>Tool names that were executed at least once during this query.</summary>
    public IReadOnlySet<string> ExecutedToolNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tool names that completed successfully at least once during this query.</summary>
    public IReadOnlySet<string> SuccessfulToolNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Total tool calls executed during this query.</summary>
    public int TotalToolCalls { get; init; }
}

/// <summary>
/// Stop-hook decision. A continuation request injects feedback and lets the runtime run another round.
/// </summary>
public sealed record BeforeStopHookResult
{
    /// <summary>Whether the runtime should continue instead of accepting the current response as terminal.</summary>
    public bool Continue { get; init; }

    /// <summary>Feedback injected as a user/system message before the next round.</summary>
    public string? Message { get; init; }

    /// <summary>Optional reason used for events and diagnostics.</summary>
    public string? Reason { get; init; }

    /// <summary>Whether the continuation round should be allowed to call tools even if it is near wrap-up.</summary>
    public bool AllowToolCallsOnNextRound { get; init; } = true;

    /// <summary>Optional tool name to force via ChatToolMode.RequireSpecific on the next round.</summary>
    public string? RequiredToolNameForNextRound { get; init; }

    /// <summary>Optional per-decision maximum continuation attempts.</summary>
    public int? MaxContinuationAttempts { get; init; }

    /// <summary>Optional terminal detail code to use if continuation attempts are exhausted.</summary>
    public string? ExhaustionDetailCode { get; init; }

    /// <summary>Optional terminal message to emit if continuation attempts are exhausted.</summary>
    public string? ExhaustionMessage { get; init; }

    /// <summary>Represents a no-op hook result.</summary>
    public static BeforeStopHookResult None { get; } = new();
}

/// <summary>
/// Default runtime-hook dispatcher with fail-log-and-continue semantics.
/// </summary>
public sealed class RuntimeHookDispatcher : IRuntimeHookDispatcher
{
    private readonly IReadOnlyList<IRuntimeHook> _hooks;
    private readonly ILogger<RuntimeHookDispatcher> _logger;

    /// <summary>
    /// Creates a dispatcher over the registered runtime hooks.
    /// </summary>
    public RuntimeHookDispatcher(IEnumerable<IRuntimeHook> hooks, ILogger<RuntimeHookDispatcher> logger)
    {
        _hooks = (hooks ?? throw new ArgumentNullException(nameof(hooks))).ToArray();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask<AfterModelResponseContext> DispatchAfterModelResponseAsync(
        AfterModelResponseContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var current = context;
        foreach (var hook in _hooks)
        {
            var startedAt = DateTime.UtcNow;
            try
            {
                var result = await hook.OnAfterModelResponseAsync(current, ct).ConfigureAwait(false)
                    ?? AfterModelResponseHookResult.None;
                var nextResponseText = result.ResponseText ?? current.ResponseText;
                var nextThinkingText = result.ThinkingText ?? current.ThinkingText;
                var modified =
                    !string.Equals(nextResponseText, current.ResponseText, StringComparison.Ordinal) ||
                    !string.Equals(nextThinkingText, current.ThinkingText, StringComparison.Ordinal);

                _logger.LogInformation(
                    "Runtime hook executed. Hook={HookType} Round={Round} Modified={Modified} DurationMs={DurationMs}",
                    hook.GetType().Name,
                    current.Round,
                    modified,
                    (DateTime.UtcNow - startedAt).TotalMilliseconds);

                current = current with
                {
                    ResponseText = nextResponseText,
                    ThinkingText = nextThinkingText
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Runtime hook failed. Hook={HookType} Round={Round}. Continuing with original response.",
                    hook.GetType().Name,
                    current.Round);
            }
        }

        return current;
    }

    /// <inheritdoc />
    public async ValueTask<BeforeStopHookResult> DispatchBeforeStopAsync(
        BeforeStopContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var hook in _hooks)
        {
            var startedAt = DateTime.UtcNow;
            try
            {
                var result = await hook.OnBeforeStopAsync(context, ct).ConfigureAwait(false)
                    ?? BeforeStopHookResult.None;
                var shouldContinue = result.Continue;

                _logger.LogInformation(
                    "Runtime stop hook executed. Hook={HookType} Round={Round} Continue={Continue} DurationMs={DurationMs}",
                    hook.GetType().Name,
                    context.Round,
                    shouldContinue,
                    (DateTime.UtcNow - startedAt).TotalMilliseconds);

                if (shouldContinue)
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Runtime stop hook failed. Hook={HookType} Round={Round}. Continuing with stop decision.",
                    hook.GetType().Name,
                    context.Round);
            }
        }

        return BeforeStopHookResult.None;
    }
}
