using CodexFlow.Core.Protocols;
using CodexFlow.Core.Telemetry;

namespace CodexFlow.Core.Workers;

public static class WorkerJobTypes
{
    public static bool TryParseWorkerType(string? value, out WorkerType workerType)
    {
        workerType = WorkerType.Explore;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "explore":
            case "exploreworker":
                workerType = WorkerType.Explore;
                return true;
            case "plan":
            case "planworker":
                workerType = WorkerType.Plan;
                return true;
            case "forge":
            case "forgeworker":
            case "executecodetask":
                workerType = WorkerType.Forge;
                return true;
            case "verify":
            case "verifyworker":
                workerType = WorkerType.Verify;
                return true;
            default:
                return false;
        }
    }

    public static bool TryResolveWorkerType(string? jobType, out WorkerType workerType)
        => TryParseWorkerType(jobType, out workerType);

    public static bool IsWorkerJobType(string? jobType)
        => TryResolveWorkerType(jobType, out _);

    public static string ToJobType(WorkerType workerType)
        => workerType switch
        {
            WorkerType.Explore => "ExploreWorker",
            WorkerType.Plan => "PlanWorker",
            WorkerType.Forge => "ForgeWorker",
            WorkerType.Verify => "VerifyWorker",
            _ => throw new ArgumentOutOfRangeException(nameof(workerType), workerType, "Unsupported worker type.")
        };

    public static QueryLoopEntryPoint ToEntryPoint(WorkerType workerType)
        => workerType switch
        {
            WorkerType.Explore => QueryLoopEntryPoint.ExploreWorker,
            WorkerType.Forge => QueryLoopEntryPoint.ForgeWorker,
            WorkerType.Plan => QueryLoopEntryPoint.PlanWorker,
            WorkerType.Verify => QueryLoopEntryPoint.VerifyWorker,
            _ => QueryLoopEntryPoint.DefaultCodexKernel
        };
}
