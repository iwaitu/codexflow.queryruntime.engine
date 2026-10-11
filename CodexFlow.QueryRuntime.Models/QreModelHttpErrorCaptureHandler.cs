namespace CodexFlow.QueryRuntime.Models;

/// <summary>
/// Records the HTTP status and Retry-After of the provider responses made during
/// one model call. Some SDKs (VllmChatClient among them) surface every HTTP error
/// as an <see cref="InvalidOperationException"/> without the status code; with
/// this handler in the provider <see cref="HttpClient"/> pipeline,
/// <see cref="MeaiRuntimeModelClient"/> can classify those failures precisely.
/// Without an active model call the handler is a pass-through.
/// </summary>
public sealed class QreModelHttpErrorCaptureHandler : DelegatingHandler
{
    public QreModelHttpErrorCaptureHandler()
    {
    }

    public QreModelHttpErrorCaptureHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    /// <summary>Creates an <see cref="HttpClient"/> whose pipeline includes the capture handler.</summary>
    public static HttpClient CreateHttpClient(HttpMessageHandler? innerHandler = null)
        => new(new QreModelHttpErrorCaptureHandler(innerHandler ?? new SocketsHttpHandler()));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Snapshot once: the capture belongs to the model call that started this request.
        var capture = QreModelHttpErrorScope.Current;
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        capture?.Record(response);
        return response;
    }
}

/// <summary>Per-model-call record of the last non-success provider response.</summary>
internal sealed class QreModelHttpErrorCapture
{
    private int _statusCode;
    private long _retryAfterTicks = -1;

    public int? StatusCode
    {
        get
        {
            var value = Volatile.Read(ref _statusCode);
            return value == 0 ? null : value;
        }
    }

    public TimeSpan? RetryAfter
    {
        get
        {
            var ticks = Interlocked.Read(ref _retryAfterTicks);
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    public void Record(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        Volatile.Write(ref _statusCode, (int)response.StatusCode);
        var retryAfter = ReadRetryAfter(response);
        Interlocked.Exchange(ref _retryAfterTicks, retryAfter?.Ticks ?? -1);
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        // Some OpenAI-compatible gateways send a millisecond variant.
        return response.Headers.TryGetValues("retry-after-ms", out var values) &&
               long.TryParse(values.FirstOrDefault(), out var milliseconds) && milliseconds >= 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;
    }
}

/// <summary>AsyncLocal bridge from the adapter's SDK enumeration step to the capture handler.</summary>
internal static class QreModelHttpErrorScope
{
    private static readonly AsyncLocal<QreModelHttpErrorCapture?> CurrentCapture = new();

    public static QreModelHttpErrorCapture? Current => CurrentCapture.Value;

    public static Scope Enter(QreModelHttpErrorCapture? capture)
    {
        var previous = CurrentCapture.Value;
        CurrentCapture.Value = capture;
        return new Scope(previous);
    }

    public readonly struct Scope(QreModelHttpErrorCapture? previous) : IDisposable
    {
        public void Dispose() => CurrentCapture.Value = previous;
    }
}
