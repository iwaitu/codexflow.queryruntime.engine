using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Abstractions;

public interface IQueryRuntimeEngine
{
    Task<QueryRuntimeResult> RunAsync(
        QueryRuntimeRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Host-facing QRE facade for applications that embed QueryRuntime as a library
/// and already own conversation history, tools, provider options, and streaming.
/// </summary>
public interface IQueryRuntimeHostEngine : IQueryRuntimeEngine
{
    Task<QueryRuntimeResult> RunAsync(
        QueryRuntimeHostRequest request,
        CancellationToken ct = default);
}

public sealed record QueryRuntimeRequest
{
    public required string Prompt { get; init; }

    public string? WorkspacePath { get; init; }

    public string? RunId { get; init; }

    public string? TraceRoot { get; init; }

    public QueryRuntimeToolProfile ToolProfile { get; init; } = QueryRuntimeToolProfile.None;

    public QueryRuntimeModelPolicyOptions ModelPolicy { get; init; } = new();

    public QueryRuntimeOutputOptions Output { get; init; } = new();

    public QueryRuntimeExecutionOptions Execution { get; init; } = new();

    public QueryRuntimeToolSearchOptions ToolSearch { get; init; } = new();
}

public sealed record QueryRuntimeHostRequest
{
    /// <summary>
    /// Pre-assembled conversation state. Use this when replacing an existing
    /// runtime that already stores system, user, assistant, and tool messages.
    /// </summary>
    public IReadOnlyList<ChatMessage> InitialMessages { get; init; } = [];

    /// <summary>
    /// CLI-style prompt fallback. Ignored when <see cref="InitialMessages"/> is
    /// non-empty.
    /// </summary>
    public string? Prompt { get; init; }

    public string? WorkspacePath { get; init; }

    public string? RunId { get; init; }

    public string? SessionId { get; init; }

    public string? TraceRoot { get; init; }

    public QueryRuntimeToolProfile ToolProfile { get; init; } = QueryRuntimeToolProfile.None;

    public QueryRuntimeModelPolicyOptions ModelPolicy { get; init; } = new();

    public QueryRuntimeOutputOptions Output { get; init; } = new();

    public QueryRuntimeExecutionOptions Execution { get; init; } = new();

    public QueryRuntimeToolSearchOptions ToolSearch { get; init; } = new();

    public ChatOptions? Options { get; init; }

    /// <summary>
    /// AOT-safe clone hook for provider-specific <see cref="ChatOptions"/> types.
    /// When unset, the runtime uses <see cref="ChatOptions.Clone"/>.
    /// </summary>
    public Func<ChatOptions, ChatOptions>? OptionsCloneFactory { get; init; }

    /// <summary>
    /// Explicitly enables or disables tools. When unset, tools are enabled when
    /// custom tools are supplied or a non-none tool profile is selected.
    /// </summary>
    public bool? EnableTools { get; init; }

    public IReadOnlyList<AIFunction> Tools { get; init; } = [];

    public string? RequiredToolName { get; init; }

    public Func<string, CancellationToken, ValueTask>? TextDeltaSink { get; init; }

    public TimeProvider? TimeProvider { get; init; }

    public Func<Guid>? QueryIdFactory { get; init; }
}

public sealed record QueryRuntimeResult(
    string RunId,
    string SessionId,
    string TraceFilePath,
    string FinalText,
    string TerminationReason,
    int TotalRounds,
    int TotalToolCalls,
    long TotalDurationMs);

public interface IModelClient
{
    IAsyncEnumerable<QueryRuntimeModelUpdate> StreamAsync(
        QueryRuntimeModelRequest request,
        CancellationToken ct = default);
}

public sealed record QueryRuntimeModelRequest(
    string RunId,
    string Prompt,
    string? WorkspacePath,
    QueryRuntimeModelPolicyOptions Policy,
    QueryRuntimeOutputOptions Output);

public sealed record QueryRuntimeModelUpdate(string Text);

public interface ITraceStore
{
    Task<QueryRuntimeTraceSummary> ReadLatestAsync(
        string workspacePath,
        CancellationToken ct = default);
}

public sealed record QueryRuntimeTraceSummary(
    string TraceFilePath,
    string Mode,
    bool ProviderCalls,
    bool ToolExecutions,
    int ModelResponses,
    int ToolResults,
    int EventCount,
    string? TerminationReason);
