using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Engine;
using CodexFlow.QueryRuntime.Experimental;
using Microsoft.Extensions.AI;
using Xunit;
using EngineModelRequest = CodexFlow.QueryRuntime.Engine.QueryRuntimeModelRequest;

namespace CodexFlow.QueryRuntime.UnitTests.Experimental;

public sealed class TraceDataSecurityTests
{
    [Fact]
    public async Task PublicTrace_DefaultDoesNotPersistSensitiveCanaryAcrossArtifacts()
    {
        var canary = $"QRE_SECRET_CANARY_{Guid.NewGuid():N}";
        var runIdCanary = $"run-{canary}";
        var queryIdCanary = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var toolCanary = $"tool_canary_{Guid.NewGuid():N}";
        var callCanary = $"call-canary-{Guid.NewGuid():N}";
        using var workspace = TemporaryWorkspace.Create(canary);
        var tool = AIFunctionFactory.Create(
            (string value) => new string('x', 5000) + value,
            new AIFunctionFactoryOptions { Name = toolCanary, Description = "Echo a value." });
        var harness = new ExperimentalQueryRuntimeHarness(
            new ScriptedModelClient(
                [new FunctionCallContent(callCanary, toolCanary, new Dictionary<string, object?> { ["value"] = canary })],
                [new TextContent($"assistant:{canary}")]));

        var result = await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = $"prompt:{canary}",
                WorkspacePath = workspace.Path,
                RunId = runIdCanary,
                SessionId = canary,
                QueryIdFactory = () => queryIdCanary,
                MaxRounds = 3,
                EnableTools = true,
                Tools = [tool],
                RequiredToolName = toolCanary
            },
            TestContext.Current.CancellationToken);

        var runDirectory = JsonlTraceStore.GetRunDirectory(result.TraceFilePath);
        Assert.DoesNotContain(runIdCanary, Path.GetFileName(runDirectory), StringComparison.Ordinal);
        Assert.StartsWith("public-", Path.GetFileName(runDirectory), StringComparison.Ordinal);
        var artifactText = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(runDirectory, "*", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.DoesNotContain(canary, artifactText, StringComparison.Ordinal);
        Assert.DoesNotContain(toolCanary, artifactText, StringComparison.Ordinal);
        Assert.DoesNotContain(callCanary, artifactText, StringComparison.Ordinal);
        Assert.DoesNotContain(runIdCanary, artifactText, StringComparison.Ordinal);
        Assert.DoesNotContain(queryIdCanary.ToString("N"), artifactText, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(runDirectory, "blobs")));

        var records = JsonlTraceStore.ReadRecords(
            result.TraceFilePath,
            TestContext.Current.CancellationToken);
        var started = records.Single(static record => record.Type == "run.started");
        Assert.Equal("PublicRedacted", started.TryGetString("DataMode"));
        Assert.Equal("SummaryOnly", started.TryGetString("ReplayCapability"));
        Assert.Equal("[redacted]", started.TryGetString("SessionId"));
        Assert.NotEqual(runIdCanary, started.TryGetString("RunId"));
        Assert.Null(started.TryGetString("Prompt"));

        Assert.All(records.Where(static record => record.Root.TryGetProperty("QueryId", out _)), record =>
            Assert.Equal("[redacted]", record.TryGetString("QueryId")));

        var requested = records.Single(static record => record.Type == "tool.call.requested");
        Assert.True(requested.TryGetData(out var requestData));
        Assert.False(requestData.TryGetProperty("ToolName", out _));
        Assert.False(requestData.TryGetProperty("CallId", out _));
        Assert.False(requestData.TryGetProperty("Arguments", out _));
        Assert.False(requestData.TryGetProperty("ArgumentHash", out _));

        var completed = records.Single(static record => record.Type == "run.completed");
        Assert.Null(completed.TryGetString("TerminalDetailCode"));
        Assert.Null(completed.TryGetString("LastFunctionCall"));
        Assert.Null(completed.TryGetString("RequiredToolName"));
    }

    [Fact]
    public async Task SanitizedFixtureTrace_ExplicitlyEnablesFullFidelityReplayData()
    {
        using var workspace = TemporaryWorkspace.Create();
        var harness = new ExperimentalQueryRuntimeHarness(new StaticExperimentalModelClient("fixture response"));

        var result = await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "sanitized fixture prompt",
                WorkspacePath = workspace.Path,
                RunId = "sanitized-fixture",
                MaxRounds = 1,
                Trace = new QueryRuntimeTraceOptions
                {
                    DataMode = QueryRuntimeTraceDataMode.SanitizedFixture
                }
            },
            TestContext.Current.CancellationToken);

        var records = JsonlTraceStore.ReadRecords(
            result.TraceFilePath,
            TestContext.Current.CancellationToken);
        var started = records.Single(static record => record.Type == "run.started");
        Assert.Equal("sanitized fixture prompt", started.TryGetString("Prompt"));
        Assert.Equal("SanitizedFixture", started.TryGetString("DataMode"));
        Assert.Equal("FullFidelity", started.TryGetString("ReplayCapability"));
    }

    [Fact]
    public async Task PublicTrace_RedactsExceptionMessageCanary()
    {
        var canary = $"QRE_EXCEPTION_CANARY_{Guid.NewGuid():N}";
        using var workspace = TemporaryWorkspace.Create();
        var harness = new ExperimentalQueryRuntimeHarness(new ThrowingModelClient(canary));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "fail safely",
                WorkspacePath = workspace.Path,
                RunId = "public-error-redaction",
                MaxRounds = 1
            },
            TestContext.Current.CancellationToken));

        var runDirectory = JsonlTraceStore.FindLatestRunDirectory(workspace.Path);
        var artifactText = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(runDirectory, "*", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.DoesNotContain(canary, artifactText, StringComparison.Ordinal);
        Assert.Contains("[redacted]", artifactText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrivateDiagnosticTrace_UsesIsolatedStorageAndOwnerOnlyPermissions()
    {
        using var workspace = TemporaryWorkspace.Create();
        var harness = new ExperimentalQueryRuntimeHarness(
            new ScriptedModelClient(
                [new FunctionCallContent(
                    "private-repair-call",
                    "qre_write_file",
                    new Dictionary<string, object?>
                    {
                        ["path"] = "notes.txt",
                        ["content"] = "private repair"
                    })],
                [new TextContent("private response")]));

        var result = await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "private prompt",
                WorkspacePath = workspace.Path,
                RunId = "host-private-run",
                MaxRounds = 3,
                EnableTools = true,
                ToolProfile = QueryRuntimeToolProfile.Repair,
                Trace = new QueryRuntimeTraceOptions
                {
                    DataMode = QueryRuntimeTraceDataMode.PrivateDiagnostic,
                    PrivateDiagnosticRetention = TimeSpan.FromDays(1)
                }
            },
            TestContext.Current.CancellationToken);

        var runDirectory = JsonlTraceStore.GetRunDirectory(result.TraceFilePath);
        Assert.Contains(Path.Combine(".qre", "private", "runs"), runDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("host-private-run", Path.GetFileName(runDirectory), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(runDirectory, "repair-edits.txt")));
        var privateFiles = Directory.EnumerateFiles(runDirectory, "*", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(privateFiles);

        if (OperatingSystem.IsWindows())
        {
            AssertOwnerOnlyWindowsAcl(runDirectory, privateFiles);
        }
        else
        {
            AssertOwnerOnlyUnixMode(runDirectory, privateFiles);
        }
    }

    [Fact]
    public async Task PrivateDiagnosticTrace_PrunesExpiredRuns()
    {
        using var workspace = TemporaryWorkspace.Create();
        var staleRun = Path.Combine(workspace.Path, ".qre", "private", "runs", "stale-run");
        Directory.CreateDirectory(staleRun);
        File.WriteAllText(Path.Combine(staleRun, "events.jsonl"), "{}");
        File.WriteAllText(
            Path.Combine(staleRun, "manifest.json"),
            "{\"Type\":\"qre.run.manifest\",\"DataMode\":\"PrivateDiagnostic\",\"Status\":\"completed\"}");
        Directory.SetLastWriteTimeUtc(staleRun, DateTime.UtcNow.AddDays(-2));
        var harness = new ExperimentalQueryRuntimeHarness(new StaticExperimentalModelClient("private response"));

        _ = await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "private prompt",
                WorkspacePath = workspace.Path,
                RunId = "fresh-private-run",
                MaxRounds = 1,
                Trace = new QueryRuntimeTraceOptions
                {
                    DataMode = QueryRuntimeTraceDataMode.PrivateDiagnostic,
                    PrivateDiagnosticRetention = TimeSpan.FromHours(12)
                }
            },
            TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(staleRun));
    }

    [Fact]
    public async Task PrivateDiagnosticTrace_DoesNotPruneExpiredActiveRunWithoutTerminalManifest()
    {
        using var workspace = TemporaryWorkspace.Create();
        var activeRun = Path.Combine(workspace.Path, ".qre", "private", "runs", "active-run");
        Directory.CreateDirectory(activeRun);
        File.WriteAllText(Path.Combine(activeRun, "events.jsonl"), "{}");
        Directory.SetLastWriteTimeUtc(activeRun, DateTime.UtcNow.AddDays(-2));
        var harness = new ExperimentalQueryRuntimeHarness(new StaticExperimentalModelClient("private response"));

        _ = await harness.RunAsync(
            new ExperimentalQueryRuntimeRequest
            {
                Prompt = "private prompt",
                WorkspacePath = workspace.Path,
                RunId = "fresh-private-run",
                MaxRounds = 1,
                Trace = new QueryRuntimeTraceOptions
                {
                    DataMode = QueryRuntimeTraceDataMode.PrivateDiagnostic,
                    PrivateDiagnosticRetention = TimeSpan.FromHours(12)
                }
            },
            TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(activeRun));
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOwnerOnlyWindowsAcl(string runDirectory, IEnumerable<string> privateFiles)
    {
        var currentUser = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(currentUser);
        AssertAcl(new DirectoryInfo(runDirectory).GetAccessControl(AccessControlSections.Access));
        foreach (var privateFile in privateFiles)
        {
            AssertAcl(new FileInfo(privateFile).GetAccessControl(AccessControlSections.Access));
        }

        void AssertAcl(FileSystemSecurity security)
        {
            Assert.True(security.AreAccessRulesProtected);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            Assert.NotEmpty(rules.Cast<FileSystemAccessRule>());
            Assert.All(rules.Cast<FileSystemAccessRule>(), rule =>
            {
                Assert.False(rule.IsInherited);
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                Assert.Equal(currentUser, rule.IdentityReference);
            });
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void AssertOwnerOnlyUnixMode(string runDirectory, IEnumerable<string> privateFiles)
    {
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(runDirectory));
        Assert.All(privateFiles, path => Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(path)));
    }

    private sealed class ScriptedModelClient(params IReadOnlyList<AIContent>[] steps) : IExperimentalModelClient
    {
        private readonly Queue<IReadOnlyList<AIContent>> _steps = new(steps);

        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            EngineModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [.. _steps.Dequeue()]);
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private sealed class ThrowingModelClient(string message) : IExperimentalModelClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            EngineModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            throw new InvalidOperationException(message);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(string path) => Path = path;

        public string Path { get; }

        public static TemporaryWorkspace Create(string? suffix = null)
        {
            var name = suffix == null ? Guid.NewGuid().ToString("N") : $"{Guid.NewGuid():N}-{suffix}";
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qre-trace-data-tests", name);
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
