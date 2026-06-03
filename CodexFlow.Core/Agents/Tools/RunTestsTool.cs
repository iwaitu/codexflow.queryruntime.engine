using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.TDD;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Globalization;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 多语言测试运行工具。
/// 利用 TDD 适配器执行测试并解析结果。
/// </summary>
public class RunTestsTool : ICodexTool
{
    private readonly IEnumerable<ITestFrameworkAdapter> _adapters;
    private readonly ILogger<RunTestsTool> _logger;

    public string Name => "run_tests";
    public string Description => "运行指定路径的单元测试。支持 C#, Python, Java, Node.js。参数：test_file_path (测试文件路径), language (可选)。Few-shot: run_tests({\"test_file_path\":\"CodexFlow.Tests/FullPipeline_IntegrationTests.cs\",\"language\":\"csharp\"})。";
    public ToolCategory Category => ToolCategory.Analysis;
    public IReadOnlyList<int> AllowedStages => [2, 3, 4];

    public RunTestsTool(IEnumerable<ITestFrameworkAdapter> adapters, ILogger<RunTestsTool> logger)
    {
        _adapters = adapters;
        _logger = logger;
    }

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        var testPath = arguments.GetValueOrDefault("test_file_path")?.ToString();
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var language = arguments.GetValueOrDefault("language")?.ToString();
        var timeoutSecondsRaw = arguments.GetValueOrDefault("timeout_seconds")?.ToString();

        if (string.IsNullOrEmpty(testPath))
            return CodexToolResult.Error("Missing test_file_path.");

        if (string.IsNullOrEmpty(workspacePath))
            workspacePath = Directory.GetCurrentDirectory();

        var timeoutSeconds = 600;
        if (!string.IsNullOrWhiteSpace(timeoutSecondsRaw) &&
            int.TryParse(timeoutSecondsRaw, out var parsedTimeout) &&
            parsedTimeout > 0)
        {
            timeoutSeconds = Math.Min(parsedTimeout, 1800); // 上限 30 分钟
        }

        // [Path Normalization Fix] 使用 ToolPathResolver 解析测试文件路径
        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        if (string.IsNullOrEmpty(baseRoot))
            baseRoot = workspacePath; // Fallback

        var resolvedTestPath = ResolveTestPath(baseRoot, testPath);

        // 1. 自动探测语言（如果未提供）
        if (string.IsNullOrEmpty(language))
        {
            language = DetectLanguage(resolvedTestPath ?? testPath);
        }

        var adapter = _adapters.FirstOrDefault(a => a.Language.Equals(language, StringComparison.OrdinalIgnoreCase));
        if (adapter == null)
            return CodexToolResult.Error($"Unsupported language: {language}. Available: {string.Join(", ", _adapters.Select(a => a.Language))}");

        StructuredLog.Information(_logger, "Running tests for {Path} using {Framework}...", testPath, adapter.FrameworkName);

        // 2. 获取测试命令
        var (command, args) = BuildCommand(language, adapter, baseRoot, testPath, resolvedTestPath);
        StructuredLog.Information(_logger, "Resolved test command: {Command} {Args}", command, args);

        // 3. 执行进程
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                WorkingDirectory = baseRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            // 并发读取 stdout/stderr，避免缓冲区阻塞导致"卡住"
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            var fullOutput = output + "\n" + error;

            // 4. 解析结果
            var result = adapter.ParseOutput(fullOutput, process.ExitCode);

            return CodexToolResult.Succeeded(
                JsonConvert.SerializeObject(new
                {
                    result.Success,
                    result.PassedCount,
                    result.FailedCount,
                    result.FailureDetails,
                    Summary = result.OutputSummary
                }),
                new Dictionary<string, object?> { ["testResult"] = result }
            );
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            StructuredLog.Warning(_logger, "run_tests timed out after {TimeoutSeconds}s. test_file_path={TestPath}", timeoutSeconds, testPath);
            return CodexToolResult.Error($"Execution timed out after {timeoutSeconds}s.");
        }
        catch (IOException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to run tests");
            return CodexToolResult.Error($"Execution failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to run tests");
            return CodexToolResult.Error($"Execution failed: {ex.Message}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            StructuredLog.Error(_logger, ex, "Failed to run tests");
            return CodexToolResult.Error($"Execution failed: {ex.Message}");
        }
    }

    private static (string Command, string Args) BuildCommand(
        string language,
        ITestFrameworkAdapter adapter,
        string workspacePath,
        string originalTestPath,
        string? resolvedTestPath)
    {
        if (!string.Equals(language, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            var target = resolvedTestPath ?? originalTestPath;
            var normalizedForAdapter = Path.IsPathRooted(target)
                ? ToRelativePath(workspacePath, target)
                : target;
            return adapter.GetTestCommand(normalizedForAdapter);
        }

        // C# 特殊处理：输入是 .cs 时，优先定位对应测试项目，避免退化为全局 dotnet test
        var targetPath = resolvedTestPath ?? originalTestPath;
        var ext = Path.GetExtension(targetPath).ToUpperInvariant();

        if (ext == ".CSPROJ")
        {
            return ("dotnet", $"test \"{ToRelativePath(workspacePath, targetPath)}\" --nologo --logger \"console;verbosity=normal\"");
        }

        if (ext == ".CS")
        {
            var fileName = Path.GetFileNameWithoutExtension(targetPath);
            var projectPath = FindNearestProjectFile(targetPath);
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                return ("dotnet", $"test \"{ToRelativePath(workspacePath, projectPath)}\" --filter \"FullyQualifiedName~{fileName}\" --nologo --logger \"console;verbosity=normal\"");
            }

            // 没找到项目就降级为目录级执行（至少限制范围）
            var dir = Path.GetDirectoryName(targetPath) ?? ".";
            return ("dotnet", $"test \"{ToRelativePath(workspacePath, dir)}\" --filter \"FullyQualifiedName~{fileName}\" --nologo --logger \"console;verbosity=normal\"");
        }

        if (Directory.Exists(targetPath))
        {
            return ("dotnet", $"test \"{ToRelativePath(workspacePath, targetPath)}\" --nologo --logger \"console;verbosity=normal\"");
        }

        return adapter.GetTestCommand(originalTestPath);
    }

    /// <summary>
    /// [Path Normalization Fix] 解析测试文件路径，带 fallback 逻辑
    /// </summary>
    private static string? ResolveTestPath(string baseRoot, string testPath)
    {
        if (Path.IsPathRooted(testPath))
        {
            return testPath;
        }

        // 首先尝试归一化后的路径
        var normalized = ToolPathResolver.NormalizeDuplicateRepoPrefix(testPath, baseRoot);
        var combined = Path.GetFullPath(Path.Combine(baseRoot, normalized));

        // Fallback: 如果归一化后路径不存在，尝试原始路径
        if (!File.Exists(combined) && !Directory.Exists(combined))
        {
            var fallback = Path.GetFullPath(Path.Combine(baseRoot, testPath));
            if (File.Exists(fallback) || Directory.Exists(fallback))
            {
                return fallback;
            }
        }

        return combined;
    }

    private static string? FindNearestProjectFile(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            var csproj = Directory.GetFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(csproj))
            {
                return csproj;
            }

            var parent = Directory.GetParent(dir);
            if (parent == null)
            {
                break;
            }
            dir = parent.FullName;
        }

        return null;
    }

    private static string ToRelativePath(string workspacePath, string absoluteOrRelativePath)
    {
        try
        {
            if (!Path.IsPathRooted(absoluteOrRelativePath))
            {
                return absoluteOrRelativePath;
            }

            var relative = Path.GetRelativePath(workspacePath, absoluteOrRelativePath);
            return string.IsNullOrWhiteSpace(relative) ? "." : relative;
        }
        catch (ArgumentException)
        {
            return absoluteOrRelativePath;
        }
        catch (IOException)
        {
            return absoluteOrRelativePath;
        }
        catch (NotSupportedException)
        {
            return absoluteOrRelativePath;
        }
    }

    private static string DetectLanguage(string path)
    {
        var ext = Path.GetExtension(path).ToUpper(CultureInfo.InvariantCulture);
        return ext switch
        {
            ".CS" => "csharp",
            ".PY" => "python",
            ".JAVA" => "java",
            ".TS" or ".JS" or ".TSX" or ".JSX" => "typescript",
            _ => "unknown"
        };
    }
}

