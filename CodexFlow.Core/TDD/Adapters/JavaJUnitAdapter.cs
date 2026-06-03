using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.TDD.Adapters;

/// <summary>
/// Java (JUnit) 适配器实现。自动检测 Maven / Gradle 构建工具。
/// </summary>
public class JavaJUnitAdapter : ITestFrameworkAdapter
{
    public string Language => "java";
    public string FrameworkName => "JUnit5";

    public string GetPromptTemplate(string taskDescription, string targetCodeContext)
    {
        return $$"""
你是一个专业的 Java 测试工程师，精通 JUnit 5 和 Mockito。
请根据以下任务需求，编写一个**失败的单元测试 (Red Test)**。

[任务描述]
{{taskDescription}}

[被测代码上下文]
{{targetCodeContext}}

[要求]
1. 使用 JUnit 5 注解 (`@Test`, `@DisplayName`)。
2. 使用 `Mockito` 模拟依赖。
3. 遵循 AAA (Arrange, Act, Assert) 模式。
4. 输出必须是完整的 Java 类文件内容。

输出必须是纯 Java 代码。
""";
    }

    public (string Command, string Args) GetTestCommand(string testFilePath)
    {
        var className = Path.GetFileNameWithoutExtension(testFilePath);
        var projectDir = FindProjectRoot(testFilePath);

        if (IsGradleProject(projectDir))
        {
            // Gradle: use --tests filter
            return ("gradle", $"test --tests \"*{className}\" --info");
        }

        // Maven (default)
        return ("mvn", $"-Dtest={className} test");
    }

    public TestExecutionResult ParseOutput(string consoleOutput, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(consoleOutput);

        var result = new TestExecutionResult { OutputSummary = consoleOutput };

        // Maven format: "Tests run: 1, Failures: 0, Errors: 0, Skipped: 0"
        var mavenMatch = Regex.Match(consoleOutput, @"Tests run:\s+(\d+),\s+Failures:\s+(\d+),\s+Errors:\s+(\d+),\s+Skipped:\s+(\d+)");
        if (mavenMatch.Success)
        {
            int total = int.Parse(mavenMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            int failures = int.Parse(mavenMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            int errors = int.Parse(mavenMatch.Groups[3].Value, CultureInfo.InvariantCulture);
            int skipped = int.Parse(mavenMatch.Groups[4].Value, CultureInfo.InvariantCulture);

            result.FailedCount = failures + errors;
            result.PassedCount = total - result.FailedCount - skipped;
            result.Success = (result.FailedCount == 0 && exitCode == 0);
            return result;
        }

        // Gradle format: "3 tests completed, 1 failed" or "SUCCESS" / "FAILURE"
        var gradleTestMatch = Regex.Match(consoleOutput, @"(\d+)\s+tests?\s+completed,?\s*(\d+)?\s*failed?");
        if (gradleTestMatch.Success)
        {
            int total = int.Parse(gradleTestMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            int failed = gradleTestMatch.Groups[2].Success ? int.Parse(gradleTestMatch.Groups[2].Value, CultureInfo.InvariantCulture) : 0;

            result.FailedCount = failed;
            result.PassedCount = total - failed;
            result.Success = (failed == 0 && exitCode == 0);
        }
        else if (consoleOutput.Contains("BUILD SUCCESSFUL", StringComparison.Ordinal) || consoleOutput.Contains("BUILD SUCCESS", StringComparison.Ordinal))
        {
            result.Success = true;
        }
        else
        {
            result.Success = false;
            var tool = consoleOutput.Contains("gradle", StringComparison.OrdinalIgnoreCase) ? "Gradle" : "Maven";
            result.FailureDetails.Add($"{tool} build failed.");
        }

        return result;
    }

    public string GetDefaultTestFileName(string targetFileName)
    {
        var name = Path.GetFileNameWithoutExtension(targetFileName);
        return $"{name}Test.java";
    }

    private static bool IsGradleProject(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        return File.Exists(Path.Combine(dir, "build.gradle")) ||
               File.Exists(Path.Combine(dir, "build.gradle.kts"));
    }

    private static string? FindProjectRoot(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "pom.xml")) ||
                File.Exists(Path.Combine(dir, "build.gradle")) ||
                File.Exists(Path.Combine(dir, "build.gradle.kts")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return Path.GetDirectoryName(filePath);
    }
}
