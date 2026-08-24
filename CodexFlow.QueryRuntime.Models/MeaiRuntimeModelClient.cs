using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Models;

/// <summary>
/// Provider adapter from the v2 model protocol to an MEAI chat client. Tool
/// declarations and provider-specific options are supplied by the host-side
/// options factory; the provider-free Protocol assembly remains unaware of MEAI.
/// </summary>
public sealed class MeaiRuntimeModelClient(
    IChatClient chatClient,
    Func<RuntimeModelRequest, ChatOptions> optionsFactory) : IRuntimeModelClient, IDisposable
{
    public async IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
        RuntimeModelRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var messages = MeaiRuntimeProtocolAdapter.ToMeaiMessages(request.Messages);
        var options = optionsFactory(request) ??
            throw new RuntimeProtocolAdapterException("The MEAI options factory returned null.");
        options.Tools = request.Tools
            .Select(static descriptor => (AITool)new RuntimeToolDeclarationAIFunction(descriptor))
            .ToList();
        options.ToolMode = request.Tools.Count == 0
            ? ChatToolMode.None
            : string.IsNullOrWhiteSpace(request.Parameters.RequiredToolName)
                ? options.ToolMode
                : ChatToolMode.RequireSpecific(request.Parameters.RequiredToolName);
        RuntimeModelCompletedEvent? pendingCompletion = null;

        await foreach (var update in chatClient
                           .GetStreamingResponseAsync(messages, options, ct)
                           .ConfigureAwait(false))
        {
            foreach (var runtimeEvent in MeaiRuntimeProtocolAdapter.ToProtocolEvents(update))
            {
                ct.ThrowIfCancellationRequested();
                if (runtimeEvent is RuntimeModelCompletedEvent completion)
                {
                    if (pendingCompletion != null && pendingCompletion.StopReason != completion.StopReason)
                    {
                        throw new RuntimeModelClientException(new RuntimeError(
                            RuntimeErrorCategory.ProviderProtocol,
                            "conflicting_provider_finish_reason",
                            "The provider emitted conflicting finish reasons."));
                    }
                    pendingCompletion = completion;
                    continue;
                }
                if (pendingCompletion != null && runtimeEvent is not (RuntimeUsageEvent or RuntimeWarningEvent))
                {
                    throw new RuntimeModelClientException(new RuntimeError(
                        RuntimeErrorCategory.ProviderProtocol,
                        "provider_content_after_finish",
                        "The provider emitted model content after its finish reason."));
                }
                yield return runtimeEvent;
            }
        }

        if (pendingCompletion == null)
        {
            yield return new RuntimeWarningEvent(new RuntimeWarning(
                "missing_provider_finish_reason",
                "The provider stream ended without a finish reason; completion is Unknown."));
            pendingCompletion = new RuntimeModelCompletedEvent(RuntimeModelStopReason.Unknown);
        }
        yield return pendingCompletion;
    }

    public void Dispose() => chatClient.Dispose();

    private sealed class RuntimeToolDeclarationAIFunction(
        RuntimeToolDescriptor descriptor) : AIFunction
    {
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

        public override MethodInfo? UnderlyingMethod => null;

        public override JsonSerializerOptions JsonSerializerOptions => SerializerOptions;

        public override JsonElement JsonSchema => descriptor.InputSchema;

        public override JsonElement? ReturnJsonSchema => null;

        public override string Name => descriptor.CanonicalName;

        public override string Description => descriptor.Description;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } =
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["qre.tool.version"] = descriptor.Version,
                ["qre.tool.side_effect"] = descriptor.SideEffect.ToString(),
                ["qre.tool.idempotency"] = descriptor.Idempotency.ToString()
            };

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
            => ValueTask.FromException<object?>(new InvalidOperationException(
                "Runtime tool declarations are executed by the QRE tool pipeline, not by the model client."));
    }
}
