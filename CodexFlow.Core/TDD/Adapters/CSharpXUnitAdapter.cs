using System.Text.RegularExpressions;
using System.Globalization;
using CodexFlow.Core.Utils;

namespace CodexFlow.Core.TDD.Adapters;

/// <summary>
/// C# (xUnit) 适配器实现。
/// </summary>
public class CSharpXUnitAdapter : ITestFrameworkAdapter
{
    public string Language => "csharp";
    public string FrameworkName => "xUnit";

    public string GetPromptTemplate(string taskDescription, string targetCodeContext)
    {
        return $$"""
你是一个专业的 C# 测试开发工程师，精通 xUnit、FluentAssertions 和 Moq。
你的目标是为当前任务编写**失败的单元测试 (Red Test)**，以指导后续的编码实现。

[任务需求]
{{taskDescription}}

[工程上下文]
{{targetCodeContext}}

[编码规范]
1. 框架：使用 xUnit (`[Fact]`, `[Theory]`)，当前环境推荐使用 xUnit v3。
2. 断言：优先使用 `FluentAssertions` (e.g. `result.Should().Be(...)`)。
3. Mocking：使用 `Moq` 模拟所有外部接口。
4. 结构：遵循 AAA (Arrange, Act, Assert) 模式。
5. 命名：测试类名后缀为 `Tests`，文件名与类名一致。
6. 放置路径：通常应放在 `test/` 目录下的对应测试项目中（如 `test/CleanAppCoreTests/`）。
7. 失败逻辑：测试必须能够成功编译，但运行断言时必须失败，以证明功能尚未实现。
8. 范围限制：只允许生成与“当前任务目标文件/类”直接相关的测试；禁止重写或扩散修改无关测试文件。
9. 文件数量：默认只输出 1-2 个测试文件；若无法确定目标，请返回 `files: []`，不要猜测并批量生成。

[输出要求]
1. 必须返回 JSON：`{ "reasoning": "...", "files": [ { "path": "...", "content": "..." } ] }`
2. `files[].path` 必须是测试目录路径（如 `test/.../...Tests.cs`）。
3. 绝对禁止输出 JSON 之外的解释文本。
""";
    }

    public (string Command, string Args) GetTestCommand(string testFilePath)
    {
        // 对于 dotnet，通常运行整个项目或指定 filter。
        // 为了精确运行，我们假设 testFilePath 所在的 Project 是测试项目。
        // 这里简化为运行特定文件对应的类名（需要从文件路径反推类名，暂用通配符）
        var fileName = Path.GetFileNameWithoutExtension(testFilePath);
        return ("dotnet", $"test --filter \"FullyQualifiedName~{fileName}\" --logger \"console;verbosity=normal\"");
    }

    public TestExecutionResult ParseOutput(string consoleOutput, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(consoleOutput);

        var result = new TestExecutionResult { OutputSummary = consoleOutput };

        // 简单正则解析 dotnet test 输出
        // "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1"
        var passedMatch = Regex.Match(consoleOutput, @"Passed:\s+(\d+)");
        var failedMatch = Regex.Match(consoleOutput, @"Failed:\s+(\d+)");

        if (passedMatch.Success && int.TryParse(passedMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var passed))
        {
            result.PassedCount = passed;
        }

        if (failedMatch.Success && int.TryParse(failedMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var failed))
        {
            result.FailedCount = failed;
        }

        // exitCode 0 = Success, 1 = Failed
        result.Success = (exitCode == 0 && result.FailedCount == 0);

        if (!result.Success)
        {
            // 提取失败详情 (简化版)
            var errorLines = consoleOutput.Split('\n')
                .Where(l => l.Contains("Error Message:", StringComparison.Ordinal) || l.Contains("Stack Trace:", StringComparison.Ordinal))
                .Take(5)
                .ToList();
            result.FailureDetails.AddRange(errorLines);
        }

        return result;
    }

    public string GetDefaultTestFileName(string targetFileName)
    {
        var name = Path.GetFileNameWithoutExtension(targetFileName);
        return $"{name}Tests.cs";
    }
}
