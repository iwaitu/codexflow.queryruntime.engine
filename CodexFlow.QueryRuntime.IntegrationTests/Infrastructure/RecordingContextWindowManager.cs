using CodexFlow.Core.Runtime;

namespace CodexFlow.QueryRuntime.IntegrationTests.Infrastructure;

internal sealed class RecordingContextWindowManager : IContextWindowManager
{
    public List<QueryRuntimeRequest> StartedTurns { get; } = [];
    public List<(QueryRuntimeRequest Request, QueryRuntimeResult Result)> Completions { get; } = [];

    public Task OnTurnStartedAsync(
        QueryRuntimeRequest request,
        CancellationToken ct = default)
    {
        StartedTurns.Add(request);
        return Task.CompletedTask;
    }

    public Task OnTurnCompletedAsync(
        QueryRuntimeRequest request,
        QueryRuntimeResult result,
        CancellationToken ct = default)
    {
        Completions.Add((request, result));
        return Task.CompletedTask;
    }
}
