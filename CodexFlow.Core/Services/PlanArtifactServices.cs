using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Collections.Concurrent;

namespace CodexFlow.Core.Services;

public sealed class InMemoryPlanArtifactStore : IPlanArtifactStore
{
    private readonly ConcurrentDictionary<string, PlanArtifact> _artifacts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _currentBySession = new(StringComparer.Ordinal);

    public Task<PlanArtifact?> GetAsync(string planArtifactId, CancellationToken ct = default)
    {
        _artifacts.TryGetValue(planArtifactId, out var artifact);
        return Task.FromResult(CloneOrNull(artifact));
    }

    public Task<PlanArtifact?> GetCurrentAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_currentBySession.TryGetValue(sessionId, out var id) || !_artifacts.TryGetValue(id, out var artifact))
        {
            return Task.FromResult<PlanArtifact?>(null);
        }

        return Task.FromResult(CloneOrNull(artifact));
    }

    public Task<IReadOnlyList<PlanArtifact>> ListBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        var result = _artifacts.Values
            .Where(artifact => string.Equals(artifact.SessionId, sessionId, StringComparison.Ordinal))
            .OrderByDescending(artifact => artifact.UpdatedAtUtc)
            .Select(Clone)
            .ToList();
        return Task.FromResult<IReadOnlyList<PlanArtifact>>(result);
    }

    public Task SaveAsync(PlanArtifact artifact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact.UpdatedAtUtc = DateTime.UtcNow;
        _artifacts[artifact.Id] = Clone(artifact);
        if (!string.IsNullOrWhiteSpace(artifact.SessionId))
        {
            _currentBySession.TryAdd(artifact.SessionId, artifact.Id);
        }

        return Task.CompletedTask;
    }

    public Task SetCurrentAsync(string sessionId, string planArtifactId, CancellationToken ct = default)
    {
        _currentBySession[sessionId] = planArtifactId;
        return Task.CompletedTask;
    }

    private static PlanArtifact? CloneOrNull(PlanArtifact? artifact) => artifact == null ? null : Clone(artifact);

    private static PlanArtifact Clone(PlanArtifact artifact)
        => JsonConvert.DeserializeObject<PlanArtifact>(JsonConvert.SerializeObject(artifact)) ?? new PlanArtifact();
}

public sealed class WorkspacePlanFileService(IOptions<PlanningOptions> options) : IPlanFileService
{
    private readonly PlanningOptions _options = options.Value;

    public string GetPlanFilePath(CodexSession session, string planArtifactId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var workspace = ResolveWorkspace(session);
        var slug = PlanArtifactSupport.CreateSessionSlug(session.Id);
        var filename = string.IsNullOrWhiteSpace(planArtifactId) ? $"{slug}.md" : $"{slug}-{planArtifactId[..Math.Min(8, planArtifactId.Length)]}.md";
        var relativeDirectory = string.IsNullOrWhiteSpace(_options.PlansDirectory)
            ? ".codexflow/plans"
            : _options.PlansDirectory;
        var fullPath = Path.GetFullPath(Path.Combine(workspace, relativeDirectory, filename));
        EnsureWithinWorkspace(workspace, fullPath);
        return fullPath;
    }

    public async Task WriteAsync(CodexSession session, PlanArtifact artifact, string markdown, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(artifact);
        var path = string.IsNullOrWhiteSpace(artifact.PlanFilePath)
            ? GetPlanFilePath(session, artifact.Id)
            : Path.GetFullPath(Path.Combine(ResolveWorkspace(session), artifact.PlanFilePath));
        EnsureWithinWorkspace(ResolveWorkspace(session), path);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ResolveWorkspace(session));
        var nextMarkdown = markdown ?? string.Empty;
        var markdownChanged = !string.Equals(artifact.Markdown, nextMarkdown, StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, nextMarkdown, ct).ConfigureAwait(false);
        artifact.PlanFilePath = Path.GetRelativePath(ResolveWorkspace(session), path).Replace('\\', '/');
        artifact.Markdown = nextMarkdown;
        if (markdownChanged)
        {
            artifact.Projection = null;
            artifact.PreviewProjectedMarkdownHash = null;
            artifact.ReplaceTasks(null);
            if (artifact.Status is PlanArtifactStatus.ReadyToExecute or PlanArtifactStatus.Projecting)
            {
                artifact.Status = PlanArtifactStatus.Draft;
            }
        }
    }

    public async Task<string?> ReadAsync(CodexSession session, PlanArtifact artifact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(artifact);
        if (string.IsNullOrWhiteSpace(artifact.PlanFilePath))
        {
            return artifact.Markdown;
        }

        var path = Path.GetFullPath(Path.Combine(ResolveWorkspace(session), artifact.PlanFilePath));
        EnsureWithinWorkspace(ResolveWorkspace(session), path);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : artifact.Markdown;
    }

    private static string ResolveWorkspace(CodexSession session)
    {
        if (string.IsNullOrWhiteSpace(session.WorkspacePath))
        {
            throw new InvalidOperationException("Session workspace path is required for plan file operations.");
        }

        var workspace = Path.GetFullPath(session.WorkspacePath);
        if (!Directory.Exists(workspace))
        {
            Directory.CreateDirectory(workspace);
        }

        return workspace;
    }

    private static void EnsureWithinWorkspace(string workspace, string path)
    {
        var root = Path.GetFullPath(workspace);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Plan file path must stay inside the workspace.");
        }
    }
}

public sealed class DefaultPlanBlueprintGenerator : IPlanBlueprintGenerator
{
    public Task<string> GenerateAsync(CodexSession session, string goal, string context, CancellationToken ct = default)
    {
        var markdown = $"""
# Plan: {TrimLine(goal, 80)}

## Context
{TrimBlock(context, 2000)}

## Scope
- In scope: implement the requested change inside the current workspace.
- Out of scope: unrelated refactors, unrelated dependency upgrades, and publishing operations unless explicitly requested.

## Findings
- Detailed code findings should be added during planning exploration.

## Proposed Approach
- Derive executable tasks from the approved plan.
- Preserve existing project patterns and validate each task with deterministic checks.

## Impacted Files
- To be confirmed by exploration and projection.

## Execution Steps
1. Inspect the relevant files and existing project conventions.
2. Apply the smallest scoped changes needed for the request.
3. Run the appropriate build and test commands.

## Risks
- The plan must be projected and validated before execution.

## Verification
- Run the project-specific build command.
- Run relevant automated tests.

## Open Questions
- None.
""";

        return Task.FromResult(markdown);
    }

    private static string TrimLine(string? value, int max)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "Implementation Plan" : value.Trim().ReplaceLineEndings(" ");
        return text.Length <= max ? text : text[..max];
    }

    private static string TrimBlock(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "No additional context was available.";
        }

        var text = value.Trim();
        return text.Length <= max ? text : text[..max];
    }
}

public sealed class DefaultPlanApprovalService(IPlanArtifactStore store) : IPlanApprovalService
{
    public async Task<PlanArtifact> RequestApprovalAsync(string planArtifactId, CancellationToken ct = default)
    {
        var artifact = await LoadAsync(planArtifactId, ct).ConfigureAwait(false);
        PreservePreviewTasksIfCurrent(artifact);
        artifact.Status = PlanArtifactStatus.ReadyForApproval;
        artifact.Projection = null;
        await store.SaveAsync(artifact, ct).ConfigureAwait(false);
        return artifact;
    }

    public async Task<PlanArtifact> ApproveAsync(string planArtifactId, string userId, string? feedback = null, CancellationToken ct = default)
    {
        var artifact = await LoadAsync(planArtifactId, ct).ConfigureAwait(false);
        if (artifact.Status != PlanArtifactStatus.ReadyForApproval)
        {
            throw new InvalidOperationException("Only ReadyForApproval plans can be approved.");
        }

        artifact.Status = PlanArtifactStatus.Approved;
        var approvedHash = PlanArtifactSupport.ComputeMarkdownHash(artifact.Markdown);
        PreservePreviewTasksIfCurrent(artifact, approvedHash);
        artifact.Approval = new PlanApprovalRecord
        {
            ApprovedByUserId = userId ?? string.Empty,
            Feedback = feedback,
            ApprovedMarkdownHash = approvedHash
        };
        artifact.Projection = null;
        await store.SaveAsync(artifact, ct).ConfigureAwait(false);
        return artifact;
    }

    public async Task<PlanArtifact> RejectAsync(string planArtifactId, string userId, string? feedback = null, CancellationToken ct = default)
    {
        var artifact = await LoadAsync(planArtifactId, ct).ConfigureAwait(false);
        artifact.Status = PlanArtifactStatus.Rejected;
        artifact.Approval = new PlanApprovalRecord
        {
            ApprovedByUserId = userId ?? string.Empty,
            Feedback = feedback,
            Mode = "rejected",
            ApprovedMarkdownHash = PlanArtifactSupport.ComputeMarkdownHash(artifact.Markdown)
        };
        artifact.Projection = null;
        artifact.PreviewProjectedMarkdownHash = null;
        artifact.ReplaceTasks(null);
        await store.SaveAsync(artifact, ct).ConfigureAwait(false);
        return artifact;
    }

    private static void PreservePreviewTasksIfCurrent(PlanArtifact artifact, string? markdownHash = null)
    {
        var currentHash = markdownHash ?? PlanArtifactSupport.ComputeMarkdownHash(artifact.Markdown);
        if (artifact.Tasks.Count > 0 &&
            string.Equals(artifact.PreviewProjectedMarkdownHash, currentHash, StringComparison.Ordinal))
        {
            return;
        }

        artifact.PreviewProjectedMarkdownHash = null;
        artifact.ReplaceTasks(null);
    }

    private async Task<PlanArtifact> LoadAsync(string id, CancellationToken ct)
        => await store.GetAsync(id, ct).ConfigureAwait(false)
           ?? throw new InvalidOperationException($"Plan artifact {id} was not found.");
}

public sealed class DefaultPlanProjectionService(
    ICodexPlanner planner,
    IPlanArtifactStore store,
    ILogger<DefaultPlanProjectionService> logger) : IPlanProjectionService
{
    public async Task<PlanProjectionResult> ProjectAsync(PlanArtifact artifact, CodexSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(session);

        if (artifact.Status != PlanArtifactStatus.Approved)
        {
            return Fail(artifact, "Plan must be Approved before projection.");
        }

        var approvedHash = artifact.Approval?.ApprovedMarkdownHash;
        var currentHash = PlanArtifactSupport.ComputeMarkdownHash(artifact.Markdown);
        if (string.IsNullOrWhiteSpace(approvedHash) || !string.Equals(approvedHash, currentHash, StringComparison.Ordinal))
        {
            return Fail(artifact, "Approved markdown hash does not match current markdown.");
        }

        List<CodexTask> tasks;
        if (artifact.Tasks.Count > 0 &&
            string.Equals(artifact.PreviewProjectedMarkdownHash, currentHash, StringComparison.Ordinal))
        {
            tasks = artifact.Tasks.ToList();
        }
        else
        {
            artifact.Status = PlanArtifactStatus.Projecting;
            await store.SaveAsync(artifact, ct).ConfigureAwait(false);

            var projectionPrompt = "请将以下已批准 Markdown 计划投影为完整 CodexTask[]。保持计划意图，不要新增范围。\n\n" + artifact.Markdown;
            tasks = await planner.GeneratePlanAsync(session, projectionPrompt, ct).ConfigureAwait(false);
        }

        CodexTaskClassifier.NormalizePlan(tasks);
        PrepareProjectedTasksForExecution(tasks);

        var errors = ValidateTasks(tasks);
        if (errors.Count > 0)
        {
            logger.LogWarning("Plan projection failed validation. PlanArtifactId={PlanArtifactId}, Errors={Errors}", artifact.Id, string.Join("; ", errors));
            artifact.Status = PlanArtifactStatus.Failed;
            artifact.Projection = BuildProjection(currentHash, tasks, errors, []);
            artifact.PreviewProjectedMarkdownHash = null;
            artifact.ReplaceTasks(null);
            await store.SaveAsync(artifact, ct).ConfigureAwait(false);
            return new PlanProjectionResult(false, artifact, tasks, errors, []);
        }

        artifact.ReplaceTasks(tasks);
        artifact.Projection = BuildProjection(currentHash, tasks, [], []);
        artifact.Status = PlanArtifactStatus.ReadyToExecute;
        await store.SaveAsync(artifact, ct).ConfigureAwait(false);
        return new PlanProjectionResult(true, artifact, tasks, [], []);
    }

    private static PlanProjectionResult Fail(PlanArtifact artifact, string error)
        => new(false, artifact, [], [error], []);

    private static void PrepareProjectedTasksForExecution(IEnumerable<CodexTask> tasks)
    {
        foreach (var task in tasks)
        {
            task.Status = CodexTaskStatus.Pending;
            task.StartedAt = null;
            task.FinishedAt = null;
            task.ResultNotes = null;
        }
    }

    private static PlanProjectionRecord BuildProjection(
        string markdownHash,
        List<CodexTask> tasks,
        IReadOnlyList<string> errors,
        IReadOnlyList<string> warnings)
    {
        var record = new PlanProjectionRecord
        {
            ProjectorVersion = "default-plan-projection-v1",
            ProjectedMarkdownHash = markdownHash,
            TaskCount = tasks.Count
        };
        record.ReplaceTaskIds(tasks.Select(task => task.Id));
        record.ReplaceValidationErrors(errors);
        record.ReplaceValidationWarnings(warnings);
        return record;
    }

    private static List<string> ValidateTasks(List<CodexTask> tasks)
    {
        var errors = new List<string>();
        if (tasks.Count is < 1 or > 10)
        {
            errors.Add("Task count must be between 1 and 10.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id) || !ids.Add(task.Id))
            {
                errors.Add($"Task id is missing or duplicated: {task.Id}");
            }

            if (!CodexTaskClassifier.IsAnalysisTask(task) && !CodexTaskClassifier.IsCodeExecutionTask(task))
            {
                errors.Add($"Task {task.Id} has invalid TaskType {task.TaskType}.");
            }

            if (CodexTaskClassifier.IsCodeExecutionTask(task) && task.ChecklistItems.Count == 0)
            {
                errors.Add($"Code task {task.Id} must include ChecklistItems.");
            }

            if (string.Equals(task.RiskLevel, "High", StringComparison.OrdinalIgnoreCase) &&
                !task.UnsafeIfDependencyFallbackPassed)
            {
                errors.Add($"High risk task {task.Id} must set UnsafeIfDependencyFallbackPassed=true.");
            }
        }

        foreach (var task in tasks)
        {
            foreach (var dependency in task.Dependencies)
            {
                if (!ids.Contains(dependency))
                {
                    errors.Add($"Task {task.Id} references missing dependency {dependency}.");
                }
            }
        }

        return errors;
    }
}

public sealed class DefaultPlanDiffService(IPlanArtifactStore store) : IPlanDiffService
{
    public async Task<PlanDiffResult> DiffAsync(string? fromPlanArtifactId, string toPlanArtifactId, CancellationToken ct = default)
    {
        var to = await store.GetAsync(toPlanArtifactId, ct).ConfigureAwait(false)
                 ?? throw new InvalidOperationException($"Plan artifact {toPlanArtifactId} was not found.");
        var from = string.IsNullOrWhiteSpace(fromPlanArtifactId)
            ? null
            : await store.GetAsync(fromPlanArtifactId, ct).ConfigureAwait(false);

        var markdownChanges = BuildLineDiff(from?.Markdown ?? string.Empty, to.Markdown);
        var taskChanges = BuildTaskDiff(from?.Tasks ?? [], to.Tasks);
        return new PlanDiffResult(from?.Id, to.Id, markdownChanges, taskChanges);
    }

    private static List<string> BuildLineDiff(string oldText, string newText)
    {
        var oldLines = oldText.ReplaceLineEndings("\n").Split('\n');
        var newLines = newText.ReplaceLineEndings("\n").Split('\n');
        var changes = new List<string>();
        foreach (var line in newLines.Except(oldLines, StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(line)) changes.Add("+ " + line);
        }

        foreach (var line in oldLines.Except(newLines, StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(line)) changes.Add("- " + line);
        }

        return changes;
    }

    private static List<string> BuildTaskDiff(IReadOnlyList<CodexTask> oldTasks, IReadOnlyList<CodexTask> newTasks)
    {
        var oldIds = oldTasks.Select(task => task.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newIds = newTasks.Select(task => task.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changes = new List<string>();
        changes.AddRange(newIds.Except(oldIds, StringComparer.OrdinalIgnoreCase).Select(id => "+ task " + id));
        changes.AddRange(oldIds.Except(newIds, StringComparer.OrdinalIgnoreCase).Select(id => "- task " + id));
        return changes;
    }
}
