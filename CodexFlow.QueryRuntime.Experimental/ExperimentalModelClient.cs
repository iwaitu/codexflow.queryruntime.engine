using Microsoft.Extensions.AI;
using CodexFlow.QueryRuntime.Engine;

namespace CodexFlow.QueryRuntime.Experimental;

public interface IExperimentalModelClient : IQueryRuntimeModelClient
{
}

public sealed class StaticExperimentalModelClient(string response) : IExperimentalModelClient
{
    public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        QueryRuntimeModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, response);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
