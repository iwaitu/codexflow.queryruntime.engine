using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;

namespace CodexFlow.Core.Services;

/// <summary>
/// 預設的策略驗證器，組合多個規則進行掃描。
/// </summary>
public class DefaultPolicyValidator : IPolicyValidator
{
    private readonly IEnumerable<IPolicyRule> _rules;
    private readonly ILogger<DefaultPolicyValidator> _logger;

    public DefaultPolicyValidator(IEnumerable<IPolicyRule> rules, ILogger<DefaultPolicyValidator> logger)
    {
        _rules = rules;
        _logger = logger;
    }

    public async Task<PolicyResult> ValidateAsync(CodexSession session, CodexTask task, string shadowPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(shadowPath);

        StructuredLog.Information(_logger, "Starting Policy Validation for task {TaskId} in {ShadowPath}", task.Id, shadowPath);

        var results = new List<RuleResult>();
        foreach (var rule in _rules)
        {
            try
            {
                var res = await rule.EvaluateAsync(session, shadowPath, ct).ConfigureAwait(false);
                results.Add(res);
            }
            catch (IOException ex)
            {
                StructuredLog.Error(_logger, ex, "Error evaluating rule {RuleName}", rule.Name);
                results.Add(new RuleResult(false, rule.Name, $"Internal error: {ex.Message}"));
            }
            catch (UnauthorizedAccessException ex)
            {
                StructuredLog.Error(_logger, ex, "Error evaluating rule {RuleName}", rule.Name);
                results.Add(new RuleResult(false, rule.Name, $"Internal error: {ex.Message}"));
            }
            catch (InvalidOperationException ex)
            {
                StructuredLog.Error(_logger, ex, "Error evaluating rule {RuleName}", rule.Name);
                results.Add(new RuleResult(false, rule.Name, $"Internal error: {ex.Message}"));
            }
            catch (JsonException ex)
            {
                StructuredLog.Error(_logger, ex, "Error evaluating rule {RuleName}", rule.Name);
                results.Add(new RuleResult(false, rule.Name, $"Internal error: {ex.Message}"));
            }
        }

        var allSuccess = results.All(r => r.Success);
        var summary = allSuccess
            ? "所有架構守衛規則通過。"
            : $"檢測到 {results.Count(r => !r.Success)} 項架構違規。";

        return new PolicyResult(allSuccess, summary, results);
    }
}

