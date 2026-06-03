using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

public sealed class ListMcpResourcesTool(
    ILogger<ListMcpResourcesTool> logger,
    string? mcpRoot = null) : ICodexTool
{
    public string Name => "list_mcp_resources";

    public string Description => "列出本地 MCP server 资源。参数: server?、pattern?、max_results?。返回 mcp://local/{server}/{relative_path} URI，可传给 read_mcp_resource。";

    public ToolCategory Category => ToolCategory.Read;
    public ToolExecutionMetadata Metadata => ToolExecutionMetadata.ForCategory(ToolCategory.Read);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        _ = ct;
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var root = McpResourcePathResolver.ResolveRoot(mcpRoot);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Task.FromResult(CodexToolResult.Error("MCP resource root not found."));
        }

        var server = arguments.GetValueOrDefault("server")?.ToString();
        var pattern = arguments.GetValueOrDefault("pattern")?.ToString();
        var maxResults = 100;
        if (int.TryParse(arguments.GetValueOrDefault("max_results")?.ToString(), out var parsedMax))
        {
            maxResults = Math.Clamp(parsedMax, 1, 500);
        }

        try
        {
            var resources = McpResourcePathResolver.ListResources(root, server, pattern, maxResults).ToArray();
            var output = resources.Length == 0
                ? "No MCP resources found."
                : $"Found {resources.Length} MCP resource(s):\n" + string.Join(
                    Environment.NewLine,
                    resources.Select(item => $"- {item.ResourceIdentifier} ({item.SizeBytes} bytes)"));

            return Task.FromResult(CodexToolResult.Succeeded(
                output,
                new { Root = root, Resources = resources },
                summary: $"mcp resources listed: {resources.Length}"));
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "list_mcp_resources failed under {Root}", root);
            return Task.FromResult(CodexToolResult.Error(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(logger, ex, "list_mcp_resources failed under {Root}", root);
            return Task.FromResult(CodexToolResult.Error(ex.Message));
        }
    }
}

public sealed class ReadMcpResourceTool(
    ILogger<ReadMcpResourceTool> logger,
    string? mcpRoot = null) : ICodexTool
{
    public string Name => "read_mcp_resource";

    public string Description => "读取 list_mcp_resources 返回的本地 MCP resource URI。参数: uri, max_chars?。只允许读取 CodexFlow.Mcp 根目录内的文本资源。";

    public ToolCategory Category => ToolCategory.Read;
    public ToolExecutionMetadata Metadata => ToolExecutionMetadata.ForCategory(ToolCategory.Read);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var root = McpResourcePathResolver.ResolveRoot(mcpRoot);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return CodexToolResult.Error("MCP resource root not found.");
        }

        var uri = arguments.GetValueOrDefault("uri")?.ToString();
        if (string.IsNullOrWhiteSpace(uri))
        {
            return CodexToolResult.Error("Missing uri.");
        }

        var maxChars = 16_384;
        if (int.TryParse(arguments.GetValueOrDefault("max_chars")?.ToString(), out var parsedMax))
        {
            maxChars = Math.Clamp(parsedMax, 256, 200_000);
        }

        if (!McpResourcePathResolver.TryResolveUri(root, uri, out var fullPath, out var error))
        {
            return CodexToolResult.Error(error);
        }

        try
        {
            var text = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
            var truncated = text.Length > maxChars;
            if (truncated)
            {
                text = text[..maxChars] + $"\n... (truncated, total {new FileInfo(fullPath).Length} bytes)";
            }

            var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            return CodexToolResult.Succeeded(
                text,
                new { Uri = uri, Path = relative, IsTruncated = truncated },
                summary: $"mcp resource read: {relative}",
                isOutputTruncated: truncated);
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "read_mcp_resource failed: {Uri}", uri);
            return CodexToolResult.Error(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(logger, ex, "read_mcp_resource failed: {Uri}", uri);
            return CodexToolResult.Error(ex.Message);
        }
    }
}

public sealed record McpResourceDescriptor(
    string ResourceIdentifier,
    string Server,
    string RelativePath,
    long SizeBytes);

internal static class McpResourcePathResolver
{
    private static readonly HashSet<string> TextResourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".jsonc", ".yaml", ".yml", ".toml", ".py", ".js", ".ts", ".cs", ".sh", ".ps1", ".dockerfile"
    };

    public static string? ResolveRoot(string? configuredRoot)
    {
        foreach (var candidate in BuildCandidates(configuredRoot))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public static IEnumerable<McpResourceDescriptor> ListResources(string root, string? server, string? pattern, int maxResults)
    {
        var serverDirs = Directory.EnumerateDirectories(root)
            .Where(dir => string.IsNullOrWhiteSpace(server) ||
                          string.Equals(Path.GetFileName(dir), server, StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

        foreach (var serverDir in serverDirs)
        {
            var serverName = Path.GetFileName(serverDir);
            foreach (var file in Directory.EnumerateFiles(serverDir, "*", SearchOption.AllDirectories)
                         .Where(IsTextResource)
                         .Where(file => MatchesPattern(file, pattern))
                         .OrderBy(file => Path.GetRelativePath(serverDir, file), StringComparer.OrdinalIgnoreCase)
                         .Take(maxResults))
            {
                var relative = Path.GetRelativePath(serverDir, file).Replace('\\', '/');
                yield return new McpResourceDescriptor(
                    $"mcp://local/{serverName}/{relative}",
                    serverName,
                    relative,
                    new FileInfo(file).Length);
            }
        }
    }

    public static bool TryResolveUri(string root, string uri, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        if (!uri.StartsWith("mcp://local/", StringComparison.OrdinalIgnoreCase))
        {
            error = "Only mcp://local/{server}/{relative_path} URIs are supported.";
            return false;
        }

        var pathPart = uri["mcp://local/".Length..].TrimStart('/', '\\');
        if (string.IsNullOrWhiteSpace(pathPart))
        {
            error = "Invalid MCP resource URI.";
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(root, pathPart.Replace('/', Path.DirectorySeparatorChar)));
        if (!ToolPathResolver.IsWithinRoot(candidate, root))
        {
            error = "MCP resource path traversal not allowed.";
            return false;
        }

        if (!File.Exists(candidate))
        {
            error = "MCP resource not found.";
            return false;
        }

        if (!IsTextResource(candidate))
        {
            error = "MCP resource is not a supported text file.";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static IEnumerable<string?> BuildCandidates(string? configuredRoot)
    {
        yield return configuredRoot;
        yield return Environment.GetEnvironmentVariable("CODEXFLOW_MCP_ROOT");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "CodexFlow.Mcp");

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            yield return Path.Combine(current.FullName, "CodexFlow.Mcp");
            current = current.Parent;
        }
    }

    private static bool IsTextResource(string file)
    {
        var extension = Path.GetExtension(file);
        return TextResourceExtensions.Contains(extension) ||
               string.Equals(Path.GetFileName(file), "Dockerfile", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesPattern(string file, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return true;
        }

        var normalized = file.Replace('\\', '/');
        return normalized.Contains(pattern.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }
}
