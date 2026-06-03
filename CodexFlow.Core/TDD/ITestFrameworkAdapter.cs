using System.Collections.ObjectModel;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.TDD;

/// <summary>
/// 多语言测试框架适配器接口。
/// 负责屏蔽不同语言（C#, Python, Java, TS/JS）在测试生成、执行和结果解析上的差异。
/// </summary>
public interface ITestFrameworkAdapter
{
    /// <summary>
    /// 适配的语言（如 "csharp", "python", "java", "typescript"）
    /// </summary>
    string Language { get; }

    /// <summary>
    /// 适配的测试框架名称（如 "xUnit", "pytest", "JUnit", "Jest"）
    /// </summary>
    string FrameworkName { get; }

    /// <summary>
    /// 生成用于指导 LLM 编写测试代码的 Prompt 模板。
    /// </summary>
    /// <param name="taskDescription">任务描述</param>
    /// <param name="targetCodeContext">被测代码的上下文（类名、方法签名等）</param>
    /// <returns>System Prompt 片段</returns>
    string GetPromptTemplate(string taskDescription, string targetCodeContext);

    /// <summary>
    /// 获取运行测试的命令行参数。
    /// </summary>
    /// <param name="testFilePath">测试文件路径</param>
    /// <returns>可执行命令 (Command) 和参数 (Args)</returns>
    (string Command, string Args) GetTestCommand(string testFilePath);

    /// <summary>
    /// 解析测试运行器的标准输出，提取结构化结果。
    /// </summary>
    /// <param name="consoleOutput">控制台输出</param>
    /// <param name="exitCode">进程退出码</param>
    /// <returns>测试执行结果</returns>
    TestExecutionResult ParseOutput(string consoleOutput, int exitCode);

    /// <summary>
    /// 获取测试文件的默认命名规则。
    /// </summary>
    /// <param name="targetFileName">被测文件名</param>
    /// <returns>推荐的测试文件名（如 UserService -> UserServiceTests.cs）</returns>
    string GetDefaultTestFileName(string targetFileName);
}

public class TestExecutionResult
{
    public bool Success { get; set; }
    public int PassedCount { get; set; }
    public int FailedCount { get; set; }
    public string OutputSummary { get; set; } = string.Empty;
    public Collection<string> FailureDetails { get; } = new();

    public void ReplaceFailureDetails(IEnumerable<string>? failureDetails)
    {
        FailureDetails.Clear();
        if (failureDetails == null)
        {
            return;
        }

        foreach (var failureDetail in failureDetails)
        {
            FailureDetails.Add(failureDetail);
        }
    }
}
