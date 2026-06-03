using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 深入分析项目结构、指纹及语义依赖。
/// </summary>
public class AnalyzeProjectTool(
    ProjectScanner scanner,
    ICodeAnalysisService semanticScanner,
    IArchitectureService archService,
    CodexSessionManager sessionManager,
    IProjectMemoryService projectMemoryService,
    ILogger<AnalyzeProjectTool> logger,
    IMemoryOrchestrator? memoryOrchestrator = null) : ICodexTool
{
    public string Name => "analyze_project";
    public string Description => "对当前项目执行一次较重的全局分析，建立工程指纹、文件索引、语义依赖图，并识别架构债务。仅当你需要全局项目总览、后续规划输入或跨模块索引时调用；如果只是定位某个具体问题，优先使用更轻量的 `ivilson_ls` / `search_in_files` / `ivilson_read`。Few-shot: analyze_project({\"workspace_path\":\".\"})。";
    public ToolCategory Category => ToolCategory.Analysis;
    public IReadOnlyList<int> AllowedStages => [0, 1, 2];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();

        if (string.IsNullOrEmpty(workspacePath))
            return CodexToolResult.Error("Missing workspace_path.");

        // [Path Normalization Fix] 使用 ToolPathResolver 解析正确的项目根目录
        var mainRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(mainRoot))
            return CodexToolResult.Error("Failed to resolve project root.");

        try
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("### 项目深度分析报告");

            // 1. 结构概览与技术栈统计
            var projectMapping = await scanner.ScanAndSummarizeAsync(mainRoot).ConfigureAwait(false);
            report.AppendLine("\n#### 1. 工程指纹");
            report.AppendLine(projectMapping);

            // 2. 语义依赖图构建
            var graph = await semanticScanner.BuildGraphAsync(mainRoot, ct).ConfigureAwait(false);
            var topCriticalPaths = new List<string>();
            if (graph != null && graph.Nodes != null)
            {
                topCriticalPaths = graph.Nodes.Values
                    .OrderByDescending(n => n.CriticalityScore)
                    .Select(n => NormalizePathForPrompt(mainRoot, n.FilePath))
                    .Where(static path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToList();

                report.AppendLine("\n#### 2. 核心组件依赖");
                var graphSummary = string.Join("\n", graph.Nodes.Values
                    .OrderByDescending(n => n.CriticalityScore)
                    .Take(5)
                    .Select(n => $"- `{NormalizePathForPrompt(mainRoot, n.FilePath)}` (引用分：{n.CriticalityScore})"));
                report.AppendLine(graphSummary);
            }

            // 3. 架构坏味道分析
            var archMetrics = archService.AnalyzeGraph(graph ?? new DependencyGraph());
            var criticalSmells = archMetrics?.Where(m => m.RefactorPriority > 0).Take(3).ToList();

            if (criticalSmells is { Count: > 0 })
            {
                report.AppendLine("\n#### 3. 架构风险/债务");
                foreach (var smell in criticalSmells)
                {
                    report.AppendLine(FormattableString.Invariant($"- **{smell.Summary}** (优先级：{smell.RefactorPriority})"));
                }
            }

            // 4. 自动探测主语言
            var detectedLanguage = DetectPrimaryLanguage(mainRoot);
            report.AppendLine(FormattableString.Invariant($"\n#### 4. 主语言探测：**{detectedLanguage}**"));

            var finalReport = report.ToString();

            // 自动读取 README（如存在），为后续 LLM 总结提供业务上下文
            string readmeContent = "";
            var readmeNames = new[] { "README.md", "readme.md", "Readme.md", "README.MD", "README", "readme" };
            foreach (var name in readmeNames)
            {
                var readmePath = Path.Combine(mainRoot, name);
                if (File.Exists(readmePath))
                {
                    try
                    {
                        var raw = await File.ReadAllTextAsync(readmePath, ct).ConfigureAwait(false);
                        // 截取前 3000 字符，防止超大 README 膨胀上下文
                        readmeContent = raw.Length > 3000 ? raw[..3000] + "\n\n... [README 已截断，共 " + raw.Length + " 字符]" : raw;
                        StructuredLog.Information(logger, "Found README at {Path} ({Len} chars)", readmePath, raw.Length);
                    }
                    catch (IOException readmeEx)
                    {
                        StructuredLog.Warning(logger, readmeEx, "Failed to read README at {Path}", readmePath);
                    }
                    catch (UnauthorizedAccessException readmeEx)
                    {
                        StructuredLog.Warning(logger, readmeEx, "Failed to read README at {Path}", readmePath);
                    }
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(readmeContent))
            {
                report.AppendLine("\n#### 5. 项目 README 摘要");
                report.AppendLine(readmeContent);
                finalReport = report.ToString();
            }

            string summaryPersistNote;
            try
            {
                Uri? projectUrl = null;
                IReadOnlyDictionary<string, string>? metadata = null;
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    var session = await sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
                    projectUrl = session.ProjectUrl;
                    metadata = session.Metadata;
                }

                var persistResult = await projectMemoryService.SaveAnalysisAsync(
                    new ProjectAnalysisMemoryInput(
                        mainRoot,
                        projectRoot,
                        sessionId,
                        projectUrl,
                        metadata,
                        detectedLanguage,
                        projectMapping,
                        criticalSmells?.Select(smell => smell.Summary).ToList() ?? [],
                        readmeContent,
                        finalReport),
                    ct).ConfigureAwait(false);
                summaryPersistNote = "\n\n✅ 项目摘要已自动保存到工作区根目录：PROJECT_SUMMARY.md";
                StructuredLog.Information(logger, "Analyze project memory persisted to {Path}", persistResult.FilePath);
            }
            catch (IOException saveEx)
            {
                StructuredLog.Warning(logger, saveEx, "analyze_project could not persist summary file for workspace {Path}", mainRoot);
                summaryPersistNote = $"\n\n⚠️ 项目摘要文件落盘失败：{saveEx.Message}";
            }
            catch (UnauthorizedAccessException saveEx)
            {
                StructuredLog.Warning(logger, saveEx, "analyze_project could not persist summary file for workspace {Path}", mainRoot);
                summaryPersistNote = $"\n\n⚠️ 项目摘要文件落盘失败：{saveEx.Message}";
            }
            catch (InvalidOperationException saveEx)
            {
                StructuredLog.Warning(logger, saveEx, "analyze_project could not persist summary file for workspace {Path}", mainRoot);
                summaryPersistNote = $"\n\n⚠️ 项目摘要文件落盘失败：{saveEx.Message}";
            }

            // 同步到 Session 记忆
            if (!string.IsNullOrEmpty(sessionId))
            {
                var analyzeProjectMeta = new MemoryEntryMetadata(
                    Scope: MemoryFactScope.Session,
                    Source: "analyze_project",
                    Confidence: MemoryFactConfidence.High).ToJson();

                // Phase 4/6: Load session once for orchestrator calls (protected-key writes + auto-refresh).
                var session = memoryOrchestrator != null
                    ? await sessionManager.GetOrCreateSessionAsync(sessionId).ConfigureAwait(false)
                    : null;

                // Phase 4/6: ProjectFingerprint is a protected key — route through MemoryOrchestrator.
                if (session != null && memoryOrchestrator != null)
                {
                    await memoryOrchestrator.WriteFactAsync(
                        session,
                        ProjectMemoryFactKeys.ProjectFingerprint,
                        projectMapping,
                        MemoryFactCategories.Project,
                        analyzeProjectMeta,
                        ct).ConfigureAwait(false);
                }
                else
                {
                    await sessionManager.LearnFactAsync(
                        sessionId,
                        ProjectMemoryFactKeys.ProjectFingerprint,
                        projectMapping,
                        MemoryFactCategories.Project,
                        analyzeProjectMeta).ConfigureAwait(false);
                }

                // ProjectLanguage is not a protected key — direct write is fine.
                await sessionManager.LearnFactAsync(
                    sessionId,
                    ProjectMemoryFactKeys.ProjectLanguage,
                    detectedLanguage,
                    MemoryFactCategories.Project,
                    analyzeProjectMeta).ConfigureAwait(false);

                try
                {
                    var fileIndex = await scanner.GenerateFileIndexAsync(mainRoot).ConfigureAwait(false);
                    var fileIndexJson = Newtonsoft.Json.JsonConvert.SerializeObject(fileIndex);
                    // ProjectFileIndex is non-vectorizable — direct write is fine.
                    await sessionManager.LearnFactAsync(
                        sessionId,
                        ProjectMemoryFactKeys.ProjectFileIndex,
                        fileIndexJson,
                        MemoryFactCategories.Project,
                        analyzeProjectMeta).ConfigureAwait(false);
                }
                catch (JsonException indexEx)
                {
                    StructuredLog.Warning(logger, indexEx, "analyze_project could not persist ProjectFileIndex for {SessionId}", sessionId);
                }
                catch (InvalidOperationException indexEx)
                {
                    StructuredLog.Warning(logger, indexEx, "analyze_project could not persist ProjectFileIndex for {SessionId}", sessionId);
                }

                if (criticalSmells is { Count: > 0 })
                {
                    // Phase 4/6: ArchitectureAudit is a protected key — route through MemoryOrchestrator.
                    if (session != null && memoryOrchestrator != null)
                    {
                        await memoryOrchestrator.WriteFactAsync(
                            session,
                            ProjectMemoryFactKeys.ArchitectureAudit,
                            finalReport,
                            MemoryFactCategories.Analysis,
                            analyzeProjectMeta,
                            ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await sessionManager.LearnFactAsync(
                            sessionId,
                            ProjectMemoryFactKeys.ArchitectureAudit,
                            finalReport,
                            MemoryFactCategories.Analysis,
                            analyzeProjectMeta).ConfigureAwait(false);
                    }
                }

                // Phase 5/6: Trigger auto-refresh so Qdrant is rebuilt and code-structure staleness is cleared.
                if (session != null && memoryOrchestrator != null)
                {
                    await memoryOrchestrator.RunAutoRefreshAsync(
                        session,
                        AutoRefreshTrigger.AnalyzeProjectCompleted,
                        ct).ConfigureAwait(false);
                }
            }

            var hasReadme = !string.IsNullOrWhiteSpace(readmeContent);
            var nextStepDirective = "\n\n🔄 [SYSTEM] 项目全局分析已完成，摘要已自动落盘。" +
                (hasReadme
                    ? "已自动读取并包含了项目 README 内容。现在请优先基于该报告和 README 向用户输出简明结论或继续定位具体问题。"
                    : "未发现 README 文件。现在请直接基于分析报告向用户输出结论或继续定位具体问题。") +
                $" 如果用户点名了具体模块、类名、文件名或子系统，下一步优先沿用这些原词，或直接读取上面已出现的真实文件路径；不要先把问题扩写成更宽泛的猜测命名。只有当用户明确要求制定计划、任务拆解或阶段推进时，才考虑调用 `{PlanningToolNames.Primary}` 或相关阶段工具；不要把“进入规划阶段”当作默认下一步。对某个系统形成结论前，至少读取对应源码文件，而不是只依据目录结构或命名搜索。";
            return CodexToolResult.Succeeded(
                finalReport + summaryPersistNote + nextStepDirective,
                summary: BuildAnalysisSummary(topCriticalPaths));
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "analyze_project failed");
            return CodexToolResult.Error($"项目分析失败：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(logger, ex, "analyze_project failed");
            return CodexToolResult.Error($"项目分析失败：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(logger, ex, "analyze_project failed");
            return CodexToolResult.Error($"项目分析失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 根据文件后缀分布和清单文件自动探测项目的主语言。
    /// </summary>
    private static string DetectPrimaryLanguage(string rootPath)
    {
        // 优先检测清单文件（高置信度）
        if (File.Exists(Path.Combine(rootPath, "pom.xml")) ||
            File.Exists(Path.Combine(rootPath, "build.gradle")) ||
            File.Exists(Path.Combine(rootPath, "build.gradle.kts")))
            return "java";

        if (File.Exists(Path.Combine(rootPath, "package.json")))
        {
            // 区分 TypeScript 与纯 JavaScript
            if (File.Exists(Path.Combine(rootPath, "tsconfig.json")))
                return "typescript";
            return "javascript";
        }

        if (File.Exists(Path.Combine(rootPath, "pyproject.toml")) ||
            File.Exists(Path.Combine(rootPath, "setup.py")) ||
            File.Exists(Path.Combine(rootPath, "requirements.txt")))
            return "python";

        if (Directory.GetFiles(rootPath, "*.sln", SearchOption.TopDirectoryOnly).Length > 0 ||
            Directory.GetFiles(rootPath, "*.slnx", SearchOption.TopDirectoryOnly).Length > 0 ||
            Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories)
                .Any(f => !f.Replace("\\", "/", StringComparison.Ordinal).Contains("/shadows/", StringComparison.OrdinalIgnoreCase)))
            return "csharp";

        // 回退：按源文件数量统计
        try
        {
            var langWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "csharp", 0 }, { "java", 0 }, { "typescript", 0 },
                { "javascript", 0 }, { "python", 0 }
            };

            var ignored = new[] { ".git", "node_modules", "bin", "obj", ".venv", "dist", "build", ".vs", ".idea", "target", "shadows" };

            foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(rootPath, file).Replace("\\", "/", StringComparison.Ordinal);
                if (ignored.Any(i => rel.Contains(i, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var ext = Path.GetExtension(file).ToUpperInvariant();
                switch (ext)
                {
                    case ".CS": langWeights["csharp"]++; break;
                    case ".JAVA": langWeights["java"]++; break;
                    case ".TS" or ".TSX": langWeights["typescript"]++; break;
                    case ".JS" or ".JSX": langWeights["javascript"]++; break;
                    case ".PY": langWeights["python"]++; break;
                }
            }

            // TypeScript 包含 JavaScript 文件也算
            langWeights["typescript"] += langWeights["javascript"];

            var best = langWeights.OrderByDescending(kv => kv.Value).First();
            if (best.Value > 0) return best.Key;
        }
        catch (IOException)
        {
            // 扫描失败时不阻塞分析流程
        }
        catch (UnauthorizedAccessException)
        {
            // 扫描失败时不阻塞分析流程
        }
        catch (ArgumentException)
        {
            // 扫描失败时不阻塞分析流程
        }

        return "csharp"; // 最终兜底
    }

    private static string NormalizePathForPrompt(string rootPath, string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return string.Empty;
        }

        try
        {
            if (Path.IsPathRooted(candidatePath))
            {
                return Path.GetRelativePath(rootPath, candidatePath).Replace('\\', '/');
            }
        }
        catch (ArgumentException)
        {
            // Ignore incompatible roots and keep the original path.
        }

        return candidatePath.Replace('\\', '/');
    }

    private static string BuildAnalysisSummary(List<string> topCriticalPaths)
    {
        if (topCriticalPaths.Count == 0)
        {
            return "Global project analysis ready. Next, reuse exact user terms or concrete file paths from the report before broader pattern searches.";
        }

        return $"Global project analysis ready. High-signal files: {string.Join("; ", topCriticalPaths)}. Read concrete files before making architecture claims.";
    }
}

