using CodexFlow.Core.Models;

namespace CodexFlow.Core.Abstractions;

public interface IProjectMemoryService
{
    Task<ProjectMemoryDocument> LoadAsync(
        string workspacePath,
        string? projectRoot = null,
        Uri? projectUrl = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    Task<ProjectMemoryWriteResult> SaveAnalysisAsync(ProjectAnalysisMemoryInput input, CancellationToken ct = default);

    Task<ProjectMemoryWriteResult> SaveManualSummaryAsync(ProjectManualSummaryInput input, CancellationToken ct = default);

    Task<ProjectMemoryWriteResult> SaveExecutionResultAsync(ProjectExecutionMemoryInput input, CancellationToken ct = default);
}
