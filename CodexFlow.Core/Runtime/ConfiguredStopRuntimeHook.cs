using CodexFlow.Core.Agents.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Runtime Stop hook backed by configured local commands.
/// </summary>
public sealed class ConfiguredStopRuntimeHook : IRuntimeHook
{
    private const string ProjectHookDirectoryName = ".codexflow";
    private const string ProjectHookSubdirectoryName = "hooks";
    private static readonly string[] ProjectStopHookNames =
    [
        "stop.cmd",
        "stop.bat",
        "stop.ps1",
        "stop.sh",
        "stop"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IOptions<RuntimeHookOptions> _options;
    private readonly ILogger<ConfiguredStopRuntimeHook> _logger;

    /// <summary>
    /// Creates a configured Stop hook runner.
    /// </summary>
    public ConfiguredStopRuntimeHook(
        IOptions<RuntimeHookOptions> options,
        ILogger<ConfiguredStopRuntimeHook> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ValueTask<AfterModelResponseHookResult> OnAfterModelResponseAsync(
        AfterModelResponseContext context,
        CancellationToken ct = default)
        => ValueTask.FromResult(AfterModelResponseHookResult.None);

    /// <inheritdoc />
    public async ValueTask<BeforeStopHookResult> OnBeforeStopAsync(
        BeforeStopContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopOptions = _options.Value.Stop;
        if (!stopOptions.Enabled)
        {
            return BeforeStopHookResult.None;
        }

        var commands = ResolveCommands(stopOptions, context);
        if (commands.Count == 0)
        {
            return BeforeStopHookResult.None;
        }

        var timeoutMs = Math.Max(100, stopOptions.TimeoutMs);
        foreach (var command in commands)
        {
            if (!IsRunnable(command))
            {
                continue;
            }

            var hookName = string.IsNullOrWhiteSpace(command.Name)
                ? command.FileName!
                : command.Name!;

            try
            {
                var result = await ExecuteCommandAsync(command, context, timeoutMs, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Configured Stop hook executed. Hook={HookName} ExitCode={ExitCode} TimedOut={TimedOut}",
                    hookName,
                    result.ExitCode,
                    result.TimedOut);

                if (result.TimedOut)
                {
                    continue;
                }

                if (result.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "Configured Stop hook returned non-zero exit code. Hook={HookName} ExitCode={ExitCode} Stderr={Stderr}",
                        hookName,
                        result.ExitCode,
                        TrimForLog(result.Stderr));
                    continue;
                }

                var decision = ParseDecision(result.Stdout, hookName);
                if (decision.Continue)
                {
                    return decision;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "Configured Stop hook failed. Hook={HookName}. Continuing with stop decision.",
                    hookName);
            }
        }

        return BeforeStopHookResult.None;
    }

    private static bool IsRunnable(StopHookCommandOptions command)
        => command.Enabled && !string.IsNullOrWhiteSpace(command.FileName);

    private static List<StopHookCommandOptions> ResolveCommands(
        StopRuntimeHookOptions stopOptions,
        BeforeStopContext context)
    {
        var commands = new List<StopHookCommandOptions>();
        commands.AddRange(stopOptions.Commands.Where(IsRunnable));

        if (stopOptions.EnableProjectHooks)
        {
            commands.AddRange(DiscoverProjectStopHookCommands(context));
        }

        return commands;
    }

    private static IEnumerable<StopHookCommandOptions> DiscoverProjectStopHookCommands(BeforeStopContext context)
    {
        foreach (var projectRoot in ResolveCandidateProjectRoots(context.Request))
        {
            var hookDirectory = Path.Combine(projectRoot, ProjectHookDirectoryName, ProjectHookSubdirectoryName);
            if (!Directory.Exists(hookDirectory))
            {
                continue;
            }

            foreach (var hookName in ProjectStopHookNames)
            {
                var hookPath = Path.Combine(hookDirectory, hookName);
                if (!File.Exists(hookPath))
                {
                    continue;
                }

                var command = BuildProjectHookCommand(hookPath, projectRoot);
                if (command != null)
                {
                    yield return command;
                }
            }
        }
    }

    private static IEnumerable<string> ResolveCandidateProjectRoots(QueryRuntimeRequest request)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in new[]
                 {
                     request.Session?.WorkspacePath,
                     request.PromptMetadata?.WorkspacePath,
                     ResolveProjectRoot(request)
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(candidate);
            if (seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }
    }

    private static StopHookCommandOptions? BuildProjectHookCommand(string hookPath, string projectRoot)
    {
        var extension = Path.GetExtension(hookPath);
        var hookFullPath = Path.GetFullPath(hookPath);

        if (OperatingSystem.IsWindows())
        {
            if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                return new StopHookCommandOptions
                {
                    Name = $"project-stop-hook:{Path.GetFileName(hookPath)}",
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{hookFullPath}\"\"",
                    WorkingDirectory = projectRoot
                };
            }

            if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                return new StopHookCommandOptions
                {
                    Name = $"project-stop-hook:{Path.GetFileName(hookPath)}",
                    FileName = "powershell",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{hookFullPath}\"",
                    WorkingDirectory = projectRoot
                };
            }

            return null;
        }

        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return new StopHookCommandOptions
            {
                Name = $"project-stop-hook:{Path.GetFileName(hookPath)}",
                FileName = "pwsh",
                Arguments = $"-NoProfile -File \"{hookFullPath}\"",
                WorkingDirectory = projectRoot
            };
        }

        return new StopHookCommandOptions
        {
            Name = $"project-stop-hook:{Path.GetFileName(hookPath)}",
            FileName = "/bin/sh",
            Arguments = $"\"{hookFullPath}\"",
            WorkingDirectory = projectRoot
        };
    }

    private static string ResolveProjectRoot(QueryRuntimeRequest request)
    {
        var session = request.Session;
        if (session != null)
        {
            return ToolPathResolver.ResolveProjectRoot(
                session.WorkspacePath,
                null,
                session.ProjectUrl,
                session.Metadata);
        }

        return ToolPathResolver.ResolveProjectRoot(
            request.PromptMetadata?.WorkspacePath,
            null,
            projectUrl: (string?)null,
            metadata: null);
    }

    private static BeforeStopHookResult ParseDecision(string stdout, string hookName)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return BeforeStopHookResult.None;
        }

        var json = ExtractJsonObject(stdout);
        if (string.IsNullOrWhiteSpace(json))
        {
            return BeforeStopHookResult.None;
        }

        var response = JsonSerializer.Deserialize<ConfiguredStopHookResponse>(json, JsonOptions);
        if (response == null)
        {
            return BeforeStopHookResult.None;
        }

        var shouldContinue = response.Continue || response.PreventStop;
        if (!shouldContinue)
        {
            return BeforeStopHookResult.None;
        }

        return new BeforeStopHookResult
        {
            Continue = true,
            Message = response.Message,
            Reason = string.IsNullOrWhiteSpace(response.Reason)
                ? $"configured Stop hook requested continuation: {hookName}"
                : response.Reason,
            AllowToolCallsOnNextRound = response.AllowToolCallsOnNextRound ?? true
        };
    }

    private static async Task<CommandExecutionResult> ExecuteCommandAsync(
        StopHookCommandOptions command,
        BeforeStopContext context,
        int timeoutMs,
        CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = command.FileName!,
            Arguments = command.Arguments ?? string.Empty,
            WorkingDirectory = string.IsNullOrWhiteSpace(command.WorkingDirectory)
                ? Environment.CurrentDirectory
                : command.WorkingDirectory!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        process.Start();

        var input = JsonSerializer.Serialize(BuildHookInput(context), JsonOptions);
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Hooks may choose to ignore stdin and exit after printing a decision. Treat a closed
            // stdin pipe as non-fatal so stdout can still drive the stop decision.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var waitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(waitTask, Task.Delay(timeoutMs, CancellationToken.None)).ConfigureAwait(false);
        if (completed != waitTask)
        {
            TryKill(process);
            var stdout = await CompleteReadAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await CompleteReadAsync(stderrTask).ConfigureAwait(false);
            return new CommandExecutionResult(-1, stdout, stderr, TimedOut: true);
        }

        await waitTask.ConfigureAwait(false);
        return new CommandExecutionResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false),
            TimedOut: false);
    }

    private static Dictionary<string, object?> BuildHookInput(BeforeStopContext context)
    {
        var projectRoot = ResolveProjectRoot(context.Request);
        return new Dictionary<string, object?>
        {
            ["hook_event_name"] = "Stop",
            ["session_id"] = context.Request.SessionId,
            ["entry_point"] = context.Request.EntryPoint.ToString(),
            ["workspace_path"] = context.Request.Session?.WorkspacePath ?? context.Request.PromptMetadata?.WorkspacePath,
            ["project_root"] = projectRoot,
            ["round"] = context.Round,
            ["stop_hook_active"] = context.StopHookActive,
            ["continuation_count"] = context.ContinuationCount,
            ["total_tool_calls"] = context.TotalToolCalls,
            ["executed_tools"] = context.ExecutedToolNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
            ["successful_tools"] = context.SuccessfulToolNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
            ["last_assistant_message"] = context.LastAssistantMessage,
            ["thinking_text"] = context.ThinkingText
        };
    }

    private static async Task<string> CompleteReadAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup for hook timeout.
        }
    }

    private static string TrimForLog(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private static string? ExtractJsonObject(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            return trimmed;
        }

        var start = value.IndexOf('{', StringComparison.Ordinal);
        var end = value.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return value[start..(end + 1)];
    }

    private sealed record ConfiguredStopHookResponse
    {
        [JsonPropertyName("continue")]
        public bool Continue { get; init; }

        [JsonPropertyName("prevent_stop")]
        public bool PreventStop { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }

        [JsonPropertyName("allow_tool_calls_on_next_round")]
        public bool? AllowToolCallsOnNextRound { get; init; }
    }

    private sealed record CommandExecutionResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        bool TimedOut);
}
