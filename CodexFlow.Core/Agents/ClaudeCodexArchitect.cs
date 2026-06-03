using System.Diagnostics;
using Microsoft.Extensions.Logging;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Agents;

// Claude-driven 架构师代理，利用本地 Claude Code CLI 进行架构评审
public class ClaudeCodexArchitect : ICodexArchitect
{
    private readonly ILogger<ClaudeCodexArchitect> _logger;

    public ClaudeCodexArchitect(
        ILogger<ClaudeCodexArchitect> logger)
    {
        _logger = logger;
    }

    public async Task<string> AnalyzeAsync(CodexSession session, string userGoal, CancellationToken ct = default)
    {
        StructuredLog.Information(_logger, "[Claude-Architect] Executing local Claude Code CLI analysis...");

        var startInfo = new ProcessStartInfo
        {
            FileName = "claude",
            Arguments = $"\"Analyze the impact of: {userGoal}\" --permission-mode bypassPermissions",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return process.ExitCode == 0 ? output : $"Claude CLI Error: {process.ExitCode}";
    }

    public async Task<List<CodexTask>> PlanAsync(CodexSession session, string goal, CancellationToken ct = default)
    {
        StructuredLog.Information(_logger, "[Claude-Architect] Generating structured development roadmap via CLI...");
        return new List<CodexTask> { new CodexTask { Id = "ClaudeGenerated", Description = "Pending CLI integration" } };
    }
}
