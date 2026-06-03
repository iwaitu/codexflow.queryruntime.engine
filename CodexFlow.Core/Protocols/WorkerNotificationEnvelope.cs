using System;
using System.Collections.Generic;

namespace CodexFlow.Core.Protocols;

public enum WorkerType { Explore, Plan, Forge, Verify }
public enum WorkerStatus { Completed, Failed, WaitingUser }

public sealed record WorkerUsageInfo
{
    public int DurationMs { get; init; }
    public int ToolCalls { get; init; }
    public int WriteToolCalls { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
}

public sealed record WorkerWorktreeInfo
{
    public string? Path { get; init; }
    public bool Retained { get; init; }
    public string? CommitHash { get; init; }
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();
}

public sealed record WorkerRecoveryInfo
{
    public string? Reason { get; init; }
    public string? ResumeStrategy { get; init; }
    public string? Guidance { get; init; }
    public IReadOnlyList<string> RuntimeFlags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Checks { get; init; } = Array.Empty<string>();
}

public sealed record WorkerNotificationEnvelope
{
    public required string TaskId { get; init; }
    public required string JobId { get; init; }
    public required WorkerType WorkerType { get; init; }
    public required WorkerStatus Status { get; init; }
    public required string Summary { get; init; }
    public string? Result { get; init; }
    public string? ResumeToken { get; init; }
    public WorkerUsageInfo? Usage { get; init; }
    public WorkerWorktreeInfo? Worktree { get; init; }
    public WorkerRecoveryInfo? Recovery { get; init; }
    public DateTime CompletedAtUtc { get; init; }
}

public sealed record VerificationEvidence
{
    public required string Check { get; init; }
    public required bool Passed { get; init; }
    public string? Command { get; init; }
    public string? ExitCode { get; init; }
    public string? Observation { get; init; }
}

public sealed record VerificationReportEnvelope
{
    public required string TaskId { get; init; }
    public required string JobId { get; init; }
    public required string Verdict { get; init; }
    public required bool Passed { get; init; }
    public required string Summary { get; init; }
    public List<VerificationEvidence> Evidence { get; init; } = new();
    public List<string> Issues { get; init; } = new();
    public WorkerUsageInfo? Usage { get; init; }
}

public sealed record WaitingUserEnvelope
{
    public required string TaskId { get; init; }
    public required string JobId { get; init; }
    public required string ResumeToken { get; init; }
    public required string Reason { get; init; }
    public string? Context { get; init; }
}
