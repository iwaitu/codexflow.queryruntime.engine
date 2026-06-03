using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;

namespace CodexFlow.Core.Abstractions;

public interface IPlanArtifactStore
{
    Task<PlanArtifact?> GetAsync(string planArtifactId, CancellationToken ct = default);
    Task<PlanArtifact?> GetCurrentAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<PlanArtifact>> ListBySessionAsync(string sessionId, CancellationToken ct = default);
    Task SaveAsync(PlanArtifact artifact, CancellationToken ct = default);
    Task SetCurrentAsync(string sessionId, string planArtifactId, CancellationToken ct = default);
}

public interface IPlanFileService
{
    string GetPlanFilePath(CodexSession session, string planArtifactId);
    Task WriteAsync(CodexSession session, PlanArtifact artifact, string markdown, CancellationToken ct = default);
    Task<string?> ReadAsync(CodexSession session, PlanArtifact artifact, CancellationToken ct = default);
}

public interface IPlanBlueprintGenerator
{
    Task<string> GenerateAsync(CodexSession session, string goal, string context, CancellationToken ct = default);
}

public interface IPlanApprovalService
{
    Task<PlanArtifact> RequestApprovalAsync(string planArtifactId, CancellationToken ct = default);
    Task<PlanArtifact> ApproveAsync(string planArtifactId, string userId, string? feedback = null, CancellationToken ct = default);
    Task<PlanArtifact> RejectAsync(string planArtifactId, string userId, string? feedback = null, CancellationToken ct = default);
}

public interface IPlanProjectionService
{
    Task<PlanProjectionResult> ProjectAsync(PlanArtifact artifact, CodexSession session, CancellationToken ct = default);
}

public interface IPlanDiffService
{
    Task<PlanDiffResult> DiffAsync(string? fromPlanArtifactId, string toPlanArtifactId, CancellationToken ct = default);
}
