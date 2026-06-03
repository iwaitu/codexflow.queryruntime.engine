using System.Text;
using System.Text.RegularExpressions;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Services;

public class ProjectMemoryService(
    CodexSessionManager sessionManager,
    ILogger<ProjectMemoryService> logger,
    IMemoryOrchestrator? memoryOrchestrator = null) : IProjectMemoryService
{
    private const string SummaryFileName = "PROJECT_SUMMARY.md";
    private const string Title = "# 项目记忆摘要";

    private static readonly string[] SectionOrder =
    [
        "项目概况",
        "技术栈与架构约束",
        "已确认工程决策",
        "当前已知风险与技术债",
        "当前验证基线",
        "最近一次任务执行结果",
        "历史分析快照"
    ];

    private static readonly Regex TopLevelSectionRegex = new(
        @"^## (?<title>" + string.Join("|", SectionOrder.Select(Regex.Escape)) + @")\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public async Task<ProjectMemoryDocument> LoadAsync(
        string workspacePath,
        string? projectRoot = null,
        Uri? projectUrl = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        var location = ResolveLocation(workspacePath, projectRoot, projectUrl, metadata);
        if (!File.Exists(location.FilePath))
        {
            return new ProjectMemoryDocument();
        }

        var content = await File.ReadAllTextAsync(location.FilePath, ct).ConfigureAwait(false);
        return Parse(content);
    }

    public async Task<ProjectMemoryWriteResult> SaveAnalysisAsync(ProjectAnalysisMemoryInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var location = ResolveLocation(input.WorkspacePath, input.ProjectRoot, input.ProjectUrl, input.Metadata);
        var document = await LoadDocumentAsync(location, ct).ConfigureAwait(false);
        document.UpdatedAtUtc = DateTime.UtcNow;
        document.ProjectOverview = BuildOverview(input.DetectedLanguage, input.ReadmeSummary);
        document.TechStackAndConstraints = BuildTechStackAndConstraints(input.DetectedLanguage, input.ProjectFingerprint);
        document.KnownRisksAndDebt = BuildRisksSection(input.RiskItems);
        document.LegacyAnalysisSnapshot = input.RawAnalysisReport.Trim();

        var result = await PersistAsync(location, input.SessionId, document, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(input.SessionId))
        {
            var versionMeta = new MemoryEntryMetadata(Scope: MemoryFactScope.Session, Source: "analyze_project", Confidence: MemoryFactConfidence.High).ToJson();
            // Phase 4/6: ProjectSummaryVersion is a protected key — route through MemoryOrchestrator when available.
            if (memoryOrchestrator != null)
            {
                var session = await sessionManager.GetOrCreateSessionAsync(input.SessionId).ConfigureAwait(false);
                await memoryOrchestrator.WriteFactAsync(
                    session,
                    ProjectMemoryFactKeys.ProjectSummaryVersion,
                    FormattableString.Invariant($"{result.Document.Version}"),
                    MemoryFactCategories.Project,
                    versionMeta,
                    ct).ConfigureAwait(false);
            }
            else
            {
                await sessionManager.LearnFactAsync(
                    input.SessionId,
                    ProjectMemoryFactKeys.ProjectSummaryVersion,
                    FormattableString.Invariant($"{result.Document.Version}"),
                    MemoryFactCategories.Project,
                    versionMeta).ConfigureAwait(false);
            }
        }

        StructuredLog.Information(logger, "Project memory analysis snapshot saved to {Path}", result.FilePath);
        return result;
    }

    public async Task<ProjectMemoryWriteResult> SaveManualSummaryAsync(ProjectManualSummaryInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var location = ResolveLocation(input.WorkspacePath, input.ProjectRoot, input.ProjectUrl, input.Metadata);
        var document = await LoadDocumentAsync(location, ct).ConfigureAwait(false);
        document.UpdatedAtUtc = DateTime.UtcNow;

        var normalizedSummary = input.Summary.Trim();
        if (string.IsNullOrWhiteSpace(document.ProjectOverview))
        {
            document.ProjectOverview = normalizedSummary;
        }

        document.ConfirmedDecisions = AppendBulletEntry(
            document.ConfirmedDecisions,
            FormattableString.Invariant($"[{DateTime.UtcNow:yyyy-MM-dd}] {normalizedSummary}"));

        var result = await PersistAsync(location, input.SessionId, document, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(input.SessionId))
        {
            // DecisionLog is not a protected key — direct write is fine.
            await sessionManager.LearnFactAsync(
                input.SessionId,
                "DecisionLog",
                document.ConfirmedDecisions,
                MemoryFactCategories.Decision,
                new MemoryEntryMetadata(Scope: MemoryFactScope.Workspace, Source: "manual_summary", Confidence: MemoryFactConfidence.High).ToJson()).ConfigureAwait(false);

            // Phase 5/6: Trigger auto-refresh after manual summary save.
            if (memoryOrchestrator != null)
            {
                var session = await sessionManager.GetOrCreateSessionAsync(input.SessionId).ConfigureAwait(false);
                await memoryOrchestrator.RunAutoRefreshAsync(session, AutoRefreshTrigger.ManualSummarySaved, ct).ConfigureAwait(false);
            }
        }

        StructuredLog.Information(logger, "Project memory manual summary saved to {Path}", result.FilePath);
        return result;
    }

    public async Task<ProjectMemoryWriteResult> SaveExecutionResultAsync(ProjectExecutionMemoryInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var location = ResolveLocation(input.WorkspacePath, input.ProjectRoot, input.ProjectUrl, input.Metadata);
        var document = await LoadDocumentAsync(location, ct).ConfigureAwait(false);
        document.UpdatedAtUtc = DateTime.UtcNow;
        document.LatestExecutionResult = BuildExecutionResultSection(input.Plan);

        var verificationSummary = string.IsNullOrWhiteSpace(input.VerificationSummary)
            ? FormattableString.Invariant($"最近一次完整任务收尾时间: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC")
            : input.VerificationSummary.Trim();
        document.VerificationBaseline = verificationSummary;

        var result = await PersistAsync(location, input.SessionId, document, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(input.SessionId))
        {
            // BUG-002 fix: Downgrade memory confidence when any task passed via fallback
            var hasFallbackTask = input.Plan?.Any(t => t.Status == CodexTaskStatus.CompletedWithWarnings) == true;
            var memoryConfidence = hasFallbackTask
                ? MemoryFactConfidence.Medium
                : MemoryFactConfidence.High;
            var executionMeta = new MemoryEntryMetadata(
                Scope: MemoryFactScope.Session,
                Source: hasFallbackTask ? "execution_closeout_with_fallback" : "execution_closeout",
                Confidence: memoryConfidence).ToJson();

            // LastExecutionOutcome is not protected — direct write is fine.
            await sessionManager.LearnFactAsync(
                input.SessionId,
                ProjectMemoryFactKeys.LastExecutionOutcome,
                document.LatestExecutionResult,
                MemoryFactCategories.Execution,
                executionMeta).ConfigureAwait(false);

            // Phase 4/6: VerificationBaseline is a protected key — route through MemoryOrchestrator when available.
            if (memoryOrchestrator != null)
            {
                var session = await sessionManager.GetOrCreateSessionAsync(input.SessionId).ConfigureAwait(false);
                await memoryOrchestrator.WriteFactAsync(
                    session,
                    ProjectMemoryFactKeys.VerificationBaseline,
                    document.VerificationBaseline,
                    MemoryFactCategories.Verification,
                    executionMeta,
                    ct).ConfigureAwait(false);

                // Phase 5/6: Trigger auto-refresh after execution close-out.
                await memoryOrchestrator.RunAutoRefreshAsync(session, AutoRefreshTrigger.PlanExecutionClosed, ct).ConfigureAwait(false);
            }
            else
            {
                await sessionManager.LearnFactAsync(
                    input.SessionId,
                    ProjectMemoryFactKeys.VerificationBaseline,
                    document.VerificationBaseline,
                    MemoryFactCategories.Verification,
                    executionMeta).ConfigureAwait(false);
            }
        }

        StructuredLog.Information(logger, "Project memory execution snapshot saved to {Path}", result.FilePath);
        return result;
    }

    private static async Task<ProjectMemoryDocument> LoadDocumentAsync(ProjectMemoryLocation location, CancellationToken ct)
    {
        if (!File.Exists(location.FilePath))
        {
            return new ProjectMemoryDocument();
        }

        var content = await File.ReadAllTextAsync(location.FilePath, ct).ConfigureAwait(false);
        return Parse(content);
    }

    private async Task<ProjectMemoryWriteResult> PersistAsync(
        ProjectMemoryLocation location,
        string? sessionId,
        ProjectMemoryDocument document,
        CancellationToken ct)
    {
        var existingDocument = await LoadDocumentAsync(location, ct).ConfigureAwait(false);
        PreserveMissingSections(existingDocument, document);
        document.Version = Math.Max(document.Version, 2);
        document.UpdatedAtUtc = DateTime.UtcNow;

        var content = Render(document);
        ValidateRenderedDocument(content);
        Directory.CreateDirectory(location.RootPath);
        await File.WriteAllTextAsync(location.FilePath, content, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var session = await sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
            session.ProjectSummary = content;
            await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
        }

        return new ProjectMemoryWriteResult(location.FilePath, content, document);
    }

    private static void PreserveMissingSections(ProjectMemoryDocument existing, ProjectMemoryDocument current)
    {
        current.ProjectOverview = PreserveIfBlank(current.ProjectOverview, existing.ProjectOverview);
        current.TechStackAndConstraints = PreserveIfBlank(current.TechStackAndConstraints, existing.TechStackAndConstraints);
        current.ConfirmedDecisions = PreserveIfBlank(current.ConfirmedDecisions, existing.ConfirmedDecisions);
        current.KnownRisksAndDebt = PreserveIfBlank(current.KnownRisksAndDebt, existing.KnownRisksAndDebt);
        current.VerificationBaseline = PreserveIfBlank(current.VerificationBaseline, existing.VerificationBaseline);
        current.LatestExecutionResult = PreserveIfBlank(current.LatestExecutionResult, existing.LatestExecutionResult);
        current.LegacyAnalysisSnapshot = PreserveIfBlank(current.LegacyAnalysisSnapshot, existing.LegacyAnalysisSnapshot);
    }

    private static string PreserveIfBlank(string current, string existing)
    {
        return string.IsNullOrWhiteSpace(current) ? existing : current;
    }

    private static void ValidateRenderedDocument(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || !content.StartsWith(Title, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PROJECT_SUMMARY.md content is malformed.");
        }

        foreach (var section in SectionOrder)
        {
            if (!content.Contains("## " + section, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"PROJECT_SUMMARY.md is missing managed section: {section}");
            }
        }
    }

    private static string BuildOverview(string detectedLanguage, string? readmeSummary)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FormattableString.Invariant($"项目已完成基础分析，当前主语言识别为 `{detectedLanguage}`。"));
        sb.AppendLine("后续规划与执行应以当前仓库结构和已确认架构约束为准。");

        if (!string.IsNullOrWhiteSpace(readmeSummary))
        {
            sb.AppendLine();
            sb.AppendLine("README 摘要：");
            sb.AppendLine(readmeSummary.Trim());
        }

        return sb.ToString().Trim();
    }

    private static string BuildTechStackAndConstraints(string detectedLanguage, string projectFingerprint)
    {
        var firstLines = projectFingerprint
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Take(6);

        var sb = new StringBuilder();
        sb.AppendLine(FormattableString.Invariant($"- 主语言: `{detectedLanguage}`"));
        sb.AppendLine("- 已建立项目指纹、文件索引与架构审计事实，可直接用于规划和执行。");
        sb.AppendLine("- 执行阶段应优先遵守现有目录边界、依赖方向和测试基线。");
        sb.AppendLine();
        sb.AppendLine("项目指纹摘录：");
        foreach (var line in firstLines)
        {
            sb.AppendLine(line);
        }

        return sb.ToString().Trim();
    }

    private static string BuildRisksSection(IReadOnlyList<string> riskItems)
    {
        if (riskItems.Count == 0)
        {
            return "当前分析未识别出高优先级架构风险。";
        }

        var sb = new StringBuilder();
        foreach (var risk in riskItems)
        {
            sb.AppendLine("- " + risk.Trim());
        }

        return sb.ToString().Trim();
    }

    private static string BuildExecutionResultSection(IReadOnlyList<CodexTask> plan)
    {
        if (plan.Count == 0)
        {
            return "当前没有可记录的任务执行结果。";
        }

        var sb = new StringBuilder();
        sb.AppendLine(CodexPlanSummaryFormatter.BuildExecutionSummary(plan));
        sb.AppendLine(FormattableString.Invariant($"更新时间: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC"));
        sb.AppendLine();

        foreach (var task in plan)
        {
            sb.AppendLine(FormattableString.Invariant($"- [{GetTaskStatusLabel(task.Status)}] {task.Id}: {task.Title}"));
        }

        return sb.ToString().Trim();
    }

    private static string GetTaskStatusLabel(CodexTaskStatus status)
    {
        return status switch
        {
            CodexTaskStatus.Success => "成功",
            CodexTaskStatus.CompletedWithWarnings => "成功(警告)",
            CodexTaskStatus.Skipped => "跳过",
            CodexTaskStatus.Failed => "失败",
            CodexTaskStatus.Executing => "执行中",
            CodexTaskStatus.Planning => "规划中",
            _ => "待处理"
        };
    }

    private static string AppendBulletEntry(string existing, string entry)
    {
        var normalized = existing?.Trim() ?? string.Empty;
        var bullet = entry.StartsWith("- ", StringComparison.Ordinal) ? entry : "- " + entry;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return bullet;
        }

        return normalized + Environment.NewLine + bullet;
    }

    private static ProjectMemoryLocation ResolveLocation(
        string workspacePath,
        string? projectRoot,
        Uri? projectUrl,
        IReadOnlyDictionary<string, string>? metadata)
    {
        var root = ToolPathResolver.ResolveProjectRoot(workspacePath, projectRoot, projectUrl, metadata);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Failed to resolve project root.");
        }

        return new ProjectMemoryLocation(root, Path.Combine(root, SummaryFileName));
    }

    private static string Render(ProjectMemoryDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Title);
        sb.AppendLine(FormattableString.Invariant($"- Version: {document.Version}"));
        sb.AppendLine(FormattableString.Invariant($"- UpdatedAtUtc: {document.UpdatedAtUtc:yyyy-MM-dd HH:mm:ss}"));
        sb.AppendLine();

        AppendSection(sb, SectionOrder[0], document.ProjectOverview, "尚未写入项目概况。");
        AppendSection(sb, SectionOrder[1], document.TechStackAndConstraints, "尚未写入技术栈与架构约束。");
        AppendSection(sb, SectionOrder[2], document.ConfirmedDecisions, "尚未确认长期工程决策。");
        AppendSection(sb, SectionOrder[3], document.KnownRisksAndDebt, "尚未记录已知风险或技术债。");
        AppendSection(sb, SectionOrder[4], document.VerificationBaseline, "尚未建立验证基线。");
        AppendSection(sb, SectionOrder[5], document.LatestExecutionResult, "尚未记录完整任务执行结果。");
        AppendSection(sb, SectionOrder[6], document.LegacyAnalysisSnapshot, "尚未保存历史分析快照。");

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendSection(StringBuilder sb, string title, string content, string placeholder)
    {
        sb.AppendLine("## " + title);
        sb.AppendLine(string.IsNullOrWhiteSpace(content) ? placeholder : content.Trim());
        sb.AppendLine();
    }

    private static ProjectMemoryDocument Parse(string content)
    {
        var document = new ProjectMemoryDocument();
        if (string.IsNullOrWhiteSpace(content))
        {
            return document;
        }

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var versionMatch = Regex.Match(normalized, @"^- Version:\s*(?<value>\d+)\s*$", RegexOptions.Multiline);
        if (versionMatch.Success && int.TryParse(versionMatch.Groups["value"].Value, out var version))
        {
            document.Version = version;
        }

        var updatedAtMatch = Regex.Match(normalized, @"^- UpdatedAtUtc:\s*(?<value>.+)\s*$", RegexOptions.Multiline);
        if (updatedAtMatch.Success && DateTime.TryParse(updatedAtMatch.Groups["value"].Value, out var updatedAt))
        {
            document.UpdatedAtUtc = DateTime.SpecifyKind(updatedAt, DateTimeKind.Utc);
        }

        var matches = TopLevelSectionRegex.Matches(normalized);
        if (matches.Count == 0)
        {
            document.LegacyAnalysisSnapshot = normalized.Trim();
            return document;
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var title = matches[i].Groups["title"].Value.Trim();
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : normalized.Length;
            var sectionContent = normalized[start..end].Trim();

            switch (title)
            {
                case "项目概况":
                    document.ProjectOverview = sectionContent;
                    break;
                case "技术栈与架构约束":
                    document.TechStackAndConstraints = sectionContent;
                    break;
                case "已确认工程决策":
                    document.ConfirmedDecisions = sectionContent;
                    break;
                case "当前已知风险与技术债":
                    document.KnownRisksAndDebt = sectionContent;
                    break;
                case "当前验证基线":
                    document.VerificationBaseline = sectionContent;
                    break;
                case "最近一次任务执行结果":
                    document.LatestExecutionResult = sectionContent;
                    break;
                case "历史分析快照":
                    document.LegacyAnalysisSnapshot = sectionContent;
                    break;
            }
        }

        return document;
    }

    private sealed record ProjectMemoryLocation(string RootPath, string FilePath);
}
