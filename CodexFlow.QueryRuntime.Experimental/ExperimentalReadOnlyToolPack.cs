using System.Text;
using CodexFlow.QueryRuntime.Abstractions;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Experimental;

public static class ExperimentalReadOnlyToolPack
{
    private const int DefaultMaxEntries = 200;
    private const int DefaultMaxLines = 200;
    private const int DefaultMaxMatches = 100;
    private const long MaxSearchFileBytes = 1_000_000;

    public static IReadOnlyList<AIFunction> Create(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var workspaceRoot = QueryRuntimePathSafety.NormalizeRoot(workspacePath);
        if (!Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException($"Workspace does not exist: {workspaceRoot}");
        }

        return
        [
            AIFunctionFactory.Create(
                (string path = ".", int max_entries = DefaultMaxEntries) => ListFiles(workspaceRoot, path, max_entries),
                new AIFunctionFactoryOptions
                {
                    Name = "qre_list_files",
                    Description = "List files and directories under the workspace root. Arguments: path, max_entries."
                }),
            AIFunctionFactory.Create(
                (string path, int start_line = 1, int max_lines = DefaultMaxLines) => ReadFile(workspaceRoot, path, start_line, max_lines),
                new AIFunctionFactoryOptions
                {
                    Name = "qre_read_file",
                    Description = "Read a UTF-8 text file under the workspace root. Arguments: path, start_line, max_lines."
                }),
            AIFunctionFactory.Create(
                (string pattern, string path = ".", int max_matches = DefaultMaxMatches) => SearchFiles(workspaceRoot, pattern, path, max_matches),
                new AIFunctionFactoryOptions
                {
                    Name = "qre_search_files",
                    Description = "Search text files under the workspace root. Arguments: pattern, path, max_matches."
                })
        ];
    }

    private static string ListFiles(string workspaceRoot, string path, int maxEntries)
    {
        maxEntries = Clamp(maxEntries, 1, 1_000);
        var target = ResolveWorkspacePath(workspaceRoot, path);
        if (!Directory.Exists(target))
        {
            return $"Directory not found: {path}";
        }

        var sb = new StringBuilder();
        foreach (var entry in Directory.EnumerateFileSystemEntries(target)
                     .Order(StringComparer.OrdinalIgnoreCase)
                     .Take(maxEntries))
        {
            var marker = Directory.Exists(entry) ? "/" : string.Empty;
            sb.AppendLine(Path.GetRelativePath(workspaceRoot, entry) + marker);
        }

        return sb.Length == 0 ? "(empty directory)" : sb.ToString();
    }

    private static string ReadFile(string workspaceRoot, string path, int startLine, int maxLines)
    {
        startLine = Math.Max(1, startLine);
        maxLines = Clamp(maxLines, 1, 1_000);
        var target = ResolveWorkspacePath(workspaceRoot, path);
        if (!File.Exists(target))
        {
            return $"File not found: {path}";
        }

        var extension = Path.GetExtension(target);
        if (IsLikelyBinaryExtension(extension))
        {
            return $"Binary file refused: {path}";
        }

        var sb = new StringBuilder();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(target).Skip(startLine - 1).Take(maxLines))
        {
            lineNumber++;
            sb.Append(startLine + lineNumber - 1).Append(": ").AppendLine(line);
        }

        return sb.Length == 0 ? "(no lines)" : sb.ToString();
    }

    private static string SearchFiles(string workspaceRoot, string pattern, string path, int maxMatches)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "Missing pattern.";
        }

        maxMatches = Clamp(maxMatches, 1, 1_000);
        var target = ResolveWorkspacePath(workspaceRoot, path);
        if (!Directory.Exists(target))
        {
            return $"Directory not found: {path}";
        }

        var sb = new StringBuilder();
        var matches = 0;
        foreach (var file in EnumerateCandidateFiles(workspaceRoot, target)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            var lineNumber = 0;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var line in lines)
            {
                lineNumber++;
                if (!line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                sb.Append(Path.GetRelativePath(workspaceRoot, file))
                    .Append(':')
                    .Append(lineNumber)
                    .Append(": ")
                    .AppendLine(line.Trim());
                matches++;
                if (matches >= maxMatches)
                {
                    return sb.ToString();
                }
            }
        }

        return matches == 0 ? "(no matches)" : sb.ToString();
    }

    private static string ResolveWorkspacePath(string workspaceRoot, string? path)
    {
        var target = QueryRuntimePathSafety.ResolveUnderRoot(workspaceRoot, path);
        QueryRuntimePathSafety.RejectProtectedWorkspacePath(workspaceRoot, target, "read by read-only tools");
        return target;
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string workspaceRoot, string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!ShouldSkipFile(file))
                {
                    yield return file;
                }
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                if (!ShouldSkipDirectory(workspaceRoot, directory))
                {
                    pending.Push(directory);
                }
            }
        }
    }

    private static bool ShouldSkipDirectory(string workspaceRoot, string directory)
    {
        var relative = Path.GetRelativePath(workspaceRoot, directory);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(static segment => segment is ".git" or "bin" or "obj" or "node_modules" or ".qre");
    }

    private static bool ShouldSkipFile(string file)
    {
        if (IsLikelyBinaryExtension(Path.GetExtension(file)))
        {
            return true;
        }

        try
        {
            return new FileInfo(file).Length > MaxSearchFileBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsLikelyBinaryExtension(string extension)
        => extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);

    private static int Clamp(int value, int min, int max)
        => Math.Min(max, Math.Max(min, value));
}
