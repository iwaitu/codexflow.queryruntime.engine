namespace CodexFlow.Core.Skills;

public interface ISkill
{
    string Name { get; }
    string Description { get; }
    Task<SkillResult> ExecuteAsync(SkillContext context);
}

public class SkillResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public object? Data { get; set; }
    public string Error { get; set; } = string.Empty;
}

public class SkillContext
{
    public Dictionary<string, object> Parameters { get; } = new();
    public string WorkspacePath { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;

    public void ReplaceParameters(IEnumerable<KeyValuePair<string, object>>? parameters)
    {
        Parameters.Clear();
        if (parameters == null)
        {
            return;
        }

        foreach (var parameter in parameters)
        {
            Parameters[parameter.Key] = parameter.Value;
        }
    }
}
