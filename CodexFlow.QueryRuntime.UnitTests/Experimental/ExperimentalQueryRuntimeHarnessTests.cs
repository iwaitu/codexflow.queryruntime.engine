using System.Text.Json;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Engine;
using CodexFlow.QueryRuntime.Experimental;
using Microsoft.Extensions.AI;
using Xunit;
using EngineModelRequest = CodexFlow.QueryRuntime.Engine.QueryRuntimeModelRequest;

namespace CodexFlow.QueryRuntime.UnitTests.Experimental;

public sealed class ExperimentalQueryRuntimeHarnessTests
{
    [Fact]
    public async Task RunAsync_WritesJsonlTrace_ForNoToolRun()
    {
        using var workspace = TemporaryWorkspace.Create();
        var harness = new ExperimentalQueryRuntimeHarness(
            new StaticExperimentalModelClient("experimental response"));

        var result = await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "explain this repo",
                WorkspacePath = workspace.Path,
                RunId = "run-no-tool",
                MaxRounds = 2
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("run-no-tool", result.RunId);
        Assert.Equal("experimental response", result.FinalText);
        Assert.True(File.Exists(result.TraceFilePath));
        var runDirectory = Path.GetDirectoryName(result.TraceFilePath)!;
        var manifestPath = Path.Combine(runDirectory, "manifest.json");
        var runJsonPath = Path.Combine(runDirectory, "run.json");
        var artifactsPath = Path.Combine(runDirectory, "artifacts");
        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(runJsonPath));
        Assert.True(Directory.Exists(artifactsPath));

        var records = ReadJsonl(result.TraceFilePath);
        Assert.Contains(records, record => record.RootElement.GetProperty("Type").GetString() == "run.started");
        Assert.Contains(records, record => record.RootElement.GetProperty("Type").GetString() == "model.request");
        Assert.Contains(records, record => record.RootElement.GetProperty("Type").GetString() == "model.response");
        Assert.Contains(records, record => record.RootElement.GetProperty("Type").GetString() == "run.completed");

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Equal("qre.run.manifest", manifest.RootElement.GetProperty("Type").GetString());
        Assert.Equal("run-no-tool", manifest.RootElement.GetProperty("RunId").GetString());
        Assert.Equal("completed", manifest.RootElement.GetProperty("Status").GetString());
        Assert.Equal(result.TraceFilePath, manifest.RootElement.GetProperty("TraceFilePath").GetString());
    }

    [Fact]
    public async Task RunAsync_WritesToolEvents_WhenModelCallsTool()
    {
        using var workspace = TemporaryWorkspace.Create();
        var workspaceInfo = AIFunctionFactory.Create(
            () => "workspace-ok",
            new AIFunctionFactoryOptions { Name = "workspace_info", Description = "inspect workspace" });
        var harness = new ExperimentalQueryRuntimeHarness(
            new ScriptedExperimentalModelClient(
                [new FunctionCallContent("tool-1", "workspace_info", new Dictionary<string, object?>())],
                [new TextContent("done")]));

        var result = await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "inspect workspace",
                WorkspacePath = workspace.Path,
                RunId = "run-tool",
                MaxRounds = 3,
                EnableTools = true,
                Tools = [workspaceInfo]
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("done", result.FinalText);
        Assert.Equal(1, result.TotalToolCalls);

        var records = ReadJsonl(result.TraceFilePath);
        Assert.Contains(records, record => record.RootElement.GetProperty("Type").GetString() == "tool.call.requested");
        Assert.Contains(records, record => record.RootElement.GetProperty("Type").GetString() == "tool.execution.completed");
        var requested = records.Single(record => record.RootElement.GetProperty("Type").GetString() == "tool.call.requested");
        Assert.False(string.IsNullOrWhiteSpace(
            requested.RootElement.GetProperty("Data").GetProperty("ArgumentHash").GetString()));
    }

    [Fact]
    public async Task RunAsync_StoresLargeToolOutputAsContentAddressedBlob()
    {
        using var workspace = TemporaryWorkspace.Create();
        var largeResult = new string('x', 5000);
        var workspaceInfo = AIFunctionFactory.Create(
            () => largeResult,
            new AIFunctionFactoryOptions { Name = "workspace_info", Description = "inspect workspace" });
        var harness = new ExperimentalQueryRuntimeHarness(
            new ScriptedExperimentalModelClient(
                [new FunctionCallContent("tool-1", "workspace_info", new Dictionary<string, object?>())],
                [new TextContent("done")]));

        var result = await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "inspect workspace",
                WorkspacePath = workspace.Path,
                RunId = "run-large-tool-output",
                MaxRounds = 3,
                EnableTools = true,
                Tools = [workspaceInfo]
            },
            TestContext.Current.CancellationToken);

        var records = ReadJsonl(result.TraceFilePath);
        var toolCompleted = records.Single(record =>
            record.RootElement.GetProperty("Type").GetString() == "tool.execution.completed");
        var data = toolCompleted.RootElement.GetProperty("Data");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("Result").ValueKind);
        var blob = data.GetProperty("ResultBlob");
        Assert.Equal("sha256", blob.GetProperty("Algorithm").GetString());
        Assert.Equal(5000, blob.GetProperty("SizeBytes").GetInt32());
        var blobPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(result.TraceFilePath)!,
            blob.GetProperty("Path").GetString()!);
        Assert.True(File.Exists(blobPath));
        Assert.Equal(largeResult, File.ReadAllText(blobPath));
    }

    [Fact]
    public async Task RecordedReplayAdapters_ReplayModelAndToolOutputsWithoutOriginalTool()
    {
        using var workspace = TemporaryWorkspace.Create();
        var originalToolCalls = 0;
        var workspaceInfo = AIFunctionFactory.Create(
            () =>
            {
                originalToolCalls++;
                return "workspace-ok";
            },
            new AIFunctionFactoryOptions { Name = "workspace_info", Description = "inspect workspace" });
        var originalHarness = new ExperimentalQueryRuntimeHarness(
            new ScriptedExperimentalModelClient(
                [new FunctionCallContent("tool-1", "workspace_info", new Dictionary<string, object?>())],
                [new TextContent("done")]));

        var original = await originalHarness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "inspect workspace",
                WorkspacePath = workspace.Path,
                RunId = "run-recorded-source",
                MaxRounds = 3,
                EnableTools = true,
                Tools = [workspaceInfo]
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, originalToolCalls);

        var replayHarness = new ExperimentalQueryRuntimeHarness(
            new RecordedReplayModelClient(original.TraceFilePath));
        var replay = await replayHarness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "inspect workspace",
                WorkspacePath = workspace.Path,
                RunId = "run-recorded-replay",
                MaxRounds = 3,
                EnableTools = true,
                Tools = RecordedReplayToolPack.Create(original.TraceFilePath)
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("done", replay.FinalText);
        Assert.Equal(1, replay.TotalToolCalls);
        Assert.Equal(1, originalToolCalls);
    }

    [Fact]
    public async Task ExternalStdioToolPack_ExecutesManifestToolOutOfProcess()
    {
        using var workspace = TemporaryWorkspace.Create();
        var toolsDirectory = Path.Combine(workspace.Path, ".qre", "tools");
        Directory.CreateDirectory(toolsDirectory);
        File.WriteAllText(
            Path.Combine(toolsDirectory, "demo.json"),
            """
            {
              "name": "demo_external_tool",
              "description": "Demo external stdio tool.",
              "transport": "stdio",
              "command": "/bin/sh",
              "args": ["-c", "cat > external-request.json; printf '{\"result\":\"external-ok\"}'"],
              "capabilities": ["read_fs"],
              "timeoutSeconds": 30,
              "maxOutputBytes": 20000,
              "inputSchema": {
                "type": "object",
                "properties": {
                  "message": { "type": "string" }
                },
                "required": ["message"]
              }
            }
            """);

        var tools = ExternalStdioToolPack.Create(workspace.Path);
        var tool = Assert.Single(tools);
        Assert.Equal("demo_external_tool", tool.Name);
        Assert.Equal("object", tool.JsonSchema.GetProperty("type").GetString());

        var harness = new ExperimentalQueryRuntimeHarness(
            new ScriptedExperimentalModelClient(
                [new FunctionCallContent(
                    "tool-1",
                    "demo_external_tool",
                    new Dictionary<string, object?> { ["message"] = "hello" })],
                [new TextContent("done")]));

        var result = await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "call external tool",
                WorkspacePath = workspace.Path,
                RunId = "run-external-stdio",
                MaxRounds = 3,
                EnableTools = true,
                Tools = tools
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("done", result.FinalText);
        Assert.Equal(1, result.TotalToolCalls);
        var requestJson = File.ReadAllText(Path.Combine(workspace.Path, "external-request.json"));
        using var request = JsonDocument.Parse(requestJson);
        Assert.Equal("demo_external_tool", request.RootElement.GetProperty("name").GetString());
        Assert.Equal("hello", request.RootElement.GetProperty("arguments").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ExternalStdioToolPack_ExecutesMcpStdioToolCall()
    {
        using var workspace = TemporaryWorkspace.Create();
        var toolsDirectory = Path.Combine(workspace.Path, ".qre", "tools");
        Directory.CreateDirectory(toolsDirectory);
        File.WriteAllText(
            Path.Combine(toolsDirectory, "mcp-demo.json"),
            """
            {
              "name": "demo_mcp_tool",
              "description": "Demo MCP stdio tool.",
              "transport": "mcp-stdio",
              "command": "/bin/sh",
              "args": ["-c", "cat > mcp-request.json; printf '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"mcp-ok\"}]}}\\n'"],
              "capabilities": ["read_fs"],
              "inputSchema": {
                "type": "object",
                "properties": {
                  "message": { "type": "string" }
                }
              }
            }
            """);

        var tool = Assert.Single(ExternalStdioToolPack.Create(workspace.Path));
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["message"] = "hello" }),
            TestContext.Current.CancellationToken);

        Assert.Equal("mcp-ok", result);
        var requestJson = File.ReadAllText(Path.Combine(workspace.Path, "mcp-request.json"));
        using var request = JsonDocument.Parse(requestJson);
        Assert.Equal("tools/call", request.RootElement.GetProperty("method").GetString());
        Assert.Equal("demo_mcp_tool", request.RootElement.GetProperty("params").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ExternalStdioToolPack_KillsTimedOutProcessTree()
    {
        using var workspace = TemporaryWorkspace.Create();
        var toolsDirectory = Path.Combine(workspace.Path, ".qre", "tools");
        Directory.CreateDirectory(toolsDirectory);
        File.WriteAllText(
            Path.Combine(toolsDirectory, "timeout-demo.json"),
            """
            {
              "name": "timeout_external_tool",
              "transport": "stdio",
              "command": "/bin/sh",
              "args": ["-c", "sleep 2; touch leaked-timeout.txt"],
              "timeoutSeconds": 1,
              "maxOutputBytes": 1000
            }
            """);

        var tool = Assert.Single(ExternalStdioToolPack.Create(workspace.Path));
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await tool.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken));

        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "leaked-timeout.txt")));
    }

    [Fact]
    public async Task ExternalStdioToolPack_BoundsStdoutWhileDraining()
    {
        using var workspace = TemporaryWorkspace.Create();
        var toolsDirectory = Path.Combine(workspace.Path, ".qre", "tools");
        Directory.CreateDirectory(toolsDirectory);
        File.WriteAllText(
            Path.Combine(toolsDirectory, "large-output-demo.json"),
            """
            {
              "name": "large_output_external_tool",
              "transport": "stdio",
              "command": "/bin/sh",
              "args": ["-c", "head -c 50000 /dev/zero | tr '\\0' x"],
              "timeoutSeconds": 10,
              "maxOutputBytes": 1000
            }
            """);

        var tool = Assert.Single(ExternalStdioToolPack.Create(workspace.Path));
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        var text = Assert.IsType<string>(result);
        Assert.Equal(1000, text.Length);
        Assert.All(text, ch => Assert.Equal('x', ch));
    }

    [Fact]
    public async Task RunAsync_PreservesAssistantText_WhenModelReturnsTextAndToolCall()
    {
        using var workspace = TemporaryWorkspace.Create();
        var workspaceInfo = AIFunctionFactory.Create(
            () => "workspace-ok",
            new AIFunctionFactoryOptions { Name = "workspace_info", Description = "inspect workspace" });
        var model = new RecordingExperimentalModelClient(
            [new TextContent("I will inspect first."), new FunctionCallContent("tool-1", "workspace_info", new Dictionary<string, object?>())],
            [new TextContent("done")]);
        var harness = new ExperimentalQueryRuntimeHarness(model);

        await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "inspect workspace",
                WorkspacePath = workspace.Path,
                RunId = "run-tool-text-preserved",
                MaxRounds = 3,
                EnableTools = true,
                Tools = [workspaceInfo]
            },
            TestContext.Current.CancellationToken);

        var followUpRequest = Assert.Single(model.Requests.Skip(1));
        Assert.Contains(
            followUpRequest.Messages,
            message => message.Role == ChatRole.Assistant &&
                message.Contents.OfType<TextContent>().Any(content => content.Text == "I will inspect first.") &&
                message.Contents.OfType<FunctionCallContent>().Any(call => call.Name == "workspace_info"));
    }

    [Fact]
    public async Task RunAsync_ClearsRequiredToolMode_AfterRequiredToolSucceeds()
    {
        using var workspace = TemporaryWorkspace.Create();
        var workspaceInfo = AIFunctionFactory.Create(
            () => "workspace-ok",
            new AIFunctionFactoryOptions { Name = "workspace_info", Description = "inspect workspace" });
        var model = new RecordingExperimentalModelClient(
            [new FunctionCallContent("tool-1", "workspace_info", new Dictionary<string, object?>())],
            [new TextContent("done")]);
        var harness = new ExperimentalQueryRuntimeHarness(model);

        await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "inspect workspace",
                WorkspacePath = workspace.Path,
                RunId = "run-required-tool-cleared",
                MaxRounds = 3,
                EnableTools = true,
                RequiredToolName = "workspace_info",
                Tools = [workspaceInfo]
            },
            TestContext.Current.CancellationToken);

        var requiredMode = Assert.IsType<RequiredChatToolMode>(model.ToolModes[0]);
        Assert.Equal("workspace_info", requiredMode.RequiredFunctionName);

        var secondOptions = Assert.IsType<VllmChatOptions>(model.Requests[1].Options);
        Assert.False(secondOptions.ToolMode is RequiredChatToolMode);
    }

    [Fact]
    public async Task RunAsync_WritesPolicyDecision_WhenVerifyProfileToolRuns()
    {
        using var workspace = TemporaryWorkspace.Create();
        var harness = new ExperimentalQueryRuntimeHarness(
            new ScriptedExperimentalModelClient(
                [new FunctionCallContent("tool-1", "qre_git_status", new Dictionary<string, object?>())],
                [new TextContent("done")]));

        var result = await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "check status",
                WorkspacePath = workspace.Path,
                RunId = "run-policy-decision",
                MaxRounds = 3,
                EnableTools = true,
                ToolProfile = QueryRuntimeToolProfile.Verify
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("done", result.FinalText);

        var records = ReadJsonl(result.TraceFilePath);
        Assert.Contains(records, record =>
            record.RootElement.GetProperty("Type").GetString() == "policy.decision" &&
            record.RootElement.GetProperty("ToolName").GetString() == "qre_git_status" &&
            record.RootElement.GetProperty("Allowed").GetBoolean());
    }

    [Fact]
    public async Task JsonlTraceStore_ReadLatestAsync_ReturnsSummary()
    {
        using var workspace = TemporaryWorkspace.Create();
        var harness = new ExperimentalQueryRuntimeHarness(
            new StaticExperimentalModelClient("summary response"));

        await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "summarize",
                WorkspacePath = workspace.Path,
                RunId = "run-trace-store",
                MaxRounds = 1
            },
            TestContext.Current.CancellationToken);

        var summary = await new JsonlTraceStore().ReadLatestAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.EndsWith(Path.Combine(".qre", "runs", "run-trace-store", "events.jsonl"), summary.TraceFilePath);
        Assert.Equal("trace-summary", summary.Mode);
        Assert.True(summary.EventCount > 0);
        Assert.Equal("NoToolCalls", summary.TerminationReason);
    }

    [Fact]
    public void JsonlTraceStore_FindLatestTraceFile_UsesRunFileTimestamps()
    {
        using var workspace = TemporaryWorkspace.Create();
        var oldRun = Path.Combine(workspace.Path, ".qre", "runs", "old-run");
        var newRun = Path.Combine(workspace.Path, ".qre", "runs", "new-run");
        Directory.CreateDirectory(oldRun);
        Directory.CreateDirectory(newRun);
        var oldTrace = Path.Combine(oldRun, "events.jsonl");
        var newTrace = Path.Combine(newRun, "events.jsonl");
        File.WriteAllText(oldTrace, "{\"Type\":\"run.completed\",\"RunId\":\"old-run\"}" + Environment.NewLine);
        File.WriteAllText(newTrace, "{\"Type\":\"run.completed\",\"RunId\":\"new-run\"}" + Environment.NewLine);
        File.WriteAllText(Path.Combine(oldRun, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(newRun, "manifest.json"), "{}");

        File.SetLastWriteTimeUtc(Path.Combine(oldRun, "manifest.json"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(newRun, "manifest.json"), new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(oldRun, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(newTrace, JsonlTraceStore.FindLatestTraceFile(workspace.Path));
    }

    [Fact]
    public async Task RunAsync_DisablesThinkingByDefault_WhenToolsAreEnabled()
    {
        using var workspace = TemporaryWorkspace.Create();
        var workspaceInfo = AIFunctionFactory.Create(
            () => "workspace-ok",
            new AIFunctionFactoryOptions { Name = "workspace_info", Description = "inspect workspace" });
        var model = new RecordingExperimentalModelClient([new TextContent("done")]);
        var harness = new ExperimentalQueryRuntimeHarness(model);

        await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "inspect workspace",
                WorkspacePath = workspace.Path,
                RunId = "run-tool-thinking-policy",
                MaxRounds = 1,
                EnableTools = true,
                Tools = [workspaceInfo],
                Options = new VllmChatOptions { ThinkingEnabled = true }
            },
            TestContext.Current.CancellationToken);

        var options = Assert.IsType<VllmChatOptions>(model.Requests.Single().Options);
        Assert.False(options.ThinkingEnabled);
        Assert.Equal(["workspace_info"], options.Tools?.Select(tool => tool.Name).ToArray() ?? []);
    }

    [Fact]
    public async Task RunAsync_DisablesThinkingByDefault_WhenJsonOutputIsRequired()
    {
        using var workspace = TemporaryWorkspace.Create();
        var model = new RecordingExperimentalModelClient([new TextContent("{\"ok\":true}")]);
        var harness = new ExperimentalQueryRuntimeHarness(model);

        await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "return json",
                WorkspacePath = workspace.Path,
                RunId = "run-json-thinking-policy",
                MaxRounds = 1,
                RequiresStructuredOutput = true,
                Options = new ChatOptions
                {
                    ResponseFormat = ChatResponseFormat.Json
                }
            },
            TestContext.Current.CancellationToken);

        var options = Assert.IsType<VllmChatOptions>(model.Requests.Single().Options);
        Assert.False(options.ThinkingEnabled);
        Assert.Same(ChatResponseFormat.Json, options.ResponseFormat);
    }

    [Fact]
    public void QreModelExecutionPolicy_UsesExplicitTrimFriendlyOptionMapping()
    {
        var workspaceInfo = AIFunctionFactory.Create(
            () => "workspace-ok",
            new AIFunctionFactoryOptions { Name = "workspace_info", Description = "inspect workspace" });
        var source = new ChatOptions
        {
            ModelId = "qre-test-model",
            ResponseFormat = ChatResponseFormat.Json,
            StopSequences = ["</done>"],
            Tools = [workspaceInfo],
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["provider"] = "qre"
            }
        };

        var mapped = QreModelExecutionPolicy.Apply(
            source,
            toolsEnabled: true,
            structuredOutputRequired: true,
            QreThinkingPolicy.Auto);

        var options = Assert.IsType<VllmChatOptions>(mapped);
        Assert.False(options.ThinkingEnabled);
        Assert.Equal("qre-test-model", options.ModelId);
        Assert.Same(ChatResponseFormat.Json, options.ResponseFormat);
        Assert.Equal(["</done>"], options.StopSequences);
        Assert.Equal(["workspace_info"], options.Tools?.Select(tool => tool.Name).ToArray() ?? []);
        Assert.Equal("qre", options.AdditionalProperties?["provider"]);
        Assert.NotSame(source.StopSequences, options.StopSequences);
        Assert.NotSame(source.Tools, options.Tools);
        Assert.NotSame(source.AdditionalProperties, options.AdditionalProperties);
    }

    [Fact]
    public void QreArgumentHash_NormalizesCollectionsAndJsonNumbersConsistently()
    {
        using var json = JsonDocument.Parse("""{"items":["a","b"],"timeout":1.0}""");
        var original = QreArgumentHash.Compute(new Dictionary<string, object?>
        {
            ["items"] = new[] { "a", "b" },
            ["timeout"] = 1.0
        });
        var replayed = QreArgumentHash.Compute(RecordedReplayModelClient.ReadArguments(json.RootElement));

        Assert.Equal(original, replayed);
    }

    [Fact]
    public async Task RunAsync_PreservesThinking_WhenPolicyIsPreserve()
    {
        using var workspace = TemporaryWorkspace.Create();
        var workspaceInfo = AIFunctionFactory.Create(
            () => "workspace-ok",
            new AIFunctionFactoryOptions { Name = "workspace_info", Description = "inspect workspace" });
        var model = new RecordingExperimentalModelClient([new TextContent("done")]);
        var harness = new ExperimentalQueryRuntimeHarness(model);

        await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "inspect workspace",
                WorkspacePath = workspace.Path,
                RunId = "run-preserve-thinking-policy",
                MaxRounds = 1,
                EnableTools = true,
                Tools = [workspaceInfo],
                ThinkingPolicy = QreThinkingPolicy.Preserve,
                Options = new VllmChatOptions { ThinkingEnabled = true }
            },
            TestContext.Current.CancellationToken);

        var options = Assert.IsType<VllmChatOptions>(model.Requests.Single().Options);
        Assert.True(options.ThinkingEnabled);
    }

    [Fact]
    public async Task RunAsync_StampsSchemaVersion_OnRunStartedRecord()
    {
        using var workspace = TemporaryWorkspace.Create();
        var harness = new ExperimentalQueryRuntimeHarness(
            new StaticExperimentalModelClient("versioned response"));

        var result = await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "summarize",
                WorkspacePath = workspace.Path,
                RunId = "run-schema-version",
                MaxRounds = 1
            },
            TestContext.Current.CancellationToken);

        var seed = DeterministicReplay.ReadSeed(result.TraceFilePath);
        Assert.Equal(QueryRuntimeTraceSchema.CurrentVersion, seed.SchemaVersion);
        Assert.NotEqual(Guid.Empty, seed.QueryId);
        Assert.NotEqual(default, seed.BaseTimestamp);
        Assert.Equal(QueryRuntimeTraceSchema.CurrentVersion, DeterministicReplay.ReadSchemaVersion(result.TraceFilePath));
    }

    [Fact]
    public async Task StrictReplay_ProducesByteIdenticalDigest_AndNeverExecutesOriginalTool()
    {
        using var workspace = TemporaryWorkspace.Create();
        var originalToolCalls = 0;
        var workspaceInfo = AIFunctionFactory.Create(
            () =>
            {
                originalToolCalls++;
                return "workspace-ok";
            },
            new AIFunctionFactoryOptions { Name = "workspace_info", Description = "inspect workspace" });
        var source = await new ExperimentalQueryRuntimeHarness(
                new ScriptedExperimentalModelClient(
                    [new FunctionCallContent("tool-1", "workspace_info", new Dictionary<string, object?>())],
                    [new TextContent("done")]))
            .RunAsync(
                new ExperimentalQueryRuntimeRequest
                {
                    Prompt = "inspect workspace",
                    WorkspacePath = workspace.Path,
                    RunId = "run-strict-source",
                    MaxRounds = 3,
                    EnableTools = true,
                    Tools = [workspaceInfo]
                },
                TestContext.Current.CancellationToken);

        Assert.Equal(1, originalToolCalls);

        var seed = DeterministicReplay.ReadSeed(source.TraceFilePath);
        Assert.Equal(QueryRuntimeTraceSchema.CurrentVersion, seed.SchemaVersion);

        var firstDigest = await StrictReplayDigestAsync(workspace.Path, source.TraceFilePath, seed, "run-strict-1");
        var secondDigest = await StrictReplayDigestAsync(workspace.Path, source.TraceFilePath, seed, "run-strict-2");

        Assert.Equal(firstDigest, secondDigest);
        Assert.False(string.IsNullOrWhiteSpace(firstDigest));
        // Strict replay returns recorded outputs and never re-invokes the original tool.
        Assert.Equal(1, originalToolCalls);
    }

    private static async Task<string> StrictReplayDigestAsync(
        string workspacePath,
        string sourceTraceFile,
        DeterministicReplaySeed seed,
        string runId)
    {
        var replay = await new ExperimentalQueryRuntimeHarness(new RecordedReplayModelClient(sourceTraceFile))
            .RunAsync(
                new ExperimentalQueryRuntimeRequest
                {
                    Prompt = "inspect workspace",
                    WorkspacePath = workspacePath,
                    RunId = runId,
                    MaxRounds = 3,
                    EnableTools = true,
                    Tools = RecordedReplayToolPack.Create(sourceTraceFile),
                    TimeProvider = new DeterministicReplayClock(seed.BaseTimestamp),
                    QueryIdFactory = () => seed.QueryId
                },
                TestContext.Current.CancellationToken);

        return DeterministicReplay.ComputeCanonicalDigest(replay.TraceFilePath);
    }

    [Theory]
    [InlineData(0, true, false, "legacy")]
    [InlineData(1, true, true, null)]
    [InlineData(2, true, false, "unsupported")]
    [InlineData(0, false, true, null)]
    public void TraceSchema_GatesStrictReplayByVersion(int version, bool strict, bool expectedCompatible, string? reasonFragment)
    {
        var compatibility = QueryRuntimeTraceSchema.GetReplayCompatibility(version, strict);

        Assert.Equal(expectedCompatible, compatibility.Compatible);
        if (reasonFragment == null)
        {
            Assert.Null(compatibility.Reason);
        }
        else
        {
            Assert.NotNull(compatibility.Reason);
            Assert.Contains(reasonFragment, compatibility.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DeterministicReplayClock_AdvancesDeterministically()
    {
        var baseUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var first = new DeterministicReplayClock(baseUtc);
        var second = new DeterministicReplayClock(baseUtc);

        Assert.Equal(first.GetUtcNow(), second.GetUtcNow());
        Assert.Equal(first.GetUtcNow(), second.GetUtcNow());

        var startA = first.GetTimestamp();
        var startB = second.GetTimestamp();
        Assert.Equal(first.GetElapsedTime(startA), second.GetElapsedTime(startB));
    }

    private static List<JsonDocument> ReadJsonl(string path)
        => File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line))
            .ToList();

    private sealed class ScriptedExperimentalModelClient(params IReadOnlyList<AIContent>[] steps) : IExperimentalModelClient
    {
        private readonly Queue<IReadOnlyList<AIContent>> _steps = new(steps);

        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            EngineModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("No scripted response remains.");
            }

            ct.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [.. _steps.Dequeue()]);
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private sealed class RecordingExperimentalModelClient(params IReadOnlyList<AIContent>[] steps) : IExperimentalModelClient
    {
        private readonly Queue<IReadOnlyList<AIContent>> _steps = new(steps);

        public List<EngineModelRequest> Requests { get; } = [];

        public List<ChatToolMode?> ToolModes { get; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            EngineModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            ToolModes.Add(request.Options?.ToolMode);
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("No scripted response remains.");
            }

            ct.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [.. _steps.Dequeue()]);
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryWorkspace Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codexflow-qre-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryWorkspace(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
