# SDK 出站诊断

出站诊断把一次模型调用从 QRE Runtime 请求、宿主 optionsFactory、MEAI 适配器，一直关联到模型 SDK 交给 HTTP 的请求，用于定位约束在哪一层丢失、比较两次运行、导出不含密钥与正文的诊断包，并把案例沉淀为离线回归 fixture。设计与证据见 [ADR-009](adr/ADR-009-sdk-outbound-diagnostics.md)；英文版见 [sdk-outbound-diagnostics.md](sdk-outbound-diagnostics.md)。

诊断**默认关闭**。记录是客户端观察点：只说明经过诊断 Handler 的内容，不证明网络字节或服务端收包。

## CLI

```bash
qre run --sdk-diagnostics structure --api-url ... --model ... "任务"
qre diagnose latest --workspace . [--json]
qre diagnose inspect <run|bundle.zip> [--json]
qre diagnose compare <left> <right> [--step-map L=R] [--alias-map L=R] [--json]
qre diagnose export <run|latest> --output bundle.zip [--force]
qre diagnose skeleton <run|latest> --output fixture.json [--call mc-0001]
qre diagnose rebuild fixture.json [--json]
```

| 模式 | 采集 | 永不采集 |
| --- | --- | --- |
| `off`（默认） | 无：不建文件、作用域或正文包装 | - |
| `metadata` | 关联 ID、路由模板、状态码、Content-Type 类别、Retry-After、流终止方式、模型结果 | 请求正文 |
| `structure` | metadata 加 Runtime 请求、最终适配选项和已序列化 HTTP 正文的白名单投影 | 提示词、工具参数、响应正文、Header、URL、工具/模型明文名、异常消息 |

`qre run --json` 只新增可选的 `diagnostics` 摘要，已有字段语义不变；诊断失败不改变 run 的退出码。`diagnose` 只读且离线；`rebuild` 使用内存终端传输，永不重发请求。`compare` 与 `rebuild` 退出码：0 验证通过、1 发现非预期差异、2 输入无效/证据不足/能力不支持。

## 观察点与记录

| 阶段 | 时机 | 事件 |
| --- | --- | --- |
| `runtime_prepared` | 适配器收到 `RuntimeModelRequest` | `model_call_started` |
| `adapter_prepared` | optionsFactory 执行且 Tools/ToolMode 最终设置之后 | `adapter_prepared` |
| `http_prepared` | Handler 转发；正文在传输写入时旁路观察 | `http_attempt_started`、`request_structure_observed` |
| 响应 | 响应头与流结束 | `http_headers_received`、`http_attempt_ended` |
| 模型调用 | 适配器完成、失败或被放弃 | `model_call_ended` |

标识：`RuntimeModelAttemptOrdinal`（Runtime 权威值，或 `unavailable`）、`ModelCallId`（一次适配器 StreamAsync）、`HttpAttemptId` 与 `AttemptOrdinal`（每次 Handler 可见发送）、`RunAttemptAlias`、`SegmentId`、`StepAlias`。采集状态为 `complete`、`partial`、`omitted`、`unsupported`、`failed`、`not_enabled`；字段状态为 `present`、`absent`、`redacted`、`unobserved`。HTTP 成功、流完整结束与模型协议正常完成分开报告。

## 比较规则

跨层结论分为 `expected_transform`、`unexpected_change`、`not_comparable`、`insufficient_evidence`。只有两侧都完整观察且有规则支持时，才判定字段丢失。

| 情形 | 分类 |
| --- | --- |
| 无工具、无必选工具，适配后 `ToolMode.None` | expected_transform |
| 无工具但有必选工具，适配后 `ToolMode.None` | unexpected_change + `input_constraint_conflict` |
| 必选工具名与声明仅大小写不同 | unexpected_change + `case_only_mismatch`（Runtime → adapter） |
| 显式 Temperature/MaxOutputTokens/RequireJsonObject 未映射 | unexpected_change |
| `ModelId` 为空、SDK 使用等价的 descriptor 默认模型 | expected_transform；默认来源未知为 not_comparable |
| Temperature 由 double 转 float、在 float 精度内相等 | expected_transform |
| 已登记的 SDK 改写（见能力矩阵） | expected_transform |
| 已观察到的 SDK 丢失显式约束 | unexpected_change + `known_sdk_limitation` |
| 协议无法承载该约束（Anthropic 的 JSON 格式） | unexpected_change + `unsupported_constraint` |
| 未验证的 Provider × API 模式组合 | not_comparable |

`RequireJsonObject = false` 等同于未指定。工具与模型名是包内、区分大小写的别名；跨包别名需显式 `--alias-map` 才能比较。跨运行的“最早差异”不是已证明的根因。

## 能力矩阵（VllmChatClient 2.0.25）

所有被接受的组合：使用注入的传输；每次流式调用一次 Handler 可见发送；流式路径无重试；请求为延迟序列化的 `JsonContent` 且长度未知；SDK 会在释放 chat client 时释放注入的 `HttpClient`，并向其写入默认 Header；流式只上报工具调用的结束原因。

| Provider | chat_completions | responses | anthropic_messages |
| --- | --- | --- | --- |
| openai-gpt-oss | verified（注入系统提示） | verified | verified |
| openai-gpt、claude、kimi、minimax、glm、qwen | verified | verified | verified |
| gemini | verified；丢失 `max_tokens`、`response_format`、`tool_choice` | unsupported | unsupported |
| deepseek | verified；丢失 `response_format` | verified；丢失 `response_format` | verified |

Anthropic 组合在未指定时默认 `max_tokens` 为 8192，且没有 JSON 响应格式字段。其他 SDK 版本的所有组合均报告 `unverified`。把同一个 `HttpClient` 实例共享给 SDK **不受支持**；请改为共享 Handler。

## 嵌入

```csharp
var diagnostics = QreOutboundDiagnostics.Create(
    new QreOutboundDiagnosticsOptions { Mode = QreOutboundDiagnosticMode.Structure },
    mySink);                                         // IQreOutboundDiagnosticSink
var http = new HttpClient(diagnostics.CreateHandler(new SocketsHttpHandler()));
var selector = QreModelProviderSelector.CreateDefault();
var descriptor = new QreModelClientDescriptor { /* ... */ HttpClient = http };
var client = new MeaiRuntimeModelClient(
    selector.CreateClient(descriptor),
    request => new ChatOptions { /* 每次调用返回新实例 */ },
    diagnostics,
    QreOutboundDiagnosticsTarget.ForProvider(selector.Select(descriptor.Model), descriptor.ApiMode));
```

Sink 接收已投影的记录，不得阻塞。[examples/SdkOutboundDiagnostics](../examples/SdkOutboundDiagnostics) 提供 QRE 自建客户端、共享 Handler、自定义 `IChatClient` 三种接入方式，以及写出 CLI 诊断包契约的最小 sink。

## 存储与安全

`.qre/v2/diagnostics/<run>/` 包含 `manifest.json`（原子写入）、`events.jsonl`（逐条 flush，尾部半行报告为不完整）和 `local-index.json`（关联本机审计运行的受限文件，永不导出）。目录与文件仅当前用户可访问。默认：请求捕获 64 KiB、单条 32 KiB、单次运行 8 MiB、队列 256 条/2 MiB、JSON 深度 32、保留 7 天（上限 30 天）、总计 100 次运行/128 MiB。导出会对每条记录重新执行白名单投影并重新生成别名，只写 `manifest.json` 和 `events.jsonl`。读取器拒绝路径越界、链接、意外或超大条目、压缩炸弹和未知 schema。

## 已知限制

- 覆盖范围为 `handler_visible`：Handler 之下的重定向、认证重试和网络重传不可见。
- 不采集响应正文；没有诊断记录不等于没有发出请求；崩溃前的内存缓冲可能丢失。
- Runtime 与 CLI 仍可能输出原始供应商异常消息，而锁定版本 SDK 会把错误响应正文写入异常消息。诊断从不保存这些消息，但这不代表所有程序输出都已脱敏。
- 诊断无法消除共享、可变 `ChatOptions` 的并发竞争。
- 严格记录回放不调用 SDK，回放通过不能证明 SDK 序列化正确；请使用 `diagnose rebuild`。
- 首版未对 `ChatClientExperimentalModelClient` 接入诊断。
