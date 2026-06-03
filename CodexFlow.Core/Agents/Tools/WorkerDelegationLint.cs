using System.Text.RegularExpressions;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Agents.Tools;

internal static partial class WorkerDelegationLint
{
    private static readonly string[] GenericContinuePrompts =
    [
        "继续",
        "继续处理",
        "继续吧",
        "继续执行",
        "继续这个任务",
        "请继续",
        "go on",
        "continue",
        "please continue"
    ];

    public static IReadOnlyList<string> Analyze(
        string? workerType,
        string? prompt,
        string? description = null,
        bool isContinue = false)
    {
        var normalizedPrompt = Normalize(prompt);
        var normalizedDescription = Normalize(description);
        var combined = string.Join("\n", new[] { normalizedPrompt, normalizedDescription }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));

        if (string.IsNullOrWhiteSpace(normalizedPrompt))
        {
            return Array.Empty<string>();
        }

        var warnings = new List<string>();
        if (normalizedPrompt.Length < 16 || WordLikeTokenCount(normalizedPrompt) < 4)
        {
            warnings.Add("委派提示过短，建议明确目标、范围和期望产出，而不是只给一句口头指令。");
        }

        if (isContinue && IsGenericContinuePrompt(normalizedPrompt))
        {
            warnings.Add("continue_worker 的 prompt 过于笼统，建议补充新的用户回复、追问焦点或需要继续的具体范围。");
        }

        if (!ContainsScopeHint(combined))
        {
            warnings.Add("委派内容未体现明确范围，建议至少指出文件/目录/模块/类名，避免 worker 自行扩散。");
        }

        if (!ContainsAcceptanceHint(combined))
        {
            warnings.Add("委派内容缺少验收或输出要求，建议说明要返回的结论、证据、测试结果或交付物。");
        }

        if (ContainsDelegatedUnderstandingHint(combined))
        {
            warnings.Add("委派提示把理解责任外包给 worker。请先补充你已确认的目标/上下文，再要求 worker 在明确范围内执行。");
        }

        if (IsReadOnlyWorker(workerType) && ContainsWriteIntent(combined))
        {
            warnings.Add("当前 worker 是只读 worker，但委派内容包含修改/实现意图；若需要写代码应改用 forge，若只做研究请明确只返回结论。");
        }

        if (string.Equals(workerType, "verify", StringComparison.OrdinalIgnoreCase) &&
            !ContainsVerificationHint(combined))
        {
            warnings.Add("verify worker 任务缺少独立验证要求，建议明确需要核对的证据、测试或回归检查。");
        }

        return warnings;
    }

    public static CodexToolResult Decorate(CodexToolResult result, IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (warnings == null || warnings.Count == 0)
        {
            return result;
        }

        var display = string.IsNullOrWhiteSpace(result.Display) ? result.Output : result.Display!;
        var warningBlock = string.Join(Environment.NewLine, warnings.Select(static warning => $"- {warning}"));

        result.Summary ??= Normalize(result.Output);
        result.Display = string.IsNullOrWhiteSpace(display)
            ? $"[委派预检提醒]{Environment.NewLine}{warningBlock}"
            : $"{display}{Environment.NewLine}{Environment.NewLine}[委派预检提醒]{Environment.NewLine}{warningBlock}";
        result.SystemHint = string.Join(" | ", warnings);
        result.Metadata = MergeMetadata(result.Metadata, warnings);
        return result;
    }

    private static Dictionary<string, object?> MergeMetadata(object? existingMetadata, IReadOnlyList<string> warnings)
    {
        var metadata = existingMetadata as Dictionary<string, object?>;
        if (metadata != null)
        {
            metadata["lintWarnings"] = warnings.ToArray();
            return metadata;
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["lintWarnings"] = warnings.ToArray(),
            ["payload"] = existingMetadata
        };
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static bool IsGenericContinuePrompt(string prompt)
        => GenericContinuePrompts.Any(candidate => string.Equals(candidate, prompt, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsScopeHint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (PathLikeRegex().IsMatch(value))
        {
            return true;
        }

        return value.Contains("文件", StringComparison.OrdinalIgnoreCase)
            || value.Contains("目录", StringComparison.OrdinalIgnoreCase)
            || value.Contains("路径", StringComparison.OrdinalIgnoreCase)
            || value.Contains("模块", StringComparison.OrdinalIgnoreCase)
            || value.Contains("类", StringComparison.OrdinalIgnoreCase)
            || value.Contains("方法", StringComparison.OrdinalIgnoreCase)
            || value.Contains("service", StringComparison.OrdinalIgnoreCase)
            || value.Contains("controller", StringComparison.OrdinalIgnoreCase)
            || value.Contains("repository", StringComparison.OrdinalIgnoreCase)
            || value.Contains("handler", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAcceptanceHint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("输出", StringComparison.OrdinalIgnoreCase)
            || value.Contains("总结", StringComparison.OrdinalIgnoreCase)
            || value.Contains("报告", StringComparison.OrdinalIgnoreCase)
            || value.Contains("验收", StringComparison.OrdinalIgnoreCase)
            || value.Contains("完成标准", StringComparison.OrdinalIgnoreCase)
            || value.Contains("测试", StringComparison.OrdinalIgnoreCase)
            || value.Contains("验证", StringComparison.OrdinalIgnoreCase)
            || value.Contains("证据", StringComparison.OrdinalIgnoreCase)
            || value.Contains("结果", StringComparison.OrdinalIgnoreCase)
            || value.Contains("return", StringComparison.OrdinalIgnoreCase)
            || value.Contains("summary", StringComparison.OrdinalIgnoreCase)
            || value.Contains("evidence", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsVerificationHint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("验证", StringComparison.OrdinalIgnoreCase)
            || value.Contains("测试", StringComparison.OrdinalIgnoreCase)
            || value.Contains("证据", StringComparison.OrdinalIgnoreCase)
            || value.Contains("回归", StringComparison.OrdinalIgnoreCase)
            || value.Contains("check", StringComparison.OrdinalIgnoreCase)
            || value.Contains("verify", StringComparison.OrdinalIgnoreCase)
            || value.Contains("assert", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsDelegatedUnderstandingHint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("根据你的发现", StringComparison.OrdinalIgnoreCase)
            || value.Contains("根据你的分析", StringComparison.OrdinalIgnoreCase)
            || value.Contains("基于你的结论", StringComparison.OrdinalIgnoreCase)
            || value.Contains("先研究再修", StringComparison.OrdinalIgnoreCase)
            || value.Contains("based on your findings", StringComparison.OrdinalIgnoreCase)
            || value.Contains("based on the research", StringComparison.OrdinalIgnoreCase)
            || value.Contains("based on your analysis", StringComparison.OrdinalIgnoreCase)
            || value.Contains("after you investigate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsWriteIntent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("修改", StringComparison.OrdinalIgnoreCase)
            || value.Contains("修复", StringComparison.OrdinalIgnoreCase)
            || value.Contains("实现", StringComparison.OrdinalIgnoreCase)
            || value.Contains("编写", StringComparison.OrdinalIgnoreCase)
            || value.Contains("patch", StringComparison.OrdinalIgnoreCase)
            || value.Contains("fix", StringComparison.OrdinalIgnoreCase)
            || value.Contains("implement", StringComparison.OrdinalIgnoreCase)
            || value.Contains("write code", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReadOnlyWorker(string? workerType)
        => string.Equals(workerType, "explore", StringComparison.OrdinalIgnoreCase)
            || string.Equals(workerType, "plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(workerType, "verify", StringComparison.OrdinalIgnoreCase);

    private static int WordLikeTokenCount(string prompt)
        => WordLikeTokenRegex().Count(prompt);

    [GeneratedRegex(@"(?:(?:[A-Za-z]:)?[\\/][^\s`""']+)|(?:[\w.-]+/[\w./-]+)|(?:[\w.-]+\.(?:cs|csproj|md|json|ts|tsx|js|jsx|yml|yaml|sql|py))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathLikeRegex();

    [GeneratedRegex(@"[\p{L}\p{N}_./\\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordLikeTokenRegex();
}
