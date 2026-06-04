using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Engine;

public sealed class QueryRuntimeEngine : IQueryRuntimeEngine
{
    private readonly IQueryRuntimeModelClient _modelClient;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _queryIdFactory;

    public QueryRuntimeEngine(IQueryRuntimeModelClient modelClient)
        : this(modelClient, timeProvider: null, queryIdFactory: null)
    {
    }

    /// <summary>
    /// Creates an engine with injectable clock and query-id sources. Both default to
    /// non-deterministic system sources; deterministic replay seeds them from a source
    /// trace so repeated replays produce a byte-identical canonical event projection.
    /// </summary>
    public QueryRuntimeEngine(
        IQueryRuntimeModelClient modelClient,
        TimeProvider? timeProvider,
        Func<Guid>? queryIdFactory)
    {
        _modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _queryIdFactory = queryIdFactory ?? Guid.NewGuid;
    }

    public async Task<QueryRuntimeResult> ExecuteAsync(
        QueryRuntimeRequest request,
        IQueryRuntimeEventSink eventSink,
        string runId,
        string traceFilePath,
        string? workspacePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(traceFilePath);

        var queryId = _queryIdFactory();
        var startTimestamp = _timeProvider.GetTimestamp();
        var messages = request.InitialMessages.ToList();
        var totalToolCalls = 0;
        var finalText = string.Empty;
        var terminationReason = QueryTerminationReason.MaxRounds;
        var seq = 0L;
        var requiredToolSatisfied = false;
        var completedRounds = 0;

        try
        {
            for (var round = 0; round < Math.Max(1, request.MaxRounds); round++)
            {
                await EmitAsync(
                    eventSink,
                    QueryRuntimeEventType.RoundStarted,
                    new RoundStartedEvent(++seq, queryId, request.SessionId, Now(), round, request.MaxRounds)).ConfigureAwait(false);

                var options = PrepareOptions(request, requiredToolSatisfied);
                await EmitAsync(
                    eventSink,
                    QueryRuntimeEventType.PromptAssemblySnapshot,
                    new PromptAssemblySnapshotEvent(
                        ++seq,
                        queryId,
                        request.SessionId,
                        Now(),
                        round,
                        messages.Count,
                        request.EnableTools,
                        options.Tools?.Select(tool => tool.Name).ToArray() ?? [],
                        request.RequiredToolName)).ConfigureAwait(false);

                var textParts = new List<string>();
                var functionCalls = new List<FunctionCallContent>();
                await foreach (var update in _modelClient.StreamAsync(
                                   new QueryRuntimeModelRequest(messages, options, runId, workspacePath),
                                   ct).ConfigureAwait(false))
                {
                    foreach (var content in update.Contents)
                    {
                        switch (content)
                        {
                            case TextContent textContent:
                                textParts.Add(textContent.Text);
                                break;
                            case FunctionCallContent functionCall:
                                functionCalls.Add(functionCall);
                                break;
                        }
                    }
                }

                var assistantText = string.Concat(textParts);
                await EmitAsync(
                    eventSink,
                    QueryRuntimeEventType.ModelResponseSampled,
                    new ModelResponseSampledEvent(
                        ++seq,
                        queryId,
                        request.SessionId,
                        Now(),
                        round,
                        assistantText.Length,
                        functionCalls.Count,
                        assistantText,
                        functionCalls
                            .Select(static functionCall => new QueryRuntimeFunctionCallSnapshot(
                                functionCall.CallId,
                                functionCall.Name,
                                functionCall.Arguments == null
                                    ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                                    : new Dictionary<string, object?>(functionCall.Arguments, StringComparer.OrdinalIgnoreCase)))
                            .ToArray())).ConfigureAwait(false);

                if (functionCalls.Count == 0 || !request.EnableTools)
                {
                    finalText = assistantText;
                    terminationReason = QueryTerminationReason.NoToolCalls;
                    await EmitRoundCompletedAsync(eventSink, ++seq, queryId, request, round, 0, assistantText, null).ConfigureAwait(false);
                    completedRounds = round + 1;
                    break;
                }

                var assistantContents = new List<AIContent>();
                if (!string.IsNullOrEmpty(assistantText))
                {
                    assistantContents.Add(new TextContent(assistantText));
                }

                assistantContents.AddRange(functionCalls);
                messages.Add(new ChatMessage(ChatRole.Assistant, assistantContents));
                var toolMessages = new List<AIContent>();
                foreach (var functionCall in functionCalls)
                {
                    var tool = request.AvailableTools.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, functionCall.Name, StringComparison.OrdinalIgnoreCase));
                    if (tool == null)
                    {
                        continue;
                    }

                    var arguments = functionCall.Arguments == null
                        ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, object?>(functionCall.Arguments, StringComparer.OrdinalIgnoreCase);
                    await EmitAsync(
                        eventSink,
                        QueryRuntimeEventType.ToolCallRequested,
                        new ToolCallRequestedEvent(
                            ++seq,
                            queryId,
                            request.SessionId,
                            Now(),
                            round,
                            tool.Name,
                            functionCall.CallId,
                            arguments)).ConfigureAwait(false);
                    await EmitAsync(
                        eventSink,
                        QueryRuntimeEventType.ToolExecutionStarted,
                        new ToolExecutionStartedEvent(++seq, queryId, request.SessionId, Now(), round, tool.Name, functionCall.CallId)).ConfigureAwait(false);

                    var result = await tool.InvokeAsync(new AIFunctionArguments(arguments), ct).ConfigureAwait(false);
                    var resultText = result?.ToString() ?? string.Empty;
                    totalToolCalls++;
                    if (string.Equals(tool.Name, request.RequiredToolName, StringComparison.OrdinalIgnoreCase))
                    {
                        requiredToolSatisfied = true;
                    }

                    toolMessages.Add(new FunctionResultContent(functionCall.CallId, resultText));
                    await EmitAsync(
                        eventSink,
                        QueryRuntimeEventType.ToolExecutionCompleted,
                        new ToolExecutionCompletedEvent(
                            ++seq,
                            queryId,
                            request.SessionId,
                            Now(),
                            round,
                            tool.Name,
                            functionCall.CallId,
                            true,
                            resultText.Length,
                            resultText)).ConfigureAwait(false);
                }

                messages.Add(new ChatMessage(ChatRole.Tool, toolMessages));
                await EmitRoundCompletedAsync(eventSink, ++seq, queryId, request, round, functionCalls.Count, assistantText, "tool_calls").ConfigureAwait(false);
                completedRounds = round + 1;
            }

            var elapsedMs = (long)_timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds;
            await EmitAsync(
                eventSink,
                QueryRuntimeEventType.Terminated,
                new TerminatedEvent(
                    ++seq,
                    queryId,
                    request.SessionId,
                    Now(),
                    terminationReason,
                    completedRounds,
                    totalToolCalls,
                    elapsedMs,
                    null)).ConfigureAwait(false);

            return new QueryRuntimeResult(
                runId,
                request.SessionId,
                traceFilePath,
                finalText,
                terminationReason,
                completedRounds,
                totalToolCalls,
                elapsedMs);
        }
        catch (Exception ex)
        {
            await EmitAsync(
                eventSink,
                QueryRuntimeEventType.Error,
                new ErrorEvent(++seq, queryId, request.SessionId, Now(), ex.GetType().Name, ex.Message, ex)).ConfigureAwait(false);
            throw;
        }
    }

    private static ChatOptions PrepareOptions(QueryRuntimeRequest request, bool requiredToolSatisfied)
    {
        var options = request.Options ?? new ChatOptions();
        if (request.EnableTools && request.AvailableTools.Count > 0)
        {
            options.Tools = request.AvailableTools.Cast<AITool>().ToList();
            if (!requiredToolSatisfied && !string.IsNullOrWhiteSpace(request.RequiredToolName))
            {
                options.ToolMode = ChatToolMode.RequireSpecific(request.RequiredToolName.Trim());
            }
            else
            {
                options.ToolMode = null;
            }
        }
        else
        {
            options.Tools = [];
            options.ToolMode = ChatToolMode.None;
        }

        return options;
    }

    private async ValueTask EmitRoundCompletedAsync(
        IQueryRuntimeEventSink eventSink,
        long seq,
        Guid queryId,
        QueryRuntimeRequest request,
        int round,
        int toolCallCount,
        string text,
        string? continueReason)
        => await EmitAsync(
            eventSink,
            QueryRuntimeEventType.RoundCompleted,
            new RoundCompletedEvent(
                seq,
                queryId,
                request.SessionId,
                Now(),
                round,
                toolCallCount,
                !string.IsNullOrWhiteSpace(text),
                text.Length,
                continueReason)).ConfigureAwait(false);

    private static async ValueTask EmitAsync(
        IQueryRuntimeEventSink sink,
        QueryRuntimeEventType eventType,
        QueryRuntimeEvent runtimeEvent)
    {
        if (sink.IsEnabled(eventType))
        {
            await sink.OnEventAsync(runtimeEvent).ConfigureAwait(false);
        }
    }

    private DateTimeOffset Now() => _timeProvider.GetUtcNow();
}
