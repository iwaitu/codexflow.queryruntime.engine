using System.Text.Json;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Models.Diagnostics;

/// <summary>
/// Adapter-side lifecycle of one observed model call. It emits the Runtime and
/// adapter observation points, advances the SDK enumerator inside a scoped
/// AsyncLocal bridge, and records <c>model_call_ended</c> exactly once.
/// </summary>
internal sealed class QreModelCallObserver
{
    private readonly QreOutboundDiagnostics _diagnostics;
    private readonly long _startTimestamp;
    private int _ended;
    private int _events;

    private QreModelCallObserver(QreOutboundDiagnostics diagnostics, QreModelCallContext call)
    {
        _diagnostics = diagnostics;
        Call = call;
        _startTimestamp = diagnostics.GetTimestamp();
    }

    public QreModelCallContext Call { get; }

    public static QreModelCallObserver Begin(
        QreOutboundDiagnostics diagnostics,
        QreOutboundDiagnosticsTarget target,
        RuntimeModelRequest request,
        RuntimeModelAttemptContext? attempt,
        CancellationToken upstreamToken)
    {
        var call = new QreModelCallContext(
            diagnostics,
            diagnostics.NextModelCallId(),
            diagnostics.Alias("step", request.StepId.Value),
            attempt?.RuntimeModelAttemptOrdinal,
            string.IsNullOrWhiteSpace(attempt?.RunAttemptId)
                ? QreOutboundDiagnostics.Unavailable
                : diagnostics.Alias("run-attempt", attempt.RunAttemptId),
            target,
            upstreamToken);
        var observer = new QreModelCallObserver(diagnostics, call);
        QreSemanticRequest? semantic = null;
        string status = QreDiagnosticCaptureStatus.Complete;
        string? reason = attempt == null ? "runtime_model_attempt_unavailable" : null;
        try
        {
            semantic = QreSemanticProjector.FromRuntime(request, diagnostics);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            diagnostics.CountProjectionFailure();
            status = QreDiagnosticCaptureStatus.Failed;
            reason = "projection_failed";
        }
        diagnostics.Emit(diagnostics.NewRecord(
            QreDiagnosticEventTypes.ModelCallStarted,
            QreDiagnosticObservationPoints.Runtime,
            call,
            status) with
        {
            Stage = QreDiagnosticStages.RuntimePrepared,
            Request = semantic,
            ReasonCode = reason
        });
        return observer;
    }

    public void AdapterPrepared(IReadOnlyList<ChatMessage> messages, ChatOptions options, string? descriptorDefaultModel)
    {
        QreSemanticRequest? semantic = null;
        string status = QreDiagnosticCaptureStatus.Complete;
        string? reason = null;
        try
        {
            semantic = QreSemanticProjector.FromAdapter(messages, options, descriptorDefaultModel, _diagnostics);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _diagnostics.CountProjectionFailure();
            status = QreDiagnosticCaptureStatus.Failed;
            reason = "projection_failed";
        }
        _diagnostics.Emit(_diagnostics.NewRecord(
            QreDiagnosticEventTypes.AdapterPrepared,
            QreDiagnosticObservationPoints.Adapter,
            Call,
            status) with
        {
            Stage = QreDiagnosticStages.AdapterPrepared,
            Request = semantic,
            ReasonCode = reason
        });
    }

    public void CountEvent() => Interlocked.Increment(ref _events);

    /// <summary>Advances the SDK enumerator with the call scope bridged to the handler.</summary>
    public async ValueTask<bool> MoveNextAsync(IAsyncEnumerator<ChatResponseUpdate> enumerator)
    {
        try
        {
            using (QreModelCallScope.Enter(Call))
            {
                return await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
    }

    public IAsyncEnumerator<ChatResponseUpdate> Start(
        IChatClient chatClient,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        CancellationToken ct)
    {
        try
        {
            using (QreModelCallScope.Enter(Call))
            {
                return chatClient.GetStreamingResponseAsync(messages, options, ct).GetAsyncEnumerator(ct);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
    }

    public async ValueTask DisposeEnumeratorAsync(IAsyncEnumerator<ChatResponseUpdate> enumerator)
    {
        using (QreModelCallScope.Enter(Call))
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Completed(RuntimeModelStopReason stopReason, bool finishReasonObserved)
        => End(
            "completed",
            stopReason.ToString(),
            finishReasonObserved,
            finishReasonObserved ? QreDiagnosticFailurePhases.None : QreDiagnosticFailurePhases.AdapterProtocol,
            finishReasonObserved ? QreDiagnosticClassifications.None : QreDiagnosticClassifications.MissingFinishReason,
            QreDiagnosticClassificationSources.Adapter,
            null,
            null);

    /// <summary>Records a failure by exception type and independent evidence only.</summary>
    public void Fail(Exception exception)
    {
        if (exception is RuntimeModelClientException typed)
        {
            End(
                "failed",
                null,
                false,
                QreDiagnosticFailurePhases.AdapterProtocol,
                QreDiagnosticClassifications.ProtocolError,
                QreDiagnosticClassificationSources.Adapter,
                typed.Error.Code,
                typed.Error.Category.ToString());
            return;
        }

        var upstreamCancelled = Call.UpstreamToken.IsCancellationRequested;
        var timeoutEvidence = exception is OperationCanceledException && exception.InnerException is TimeoutException;
        string outcome = "failed";
        string phase = QreDiagnosticFailurePhases.Unknown;
        string classification;
        if (upstreamCancelled && timeoutEvidence)
        {
            outcome = "cancelled";
            classification = QreDiagnosticClassifications.CancellationTimeoutRace;
        }
        else if (upstreamCancelled && exception is OperationCanceledException)
        {
            outcome = "cancelled";
            classification = QreDiagnosticClassifications.CallerCancelled;
        }
        else if (timeoutEvidence)
        {
            // HttpClient.Timeout wraps the cancellation outside the handler, so only
            // the adapter sees the TimeoutException type evidence.
            classification = QreDiagnosticClassifications.HttpClientTimeout;
            phase = Call.LastStatusCode == 0
                ? QreDiagnosticFailurePhases.BeforeHeaders
                : QreDiagnosticFailurePhases.ResponseRead;
        }
        else if (Call.LastStatusCode >= 400)
        {
            classification = QreDiagnosticClassifications.HttpStatusFailure;
            phase = QreDiagnosticFailurePhases.ResponseRead;
        }
        else if (exception is HttpRequestException)
        {
            classification = QreDiagnosticClassifications.TransportError;
            phase = Call.LastStatusCode == 0
                ? QreDiagnosticFailurePhases.BeforeHeaders
                : QreDiagnosticFailurePhases.ResponseRead;
        }
        else if (exception is JsonException)
        {
            classification = QreDiagnosticClassifications.SdkParseFailure;
            phase = QreDiagnosticFailurePhases.AdapterProtocol;
        }
        else if (exception is RuntimeProtocolAdapterException)
        {
            classification = QreDiagnosticClassifications.ProtocolError;
            phase = QreDiagnosticFailurePhases.AdapterProtocol;
        }
        else
        {
            classification = QreDiagnosticClassifications.Unknown;
        }
        End(outcome, null, false, phase, classification, QreDiagnosticClassificationSources.Adapter, null, null);
    }

    /// <summary>Records an abandoned call when the consumer stopped enumerating early.</summary>
    public void EndIfOpen()
        => End(
            "abandoned",
            null,
            false,
            QreDiagnosticFailurePhases.None,
            QreDiagnosticClassifications.None,
            QreDiagnosticClassificationSources.Adapter,
            null,
            null);

    private void End(
        string outcome,
        string? stopReason,
        bool finishReasonObserved,
        string phase,
        string classification,
        string source,
        string? errorCode,
        string? errorCategory)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }
        _diagnostics.Emit(_diagnostics.NewRecord(
            QreDiagnosticEventTypes.ModelCallEnded,
            QreDiagnosticObservationPoints.Adapter,
            Call) with
        {
            Stage = QreDiagnosticStages.ModelCall,
            ModelOutcome = new QreModelCallOutcome
            {
                Outcome = outcome,
                StopReason = stopReason,
                FinishReasonObserved = finishReasonObserved,
                FailurePhase = phase,
                Classification = classification,
                ClassificationSource = source,
                ErrorCode = errorCode,
                ErrorCategory = errorCategory,
                ProtocolEventCount = Volatile.Read(ref _events),
                HttpAttemptCount = Call.HttpAttemptCount,
                ElapsedMs = _diagnostics.ElapsedSince(_startTimestamp)
            }
        });
    }
}
