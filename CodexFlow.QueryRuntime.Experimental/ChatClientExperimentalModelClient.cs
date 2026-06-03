using System.Runtime.CompilerServices;
using CodexFlow.QueryRuntime.Engine;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Experimental;

public sealed class ChatClientExperimentalModelClient(IChatClient chatClient) : IExperimentalModelClient, IDisposable
{
    public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        QueryRuntimeModelRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await foreach (var update in chatClient
                           .GetStreamingResponseAsync(request.Messages, request.Options, ct)
                           .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public void Dispose() => chatClient.Dispose();
}
