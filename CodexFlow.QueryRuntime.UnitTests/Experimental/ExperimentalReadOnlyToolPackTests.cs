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
