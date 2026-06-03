namespace CodexFlow.Core.Planning.Committee;

/// <summary>
/// 委员会规划配置（映射 appsettings.json 的 Planning 节）。
/// </summary>
public class CommitteePlanningOptions
{
    public const string SectionName = "Planning";

    /// <summary>委员会模式开关：Off / Shadow / On。</summary>
    public string CommitteeModeEnabled { get; set; } = "Off";

    /// <summary>最大评审轮次。</summary>
    public int CommitteeMaxRounds { get; set; } = 5;

    /// <summary>会议日志根目录（相对于工作区）。</summary>
    public string CommitteeLogRoot { get; set; } = ".architect-committee-logs";

    /// <summary>角色 → vllm 配置节名称的映射。</summary>
    public Dictionary<string, string> CommitteeRoleBindings { get; set; } = new()
    {
        ["Analyst"] = "VllmCommitteeAnalyst",
        ["Architect"] = "VllmCommitteeArchitect",
        ["ProjectManager"] = "VllmCommitteeProjectManager"
    };

    /// <summary>解析 CommitteeModeEnabled 为枚举。</summary>
    public CommitteeMode GetMode()
    {
        return CommitteeModeEnabled?.Trim() switch
        {
            "On" or "on" or "ON" => CommitteeMode.On,
            "Shadow" or "shadow" or "SHADOW" => CommitteeMode.Shadow,
            _ => CommitteeMode.Off
        };
    }
}
