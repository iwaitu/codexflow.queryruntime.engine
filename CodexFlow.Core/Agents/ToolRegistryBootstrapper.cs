using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.LanguageServices;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.Agents;

/// <summary>
/// Ensures a fresh scoped <see cref="IToolRegistry"/> is populated with the kernel tools
/// required by orchestrator/background execution scopes.
/// </summary>
public sealed class ToolRegistryBootstrapper
{
    private readonly IGitService _gitService;
    private readonly CodexOrchestrator _orchestrator;
    private readonly ProjectScanner _scanner;
    private readonly ICodeAnalysisService _codeAnalysisService;
    private readonly IArchitectureService _architectureService;
    private readonly ICodexPlanner _planner;
    private readonly CodexSessionManager _sessionManager;
    private readonly IProjectMemoryService _projectMemoryService;
    private readonly ITaskListService? _taskListService;
    private readonly SearchFileIndexTool _searchFileIndexTool;
    private readonly RunTestsTool _runTestsTool;
    private readonly IMemoryOrchestrator? _memoryOrchestrator;
    private readonly IHashlineFileService? _hashlineService;
    private readonly HashlineOptions? _hashlineOptions;
    private readonly ILanguageServiceRegistry? _languageServiceRegistry;
    private readonly ILanguageServiceSessionFactory? _languageServiceSessionFactory;
    private readonly ILanguageServiceRefreshNotifier? _languageServiceRefreshNotifier;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ISkillScriptRunner? _skillScriptRunner;
    private readonly ICronSchedulerService _cronSchedulerService;
    private readonly IRemoteTriggerService _remoteTriggerService;
    private readonly IPushNotificationService _pushNotificationService;
    private readonly IWorkflowAuditStore _workflowAuditStore;
    private readonly IPlanArtifactStore? _planArtifactStore;
    private readonly IPlanFileService? _planFileService;
    private readonly IPlanApprovalService? _planApprovalService;
    private readonly IPlanProjectionService? _planProjectionService;
    private readonly IPlanDiffService? _planDiffService;
    private readonly IPlanBlueprintGenerator? _planBlueprintGenerator;
    private readonly IOptions<PlanningOptions>? _planningOptions;
    private readonly Func<string, Task<bool>> _retryJobFunc;
    private readonly Func<SpawnWorkerRequest, CancellationToken, Task<SpawnWorkerResult>>? _spawnWorkerFunc;
    private readonly Func<ContinueWorkerRequest, CancellationToken, Task<ContinueWorkerResult>>? _continueWorkerFunc;
    private readonly Func<string, CancellationToken, Task<StopWorkerResult>>? _stopWorkerFunc;
    private readonly Func<string, CancellationToken, Task<CleanupWorkerWorktreeResult>>? _cleanupWorkerWorktreeFunc;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<WorkerJobSummary>>>? _listWorkersFunc;
    private readonly Func<WorkerOutputRequest, CancellationToken, Task<WorkerOutputResult>>? _workerOutputFunc;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ToolRegistryBootstrapper> _logger;

    public ToolRegistryBootstrapper(
        IGitService gitService,
        CodexOrchestrator orchestrator,
        ProjectScanner scanner,
        ICodeAnalysisService codeAnalysisService,
        IArchitectureService architectureService,
        ICodexPlanner planner,
        CodexSessionManager sessionManager,
        IProjectMemoryService projectMemoryService,
        SearchFileIndexTool searchFileIndexTool,
        RunTestsTool runTestsTool,
        Func<string, Task<bool>> retryJobFunc,
        ILoggerFactory loggerFactory,
        ILogger<ToolRegistryBootstrapper> logger,
        IMemoryOrchestrator? memoryOrchestrator = null,
        IHashlineFileService? hashlineService = null,
        HashlineOptions? hashlineOptions = null,
        ILanguageServiceRegistry? languageServiceRegistry = null,
        ILanguageServiceSessionFactory? languageServiceSessionFactory = null,
        ILanguageServiceRefreshNotifier? languageServiceRefreshNotifier = null,
        IHttpClientFactory? httpClientFactory = null,
        ISkillScriptRunner? skillScriptRunner = null,
        Func<SpawnWorkerRequest, CancellationToken, Task<SpawnWorkerResult>>? spawnWorkerFunc = null,
        Func<ContinueWorkerRequest, CancellationToken, Task<ContinueWorkerResult>>? continueWorkerFunc = null,
        Func<string, CancellationToken, Task<StopWorkerResult>>? stopWorkerFunc = null,
        Func<string, CancellationToken, Task<CleanupWorkerWorktreeResult>>? cleanupWorkerWorktreeFunc = null,
        Func<string, CancellationToken, Task<IReadOnlyList<WorkerJobSummary>>>? listWorkersFunc = null,
        Func<WorkerOutputRequest, CancellationToken, Task<WorkerOutputResult>>? workerOutputFunc = null,
        ITaskListService? taskListService = null,
        ICronSchedulerService? cronSchedulerService = null,
        IRemoteTriggerService? remoteTriggerService = null,
        IPushNotificationService? pushNotificationService = null,
        IWorkflowAuditStore? workflowAuditStore = null,
        IPlanArtifactStore? planArtifactStore = null,
        IPlanFileService? planFileService = null,
        IPlanApprovalService? planApprovalService = null,
        IPlanProjectionService? planProjectionService = null,
        IPlanDiffService? planDiffService = null,
        IPlanBlueprintGenerator? planBlueprintGenerator = null,
        IOptions<PlanningOptions>? planningOptions = null)
    {
        _gitService = gitService;
        _orchestrator = orchestrator;
        _scanner = scanner;
        _codeAnalysisService = codeAnalysisService;
        _architectureService = architectureService;
        _planner = planner;
        _sessionManager = sessionManager;
        _projectMemoryService = projectMemoryService;
        _taskListService = taskListService;
        _searchFileIndexTool = searchFileIndexTool;
        _runTestsTool = runTestsTool;
        _retryJobFunc = retryJobFunc;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _memoryOrchestrator = memoryOrchestrator;
        _hashlineService = hashlineService;
        _hashlineOptions = hashlineOptions;
        _languageServiceRegistry = languageServiceRegistry;
        _languageServiceSessionFactory = languageServiceSessionFactory;
        _languageServiceRefreshNotifier = languageServiceRefreshNotifier;
        _httpClientFactory = httpClientFactory;
        _skillScriptRunner = skillScriptRunner;
        _cronSchedulerService = cronSchedulerService ?? new InMemoryCronSchedulerService();
        _remoteTriggerService = remoteTriggerService ?? new InMemoryRemoteTriggerService();
        _pushNotificationService = pushNotificationService ?? new InMemoryPushNotificationService();
        _workflowAuditStore = workflowAuditStore ?? new InMemoryWorkflowAuditStore();
        _planArtifactStore = planArtifactStore;
        _planFileService = planFileService;
        _planApprovalService = planApprovalService;
        _planProjectionService = planProjectionService;
        _planDiffService = planDiffService;
        _planBlueprintGenerator = planBlueprintGenerator;
        _planningOptions = planningOptions;
        _spawnWorkerFunc = spawnWorkerFunc;
        _continueWorkerFunc = continueWorkerFunc;
        _stopWorkerFunc = stopWorkerFunc;
        _cleanupWorkerWorktreeFunc = cleanupWorkerWorktreeFunc;
        _listWorkersFunc = listWorkersFunc;
        _workerOutputFunc = workerOutputFunc;
    }

    public void EnsureRegistered(IToolRegistry toolRegistry)
    {
        ArgumentNullException.ThrowIfNull(toolRegistry);

        // ──────────────────────────────────────────────────────────
        // Always-On Tools (核心文件操作、基础工具)
        // 这些工具每次请求都会注入到 LLM
        // ──────────────────────────────────────────────────────────

        // 元工具:tool_search 用于按需加载 deferred 工具
        toolRegistry.RegisterTool(new ToolSearchMetaTool(toolRegistry, _loggerFactory.CreateLogger<ToolSearchMetaTool>()), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new SkillTool(_loggerFactory.CreateLogger<SkillTool>(), _skillScriptRunner), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new ListMcpResourcesTool(_loggerFactory.CreateLogger<ListMcpResourcesTool>()), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new ReadMcpResourceTool(_loggerFactory.CreateLogger<ReadMcpResourceTool>()), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new EnterWorktreeTool(_sessionManager, _loggerFactory.CreateLogger<EnterWorktreeTool>()), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new ExitWorktreeTool(_sessionManager, _loggerFactory.CreateLogger<ExitWorktreeTool>()), ToolLoading.AlwaysOn);

        // 文件操作
        toolRegistry.RegisterTool(new ListWorkspaceTool(_loggerFactory.CreateLogger<ListWorkspaceTool>()), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new GlobTool(_loggerFactory.CreateLogger<GlobTool>()), ToolLoading.AlwaysOn);
        var readFileTool = new ReadFileTool(_loggerFactory.CreateLogger<ReadFileTool>(), _hashlineService, _hashlineOptions);
        toolRegistry.RegisterTool(readFileTool, ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new HashlineReadTool(readFileTool), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new WriteFileTool(_loggerFactory.CreateLogger<WriteFileTool>(), _languageServiceRefreshNotifier), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new EditFileTool(_loggerFactory.CreateLogger<EditFileTool>(), _languageServiceRefreshNotifier), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new NotebookEditTool(_loggerFactory.CreateLogger<NotebookEditTool>(), _languageServiceRefreshNotifier), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new DeleteFileTool(_loggerFactory.CreateLogger<DeleteFileTool>(), _languageServiceRefreshNotifier), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new CreateDirectoryTool(_loggerFactory.CreateLogger<CreateDirectoryTool>()), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new SearchInFilesTool(_loggerFactory.CreateLogger<SearchInFilesTool>()), ToolLoading.AlwaysOn);

        // 执行与命令
        toolRegistry.RegisterTool(new ExecCodeTool(_loggerFactory.CreateLogger<ExecCodeTool>()), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new RunCommandTool(_loggerFactory.CreateLogger<RunCommandTool>()), ToolLoading.AlwaysOn);

        // 搜索、分析与测试
        toolRegistry.RegisterTool(_searchFileIndexTool, ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(_runTestsTool, ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new AnalyzeProjectTool(
            _scanner,
            _codeAnalysisService,
            _architectureService,
            _sessionManager,
            _projectMemoryService,
            _loggerFactory.CreateLogger<AnalyzeProjectTool>(),
            _memoryOrchestrator), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new RoslynCodeAnalysisTool(_codeAnalysisService), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new StructuralLsTool(
            _codeAnalysisService,
            _loggerFactory.CreateLogger<StructuralLsTool>()), ToolLoading.AlwaysOn);
        if (_languageServiceRegistry != null && _languageServiceSessionFactory != null)
        {
            toolRegistry.RegisterTool(new LspGetDiagnosticsTool(
                _languageServiceRegistry,
                _languageServiceSessionFactory,
                _loggerFactory.CreateLogger<LspGetDiagnosticsTool>()), ToolLoading.AlwaysOn);
            toolRegistry.RegisterTool(new LspDocumentSymbolsTool(
                _languageServiceRegistry,
                _languageServiceSessionFactory), ToolLoading.AlwaysOn);
            toolRegistry.RegisterTool(new LspFindReferencesTool(
                _languageServiceRegistry,
                _languageServiceSessionFactory), ToolLoading.AlwaysOn);
            toolRegistry.RegisterTool(new LspGoToDefinitionTool(
                _languageServiceRegistry,
                _languageServiceSessionFactory), ToolLoading.AlwaysOn);
            toolRegistry.RegisterTool(new LspWorkspaceSymbolsTool(
                _languageServiceRegistry,
                _languageServiceSessionFactory), ToolLoading.AlwaysOn);
        }
        // Note: StructuralReadTool removed - ReadFileTool now serves as unified ivilson_read with hashline support

        // 规划与任务主流程
        var createSessionPlanTool = new GenerateDevPlanTool(
            _planner,
            _sessionManager,
            _orchestrator,
            _loggerFactory.CreateLogger<GenerateDevPlanTool>(),
            _taskListService,
            _planningOptions,
            _planArtifactStore,
            _planFileService,
            _planBlueprintGenerator,
            _planApprovalService);
        toolRegistry.RegisterTool(createSessionPlanTool, ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new DelegateCodexTool(
            PlanningToolNames.LegacyAlias,
            $"兼容别名：请优先使用 `{PlanningToolNames.Primary}`。{createSessionPlanTool.Description}",
            ToolCategory.Planning,
            createSessionPlanTool.AllowedStages,
            createSessionPlanTool.Metadata,
            (args, ct) => createSessionPlanTool.ExecuteAsync(args, ct)), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new TaskCreateTool(
            _sessionManager,
            _taskListService,
            _loggerFactory.CreateLogger<TaskCreateTool>()), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new TaskGetTool(_sessionManager), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new TaskListTool(_sessionManager, _taskListService), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new TaskUpdateTool(
            _sessionManager,
            _taskListService,
            _loggerFactory.CreateLogger<TaskUpdateTool>()), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new RetryTaskTool(
            _retryJobFunc,
            _loggerFactory.CreateLogger<RetryTaskTool>()), ToolLoading.AlwaysOn);
        if (_spawnWorkerFunc != null)
        {
            toolRegistry.RegisterTool(new SpawnWorkerTool(
                _spawnWorkerFunc,
                _loggerFactory.CreateLogger<SpawnWorkerTool>()), ToolLoading.AlwaysOn);
        }
        if (_continueWorkerFunc != null)
        {
            toolRegistry.RegisterTool(new ContinueWorkerTool(
                _continueWorkerFunc,
                _loggerFactory.CreateLogger<ContinueWorkerTool>()), ToolLoading.AlwaysOn);
        }
        if (_stopWorkerFunc != null)
        {
            toolRegistry.RegisterTool(new StopWorkerTool(
                _stopWorkerFunc,
                _loggerFactory.CreateLogger<StopWorkerTool>()), ToolLoading.AlwaysOn);
        }
        toolRegistry.RegisterTool(new TaskStopTool(
            _stopWorkerFunc,
            _loggerFactory.CreateLogger<TaskStopTool>()), ToolLoading.AlwaysOn);
        if (_cleanupWorkerWorktreeFunc != null)
        {
            toolRegistry.RegisterTool(new CleanupWorkerWorktreeTool(
                _cleanupWorkerWorktreeFunc,
                _loggerFactory.CreateLogger<CleanupWorkerWorktreeTool>()), ToolLoading.AlwaysOn);
        }
        if (_listWorkersFunc != null)
        {
            toolRegistry.RegisterTool(new ListWorkersTool(
                _listWorkersFunc,
                _loggerFactory.CreateLogger<ListWorkersTool>()), ToolLoading.AlwaysOn);
        }
        if (_workerOutputFunc != null)
        {
            toolRegistry.RegisterTool(new WorkerOutputTool(
                "worker_output",
                _workerOutputFunc,
                _loggerFactory.CreateLogger<WorkerOutputTool>()), ToolLoading.AlwaysOn);
            toolRegistry.RegisterTool(new WorkerOutputTool(
                "task_output",
                _workerOutputFunc,
                _loggerFactory.CreateLogger<WorkerOutputTool>()), ToolLoading.AlwaysOn);
        }
        toolRegistry.RegisterTool(new AskUserQuestionTool(), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new EnterPlanModeTool(_sessionManager), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new ExitPlanModeTool(_sessionManager), ToolLoading.AlwaysOn);
        if (_planArtifactStore != null &&
            _planFileService != null &&
            _planApprovalService != null &&
            _planProjectionService != null &&
            _planDiffService != null)
        {
            toolRegistry.RegisterTool(new WritePlanFileTool(
                _sessionManager,
                _planArtifactStore,
                _planFileService,
                _orchestrator,
                _planner,
                _loggerFactory.CreateLogger<WritePlanFileTool>()), ToolLoading.AlwaysOn);
            toolRegistry.RegisterTool(new ReadPlanFileTool(_sessionManager, _planArtifactStore, _planFileService), ToolLoading.AlwaysOn);
            toolRegistry.RegisterTool(new RequestPlanApprovalTool(_sessionManager, _planArtifactStore, _planApprovalService), ToolLoading.AlwaysOn);
            toolRegistry.RegisterTool(new ApprovePlanTool(
                _sessionManager,
                _planArtifactStore,
                _planApprovalService,
                _planProjectionService,
                _orchestrator), ToolLoading.AlwaysOn);
            toolRegistry.RegisterTool(new RejectPlanTool(
                _sessionManager,
                _planArtifactStore,
                _planApprovalService,
                _orchestrator), ToolLoading.AlwaysOn);
            toolRegistry.RegisterTool(new ProjectPlanToTasksTool(
                _sessionManager,
                _planArtifactStore,
                _planProjectionService,
                _orchestrator,
                _taskListService), ToolLoading.AlwaysOn);
            toolRegistry.RegisterTool(new PlanDiffTool(_planDiffService), ToolLoading.AlwaysOn);
        }
        toolRegistry.RegisterTool(new SyntheticOutputTool(), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new CronCreateTool(
            _cronSchedulerService,
            _loggerFactory.CreateLogger<CronCreateTool>()), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new CronDeleteTool(_cronSchedulerService), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new CronListTool(_cronSchedulerService), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new MonitorTool(
            _sessionManager,
            _listWorkersFunc,
            _workerOutputFunc), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new RemoteTriggerTool(
            _remoteTriggerService,
            _spawnWorkerFunc), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new PushNotificationTool(_pushNotificationService), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new WorkflowTool(_skillScriptRunner, _workflowAuditStore), ToolLoading.AlwaysOn);

        if (_httpClientFactory != null)
        {
            toolRegistry.RegisterTool(new FetchWebpageTool(
                _loggerFactory.CreateLogger<FetchWebpageTool>(),
                _httpClientFactory), ToolLoading.Deferred);
            toolRegistry.RegisterTool(new WebSearchTool(
                _loggerFactory.CreateLogger<WebSearchTool>(),
                _httpClientFactory), ToolLoading.Deferred);
        }

        // Archive
        toolRegistry.RegisterTool(new ZipDirectoryTool(_loggerFactory.CreateLogger<ZipDirectoryTool>()), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new DownloadArtifactTool(_loggerFactory.CreateLogger<DownloadArtifactTool>()), ToolLoading.AlwaysOn);

        // ──────────────────────────────────────────────────────────
        // Deferred Tools (按需加载)
        // 这些工具需要通过 tool_search 激活
        // ──────────────────────────────────────────────────────────

        // Git 操作类
        var applyPatchTool = new ApplyPatchTool(_gitService, _hashlineService, _hashlineOptions, _languageServiceRefreshNotifier, _loggerFactory.CreateLogger<ApplyPatchTool>());
        toolRegistry.RegisterTool(applyPatchTool, ToolLoading.Deferred);
        toolRegistry.RegisterTool(new HashlineWriteTool(applyPatchTool, _hashlineService), ToolLoading.AlwaysOn);
        toolRegistry.RegisterTool(new GitWorkspaceTool(
            "openspec_revert_changes",
            "撤销当前任务的所有未提交改动，将目标仓库恢复到最近一个快照。多仓库时需传 repo_name 或 repo_path。Few-shot: openspec_revert_changes({\"repo_name\":\"my-repo\"})。",
            [3, 4],
            _gitService,
            _loggerFactory.CreateLogger<GitWorkspaceTool>()), ToolLoading.Deferred);
        toolRegistry.RegisterTool(new GitWorkspaceTool(
            "openspec_create_checkpoint",
            "手动创建阶段性快照 (Checkpoint)。多仓库时需传 repo_name 或 repo_path。Few-shot: openspec_create_checkpoint({\"repo_name\":\"my-repo\",\"reason\":\"before refactor\"})。",
            [3],
            _gitService,
            _loggerFactory.CreateLogger<GitWorkspaceTool>()), ToolLoading.Deferred);
        toolRegistry.RegisterTool(new GitWorkspaceTool(
            "git_clone",
            "从远程 GitHub 仓库拉取代码到工作区子目录。参数:url(必填), folder(可选，默认取仓库名)。Few-shot: git_clone({\"url\":\"https://github.com/example/repo.git\",\"folder\":\"repo\"})。",
            [1],
            _gitService,
            _loggerFactory.CreateLogger<GitWorkspaceTool>()), ToolLoading.AlwaysOn);

        // 项目记忆
        toolRegistry.RegisterTool(new SaveProjectSummaryTool(
            _loggerFactory.CreateLogger<SaveProjectSummaryTool>(),
            _sessionManager,
            _projectMemoryService), ToolLoading.Deferred);
        toolRegistry.RegisterTool(new UserMemoryTool(
            _sessionManager,
            _loggerFactory.CreateLogger<UserMemoryTool>()), ToolLoading.Deferred);

        // 智能修复
        toolRegistry.RegisterTool(new SmartPatchTool(
            _gitService,
            _hashlineService,
            _hashlineOptions,
            _languageServiceRefreshNotifier,
            _loggerFactory.CreateLogger<SmartPatchTool>()), ToolLoading.Deferred);

        _logger.LogDebug("Tool registry bootstrap completed for scope. Use tool_search() to activate deferred tools.");
    }
}
