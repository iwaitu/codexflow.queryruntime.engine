using System.Net;
using System.Net.Http.Headers;

namespace CodexFlow.QueryRuntime.Models.Diagnostics;

/// <summary>
/// Client-side observation point for model SDK HTTP sends. It records only what
/// passes through this handler (<c>attemptCoverage=handler_visible</c>): redirects,
/// authentication retries or network retransmits below it are not observed. It
/// never adds headers, never buffers bodies ahead of the transport, never reads
/// the response ahead of the caller, and never changes the business request.
/// </summary>
internal sealed class QreOutboundDiagnosticHandler(QreOutboundDiagnostics diagnostics) : DelegatingHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => SendAsync(request, cancellationToken).GetAwaiter().GetResult();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!diagnostics.IsEnabled)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Snapshot the AsyncLocal bridge exactly once. Everything after this point
        // (content wrappers, stream wrappers, end callbacks) uses the snapshot.
        var context = QreModelCallScope.Current;
        if (context != null && !ReferenceEquals(context.Diagnostics, diagnostics))
        {
            context = null;
        }
        if (context == null)
        {
            diagnostics.CountUncorrelated();
        }

        var attempt = new QreHttpAttempt(diagnostics, context, request);
        attempt.EmitStarted(request);

        var originalContent = request.Content;
        if (diagnostics.Options.Mode == QreOutboundDiagnosticMode.Structure)
        {
            request.Content = attempt.PrepareRequestObservation(originalContent);
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            attempt.EndBeforeHeaders(ex);
            throw;
        }
        finally
        {
            if (!ReferenceEquals(request.Content, originalContent))
            {
                // Restore the caller's content object; the in-flight write keeps its
                // own reference to the observer, so the transport is unaffected.
                request.Content = originalContent;
            }
        }

        attempt.EmitHeaders(response);
        if (response.Content == null)
        {
            attempt.End(QreDiagnosticStreamTerminations.NoResponse, null);
        }
        else
        {
            response.Content = new QreObservingResponseContent(response.Content, attempt);
        }
        return response;
    }
}

/// <summary>State of one handler-visible HTTP attempt; its end is recorded once.</summary>
internal sealed class QreHttpAttempt
{
    private readonly QreOutboundDiagnostics _diagnostics;
    private readonly QreModelCallContext? _context;
    private readonly string _attemptId;
    private readonly int? _ordinal;
    private readonly string _apiMode;
    private readonly long _startTimestamp;
    private int _ended;
    private int _requestObserved;
    private long _bytesRead;
    private int? _statusCode;

    public QreHttpAttempt(QreOutboundDiagnostics diagnostics, QreModelCallContext? context, HttpRequestMessage request)
    {
        _diagnostics = diagnostics;
        _context = context;
        _attemptId = diagnostics.NextHttpAttemptId();
        _ordinal = context?.NextHttpAttemptOrdinal();
        _apiMode = context?.Target.ApiMode ?? RouteApiMode(request.RequestUri);
        _startTimestamp = diagnostics.GetTimestamp();
    }

    public void AddBytesRead(int count) => Interlocked.Add(ref _bytesRead, count);

    public void EmitStarted(HttpRequestMessage request)
    {
        var content = request.Content;
        var (route, endpoint) = Route(request.RequestUri);
        _diagnostics.Emit(Record(QreDiagnosticEventTypes.HttpAttemptStarted, QreDiagnosticObservationPoints.HttpHandler) with
        {
            Stage = QreDiagnosticStages.HttpPrepared,
            HttpRequest = new QreHttpRequestMetadata
            {
                Method = SafeMethod(request.Method),
                RouteTemplate = route,
                EndpointAlias = endpoint,
                HttpVersion = request.Version.ToString(),
                ContentKind = ContentKind(content),
                // Reading ContentLength asks the content to compute its length
                // without serializing it; deferred JSON content reports unknown.
                ContentLengthKnown = content?.Headers.ContentLength != null,
                ContentEncoded = content == null ? null : content.Headers.ContentEncoding.Count > 0
            }
        });
    }

    public HttpContent? PrepareRequestObservation(HttpContent? content)
    {
        if (content == null)
        {
            EmitStructure(null, QreDiagnosticCaptureStatus.Omitted, "no_request_content", null);
            return null;
        }
        if (content is QreObservingRequestContent existing)
        {
            // A caller above us re-sent the same request: observe the original again.
            content = existing.Inner;
        }
        if (content.Headers.ContentEncoding.Count > 0)
        {
            EmitStructure(null, QreDiagnosticCaptureStatus.Unsupported, "content_encoded", null);
            return content;
        }
        var kind = ContentKind(content);
        if (kind != "json")
        {
            EmitStructure(null, QreDiagnosticCaptureStatus.Unsupported, $"content_kind_{kind}", null);
            return content;
        }
        return new QreObservingRequestContent(content, this, _diagnostics.Options.MaxRequestCaptureBytes);
    }

    /// <summary>Called by the request observer after the transport write finished or failed.</summary>
    public void ObserveRequest(QreBoundedTeeStream tee, Exception? writeFailure)
    {
        if (Interlocked.Exchange(ref _requestObserved, 1) != 0)
        {
            return;
        }
        try
        {
            if (writeFailure != null)
            {
                EmitStructure(null, QreDiagnosticCaptureStatus.Partial, "request_write_failed", tee.TotalBytes);
                return;
            }
            if (tee.Overflowed)
            {
                EmitStructure(null, QreDiagnosticCaptureStatus.Omitted, "capture_limit_exceeded", tee.TotalBytes);
                return;
            }
            var (semantic, status, reason) = QreSemanticProjector.FromHttpJson(
                tee.Captured,
                _apiMode,
                _diagnostics.Options.MaxJsonDepth,
                _diagnostics);
            if (semantic == null)
            {
                _diagnostics.CountProjectionFailure();
            }
            EmitStructure(semantic, status, reason, tee.TotalBytes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Projection errors drop the sensitive input and are only counted.
            _diagnostics.CountProjectionFailure();
            EmitStructure(null, QreDiagnosticCaptureStatus.Failed, "projection_failed", tee.TotalBytes);
        }
        finally
        {
            tee.ReleaseCapture();
        }
    }

    public void EmitHeaders(HttpResponseMessage response)
    {
        _statusCode = (int)response.StatusCode;
        _context?.RecordStatus(_statusCode.Value);
        var headers = response.Headers;
        _diagnostics.Emit(Record(QreDiagnosticEventTypes.HttpHeadersReceived, QreDiagnosticObservationPoints.HttpHandler) with
        {
            Stage = QreDiagnosticStages.HttpResponse,
            HttpResponse = new QreHttpResponseMetadata
            {
                StatusCode = (int)response.StatusCode,
                StatusClass = StatusClass((int)response.StatusCode),
                ContentTypeCategory = ContentTypeCategory(response.Content?.Headers.ContentType),
                ProviderRequestIdAlias = RequestId(headers) is { } requestId
                    ? _diagnostics.Alias("provider-request", requestId)
                    : null,
                RetryAfterSeconds = RetryAfter(headers.RetryAfter),
                HeadersElapsedMs = _diagnostics.ElapsedSince(_startTimestamp),
                HttpVersion = response.Version.ToString()
            }
        });
    }

    public void EndBeforeHeaders(Exception exception)
    {
        var (classification, kind) = ClassifyException(exception, _context);
        EndCore(
            QreDiagnosticFailurePhases.BeforeHeaders,
            classification,
            QreDiagnosticClassificationSources.Handler,
            QreDiagnosticStreamTerminations.NoResponse,
            kind);
    }

    /// <summary>Ends the attempt after response headers; <paramref name="failure"/> is a read failure.</summary>
    public void End(string termination, Exception? failure)
    {
        string phase = QreDiagnosticFailurePhases.None;
        string classification = QreDiagnosticClassifications.None;
        string? kind = null;
        if (failure != null)
        {
            phase = QreDiagnosticFailurePhases.ResponseRead;
            (classification, kind) = ClassifyException(failure, _context);
            if (classification == QreDiagnosticClassifications.Unknown && failure is not OperationCanceledException)
            {
                classification = QreDiagnosticClassifications.ReadError;
            }
        }
        else if (_statusCode is >= 400)
        {
            phase = QreDiagnosticFailurePhases.None;
            classification = QreDiagnosticClassifications.HttpStatusFailure;
        }
        EndCore(phase, classification, QreDiagnosticClassificationSources.StreamWrapper, termination, kind);
    }

    private void EndCore(string phase, string classification, string source, string termination, string? kind)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }
        if (classification != QreDiagnosticClassifications.None)
        {
            _context?.RecordHandlerClassification(classification);
        }
        _diagnostics.Emit(Record(QreDiagnosticEventTypes.HttpAttemptEnded, QreDiagnosticObservationPoints.StreamWrapper) with
        {
            Stage = QreDiagnosticStages.HttpResponse,
            HttpOutcome = new QreHttpAttemptOutcome
            {
                FailurePhase = phase,
                Classification = classification,
                ClassificationSource = source,
                StreamTermination = termination,
                ResponseBytesRead = Interlocked.Read(ref _bytesRead),
                TransportErrorKind = kind,
                ElapsedMs = _diagnostics.ElapsedSince(_startTimestamp)
            }
        });
    }

    private void EmitStructure(QreSemanticRequest? semantic, string status, string? reason, long? observedBytes)
        => _diagnostics.Emit(Record(
            QreDiagnosticEventTypes.RequestStructureObserved,
            QreDiagnosticObservationPoints.RequestContent,
            status) with
        {
            Stage = QreDiagnosticStages.HttpPrepared,
            ReasonCode = reason,
            Request = semantic,
            ApiMode = _apiMode,
            HttpRequest = observedBytes == null
                ? null
                : new QreHttpRequestMetadata
                {
                    Method = "POST",
                    RouteTemplate = "observed",
                    EndpointAlias = "observed",
                    HttpVersion = "observed",
                    ContentKind = "json",
                    ContentLengthKnown = false,
                    ObservedRequestBytes = observedBytes
                }
        });

    private QreOutboundDiagnosticRecord Record(
        string eventType,
        string observationPoint,
        string status = QreDiagnosticCaptureStatus.Complete)
        => _diagnostics.NewRecord(eventType, observationPoint, _context, status) with
        {
            HttpAttemptId = _attemptId,
            AttemptOrdinal = _ordinal,
            AttemptCoverage = "handler_visible",
            ApiMode = _apiMode
        };

    /// <summary>
    /// Classifies by exception type and the upstream token only; never by message
    /// text. Without an upstream token snapshot a bare cancellation stays unknown.
    /// </summary>
    internal static (string Classification, string? TransportErrorKind) ClassifyException(
        Exception exception,
        QreModelCallContext? context)
    {
        var upstreamCancelled = context?.UpstreamToken.IsCancellationRequested == true;
        var timeoutEvidence = exception is OperationCanceledException && exception.InnerException is TimeoutException;
        if (upstreamCancelled && timeoutEvidence)
        {
            return (QreDiagnosticClassifications.CancellationTimeoutRace, null);
        }
        if (upstreamCancelled && exception is OperationCanceledException)
        {
            return (QreDiagnosticClassifications.CallerCancelled, null);
        }
        if (timeoutEvidence)
        {
            return (QreDiagnosticClassifications.HttpClientTimeout, null);
        }
        if (exception is HttpRequestException requestException)
        {
            return (QreDiagnosticClassifications.TransportError, requestException.HttpRequestError.ToString());
        }
        return (QreDiagnosticClassifications.Unknown, null);
    }

    private (string Route, string Endpoint) Route(Uri? uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return ("unknown", "endpoint-unknown");
        }
        var path = uri.AbsolutePath.TrimEnd('/');
        foreach (var suffix in KnownRoutes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var basePath = path[..^suffix.Length];
                // Scheme, host, port and base path are sensitive deployment
                // identifiers: only a local alias leaves memory.
                var endpoint = _diagnostics.Alias("endpoint", $"{uri.Scheme}://{uri.Host}:{uri.Port}{basePath}");
                return ("{endpoint}" + suffix, endpoint);
            }
        }
        return ("unknown", _diagnostics.Alias("endpoint", $"{uri.Scheme}://{uri.Host}:{uri.Port}{path}"));
    }

    private static readonly string[] KnownRoutes = ["/chat/completions", "/responses", "/messages"];

    private static string RouteApiMode(Uri? uri)
    {
        var path = uri?.IsAbsoluteUri == true ? uri.AbsolutePath.TrimEnd('/') : string.Empty;
        return path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? QreApiModeNames.ChatCompletions
            : path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) ? QreApiModeNames.Responses
            : path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) ? QreApiModeNames.AnthropicMessages
            : "unknown";
    }

    private static string SafeMethod(HttpMethod method)
        => method.Method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS"
            ? method.Method
            : "OTHER";

    internal static string ContentKind(HttpContent? content)
    {
        if (content == null)
        {
            return "absent";
        }
        var media = content.Headers.ContentType?.MediaType;
        if (media == null)
        {
            return content is MultipartContent ? "multipart" : "unknown";
        }
        if (media.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            media.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            return "json";
        }
        if (media.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return "multipart";
        }
        if (media.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return "text";
        }
        return "binary";
    }

    private static string ContentTypeCategory(MediaTypeHeaderValue? contentType)
    {
        var media = contentType?.MediaType;
        if (media == null)
        {
            return "absent";
        }
        if (media.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return "event_stream";
        }
        if (media.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            media.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            return "json";
        }
        return media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? "text" : "other";
    }

    private static string StatusClass(int status) => status switch
    {
        < 200 => "informational",
        < 300 => "success",
        < 400 => "redirect",
        < 500 => "client_error",
        _ => "server_error"
    };

    private static string? RequestId(HttpResponseHeaders headers)
    {
        foreach (var name in RequestIdHeaders)
        {
            if (headers.TryGetValues(name, out var values) && values.FirstOrDefault() is { Length: > 0 and <= 256 } value)
            {
                return value;
            }
        }
        return null;
    }

    private static readonly string[] RequestIdHeaders = ["x-request-id", "request-id", "x-amzn-requestid"];

    private static int? RetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return (int)Math.Clamp(delta.TotalSeconds, 0, int.MaxValue);
        }
        if (retryAfter?.Date is { } date)
        {
            return (int)Math.Clamp((date - DateTimeOffset.UtcNow).TotalSeconds, 0, int.MaxValue);
        }
        return null;
    }
}

/// <summary>
/// Wraps a request content and observes the bytes the transport actually writes,
/// copying at most a bounded prefix. It preserves the inner content's headers,
/// length semantics (unknown stays unknown), cancellation and repeatable sends,
/// and never serializes ahead of the transport.
/// </summary>
internal sealed class QreObservingRequestContent : HttpContent
{
    private readonly QreHttpAttempt _attempt;
    private readonly int _maxCaptureBytes;

    public QreObservingRequestContent(HttpContent inner, QreHttpAttempt attempt, int maxCaptureBytes)
    {
        Inner = inner;
        _attempt = attempt;
        _maxCaptureBytes = maxCaptureBytes;
        foreach (var header in inner.Headers.NonValidated)
        {
            if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }

    public HttpContent Inner { get; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        var tee = new QreBoundedTeeStream(stream, _maxCaptureBytes);
        try
        {
            await Inner.CopyToAsync(tee, context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _attempt.ObserveRequest(tee, ex);
            throw;
        }
        _attempt.ObserveRequest(tee, null);
    }

    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var tee = new QreBoundedTeeStream(stream, _maxCaptureBytes);
        try
        {
            Inner.CopyTo(tee, context, cancellationToken);
        }
        catch (Exception ex)
        {
            _attempt.ObserveRequest(tee, ex);
            throw;
        }
        _attempt.ObserveRequest(tee, null);
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
        => Inner.ReadAsStreamAsync();

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        => Inner.ReadAsStreamAsync(cancellationToken);

    protected override bool TryComputeLength(out long length)
    {
        // Delegate to the inner content's own computation; never compute a length
        // the inner content would not report.
        if (Inner.Headers.ContentLength is { } known)
        {
            length = known;
            return true;
        }
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Write-through stream that keeps a bounded copy of the first bytes written.</summary>
internal sealed class QreBoundedTeeStream(Stream target, int maxCaptureBytes) : Stream
{
    private byte[]? _buffer = new byte[Math.Min(maxCaptureBytes, 4096)];
    private int _captured;

    public long TotalBytes { get; private set; }

    public bool Overflowed { get; private set; }

    public ReadOnlySpan<byte> Captured => _buffer.AsSpan(0, _captured);

    public void ReleaseCapture() => _buffer = null;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        target.Write(buffer);
        Capture(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await target.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Capture(buffer.Span);
    }

    public override void Flush() => target.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => target.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void Capture(ReadOnlySpan<byte> written)
    {
        TotalBytes += written.Length;
        if (Overflowed || _buffer == null)
        {
            return;
        }
        if (_captured + written.Length > maxCaptureBytes)
        {
            Overflowed = true;
            _captured = 0;
            _buffer = null;
            return;
        }
        if (_captured + written.Length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Min(maxCaptureBytes, Math.Max(_buffer.Length * 2, _captured + written.Length)));
        }
        written.CopyTo(_buffer.AsSpan(_captured));
        _captured += written.Length;
    }
}

/// <summary>
/// Response content wrapper that counts what the caller actually reads and records
/// the stream's end once: EOF, early disposal, cancellation or read error. It never
/// reads ahead of the caller.
/// </summary>
internal sealed class QreObservingResponseContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly QreHttpAttempt _attempt;
    private int _streamCreated;

    public QreObservingResponseContent(HttpContent inner, QreHttpAttempt attempt)
    {
        _inner = inner;
        _attempt = attempt;
        foreach (var header in inner.Headers.NonValidated)
        {
            if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _streamCreated, 1);
        var counting = new QreCountingWriteStream(stream, _attempt);
        try
        {
            await _inner.CopyToAsync(counting, context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _attempt.End(
                ex is OperationCanceledException ? QreDiagnosticStreamTerminations.Cancelled : QreDiagnosticStreamTerminations.ReadError,
                ex);
            throw;
        }
        _attempt.End(QreDiagnosticStreamTerminations.Eof, null);
    }

    protected override async Task<Stream> CreateContentReadStreamAsync()
        => await CreateContentReadStreamAsync(CancellationToken.None).ConfigureAwait(false);

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _streamCreated, 1);
        Stream inner;
        try
        {
            inner = await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _attempt.End(
                ex is OperationCanceledException ? QreDiagnosticStreamTerminations.Cancelled : QreDiagnosticStreamTerminations.ReadError,
                ex);
            throw;
        }
        return new QreObservingReadStream(inner, _attempt);
    }

    protected override bool TryComputeLength(out long length)
    {
        if (_inner.Headers.ContentLength is { } known)
        {
            length = known;
            return true;
        }
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (Volatile.Read(ref _streamCreated) == 0)
            {
                _attempt.End(QreDiagnosticStreamTerminations.NotRead, null);
            }
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Read-through stream that counts consumed bytes and records termination once.</summary>
internal sealed class QreObservingReadStream(Stream inner, QreHttpAttempt attempt) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read;
        try
        {
            read = inner.Read(buffer);
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
        return Observe(read, buffer.Length);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read;
        try
        {
            read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
        return Observe(read, buffer.Length);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            attempt.End(QreDiagnosticStreamTerminations.DisposedBeforeEof, null);
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        attempt.End(QreDiagnosticStreamTerminations.DisposedBeforeEof, null);
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private int Observe(int read, int requested)
    {
        if (read > 0)
        {
            attempt.AddBytesRead(read);
        }
        else if (requested > 0)
        {
            attempt.End(QreDiagnosticStreamTerminations.Eof, null);
        }
        return read;
    }

    private void Fail(Exception ex)
        => attempt.End(
            ex is OperationCanceledException ? QreDiagnosticStreamTerminations.Cancelled : QreDiagnosticStreamTerminations.ReadError,
            ex);
}

/// <summary>Write-through stream used when a caller buffers the response content.</summary>
internal sealed class QreCountingWriteStream(Stream target, QreHttpAttempt attempt) : Stream
{
    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        target.Write(buffer);
        attempt.AddBytesRead(buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await target.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        attempt.AddBytesRead(buffer.Length);
    }

    public override void Flush() => target.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => target.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
