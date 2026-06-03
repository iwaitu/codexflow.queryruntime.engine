using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Planning.Committee;

/// <summary>
/// 投影后的任务 DTO（LLM 输出 schema）。
/// </summary>
public class ProjectedTask
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("task_type")]
    public string TaskType { get; set; } = "code";

    [JsonProperty("dependencies")]
    public List<string> Dependencies { get; set; } = [];

    [JsonProperty("risk_level")]
    public string RiskLevel { get; set; } = "Low";

    [JsonProperty("complexity_level")]
    public int ComplexityLevel { get; set; } = 2;

    [JsonProperty("verification_steps")]
    public List<string> VerificationSteps { get; set; } = [];
}

/// <summary>
/// 投影校验结果。
/// </summary>
public class ProjectionValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// 蓝图投影服务。
/// 将 final_plan.md 转换为 List&lt;CodexTask&gt;，并执行 schema 校验。
/// </summary>
public class PlanProjectionService
{
    private readonly ILogger _logger;

    // 验证步骤不可执行的模式
    private static readonly string[] NonExecutablePatterns =
    [
        "手动验证", "手动测试", "人工检查", "目测", "手动确认",
        "manual test", "manual check", "visually inspect",
        "verify manually", "check manually"
    ];

    public PlanProjectionService(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 使用 LLM 将蓝图投影为结构化任务列表。
    /// </summary>
    public async Task<List<ProjectedTask>?> ProjectAsync(
        string finalPlan,
        IChatClient chatClient,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(finalPlan);
        ArgumentNullException.ThrowIfNull(chatClient);

        var prompt = CommitteePrompts.Fill(CommitteePrompts.PlanProjectionPrompt,
            new Dictionary<string, string>
            {
                ["final_plan"] = finalPlan.Length > 8000 ? finalPlan[..8000] + "\n\n... (已截断)" : finalPlan
            });

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "你是一个任务分解器。将实施蓝图转换为 JSON 任务数组。只输出 JSON 数组。"),
            new(ChatRole.User, prompt)
        };

        var options = new ChatOptions
        {
            Temperature = 0.1f,
            ResponseFormat = ChatResponseFormat.Json
        };

        var helper = new BufferedNonStreamingLlmHelper(chatClient, _logger);
        var rawText = await helper.GetJsonTextAsync(messages, options, "committee_plan_projection", ct).ConfigureAwait(false);

        try
        {
            // 尝试直接解析为数组
            var tasks = JsonConvert.DeserializeObject<List<ProjectedTask>>(rawText);
            if (tasks is { Count: > 0 })
            {
                return tasks;
            }
        }
        catch (JsonException)
        {
            // 尝试从包装对象中提取数组
            try
            {
                var obj = JObject.Parse(rawText);
                var arrayProp = obj.Properties().FirstOrDefault(p => p.Value is JArray);
                if (arrayProp != null)
                {
                    var tasks = arrayProp.Value.ToObject<List<ProjectedTask>>();
                    if (tasks is { Count: > 0 }) return tasks;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "投影结果 JSON 解析完全失败");
            }
        }

        return null;
    }

    /// <summary>
    /// 对投影结果执行 schema 校验。
    /// </summary>
    public ProjectionValidationResult Validate(List<ProjectedTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var result = new ProjectionValidationResult { IsValid = true };

        if (tasks.Count == 0)
        {
            result.IsValid = false;
            result.Errors.Add("投影结果为空");
            return result;
        }

        var allIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks)
        {
            var prefix = $"任务 [{task.Id}]";

            // 必填字段检查
            if (string.IsNullOrWhiteSpace(task.Id))
            {
                result.IsValid = false;
                result.Errors.Add($"{prefix}: 缺少 id");
            }
            else if (!allIds.Add(task.Id))
            {
                result.IsValid = false;
                result.Errors.Add($"{prefix}: id 重复");
            }

            if (string.IsNullOrWhiteSpace(task.Title))
            {
                result.IsValid = false;
                result.Errors.Add($"{prefix}: 缺少 title");
            }

            if (string.IsNullOrWhiteSpace(task.Description))
            {
                result.IsValid = false;
                result.Errors.Add($"{prefix}: 缺少 description");
            }

            // TaskType 校验
            if (!string.Equals(task.TaskType, "code", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(task.TaskType, "analysis", StringComparison.OrdinalIgnoreCase))
            {
                result.Warnings.Add($"{prefix}: task_type '{task.TaskType}' 非标准值，将被规范化");
            }

            // RiskLevel 校验
            if (!IsValidRiskLevel(task.RiskLevel))
            {
                result.Warnings.Add($"{prefix}: risk_level '{task.RiskLevel}' 非标准值");
            }

            // ComplexityLevel 校验
            if (task.ComplexityLevel < 1 || task.ComplexityLevel > 3)
            {
                result.Warnings.Add($"{prefix}: complexity_level {task.ComplexityLevel} 超出 [1,3] 范围");
            }

            // 依赖引用校验
            foreach (var dep in task.Dependencies)
            {
                if (!tasks.Any(t => string.Equals(t.Id, dep, StringComparison.OrdinalIgnoreCase)))
                {
                    result.IsValid = false;
                    result.Errors.Add($"{prefix}: 依赖 '{dep}' 引用无效");
                }
            }

            // 验证步骤校验
            ValidateVerificationSteps(task, prefix, result);
        }

        return result;
    }

    /// <summary>
    /// 将投影结果转换为 CodexTask 列表。
    /// </summary>
    public static List<CodexTask> ToCodexTasks(List<ProjectedTask> projectedTasks)
    {
        ArgumentNullException.ThrowIfNull(projectedTasks);

        var tasks = new List<CodexTask>();

        foreach (var pt in projectedTasks)
        {
            var task = new CodexTask
            {
                Id = pt.Id,
                Title = pt.Title,
                Description = pt.Description,
                TaskType = CodexTaskClassifier.NormalizeTaskType(pt.TaskType, pt.Title, pt.Description),
                Status = CodexTaskStatus.Pending,
                RiskLevel = NormalizeRiskLevel(pt.RiskLevel),
                ComplexityLevel = Math.Clamp(pt.ComplexityLevel, 1, 3)
            };

            task.ReplaceDependencies(pt.Dependencies);

            // 将验证步骤编入 Description 尾部，供 Stage 4 使用
            if (pt.VerificationSteps.Count > 0)
            {
                task.Description += "\n\n### 验证步骤\n" +
                    string.Join("\n", pt.VerificationSteps.Select((s, i) => $"{i + 1}. {s}"));
            }

            tasks.Add(task);
        }

        CodexTaskClassifier.NormalizePlan(tasks);
        return tasks;
    }

    #region Shadow Diff

    /// <summary>
    /// 生成 shadow-plan-diff 数据。
    /// </summary>
    public static object GenerateShadowDiff(
        List<CodexTask> baselinePlan, List<CodexTask> committeePlan)
    {
        ArgumentNullException.ThrowIfNull(baselinePlan);
        ArgumentNullException.ThrowIfNull(committeePlan);

        var baselineIds = baselinePlan.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var committeeIds = committeePlan.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sharedIds = baselineIds.Intersect(committeeIds).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        return new
        {
            baseline_plan_source = "DefaultCodexPlanner",
            committee_plan_source = "CommitteePlanning",
            baseline_task_count = baselinePlan.Count,
            committee_task_count = committeePlan.Count,
            task_diffs = new
            {
                only_in_baseline = baselineIds.Except(committeeIds).ToList(),
                only_in_committee = committeeIds.Except(baselineIds).ToList(),
                in_both = sharedIds
            },
            dependency_diffs = new
            {
                changed_tasks = sharedIds
                    .Where(id => HaveDifferentDependencies(
                        FindTaskById(baselinePlan, id),
                        FindTaskById(committeePlan, id)))
                    .Select(id => new
                    {
                        task_id = id,
                        baseline = FindTaskById(baselinePlan, id)!.Dependencies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                        committee = FindTaskById(committeePlan, id)!.Dependencies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
                    })
                    .ToList()
            },
            validation_diffs = new
            {
                changed_tasks = sharedIds
                    .Where(id => HaveDifferentValidationSteps(
                        FindTaskById(baselinePlan, id),
                        FindTaskById(committeePlan, id)))
                    .Select(id => new
                    {
                        task_id = id,
                        baseline = ExtractValidationSteps(FindTaskById(baselinePlan, id)!.Description),
                        committee = ExtractValidationSteps(FindTaskById(committeePlan, id)!.Description)
                    })
                    .ToList()
            },
            risk_diffs = new
            {
                baseline_high_risk = baselinePlan.Count(t => string.Equals(t.RiskLevel, "High", StringComparison.OrdinalIgnoreCase)),
                committee_high_risk = committeePlan.Count(t => string.Equals(t.RiskLevel, "High", StringComparison.OrdinalIgnoreCase))
            },
            summary = $"基线 {baselinePlan.Count} 任务 vs 委员会 {committeePlan.Count} 任务",
            generated_at = DateTime.UtcNow
        };
    }

    #endregion

    #region Validation Helpers

    private static void ValidateVerificationSteps(ProjectedTask task, string prefix, ProjectionValidationResult result)
    {
        if (task.VerificationSteps.Count == 0)
        {
            result.IsValid = false;
            result.Errors.Add($"{prefix}: 缺少验证步骤");
            return;
        }

        foreach (var step in task.VerificationSteps)
        {
            if (string.IsNullOrWhiteSpace(step))
            {
                result.Warnings.Add($"{prefix}: 存在空验证步骤");
                continue;
            }

            // 不可执行验证步骤检测
            if (NonExecutablePatterns.Any(p => step.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                result.IsValid = false;
                result.Errors.Add($"{prefix}: 验证步骤不可执行 — '{step}'");
            }
        }
    }

    private static bool IsValidRiskLevel(string riskLevel)
    {
        return string.Equals(riskLevel, "Low", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(riskLevel, "Medium", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(riskLevel, "High", StringComparison.OrdinalIgnoreCase);
    }

    private static CodexTask? FindTaskById(IEnumerable<CodexTask> tasks, string id)
        => tasks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    private static bool HaveDifferentDependencies(CodexTask? baseline, CodexTask? committee)
    {
        if (baseline == null || committee == null)
        {
            return false;
        }

        var baselineDeps = baseline.Dependencies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var committeeDeps = committee.Dependencies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        return !baselineDeps.SequenceEqual(committeeDeps, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HaveDifferentValidationSteps(CodexTask? baseline, CodexTask? committee)
    {
        if (baseline == null || committee == null)
        {
            return false;
        }

        var baselineSteps = ExtractValidationSteps(baseline.Description);
        var committeeSteps = ExtractValidationSteps(committee.Description);
        return !baselineSteps.SequenceEqual(committeeSteps, StringComparer.Ordinal);
    }

    private static List<string> ExtractValidationSteps(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return [];
        }

        const string marker = "### 验证步骤";
        var index = description.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return [];
        }

        var section = description[(index + marker.Length)..]
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();

        var steps = new List<string>();
        foreach (var line in section.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                continue;
            }

            var dotIndex = trimmed.IndexOf(". ", StringComparison.Ordinal);
            if (dotIndex > 0 && int.TryParse(trimmed[..dotIndex], out _))
            {
                steps.Add(trimmed[(dotIndex + 2)..].Trim());
            }
            else
            {
                steps.Add(trimmed);
            }
        }

        return steps;
    }

    private static string NormalizeRiskLevel(string riskLevel)
    {
        if (string.Equals(riskLevel, "High", StringComparison.OrdinalIgnoreCase)) return "High";
        if (string.Equals(riskLevel, "Medium", StringComparison.OrdinalIgnoreCase)) return "Medium";
        return "Low";
    }

    #endregion
}
