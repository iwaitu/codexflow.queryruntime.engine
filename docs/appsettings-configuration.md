# appsettings.json 配置说明

> 版本：1.0  
> 最后更新：2026-04-25  
> 配置文件：`CodexFlow/appsettings.json`

本文说明 `CodexFlow/appsettings.json` 中各配置项的用途。文档只描述配置语义，不记录真实密钥、连接串或 token 值。生产环境应优先通过环境变量、Secret Manager、Key Vault 或部署平台的 secret 配置覆盖敏感项。

---

## 1. Logging

控制 ASP.NET Core 与依赖库的日志级别。

| 配置项 | 说明 |
|--------|------|
| `Logging:LogLevel:Default` | 应用默认日志级别。 |
| `Logging:LogLevel:Microsoft` | Microsoft 框架日志级别，通常设为 `Warning` 降低噪声。 |
| `Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command` | EF Core SQL command 日志级别，用于控制数据库命令日志输出。 |

---

## 2. Git

控制 Git 相关自动化行为。

| 配置项 | 说明 |
|--------|------|
| `Git:AutoPush` | 是否在特定 Git 自动化流程中自动 push。默认建议关闭，避免非预期远端写入。 |

---

## 3. ContextCompression

控制 Gateway 会话上下文压缩策略。

| 配置项 | 说明 |
|--------|------|
| `GatewaySummaryMaxChars` | Gateway 历史摘要最大字符数。 |
| `PromptHistoryHardLimit` | 构造 prompt 时保留的历史消息硬上限。 |
| `AutoCompressionTriggerRatio` | 上下文 token 使用率达到该比例时触发自动压缩。 |
| `EstimatedCharsPerToken` | 字符数到 token 数的估算比例。 |
| `MinRecentTurnsBeforeCompression` | 压缩时至少保留的最近对话轮数。 |

---

## 4. RuntimeHooks

控制 runtime hook 执行，当前配置了 stop hook。

| 配置项 | 说明 |
|--------|------|
| `RuntimeHooks:Stop:Enabled` | 是否启用 stop hook。 |
| `RuntimeHooks:Stop:TimeoutMs` | stop hook 命令最大执行时间。 |
| `RuntimeHooks:Stop:EnableProjectHooks` | 是否允许读取项目级 `.codexflow/hooks` 配置。 |
| `RuntimeHooks:Stop:Commands` | 全局 stop hook 命令列表。 |

---

## 5. Runtime

控制统一 Query Runtime 的运行特性。

### 5.1 StreamingToolExecution

| 配置项 | 说明 |
|--------|------|
| `Enabled` | 是否启用流式工具执行优化。 |
| `ReadOnlyOnly` | 是否只允许只读工具参与流式提前执行。 |
| `MaxConcurrentStreamingTools` | 流式阶段允许并发执行的最大工具数。 |
| `AllowToolNames` | 流式执行工具白名单；为空表示不额外限制。 |
| `DenyToolNames` | 流式执行工具黑名单。 |
| `EmitDecisionEvents` | 是否输出工具提前执行决策事件。 |
| `LogSkippedDecisions` | 是否记录被跳过的工具决策。 |

---

## 6. Hashline

控制文件编辑的 hashline 审计与安全策略。

| 配置项 | 说明 |
|--------|------|
| `Enabled` | 是否启用 hashline 保护。 |
| `ForceForHighRiskFiles` | 高风险文件是否强制使用 hashline。 |
| `EnableHashlineAuditDetails` | 是否记录详细 hashline 审计信息。 |
| `MaxFileSizeBytes` | 允许 hashline 处理的最大文件大小。 |
| `MaxLineCount` | 允许 hashline 处理的最大行数。 |
| `AllowRewriteWholeFile` | 是否允许整文件重写。 |
| `EnableAuditLogging` | 是否启用审计日志。 |
| `AllowedRoots` | 允许 hashline 操作的根目录列表；为空时使用默认工作区约束。 |

---

## 7. Workspace

控制 worker 与任务执行工作区行为。

| 配置项 | 说明 |
|--------|------|
| `EnableShadowWorkspace` | 是否启用 shadow workspace / worktree 隔离执行。 |
| `KeepShadowOnFailure` | 任务失败时是否保留 shadow workspace 便于排查。 |
| `KeepShadowOnInfrastructureError` | 基础设施异常时是否保留 shadow workspace。 |
| `EnableTaskFileScopeGuard` | 是否启用任务文件范围保护。 |
| `EnableTdd` | 是否启用 TDD 适配能力。 |
| `EnableAuditor` | 是否启用审计器。 |

---

## 8. SemanticRecall

控制语义召回能力。

| 配置项 | 说明 |
|--------|------|
| `Enabled` | 是否启用语义召回。 |
| `Provider` | 召回 provider，例如 `Native`。 |
| `Threshold` | 召回相似度阈值。 |
| `Timeout` | 召回超时时间，单位通常为毫秒。 |
| `PythonExecutable` | 向量索引脚本使用的 Python 可执行文件。 |
| `ScriptPath` | 语义索引脚本路径。 |

---

## 9. TextGrpc

控制文本向量或文本服务的 gRPC 接入。

| 配置项 | 说明 |
|--------|------|
| `Enabled` | 是否启用 TextGrpc 服务。 |
| `Endpoint` | TextGrpc 服务地址。 |
| `UseGteEmbedding` | 是否使用 GTE embedding 路径。 |

---

## 10. SearchTool

控制 Web 搜索工具 provider。

| 配置项 | 说明 |
|--------|------|
| `Provider` | 搜索 provider，例如 `Native` 或 `Brave`。 |
| `BraveApiKey` | Brave Search API key。敏感项，生产环境应通过 secret 覆盖。 |

---

## 11. AllowedHosts

ASP.NET Core host 过滤配置。

| 配置项 | 说明 |
|--------|------|
| `AllowedHosts` | 允许的 Host header。`*` 表示不限制。 |

---

## 12. ConnectionStrings

数据库连接串配置。

| 配置项 | 说明 |
|--------|------|
| `DefaultConnection` | PostgreSQL 默认连接串。包含服务器、端口、账号、密码、数据库、连接池与超时设置。敏感项，生产环境必须通过 secret 覆盖。 |

---

## 13. McpServer

远程 MCP Server 连接配置。

| 配置项 | 说明 |
|--------|------|
| `Id` | MCP server 标识。 |
| `Url` | MCP SSE 或 HTTP endpoint。 |
| `Name` | MCP server 显示名称。 |

---

## 14. Elasticsearch

Elasticsearch 连接配置。

| 配置项 | 说明 |
|--------|------|
| `Url` | Elasticsearch endpoint。 |
| `Username` | Elasticsearch 用户名。 |
| `Password` | Elasticsearch 密码。敏感项，生产环境应通过 secret 覆盖。 |

---

## 15. Qdrant

Qdrant 向量数据库配置。

| 配置项 | 说明 |
|--------|------|
| `Url` | Qdrant endpoint。 |

---

## 16. Token

JWT token 相关配置。

| 配置项 | 说明 |
|--------|------|
| `SecretKey` | JWT 签名密钥。敏感项，必须使用高强度 secret。 |
| `Issuer` | JWT issuer。 |
| `Audience` | JWT audience。 |

---

## 17. Redis

Redis 连接配置。

| 配置项 | 说明 |
|--------|------|
| `EndPoints` | Redis endpoint 列表或地址。 |
| `Password` | Redis 密码。敏感项，生产环境应通过 secret 覆盖。 |

---

## 18. Proxy

本地代理配置。

| 配置项 | 说明 |
|--------|------|
| `Socks5Address` | SOCKS5 代理地址。 |
| `Socks5Port` | SOCKS5 代理端口。 |
| `Enabled` | 是否启用代理。 |

---

## 19. Planning

控制默认 Planner、PlanArtifact、Plan Mode 与专家委员会规划。

| 配置项 | 说明 |
|--------|------|
| `PlanArtifactMode` | PlanArtifact 主流程开关。`Off` 走旧直接 `session.Plan` 流程；`Shadow` 双写观察；`On` 启用 Markdown 计划审批主路径。 |
| `PlanApprovalRequired` | 是否强制执行入口必须存在已批准并投影的 current PlanArtifact。 |
| `PlanPermissionModeEnabled` | 是否在 Plan Mode active 时收窄工具权限，只允许规划、只读和 plan file 相关工具。 |
| `CommitteePlanArtifactMode` | Committee planner 是否接入 PlanArtifact，语义同 `PlanArtifactMode`。 |
| `PlanProjectionRepairEnabled` | 计划投影失败时是否允许修复/重试策略。 |
| `PlanMarkdownStyle` | Markdown 计划生成风格，例如 `normal`。 |
| `PlansDirectory` | workspace 内保存计划文件的相对目录。 |
| `CommitteeModeEnabled` | 专家委员会规划开关或模式。 |
| `CommitteeMaxRounds` | 专家委员会最大评审轮数。 |
| `CommitteeLogRoot` | 专家委员会日志输出目录。 |
| `CommitteeRoleBindings:Analyst` | 需求分析师角色绑定的 LLM 配置 section。 |
| `CommitteeRoleBindings:Architect` | 系统架构师角色绑定的 LLM 配置 section。 |
| `CommitteeRoleBindings:ProjectManager` | 项目经理角色绑定的 LLM 配置 section。 |

推荐 PlanArtifact 正式启用配置：

```json
{
  "Planning": {
    "PlanArtifactMode": "On",
    "PlanApprovalRequired": true,
    "PlanPermissionModeEnabled": true
  }
}
```

---

## 20. OpenSpecExecution

控制 OpenSpec 任务执行方式。

| 配置项 | 说明 |
|--------|------|
| `UseBackgroundJob` | `true` 时通过 background job 执行；`false` 时仍创建 job record，但在当前请求中 inline 执行。 |

---

## 21. VLLM / LLM Provider Sections

以下 section 使用相同的基础字段，用于不同 agent、worker 或专家角色：

- `VllmAgent`
- `VllmCommitteeAnalyst`
- `VllmCommitteeArchitect`
- `VllmCommitteeProjectManager`
- `Vllm`
- `VllmKimi`
- `VllmKMinimax`
- `VllmGlm`

通用字段：

| 配置项 | 说明 |
|--------|------|
| `ApiUrl` | OpenAI-compatible API endpoint 模板。 |
| `ApiKey` | LLM provider API key。敏感项，生产环境应通过 secret 覆盖。 |
| `Model` | 使用的模型名称。 |
| `MaxTokensLength` | 当前模型或客户端允许的最大 token 长度。 |
| `Temperature` | 采样温度，值越高输出越随机。部分 section 未配置时使用代码默认值。 |
| `TopP` | nucleus sampling 参数。部分 section 未配置时使用代码默认值。 |
| `SystemPrompt` | 该角色或 provider 的默认 system prompt。 |

角色说明：

| Section | 主要用途 |
|---------|----------|
| `VllmAgent` | 主 agent 或默认执行 agent 配置。 |
| `VllmCommitteeAnalyst` | 专家委员会需求分析师。 |
| `VllmCommitteeArchitect` | 专家委员会系统架构师。 |
| `VllmCommitteeProjectManager` | 专家委员会项目经理/主持人。 |
| `Vllm` | 通用默认 LLM 配置。 |
| `VllmKimi` | Kimi 模型配置。 |
| `VllmKMinimax` | Minimax 模型配置。 |
| `VllmGlm` | GLM 模型配置。 |

---

## 22. MongoDB

MongoDB 连接配置。

| 配置项 | 说明 |
|--------|------|
| `ConnectionString` | MongoDB 连接串。敏感项，生产环境应通过 secret 覆盖。 |
| `DatabaseName` | MongoDB 数据库名。 |

---

## 23. BrevoEmail

Brevo 邮件服务配置。

| 配置项 | 说明 |
|--------|------|
| `Enabled` | 是否启用 Brevo 邮件发送。 |
| `BaseUrl` | Brevo API base URL。 |
| `ApiKey` | Brevo API key。敏感项，生产环境应通过 secret 覆盖。 |
| `SenderName` | 发件人显示名。 |
| `SenderEmail` | 发件邮箱。 |
| `ReplyToName` | 回复显示名。 |
| `ReplyToEmail` | 回复邮箱。 |
| `UseSandbox` | 是否使用 sandbox 模式。 |

---

## 24. Notifications

通知、后台 job 状态机和主会话注入配置。

| 配置项 | 说明 |
|--------|------|
| `EnableStateMachine` | 是否启用通知状态机。 |
| `EnableMainSessionInjection` | 是否将后台任务/worker 结果注入主会话。 |
| `EnableLlmSummary` | 是否启用 LLM 摘要生成通知内容。 |
| `EnableNotificationCenter` | 是否启用通知中心。 |
| `HeartbeatIntervalSeconds` | heartbeat 周期，单位秒。 |
| `LeaseTimeoutMinutes` | worker/job lease 超时时间，单位分钟。 |
| `MaxRetryCount` | 通知或后台处理最大重试次数。 |
| `ScanWindowMinutes` | 扫描待处理事件的时间窗口，单位分钟。 |
| `HighValueJobTypes` | 高价值 job 类型列表，用于通知、恢复或 UI 优先级判断。 |

---

## 25. CORS

CORS 跨域配置。

| 配置项 | 说明 |
|--------|------|
| `AllowedOrigins` | 允许跨域访问的 origin 列表，当前以逗号分隔字符串表示。 |

---

## 26. 敏感配置建议

以下配置不应长期保存在仓库明文文件中：

- `ConnectionStrings:DefaultConnection`
- `SearchTool:BraveApiKey`
- `Elasticsearch:Password`
- `Token:SecretKey`
- `Redis:Password`
- 所有 `*:ApiKey`
- `MongoDB:ConnectionString`
- `BrevoEmail:ApiKey`

建议使用环境变量覆盖，例如：

```powershell
$env:ConnectionStrings__DefaultConnection = "<postgres-connection-string>"
$env:Token__SecretKey = "<jwt-secret>"
$env:Planning__PlanArtifactMode = "On"
```

ASP.NET Core 使用双下划线 `__` 表示配置层级。

---

## 27. 相关文档

- [Plan Mode 审批与投影系统](plan-mode-tech.md)
- [Planner 规划系统](planner-tech.md)
- [统一会话消息网关](gateway-tech.md)
- [会话上下文与记忆管理](session-context-tech.md)
