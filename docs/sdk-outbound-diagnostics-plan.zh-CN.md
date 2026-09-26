# QRE 执行证据链与 SDK 出站诊断改造计划

- 状态：已实施（R6，2026-09-26）。实施结果、P0 证据与验收见第 18 节、[ADR-009](adr/ADR-009-sdk-outbound-diagnostics.md) 与[验收报告](sdk-outbound-diagnostics-acceptance-report.zh-CN.md)；下文保留 R5 设计原文，待验证项以第 18 节回填为准。
- 日期：2026-09-26。
- 修订：R5，补齐定稿前的演示响应序列、终态断言和工具搜索参数说明；仓库代码可直接确认的结论与第三方 SDK 待验证行为分开记录。
- 分析基线：仓库提交 `9c56da3`；实施时需重新确认目标版本。
- 目标：把一次模型调用从 Runtime 意图、适配结果到 HTTP 发送前请求关联起来，让故障可以定位、比较，并沉淀为离线回归案例。
- 本文出现的新增类型、命令、目录和配置在 R5 时为拟议设计；R6 已按第 18 节实现，命令形态已固定。

## 1. 目标与成功标准

用户应能针对一次失败回答：

1. QRE 准备了什么消息、工具约束和模型参数？
2. 宿主选项工厂与适配器如何改变这些内容？
3. SDK 生成的 HTTP 请求保留了哪些语义，在哪个观察点首次出现差异？
4. 请求发生了几次可观测的发送尝试，失败在响应头之前、流读取期间，还是协议解释阶段？
5. 修复后，同一测试输入是否重新生成了符合预期的请求？

首版成功标准：使用人工构造的非敏感输入，通过演示宿主中存在缺陷的 optionsFactory 触发参数漏映射，自动定位参数首次丢失的位置；导出不含密钥与正文的诊断包；将案例转换为无需真实网络的序列化回归测试。关闭诊断时行为兼容，开启时保持请求内容、流读取顺序、取消与重试语义。

## 2. 当前基础与证据缺口

| 当前入口 | 已有能力 | 本次需要补充 |
|---|---|---|
| `Engine/V2/RuntimeAudit.cs` | ModelRequestPrepared、ModelResponseCommitted 等稳定审计事件；Session/Turn/Step 和因果关联 | SDK 转换和 HTTP 尝试的独立观察记录 |
| `Engine/V2/RuntimeJsonlAuditStore.cs` | public 白名单摘要、sanitized/private 数据存储、配额与保留策略 | 出站专用字段投影、采集完整性和独立存储策略 |
| `Models/MeaiRuntimeModelClient.cs` | Runtime 消息转换、optionsFactory、工具声明和 ToolMode 设置、流事件转换 | 转换完成后且调用 SDK 前的最终选项快照；逻辑调用作用域 |
| `Models/MeaiRuntimeProtocolAdapter.cs` | Runtime 与 MEAI 消息/事件转换 | 可用于比较的安全语义摘要 |
| `Models/QreModelClientDescriptor.cs` 与 Providers | 可注入 HttpClient，并交给供应商客户端 | 受控的诊断 Handler 接入、覆盖能力声明与生命周期约定 |
| `Cli/Program.cs`、`QreVllmChatClientFactory.cs` | 模型构建、参数解析、运行和恢复组合 | 诊断选项、结果展示、检查/比较/导出命令 |
| `RuntimeRecordedReplay` | 无供应商和工具调用的记录校验 | 独立的离线 SDK 请求重建能力 |

表中的目录简称对应 `CodexFlow.QueryRuntime.*` 项目。

当前 Models 引用 Microsoft.Extensions.AI 10.10.0 与 VllmChatClient 2.0.25；首版以仓库锁定版本为验证对象，不顺带升级依赖。

关键事实和限制：

- Runtime 请求不是最终 HTTP 请求；optionsFactory、工具选项和 SDK 序列化都可能参与转换。
- 当前 public 持久化会移除原始会话、步骤和关联标识，不能靠公开文件中的原始 StepId 建立新关联。
- 当前存储代码对非 public payload 保留其内容。`SanitizedFixture` 名称不等于对任意生产请求执行自动脱敏，必须单独建立出站投影规则。
- 严格记录回放不调用 SDK；回放通过不证明新版 SDK 序列化正确。
- 注入 HttpClient 只是接入基础，尚需验证每个供应商、API 模式的实际覆盖，以及重试发生在哪一层。
- `RuntimeAgentLoop.SampleAsync` 在每次调用 StreamAsync 前增加 `ModelAttempts`；可重试的 RuntimeModelClientException、尚无流事件和预算条件共同决定是否重试。同一 Step 可以有多次模型尝试，不能与恢复 attempt 或 SDK 内部发送混为一谈。
- `RuntimeModelRequest` 当前不传模型尝试序号。关联方案需要有限的 Protocol 增量契约，见 4.3；不能靠适配器本地计数冒充权威序号。
- 当前适配器未把普通 SDK 异常转换成可重试的 RuntimeModelClientException。普通异常会由 Runtime 统一映射为不可重试的 `model_stream_failed` 并保留 `ex.Message`；已类型化的协议异常、流校验异常及调用方取消有各自分支，不能统称所有异常都被压平。
- CLI 的 v2 模型构建和保留的 Experimental 模型构建目前都没有向工厂传 HttpClient，虽然工厂提供了该可选参数。已全仓搜索确认该私有 CreateModelClient(QueryRuntimeProviderOptions) 仅有定义、没有调用方，从当前 CLI 入口不可达；不能将其列为已覆盖的主路径。
- GeminiModelProvider 声明仅支持 ChatCompletions，构造函数不传 ApiMode；能力清单必须覆盖 Provider × API 模式，并区分不支持与未验证。
- CLI 的 optionsFactory 已映射 Temperature、MaxOutputTokens 和 RequireJsonObject；演示宿主漏映射是特意构造的错误用法，不是已经确认的 CLI 缺陷。RuntimeModelParameters.Model 未映射到 ChatOptions.ModelId，但当前 CLI 构建请求与 descriptor 时使用同一模型配置来源，不能仅凭 ModelId 为空断定模型选错。
- AgentRuntime.RunAsync 和 ResumeAsync 均通过 PresentingModelClient 包装内层客户端；当前包装器只实现旧接口。它必须随新契约一起改造，否则主路径能力检测会被截断。
- RuntimeAgentLoop 的 ValidateRequest 会先检查必选工具是否存在于 request.Tools，不存在则抛 ArgumentException；后续 Step 门禁再检查 stepTools，缺失时使用 required_tool_omitted_from_context。因此当前 CLI 初始空工具加必选工具会在初始校验拒绝，不会到达适配器的 ToolMode.None 分支。

## 3. 范围与非目标

### 3.1 本次范围

- 模型 SDK 的 HTTP 出站诊断，默认关闭。
- QRE 请求、适配结果、HTTP 发送前请求三个观察点。
- 逻辑调用与可见 HTTP 尝试关联、响应元数据和流终止分类。
- 白名单脱敏、资源配额、独立诊断文件和安全导出。
- 跨层语义比较、跨运行比较、离线 SDK 请求重建、回归样例。
- v2 CLI 与 MeaiRuntimeModelClient 嵌入式接入；Native AOT 兼容。

### 3.2 不纳入首版

- 工具、MCP 和任意业务 HTTP 流量的全局抓取。
- 原始提示词、工具参数值、响应正文、推理正文的通用抓包模式。
- TLS 抓包、代理之后的网络字节证明、服务端收包证明。
- 自动重新发送失败请求、自动调用真实服务进行修复验证。
- 分布式恢复、远程诊断上传服务、独立可视化管理平台。
- 对历史 trace 自动补齐从未采集的请求内容。
- `ChatClientExperimentalModelClient` 的诊断接入不纳入首版。CLI 中保留的 CreateModelClient 私有工厂从当前入口不可达，首版只需明确不覆盖、不误报覆盖；不将此结论扩大为 Experimental 类型在任何外部宿主中都不可用。不删除该路径、不顺带迁移它。
- 核心 Runtime 原始异常消息治理、重试策略调整与共享 ChatOptions 并发隐患修复另列问题；本次仅对新诊断链路实施安全投影和明确分类。

## 4. 架构设计

### 4.1 保持核心依赖方向

遵循 ADR-003 和 ADR-004：Protocol 不引用 HTTP、MEAI 或具体 SDK；诊断不参与 Runtime reducer 的权威状态。

Models 下新增 `Diagnostics/`，仅容纳类型化 DTO、Sink 接口、投影器、调用作用域和 Handler，不引入文件系统存储、ZIP、比较或重建工作流。CLI 下新增独立 `Diagnostics/` 组件容纳 Store、Normalizer、Comparer、Rebuilder 与 Exporter；Program.cs 只接入命令路由和组合，不继续堆叠实现。

嵌入方通过 Models 的 Sink 接口接收已投影记录，可使用自己的存储；仓库示例给出最小安全 sink，并通过 CLI 读取符合契约的诊断包。若未来需要面向嵌入方发行标准文件存储，再拆独立可选包，不能让 Models 反向依赖 CLI。

新诊断通过独立 sidecar 文件关联既有审计，不往 `RuntimeAuditEventKind` 插入 HTTP 事件，不修改旧严格回放的必需事件序列。诊断缺失、过期或损坏不应阻止原有记录回放和 checkpoint 恢复。

### 4.2 三个观察点

| 阶段 | 采集时机 | 主要内容 |
|---|---|---|
| runtime_prepared | MeaiRuntimeModelClient 收到 RuntimeModelRequest 后 | 消息结构、工具约束、安全模型参数 |
| adapter_prepared | optionsFactory 执行且 Tools/ToolMode 最终设置完成后 | 实际传给 IChatClient 的消息结构与最终选项 |
| http_prepared / request_structure_observed | Handler 转发前捕获方法和路由；实际序列化写入时旁路观察正文，完成后生成结构投影 | 方法、允许的路由分类、协议形状和安全字段；结构可晚于发送开始产生 |

不要把 optionsFactory 刚返回的值误记为最终适配结果。采集使用不可变 DTO，不能保留可变 ChatOptions 引用，也不能对任意 AdditionalProperties 对象调用通用序列化或 ToString。

如果 optionsFactory 返回共享 ChatOptions，现有客户端会原地修改 Tools/ToolMode；这是既有并发隐患。不可变诊断 DTO 只能防止记录随后被改写，不能保证读取快照时没有竞争。示例要求每次返回独立实例；修复现有选项所有权或克隆语义另立任务，诊断不能宣称已消除该竞争。

### 4.3 关联与重试

- `RuntimeModelAttemptOrdinal`：Runtime 在同一 Step 中执行 RecordModelAttempt 后的权威 ModelAttempts 值。
- `ModelCallId`：一次实际 StreamAsync 调用；Runtime 重试创建新 ID，SDK 内部重试共享该 ID。
- `HttpAttemptId`、`AttemptOrdinal`：每次经过诊断 Handler 的 SendAsync 使用独立编号。
- `RuntimeRunAttemptId`：运行/恢复 attempt 身份，由宿主提供或标记 unavailable；不与模型尝试及 HTTP 重试序号混用。
- Session/Turn/Step 信息仅存在内存关联上下文或受限本地索引，公开导出使用包内局部别名。
- AsyncLocal 仅作为适配器调用 SDK 到 Handler.SendAsync 的桥接，不作为跨 yield、跨 MoveNextAsync 和后续响应读取的持久上下文。Handler 在 SendAsync 入口一次性捕获不可变关联快照，将其显式传给请求内容包装器、响应内容/流包装器和结束回调；后续不再读取 AsyncLocal。
- SDK 枚举推进时按需建立局部作用域，在异常、取消和退出时恢复父值。model_call_ended 使用适配器局部保存的上下文；http_attempt_ended 使用包装器绑定的上下文，并保证结束只记录一次。
- 共享 HttpClient 的并发调用必须隔离；无可用作用域时标记 uncorrelated，不能猜测归属。
- 自动重定向、底层认证重试或网络重传可能发生在 Handler 之下；记录 `attemptCoverage=handler_visible`，不能声称观察到了每个真实网络请求。
- 不向远端自动注入内部关联 Header；供应商响应 request ID 经投影后仅作辅助证据。

**P0 前确定的契约方向：选择由 Runtime 显式传递模型尝试信息，不采用客户端按 Step 自行计数。** 在 Protocol 新增 provider-free 的可选能力接口（拟名 `IRuntimeModelAttemptClient`，继承现有 IRuntimeModelClient），提供接收 `RuntimeModelAttemptContext` 的 StreamAsync 重载。Context 包含 RuntimeModelAttemptOrdinal，不包含 HTTP/MEAI 类型。旧接口和 RuntimeModelRequest 的构造、序列化形状保持不变。

Engine.SampleAsync 在 RecordModelAttempt 之后读取 reducer 状态，对支持该能力的客户端传入 Context；旧客户端继续走原接口，诊断中序号为 unavailable，不能默认填 1。MeaiRuntimeModelClient 实现新接口；直接通过旧接口调用时也明确标记缺少权威序号。模型重试判据、计数与预算均不改变。

**主路径透传是必改项，不是可选降级项。** `Engine/V2/AgentRuntime.cs` 中 PresentingModelClient 必须实现新接口：接收 Context 后，若 inner 支持新能力则原样透传，否则调用 inner 的旧接口。旧 StreamAsync 不伪造 Context。两种入口复用同一流展示逻辑，保持 StepStarted、增量事件、异常和取消行为，不重复枚举或重复发事件。RunAsync、ResumeAsync 都使用该包装器，必须同时覆盖；外层实现了接口不能被当成内层已接收 Context 的证据。

P0/验收必须从 `new AgentRuntime(attemptAwareClient)` 进入，在内层测试客户端断言实际收到权威序号及相同 Context，而不是只对 RuntimeAgentLoop 或 MeaiRuntimeModelClient 直连测试。旧 inner 的降级测试单独保留；首版 CLI 主路径不得以 unavailable 作为通过条件。

恢复后该序号只表示已恢复状态中的 ModelAttempts，不承诺覆盖崩溃前未持久化的尝试。同一序号跨恢复可能重现，唯一关联使用 RunAttempt/诊断 segment + Step + ModelCallId，序号只是可解释属性。P0 验证 checkpoint 恢复的实际计数边界，并覆盖故障注入，禁止宣称跨崩溃全局 exactly-once。

这是对原计划“Protocol 原则上不变”的明确例外：增加可选能力而不改请求与 checkpoint schema。P0 须完成旧客户端源码/二进制兼容、AOT、委托包装器能力传递及契约基线测试；不能透传能力的包装器必须降级为 unavailable。

### 4.4 HttpClient 接入与所有权

- CLI 创建自身拥有的 Handler/HttpClient，并明确释放顺序。
- P0 单独核实 MeaiRuntimeModelClient.Dispose → IChatClient.Dispose → 注入 HttpClient/Handler 的实际释放链，覆盖“SDK 自建”和“宿主注入”两种情况；当前只能确认适配器会 Dispose chatClient，不能据此断言共享 HttpClient 存活。
- 嵌入宿主在构建客户端管线时显式加入诊断 Handler；不能向已经构建的 HttpClient 事后插入 Handler。
- 宿主传入共享客户端时，不替换其代理、证书、超时、连接池、重定向和重试配置，不由诊断组件释放它。
- 外部 IChatClient、自定义传输或 SDK 未使用注入客户端时，展示 `transport_capture_unavailable`；适配阶段诊断仍可保留。
- 给每个已验证供应商/API 组合记录能力清单；未验证组合标记 unverified，不通过名称推断覆盖。
- 若锁定 SDK 会释放宿主注入客户端，先确定其是否提供受支持的所有权开关。没有安全接入方式则该共享客户端场景标为 unsupported，不静默绕过 Dispose，不临时换客户端改变宿主配置；所需 SDK 修复单列前置任务。
- Provider × API 模式矩阵逐格记录：选择器是否接受、实际路由、注入传输是否使用、流式路径是否重试、可见尝试范围、请求 Content 类型/长度行为、释放责任及证据。Gemini 的非 ChatCompletions 格应为 unsupported，并验证现有拒绝行为。
- 用户核查报告指出 SDK 使用延迟序列化 JsonContent，且工具格式修正重试位于非流式 GetResponseAsync。两项尚未在本轮直接核验依赖实现，均列入 P0；不能把非流式重试套用到 QRE 的 GetStreamingResponseAsync，也不能预设每次调用必有 SDK 重试。

## 5. 配置、数据契约与持久化

### 5.1 配置

拟议 `QreOutboundDiagnosticsOptions`：

| 字段 | 默认值/规则 |
|---|---|
| Mode | Off；可选 Metadata、Structure |
| MaxRequestCaptureBytes | 64 KiB，仅限制诊断捕获，不限制业务请求 |
| MaxRecordBytes | 32 KiB |
| MaxRunBytes | 8 MiB |
| MaxPendingRecords | 256，必须同时限制队列总字节 |
| MaxPendingBytes | 2 MiB |
| MaxJsonDepth | 32 |
| Retention | 7 天；首版上限 30 天 |
| MaxStoredRuns / MaxTotalStorageBytes | 100 / 128 MiB |
| ProjectionPolicyVersion | 必填、随发布锁定 |
| FailurePolicy | 首版固定 BestEffort；不得降低既有核心审计的 FailClosed 规则 |

以上容量为初始设计值，需根据测试调整。Off 不创建文件、作用域或正文读取包装。Metadata 不读取正文；Structure 也只输出经过白名单投影的结构。

### 5.2 诊断信封

拟议 `QreOutboundDiagnosticRecord` 至少包含：

- 独立 `SchemaVersion`、`ProjectionPolicyVersion`、`NormalizerVersion`。
- 文件内 Sequence、事件类型、UTC 时间和单调时钟耗时。
- RuntimeRunAttemptAlias（或 unavailable）、诊断 SegmentId、RuntimeModelAttemptOrdinal（或 unavailable）、ModelCallId、HttpAttemptId、AttemptOrdinal、包内 StepAlias。
- Stage、API 模式、投影后的 provider 分类、SDK/适配器版本。
- ObservationPoint、AttemptCoverage、CaptureStatus、ReasonCode。
- 类型化 Payload；禁止任意原始对象字典进入存储接口。

建议事件：model_call_started、adapter_prepared、http_attempt_started、request_structure_observed、http_headers_received、http_attempt_ended、model_call_ended。崩溃时缺少 ended 是合法的不完整证据，不伪造结束事件。

采集状态：complete、partial、omitted、unsupported、failed、not_enabled。字段状态另设 present、absent、redacted、unobserved；JSON 的 null 是 present 的一种值，不等于 absent。

响应记录仅包括经过白名单处理的状态码、Content-Type 类别、请求 ID、Retry-After、收到响应头耗时和流读取终态。区分 HTTP 成功、流完整结束与模型协议正常完成，HTTP 200 不能代表三者全部成功。

另设 `FailurePhase` 与 `ClassificationSource`：before_headers、response_read、adapter_protocol、runtime_validation、unknown，以及 handler、stream_wrapper、adapter、runtime 等来源。只有对应观察点证据足够时才细分阶段；不能从被压平的 `RuntimeError.Category=ProviderTransport` 或 `model_stream_failed` 反推真实传输错误。HTTP 状态失败、超时、调用方取消、SDK 解析失败等保持事实分类，不自动改变 Retryable，也不解析原始异常字符串猜测原因。拿不到 Runtime validator 的独立安全证据时应保留 unknown，不为了完善诊断重写核心错误语义。

特别覆盖 HttpClient.Timeout：其取消异常若发生时 Runtime 调用方 ct 未取消，不满足 Runtime 的取消过滤条件，会落入普通异常分支成为 model_stream_failed。适配器在推进 SDK 枚举前，将收到的 ct 句柄放入仅内存使用的关联上下文，由 Handler 捕获并传给包装器；发生异常时读取该 token 的 IsCancellationRequested，而不是只保存调用开始时通常为 false 的布尔值。这里的“原始 token”指相对于 HttpClient 内部链接 token 的上游 token，可能已经由 Runtime 合并用户取消与 TurnHandle 取消，不冒充最外层用户 token。token 不进入持久化 DTO、不被诊断组件取消；如注册回调，必须随调用结束释放。

.NET 5+ 的 HttpClient 超时具有类型证据：OperationCanceledException（常见具体类型为 TaskCanceledException）嵌套 TimeoutException。适配器可按 `ex is OperationCanceledException && ex.InnerException is TimeoutException` 分类，不读取 Message；这依据 [Microsoft HttpClient.SendAsync 文档](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.sendasync?view=net-10.0)。该包装可能在 HttpClient 外层形成，Handler 内尚不可见，所以由适配器补充安全的模型调用分类；不能把一次模型调用末尾的异常随意归给所有内部 HTTP 尝试。

Handler 仅凭收到的链接 token 或 TaskCanceledException 类型不能认定调用方取消；上游 token 未取消但缺少超时类型证据时为 unknown。若上游已取消且同时存在超时证据，报告竞态/不确定性；SDK 若改写异常导致类型链丢失，也保留 unknown。超时分类不改变 Runtime 的重试与终止语义。

### 5.3 存储

建议目录：`.qre/v2/diagnostics/<diagnostic-run-id>/`，包含：

- `manifest.json`：版本、采集能力、配额、覆盖范围、丢弃统计、完成状态。
- `events.jsonl`：仅包含已投影诊断事件。
- `local-index.json`：必要时关联本机 trace；受限本地文件，永不原样导出。

原有 trace/checkpoint 不引用 sidecar 为必需依赖。新目录默认使用当前用户权限保护；清理仅在专属目录内进行，防止路径穿越和链接跳转。Manifest 原子写入，进程中断后的完整 JSONL 行可读取，尾部半行报告损坏或不完整。

内容摘要只基于安全投影，用于完整性和比较；不能声称具有签名真实性。不要公开原始正文的普通哈希，避免低熵敏感内容被猜测。

## 6. 脱敏规则

### 6.1 白名单优先

- 永不记录 Authorization、Proxy-Authorization、Cookie、Set-Cookie、API key、URL userinfo。
- 不保留任意原始 URL、query、路径段；已知 API 归为路由模板，未知目标使用本地别名。租户名、部署名和模型自定义名称按敏感标识处理。
- 消息记录角色、条目类型和计数；默认不记录正文、正文片段或原文哈希。
- 工具名称使用包内别名，别名映射键必须使用 StringComparer.Ordinal 区分大小写，不执行 ToLower/ToUpper 或忽略大小写合并；同包内 QRE_READ_FILE 与 qre_read_file 必须产生不同别名，以免掩盖选择名与声明名不一致。仅在内存比较原值后可输出安全关系标记 case_only_mismatch，不输出明文或原文哈希；不保留工具描述、参数值、schema 的 description/example/default/enum 等可能承载业务内容的字段。
- 可记录经过审核的 temperature、max output tokens、stream、响应格式类别和工具选择模式等控制参数。
- 自定义扩展字段只识别已登记规则；未知字段最多报告数量或“存在未检查字段”，不直接公开字段名和值。
- Header、供应商错误码和异常信息按规则投影；禁止输出原始异常 Message，因为其中可能包含 URL、响应正文和密钥。

### 6.2 失败和缺失处理

解析失败、超限、multipart、二进制、压缩或不支持的流式请求：继续业务发送，仅输出 omitted/unsupported 和安全原因码。不保存原始片段作为兜底。

投影错误丢弃该条敏感输入；队列或磁盘故障不影响模型调用，但在结束摘要报告 evidence_incomplete。没有诊断记录不等于没有发出请求。

导出时再次执行白名单投影并重新生成局部别名，不仅扫描敏感词。诊断包不附带 checkpoint、private trace 或环境变量。若允许附带核心审计，只导出明确的 public 投影。

## 7. 流、取消和性能约束

禁止为了诊断无界调用 ReadAsStringAsync，也禁止提前读完 SSE 响应。首版响应体不采集。

Structure 模式优先对实际序列化写入进行有界旁路观察：向原传输写入时复制不超过上限的字节，完成后投影；超限只记录缺失原因。包装 HttpContent 必须保留 Headers、长度计算、取消传递、释放和重复发送语义。无法安全包装的类型退化为元数据。

旁路方案需先做原型验证：对照传输接收到的字节、Content-Length/分块行为、异常、重试和取消。若无法保持等价，不启用该内容类型的结构捕获，不采用“先全部缓冲再发送”的隐式替代。

针对核查报告中的 JsonContent：验证实际内容是否延迟序列化、TryComputeLength 是否为 false；若为 false，包装器必须继续返回 false，不能为诊断预计算长度或添加 Content-Length。分别测试 HTTP/1.1 的分块行为与其他 HTTP 版本的对应传输行为，不笼统声称所有协议均使用 chunked。验证读取/写入过程中抛错时只产生 partial 证据，响应包装器始终使用 SendAsync 时捕获的关联快照。

响应流如需包装，仅统计调用方实际读取的字节和结束状态，不抢先消费。区别 EOF、提前释放、取消和读取异常；模型解析失败在适配层单独记录。

诊断写盘使用有界队列；满时丢弃并计数。完成时进行有限时长 flush，超时标记不完整。崩溃前的内存缓冲不保证落盘，因此不能承诺完整抓取每次失败。

性能验收建议：固定环境预热后对同一无网络测试负载至少三轮测量；Metadata 的 p95 额外耗时目标不超过 max(1 ms, 基线的 5%)，Structure 不超过 max(2 ms, 基线的 10%)。记录分配量与峰值内存并验证受配置约束；阈值如需调整须在验收报告说明依据。

## 8. 比较能力

### 8.1 跨层语义比较

分别实现 Chat Completions、Responses、Anthropic Messages 的版本化规范化规则，将不同协议映射为共同语义：消息角色结构、工具集合/别名、工具选择模式、指定工具、输出限制、响应格式等。

输出每条差异的源阶段、目标阶段、字段路径、安全前后值、分类和证据引用。分类：expected_transform、unexpected_change、not_comparable、insufficient_evidence。

只能在采集完整且映射规则支持时判定字段丢失。脱敏、未采集和不支持不能算 absent。协议间合法转换不报故障；规则不支持的组合不能给出“完全一致”。

现有代码路径必须采用以下基线规则，不能以“代码一直如此”为由认定预期：

| 情形 | 分类 | 说明 |
|---|---|---|
| Tools 为空，RequiredToolName 也为空，适配后 ToolMode.None | expected_transform | 无工具时正常禁用工具 |
| Tools 为空，但 RequiredToolName 非空，适配后 ToolMode.None | unexpected_change，附 input_constraint_conflict | 输入约束相互冲突且必选工具要求被丢弃；定位到 Runtime → adapter，不归责于 SDK。本次报告问题，不修改既有行为 |
| 必选工具名与声明的 CanonicalName 仅大小写不同 | unexpected_change，附 case_only_mismatch | Runtime 门禁按 OrdinalIgnoreCase 接受，但 adapter 将原选择名与规范声明名原样交给 SDK；首次边界不一致定位 Runtime → adapter。表示约束匹配语义不一致，不声称适配器改写了名字或供应商必定拒绝 |
| 显式 Temperature/MaxOutputTokens/RequireJsonObject 未经 optionsFactory 映射 | unexpected_change | 只在两侧均完整可观测且规则支持时认定；针对显式约束，不把未指定值强行映射为默认值 |
| Runtime Model 有值，ChatOptions.ModelId 为空，SDK 使用 descriptor 默认模型 | expected_transform 或 not_comparable | 有完整默认模型来源且最终请求模型与意图等价时为 expected_transform；来源/优先级不明或缺证据则 not_comparable，不能仅凭 ModelId 为空报丢失。确证最终模型不同时才是 unexpected_change |
| Temperature 在 double → float 转换中发生受定义精度影响的变化 | expected_transform 或 not_comparable | P0 明确数值容差与表示规则，避免逐字节误报 |
| 参数本来未指定，或经明确登记的协议规则转为等价字段 | expected_transform | 需要映射规则证据，不凭供应商名称猜测 |
| 共享 ChatOptions 的并发修改 | insufficient_evidence 或 unexpected_change | 观察到确定冲突才报告差异；快照无法证明无竞态，不据此声称已查明根因 |

RequireJsonObject 是默认 false 的非 nullable bool，Runtime 层无法区分 false 与未指定；比较时二者等价，只有 true → 无格式要求才算此项约束丢失。协议不支持显式约束时报告 unsupported_constraint，不静默归为正常转换。

模型身份比较使用单次诊断包内的统一随机别名映射：Runtime Model、已知 descriptor 默认值、最终 HTTP model 在内存中按明确的 provider 身份规则比较，再投影为相同或不同的别名，模型明文与映射表不落盘、不导出，不使用普通哈希替代脱敏。各观察点不得各自生成无法对应的别名；映射资源受配额约束。P0 核实 SDK 的 ModelId/descriptor 优先级；自定义客户端未提供安全可用的默认来源时报告 not_comparable。跨包独立别名不能直接比较，需显式对应关系，否则仍为 not_comparable。

工具名大小写案例的源码证据：RuntimeAgentLoop 的初始请求校验与 Step 工具门禁均用 OrdinalIgnoreCase；RuntimeTurnProgress.NormalizeOptional 仅 Trim；MeaiRuntimeModelClient 使用原 RequiredToolName 构造 RequireSpecific，而工具声明 Name 取 descriptor.CanonicalName。当前内置读取工具是 qre_read_file（不是 read_file），所以 CLI 对照输入为 readonly profile 下的 QRE_READ_FILE 与 qre_read_file。规则比较采用大小写敏感别名及 case_only_mismatch 关系，不沿用 Runtime 的大小写不敏感比较器。

源码已确认差异到达 SDK 输入，但不能代替 SDK 序列化证据：离线真实 SDK 传输截获若保留大小写差异，则补充 HTTP 层选择名未精确匹配声明名的证据；若 SDK 做了规范化，则记录 adapter → HTTP 的修正，不宣称最终 HTTP 存在该差异。供应商返回 400、忽略约束或忽略大小写匹配均需另行实测，不纳入首版离线验证结论。

空 Tools + RequiredToolName 的适配规则仅用于直接调用适配器或其他未执行 Runtime 门禁的宿主。CLI `--required-tool <name>` 配合无工具 profile（例如工具集合确为空的 none）是另一条真实验收路径：应在 Runtime 初始 ValidateRequest 抛 ArgumentException，模型与 HTTP 调用次数均为零；只有通过初始校验后 Step 工具选择遗漏必选工具，才是 required_tool_omitted_from_context。诊断展示为 model_call_not_started；初始校验可能早于核心审计创建，应引用安全的入口校验结果，不伪造审计事件。存在 Step 审计错误时才引用该事件；不能把没有出站记录当作采集失败或构造并不存在的跨层差异。

### 8.2 跨运行比较

- 同一运行优先按关联 ID 对齐；不同运行按显式 Step 映射或调用顺序加安全结构特征进行候选匹配。
- 对齐有歧义时要求选择或报告无法对齐，不宣称找到了最早差异。
- 忽略随机 ID、时间和供应商请求 ID；保留数组顺序及 missing/null/默认值差异。
- 记录 SDK、适配器、策略和规范化器版本，禁止静默跨不兼容版本比较。
- 输出最早可观测差异；不把“首次差异”直接称为已证明根因。

同包内别名可比较；跨包工具别名无法稳定对应时，使用用户指定映射或报告不可比较，不能公开原始名称来换取便利。

## 9. 回放与修复验证

| 模式 | 输入 | 外部调用 | 证明范围 |
|---|---|---|---|
| 记录回放 | 既有可回放 audit | 无 | 记录结构、状态演进与约束 |
| 离线请求重建 | 审核后的完整非敏感 fixture | SDK 运行，但传输由内存测试终端替代 | 当前适配器/SDK 的请求构造 |
| 真实服务对照 | 显式选择的输入与服务配置 | 有 | 指定环境下的服务行为 |

首版完成前两项，真实服务对照留后续独立范围。离线终端无下游网络 Handler，不依靠错误 URL 或无效密钥阻止联网，并返回各 API 模式所需的最小合法模拟响应。

Fixture 包含：输入协议版本、SDK/适配器版本、API 模式、人工构造消息和工具定义、预期语义断言、可选安全请求快照、关联问题说明。

正文已删除的诊断包不能直接重建完整原请求。转换命令只能产生待补全样例骨架，列出缺失内容；人工补全并审核后才声明 runnable。Fixture 不自动包含写工具执行或生产凭据。

断言优先验证行为，例如“指定工具仍为必选”“工具结果调用 ID 保留对应关系”“JSON 输出模式被正确映射”，而不是只比较整段字符串快照。

## 10. CLI 与嵌入式体验

拟议命令形态，实施前在 P0 固定：

```text
qre run --sdk-diagnostics metadata ...
qre run --sdk-diagnostics structure ...
qre diagnose latest --workspace . --json
qre diagnose compare <left> <right> --json
qre diagnose export <run> --output <bundle.zip>
qre diagnose rebuild <fixture> --json
```

- 首版只提供显式参数/API 开启，不增加容易遗忘的全局默认开启设置。
- `diagnose` 默认仅查看；rebuild 明确离线；不得隐式联网或重发请求。
- run/resume 的业务退出码不受 BestEffort 诊断写入失败影响；摘要单列诊断状态。
- compare/rebuild 单独定义退出码：0 验证通过，1 发现非预期差异，2 输入无效/证据不足/能力不支持；JSON 保留细分 reasonCode。
- 原 run 的 JSON 仅增加可选 diagnostics 摘要，不改变已有字段语义。
- 恢复时使用新的诊断 segment/call ID 并关联恢复 attempt；不开启诊断不影响旧 checkpoint 的兼容性。
- 帮助输出必须说明“客户端观察点”“覆盖范围”和“缺失证据”；不显示未采集字段为成功。

嵌入式文档分别提供：QRE 自建 HttpClient、宿主共享 HttpClient、自定义 IChatClient 三种示例。第三种明确说明仅能自动获得的观察层级。

## 11. 分阶段任务与交付

| 阶段 | 工作内容 | 交付物 | 退出条件 | 预估人日 |
|---|---|---|---|---|
| P0 契约与原型 | 可选模型尝试契约、恢复边界、SDK 释放责任、逐 Provider/API 覆盖、跨 yield 关联、Content 包装；制定威胁模型 | 新 ADR、契约草案、能力矩阵、传输原型验证报告 | 权威尝试计数和关联可证；共享客户端与不支持路径有明确结论 | 3–5 |
| P1 元数据链路 | DTO、调用作用域、Handler、CLI 存储/配额、开关、状态摘要 | 可选 Metadata 诊断 | 超时、取消、重试、写盘失败与关闭模式验收通过 | 4–5 |
| P2 结构与脱敏 | 三层投影、三类 API 提取规则、限额、公开导出 | Structure 诊断与安全包 | 新诊断链路无敏感标记落盘；不同缺失状态可识别 | 6–9 |
| P3 对照定位 | 三类协议语义规范化、跨运行对齐、差异解释和证据引用 | diagnose/compare 命令与报告 | 合法转换不误报；能定位演示宿主漏映射及直接适配器输入约束冲突 | 5–7 |
| P4 离线验证 | 内存传输、fixture 格式、补全骨架、重建断言 | rebuild 命令、回归样例 | 零真实网络；错误宿主映射失败、正确映射通过 | 3–5 |
| P5 发布收口 | AOT、兼容、性能、安全回归，示例与文档 | 验收报告、发布说明、使用示例 | 全部门禁通过、限制清楚、可通过 Off 回退 | 3–4 |

修订后合计约 24–35 人日，按单人顺序实施估算，P2/P3 已为三套协议和比较规则预留缓冲；不含第三方 SDK 缺陷修复、核心错误消息治理、真实服务矩阵和新增 UI。P0 后依据逐格能力矩阵重新估算，不能将此估算当作交付承诺。Program.cs 现有体量较大，命令实现拆分计入 P1/P3。P1 可发布元数据预览，但不能宣称完整证据链已经完成。

P0 开始前的设计门槛（本修订已明确方向）：权威模型尝试信息由 Runtime 显式传递；错误阶段依赖独立观察而不是 RuntimeError 反推；Handler 捕获上下文后显式绑定包装器。P0 用原型验证这些决定，若验证失败则先修订 ADR 和范围，不能回退为不可靠的本地计数或跨 yield 隐式上下文。

P2 负责协议字段提取与脱敏，P3 复用提取结果做语义规范化和比较，共享版本化字段规则与 fixture，避免两阶段独立维护两套冲突的映射表。首版先完成 ChatCompletions 纵向链路，再扩展 Responses、AnthropicMessages；未通过逐格验证的组合不标为支持。

每阶段独立可审查提交：先契约/原型，再元数据，再结构/导出，再比较，再离线重建，最后发布文档。一个阶段未满足退出条件，不用增加宣传范围弥补。

## 12. 文件改造清单

| 路径 | 计划修改 |
|---|---|
| `CodexFlow.QueryRuntime.Protocol/ModelContracts.cs` | 新增可选尝试感知接口和 provider-free Context；保留旧接口和请求序列化形状 |
| `CodexFlow.QueryRuntime.Engine/V2/RuntimeAgentLoop.cs` | RecordModelAttempt 后按客户端能力传递权威序号；不改变重试条件 |
| `CodexFlow.QueryRuntime.Engine/V2/AgentRuntime.cs` | PresentingModelClient 实现可选接口并向内层透传；同时覆盖 RunAsync/ResumeAsync，复用展示逻辑 |
| `CodexFlow.QueryRuntime.Models/Diagnostics/`（新增） | Options、类型化记录、Sink 接口、Scope、Projection、Handler；无存储/ZIP/比较工作流 |
| `CodexFlow.QueryRuntime.Models/MeaiRuntimeModelClient.cs` | 可选诊断依赖、最终选项投影、全枚举生命周期、模型终态 |
| `CodexFlow.QueryRuntime.Models/QreModelClientDescriptor.cs`、Providers | 仅在必要时补接入信息；保持已有构造与 HttpClient 注入兼容 |
| `CodexFlow.QueryRuntime.Cli/Program.cs` | 参数、run/resume 组合、独立 diagnose 路由和结果 |
| `CodexFlow.QueryRuntime.Cli/QreVllmChatClientFactory.cs` | CLI 拥有的诊断管线构建与客户端传递 |
| `CodexFlow.QueryRuntime.Cli/QreDiagnosticsCommands.cs`（新增） | 查看、比较、导出和重建，避免继续堆叠 Program.cs |
| `CodexFlow.QueryRuntime.Cli/Diagnostics/`（新增） | Store、协议 Normalizer、Comparer、Rebuilder、ZIP Exporter |
| `CodexFlow.QueryRuntime.UnitTests/Models/Diagnostics/`（新增） | 投影、关联、传输等价、包装器资源限制 |
| `CodexFlow.QueryRuntime.UnitTests/Cli/` | 存储、比较、重建、导出、命令、JSON 与退出码、参数兼容 |
| `CodexFlow.QueryRuntime.UnitTests/Protocol/` 与 RuntimeAgentLoopTests | 可选契约兼容、同 Step 多模型尝试、旧客户端降级、恢复计数边界 |
| `CodexFlow.QueryRuntime.UnitTests/Runtime/AgentRuntimeFacadeTests.cs` | 经 facade 新运行/恢复的 Context 实收验证、旧 inner 降级、展示事件无重复；现有 ScriptedModelClient、BlockingModelClient 保持旧 IRuntimeModelClient 不变作降级用例，新增独立 attempt-aware 替身验证透传 |
| `CodexFlow.QueryRuntime.UnitTests/Runtime/RuntimeAuditTests.cs` 等 | 验证新诊断不改变既有回放和恢复保证 |
| `CodexFlow.QueryRuntime.IntegrationTests/` | CLI 端到端、磁盘故障、发布产物测试 |
| `examples/SdkOutboundDiagnostics/`（新增） | 三类宿主接入和人工构造故障案例 |
| `docs/adr/`、README 双语、SECURITY、技术指南 | ADR、能力边界、脱敏与使用说明 |

所有新增 JSON 契约使用显式类型和 source-generated serializer；不依赖任意反射序列化 SDK 对象。Protocol 仅做 4.3 定义的可选能力增量，核心 reducer、RuntimeModelRequest 序列化形状和 checkpoint schema 保持不变；其他变动需另行说明必要性和迁移方案。

## 13. 验收矩阵

| 类别 | 必测情形 | 合格判据 |
|---|---|---|
| 关闭兼容 | Off、旧构造方法、旧 CLI | 不生成诊断数据，不读取正文；现有调用行为保持 |
| 关联 | 共享客户端并发、嵌套作用域、多次 MoveNextAsync、跨 yield 后读流、提前 Dispose | 响应包装器绑定原调用；无串线/泄漏；无法关联有标记 |
| 模型尝试 | 经 AgentRuntime facade 的 RunAsync/ResumeAsync 调用、同 Step 两次可重试尝试、旧客户端/包装器 | 主路径内层实收权威序号且与 reducer 一致；旧客户端仍可用，不可得显式 unavailable；跨恢复不假定唯一 |
| 错误阶段 | 头前失败、读流失败、解析失败、HttpClient.Timeout、调用方主动取消及两者竞态 | 超时不误标为调用方取消；按独立证据区分，无证据 unknown；不改变重试语义 |
| 所有权 | SDK 自建/注入 HttpClient；Dispose 模型客户端后复用共享客户端 | 锁定依赖实际行为有证据；不安全场景拒绝声明支持 |
| 尝试 | 可见 SDK 重试、重定向、连接失败 | 次序正确；不夸大底层重试覆盖 |
| 请求等价 | 延迟 JsonContent、TryComputeLength=false、超限、未知长度、不可 seek、重试发送 | 字节、长度属性及各 HTTP 版本传输语义等价；不提前缓冲 |
| 流行为 | SSE 多片段、慢消费、取消、断流、早释放、协议缺终止标志 | 不预读、不吞数据；HTTP/流/模型终态分开 |
| 脱敏 | Header/URL/正文/schema/错误字符串中植入唯一敏感标记 | 新诊断文件/导出/诊断命令输出均无标记；既有 Runtime/CLI 原始错误单列已知风险，不谎称全程序已脱敏 |
| 缺失语义 | absent/null/redacted/unobserved、解析失败 | 不误判为参数丢失或一致 |
| API 规则 | 三种 API 模式、合法转换、未知字段 | 已支持语义正确；未知显式不可比较 |
| 比较 | 空 Tools + RequiredToolName、宿主漏映射、数值精度、数组重排 | 按 8.1 分类；缺证据不报验证通过；不自动修正原输入 |
| 模型身份 | ModelId 空但 descriptor 等价、默认来源未知、最终模型不同、跨包别名 | 等价回退不误报；未知不可比较；明文不落盘 |
| 调用前拒绝 | CLI 初始空工具 + --required-tool；初始有效但 Step 选择遗漏必选工具 | 前者为初始 ArgumentException，后者为 required_tool_omitted_from_context；模型/HTTP 零调用；不伪造尚未创建的审计 |
| 工具名大小写 | readonly + QRE_READ_FILE，对照 qre_read_file；真实 SDK 离线截获 | Runtime 门禁通过且 adapter 差异可见；别名不合并；HTTP 是否保留差异按捕获事实报告，不假设供应商响应 |
| 超时类型证据 | 上游 ct 未取消且取消异常内嵌 TimeoutException、无该类型证据、取消竞态 | 适配器读上游 token 并按异常类型分类；Handler 看不到类型链时允许模型层补充，未知不猜测 |
| 重建 | 合法 fixture、不完整骨架、SDK 版本变化 | 完整样例可离线断言；不完整明确阻断；无真实网络 |
| 资源 | 队列满、磁盘满/无权限、配额、深 JSON、大请求 | 有界资源；调用继续；诊断不完整可见 |
| 输入安全 | 恶意路径/压缩包、过大条目、未来 schema | 拒绝越界、压缩炸弹与不支持版本；不执行内容 |
| 兼容恢复 | 老 trace、无 sidecar、损坏 sidecar、旧 checkpoint | 旧回放/恢复独立可用；诊断故障只影响诊断 |
| 发布 | Native AOT、目标平台文件权限、性能对照 | 诊断功能可运行，无新增未解释警告；性能达标 |

测试优先沿用现有 UnitTests 和 IntegrationTests 工程。先运行受影响测试，阶段收口再运行完整相关套件；新增代码后完成构建及项目要求的发布检查。本计划编写本身不代表这些测试已经执行。

## 14. 风险、退路与最终验收

1. **SDK 不经过注入 HttpClient**：P0 用真实锁定依赖和内存终端验证；不能覆盖就标明 unavailable，必要的上游 SDK 修改另列任务。
2. **诊断包装改变传输**：以字节和行为等价为硬门槛；不安全的类型仅保留元数据。
3. **脱敏降低可复现性**：明确区分诊断包与可运行 fixture；用人工非敏感样例补全，禁止把生产正文自动复制进测试库。
4. **比较误报**：规则版本化、明确证据充分性与对齐状态；无法比较时不出具一致结论。
5. **诊断影响主流程**：独立 BestEffort、队列与配额；关闭即可回退，保持核心审计原有失败策略。
6. **既有错误消息外泄（范围外已知问题）**：SampleAsync 的普通异常分支直接把 ex.Message 写入 RuntimeError。public 审计只投影 ErrorCode，但非 public 审计和可能打印 Message 的 CLI 路径存在暴露原始异常文本的风险。SDK 消息是否包含响应正文（核查报告提到 ReadAsStringAsync）及具体 CLI 输出链在 P0 用非敏感标记验证。新诊断禁止复用原始 Message；本次不修改核心错误契约。独立登记后续治理任务，不把“诊断包已脱敏”宣传成“所有程序输出已脱敏”。
7. **历史语义混淆**：同步修正文档中容易被理解为“自动脱敏”或“回放验证 SDK”的表述，不改变历史数据含义。
8. **共享选项竞态（范围外已知问题）**：optionsFactory 返回共享 ChatOptions 时的原地修改不是诊断 DTO 能修复的；示例使用每调用独立实例，并登记单独的所有权/复制策略修复任务。

最终演示使用非敏感 fixture，通过演示宿主中存在缺陷的 optionsFactory 触发参数漏映射：Runtime 显式设置 MaxOutputTokens/RequireJsonObject → optionsFactory 漏映射 → 捕获三层证据 → 报告 Runtime → adapter 首次丢失 → 导出安全包 → 创建完整 fixture → 离线验证遗漏映射的宿主失败、正确映射的宿主通过。只在演示宿主中修正映射，不扩散修改运行行为。

第二个演示使用仓库真实的工具名大小写路径：`--profile readonly --required-tool QRE_READ_FILE --max-rounds 3`，对照 `--profile readonly --required-tool qre_read_file --max-rounds 3`。不传 `--tool-search`（默认关闭），也不传会同时开启搜索的 `--tool-search-top-k`，以固定声明集合；不存在需要指定的关闭参数。通过 v2 CLI/AgentRuntime 主链、锁定版本 SDK 与离线测试传输运行；测试宿主只提供传输替换入口，不改工具校验和映射逻辑，不用 StaticRuntimeModelClient 绕开 SDK。若 CLI 离线注入入口尚未实现，先标记演示未完成，不以直连适配器测试冒充 CLI 验证。

Fixture 必须提供以下三次有序模型响应，不能反复返回一个最小文本响应：

| 模型调用 | 离线传输返回内容 | 预期 Runtime 行为 |
|---|---|---|
| 第 1 次 | 固定非敏感文本，不含工具调用，以该 API 的正常完成标志结束 | 必选工具尚未满足，注入要求调用必选工具的继续消息，进入下一 Step |
| 第 2 次 | 一个结构化工具调用，名称严格为 `qre_read_file`，固定唯一调用 ID，参数指向临时 workspace 内预先创建的 `fixture.txt`；使用合法工具调用结束标志 | 实际执行只读工具且成功；OrdinalIgnoreCase 满足性判断令大小写两组输入均达到 RequiredToolSatisfied=true；提交工具结果后进入下一 Step |
| 第 3 次 | 固定最终文本 `fixture completed`，无工具调用，以正常完成标志结束 | 接受最终答案并正常完成，不再注入必选工具继续消息 |

测试前创建非敏感 `fixture.txt`，验证 readonly 工具策略允许读取，且工具结果必须成功；仅模型返回工具调用并不足以满足必选工具条件。API-specific fixture 应完整包含 SDK 所需的流片段、调用 ID、工具参数和结束标志，不能依赖“流结束但没有 finish reason”的降级路径。设置足够的 token/时间预算和至少一次 continuation 预算，禁用额外宿主 steering 与自定义终止策略；HTTP 模拟响应均正常成功，避免错误重试干扰计数。

两组大小写输入分别独立运行，固定断言：`Status=Completed`、`TerminationReason=Completed`、`Error=null`、最终文本为 `fixture completed`；模型调用恰好 3 次、Step 恰好 3 个、每 Step 的 ModelAttempts=1、成功工具调用恰好 1 次、ContinuationCount=1、RequiredToolSatisfied=true。第三次请求应带有第二次调用对应的工具结果，RequiredToolName 已清除，不再强制指定工具；fixture 拒绝第 4 次请求，不能循环复用最后一条响应来掩盖意外续轮。正常样例不应以 MaxSteps 或其他预算耗尽终止。

模型调用次数与 HTTP 次数分别计数：选定并经 P0 确认“一次流式调用对应一次可见发送”的 SDK/API 组合时，进一步断言 HTTP 尝试恰好 3 次；其他组合按已验证矩阵设置期望，不能把模型次数当作 HTTP 次数。正常完成只证明本地 fixture 的执行路径收敛，不能证明真实供应商接受大小写不匹配的工具选择。

演示验收为 Runtime 接受大小写变体、adapter 产生 case_only_mismatch、工具别名保持不同，并按实际截获结果说明 HTTP 是否保留差异。首次数据比较应无需真实供应商调用；人工构造的是安全输入，缺陷来自仓库既有匹配语义，不必在演示中植入缺陷。另立“门禁接受后将 RequiredToolName 解析为声明的 CanonicalName”修复任务，届时单独评审唯一匹配、恢复、工具搜索和历史记录兼容性，本计划不直接修复。

另外通过直接调用适配器演示 Tools 为空且 RequiredToolName 非空的冲突，按 8.1 报告 unexpected_change；CLI 则用无工具 profile 加 --required-tool 覆盖 Runtime 初始 ArgumentException 提前拒绝；Step 选择遗漏必选工具的错误码另测 required_tool_omitted_from_context，断言模型和 HTTP 均未调用，不声称 CLI 会静默发送 ToolMode.None；再展示证据不足案例，确保缺失记录不能充当验证通过的证明。保留人工植入差异作为单元测试方法，但不把它作为唯一端到端价值证明。

发布材料必须列出支持的 SDK/API 组合、已执行测试、性能结果和未覆盖边界。达到此标准后，才对外声明具备“从 Runtime 输入到 SDK 出站请求的可对照诊断能力”。

## 15. R2 核查意见闭环

| 核查意见 | 处理决定 | 位置 |
|---|---|---|
| 1. 缺 Runtime 模型尝试层 | 接受；可选 Protocol 能力由 Runtime 显式传序号，禁止本地猜测 | 4.3、11–13 |
| 2. 错误压平与 Message 风险 | 接受并限定普通异常分支；独立证据分类，既有泄露风险单列范围外 | 2、5.2、14 |
| 3. AsyncLocal 跨 yield | 接受风险；SendAsync 捕获快照并显式绑定包装器，原型验证执行上下文边界 | 4.3、7、13 |
| 4. CLI 未注入、Provider/API 差异 | 已核实仓库调用；新增逐格能力矩阵，Gemini 明确不支持格 | 2、4.4 |
| 5. Experimental 第二路径 | 明确首版不覆盖；R3 已确认 CLI 私有工厂无调用方，不误报覆盖 | 3.2 |
| 6. 客户端释放责任 | SDK 细节未确认；设 P0 所有权硬门槛 | 4.4、13 |
| 7. JsonContent 与 SDK 重试 | 作为第三方依赖待验证项；保留长度语义，流式/非流式分别核实 | 4.4、7 |
| 8. 现有丢失点与共享选项 | 定义差异规则，用真实代码路径演示；共享选项竞争单列 | 4.2、8.1、14 |
| 9. 组件放置 | 接受；Models 保持采集核心，存储/比较/重建/导出放 CLI | 4.1、12 |
| 10. 工期 | 接受；调整为 24–35 人日，P2/P3 留缓冲，P0 后重估 | 11 |

本轮仅核对仓库源码并修订计划，未运行 SDK 行为实验、未执行功能测试，也未修复上述范围外问题。P0 产生的证据应回填本文与 ADR，替换对应待验证项。

## 16. R3 核查意见闭环

| 核查意见 | 处理决定 | 位置 |
|---|---|---|
| 1. PresentingModelClient 吞掉接口 | 已核实；列为主路径必改文件，Run/Resume 透传和 facade 实收测试为硬门槛 | 2、4.3、12–13 |
| 2. 漏映射案例及模型身份 | 更正为有缺陷的演示宿主；补充默认模型来源、包内别名与不可比较规则 | 1、2、8.1、14 |
| 2. 空工具可从 CLI 到达适配器 | 部分不采纳：CLI 可提交该冲突，但 Runtime 在调用模型前已拒绝；分别测试 Runtime 门禁与直接适配器分支 | 2、8.1、13–14 |
| 3. Experimental 可达性 | 全仓静态搜索确认 CLI 私有工厂仅有定义；移除 P0 待核实项 | 2、3.2 |
| 4. RequireJsonObject 状态 | 明确 bool 默认 false，与未指定等价；仅 true 丢失为该约束丢失 | 8.1 |
| 5. 超时归入普通异常 | 明确 Runtime 分支，区分原始与链接 token；证据不足或竞态不强判 | 5.2、13 |

R3 仅进行源码核对和计划修订，未执行功能测试。CLI 调用前拒绝结论来自 RuntimeAgentLoop 的前置检查，不冒充已运行验证；R4 进一步纠正：初始空工具先触发 ValidateRequest 的 ArgumentException，后续 Step 门禁才使用 required_tool_omitted_from_context。

## 17. R4 核查意见闭环

| 核查意见 | 处理决定 | 位置 |
|---|---|---|
| 工具名大小写不一致 | 已核实 Runtime → adapter；新增规则和 CLI 真实案例，实际内置名使用 qre_read_file；HTTP/供应商行为待验证 | 6.1、8.1、13–14 |
| 别名掩盖差异 | 工具别名按 Ordinal 映射，保留 case_only_mismatch 安全关系 | 6.1 |
| 上游 token 与超时类型证据 | 适配器捕获 token 句柄、异常时读状态；用取消异常内嵌 TimeoutException 作证据，Handler 不可见时由适配器补充 | 5.2、13 |
| 旧测试替身 | 保持 ScriptedModelClient/BlockingModelClient 旧接口作为降级用例，新增替身测试新能力 | 12 |

本次只修改计划。大小写路径未实际执行 CLI/SDK 截获或供应商请求，不能将源码判断视为端到端测试通过。

R4 同时细化了 R3 的空工具案例：初始校验与 Step 校验是两条不同拒绝路径，验收分别检查异常类型与错误码，不能将后者的错误码套给前者。

## 18. R6 实施闭环与 P0 证据回填

实施提交：P0 契约、Models 诊断核心、CLI 存储与 diagnose 命令、输入加固、示例与离线检查、文档。命令形态固定为 `qre run --sdk-diagnostics off|metadata|structure` 与 `qre diagnose latest|inspect|compare|export|skeleton|rebuild`（第 10 节的拟议形态加上 `inspect` 与 `skeleton`）。

| 原待验证项 | 回填结论 | 证据 |
|---|---|---|
| 注入 HttpClient 是否覆盖每个 Provider/API（2、4.4） | 所有被选择器接受的格均经注入传输、每次流式调用一次可见发送；Gemini 非 ChatCompletions 两格被选择器拒绝，标 unsupported | ADR-009 E1；`SdkTransportCapabilityTests` |
| MeaiRuntimeModelClient.Dispose → 注入 HttpClient 的释放链（4.4） | 锁定 SDK 会释放注入 HttpClient 并写入默认 Header；共享 HttpClient 场景 unsupported，改为共享 Handler | E3 |
| 延迟序列化 JsonContent 与 TryComputeLength（4.4、7） | 确认为 JsonContent 且长度未知；包装器保持 HTTP/1.1 chunked、HTTP/2 无 Content-Length 与字节等价 | E2；`OutboundTransportEquivalenceTests` |
| 工具格式修正重试位置（4.4） | 仅在非流式 GetResponseAsync；流式路径 HTTP 500 只有一次发送 | E4 |
| SDK 消息是否包含响应正文（14.6） | 是：错误体进入 InvalidOperationException.Message；诊断从不保存，Runtime/CLI 原始消息风险单列 | E5 |
| 流式结束原因 | 仅工具调用上报；文本响应一律走 missing_finish_reason 降级（与第 14 节 fixture 期望不同，见验收报告第 4 节） | E6 |
| 大小写差异是否到达 HTTP（8.1） | 是：SDK 原样保留 `QRE_READ_FILE` | E7；CLI 大小写演示 |
| 恢复后的计数边界（4.3） | StepPrepared checkpoint 持久化 ModelAttempts=0，恢复后同一 Step 以序号 1 重新采样；序号跨恢复重现 | E10 |
| 数值容差（8.1） | 以 float 精度相等为 expected_transform（double_to_float_precision） | `TemperaturePrecisionAndNestedPathAreExpectedTransforms` |

范围外问题按第 3.2、14 节保持未修改，并在验收报告第 5 节列为后续任务。

