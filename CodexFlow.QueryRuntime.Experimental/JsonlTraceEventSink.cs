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
    private readonly QueryRuntimeTraceOptions _traceOptions;

    private JsonlTraceEventSink(string traceFilePath, QueryRuntimeTraceOptions traceOptions)
    {
        TraceFilePath = traceFilePath;
        _traceOptions = traceOptions;
        var runDirectory = Path.GetDirectoryName(traceFilePath)!;
        if (traceOptions.DataMode == QueryRuntimeTraceDataMode.PrivateDiagnostic)
        {
            TraceStorageSecurity.CreatePrivateDirectory(runDirectory);
        }
        else
        {
            Directory.CreateDirectory(runDirectory);
        }

        _writer = new StreamWriter(new FileStream(traceFilePath, FileMode.Create, FileAccess.Write, FileShare.Read));
        if (traceOptions.DataMode == QueryRuntimeTraceDataMode.PrivateDiagnostic)
        {
            TraceStorageSecurity.RestrictPrivateFile(traceFilePath);
        }
    }

    public string TraceFilePath { get; }

    public static QueryRuntimeTraceRunLocation PrepareAuxiliaryRunLocation(
        string traceRoot,
        string requestedRunId,
        QueryRuntimeTraceOptions traceOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedRunId);
        ArgumentNullException.ThrowIfNull(traceOptions);
        traceOptions.Validate();

        var persistedRunId = traceOptions.DataMode switch
        {
            QueryRuntimeTraceDataMode.PublicRedacted => $"public-{Guid.NewGuid():N}",
            QueryRuntimeTraceDataMode.PrivateDiagnostic => $"private-{Guid.NewGuid():N}",
            _ => requestedRunId
        };
        var normalizedTraceRoot = Path.GetFullPath(traceRoot);
        var runsRoot = QueryRuntimePathSafety.ResolveUnderRoot(normalizedTraceRoot, "runs");
        if (traceOptions.DataMode == QueryRuntimeTraceDataMode.PrivateDiagnostic)
        {
            var privateRoot = QueryRuntimePathSafety.ResolveUnderRoot(normalizedTraceRoot, "private");
            TraceStorageSecurity.PreparePrivateRoot(privateRoot, traceOptions.PrivateDiagnosticRetention);
            runsRoot = QueryRuntimePathSafety.ResolveUnderRoot(privateRoot, "runs");
        }

        var traceFilePath = QueryRuntimePathSafety.ResolveUnderRoot(
            runsRoot,
            Path.Combine(persistedRunId, "events.jsonl"));
        var runDirectory = Path.GetDirectoryName(traceFilePath)!;
        if (traceOptions.DataMode == QueryRuntimeTraceDataMode.PrivateDiagnostic)
        {
            TraceStorageSecurity.CreatePrivateDirectory(runDirectory);
        }
        else
        {
            Directory.CreateDirectory(runDirectory);
        }

        return new QueryRuntimeTraceRunLocation(persistedRunId, runDirectory, traceFilePath);
    }

    public static void ApplyAuxiliaryArtifactSecurity(
        string path,
        QueryRuntimeTraceOptions traceOptions,
        bool isDirectory = false)
    {
        if (traceOptions.DataMode != QueryRuntimeTraceDataMode.PrivateDiagnostic)
        {
            return;
        }

        if (isDirectory)
        {
            TraceStorageSecurity.CreatePrivateDirectory(path);
        }
        else
        {
            TraceStorageSecurity.RestrictPrivateFile(path);
        }
    }

    public static async Task<JsonlTraceEventSink> CreateAsync(
        string traceFilePath,
        ExperimentalRunStartedRecord started,
        QueryRuntimeTraceOptions traceOptions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(traceOptions);
        var sink = new JsonlTraceEventSink(traceFilePath, traceOptions);
        await sink.WriteRecordAsync(started, ct).ConfigureAwait(false);
        return sink;
    }

    public bool IsEnabled(QueryRuntimeEventType eventType) => true;

    public async ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent)
    {
        await WriteRecordAsync(
            TraceRecord.FromRuntimeEvent(runtimeEvent, _traceOptions),
            CancellationToken.None).ConfigureAwait(false);
    }

    public Task WriteCompletedAsync(ExperimentalRunCompletedRecord completed, CancellationToken ct = default)
        => WriteRecordAsync(
            _traceOptions.DataMode == QueryRuntimeTraceDataMode.PublicRedacted
                ? completed with
                {
                    SessionId = "[redacted]",
                    TerminalDetailCode = null,
                    LastFunctionCall = null,
                    RequiredToolName = null,
                    ExecutedToolNames = [],
                    SuccessfulToolNames = []
                }
                : completed,
            ct);

    public Task WriteFailedAsync(ExperimentalRunFailedRecord failed, CancellationToken ct = default)
        => WriteRecordAsync(
            _traceOptions.DataMode == QueryRuntimeTraceDataMode.PublicRedacted
                ? failed with { SessionId = "[redacted]", ErrorType = "RuntimeError", Message = "[redacted]" }
                : failed,
            ct);

    public Task OnPolicyDecisionAsync(
        QueryRuntimePolicyDecisionRecord decision,
        CancellationToken ct = default)
        => WriteRecordAsync(ExperimentalPolicyDecisionTraceRecord.Create(decision, _traceOptions), ct);

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

        var isPrivate = string.Equals(
            manifest.DataMode,
            QueryRuntimeTraceDataMode.PrivateDiagnostic.ToString(),
            StringComparison.Ordinal);
        if (isPrivate)
        {
            TraceStorageSecurity.CreatePrivateDirectory(runDirectory);
            TraceStorageSecurity.CreatePrivateDirectory(Path.Combine(runDirectory, "artifacts"));
        }
        else
        {
            Directory.CreateDirectory(runDirectory);
            Directory.CreateDirectory(Path.Combine(runDirectory, "artifacts"));
        }
        var manifestPath = Path.Combine(runDirectory, "manifest.json");
        var runJsonPath = Path.Combine(runDirectory, "run.json");
        var json = JsonSerializer.Serialize(manifest, QueryRuntimeExperimentalJsonContext.Default.ExperimentalRunManifest);
        await File.WriteAllTextAsync(manifestPath, json + Environment.NewLine, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(runJsonPath, json + Environment.NewLine, ct).ConfigureAwait(false);
        if (isPrivate)
        {
            TraceStorageSecurity.RestrictPrivateFile(manifestPath);
            TraceStorageSecurity.RestrictPrivateFile(runJsonPath);
        }
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
        var blobDirectory = Path.GetDirectoryName(blobPath)!;
        if (_traceOptions.DataMode == QueryRuntimeTraceDataMode.PrivateDiagnostic)
        {
            TraceStorageSecurity.CreatePrivateDirectory(blobDirectory);
        }
        else
        {
            Directory.CreateDirectory(blobDirectory);
        }
        if (!File.Exists(blobPath))
        {
            File.WriteAllBytes(blobPath, bytes);
            if (_traceOptions.DataMode == QueryRuntimeTraceDataMode.PrivateDiagnostic)
            {
                TraceStorageSecurity.RestrictPrivateFile(blobPath);
            }
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

public sealed record QueryRuntimeTraceRunLocation(
    string PersistedRunId,
    string RunDirectory,
    string TraceFilePath);

public sealed record ExperimentalRunStartedRecord(
    string Type,
    int SchemaVersion,
    string RunId,
    string SessionId,
    string? WorkspacePath,
    string? Prompt,
    int PromptLength,
    string DataMode,
    string ReplayCapability,
    DateTimeOffset Timestamp)
{
    public static ExperimentalRunStartedRecord Create(
        string runId,
        string sessionId,
        string? workspacePath,
        string prompt,
        QueryRuntimeTraceOptions traceOptions)
    {
        var isPublic = traceOptions.DataMode == QueryRuntimeTraceDataMode.PublicRedacted;
        return new(
            "run.started",
            QueryRuntimeTraceSchema.CurrentVersion,
            runId,
            isPublic ? "[redacted]" : sessionId,
            isPublic ? null : workspacePath,
            isPublic ? null : prompt,
            prompt.Length,
            traceOptions.DataMode.ToString(),
            traceOptions.ReplayCapability.ToString(),
            DateTimeOffset.UtcNow);
    }
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
    public static ExperimentalPolicyDecisionTraceRecord Create(
        QueryRuntimePolicyDecisionRecord decision,
        QueryRuntimeTraceOptions traceOptions)
    {
        var isPublic = traceOptions.DataMode == QueryRuntimeTraceDataMode.PublicRedacted;
        return new(
            "policy.decision",
            isPublic ? "[redacted]" : decision.Profile,
            isPublic ? "[redacted]" : decision.ToolName,
            isPublic ? new HashSet<string>(StringComparer.Ordinal) : decision.Capabilities,
            isPublic ? [] : decision.Command,
            isPublic ? "[redacted]" : decision.Network,
            isPublic ? "[redacted]" : decision.Mount,
            isPublic ? (decision.Allowed ? "allowed" : "blocked") : decision.Decision,
            decision.Allowed,
            isPublic ? "[redacted]" : decision.Reason,
            decision.Timestamp);
    }
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
    string DataMode,
    string ReplayCapability,
    DateTimeOffset Timestamp)
{
    public static ExperimentalRunManifest Completed(
        string runId,
        string sessionId,
        string? workspacePath,
        string traceFilePath,
        string toolProfile,
        QueryRuntimeTraceOptions traceOptions,
        EngineQueryRuntimeResult result)
    {
        var isPublic = traceOptions.DataMode == QueryRuntimeTraceDataMode.PublicRedacted;
        return new(
            QueryRuntimeTraceSchema.CurrentVersion,
            "qre.run.manifest",
            runId,
            isPublic ? "[redacted]" : sessionId,
            isPublic ? null : workspacePath,
            isPublic ? "events.jsonl" : traceFilePath,
            isPublic ? "." : Path.GetDirectoryName(traceFilePath)!,
            isPublic ? "[redacted]" : toolProfile,
            "completed",
            result.TerminationReason.ToString(),
            result.TotalRounds,
            result.TotalToolCalls,
            result.TotalDurationMs,
            isPublic ? null : result.TerminalDetailCode,
            result.ZeroToolCallRounds,
            result.ContinuationCount,
            result.WriteToolCalls,
            isPublic ? null : result.LastFunctionCall,
            isPublic ? null : result.RequiredToolName,
            result.RequiredToolSatisfied,
            traceOptions.DataMode.ToString(),
            traceOptions.ReplayCapability.ToString(),
            DateTimeOffset.UtcNow);
    }

    public static ExperimentalRunManifest Failed(
        string runId,
        string sessionId,
        string? workspacePath,
        string traceFilePath,
        string toolProfile,
        QueryRuntimeTraceOptions traceOptions,
        Exception exception)
    {
        var isPublic = traceOptions.DataMode == QueryRuntimeTraceDataMode.PublicRedacted;
        return new(
            QueryRuntimeTraceSchema.CurrentVersion,
            "qre.run.manifest",
            runId,
            isPublic ? "[redacted]" : sessionId,
            isPublic ? null : workspacePath,
            isPublic ? "events.jsonl" : traceFilePath,
            isPublic ? "." : Path.GetDirectoryName(traceFilePath)!,
            isPublic ? "[redacted]" : toolProfile,
            "failed",
            isPublic ? "RuntimeError" : exception.GetType().Name,
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
            traceOptions.DataMode.ToString(),
            traceOptions.ReplayCapability.ToString(),
            DateTimeOffset.UtcNow);
    }
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
    public static TraceRecord FromRuntimeEvent(
        QueryRuntimeEvent runtimeEvent,
        QueryRuntimeTraceOptions traceOptions)
        => new(
            MapType(runtimeEvent),
            runtimeEvent.Seq,
            runtimeEvent.GetType().Name,
            traceOptions.DataMode == QueryRuntimeTraceDataMode.PublicRedacted
                ? "[redacted]"
                : runtimeEvent.QueryId.ToString("N"),
            traceOptions.DataMode == QueryRuntimeTraceDataMode.PublicRedacted
                ? "[redacted]"
                : runtimeEvent.SessionId,
            "qre",
            runtimeEvent.Timestamp == default ? DateTimeOffset.UtcNow : runtimeEvent.Timestamp,
            ProjectData(runtimeEvent, traceOptions));

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

    private static JsonObject ProjectData(
        QueryRuntimeEvent runtimeEvent,
        QueryRuntimeTraceOptions traceOptions)
    {
        var includeSensitiveData = traceOptions.DataMode != QueryRuntimeTraceDataMode.PublicRedacted;
        if (!includeSensitiveData)
        {
            return ProjectPublicData(runtimeEvent);
        }

        return runtimeEvent switch
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
                ("AssistantText", includeSensitiveData ? evt.AssistantText : null),
                ("ToolCalls", ToolCallArray(evt.ToolCalls, includeSensitiveData))),
            ToolCallRequestedEvent evt => Obj(
                ("Round", evt.Round),
                ("ToolName", evt.ToolName),
                ("CallId", evt.CallId),
                ("ArgumentHash", includeSensitiveData ? ComputeArgumentHash(evt.Arguments) : null),
                ("Arguments", includeSensitiveData ? DictionaryObject(evt.Arguments) : null)),
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
                ("Result", includeSensitiveData ? evt.Result : null)),
            PolicyInterventionDecisionEvent evt => Obj(
                ("Round", evt.Round),
                ("ToolName", evt.ToolName),
                ("CallId", evt.CallId),
                ("Decision", evt.Decision),
                ("Reason", includeSensitiveData ? evt.Reason : null),
                ("DetailCode", evt.DetailCode),
                ("Feedback", includeSensitiveData ? evt.Feedback : null)),
            StopGateDecisionEvent evt => Obj(
                ("Round", evt.Round),
                ("Decision", evt.Decision),
                ("RequiredToolName", evt.RequiredToolName),
                ("Reason", includeSensitiveData ? evt.Reason : null),
                ("DetailCode", evt.DetailCode),
                ("Feedback", includeSensitiveData ? evt.Feedback : null),
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
                ("Message", includeSensitiveData ? evt.Message : null),
                ("ExceptionType", evt.Exception?.GetType().Name)),
            _ => []
        };
    }

    // Public traces intentionally enumerate every retained field. Never derive
    // this projection by subtracting a deny-list from the private payload: new
    // event properties must remain private until explicitly reviewed here.
    private static JsonObject ProjectPublicData(QueryRuntimeEvent runtimeEvent)
        => runtimeEvent switch
        {
            PromptAssemblySnapshotEvent evt => Obj(
                ("Round", evt.Round),
                ("MessageCount", evt.MessageCount),
                ("ToolCallsAllowed", evt.ToolCallsAllowed),
                ("ToolCount", evt.ToolNames.Count),
                ("RequiredToolSatisfied", evt.RequiredToolSatisfied)),
            ModelResponseSampledEvent evt => Obj(
                ("Round", evt.Round),
                ("AssistantTextLength", evt.AssistantTextLength),
                ("StructuredToolCallCount", evt.StructuredToolCallCount)),
            ToolCallRequestedEvent evt => Obj(("Round", evt.Round)),
            ToolExecutionStartedEvent evt => Obj(("Round", evt.Round)),
            ToolExecutionCompletedEvent evt => Obj(
                ("Round", evt.Round),
                ("Success", evt.Success),
                ("ResultLength", evt.ResultLength)),
            PolicyInterventionDecisionEvent evt => Obj(("Round", evt.Round)),
            StopGateDecisionEvent evt => Obj(
                ("Round", evt.Round),
                ("ContinuationCount", evt.ContinuationCount)),
            RoundStartedEvent evt => Obj(("Round", evt.Round), ("MaxRounds", evt.MaxRounds)),
            RoundCompletedEvent evt => Obj(
                ("Round", evt.Round),
                ("ToolCallCount", evt.ToolCallCount),
                ("HasText", evt.HasText),
                ("TextLength", evt.TextLength)),
            TerminatedEvent evt => Obj(
                ("Reason", evt.Reason.ToString()),
                ("TotalRounds", evt.TotalRounds),
                ("TotalToolCalls", evt.TotalToolCalls),
                ("TotalDurationMs", evt.TotalDurationMs),
                ("ZeroToolCallRounds", evt.ZeroToolCallRounds),
                ("ContinuationCount", evt.ContinuationCount),
                ("WriteToolCalls", evt.WriteToolCalls),
                ("RequiredToolSatisfied", evt.RequiredToolSatisfied)),
            ErrorEvent => [],
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

    private static JsonArray ToolCallArray(
        IEnumerable<QueryRuntimeFunctionCallSnapshot> calls,
        bool includeSensitiveData)
    {
        var array = new JsonArray();
        foreach (var call in calls)
        {
            array.Add((JsonNode?)Obj(
                ("CallId", call.CallId),
                ("Name", call.Name),
                ("ArgumentHash", includeSensitiveData ? ComputeArgumentHash(call.Arguments) : null),
                ("Arguments", includeSensitiveData ? DictionaryObject(call.Arguments) : null)));
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
