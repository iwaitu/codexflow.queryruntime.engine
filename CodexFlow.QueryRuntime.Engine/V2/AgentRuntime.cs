using CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.QueryRuntime.Engine.V2;

/// <summary>
/// Stable hosting facade for the v2 single-process runtime. The lower-level
/// loop remains available for focused tests and adapter development.
/// </summary>
public interface IAgentRuntime
{
    Task<RuntimeTurnResult> RunAsync(
        RuntimeRunRequest request,
        IRuntimeEventSink? eventSink,
        CancellationToken ct);
}

public sealed record RuntimeRunRequest(RuntimeAgentLoopRequest LoopRequest)
{
    public RuntimeTurnHandle? Handle { get; init; }
}

public sealed record RuntimeTurnResult(RuntimeAgentLoopResult LoopResult)
{
    public RuntimeSessionState Session => LoopResult.Session;

    public RuntimeTurnState Turn => LoopResult.Turn;

    public IReadOnlyList<RuntimeMessage> History => LoopResult.History;

    public string FinalText => LoopResult.FinalText;

    public RuntimeTurnStatus Status => LoopResult.Status;

    public RuntimeTerminationReason TerminationReason => LoopResult.TerminationReason;

    public RuntimeError? Error => LoopResult.Error;

    public RuntimeUsageTotals Usage => LoopResult.Usage;
}

public interface IRuntimeEventSink
{
    ValueTask OnEventAsync(RuntimePresentationEvent runtimeEvent, CancellationToken ct);
}

public enum RuntimePresentationEventType
{
    TurnStarted = 0,
    StepStarted = 1,
    TextDelta = 2,
    ReasoningDelta = 3,
    ToolCallRequested = 4,
    UsageUpdated = 5,
    Warning = 6,
    ToolExecutionCompleted = 7,
    TurnCompleted = 8,
    TurnFailed = 9,
    TurnCancelled = 10,
    ContextPrepared = 11,
    HistoryCompacted = 12
}

/// <summary>
/// Ephemeral, ordered host presentation event. This is not the C6 durable
/// audit envelope and intentionally has no replay or cursor promise.
/// </summary>
public sealed record RuntimePresentationEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    RuntimePresentationEventType Type,
    RuntimeSessionId SessionId,
    RuntimeTurnId TurnId,
    RuntimeStepId? StepId = null,
    RuntimeInvocationId? InvocationId = null,
    string? ToolName = null,
    RuntimeToolCall? ToolCall = null,
    string? Text = null,
    RuntimeToolResult? ToolResult = null,
    RuntimeUsage? Usage = null,
    RuntimeWarning? Warning = null,
    RuntimeTurnStatus? TurnStatus = null,
    RuntimeTerminationReason? TerminationReason = null,
    int? TotalSteps = null,
    int? TotalToolCalls = null,
    int? ContinuationCount = null,
    RuntimeError? Error = null,
    RuntimeContextEvent? ContextEvent = null);

public sealed class AgentRuntime : IAgentRuntime
{
    private readonly IRuntimeModelClient _modelClient;
    private readonly TimeProvider _timeProvider;

    public AgentRuntime(IRuntimeModelClient modelClient, TimeProvider? timeProvider = null)
    {
        _modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RuntimeTurnResult> RunAsync(
        RuntimeRunRequest request,
        IRuntimeEventSink? eventSink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.LoopRequest);

        var emitter = new RuntimePresentationEmitter(
            eventSink,
            request.LoopRequest.SessionId,
            request.LoopRequest.TurnId,
            _timeProvider);
        await emitter.EmitAsync(RuntimePresentationEventType.TurnStarted, ct).ConfigureAwait(false);

        var loopRequest = request.LoopRequest with
        {
            ToolPipeline = request.LoopRequest.ToolPipeline == null
                ? null
                : new PresentingToolPipeline(request.LoopRequest.ToolPipeline, emitter),
            ToolExecutor = request.LoopRequest.ToolExecutor == null
                ? null
                : new PresentingToolExecutor(request.LoopRequest.ToolExecutor, emitter),
            ContextEventSink = new PresentingContextEventSink(
                request.LoopRequest.ContextEventSink,
                emitter)
        };
        var loop = new RuntimeAgentLoop(
            new PresentingModelClient(_modelClient, emitter),
            _timeProvider);
        var result = await loop.RunAsync(loopRequest, request.Handle, ct).ConfigureAwait(false);
        await emitter.EmitAsync(
            result.Status switch
            {
                RuntimeTurnStatus.Completed => RuntimePresentationEventType.TurnCompleted,
                RuntimeTurnStatus.Cancelled => RuntimePresentationEventType.TurnCancelled,
                _ => RuntimePresentationEventType.TurnFailed
            },
            CancellationToken.None,
            text: result.FinalText,
            turnStatus: result.Status,
            terminationReason: result.TerminationReason,
            totalSteps: result.Turn.Steps.Count,
            totalToolCalls: result.Turn.Progress.ToolCallCount,
            continuationCount: result.Turn.Progress.ContinuationCount,
            error: result.Error).ConfigureAwait(false);
        return new RuntimeTurnResult(result);
    }

    private sealed class PresentingModelClient(
        IRuntimeModelClient inner,
        RuntimePresentationEmitter emitter) : IRuntimeModelClient
    {
        public async IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
            RuntimeModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await emitter.EmitAsync(
                RuntimePresentationEventType.StepStarted,
                ct,
                stepId: request.StepId).ConfigureAwait(false);

            await foreach (var runtimeEvent in inner.StreamAsync(request, ct).ConfigureAwait(false))
            {
                switch (runtimeEvent)
                {
                    case RuntimeTextDeltaEvent text:
                        await emitter.EmitAsync(
                            RuntimePresentationEventType.TextDelta,
                            ct,
                            stepId: request.StepId,
                            text: text.Text).ConfigureAwait(false);
                        break;
                    case RuntimeReasoningDeltaEvent reasoning:
                        await emitter.EmitAsync(
                            RuntimePresentationEventType.ReasoningDelta,
                            ct,
                            stepId: request.StepId,
                            text: reasoning.Text).ConfigureAwait(false);
                        break;
                    case RuntimeToolCallEvent toolCall:
                        await emitter.EmitAsync(
                            RuntimePresentationEventType.ToolCallRequested,
                            ct,
                            stepId: request.StepId,
                            invocationId: toolCall.Call.InvocationId,
                            toolName: toolCall.Call.Name,
                            toolCall: toolCall.Call).ConfigureAwait(false);
                        break;
                    case RuntimeUsageEvent usage:
                        await emitter.EmitAsync(
                            RuntimePresentationEventType.UsageUpdated,
                            ct,
                            stepId: request.StepId,
                            usage: usage.Usage).ConfigureAwait(false);
                        break;
                    case RuntimeWarningEvent warning:
                        await emitter.EmitAsync(
                            RuntimePresentationEventType.Warning,
                            ct,
                            stepId: request.StepId,
                            warning: warning.Warning).ConfigureAwait(false);
                        break;
                }

                yield return runtimeEvent;
            }
        }
    }

    private sealed class PresentingContextEventSink(
        IRuntimeContextEventSink? inner,
        RuntimePresentationEmitter emitter) : IRuntimeContextEventSink
    {
        public async ValueTask OnEventAsync(RuntimeContextEvent runtimeEvent, CancellationToken ct)
        {
            if (inner != null)
            {
                await inner.OnEventAsync(runtimeEvent, ct).ConfigureAwait(false);
            }
            await emitter.EmitAsync(
                runtimeEvent.Kind == RuntimeContextEventKind.ContextCompacted
                    ? RuntimePresentationEventType.HistoryCompacted
                    : RuntimePresentationEventType.ContextPrepared,
                ct,
                contextEvent: runtimeEvent).ConfigureAwait(false);
        }
    }

    private sealed class PresentingToolExecutor(
        IRuntimeToolExecutor inner,
        RuntimePresentationEmitter emitter) : IRuntimeToolExecutor
    {
        public async ValueTask<RuntimeToolResult> ExecuteAsync(
            RuntimeToolDescriptor descriptor,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
        {
            var result = await inner.ExecuteAsync(descriptor, call, context, ct).ConfigureAwait(false);
            await emitter.EmitAsync(
                RuntimePresentationEventType.ToolExecutionCompleted,
                ct,
                stepId: context.StepId,
                invocationId: call.InvocationId,
                toolName: call.Name,
                toolResult: result).ConfigureAwait(false);
            return result;
        }
    }

    private sealed class PresentingToolPipeline(
        IRuntimeToolExecutionPipeline inner,
        RuntimePresentationEmitter emitter) : IRuntimeToolExecutionPipeline
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, long> _ordinals = new(StringComparer.Ordinal);
        private readonly SortedDictionary<long, PendingObservation> _pending = [];
        private long _assigned;
        private long _nextToEmit;
        private bool _emitting;

        public IReadOnlyList<RuntimeToolDescriptor> Descriptors => inner.Descriptors;

        public async ValueTask<RuntimePreparedToolInvocation> PrepareAsync(
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
        {
            var prepared = await inner.PrepareAsync(call, context, ct).ConfigureAwait(false);
            if (prepared.Kind == RuntimeToolPreparationKind.Ready)
            {
                lock (_sync)
                {
                    _ordinals.Add(call.InvocationId.Value, _assigned++);
                }
            }
            return prepared;
        }

        public async ValueTask<RuntimeToolResult> ExecuteAsync(
            RuntimePreparedToolInvocation prepared,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
        {
            var result = await inner.ExecuteAsync(prepared, context, ct).ConfigureAwait(false);
            Task completion;
            var shouldDrain = false;
            lock (_sync)
            {
                if (!_ordinals.TryGetValue(prepared.Call.InvocationId.Value, out var ordinal))
                {
                    throw new InvalidOperationException("The presenting pipeline did not observe tool preparation.");
                }
                var pending = new PendingObservation(prepared, context, result);
                _pending.Add(ordinal, pending);
                completion = pending.Completion.Task;
                if (!_emitting && _pending.ContainsKey(_nextToEmit))
                {
                    _emitting = true;
                    shouldDrain = true;
                }
            }
            if (shouldDrain)
            {
                await DrainAsync(ct).ConfigureAwait(false);
            }
            await completion.WaitAsync(ct).ConfigureAwait(false);
            return result;
        }

        private async Task DrainAsync(CancellationToken ct)
        {
            while (true)
            {
                PendingObservation? pending;
                lock (_sync)
                {
                    if (!_pending.Remove(_nextToEmit, out pending))
                    {
                        _emitting = false;
                        return;
                    }
                    _nextToEmit++;
                }
                try
                {
                    await emitter.EmitAsync(
                        RuntimePresentationEventType.ToolExecutionCompleted,
                        ct,
                        stepId: pending.Context.StepId,
                        invocationId: pending.Prepared.Call.InvocationId,
                        toolName: pending.Prepared.Plan!.ToolCanonicalName,
                        toolResult: pending.Result).ConfigureAwait(false);
                    pending.Completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    pending.Completion.TrySetException(ex);
                    throw;
                }
            }
        }

        private sealed record PendingObservation(
            RuntimePreparedToolInvocation Prepared,
            RuntimeToolExecutionContext Context,
            RuntimeToolResult Result)
        {
            public TaskCompletionSource Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class RuntimePresentationEmitter(
        IRuntimeEventSink? sink,
        RuntimeSessionId sessionId,
        RuntimeTurnId turnId,
        TimeProvider timeProvider)
    {
        private long _sequence;

        public ValueTask EmitAsync(
            RuntimePresentationEventType type,
            CancellationToken ct,
            RuntimeStepId? stepId = null,
            RuntimeInvocationId? invocationId = null,
            string? toolName = null,
            RuntimeToolCall? toolCall = null,
            string? text = null,
            RuntimeToolResult? toolResult = null,
            RuntimeUsage? usage = null,
            RuntimeWarning? warning = null,
            RuntimeTurnStatus? turnStatus = null,
            RuntimeTerminationReason? terminationReason = null,
            int? totalSteps = null,
            int? totalToolCalls = null,
            int? continuationCount = null,
            RuntimeError? error = null,
            RuntimeContextEvent? contextEvent = null)
        {
            if (sink == null)
            {
                return ValueTask.CompletedTask;
            }

            return sink.OnEventAsync(
                new RuntimePresentationEvent(
                    Interlocked.Increment(ref _sequence),
                    timeProvider.GetUtcNow(),
                    type,
                    sessionId,
                    turnId,
                    stepId,
                    invocationId,
                    toolName,
                    toolCall,
                    text,
                    toolResult,
                    usage,
                    warning,
                    turnStatus,
                    terminationReason,
                    totalSteps,
                    totalToolCalls,
                    continuationCount,
                    error,
                    contextEvent),
                ct);
        }
    }
}
