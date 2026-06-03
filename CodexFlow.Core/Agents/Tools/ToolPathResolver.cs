using System.Text.RegularExpressions;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Agents.Tools;

public static class ToolPathResolver
{
    public const string PreferredProjectRootMetadataKey = "PreferredProjectRoot";
    public const string TrustedExternalProjectRootMetadataKey = "TrustedExternalProjectRoot";

    public static string ResolveBaseRoot(string? workspacePath, string? projectRoot)
    {
        if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
            return Path.GetFullPath(projectRoot);

        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
            return Path.GetFullPath(workspacePath);

        return string.Empty;
    }

    public static string ResolveProjectRoot(
        string? workspacePath,
        string? projectRoot,
        string? projectUrl = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return ResolveProjectRoot(workspacePath, projectRoot, CodexSession.CreateProjectUri(projectUrl), metadata);
    }

    public static string ResolveProjectRoot(
        string? workspacePath,
        string? projectRoot,
        Uri? projectUrl = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
            return Path.GetFullPath(projectRoot);

        if (metadata != null &&
            metadata.TryGetValue(TrustedExternalProjectRootMetadataKey, out var trustedExternalRoot) &&
            !string.IsNullOrWhiteSpace(trustedExternalRoot) &&
            Directory.Exists(trustedExternalRoot))
        {
            return Path.GetFullPath(trustedExternalRoot);
        }

        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return string.Empty;

        var normalizedWorkspace = Path.GetFullPath(workspacePath);

        if (metadata != null &&
            metadata.TryGetValue(PreferredProjectRootMetadataKey, out var preferredRoot) &&
            !string.IsNullOrWhiteSpace(preferredRoot) &&
            Directory.Exists(preferredRoot))
        {
            var normalizedPreferred = Path.GetFullPath(preferredRoot);
            if (IsWithinRoot(normalizedPreferred, normalizedWorkspace) || string.Equals(normalizedPreferred, normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedPreferred;
            }
        }

        if (Directory.Exists(Path.Combine(normalizedWorkspace, ".git")))
            return normalizedWorkspace;

        var candidateRepos = DiscoverGitRepositories(normalizedWorkspace).ToList();
        if (candidateRepos.Count == 0)
            return normalizedWorkspace;

        if (projectUrl != null)
        {
            var preferredName = TryGetRepoNameFromUrl(projectUrl);
            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                var matchingRepos = candidateRepos
                    .Where(path =>
                    {
                        var repoName = Path.GetFileName(path);
                        return repoName.Equals(preferredName, StringComparison.OrdinalIgnoreCase)
                               || repoName.StartsWith(preferredName + "-", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(path => Directory.GetLastWriteTimeUtc(path))
                    .ToList();

                if (matchingRepos.Count > 0)
                    return matchingRepos[0];
            }
        }

        if (candidateRepos.Count == 1)
            return candidateRepos[0];

        return candidateRepos
            .OrderByDescending(path => Directory.GetLastWriteTimeUtc(path))
            .First();
    }

    public static string NormalizeDuplicateRepoPrefix(string? path, string baseRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path ?? string.Empty;

        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized is ".")
            return ".";

        foreach (var prefix in GetRepoPrefixCandidates(baseRoot))
        {
            if (normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                return ".";

            if (normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return normalized[(prefix.Length + 1)..];
        }

        return normalized;
    }

    public static bool IsWithinRoot(string fullPath, string baseRoot)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(baseRoot))
            return false;

        return Path.GetFullPath(fullPath)
            .StartsWith(Path.GetFullPath(baseRoot), StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeDuplicateRepoPrefixInPatch(string patchContent, string baseRoot)
    {
        if (string.IsNullOrWhiteSpace(patchContent))
            return patchContent;

        var result = patchContent;
        foreach (var repoName in GetRepoPrefixCandidates(baseRoot))
        {
            var escapedRepo = Regex.Escape(repoName);

            // Codex patch format: *** Update/Add/Delete File: CleanApp/...
            result = Regex.Replace(
                result,
                $@"(?m)^(\*\*\* (?:Update|Add|Delete) File:\s+){escapedRepo}/",
                "$1");

            // Unified diff headers
            result = Regex.Replace(result, $@"(?m)^(---\s+(?:a/)?)" + escapedRepo + "/", "$1");
            result = Regex.Replace(result, $@"(?m)^(\+\+\+\s+(?:b/)?)" + escapedRepo + "/", "$1");
            result = Regex.Replace(result, $@"(?m)^(diff --git\s+a/)" + escapedRepo + "/", "$1");
            result = Regex.Replace(result, $@"(?m)^(diff --git\s+a/[^\s]+\s+b/)" + escapedRepo + "/", "$1");
        }

        return result;
    }

    private static IEnumerable<string> GetRepoPrefixCandidates(string baseRoot)
    {
        var normalizedRoot = Path.GetFullPath(baseRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var leaf = Path.GetFileName(normalizedRoot);
        if (!string.IsNullOrWhiteSpace(leaf) && seen.Add(leaf))
            yield return leaf;

        var dir = new DirectoryInfo(normalizedRoot);
        // shadow worktree layout: .../shadows/{repoName}/{taskId}
        if (dir.Parent is not null &&
            dir.Parent.Parent is not null &&
            dir.Parent.Parent.Name.Equals("shadows", StringComparison.OrdinalIgnoreCase))
        {
            var repoName = dir.Parent.Name;
            if (!string.IsNullOrWhiteSpace(repoName) && seen.Add(repoName))
                yield return repoName;
        }
    }

    private static IEnumerable<string> DiscoverGitRepositories(string workspacePath)
    {
        var workspaceFull = Path.GetFullPath(workspacePath);
        var queue = new Queue<(DirectoryInfo Dir, int Depth)>();
        queue.Enqueue((new DirectoryInfo(workspaceFull), 0));

        var ignoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", "bin", "obj", ".next", "dist", "build", ".venv", "shadows"
        };

        const int maxDepth = 3;
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            var current = dir.FullName;

            if (Directory.Exists(Path.Combine(current, ".git")))
            {
                yield return current;
                continue;
            }

            if (depth >= maxDepth)
                continue;

            DirectoryInfo[] children;
            try { children = dir.GetDirectories(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var child in children)
            {
                if (ignoredNames.Contains(child.Name))
                    continue;

                queue.Enqueue((child, depth + 1));
            }
        }
    }

    private static string? TryGetRepoNameFromUrl(Uri? projectUrl)
    {
        if (projectUrl == null)
            return null;

        var trimmed = projectUrl.ToString().Trim();
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        var slashIndex = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        if (slashIndex < 0 || slashIndex == trimmed.Length - 1)
            return null;

        var name = trimmed[(slashIndex + 1)..].Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
