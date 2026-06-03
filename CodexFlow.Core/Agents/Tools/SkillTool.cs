using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

public sealed class SkillTool(
    ILogger<SkillTool> logger,
    ISkillScriptRunner? scriptRunner = null,
    string? skillRoot = null) : ICodexTool
{
    public string Name => "skill";

    public string Description => "统一管理本地 skills。参数: action(list/read/run_script), name?, script_path?, args?。list 列出 SKILL.md，read 读取指定 SKILL.md，run_script 运行 skill 目录内脚本。";

    public ToolCategory Category => ToolCategory.Read;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 16_384);

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        var action = arguments.GetValueOrDefault("action")?.ToString() ?? "list";
        var root = ResolveSkillRoot(skillRoot);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return CodexToolResult.Error("Skills directory not found.");
        }

        return action.Trim().ToLowerInvariant() switch
        {
            "list" => ListSkills(root),
            "read" => await ReadSkillAsync(root, arguments, ct).ConfigureAwait(false),
            "run_script" => await RunScriptAsync(root, arguments, ct).ConfigureAwait(false),
            _ => CodexToolResult.Error("action must be list, read, or run_script.")
        };
    }

    private static CodexToolResult ListSkills(string root)
    {
        var skills = Directory
            .EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories)
            .Select(path => new
            {
                Name = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty,
                RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/')
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var output = skills.Length == 0
            ? "No skills found."
            : $"Found {skills.Length} skill(s):\n- " + string.Join("\n- ", skills.Select(item => $"{item.Name} ({item.RelativePath})"));

        return CodexToolResult.Succeeded(output, new { Root = root, Skills = skills }, summary: $"skills listed: {skills.Length}");
    }

    private static async Task<CodexToolResult> ReadSkillAsync(string root, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var name = NormalizeSkillName(arguments.GetValueOrDefault("name")?.ToString()
            ?? arguments.GetValueOrDefault("fileName")?.ToString());
        if (string.IsNullOrWhiteSpace(name))
        {
            return CodexToolResult.Error("Missing name.");
        }

        if (!TryResolveSkillFile(root, name, out var skillFile))
        {
            return CodexToolResult.Error($"Skill not found: {name}");
        }

        var content = await File.ReadAllTextAsync(skillFile, ct).ConfigureAwait(false);
        return CodexToolResult.Succeeded(
            content,
            new { Skill = name, Path = skillFile },
            summary: $"skill read: {name}");
    }

    private async Task<CodexToolResult> RunScriptAsync(string root, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        if (scriptRunner == null)
        {
            return CodexToolResult.Error("Skill script runner is not available.");
        }

        var name = NormalizeSkillName(arguments.GetValueOrDefault("name")?.ToString());
        var scriptPath = arguments.GetValueOrDefault("script_path")?.ToString();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(scriptPath))
        {
            return CodexToolResult.Error("Missing name or script_path.");
        }

        if (!TryResolveSkillDirectory(root, name, out var skillDirectory))
        {
            return CodexToolResult.Error($"Skill not found: {name}");
        }

        var normalizedScriptPath = scriptPath.Trim().TrimStart('/', '\\');
        var fullScriptPath = Path.GetFullPath(Path.Combine(skillDirectory, normalizedScriptPath));
        if (!ToolPathResolver.IsWithinRoot(fullScriptPath, skillDirectory) || !File.Exists(fullScriptPath))
        {
            return CodexToolResult.Error("script_path must point to an existing file under the skill directory.");
        }

        var args = ParseArgs(arguments.GetValueOrDefault("args"));
        try
        {
            var output = await scriptRunner.RunAsync(name, normalizedScriptPath, args, ct).ConfigureAwait(false);
            return CodexToolResult.Succeeded(
                output,
                new { Skill = name, ScriptPath = normalizedScriptPath, Args = args },
                summary: $"skill script ran: {name}/{normalizedScriptPath}");
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "skill run_script failed: {Skill}/{Script}", name, normalizedScriptPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(logger, ex, "skill run_script failed: {Skill}/{Script}", name, normalizedScriptPath);
            return CodexToolResult.Error(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(logger, ex, "skill run_script failed: {Skill}/{Script}", name, normalizedScriptPath);
            return CodexToolResult.Error(ex.Message);
        }
    }

    private static IReadOnlyList<string> ParseArgs(object? value)
    {
        if (value == null)
        {
            return [];
        }

        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                return jsonElement.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToArray();
            }

            if (jsonElement.ValueKind == JsonValueKind.String)
            {
                return ParseArgs(jsonElement.GetString());
            }
        }

        if (value is JArray jArray)
        {
            return jArray.Select(item => item.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
        }

        if (value is IEnumerable<object> objects)
        {
            return objects.Select(item => item.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
        }

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (text.TrimStart()[0] == '[')
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(text) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string NormalizeSkillName(string? name)
        => (name ?? string.Empty)
            .Replace("/SKILL.md", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("\\SKILL.md", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim()
            .Trim('/', '\\');

    private static bool TryResolveSkillFile(string root, string name, out string skillFile)
    {
        skillFile = string.Empty;
        if (!TryResolveSkillDirectory(root, name, out var skillDirectory))
        {
            return false;
        }

        var candidate = Path.Combine(skillDirectory, "SKILL.md");
        if (!File.Exists(candidate))
        {
            return false;
        }

        skillFile = candidate;
        return true;
    }

    private static bool TryResolveSkillDirectory(string root, string name, out string skillDirectory)
    {
        skillDirectory = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(root, name));
        if (!ToolPathResolver.IsWithinRoot(candidate, root) || !Directory.Exists(candidate))
        {
            return false;
        }

        skillDirectory = candidate;
        return true;
    }

    private static string? ResolveSkillRoot(string? configuredRoot)
    {
        foreach (var candidate in BuildSkillRootCandidates(configuredRoot))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string?> BuildSkillRootCandidates(string? configuredRoot)
    {
        yield return configuredRoot;
        yield return Environment.GetEnvironmentVariable("CODEX_SKILLS_DIR");
        yield return Path.Combine(AppContext.BaseDirectory, "skills");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "skills");

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            yield return Path.Combine(current.FullName, "CodexFlow", "skills");
            yield return Path.Combine(current.FullName, "skills");
            current = current.Parent;
        }
    }
}
