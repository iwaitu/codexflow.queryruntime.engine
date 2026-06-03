namespace CodexFlow.Core.Abstractions;

public interface ISkillScriptRunner
{
    Task<object?> RunScriptAsync(string scriptName, object? context = null);
    Task<string> RunAsync(string skillName, string scriptRelativePath, IReadOnlyList<string>? args, CancellationToken cancellationToken);
}
