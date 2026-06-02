using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Engine;

public sealed class QueryRuntimeEngine(IQueryRuntimeModelClient modelClient)
{
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

        var queryId = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();
        var messages = request.InitialMessages.ToList();
        var totalToolCalls = 0;
        var finalText = string.Empty;
        var terminationReason = QueryTerminationReason.MaxRounds;
        var seq = 0L;
        var requiredToolSatisfied = false;

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
                await foreach (var update in modelClient.StreamAsync(
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
                        functionCalls.Count)).ConfigureAwait(false);

                if (functionCalls.Count == 0 || !request.EnableTools)
                {
                    finalText = assistantText;
                    terminationReason = QueryTerminationReason.NoToolCalls;
                    await EmitRoundCompletedAsync(eventSink, ++seq, queryId, request, round, 0, assistantText, null).ConfigureAwait(false);
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
            }

            stopwatch.Stop();
            await EmitAsync(
                eventSink,
                QueryRuntimeEventType.Terminated,
                new TerminatedEvent(
                    ++seq,
                    queryId,
                    request.SessionId,
                    Now(),
                    terminationReason,
                    Math.Min(Math.Max(1, request.MaxRounds), seq == 0 ? 0 : request.MaxRounds),
                    totalToolCalls,
                    stopwatch.ElapsedMilliseconds,
                    null)).ConfigureAwait(false);

            return new QueryRuntimeResult(
                runId,
                request.SessionId,
                traceFilePath,
                finalText,
                terminationReason,
                Math.Min(Math.Max(1, request.MaxRounds), Math.Max(1, totalToolCalls + 1)),
                totalToolCalls,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
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

    private static async ValueTask EmitRoundCompletedAsync(
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

    private static DateTimeOffset Now() => DateTimeOffset.UtcNow;
}
