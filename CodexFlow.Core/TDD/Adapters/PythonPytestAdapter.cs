using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.TDD.Adapters;

/// <summary>
/// Python (pytest) 适配器实现。
/// </summary>
public class PythonPytestAdapter : ITestFrameworkAdapter
{
    public string Language => "python";
    public string FrameworkName => "pytest";

    public string GetPromptTemplate(string taskDescription, string targetCodeContext)
    {
        return $$"""
你是一个专业的 Python 测试工程师，精通 pytest 和 unittest.mock。
请根据以下任务需求，编写一个**失败的单元测试 (Red Test)**。

[任务描述]
{{taskDescription}}

[被测代码上下文]
{{targetCodeContext}}

[要求]
1. 使用 `pytest` 风格。
2. 使用 `unittest.mock` 或 `pytest-mock` 模拟依赖。
3. 遵循 AAA (Arrange, Act, Assert) 模式。
4. 测试必须语法正确，但断言应针对“尚未实现的功能”失败。

输出必须是纯 Python 代码。
""";
    }

    public (string Command, string Args) GetTestCommand(string testFilePath)
    {
        return ("pytest", $"{testFilePath} -v");
    }

    public TestExecutionResult ParseOutput(string consoleOutput, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(consoleOutput);

        var result = new TestExecutionResult { OutputSummary = consoleOutput };

        // pytest output: "=== 1 failed, 2 passed in 0.12s ==="
        var passedMatch = Regex.Match(consoleOutput, @"(\d+)\s+passed");
        var failedMatch = Regex.Match(consoleOutput, @"(\d+)\s+failed");
        var errorMatch = Regex.Match(consoleOutput, @"(\d+)\s+error");

        result.PassedCount = passedMatch.Success ? int.Parse(passedMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        result.FailedCount = failedMatch.Success ? int.Parse(failedMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        if (errorMatch.Success) result.FailedCount += int.Parse(errorMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        // pytest exit codes: 0=All passed, 1=Tests failed, 2=Interrupted, etc.
        result.Success = (exitCode == 0);

        if (!result.Success)
        {
            // 提取 FAILED 标记附近的行
            var lines = consoleOutput.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("FAILED", StringComparison.Ordinal))
                {
                    result.FailureDetails.Add(lines[i]);
                    if (i + 1 < lines.Length) result.FailureDetails.Add(lines[i + 1]);
                }
            }
        }

        return result;
    }

    public string GetDefaultTestFileName(string targetFileName)
    {
        var name = Path.GetFileNameWithoutExtension(targetFileName);
        return $"test_{name}.py";
    }
}
