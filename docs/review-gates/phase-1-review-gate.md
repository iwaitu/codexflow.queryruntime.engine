# Phase 1 评审门控报告：LLM-facing Worker 通知协议

> 日期：2026-04-13
> 上游文档：[coordinator-worker-runtime-upgrade-blueprint.md](../archived-blueprints/coordinator-worker-runtime-upgrade-blueprint.md)
> 前置文档：[coordinator-worker-spike-plan.md](../archived-blueprints/coordinator-worker-spike-plan.md)
> 状态：✅ **Gate Passed — Phase 1 Approved**
> 正式格式：**XML Envelope**（基于 Spike B 数据，100% 准确率）

---

## 1. Phase 0 验收确认

| # | 验收项 | 状态 | 证据 |
|---|--------|------|------|
| P0-1 | 术语统一（Coordinator / Worker / WorkerType / WorkerNotification） | ✅ | 蓝图 §1.2 已冻结 |
| P0-2 | 明确第一阶段只做 4 种 worker（explore / plan / forge / verify） | ✅ | 蓝图 §3.3 |
| P0-3 | 明确 XML 仅用于 LLM-facing envelope | ✅ | 蓝图 §3.4 |
| P0-4 | Runtime / Outbox / Gateway / TaskList 不做大规模重命名 | ✅ | 蓝图 §4 Phase 0 |
| P0-5 | 冻结 Hook 术语（Runtime Hook / Worker Hook / Intervention Hook） | ✅ | 蓝图 §3.6 |
| P0-6 | 与 Query Runtime 蓝图对齐时间线 | ✅ | 蓝图 §1.1 已对齐 |

**结论：Phase 0 全部通过。**

---

## 2. Phase 0.5 Spike 验收确认

| # | 验收项 | 状态 | 证据 |
|---|--------|------|------|
| S0-1 | Explore Worker Vertical Slice 完成 | ✅ | [ExploreWorkerSpikeReport.md](../spike-reports/ExploreWorkerSpikeReport.md) |
| S0-2 | Envelope Format A/B Spike 完成 | ✅ | [EnvelopeFormatSpikeReport.md](../spike-reports/EnvelopeFormatSpikeReport.md) |
| S0-3 | 至少 1 个只读 worker 通过 BackgroundJob 全链路 | ✅ | `BackgroundJobRunner -> IQueryRuntimeEngine -> Outbox -> OutboxProjector` |
| S0-4 | 至少 10 个真实样本完成格式对比 | ✅ | 15 样本 × 3 格式 × 6 场景 = 270 个数据点 |
| S0-5 | 明确选定正式格式 | ✅ | **XML Envelope**（100% 准确率，远超 Markdown 95% 和 Json 98.3%） |

**结论：Phase 0.5 全部通过。正式格式选定为 XML Envelope。**

---

## 3. Phase 1 详细设计

> 2026-04-13 实现同步说明：
> - 当前代码使用 `sealed record`，不是下文示意中的 `class`
> - `WorkerNotificationEnvelope.cs` 合并承载三个 envelope record 与 supporting types
> - `WorkerStatus.WaitingUser` 的协议值固定映射为 `waiting`
> - CDATA 对 `]]>` 做了分段防御：`]]]]><![CDATA[>`
> - adapter 实际落点为 `CodexFlow.Application/Notifications/IWorkerNotificationProjector.cs` 与 `CodexFlow/Services/Notifications/WorkerNotificationProjector.cs`
> - 主链路集成已接通：`BackgroundJobRunner -> Outbox payload(workerNotificationXml) -> NotificationDispatcher -> MainSessionInjectionService -> GatewayMessageProcessor`

### 3.1 核心类型定义

```csharp
namespace CodexFlow.Core.Protocols;

/// <summary>Worker 类型枚举</summary>
public enum WorkerType
{
    /// <summary>只读探索：搜索、定位文件、归纳调用链</summary>
    Explore,
    /// <summary>只读规划：输出实现路线与关键文件</summary>
    Plan,
    /// <summary>写型：默认在 shadow worktree 运行，修改代码</summary>
    Forge,
    /// <summary>严格只读：可运行命令、测试、输出证据化报告</summary>
    Verify
}

/// <summary>Worker 状态枚举</summary>
public enum WorkerStatus
{
    Completed,
    Failed,
    WaitingUser
}

/// <summary>通用 Worker 通知信封 — 注入给 Coordinator 模型看的 XML 文本</summary>
public sealed class WorkerNotificationEnvelope
{
    public required string TaskId { get; init; }
    public required string JobId { get; init; }
    public required WorkerType WorkerType { get; init; }
    public required WorkerStatus Status { get; init; }
    public required string Summary { get; init; }
    public string? Result { get; init; }
    public string? ResumeToken { get; init; }  // 仅 WaitingUser 时有值
    public WorkerUsageInfo? Usage { get; init; }
    public DateTime CompletedAtUtc { get; init; }
}

/// <summary>Worker 用量信息</summary>
public sealed class WorkerUsageInfo
{
    public int DurationMs { get; init; }
    public int ToolCalls { get; init; }
    public int WriteToolCalls { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
}

/// <summary>验证报告信封 — verify worker 输出</summary>
public sealed class VerificationReportEnvelope
{
    public required string TaskId { get; init; }
    public required string JobId { get; init; }
    public required bool Passed { get; init; }
    public required string Summary { get; init; }
    public List<VerificationEvidence> Evidence { get; init; } = new();
    public List<string> Issues { get; init; } = new();
    public WorkerUsageInfo? Usage { get; init; }
}

/// <summary>验证证据项</summary>
public sealed class VerificationEvidence
{
    public required string Check { get; init; }
    public required bool Passed { get; init; }
    public string? Command { get; init; }
    public string? ExitCode { get; init; }
    public string? Observation { get; init; }
}

/// <summary>WaitingUser 通知信封</summary>
public sealed class WaitingUserEnvelope
{
    public required string TaskId { get; init; }
    public required string JobId { get; init; }
    public required string ResumeToken { get; init; }
    public required string Reason { get; init; }
    public string? Context { get; init; }
}
```

### 3.2 XML Serializer 设计

```csharp
namespace CodexFlow.Core.Protocols;

/// <summary>XML Envelope 序列化器 — 强类型对象 → LLM-facing XML 文本</summary>
public interface IWorkerNotificationSerializer
{
    string Serialize(WorkerNotificationEnvelope envelope);
    string Serialize(VerificationReportEnvelope envelope);
    string Serialize(WaitingUserEnvelope envelope);
}

public sealed class XmlWorkerNotificationSerializer : IWorkerNotificationSerializer
{
    public string Serialize(WorkerNotificationEnvelope env) => $"""
        <task-notification>
          <task-id>{XmlEscape(env.TaskId)}</task-id>
          <job-id>{XmlEscape(env.JobId)}</job-id>
          <worker-type>{env.WorkerType.ToString().ToLowerInvariant()}</worker-type>
          <status>{env.Status.ToString().ToLowerInvariant()}</status>
          <summary>{XmlEscape(env.Summary)}</summary>
          {SerializeResult(env.Result)}
          {SerializeResumeToken(env.ResumeToken)}
          {SerializeUsage(env.Usage)}
          <completed-at>{env.CompletedAtUtc:O}</completed-at>
        </task-notification>
        """;

    public string Serialize(VerificationReportEnvelope env) => $"""
        <verification-report>
          <task-id>{XmlEscape(env.TaskId)}</task-id>
          <job-id>{XmlEscape(env.JobId)}</job-id>
          <passed>{env.Passed.ToString().ToLowerInvariant()}</passed>
          <summary>{XmlEscape(env.Summary)}</summary>
          {SerializeEvidenceList(env.Evidence)}
          {SerializeIssuesList(env.Issues)}
          {SerializeUsage(env.Usage)}
        </verification-report>
        """;

    public string Serialize(WaitingUserEnvelope env) => $"""
        <waiting-user>
          <task-id>{XmlEscape(env.TaskId)}</task-id>
          <job-id>{XmlEscape(env.JobId)}</job-id>
          <resume-token>{XmlEscape(env.ResumeToken)}</resume-token>
          <reason>{XmlEscape(env.Reason)}</reason>
          {SerializeContext(env.Context)}
        </waiting-user>
        """;

    private static string XmlEscape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static string SerializeResult(string? result)
    {
        if (string.IsNullOrEmpty(result)) return string.Empty;
        // 长文本用 CDATA 包裹，避免转义问题
        if (result.Length > 500 || result.Contains('<') || result.Contains('&'))
            return $"<result><![CDATA[{result}]]></result>";
        return $"<result>{XmlEscape(result)}</result>";
    }

    private static string SerializeResumeToken(string? token)
        => string.IsNullOrEmpty(token) ? string.Empty
           : $"<resume-token>{XmlEscape(token)}</resume-token>";

    private static string SerializeUsage(WorkerUsageInfo? usage)
    {
        if (usage == null) return string.Empty;
        return $"""
            <usage>
              <duration_ms>{usage.DurationMs}</duration_ms>
              <tool_calls>{usage.ToolCalls}</tool_calls>
              <write_tool_calls>{usage.WriteToolCalls}</write_tool_calls>
              {FormatTokenUsage(usage)}
            </usage>
            """;
    }

    private static string FormatTokenUsage(WorkerUsageInfo usage)
    {
        if (usage.InputTokens == 0 && usage.OutputTokens == 0) return string.Empty;
        return $"""
            <tokens>
              <input>{usage.InputTokens}</input>
              <output>{usage.OutputTokens}</output>
            </tokens>
            """;
    }

    private static string SerializeContext(string? context)
    {
        if (string.IsNullOrEmpty(context)) return string.Empty;
        if (context.Length > 500 || context.Contains('<') || context.Contains('&'))
            return $"<context><![CDATA[{context}]]></context>";
        return $"<context>{XmlEscape(context)}</context>";
    }

    private static string SerializeEvidenceList(List<VerificationEvidence> evidence)
    {
        if (evidence.Count == 0) return string.Empty;
        var items = evidence.Select(e => $"""
              <evidence>
                <check>{XmlEscape(e.Check)}</check>
                <passed>{e.Passed.ToString().ToLowerInvariant()}</passed>
                {FormatIfNotNull(e.Command, "command")}
                {FormatIfNotNull(e.ExitCode, "exit_code")}
                {FormatIfNotNull(e.Observation, "observation")}
              </evidence>
            """);
        return $"<evidence-list>\n{string.Join("\n", items)}\n</evidence-list>";
    }

    private static string SerializeIssuesList(List<string> issues)
    {
        if (issues.Count == 0) return string.Empty;
        var items = issues.Select(i => $"<issue>{XmlEscape(i)}</issue>");
        return $"<issues>\n{string.Join("\n", items)}\n</issues>";
    }

    private static string FormatIfNotNull(string? value, string tag)
        => string.IsNullOrEmpty(value) ? string.Empty : $"<{tag}>{XmlEscape(value)}</{tag}>";
}
```

### 3.3 字段约定

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `task-id` | string | ✅ | 任务 ID（`TASK_XXX_001` 格式） |
| `job-id` | string | ✅ | 后台 Job ID（ULID 格式） |
| `worker-type` | string | ✅ | `explore` / `plan` / `forge` / `verify` |
| `status` | string | ✅ | `completed` / `failed` / `waiting` |
| `summary` | string | ✅ | 人类+模型可读的结果摘要（≤500 字符） |
| `result` | string | ❌ | 详细结果（支持 CDATA，长文本/含特殊字符时自动使用） |
| `resume-token` | string | ❌ | 仅 `waiting` 状态时有值，用于恢复 worker |
| `usage` | 嵌套 | ❌ | 耗时、工具调用数、token 用量 |
| `completed-at` | ISO 8601 | ✅ | 完成时间戳 |

### 3.4 Escaping 规则

| 规则 | 处理方式 |
|------|---------|
| 标准 XML 转义 | `& → &amp;`, `< → &lt;`, `> → &gt;`, `"` → `&quot;`, `' → &apos;` |
| 长文本（>500 字符） | 自动使用 `<![CDATA[...]]>` 包裹 |
| 含 `<` 或 `&` 的文本 | 自动使用 CDATA 包裹 |
| CDATA 中含 `]]>` | 自动拆分为 `]]]]><![CDATA[>` |
| 空值字段 | 整个 XML 元素省略（不输出空标签） |
| 列表为空 | 整个列表元素省略 |

### 3.5 落点目录结构

```
CodexFlow.Core/
├── Protocols/
│   ├── IWorkerNotificationSerializer.cs
│   ├── XmlWorkerNotificationSerializer.cs
│   └── WorkerNotificationEnvelope.cs
CodexFlow.Application/
├── Notifications/
│   └── IWorkerNotificationProjector.cs
CodexFlow/
├── Services/
│   └── Notifications/
│       └── WorkerNotificationProjector.cs
```

### 3.6 与现有系统的集成点

| 集成点 | 方向 | 说明 |
|--------|------|------|
| **BackgroundJobRunner** | 产出 → | 在 `JobCompleted` / `JobFailed` / `JobWaitingUser` 时构造 XML，并把 `workerNotificationXml` 写入 outbox payload / `ResultPayloadJson` |
| **OutboxProjector** | 投影 → | 保持 outbox payload 原样投影到 Mongo / Redis / SignalR，保证 XML 随事件回流 |
| **NotificationDispatcher** | 消费/产出 | 读取 `workerNotificationXml`，写入 `NotificationEnvelope.Payload.WorkerNotificationXml` |
| **MainSessionInjectionService** | 消费 ← | 将 XML 作为 `GatewayMessage.Content` 入队 |
| **GatewayMessageProcessor** | 消费 ← | 将 XML 信封作为系统通知喂给 Coordinator 模型 |
| **Validator** | 产出 → | 通过 `IWorkerNotificationProjector.ProjectVerificationReport(...)` 生成 `VerificationReportEnvelope`（接口已就绪） |

### 3.7 边界场景测试矩阵（≥10 个场景）

| # | 测试场景 | 信封类型 | 验证点 |
|---|---------|---------|--------|
| T1 | 正常完成，短 summary | WorkerNotification | 基本 XML 结构正确 |
| T2 | 正常完成，长 summary（>500 字符） | WorkerNotification | 自动使用 CDATA |
| T3 | result 含 `< > &` 特殊字符 | WorkerNotification | XML 转义或 CDATA 正确 |
| T4 | result 为 null/空 | WorkerNotification | `<result>` 元素被省略 |
| T5 | WaitingUser 状态 | WorkerNotification | `status=waiting`，`resume-token` 存在 |
| T6 | 非 WaitingUser 状态 | WorkerNotification | `resume-token` 被省略 |
| T7 | 验证报告全部通过 | VerificationReport | `passed=true`, evidence 列表正确 |
| T8 | 验证报告部分失败 | VerificationReport | issues 列表正确，evidence 包含失败项 |
| T9 | 验证报告无 evidence | VerificationReport | `<evidence-list>` 被省略 |
| T10 | 超长 result（>10KB） | WorkerNotification | CDATA 包裹，不被截断 |
| T11 | Usage 为 null | WorkerNotification | `<usage>` 被省略 |
| T12 | 空 summary | WorkerNotification | 不抛出异常，省略 `<summary>` |
| T13 | 多行日志 result | WorkerNotification | 换行符保留，CDATA 正确 |
| T14 | WaitingUser 含长 context | WaitingUserEnvelope | context 使用 CDATA |
| T15 | 所有可选字段均有值 | WorkerNotification | 完整 XML 结构正确 |
| T16 | result 含 `]]>` | WorkerNotification | 自动拆分 CDATA，`XDocument.Parse()` 成功 |

### 3.8 验收标准

- [x] 新增 `CodexFlow.Core/Protocols/` 协议类型与 serializer
- [x] 新增 projector adapter，并接入 `BackgroundJobRunner` / `NotificationDispatcher` / `MainSessionInjectionService`
- [x] `XmlWorkerNotificationSerializer` 通过 16 个边界场景测试
- [x] XML 输出可被 `XDocument.Parse()` 稳定解析（含 `]]>` 边界）
- [x] `BackgroundJobRunner` 在完成/失败/WaitingUser 时自动构造 XML envelope
- [x] XML 通过 outbox payload 回流，并作为 `GatewayMessage.Content` 进入 Coordinator
- [x] Spike 报告选定的 XML 格式被唯一实现
- [x] 文档同步更新

---

## 4. Phase 1 实施优先级排序与工时

| 优先级 | 任务 | 预估工时 | 依赖 |
|--------|------|---------|------|
| **P0** | 定义核心类型（Envelope / Usage / Evidence 等） | 0.5 天 | 无 |
| **P0** | 实现 `XmlWorkerNotificationSerializer` + 单元测试 | 1.5 天 | 核心类型 |
| **P1** | `BackgroundJobRunner` 集成：构造 XML envelope | 1 天 | Serializer |
| **P1** | `OutboxProjector` 集成：XML 注入 Gateway 消息队列 | 1 天 | BackgroundJobRunner |
| **P2** | `Validator` 集成：`VerificationReportEnvelope` 输出 | 1 天 | 核心类型 |
| **P2** | `WaitingUserEnvelope` 集成 | 0.5 天 | 核心类型 |
| **P2** | 端到端集成测试（job → XML → Gateway → Coordinator 消费） | 1 天 | P0+P1+P2 |

**总计：约 6.5 天**

---

## 5. 决策记录

| 项目 | 值 |
|------|-----|
| **评审结论** | ✅ **Gate Passed — Phase 1 Approved** |
| **正式 Envelope 格式** | XML |
| **依据** | EnvelopeFormatSpikeReport: 15×3×6 完整 A/B 测试，XML 100% 准确率 |
| **评审日期** | 2026-04-13 |
| **评审人** | CTO + GPT Architect |
| **替代方案考虑** | Markdown（实现成本低 1.5 天，但 `waiting_user` 75% 准确率不可接受） |
| **重新评估条件** | 如果未来 Agent 模型对 Markdown 结构遵循度提升到 99%+ |
