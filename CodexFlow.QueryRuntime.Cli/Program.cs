using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Experimental;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;
using CodexFlow.QueryRuntime.Sandbox.Docker;
using CodexFlow.QueryRuntime.Sandbox.LocalProcess;
using Microsoft.Extensions.AI;

return await QreCli.RunAsync(args, CancellationToken.None).ConfigureAwait(false);

internal static class QreCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        if (args[0] is "--version" or "version")
        {
            Console.WriteLine(GetVersion());
            return 0;
        }

        return args[0] switch
        {
            "run" => await RunQueryAsync(args[1..], ct).ConfigureAwait(false),
            "trace" => Trace(args[1..]),
            "tool" => Tool(args[1..]),
            "policy" => Policy(args[1..]),
            "replay" => await Replay(args[1..], ct).ConfigureAwait(false),
            "rerun" => await Rerun(args[1..], ct).ConfigureAwait(false),
            "diff" => await Diff(args[1..], ct).ConfigureAwait(false),
            "sandbox" => await Sandbox(args[1..], ct).ConfigureAwait(false),
            "doctor" => await Doctor(args[1..], ct).ConfigureAwait(false),
            "init" => Init(args[1..]),
            _ => Fail($"Unknown command: {args[0]}")
        };
    }

    private static async Task<int> RunQueryAsync(string[] args, CancellationToken ct)
    {
        var options = new QreRunOptions
        {
            Workspace = Directory.GetCurrentDirectory()
        };
        var promptParts = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace":
                case "-w":
                    if (++i >= args.Length)
                    {
                        return Fail("--workspace requires a path.");
                    }
                    options.Workspace = args[i];
                    break;
                case "--response":
                    if (++i >= args.Length)
                    {
                        return Fail("--response requires text.");
                    }
                    options.Provider.StaticResponse = args[i];
                    break;
                case "--api-url":
                    if (++i >= args.Length)
                    {
                        return Fail("--api-url requires a URL.");
                    }
                    options.Provider.ApiUrl = args[i];
                    break;
                case "--api-key":
                    if (++i >= args.Length)
                    {
                        return Fail("--api-key requires a value.");
                    }
                    options.Provider.ApiKey = args[i];
                    break;
                case "--model":
                    if (++i >= args.Length)
                    {
                        return Fail("--model requires a model name.");
                    }
                    options.Provider.Model = args[i];
                    break;
                case "--api-mode":
                    if (++i >= args.Length)
                    {
                        return Fail("--api-mode requires chat-completions, responses, or anthropic-messages.");
                    }
                    options.Provider.ApiMode = args[i];
                    break;
                case "--max-rounds":
                    if (++i >= args.Length || !int.TryParse(args[i], out var maxRounds))
                    {
                        return Fail("--max-rounds requires an integer.");
                    }
                    options.Runtime.MaxRounds = maxRounds;
                    break;
                case "--runtime":
                    if (++i >= args.Length || args[i] != "v2")
                    {
                        return Fail("v1 execution has been removed from qre run. --runtime accepts only v2 and is now optional.");
                    }
                    break;
                case "--required-tool":
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    {
                        return Fail("--required-tool requires a tool name.");
                    }
                    options.RequiredToolName = args[i];
                    break;
                case "--approve-risk":
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    {
                        return Fail("--approve-risk requires a reason.");
                    }
                    options.ApprovalReason = args[i].Trim();
                    break;
                case "--runner":
                    if (++i >= args.Length)
                    {
                        return Fail("--runner requires local or docker.");
                    }
                    options.Runner = args[i];
                    break;
                case "--docker-image":
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    {
                        return Fail("--docker-image requires an image name.");
                    }
                    options.DockerImage = args[i];
                    break;
                case "--profile":
                case "--tools":
                    if (++i >= args.Length)
                    {
                        return Fail($"{args[i - 1]} requires none, readonly, verify, or repair.");
                    }
                    options.ToolProfile = new QueryRuntimeToolProfile(args[i]);
                    break;
                case "--thinking":
                    if (++i >= args.Length || !TryParseThinkingPolicy(args[i], out var thinkingPolicy))
                    {
                        return Fail("--thinking requires auto, off, on, or preserve.");
                    }
                    options.ModelPolicy.ThinkingPolicy = thinkingPolicy;
                    break;
                case "--trace-data":
                    if (++i >= args.Length || !TryParseTraceDataMode(args[i], out var traceDataMode))
                    {
                        return Fail("--trace-data requires public, private, or sanitized.");
                    }
                    options.Trace = new QueryRuntimeTraceOptions { DataMode = traceDataMode };
                    break;
                case "--json-output":
                    options.Output.RequestJson = true;
                    break;
                case "--json":
                    options.Output.Json = true;
                    break;
                case "--stream":
                    options.Output.Stream = true;
                    break;
                case "--jsonl-stream":
                    return Fail("--jsonl-stream is reserved for the future machine-readable streaming event mode and is not implemented yet. Use --json for the final result object.");
                case "--external":
                    options.IncludeExternalTools = true;
                    break;
                case "--tool-search":
                    options.ToolSearch.Enabled = true;
                    break;
                case "--tool-search-top-k":
                    if (++i >= args.Length || !int.TryParse(args[i], out var toolSearchTopK) || toolSearchTopK <= 0)
                    {
                        return Fail("--tool-search-top-k requires a positive integer.");
                    }
                    options.ToolSearch.Enabled = true;
                    options.ToolSearch.TopK = toolSearchTopK;
                    break;
                case "--help":
                case "-h":
                    PrintRunHelp();
                    return 0;
                default:
                    if (args[i].StartsWith("-", StringComparison.Ordinal))
                    {
                        return Fail($"Unknown qre run option: {args[i]}");
                    }
                    promptParts.Add(args[i]);
                    break;
            }
        }

        var prompt = string.Join(' ', promptParts).Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Fail("qre run requires a prompt.");
        }
        if (options.Output.Json && options.Output.Stream)
        {
            return Fail("--stream cannot be combined with --json. Use --stream for human-readable text or --json for the final result object.");
        }

        var resolvedWorkspace = Path.GetFullPath(options.Workspace);
        if (!Directory.Exists(resolvedWorkspace))
        {
            return Fail($"Workspace does not exist: {resolvedWorkspace}");
        }

        return await RunQueryV2Async(options, prompt, resolvedWorkspace, ct).ConfigureAwait(false);
    }

    private static async Task<int> RunQueryV2Async(
        QreRunOptions options,
        string prompt,
        string resolvedWorkspace,
        CancellationToken ct)
    {
        var normalizedProfile = ExperimentalToolRegistry.NormalizeProfileName(options.ToolProfile.Name);
        if (normalizedProfile is not ("none" or "readonly" or "verify" or "repair"))
        {
            return Fail($"Unsupported profile value: {options.ToolProfile.Name}");
        }

        var sandboxRunner = CreateSandboxRunner(
            options.Runner,
            options.DockerImage,
            out var runnerName,
            out _,
            out var runnerError);
        if (sandboxRunner == null)
        {
            return Fail(runnerError);
        }
        var runSuffix = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}";
        var auditOptions = new RuntimeAuditStoreOptions
        {
            DataMode = ToV2AuditDataMode(options.Trace.DataMode),
            Retention = options.Trace.PrivateDiagnosticRetention
        };
        await using var auditStore = RuntimeJsonlAuditStore.Create(
            resolvedWorkspace,
            $"v2-{runSuffix}",
            auditOptions);

        ExperimentalV2RuntimeComposition composition;
        try
        {
            var toolSearch = options.ToolSearch.Enabled
                ? ResolveV2ToolSearchOptions(options.ToolSearch, options.RequiredToolName)
                : null;
            composition = ExperimentalV2ToolComposition.CreateRuntime(
                new QueryRuntimeToolProfile(normalizedProfile),
                resolvedWorkspace,
                sandboxRunner,
                string.Equals(runnerName, "docker", StringComparison.Ordinal)
                    ? RuntimeSandboxKind.Docker
                    : RuntimeSandboxKind.LocalProcess,
                options.IncludeExternalTools,
                toolSearch,
                runDirectory: auditOptions.DataMode == RuntimeAuditDataMode.PublicRedacted
                    ? null
                    : auditStore.RunDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return Fail(ex.Message);
        }

        IRuntimeModelClient? modelClient;
        try
        {
            modelClient = CreateV2ModelClient(options);
        }
        catch (QreModelSelectionException ex)
        {
            return Fail(ex.Message);
        }

        if (modelClient == null)
        {
            return Fail(
                "No v2 model client configured. Provide --response for static mode, or set --api-url, --api-key, and --model.");
        }

        try
        {
            var sessionId = new RuntimeSessionId($"qre-cli-{runSuffix}");
            var turnId = new RuntimeTurnId($"qre-cli-turn-{runSuffix}");
            var toolPipeline = composition.Pipeline;
            var runtime = new AgentRuntime(modelClient);
            using var handle = new RuntimeTurnHandle();
            var eventSink = options.Output.Stream
                ? new CliV2EventSink(options.Output.Stream)
                : null;
            var result = await runtime.RunAsync(
                new RuntimeRunRequest(new RuntimeAgentLoopRequest(
                    sessionId,
                    turnId,
                    prompt,
                    string.IsNullOrWhiteSpace(composition.CapabilityCatalog)
                        ? [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(prompt)])]
                        :
                        [
                            new RuntimeMessage(
                                RuntimeMessageRole.System,
                                [new RuntimeTextItem(composition.CapabilityCatalog)]),
                            new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(prompt)])
                        ],
                    toolPipeline.Descriptors,
                    new RuntimeModelParameters(
                        Model: FirstNonEmpty(options.Provider.Model, Environment.GetEnvironmentVariable("QRE_MODEL")),
                        RequireJsonObject: options.Output.RequestJson,
                        RequiredToolName: options.RequiredToolName),
                    new RuntimePolicySnapshot("v2", normalizedProfile),
                    new RuntimeEnvironmentSnapshot(runnerName, resolvedWorkspace, $"v2:{normalizedProfile}"),
                    new RuntimeBudgetSnapshot(
                        Math.Max(1, options.Runtime.MaxRounds),
                        maxToolCalls: Math.Max(4, options.Runtime.MaxRounds * 4),
                        maxModelRetries: 1,
                        maxContinuations: Math.Max(0, options.Runtime.MaxStopGateContinuations)))
                {
                    ToolPipeline = toolPipeline,
                    ToolCatalogSelector = composition.ToolCatalogSelector,
                    AuditSink = auditStore,
                    AuditFailureMode = RuntimeAuditFailureMode.FailClosed,
                    ToolApproval = options.ApprovalReason == null
                        ? null
                        : new CliV2ToolApproval(options.ApprovalReason)
                })
                {
                    Handle = handle
                },
                eventSink,
                ct).ConfigureAwait(false);
            await FinalizeV2RunArtifactsAsync(
                auditStore.RunDirectory,
                resolvedWorkspace,
                prompt,
                result,
                options.Trace,
                ct).ConfigureAwait(false);

            if (options.Output.Json)
            {
                WriteJson(new QreV2RunOutput(
                    "qre.v2.run.completed",
                    result.FinalText,
                    sessionId.Value,
                    turnId.Value,
                    result.Status.ToString(),
                    result.TerminationReason.ToString(),
                    result.Turn.Steps.Count,
                    result.Turn.Progress.ToolCallCount,
                    result.Turn.Progress.ContinuationCount,
                    result.Usage.InputTokens,
                    result.Usage.OutputTokens,
                    result.Usage.TotalTokens,
                    result.Error?.Code)
                {
                    Profile = normalizedProfile,
                    Runner = runnerName,
                    Tools = toolPipeline.Descriptors.Select(static tool => tool.CanonicalName).ToArray(),
                    HistoryVersion = result.Session.HistoryVersion,
                    ContextPreparations = result.LoopResult.PreparedContexts.Count,
                    CompactionCount = result.LoopResult.PreparedContexts.Count(static context => context.Compacted),
                    MaxPreparedContextTokens = result.LoopResult.PreparedContexts.Count == 0
                        ? 0
                        : result.LoopResult.PreparedContexts.Max(static context => context.EstimatedTokens),
                    ContextEstimator = RuntimeTokenEstimator.Version,
                    DeferredToolSearch = composition.ToolCatalogSelector != null,
                    AuditSchemaVersion = RuntimeAuditSchema.CurrentVersion,
                    AuditEventCount = result.LoopResult.AuditEvents.Count,
                    AuditFilePath = auditStore.AuditFilePath,
                    RunDirectory = auditStore.RunDirectory,
                    AuditDataMode = auditOptions.DataMode.ToString(),
                    AuditReplayCapability = auditOptions.ReplayCapability.ToString()
                });
            }
            else
            {
                if (options.Output.Stream && !result.FinalText.EndsWith('\n'))
                {
                    Console.WriteLine();
                }
                else if (!options.Output.Stream)
                {
                    Console.WriteLine(result.FinalText);
                    Console.WriteLine();
                }
                Console.WriteLine("runtime: v2");
                Console.WriteLine($"session_id: {sessionId.Value}");
                Console.WriteLine($"turn_id: {turnId.Value}");
                Console.WriteLine($"status: {result.Status}");
                Console.WriteLine($"termination: {result.TerminationReason}");
                Console.WriteLine($"profile: {normalizedProfile}");
                Console.WriteLine($"runner: {runnerName}");
                Console.WriteLine($"audit: {auditStore.AuditFilePath}");
                Console.WriteLine($"audit_events: {result.LoopResult.AuditEvents.Count}");
                Console.WriteLine($"audit_data_mode: {auditOptions.DataMode}");
                Console.WriteLine($"audit_replay: {auditOptions.ReplayCapability}");
            }

            return result.Status == RuntimeTurnStatus.Completed ? 0 : 1;
        }
        finally
        {
            (modelClient as IDisposable)?.Dispose();
        }
    }

    private static IRuntimeModelClient? CreateV2ModelClient(QreRunOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Provider.StaticResponse))
        {
            return new StaticRuntimeModelClient(options.Provider.StaticResponse);
        }

        var apiUrl = FirstNonEmpty(options.Provider.ApiUrl, Environment.GetEnvironmentVariable("QRE_API_URL"));
        var apiKey = FirstNonEmpty(options.Provider.ApiKey, Environment.GetEnvironmentVariable("QRE_API_KEY"));
        var model = FirstNonEmpty(options.Provider.Model, Environment.GetEnvironmentVariable("QRE_MODEL"));
        var apiMode = FirstNonEmpty(options.Provider.ApiMode, Environment.GetEnvironmentVariable("QRE_API_MODE"));
        if (string.IsNullOrWhiteSpace(apiUrl) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return new MeaiRuntimeModelClient(
            QreVllmChatClientFactory.Create(apiUrl, apiKey, model, apiMode),
            request => new ChatOptions
            {
                Temperature = request.Parameters.Temperature is { } temperature
                    ? (float)temperature
                    : null,
                MaxOutputTokens = request.Parameters.MaxOutputTokens,
                ResponseFormat = request.Parameters.RequireJsonObject
                    ? ChatResponseFormat.Json
                    : null
            });
    }

    private static QueryRuntimeToolSearchOptions ResolveV2ToolSearchOptions(
        QueryRuntimeToolSearchOptions source,
        string? requiredToolName)
    {
        var alwaysOn = new HashSet<string>(source.AlwaysOnToolNames, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(requiredToolName))
        {
            alwaysOn.Add(requiredToolName);
        }
        return new QueryRuntimeToolSearchOptions
        {
            Enabled = true,
            TopK = source.TopK,
            AlwaysOnToolNames = alwaysOn.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            DeferredToolNames = source.DeferredToolNames.ToArray(),
            IncludeAlreadyActive = source.IncludeAlreadyActive,
            IncludeUnavailable = source.IncludeUnavailable
        };
    }

    private static IExperimentalModelClient? CreateModelClient(QueryRuntimeProviderOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.StaticResponse))
        {
            return new StaticExperimentalModelClient(options.StaticResponse);
        }

        var apiUrl = FirstNonEmpty(options.ApiUrl, Environment.GetEnvironmentVariable("QRE_API_URL"));
        var apiKey = FirstNonEmpty(options.ApiKey, Environment.GetEnvironmentVariable("QRE_API_KEY"));
        var model = FirstNonEmpty(options.Model, Environment.GetEnvironmentVariable("QRE_MODEL"));
        var apiMode = FirstNonEmpty(options.ApiMode, Environment.GetEnvironmentVariable("QRE_API_MODE"));

        if (string.IsNullOrWhiteSpace(apiUrl) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return new ChatClientExperimentalModelClient(
            QreVllmChatClientFactory.Create(apiUrl, apiKey, model, apiMode));
    }

    private static IReadOnlyList<Microsoft.Extensions.AI.AIFunction>? ResolveTools(
        QueryRuntimeToolProfile options,
        string resolvedWorkspace,
        ISandboxRunner? sandboxRunner = null,
        bool includeExternal = false)
    {
        var toolMode = FirstNonEmpty(options.Name, "none")!;
        var tools = toolMode.Trim().ToLowerInvariant() switch
        {
            "none" => [],
            "readonly" or "read-only" or "read" => ExperimentalReadOnlyToolPack.Create(resolvedWorkspace),
            "verify" => [.. ExperimentalReadOnlyToolPack.Create(resolvedWorkspace), .. ExperimentalVerifyToolPack.Create(resolvedWorkspace, sandboxRunner)],
            "repair" => [.. ExperimentalReadOnlyToolPack.Create(resolvedWorkspace), .. ExperimentalRepairToolPack.Create(resolvedWorkspace)],
            _ => null
        };
        if (tools == null)
        {
            return null;
        }

        return includeExternal
            ? [.. ExternalStdioToolPack.Create(resolvedWorkspace), .. tools]
            : tools;
    }

    private static IReadOnlySet<string> ResolveApprovalRequiredToolNames(
        QueryRuntimeToolProfile profile,
        string workspacePath,
        bool includeExternal)
    {
        var names = new ExperimentalToolRegistry()
            .ListTools(profile)
            .Where(static descriptor => descriptor.Capabilities.Contains(QueryRuntimeCapabilities.WriteFileSystem))
            .Select(static descriptor => descriptor.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (includeExternal)
        {
            names.UnionWith(ExternalStdioToolPack
                .ListDescriptors(profile, workspacePath)
                .Select(static descriptor => descriptor.Name));
        }

        return names;
    }

    internal static bool IsSuccessfulV1Run(
        string terminationReason,
        string? requiredToolName,
        bool requiredToolSatisfied)
        => string.Equals(terminationReason, "NoToolCalls", StringComparison.Ordinal) &&
           (string.IsNullOrWhiteSpace(requiredToolName) || requiredToolSatisfied);

    private static ChatOptions? BuildChatOptions(QreRunOptions options)
    {
        if (!options.Output.RequestJson)
        {
            return null;
        }

        return new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.Json
        };
    }

    private static bool TryParseThinkingPolicy(string value, out QreThinkingPolicy policy)
    {
        policy = value.Trim().ToLowerInvariant() switch
        {
            "auto" => QreThinkingPolicy.Auto,
            "off" or "false" or "disabled" or "disable" => QreThinkingPolicy.ForceDisabled,
            "on" or "true" or "enabled" or "enable" => QreThinkingPolicy.ForceEnabled,
            "preserve" or "keep" => QreThinkingPolicy.Preserve,
            _ => (QreThinkingPolicy)(-1)
        };

        return Enum.IsDefined(policy);
    }

    private static bool TryParseTraceDataMode(string value, out QueryRuntimeTraceDataMode mode)
    {
        mode = value.Trim().ToLowerInvariant() switch
        {
            "public" or "redacted" => QueryRuntimeTraceDataMode.PublicRedacted,
            "private" or "diagnostic" => QueryRuntimeTraceDataMode.PrivateDiagnostic,
            "sanitized" or "fixture" => QueryRuntimeTraceDataMode.SanitizedFixture,
            _ => (QueryRuntimeTraceDataMode)(-1)
        };

        return Enum.IsDefined(mode);
    }

    private static RuntimeAuditDataMode ToV2AuditDataMode(QueryRuntimeTraceDataMode mode)
        => mode switch
        {
            QueryRuntimeTraceDataMode.PublicRedacted => RuntimeAuditDataMode.PublicRedacted,
            QueryRuntimeTraceDataMode.PrivateDiagnostic => RuntimeAuditDataMode.PrivateDiagnostic,
            QueryRuntimeTraceDataMode.SanitizedFixture => RuntimeAuditDataMode.SanitizedFixture,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported v2 audit data mode.")
        };

    private static bool TryParseNetworkPolicy(string value, out SandboxNetworkPolicy policy)
    {
        policy = value.Trim().ToLowerInvariant() switch
        {
            "deny" or "none" or "off" => SandboxNetworkPolicy.Deny,
            "allow" or "on" => SandboxNetworkPolicy.Allow,
            _ => new SandboxNetworkPolicy(string.Empty)
        };

        return !string.IsNullOrWhiteSpace(policy.Mode);
    }

    private static bool TryParseMountPolicy(string value, out SandboxMountPolicy policy)
    {
        policy = value.Trim().ToLowerInvariant() switch
        {
            "readonly" or "read-only" or "ro" => SandboxMountPolicy.WorkspaceReadOnly,
            "readwrite" or "read-write" or "rw" => SandboxMountPolicy.WorkspaceReadWrite,
            _ => new SandboxMountPolicy(string.Empty)
        };

        return !string.IsNullOrWhiteSpace(policy.Mode);
    }

    private static ISandboxRunner? CreateSandboxRunner(
        string? runner,
        string? dockerImage,
        out string runnerName,
        out QreSandboxRunnerConfiguration? runnerConfiguration,
        out string error)
    {
        runnerName = FirstNonEmpty(runner, "local")!.Trim().ToLowerInvariant();
        runnerConfiguration = null;
        error = string.Empty;
        if (runnerName == "local")
        {
            return new LocalProcessSandboxRunner();
        }

        if (runnerName == "docker")
        {
            var dockerOptions = new DockerSandboxOptions
            {
                Image = FirstNonEmpty(dockerImage, Environment.GetEnvironmentVariable("QRE_DOCKER_IMAGE"))
                        ?? new DockerSandboxOptions().Image
            };
            runnerConfiguration = QreSandboxRunnerConfiguration.FromDocker(dockerOptions);
            return new DockerSandboxRunner(dockerOptions);
        }

        return CreateUnsupportedRunner(runnerName, out error);
    }

    private static ISandboxRunner? CreateUnsupportedRunner(string runnerName, out string error)
    {
        error = $"Unsupported runner value: {runnerName}. Expected local or docker.";
        return null;
    }

    private static int Trace(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintTraceHelp();
            return 0;
        }

        return args[0] switch
        {
            "latest" => TraceLatest(args[1..]),
            _ => Fail($"Unknown trace command: {args[0]}")
        };
    }

    private static int Tool(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintToolHelp();
            return 0;
        }

        return args[0] switch
        {
            "list" => ToolList(args[1..]),
            "register" => ToolRegister(args[1..]),
            "invoke" => ToolInvoke(args[1..]),
            _ => Fail($"Unknown tool command: {args[0]}")
        };
    }

    private static int ToolInvoke(string[] args)
    {
        var workspace = Directory.GetCurrentDirectory();
        string? name = null;
        var argumentsJson = "{}";
        var json = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace":
                case "-w":
                    if (++i >= args.Length)
                    {
                        return Fail("--workspace requires a path.");
                    }
                    workspace = args[i];
                    break;
                case "--name":
                case "-n":
                    if (++i >= args.Length)
                    {
                        return Fail("--name requires a tool name.");
                    }
                    name = args[i];
                    break;
                case "--arguments":
                case "--args":
                    if (++i >= args.Length)
                    {
                        return Fail("--arguments requires a JSON object.");
                    }
                    argumentsJson = args[i];
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    return Fail($"Unknown tool invoke option: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return Fail("--name is required.");
        }

        var resolvedWorkspace = Path.GetFullPath(workspace);
        if (!Directory.Exists(resolvedWorkspace))
        {
            return Fail($"Workspace does not exist: {resolvedWorkspace}");
        }

        Dictionary<string, object?> arguments;
        try
        {
            arguments = ParseToolArguments(argumentsJson);
        }
        catch (JsonException ex)
        {
            return Fail($"--arguments must be a JSON object: {ex.Message}");
        }

        var tool = ExternalStdioToolPack.Create(resolvedWorkspace)
            .FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));
        if (tool == null)
        {
            return Fail($"External tool is not registered: {name}");
        }

        string resultText;
        try
        {
            var result = tool.InvokeAsync(new AIFunctionArguments(arguments), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            resultText = FormatToolResult(result);
        }
        catch (Exception ex)
        {
            return Fail($"Tool invocation failed: {ex.Message}");
        }

        if (json)
        {
            WriteJson(new QreToolInvokeOutput(
                "qre.tool.invoked",
                resolvedWorkspace,
                tool.Name,
                arguments,
                resultText));
            return 0;
        }

        Console.WriteLine(resultText);
        return 0;
    }

    private static int ToolRegister(string[] args)
    {
        var workspace = Directory.GetCurrentDirectory();
        string? manifest = null;
        var json = false;
        var force = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace":
                case "-w":
                    if (++i >= args.Length)
                    {
                        return Fail("--workspace requires a path.");
                    }
                    workspace = args[i];
                    break;
                case "--manifest":
                case "-m":
                    if (++i >= args.Length)
                    {
                        return Fail("--manifest requires a path.");
                    }
                    manifest = args[i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    return Fail($"Unknown tool register option: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(manifest))
        {
            return Fail("--manifest is required.");
        }

        var resolvedWorkspace = Path.GetFullPath(workspace);
        if (!Directory.Exists(resolvedWorkspace))
        {
            return Fail($"Workspace does not exist: {resolvedWorkspace}");
        }

        var manifestPath = Path.GetFullPath(manifest);
        if (!File.Exists(manifestPath))
        {
            return Fail($"External tool manifest does not exist: {manifestPath}");
        }

        var descriptor = TryReadExternalToolDescriptor(manifestPath);
        if (descriptor == null)
        {
            return Fail("External tool manifest is invalid or uses an unsupported transport.");
        }

        if (!IsValidToolManifestFileName(descriptor.Name))
        {
            return Fail($"External tool name is not safe for registration: {descriptor.Name}");
        }

        var toolsDirectory = Path.Combine(resolvedWorkspace, ".qre", "tools");
        var destinationPath = Path.Combine(toolsDirectory, descriptor.Name + ".json");
        var overwritten = File.Exists(destinationPath);
        if (overwritten && !force)
        {
            return Fail($"External tool is already registered: {destinationPath}. Use --force to overwrite.");
        }

        Directory.CreateDirectory(toolsDirectory);
        File.Copy(manifestPath, destinationPath, overwrite: force);

        var installedDescriptor = TryReadExternalToolDescriptor(destinationPath);
        if (installedDescriptor == null)
        {
            return Fail($"Registered manifest could not be read back: {destinationPath}");
        }

        var output = new QreToolRegisterOutput(
            "qre.tool.registered",
            resolvedWorkspace,
            manifestPath,
            destinationPath,
            installedDescriptor.Name,
            installedDescriptor.Transport ?? "stdio",
            installedDescriptor.Capabilities,
            overwritten);

        if (json)
        {
            WriteJson(output);
            return 0;
        }

        Console.WriteLine($"registered {output.ToolName} -> {output.DestinationPath}");
        return 0;
    }

    private static int ToolList(string[] args)
    {
        var workspace = Directory.GetCurrentDirectory();
        var toolsMode = "readonly";
        var json = false;
        var includeExternal = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace":
                case "-w":
                    if (++i >= args.Length)
                    {
                        return Fail("--workspace requires a path.");
                    }
                    workspace = args[i];
                    break;
                case "--profile":
                case "--tools":
                    if (++i >= args.Length)
                    {
                        return Fail($"{args[i - 1]} requires none, readonly, verify, or repair.");
                    }
                    toolsMode = args[i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--external":
                    includeExternal = true;
                    break;
                default:
                    return Fail($"Unknown tool list option: {args[i]}");
            }
        }

        var resolvedWorkspace = Path.GetFullPath(workspace);
        if (!Directory.Exists(resolvedWorkspace))
        {
            return Fail($"Workspace does not exist: {resolvedWorkspace}");
        }

        var profile = new QueryRuntimeToolProfile(toolsMode);
        var tools = ResolveTools(profile, resolvedWorkspace);
        if (tools == null)
        {
            return Fail($"Unsupported profile value: {toolsMode}");
        }
        var descriptors = new ExperimentalToolRegistry().ListTools(profile);
        var outputTools = tools.Select(tool =>
                new QreToolDescriptor(
                    tool.Name,
                    tool.Description,
                    descriptors.FirstOrDefault(descriptor => descriptor.Name == tool.Name)?.Capabilities ?? new HashSet<string>()))
            .ToList();

        if (includeExternal)
        {
            outputTools.AddRange(ReadExternalToolDescriptors(resolvedWorkspace));
        }

        if (json)
        {
            WriteJson(new QreToolListOutput(
                "qre.tool.list",
                toolsMode,
                includeExternal,
                outputTools));
            return 0;
        }

        foreach (var tool in outputTools)
        {
            var origin = tool.Transport == null ? tool.Source : $"{tool.Source}/{tool.Transport}";
            Console.WriteLine($"{tool.Name}\t{origin}\t{tool.Description}");
        }

        return 0;
    }

    private static IReadOnlyList<QreToolDescriptor> ReadExternalToolDescriptors(string workspace)
    {
        var toolsDirectory = Path.Combine(workspace, ".qre", "tools");
        if (!Directory.Exists(toolsDirectory))
        {
            return [];
        }

        var descriptors = new List<QreToolDescriptor>();
        foreach (var manifestPath in Directory.EnumerateFiles(toolsDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            var descriptor = TryReadExternalToolDescriptor(manifestPath);
            if (descriptor != null)
            {
                descriptors.Add(descriptor);
            }
        }

        return descriptors;
    }

    private static QreToolDescriptor? TryReadExternalToolDescriptor(string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;
            var name = TryGetJsonString(root, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var transport = TryGetJsonString(root, "transport") ?? "stdio";
            if (!IsSupportedExternalToolTransport(transport))
            {
                return null;
            }

            return new QreToolDescriptor(
                name,
                TryGetJsonString(root, "description"),
                ReadStringArray(root, "capabilities"),
                "external",
                transport);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool IsSupportedExternalToolTransport(string transport)
        => transport.Equals("stdio", StringComparison.OrdinalIgnoreCase) ||
           transport.Equals("mcp-stdio", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidToolManifestFileName(string name)
        => !string.IsNullOrWhiteSpace(name) &&
           name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
           !name.Contains(Path.DirectorySeparatorChar) &&
           !name.Contains(Path.AltDirectorySeparatorChar);

    private static string? TryGetJsonString(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(propertyName, out var element) &&
           element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? TryGetJsonString(JsonElement? root, string propertyName)
        => root is { ValueKind: JsonValueKind.Object } element &&
           element.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Dictionary<string, object?> ParseToolArguments(string argumentsJson)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("root value is not an object.");
        }

        var arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            arguments[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number when property.Value.TryGetInt32(out var integer) => integer,
                JsonValueKind.Number when property.Value.TryGetInt64(out var integer) => integer,
                JsonValueKind.Number when property.Value.TryGetDouble(out var number) => number,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => property.Value.Clone()
            };
        }

        return arguments;
    }

    private static string FormatToolResult(object? result)
        => result switch
        {
            null => string.Empty,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            JsonElement element => element.GetRawText(),
            _ => result.ToString() ?? string.Empty
        };

    private static int? TryGetJsonInt(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? TryGetJsonBool(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(propertyName, out var value) &&
           (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    private static IReadOnlySet<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                values.Add(item.GetString()!);
            }
        }

        return values;
    }

    private static int Policy(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintPolicyHelp();
            return 0;
        }

        return args[0] switch
        {
            "check" => PolicyCheck(args[1..]),
            _ => Fail($"Unknown policy command: {args[0]}")
        };
    }

    private static int PolicyCheck(string[] args)
    {
        var workspace = Directory.GetCurrentDirectory();
        var profileName = "readonly";
        string? toolName = null;
        var json = false;
        var network = SandboxNetworkPolicy.Deny;
        SandboxMountPolicy? mount = null;
        string? approvalReason = null;
        IReadOnlyList<string> command = [];

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--":
                    command = args[(i + 1)..];
                    i = args.Length;
                    break;
                case "--workspace":
                case "-w":
                    if (++i >= args.Length)
                    {
                        return Fail("--workspace requires a path.");
                    }
                    workspace = args[i];
                    break;
                case "--profile":
                    if (++i >= args.Length)
                    {
                        return Fail("--profile requires none, readonly, verify, or repair.");
                    }
                    profileName = args[i];
                    break;
                case "--tool":
                    if (++i >= args.Length)
                    {
                        return Fail("--tool requires a tool name.");
                    }
                    toolName = args[i];
                    break;
                case "--network":
                    if (++i >= args.Length || !TryParseNetworkPolicy(args[i], out network))
                    {
                        return Fail("--network requires deny or allow.");
                    }
                    break;
                case "--mount":
                    if (++i >= args.Length || !TryParseMountPolicy(args[i], out var parsedMount))
                    {
                        return Fail("--mount requires readonly or readwrite.");
                    }
                    mount = parsedMount;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--approve-risk":
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    {
                        return Fail("--approve-risk requires a reason.");
                    }
                    approvalReason = args[i];
                    break;
                default:
                    return Fail($"Unknown policy check option: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(toolName))
        {
            return Fail("policy check requires --tool.");
        }

        var resolvedWorkspace = Path.GetFullPath(workspace);
        if (!Directory.Exists(resolvedWorkspace))
        {
            return Fail($"Workspace does not exist: {resolvedWorkspace}");
        }

        var profile = new QueryRuntimeToolProfile(profileName);
        var descriptor = new ExperimentalToolRegistry()
            .ListTools(profile)
            .FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));
        if (descriptor == null &&
            string.Equals(toolName, "qre_sandbox_exec", StringComparison.Ordinal) &&
            command.Count > 0)
        {
            descriptor = ResolveSandboxExecDescriptor(profile, command);
        }

        if (descriptor == null)
        {
            return Fail($"Tool is not registered for profile {profileName}: {toolName}");
        }

        var mounts = mount ?? ResolveDefaultMount(profile, descriptor);
        var commandCapabilities = ExperimentalCommandCapabilityClassifier.Classify(command, mounts);
        var decision = new ExperimentalCapabilityPolicy().Evaluate(
            new QueryRuntimeCapabilityRequest
            {
                Profile = profile,
                ToolName = descriptor.Name,
                Capabilities = descriptor.Capabilities,
                Command = command,
                CommandCapabilities = commandCapabilities,
                ExplicitApproval = approvalReason != null,
                ApprovalReason = approvalReason,
                WorkspacePath = resolvedWorkspace,
                Network = network,
                Mounts = mounts
            });
        var output = new QrePolicyCheckOutput(
            "qre.policy.check",
            profile.Name,
            descriptor.Name,
            descriptor.Capabilities,
            command,
            commandCapabilities,
            approvalReason != null,
            approvalReason,
            network.Mode,
            mounts.Mode,
            decision.Kind.ToString(),
            decision.Kind == QueryRuntimeCapabilityDecisionKind.Allow,
            decision.Reason);

        if (json)
        {
            WriteJson(output);
            return 0;
        }

        Console.WriteLine($"decision: {output.Decision}");
        Console.WriteLine($"allowed: {output.Allowed.ToString().ToLowerInvariant()}");
        Console.WriteLine($"reason: {output.Reason}");
        return 0;
    }

    private static int TraceLatest(string[] args)
    {
        var workspace = Directory.GetCurrentDirectory();
        var json = false;
        var jsonl = false;
        var runtime = "v2";
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace":
                case "-w":
                    if (++i >= args.Length)
                    {
                        return Fail("--workspace requires a path.");
                    }
                    workspace = args[i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--jsonl":
                    jsonl = true;
                    break;
                case "--runtime":
                    if (++i >= args.Length || args[i] is not ("v1" or "v2"))
                    {
                        return Fail("--runtime requires v1 or v2.");
                    }
                    runtime = args[i];
                    break;
                default:
                    return Fail($"Unknown trace latest option: {args[i]}");
            }
        }

        if (json && jsonl)
        {
            return Fail("--json and --jsonl cannot be used together.");
        }

        if (runtime == "v2")
        {
            return TraceLatestV2(workspace, json, jsonl);
        }

        if (!TryFindLatestTraceFile(workspace, out var traceFile, out var error))
        {
            return Fail(error);
        }

        if (jsonl)
        {
            WriteTraceJsonl(traceFile);
            return 0;
        }

        var replay = ReadReplaySummary(traceFile);
        var completed = replay.TerminalRecord;
        if (json)
        {
            WriteJson(new QreTraceLatestOutput(
                "qre.trace.latest",
                replay.RunId,
                traceFile,
                replay.RunDirectory,
                replay.ManifestPath,
                replay.EventCount,
                completed));
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(replay.RunId))
        {
            Console.WriteLine($"run_id: {replay.RunId}");
        }
        Console.WriteLine($"trace: {traceFile}");
        Console.WriteLine($"run_directory: {replay.RunDirectory}");
        Console.WriteLine($"events: {replay.EventCount}");
        if (completed != null)
        {
            Console.WriteLine(completed.Value.ToString());
        }

        return 0;
    }

    private static int TraceLatestV2(string workspace, bool json, bool jsonl)
    {
        try
        {
            var auditFile = RuntimeJsonlAuditStore.FindLatestAuditFile(workspace);
            if (jsonl)
            {
                foreach (var line in File.ReadLines(auditFile))
                {
                    Console.WriteLine(line);
                }
                return 0;
            }

            var recording = RuntimeJsonlAuditStore.Read(auditFile);
            var terminal = recording.Events.LastOrDefault(static auditEvent =>
                auditEvent.Kind == RuntimeAuditEventKind.TurnTerminal)?.Payload;
            var status = terminal switch
            {
                RuntimeTurnTerminalAuditPayload payload => payload.Status.ToString(),
                RuntimePublicAuditPayload payload => payload.TurnStatus?.ToString(),
                _ => null
            };
            var termination = terminal switch
            {
                RuntimeTurnTerminalAuditPayload payload => payload.TerminationReason.ToString(),
                RuntimePublicAuditPayload payload => payload.TerminationReason?.ToString(),
                _ => null
            };
            var errorCode = terminal switch
            {
                RuntimeTurnTerminalAuditPayload payload => payload.Error?.Code,
                RuntimePublicAuditPayload payload => payload.ErrorCode,
                _ => null
            };
            var runDirectory = Path.GetDirectoryName(auditFile)!;
            var output = new QreV2TraceLatestOutput(
                "qre.v2.trace.latest",
                auditFile,
                runDirectory,
                Path.Combine(runDirectory, "manifest.json"),
                recording.Events.Count,
                RuntimeAuditSchema.CurrentVersion,
                recording.DataMode.ToString(),
                recording.ReplayCapability.ToString(),
                status,
                termination,
                errorCode);
            if (json)
            {
                WriteJson(output);
                return 0;
            }

            Console.WriteLine($"audit: {output.AuditFilePath}");
            Console.WriteLine($"run_directory: {output.RunDirectory}");
            Console.WriteLine($"events: {output.EventCount}");
            Console.WriteLine($"schema_version: {output.SchemaVersion}");
            Console.WriteLine($"data_mode: {output.DataMode}");
            Console.WriteLine($"replay_capability: {output.ReplayCapability}");
            if (output.Status != null)
            {
                Console.WriteLine($"status: {output.Status}");
                Console.WriteLine($"termination: {output.TerminationReason}");
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return Fail($"Could not safely read the latest v2 audit: {ex.Message}");
        }
    }

    private static void WriteTraceJsonl(string traceFile)
    {
        var records = JsonlTraceStore.ReadRecords(traceFile);
        var runId = JsonlTraceStore.TryReadRunId(records);

        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            WriteJson(new QreTraceJsonlEvent(
                "qre.trace.event",
                runId,
                i,
                record.Type,
                record.TryGetLong("Seq"),
                record.TryGetString("Timestamp"),
                CreatePublicTracePayload(record)));
        }
    }

    private static JsonElement CreatePublicTracePayload(JsonlTraceNodeRecord record)
    {
        if (record.Root.TryGetProperty("Data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            return data.Clone();
        }

        var node = JsonNode.Parse(record.Root.GetRawText()) as JsonObject ?? [];
        node.Remove("Type");
        node.Remove("type");
        node.Remove("Seq");
        node.Remove("seq");
        node.Remove("RuntimeEventType");
        node.Remove("runtimeEventType");
        node.Remove("Timestamp");
        node.Remove("timestamp");
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static async Task<int> Replay(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintReplayHelp();
            return 0;
        }

        return args[0] switch
        {
            "latest" => await ReplayLatestAsync(args[1..], ct).ConfigureAwait(false),
            _ => Fail($"Unknown replay command: {args[0]}")
        };
    }

    private static async Task<int> Rerun(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintRerunHelp();
            return 0;
        }

        return args[0] switch
        {
            "latest" => await RerunLatestAsync(args[1..], ct).ConfigureAwait(false),
            _ => Fail($"Unknown rerun command: {args[0]}")
        };
    }

    private static async Task<int> RerunLatestAsync(string[] args, CancellationToken ct)
    {
        var workspace = Directory.GetCurrentDirectory();
        string? response = null;
        string? profileOverride = null;
        QueryRuntimeTraceDataMode? traceDataMode = null;
        var json = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace":
                case "-w":
                    if (++i >= args.Length)
                    {
                        return Fail("--workspace requires a path.");
                    }
                    workspace = args[i];
                    break;
                case "--response":
                    if (++i >= args.Length)
                    {
                        return Fail("--response requires text.");
                    }
                    response = args[i];
                    break;
                case "--profile":
                case "--tools":
                    if (++i >= args.Length)
                    {
                        return Fail($"{args[i - 1]} requires none, readonly, verify, or repair.");
                    }
                    profileOverride = args[i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--trace-data":
                    if (++i >= args.Length || !TryParseTraceDataMode(args[i], out var parsedTraceDataMode))
                    {
                        return Fail("--trace-data requires public, private, or sanitized.");
                    }
                    traceDataMode = parsedTraceDataMode;
                    break;
                default:
                    return Fail($"Unknown rerun latest option: {args[i]}");
            }
        }

        string auditFile;
        RuntimeAuditRecording recording;
        try
        {
            auditFile = RuntimeJsonlAuditStore.FindLatestAuditFile(workspace);
            recording = RuntimeJsonlAuditStore.Read(auditFile, ct: ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return Fail($"Could not safely read the latest v2 audit: {ex.Message}");
        }

        var started = recording.Events
            .FirstOrDefault(static auditEvent => auditEvent.Kind == RuntimeAuditEventKind.TurnStarted)?
            .Payload as RuntimeTurnStartedAuditPayload;
        if (started == null || string.IsNullOrWhiteSpace(started.Objective))
        {
            return Fail(
                $"Latest v2 audit is {recording.DataMode} and has no rerunnable objective. " +
                "Create the source run with --trace-data sanitized or private.");
        }

        var prompt = started.Objective;
        var profile = profileOverride ?? started.Policy.Profile;

        var runArgs = new List<string>
        {
            "--workspace",
            Path.GetFullPath(workspace),
            "--profile",
            profile
        };

        if (response != null)
        {
            runArgs.Add("--response");
            runArgs.Add(response);
        }

        if (json)
        {
            runArgs.Add("--json");
        }

        if (traceDataMode.HasValue)
        {
            runArgs.Add("--trace-data");
            runArgs.Add(traceDataMode.Value switch
            {
                QueryRuntimeTraceDataMode.PrivateDiagnostic => "private",
                QueryRuntimeTraceDataMode.SanitizedFixture => "sanitized",
                _ => "public"
            });
        }

        runArgs.Add(prompt);
        return await RunQueryAsync(runArgs.ToArray(), ct).ConfigureAwait(false);
    }

    private static async Task<int> Diff(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintDiffHelp();
            return 0;
        }

        return args[0] switch
        {
            "latest" => await DiffLatest(args[1..], ct).ConfigureAwait(false),
            _ => Fail($"Unknown diff command: {args[0]}")
        };
    }

    private static async Task<int> Sandbox(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintSandboxHelp();
            return 0;
        }

        return args[0] switch
        {
            "exec" => await SandboxExec(args[1..], ct).ConfigureAwait(false),
            _ => Fail($"Unknown sandbox command: {args[0]}")
        };
    }

    private static async Task<int> SandboxExec(string[] args, CancellationToken ct)
    {
        var workspace = Directory.GetCurrentDirectory();
        string? workspaceRoot = null;
        var profileName = "verify";
        var runnerOption = "local";
        string? dockerImage = null;
        var json = false;
        var timeoutSeconds = 120;
        var maxOutputBytes = 1024 * 1024;
        var traceOptions = new QueryRuntimeTraceOptions();
        string? approvalReason = null;
        IReadOnlyList<string> command = [];

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--":
                    command = args[(i + 1)..];
                    i = args.Length;
                    break;
                case "--workspace":
                case "-w":
                    if (++i >= args.Length)
                    {
                        return Fail("--workspace requires a path.");
                    }
                    workspace = args[i];
                    break;
                case "--workspace-root":
                    if (++i >= args.Length)
                    {
                        return Fail("--workspace-root requires a path.");
                    }
                    workspaceRoot = args[i];
                    break;
                case "--profile":
                    if (++i >= args.Length)
                    {
                        return Fail("--profile requires verify.");
                    }
                    profileName = args[i];
                    break;
                case "--runner":
                    if (++i >= args.Length)
                    {
                        return Fail("--runner requires local or docker.");
                    }
                    runnerOption = args[i];
                    break;
                case "--docker-image":
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    {
                        return Fail("--docker-image requires an image name.");
                    }
                    dockerImage = args[i];
                    break;
                case "--timeout-seconds":
                    if (++i >= args.Length || !int.TryParse(args[i], out timeoutSeconds) || timeoutSeconds <= 0)
                    {
                        return Fail("--timeout-seconds requires a positive integer.");
                    }
                    break;
                case "--max-output-bytes":
                    if (++i >= args.Length || !int.TryParse(args[i], out maxOutputBytes) || maxOutputBytes <= 0)
                    {
                        return Fail("--max-output-bytes requires a positive integer.");
                    }
                    break;
                case "--json":
                    json = true;
                    break;
                case "--trace-data":
                    if (++i >= args.Length || !TryParseTraceDataMode(args[i], out var traceDataMode))
                    {
                        return Fail("--trace-data requires public, private, or sanitized.");
                    }
                    traceOptions = new QueryRuntimeTraceOptions { DataMode = traceDataMode };
                    break;
                case "--approve-risk":
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    {
                        return Fail("--approve-risk requires a reason.");
                    }
                    approvalReason = args[i];
                    break;
                default:
                    return Fail($"Unknown sandbox exec option: {args[i]}");
            }
        }

        if (command.Count == 0)
        {
            return Fail("sandbox exec requires a command after --.");
        }

        var resolvedWorkspace = Path.GetFullPath(workspace);
        if (!Directory.Exists(resolvedWorkspace))
        {
            return Fail($"Workspace does not exist: {resolvedWorkspace}");
        }
        var resolvedWorkspaceRoot = workspaceRoot == null ? null : Path.GetFullPath(workspaceRoot);
        if (resolvedWorkspaceRoot != null && !Directory.Exists(resolvedWorkspaceRoot))
        {
            return Fail($"Workspace root does not exist: {resolvedWorkspaceRoot}");
        }

        var runner = CreateSandboxRunner(
            runnerOption,
            dockerImage,
            out var runnerName,
            out var runnerConfiguration,
            out var runnerError);
        if (runner == null)
        {
            return Fail(runnerError);
        }

        var profile = new QueryRuntimeToolProfile(profileName);
        var descriptor = ResolveSandboxExecDescriptor(profile, command);
        if (descriptor == null)
        {
            return Fail($"Command is not registered for sandbox exec profile {profileName}: {string.Join(' ', command)}");
        }

        var mount = ResolveDefaultMount(profile, descriptor);
        var commandCapabilities = ExperimentalCommandCapabilityClassifier.Classify(command, mount);
        var decision = new ExperimentalCapabilityPolicy().Evaluate(
            new QueryRuntimeCapabilityRequest
            {
                Profile = profile,
                ToolName = descriptor.Name,
                Capabilities = descriptor.Capabilities,
                Command = command,
                CommandCapabilities = commandCapabilities,
                ExplicitApproval = approvalReason != null,
                ApprovalReason = approvalReason,
                WorkspacePath = resolvedWorkspace,
                Network = SandboxNetworkPolicy.Deny,
                Mounts = mount
            });

        SandboxResult? result = null;
        if (decision.Kind == QueryRuntimeCapabilityDecisionKind.Allow)
        {
            result = await runner.RunAsync(
                new SandboxJobSpec
                {
                    Command = command,
                    WorkingDirectory = resolvedWorkspace,
                    WorkspaceRoot = resolvedWorkspaceRoot,
                    Environment = TrustedLocalSandboxEnvironment.Create(),
                    Limits = new SandboxLimits
                    {
                        Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                        MaxOutputBytes = maxOutputBytes
                    },
                    Network = SandboxNetworkPolicy.Deny,
                    Mounts = mount
                },
                ct).ConfigureAwait(false);
        }

        var output = new QreSandboxExecOutput(
            "qre.sandbox.exec",
            null,
            profile.Name,
            runnerName,
            runnerConfiguration,
            descriptor.Name,
            descriptor.Capabilities,
            command,
            commandCapabilities,
            approvalReason != null,
            approvalReason,
            resolvedWorkspace,
            SandboxNetworkPolicy.Deny.Mode,
            mount.Mode,
            decision.Kind.ToString(),
            decision.Kind == QueryRuntimeCapabilityDecisionKind.Allow,
            decision.Reason,
            result?.ExitCode,
            result?.TimedOut,
            result?.DurationMs,
            result?.StandardOutput,
            result?.StandardError);
        var traceFilePath = await WriteSandboxExecTraceAsync(output with
        {
            TraceFilePath = null
        }, traceOptions).ConfigureAwait(false);
        output = output with { TraceFilePath = traceFilePath };

        if (json)
        {
            WriteJson(output);
        }
        else
        {
            Console.WriteLine($"decision: {output.Decision}");
            Console.WriteLine($"allowed: {output.Allowed.ToString().ToLowerInvariant()}");
            Console.WriteLine($"reason: {output.Reason}");
            if (result != null)
            {
                Console.WriteLine($"exit_code: {result.ExitCode}");
                if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    Console.WriteLine(result.StandardOutput);
                }
                if (!string.IsNullOrWhiteSpace(result.StandardError))
                {
                    Console.Error.WriteLine(result.StandardError);
                }
            }
        }

        if (decision.Kind != QueryRuntimeCapabilityDecisionKind.Allow)
        {
            return 1;
        }

        return result?.ExitCode ?? 1;
    }

    private static async Task<string> WriteSandboxExecTraceAsync(
        QreSandboxExecOutput output,
        QueryRuntimeTraceOptions traceOptions)
    {
        var includeSensitiveData = traceOptions.DataMode != QueryRuntimeTraceDataMode.PublicRedacted;
        IReadOnlySet<string> visibleCapabilities = includeSensitiveData
            ? output.Capabilities
            : new HashSet<string>(StringComparer.Ordinal);
        IReadOnlySet<string> visibleCommandCapabilities = includeSensitiveData
            ? output.CommandCapabilities
            : new HashSet<string>(StringComparer.Ordinal);
        var visibleProfile = includeSensitiveData ? output.Profile : "[redacted]";
        var visibleTool = includeSensitiveData ? output.Tool : "[redacted]";
        var visibleRunner = output.Runner is "local" or "docker" ? output.Runner : "[redacted]";
        var visibleNetwork = output.Network is "none" or "default" ? output.Network : "[redacted]";
        var visibleMount = output.Mount is "readonly" or "readwrite" ? output.Mount : "[redacted]";
        var visibleDecision = includeSensitiveData ? output.Decision : (output.Allowed ? "allowed" : "blocked");
        var visibleConfiguration = includeSensitiveData
            ? output.RunnerConfiguration
            : QreSandboxRunnerConfiguration.PublicSummary(visibleRunner);
        var requestedRunId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        var workspaceRoot = Path.GetFullPath(output.WorkspacePath);
        var traceRoot = QueryRuntimePathSafety.ResolveUnderRoot(workspaceRoot, ".qre");
        var location = JsonlTraceEventSink.PrepareAuxiliaryRunLocation(
            traceRoot,
            requestedRunId,
            traceOptions);
        var runId = location.PersistedRunId;
        var traceFilePath = location.TraceFilePath;
        var timestamp = DateTimeOffset.UtcNow;
        var records = new object[]
        {
            new QreSandboxExecStartedTraceRecord(
                "sandbox.exec.started",
                runId,
                includeSensitiveData ? output.WorkspacePath : "[redacted]",
                visibleProfile,
                visibleRunner,
                visibleConfiguration,
                visibleTool,
                visibleCapabilities,
                includeSensitiveData ? output.Command : [],
                visibleCommandCapabilities,
                output.ExplicitApproval,
                includeSensitiveData ? output.ApprovalReason : null,
                visibleNetwork,
                visibleMount,
                traceOptions.DataMode.ToString(),
                traceOptions.ReplayCapability.ToString(),
                timestamp),
            new QreSandboxPolicyDecisionTraceRecord(
                "policy.decision",
                visibleProfile,
                visibleRunner,
                visibleTool,
                visibleCapabilities,
                includeSensitiveData ? output.Command : [],
                visibleCommandCapabilities,
                output.ExplicitApproval,
                includeSensitiveData ? output.ApprovalReason : null,
                visibleNetwork,
                visibleMount,
                visibleDecision,
                output.Allowed,
                includeSensitiveData ? output.Reason : "[redacted]",
                timestamp)
        };
        if (!output.Allowed)
        {
            records =
            [
                .. records,
                new QreSandboxPolicyBlockedTraceRecord(
                    output.Decision == nameof(QueryRuntimeCapabilityDecisionKind.RequireApproval)
                        ? "policy.approval_required"
                        : "policy.denied",
                    visibleProfile,
                    visibleRunner,
                    visibleTool,
                    visibleCapabilities,
                    includeSensitiveData ? output.Command : [],
                    visibleCommandCapabilities,
                    output.ExplicitApproval,
                    includeSensitiveData ? output.ApprovalReason : null,
                    visibleNetwork,
                    visibleMount,
                    visibleDecision,
                    includeSensitiveData ? output.Reason : "[redacted]",
                    timestamp)
            ];
        }

        records =
        [
            .. records,
            new QreSandboxExecCompletedTraceRecord(
                "sandbox.exec.completed",
                runId,
                output.ExitCode,
                output.TimedOut,
                output.DurationMs,
                includeSensitiveData ? output.StandardOutput : null,
                includeSensitiveData ? output.StandardError : null,
                DateTimeOffset.UtcNow)
        ];

        await File.WriteAllLinesAsync(
            traceFilePath,
            records.Select(SerializeJsonOutput)).ConfigureAwait(false);
        JsonlTraceEventSink.ApplyAuxiliaryArtifactSecurity(traceFilePath, traceOptions);
        return traceFilePath;
    }

    private static async Task AppendRunRunnerConfigurationTraceAsync(
        string traceFilePath,
        string runner,
        QreSandboxRunnerConfiguration? runnerConfiguration,
        QueryRuntimeTraceOptions traceOptions)
    {
        var isPublic = traceOptions.DataMode == QueryRuntimeTraceDataMode.PublicRedacted;
        var visibleRunner = runner is "local" or "docker" ? runner : "[redacted]";
        var persistedRunId = Path.GetFileName(JsonlTraceStore.GetRunDirectory(traceFilePath));
        var record = new QreRunRunnerConfigurationTraceRecord(
            "runner.configuration",
            persistedRunId,
            visibleRunner,
            isPublic ? QreSandboxRunnerConfiguration.PublicSummary(visibleRunner) : runnerConfiguration,
            DateTimeOffset.UtcNow);
        await File.AppendAllTextAsync(
            traceFilePath,
            SerializeJsonOutput(record) + Environment.NewLine).ConfigureAwait(false);
    }

    private static async Task FinalizeRunArtifactsAsync(
        string traceFilePath,
        string workspacePath,
        int totalRounds,
        int totalToolCalls,
        long totalDurationMs,
        QueryRuntimeTraceOptions traceOptions,
        CancellationToken ct)
    {
        var runDirectory = JsonlTraceStore.GetRunDirectory(traceFilePath);
        var artifactsDirectory = Path.Combine(runDirectory, "artifacts");
        Directory.CreateDirectory(artifactsDirectory);
        JsonlTraceEventSink.ApplyAuxiliaryArtifactSecurity(
            artifactsDirectory,
            traceOptions,
            isDirectory: true);
        await WriteRunDiffPatchAsync(
            runDirectory,
            workspacePath,
            ct,
            includeSensitiveData: traceOptions.DataMode != QueryRuntimeTraceDataMode.PublicRedacted).ConfigureAwait(false);

        var persistedRunId = Path.GetFileName(runDirectory);
        var usage = BuildBudgetUsage(
            traceFilePath,
            persistedRunId,
            totalRounds,
            totalToolCalls,
            totalDurationMs);
        var usagePath = Path.Combine(runDirectory, "usage.json");
        await File.WriteAllTextAsync(
            usagePath,
            SerializeJsonOutput(usage) + Environment.NewLine,
            ct).ConfigureAwait(false);
        JsonlTraceEventSink.ApplyAuxiliaryArtifactSecurity(usagePath, traceOptions);
        await File.AppendAllTextAsync(
            traceFilePath,
            SerializeJsonOutput(usage) + Environment.NewLine,
            ct).ConfigureAwait(false);
        JsonlTraceEventSink.ApplyAuxiliaryArtifactSecurity(traceFilePath, traceOptions);

        var diffPath = Path.Combine(runDirectory, "diff.patch");
        if (File.Exists(diffPath))
        {
            JsonlTraceEventSink.ApplyAuxiliaryArtifactSecurity(diffPath, traceOptions);
        }
    }

    internal static async Task WriteRunDiffPatchAsync(
        string runDirectory,
        string workspacePath,
        CancellationToken ct,
        bool includeSensitiveData = true)
    {
        var diffPath = Path.Combine(runDirectory, "diff.patch");
        var editedPaths = includeSensitiveData
            ? await TryReadRunEditedPathsAsync(runDirectory, ct).ConfigureAwait(false)
            : [];
        var diff = editedPaths.Count == 0
            ? string.Empty
            : await TryReadGitDiffPatchAsync(workspacePath, editedPaths, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(diffPath, diff ?? string.Empty, ct).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<string>> TryReadRunEditedPathsAsync(
        string runDirectory,
        CancellationToken ct)
    {
        var editsPath = Path.Combine(runDirectory, "repair-edits.txt");
        if (!File.Exists(editsPath))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(editsPath, ct).ConfigureAwait(false);
        return lines
            .Select(static line => line.Trim().Replace('\\', '/'))
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Where(static line => !line.Equals(".qre", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith(".qre/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    internal static async Task<string?> TryReadGitDiffPatchAsync(
        string workspacePath,
        IReadOnlyList<string> editedPaths,
        CancellationToken ct)
    {
        var insideWorkTree = await TryRunGitForStdoutAsync(
            workspacePath,
            ["rev-parse", "--is-inside-work-tree"],
            [0],
            ct).ConfigureAwait(false);
        if (!string.Equals(insideWorkTree?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tempIndexPath = Path.Combine(Path.GetTempPath(), $"qre-git-index-{Guid.NewGuid():N}");
        var tempGitEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_INDEX_FILE"] = tempIndexPath
        };

        try
        {
            var headExists = await TryRunGitForStdoutAsync(
                workspacePath,
                ["rev-parse", "--verify", "HEAD"],
                [0],
                ct).ConfigureAwait(false) != null;
            var baseTree = headExists ? "HEAD" : EmptyGitTreeHash;
            var readTreeArgs = headExists
                ? new[] { "read-tree", "HEAD" }
                : new[] { "read-tree", "--empty" };
            var readTree = await TryRunGitForStdoutAsync(
                workspacePath,
                readTreeArgs,
                [0],
                ct,
                tempGitEnvironment).ConfigureAwait(false);
            if (readTree == null)
            {
                return null;
            }

            var files = await TryReadGitWorkspaceFilesForPatchAsync(workspacePath, editedPaths, ct).ConfigureAwait(false);
            if (files == null)
            {
                return null;
            }

            if (files.Count > 0)
            {
                var updateIndexInput = string.Join('\0', files) + '\0';
                var updateIndex = await TryRunGitForStdoutAsync(
                    workspacePath,
                    ["update-index", "--add", "--remove", "-z", "--stdin"],
                    [0],
                    ct,
                    tempGitEnvironment,
                    updateIndexInput).ConfigureAwait(false);
                if (updateIndex == null)
                {
                    return null;
                }
            }

            return await TryRunGitForStdoutAsync(
                workspacePath,
                ["diff", "--cached", "--no-ext-diff", "--binary", baseTree],
                [0],
                ct,
                tempGitEnvironment).ConfigureAwait(false);
        }
        finally
        {
            DeleteQuietly(tempIndexPath);
            DeleteQuietly(tempIndexPath + ".lock");
        }
    }

    internal static async Task<IReadOnlyList<string>?> TryReadGitWorkspaceFilesForPatchAsync(
        string workspacePath,
        IReadOnlyList<string> editedPaths,
        CancellationToken ct)
    {
        if (editedPaths.Count == 0)
        {
            return [];
        }

        var pathComparer = GetFileSystemPathComparer();
        var edited = new HashSet<string>(editedPaths, pathComparer);
        var stdout = await TryRunGitForStdoutAsync(
            workspacePath,
            ["ls-files", "-z", "--cached", "--modified", "--deleted", "--others", "--exclude-standard"],
            [0],
            ct).ConfigureAwait(false);
        if (stdout == null)
        {
            return null;
        }

        return stdout
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(static path => !path.Equals(".qre", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith(".qre/", StringComparison.OrdinalIgnoreCase))
            .Where(edited.Contains)
            .Distinct(pathComparer)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static StringComparer GetFileSystemPathComparer()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private const string EmptyGitTreeHash = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task<string?> TryRunGitForStdoutAsync(
        string workspacePath,
        IReadOnlyList<string> arguments,
        IReadOnlyCollection<int> successExitCodes,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workspacePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput != null,
                UseShellExecute = false
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            if (environment != null)
            {
                foreach (var pair in environment)
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return null;
            }

            if (standardInput != null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return successExitCodes.Contains(process.ExitCode) ? stdout : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static QreBudgetUsageTraceRecord BuildBudgetUsage(
        string traceFilePath,
        string runId,
        int totalRounds,
        int totalToolCalls,
        long totalDurationMs)
    {
        var records = JsonlTraceStore.ReadRecords(traceFilePath);
        var promptChars = records
            .Where(static record => record.Type == "run.started")
            .Select(static record => record.TryGetString("Prompt")?.Length ?? 0)
            .Sum();
        var assistantChars = 0;
        var toolOutputChars = 0;

        foreach (var record in records)
        {
            if (!record.TryGetData(out var data))
            {
                continue;
            }

            if (record.Type == "model.response")
            {
                assistantChars += ReadTraceTextLength(data, "AssistantText", "AssistantTextLength");
            }
            else if (record.Type == "tool.execution.completed")
            {
                toolOutputChars += ReadTraceTextLength(data, "Result", "ResultLength");
            }
        }

        var promptTokens = EstimateTokens(promptChars);
        var completionTokens = EstimateTokens(assistantChars);
        var toolOutputTokens = EstimateTokens(toolOutputChars);
        return new QreBudgetUsageTraceRecord(
            "budget.usage",
            runId,
            true,
            promptChars,
            assistantChars,
            toolOutputChars,
            promptTokens,
            completionTokens,
            toolOutputTokens,
            promptTokens + completionTokens + toolOutputTokens,
            null,
            totalRounds,
            totalToolCalls,
            totalDurationMs,
            DateTimeOffset.UtcNow);
    }

    private static int ReadTraceTextLength(JsonElement data, string inlineProperty, string lengthProperty)
    {
        if (data.TryGetProperty(lengthProperty, out var length) &&
            length.ValueKind == JsonValueKind.Number &&
            length.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        return data.TryGetProperty(inlineProperty, out var inline) && inline.ValueKind == JsonValueKind.String
            ? inline.GetString()?.Length ?? 0
            : 0;
    }

    private static int EstimateTokens(int chars)
        => chars <= 0 ? 0 : (int)Math.Ceiling(chars / 4.0);

    private static async Task<int> Doctor(string[] args, CancellationToken ct)
    {
        var workspace = Directory.GetCurrentDirectory();
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace":
                case "-w":
                    if (++i >= args.Length)
                    {
                        return Fail("--workspace requires a path.");
                    }
                    workspace = args[i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--help":
                case "-h":
                    PrintDoctorHelp();
                    return 0;
                default:
                    return Fail($"Unknown doctor option: {args[i]}");
            }
        }

        var resolvedWorkspace = Path.GetFullPath(workspace);
        var checks = new List<QreDoctorCheck>();
        var workspaceExists = Directory.Exists(resolvedWorkspace);
        checks.Add(new QreDoctorCheck(
            "workspace",
            workspaceExists ? "pass" : "fail",
            workspaceExists ? "workspace exists" : "workspace does not exist",
            resolvedWorkspace));

        var runnerWorkspace = workspaceExists ? resolvedWorkspace : Directory.GetCurrentDirectory();
        checks.Add(await RunDiagnosticCommandAsync(
            "dotnet",
            ["dotnet", "--version"],
            runnerWorkspace,
            ct).ConfigureAwait(false));
        checks.Add(await RunDiagnosticCommandAsync(
            "git",
            ["git", "--version"],
            runnerWorkspace,
            ct).ConfigureAwait(false));

        if (workspaceExists)
        {
            var latestTrace = TryFindLatestTraceFile(resolvedWorkspace, out var traceFile, out _)
                ? traceFile
                : null;
            checks.Add(new QreDoctorCheck(
                "latest_trace",
                latestTrace == null ? "warn" : "pass",
                latestTrace == null ? "no .qre trace found" : "latest trace found",
                latestTrace));
        }

        var providerVariables =
            new[] { "QRE_API_URL", "QRE_API_KEY", "QRE_MODEL" }
                .Where(static name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                .ToArray();
        checks.Add(new QreDoctorCheck(
            "provider_env",
            providerVariables.Length == 3 ? "pass" : "warn",
            providerVariables.Length == 3
                ? "QRE provider environment appears configured"
                : "QRE provider environment is incomplete; --response can still be used for offline smoke",
            string.Join(',', providerVariables)));

        var exitCode = checks.Any(static check => check.Status == "fail") ? 1 : 0;
        var output = new QreDoctorOutput(
            "qre.doctor",
            GetVersion(),
            resolvedWorkspace,
            checks,
            exitCode == 0);

        if (json)
        {
            WriteJson(output);
            return exitCode;
        }

        Console.WriteLine($"qre: {output.Version}");
        Console.WriteLine($"workspace: {output.WorkspacePath}");
        foreach (var check in output.Checks)
        {
            Console.WriteLine($"{check.Status}\t{check.Name}\t{check.Message}");
            if (!string.IsNullOrWhiteSpace(check.Detail))
            {
                Console.WriteLine($"  {check.Detail}");
            }
        }

        return exitCode;
    }

    private static int Init(string[] args)
    {
        var workspace = Directory.GetCurrentDirectory();
        var json = false;
        var force = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace":
                case "-w":
                    if (++i >= args.Length)
                    {
                        return Fail("--workspace requires a path.");
                    }
                    workspace = args[i];
                    break;
                case "--force":
                    force = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--help":
                case "-h":
                    PrintInitHelp();
                    return 0;
                default:
                    return Fail($"Unknown init option: {args[i]}");
            }
        }

        var resolvedWorkspace = Path.GetFullPath(workspace);
        if (!Directory.Exists(resolvedWorkspace))
        {
            return Fail($"Workspace does not exist: {resolvedWorkspace}");
        }

        var qreDirectory = Path.Combine(resolvedWorkspace, ".qre");
        Directory.CreateDirectory(qreDirectory);
        var created = new List<string>();
        var skipped = new List<string>();
        WriteTemplateFile(
            Path.Combine(qreDirectory, "config.toml"),
            BuildDefaultConfigTemplate(),
            force,
            created,
            skipped);
        WriteTemplateFile(
            Path.Combine(qreDirectory, "README.md"),
            BuildQreReadmeTemplate(),
            force,
            created,
            skipped);

        var output = new QreInitOutput(
            "qre.init",
            resolvedWorkspace,
            qreDirectory,
            created,
            skipped,
            force);

        if (json)
        {
            WriteJson(output);
            return 0;
        }

        Console.WriteLine($"workspace: {resolvedWorkspace}");
        Console.WriteLine($"qre_directory: {qreDirectory}");
        foreach (var path in created)
        {
            Console.WriteLine($"created: {path}");
        }
        foreach (var path in skipped)
        {
            Console.WriteLine($"skipped: {path}");
        }

        return 0;
    }

    private static async Task<int> DiffLatest(string[] args, CancellationToken ct)
    {
        var workspace = Directory.GetCurrentDirectory();
        var json = false;
        var stat = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace":
                case "-w":
                    if (++i >= args.Length)
                    {
                        return Fail("--workspace requires a path.");
                    }
                    workspace = args[i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--stat":
                    stat = true;
                    break;
                default:
                    return Fail($"Unknown diff latest option: {args[i]}");
            }
        }

        var resolvedWorkspace = Path.GetFullPath(workspace);
        if (!Directory.Exists(resolvedWorkspace))
        {
            return Fail($"Workspace does not exist: {resolvedWorkspace}");
        }

        string? runId = null;
        string? runDirectory = null;
        string? manifestPath = null;
        if (TryFindLatestTraceFile(resolvedWorkspace, out var latestTraceFile, out _))
        {
            var replay = ReadReplaySummary(latestTraceFile);
            runId = replay.RunId;
            runDirectory = replay.RunDirectory;
            manifestPath = replay.ManifestPath;
            var runDiffPath = Path.Combine(replay.RunDirectory, "diff.patch");
            if (!stat && File.Exists(runDiffPath))
            {
                var patch = await File.ReadAllTextAsync(runDiffPath, ct).ConfigureAwait(false);
                var runDiffOutput = string.IsNullOrWhiteSpace(patch) ? "(no git diff)" : patch;
                if (json)
                {
                    WriteJson(new QreDiffLatestOutput(
                        "qre.diff.latest",
                        resolvedWorkspace,
                        "run-diff-patch",
                        runId,
                        runDirectory,
                        manifestPath,
                        stat,
                        0,
                        false,
                        0,
                        "(from latest run)",
                        runDiffOutput,
                        null));
                    return 0;
                }

                Console.WriteLine("status:");
                Console.WriteLine("(from latest run)");
                Console.WriteLine();
                Console.WriteLine(stat ? "diff_stat:" : "diff:");
                Console.WriteLine(runDiffOutput);
                return 0;
            }
        }

        var statusResult = await new LocalProcessSandboxRunner().RunAsync(
            new SandboxJobSpec
            {
                Command = ["git", "status", "--short"],
                WorkingDirectory = resolvedWorkspace,
                Environment = TrustedLocalSandboxEnvironment.Create(),
                Limits = new SandboxLimits
                {
                    Timeout = TimeSpan.FromSeconds(30),
                    MaxOutputBytes = 256 * 1024
                },
                Network = SandboxNetworkPolicy.Deny,
                Mounts = SandboxMountPolicy.WorkspaceReadOnly
            },
            ct).ConfigureAwait(false);

        var diffCommand = stat
            ? new[] { "git", "diff", "--stat" }
            : ["git", "diff"];
        var diffResult = await new LocalProcessSandboxRunner().RunAsync(
            new SandboxJobSpec
            {
                Command = diffCommand,
                WorkingDirectory = resolvedWorkspace,
                Environment = TrustedLocalSandboxEnvironment.Create(),
                Limits = new SandboxLimits
                {
                    Timeout = TimeSpan.FromSeconds(30),
                    MaxOutputBytes = 1024 * 1024
                },
                Network = SandboxNetworkPolicy.Deny,
                Mounts = SandboxMountPolicy.WorkspaceReadOnly
            },
            ct).ConfigureAwait(false);

        var output = string.IsNullOrWhiteSpace(diffResult.StandardOutput)
            ? "(no git diff)"
            : diffResult.StandardOutput;
        var status = string.IsNullOrWhiteSpace(statusResult.StandardOutput)
            ? "(clean)"
            : statusResult.StandardOutput;
        var stderr = CombineNonEmpty(statusResult.StandardError, diffResult.StandardError);
        var exitCode = statusResult.ExitCode != 0 ? statusResult.ExitCode : diffResult.ExitCode;
        var timedOut = statusResult.TimedOut || diffResult.TimedOut;
        if (json)
        {
            WriteJson(new QreDiffLatestOutput(
                "qre.diff.latest",
                resolvedWorkspace,
                "workspace-git-diff",
                runId,
                runDirectory,
                manifestPath,
                stat,
                exitCode,
                timedOut,
                statusResult.DurationMs + diffResult.DurationMs,
                status,
                output,
                string.IsNullOrWhiteSpace(stderr) ? null : stderr));
            return exitCode == 0 ? 0 : 1;
        }

        Console.WriteLine("status:");
        Console.WriteLine(status);
        if (!string.IsNullOrWhiteSpace(runId))
        {
            Console.WriteLine($"run_id: {runId}");
            Console.WriteLine($"run_directory: {runDirectory}");
        }
        Console.WriteLine("diff:");
        Console.WriteLine(output);
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Console.Error.WriteLine(stderr);
        }

        return exitCode == 0 ? 0 : 1;
    }

    private static async Task<int> ReplayLatestAsync(string[] args, CancellationToken ct)
    {
        var workspace = Directory.GetCurrentDirectory();
        var runtime = "v2";
        var json = false;
        var summaryOnly = false;
        var strict = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace":
                case "-w":
                    if (++i >= args.Length)
                    {
                        return Fail("--workspace requires a path.");
                    }
                    workspace = args[i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--summary":
                    summaryOnly = true;
                    break;
                case "--strict":
                    strict = true;
                    break;
                case "--runtime":
                    if (++i >= args.Length || args[i] is not ("v1" or "v2"))
                    {
                        return Fail("--runtime requires v1 or v2.");
                    }
                    runtime = args[i];
                    break;
                default:
                    return Fail($"Unknown replay latest option: {args[i]}");
            }
        }

        if (runtime == "v2")
        {
            return ReplayLatestV2(workspace, json, summaryOnly, strict, ct);
        }

        if (!summaryOnly)
        {
            return Fail("v1 recorded execution replay is disabled after the v2-only cutover. Use --summary to inspect legacy traces.");
        }

        if (!TryFindLatestTraceFile(workspace, out var traceFile, out var error))
        {
            return Fail(error);
        }

        var summary = ReadReplaySummary(traceFile);
        if (summaryOnly)
        {
            if (json)
            {
                WriteJson(summary);
                return 0;
            }

            PrintReplaySummary(summary);
            return 0;
        }

        if (!summary.StrictReplayable)
        {
            if (string.Equals(
                    summary.ReplayCapability,
                    QueryRuntimeReplayCapability.SummaryOnly.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return Fail(
                    $"Latest trace is summary-only ({summary.DataMode}) and cannot be replayed. " +
                    "Create a sanitized fixture or explicitly opt in to private diagnostic trace data.");
            }

            return Fail($"Latest trace is not strict-replayable: {string.Join(", ", summary.MissingReplayRecords)}");
        }

        var compatibility = QueryRuntimeTraceSchema.GetReplayCompatibility(summary.SchemaVersion, strict);
        if (!compatibility.Compatible)
        {
            return Fail(compatibility.Reason ??
                $"Latest trace is not replay-compatible at schema version {summary.SchemaVersion}.");
        }

        if (strict)
        {
            return await ExecuteStrictReplayAsync(workspace, traceFile, summary, json, ct).ConfigureAwait(false);
        }

        return await ExecuteReplayAsync(workspace, traceFile, summary, json, ct).ConfigureAwait(false);
    }

    private static async Task FinalizeV2RunArtifactsAsync(
        string runDirectory,
        string workspacePath,
        string prompt,
        RuntimeTurnResult result,
        QueryRuntimeTraceOptions traceOptions,
        CancellationToken ct)
    {
        var artifactsDirectory = Path.Combine(runDirectory, "artifacts");
        Directory.CreateDirectory(artifactsDirectory);
        JsonlTraceEventSink.ApplyAuxiliaryArtifactSecurity(
            artifactsDirectory,
            traceOptions,
            isDirectory: true);
        await WriteRunDiffPatchAsync(
            runDirectory,
            workspacePath,
            ct,
            includeSensitiveData: traceOptions.DataMode != QueryRuntimeTraceDataMode.PublicRedacted).ConfigureAwait(false);

        var promptChars = prompt.Length;
        var assistantChars = result.FinalText.Length;
        var promptTokens = checked((int)Math.Min(
            result.Usage.InputTokens > 0 ? result.Usage.InputTokens : EstimateTokens(promptChars),
            int.MaxValue));
        var completionTokens = checked((int)Math.Min(
            result.Usage.OutputTokens > 0 ? result.Usage.OutputTokens : EstimateTokens(assistantChars),
            int.MaxValue));
        var usage = new QreBudgetUsageTraceRecord(
            "budget.usage",
            Path.GetFileName(runDirectory),
            Estimated: result.Usage.InputTokens <= 0 || result.Usage.OutputTokens <= 0,
            PromptChars: promptChars,
            AssistantChars: assistantChars,
            ToolOutputChars: 0,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            ToolOutputTokens: 0,
            TotalTokens: (int)Math.Min((long)promptTokens + completionTokens, int.MaxValue),
            EstimatedUsd: null,
            TotalRounds: result.Turn.Steps.Count,
            TotalToolCalls: result.Turn.Progress.ToolCallCount,
            TotalDurationMs: 0,
            Timestamp: DateTimeOffset.UtcNow);
        var usagePath = Path.Combine(runDirectory, "usage.json");
        await File.WriteAllTextAsync(
            usagePath,
            SerializeJsonOutput(usage) + Environment.NewLine,
            ct).ConfigureAwait(false);
        JsonlTraceEventSink.ApplyAuxiliaryArtifactSecurity(usagePath, traceOptions);

        var diffPath = Path.Combine(runDirectory, "diff.patch");
        JsonlTraceEventSink.ApplyAuxiliaryArtifactSecurity(diffPath, traceOptions);
    }

    private static int ReplayLatestV2(
        string workspace,
        bool json,
        bool summaryOnly,
        bool strict,
        CancellationToken ct)
    {
        try
        {
            var auditFile = RuntimeJsonlAuditStore.FindLatestAuditFile(workspace);
            var recording = RuntimeJsonlAuditStore.Read(auditFile, ct: ct);
            if (summaryOnly)
            {
                var summary = new QreV2ReplayOutput(
                    "qre.v2.replay.summary",
                    "summary",
                    RuntimeAuditSchema.CurrentVersion,
                    recording.DataMode.ToString(),
                    recording.ReplayCapability.ToString(),
                    recording.Events.Count,
                    ProviderCalls: false,
                    ToolExecutions: false,
                    auditFile);
                QreV2CliPresentation.WriteReplayOutput(summary, json);
                return 0;
            }

            if (recording.ReplayCapability != RuntimeAuditReplayCapability.Recorded ||
                recording.DataMode == RuntimeAuditDataMode.PublicRedacted)
            {
                return Fail(
                    $"Latest v2 audit is summary-only ({recording.DataMode}) and cannot be replayed. " +
                    "Run with --trace-data sanitized, or explicitly opt in to private diagnostic data.");
            }

            var replay = RuntimeRecordedReplay.Replay(recording);
            var output = new QreV2ReplayOutput(
                "qre.v2.replay.completed",
                strict ? "strict-recorded-replay" : "recorded-replay",
                RuntimeAuditSchema.CurrentVersion,
                recording.DataMode.ToString(),
                recording.ReplayCapability.ToString(),
                replay.EventCount,
                replay.ProviderCalls,
                replay.ToolExecutions,
                auditFile)
            {
                FinalText = replay.FinalText,
                Status = replay.Status.ToString(),
                TerminationReason = replay.TerminationReason.ToString(),
                TotalSteps = replay.TotalSteps,
                TotalToolCalls = replay.TotalToolCalls,
                ContinuationCount = replay.ContinuationCount,
                ReplayDigest = replay.ReplayDigest
            };
            QreV2CliPresentation.WriteReplayOutput(output, json);
            return 0;
        }
        catch (RuntimeAuditReplayException ex)
        {
            return Fail($"v2 replay rejected the audit ({ex.Error.Code}): {ex.Error.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return Fail($"v2 replay could not safely read the audit: {ex.Message}");
        }
    }

    private static void PrintReplaySummary(QreReplaySummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.RunId))
        {
            Console.WriteLine($"run_id: {summary.RunId}");
        }
        Console.WriteLine($"trace: {summary.TraceFilePath}");
        Console.WriteLine($"run_directory: {summary.RunDirectory}");
        if (!string.IsNullOrWhiteSpace(summary.ManifestPath))
        {
            Console.WriteLine($"manifest: {summary.ManifestPath}");
        }
        Console.WriteLine($"mode: {summary.Mode}");
        Console.WriteLine($"provider_calls: {summary.ProviderCalls}");
        Console.WriteLine($"tool_executions: {summary.ToolExecutions}");
        Console.WriteLine($"model_responses: {summary.ModelResponses}");
        Console.WriteLine($"tool_results: {summary.ToolResults}");
        Console.WriteLine($"events: {summary.EventCount}");
        Console.WriteLine($"strict_replayable: {summary.StrictReplayable.ToString().ToLowerInvariant()}");
        Console.WriteLine($"data_mode: {summary.DataMode}");
        Console.WriteLine($"replay_capability: {summary.ReplayCapability}");
        Console.WriteLine($"schema_version: {summary.SchemaVersion}");
        Console.WriteLine($"strict_replay_compatible: {summary.StrictReplayCompatible.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(summary.StrictReplayBlockedReason))
        {
            Console.WriteLine($"strict_replay_blocked_reason: {summary.StrictReplayBlockedReason}");
        }
        Console.WriteLine($"trajectory_steps: {summary.DecisionTrajectory.Count}");
        if (!string.IsNullOrWhiteSpace(summary.TerminationReason))
        {
            Console.WriteLine($"termination: {summary.TerminationReason}");
        }
    }

    private static async Task<int> ExecuteReplayAsync(
        string workspace,
        string traceFile,
        QreReplaySummary summary,
        bool json,
        CancellationToken ct)
    {
        var records = JsonlTraceStore.ReadRecords(traceFile);
        var prompt = records.FirstOrDefault(static record => record.Type == "run.started")?.TryGetString("Prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Fail($"Latest trace has no recorded prompt: {traceFile}");
        }

        var profileName = TryGetJsonString(summary.Manifest, "ToolProfile") ?? "readonly";
        var replayReadContext = new RecordedReplayReadContext();
        var tools = summary.ToolResults > 0
            ? RecordedReplayToolPack.Create(traceFile, replayReadContext)
            : [];
        var harness = new ExperimentalQueryRuntimeHarness(new RecordedReplayModelClient(traceFile, replayReadContext));
        var result = await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = prompt,
                WorkspacePath = Path.GetFullPath(workspace),
                MaxRounds = Math.Max(1, summary.DecisionTrajectory.Count(static step => step.Kind == "model")),
                EnableTools = tools.Count > 0,
                ToolProfile = new QueryRuntimeToolProfile(profileName),
                Tools = tools,
                Trace = new QueryRuntimeTraceOptions { DataMode = QueryRuntimeTraceDataMode.SanitizedFixture }
            },
            ct).ConfigureAwait(false);

        var output = new QreRunOutput(
            "qre.replay.completed",
            result.FinalText,
            result.RunId,
            result.TerminationReason,
            profileName,
            "recorded-replay",
            null,
            tools.Select(static tool => tool.Name).ToArray(),
            Path.GetFullPath(workspace),
            result.TraceFilePath,
            JsonlTraceStore.GetRunDirectory(result.TraceFilePath),
            Path.Combine(JsonlTraceStore.GetRunDirectory(result.TraceFilePath), "manifest.json"),
            result.TotalRounds,
            result.TotalToolCalls,
            result.TotalDurationMs)
        {
            TerminalDetailCode = result.TerminalDetailCode,
            ZeroToolCallRounds = result.ZeroToolCallRounds,
            ContinuationCount = result.ContinuationCount,
            WriteToolCalls = result.WriteToolCalls,
            LastFunctionCall = result.LastFunctionCall,
            RequiredToolName = result.RequiredToolName,
            RequiredToolSatisfied = result.RequiredToolSatisfied,
            ExecutedToolNames = result.ExecutedToolNames,
            SuccessfulToolNames = result.SuccessfulToolNames
        };
        await FinalizeRunArtifactsAsync(
            result.TraceFilePath,
            Path.GetFullPath(workspace),
            result.TotalRounds,
            result.TotalToolCalls,
            result.TotalDurationMs,
            new QueryRuntimeTraceOptions { DataMode = QueryRuntimeTraceDataMode.SanitizedFixture },
            ct).ConfigureAwait(false);

        if (json)
        {
            WriteJson(output);
            return 0;
        }

        Console.WriteLine(output.FinalText);
        Console.WriteLine($"run_id: {output.RunId}");
        Console.WriteLine($"trace: {output.TraceFilePath}");
        Console.WriteLine($"mode: recorded-replay");
        Console.WriteLine($"provider_calls: false");
        Console.WriteLine($"tool_executions: false");
        return 0;
    }

    private static async Task<int> ExecuteStrictReplayAsync(
        string workspace,
        string traceFile,
        QreReplaySummary summary,
        bool json,
        CancellationToken ct)
    {
        var records = JsonlTraceStore.ReadRecords(traceFile);
        var prompt = records.FirstOrDefault(static record => record.Type == "run.started")?.TryGetString("Prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Fail($"Latest trace has no recorded prompt: {traceFile}");
        }

        var seed = DeterministicReplay.ReadSeed(traceFile);
        var profileName = TryGetJsonString(summary.Manifest, "ToolProfile") ?? "readonly";
        var replayReadContext = new RecordedReplayReadContext();
        var tools = summary.ToolResults > 0
            ? RecordedReplayToolPack.Create(traceFile, replayReadContext)
            : [];

        // Strict replay seeds the engine with a deterministic clock + query id from the
        // source trace so repeated replays produce a byte-identical canonical projection.
        // It never instantiates a provider chat client (RecordedReplayModelClient) and
        // never executes the original tools (RecordedReplayToolPack returns recorded output).
        var harness = new ExperimentalQueryRuntimeHarness(new RecordedReplayModelClient(traceFile, replayReadContext));
        var result = await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = prompt,
                WorkspacePath = Path.GetFullPath(workspace),
                MaxRounds = Math.Max(1, summary.DecisionTrajectory.Count(static step => step.Kind == "model")),
                EnableTools = tools.Count > 0,
                ToolProfile = new QueryRuntimeToolProfile(profileName),
                Tools = tools,
                TimeProvider = new DeterministicReplayClock(seed.BaseTimestamp),
                QueryIdFactory = () => seed.QueryId,
                Trace = new QueryRuntimeTraceOptions { DataMode = QueryRuntimeTraceDataMode.SanitizedFixture }
            },
            ct).ConfigureAwait(false);

        var replayDigest = DeterministicReplay.ComputeCanonicalDigest(result.TraceFilePath);
        var output = new QreStrictReplayOutput(
            "qre.replay.completed",
            "strict-replay",
            result.FinalText,
            summary.RunId,
            result.RunId,
            result.TerminationReason,
            profileName,
            summary.SchemaVersion,
            replayDigest,
            ProviderCalls: false,
            ToolExecutions: false,
            tools.Select(static tool => tool.Name).ToArray(),
            Path.GetFullPath(workspace),
            result.TraceFilePath,
            JsonlTraceStore.GetRunDirectory(result.TraceFilePath),
            Path.Combine(JsonlTraceStore.GetRunDirectory(result.TraceFilePath), "manifest.json"),
            result.TotalRounds,
            result.TotalToolCalls,
            result.TotalDurationMs)
        {
            TerminalDetailCode = result.TerminalDetailCode,
            ZeroToolCallRounds = result.ZeroToolCallRounds,
            ContinuationCount = result.ContinuationCount,
            WriteToolCalls = result.WriteToolCalls,
            LastFunctionCall = result.LastFunctionCall,
            RequiredToolName = result.RequiredToolName,
            RequiredToolSatisfied = result.RequiredToolSatisfied,
            ExecutedToolNames = result.ExecutedToolNames,
            SuccessfulToolNames = result.SuccessfulToolNames
        };
        await FinalizeRunArtifactsAsync(
            result.TraceFilePath,
            Path.GetFullPath(workspace),
            result.TotalRounds,
            result.TotalToolCalls,
            result.TotalDurationMs,
            new QueryRuntimeTraceOptions { DataMode = QueryRuntimeTraceDataMode.SanitizedFixture },
            ct).ConfigureAwait(false);

        if (json)
        {
            WriteJson(output);
            return 0;
        }

        Console.WriteLine(output.FinalText);
        Console.WriteLine($"run_id: {output.RunId}");
        Console.WriteLine($"source_run_id: {output.SourceRunId}");
        Console.WriteLine($"trace: {output.TraceFilePath}");
        Console.WriteLine($"mode: strict-replay");
        Console.WriteLine($"schema_version: {output.SchemaVersion}");
        Console.WriteLine($"replay_digest: {output.ReplayDigest}");
        Console.WriteLine($"provider_calls: false");
        Console.WriteLine($"tool_executions: false");
        return 0;
    }

    private static bool TryFindLatestTraceFile(string workspace, out string traceFile, out string error)
    {
        try
        {
            traceFile = JsonlTraceStore.FindLatestTraceFile(workspace);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            traceFile = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    private static QreReplaySummary ReadReplaySummary(string traceFile)
    {
        var records = JsonlTraceStore.ReadRecords(traceFile);
        var terminalRecord = records
            .LastOrDefault(static record => record.Type is "run.completed" or "run.failed");
        var terminationReason = terminalRecord?.TryGetString("TerminationReason") ??
            terminalRecord?.TryGetNestedString("Data", "Reason");
        var runDirectory = JsonlTraceStore.GetRunDirectory(traceFile);
        var manifestPath = Path.Combine(runDirectory, "manifest.json");
        var trajectory = BuildReplayTrajectory(records);
        var missing = GetReplayMissingRecords(records, trajectory);
        var strictReplayable = terminalRecord != null &&
            trajectory.Any(static step => step.Kind == "model") &&
            missing.Count == 0;
        var schemaVersion = DeterministicReplay.ReadSchemaVersion(traceFile);
        var compatibility = QueryRuntimeTraceSchema.GetReplayCompatibility(schemaVersion, strict: true);
        var started = records.FirstOrDefault(static record => record.Type == "run.started");
        var manifest = JsonlTraceStore.TryReadManifest(runDirectory);
        var dataMode = started?.TryGetString("DataMode") ??
            TryGetJsonString(manifest, "DataMode") ??
            QueryRuntimeTraceDataMode.PrivateDiagnostic.ToString();
        var replayCapability = started?.TryGetString("ReplayCapability") ??
            TryGetJsonString(manifest, "ReplayCapability") ??
            QueryRuntimeReplayCapability.FullFidelity.ToString();
        var fullFidelity = string.Equals(
            replayCapability,
            QueryRuntimeReplayCapability.FullFidelity.ToString(),
            StringComparison.OrdinalIgnoreCase);
        strictReplayable = strictReplayable && fullFidelity;
        var blockedReason = !fullFidelity
            ? $"trace data mode {dataMode} is summary-only"
            : compatibility.Compatible ? null : compatibility.Reason;

        return new QreReplaySummary(
            Type: "qre.replay.summary",
            RunId: JsonlTraceStore.TryReadRunId(records),
            TraceFilePath: traceFile,
            RunDirectory: runDirectory,
            ManifestPath: File.Exists(manifestPath) ? manifestPath : null,
            Mode: strictReplayable ? "strict-replay" : "trace-summary",
            ProviderCalls: false,
            ToolExecutions: false,
            ModelResponses: records.Count(static record => record.Type == "model.response"),
            ToolResults: records.Count(static record => record.Type == "tool.execution.completed"),
            EventCount: records.Length,
            TerminationReason: terminationReason,
            StrictReplayable: strictReplayable,
            DecisionTrajectory: trajectory,
            MissingReplayRecords: missing,
            TerminalRecord: terminalRecord?.Root,
            Manifest: manifest,
            SchemaVersion: schemaVersion,
            StrictReplayCompatible: strictReplayable && compatibility.Compatible,
            StrictReplayBlockedReason: blockedReason,
            DataMode: dataMode,
            ReplayCapability: replayCapability);
    }

    private static IReadOnlyList<QreReplayStep> BuildReplayTrajectory(JsonlTraceNodeRecord[] records)
    {
        var steps = new List<QreReplayStep>();
        var argumentHashesByCallId = records
            .Where(static record => record.Type == "tool.call.requested")
            .Select(static record => record.TryGetData(out var data)
                ? new
                {
                    CallId = TryGetJsonString(data, "CallId"),
                    ArgumentHash = TryGetJsonString(data, "ArgumentHash")
                }
                : null)
            .Where(static item => item is { CallId: not null, ArgumentHash: not null })
            .ToDictionary(
                static item => item!.CallId!,
                static item => item!.ArgumentHash!,
                StringComparer.Ordinal);

        foreach (var record in records)
        {
            if (!record.TryGetData(out var data))
            {
                continue;
            }

            switch (record.Type)
            {
                case "model.response":
                    steps.Add(new QreReplayStep(
                        "model",
                        record.TryGetLong("Seq"),
                        TryGetJsonInt(data, "Round"),
                        null,
                        null,
                        TryGetJsonInt(data, "AssistantTextLength"),
                        TryGetJsonInt(data, "StructuredToolCallCount"),
                        TryGetJsonString(data, "AssistantText") != null || data.TryGetProperty("AssistantTextBlob", out _),
                        null,
                        null));
                    break;
                case "tool.execution.completed":
                    var callId = TryGetJsonString(data, "CallId");
                    steps.Add(new QreReplayStep(
                        "tool",
                        record.TryGetLong("Seq"),
                        TryGetJsonInt(data, "Round"),
                        TryGetJsonString(data, "ToolName"),
                        callId,
                        TryGetJsonInt(data, "ResultLength"),
                        null,
                        TryGetJsonString(data, "Result") != null || data.TryGetProperty("ResultBlob", out _),
                        callId != null && argumentHashesByCallId.TryGetValue(callId, out var argumentHash)
                            ? argumentHash
                            : null,
                        TryGetJsonBool(data, "Success")));
                    break;
            }
        }

        return steps;
    }

    private static IReadOnlyList<string> GetReplayMissingRecords(
        JsonlTraceNodeRecord[] records,
        IReadOnlyList<QreReplayStep> trajectory)
    {
        var missing = new List<string>();
        if (!records.Any(static record => record.Type == "run.started"))
        {
            missing.Add("run.started");
        }

        if (!records.Any(static record => record.Type == "model.response"))
        {
            missing.Add("model.response");
        }

        if (!records.Any(static record => record.Type is "run.completed" or "run.failed"))
        {
            missing.Add("terminal");
        }

        if (trajectory.Any(static step => !step.HasRecordedPayload))
        {
            missing.Add("recorded.payload");
        }

        return missing;
    }

    private static void WriteJson<T>(T value)
    {
        Console.WriteLine(SerializeJsonOutput(value));
    }

    private static string SerializeJsonOutput(object? value)
        => value switch
        {
            QreRunOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreRunOutput),
            QreV2RunOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreV2RunOutput),
            QreV2ReplayOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreV2ReplayOutput),
            QreV2TraceLatestOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreV2TraceLatestOutput),
            QreTraceLatestOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreTraceLatestOutput),
            QreTraceJsonlEvent output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreTraceJsonlEvent),
            QreToolListOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreToolListOutput),
            QreToolRegisterOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreToolRegisterOutput),
            QreToolInvokeOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreToolInvokeOutput),
            QrePolicyCheckOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QrePolicyCheckOutput),
            QreReplaySummary output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreReplaySummary),
            QreStrictReplayOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreStrictReplayOutput),
            QreDiffLatestOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreDiffLatestOutput),
            QreSandboxExecOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreSandboxExecOutput),
            QreDoctorOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreDoctorOutput),
            QreInitOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreInitOutput),
            QreSandboxExecStartedTraceRecord output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreSandboxExecStartedTraceRecord),
            QreSandboxPolicyDecisionTraceRecord output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreSandboxPolicyDecisionTraceRecord),
            QreSandboxPolicyBlockedTraceRecord output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreSandboxPolicyBlockedTraceRecord),
            QreSandboxExecCompletedTraceRecord output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreSandboxExecCompletedTraceRecord),
            QreRunRunnerConfigurationTraceRecord output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreRunRunnerConfigurationTraceRecord),
            QreBudgetUsageTraceRecord output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreBudgetUsageTraceRecord),
            _ => throw new InvalidOperationException($"Unsupported JSON output type: {value?.GetType().Name ?? "<null>"}")
        };

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("qre experimental QueryRuntime CLI");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre --version");
        Console.WriteLine("  qre init --workspace . [--json] [--force]");
        Console.WriteLine("  qre doctor --workspace . [--json]");
        Console.WriteLine();
        PrintRunHelp();
        Console.WriteLine();
        PrintTraceHelp();
        Console.WriteLine();
        PrintToolHelp();
        Console.WriteLine();
        PrintPolicyHelp();
        Console.WriteLine();
        PrintReplayHelp();
        Console.WriteLine();
        PrintRerunHelp();
        Console.WriteLine();
        PrintDiffHelp();
        Console.WriteLine();
        PrintSandboxHelp();
        Console.WriteLine();
        PrintDoctorHelp();
        Console.WriteLine();
        PrintInitHelp();
    }

    private static void PrintRunHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre run --workspace . \"analyze this repo\"");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -w, --workspace <path>  Workspace root. Defaults to current directory.");
        Console.WriteLine("  --response <text>       Static experimental model response for local smoke tests.");
        Console.WriteLine("  --api-url <url>         Provider endpoint. Env fallback: QRE_API_URL.");
        Console.WriteLine("  --api-key <key>         Provider API key. Env fallback: QRE_API_KEY.");
        Console.WriteLine("  --model <name>          Provider model name. Env fallback: QRE_MODEL.");
        Console.WriteLine("  --api-mode <mode>       chat-completions, responses, or anthropic-messages. Env fallback: QRE_API_MODE.");
        Console.WriteLine("  --profile <name>        none, readonly, verify, or repair. Defaults to none.");
        Console.WriteLine("  --tools <mode>          Backward-compatible alias for --profile.");
        Console.WriteLine("  --runner <name>         local or docker for verify tool execution. Defaults to local.");
        Console.WriteLine("  --docker-image <image>  Docker image for --runner docker. Env fallback: QRE_DOCKER_IMAGE.");
        Console.WriteLine("  --external              Include .qre/tools/*.json stdio tool manifests.");
        Console.WriteLine("  --tool-search           Start with tool_search and lazy-activate profile tools.");
        Console.WriteLine("  --tool-search-top-k <n> Max search hits to activate. Defaults to 5.");
        Console.WriteLine("  --max-rounds <n>        Runtime loop round limit. Defaults to 3.");
        Console.WriteLine("  --runtime <v2>          Compatibility selector. v2 is the only execution runtime and the default.");
        Console.WriteLine("  --required-tool <name>  Require one tool call before normal tool mode resumes.");
        Console.WriteLine("  --approve-risk <reason> Approve plan-bound repair/external tool calls for this run.");
        Console.WriteLine("  --thinking <mode>       auto, off, on, or preserve. Defaults to auto.");
        Console.WriteLine("  --trace-data <mode>     public (default), private, or sanitized. Private may persist secrets.");
        Console.WriteLine("  --json-output           Request JSON output and disable thinking by default.");
        Console.WriteLine("  --json                  Print CLI result as JSON.");
        Console.WriteLine("  --stream                Stream human-readable assistant text as it is produced.");
        Console.WriteLine("  --jsonl-stream          Reserved for future machine-readable event streaming.");
    }

    private static void PrintTraceHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre trace latest --workspace . [--runtime v2] [--json|--jsonl]");
        Console.WriteLine("  qre trace latest --workspace . --runtime v1 [--json|--jsonl]  Legacy read-only inspection.");
    }

    private static void PrintToolHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre tool list --workspace . --profile readonly|verify|repair [--json] [--external]");
        Console.WriteLine("  qre tool register --workspace . --manifest tool.json [--json] [--force]");
        Console.WriteLine("  qre tool invoke --workspace . --name tool_name --arguments '{\"key\":\"value\"}' [--json]");
    }

    private static void PrintPolicyHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre policy check --workspace . --profile verify --tool qre_dotnet_test [--json] [--approve-risk <reason>] -- dotnet test --no-restore");
    }

    private static void PrintReplayHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre replay latest --workspace . [--runtime v2] [--json] [--summary] [--strict]");
        Console.WriteLine("  qre replay latest --workspace . --runtime v1 --summary [--json]  Legacy read-only inspection.");
        Console.WriteLine("    --summary  Read-only trace summary; the runtime is not executed.");
        Console.WriteLine("    --strict   Validates the complete recorded trajectory and emits a stable replay_digest.");
        Console.WriteLine("               v2 replay is data-only and never calls a provider or executes a tool.");
    }

    private static void PrintRerunHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre rerun latest --workspace . [--json] [--response text] [--trace-data public|private|sanitized]");
    }

    private static void PrintDiffHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre diff latest --workspace . [--stat] [--json]");
    }

    private static void PrintSandboxHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre sandbox exec --workspace . --profile verify [--workspace-root <path>] [--runner local|docker] [--trace-data public|private|sanitized] [--json] [--approve-risk <reason>] -- dotnet test --no-restore");
    }

    private static void PrintDoctorHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre doctor --workspace . [--json]");
    }

    private static void PrintInitHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre init --workspace . [--json] [--force]");
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string CombineNonEmpty(params string[] values)
        => string.Join(
            Environment.NewLine,
            values.Where(static value => !string.IsNullOrWhiteSpace(value)));

    private static string GetVersion()
        => typeof(QreCli).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()
            ?.InformationalVersion
           ?? typeof(QreCli).Assembly.GetName().Version?.ToString()
           ?? "0.0.0";

    private static async Task<QreDoctorCheck> RunDiagnosticCommandAsync(
        string name,
        IReadOnlyList<string> command,
        string workingDirectory,
        CancellationToken ct)
    {
        try
        {
            var result = await new LocalProcessSandboxRunner().RunAsync(
                new SandboxJobSpec
                {
                    Command = command,
                    WorkingDirectory = workingDirectory,
                    Environment = TrustedLocalSandboxEnvironment.Create(),
                    Limits = new SandboxLimits
                    {
                        Timeout = TimeSpan.FromSeconds(10),
                        MaxOutputBytes = 64 * 1024
                    },
                    Network = SandboxNetworkPolicy.Deny,
                    Mounts = SandboxMountPolicy.WorkspaceReadOnly
                },
                ct).ConfigureAwait(false);

            var detail = FirstNonEmpty(result.StandardOutput.Trim(), result.StandardError.Trim());
            return new QreDoctorCheck(
                name,
                result.ExitCode == 0 && !result.TimedOut ? "pass" : "fail",
                result.TimedOut ? $"{name} command timed out" : $"{name} command exited {result.ExitCode}",
                detail);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new QreDoctorCheck(name, "fail", $"{name} command failed to start", ex.Message);
        }
    }

    private static QueryRuntimeToolDescriptor? ResolveSandboxExecDescriptor(
        QueryRuntimeToolProfile profile,
        IReadOnlyList<string> command)
    {
        var toolName = InferSandboxExecToolName(command);
        if (toolName == null)
        {
            return CreatePolicyGatedSandboxExecDescriptor(profile, command);
        }

        var registered = new ExperimentalToolRegistry()
            .ListTools(profile)
            .FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));
        return registered ?? CreatePolicyGatedSandboxExecDescriptor(profile, command);
    }

    private static SandboxMountPolicy ResolveDefaultMount(
        QueryRuntimeToolProfile profile,
        QueryRuntimeToolDescriptor descriptor)
    {
        var normalizedProfile = ExperimentalToolRegistry.NormalizeProfileName(profile.Name);
        if (normalizedProfile == "readonly")
        {
            return SandboxMountPolicy.WorkspaceReadOnly;
        }

        return descriptor.Capabilities.Contains(QueryRuntimeCapabilities.WriteArtifacts)
            ? SandboxMountPolicy.WorkspaceReadWrite
            : SandboxMountPolicy.WorkspaceReadOnly;
    }

    private static string? InferSandboxExecToolName(IReadOnlyList<string> command)
    {
        if (command.Count < 2)
        {
            return null;
        }

        if (string.Equals(command[0], "git", StringComparison.Ordinal) &&
            string.Equals(command[1], "status", StringComparison.Ordinal))
        {
            return "qre_git_status";
        }

        if (string.Equals(command[0], "git", StringComparison.Ordinal) &&
            string.Equals(command[1], "diff", StringComparison.Ordinal))
        {
            return "qre_git_diff";
        }

        if (string.Equals(command[0], "rg", StringComparison.Ordinal))
        {
            return "qre_rg_search";
        }

        if (string.Equals(command[0], "dotnet", StringComparison.Ordinal) &&
            string.Equals(command[1], "test", StringComparison.Ordinal))
        {
            return "qre_dotnet_test";
        }

        if (string.Equals(command[0], "dotnet", StringComparison.Ordinal) &&
            string.Equals(command[1], "build", StringComparison.Ordinal))
        {
            return "qre_dotnet_build";
        }

        return null;
    }

    private static QueryRuntimeToolDescriptor? CreatePolicyGatedSandboxExecDescriptor(
        QueryRuntimeToolProfile profile,
        IReadOnlyList<string> command)
    {
        var normalizedProfile = ExperimentalToolRegistry.NormalizeProfileName(profile.Name);
        if (normalizedProfile is not ("readonly" or "verify"))
        {
            return null;
        }

        var commandCapabilities = ExperimentalCommandCapabilityClassifier.Classify(
            command,
            SandboxMountPolicy.WorkspaceReadWrite);
        if (commandCapabilities.Count == 0)
        {
            return null;
        }

        return new QueryRuntimeToolDescriptor(
            "qre_sandbox_exec",
            "Policy-gated sandbox command execution.",
            ExperimentalCommandToolCapabilityMapper.InferToolCapabilities(commandCapabilities),
            profile);
    }

    private static void WriteTemplateFile(
        string path,
        string contents,
        bool force,
        ICollection<string> created,
        ICollection<string> skipped)
    {
        if (File.Exists(path) && !force)
        {
            skipped.Add(path);
            return;
        }

        File.WriteAllText(path, contents);
        created.Add(path);
    }

    private static string BuildDefaultConfigTemplate()
        => """
           # QRE local workspace configuration.
           # Phase 1 note: this file is a scaffold only. The CLI does not
           # parse it yet; runtime provider settings still come from command
           # line options or the environment variables listed below.
           # This file intentionally does not store provider secrets.
           # Configure provider credentials through environment variables:
           # QRE_API_URL, QRE_API_KEY, QRE_MODEL, QRE_API_MODE.

           [runtime]
           default_profile = "readonly"
           max_rounds = 3

           [provider]
           api_url_env = "QRE_API_URL"
           api_key_env = "QRE_API_KEY"
           model_env = "QRE_MODEL"
           api_mode_env = "QRE_API_MODE"

           [trace]
           root = ".qre/runs"
           """;

    private static string BuildQreReadmeTemplate()
        => """
           # .qre

           This directory is for local QueryRuntime traces and workspace-scoped
           configuration templates.

           Do not commit raw run artifacts from this directory. Trace files may
           contain prompts, model responses, tool arguments, tool output, file
           snippets, or other private repository data.
           """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = QreCliJsonContext.Default,
        WriteIndented = false
    };

    private sealed record QreRunOptions
    {
        public required string Workspace { get; set; }

        public QueryRuntimeProviderOptions Provider { get; } = new();

        public QueryRuntimeToolProfile ToolProfile { get; set; } = QueryRuntimeToolProfile.None;

        public QueryRuntimeModelPolicyOptions ModelPolicy { get; } = new();

        public QueryRuntimeOutputOptions Output { get; } = new();

        public QueryRuntimeExecutionOptions Runtime { get; } = new();

        public QueryRuntimeToolSearchOptions ToolSearch { get; } = new();

        public QueryRuntimeTraceOptions Trace { get; set; } = new();

        public string Runner { get; set; } = "local";

        public string? DockerImage { get; set; }

        public bool IncludeExternalTools { get; set; }

        public string? RequiredToolName { get; set; }

        public string? ApprovalReason { get; set; }
    }

    private sealed class CliV2ToolApproval(string reason) : IRuntimeToolApproval
    {
        public ValueTask<RuntimeToolApprovalDecision> DecideAsync(
            ResolvedExecutionPlan plan,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                plan.Approval == null || plan.Approval.ExpiresAt <= DateTimeOffset.UtcNow
                    ? RuntimeToolApprovalDecision.Decline("The frozen approval binding is missing or expired.")
                    : RuntimeToolApprovalDecision.Approve(reason));
        }
    }

    internal sealed class CliV1ToolApprovalIntervention(
        IReadOnlySet<string> approvalRequiredToolNames,
        string? approvalReason) : IQueryRuntimeToolIntervention
    {
        private readonly IReadOnlySet<string> _approvalRequiredToolNames =
            new HashSet<string>(approvalRequiredToolNames, StringComparer.OrdinalIgnoreCase);
        private readonly string? _approvalReason = string.IsNullOrWhiteSpace(approvalReason)
            ? null
            : approvalReason.Trim();

        public ValueTask<QueryRuntimeToolInterventionDecision> BeforeToolCallAsync(
            QueryRuntimeToolCallContext context,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_approvalRequiredToolNames.Contains(context.ToolName))
            {
                return ValueTask.FromResult(QueryRuntimeToolInterventionDecision.Allow());
            }

            return ValueTask.FromResult(_approvalReason == null
                ? QueryRuntimeToolInterventionDecision.FailClosed(
                    "High-risk tool execution requires explicit CLI approval.",
                    "bound_approval_unavailable")
                : QueryRuntimeToolInterventionDecision.Allow(
                    "Explicit CLI approval granted for high-risk tool execution."));
        }

        public ValueTask AfterToolExecutionAsync(
            QueryRuntimeToolExecutionResultContext context,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed record QreRunOutput(
        string Type,
        string FinalText,
        string RunId,
        string Termination,
        string Profile,
        string Runner,
        QreSandboxRunnerConfiguration? RunnerConfiguration,
        IReadOnlyList<string> Tools,
        string WorkspacePath,
        string TraceFilePath,
        string RunDirectory,
        string ManifestPath,
        int TotalRounds,
        int TotalToolCalls,
        long TotalDurationMs)
    {
        public string TerminationReason => Termination;

        public string? TerminalDetailCode { get; init; }

        public int ZeroToolCallRounds { get; init; }

        public int ContinuationCount { get; init; }

        public int WriteToolCalls { get; init; }

        public string? LastFunctionCall { get; init; }

        public string? RequiredToolName { get; init; }

        public bool RequiredToolSatisfied { get; init; }

        public IReadOnlyList<string> ExecutedToolNames { get; init; } = [];

        public IReadOnlyList<string> SuccessfulToolNames { get; init; } = [];
    }

    internal sealed record QreTraceLatestOutput(
        string Type,
        string? RunId,
        string TraceFilePath,
        string RunDirectory,
        string? ManifestPath,
        int EventCount,
        JsonElement? TerminalRecord);

    internal sealed record QreV2TraceLatestOutput(
        string Type,
        string AuditFilePath,
        string RunDirectory,
        string ManifestPath,
        int EventCount,
        int SchemaVersion,
        string DataMode,
        string ReplayCapability,
        string? Status,
        string? TerminationReason,
        string? ErrorCode);

    internal sealed record QreTraceJsonlEvent(
        string Type,
        string? RunId,
        int Index,
        string EventType,
        long? Sequence,
        string? Timestamp,
        JsonElement Payload);

    internal sealed record QreToolListOutput(
        string Type,
        string Profile,
        bool External,
        IReadOnlyList<QreToolDescriptor> Tools);

    internal sealed record QreToolDescriptor(
        string Name,
        string? Description,
        IReadOnlySet<string> Capabilities,
        string Source = "builtin",
        string? Transport = null);

    internal sealed record QreToolRegisterOutput(
        string Type,
        string WorkspacePath,
        string ManifestPath,
        string DestinationPath,
        string ToolName,
        string Transport,
        IReadOnlySet<string> Capabilities,
        bool Overwritten);

    internal sealed record QreToolInvokeOutput(
        string Type,
        string WorkspacePath,
        string ToolName,
        IReadOnlyDictionary<string, object?> Arguments,
        string Result);

    internal sealed record QrePolicyCheckOutput(
        string Type,
        string Profile,
        string Tool,
        IReadOnlySet<string> Capabilities,
        IReadOnlyList<string> Command,
        IReadOnlySet<string> CommandCapabilities,
        bool ExplicitApproval,
        string? ApprovalReason,
        string Network,
        string Mount,
        string Decision,
        bool Allowed,
        string Reason);

    internal sealed record QreReplaySummary(
        string Type,
        string? RunId,
        string TraceFilePath,
        string RunDirectory,
        string? ManifestPath,
        string Mode,
        bool ProviderCalls,
        bool ToolExecutions,
        int ModelResponses,
        int ToolResults,
        int EventCount,
        string? TerminationReason,
        bool StrictReplayable,
        IReadOnlyList<QreReplayStep> DecisionTrajectory,
        IReadOnlyList<string> MissingReplayRecords,
        JsonElement? TerminalRecord,
        JsonElement? Manifest,
        int SchemaVersion,
        bool StrictReplayCompatible,
        string? StrictReplayBlockedReason,
        string DataMode,
        string ReplayCapability);

    internal sealed record QreStrictReplayOutput(
        string Type,
        string Mode,
        string FinalText,
        string? SourceRunId,
        string RunId,
        string Termination,
        string Profile,
        int SchemaVersion,
        string ReplayDigest,
        bool ProviderCalls,
        bool ToolExecutions,
        IReadOnlyList<string> Tools,
        string WorkspacePath,
        string TraceFilePath,
        string RunDirectory,
        string ManifestPath,
        int TotalRounds,
        int TotalToolCalls,
        long TotalDurationMs)
    {
        public string? TerminalDetailCode { get; init; }

        public int ZeroToolCallRounds { get; init; }

        public int ContinuationCount { get; init; }

        public int WriteToolCalls { get; init; }

        public string? LastFunctionCall { get; init; }

        public string? RequiredToolName { get; init; }

        public bool RequiredToolSatisfied { get; init; }

        public IReadOnlyList<string> ExecutedToolNames { get; init; } = [];

        public IReadOnlyList<string> SuccessfulToolNames { get; init; } = [];
    }

    internal sealed record QreReplayStep(
        string Kind,
        long? Sequence,
        int? Round,
        string? ToolName,
        string? CallId,
        int? TextLength,
        int? ToolCallCount,
        bool HasRecordedPayload,
        string? ArgumentHash,
        bool? Success);

    internal sealed record QreDiffLatestOutput(
        string Type,
        string WorkspacePath,
        string Mode,
        string? RunId,
        string? RunDirectory,
        string? ManifestPath,
        bool Stat,
        int ExitCode,
        bool TimedOut,
        long DurationMs,
        string Status,
        string Diff,
        string? StandardError);

    internal sealed record QreSandboxExecOutput(
        string Type,
        string? TraceFilePath,
        string Profile,
        string Runner,
        QreSandboxRunnerConfiguration? RunnerConfiguration,
        string Tool,
        IReadOnlySet<string> Capabilities,
        IReadOnlyList<string> Command,
        IReadOnlySet<string> CommandCapabilities,
        bool ExplicitApproval,
        string? ApprovalReason,
        string WorkspacePath,
        string Network,
        string Mount,
        string Decision,
        bool Allowed,
        string Reason,
        int? ExitCode,
        bool? TimedOut,
        long? DurationMs,
        string? StandardOutput,
        string? StandardError);

    internal sealed record QreDoctorOutput(
        string Type,
        string Version,
        string WorkspacePath,
        IReadOnlyList<QreDoctorCheck> Checks,
        bool Healthy);

    internal sealed record QreDoctorCheck(
        string Name,
        string Status,
        string Message,
        string? Detail);

    internal sealed record QreInitOutput(
        string Type,
        string WorkspacePath,
        string QreDirectory,
        IReadOnlyList<string> Created,
        IReadOnlyList<string> Skipped,
        bool Force);

    internal sealed record QreSandboxExecStartedTraceRecord(
        string Type,
        string RunId,
        string WorkspacePath,
        string Profile,
        string Runner,
        QreSandboxRunnerConfiguration? RunnerConfiguration,
        string Tool,
        IReadOnlySet<string> Capabilities,
        IReadOnlyList<string> Command,
        IReadOnlySet<string> CommandCapabilities,
        bool ExplicitApproval,
        string? ApprovalReason,
        string Network,
        string Mount,
        string DataMode,
        string ReplayCapability,
        DateTimeOffset Timestamp);

    internal sealed record QreSandboxPolicyDecisionTraceRecord(
        string Type,
        string Profile,
        string Runner,
        string ToolName,
        IReadOnlySet<string> Capabilities,
        IReadOnlyList<string> Command,
        IReadOnlySet<string> CommandCapabilities,
        bool ExplicitApproval,
        string? ApprovalReason,
        string Network,
        string Mount,
        string Decision,
        bool Allowed,
        string Reason,
        DateTimeOffset Timestamp);

    internal sealed record QreSandboxPolicyBlockedTraceRecord(
        string Type,
        string Profile,
        string Runner,
        string ToolName,
        IReadOnlySet<string> Capabilities,
        IReadOnlyList<string> Command,
        IReadOnlySet<string> CommandCapabilities,
        bool ExplicitApproval,
        string? ApprovalReason,
        string Network,
        string Mount,
        string Decision,
        string Reason,
        DateTimeOffset Timestamp);

    internal sealed record QreSandboxExecCompletedTraceRecord(
        string Type,
        string RunId,
        int? ExitCode,
        bool? TimedOut,
        long? DurationMs,
        string? StandardOutput,
        string? StandardError,
        DateTimeOffset Timestamp);

    internal sealed record QreRunRunnerConfigurationTraceRecord(
        string Type,
        string RunId,
        string Runner,
        QreSandboxRunnerConfiguration? RunnerConfiguration,
        DateTimeOffset Timestamp);

    internal sealed record QreBudgetUsageTraceRecord(
        string Type,
        string RunId,
        bool Estimated,
        int PromptChars,
        int AssistantChars,
        int ToolOutputChars,
        int PromptTokens,
        int CompletionTokens,
        int ToolOutputTokens,
        int TotalTokens,
        decimal? EstimatedUsd,
        int TotalRounds,
        int TotalToolCalls,
        long TotalDurationMs,
        DateTimeOffset Timestamp);

    internal sealed record QreSandboxRunnerConfiguration(
        string Type,
        string? Image,
        string? ContainerUser,
        bool? DropAllCapabilities,
        bool? NoNewPrivileges,
        bool? ReadOnlyRootFilesystem,
        string? TmpfsMount,
        string? SeccompProfilePath,
        bool? RequireSeccompProfile,
        bool? CopyWorkspaceForWriteJobs)
    {
        public static QreSandboxRunnerConfiguration FromDocker(DockerSandboxOptions options)
            => new(
                "docker",
                options.Image,
                options.ContainerUser,
                options.DropAllCapabilities,
                options.NoNewPrivileges,
                options.ReadOnlyRootFilesystem,
                options.TmpfsMount,
                options.SeccompProfilePath,
                options.RequireSeccompProfile,
                options.CopyWorkspaceForWriteJobs);

        public static QreSandboxRunnerConfiguration? PublicSummary(string runner)
            => runner == "docker"
                ? new("docker", null, null, null, null, null, null, null, null, null)
                : null;
    }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(QreCli.QreRunOutput))]
[JsonSerializable(typeof(QreV2RunOutput))]
[JsonSerializable(typeof(QreV2ReplayOutput))]
[JsonSerializable(typeof(QreCli.QreV2TraceLatestOutput))]
[JsonSerializable(typeof(QreCli.QreTraceLatestOutput))]
[JsonSerializable(typeof(QreCli.QreTraceJsonlEvent))]
[JsonSerializable(typeof(QreCli.QreToolListOutput))]
[JsonSerializable(typeof(QreCli.QreToolRegisterOutput))]
[JsonSerializable(typeof(QreCli.QreToolInvokeOutput))]
[JsonSerializable(typeof(QreCli.QreToolDescriptor))]
[JsonSerializable(typeof(QreCli.QrePolicyCheckOutput))]
[JsonSerializable(typeof(QreCli.QreReplaySummary))]
[JsonSerializable(typeof(QreCli.QreStrictReplayOutput))]
[JsonSerializable(typeof(QreCli.QreReplayStep))]
[JsonSerializable(typeof(QreCli.QreDiffLatestOutput))]
[JsonSerializable(typeof(QreCli.QreSandboxExecOutput))]
[JsonSerializable(typeof(QreCli.QreDoctorOutput))]
[JsonSerializable(typeof(QreCli.QreDoctorCheck))]
[JsonSerializable(typeof(QreCli.QreInitOutput))]
[JsonSerializable(typeof(QreCli.QreSandboxExecStartedTraceRecord))]
[JsonSerializable(typeof(QreCli.QreSandboxPolicyDecisionTraceRecord))]
[JsonSerializable(typeof(QreCli.QreSandboxPolicyBlockedTraceRecord))]
[JsonSerializable(typeof(QreCli.QreSandboxExecCompletedTraceRecord))]
[JsonSerializable(typeof(QreCli.QreRunRunnerConfigurationTraceRecord))]
[JsonSerializable(typeof(QreCli.QreBudgetUsageTraceRecord))]
[JsonSerializable(typeof(QreCli.QreSandboxRunnerConfiguration))]
internal sealed partial class QreCliJsonContext : JsonSerializerContext;
