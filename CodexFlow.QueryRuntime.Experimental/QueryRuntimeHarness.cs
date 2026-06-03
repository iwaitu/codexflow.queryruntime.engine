using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Engine;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Experimental;

public sealed record ExperimentalQueryRuntimeRequest
{
    public required string Prompt { get; init; }

    public string? WorkspacePath { get; init; }

    public string? RunId { get; init; }

    public string? TraceRoot { get; init; }

    public int MaxRounds { get; init; } = 3;

    public bool EnableTools { get; init; }

    public QueryRuntimeToolProfile ToolProfile { get; init; } = QueryRuntimeToolProfile.None;

    public IReadOnlyList<AIFunction> Tools { get; init; } = [];

    public ChatOptions? Options { get; init; }

    public string? RequiredToolName { get; init; }

    public bool RequiresStructuredOutput { get; init; }

    public QreThinkingPolicy ThinkingPolicy { get; init; } = QreThinkingPolicy.Auto;
}

public sealed record ExperimentalQueryRuntimeResult(
    string RunId,
    string SessionId,
    string TraceFilePath,
    string FinalText,
    string TerminationReason,
    int TotalRounds,
    int TotalToolCalls,
    long TotalDurationMs);

public interface IExperimentalQueryRuntimeHarness
{
    Task<ExperimentalQueryRuntimeResult> RunAsync(
        ExperimentalQueryRuntimeRequest request,
        CancellationToken ct = default);
}

public sealed class ExperimentalQueryRuntimeHarness(
    IExperimentalModelClient modelClient) : IExperimentalQueryRuntimeHarness, CodexFlow.QueryRuntime.Abstractions.IQueryRuntimeEngine
{
    async Task<CodexFlow.QueryRuntime.Abstractions.QueryRuntimeResult> CodexFlow.QueryRuntime.Abstractions.IQueryRuntimeEngine.RunAsync(
        CodexFlow.QueryRuntime.Abstractions.QueryRuntimeRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = request.Prompt,
                WorkspacePath = request.WorkspacePath,
                RunId = request.RunId,
                TraceRoot = request.TraceRoot,
                MaxRounds = request.Execution.MaxRounds,
                EnableTools = !request.ToolProfile.IsNone,
                ToolProfile = request.ToolProfile,
                RequiresStructuredOutput = request.Output.RequestJson,
                ThinkingPolicy = request.ModelPolicy.ThinkingPolicy,
                Options = request.Output.RequestJson
                    ? new ChatOptions { ResponseFormat = ChatResponseFormat.Json }
                    : null
            },
            ct).ConfigureAwait(false);

        return new CodexFlow.QueryRuntime.Abstractions.QueryRuntimeResult(
            result.RunId,
            result.SessionId,
            result.TraceFilePath,
            result.FinalText,
            result.TerminationReason,
            result.TotalRounds,
            result.TotalToolCalls,
            result.TotalDurationMs);
    }

    public async Task<ExperimentalQueryRuntimeResult> RunAsync(
        ExperimentalQueryRuntimeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("Prompt is required.", nameof(request));
        }

        var runId = string.IsNullOrWhiteSpace(request.RunId)
            ? DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff")
            : request.RunId!;
        var sessionId = $"qre-{runId}";
        var traceRoot = string.IsNullOrWhiteSpace(request.TraceRoot)
            ? ResolveDefaultTraceRoot(request.WorkspacePath)
            : request.TraceRoot!;
        var traceFilePath = Path.Combine(traceRoot, "runs", runId, "events.jsonl");
        var started = ExperimentalRunStartedRecord.Create(runId, sessionId, request.WorkspacePath, request.Prompt);

        await using var traceSink = await JsonlTraceEventSink.CreateAsync(traceFilePath, started, ct).ConfigureAwait(false);
        var engine = new QueryRuntimeEngine(modelClient);

        try
        {
            var tools = request.Tools.Count > 0
                ? request.Tools
                : ResolveProfileTools(request.ToolProfile, request.WorkspacePath, traceSink);
            var toolsEnabled = request.EnableTools && tools.Count > 0;
            var runtimeRequest = new CodexFlow.QueryRuntime.Engine.QueryRuntimeRequest
            {
                SessionId = sessionId,
                InitialMessages =
                [
                    new ChatMessage(ChatRole.System, "You are running inside the experimental CodexFlow QueryRuntime harness."),
                    new ChatMessage(ChatRole.User, request.Prompt)
                ],
                MaxRounds = Math.Max(1, request.MaxRounds),
                EnableTools = toolsEnabled,
                Options = QreModelExecutionPolicy.Apply(
                    request.Options,
                    toolsEnabled,
                    request.RequiresStructuredOutput,
                    request.ThinkingPolicy),
                AvailableTools = tools,
                RequiredToolName = request.RequiredToolName
            };

            var result = await engine.ExecuteAsync(
                runtimeRequest,
                traceSink,
                runId,
                traceFilePath,
                request.WorkspacePath,
                ct).ConfigureAwait(false);
            await traceSink.WriteCompletedAsync(ExperimentalRunCompletedRecord.Create(runId, sessionId, result), ct).ConfigureAwait(false);
            await JsonlTraceEventSink.WriteManifestAsync(
                traceFilePath,
                ExperimentalRunManifest.Completed(
                    runId,
                    sessionId,
                    request.WorkspacePath,
                    traceFilePath,
                    request.ToolProfile.Name,
                    result),
                ct).ConfigureAwait(false);

            return new ExperimentalQueryRuntimeResult(
                runId,
                sessionId,
                traceFilePath,
                result.FinalText,
                result.TerminationReason.ToString(),
                result.TotalRounds,
                result.TotalToolCalls,
                result.TotalDurationMs);
        }
        catch (Exception ex)
        {
            await traceSink.WriteFailedAsync(ExperimentalRunFailedRecord.Create(runId, sessionId, ex), CancellationToken.None).ConfigureAwait(false);
            await JsonlTraceEventSink.WriteManifestAsync(
                traceFilePath,
                ExperimentalRunManifest.Failed(
                    runId,
                    sessionId,
                    request.WorkspacePath,
                    traceFilePath,
                    request.ToolProfile.Name,
                    ex),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static string ResolveDefaultTraceRoot(string? workspacePath)
    {
        var root = string.IsNullOrWhiteSpace(workspacePath)
            ? Directory.GetCurrentDirectory()
            : workspacePath;
        return Path.Combine(Path.GetFullPath(root), ".qre");
    }

    private static IReadOnlyList<AIFunction> ResolveProfileTools(
        QueryRuntimeToolProfile profile,
        string? workspacePath,
        IQueryRuntimePolicyDecisionSink? policyDecisionSink = null)
    {
        var profileName = profile.Name.Trim().ToLowerInvariant();
        if (profileName is "none")
        {
            return [];
        }

        if (profileName is "readonly" or "read-only" or "read" or "verify")
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                throw new ArgumentException("A workspace path is required when a workspace tool profile is enabled.", nameof(workspacePath));
            }

            var workspaceRoot = Path.GetFullPath(workspacePath);
            return profileName is "verify"
                ? [.. ExperimentalReadOnlyToolPack.Create(workspaceRoot), .. ExperimentalVerifyToolPack.Create(workspaceRoot, policyDecisionSink: policyDecisionSink)]
                : ExperimentalReadOnlyToolPack.Create(workspaceRoot);
        }

        throw new ArgumentException($"Unsupported QueryRuntime tool profile: {profile.Name}", nameof(profile));
    }
}
