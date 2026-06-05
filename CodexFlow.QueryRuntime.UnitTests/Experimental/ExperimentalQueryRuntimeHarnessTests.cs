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
        Assert.Equal(1, result.ZeroToolCallRounds);
        Assert.Equal(0, result.ContinuationCount);
        Assert.Equal(0, result.WriteToolCalls);
        Assert.True(File.Exists(result.TraceFilePath));
        var runDirectory = Path.GetDirectoryName(result.TraceFilePath)!;
        Assert.Equal(runDirectory, result.RunDirectory);
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
        Assert.Equal(runDirectory, manifest.RootElement.GetProperty("RunDirectory").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("ZeroToolCallRounds").GetInt32());
        Assert.Equal(0, manifest.RootElement.GetProperty("ContinuationCount").GetInt32());
        Assert.Equal(0, manifest.RootElement.GetProperty("WriteToolCalls").GetInt32());

        var completed = records.Single(record => record.RootElement.GetProperty("Type").GetString() == "run.completed");
        Assert.Equal(1, completed.RootElement.GetProperty("ZeroToolCallRounds").GetInt32());
        Assert.Equal(0, completed.RootElement.GetProperty("ContinuationCount").GetInt32());
        Assert.Equal(0, completed.RootElement.GetProperty("WriteToolCalls").GetInt32());
    }

    [Fact]
    public async Task RunAsync_RejectsRunIdPathTraversal()
    {
        using var workspace = TemporaryWorkspace.Create();
        var harness = new ExperimentalQueryRuntimeHarness(new StaticExperimentalModelClient("should not run"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await harness.RunAsync(
                new ExperimentalQueryRuntimeRequest
                {
                    Prompt = "test",
                    WorkspacePath = workspace.Path,
                    RunId = "../escape",
                    MaxRounds = 1
                },
                TestContext.Current.CancellationToken));

        Assert.Contains("RunId must be a single safe path segment", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".git/qre")]
    [InlineData("secret-traces")]
    public async Task RunAsync_RejectsUnsafeTraceRootSegments(string traceRoot)
    {
        using var workspace = TemporaryWorkspace.Create();
        var harness = new ExperimentalQueryRuntimeHarness(new StaticExperimentalModelClient("should not run"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await harness.RunAsync(
                new ExperimentalQueryRuntimeRequest
                {
                    Prompt = "test",
                    WorkspacePath = workspace.Path,
                    TraceRoot = traceRoot,
                    RunId = "safe-run",
                    MaxRounds = 1
                },
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_RejectsTraceRootSymlinkEscape()
    {
        using var workspace = TemporaryWorkspace.Create();
        var traceRoot = Path.Combine(workspace.Path, "trace-root");
        var outside = Path.Combine(Path.GetTempPath(), $"qre-trace-outside-{Guid.NewGuid():N}");
        var runsLink = Path.Combine(traceRoot, "runs");
        Directory.CreateDirectory(traceRoot);
        Directory.CreateDirectory(outside);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(runsLink, outside);
            }
            catch (Exception createLinkException) when (createLinkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var harness = new ExperimentalQueryRuntimeHarness(new StaticExperimentalModelClient("should not run"));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await harness.RunAsync(
                    new ExperimentalQueryRuntimeRequest
                    {
                        Prompt = "test",
                        WorkspacePath = workspace.Path,
                        TraceRoot = traceRoot,
                        RunId = "safe-run",
                        MaxRounds = 1
                    },
                    TestContext.Current.CancellationToken));

            Assert.Contains("Symlink traversal outside workspace", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeletePathIfExists(runsLink);
            DeletePathIfExists(outside);
        }
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
        Assert.Equal(0, result.WriteToolCalls);
        Assert.Equal(["workspace_info"], result.ExecutedToolNames);
        Assert.Equal(["workspace_info"], result.SuccessfulToolNames);

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
    public async Task RunAsync_ToolSearchActivatesDeferredTool_ForNextRound()
    {
        using var workspace = TemporaryWorkspace.Create();
        File.WriteAllText(Path.Combine(workspace.Path, "README.md"), "hello qre");
        var model = new RecordingExperimentalModelClient(
            [new FunctionCallContent(
                "call-search",
                "tool_search",
                new Dictionary<string, object?> { ["query"] = "read file", ["top_k"] = 1 })],
            [new FunctionCallContent(
                "call-read",
                "qre_read_file",
                new Dictionary<string, object?> { ["path"] = "README.md", ["max_lines"] = 5 })],
            [new TextContent("done")]);
        var harness = new ExperimentalQueryRuntimeHarness(model);

        var result = await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "read README",
                WorkspacePath = workspace.Path,
                RunId = "run-tool-search-activation",
                MaxRounds = 4,
                EnableTools = true,
                ToolProfile = QueryRuntimeToolProfile.ReadOnly,
                ToolSearch = new QueryRuntimeToolSearchOptions { Enabled = true, TopK = 1 }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("done", result.FinalText);
        Assert.Equal(2, result.TotalToolCalls);
        Assert.Contains(
            model.Requests[0].Messages,
            message => message.Role == ChatRole.System &&
                ReadText(message).Contains("qre_read_file", StringComparison.Ordinal));
        var snapshots = ReadJsonl(result.TraceFilePath)
            .Where(record => record.RootElement.GetProperty("Type").GetString() == "model.request")
            .Select(record => record.RootElement.GetProperty("Data").GetProperty("ToolNames").EnumerateArray().Select(item => item.GetString()).ToArray())
            .ToArray();
        Assert.Contains("tool_search", snapshots[0]);
        Assert.DoesNotContain("qre_read_file", snapshots[0]);
        Assert.Contains("qre_read_file", snapshots[1]);
    }

    [Fact]
    public async Task RunAsync_ToolSearchAddsCatalog_ForHostInitialMessages()
    {
        using var workspace = TemporaryWorkspace.Create();
        var model = new RecordingExperimentalModelClient([new TextContent("done")]);
        var harness = new ExperimentalQueryRuntimeHarness(model);

        await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                InitialMessages =
                [
                    new ChatMessage(ChatRole.System, "host system"),
                    new ChatMessage(ChatRole.User, "read README")
                ],
                WorkspacePath = workspace.Path,
                RunId = "run-tool-search-host-messages",
                MaxRounds = 1,
                EnableTools = true,
                ToolProfile = QueryRuntimeToolProfile.ReadOnly,
                ToolSearch = new QueryRuntimeToolSearchOptions { Enabled = true, TopK = 1 }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, model.Requests[0].Messages.Count);
        Assert.Equal(ChatRole.System, model.Requests[0].Messages[0].Role);
        Assert.Contains("tool_search", ReadText(model.Requests[0].Messages[0]), StringComparison.Ordinal);
        Assert.Contains("qre_read_file", ReadText(model.Requests[0].Messages[0]), StringComparison.Ordinal);
        Assert.Equal("host system", ReadText(model.Requests[0].Messages[1]));
        Assert.Equal(["tool_search"], model.Requests[0].Options?.Tools?.Select(tool => tool.Name).ToArray() ?? []);
    }

    [Fact]
    public async Task RunAsync_ToolSearchKeepsRequiredToolVisibleInFirstRound()
    {
        using var workspace = TemporaryWorkspace.Create();
        var model = new RecordingExperimentalModelClient(
            [new FunctionCallContent(
                "call-read",
                "qre_read_file",
                new Dictionary<string, object?> { ["path"] = "README.md", ["max_lines"] = 5 })],
            [new TextContent("done")]);
        var harness = new ExperimentalQueryRuntimeHarness(model);

        await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "read README",
                WorkspacePath = workspace.Path,
                RunId = "run-tool-search-required",
                MaxRounds = 2,
                EnableTools = true,
                ToolProfile = QueryRuntimeToolProfile.ReadOnly,
                RequiredToolName = "qre_read_file",
                ToolSearch = new QueryRuntimeToolSearchOptions { Enabled = true, TopK = 1 }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["qre_read_file", "tool_search"],
            model.Requests[0].Options?.Tools?.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray() ?? []);
        var requiredMode = Assert.IsType<RequiredChatToolMode>(model.Requests[0].Options?.ToolMode);
        Assert.Equal("qre_read_file", requiredMode.RequiredFunctionName);
    }

    [Fact]
    public void ToolSearch_SelectActivatesExactDeferredToolName()
    {
        var profile = QueryRuntimeToolProfile.ReadOnly;
        var readTool = AIFunctionFactory.Create(
            (string path) => $"read {path}",
            new AIFunctionFactoryOptions { Name = "qre_read_file", Description = "Read a file." });
        var descriptor = new ExperimentalToolRegistry().ListTools(profile)
            .Single(descriptor => descriptor.Name == "qre_read_file");
        var session = new ExperimentalToolSearchSession(
            profile,
            [readTool],
            [descriptor],
            new QueryRuntimeToolSearchOptions { Enabled = true });

        using var result = JsonDocument.Parse(session.Search("select:qre_read_file"));

        var tool = Assert.Single(result.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("qre_read_file", tool.GetProperty("name").GetString());
        Assert.True(tool.GetProperty("activated").GetBoolean());
        Assert.Contains("qre_read_file", session.ActiveToolNames);
    }

    [Fact]
    public void ToolSearch_UsesPlainTextByDefault_AndSupportsExplicitRegex()
    {
        var profile = QueryRuntimeToolProfile.ReadOnly;
        var descriptors = new[]
        {
            new QueryRuntimeToolDescriptor(
                "git_status",
                "Inspect git status.",
                new HashSet<string>(StringComparer.Ordinal),
                profile,
                new QueryRuntimeToolDiscoveryMetadata(
                    "Inspect git status.",
                    ["git", "status"],
                    [],
                    [],
                    [],
                    "git"),
                QueryRuntimeToolLoading.Deferred)
        };

        var plain = QueryRuntimeToolSearch.Search(
            descriptors,
            new QueryRuntimeToolSearchRequest
            {
                Query = "^git_status$",
                Profile = profile,
                TopK = 5
            });
        var regex = QueryRuntimeToolSearch.Search(
            descriptors,
            new QueryRuntimeToolSearchRequest
            {
                Query = "regex:git_status",
                Profile = profile,
                TopK = 5
            });

        Assert.Empty(plain);
        Assert.Contains(regex, hit => hit.Tool.Name == "git_status");
        Assert.Contains(regex, hit => hit.MatchedFields.Contains("regex"));
    }

    [Fact]
    public void ToolSearch_TopKAndRiskLimitActivation()
    {
        var profile = QueryRuntimeToolProfile.Repair;
        var writeTool = AIFunctionFactory.Create(
            (string path, string content) => $"write {path}",
            new AIFunctionFactoryOptions { Name = "qre_write_file", Description = "Write a file." });
        var patchTool = AIFunctionFactory.Create(
            (string path, string old_text, string new_text) => $"patch {path}",
            new AIFunctionFactoryOptions { Name = "qre_apply_patch", Description = "Apply a patch." });
        var descriptors = new ExperimentalToolRegistry().ListTools(profile)
            .Where(descriptor => descriptor.Name is "qre_write_file" or "qre_apply_patch")
            .ToArray();
        var session = new ExperimentalToolSearchSession(
            profile,
            [writeTool, patchTool],
            descriptors,
            new QueryRuntimeToolSearchOptions { Enabled = true, TopK = 1 });

        using var readResult = JsonDocument.Parse(session.Search("file", top_k: 1));
        Assert.Equal(1, readResult.RootElement.GetProperty("tools").GetArrayLength());
        Assert.False(readResult.RootElement.GetProperty("tools")[0].GetProperty("activated").GetBoolean());

        using var writeResult = JsonDocument.Parse(session.Search("patch file", top_k: 1));
        Assert.Equal(1, writeResult.RootElement.GetProperty("tools").GetArrayLength());
        Assert.True(writeResult.RootElement.GetProperty("tools")[0].GetProperty("activated").GetBoolean());
        Assert.Contains("qre_apply_patch", session.ActiveToolNames);
        Assert.DoesNotContain("qre_write_file", session.ActiveToolNames);
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

    private static string ReadText(ChatMessage message)
        => string.Concat(message.Contents.OfType<TextContent>().Select(static content => content.Text));

    private static void DeletePathIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

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
