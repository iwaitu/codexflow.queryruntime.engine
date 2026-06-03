namespace CodexFlow.Core.Agents;

/// <summary>
/// Shared classifier for tool-name based heuristics used by both the manual
/// DefaultCodexKernel loop and the runtime-driven QueryRuntimeEngine so that
/// WriteToolCalls bookkeeping stays consistent across both paths.
/// </summary>
public static class ToolClassification
{
    public static bool IsWriteTool(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName))
        {
            return false;
        }

        return toolName.Contains("write_file", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("apply_patch", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("ivilson_write", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("hs_write", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("ivilson_smart_patch", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("smart_patch", StringComparison.OrdinalIgnoreCase); // Legacy fallback for historical logs
    }
}
