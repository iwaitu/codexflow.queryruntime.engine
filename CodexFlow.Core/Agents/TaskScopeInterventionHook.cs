using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents;

/// <summary>
/// Pre-execution scope guard: blocks write/edit tool calls targeting files
/// outside the current task's file scope before they reach the filesystem.
/// </summary>
public sealed class TaskScopeInterventionHook : IQueryRuntimeInterventionHook
{
    private readonly TaskFileScopeDescriptor _scope;
    private readonly ILogger _logger;
    private int _blockCount;

    private static readonly HashSet<string> ScopeCheckedToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file",
        "edit_file",
        "delete_file",
        "ivilson_smart_patch",
        "apply_patch",
        "hs_write",
        "ApplyPatchTool",
        "create_directory"
    };

    private static readonly string[] PathArgumentKeys = ["path", "file_path", "filePath", "target_file", "target_path"];

    public TaskScopeInterventionHook(TaskFileScopeDescriptor scope, ILogger logger)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Number of tool calls blocked by this hook since construction.</summary>
    public int BlockCount => _blockCount;

    public ValueTask<QueryRuntimeIntervention> OnToolCallRequestedAsync(
        string toolName,
        IDictionary<string, object?> arguments,
        object? session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        // 1. Only check scope for write/edit tools; pass read-only tools through.
        if (!ScopeCheckedToolNames.Contains(toolName))
        {
            return ValueTask.FromResult(QueryRuntimeIntervention.None);
        }

        // 2. If scope has no constraints, allow everything.
        if (!_scope.HasConstraints)
        {
            return ValueTask.FromResult(QueryRuntimeIntervention.None);
        }

        // 3. Extract the target file path from tool arguments.
        var targetPath = ExtractPathFromArguments(arguments);
        if (string.IsNullOrEmpty(targetPath))
        {
            _logger.LogDebug("Scope check skipped for tool {ToolName}: could not extract target path from arguments", toolName);
            return ValueTask.FromResult(QueryRuntimeIntervention.None);
        }

        // 4. Normalize and check scope.
        var normalized = TaskFileScopeGuard.NormalizePathLike(targetPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return ValueTask.FromResult(QueryRuntimeIntervention.None);
        }

        // write_file and create_directory can create new files;
        // other write tools target existing files.
        var isNewFile = toolName is "write_file" or "create_directory";

        if (TaskFileScopeGuard.IsPathInScope(normalized, _scope, isNewFile))
        {
            return ValueTask.FromResult(QueryRuntimeIntervention.None);
        }

        // 5. Block the call with a clear reason message.
        _blockCount++;
        var reason = BuildBlockReason(normalized, toolName);

        _logger.LogWarning(
            "Scope guard blocked tool {ToolName} targeting out-of-scope file {Path}. Block count: {BlockCount}",
            toolName, normalized, _blockCount);

        var feedbackMessage = new ChatMessage(
            ChatRole.User,
            $"Task scope violation: file `{normalized}` is not in the allowed scope for this task.\n\n" +
            $"Allowed files: {string.Join(", ", _scope.AllowedFiles.Take(12))}" +
            (_scope.AllowedFiles.Count > 12 ? " ..." : "") + "\n" +
            (_scope.AllowedDirectories.Count > 0
                ? $"Allowed directories for new files: {string.Join(", ", _scope.AllowedDirectories.Take(8))}\n"
                : "") +
            (_scope.DisallowedDirectories.Count > 0
                ? $"Disallowed directories: {string.Join(", ", _scope.DisallowedDirectories.Take(8))}\n"
                : "") +
            "\nPlease stay within the defined scope. If you genuinely need to modify a file outside scope, " +
            "explain the reason in your response and stop attempting modifications beyond the allowed files.");

        return ValueTask.FromResult(QueryRuntimeIntervention.BlockWithMessage(feedbackMessage, reason));
    }

    public ValueTask<QueryRuntimeIntervention> OnToolExecutionCompletedAsync(
        string toolName,
        string result,
        bool success,
        object? session,
        CancellationToken ct = default)
    {
        // Scope guard only acts pre-execution.
        return ValueTask.FromResult(QueryRuntimeIntervention.None);
    }

    private static string? ExtractPathFromArguments(IDictionary<string, object?> arguments)
    {
        foreach (var key in PathArgumentKeys)
        {
            if (arguments.TryGetValue(key, out var value) && value is string pathStr && !string.IsNullOrWhiteSpace(pathStr))
            {
                return pathStr.TrimStart('/', '\\');
            }
        }

        // Special handling for ivilson_smart_patch / apply_patch which nest path inside a "request" object.
        if (arguments.TryGetValue("request", out var requestObj))
        {
            if (requestObj is IDictionary<string, object?> requestDict)
            {
                foreach (var key in PathArgumentKeys)
                {
                    if (requestDict.TryGetValue(key, out var nestedValue) && nestedValue is string nestedPath && !string.IsNullOrWhiteSpace(nestedPath))
                    {
                        return nestedPath.TrimStart('/', '\\');
                    }
                }
            }

            // request might be a JSON string; try to extract path from it.
            if (requestObj is string requestJson && !string.IsNullOrWhiteSpace(requestJson))
            {
                try
                {
                    var jsonObj = Newtonsoft.Json.Linq.JObject.Parse(requestJson);
                    foreach (var key in PathArgumentKeys)
                    {
                        var token = jsonObj[key];
                        if (token != null && token.Type == Newtonsoft.Json.Linq.JTokenType.String)
                        {
                            var extracted = token.ToString();
                            if (!string.IsNullOrWhiteSpace(extracted))
                            {
                                return extracted.TrimStart('/', '\\');
                            }
                        }
                    }
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    // Not valid JSON; skip
                }
            }
        }

        return null;
    }

    private string BuildBlockReason(string normalizedPath, string toolName)
    {
        return $"Task scope violation: tool `{toolName}` targeting `{normalizedPath}` is outside allowed scope. " +
               $"Allowed: [{string.Join(", ", _scope.AllowedFiles.Take(6))}]. Block #{_blockCount}.";
    }
}