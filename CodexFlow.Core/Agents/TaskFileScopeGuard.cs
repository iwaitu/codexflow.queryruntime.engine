using System.Text.RegularExpressions;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Agents;

public sealed class TaskFileScopeDescriptor
{
    public HashSet<string> AllowedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllowedTestFileNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllowedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DisallowedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AllowProjectFileEdits { get; set; }
    public bool AllowDiRegistrationFiles { get; set; }
    public bool HasConstraints => AllowedFiles.Count > 0;
}

public static class TaskFileScopeGuard
{
    private static readonly string[] SiblingLayers =
    [
        "Infrastructure",
        "Persistence",
        "Application",
        "Domain",
        "Core"
    ];

    private static readonly Dictionary<string, string> LayerAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["core"] = "Core",
        ["infrastructure"] = "Infrastructure",
        ["application"] = "Application",
        ["domain"] = "Domain",
        ["persistence"] = "Persistence",
        ["api"] = "Api",
        ["web"] = "Web"
    };

    public static TaskFileScopeDescriptor BuildTaskFileScope(CodexTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var scope = new TaskFileScopeDescriptor();
        var text = string.Join("\n", new[]
        {
            task.Title ?? string.Empty,
            task.Description ?? string.Empty,
            string.Join("\n", task.Outputs)
        });

        var seedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathRegex = new Regex(@"(?:[A-Za-z0-9_.-]+[\\/])+[A-Za-z0-9_.-]+\.(?:csproj|cs|json|md)", RegexOptions.IgnoreCase);
        foreach (Match m in pathRegex.Matches(text))
        {
            var normalized = NormalizePathLike(m.Value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            scope.AllowedFiles.Add(normalized);
            var fileName = Path.GetFileNameWithoutExtension(normalized);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                scope.AllowedTestFileNames.Add($"{fileName}Tests.cs");
                scope.AllowedTestFileNames.Add($"{fileName}IntegrationTests.cs");
                scope.AllowedTestFileNames.Add($"{fileName}ArchitectureTests.cs");
            }

            var parentDir = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                scope.AllowedDirectories.Add(parentDir);
                seedDirectories.Add(parentDir);
            }
        }

        ExpandBareFileMentionsIntoSeedDirectories(text, scope, seedDirectories);

        var taskText = text.ToLowerInvariant();
        var exclusiveLayers = ExtractExplicitExclusiveLayers(taskText);
        var excludedLayers = ExtractExplicitExcludedLayers(taskText);

        var isCreationTask = taskText.Contains("创建", StringComparison.Ordinal) ||
                             taskText.Contains("新建", StringComparison.Ordinal) ||
                             taskText.Contains("引入接口", StringComparison.Ordinal) ||
                             taskText.Contains("添加类", StringComparison.Ordinal) ||
                             taskText.Contains("introduce", StringComparison.Ordinal) ||
                             taskText.Contains("create interface", StringComparison.Ordinal);

        if (isCreationTask)
        {
            var relatedDirs = new[] { "Interfaces", "Abstractions", "Repositories" };
            foreach (var dir in scope.AllowedDirectories.ToList())
            {
                var projectDir = ResolveProjectDirectory(dir) ?? dir;
                foreach (var related in relatedDirs)
                {
                    scope.AllowedDirectories.Add($"{projectDir}/{related}");
                }
            }
        }

        var isArchRefactor = taskText.Contains("重构", StringComparison.Ordinal) ||
                             taskText.Contains("架构", StringComparison.Ordinal) ||
                             taskText.Contains("refactor", StringComparison.Ordinal) ||
                             taskText.Contains("restructur", StringComparison.Ordinal) ||
                             taskText.Contains("引入仓储", StringComparison.Ordinal) ||
                             taskText.Contains("引入生成器", StringComparison.Ordinal) ||
                             taskText.Contains("introduce repository", StringComparison.Ordinal) ||
                             taskText.Contains("introduce generator", StringComparison.Ordinal) ||
                             taskText.Contains("依赖注入", StringComparison.Ordinal) ||
                             taskText.Contains("dependency injection", StringComparison.Ordinal) ||
                             taskText.Contains("di registration", StringComparison.Ordinal) ||
                             taskText.Contains("di 注册", StringComparison.Ordinal) ||
                             taskText.Contains("hexagonal", StringComparison.Ordinal) ||
                             taskText.Contains("clean architecture", StringComparison.Ordinal) ||
                             taskText.Contains("六边形", StringComparison.Ordinal) ||
                             taskText.Contains("洋葱架构", StringComparison.Ordinal);

        if (isArchRefactor || isCreationTask)
        {
            scope.AllowDiRegistrationFiles = true;

            foreach (var dir in scope.AllowedDirectories.ToList())
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                var projectDir = ResolveProjectDirectory(dir) ?? dir;
                var inProjectPatterns = new[]
                {
                    "Generators",
                    "Extensions",
                    "DependencyInjection",
                    "Implementations",
                    "Interfaces",
                    "Abstractions",
                    "Repositories"
                };

                foreach (var sub in inProjectPatterns)
                {
                    scope.AllowedDirectories.Add($"{projectDir}/{sub}");
                }

                var projectName = Path.GetFileName(projectDir);
                var solutionRoot = Path.GetDirectoryName(projectDir)?.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(solutionRoot) || string.IsNullOrWhiteSpace(projectName))
                {
                    continue;
                }

                var dotIdx = projectName.IndexOf('.', StringComparison.Ordinal);
                var projectPrefix = dotIdx > 0 ? projectName[..dotIdx] : projectName;

                foreach (var layer in SiblingLayers)
                {
                    scope.AllowedDirectories.Add($"{solutionRoot}/{projectPrefix}.{layer}");
                }

                scope.AllowedDirectories.Add($"{solutionRoot}/{projectPrefix}");
            }
        }

        if (taskText.Contains("项目引用", StringComparison.Ordinal) ||
            taskText.Contains("配置引用", StringComparison.Ordinal) ||
            taskText.Contains("csproj", StringComparison.Ordinal) ||
            taskText.Contains("package reference", StringComparison.Ordinal) ||
            isArchRefactor)
        {
            scope.AllowProjectFileEdits = true;
        }

        ApplyExplicitLayerRestrictions(scope, exclusiveLayers, excludedLayers);
        return scope;
    }

    public static IReadOnlyList<string> GetOutOfScopeChanges(
        IReadOnlyList<GitFileChange> changes,
        TaskFileScopeDescriptor scope)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(scope);

        var violations = new List<string>();
        foreach (var change in changes)
        {
            var normalized = NormalizePathLike(change.FilePath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            var isNewFile = string.Equals(change.Status, "A", StringComparison.OrdinalIgnoreCase);
            if (!IsPathInScope(normalized, scope, isNewFile))
            {
                violations.Add(normalized);
            }
        }

        return violations;
    }

    private static void ApplyExplicitLayerRestrictions(
        TaskFileScopeDescriptor scope,
        HashSet<string> exclusiveLayers,
        HashSet<string> excludedLayers)
    {
        if (exclusiveLayers.Count == 0 && excludedLayers.Count == 0)
        {
            return;
        }

        var seeds = scope.AllowedDirectories
            .Concat(scope.AllowedFiles.Select(file => Path.GetDirectoryName(file)?.Replace('\\', '/')))
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var seed in seeds)
        {
            var projectDir = ResolveProjectDirectory(seed) ?? seed!;
            var projectName = Path.GetFileName(projectDir);
            var solutionRoot = Path.GetDirectoryName(projectDir)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(solutionRoot))
            {
                continue;
            }

            var dotIdx = projectName.IndexOf('.', StringComparison.Ordinal);
            var projectPrefix = dotIdx > 0 ? projectName[..dotIdx] : projectName;

            if (exclusiveLayers.Count > 0)
            {
                foreach (var layer in SiblingLayers)
                {
                    if (exclusiveLayers.Contains(layer))
                    {
                        continue;
                    }

                    scope.DisallowedDirectories.Add($"{solutionRoot}/{projectPrefix}.{layer}");
                }
            }

            foreach (var excludedLayer in excludedLayers)
            {
                scope.DisallowedDirectories.Add($"{solutionRoot}/{projectPrefix}.{excludedLayer}");
            }
        }
    }

    private static void ExpandBareFileMentionsIntoSeedDirectories(
        string text,
        TaskFileScopeDescriptor scope,
        HashSet<string> seedDirectories)
    {
        if (string.IsNullOrWhiteSpace(text) || seedDirectories.Count == 0)
        {
            return;
        }

        var bareFileRegex = new Regex(
            @"(?<![\\/])\b(?<name>[A-Za-z0-9_.-]+\.(?:csproj|cs|json|md))\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (Match match in bareFileRegex.Matches(text))
        {
            var bareFileName = match.Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(bareFileName))
            {
                continue;
            }

            // Skip entries that already arrived through explicit path extraction.
            if (scope.AllowedFiles.Any(path =>
                    string.Equals(Path.GetFileName(path), bareFileName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var directory in seedDirectories)
            {
                scope.AllowedFiles.Add($"{directory.TrimEnd('/')}/{bareFileName}");
            }

            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(bareFileName);
            if (!string.IsNullOrWhiteSpace(fileNameWithoutExtension))
            {
                scope.AllowedTestFileNames.Add($"{fileNameWithoutExtension}Tests.cs");
                scope.AllowedTestFileNames.Add($"{fileNameWithoutExtension}IntegrationTests.cs");
                scope.AllowedTestFileNames.Add($"{fileNameWithoutExtension}ArchitectureTests.cs");
            }
        }
    }

    private static HashSet<string> ExtractExplicitExclusiveLayers(string taskText)
    {
        var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, canonical) in LayerAliases)
        {
            if (Regex.IsMatch(taskText, $@"(仅修改|只修改|仅限|只限|only modify|only touch)\s*{Regex.Escape(alias)}(\s*(layer|层))?",
                    RegexOptions.IgnoreCase) ||
                Regex.IsMatch(taskText, $@"{Regex.Escape(alias)}\s*(layer|层)\s*only",
                    RegexOptions.IgnoreCase))
            {
                layers.Add(canonical);
            }
        }

        return layers;
    }

    private static HashSet<string> ExtractExplicitExcludedLayers(string taskText)
    {
        var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, canonical) in LayerAliases)
        {
            if (Regex.IsMatch(taskText, $@"(不涉及|不修改|不要改|不得修改|不触碰)\s*{Regex.Escape(alias)}(\s*层)?",
                    RegexOptions.IgnoreCase) ||
                Regex.IsMatch(taskText, $@"(do not touch|do not modify|without touching)\s+{Regex.Escape(alias)}(\s+layer)?",
                    RegexOptions.IgnoreCase))
            {
                layers.Add(canonical);
            }
        }

        return layers;
    }

    private static string? ResolveProjectDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var current = directory.Replace('\\', '/').TrimEnd('/');
        while (!string.IsNullOrWhiteSpace(current))
        {
            var segment = Path.GetFileName(current);
            if (segment.Contains('.', StringComparison.Ordinal) &&
                !segment.EndsWith('.'))
            {
                return current;
            }

            var parent = Path.GetDirectoryName(current)?.Replace('\\', '/');
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent ?? string.Empty;
        }

        return null;
    }

    private static bool IsSystemManagedTaskSideEffect(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        var fileName = Path.GetFileName(normalizedPath);
        return string.Equals(fileName, "PROJECT_SUMMARY.md", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether a single normalized path is within the allowed scope.
    /// Used by both GetOutOfScopeChanges (post-execution) and TaskScopeInterventionHook (pre-execution).
    /// </summary>
    /// <param name="normalizedPath">Workspace-relative normalized path (forward slashes, no leading slash)</param>
    /// <param name="scope">The task file scope descriptor</param>
    /// <param name="isNewFile">Whether this path represents a file being created (not yet existing)</param>
    /// <returns>true if the path is in scope; false if it is out of scope</returns>
    public static bool IsPathInScope(string normalizedPath, TaskFileScopeDescriptor scope, bool isNewFile = false)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath) || scope == null)
        {
            return true; // Conservative: allow if path or scope is invalid
        }

        if (IsSystemManagedTaskSideEffect(normalizedPath))
        {
            return true;
        }

        // Explicit exclusion: disallowed directories block regardless of other rules
        var blockedByExplicitRestriction = scope.DisallowedDirectories.Any(dir =>
            normalizedPath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedPath, dir, StringComparison.OrdinalIgnoreCase));
        if (blockedByExplicitRestriction)
        {
            return false;
        }

        // Explicit inclusion: exact match in allowed files
        if (scope.AllowedFiles.Contains(normalizedPath))
        {
            return true;
        }

        // Suffix match: partial path overlap (e.g., "Interfaces/IUnitOfWork.cs" matches "src/Core/Interfaces/IUnitOfWork.cs")
        var isSuffixMatch = scope.AllowedFiles.Any(allowed =>
            normalizedPath.EndsWith("/" + allowed, StringComparison.OrdinalIgnoreCase) ||
            allowed.EndsWith("/" + normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (isSuffixMatch)
        {
            return true;
        }

        // New file in allowed directory
        if (isNewFile)
        {
            var isInAllowedDirectory = scope.AllowedDirectories.Any(dir =>
                normalizedPath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase));
            if (isInAllowedDirectory)
            {
                return true;
            }
        }

        // Project file edits (.csproj)
        if (scope.AllowProjectFileEdits &&
            normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // DI registration files
        if (scope.AllowDiRegistrationFiles)
        {
            var fn = Path.GetFileName(normalizedPath);
            if (string.Equals(fn, "Program.cs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fn, "Startup.cs", StringComparison.OrdinalIgnoreCase) ||
                fn.EndsWith("ServiceCollectionExtensions.cs", StringComparison.OrdinalIgnoreCase) ||
                fn.EndsWith("DependencyInjection.cs", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (fn.StartsWith("Base", StringComparison.OrdinalIgnoreCase) &&
                fn.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Scoped test files
        var fileName = Path.GetFileName(normalizedPath);
        var isScopedTest =
            (normalizedPath.StartsWith("test/", StringComparison.OrdinalIgnoreCase) ||
             normalizedPath.Contains("/test/", StringComparison.OrdinalIgnoreCase)) &&
            scope.AllowedTestFileNames.Contains(fileName);
        if (isScopedTest)
        {
            return true;
        }

        // Not in scope
        return false;
    }

    internal static string NormalizePathLike(string path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        var firstSlash = normalized.IndexOf('/', StringComparison.Ordinal);
        if (firstSlash > 0 && normalized.Contains("/src/", StringComparison.OrdinalIgnoreCase))
        {
            var srcIndex = normalized.IndexOf("/src/", StringComparison.OrdinalIgnoreCase);
            normalized = normalized[(srcIndex + 1)..];
        }
        else if (firstSlash > 0 && normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase))
        {
            var testIndex = normalized.IndexOf("/test/", StringComparison.OrdinalIgnoreCase);
            normalized = normalized[(testIndex + 1)..];
        }

        return normalized.TrimStart('/');
    }
}
