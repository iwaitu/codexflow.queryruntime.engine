namespace CodexFlow.QueryRuntime.Models;

/// <summary>Opt-in behaviors of <see cref="MeaiRuntimeModelClient"/>; defaults keep the legacy behavior.</summary>
public sealed record MeaiRuntimeModelClientOptions
{
    public static MeaiRuntimeModelClientOptions Default { get; } = new();

    /// <summary>
    /// Converts SDK exceptions into typed <c>RuntimeModelClientException</c>
    /// errors via <see cref="QreModelErrorClassifier"/> (rate limits and transport
    /// failures become retryable, authentication failures terminal). When false,
    /// SDK exceptions propagate unchanged and the Runtime reports them as
    /// non-retryable <c>model_stream_failed</c>. Add
    /// <see cref="QreModelHttpErrorCaptureHandler"/> to the provider HttpClient
    /// for SDKs that drop the HTTP status code.
    /// </summary>
    public bool ClassifyProviderErrors { get; init; }

    /// <summary>How the adapter calls the chat client; defaults to <see cref="MeaiStreamingMode.Streaming"/>.</summary>
    public MeaiStreamingMode StreamingMode { get; init; } = MeaiStreamingMode.Streaming;
}

public enum MeaiStreamingMode
{
    /// <summary><see cref="Microsoft.Extensions.AI.IChatClient.GetStreamingResponseAsync"/> only (legacy).</summary>
    Streaming = 0,

    /// <summary>
    /// <see cref="Microsoft.Extensions.AI.IChatClient.GetResponseAsync"/>, replayed as
    /// updates so finish reason and usage map exactly as in streaming mode.
    /// </summary>
    NonStreaming = 1,

    /// <summary>
    /// Stream, but when the stream ends without any text, reasoning or tool call,
    /// discard it and resend the same request once without streaming, emitting an
    /// <c>empty_stream_fallback</c> warning. Once content was emitted there is no fallback.
    /// </summary>
    StreamingWithNonStreamingFallbackOnEmpty = 2
}
