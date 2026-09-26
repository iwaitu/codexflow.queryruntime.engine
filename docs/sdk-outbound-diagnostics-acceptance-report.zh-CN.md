# SDK 出站诊断验收报告

- 日期：2026-09-26
- R7 修复复核：2026-09-27
- 对应计划：[sdk-outbound-diagnostics-plan.zh-CN.md](sdk-outbound-diagnostics-plan.zh-CN.md)（R5 设计、R7 核查修复）
- 设计与 P0 证据：[ADR-009](adr/ADR-009-sdk-outbound-diagnostics.md)
- 验证依赖：VllmChatClient 2.0.25、Microsoft.Extensions.AI 10.10.0、.NET SDK 10.0.401
- 验证环境：Windows 11 x64（win-x64）。macOS/Linux 已配置 CI 作业，但本报告没有本次提交在这些平台的成功运行证据；macOS AOT 目前为非阻塞作业。
- 验收状态：诊断误判与采集缺陷已按第 7 节修复；原计划要求的“文本完成不依赖缺失 finish reason 降级”仍未满足，不宣称全部原始门槛通过。

## 1. 交付范围

| 阶段 | 交付 | 位置 |
|---|---|---|
| P0 | 可选 `IRuntimeModelAttemptClient` 契约；Runtime 权威序号透传（含 PresentingModelClient 的 Run/Resume）；Provider × API 模式能力矩阵及证据测试；ADR-009 | Protocol、Engine、`Models/Diagnostics/QreTransportCapabilityMatrix.cs` |
| P1 | 类型化记录、Sink、调用作用域、诊断 Handler、CLI 存储/配额/保留、`--sdk-diagnostics`、run 摘要 | `Models/Diagnostics/`、`Cli/Diagnostics/QreDiagnosticsStore.cs` |
| P2 | 三层投影（ChatCompletions、Responses、AnthropicMessages）、有界旁路观察、白名单导出 | `QreSemanticProjector.cs`、`QreOutboundDiagnosticHandler.cs`、`QreDiagnosticsExporter.cs` |
| P3 | 版本化规范化与 §8.1 规则表、跨运行对齐与最早差异、`diagnose latest/inspect/compare` | `QreDiagnosticsAnalyzer.cs`、`QreDiagnosticsComparer.cs` |
| P4 | 内存终端传输、fixture 格式、补全骨架、离线重建断言、`diagnose skeleton/rebuild` | `QreOfflineModelTransport.cs`、`QreDiagnosticsRebuilder.cs` |
| P5 | AOT、兼容、性能、安全回归；示例、双语文档、SECURITY/威胁模型更新 | `examples/SdkOutboundDiagnostics/`、`docs/` |

## 2. 验收矩阵（计划 §13）

| 类别 | 已执行测试 | 结果 |
|---|---|---|
| 关闭兼容 | `Off_ProducesNoRecordsNoScopesAndNoContentWrapping`、`DiagnosticsOff_KeepsRunOutputShapeAndCreatesNoDiagnosticFiles`、全部既有单元测试 | 通过；Off 不建文件、不包装正文，run JSON 形状不变 |
| 关联 | `SharedHttpClient_ConcurrentCallsNeverCrossCorrelate`（12 并发）、`AsyncLocalBridge_DoesNotLeakAcrossYieldsToTheConsumer`、`SendWithoutModelCallScope_IsMarkedUncorrelated`、`ResponseStream_EarlyDisposeIsRecordedOnceWithReadBytes` | 通过 |
| 模型尝试 | `RuntimeModelAttemptContractTests`（facade Run/Resume、同 Step 两次可重试尝试、旧客户端降级、展示事件一致、取消）、`RuntimeRetry_CreatesNewModelCallIdsWithReducerOrdinals`、`LegacyEntryPoint_MarksRuntimeAttemptOrdinalUnavailable` | 通过；跨恢复序号重现已验证（见 ADR-009 E10） |
| 错误阶段 | `BeforeHeadersTransportError_IsClassifiedByType`、`HttpStatusFailure_IsClassifiedFromHandlerEvidenceNotMessage`、`ResponseStream_ReadErrorAndEofAreDistinguished`、`HttpClientTimeout_IsNotMisreportedAsCallerCancellation`、`CallerCancellation_IsClassifiedFromUpstreamToken`、`Classification_CancellationAndTimeoutRaceIsReportedAsRace` | 通过；超时由适配器凭类型链分类，Handler 报 unknown |
| 所有权 | `Cell_MatchesLockedSdkBehavior`（Dispose 释放注入 HttpClient）、示例 `hosts`（共享 Handler 在 Dispose 后仍可用） | 通过；共享 HttpClient 实例标为 unsupported |
| 尝试 | `StreamingPath_DoesNotRetryHttpFailure...`、`RetryHandlerAbove_ResendsOriginalContentAndEachAttemptIsObserved` | 通过；覆盖声明为 handler_visible |
| 请求等价 | `OutboundTransportEquivalenceTests`：HTTP/1.1 真实 SDK（chunked、无 Content-Length、字节一致）、HTTP/2 h2c（无 Content-Length/Transfer-Encoding、字节一致）、已知长度保持、重发、写入中取消 | 通过 |
| 流行为 | EOF/提前释放/取消/读错误区分；HTTP、流、模型终态分别记录 | 通过；SSE 多片段由真实 SDK 解析路径覆盖 |
| 脱敏 | `Structure_ProjectsRequestWithoutSensitiveMaterial`（Header/URL userinfo/query/path/host/正文/schema/描述/错误体/请求 ID/Cookie 金丝雀）、CLI 导出金丝雀、`Export_KeepsCleanRecordsIntactAndDropsUnknownVocabulary` | 通过；既有 Runtime/CLI 原始错误消息列为已知风险 |
| 缺失语义 | `ReorderedToolsAndMissingEvidenceAreNeverReportedAsConsistent`、`CaptureLimit_OmitsStructureButSendsRequestIntact`、`DeepOrInvalidJson_FailsProjectionOnly`、`UnsupportedContent_IsNotWrappedAndReportsUnsupported` | 通过 |
| API 规则 | 27 个能力格证据测试、`KnownSdkLimitations_RemainUnexpectedChangesWithTags` | 通过 |
| 比较 | 空 Tools + RequiredToolName、宿主漏映射、数值精度、数组重排、跨运行最早差异 | 通过 |
| 模型身份 | `ModelIdentity_DescriptorDefaultIsEquivalentAndUnknownDefaultIsNotComparable`、导出后模型明文不出现 | 通过 |
| 调用前拒绝 | `NoToolProfileWithRequiredTool_IsRejectedBeforeAnyModelOrHttpCall`（ArgumentException、零调用、model_call_not_started）、`StepToolSelectionOmittingRequiredTool_FailsBeforeAnyModelOrHttpCall` | 通过 |
| 工具名大小写 | `CaseDemo_ReadonlyRequiredToolRunsThroughCliSdkAndOfflineTransport`（两组） | 通过；见第 4 节 |
| 超时类型证据 | 同“错误阶段” | 通过 |
| 重建 | `ExportCompareSkeletonAndRebuild_WorkOfflineAndStaySafe`、`DefectiveOptionsFactoryDemo_...`、`HandWrittenInputsWithOmittedOptionalFields_KeepSafeDefaults` | 通过；骨架被阻断（exit 2），HTTP 请求均为内存终端 |
| 资源 | `Store_QueueFullDropsRecordsAndReportsIncomplete`、`Store_RunQuotaAndDiskFailureNeverBreakTheModelCall`、`OversizedRecord_IsReducedToAnOmittedEnvelope`、`SinkFailuresAndDrops_NeverAffectTheModelCall` | 通过 |
| 输入安全 | `Reader_RejectsUnsafeBundleEntries`（路径越界、嵌套、非白名单条目）、`Reader_RejectsCompressionBombsOversizeAndFutureSchemas` | 通过 |
| 兼容恢复 | `CorruptDiagnosticSidecar_DoesNotAffectStrictReplay`、`Reader_ReportsTruncatedTailAndSequenceGapsWithoutFailing`、`Prune_AppliesRetentionAndRunCountOnlyInsideTheRoot` | 通过 |
| 发布 | Native AOT 门禁、AOT smoke、原生二进制运行 diagnose/rebuild、性能测试 | 通过；见第 3 节 |

R7 诊断修复时的 Release 单元测试为 563 个：551 通过、12 失败。基线 `9c56da3` 在同机 Release 下为 435 个：423 通过、12 失败；逐项比较 TRX 中的失败测试名，集合完全相同。失败涉及缺失 /bin/sh、沙箱命令返回码、Windows 路径差异与 Git 临时对象清理权限。诊断与模型尝试相关筛选测试为 128 个，全部通过，其中本轮新增 26 个边界回归用例。此结果替代初版的 535/520/15 和 104 个新增测试统计。随后完成第 8 节的测试跨平台修复，Windows 最新全量结果为 **563 通过、0 失败、0 跳过**。

## 3. 发布门禁

- Native AOT：`scripts/qre-aot-gate.sh win-x64 Release` 通过，**无 trim/AOT 警告**，未新增豁免。首次运行因本机 PATH 缺少 `vswhere.exe` 在链接阶段失败，补充 VS Installer 目录后通过（环境问题，非代码问题）。
- AOT 原生二进制：`qre run --sdk-diagnostics structure`、`diagnose latest/inspect/export/skeleton/rebuild` 均实际运行；两个示例 fixture 在原生二进制中分别得到 exit 1 与 exit 0。`scripts/qre-aot-smoke.sh` 通过。
- 性能（Release，真实 SDK + 内存传输，预热 60 次，3 轮 × 300 次，取中位 p95）：

| 模式 | p95 | p95 额外耗时 | 阈值 | 每次调用分配 |
|---|---|---|---|---|
| Off（基线） | 0.171 ms | - | - | ≈47 KB |
| Metadata | 0.273 ms | 0.102 ms | max(1 ms, 5%) | ≈73 KB |
| Structure | 0.379 ms | 0.207 ms | max(2 ms, 10%) | ≈92 KB |

  基线本身不足 1 ms，因此适用绝对阈值；相对基线的比例开销较大（约 60%/120%），在真实网络调用（数百毫秒量级）下可忽略，但报告如实列出。

## 4. 与计划预期不同的事实

1. **流式文本不上报结束原因（E6）**：锁定 SDK 的三种 API 流式路径只在工具调用时设置 `FinishReason`。即使离线 fixture 在线上发送了 `finish_reason: stop`/`response.completed`/`end_turn`，适配器仍会发出 `missing_provider_finish_reason` 并以 Unknown 结束。计划要求“不能依赖流结束但没有 finish reason 的降级路径”；fixture 已包含合法结束标志，但 SDK 丢弃它，这一点无法在不修改 SDK 的前提下满足。大小写演示中第 1、3 次调用的正常完成因此经由该降级路径；第 2 次（工具调用）有真实结束原因。已列为后续 SDK 任务。
2. **共享 HttpClient 不受支持（E3）**：SDK Dispose 会释放注入客户端并写入默认 Header。按计划不绕过 Dispose，改为支持“共享 Handler + 每客户端独立 HttpClient（disposeHandler: false）”。
3. **SDK 丢失显式约束（E9）**：Gemini chat 丢失 `max_tokens`、`response_format`、`tool_choice`；DeepSeek 丢失 `response_format`。诊断将其报告为 adapter → HTTP 的 unexpected_change 并标注 `known_sdk_limitation`，不视为正常转换。
4. **大小写演示的 HTTP 事实（E7）**：离线真实 SDK 截获表明，`tool_choice.function.name` 保留 `QRE_READ_FILE` 原样，而声明名为 `qre_read_file`；诊断在 Runtime → adapter 标记 `case_only_mismatch`，并在调用备注 `http_preserves_case_only_mismatch`。供应商是否接受或忽略该差异未验证，不在首版结论内。
5. **可重试异常**：适配器不产生可重试的 RuntimeModelClientException，因此 CLI 真实路径下不会出现同 Step 多次模型尝试；该行为用自定义 IChatClient 抛出类型化可重试异常的测试覆盖。

## 5. 支持的组合与未覆盖边界

- 支持（verified）：9 个默认 Provider 中，除 Gemini 仅 ChatCompletions 外，其余 Provider × {ChatCompletions, Responses, AnthropicMessages} 共 25 格；Gemini 的 Responses/Anthropic 两格为 unsupported。其他 SDK 版本全部降为 unverified。
- 未覆盖：真实供应商服务对照；Handler 之下的重定向、认证重试与网络重传；响应正文；TLS/代理之后的字节；`ChatClientExperimentalModelClient`；崩溃前未落盘的内存缓冲；macOS/Linux 权限与本次提交的跨平台诊断验收（CI 配置不等于成功证据）。
- 范围外已知问题（已登记为后续任务，未在本次修改）：Runtime 普通异常分支保留原始 `ex.Message`（E5）；共享 `ChatOptions` 的所有权/克隆语义；Runtime 接受大小写变体后将 `RequiredToolName` 解析为声明的 CanonicalName；SDK 注入客户端的所有权开关（E3）；SDK 流式文本结束原因（E6）；Gemini/DeepSeek 约束丢失（E9）。

## 6. 回退

诊断默认关闭；移除 `--sdk-diagnostics` 或传 `off` 即恢复原行为。诊断 sidecar 损坏、缺失或过期不影响既有审计回放与 checkpoint 恢复（已测试）。

## 7. R7 独立核查修复

核查对象为 `e3453df`，修复范围为诊断分析、读取、投影和队列，不改变模型调用或重试策略。

| 核查项 | 修复与回归证据 |
|---|---|
| 无别名映射仍判一致 | 未映射模型/工具身份生成 not_comparable finding 并进入总判定；显式映射后可比较 |
| 双方都缺结构仍通过 | 共同缺失、partial/omitted 结构及空运行都不能返回 identical；metadata CLI compare 实测 exit 2 |
| 右侧额外调用被忽略 | 同 segment 双向检查调用集合；显式 step-map 必须覆盖双方全部调用且不能多对一 |
| 尾部整行丢失仍 complete | 核对 RecordsWritten、正数且唯一递增的 Sequence、模型与 HTTP span 闭合；JSON 输出提供 recordCountMismatch、invalidSequences、incompleteLifecycles |
| 后续 HTTP 尝试漏比 | 每次 HttpAttemptId 均参与跨层分析；跨运行按 AttemptOrdinal 比较全部请求结构，尝试缺失、数量变化或序号不可用均显式报告 |
| null 与 absent 混淆 | 数值、响应格式和布尔标量保留 present(null)；错误数值类型标 invalid；redacted/unobserved/invalid 不当成参数丢失或验证通过 |
| 队列丢弃后计数泄漏 | Wait 模式配合非阻塞 TryWrite；队满返回 false 并归还字节预算；验证 5000 次提交的接受/写入/丢弃计数与排空后恢复 |

主要回归位于 `QreDiagnosticsEvidenceRegressionTests`、`QreDiagnosticsStorageTests.Store_RecordCapacityDropsAreAccountedAndDrainRestoresByteBudget` 和 CLI metadata 测试。

投影与比较版本分别更新为 `qre.outbound-projection/2` 和 `qre.outbound-normalizer/2`；事件与 manifest schema 不变。旧版本诊断包可读取，但不会被当作已具备新语义的可验证输入，也不会自动补齐历史丢失证据。

可复现命令：

```powershell
dotnet test CodexFlow.QueryRuntime.UnitTests -c Release --no-restore --verbosity quiet --logger "trx;LogFileName=patched.trx"
dotnet test CodexFlow.QueryRuntime.UnitTests --no-restore --filter "FullyQualifiedName~Diagnostics|FullyQualifiedName~RuntimeModelAttemptContractTests|FullyQualifiedName~OutboundTransportEquivalenceTests|FullyQualifiedName~SdkTransportCapabilityTests" --verbosity quiet
```

基线比较使用 `9c56da3` 的 Release 全套测试，TRX 分别为 baseline-release.trx、patched.trx。本轮不重新解释初版性能数据为新版本实测；文本结束原因 E6 仍是未满足的原始验收门槛，需后续 SDK 修复或单独明确调整验收范围。

R7 修复后的发布验证（win-x64）：

- `scripts/qre-aot-gate.sh win-x64 Release`：通过，无 trim/AOT 警告。
- `scripts/qre-aot-smoke.sh <原生 qre.exe>`：通过。
- Release 构建 `examples/SdkOutboundDiagnostics`：0 警告、0 错误。
- `QRE_EXAMPLE_CONFIGURATION=Release`、`QRE_BIN=<原生 qre.exe>` 下运行 `python scripts/test-examples.py Examples.test_sdk_outbound_diagnostics`：通过，实际执行原生 inspect/latest、export、rebuild，错误/正确 fixture 分别得到 exit 1/0。
- 本机原始日志与 TRX 保存在 `C:/Users/iwaitu/AppData/Local/Temp/qre-diagnostics-fix-validation/`；此目录不随仓库提交。

## 8. Windows 测试兼容性闭环（2026-09-27）

原 12 个失败均保留执行，没有增加平台 Skip 或以提前 return 代替测试：

- 环境隔离、注入、超时、输出截断以及 stdio/MCP 测试改用 `tests/ProcessFixture` 的 .NET 子进程，移除 /bin/sh、/usr/bin/env、head、tr 和 cmd 引号语义依赖。超时树测试启动真实子进程，并先确认其已启动，再验证超时后没有延迟写入。
- 运行目录断言使用当前平台的临时目录与 Path.Combine。
- CLI 临时 Git 仓库清理只清除测试目录内文件的 ReadOnly 位，再删除目录；不吞掉清理错误。
- 增加 windows-latest 全量单元测试 CI 作业；未将 CI 配置视为已在远端执行通过。

本机 Release 验证：受影响筛选测试 16/16 通过；全套 563/563 通过、0 失败、0 跳过。结果文件为上述验证目录中的 `windows-portable.trx`。本次只修改测试工程、fixture、CI 和文档，未修改生产行为；Linux/macOS 本轮未本地复现。

### 0.23.0 发布前的托管 Windows runner 修正

远端 CI 暴露了本机 PATH 中已有 rg.exe 掩盖的问题：UseShellExecute=false 无法直接执行 Windows 的 rg.cmd 替身。替身现使用 .NET 原生 apphost rg.exe，保留原有三项命令输出与隐私断言；发布工作流同时强制执行 Ubuntu 和 Windows 全量测试。修正后本机 Release 563/563 通过、0 失败、0 跳过，证据为 `release-023.trx`。

密钥扫描命中诊断测试的合成标记 CANARY-API-KEY-8d1e；配置仅豁免该完整精确值，保留默认规则和全历史扫描。发布前本机全历史 50 个提交扫描通过。托管 CI 结果仍需以发布运行记录为准。

首轮发布运行 `36255471492` 的 Ubuntu 测试、安全检查和 NuGet 打包通过，Windows 为 562 通过、1 失败：性能测试 Metadata p95 开销 1.152 ms 超过 1 ms 门限。该基准原先与其他测试并行，且统计进程级分配；现通过独占 xUnit collection 消除其他测试干扰，不跳过测试、不放宽原有门限。
