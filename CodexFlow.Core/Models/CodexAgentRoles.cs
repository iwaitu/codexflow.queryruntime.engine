namespace CodexFlow.Core.Models;

public enum CodexAgentRole
{
    Architect, // Ivilson-Prime: 负责架构设计与规划 (Stage 1-2)
    Coordinator, // Ivilson-Coordinator: 只负责任务调度、计划确认、worker 输出汇总
    Forge,     // Ivilson-Forge: 负责代码实现 (Stage 3)
    Sentry,    // Ivilson-Sentry: 负责质量校验与安全审计 (Stage 4 & Critique)
    Security   // Ivilson-Guard: 负责全域安全扫描与合规审计 (Level 8)
}
