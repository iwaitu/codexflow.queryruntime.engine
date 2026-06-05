using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Engine;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Experimental;

public sealed record ExperimentalQueryRuntimeRequest
{
    public string? Prompt { get; init; }

    public IReadOnlyList<ChatMessage> InitialMessages { get; init; } = [];

    public string? WorkspacePath { get; init; }

    public string? RunId { get; init; }

    public string? SessionId { get; init; }

    public string? TraceRoot { get; init; }

    public int MaxRounds { get; init; } = 3;

    public bool EnableTools { get; init; }

    public QueryRuntimeToolSearchOptions ToolSearch { get; init; } = new();

    public QueryRuntimeToolProfile ToolProfile { get; init; } = QueryRuntimeToolProfile.None;

    public IReadOnlyList<AIFunction> Tools { get; init; } = [];

    public ChatOptions? Options { get; init; }

    public string? RequiredToolName { get; init; }

    public bool RequiresStructuredOutput { get; init; }

    public QreThinkingPolicy ThinkingPolicy { get; init; } = QreThinkingPolicy.Auto;

    /// <summary>
    /// Optional deterministic clock. When set (strict replay), the engine stamps every
    /// event with deterministic timestamps and durations instead of wall-clock time.
    /// </summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// Optional deterministic query-id source. When set (strict replay), the engine's
    /// per-run query id is seeded from the source trace instead of <see cref="Guid.NewGuid"/>.
    /// </summary>
    public Func<Guid>? QueryIdFactory { get; init; }

    public Func<string, CancellationToken, ValueTask>? TextDeltaSink { get; init; }
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
    IExperimentalModelClient modelClient) : IExperimentalQueryRuntimeHarness, CodexFlow.QueryRuntime.Abstractions.IQueryRuntimeHostEngine
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
                ToolSearch = request.ToolSearch,
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

    async Task<CodexFlow.QueryRuntime.Abstractions.QueryRuntimeResult> CodexFlow.QueryRuntime.Abstractions.IQueryRuntimeHostEngine.RunAsync(
        CodexFlow.QueryRuntime.Abstractions.QueryRuntimeHostRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = request.Prompt,
                InitialMessages = request.InitialMessages,
                WorkspacePath = request.WorkspacePath,
                RunId = request.RunId,
                SessionId = request.SessionId,
                TraceRoot = request.TraceRoot,
                MaxRounds = request.Execution.MaxRounds,
                EnableTools = request.EnableTools ?? (request.Tools.Count > 0 || !request.ToolProfile.IsNone),
                ToolSearch = request.ToolSearch,
                ToolProfile = request.ToolProfile,
                Tools = request.Tools,
                Options = ResolveHostOptions(request.Options, request.Output.RequestJson),
                RequiredToolName = request.RequiredToolName,
                RequiresStructuredOutput = request.Output.RequestJson,
                ThinkingPolicy = request.ModelPolicy.ThinkingPolicy,
                TextDeltaSink = request.TextDeltaSink,
                TimeProvider = request.TimeProvider,
                QueryIdFactory = request.QueryIdFactory
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
        var prompt = ResolvePrompt(request);

        var runId = string.IsNullOrWhiteSpace(request.RunId)
            ? DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff")
            : request.RunId!;
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? $"qre-{runId}"
            : request.SessionId!;
        var traceRoot = string.IsNullOrWhiteSpace(request.TraceRoot)
            ? ResolveDefaultTraceRoot(request.WorkspacePath)
            : request.TraceRoot!;
        var traceFilePath = Path.Combine(traceRoot, "runs", runId, "events.jsonl");
        var started = ExperimentalRunStartedRecord.Create(runId, sessionId, request.WorkspacePath, prompt);

        await using var traceSink = await JsonlTraceEventSink.CreateAsync(traceFilePath, started, ct).ConfigureAwait(false);
        var engine = new QueryRuntimeEngine(modelClient, request.TimeProvider, request.QueryIdFactory);

        try
        {
            var runDirectory = JsonlTraceStore.GetRunDirectory(traceFilePath);
            var tools = request.Tools.Count > 0
                ? request.Tools
                : ResolveProfileTools(request.ToolProfile, request.WorkspacePath, traceSink, runDirectory);
            var toolsEnabled = request.EnableTools && tools.Count > 0;
            Func<QueryRuntimeToolResolutionContext, IReadOnlyList<AIFunction>>? toolProvider = null;
            var toolSearchCatalog = string.Empty;
            if (request.ToolSearch.Enabled && request.EnableTools)
            {
                var toolSearchOptions = ResolveToolSearchOptions(request.ToolSearch, request.RequiredToolName);
                var descriptors = request.Tools.Count > 0
                    ? ExperimentalToolSearchSession.CreateDescriptors(request.ToolProfile, tools)
                    : ExperimentalToolSearchSession.CreateDescriptors(
                        request.ToolProfile,
                        tools,
                        ResolveProfileToolDescriptors(request.ToolProfile, request.WorkspacePath));
                var toolSearchSession = new ExperimentalToolSearchSession(
                    request.ToolProfile,
                    tools,
                    descriptors,
                    toolSearchOptions);
                toolSearchCatalog = toolSearchSession.GetCapabilityCatalog();
                toolProvider = _ => toolSearchSession.GetActiveTools();
                tools = toolSearchSession.GetActiveTools();
                toolsEnabled = tools.Count > 0;
            }

            var runtimeRequest = new CodexFlow.QueryRuntime.Engine.QueryRuntimeRequest
            {
                SessionId = sessionId,
                InitialMessages = ResolveInitialMessages(request, prompt, toolSearchCatalog),
                MaxRounds = Math.Max(1, request.MaxRounds),
                EnableTools = toolsEnabled,
                Options = QreModelExecutionPolicy.Apply(
                    request.Options,
                    toolsEnabled,
                    request.RequiresStructuredOutput,
                    request.ThinkingPolicy),
                AvailableTools = tools,
                ToolProvider = toolProvider,
                RequiredToolName = request.RequiredToolName,
                TextDeltaSink = request.TextDeltaSink
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

    private static string ResolvePrompt(ExperimentalQueryRuntimeRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            return request.Prompt!;
        }

        if (request.InitialMessages.Count == 0)
        {
            throw new ArgumentException("Prompt or initial messages are required.", nameof(request));
        }

        var lastUserText = request.InitialMessages
            .Where(static message => message.Role == ChatRole.User)
            .Reverse()
            .Select(ExtractText)
            .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text));

        return lastUserText ?? "(message-based request)";
    }

    private static string ExtractText(ChatMessage message)
        => string.Concat(message.Contents.OfType<TextContent>().Select(static content => content.Text));

    private static string BuildSystemPrompt(bool toolSearchEnabled, string toolSearchCatalog)
        => toolSearchEnabled
            ? "You are running inside the experimental CodexFlow QueryRuntime harness. Use tool_search to discover and activate deferred tools when a needed capability is not currently available.\n\n" + toolSearchCatalog
            : "You are running inside the experimental CodexFlow QueryRuntime harness.";

    private static IReadOnlyList<ChatMessage> ResolveInitialMessages(
        ExperimentalQueryRuntimeRequest request,
        string prompt,
        string toolSearchCatalog)
    {
        if (request.InitialMessages.Count == 0)
        {
            return
            [
                new ChatMessage(ChatRole.System, BuildSystemPrompt(request.ToolSearch.Enabled, toolSearchCatalog)),
                new ChatMessage(ChatRole.User, prompt)
            ];
        }

        if (!request.ToolSearch.Enabled)
        {
            return request.InitialMessages;
        }

        return
        [
            new ChatMessage(ChatRole.System, BuildSystemPrompt(toolSearchEnabled: true, toolSearchCatalog: toolSearchCatalog)),
            .. request.InitialMessages
        ];
    }

    private static QueryRuntimeToolSearchOptions ResolveToolSearchOptions(
        QueryRuntimeToolSearchOptions options,
        string? requiredToolName)
    {
        if (string.IsNullOrWhiteSpace(requiredToolName) ||
            options.AlwaysOnToolNames.Contains(requiredToolName, StringComparer.OrdinalIgnoreCase))
        {
            return options;
        }

        return options with
        {
            AlwaysOnToolNames = [.. options.AlwaysOnToolNames, requiredToolName.Trim()]
        };
    }

    private static ChatOptions? ResolveHostOptions(ChatOptions? options, bool requestJson)
    {
        if (!requestJson)
        {
            return options;
        }

        var runtimeOptions = options?.Clone() ?? new ChatOptions();
        runtimeOptions.ResponseFormat ??= ChatResponseFormat.Json;
        return runtimeOptions;
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
        IQueryRuntimePolicyDecisionSink? policyDecisionSink = null,
        string? runDirectory = null)
    {
        var profileName = profile.Name.Trim().ToLowerInvariant();
        if (profileName is "none")
        {
            return [];
        }

        if (profileName is "readonly" or "read-only" or "read" or "verify" or "repair")
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                throw new ArgumentException("A workspace path is required when a workspace tool profile is enabled.", nameof(workspacePath));
            }

            var workspaceRoot = Path.GetFullPath(workspacePath);
            return profileName switch
            {
                "verify" => [.. ExperimentalReadOnlyToolPack.Create(workspaceRoot), .. ExperimentalVerifyToolPack.Create(workspaceRoot, policyDecisionSink: policyDecisionSink)],
                "repair" => [.. ExperimentalReadOnlyToolPack.Create(workspaceRoot), .. ExperimentalRepairToolPack.Create(workspaceRoot, runDirectory, policyDecisionSink: policyDecisionSink)],
                _ => ExperimentalReadOnlyToolPack.Create(workspaceRoot)
            };
        }

        throw new ArgumentException($"Unsupported QueryRuntime tool profile: {profile.Name}", nameof(profile));
    }

    private static IReadOnlyList<QueryRuntimeToolDescriptor> ResolveProfileToolDescriptors(
        QueryRuntimeToolProfile profile,
        string? workspacePath)
    {
        var descriptors = new List<QueryRuntimeToolDescriptor>();
        descriptors.AddRange(new ExperimentalToolRegistry().ListTools(profile));
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            descriptors.AddRange(ExternalStdioToolPack.ListDescriptors(profile, workspacePath));
        }

        return descriptors;
    }
}
