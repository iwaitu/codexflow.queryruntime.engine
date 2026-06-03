using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Constants;
using Microsoft.Extensions.AI;
using System.Linq;
using System.Text.Json;

namespace CodexFlow.Core.Agents;

/// <summary>
/// Unified factory for converting <see cref="ICodexTool"/> instances into
/// <see cref="AIFunction"/> objects with flat/typed parameter schemas.
/// <para>
/// This eliminates the <c>Dictionary&lt;string, object?&gt; input_params</c> wrapper
/// that <c>AIFunctionFactory</c> would otherwise generate from a generic delegate,
/// which causes the LLM to see <c>{ "input_params": { ... } }</c> instead of
/// <c>{ "path": "...", "start_line": 1 }</c>.
/// </para>
/// <para>
/// Each tool is mapped to a typed lambda whose signature matches the parameters
/// documented in the tool's <see cref="ICodexTool.Description"/>. The lambda
/// internally builds a <c>Dictionary&lt;string, object?&gt;</c> and delegates to
/// <see cref="ICodexTool.ExecuteAsync"/>. Server-side normalisation
/// (<see cref="ToolArgumentNormalizer"/>) is still applied so legacy container
/// formats continue to work at the execution layer.
/// </para>
/// </summary>
public static class CodexToolFunctionAdapterFactory
{
    /// <summary>
    /// Creates an <see cref="AIFunction"/> from an <see cref="ICodexTool"/>
    /// with a flat/typed parameter schema appropriate for LLM consumption.
    /// </summary>
    public static AIFunction CreateAIFunction(ICodexTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var name = tool.Name ?? "unknown";
        var description = tool.Description ?? "";

        return name.ToLowerInvariant() switch
        {
            "ivilson_read"          => CreateForIvilsonRead(tool, description),
            "hs_read"               => CreateForHsRead(tool, description),
            "ivilson_ls"            => CreateForIvilsonLs(tool, description),
            "list_workspace"        => CreateForListWorkspace(tool, description),
            "glob"                  => CreateForGlob(tool, description),
            "write_file"            => CreateForWriteFile(tool, description),
            "edit_file"             => CreateForEditFile(tool, description),
            "notebook_edit"         => CreateForNotebookEdit(tool, description),
            "delete_file"           => CreateForDeleteFile(tool, description),
            "create_directory"      => CreateForCreateDirectory(tool, description),
            "search_in_files"       => CreateForSearchInFiles(tool, description),
            "exec_code"             => CreateForExecCode(tool, description),
            "run_command" or "exec_cmd" => CreateForRunCommand(tool, description),
            "search_file_index"     => CreateForSearchFileIndex(tool, description),
            "run_tests"             => CreateForRunTests(tool, description),
            "analyze_project"       => CreateForAnalyzeProject(tool, description),
            "analyze_code"          => CreateForAnalyzeCode(tool, description),
            "lsp_get_diagnostics"   => CreateForLspGetDiagnostics(tool, description),
            "lsp_document_symbols"  => CreateForLspDocumentSymbols(tool, description),
            "lsp_workspace_symbols" => CreateForLspWorkspaceSymbols(tool, description),
            "lsp_go_to_definition"  => CreateForLspGoToDefinition(tool, description),
            "lsp_find_references"   => CreateForLspFindReferences(tool, description),
            PlanningToolNames.Primary or PlanningToolNames.LegacyAlias => CreateForGenerateDevPlan(tool, description),
            "task_create"          => CreateForTaskCreate(tool, description),
            "task_get"             => CreateForTaskGet(tool, description),
            "task_list"            => CreateForTaskList(tool, description),
            "task_update"          => CreateForTaskUpdate(tool, description),
            "write_plan_file"      => CreateForWritePlanFile(tool, description),
            "read_plan_file"       => CreateForReadPlanFile(tool, description),
            "request_plan_approval"=> CreateForPlanArtifactIdOptional(tool, "request_plan_approval", description),
            "approve_plan"         => CreateForApprovePlan(tool, description),
            "reject_plan"          => CreateForRejectPlan(tool, description),
            "project_plan_to_tasks" => CreateForPlanArtifactIdOptional(tool, "project_plan_to_tasks", description),
            "plan_diff"            => CreateForPlanDiff(tool, description),
            "execute_code_task"     => CreateForExecuteCodeTask(tool, description),
            "retry_failed_task"     => CreateForRetryFailedTask(tool, description),
            "task_stop"             => CreateForTaskStop(tool, description),
            "zip_directory"         => CreateForZipDirectory(tool, description),
            "download_artifact"     => CreateForDownloadArtifact(tool, description),
            "apply_patch"           => CreateForApplyPatch(tool, description),
            "ivilson_smart_patch"   => CreateForSmartPatch(tool, description),
            "hs_write"              => CreateForHsWrite(tool, description),
            "git_clone"             => CreateForGitClone(tool, description),
            "openspec_revert_changes"   => CreateForRevertChanges(tool, description),
            "openspec_create_checkpoint" => CreateForCreateCheckpoint(tool, description),
            "save_project_summary"  => CreateForSaveProjectSummary(tool, description),
            "user_learn_preference" => CreateForUserLearnPreference(tool, description),
            "fetch_webpage"         => CreateForFetchWebpage(tool, description),
            "web_search"            => CreateForWebSearch(tool, description),
            "skill"                 => CreateForSkill(tool, description),
            "list_mcp_resources"    => CreateForListMcpResources(tool, description),
            "read_mcp_resource"     => CreateForReadMcpResource(tool, description),
            "enter_worktree"        => CreateForEnterWorktree(tool, description),
            "exit_worktree"         => CreateForExitWorktree(tool, description),
            "cron_create"           => CreateForCronCreate(tool, description),
            "cron_delete"           => CreateForCronDelete(tool, description),
            "cron_list"             => CreateForCronList(tool, description),
            "monitor"               => CreateForMonitor(tool, description),
            "remote_trigger"        => CreateForRemoteTrigger(tool, description),
            "push_notification"     => CreateForPushNotification(tool, description),
            "workflow"              => CreateForWorkflow(tool, description),
            "tool_search"           => CreateForToolSearch(tool, description),
            _                       => CreateFallback(tool, name, description),
        };
    }

    private static Dictionary<string, object?> CreateArgs(
        string? session_id = null,
        string? workspace_path = null,
        string? project_root = null)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddTrustedRuntimeArguments(args, session_id, workspace_path, project_root);
        return args;
    }

    private static void AddTrustedRuntimeArguments(
        Dictionary<string, object?> args,
        string? session_id,
        string? workspace_path,
        string? project_root)
    {
        if (!string.IsNullOrWhiteSpace(session_id))
        {
            args["session_id"] = session_id;
        }

        if (!string.IsNullOrWhiteSpace(workspace_path))
        {
            args["workspace_path"] = workspace_path;
        }

        if (!string.IsNullOrWhiteSpace(project_root))
        {
            args["project_root"] = project_root;
        }
    }

    // ──────────────────────────────────────────────────────────
    // Per-tool typed factories
    // ──────────────────────────────────────────────────────────

    private static AIFunction CreateForIvilsonRead(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string path, string? mode = null, int? start_line = null, int? end_line = null,
                   int? window_start_line = null, int? window_line_count = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["path"] = path;
                if (mode is not null) args["mode"] = mode;
                if (start_line.HasValue) args["start_line"] = start_line.Value;
                if (end_line.HasValue) args["end_line"] = end_line.Value;
                if (window_start_line.HasValue) args["window_start_line"] = window_start_line.Value;
                if (window_line_count.HasValue) args["window_line_count"] = window_line_count.Value;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "ivilson_read", Description = description });

    private static AIFunction CreateForHsRead(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string path, int? window_start_line = null, int? window_line_count = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["path"] = path;
                if (window_start_line.HasValue) args["window_start_line"] = window_start_line.Value;
                if (window_line_count.HasValue) args["window_line_count"] = window_line_count.Value;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "hs_read", Description = description });

    private static AIFunction CreateForIvilsonLs(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? path = null, bool? recursive = null, int? max_depth = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["path"] = path ?? ".";
                args["recursive"] = recursive ?? false;
                args["max_depth"] = max_depth ?? 5;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "ivilson_ls", Description = description });

    private static AIFunction CreateForListWorkspace(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? path = null, bool? recursive = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["path"] = path ?? ".";
                args["recursive"] = recursive ?? false;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "list_workspace", Description = description });

    private static AIFunction CreateForGlob(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string pattern, string? path = null, int? max_results = null, bool? include_directories = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["pattern"] = pattern;
                args["path"] = path ?? ".";
                args["max_results"] = max_results ?? 100;
                args["include_directories"] = include_directories ?? false;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "glob", Description = description });

    private static AIFunction CreateForWriteFile(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string path, string content,
                   string? worker_id = null, string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                if (worker_id is not null) args["worker_id"] = worker_id;
                args["path"] = path;
                args["content"] = content;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "write_file", Description = description });

    private static AIFunction CreateForEditFile(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string path, string old_string, string new_string,
                   bool? replace_all = null, bool? dry_run = null,
                   string? worker_id = null, string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                if (worker_id is not null) args["worker_id"] = worker_id;
                args["path"] = path;
                args["old_string"] = old_string;
                args["new_string"] = new_string;
                args["replace_all"] = replace_all ?? false;
                args["dry_run"] = dry_run ?? false;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "edit_file", Description = description });

    private static AIFunction CreateForNotebookEdit(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string path, int cell_index, string? source = null,
                   string? operation = null, string? cell_type = null, bool? dry_run = null,
                   string? worker_id = null, string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                if (worker_id is not null) args["worker_id"] = worker_id;
                args["path"] = path;
                args["cell_index"] = cell_index;
                if (source is not null) args["source"] = source;
                if (operation is not null) args["operation"] = operation;
                if (cell_type is not null) args["cell_type"] = cell_type;
                if (dry_run.HasValue) args["dry_run"] = dry_run.Value;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "notebook_edit", Description = description });

    private static AIFunction CreateForDeleteFile(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string path,
                   string? worker_id = null, string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                if (worker_id is not null) args["worker_id"] = worker_id;
                args["path"] = path;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "delete_file", Description = description });

    private static AIFunction CreateForCreateDirectory(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string path,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["path"] = path;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "create_directory", Description = description });

    private static AIFunction CreateForSearchInFiles(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string pattern, string? path = null, bool? ignore_case = null,
                   int? max_results = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["pattern"] = pattern;
                args["path"] = path ?? ".";
                args["ignore_case"] = ignore_case ?? true;
                args["max_results"] = max_results ?? 50;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "search_in_files", Description = description });

    private static AIFunction CreateForExecCode(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string code, string? language = null, CancellationToken ct = default) =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["code"] = code,
                };
                if (language is not null) args["language"] = language;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "exec_code", Description = description });

    private static AIFunction CreateForRunCommand(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (object command, string? cwd = null,
                   bool? background = null, int? timeout_seconds = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["command"] = command;
                if (cwd is not null) args["cwd"] = cwd;
                if (background.HasValue) args["background"] = background.Value;
                if (timeout_seconds.HasValue) args["timeout_seconds"] = timeout_seconds.Value;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "exec_cmd", Description = description });

    private static AIFunction CreateForSearchFileIndex(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string query, string? session_id = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                args["query"] = query;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "search_file_index", Description = description });

    private static AIFunction CreateForRunTests(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string test_file_path, string? language = null, int? timeout_seconds = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["test_file_path"] = test_file_path;
                if (language is not null) args["language"] = language;
                if (timeout_seconds.HasValue) args["timeout_seconds"] = timeout_seconds.Value;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "run_tests", Description = description });

    private static AIFunction CreateForAnalyzeProject(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? session_id = null, string? workspace_path = null, string? project_root = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "analyze_project", Description = description });

    private static AIFunction CreateForLspGetDiagnostics(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? path = null, string? language = null, string? worker_id = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                if (path is not null) args["path"] = path;
                if (language is not null) args["language"] = language;
                if (worker_id is not null) args["worker_id"] = worker_id;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "lsp_get_diagnostics", Description = description });

    private static AIFunction CreateForLspDocumentSymbols(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string path, string? language = null, string? worker_id = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["path"] = path;
                if (language is not null) args["language"] = language;
                if (worker_id is not null) args["worker_id"] = worker_id;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "lsp_document_symbols", Description = description });

    private static AIFunction CreateForLspWorkspaceSymbols(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string query, string? language = null, string? worker_id = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["query"] = query;
                if (language is not null) args["language"] = language;
                if (worker_id is not null) args["worker_id"] = worker_id;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "lsp_workspace_symbols", Description = description });

    private static AIFunction CreateForLspGoToDefinition(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string path, string symbol, string? language = null, string? worker_id = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["path"] = path;
                args["symbol"] = symbol;
                if (language is not null) args["language"] = language;
                if (worker_id is not null) args["worker_id"] = worker_id;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "lsp_go_to_definition", Description = description });

    private static AIFunction CreateForLspFindReferences(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string path, string symbol, string? language = null, string? worker_id = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["path"] = path;
                args["symbol"] = symbol;
                if (language is not null) args["language"] = language;
                if (worker_id is not null) args["worker_id"] = worker_id;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "lsp_find_references", Description = description });

    private static AIFunction CreateForAnalyzeCode(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string code, CancellationToken ct = default) =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["code"] = code,
                };
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "analyze_code", Description = description });

    private static AIFunction CreateForGenerateDevPlan(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string requirement, string? session_id = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                args["requirement"] = requirement;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = tool.Name, Description = description });

    private static AIFunction CreateForTaskCreate(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string title, string? description_text = null, string? task_id = null,
                   string? task_type = null, string? status = null, int? stage_id = null,
                   object? dependencies = null, object? inputs = null, object? outputs = null,
                   object? checklist_items = null, string? session_id = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                args["title"] = title;
                if (description_text is not null) args["description"] = description_text;
                if (task_id is not null) args["task_id"] = task_id;
                if (task_type is not null) args["task_type"] = task_type;
                if (status is not null) args["status"] = status;
                if (stage_id.HasValue) args["stage_id"] = stage_id.Value;
                if (dependencies is not null) args["dependencies"] = dependencies;
                if (inputs is not null) args["inputs"] = inputs;
                if (outputs is not null) args["outputs"] = outputs;
                if (checklist_items is not null) args["checklist_items"] = checklist_items;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "task_create", Description = description });

    private static AIFunction CreateForTaskGet(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string task_id, string? session_id = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                args["task_id"] = task_id;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "task_get", Description = description });

    private static AIFunction CreateForTaskList(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? session_id = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "task_list", Description = description });

    private static AIFunction CreateForTaskUpdate(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string task_id, string? title = null, string? description_text = null,
                   string? task_type = null, string? status = null, int? stage_id = null,
                   object? dependencies = null, object? inputs = null, object? outputs = null,
                   object? checklist_items = null, string? result_notes = null,
                   string? error_message = null, string? session_id = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                args["task_id"] = task_id;
                if (title is not null) args["title"] = title;
                if (description_text is not null) args["description"] = description_text;
                if (task_type is not null) args["task_type"] = task_type;
                if (status is not null) args["status"] = status;
                if (stage_id.HasValue) args["stage_id"] = stage_id.Value;
                if (dependencies is not null) args["dependencies"] = dependencies;
                if (inputs is not null) args["inputs"] = inputs;
                if (outputs is not null) args["outputs"] = outputs;
                if (checklist_items is not null) args["checklist_items"] = checklist_items;
                if (result_notes is not null) args["result_notes"] = result_notes;
                if (error_message is not null) args["error_message"] = error_message;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "task_update", Description = description });

    private static AIFunction CreateForWritePlanFile(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string markdown, string? session_id = null, string? plan_artifact_id = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                args["markdown"] = markdown;
                if (plan_artifact_id is not null) args["plan_artifact_id"] = plan_artifact_id;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "write_plan_file", Description = description });

    private static AIFunction CreateForReadPlanFile(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? session_id = null, string? plan_artifact_id = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                if (plan_artifact_id is not null) args["plan_artifact_id"] = plan_artifact_id;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "read_plan_file", Description = description });

    private static AIFunction CreateForPlanArtifactIdOptional(ICodexTool tool, string name, string description) =>
        AIFunctionFactory.Create(
            async (string? session_id = null, string? plan_artifact_id = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                if (plan_artifact_id is not null) args["plan_artifact_id"] = plan_artifact_id;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = name, Description = description });

    private static AIFunction CreateForApprovePlan(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? session_id = null, string? plan_artifact_id = null, string? user_id = null, string? feedback = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                if (plan_artifact_id is not null) args["plan_artifact_id"] = plan_artifact_id;
                if (user_id is not null) args["user_id"] = user_id;
                if (feedback is not null) args["feedback"] = feedback;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "approve_plan", Description = description });

    private static AIFunction CreateForRejectPlan(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? session_id = null, string? plan_artifact_id = null, string? user_id = null, string? feedback = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                if (plan_artifact_id is not null) args["plan_artifact_id"] = plan_artifact_id;
                if (user_id is not null) args["user_id"] = user_id;
                if (feedback is not null) args["feedback"] = feedback;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "reject_plan", Description = description });

    private static AIFunction CreateForPlanDiff(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string to_plan_artifact_id, string? from_plan_artifact_id = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs();
                args["to_plan_artifact_id"] = to_plan_artifact_id;
                if (from_plan_artifact_id is not null) args["from_plan_artifact_id"] = from_plan_artifact_id;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "plan_diff", Description = description });

    private static AIFunction CreateForExecuteCodeTask(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string task_id, string? session_id = null, string? workspace_path = null, string? project_root = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["task_id"] = task_id;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "execute_code_task", Description = description });

    private static AIFunction CreateForRetryFailedTask(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string job_id, CancellationToken ct = default) =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["job_id"] = job_id,
                };
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "retry_failed_task", Description = description });

    private static AIFunction CreateForTaskStop(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string job_id, CancellationToken ct = default) =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["job_id"] = job_id,
                };
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "task_stop", Description = description });

    private static AIFunction CreateForZipDirectory(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string zip_path, string? source_dir = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["zip_path"] = zip_path;
                if (source_dir is not null) args["source_dir"] = source_dir;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "zip_directory", Description = description });

    private static AIFunction CreateForDownloadArtifact(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string filename, bool? as_text = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["filename"] = filename;
                if (as_text.HasValue) args["as_text"] = as_text.Value;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "download_artifact", Description = description });

    private static AIFunction CreateForApplyPatch(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? patch = null, string? patch_content = null, string? edit_mode = null,
                   object? request = null,
                   string? worker_id = null, string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                if (worker_id is not null) args["worker_id"] = worker_id;
                if (patch is not null) args["patch"] = patch;
                if (patch_content is not null && !args.ContainsKey("patch")) args["patch"] = patch_content;
                if (edit_mode is not null) args["edit_mode"] = edit_mode;
                var normalizedRequest = CreateHashlineRequestFromInput(request);
                if (normalizedRequest is not null) args["request"] = normalizedRequest;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "apply_patch", Description = description });

    private static AIFunction CreateForSmartPatch(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? patch_content = null, string? patch = null, string? reason = null,
                   string? edit_mode = null, object? request = null,
                   string? worker_id = null, string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                if (worker_id is not null) args["worker_id"] = worker_id;
                if (patch_content is not null) args["patch_content"] = patch_content;
                if (patch is not null && !args.ContainsKey("patch_content")) args["patch_content"] = patch;
                if (reason is not null) args["reason"] = reason;
                if (edit_mode is not null) args["edit_mode"] = edit_mode;
                var normalizedRequest = CreateHashlineRequestFromInput(request);
                if (normalizedRequest is not null) args["request"] = normalizedRequest;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "ivilson_smart_patch", Description = description });

    private static AIFunction CreateForHsWrite(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string filePath, string? snapshotId = null, string? fileFingerprint = null, object? operations = null,
                   string? oldString = null, string? newString = null, bool? replaceAll = null, bool? dryRun = null,
                   string? worker_id = null, string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                if (worker_id is not null) args["worker_id"] = worker_id;
                args["filePath"] = filePath;
                if (snapshotId is not null) args["snapshotId"] = snapshotId;
                if (fileFingerprint is not null) args["fileFingerprint"] = fileFingerprint;
                if (operations is not null) args["operations"] = operations;
                if (oldString is not null) args["oldString"] = oldString;
                if (newString is not null) args["newString"] = newString;
                if (replaceAll.HasValue) args["replaceAll"] = replaceAll.Value;
                if (dryRun.HasValue) args["dryRun"] = dryRun.Value;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "hs_write", Description = description });

    private static Dictionary<string, object?>? CreateHashlineRequestFromInput(object? request)
    {
        if (request is null)
        {
            return null;
        }

        var dict = TryConvertToDictionary(request);
        if (dict == null || dict.Count == 0)
        {
            return null;
        }

        var payload = new Dictionary<string, object?>();
        if (TryGetNonEmptyString(dict, "filePath", out var filePath)) payload["filePath"] = filePath;
        if (TryGetNonEmptyString(dict, "snapshotId", out var snapshotId)) payload["snapshotId"] = snapshotId;
        if (TryGetNonEmptyString(dict, "fileFingerprint", out var fileFingerprint)) payload["fileFingerprint"] = fileFingerprint;
        if (TryGetBoolean(dict, "dryRun", out var dryRun)) payload["dryRun"] = dryRun;

        if (dict.TryGetValue("operations", out var operationsObj))
        {
            var operations = EnumerateObjects(operationsObj)
                .Select(TryConvertToDictionary)
                .Where(static o => o is { Count: > 0 })
                .Cast<object?>()
                .ToList();

            if (operations.Count > 0)
            {
                payload["operations"] = operations;
            }
        }

        return payload.Count == 0 ? null : payload;
    }

    private static Dictionary<string, object?>? TryConvertToDictionary(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case Dictionary<string, object?> dict:
                return new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase);
            case IDictionary<string, object> dict:
                return dict.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.OrdinalIgnoreCase);
            case JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Object:
                return JsonElementToDictionary(jsonElement);
            case string text when TryParseRequestString(text, out var parsed):
                return parsed;
            default:
                return null;
        }
    }

    private static bool TryGetNonEmptyString(Dictionary<string, object?> dict, string key, out string value)
    {
        value = string.Empty;
        if (!dict.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        value = raw.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetBoolean(Dictionary<string, object?> dict, string key, out bool value)
    {
        value = false;
        if (!dict.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case bool boolean:
                value = boolean;
                return true;
            case JsonElement jsonElement when jsonElement.ValueKind is JsonValueKind.True or JsonValueKind.False:
                value = jsonElement.GetBoolean();
                return true;
            default:
                return bool.TryParse(raw.ToString(), out value);
        }
    }

    private static IEnumerable<object?> EnumerateObjects(object? value)
    {
        if (value is null)
        {
            yield break;
        }

        switch (value)
        {
            case string:
                yield return value;
                yield break;
            case Dictionary<string, object?>:
            case IDictionary<string, object>:
                yield return value;
                yield break;
            case JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Array:
                foreach (var item in jsonElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var nested in EnumerateObjects(item))
                        {
                            yield return nested;
                        }
                    }
                    else
                    {
                        yield return JsonElementToObject(item);
                    }
                }
                yield break;
            case System.Collections.IEnumerable enumerable:
                foreach (var item in enumerable)
                {
                    if (item is string)
                    {
                        yield return item;
                    }
                    else
                    {
                        foreach (var nested in EnumerateObjects(item))
                        {
                            yield return nested;
                        }
                    }
                }
                yield break;
            default:
                yield return value;
                yield break;
        }
    }

    private static Dictionary<string, object?> JsonElementToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            dict[property.Name] = JsonElementToObject(property.Value);
        }

        return dict;
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt32(out var intValue)
                ? intValue
                : element.TryGetInt64(out var longValue)
                    ? longValue
                    : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => JsonElementToDictionary(element),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            _ => element.ToString()
        };
    }

    private static bool TryParseRequestString(string text, out Dictionary<string, object?>? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }

            if (trimmed.EndsWith("```", StringComparison.Ordinal))
            {
                trimmed = trimmed[..^3].Trim();
            }
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            parsed = JsonElementToDictionary(document.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static AIFunction CreateForGitClone(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? url = null, string? folder = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                if (url is not null) args["url"] = url;
                if (folder is not null) args["folder"] = folder;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "git_clone", Description = description });

    private static AIFunction CreateForRevertChanges(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? repo_name = null, string? repo_path = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                if (repo_name is not null) args["repo_name"] = repo_name;
                if (repo_path is not null) args["repo_path"] = repo_path;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "openspec_revert_changes", Description = description });

    private static AIFunction CreateForCreateCheckpoint(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? reason = null, string? repo_name = null, string? repo_path = null,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                if (reason is not null) args["reason"] = reason;
                if (repo_name is not null) args["repo_name"] = repo_name;
                if (repo_path is not null) args["repo_path"] = repo_path;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "openspec_create_checkpoint", Description = description });

    private static AIFunction CreateForSaveProjectSummary(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string summary,
                   string? session_id = null, string? workspace_path = null, string? project_root = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path, project_root);
                args["summary"] = summary;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "save_project_summary", Description = description });

    private static AIFunction CreateForUserLearnPreference(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string key, string value, string? session_id = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                args["key"] = key;
                args["value"] = value;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "user_learn_preference", Description = description });

    private static AIFunction CreateForToolSearch(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string query, CancellationToken ct = default) =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["query"] = query,
                };
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "tool_search", Description = description });

    private static AIFunction CreateForFetchWebpage(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string url, CancellationToken ct = default) =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["url"] = url
                };
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "fetch_webpage", Description = description });

    private static AIFunction CreateForWebSearch(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string query, string? search_type = null, CancellationToken ct = default) =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["query"] = query
                };
                if (search_type is not null)
                {
                    args["search_type"] = search_type;
                }
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "web_search", Description = description });

    private static AIFunction CreateForSkill(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? action = null, string? name = null, string? script_path = null, object? args = null,
                   CancellationToken ct = default) =>
            {
                var toolArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (action is not null)
                {
                    toolArgs["action"] = action;
                }
                if (name is not null)
                {
                    toolArgs["name"] = name;
                }
                if (script_path is not null)
                {
                    toolArgs["script_path"] = script_path;
                }
                if (args is not null)
                {
                    toolArgs["args"] = args;
                }
                return await tool.ExecuteAsync(toolArgs, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "skill", Description = description });

    private static AIFunction CreateForListMcpResources(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? server = null, string? pattern = null, int? max_results = null,
                   CancellationToken ct = default) =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (server is not null)
                {
                    args["server"] = server;
                }
                if (pattern is not null)
                {
                    args["pattern"] = pattern;
                }
                if (max_results.HasValue)
                {
                    args["max_results"] = max_results.Value;
                }
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "list_mcp_resources", Description = description });

    private static AIFunction CreateForReadMcpResource(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string uri, int? max_chars = null, CancellationToken ct = default) =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["uri"] = uri
                };
                if (max_chars.HasValue)
                {
                    args["max_chars"] = max_chars.Value;
                }
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "read_mcp_resource", Description = description });

    private static AIFunction CreateForEnterWorktree(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string path, bool? allow_external = null, string? session_id = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                args["path"] = path;
                if (allow_external.HasValue)
                {
                    args["allow_external"] = allow_external.Value;
                }
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "enter_worktree", Description = description });

    private static AIFunction CreateForExitWorktree(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? session_id = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "exit_worktree", Description = description });

    private static AIFunction CreateForCronCreate(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string session_id, string cron, string prompt, string? worker_type = null,
                   string? name = null, string? task_id = null, string? workspace_path = null,
                   string? timezone = null, int? max_rounds = null, bool? enabled = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path);
                args["cron"] = cron;
                args["prompt"] = prompt;
                if (worker_type is not null) args["worker_type"] = worker_type;
                if (name is not null) args["name"] = name;
                if (task_id is not null) args["task_id"] = task_id;
                if (timezone is not null) args["timezone"] = timezone;
                if (max_rounds.HasValue) args["max_rounds"] = max_rounds.Value;
                if (enabled.HasValue) args["enabled"] = enabled.Value;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "cron_create", Description = description });

    private static AIFunction CreateForCronDelete(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string cron_id, CancellationToken ct = default) =>
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["cron_id"] = cron_id
                };
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "cron_delete", Description = description });

    private static AIFunction CreateForCronList(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? session_id = null, CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "cron_list", Description = description });

    private static AIFunction CreateForMonitor(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? session_id = null, string? job_id = null, string? worker_id = null,
                   bool? include_events = null, int? max_events = null, long? after_seq = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                if (job_id is not null) args["job_id"] = job_id;
                if (worker_id is not null) args["worker_id"] = worker_id;
                if (include_events.HasValue) args["include_events"] = include_events.Value;
                if (max_events.HasValue) args["max_events"] = max_events.Value;
                if (after_seq.HasValue) args["after_seq"] = after_seq.Value;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "monitor", Description = description });

    private static AIFunction CreateForRemoteTrigger(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? source = null, string? event_type = null, object? payload = null,
                   string? session_id = null, string? user_id = null, string? workspace_path = null,
                   bool? dispatch_worker = null, string? worker_type = null, string? prompt = null,
                   string? task_id = null, int? max_rounds = null, string? action = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id, workspace_path);
                if (source is not null) args["source"] = source;
                if (event_type is not null) args["event_type"] = event_type;
                if (payload is not null) args["payload"] = payload;
                if (user_id is not null) args["user_id"] = user_id;
                if (dispatch_worker.HasValue) args["dispatch_worker"] = dispatch_worker.Value;
                if (worker_type is not null) args["worker_type"] = worker_type;
                if (prompt is not null) args["prompt"] = prompt;
                if (task_id is not null) args["task_id"] = task_id;
                if (max_rounds.HasValue) args["max_rounds"] = max_rounds.Value;
                if (action is not null) args["action"] = action;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "remote_trigger", Description = description });

    private static AIFunction CreateForPushNotification(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string user_id, string message, string? title = null, string? session_id = null,
                   string? task_id = null, string? job_id = null, string? priority = null,
                   object? channels = null, string? markdown_report = null, object? metadata = null,
                   CancellationToken ct = default) =>
            {
                var args = CreateArgs(session_id);
                args["user_id"] = user_id;
                args["message"] = message;
                if (title is not null) args["title"] = title;
                if (task_id is not null) args["task_id"] = task_id;
                if (job_id is not null) args["job_id"] = job_id;
                if (priority is not null) args["priority"] = priority;
                if (channels is not null) args["channels"] = channels;
                if (markdown_report is not null) args["markdown_report"] = markdown_report;
                if (metadata is not null) args["metadata"] = metadata;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "push_notification", Description = description });

    private static AIFunction CreateForWorkflow(ICodexTool tool, string description) =>
        AIFunctionFactory.Create(
            async (string? action = null, string? name = null, string? skill_name = null,
                   string? script_path = null, object? args = null, string? workflow_id = null,
                   string? session_id = null, string? user_id = null, string? workspace_path = null,
                   CancellationToken ct = default) =>
            {
                var toolArgs = CreateArgs(session_id, workspace_path);
                if (action is not null) toolArgs["action"] = action;
                if (name is not null) toolArgs["name"] = name;
                if (skill_name is not null) toolArgs["skill_name"] = skill_name;
                if (script_path is not null) toolArgs["script_path"] = script_path;
                if (args is not null) toolArgs["args"] = args;
                if (workflow_id is not null) toolArgs["workflow_id"] = workflow_id;
                if (user_id is not null) toolArgs["user_id"] = user_id;
                return await tool.ExecuteAsync(toolArgs, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = "workflow", Description = description });

    /// <summary>
    /// Fallback for tools not yet mapped to a typed schema.
    /// Uses <c>Dictionary&lt;string, object?&gt;</c> as a single parameter
    /// but names it descriptively and applies normalisation.
    /// </summary>
    private static AIFunction CreateFallback(ICodexTool tool, string name, string description) =>
        AIFunctionFactory.Create(
            async (Dictionary<string, object?> parameters, CancellationToken ct) =>
            {
                var args = ToolArgumentNormalizer.NormalizeCopy(parameters);
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            },
            new AIFunctionFactoryOptions { Name = name, Description = description });
}
