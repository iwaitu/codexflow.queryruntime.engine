using System.Collections.ObjectModel;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.TDD;

/// <summary>
/// 核心 TDD 设计器接口。
/// 负责根据 Planning 结果和架构规范，生成“红灯”测试用例。
/// </summary>
public interface ICodexTestDesigner
{
    /// <summary>
    /// 为指定的 Task 生成测试计划。
    /// </summary>
    /// <param name="task">规划的任务</param>
    /// <param name="session">当前会话上下文（含 ProjectFacts 用于探测语言）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>包含测试代码的计划</returns>
    Task<TestPlan> DesignTestsAsync(CodexTask task, CodexSession session, CancellationToken ct = default);
}

public class TestPlan
{
    public string TaskId { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Framework { get; set; } = string.Empty;

    /// <summary>
    /// 生成的测试文件列表
    /// </summary>
    public Collection<TestFile> TestFiles { get; } = new();

    /// <summary>
    /// 测试设计思路（思维链）
    /// </summary>
    public string Reasoning { get; set; } = string.Empty;

    public void ReplaceTestFiles(IEnumerable<TestFile>? testFiles)
    {
        TestFiles.Clear();
        if (testFiles == null)
        {
            return;
        }

        foreach (var testFile in testFiles)
        {
            TestFiles.Add(testFile);
        }
    }
}

public class TestFile
{
    public string FilePath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string TargetClassOrModule { get; set; } = string.Empty;
}
