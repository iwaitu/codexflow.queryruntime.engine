using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.TDD.Adapters;

/// <summary>
/// TypeScript/Node.js (Jest) 适配器实现。
/// </summary>
public class NodeJestAdapter : ITestFrameworkAdapter
{
    public string Language => "typescript"; // 也支持 javascript
    public string FrameworkName => "Jest";

    public string GetPromptTemplate(string taskDescription, string targetCodeContext)
    {
        return $$"""
你是一个专业的 TypeScript 测试工程师，精通 Jest。
请根据以下任务需求，编写一个**失败的单元测试 (Red Test)**。

[任务描述]
{{taskDescription}}

[被测代码上下文]
{{targetCodeContext}}

[要求]
1. 使用 `describe`, `it`, `expect` 语法。
2. 如果需要模拟模块，使用 `jest.mock()`。
3. 遵循 AAA (Arrange, Act, Assert) 模式。
4. 代码必须是合法的 TypeScript/JavaScript。

输出必须是纯代码。
""";
    }

    public (string Command, string Args) GetTestCommand(string testFilePath)
    {
        // 使用 --json 方便解析
        return ("npx", $"jest {testFilePath} --json");
    }

    public TestExecutionResult ParseOutput(string consoleOutput, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(consoleOutput);

        var result = new TestExecutionResult { OutputSummary = consoleOutput };

        try
        {
            // 尝试寻找 JSON 输出部分（Jest 可能会混杂一些 console.log 在 JSON 前后）
            var jsonStart = consoleOutput.IndexOf('{', StringComparison.Ordinal);
            var jsonEnd = consoleOutput.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = new string(consoleOutput.AsSpan(jsonStart, jsonEnd - jsonStart + 1));
                var jsonObj = JObject.Parse(json);

                result.PassedCount = (int)(jsonObj["numPassedTests"] ?? 0);
                result.FailedCount = (int)(jsonObj["numFailedTests"] ?? 0);
                result.Success = (bool)(jsonObj["success"] ?? false);

                if (!result.Success)
                {
                    var testResults = jsonObj["testResults"] as JArray;
                    if (testResults != null)
                    {
                        foreach (var suite in testResults)
                        {
                            var message = suite["message"]?.ToString();
                            if (!string.IsNullOrEmpty(message))
                            {
                                result.FailureDetails.Add(string.Concat(message.AsSpan(0, Math.Min(200, message.Length)), "..."));
                            }
                        }
                    }
                }
            }
            else
            {
                // Fallback parsing if JSON fails
                result.FailureDetails.Add("Jest JSON output parsing failed.");
            }
        }
        catch (JsonReaderException)
        {
            result.FailureDetails.Add("Jest output parsing exception.");
        }
        catch (FormatException)
        {
            result.FailureDetails.Add("Jest output parsing exception.");
        }
        catch (InvalidCastException)
        {
            result.FailureDetails.Add("Jest output parsing exception.");
        }

        return result;
    }

    public string GetDefaultTestFileName(string targetFileName)
    {
        var name = Path.GetFileNameWithoutExtension(targetFileName);
        return $"{name}.test.ts";
    }
}
