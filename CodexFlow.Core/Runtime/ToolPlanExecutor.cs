using CodexFlow.Core.Agents;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.Runtime;

public interface IToolPlanExecutor
{
    Task<ToolPlanExecutionResult> ExecuteAsync(
        ToolPlanExecutionRequest request,
        CancellationToken ct = default);
}

public sealed class DefaultToolPlanExecutor : IToolPlanExecutor
{
    private readonly IToolExecutionCoordinator? _toolCoordinator;
    private readonly Func<FunctionCallContent, QueryRuntimeRequest, QueryRuntimeState, CancellationToken, Task<ToolExecutionResult>> _fallbackExecutor;

    public DefaultToolPlanExecutor(
        IToolExecutionCoordinator? toolCoordinator,
        Func<FunctionCallContent, QueryRuntimeRequest, QueryRuntimeState, CancellationToken, Task<ToolExecutionResult>> fallbackExecutor)
    {
        _toolCoordinator = toolCoordinator;
        _fallbackExecutor = fallbackExecutor ?? throw new ArgumentNullException(nameof(fallbackExecutor));
    }

    public async Task<ToolPlanExecutionResult> ExecuteAsync(
        ToolPlanExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var orderedResults = new ToolExecutionResult?[request.Calls.Count];
        var deferredCalls = new List<(int Index, FunctionCallContent Call)>();

        for (var i = 0; i < request.Calls.Count; i++)
        {
            var call = request.Calls[i];
            if (request.PrestartedExecutions.TryGetValue(call, out var prestarted))
            {
                await FlushDeferredToolCallsAsync().ConfigureAwait(false);
                prestarted.Consumed = true;
                orderedResults[i] = await prestarted.Task.ConfigureAwait(false);
                continue;
            }

            deferredCalls.Add((i, call));
        }

        await FlushDeferredToolCallsAsync().ConfigureAwait(false);

        return new ToolPlanExecutionResult
        {
            Results = orderedResults
                .Select((result, index) => result ?? BuildMissingToolResult(request.Calls[index]))
                .ToArray()
        };

        async Task FlushDeferredToolCallsAsync()
        {
            if (deferredCalls.Count == 0)
            {
                return;
            }

            var indexes = deferredCalls.Select(static item => item.Index).ToArray();
            var calls = deferredCalls.Select(static item => item.Call).ToArray();
            deferredCalls.Clear();

            if (_toolCoordinator != null)
            {
                var resultIndex = 0;
                await foreach (var result in _toolCoordinator.ExecuteBatchAsync(
                                   calls,
                                   request.AvailableTools,
                                   request.RuntimeRequest,
                                   request.State,
                                   ct).ConfigureAwait(false))
                {
                    if (resultIndex >= indexes.Length)
                    {
                        break;
                    }

                    orderedResults[indexes[resultIndex++]] = result;
                }

                return;
            }

            for (var j = 0; j < calls.Length; j++)
            {
                orderedResults[indexes[j]] = await _fallbackExecutor(
                    calls[j],
                    request.RuntimeRequest,
                    request.State,
                    ct).ConfigureAwait(false);
            }
        }
    }

    private static ToolExecutionResult BuildMissingToolResult(FunctionCallContent call)
    {
        var toolName = call.Name ?? "unknown";
        return new ToolExecutionResult(
            ToolName: toolName,
            CallId: call.CallId ?? string.Empty,
            Result: $"Tool execution did not return a result for '{toolName}'",
            Success: false);
    }
}

public sealed record ToolPlanExecutionRequest
{
    public required IReadOnlyList<FunctionCallContent> Calls { get; init; }

    public required IReadOnlyDictionary<FunctionCallContent, PrestartedToolExecution> PrestartedExecutions { get; init; }

    public required IReadOnlyList<AIFunction>? AvailableTools { get; init; }

    public required QueryRuntimeRequest RuntimeRequest { get; init; }

    public required QueryRuntimeState State { get; init; }
}

public sealed record ToolPlanExecutionResult
{
    public required IReadOnlyList<ToolExecutionResult> Results { get; init; }
}

public sealed class PrestartedToolExecution(Task<ToolExecutionResult> task)
{
    public Task<ToolExecutionResult> Task { get; } = task;

    public bool Consumed { get; set; }
}
