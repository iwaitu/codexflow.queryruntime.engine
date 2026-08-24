using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexFlow.QueryRuntime.Protocol;

public enum RuntimeMessageRole
{
    System = 0,
    User = 1,
    Assistant = 2,
    Tool = 3
}

public sealed record RuntimeMessage(
    RuntimeMessageRole Role,
    IReadOnlyList<RuntimeItem> Items);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RuntimeTextItem), "text")]
[JsonDerivedType(typeof(RuntimeReasoningItem), "reasoning")]
[JsonDerivedType(typeof(RuntimeToolCallItem), "tool_call")]
[JsonDerivedType(typeof(RuntimeToolResultItem), "tool_result")]
[JsonDerivedType(typeof(RuntimeArtifactItem), "artifact")]
public abstract record RuntimeItem;

public sealed record RuntimeTextItem(string Text) : RuntimeItem;

public sealed record RuntimeReasoningItem(
    string Text,
    string? ProtectedData = null) : RuntimeItem;

public sealed record RuntimeToolCallItem(RuntimeToolCall Call) : RuntimeItem;

public sealed record RuntimeToolResultItem(RuntimeToolResult Result) : RuntimeItem;

public sealed record RuntimeArtifactItem(RuntimeArtifactReference Artifact) : RuntimeItem;

public sealed record RuntimeToolCall(
    RuntimeInvocationId InvocationId,
    string Name,
    JsonElement Arguments,
    bool InformationalOnly = false);

public sealed record RuntimeToolResult(
    RuntimeInvocationId InvocationId,
    string? Text,
    bool Success,
    RuntimeError? Error = null,
    IReadOnlyList<RuntimeArtifactReference>? Artifacts = null,
    RuntimeToolResultDetails? Details = null);

public enum RuntimeToolOutcome
{
    Succeeded = 0,
    Denied = 1,
    Cancelled = 2,
    Failed = 3,
    TimedOut = 4
}

/// <summary>
/// Structured execution observation. Model-visible text remains bounded and
/// separate from host diagnostics and artifact references.
/// </summary>
public sealed record RuntimeToolResultDetails(
    RuntimeToolOutcome Outcome,
    string? StandardOutput = null,
    string? StandardError = null,
    int? ExitCode = null,
    long? DurationMs = null,
    bool Truncated = false,
    bool Retryable = false,
    string? WorkspaceChangeEvidence = null);

public sealed record RuntimeArtifactReference(
    string Path,
    string? MediaType = null,
    string? Digest = null,
    long? Length = null);
