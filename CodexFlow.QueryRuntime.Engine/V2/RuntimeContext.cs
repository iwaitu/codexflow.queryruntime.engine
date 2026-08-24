using System.Buffers;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.QueryRuntime.Engine.V2;

public readonly record struct RuntimeHistoryMessageId(string Value);

public readonly record struct RuntimeHistoryItemId(string Value);

public sealed record RuntimeHistoryMessage(
    RuntimeHistoryMessageId Id,
    long CommittedVersion,
    RuntimeMessage Message,
    IReadOnlyList<RuntimeHistoryItemId> ItemIds);

public sealed record RuntimeHistorySnapshot(
    long Version,
    IReadOnlyList<RuntimeHistoryMessage> Messages,
    IReadOnlyDictionary<string, RuntimeHistoryBlob> Blobs);

public sealed record RuntimeHistoryBlob(
    string Digest,
    string MediaType,
    ReadOnlyMemory<byte> Data);

public enum RuntimeContextPartition
{
    Goal = 0,
    Constraints = 1,
    LatestUser = 2,
    ToolState = 3,
    RecentTrajectory = 4,
    Summary = 5
}

public sealed record RuntimeContextPartitionUsage(
    RuntimeContextPartition Partition,
    int BudgetTokens,
    int UsedTokens);

public enum RuntimeContextEventKind
{
    HistoryNormalized = 0,
    ContextPrepared = 1,
    ContextCompacted = 2,
    ItemReplaced = 3
}

/// <summary>
/// In-memory C5 audit candidate. C6 owns the durable/versioned audit envelope.
/// </summary>
public sealed record RuntimeContextEvent(
    RuntimeContextEventKind Kind,
    long HistoryVersion,
    string Code,
    IReadOnlyList<RuntimeHistoryItemId> ItemIds,
    string? ReplacementId = null,
    string? Detail = null);

public sealed record PreparedRuntimeContext(
    long HistoryVersion,
    IReadOnlyList<RuntimeMessage> Messages,
    IReadOnlyList<RuntimeHistoryItemId> IncludedItemIds,
    IReadOnlyList<RuntimeHistoryItemId> OmittedItemIds,
    IReadOnlyList<RuntimeHistoryItemId> ReplacedItemIds,
    IReadOnlyList<RuntimeContextPartitionUsage> Partitions,
    string EstimatorVersion,
    int EstimatedTokens,
    int ReservedToolTokens,
    bool Compacted,
    IReadOnlyList<RuntimeContextEvent> Events);

public sealed record RuntimeContextOptions
{
    public static RuntimeContextOptions Default { get; } = new();

    public int MaxContextTokens { get; init; } = 32_000;

    public int MaxItemTokens { get; init; } = 8_000;

    public int MaxToolResultTokens { get; init; } = 4_000;

    public int LargeToolResultTokens { get; init; } = 2_000;

    public int SummaryTokens { get; init; } = 2_000;

    public int RecentTrajectoryMessages { get; init; } = 12;

    public int MaxArtifactsPerResult { get; init; } = 16;

    public int MaxBlobBytes { get; init; } = 1_048_576;

    public int MaxTotalBlobBytes { get; init; } = 8_388_608;

    internal void Validate()
    {
        if (MaxContextTokens <= 0 || MaxItemTokens <= 0 || MaxToolResultTokens <= 0 ||
            LargeToolResultTokens <= 0 || SummaryTokens <= 0 || RecentTrajectoryMessages <= 0 ||
            MaxArtifactsPerResult <= 0 || MaxBlobBytes <= 0 || MaxTotalBlobBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RuntimeContextOptions), "C5 context limits must be positive.");
        }
        if (MaxItemTokens > MaxContextTokens || MaxToolResultTokens > MaxContextTokens ||
            LargeToolResultTokens > MaxToolResultTokens || SummaryTokens > MaxContextTokens)
        {
            throw new ArgumentException("C5 context limits are inconsistent.", nameof(RuntimeContextOptions));
        }
        if (MaxBlobBytes > MaxTotalBlobBytes)
        {
            throw new ArgumentException("A single C5 history blob cannot exceed the total blob budget.", nameof(RuntimeContextOptions));
        }
    }
}

public interface IRuntimeContextManager
{
    RuntimeContextOptions Options { get; }

    PreparedRuntimeContext Prepare(
        RuntimeHistorySnapshot history,
        string objective,
        string? requiredToolName,
        int reservedToolTokens = 0);
}

public interface IRuntimeContextEventSink
{
    ValueTask OnEventAsync(RuntimeContextEvent runtimeEvent, CancellationToken ct);
}

/// <summary>
/// Selects a per-Step model-visible subset from the frozen execution registry.
/// The execution pipeline remains the authority and rejects calls that were not
/// exposed in the prepared Step.
/// </summary>
public interface IRuntimeToolCatalogSelector
{
    IReadOnlyList<RuntimeToolDescriptor> SelectTools(
        PreparedRuntimeContext context,
        IReadOnlyList<RuntimeToolDescriptor> frozenCatalog,
        int stepIndex);

    void Observe(RuntimeToolCall call, RuntimeToolResult result);
}

/// <summary>
/// Canonical, bounded, in-memory history. Context preparation never mutates this
/// collection; normalization happens only at append boundaries.
/// </summary>
public sealed class RuntimeHistory
{
    private readonly RuntimeContextOptions _options;
    private readonly List<RuntimeHistoryMessage> _messages = [];
    private readonly List<RuntimeContextEvent> _pendingEvents = [];
    private readonly HashSet<string> _systemFragments = new(StringComparer.Ordinal);
    private readonly HashSet<string> _toolCalls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _toolResults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeHistoryBlob> _blobs = new(StringComparer.Ordinal);
    private int _blobBytes;
    private long _messageSequence;

    private RuntimeHistory(long version, RuntimeContextOptions options)
    {
        Version = version;
        _options = options;
    }

    public long Version { get; private set; }

    public static RuntimeHistory Create(
        IReadOnlyList<RuntimeMessage> initialMessages,
        long initialVersion,
        RuntimeContextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(initialMessages);
        if (initialVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialVersion));
        }
        var resolved = options ?? RuntimeContextOptions.Default;
        resolved.Validate();
        var history = new RuntimeHistory(initialVersion, resolved);
        history.AppendCore(initialMessages, initialVersion);
        return history;
    }

    public long AppendBatch(IReadOnlyList<RuntimeMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        Version = checked(Version + 1);
        AppendCore(messages, Version);
        return Version;
    }

    public RuntimeHistorySnapshot Snapshot()
        => new(
            Version,
            Array.AsReadOnly(_messages.Select(SnapshotMessage).ToArray()),
            new ReadOnlyDictionary<string, RuntimeHistoryBlob>(_blobs.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value with { Data = pair.Value.Data.ToArray() },
                StringComparer.Ordinal)));

    public IReadOnlyList<RuntimeMessage> ToMessages()
        => Array.AsReadOnly(_messages.Select(static entry => Snapshot(entry.Message)).ToArray());

    public IReadOnlyList<RuntimeContextEvent> DrainEvents()
    {
        var events = _pendingEvents.ToArray();
        _pendingEvents.Clear();
        return Array.AsReadOnly(events);
    }

    private void AppendCore(IReadOnlyList<RuntimeMessage> messages, long version)
    {
        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);
            var messageId = new RuntimeHistoryMessageId($"h{version}:m{_messageSequence++}");
            var normalized = Normalize(message, messageId, version);
            if (normalized == null)
            {
                continue;
            }
            var itemIds = normalized.Items
                .Select((_, index) => new RuntimeHistoryItemId($"{messageId.Value}:i{index}"))
                .ToArray();
            _messages.Add(new RuntimeHistoryMessage(
                messageId,
                version,
                normalized,
                Array.AsReadOnly(itemIds)));
        }
    }

    private RuntimeMessage? Normalize(
        RuntimeMessage message,
        RuntimeHistoryMessageId messageId,
        long version)
    {
        ArgumentNullException.ThrowIfNull(message.Items);
        var items = new List<RuntimeItem>();
        for (var sourceIndex = 0; sourceIndex < message.Items.Count; sourceIndex++)
        {
            var item = message.Items[sourceIndex];
            var sourceId = new RuntimeHistoryItemId($"{messageId.Value}:source:{sourceIndex}");
            switch (item)
            {
                case RuntimeTextItem text:
                {
                    var bounded = Bound(text.Text, _options.MaxItemTokens, out var truncated);
                    if (string.IsNullOrEmpty(bounded))
                    {
                        Record(version, "empty_text_omitted", sourceId);
                        continue;
                    }
                    if (message.Role == RuntimeMessageRole.System && !_systemFragments.Add(bounded))
                    {
                        Record(version, "duplicate_system_fragment_omitted", sourceId);
                        continue;
                    }
                    items.Add(new RuntimeTextItem(bounded));
                    if (truncated)
                    {
                        Record(version, "text_fragment_hard_cap", sourceId, detail: "Text was truncated at the C5 item hard cap.");
                    }
                    break;
                }
                case RuntimeReasoningItem reasoning:
                {
                    var bounded = Bound(reasoning.Text, _options.MaxItemTokens, out var truncated);
                    var protectedData = Bound(reasoning.ProtectedData, _options.MaxItemTokens, out var protectedTruncated);
                    if (string.IsNullOrEmpty(bounded) && string.IsNullOrEmpty(protectedData))
                    {
                        Record(version, "empty_reasoning_omitted", sourceId);
                        continue;
                    }
                    items.Add(new RuntimeReasoningItem(bounded, protectedData));
                    if (truncated || protectedTruncated)
                    {
                        Record(version, "reasoning_fragment_hard_cap", sourceId);
                    }
                    break;
                }
                case RuntimeToolCallItem toolCall:
                {
                    var id = toolCall.Call.InvocationId.Value;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(toolCall.Call.Name))
                    {
                        items.Add(new RuntimeTextItem("[invalid tool call omitted]"));
                        Record(version, "invalid_tool_call_normalized", sourceId);
                        continue;
                    }
                    if (!_toolCalls.Add(id))
                    {
                        items.Add(new RuntimeTextItem($"[duplicate tool call omitted: {Bound(id, 64, out _)}]"));
                        Record(version, "duplicate_tool_call_normalized", sourceId);
                        continue;
                    }
                    var arguments = toolCall.Call.Arguments.Clone();
                    if (RuntimeTokenEstimator.Estimate(arguments.GetRawText()) > _options.MaxItemTokens)
                    {
                        arguments = CreateTruncatedArguments(Digest(toolCall.Call.Arguments.GetRawText()));
                        Record(version, "tool_arguments_hard_cap", sourceId);
                    }
                    items.Add(new RuntimeToolCallItem(toolCall.Call with { Arguments = arguments }));
                    break;
                }
                case RuntimeToolResultItem toolResult:
                {
                    var id = toolResult.Result.InvocationId.Value;
                    if (string.IsNullOrWhiteSpace(id) || !_toolCalls.Contains(id))
                    {
                        items.Add(new RuntimeTextItem($"[orphan tool result omitted: {Bound(id, 64, out _)}]"));
                        Record(version, "orphan_tool_result_normalized", sourceId);
                        continue;
                    }
                    if (!_toolResults.Add(id))
                    {
                        items.Add(new RuntimeTextItem($"[duplicate tool result omitted: {Bound(id, 64, out _)}]"));
                        Record(version, "duplicate_tool_result_normalized", sourceId);
                        continue;
                    }
                    items.Add(new RuntimeToolResultItem(NormalizeToolResult(toolResult.Result, sourceId, version)));
                    break;
                }
                case RuntimeArtifactItem artifact:
                    items.Add(new RuntimeArtifactItem(artifact.Artifact with
                    {
                        Path = Bound(artifact.Artifact.Path, _options.MaxItemTokens, out _),
                        MediaType = Bound(artifact.Artifact.MediaType, 128, out _),
                        Digest = Bound(artifact.Artifact.Digest, 128, out _)
                    }));
                    break;
                case null:
                    items.Add(new RuntimeTextItem("[null runtime item omitted]"));
                    Record(version, "null_item_normalized", sourceId);
                    break;
                default:
                    items.Add(new RuntimeTextItem($"[unsupported runtime item omitted: {item.GetType().Name}]"));
                    Record(version, "unsupported_item_normalized", sourceId);
                    break;
            }
        }

        return items.Count == 0 ? null : new RuntimeMessage(message.Role, Array.AsReadOnly(items.ToArray()));
    }

    private RuntimeToolResult NormalizeToolResult(
        RuntimeToolResult result,
        RuntimeHistoryItemId sourceId,
        long version)
    {
        var original = result.Text;
        var originalTokens = RuntimeTokenEstimator.Estimate(original);
        var text = Bound(original, _options.MaxToolResultTokens, out var truncated);
        var artifacts = (result.Artifacts ?? [])
            .Take(_options.MaxArtifactsPerResult)
            .Select(static artifact => artifact with { })
            .ToList();
        if (originalTokens > _options.LargeToolResultTokens && !string.IsNullOrEmpty(original))
        {
            var digest = Digest(original);
            var bytes = Encoding.UTF8.GetBytes(original);
            var stored = TryStoreBlob(digest, bytes);
            if (stored)
            {
                artifacts.Add(new RuntimeArtifactReference(
                    $"runtime-history://sha256/{digest}",
                    "text/plain",
                    digest,
                    bytes.Length));
            }
            text = Bound(text, _options.LargeToolResultTokens, out _) +
                (stored
                    ? $"\n[large tool output replaced; artifact sha256:{digest}]"
                    : $"\n[large tool output omitted: blob budget exceeded; sha256:{digest}]");
            truncated = true;
            Record(
                version,
                stored ? "large_tool_result_replaced" : "large_tool_result_blob_budget_exhausted",
                sourceId,
                stored ? $"runtime-history://sha256/{digest}" : null);
        }
        else if (truncated)
        {
            Record(version, "tool_result_hard_cap", sourceId);
        }

        var details = result.Details == null
            ? null
            : result.Details with
            {
                StandardOutput = Bound(result.Details.StandardOutput, _options.MaxToolResultTokens, out var stdoutTruncated),
                StandardError = Bound(result.Details.StandardError, _options.MaxToolResultTokens, out var stderrTruncated),
                Truncated = result.Details.Truncated || truncated || stdoutTruncated || stderrTruncated,
                WorkspaceChangeEvidence = Bound(result.Details.WorkspaceChangeEvidence, 512, out _)
            };
        return result with
        {
            Text = text,
            Artifacts = Array.AsReadOnly(artifacts.ToArray()),
            Details = details
        };
    }

    private bool TryStoreBlob(string digest, byte[] bytes)
    {
        if (_blobs.ContainsKey(digest))
        {
            return true;
        }
        if (bytes.Length > _options.MaxBlobBytes || _blobBytes + bytes.Length > _options.MaxTotalBlobBytes)
        {
            return false;
        }
        _blobs.Add(digest, new RuntimeHistoryBlob(digest, "text/plain", bytes));
        _blobBytes = checked(_blobBytes + bytes.Length);
        return true;
    }

    private void Record(
        long version,
        string code,
        RuntimeHistoryItemId itemId,
        string? replacementId = null,
        string? detail = null)
        => _pendingEvents.Add(new RuntimeContextEvent(
            replacementId == null ? RuntimeContextEventKind.HistoryNormalized : RuntimeContextEventKind.ItemReplaced,
            version,
            code,
            Array.AsReadOnly([itemId]),
            replacementId,
            detail));

    private static RuntimeHistoryMessage SnapshotMessage(RuntimeHistoryMessage entry)
        => entry with
        {
            Message = Snapshot(entry.Message),
            ItemIds = Array.AsReadOnly(entry.ItemIds.ToArray())
        };

    internal static RuntimeMessage Snapshot(RuntimeMessage message)
        => message with
        {
            Items = Array.AsReadOnly(message.Items.Select(SnapshotItem).ToArray())
        };

    private static RuntimeItem SnapshotItem(RuntimeItem item)
        => item switch
        {
            RuntimeTextItem text => text,
            RuntimeReasoningItem reasoning => reasoning,
            RuntimeToolCallItem call => call with { Call = call.Call with { Arguments = call.Call.Arguments.Clone() } },
            RuntimeToolResultItem result => result with
            {
                Result = result.Result with
                {
                    Artifacts = result.Result.Artifacts == null
                        ? null
                        : Array.AsReadOnly(result.Result.Artifacts.Select(static artifact => artifact with { }).ToArray())
                }
            },
            RuntimeArtifactItem artifact => artifact with { Artifact = artifact.Artifact with { } },
            _ => throw new InvalidOperationException("RuntimeHistory contains an unsupported item after normalization.")
        };

    internal static string Bound(string? value, int maxTokens, out bool truncated)
    {
        if (string.IsNullOrEmpty(value))
        {
            truncated = false;
            return value ?? string.Empty;
        }
        if (RuntimeTokenEstimator.Estimate(value) <= maxTokens)
        {
            truncated = false;
            return value;
        }
        var maxBytes = checked(maxTokens * 4);
        var builder = new StringBuilder(Math.Min(value.Length, maxBytes));
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var bytes = rune.Utf8SequenceLength;
            if (used + bytes > maxBytes)
            {
                break;
            }
            builder.Append(rune.ToString());
            used += bytes;
        }
        truncated = true;
        return builder.ToString();
    }

    private static string Digest(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static JsonElement CreateTruncatedArguments(string digest)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("_qre_context_truncated", true);
            writer.WriteString("_qre_original_sha256", digest);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}

public static class RuntimeTokenEstimator
{
    public const string Version = "utf8-bytes-div4-v2";

    public static int Estimate(string? value)
        => string.IsNullOrEmpty(value) ? 0 : Math.Max(1, (Encoding.UTF8.GetByteCount(value) + 3) / 4);

    public static int Estimate(RuntimeItem item)
        => item switch
        {
            RuntimeTextItem text => Estimate(text.Text),
            RuntimeReasoningItem reasoning => Estimate(reasoning.Text) + Estimate(reasoning.ProtectedData),
            RuntimeToolCallItem call => Estimate(call.Call.Name) + Estimate(call.Call.Arguments.GetRawText()) + 8,
            RuntimeToolResultItem result => Estimate(result.Result.Text) +
                (result.Result.Artifacts?.Count ?? 0) * 16 + 8,
            RuntimeArtifactItem artifact => Estimate(artifact.Artifact.Path) + 8,
            _ => 1
        };

    public static int Estimate(RuntimeMessage message)
        => 4 + message.Items.Sum(Estimate);

    public static int Estimate(RuntimeToolDescriptor tool)
        => 16 +
           Estimate(tool.CanonicalName) +
           Estimate(tool.Version) +
           Estimate(tool.Description) +
           Estimate(tool.InputSchema.GetRawText());
}

public sealed class RuntimeContextManager : IRuntimeContextManager
{
    public RuntimeContextManager(RuntimeContextOptions? options = null)
    {
        Options = options ?? RuntimeContextOptions.Default;
        Options.Validate();
    }

    public RuntimeContextOptions Options { get; }

    public PreparedRuntimeContext Prepare(
        RuntimeHistorySnapshot history,
        string objective,
        string? requiredToolName,
        int reservedToolTokens = 0)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);
        if (reservedToolTokens < 0 || reservedToolTokens >= Options.MaxContextTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reservedToolTokens),
                $"C5 tool schemas require {reservedToolTokens} estimated tokens, leaving no message budget within {Options.MaxContextTokens} tokens.");
        }
        var messageBudget = Options.MaxContextTokens - reservedToolTokens;
        var allTokens = history.Messages.Sum(static entry => RuntimeTokenEstimator.Estimate(entry.Message));
        if (allTokens <= messageBudget)
        {
            var included = history.Messages.SelectMany(static entry => entry.ItemIds).ToArray();
            var prepared = history.Messages.Select(static entry => RuntimeHistory.Snapshot(entry.Message)).ToArray();
            var partitions = BuildPartitionUsage(history.Messages, messageBudget);
            var contextEvent = new RuntimeContextEvent(
                RuntimeContextEventKind.ContextPrepared,
                history.Version,
                "context_prepared_full",
                Array.AsReadOnly(included));
            return new PreparedRuntimeContext(
                history.Version,
                Array.AsReadOnly(prepared),
                Array.AsReadOnly(included),
                Array.Empty<RuntimeHistoryItemId>(),
                Array.Empty<RuntimeHistoryItemId>(),
                partitions,
                RuntimeTokenEstimator.Version,
                allTokens + reservedToolTokens,
                reservedToolTokens,
                false,
                Array.AsReadOnly([contextEvent]));
        }

        var selected = Select(history, requiredToolName, messageBudget);
        var selectedIds = selected.Select(static entry => entry.Id).ToHashSet();
        var omitted = history.Messages.Where(entry => !selectedIds.Contains(entry.Id)).ToArray();
        var remaining = Math.Max(0, messageBudget - selected.Sum(static entry => RuntimeTokenEstimator.Estimate(entry.Message)));
        var summaryTokenBudget = Math.Max(0, Math.Min(Options.SummaryTokens, remaining - 4));
        var summary = summaryTokenBudget == 0
            ? string.Empty
            : BuildSummary(omitted, objective, requiredToolName, summaryTokenBudget);
        var messages = new List<RuntimeMessage>();
        var replacementIds = omitted.SelectMany(static entry => entry.ItemIds).ToArray();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            messages.Add(new RuntimeMessage(RuntimeMessageRole.System, [new RuntimeTextItem(summary)]));
        }
        messages.AddRange(selected.Select(static entry => RuntimeHistory.Snapshot(entry.Message)));

        var includedIds = selected.SelectMany(static entry => entry.ItemIds).ToArray();
        var estimated = messages.Sum(RuntimeTokenEstimator.Estimate);
        if (estimated > messageBudget)
        {
            throw new InvalidOperationException("The deterministic C5 context projection exceeded its hard budget.");
        }
        var events = new RuntimeContextEvent[]
        {
            new(
                RuntimeContextEventKind.ContextCompacted,
                history.Version,
                "deterministic_local_compaction",
                Array.AsReadOnly(replacementIds),
                "context:local-summary",
                "Canonical RuntimeHistory was preserved; only the model projection was compacted."),
            new(
                RuntimeContextEventKind.ContextPrepared,
                history.Version,
                "context_prepared_compacted",
                Array.AsReadOnly(includedIds))
        };
        return new PreparedRuntimeContext(
            history.Version,
            Array.AsReadOnly(messages.ToArray()),
            Array.AsReadOnly(includedIds),
            Array.AsReadOnly(replacementIds),
            Array.AsReadOnly(replacementIds),
            BuildPartitionUsage(selected, messageBudget),
            RuntimeTokenEstimator.Version,
            estimated + reservedToolTokens,
            reservedToolTokens,
            true,
            Array.AsReadOnly(events));
    }

    private IReadOnlyList<RuntimeHistoryMessage> Select(
        RuntimeHistorySnapshot history,
        string? requiredToolName,
        int messageBudget)
    {
        var selected = new HashSet<RuntimeHistoryMessageId>();
        var calls = new Dictionary<string, RuntimeHistoryMessage>(StringComparer.Ordinal);
        var results = new Dictionary<string, RuntimeHistoryMessage>(StringComparer.Ordinal);
        foreach (var entry in history.Messages)
        {
            foreach (var call in entry.Message.Items.OfType<RuntimeToolCallItem>())
            {
                calls[call.Call.InvocationId.Value] = entry;
            }
            foreach (var result in entry.Message.Items.OfType<RuntimeToolResultItem>())
            {
                results[result.Result.InvocationId.Value] = entry;
            }
        }

        var groups = new List<IReadOnlyList<RuntimeHistoryMessage>>();
        var latestUser = history.Messages.LastOrDefault(static entry => entry.Message.Role == RuntimeMessageRole.User);
        if (latestUser != null)
        {
            groups.Add([latestUser]);
        }
        foreach (var system in history.Messages.Where(static entry => entry.Message.Role == RuntimeMessageRole.System))
        {
            groups.Add([system]);
        }
        foreach (var (id, callEntry) in calls.Where(pair => !results.ContainsKey(pair.Key)))
        {
            groups.Add([callEntry]);
        }
        foreach (var entry in history.Messages.TakeLast(Options.RecentTrajectoryMessages).Reverse())
        {
            var group = new Dictionary<RuntimeHistoryMessageId, RuntimeHistoryMessage> { [entry.Id] = entry };
            foreach (var call in entry.Message.Items.OfType<RuntimeToolCallItem>())
            {
                if (results.TryGetValue(call.Call.InvocationId.Value, out var resultEntry))
                {
                    group[resultEntry.Id] = resultEntry;
                }
            }
            foreach (var result in entry.Message.Items.OfType<RuntimeToolResultItem>())
            {
                if (calls.TryGetValue(result.Result.InvocationId.Value, out var callEntry))
                {
                    group[callEntry.Id] = callEntry;
                }
            }
            groups.Add(group.Values.ToArray());
        }

        var reserve = Math.Min(Options.SummaryTokens, messageBudget / 4);
        var used = 0;
        foreach (var group in groups)
        {
            var newEntries = group.Where(entry => !selected.Contains(entry.Id)).ToArray();
            var cost = newEntries.Sum(static entry => RuntimeTokenEstimator.Estimate(entry.Message));
            if (newEntries.Length == 0 || used + cost > messageBudget - reserve)
            {
                continue;
            }
            foreach (var entry in newEntries)
            {
                selected.Add(entry.Id);
            }
            used += cost;
        }

        return Array.AsReadOnly(history.Messages.Where(entry => selected.Contains(entry.Id)).ToArray());
    }

    private static string BuildSummary(
        IReadOnlyList<RuntimeHistoryMessage> omitted,
        string objective,
        string? requiredToolName,
        int maxTokens)
    {
        var progress = new List<string>();
        var findings = new List<string>();
        var failures = new List<string>();
        var outstanding = new List<string>();
        foreach (var entry in omitted)
        {
            foreach (var item in entry.Message.Items)
            {
                switch (item)
                {
                    case RuntimeTextItem text when entry.Message.Role == RuntimeMessageRole.Assistant:
                        progress.Add(Snippet(text.Text));
                        break;
                    case RuntimeToolResultItem result when result.Result.Success:
                        findings.Add($"{result.Result.InvocationId.Value}: {Snippet(result.Result.Text)}");
                        break;
                    case RuntimeToolResultItem result:
                        failures.Add($"{result.Result.InvocationId.Value}: {Snippet(result.Result.Error?.Message ?? result.Result.Text)}");
                        break;
                    case RuntimeToolCallItem call:
                        outstanding.Add($"{call.Call.Name} ({call.Call.InvocationId.Value})");
                        break;
                }
            }
        }

        var builder = new StringBuilder();
        builder.AppendLine("[C5 deterministic local summary; non-authoritative model context]");
        AppendSection(builder, "Goal", [Snippet(objective)]);
        AppendSection(builder, "Constraints", requiredToolName == null ? [] : [$"Required tool: {requiredToolName}"]);
        AppendSection(builder, "Progress", progress);
        AppendSection(builder, "ImportantFindings", findings);
        AppendSection(builder, "Decisions", []);
        AppendSection(builder, "FailedAttempts", failures);
        AppendSection(builder, "OutstandingTasks", outstanding);
        return RuntimeHistory.Bound(builder.ToString(), maxTokens, out _);
    }

    private static void AppendSection(StringBuilder builder, string name, IReadOnlyList<string> values)
    {
        builder.AppendLine(name + ":");
        if (values.Count == 0)
        {
            builder.AppendLine("- none recorded");
            return;
        }
        foreach (var value in values.Where(static value => !string.IsNullOrWhiteSpace(value)).Take(12))
        {
            builder.Append("- ").AppendLine(value);
        }
    }

    private static string Snippet(string? value)
    {
        var normalized = string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return RuntimeHistory.Bound(normalized, 96, out _);
    }

    private static IReadOnlyList<RuntimeContextPartitionUsage> BuildPartitionUsage(
        IReadOnlyList<RuntimeHistoryMessage> messages,
        int totalBudget)
    {
        var usage = Enum.GetValues<RuntimeContextPartition>()
            .ToDictionary(static partition => partition, static _ => 0);
        foreach (var entry in messages)
        {
            var partition = entry.Message.Role switch
            {
                RuntimeMessageRole.System => RuntimeContextPartition.Constraints,
                RuntimeMessageRole.User => RuntimeContextPartition.LatestUser,
                RuntimeMessageRole.Tool => RuntimeContextPartition.ToolState,
                _ => RuntimeContextPartition.RecentTrajectory
            };
            usage[partition] += RuntimeTokenEstimator.Estimate(entry.Message);
        }
        return Array.AsReadOnly(new[]
        {
            new RuntimeContextPartitionUsage(RuntimeContextPartition.Goal, totalBudget / 10, usage[RuntimeContextPartition.Goal]),
            new RuntimeContextPartitionUsage(RuntimeContextPartition.Constraints, totalBudget / 5, usage[RuntimeContextPartition.Constraints]),
            new RuntimeContextPartitionUsage(RuntimeContextPartition.LatestUser, totalBudget * 15 / 100, usage[RuntimeContextPartition.LatestUser]),
            new RuntimeContextPartitionUsage(RuntimeContextPartition.ToolState, totalBudget / 4, usage[RuntimeContextPartition.ToolState]),
            new RuntimeContextPartitionUsage(RuntimeContextPartition.RecentTrajectory, totalBudget * 3 / 10, usage[RuntimeContextPartition.RecentTrajectory]),
            new RuntimeContextPartitionUsage(RuntimeContextPartition.Summary, totalBudget / 10, usage[RuntimeContextPartition.Summary])
        });
    }
}
