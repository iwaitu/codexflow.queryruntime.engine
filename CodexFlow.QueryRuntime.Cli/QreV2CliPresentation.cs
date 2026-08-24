using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;

internal sealed record QreV2RunOutput(
    string Type,
    string FinalText,
    string SessionId,
    string TurnId,
    string Status,
    string TerminationReason,
    int TotalSteps,
    int TotalToolCalls,
    int ContinuationCount,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    string? ErrorCode)
{
    public string Profile { get; init; } = "none";

    public string Runner { get; init; } = "local";

    public IReadOnlyList<string> Tools { get; init; } = [];

    public long HistoryVersion { get; init; }

    public int ContextPreparations { get; init; }

    public int CompactionCount { get; init; }

    public int MaxPreparedContextTokens { get; init; }

    public string ContextEstimator { get; init; } = RuntimeTokenEstimator.Version;

    public bool DeferredToolSearch { get; init; }

    public int AuditSchemaVersion { get; init; }

    public int AuditEventCount { get; init; }

    public string AuditFilePath { get; init; } = string.Empty;

    public string AuditDataMode { get; init; } = string.Empty;

    public string AuditReplayCapability { get; init; } = string.Empty;
}

internal sealed record QreV2ReplayOutput(
    string Type,
    string Mode,
    int SchemaVersion,
    string DataMode,
    string ReplayCapability,
    int EventCount,
    bool ProviderCalls,
    bool ToolExecutions,
    string AuditFilePath)
{
    public string? FinalText { get; init; }

    public string? Status { get; init; }

    public string? TerminationReason { get; init; }

    public int? TotalSteps { get; init; }

    public int? TotalToolCalls { get; init; }

    public int? ContinuationCount { get; init; }

    public string? ReplayDigest { get; init; }
}

internal sealed class CliV2EventSink(bool writeText) : IRuntimeEventSink
{
    public ValueTask OnEventAsync(RuntimePresentationEvent runtimeEvent, CancellationToken ct)
    {
        if (writeText && runtimeEvent.Type == RuntimePresentationEventType.TextDelta &&
            !string.IsNullOrEmpty(runtimeEvent.Text))
        {
            Console.Write(runtimeEvent.Text);
        }
        return ValueTask.CompletedTask;
    }
}

internal static class QreV2CliPresentation
{
    public static void WriteReplayOutput(QreV2ReplayOutput output, bool json)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreV2ReplayOutput));
            return;
        }

        Console.WriteLine($"mode: {output.Mode}");
        Console.WriteLine($"schema_version: {output.SchemaVersion}");
        Console.WriteLine($"data_mode: {output.DataMode}");
        Console.WriteLine($"replay_capability: {output.ReplayCapability}");
        Console.WriteLine($"events: {output.EventCount}");
        Console.WriteLine($"provider_calls: {output.ProviderCalls.ToString().ToLowerInvariant()}");
        Console.WriteLine($"tool_executions: {output.ToolExecutions.ToString().ToLowerInvariant()}");
        Console.WriteLine($"audit: {output.AuditFilePath}");
        if (output.FinalText != null)
        {
            Console.WriteLine($"status: {output.Status}");
            Console.WriteLine($"termination: {output.TerminationReason}");
            Console.WriteLine($"replay_digest: {output.ReplayDigest}");
            Console.WriteLine();
            Console.WriteLine(output.FinalText);
        }
    }
}
