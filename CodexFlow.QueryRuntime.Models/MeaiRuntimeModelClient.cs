using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Models;

/// <summary>
/// Provider adapter from the v2 model protocol to an MEAI chat client. Tool
/// declarations and provider-specific options are supplied by the host-side
/// options factory; the provider-free Protocol assembly remains unaware of MEAI.
/// </summary>
/// <remarks>
/// When optional outbound diagnostics are enabled, the adapter records the
/// Runtime request, the final options after tool mode is applied, and the model
/// call outcome, and bridges a per-call scope to the diagnostic HTTP handler.
/// With diagnostics absent or <c>Off</c>, behavior is unchanged. The options
/// factory should return a new <see cref="ChatOptions"/> per call: the adapter
/// assigns Tools and ToolMode in place, and diagnostics cannot make a shared
/// instance race-free.
/// </remarks>
public sealed class MeaiRuntimeModelClient : IRuntimeModelAttemptClient, IDisposable
{
    private readonly IChatClient _chatClient;
    private readonly Func<RuntimeModelRequest, ChatOptions> _optionsFactory;
    private readonly QreOutboundDiagnostics? _diagnostics;
    private readonly QreOutboundDiagnosticsTarget _target;

    public MeaiRuntimeModelClient(
        IChatClient chatClient,
        Func<RuntimeModelRequest, ChatOptions> optionsFactory)
        : this(chatClient, optionsFactory, diagnostics: null)
    {
    }

    public MeaiRuntimeModelClient(
        IChatClient chatClient,
        Func<RuntimeModelRequest, ChatOptions> optionsFactory,
        QreOutboundDiagnostics? diagnostics,
        QreOutboundDiagnosticsTarget? target = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _optionsFactory = optionsFactory ?? throw new ArgumentNullException(nameof(optionsFactory));
        _diagnostics = diagnostics is { IsEnabled: true } ? diagnostics : null;
        _target = target ?? QreOutboundDiagnosticsTarget.Unknown;
    }

    /// <summary>
    /// Legacy entry point. With diagnostics enabled, records mark the Runtime
    /// model attempt ordinal as unavailable instead of inventing one.
    /// </summary>
    public IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
        RuntimeModelRequest request,
        CancellationToken ct = default)
        => _diagnostics == null
            ? StreamCoreAsync(request, ct)
            : StreamObservedAsync(request, attempt: null, ct);

    public IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
        RuntimeModelRequest request,
        RuntimeModelAttemptContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _diagnostics == null
            ? StreamCoreAsync(request, ct)
            : StreamObservedAsync(request, context, ct);
    }

    public void Dispose() => _chatClient.Dispose();

    private async IAsyncEnumerable<RuntimeModelStreamEvent> StreamCoreAsync(
        RuntimeModelRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var (messages, options) = Prepare(request);
        RuntimeModelCompletedEvent? pendingCompletion = null;

        await foreach (var update in _chatClient
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
                        throw ConflictingFinishReason();
                    }
                    pendingCompletion = completion;
                    continue;
                }
                if (pendingCompletion != null && runtimeEvent is not (RuntimeUsageEvent or RuntimeWarningEvent))
                {
                    throw ContentAfterFinish();
                }
                yield return runtimeEvent;
            }
        }

        if (pendingCompletion == null)
        {
            yield return MissingFinishReasonWarning();
            pendingCompletion = new RuntimeModelCompletedEvent(RuntimeModelStopReason.Unknown);
        }
        yield return pendingCompletion;
    }

    /// <summary>
    /// Same event semantics as <see cref="StreamCoreAsync"/>, with the SDK
    /// enumerator advanced step by step inside the diagnostic call scope.
    /// </summary>
    private async IAsyncEnumerable<RuntimeModelStreamEvent> StreamObservedAsync(
        RuntimeModelRequest request,
        RuntimeModelAttemptContext? attempt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var diagnostics = _diagnostics!;
        var observer = QreModelCallObserver.Begin(diagnostics, _target, request, attempt, ct);
        IReadOnlyList<ChatMessage> messages;
        ChatOptions options;
        try
        {
            (messages, options) = Prepare(request);
        }
        catch (Exception ex)
        {
            observer.Fail(ex);
            throw;
        }
        observer.AdapterPrepared(
            messages,
            options,
            _chatClient.GetService<ChatClientMetadata>()?.DefaultModelId);

        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        try
        {
            enumerator = observer.Start(_chatClient, messages, options, ct);
            RuntimeModelCompletedEvent? pendingCompletion = null;
            while (await observer.MoveNextAsync(enumerator).ConfigureAwait(false))
            {
                IReadOnlyList<RuntimeModelStreamEvent> events;
                try
                {
                    events = MeaiRuntimeProtocolAdapter.ToProtocolEvents(enumerator.Current);
                }
                catch (Exception ex)
                {
                    observer.Fail(ex);
                    throw;
                }
                foreach (var runtimeEvent in events)
                {
                    if (ct.IsCancellationRequested)
                    {
                        observer.Fail(new OperationCanceledException(ct));
                        ct.ThrowIfCancellationRequested();
                    }
                    if (runtimeEvent is RuntimeModelCompletedEvent completion)
                    {
                        if (pendingCompletion != null && pendingCompletion.StopReason != completion.StopReason)
                        {
                            var conflict = ConflictingFinishReason();
                            observer.Fail(conflict);
                            throw conflict;
                        }
                        pendingCompletion = completion;
                        continue;
                    }
                    if (pendingCompletion != null && runtimeEvent is not (RuntimeUsageEvent or RuntimeWarningEvent))
                    {
                        var afterFinish = ContentAfterFinish();
                        observer.Fail(afterFinish);
                        throw afterFinish;
                    }
                    observer.CountEvent();
                    yield return runtimeEvent;
                }
            }

            var finishReasonObserved = pendingCompletion != null;
            if (pendingCompletion == null)
            {
                observer.CountEvent();
                yield return MissingFinishReasonWarning();
                pendingCompletion = new RuntimeModelCompletedEvent(RuntimeModelStopReason.Unknown);
            }
            observer.CountEvent();
            observer.Completed(pendingCompletion.StopReason, finishReasonObserved);
            yield return pendingCompletion;
        }
        finally
        {
            if (enumerator != null)
            {
                await observer.DisposeEnumeratorAsync(enumerator).ConfigureAwait(false);
            }
            observer.EndIfOpen();
        }
    }

    private (IReadOnlyList<ChatMessage> Messages, ChatOptions Options) Prepare(RuntimeModelRequest request)
    {
        var messages = MeaiRuntimeProtocolAdapter.ToMeaiMessages(request.Messages);
        var options = _optionsFactory(request) ??
            throw new RuntimeProtocolAdapterException("The MEAI options factory returned null.");
        options.Tools = request.Tools
            .Select(static descriptor => (AITool)new RuntimeToolDeclarationAIFunction(descriptor))
            .ToList();
        options.ToolMode = request.Tools.Count == 0
            ? ChatToolMode.None
            : string.IsNullOrWhiteSpace(request.Parameters.RequiredToolName)
                ? options.ToolMode
                : ChatToolMode.RequireSpecific(request.Parameters.RequiredToolName);
        return (messages, options);
    }

    private static RuntimeModelClientException ConflictingFinishReason()
        => new(new RuntimeError(
            RuntimeErrorCategory.ProviderProtocol,
            "conflicting_provider_finish_reason",
            "The provider emitted conflicting finish reasons."));

    private static RuntimeModelClientException ContentAfterFinish()
        => new(new RuntimeError(
            RuntimeErrorCategory.ProviderProtocol,
            "provider_content_after_finish",
            "The provider emitted model content after its finish reason."));

    private static RuntimeWarningEvent MissingFinishReasonWarning()
        => new(new RuntimeWarning(
            "missing_provider_finish_reason",
            "The provider stream ended without a finish reason; completion is Unknown."));

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
