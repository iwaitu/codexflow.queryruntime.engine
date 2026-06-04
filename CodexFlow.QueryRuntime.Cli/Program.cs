using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Experimental;
using CodexFlow.QueryRuntime.Models;
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
                        return Fail($"{args[i - 1]} requires none, readonly, or verify.");
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
                case "--json-output":
                    options.Output.RequestJson = true;
                    break;
                case "--json":
                    options.Output.Json = true;
                    break;
                case "--stream":
                    return Fail("--stream is reserved for the future human-readable streaming mode and is not implemented yet. Use qre run without --stream for stable final output.");
                case "--jsonl-stream":
                    return Fail("--jsonl-stream is reserved for the future machine-readable streaming event mode and is not implemented yet. Use --json for the final result object.");
                case "--external":
                    options.IncludeExternalTools = true;
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

        var resolvedWorkspace = Path.GetFullPath(options.Workspace);
        if (!Directory.Exists(resolvedWorkspace))
        {
            return Fail($"Workspace does not exist: {resolvedWorkspace}");
        }

        IExperimentalModelClient? modelClient;
        try
        {
            modelClient = CreateModelClient(options.Provider);
        }
        catch (QreModelSelectionException ex)
        {
            return Fail(ex.Message);
        }

        if (modelClient == null)
        {
            return Fail(
                "No model client configured. Provide --response for offline smoke mode, or set --api-url, --api-key, and --model.");
        }

        var sandboxRunner = CreateSandboxRunner(
            options.Runner,
            options.DockerImage,
            out var runnerName,
            out var runnerConfiguration,
            out var runnerError);
        if (sandboxRunner == null)
        {
            return Fail(runnerError);
        }

        var tools = ResolveTools(
            options.ToolProfile,
            resolvedWorkspace,
            sandboxRunner,
            options.IncludeExternalTools);
        if (tools == null)
        {
            return Fail($"Unsupported profile value: {options.ToolProfile.Name}");
        }

        try
        {
            var harness = new ExperimentalQueryRuntimeHarness(modelClient);
            var result = await harness.RunAsync(
                new ExperimentalQueryRuntimeRequest
                {
                    Prompt = prompt,
                    WorkspacePath = resolvedWorkspace,
                    MaxRounds = options.Runtime.MaxRounds,
                    EnableTools = tools.Count > 0,
                    ToolProfile = options.ToolProfile,
                    RequiresStructuredOutput = options.Output.RequestJson,
                    ThinkingPolicy = options.ModelPolicy.ThinkingPolicy,
                    Options = BuildChatOptions(options)
                },
                ct).ConfigureAwait(false);
            await AppendRunRunnerConfigurationTraceAsync(
                result.TraceFilePath,
                result.RunId,
                runnerName,
                runnerConfiguration).ConfigureAwait(false);
            await FinalizeRunArtifactsAsync(
                result.TraceFilePath,
                resolvedWorkspace,
                result.RunId,
                result.TotalRounds,
                result.TotalToolCalls,
                result.TotalDurationMs,
                ct).ConfigureAwait(false);

            if (options.Output.Json)
            {
                WriteJson(new QreRunOutput(
                    "qre.run.completed",
                    result.FinalText,
                    result.RunId,
                    result.TerminationReason,
                    options.ToolProfile.Name,
                    runnerName,
                    runnerConfiguration,
                    tools.Select(static tool => tool.Name).ToArray(),
                    resolvedWorkspace,
                    result.TraceFilePath,
                    JsonlTraceStore.GetRunDirectory(result.TraceFilePath),
                    Path.Combine(JsonlTraceStore.GetRunDirectory(result.TraceFilePath), "manifest.json"),
                    result.TotalRounds,
                    result.TotalToolCalls,
                    result.TotalDurationMs));
            }
            else
            {
                Console.WriteLine(result.FinalText);
                Console.WriteLine();
                Console.WriteLine($"run_id: {result.RunId}");
                Console.WriteLine($"termination: {result.TerminationReason}");
                Console.WriteLine($"runner: {runnerName}");
                Console.WriteLine($"tools: {(tools.Count == 0 ? "none" : string.Join(',', tools.Select(tool => tool.Name)))}");
                Console.WriteLine($"trace: {result.TraceFilePath}");
                Console.WriteLine($"run_directory: {JsonlTraceStore.GetRunDirectory(result.TraceFilePath)}");
            }

            return 0;
        }
        finally
        {
            (modelClient as IDisposable)?.Dispose();
        }
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
            _ => null
        };
        if (tools == null)
        {
            return null;
        }

        return includeExternal
            ? [.. tools, .. ExternalStdioToolPack.Create(resolvedWorkspace)]
            : tools;
    }

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
            _ => Fail($"Unknown tool command: {args[0]}")
        };
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
                        return Fail($"{args[i - 1]} requires none, readonly, or verify.");
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
                default:
                    return Fail($"Unknown trace latest option: {args[i]}");
            }
        }

        if (json && jsonl)
        {
            return Fail("--json and --jsonl cannot be used together.");
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
                        return Fail($"{args[i - 1]} requires none, readonly, or verify.");
                    }
                    profileOverride = args[i];
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    return Fail($"Unknown rerun latest option: {args[i]}");
            }
        }

        if (!TryFindLatestTraceFile(workspace, out var traceFile, out var error))
        {
            return Fail(error);
        }

        var records = JsonlTraceStore.ReadRecords(traceFile);
        var prompt = records.FirstOrDefault(static record => record.Type == "run.started")?.TryGetString("Prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Fail($"Latest trace has no recorded prompt: {traceFile}");
        }

        var runDirectory = JsonlTraceStore.GetRunDirectory(traceFile);
        var profile = profileOverride ??
            TryGetJsonString(JsonlTraceStore.TryReadManifest(runDirectory), "ToolProfile") ??
            "readonly";

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
        }).ConfigureAwait(false);
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

    private static async Task<string> WriteSandboxExecTraceAsync(QreSandboxExecOutput output)
    {
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        var traceFilePath = Path.Combine(output.WorkspacePath, ".qre", "runs", runId, "events.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(traceFilePath)!);
        var timestamp = DateTimeOffset.UtcNow;
        var records = new object[]
        {
            new QreSandboxExecStartedTraceRecord(
                "sandbox.exec.started",
                runId,
                output.WorkspacePath,
                output.Profile,
                output.Runner,
                output.RunnerConfiguration,
                output.Tool,
                output.Capabilities,
                output.Command,
                output.CommandCapabilities,
                output.ExplicitApproval,
                output.ApprovalReason,
                output.Network,
                output.Mount,
                timestamp),
            new QreSandboxPolicyDecisionTraceRecord(
                "policy.decision",
                output.Profile,
                output.Runner,
                output.Tool,
                output.Capabilities,
                output.Command,
                output.CommandCapabilities,
                output.ExplicitApproval,
                output.ApprovalReason,
                output.Network,
                output.Mount,
                output.Decision,
                output.Allowed,
                output.Reason,
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
                    output.Profile,
                    output.Runner,
                    output.Tool,
                    output.Capabilities,
                    output.Command,
                    output.CommandCapabilities,
                    output.ExplicitApproval,
                    output.ApprovalReason,
                    output.Network,
                    output.Mount,
                    output.Decision,
                    output.Reason,
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
                output.StandardOutput,
                output.StandardError,
                DateTimeOffset.UtcNow)
        ];

        await File.WriteAllLinesAsync(
            traceFilePath,
            records.Select(SerializeJsonOutput)).ConfigureAwait(false);
        return traceFilePath;
    }

    private static async Task AppendRunRunnerConfigurationTraceAsync(
        string traceFilePath,
        string runId,
        string runner,
        QreSandboxRunnerConfiguration? runnerConfiguration)
    {
        var record = new QreRunRunnerConfigurationTraceRecord(
            "runner.configuration",
            runId,
            runner,
            runnerConfiguration,
            DateTimeOffset.UtcNow);
        await File.AppendAllTextAsync(
            traceFilePath,
            SerializeJsonOutput(record) + Environment.NewLine).ConfigureAwait(false);
    }

    private static async Task FinalizeRunArtifactsAsync(
        string traceFilePath,
        string workspacePath,
        string runId,
        int totalRounds,
        int totalToolCalls,
        long totalDurationMs,
        CancellationToken ct)
    {
        var runDirectory = JsonlTraceStore.GetRunDirectory(traceFilePath);
        Directory.CreateDirectory(Path.Combine(runDirectory, "artifacts"));
        await WriteRunDiffPatchAsync(runDirectory, workspacePath, ct).ConfigureAwait(false);

        var usage = BuildBudgetUsage(traceFilePath, runId, totalRounds, totalToolCalls, totalDurationMs);
        var usagePath = Path.Combine(runDirectory, "usage.json");
        await File.WriteAllTextAsync(
            usagePath,
            SerializeJsonOutput(usage) + Environment.NewLine,
            ct).ConfigureAwait(false);
        await File.AppendAllTextAsync(
            traceFilePath,
            SerializeJsonOutput(usage) + Environment.NewLine,
            ct).ConfigureAwait(false);
    }

    private static async Task WriteRunDiffPatchAsync(
        string runDirectory,
        string workspacePath,
        CancellationToken ct)
    {
        var diffPath = Path.Combine(runDirectory, "diff.patch");
        var diff = await TryReadGitDiffPatchAsync(workspacePath, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(diffPath, diff ?? string.Empty, ct).ConfigureAwait(false);
    }

    private static async Task<string?> TryReadGitDiffPatchAsync(string workspacePath, CancellationToken ct)
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

            var files = await TryReadGitWorkspaceFilesForPatchAsync(workspacePath, ct).ConfigureAwait(false);
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

    private static async Task<IReadOnlyList<string>?> TryReadGitWorkspaceFilesForPatchAsync(
        string workspacePath,
        CancellationToken ct)
    {
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
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

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
                default:
                    return Fail($"Unknown replay latest option: {args[i]}");
            }
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
        var tools = summary.ToolResults > 0
            ? RecordedReplayToolPack.Create(traceFile)
            : [];
        var harness = new ExperimentalQueryRuntimeHarness(new RecordedReplayModelClient(traceFile));
        var result = await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = prompt,
                WorkspacePath = Path.GetFullPath(workspace),
                MaxRounds = Math.Max(1, summary.DecisionTrajectory.Count(static step => step.Kind == "model")),
                EnableTools = tools.Count > 0,
                ToolProfile = new QueryRuntimeToolProfile(profileName),
                Tools = tools
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
            result.TotalDurationMs);
        await FinalizeRunArtifactsAsync(
            result.TraceFilePath,
            Path.GetFullPath(workspace),
            result.RunId,
            result.TotalRounds,
            result.TotalToolCalls,
            result.TotalDurationMs,
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
        var tools = summary.ToolResults > 0
            ? RecordedReplayToolPack.Create(traceFile)
            : [];

        // Strict replay seeds the engine with a deterministic clock + query id from the
        // source trace so repeated replays produce a byte-identical canonical projection.
        // It never instantiates a provider chat client (RecordedReplayModelClient) and
        // never executes the original tools (RecordedReplayToolPack returns recorded output).
        var harness = new ExperimentalQueryRuntimeHarness(new RecordedReplayModelClient(traceFile));
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
                QueryIdFactory = () => seed.QueryId
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
            result.TotalDurationMs);
        await FinalizeRunArtifactsAsync(
            result.TraceFilePath,
            Path.GetFullPath(workspace),
            result.RunId,
            result.TotalRounds,
            result.TotalToolCalls,
            result.TotalDurationMs,
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
            Manifest: JsonlTraceStore.TryReadManifest(runDirectory),
            SchemaVersion: schemaVersion,
            StrictReplayCompatible: strictReplayable && compatibility.Compatible,
            StrictReplayBlockedReason: compatibility.Compatible ? null : compatibility.Reason);
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
            QreTraceLatestOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreTraceLatestOutput),
            QreTraceJsonlEvent output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreTraceJsonlEvent),
            QreToolListOutput output => JsonSerializer.Serialize(output, QreCliJsonContext.Default.QreToolListOutput),
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
        Console.WriteLine("  --profile <name>        none, readonly, or verify. Defaults to none.");
        Console.WriteLine("  --tools <mode>          Backward-compatible alias for --profile.");
        Console.WriteLine("  --runner <name>         local or docker for verify tool execution. Defaults to local.");
        Console.WriteLine("  --docker-image <image>  Docker image for --runner docker. Env fallback: QRE_DOCKER_IMAGE.");
        Console.WriteLine("  --external              Include .qre/tools/*.json stdio tool manifests.");
        Console.WriteLine("  --max-rounds <n>        Runtime loop round limit. Defaults to 3.");
        Console.WriteLine("  --thinking <mode>       auto, off, on, or preserve. Defaults to auto.");
        Console.WriteLine("  --json-output           Request JSON output and disable thinking by default.");
        Console.WriteLine("  --json                  Print CLI result as JSON.");
        Console.WriteLine("  --stream                Reserved for future human-readable text streaming.");
        Console.WriteLine("  --jsonl-stream          Reserved for future machine-readable event streaming.");
    }

    private static void PrintTraceHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre trace latest --workspace . [--json|--jsonl]");
    }

    private static void PrintToolHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre tool list --workspace . --profile readonly|verify [--json] [--external]");
    }

    private static void PrintPolicyHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre policy check --workspace . --profile verify --tool qre_dotnet_test [--json] [--approve-risk <reason>] -- dotnet test --no-restore");
    }

    private static void PrintReplayHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre replay latest --workspace . [--json] [--summary] [--strict]");
        Console.WriteLine("    --summary  Read-only trace summary; the runtime is not executed.");
        Console.WriteLine("    --strict   Deterministic replay with injected clock/ids; emits a byte-stable");
        Console.WriteLine("               replay_digest and requires schema version >= 1. No provider/tool calls.");
    }

    private static void PrintRerunHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre rerun latest --workspace . [--json] [--response text]");
    }

    private static void PrintDiffHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre diff latest --workspace . [--stat] [--json]");
    }

    private static void PrintSandboxHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre sandbox exec --workspace . --profile verify [--workspace-root <path>] [--runner local|docker] [--json] [--approve-risk <reason>] -- dotnet test --no-restore");
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

        public string Runner { get; set; } = "local";

        public string? DockerImage { get; set; }

        public bool IncludeExternalTools { get; set; }
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
        long TotalDurationMs);

    internal sealed record QreTraceLatestOutput(
        string Type,
        string? RunId,
        string TraceFilePath,
        string RunDirectory,
        string? ManifestPath,
        int EventCount,
        JsonElement? TerminalRecord);

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
        string? StrictReplayBlockedReason);

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
        long TotalDurationMs);

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
    }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(QreCli.QreRunOutput))]
[JsonSerializable(typeof(QreCli.QreTraceLatestOutput))]
[JsonSerializable(typeof(QreCli.QreTraceJsonlEvent))]
[JsonSerializable(typeof(QreCli.QreToolListOutput))]
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
