using System.Text.Json.Serialization;

namespace CodexFlow.QueryRuntime.Protocol;

public static class QueryRuntimeProtocolSchema
{
    public const int CurrentVersion = 1;
}

public sealed record RuntimeProtocolFixture(
    int SchemaVersion,
    IReadOnlyList<RuntimeMessage> Messages,
    IReadOnlyList<RuntimeModelStreamEvent> Events);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(RuntimeProtocolFixture))]
[JsonSerializable(typeof(RuntimeModelRequest))]
[JsonSerializable(typeof(RuntimeMessage))]
[JsonSerializable(typeof(RuntimeItem))]
[JsonSerializable(typeof(RuntimeTextItem))]
[JsonSerializable(typeof(RuntimeReasoningItem))]
[JsonSerializable(typeof(RuntimeToolCallItem))]
[JsonSerializable(typeof(RuntimeToolResultItem))]
[JsonSerializable(typeof(RuntimeArtifactItem))]
[JsonSerializable(typeof(RuntimeModelStreamEvent))]
[JsonSerializable(typeof(RuntimeTextDeltaEvent))]
[JsonSerializable(typeof(RuntimeReasoningDeltaEvent))]
[JsonSerializable(typeof(RuntimeToolCallEvent))]
[JsonSerializable(typeof(RuntimeUsageEvent))]
[JsonSerializable(typeof(RuntimeWarningEvent))]
[JsonSerializable(typeof(RuntimeModelCompletedEvent))]
public partial class QueryRuntimeProtocolJsonContext : JsonSerializerContext;
