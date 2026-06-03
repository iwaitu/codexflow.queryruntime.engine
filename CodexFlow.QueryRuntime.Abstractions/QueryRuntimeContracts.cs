namespace CodexFlow.QueryRuntime.Abstractions;

public interface IQueryRuntimeEngine
{
    Task<QueryRuntimeResult> RunAsync(
        QueryRuntimeRequest request,
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
