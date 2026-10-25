using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.QueryRuntime.Models;

/// <summary>
/// Maps provider SDK exceptions to typed <see cref="RuntimeModelClientException"/>
/// errors so the Runtime can retry transport failures and stop on authentication
/// or request errors. The HTTP status comes from the exception when the SDK keeps
/// it (<see cref="HttpRequestException.StatusCode"/>), otherwise from the
/// response observed by <see cref="QreModelHttpErrorCaptureHandler"/>.
/// </summary>
public static partial class QreModelErrorClassifier
{
    public const string RateLimitedCode = "provider_rate_limited";
    public const string ServerErrorCode = "provider_server_error";
    public const string TimeoutCode = "provider_timeout";
    public const string AuthenticationFailedCode = "provider_authentication_failed";
    public const string RequestRejectedCode = "provider_request_rejected";
    public const string TransportFailedCode = "provider_transport_failed";
    public const string MalformedToolCallCode = "malformed_tool_call";
    public const string StreamFailedCode = "model_stream_failed";

    /// <summary>
    /// Returns the typed failure for <paramref name="exception"/>, or null when it
    /// is the caller's own cancellation, which must propagate unchanged.
    /// </summary>
    /// <param name="observedStatusCode">Last non-success HTTP status seen for the call, if any.</param>
    /// <param name="observedRetryAfter">Retry-After of that response, if any.</param>
    public static RuntimeModelClientException? Classify(
        Exception exception,
        CancellationToken callerToken,
        int? observedStatusCode = null,
        TimeSpan? observedRetryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException && callerToken.IsCancellationRequested)
        {
            return null;
        }
        if (exception is RuntimeModelClientException typed)
        {
            return typed;
        }

        var status = FindStatusCode(exception) ?? observedStatusCode ?? ParseStatusCode(exception.Message);
        if (status is { } code)
        {
            var mapped = FromStatus(code, exception);
            if (mapped != null)
            {
                return new RuntimeModelClientException(
                    mapped with { RetryAfter = observedRetryAfter ?? FindRetryAfter(exception) },
                    exception);
            }
        }
        if (exception is OperationCanceledException)
        {
            // Not the caller's token: an HTTP timeout or provider-side abort.
            return Create(RuntimeErrorCategory.ProviderTransport, TimeoutCode, exception, retryable: true);
        }
        if (exception is NotSupportedException)
        {
            // Deterministic SDK request rejection (for example native Anthropic
            // JSON without a schema) is not a failed network stream.
            return Create(RuntimeErrorCategory.ProviderProtocol, RequestRejectedCode, exception, retryable: false);
        }
        if (IsAuthenticationMessage(exception.Message))
        {
            return Create(RuntimeErrorCategory.ProviderAuthentication, AuthenticationFailedCode, exception, retryable: false);
        }
        if (exception is InvalidOperationException &&
            (exception.Message.Contains("empty function.name", StringComparison.OrdinalIgnoreCase) ||
             exception.Message.Contains("function name is empty", StringComparison.OrdinalIgnoreCase)))
        {
            return Create(RuntimeErrorCategory.ProviderProtocol, MalformedToolCallCode, exception, retryable: false);
        }
        if (IsNetworkFailure(exception))
        {
            return Create(RuntimeErrorCategory.ProviderTransport, TransportFailedCode, exception, retryable: true);
        }
        return Create(RuntimeErrorCategory.ProviderTransport, StreamFailedCode, exception, retryable: false);
    }

    private static RuntimeError? FromStatus(int status, Exception exception)
        => status switch
        {
            429 => new(RuntimeErrorCategory.ProviderRateLimit, RateLimitedCode, exception.Message, Retryable: true),
            408 => new(RuntimeErrorCategory.ProviderTransport, TimeoutCode, exception.Message, Retryable: true),
            500 or 502 or 503 or 504 => new(RuntimeErrorCategory.ProviderTransport, ServerErrorCode, exception.Message, Retryable: true),
            401 or 403 => new(RuntimeErrorCategory.ProviderAuthentication, AuthenticationFailedCode, exception.Message),
            400 or 404 or 422 => new(RuntimeErrorCategory.ProviderProtocol, RequestRejectedCode, exception.Message),
            _ => null
        };

    private static RuntimeModelClientException Create(
        RuntimeErrorCategory category,
        string code,
        Exception exception,
        bool retryable)
        => new(new RuntimeError(category, code, exception.Message, retryable), exception);

    private static int? FindStatusCode(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } status })
            {
                return (int)status;
            }
        }
        return null;
    }

    private static TimeSpan? FindRetryAfter(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            switch (current.Data["Retry-After"])
            {
                case TimeSpan value when value >= TimeSpan.Zero:
                    return value;
                case int seconds when seconds >= 0:
                    return TimeSpan.FromSeconds(seconds);
                case string text when int.TryParse(text, out var seconds) && seconds >= 0:
                    return TimeSpan.FromSeconds(seconds);
            }
        }
        return null;
    }

    private static int? ParseStatusCode(string message)
    {
        // HttpResponseMessage.EnsureSuccessStatusCode: "... does not indicate success: 503 (Service Unavailable)."
        var match = StatusCodeMessage().Match(message);
        return match.Success && int.TryParse(match.Groups[1].ValueSpan, out var status) ? status : null;
    }

    private static bool IsAuthenticationMessage(string message)
        => message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsNetworkFailure(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is HttpRequestException or IOException or SocketException)
            {
                return true;
            }
        }
        return false;
    }

    [GeneratedRegex(@"status code does not indicate success: (\d{3})", RegexOptions.CultureInvariant)]
    private static partial Regex StatusCodeMessage();
}
