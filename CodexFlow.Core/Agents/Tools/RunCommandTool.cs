using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 在工作区执行白名单命令（替代 MCP run_command）。
/// 仅允许预定义的安全命令。
/// </summary>
public sealed record CommandExecutionPolicy(
    string Name,
    IReadOnlySet<string>? AllowedCommands = null,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? DeniedSubcommands = null,
    int MaxTimeoutSeconds = 3600,
    bool AllowBackground = true)
{
    public static CommandExecutionPolicy Default { get; } = new("default");

    public static CommandExecutionPolicy VerifyWorker { get; } = new(
        "verify-worker",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dotnet", "npm", "npx", "node", "python", "python3",
            "cargo", "go", "make", "cmake", "gradle", "mvn",
            "git", "ls", "dir", "cat", "head", "tail", "find", "grep", "rg", "wc",
            "echo", "pwd", "which", "env"
        },
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["git"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "add", "apply", "bisect", "branch", "checkout", "cherry-pick", "clean",
                "commit", "merge", "mv", "pull", "push", "rebase", "reset", "restore",
                "revert", "rm", "stash", "switch", "tag"
            },
            ["npm"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "adduser", "audit", "ci", "dedupe", "install", "link", "login", "logout",
                "publish", "rebuild", "star", "unpublish", "update", "version"
            },
            ["npx"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "npm", "yarn", "pnpm"
            },
            ["dotnet"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "add", "new", "nuget", "pack", "publish", "remove", "restore", "tool", "workload"
            }
        },
        MaxTimeoutSeconds: 300,
        AllowBackground: false);
}

public class RunCommandTool(ILogger<RunCommandTool> logger, CommandExecutionPolicy? policy = null) : ICodexTool
{
    private readonly CommandExecutionPolicy _policy = policy ?? CommandExecutionPolicy.Default;

    public string Name => "exec_cmd";
    public string Description => "在工作区中执行白名单命令。支持 dotnet/npm/git/python/grep/ls 等常用命令。\n" +
        "参数（JSON object）：\n" +
        "  - command (string[] 或 string, 必填): 命令及参数，推荐使用数组格式如 [\"dotnet\",\"build\"]，也可用字符串 \"dotnet build\"\n" +
        "  - cwd (string, 可选): 相对于工作区的工作目录，默认为工作区根目录\n" +
        "  - background (bool, 可选): true 时后台运行并立即返回 command_task_id，可用 task_output 读取增量输出；受 worker 策略限制\n" +
        "  - timeout_seconds (int, 可选): 超时秒数，默认 120，范围 1-3600；受 worker 策略上限限制\n" +
        "返回：同步模式返回 stdout/stderr/exit code 并包含 command_task_id；后台模式返回 command_task_id。\n" +
        "白名单：dotnet, npm, npx, node, python, python3, git, make, ls, cat, find, grep, rg, mkdir, cp, mv, echo, pwd, which\n" +
        "调用示例：\n" +
        "  exec_cmd({\"command\":[\"dotnet\",\"build\",\"CodexFlow.Core/CodexFlow.Core.csproj\"]})\n" +
        "  exec_cmd({\"command\":[\"dotnet\",\"test\"],\"background\":true})\n" +
        "  exec_cmd({\"command\":[\"dotnet\",\"test\",\"--filter\",\"FullyQualifiedName~MyTests\"]})\n" +
        "  exec_cmd({\"command\":[\"grep\",\"-rn\",\"IFileRepository\",\"src/\"],\"cwd\":\".\"})" +
        (_policy.Name.Equals(CommandExecutionPolicy.Default.Name, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $"\n当前命令策略：{_policy.Name}；最大超时 {_policy.MaxTimeoutSeconds}s；后台执行：{(_policy.AllowBackground ? "允许" : "禁止")}。");
    public ToolCategory Category => ToolCategory.Forge;
    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: true,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 16_384);
    public IReadOnlyList<int> AllowedStages => [3, 4];

    private const int DefaultTimeoutSeconds = 120;

    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "npm", "npx", "node", "python", "python3", "pip", "pip3",
        "cargo", "go", "git", "make", "cmake", "gradle", "mvn",
        "ls", "dir", "cat", "head", "tail", "find", "grep", "rg", "wc",
        "mkdir", "cp", "mv", "echo", "pwd", "which", "env"
    };

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var cwd = arguments.GetValueOrDefault("cwd")?.ToString();
        var background = arguments.GetValueOrDefault("background")?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
        var timeoutSeconds = DefaultTimeoutSeconds;
        if (arguments.GetValueOrDefault("timeout_seconds") is { } timeoutRaw &&
            int.TryParse(timeoutRaw.ToString(), out var parsedTimeout))
        {
            timeoutSeconds = Math.Clamp(parsedTimeout, 1, 3600);
        }
        timeoutSeconds = Math.Min(timeoutSeconds, Math.Clamp(_policy.MaxTimeoutSeconds, 1, 3600));

        // Parse command array
        var commandParts = ParseCommandArray(arguments.GetValueOrDefault("command"));
        if (commandParts is null || commandParts.Count == 0)
            return CodexToolResult.Error("Missing or invalid command parameter. Expected a string array like [\"dotnet\", \"build\"].");

        var executable = commandParts[0];
        if (!AllowedCommands.Contains(executable))
            return CodexToolResult.Error($"Command \"{executable}\" is not in the whitelist. Allowed: {string.Join(", ", AllowedCommands)}");
        if (!ValidatePolicy(commandParts, background, out var policyError))
            return CodexToolResult.Error(policyError);

        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        var workDir = !string.IsNullOrEmpty(baseRoot) ? baseRoot : (workspacePath ?? Directory.GetCurrentDirectory());
        if (!string.IsNullOrEmpty(cwd))
        {
            var normalizedCwd = ToolPathResolver.NormalizeDuplicateRepoPrefix(cwd, workDir);
            var resolvedWorkDir = Path.GetFullPath(Path.Combine(workDir, normalizedCwd));
            if (!ToolPathResolver.IsWithinRoot(resolvedWorkDir, baseRoot))
                return CodexToolResult.Error("Path traversal not allowed in cwd.");

            if (!Directory.Exists(resolvedWorkDir) &&
                TryResolveRedundantRepoCwdFallback(workDir, cwd, out var fallbackWorkDir, out var fallbackReason))
            {
                StructuredLog.Warning(logger, 
                    "run_command cwd fallback applied. cwd={Cwd}, baseRoot={BaseRoot}, initialResolved={InitialResolved}, fallback={Fallback}, reason={Reason}",
                    cwd,
                    baseRoot,
                    resolvedWorkDir,
                    fallbackWorkDir,
                    fallbackReason);
                resolvedWorkDir = fallbackWorkDir;
            }

            workDir = resolvedWorkDir;
        }

        if (!Directory.Exists(workDir))
            return CodexToolResult.Error($"Working directory not found: {cwd ?? workDir} (resolvedRoot={baseRoot}, resolvedCwd={workDir})");

        try
        {
            commandParts = NormalizePathLikeArguments(commandParts, workDir);
            commandParts = NormalizeCommandForPlatform(commandParts);
            executable = commandParts[0];

            var args = commandParts.Count > 1
                ? string.Join(' ', commandParts.Skip(1).Select(QuoteIfNeeded))
                : "";
            var displayCommand = string.IsNullOrWhiteSpace(args) ? executable : $"{executable} {args}";
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            };

            var commandTaskId = CommandTaskRegistry.Start(psi, displayCommand, TimeSpan.FromSeconds(timeoutSeconds));
            if (background)
            {
                return CodexToolResult.Succeeded(
                    $"COMMAND_TASK_STARTED\ncommand_task_id: {commandTaskId}\n$ {displayCommand}\nUse task_output({{\"job_id\":\"{commandTaskId}\"}}) to read stdout/stderr, and task_stop({{\"job_id\":\"{commandTaskId}\"}}) to stop it.",
                    new
                    {
                        CommandTaskId = commandTaskId,
                        Command = displayCommand,
                        WorkingDirectory = workDir,
                        Background = true,
                        TimeoutSeconds = timeoutSeconds
                    },
                    summary: $"command task started: {commandTaskId}");
            }

            var snapshot = await CommandTaskRegistry.WaitForCompletionAsync(commandTaskId, ct).ConfigureAwait(false);
            if (snapshot == null)
            {
                return CodexToolResult.Error($"command task not found after start: {commandTaskId}");
            }

            var output = BuildStreamText(snapshot, "stdout");
            var errors = BuildStreamText(snapshot, "stderr");

            // Truncate if too long
            const int maxChars = 8000;
            var originalOutputLength = output.Length;
            var originalErrorLength = errors.Length;
            if (output.Length > maxChars) output = output[..maxChars] + $"\n... (truncated, total {originalOutputLength} chars)";
            if (errors.Length > maxChars) errors = errors[..maxChars] + $"\n... (truncated, total {originalErrorLength} chars)";

            var result = new
            {
                CommandTaskId = commandTaskId,
                ExitCode = snapshot.ExitCode,
                Status = snapshot.Status.ToString(),
                Stdout = output,
                Stderr = errors
            };

            if (snapshot.Status != CommandTaskStatus.Completed || snapshot.ExitCode != 0)
            {
                return CodexToolResult.Succeeded(
                    $"⚠️ 命令执行完成（status {snapshot.Status}, exit code {snapshot.ExitCode?.ToString() ?? "n/a"}）\ncommand_task_id: {commandTaskId}\n$ {displayCommand}\n[stdout]\n{output}\n[stderr]\n{errors}",
                    result,
                    summary: $"command {commandTaskId}: status={snapshot.Status}, exit={snapshot.ExitCode?.ToString() ?? "n/a"}");
            }

            return CodexToolResult.Succeeded(
                $"✅ command_task_id: {commandTaskId}\n$ {displayCommand}\n{output}" + (string.IsNullOrWhiteSpace(errors) ? "" : $"\n[stderr]\n{errors}"),
                result,
                summary: $"command {commandTaskId}: exit=0");
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "run_command failed: {Cmd}", string.Join(' ', commandParts));
            return CodexToolResult.Error($"run_command 异常: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(logger, ex, "run_command failed: {Cmd}", string.Join(' ', commandParts));
            return CodexToolResult.Error($"run_command 异常: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(logger, ex, "run_command failed: {Cmd}", string.Join(' ', commandParts));
            return CodexToolResult.Error($"run_command 异常: {ex.Message}");
        }
        catch (Win32Exception ex)
        {
            StructuredLog.Error(logger, ex, "run_command failed: {Cmd}", string.Join(' ', commandParts));
            return CodexToolResult.Error($"run_command 异常: {ex.Message}");
        }
    }

    public RunCommandTool WithPolicy(CommandExecutionPolicy policy)
        => new(logger, policy);

    private bool ValidatePolicy(List<string> commandParts, bool background, out string error)
    {
        error = string.Empty;
        if (background && !_policy.AllowBackground)
        {
            error = $"Command policy \"{_policy.Name}\" does not allow background command tasks.";
            return false;
        }

        var executable = commandParts[0];
        if (_policy.AllowedCommands is not null && !_policy.AllowedCommands.Contains(executable))
        {
            error = $"Command \"{executable}\" is not allowed by command policy \"{_policy.Name}\".";
            return false;
        }

        if (commandParts.Count > 1 &&
            _policy.DeniedSubcommands is not null &&
            _policy.DeniedSubcommands.TryGetValue(executable, out var deniedSubcommands) &&
            deniedSubcommands.Contains(commandParts[1]))
        {
            error = $"Command \"{executable} {commandParts[1]}\" is denied by command policy \"{_policy.Name}\".";
            return false;
        }

        return true;
    }

    private static string BuildStreamText(CommandTaskSnapshot snapshot, string stream)
        => string.Join(
            Environment.NewLine,
            snapshot.Events
                .Where(evt => evt.Stream.Equals(stream, StringComparison.OrdinalIgnoreCase))
                .Select(evt => evt.Text));

    private static List<string>? ParseCommandArray(object? value)
    {
        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                return jsonElement.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToList();
            }

            if (jsonElement.ValueKind == JsonValueKind.String)
            {
                return ParseCommandArray(jsonElement.GetString());
            }
        }

        if (value is JArray jArray)
            return jArray.Select(token => token.ToString()).Where(token => !string.IsNullOrWhiteSpace(token)).ToList();

        if (value is IEnumerable<object> arr)
            return arr.Select(o => o.ToString()!).ToList();

        if (value is string s)
        {
            // Try parse as JSON array
            s = StripMarkdownCodeFences(s).Trim();
            if (s.StartsWith('['))
            {
                try
                {
                    var parts = System.Text.Json.JsonSerializer.Deserialize<List<string>>(s);
                    return parts;
                }
                catch (JsonException) { }
                catch (NotSupportedException) { }
            }
            // Fallback: split by space
            return s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        return null;
    }

    private static string StripMarkdownCodeFences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal) || !trimmed.EndsWith("```", StringComparison.Ordinal))
            return text;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
            return text;

        var body = trimmed[(firstNewline + 1)..];
        var closingFenceIndex = body.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFenceIndex < 0)
            return text;

        return body[..closingFenceIndex].TrimEnd('\r', '\n');
    }

    private static List<string> NormalizeCommandForPlatform(List<string> commandParts)
    {
        if (commandParts.Count == 0)
        {
            return commandParts;
        }

        if (!OperatingSystem.IsWindows())
        {
            return commandParts;
        }

        if (commandParts[0].Equals("pwd", StringComparison.OrdinalIgnoreCase) &&
            commandParts.Count == 1)
        {
            return ["cmd.exe", "/c", "cd"];
        }

        if ((commandParts[0].Equals("ls", StringComparison.OrdinalIgnoreCase) ||
             commandParts[0].Equals("dir", StringComparison.OrdinalIgnoreCase)) &&
            commandParts.Skip(1).All(IsSafeWindowsDirectoryListingArgument))
        {
            return ["cmd.exe", "/c", "dir", .. commandParts.Skip(1)];
        }

        if (commandParts[0].Equals("grep", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = new List<string>(commandParts.Count) { "rg" };
            foreach (var part in commandParts.Skip(1))
            {
                if (part is "-r" or "-R" or "--recursive")
                {
                    continue;
                }

                normalized.Add(part);
            }

            return normalized;
        }

        return commandParts;
    }

    private static bool IsSafeWindowsDirectoryListingArgument(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg) ||
            arg.StartsWith('-') ||
            arg.StartsWith('/'))
        {
            return false;
        }

        return arg.All(ch => char.IsLetterOrDigit(ch) ||
                             ch is '_' or '-' or '.' or '\\' or '/' or ':');
    }

    private static List<string> NormalizePathLikeArguments(List<string> commandParts, string workDir)
    {
        if (commandParts.Count <= 1)
            return commandParts;

        var normalized = new List<string>(commandParts.Count) { commandParts[0] };
        foreach (var part in commandParts.Skip(1))
        {
            if (!LooksLikePath(part))
            {
                normalized.Add(part);
                continue;
            }

            var candidate = ToolPathResolver.NormalizeDuplicateRepoPrefix(part, workDir);
            normalized.Add(candidate);
        }

        return normalized;
    }

    private static bool LooksLikePath(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return false;

        if (arg[0] == '-')
            return false;

        if (arg.Contains("://", StringComparison.Ordinal))
            return false;

        if (arg.Contains('/', StringComparison.Ordinal) || arg.Contains('\\', StringComparison.Ordinal))
            return true;

        return arg.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
               || arg.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
               || arg.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
               || arg.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
               || arg.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteIfNeeded(string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return "\"\"";

        if (arg.Contains(' ', StringComparison.Ordinal) && !(arg.StartsWith('"') && arg.EndsWith('"')))
            return $"\"{arg}\"";

        return arg;
    }

    private static bool TryResolveRedundantRepoCwdFallback(
        string rootWorkDir,
        string rawCwd,
        out string fallbackWorkDir,
        out string reason)
    {
        fallbackWorkDir = rootWorkDir;
        reason = string.Empty;

        var cwd = rawCwd.Trim().TrimStart('.', '/', '\\');
        if (string.IsNullOrWhiteSpace(cwd))
            return false;

        if (cwd.Contains('/', StringComparison.Ordinal) || cwd.Contains('\\', StringComparison.Ordinal))
            return false;

        var candidate = Path.Combine(rootWorkDir, cwd);
        if (Directory.Exists(candidate))
            return false;

        var isGitRepoRoot = Directory.Exists(Path.Combine(rootWorkDir, ".git"));
        var hasSolutionFile = Directory.EnumerateFiles(rootWorkDir, "*.sln*").Any();
        var hasCommonProjectLayout = Directory.Exists(Path.Combine(rootWorkDir, "src"))
                                     || Directory.Exists(Path.Combine(rootWorkDir, "test"));
        if (!isGitRepoRoot && !hasSolutionFile && !hasCommonProjectLayout)
            return false;

        fallbackWorkDir = rootWorkDir;
        reason = "cwd-single-segment-missing-under-likely-project-root";
        return true;
    }
}

