namespace CodexFlow.Core.Constants;

public static class PlanningToolNames
{
    public const string Primary = "create_session_plan";
    public const string LegacyAlias = "generate_dev_plan";

    public static bool IsPlanCreationTool(string? toolName)
        => string.Equals(toolName, Primary, StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolName, LegacyAlias, StringComparison.OrdinalIgnoreCase);
}
