# H1 本地崩溃恢复实施报告

日期：2026-08-24  
版本：`CodexFlow.QueryRuntime.Engine 0.2.0-preview.21`  
Runtime contract：`qre-v2-h1-4`  
状态：最终对抗核查通过

## 1. 结论

H1 已在路线三约定的边界内完成：同 Runtime contract 版本、本地、单进程的未完成 Turn 可以从稳定
checkpoint 继续执行。恢复保持原 `SessionId`/`TurnId`，创建新的 `RunAttemptId`，并记录 root/parent/ordinal
lineage。实现不声称多进程接管、跨版本迁移、exactly-once 或非幂等工具自动恢复。

H1 的恢复策略是保守且 fail-closed 的：已持久化的纯文本模型输出和完整工具批次不会重复执行；工具调用
已经由模型提交、但工具结果尚未完整持久化时，checkpoint 被标记为 `NeedsReconciliation`，恢复会在任何
provider/tool 调用之前停止。

## 2. 已完成内容

- 新增 `RuntimeRunAttemptId` 与 root/parent/ordinal lineage；每次恢复必须创建子 attempt。
- 新增 versioned `RuntimeCheckpointDocument`，保存冻结请求、请求指纹、完整 Session state、canonical
  history、恢复边界和 reconciliation 分类。
- 新增 `IRuntimeCheckpointSink`、内存实现和 `RuntimeJsonCheckpointStore`。
- JSON checkpoint 使用规范文件名、路径 containment、大小/深度上限、严格 JSON、payload length、SHA-256
  完整性校验、原子替换和可选 private-file 权限。
- `RuntimeAgentLoop` 新增恢复入口；Hosting facade 通过 `IResumableAgentRuntime` 暴露恢复能力，同时保持
  `IAgentRuntime` 源码兼容。
- `qre resume latest` 查找最新未完成叶子 attempt；已完成叶子及其被领取的父 attempt 不会再次恢复。
- CLI 仅为 sanitized/private 模式持久化 checkpoint；public-redacted 执行不产生可恢复 checkpoint。
- provider、model、API mode、runner、profile、thinking、approval 和有效工具 composition 等宿主配置进入
  `RecoveryCompatibilityId`。外部 stdio 执行器、descriptor 与 recovery digest 来自同一次 manifest 快照；
  command、args、transport、capabilities、inputSchema、timeout 和 output limit 均进入 digest，漂移时请求指纹
  校验失败。当前 external adapter 仍使用固定参数 envelope，manifest `inputSchema` 只参与兼容性绑定。
- compatibility fingerprint 使用解析后的有效 Docker 配置和 checkpoint storage class；private 与 sanitized
  checkpoint 不允许相互降级/切换恢复。
- canonical history 以完整 `RuntimeHistoryMessage` entry 持久化；恢复会校验并保留原 MessageId、
  CommittedVersion、ItemIds 和真实 next sequence，不会二次 normalization；内部、尾部与全量省略造成的
  sequence gap 均可稳定恢复。受限 history blobs 连同 digest/data
  进入 checkpoint，并验证 `runtime-history://sha256/...` 引用闭包。
- 新增自崩溃 harness，在 `StepPrepared` 成功落盘后立即 `Environment.FailFast`，用于验证独立进程恢复。
- STJ source generation 覆盖 checkpoint 类型，Native AOT 无反射序列化依赖。

## 3. 故障窗口语义

| 最后稳定 checkpoint | 恢复行为 |
| --- | --- |
| `TurnStarted` | 从 Step 0 开始 |
| `StepPrepared` | 丢弃未完成 Step snapshot，重新采样该 Step |
| `ModelCommitted`，无工具 | 不再调用 provider，提交已保存 assistant output |
| `ModelCommitted`，含工具 | `NeedsReconciliation`，provider/tool 调用均为 0 |
| `ToolBatchCommitted` | 不重复工具，从下一 Step 继续 |
| `StepCommitted` | 不重新采样，重新执行无副作用的终止决策 |
| `ContinuationCommitted` | 从已提交 continuation 后继续下一 Step |
| `Terminal` | 拒绝恢复 |

## 4. 安全与一致性门禁

- 截断、非法 JSON、超限文件、非法摘要、payload 篡改、错误 Runtime contract、错误请求指纹、损坏
  lineage、非规范文件名和不稳定状态边界均在执行前拒绝。
- 工具批次 checkpoint 必须保证 model tool calls 与 terminal invocation results 数量、顺序和 invocation ID
  一一对应。
- checkpoint sink 启用时必须提供非空且已规范化的宿主兼容标识。
- durable checkpoint 写失败始终终止 Turn；H1 不再提供可继续执行的 `BestEffort` 模式。
- SHA-256 用于检测意外损坏和内容篡改，不代表 checkpoint 来源认证；本地信任边界仍依赖目录 ACL 与宿主
  部署控制。

## 5. 验证证据

| 门禁 | 结果 |
| --- | --- |
| H1/CLI 专项测试 | 26/26 通过 |
| Linux Runtime unit suite | 435/435 通过 |
| Linux integration suite | 0 失败；15 个需 Docker/真实 provider 开关的测试按门禁跳过 |
| Windows Release solution build | 0 warning，0 error |
| CodexFlow 最终包专项测试 | 13/13 通过 |
| CodexFlow Core 全量回归 | 975/975 通过 |
| Windows x64 Native AOT | 发布成功；`qre --version` 为 `0.2.0-preview.21` |
| AOT 自崩溃恢复 | `StepPrepared` 后源进程异常退出；子 attempt 恢复输出 `H1_PREVIEW21_AOT_OK` |
| 顺序单 owner 叶子选择 | 已完成后再次 `resume latest` 返回“无未完成 v2 checkpoint”；并发领取属于 H2 |
| 真实 vLLM 恢复 | `qwen3.8-27b-nvfp4` 返回 `H1_REAL_RESUME_OK`；65/166/231 tokens |
| clean NuGet consumer | 输出 `qre-v2-h1-4:package-smoke-preview21` |

最终 NuGet 包：`CodexFlow.QueryRuntime.Engine.0.2.0-preview.21.nupkg`  
SHA-256：`F851E8673F7256EC98ACC4D6F1DDD84A9035EEB690713E6BC30139D59FF7A315`

## 6. 明确边界与后续项

- H1 不包含 H2 的 lease、heartbeat、CAS、fencing 或多 owner takeover。
- H1 不包含 H3 的 durable intent/outcome ledger、external idempotency key 或 `ReconcileAsync`。
- H1 仅支持相同 `qre-v2-h1-4` contract；不提供 N/N-1 checkpoint migration。
- `StepPrepared` 恢复可能重复 provider 请求与费用；终止策略必须保持无副作用。
- 当前正式自动恢复消费者是 Runtime/CLI。CodexFlow 已升级并通过回归，但尚未加入平台级后台扫描、请求/
  工具依赖重建和自动领取；这些属于宿主产品集成，不应被描述为已经完成。

相关设计文档：`docs/adr/ADR-008-local-crash-resume.md`、
`docs/h1-crash-resume-threat-model.md`。
