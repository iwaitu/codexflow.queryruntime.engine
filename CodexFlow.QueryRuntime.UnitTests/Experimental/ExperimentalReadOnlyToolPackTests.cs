using CodexFlow.QueryRuntime.Experimental;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Experimental;

public sealed class ExperimentalReadOnlyToolPackTests
{
    [Fact]
    public async Task ReadFile_RejectsPathTraversalOutsideWorkspace()
    {
        using var workspace = TemporaryWorkspace.Create();
        var outside = Path.Combine(Directory.GetParent(workspace.Path)!.FullName, "outside.txt");
        File.WriteAllText(outside, "outside");
        var tools = ExperimentalReadOnlyToolPack.Create(workspace.Path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(tools, "qre_read_file", new()
            {
                ["path"] = "../outside.txt"
            }));

        Assert.Contains("outside workspace", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFile_RejectsSymlinkToOutsideWorkspace()
    {
        using var workspace = TemporaryWorkspace.Create();
        var outside = Path.Combine(Path.GetTempPath(), $"qre-outside-{Guid.NewGuid():N}.txt");
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

            var tools = ExperimentalReadOnlyToolPack.Create(workspace.Path);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await InvokeAsync(tools, "qre_read_file", new()
                {
                    ["path"] = "outside-link.txt"
                }));

            Assert.Contains("Symlink traversal outside workspace", ex.Message, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(".git/config")]
    [InlineData(".qre/runs/events.jsonl")]
    [InlineData(".env")]
    [InlineData(".env.staging")]
    public async Task ReadFile_RejectsProtectedAndSecretLookingPaths(string path)
    {
        using var workspace = TemporaryWorkspace.Create();
        var target = System.IO.Path.Combine(workspace.Path, path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "sensitive");
        var tools = ExperimentalReadOnlyToolPack.Create(workspace.Path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(tools, "qre_read_file", new()
            {
                ["path"] = path
            }));

        Assert.Contains("cannot be read", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".env.example")]
    [InlineData(".env.sample")]
    [InlineData(".env.template")]
    public async Task ReadFile_AllowsEnvironmentTemplates(string path)
    {
        using var workspace = TemporaryWorkspace.Create();
        File.WriteAllText(System.IO.Path.Combine(workspace.Path, path), "safe template");
        var tools = ExperimentalReadOnlyToolPack.Create(workspace.Path);

        var result = await InvokeAsync(tools, "qre_read_file", new() { ["path"] = path });

        Assert.Contains("safe template", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFile_RejectsInWorkspaceAliasToProtectedCredential()
    {
        using var workspace = TemporaryWorkspace.Create();
        var credential = Path.Combine(workspace.Path, ".env.staging");
        var alias = Path.Combine(workspace.Path, "safe.txt");
        File.WriteAllText(credential, "API_KEY=must-not-read");
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

            var tools = ExperimentalReadOnlyToolPack.Create(workspace.Path);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await InvokeAsync(tools, "qre_read_file", new() { ["path"] = "safe.txt" }));

            Assert.Contains("Linked workspace paths cannot be read", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteFileIfExists(alias);
        }
    }

    [Fact]
    public async Task SearchFiles_SkipsProtectedCredentialsButAllowsEnvironmentTemplates()
    {
        using var workspace = TemporaryWorkspace.Create();
        File.WriteAllText(Path.Combine(workspace.Path, ".env.staging"), "QRE_SEARCH_CANARY=secret");
        File.WriteAllText(Path.Combine(workspace.Path, ".env.example"), "QRE_SEARCH_CANARY=template");
        var nested = Path.Combine(workspace.Path, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, ".env.qa"), "QRE_SEARCH_CANARY=nested-secret");
        var tools = ExperimentalReadOnlyToolPack.Create(workspace.Path);

        var result = await InvokeAsync(tools, "qre_search_files", new()
        {
            ["pattern"] = "QRE_SEARCH_CANARY",
            ["path"] = "."
        });

        Assert.Contains(".env.example", result, StringComparison.Ordinal);
        Assert.DoesNotContain(".env.staging", result, StringComparison.Ordinal);
        Assert.DoesNotContain(".env.qa", result, StringComparison.Ordinal);
        Assert.DoesNotContain("nested-secret", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchFiles_DoesNotFollowDirectoryLinkOutsideWorkspace()
    {
        using var workspace = TemporaryWorkspace.Create();
        var outside = Path.Combine(Path.GetTempPath(), $"qre-search-outside-{Guid.NewGuid():N}");
        var link = Path.Combine(workspace.Path, "linked-directory");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "outside.txt"), "QRE_OUTSIDE_SEARCH_CANARY");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception createLinkException) when (createLinkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var tools = ExperimentalReadOnlyToolPack.Create(workspace.Path);
            var result = await InvokeAsync(tools, "qre_search_files", new()
            {
                ["pattern"] = "QRE_OUTSIDE_SEARCH_CANARY",
                ["path"] = "."
            });

            Assert.Equal("(no matches)", result);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ListFiles_HidesProtectedAndLinkedEntries()
    {
        using var workspace = TemporaryWorkspace.Create();
        var credential = Path.Combine(workspace.Path, ".env.staging");
        var alias = Path.Combine(workspace.Path, "safe.txt");
        File.WriteAllText(credential, "secret");
        File.WriteAllText(Path.Combine(workspace.Path, ".env.example"), "template");
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

            var tools = ExperimentalReadOnlyToolPack.Create(workspace.Path);
            var result = await InvokeAsync(tools, "qre_list_files", new() { ["path"] = "." });

            Assert.Contains(".env.example", result, StringComparison.Ordinal);
            Assert.DoesNotContain(".env.staging", result, StringComparison.Ordinal);
            Assert.DoesNotContain("safe.txt", result, StringComparison.Ordinal);
        }
        finally
        {
            DeleteFileIfExists(alias);
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

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
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
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codexflow-qre-readonly-tools", Guid.NewGuid().ToString("N"));
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
