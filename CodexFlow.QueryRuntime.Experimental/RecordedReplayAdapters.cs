using System.Runtime.CompilerServices;
using System.Text.Json;
using CodexFlow.QueryRuntime.Engine;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Experimental;

public sealed class RecordedReplayModelClient : IExperimentalModelClient
{
    private readonly Queue<RecordedModelResponse> _responses;

    public RecordedReplayModelClient(
        string traceFilePath,
        QueryRuntimeTraceReadOptions? options = null)
        : this(traceFilePath, new RecordedReplayReadContext(options))
    {
    }

    public RecordedReplayModelClient(
        string traceFilePath,
        RecordedReplayReadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _responses = new Queue<RecordedModelResponse>(LoadResponses(traceFilePath, context));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        QueryRuntimeModelRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("Recorded replay has no remaining model responses.");
        }

        var response = _responses.Dequeue();
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(response.AssistantText))
        {
            contents.Add(new TextContent(response.AssistantText));
        }

        contents.AddRange(response.ToolCalls.Select(call =>
            new FunctionCallContent(call.CallId, call.Name, call.Arguments)));

        yield return new ChatResponseUpdate(ChatRole.Assistant, contents);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static IReadOnlyList<RecordedModelResponse> LoadResponses(
        string traceFilePath,
        RecordedReplayReadContext context)
    {
        return JsonlTraceStore.ReadRecords(traceFilePath, context.Options)
            .Where(static record => record.Type == "model.response")
            .Select(record =>
            {
                if (!record.TryGetData(out var data))
                {
                    throw new InvalidOperationException("Recorded model response is missing Data.");
                }

                return new RecordedModelResponse(
                    ReadPayloadText(traceFilePath, data, "AssistantText", context.ArtifactBudget),
                    ReadToolCalls(data));
            })
            .ToArray();
    }

    private static IReadOnlyList<RecordedToolCall> ReadToolCalls(JsonElement data)
    {
        if (!data.TryGetProperty("ToolCalls", out var calls) || calls.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<RecordedToolCall>();
        foreach (var call in calls.EnumerateArray())
        {
            var callId = ReadRequiredString(call, "CallId");
            var name = ReadRequiredString(call, "Name");
            var arguments = call.TryGetProperty("Arguments", out var args) && args.ValueKind == JsonValueKind.Object
                ? ReadArguments(args)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            results.Add(new RecordedToolCall(callId, name, arguments));
        }

        return results;
    }

    internal static string ReadPayloadText(
        string traceFilePath,
        JsonElement data,
        string propertyName,
        TraceArtifactReadBudget? budget = null)
    {
        if (data.TryGetProperty(propertyName, out var inline) && inline.ValueKind == JsonValueKind.String)
        {
            return inline.GetString() ?? string.Empty;
        }

        if (!data.TryGetProperty($"{propertyName}Blob", out var blob) || blob.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return (budget ?? new TraceArtifactReadBudget(QueryRuntimeTraceReadOptions.Default))
            .ReadText(traceFilePath, blob);
    }

    public static Dictionary<string, object?> ReadArguments(JsonElement args)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in args.EnumerateObject())
        {
            result[property.Name] = ReadJsonValue(property.Value);
        }

        return result;
    }

    private static object? ReadJsonValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.Clone()
        };

    internal static string ReadRequiredString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : throw new InvalidOperationException($"Recorded replay field is missing: {propertyName}");

    private sealed record RecordedModelResponse(
        string AssistantText,
        IReadOnlyList<RecordedToolCall> ToolCalls);

    private sealed record RecordedToolCall(
        string CallId,
        string Name,
        Dictionary<string, object?> Arguments);
}

public static class RecordedReplayToolPack
{
    public static IReadOnlyList<AIFunction> Create(
        string traceFilePath,
        QueryRuntimeTraceReadOptions? options = null)
        => Create(traceFilePath, new RecordedReplayReadContext(options));

    public static IReadOnlyList<AIFunction> Create(
        string traceFilePath,
        RecordedReplayReadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var outputs = LoadOutputs(traceFilePath, context);
        return outputs
            .GroupBy(static output => output.ToolName, StringComparer.OrdinalIgnoreCase)
            .Select(group => AIFunctionFactory.Create(
                (AIFunctionArguments args) =>
                {
                    var arguments = args.ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase);
                    var hash = QreArgumentHash.Compute(arguments);
                    var output = group.FirstOrDefault(candidate =>
                        string.Equals(candidate.ArgumentHash, hash, StringComparison.Ordinal));
                    if (output == null)
                    {
                        throw new InvalidOperationException($"Recorded replay has no output for {group.Key} with argument hash {hash}.");
                    }

                    return output.Result;
                },
                new AIFunctionFactoryOptions
                {
                    Name = group.Key,
                    Description = "Recorded replay tool output."
                }))
            .ToArray();
    }

    private static IReadOnlyList<RecordedToolOutput> LoadOutputs(
        string traceFilePath,
        RecordedReplayReadContext context)
    {
        var records = JsonlTraceStore.ReadRecords(traceFilePath, context.Options);
        var hashesByCallId = records
            .Where(static record => record.Type == "tool.call.requested")
            .Select(static record => record.TryGetData(out var data)
                ? new
                {
                    CallId = TryReadOptionalString(data, "CallId"),
                    Hash = TryReadOptionalString(data, "ArgumentHash")
                }
                : null)
            .Where(static item => item is { CallId: not null, Hash: not null })
            .ToDictionary(
                static item => item!.CallId!,
                static item => item!.Hash!,
                StringComparer.Ordinal);

        var outputs = new List<RecordedToolOutput>();
        foreach (var record in records.Where(static record => record.Type == "tool.execution.completed"))
        {
            if (!record.TryGetData(out var data))
            {
                continue;
            }

            var callId = TryReadOptionalString(data, "CallId");
            var toolName = TryReadOptionalString(data, "ToolName");
            if (callId == null || toolName == null || !hashesByCallId.TryGetValue(callId, out var hash))
            {
                continue;
            }

            outputs.Add(new RecordedToolOutput(
                toolName,
                hash,
                RecordedReplayModelClient.ReadPayloadText(traceFilePath, data, "Result", context.ArtifactBudget)));
        }

        return outputs;
    }

    private static string? TryReadOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record RecordedToolOutput(
        string ToolName,
        string ArgumentHash,
        string Result);
}
