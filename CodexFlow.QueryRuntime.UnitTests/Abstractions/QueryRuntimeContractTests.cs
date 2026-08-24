using CodexFlow.QueryRuntime.Experimental;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Qre = CodexFlow.QueryRuntime.Abstractions;
using EngineModelRequest = CodexFlow.QueryRuntime.Engine.QueryRuntimeModelRequest;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Abstractions;

public sealed class QueryRuntimeContractTests
{
    [Fact]
    public async Task StableEngineContract_RunsThroughExperimentalHarness()
    {
        using var workspace = TemporaryWorkspace.Create();
        Qre.IQueryRuntimeEngine engine = new ExperimentalQueryRuntimeHarness(
            new StaticExperimentalModelClient("stable contract response"));

        var result = await engine.RunAsync(
            new Qre.QueryRuntimeRequest
            {
                Prompt = "explain the workspace",
                WorkspacePath = workspace.Path,
                RunId = "stable-contract-run",
                Execution = new Qre.QueryRuntimeExecutionOptions { MaxRounds = 1 }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("stable-contract-run", result.RunId);
        Assert.Equal("stable contract response", result.FinalText);
        Assert.True(File.Exists(result.TraceFilePath));
    }

    [Fact]
    public async Task HostEngineContract_RunsMessageHistoryCustomToolsAndStreaming()
    {
        using var workspace = TemporaryWorkspace.Create();
        var modelClient = new ScriptedHostModelClient();
        Qre.IQueryRuntimeHostEngine engine = new ExperimentalQueryRuntimeHarness(modelClient);
        var streamed = new List<string>();
        var toolCalls = 0;
        var tool = AIFunctionFactory.Create(
            () =>
            {
                toolCalls++;
                return "workspace-ok";
            },
            new AIFunctionFactoryOptions { Name = "workspace_info", Description = "Inspect the workspace." });

        var result = await engine.RunAsync(
            new Qre.QueryRuntimeHostRequest
            {
                InitialMessages =
                [
                    new ChatMessage(ChatRole.System, "You are the host replacement runtime."),
                    new ChatMessage(ChatRole.User, "inspect this repository")
                ],
                WorkspacePath = workspace.Path,
                RunId = "host-contract-run",
                SessionId = "codexflow-session-1",
                Tools = [tool],
                RequiredToolName = "workspace_info",
                Execution = new Qre.QueryRuntimeExecutionOptions { MaxRounds = 3 },
                TextDeltaSink = (text, _) =>
                {
                    streamed.Add(text);
                    return ValueTask.CompletedTask;
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("host-contract-run", result.RunId);
        Assert.Equal("codexflow-session-1", result.SessionId);
        Assert.Equal("host done", result.FinalText);
        Assert.Equal(1, result.TotalToolCalls);
        Assert.Contains(result.FinalMessages, message => message.Role == ChatRole.Tool);
        Assert.Contains(
            result.FinalMessages,
            message => message.Role == ChatRole.Assistant &&
                       ReadText(message) == "host done");
        Assert.Equal(1, toolCalls);
        Assert.Equal(["host done"], streamed);
        Assert.Equal(2, modelClient.Requests[0].Messages.Count);
        Assert.Equal(ChatRole.System, modelClient.Requests[0].Messages[0].Role);
        Assert.Contains(modelClient.Requests[1].Messages, message => message.Role == ChatRole.Tool);
        Assert.True(File.Exists(result.TraceFilePath));
    }

    [Fact]
    public async Task HostEngineContract_RespectsExplicitToolDisable()
    {
        using var workspace = TemporaryWorkspace.Create();
        var modelClient = new ScriptedHostModelClient();
        Qre.IQueryRuntimeHostEngine engine = new ExperimentalQueryRuntimeHarness(modelClient);
        var toolCalls = 0;
        var tool = AIFunctionFactory.Create(
            () =>
            {
                toolCalls++;
                return "workspace-ok";
            },
            new AIFunctionFactoryOptions { Name = "workspace_info", Description = "Inspect the workspace." });

        var result = await engine.RunAsync(
            new Qre.QueryRuntimeHostRequest
            {
                Prompt = "inspect this repository",
                WorkspacePath = workspace.Path,
                RunId = "host-tools-disabled",
                Tools = [tool],
                EnableTools = false,
                Execution = new Qre.QueryRuntimeExecutionOptions { MaxRounds = 3 }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.TotalToolCalls);
        Assert.Equal(0, toolCalls);
        Assert.Single(modelClient.Requests);
        Assert.Equal(ChatToolMode.None, modelClient.Requests[0].Options?.ToolMode);
        Assert.Empty(modelClient.Requests[0].Options?.Tools ?? []);
    }

    [Fact]
    public async Task HostEngineContract_MapsJsonOutputAndDoesNotMutateHostOptions()
    {
        using var workspace = TemporaryWorkspace.Create();
        var modelClient = new StaticCapturingModelClient("json response");
        Qre.IQueryRuntimeHostEngine engine = new ExperimentalQueryRuntimeHarness(modelClient);
        var hostOptions = new ChatOptions();
        var tool = AIFunctionFactory.Create(
            () => "workspace-ok",
            new AIFunctionFactoryOptions { Name = "workspace_info", Description = "Inspect the workspace." });

        await engine.RunAsync(
            new Qre.QueryRuntimeHostRequest
            {
                Prompt = "return json",
                WorkspacePath = workspace.Path,
                RunId = "host-json-options",
                Tools = [tool],
                Options = hostOptions,
                Output = new Qre.QueryRuntimeOutputOptions { RequestJson = true },
                Execution = new Qre.QueryRuntimeExecutionOptions { MaxRounds = 1 }
            },
            TestContext.Current.CancellationToken);

        Assert.Null(hostOptions.ResponseFormat);
        Assert.Null(hostOptions.ToolMode);
        Assert.Null(hostOptions.Tools);
        var runtimeOptions = Assert.Single(modelClient.Requests).Options;
        Assert.Equal(ChatResponseFormat.Json, runtimeOptions?.ResponseFormat);
        Assert.Single(runtimeOptions?.Tools ?? []);
    }

    [Fact]
    public void HostSecurityDecisions_AreSerializableForTraceAndAdapterLogs()
    {
        var toolDecision = Qre.QueryRuntimeToolInterventionDecision.BlockWithFeedback(
            "Blocked by host policy.",
            "write tools require scope approval",
            "tool_blocked");
        var stopDecision = Qre.QueryRuntimeStopDecision.RequireTool(
            "verify_state",
            "Run verify_state before final answer.",
            "required verification missing",
            "verification_incomplete");

        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var toolJson = JsonSerializer.Serialize(toolDecision, options);
        var stopJson = JsonSerializer.Serialize(stopDecision, options);

        Assert.Contains("BlockWithFeedback", toolJson, StringComparison.Ordinal);
        Assert.Contains("tool_blocked", toolJson, StringComparison.Ordinal);
        Assert.Contains("RequireTool", stopJson, StringComparison.Ordinal);
        Assert.Contains("verify_state", stopJson, StringComparison.Ordinal);
    }

    [Fact]
    public void PathSafety_ExposesReusableContainmentAndProtectedPathChecks()
    {
        using var workspace = TemporaryWorkspace.Create();
        var safeFile = Path.Combine(workspace.Path, "src", "file.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(safeFile)!);
        File.WriteAllText(safeFile, "ok");

        var resolved = Qre.QueryRuntimePathSafety.ResolveUnderRoot(workspace.Path, "src/file.txt");

        Assert.Equal(safeFile, resolved);
        Assert.True(Qre.QueryRuntimePathSafety.IsUnderRoot(workspace.Path, resolved));
        Assert.Throws<InvalidOperationException>(() =>
            Qre.QueryRuntimePathSafety.ResolveUnderRoot(workspace.Path, "../escape.txt"));
        Assert.Throws<InvalidOperationException>(() =>
            Qre.QueryRuntimePathSafety.RejectProtectedWorkspacePath(
                workspace.Path,
                Qre.QueryRuntimePathSafety.ResolveUnderRoot(workspace.Path, ".git/config"),
                "read"));
        Assert.True(Qre.QueryRuntimePathSafety.IsSecretLookingSegment(".env"));
        Assert.True(Qre.QueryRuntimePathSafety.IsSecretLookingSegment("TokenService.cs"));
        Assert.False(Qre.QueryRuntimePathSafety.IsProtectedCredentialSegment("TokenService.cs"));
        Assert.True(Qre.QueryRuntimePathSafety.IsProtectedCredentialSegment(".env"));
        Assert.True(Qre.QueryRuntimePathSafety.IsProtectedCredentialSegment(".env.staging"));
        Assert.True(Qre.QueryRuntimePathSafety.IsProtectedCredentialSegment(".env.qa"));
        Assert.False(Qre.QueryRuntimePathSafety.IsProtectedCredentialSegment(".env.example"));
        Assert.False(Qre.QueryRuntimePathSafety.IsProtectedCredentialSegment(".env.sample"));
        Assert.False(Qre.QueryRuntimePathSafety.IsProtectedCredentialSegment(".env.template"));

        var credential = Path.Combine(workspace.Path, ".env.staging");
        var alias = Path.Combine(workspace.Path, "src", "credential-alias.txt");
        File.WriteAllText(credential, "secret");
        try
        {
            try
            {
                File.CreateSymbolicLink(alias, credential);
            }
            catch (Exception createLinkException) when (createLinkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            Assert.Throws<InvalidOperationException>(() =>
                Qre.QueryRuntimePathSafety.RejectProtectedWorkspacePath(
                    workspace.Path,
                    Qre.QueryRuntimePathSafety.ResolveUnderRoot(workspace.Path, alias),
                    "read"));
        }
        finally
        {
            if (File.Exists(alias))
            {
                File.Delete(alias);
            }
        }
    }

    private static string ReadText(ChatMessage message)
        => string.Concat(message.Contents.Select(static content => content switch
        {
            TextContent text => text.Text,
            FunctionResultContent result => result.Result?.ToString() ?? string.Empty,
            _ => string.Empty
        }));

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryWorkspace Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codexflow-qre-contract-tests", Guid.NewGuid().ToString("N"));
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

    private sealed class ScriptedHostModelClient : IExperimentalModelClient
    {
        private int _calls;

        public List<EngineModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            EngineModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (_calls++ == 0)
            {
                yield return new ChatResponseUpdate
                {
                    Contents =
                    [
                        new FunctionCallContent(
                            "tool-1",
                            "workspace_info",
                            new Dictionary<string, object?>())
                    ]
                };
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "host done");
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private sealed class StaticCapturingModelClient(string response) : IExperimentalModelClient
    {
        public List<EngineModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            EngineModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response);
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
