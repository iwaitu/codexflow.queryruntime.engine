using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Models;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Runtime;

namespace CodexFlow.Core.Workers;

public enum WorkerIsolationMode
{
    Workspace = 0,
    ShadowWorktree = 1
}

public enum WorkerOutputContract
{
    TaskNotificationEnvelope = 0,
    VerificationReportEnvelope = 1
}

public enum WorkerLanguageServiceAccess
{
    None = 0,
    ReadOnly = 1,
    ReadWriteRefresh = 2,
    DiagnosticsEvidenceRead = 3
}

public sealed record WorkerDefinition
{
    public required WorkerType WorkerType { get; init; }
    public required string DisplayName { get; init; }
    public required Func<CodexSession?, string> SystemPromptBuilder { get; init; }
    public required IReadOnlySet<ToolCategory> AllowedToolCategories { get; init; }
    public required IReadOnlySet<string> AllowedToolNames { get; init; }
    public IReadOnlySet<string> AutoActivateToolNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public RequiredToolExecutionContract? RequiredToolContract { get; init; }
    public required WorkerIsolationMode DefaultIsolationMode { get; init; }
    public required WorkerOutputContract OutputContract { get; init; }
    public required WorkerLanguageServiceAccess LanguageServiceAccess { get; init; }

    public string BuildSystemPrompt(CodexSession? session = null)
        => SystemPromptBuilder(session);
}

public sealed record WorkerRuntimeContext
{
    public required WorkerType WorkerType { get; init; }
    public required string DisplayName { get; init; }
    public required WorkerIsolationMode IsolationMode { get; init; }
    public required WorkerOutputContract OutputContract { get; init; }
    public required WorkerLanguageServiceAccess LanguageServiceAccess { get; init; }
    public required IReadOnlyList<string> AllowedToolNames { get; init; }
    public required IReadOnlyList<ToolCategory> AllowedToolCategories { get; init; }
    public IReadOnlyList<string> AutoActivateToolNames { get; init; } = [];
    public RequiredToolExecutionContract? RequiredToolContract { get; init; }
}

public interface IWorkerDefinitionRegistry
{
    WorkerDefinition GetRequired(WorkerType workerType);
    bool TryGet(WorkerType workerType, out WorkerDefinition? definition);
    IReadOnlyList<WorkerDefinition> GetAll();
    IReadOnlyList<ICodexTool> FilterAvailableTools(WorkerType workerType, IEnumerable<ICodexTool> availableTools);
    WorkerRuntimeContext BuildRuntimeContext(WorkerType workerType);
}

public sealed class DefaultWorkerDefinitionRegistry : IWorkerDefinitionRegistry
{
    private static readonly string[] ReadOnlyWorkspaceTools =
    [
        "tool_search",
        "fetch_webpage",
        "glob",
        "web_search",
        "search_file_index",
        "search_in_files",
        "ivilson_read",
        "ivilson_ls",
        "list_workspace",
        "analyze_project",
        "analyze_code",
        "lsp_get_diagnostics",
        "lsp_document_symbols",
        "lsp_find_references",
        "lsp_go_to_definition",
        "lsp_workspace_symbols"
    ];

    private static readonly string[] ForgeWorkspaceTools =
    [
        "search_file_index",
        "search_in_files",
        "glob",
        "ivilson_read",
        "ivilson_ls",
        "list_workspace",
        "analyze_code",
        "lsp_get_diagnostics",
        "lsp_document_symbols",
        "lsp_find_references",
        "lsp_go_to_definition",
        "lsp_workspace_symbols",
        "hs_read",
        "hs_write",
        "write_file",
        "edit_file",
        "notebook_edit",
        "delete_file",
        "create_directory",
        "apply_patch",
        "ivilson_smart_patch",
        "run_tests",
        "exec_cmd",
        "exec_code",
        "openspec_revert_changes",
        "openspec_create_checkpoint"
    ];

    private static readonly string[] VerifyWorkspaceTools =
    [
        .. ReadOnlyWorkspaceTools,
        "run_tests",
        "exec_cmd"
    ];

    private static readonly string[] ReadEvidenceContractTools =
    [
        "search_file_index",
        "search_in_files",
        "glob",
        "ivilson_read",
        "ivilson_ls",
        "list_workspace",
        "analyze_project",
        "analyze_code",
        "lsp_get_diagnostics",
        "lsp_document_symbols",
        "lsp_find_references",
        "lsp_go_to_definition",
        "lsp_workspace_symbols",
        "fetch_webpage",
        "web_search"
    ];

    private static readonly string[] ForgeWriteContractTools =
    [
        "hs_write",
        "ivilson_smart_patch",
        "apply_patch",
        "edit_file",
        "write_file",
        "notebook_edit",
        "delete_file",
        "create_directory"
    ];

    private static readonly string[] VerifyEvidenceContractTools =
    [
        "exec_cmd",
        "run_tests",
        "lsp_get_diagnostics"
    ];

    private static readonly string[] WebDeferredTools =
    [
        "fetch_webpage",
        "web_search"
    ];

    private static readonly string[] ForgeDeferredTools =
    [
        "apply_patch",
        "ivilson_smart_patch",
        "openspec_create_checkpoint"
    ];

    private readonly IReadOnlyDictionary<WorkerType, WorkerDefinition> _definitions;

    public DefaultWorkerDefinitionRegistry(HashlineOptions? hashlineOptions = null)
    {
        _definitions = new Dictionary<WorkerType, WorkerDefinition>
        {
            [WorkerType.Explore] = new()
            {
                WorkerType = WorkerType.Explore,
                DisplayName = "Ivilson-Explore",
                SystemPromptBuilder = session => CodexPrompts.GetExploreWorkerPrompt(session),
                AllowedToolCategories = new HashSet<ToolCategory>([ToolCategory.Read, ToolCategory.Analysis, ToolCategory.System]),
                AllowedToolNames = new HashSet<string>(ReadOnlyWorkspaceTools, StringComparer.OrdinalIgnoreCase),
                AutoActivateToolNames = new HashSet<string>(WebDeferredTools, StringComparer.OrdinalIgnoreCase),
                RequiredToolContract = CreateReadEvidenceContract("explore_worker_read_evidence"),
                DefaultIsolationMode = WorkerIsolationMode.Workspace,
                OutputContract = WorkerOutputContract.TaskNotificationEnvelope,
                LanguageServiceAccess = WorkerLanguageServiceAccess.ReadOnly
            },
            [WorkerType.Plan] = new()
            {
                WorkerType = WorkerType.Plan,
                DisplayName = "Ivilson-Plan",
                SystemPromptBuilder = session => CodexPrompts.GetPlanWorkerPrompt(session),
                AllowedToolCategories = new HashSet<ToolCategory>([ToolCategory.Read, ToolCategory.Analysis, ToolCategory.Planning, ToolCategory.System]),
                AllowedToolNames = new HashSet<string>(ReadOnlyWorkspaceTools, StringComparer.OrdinalIgnoreCase),
                AutoActivateToolNames = new HashSet<string>(WebDeferredTools, StringComparer.OrdinalIgnoreCase),
                RequiredToolContract = CreateReadEvidenceContract("plan_worker_read_evidence"),
                DefaultIsolationMode = WorkerIsolationMode.Workspace,
                OutputContract = WorkerOutputContract.TaskNotificationEnvelope,
                LanguageServiceAccess = WorkerLanguageServiceAccess.ReadOnly
            },
            [WorkerType.Forge] = new()
            {
                WorkerType = WorkerType.Forge,
                DisplayName = "Ivilson-Forge",
                SystemPromptBuilder = session => CodexPrompts.GetForgePrompt(session, hashlineOptions),
                AllowedToolCategories = new HashSet<ToolCategory>([ToolCategory.Read, ToolCategory.Analysis, ToolCategory.Forge, ToolCategory.System]),
                AllowedToolNames = new HashSet<string>(ForgeWorkspaceTools, StringComparer.OrdinalIgnoreCase),
                AutoActivateToolNames = new HashSet<string>(ForgeDeferredTools, StringComparer.OrdinalIgnoreCase),
                RequiredToolContract = new RequiredToolExecutionContract
                {
                    Name = "forge_worker_write_evidence",
                    AnyOfToolNames = ForgeWriteContractTools,
                    PreferredRecoveryToolName = "hs_write",
                    MaxContinuationAttempts = 2,
                    Feedback = "Forge worker cannot stop before successfully applying a workspace change. Call a write tool now; do not summarize first."
                },
                DefaultIsolationMode = WorkerIsolationMode.ShadowWorktree,
                OutputContract = WorkerOutputContract.TaskNotificationEnvelope,
                LanguageServiceAccess = WorkerLanguageServiceAccess.ReadWriteRefresh
            },
            [WorkerType.Verify] = new()
            {
                WorkerType = WorkerType.Verify,
                DisplayName = "Ivilson-Verify",
                SystemPromptBuilder = session => CodexPrompts.GetVerifyWorkerPrompt(session),
                AllowedToolCategories = new HashSet<ToolCategory>([ToolCategory.Read, ToolCategory.Analysis, ToolCategory.Sentry, ToolCategory.System, ToolCategory.Forge]),
                AllowedToolNames = new HashSet<string>(VerifyWorkspaceTools, StringComparer.OrdinalIgnoreCase),
                AutoActivateToolNames = new HashSet<string>(WebDeferredTools, StringComparer.OrdinalIgnoreCase),
                RequiredToolContract = new RequiredToolExecutionContract
                {
                    Name = "verify_worker_evidence",
                    AnyOfToolNames = VerifyEvidenceContractTools,
                    PreferredRecoveryToolName = "exec_cmd",
                    MaxContinuationAttempts = 2,
                    Feedback = "Verify worker cannot stop before collecting verification evidence. Call `exec_cmd`, `run_tests`, or `lsp_get_diagnostics` now; do not summarize first."
                },
                DefaultIsolationMode = WorkerIsolationMode.Workspace,
                OutputContract = WorkerOutputContract.VerificationReportEnvelope,
                LanguageServiceAccess = WorkerLanguageServiceAccess.DiagnosticsEvidenceRead
            }
        };
    }

    public WorkerDefinition GetRequired(WorkerType workerType)
        => _definitions.TryGetValue(workerType, out var definition)
            ? definition
            : throw new KeyNotFoundException($"No worker definition registered for '{workerType}'.");

    public bool TryGet(WorkerType workerType, out WorkerDefinition? definition)
        => _definitions.TryGetValue(workerType, out definition);

    public IReadOnlyList<WorkerDefinition> GetAll()
        => _definitions.Values.OrderBy(definition => definition.WorkerType).ToArray();

    public IReadOnlyList<ICodexTool> FilterAvailableTools(WorkerType workerType, IEnumerable<ICodexTool> availableTools)
    {
        ArgumentNullException.ThrowIfNull(availableTools);

        var definition = GetRequired(workerType);
        return availableTools
            .Where(tool => definition.AllowedToolNames.Contains(tool.Name)
                && definition.AllowedToolCategories.Contains(tool.Category))
            .ToArray();
    }

    public WorkerRuntimeContext BuildRuntimeContext(WorkerType workerType)
    {
        var definition = GetRequired(workerType);
        return new WorkerRuntimeContext
        {
            WorkerType = definition.WorkerType,
            DisplayName = definition.DisplayName,
            IsolationMode = definition.DefaultIsolationMode,
            OutputContract = definition.OutputContract,
            LanguageServiceAccess = definition.LanguageServiceAccess,
            AllowedToolNames = definition.AllowedToolNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
            AllowedToolCategories = definition.AllowedToolCategories.OrderBy(static category => category).ToArray(),
            AutoActivateToolNames = definition.AutoActivateToolNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
            RequiredToolContract = definition.RequiredToolContract
        };
    }

    private static RequiredToolExecutionContract CreateReadEvidenceContract(string name)
        => new()
        {
            Name = name,
            AnyOfToolNames = ReadEvidenceContractTools,
            PreferredRecoveryToolName = "search_file_index",
            MaxContinuationAttempts = 2,
            Feedback = "Worker cannot stop before collecting workspace evidence. Call a read, search, analysis, or diagnostics tool now; do not summarize first."
        };
}

public static class WorkerExecutionSessionFactory
{
    public static CodexSession CreateForWorker(CodexSession session, WorkerType workerType)
    {
        ArgumentNullException.ThrowIfNull(session);

        var effectiveStage = ResolveStage(workerType);
        if (session.CurrentStage == effectiveStage)
        {
            return session;
        }

        var workerSession = new CodexSession
        {
            Id = session.Id,
            UserId = session.UserId,
            ProjectUrl = session.ProjectUrl,
            ProjectSummary = session.ProjectSummary,
            WorkspacePath = session.WorkspacePath,
            CurrentStage = effectiveStage,
            ActiveTaskId = session.ActiveTaskId,
            PlanVersion = session.PlanVersion,
            PlanGeneratedAtUtc = session.PlanGeneratedAtUtc,
            HistorySummary = session.HistorySummary,
            LastCompressedAt = session.LastCompressedAt,
            ExecutionSummary = session.ExecutionSummary
        };

        workerSession.ReplacePlan(session.Plan);
        workerSession.ReplaceMetadata(session.Metadata);
        workerSession.ReplaceRecentTurns(session.RecentTurns);
        workerSession.ReplaceActiveFacts(session.ActiveFacts);
        workerSession.ReplaceUserFacts(session.UserFacts);

        return workerSession;
    }

    public static int ResolveStage(WorkerType workerType)
        => workerType switch
        {
            WorkerType.Forge => 3,
            WorkerType.Verify => 3,
            _ => 1
        };
}
