using System.Text.Encodings.Web;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Engine;
using EngineQueryRuntimeResult = CodexFlow.QueryRuntime.Engine.QueryRuntimeResult;

namespace CodexFlow.QueryRuntime.Experimental;

public sealed class JsonlTraceEventSink : IQueryRuntimeEventSink, IQueryRuntimePolicyDecisionSink, IAsyncDisposable
{
    private const int InlineTextLimit = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = QueryRuntimeExperimentalJsonContext.Default,
        WriteIndented = false
    };

    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private JsonlTraceEventSink(string traceFilePath)
    {
        TraceFilePath = traceFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(traceFilePath)!);
        _writer = new StreamWriter(new FileStream(traceFilePath, FileMode.Create, FileAccess.Write, FileShare.Read));
    }

    public string TraceFilePath { get; }

    public static async Task<JsonlTraceEventSink> CreateAsync(
        string traceFilePath,
        ExperimentalRunStartedRecord started,
        CancellationToken ct = default)
    {
        var sink = new JsonlTraceEventSink(traceFilePath);
        await sink.WriteRecordAsync(started, ct).ConfigureAwait(false);
        return sink;
    }

    public bool IsEnabled(QueryRuntimeEventType eventType) => true;

    public async ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent)
    {
        await WriteRecordAsync(TraceRecord.FromRuntimeEvent(runtimeEvent), CancellationToken.None).ConfigureAwait(false);
    }

    public Task WriteCompletedAsync(ExperimentalRunCompletedRecord completed, CancellationToken ct = default)
        => WriteRecordAsync(completed, ct);

    public Task WriteFailedAsync(ExperimentalRunFailedRecord failed, CancellationToken ct = default)
        => WriteRecordAsync(failed, ct);

    public Task OnPolicyDecisionAsync(
        QueryRuntimePolicyDecisionRecord decision,
        CancellationToken ct = default)
        => WriteRecordAsync(ExperimentalPolicyDecisionTraceRecord.Create(decision), ct);

    public static async Task WriteManifestAsync(
        string traceFilePath,
        ExperimentalRunManifest manifest,
        CancellationToken ct = default)
    {
        var runDirectory = Path.GetDirectoryName(traceFilePath);
        if (string.IsNullOrWhiteSpace(runDirectory))
        {
            throw new ArgumentException("Trace file path must include a run directory.", nameof(traceFilePath));
        }

        Directory.CreateDirectory(runDirectory);
        Directory.CreateDirectory(Path.Combine(runDirectory, "artifacts"));
        var manifestPath = Path.Combine(runDirectory, "manifest.json");
        var runJsonPath = Path.Combine(runDirectory, "run.json");
        var json = JsonSerializer.Serialize(manifest, QueryRuntimeExperimentalJsonContext.Default.ExperimentalRunManifest);
        await File.WriteAllTextAsync(manifestPath, json + Environment.NewLine, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(runJsonPath, json + Environment.NewLine, ct).ConfigureAwait(false);
    }

    private async Task WriteRecordAsync(object record, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            record = MaterializeLargePayloads(record);
            var json = SerializeRecord(record);
            await _writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private static string SerializeRecord(object record)
        => record switch
        {
            ExperimentalRunStartedRecord value => JsonSerializer.Serialize(value, QueryRuntimeExperimentalJsonContext.Default.ExperimentalRunStartedRecord),
            ExperimentalRunCompletedRecord value => JsonSerializer.Serialize(value, QueryRuntimeExperimentalJsonContext.Default.ExperimentalRunCompletedRecord),
            ExperimentalRunFailedRecord value => JsonSerializer.Serialize(value, QueryRuntimeExperimentalJsonContext.Default.ExperimentalRunFailedRecord),
            ExperimentalPolicyDecisionTraceRecord value => JsonSerializer.Serialize(value, QueryRuntimeExperimentalJsonContext.Default.ExperimentalPolicyDecisionTraceRecord),
            TraceRecord value => JsonSerializer.Serialize(value, QueryRuntimeExperimentalJsonContext.Default.TraceRecord),
            _ => throw new InvalidOperationException($"Unsupported trace record type: {record.GetType().Name}")
        };

    private object MaterializeLargePayloads(object record)
    {
        if (record is not TraceRecord traceRecord)
        {
            return record;
        }

        MaterializeLargeText(traceRecord.Data, "AssistantText");
        MaterializeLargeText(traceRecord.Data, "Result");
        return traceRecord;
    }

    private void MaterializeLargeText(JsonObject data, string propertyName)
    {
        if (!data.TryGetPropertyValue(propertyName, out var node) ||
            node is not JsonValue value ||
            !value.TryGetValue<string>(out var text) ||
            text.Length <= InlineTextLimit)
        {
            return;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var blobRelativePath = Path.Combine("blobs", "sha256", digest[..2], digest);
        var blobPath = Path.Combine(Path.GetDirectoryName(TraceFilePath)!, blobRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
        if (!File.Exists(blobPath))
        {
            File.WriteAllBytes(blobPath, bytes);
        }

        data[propertyName] = null;
        data[$"{propertyName}Blob"] = new JsonObject
        {
            ["Algorithm"] = "sha256",
            ["Digest"] = digest,
            ["SizeBytes"] = bytes.Length,
            ["Path"] = blobRelativePath.Replace('\\', '/')
        };
    }
}

public sealed record ExperimentalRunStartedRecord(
    string Type,
    int SchemaVersion,
    string RunId,
    string SessionId,
    string? WorkspacePath,
    string Prompt,
    DateTimeOffset Timestamp)
{
    public static ExperimentalRunStartedRecord Create(
        string runId,
        string sessionId,
        string? workspacePath,
        string prompt)
        => new(
            "run.started",
            QueryRuntimeTraceSchema.CurrentVersion,
            runId,
            sessionId,
            workspacePath,
            prompt,
            DateTimeOffset.UtcNow);
}

public sealed record ExperimentalRunCompletedRecord(
    string Type,
    string RunId,
    string SessionId,
    string TerminationReason,
    int TotalRounds,
    int TotalToolCalls,
    long TotalDurationMs,
    string? TerminalDetailCode,
    int ZeroToolCallRounds,
    int ContinuationCount,
    int WriteToolCalls,
    string? LastFunctionCall,
    string? RequiredToolName,
    bool RequiredToolSatisfied,
    IReadOnlyList<string> ExecutedToolNames,
    IReadOnlyList<string> SuccessfulToolNames,
    DateTimeOffset Timestamp)
{
    public static ExperimentalRunCompletedRecord Create(string runId, string sessionId, EngineQueryRuntimeResult result)
        => new(
            "run.completed",
            runId,
            sessionId,
            result.TerminationReason.ToString(),
            result.TotalRounds,
            result.TotalToolCalls,
            result.TotalDurationMs,
            result.TerminalDetailCode,
            result.ZeroToolCallRounds,
            result.ContinuationCount,
            result.WriteToolCalls,
            result.LastFunctionCall,
            result.RequiredToolName,
            result.RequiredToolSatisfied,
            result.ExecutedToolNames,
            result.SuccessfulToolNames,
            DateTimeOffset.UtcNow);
}

public sealed record ExperimentalRunFailedRecord(
    string Type,
    string RunId,
    string SessionId,
    string ErrorType,
    string Message,
    DateTimeOffset Timestamp)
{
    public static ExperimentalRunFailedRecord Create(string runId, string sessionId, Exception exception)
        => new("run.failed", runId, sessionId, exception.GetType().Name, exception.Message, DateTimeOffset.UtcNow);
}

public sealed record ExperimentalPolicyDecisionTraceRecord(
    string Type,
    string Profile,
    string ToolName,
    IReadOnlySet<string> Capabilities,
    IReadOnlyList<string> Command,
    string Network,
    string Mount,
    string Decision,
    bool Allowed,
    string Reason,
    DateTimeOffset Timestamp)
{
    public static ExperimentalPolicyDecisionTraceRecord Create(QueryRuntimePolicyDecisionRecord decision)
        => new(
            "policy.decision",
            decision.Profile,
            decision.ToolName,
            decision.Capabilities,
            decision.Command,
            decision.Network,
            decision.Mount,
            decision.Decision,
            decision.Allowed,
            decision.Reason,
            decision.Timestamp);
}

public sealed record ExperimentalRunManifest(
    int SchemaVersion,
    string Type,
    string RunId,
    string SessionId,
    string? WorkspacePath,
    string TraceFilePath,
    string RunDirectory,
    string ToolProfile,
    string Status,
    string? TerminationReason,
    int TotalRounds,
    int TotalToolCalls,
    long TotalDurationMs,
    string? TerminalDetailCode,
    int ZeroToolCallRounds,
    int ContinuationCount,
    int WriteToolCalls,
    string? LastFunctionCall,
    string? RequiredToolName,
    bool RequiredToolSatisfied,
    DateTimeOffset Timestamp)
{
    public static ExperimentalRunManifest Completed(
        string runId,
        string sessionId,
        string? workspacePath,
        string traceFilePath,
        string toolProfile,
        EngineQueryRuntimeResult result)
        => new(
            QueryRuntimeTraceSchema.CurrentVersion,
            "qre.run.manifest",
            runId,
            sessionId,
            workspacePath,
            traceFilePath,
            Path.GetDirectoryName(traceFilePath)!,
            toolProfile,
            "completed",
            result.TerminationReason.ToString(),
            result.TotalRounds,
            result.TotalToolCalls,
            result.TotalDurationMs,
            result.TerminalDetailCode,
            result.ZeroToolCallRounds,
            result.ContinuationCount,
            result.WriteToolCalls,
            result.LastFunctionCall,
            result.RequiredToolName,
            result.RequiredToolSatisfied,
            DateTimeOffset.UtcNow);

    public static ExperimentalRunManifest Failed(
        string runId,
        string sessionId,
        string? workspacePath,
        string traceFilePath,
        string toolProfile,
        Exception exception)
        => new(
            QueryRuntimeTraceSchema.CurrentVersion,
            "qre.run.manifest",
            runId,
            sessionId,
            workspacePath,
            traceFilePath,
            Path.GetDirectoryName(traceFilePath)!,
            toolProfile,
            "failed",
            exception.GetType().Name,
            0,
            0,
            0,
            null,
            0,
            0,
            0,
            null,
            null,
            false,
            DateTimeOffset.UtcNow);
}

internal sealed record TraceRecord(
    string Type,
    long Seq,
    string RuntimeEventType,
    string QueryId,
    string SessionId,
    string EntryPoint,
    DateTimeOffset Timestamp,
    JsonObject Data)
{
    public static TraceRecord FromRuntimeEvent(QueryRuntimeEvent runtimeEvent)
        => new(
            MapType(runtimeEvent),
            runtimeEvent.Seq,
            runtimeEvent.GetType().Name,
            runtimeEvent.QueryId.ToString("N"),
            runtimeEvent.SessionId,
            "qre",
            runtimeEvent.Timestamp == default ? DateTimeOffset.UtcNow : runtimeEvent.Timestamp,
            ProjectData(runtimeEvent));

    private static string MapType(QueryRuntimeEvent runtimeEvent)
        => runtimeEvent switch
        {
            PromptAssemblySnapshotEvent => "model.request",
            ModelResponseSampledEvent => "model.response",
            ToolCallRequestedEvent => "tool.call.requested",
            ToolExecutionStartedEvent => "tool.execution.started",
            ToolExecutionCompletedEvent => "tool.execution.completed",
            PolicyInterventionDecisionEvent => "policy.intervention.decision",
            StopGateDecisionEvent => "stop.gate.decision",
            RoundStartedEvent => "round.started",
            RoundCompletedEvent => "round.completed",
            TerminatedEvent => "runtime.terminated",
            ErrorEvent => "runtime.error",
            _ => "runtime.event"
        };

    private static JsonObject ProjectData(QueryRuntimeEvent runtimeEvent)
        => runtimeEvent switch
        {
            PromptAssemblySnapshotEvent evt => Obj(
                ("Round", evt.Round),
                ("MessageCount", evt.MessageCount),
                ("ToolCallsAllowed", evt.ToolCallsAllowed),
                ("ToolNames", StringArray(evt.ToolNames)),
                ("RequiredToolName", evt.RequiredToolName),
                ("RequiredToolSatisfied", evt.RequiredToolSatisfied)),
            ModelResponseSampledEvent evt => Obj(
                ("Round", evt.Round),
                ("AssistantTextLength", evt.AssistantTextLength),
                ("StructuredToolCallCount", evt.StructuredToolCallCount),
                ("AssistantText", evt.AssistantText),
                ("ToolCalls", ToolCallArray(evt.ToolCalls))),
            ToolCallRequestedEvent evt => Obj(
                ("Round", evt.Round),
                ("ToolName", evt.ToolName),
                ("CallId", evt.CallId),
                ("ArgumentHash", ComputeArgumentHash(evt.Arguments)),
                ("Arguments", DictionaryObject(evt.Arguments))),
            ToolExecutionStartedEvent evt => Obj(
                ("Round", evt.Round),
                ("ToolName", evt.ToolName),
                ("CallId", evt.CallId)),
            ToolExecutionCompletedEvent evt => Obj(
                ("Round", evt.Round),
                ("ToolName", evt.ToolName),
                ("CallId", evt.CallId),
                ("Success", evt.Success),
                ("ResultLength", evt.ResultLength),
                ("Result", evt.Result)),
            PolicyInterventionDecisionEvent evt => Obj(
                ("Round", evt.Round),
                ("ToolName", evt.ToolName),
                ("CallId", evt.CallId),
                ("Decision", evt.Decision),
                ("Reason", evt.Reason),
                ("DetailCode", evt.DetailCode),
                ("Feedback", evt.Feedback)),
            StopGateDecisionEvent evt => Obj(
                ("Round", evt.Round),
                ("Decision", evt.Decision),
                ("RequiredToolName", evt.RequiredToolName),
                ("Reason", evt.Reason),
                ("DetailCode", evt.DetailCode),
                ("Feedback", evt.Feedback),
                ("ContinuationCount", evt.ContinuationCount)),
            RoundStartedEvent evt => Obj(("Round", evt.Round), ("MaxRounds", evt.MaxRounds)),
            RoundCompletedEvent evt => Obj(
                ("Round", evt.Round),
                ("ToolCallCount", evt.ToolCallCount),
                ("HasText", evt.HasText),
                ("TextLength", evt.TextLength),
                ("ContinueReason", evt.ContinueReason)),
            TerminatedEvent evt => Obj(
                ("Reason", evt.Reason.ToString()),
                ("TotalRounds", evt.TotalRounds),
                ("TotalToolCalls", evt.TotalToolCalls),
                ("TotalDurationMs", evt.TotalDurationMs),
                ("DetailCode", evt.DetailCode),
                ("ZeroToolCallRounds", evt.ZeroToolCallRounds),
                ("ContinuationCount", evt.ContinuationCount),
                ("WriteToolCalls", evt.WriteToolCalls),
                ("LastFunctionCall", evt.LastFunctionCall),
                ("RequiredToolName", evt.RequiredToolName),
                ("RequiredToolSatisfied", evt.RequiredToolSatisfied)),
            ErrorEvent evt => Obj(
                ("ErrorType", evt.ErrorType),
                ("Message", evt.Message),
                ("ExceptionType", evt.Exception?.GetType().Name)),
            _ => []
        };

    private static JsonObject Obj(params (string Name, object? Value)[] properties)
    {
        var obj = new JsonObject();
        foreach (var (name, value) in properties)
        {
            obj[name] = ToJsonNode(value);
        }

        return obj;
    }

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add((JsonNode?)JsonValue.Create(value));
        }

        return array;
    }

    private static JsonObject DictionaryObject(IReadOnlyDictionary<string, object?> values)
    {
        var obj = new JsonObject();
        foreach (var pair in values)
        {
            obj[pair.Key] = ToJsonNode(pair.Value);
        }

        return obj;
    }

    private static JsonArray ToolCallArray(IEnumerable<QueryRuntimeFunctionCallSnapshot> calls)
    {
        var array = new JsonArray();
        foreach (var call in calls)
        {
            array.Add((JsonNode?)Obj(
                ("CallId", call.CallId),
                ("Name", call.Name),
                ("ArgumentHash", ComputeArgumentHash(call.Arguments)),
                ("Arguments", DictionaryObject(call.Arguments))));
        }

        return array;
    }

    private static string ComputeArgumentHash(IReadOnlyDictionary<string, object?> arguments)
        => QreArgumentHash.Compute(arguments);

    private static JsonNode? ToJsonNode(object? value)
        => value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            string text => JsonValue.Create(text),
            bool boolean => JsonValue.Create(boolean),
            int number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            float number => JsonValue.Create(number),
            decimal number => JsonValue.Create(number),
            IEnumerable<string> strings => StringArray(strings),
            IReadOnlyDictionary<string, object?> dictionary => DictionaryObject(dictionary),
            _ => JsonValue.Create(value.ToString())
        };
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false)]
[JsonSerializable(typeof(ExperimentalRunStartedRecord))]
[JsonSerializable(typeof(ExperimentalRunCompletedRecord))]
[JsonSerializable(typeof(ExperimentalRunFailedRecord))]
[JsonSerializable(typeof(ExperimentalPolicyDecisionTraceRecord))]
[JsonSerializable(typeof(ExperimentalRunManifest))]
[JsonSerializable(typeof(TraceRecord))]
internal sealed partial class QueryRuntimeExperimentalJsonContext : JsonSerializerContext;
