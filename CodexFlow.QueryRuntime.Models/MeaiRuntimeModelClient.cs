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
/// instance race-free. A non-null <see cref="RuntimeModelParameters.ResponseFormat"/>
/// on the Step request replaces the factory's <see cref="ChatOptions.ResponseFormat"/>.
/// </remarks>
public sealed class MeaiRuntimeModelClient : IRuntimeModelAttemptClient, IDisposable
{
    private readonly IChatClient _chatClient;
    private readonly Func<RuntimeModelRequest, ChatOptions> _optionsFactory;
    private readonly QreOutboundDiagnostics? _diagnostics;
    private readonly QreOutboundDiagnosticsTarget _target;
    private readonly MeaiRuntimeModelClientOptions _options;

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
        : this(chatClient, optionsFactory, MeaiRuntimeModelClientOptions.Default, diagnostics, target)
    {
    }

    public MeaiRuntimeModelClient(
        IChatClient chatClient,
        Func<RuntimeModelRequest, ChatOptions> optionsFactory,
        MeaiRuntimeModelClientOptions options,
        QreOutboundDiagnostics? diagnostics = null,
        QreOutboundDiagnosticsTarget? target = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _optionsFactory = optionsFactory ?? throw new ArgumentNullException(nameof(optionsFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
        => Dispatch(request, attempt: null, ct);

    public IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
        RuntimeModelRequest request,
        RuntimeModelAttemptContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Dispatch(request, context, ct);
    }

    private IAsyncEnumerable<RuntimeModelStreamEvent> Dispatch(
        RuntimeModelRequest request,
        RuntimeModelAttemptContext? attempt,
        CancellationToken ct)
    {
        IAsyncEnumerable<RuntimeModelStreamEvent> Run(bool streaming)
            => _diagnostics == null
                ? StreamCoreAsync(request, streaming, ct)
                : StreamObservedAsync(request, attempt, streaming, ct);
        return _options.StreamingMode switch
        {
            MeaiStreamingMode.NonStreaming => Run(streaming: false),
            MeaiStreamingMode.StreamingWithNonStreamingFallbackOnEmpty => WithEmptyStreamFallbackAsync(Run, ct),
            _ => Run(streaming: true)
        };
    }

    /// <summary>
    /// Buffers content-free events (usage, warnings, completion) until the first
    /// text, reasoning or tool call. A stream that ends without one is discarded
    /// and the request is sent once more without streaming.
    /// </summary>
    private static async IAsyncEnumerable<RuntimeModelStreamEvent> WithEmptyStreamFallbackAsync(
        Func<bool, IAsyncEnumerable<RuntimeModelStreamEvent>> run,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffered = new List<RuntimeModelStreamEvent>();
        var substantive = false;
        await foreach (var runtimeEvent in run(true).WithCancellation(ct).ConfigureAwait(false))
        {
            if (!substantive && IsSubstantive(runtimeEvent))
            {
                substantive = true;
                foreach (var pending in buffered)
                {
                    yield return pending;
                }
                buffered.Clear();
            }
            if (substantive)
            {
                yield return runtimeEvent;
            }
            else
            {
                buffered.Add(runtimeEvent);
            }
        }
        if (substantive)
        {
            yield break;
        }
        var fallbackWarning = new RuntimeWarningEvent(new RuntimeWarning(
            "empty_stream_fallback",
            "The provider stream ended without content; the request was resent without streaming."));
        // Do not expose an event before the resend starts returning results:
        // a content-free provider failure must remain eligible for loop retry.
        var warningEmitted = false;
        await foreach (var runtimeEvent in run(false).WithCancellation(ct).ConfigureAwait(false))
        {
            if (!warningEmitted)
            {
                yield return fallbackWarning;
                warningEmitted = true;
            }
            yield return runtimeEvent;
        }
        if (!warningEmitted)
        {
            yield return fallbackWarning;
        }
    }

    private static bool IsSubstantive(RuntimeModelStreamEvent runtimeEvent)
        => runtimeEvent switch
        {
            RuntimeTextDeltaEvent text => !string.IsNullOrEmpty(text.Text),
            RuntimeReasoningDeltaEvent reasoning => !string.IsNullOrEmpty(reasoning.Text) ||
                                                   !string.IsNullOrEmpty(reasoning.ProtectedData),
            RuntimeToolCallEvent => true,
            _ => false
        };

    private IChatClient ChatClientFor(bool streaming)
        => streaming ? _chatClient : new NonStreamingChatClientView(_chatClient);

    public void Dispose() => _chatClient.Dispose();

    private async IAsyncEnumerable<RuntimeModelStreamEvent> StreamCoreAsync(
        RuntimeModelRequest request,
        bool streaming,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var (messages, options) = Prepare(request);
        RuntimeModelCompletedEvent? pendingCompletion = null;
        var capture = _options.ClassifyProviderErrors ? new QreModelHttpErrorCapture() : null;

        IAsyncEnumerator<ChatResponseUpdate> enumerator;
        try
        {
            using (QreModelHttpErrorScope.Enter(capture))
            {
                enumerator = ChatClientFor(streaming).GetStreamingResponseAsync(messages, options, ct).GetAsyncEnumerator(ct);
            }
        }
        catch (Exception ex) when (Classified(ex, capture, ct) is { } classified)
        {
            throw classified;
        }
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    using (QreModelHttpErrorScope.Enter(capture))
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (Classified(ex, capture, ct) is { } classified)
                {
                    throw classified;
                }
                if (!moved)
                {
                    break;
                }
                foreach (var runtimeEvent in MeaiRuntimeProtocolAdapter.ToProtocolEvents(enumerator.Current))
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
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
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
        bool streaming,
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
            _chatClient.GetService<ChatClientMetadata>()?.DefaultModelId,
            streaming);

        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        var capture = _options.ClassifyProviderErrors ? new QreModelHttpErrorCapture() : null;
        try
        {
            try
            {
                using (QreModelHttpErrorScope.Enter(capture))
                {
                    enumerator = observer.Start(ChatClientFor(streaming), messages, options, ct);
                }
            }
            catch (Exception ex) when (Classified(ex, capture, ct) is { } classified)
            {
                throw classified;
            }
            RuntimeModelCompletedEvent? pendingCompletion = null;
            while (true)
            {
                bool moved;
                try
                {
                    using (QreModelHttpErrorScope.Enter(capture))
                    {
                        moved = await observer.MoveNextAsync(enumerator).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (Classified(ex, capture, ct) is { } classified)
                {
                    throw classified;
                }
                if (!moved)
                {
                    break;
                }
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
        if (request.Parameters.ResponseFormat is { } responseFormat)
        {
            // The Runtime format is the Step's effective decision and overrides the
            // factory; without one the factory value is kept for existing hosts.
            options.ResponseFormat = ToChatResponseFormat(responseFormat);
        }
        return (messages, options);
    }

    internal static ChatResponseFormat ToChatResponseFormat(RuntimeResponseFormat format)
        => format.Kind switch
        {
            RuntimeResponseFormatKind.Text => ChatResponseFormat.Text,
            RuntimeResponseFormatKind.JsonObject => ChatResponseFormat.Json,
            RuntimeResponseFormatKind.JsonSchema when format.Schema is { } schema => ChatResponseFormat.ForJsonSchema(
                schema,
                format.SchemaName,
                format.SchemaDescription),
            _ => throw new RuntimeProtocolAdapterException(
                $"Response format '{format.Kind}' cannot be mapped to an MEAI response format.")
        };

    /// <summary>
    /// The typed replacement for an SDK exception, or null to rethrow it unchanged
    /// (classification disabled, caller cancellation, or already typed).
    /// </summary>
    private static RuntimeModelClientException? Classified(
        Exception exception,
        QreModelHttpErrorCapture? capture,
        CancellationToken ct)
    {
        if (capture == null)
        {
            return null;
        }
        var classified = QreModelErrorClassifier.Classify(exception, ct, capture.StatusCode, capture.RetryAfter);
        return ReferenceEquals(classified, exception) ? null : classified;
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

    /// <summary>
    /// Presents <see cref="IChatClient.GetResponseAsync"/> as a single-shot update
    /// sequence so the non-streaming path reuses the streaming event mapping.
    /// </summary>
    private sealed class NonStreamingChatClientView(IChatClient inner) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => inner.GetResponseAsync(messages, options, cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => inner.GetService(serviceType, serviceKey);

        public void Dispose()
        {
            // The adapter owns and disposes the inner client.
        }
    }

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
