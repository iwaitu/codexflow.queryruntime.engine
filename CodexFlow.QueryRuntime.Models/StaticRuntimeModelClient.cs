using CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.QueryRuntime.Models;

/// <summary>Deterministic provider-free response source for CLI and host smoke tests.</summary>
public sealed class StaticRuntimeModelClient(string response) : IRuntimeModelClient
{
    public async IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
        RuntimeModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        yield return new RuntimeTextDeltaEvent(response);
        yield return new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
