using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using CodexFlow.Core.Models;
using CodexFlow.Core.TDD.Adapters;
using Newtonsoft.Json;

namespace CodexFlow.Core.TDD;

public class DefaultCodexTestDesigner : ICodexTestDesigner
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<DefaultCodexTestDesigner> _logger;
    private readonly IEnumerable<ITestFrameworkAdapter> _adapters;

    public DefaultCodexTestDesigner(IChatClient chatClient, ILogger<DefaultCodexTestDesigner> logger)
        : this(chatClient, logger, null)
    {
    }

    public DefaultCodexTestDesigner(IChatClient chatClient, ILogger<DefaultCodexTestDesigner> logger, IEnumerable<ITestFrameworkAdapter>? adapters)
    {
        _chatClient = chatClient;
        _logger = logger;

        var adapterList = adapters?.ToList() ?? new List<ITestFrameworkAdapter>();
        if (adapterList.Count == 0)
        {
            StructuredLog.Debug(_logger, "No adapters injected via DI, using default fallback adapters.");
            adapterList.Add(new CSharpXUnitAdapter());
            adapterList.Add(new PythonPytestAdapter());
            adapterList.Add(new NodeJestAdapter());
            adapterList.Add(new JavaJUnitAdapter());
        }
        _adapters = adapterList;
    }

    public async Task<TestPlan> DesignTestsAsync(CodexTask task, CodexSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(session);

        // 1. 探测语言 (简化逻辑：从 Session Metadata 或文件后缀推断)
        // 实际项目应读取 ProjectScanner 的结果
        var language = DetectLanguage(session);
        var adapter = _adapters.FirstOrDefault(a => a.Language.Equals(language, StringComparison.OrdinalIgnoreCase));

        if (adapter == null)
        {
            StructuredLog.Warning(_logger, "TestDesigner: No adapter found for language {Language}. Skipping TDD.", language);
            return new TestPlan { TaskId = task.Id, Reasoning = "Unsupported language" };
        }

        StructuredLog.Information(_logger, "Designing tests for Task {TaskId} using {Framework}...", task.Id, adapter.FrameworkName);

        var taskScope = BuildTaskTestScope(task, adapter);

        // 2. 构造 Prompt
        var projectFacts = session.ActiveFacts != null
            ? string.Join("\n", session.ActiveFacts.Select(f => $"[{f.Category}] {f.Key}: {f.Value}"))
            : "无项目事实背景";

        var projectSummary = !string.IsNullOrEmpty(session.ProjectSummary)
            ? session.ProjectSummary
            : "无项目摘要";

        var context = $"""
[项目摘要]
{projectSummary}

[核心事实与偏好]
{projectFacts}

[当前影子工作区路径]
{session.WorkspacePath}

[当前任务测试范围约束]
{BuildScopeText(taskScope)}
""";

        var prompt = adapter.GetPromptTemplate(task.Description, context);

        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, prompt),
            new ChatMessage(ChatRole.User, "请生成测试代码。确保包含文件名。格式：JSON { \"reasoning\": \"...\", \"files\": [ { \"path\": \"...\", \"content\": \"...\" } ] }")
        };

        var options = new ChatOptions { Temperature = 0.2f, ResponseFormat = ChatResponseFormat.Json };

        // 3. 调用 LLM (Defensive: 120s timeout)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));

        try
        {
            var response = await _chatClient.GetResponseAsync(messages, options, cts.Token).ConfigureAwait(false);
            var json = response.Text?.Trim() ?? string.Empty;

            // 4. 解析结果
            // 简单的 JSON 清洗
            if (!string.IsNullOrEmpty(json) && json.StartsWith("```json", StringComparison.Ordinal))
            {
                json = json.Replace("```json", string.Empty, StringComparison.Ordinal)
                    .Replace("```", string.Empty, StringComparison.Ordinal)
                    .Trim();
            }

            var result = JsonConvert.DeserializeObject<TestDesignResult>(json);
            var rawFiles = result?.Files?.Select(f => new TestFile
            {
                FilePath = f.Path,
                Content = f.Content,
                TargetClassOrModule = "Unknown"
            }).ToList() ?? new List<TestFile>();

            var scopedFiles = FilterByTaskScope(rawFiles, taskScope);

            var plan = new TestPlan
            {
                TaskId = task.Id,
                Language = language,
                Framework = adapter.FrameworkName,
                Reasoning = result?.Reasoning ?? "Auto-generated"
            };
            plan.ReplaceTestFiles(scopedFiles);

            if (rawFiles.Count != scopedFiles.Count)
            {
                StructuredLog.Warning(_logger, 
                    "TestDesigner scope filter removed {Removed}/{Total} generated test files for Task {TaskId}.",
                    rawFiles.Count - scopedFiles.Count,
                    rawFiles.Count,
                    task.Id);
            }

            StructuredLog.Information(_logger, "Test design completed for {TaskId}. Generated {Count} files.", task.Id, plan.TestFiles.Count);
            return plan;
        }
        catch (OperationCanceledException)
        {
            StructuredLog.Warning(_logger, "Test design timed out for Task {TaskId} after 120s.", task.Id);
            return new TestPlan { TaskId = task.Id, Reasoning = "Timeout generating tests" };
        }
        catch (JsonException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to generate/parse Test Design JSON");
            return new TestPlan { TaskId = task.Id, Reasoning = $"Error: {ex.Message}" };
        }
        catch (HttpRequestException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to generate/parse Test Design JSON");
            return new TestPlan { TaskId = task.Id, Reasoning = $"Error: {ex.Message}" };
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to generate/parse Test Design JSON");
            return new TestPlan { TaskId = task.Id, Reasoning = $"Error: {ex.Message}" };
        }
    }

    private static string DetectLanguage(CodexSession session)
    {
        // 1. 优先读取 analyze_project 写入的 Fact
        var langFact = session.ActiveFacts?.FirstOrDefault(f => f.Key == "ProjectLanguage")?.Value;
        if (!string.IsNullOrWhiteSpace(langFact))
            return NormalizeLanguageName(langFact);

        // 2. 从 ProjectFingerprint 文本中启发式推断
        var fingerprint = session.ActiveFacts?.FirstOrDefault(f => f.Key == "ProjectFingerprint")?.Value ?? "";
        if (fingerprint.Contains(".java", StringComparison.OrdinalIgnoreCase) ||
            fingerprint.Contains("pom.xml", StringComparison.OrdinalIgnoreCase) ||
            fingerprint.Contains("build.gradle", StringComparison.OrdinalIgnoreCase))
            return "java";
        if (fingerprint.Contains("tsconfig.json", StringComparison.OrdinalIgnoreCase) ||
            fingerprint.Contains(".tsx", StringComparison.OrdinalIgnoreCase))
            return "typescript";
        if (fingerprint.Contains("package.json", StringComparison.OrdinalIgnoreCase))
            return "javascript";
        if (fingerprint.Contains(".py", StringComparison.OrdinalIgnoreCase) ||
            fingerprint.Contains("pyproject.toml", StringComparison.OrdinalIgnoreCase))
            return "python";

        // 3. 从工作区路径直接探测清单文件
        if (!string.IsNullOrEmpty(session.WorkspacePath) && Directory.Exists(session.WorkspacePath))
        {
            var root = session.WorkspacePath;
            if (File.Exists(Path.Combine(root, "pom.xml")) || File.Exists(Path.Combine(root, "build.gradle")))
                return "java";
            if (File.Exists(Path.Combine(root, "tsconfig.json")))
                return "typescript";
            if (File.Exists(Path.Combine(root, "package.json")))
                return "javascript";
            if (File.Exists(Path.Combine(root, "pyproject.toml")) || File.Exists(Path.Combine(root, "requirements.txt")))
                return "python";
        }

        return "csharp";
    }

    private static string NormalizeLanguageName(string value)
    {
        if (string.Equals(value, "csharp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "c#", StringComparison.OrdinalIgnoreCase))
        {
            return "csharp";
        }

        if (string.Equals(value, "java", StringComparison.OrdinalIgnoreCase))
        {
            return "java";
        }

        if (string.Equals(value, "typescript", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "ts", StringComparison.OrdinalIgnoreCase))
        {
            return "typescript";
        }

        if (string.Equals(value, "javascript", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "js", StringComparison.OrdinalIgnoreCase))
        {
            return "javascript";
        }

        if (string.Equals(value, "python", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "py", StringComparison.OrdinalIgnoreCase))
        {
            return "python";
        }

        return "csharp";
    }

    // 内部类用于 JSON 反序列化
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Used by JSON deserialization.")]
    private sealed class TestDesignResult
    {
        public required string Reasoning { get; set; }
        public required List<TestDesignFile> Files { get; set; }
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Used by JSON deserialization.")]
    private sealed class TestDesignFile
    {
        public required string Path { get; set; }
        public required string Content { get; set; }
    }

    private sealed class TaskTestScope
    {
        public HashSet<string> AllowedSourceFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AllowedTestFileNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TargetSymbols { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasConstraints => AllowedSourceFiles.Count > 0 || AllowedTestFileNames.Count > 0 || TargetSymbols.Count > 0;
    }

    private static TaskTestScope BuildTaskTestScope(CodexTask task, ITestFrameworkAdapter adapter)
    {
        var scope = new TaskTestScope();
        var text = string.Join("\n", new[]
        {
            task.Title ?? string.Empty,
            task.Description ?? string.Empty,
            string.Join("\n", task.Outputs)
        });

        var pathRegex = new Regex(@"(?:[A-Za-z0-9_.-]+[\\/])+[A-Za-z0-9_.-]+\.(?:cs|py|ts|tsx|js|java|csproj|json|md)", RegexOptions.IgnoreCase);
        foreach (Match m in pathRegex.Matches(text))
        {
            var normalized = NormalizePathLike(m.Value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            var fileName = Path.GetFileName(normalized);
            var extension = Path.GetExtension(fileName);

            if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".py", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".ts", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".tsx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".js", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".java", StringComparison.OrdinalIgnoreCase))
            {
                scope.AllowedSourceFiles.Add(normalized);
                var symbol = Path.GetFileNameWithoutExtension(fileName);
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    scope.TargetSymbols.Add(symbol);
                    scope.AllowedTestFileNames.Add(adapter.GetDefaultTestFileName(fileName));
                    scope.AllowedTestFileNames.Add($"{symbol}IntegrationTests.cs");
                    scope.AllowedTestFileNames.Add($"{symbol}ArchitectureTests.cs");
                }
            }

            if (normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("test/", StringComparison.OrdinalIgnoreCase))
            {
                scope.AllowedTestFileNames.Add(fileName);
                var symbol = Path.GetFileNameWithoutExtension(fileName)
                    .Replace("Tests", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("Test", string.Empty, StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    scope.TargetSymbols.Add(symbol);
                }
            }
        }

        return scope;
    }

    private static string BuildScopeText(TaskTestScope scope)
    {
        if (!scope.HasConstraints)
        {
            return "任务描述中未提取到明确文件路径。仅允许输出和当前任务语义直接相关的测试，禁止大范围改写既有测试套件。";
        }

        var lines = new List<string>();
        if (scope.AllowedSourceFiles.Count > 0)
        {
            lines.Add($"- 目标源文件: {string.Join(", ", scope.AllowedSourceFiles.Take(8))}{(scope.AllowedSourceFiles.Count > 8 ? " ..." : string.Empty)}");
        }

        if (scope.AllowedTestFileNames.Count > 0)
        {
            lines.Add($"- 允许测试文件名: {string.Join(", ", scope.AllowedTestFileNames.Take(10))}{(scope.AllowedTestFileNames.Count > 10 ? " ..." : string.Empty)}");
        }

        if (scope.TargetSymbols.Count > 0)
        {
            lines.Add($"- 目标符号: {string.Join(", ", scope.TargetSymbols.Take(10))}{(scope.TargetSymbols.Count > 10 ? " ..." : string.Empty)}");
        }

        return string.Join("\n", lines);
    }

    private static List<TestFile> FilterByTaskScope(List<TestFile> files, TaskTestScope scope)
    {
        if (files.Count == 0)
        {
            return files;
        }

        var normalized = files
            .Where(f => f != null && !string.IsNullOrWhiteSpace(f.FilePath))
            .Select(f => new TestFile
            {
                FilePath = NormalizePathLike(f.FilePath),
                Content = f.Content ?? string.Empty,
                TargetClassOrModule = f.TargetClassOrModule ?? "Unknown"
            })
            .Where(f => !string.IsNullOrWhiteSpace(f.FilePath))
            .ToList();

        // Always avoid non-test paths to reduce accidental wide rewrites.
        normalized = normalized
            .Where(f =>
            {
                var p = f.FilePath;
                return p.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
                       p.StartsWith("test/", StringComparison.OrdinalIgnoreCase) ||
                       p.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) ||
                       p.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (!scope.HasConstraints)
        {
            return normalized
                .GroupBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Take(2)
                .ToList();
        }

        var filtered = normalized
            .Where(f =>
            {
                var fileName = Path.GetFileName(f.FilePath);
                if (scope.AllowedTestFileNames.Contains(fileName))
                {
                    return true;
                }

                if (scope.TargetSymbols.Count == 0)
                {
                    return false;
                }

                return scope.TargetSymbols.Any(symbol =>
                    fileName.Contains(symbol, StringComparison.OrdinalIgnoreCase));
            })
            .GroupBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(3)
            .ToList();

        return filtered;
    }

    private static string NormalizePathLike(string? path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.TrimStart('/');
    }
}

