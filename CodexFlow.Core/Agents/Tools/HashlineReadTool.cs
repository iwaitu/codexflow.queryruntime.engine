using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Agents.Tools;

public sealed class HashlineReadTool : ICodexTool
{
    private readonly ReadFileTool _inner;

    public HashlineReadTool(ReadFileTool inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public string Name => "hs_read";

    public string Description => "Hashline 专用读取工具。固定以 Hashline 快照模式读取既有文件，返回 snapshotId、fileFingerprint 和带锚点的 renderedText。\n" +
        "参数（JSON object）：\n" +
        "  - path (string, 必填): 相对于工作区根目录的文件路径\n" +
        "  - window_start_line (int, 可选): 分段读取起始行（1-based）\n" +
        "  - window_line_count (int, 可选): 分段读取返回的最大行数\n" +
        "返回：Hashline 快照。\n" +
        "适用：Program.cs、*.csproj、appsettings*.json 等高风险既有文件修改前的快照读取。\n" +
        "Few-shot:\n" +
        "  hs_read({\"path\":\"src/CleanApp/Program.cs\"})\n" +
        "  hs_read({\"path\":\"src/CleanApp/appsettings.json\",\"window_start_line\":1,\"window_line_count\":80})";

    public ToolCategory Category => ToolCategory.Read;

    public IReadOnlyList<int> AllowedStages => _inner.AllowedStages;

    public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var delegated = new Dictionary<string, object?>(arguments, StringComparer.OrdinalIgnoreCase)
        {
            ["mode"] = "hashline"
        };

        delegated.Remove("start_line");
        delegated.Remove("end_line");

        return _inner.ExecuteAsync(delegated, ct);
    }
}
