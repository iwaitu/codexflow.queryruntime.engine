namespace CodexFlow.Core.Models;

public record PolicyResult(bool Success, string Summary, IReadOnlyList<RuleResult> Details);

public record RuleResult(bool Success, string RuleName, string Message, string? Evidence = null);
