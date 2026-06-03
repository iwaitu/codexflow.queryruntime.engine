using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CodexFlow.Core.Protocols;

public sealed class XmlWorkerNotificationSerializer : IWorkerNotificationSerializer
{
    public string Serialize(WorkerNotificationEnvelope env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var sb = new StringBuilder();
        sb.AppendLine("<task-notification>");
        AppendElement(sb, "task-id", env.TaskId);
        AppendElement(sb, "job-id", env.JobId);
        AppendElement(sb, "worker-type", env.WorkerType.ToString().ToLowerInvariant());
        AppendElement(sb, "status", FormatStatus(env.Status));
        AppendLongTextElement(sb, "summary", env.Summary);
        AppendLongTextElement(sb, "result", env.Result);
        AppendElement(sb, "resume-token", env.ResumeToken);
        
        if (env.Usage != null)
        {
            sb.AppendLine("  <usage>");
            AppendElement(sb, "duration_ms", env.Usage.DurationMs.ToString());
            AppendElement(sb, "tool_calls", env.Usage.ToolCalls.ToString());
            AppendElement(sb, "write_tool_calls", env.Usage.WriteToolCalls.ToString());
            
            if (env.Usage.InputTokens > 0 || env.Usage.OutputTokens > 0)
            {
                sb.AppendLine("    <tokens>");
                AppendElement(sb, "input", env.Usage.InputTokens.ToString());
                AppendElement(sb, "output", env.Usage.OutputTokens.ToString());
                sb.AppendLine("    </tokens>");
            }
            sb.AppendLine("  </usage>");
        }

        if (env.Worktree != null)
        {
            sb.AppendLine("  <worktree>");
            AppendElement(sb, "path", env.Worktree.Path);
            AppendElement(sb, "retained", env.Worktree.Retained.ToString().ToLowerInvariant());
            AppendElement(sb, "commit-hash", env.Worktree.CommitHash);

            if (env.Worktree.ChangedFiles.Count > 0)
            {
                sb.AppendLine("    <changed-files>");
                foreach (var changedFile in env.Worktree.ChangedFiles.Where(static path => !string.IsNullOrWhiteSpace(path)))
                {
                    AppendElement(sb, "file", changedFile);
                }
                sb.AppendLine("    </changed-files>");
            }

            sb.AppendLine("  </worktree>");
        }

        if (env.Recovery != null)
        {
            sb.AppendLine("  <recovery>");
            AppendElement(sb, "reason", env.Recovery.Reason);
            AppendElement(sb, "resume-strategy", env.Recovery.ResumeStrategy);
            AppendLongTextElement(sb, "guidance", env.Recovery.Guidance);

            if (env.Recovery.RuntimeFlags.Count > 0)
            {
                sb.AppendLine("    <runtime-flags>");
                foreach (var flag in env.Recovery.RuntimeFlags.Where(static flag => !string.IsNullOrWhiteSpace(flag)))
                {
                    AppendElement(sb, "flag", flag);
                }
                sb.AppendLine("    </runtime-flags>");
            }

            if (env.Recovery.Steps.Count > 0)
            {
                sb.AppendLine("    <steps>");
                foreach (var step in env.Recovery.Steps.Where(static step => !string.IsNullOrWhiteSpace(step)))
                {
                    AppendLongTextElement(sb, "step", step);
                }
                sb.AppendLine("    </steps>");
            }

            if (env.Recovery.Checks.Count > 0)
            {
                sb.AppendLine("    <checks>");
                foreach (var check in env.Recovery.Checks.Where(static check => !string.IsNullOrWhiteSpace(check)))
                {
                    AppendLongTextElement(sb, "check", check);
                }
                sb.AppendLine("    </checks>");
            }

            sb.AppendLine("  </recovery>");
        }
        
        AppendElement(sb, "completed-at", env.CompletedAtUtc.ToString("O"));
        sb.AppendLine("</task-notification>");
        return sb.ToString();
    }

    public string Serialize(VerificationReportEnvelope env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var sb = new StringBuilder();
        sb.AppendLine("<verification-report>");
        AppendElement(sb, "task-id", env.TaskId);
        AppendElement(sb, "job-id", env.JobId);
        AppendElement(sb, "verdict", env.Verdict);
        AppendElement(sb, "passed", env.Passed.ToString().ToLowerInvariant());
        AppendElement(sb, "summary", env.Summary);

        if (env.Evidence.Count > 0)
        {
            sb.AppendLine("  <evidence-list>");
            foreach (var e in env.Evidence)
            {
                sb.AppendLine("    <evidence>");
                AppendElement(sb, "check", e.Check);
                AppendElement(sb, "passed", e.Passed.ToString().ToLowerInvariant());
                AppendElement(sb, "command", e.Command);
                AppendElement(sb, "exit_code", e.ExitCode);
                AppendLongTextElement(sb, "observation", e.Observation);
                sb.AppendLine("    </evidence>");
            }
            sb.AppendLine("  </evidence-list>");
        }

        if (env.Issues.Count > 0)
        {
            sb.AppendLine("  <issues>");
            foreach (var issue in env.Issues)
                AppendElement(sb, "issue", issue);
            sb.AppendLine("  </issues>");
        }

        if (env.Usage != null)
        {
            sb.AppendLine("  <usage>");
            AppendElement(sb, "duration_ms", env.Usage.DurationMs.ToString());
            AppendElement(sb, "tool_calls", env.Usage.ToolCalls.ToString());
            sb.AppendLine("  </usage>");
        }

        sb.AppendLine("</verification-report>");
        return sb.ToString();
    }

    public string Serialize(WaitingUserEnvelope env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var sb = new StringBuilder();
        sb.AppendLine("<waiting-user>");
        AppendElement(sb, "task-id", env.TaskId);
        AppendElement(sb, "job-id", env.JobId);
        AppendElement(sb, "resume-token", env.ResumeToken);
        AppendElement(sb, "reason", env.Reason);
        AppendLongTextElement(sb, "context", env.Context);
        sb.AppendLine("</waiting-user>");
        return sb.ToString();
    }

    private static void AppendElement(StringBuilder sb, string tag, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.AppendLine($"  <{tag}>{XmlEscape(value)}</{tag}>");
    }

    private static void AppendLongTextElement(StringBuilder sb, string tag, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        
        bool useCData = value.Length > 500 || value.Contains('<') || value.Contains('&');
        if (useCData)
            sb.AppendLine($"  <{tag}><![CDATA[{EscapeCData(value)}]]></{tag}>");
        else
            sb.AppendLine($"  <{tag}>{XmlEscape(value)}</{tag}>");
    }

    private static string XmlEscape(string text)
    {
        return text.Replace("&", "&amp;")
                   .Replace("<", "&lt;")
                   .Replace(">", "&gt;")
                   .Replace("\"", "&quot;")
                   .Replace("'", "&apos;");
    }

    private static string EscapeCData(string text) => text.Replace("]]>", "]]]]><![CDATA[>");

    private static string FormatStatus(WorkerStatus status) =>
        status switch
        {
            WorkerStatus.Completed => "completed",
            WorkerStatus.Failed => "failed",
            WorkerStatus.WaitingUser => "waiting",
            _ => status.ToString().ToLowerInvariant()
        };
}
