using CodexFlow.QueryRuntime.Experimental;
using CodexFlow.QueryRuntime.Abstractions;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Experimental;

public sealed class ExperimentalRepairToolPackTests
{
    [Fact]
    public async Task WriteFile_WritesInsideWorkspaceAndRecordsRunEdit()
    {
        using var workspace = TemporaryWorkspace.Create();
        var runDirectory = Path.Combine(workspace.Path, ".qre", "runs", "repair-test");
        var tools = ExperimentalRepairToolPack.Create(workspace.Path, runDirectory);

        var result = await InvokeAsync(tools, "qre_write_file", new()
        {
            ["path"] = "src/notes.txt",
            ["content"] = "hello repair" + Environment.NewLine
        });

        Assert.Contains("wrote src/notes.txt", result, StringComparison.Ordinal);
        Assert.Equal("hello repair" + Environment.NewLine, File.ReadAllText(Path.Combine(workspace.Path, "src", "notes.txt")));
        Assert.Equal("src/notes.txt", File.ReadAllText(Path.Combine(runDirectory, "repair-edits.txt")).Trim());
    }

    [Fact]
    public async Task ApplyPatch_ReplacesTargetedText()
    {
        using var workspace = TemporaryWorkspace.Create();
        File.WriteAllText(Path.Combine(workspace.Path, "notes.txt"), "before TODO after");
        var tools = ExperimentalRepairToolPack.Create(workspace.Path, Path.Combine(workspace.Path, ".qre", "runs", "patch-test"));

        var result = await InvokeAsync(tools, "qre_apply_patch", new()
        {
            ["path"] = "notes.txt",
            ["old_text"] = "TODO",
            ["new_text"] = "done"
        });

        Assert.Contains("patched notes.txt", result, StringComparison.Ordinal);
        Assert.Equal("before done after", File.ReadAllText(Path.Combine(workspace.Path, "notes.txt")));
    }

    [Fact]
    public async Task WriteFile_RejectsPathTraversalOutsideWorkspace()
    {
        using var workspace = TemporaryWorkspace.Create();
        var sink = new RecordingPolicyDecisionSink();
        var tools = ExperimentalRepairToolPack.Create(workspace.Path, policyDecisionSink: sink);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(tools, "qre_write_file", new()
            {
                ["path"] = "../outside.txt",
                ["content"] = "outside"
            }));

        Assert.Contains("outside workspace", ex.Message, StringComparison.Ordinal);
        var record = Assert.Single(sink.Decisions);
        Assert.Equal("Deny", record.Decision);
        Assert.False(record.Allowed);
    }

    [Theory]
    [InlineData(".qre/config.toml")]
    [InlineData(".QRE/events.jsonl")]
    [InlineData(".git/config")]
    [InlineData(".Git/config")]
    [InlineData(".env")]
    [InlineData(".env.staging")]
    [InlineData(".env.test")]
    [InlineData("keys/credentials")]
    [InlineData("keys/id_rsa")]
    public async Task WriteFile_RejectsProtectedAndSecretLookingPaths(string path)
    {
        using var workspace = TemporaryWorkspace.Create();
        var tools = ExperimentalRepairToolPack.Create(workspace.Path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(tools, "qre_write_file", new()
            {
                ["path"] = path,
                ["content"] = "secret"
            }));

        Assert.True(
            ex.Message.Contains("Protected workspace artifacts", StringComparison.Ordinal) ||
            ex.Message.Contains("Protected credential paths", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("src/TokenService.cs")]
    [InlineData("tests/SecretMaskerTests.cs")]
    [InlineData("docs/credentials-guide.md")]
    [InlineData(".env.example")]
    [InlineData(".env.sample")]
    [InlineData(".env.template")]
    public async Task WriteFile_AllowsFuzzySecretLookingSourcePaths(string path)
    {
        using var workspace = TemporaryWorkspace.Create();
        var tools = ExperimentalRepairToolPack.Create(workspace.Path);

        _ = await InvokeAsync(tools, "qre_write_file", new()
        {
            ["path"] = path,
            ["content"] = "normal source content"
        });

        Assert.True(File.Exists(Path.Combine(workspace.Path, path)));
    }

    [Fact]
    public async Task WriteFile_RejectsNestedSymlinkEscape()
    {
        using var workspace = TemporaryWorkspace.Create();
        var outside = Path.Combine(Path.GetTempPath(), $"qre-repair-outside-{Guid.NewGuid():N}.txt");
        var intermediateLink = Path.Combine(workspace.Path, "inside-link.txt");
        var entryLink = Path.Combine(workspace.Path, "entry-link.txt");
        File.WriteAllText(outside, "outside");
        try
        {
            try
            {
                File.CreateSymbolicLink(intermediateLink, outside);
                File.CreateSymbolicLink(entryLink, intermediateLink);
            }
            catch (Exception createLinkException) when (createLinkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var tools = ExperimentalRepairToolPack.Create(workspace.Path);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await InvokeAsync(tools, "qre_write_file", new()
                {
                    ["path"] = "entry-link.txt",
                    ["content"] = "must not write through a symlink chain"
                }));

            Assert.Contains("Symlink traversal outside workspace", ex.Message, StringComparison.Ordinal);
            Assert.Equal("outside", File.ReadAllText(outside));
        }
        finally
        {
            DeleteFileIfExists(entryLink);
            DeleteFileIfExists(intermediateLink);
            DeleteFileIfExists(outside);
        }
    }

    [Fact]
    public async Task WriteFile_RejectsSymlinkEscape()
    {
        using var workspace = TemporaryWorkspace.Create();
        var outside = Path.Combine(Path.GetTempPath(), $"qre-repair-outside-{Guid.NewGuid():N}.txt");
        var link = Path.Combine(workspace.Path, "outside-link.txt");
        File.WriteAllText(outside, "outside");
        try
        {
            try
            {
                File.CreateSymbolicLink(link, outside);
            }
            catch (Exception createLinkException) when (createLinkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var tools = ExperimentalRepairToolPack.Create(workspace.Path);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await InvokeAsync(tools, "qre_write_file", new()
                {
                    ["path"] = "outside-link.txt",
                    ["content"] = "must not write outside"
                }));

            Assert.Contains("Symlink traversal outside workspace", ex.Message, StringComparison.Ordinal);
            Assert.Equal("outside", File.ReadAllText(outside));
        }
        finally
        {
            if (File.Exists(link))
            {
                File.Delete(link);
            }
            if (File.Exists(outside))
            {
                File.Delete(outside);
            }
        }
    }

    [Fact]
    public async Task WriteFile_RejectsInWorkspaceAliasToProtectedCredential()
    {
        using var workspace = TemporaryWorkspace.Create();
        var credential = Path.Combine(workspace.Path, ".env.staging");
        var alias = Path.Combine(workspace.Path, "safe.txt");
        File.WriteAllText(credential, "API_KEY=must-not-change");
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

            var tools = ExperimentalRepairToolPack.Create(workspace.Path);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await InvokeAsync(tools, "qre_write_file", new()
                {
                    ["path"] = "safe.txt",
                    ["content"] = "overwritten"
                }));

            Assert.Contains("Linked workspace paths cannot be modified", ex.Message, StringComparison.Ordinal);
            Assert.Equal("API_KEY=must-not-change", File.ReadAllText(credential));
        }
        finally
        {
            DeleteFileIfExists(alias);
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static async Task<string> InvokeAsync(
        IReadOnlyList<AIFunction> tools,
        string name,
        Dictionary<string, object?> arguments)
    {
        var tool = tools.Single(tool => tool.Name == name);
        var result = await tool.InvokeAsync(new AIFunctionArguments(arguments), TestContext.Current.CancellationToken);
        return result?.ToString() ?? string.Empty;
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
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codexflow-qre-repair-tools", Guid.NewGuid().ToString("N"));
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

    private sealed class RecordingPolicyDecisionSink : IQueryRuntimePolicyDecisionSink
    {
        public List<QueryRuntimePolicyDecisionRecord> Decisions { get; } = [];

        public Task OnPolicyDecisionAsync(QueryRuntimePolicyDecisionRecord decision, CancellationToken ct = default)
        {
            Decisions.Add(decision);
            return Task.CompletedTask;
        }
    }
}
