# Embedded Structured Extraction（嵌入式结构化抽取示例）

本示例模拟“材料包规则审查”这类嵌入场景：宿主为一条规则发起一次带只读工具的会话，模型查阅文档后输出一份
符合 JSON Schema 的结论。它把 0.24 新增的 opt-in 能力组合在一起：

| 能力 | 本例用法 |
|---|---|
| `RuntimeAIFunctionTool` | 3 个宿主 `AIFunction`（`list_documents` / `open_document` / `find_keyword`，数据为内置样例）直接注册为 V2 工具，默认只读、幂等、可并行。 |
| `RuntimeModelParameters.ResponseFormat` | `JsonSchema` + `Scope = ToolFreeStepsOnly`：暴露工具的 Step 不带格式，无工具 Step 才带 schema。 |
| `ToolFinalization` | 工具调用达到 3 次或耗时达到 2 分钟的 85% 时，追加“禁止工具”的收尾 Step。 |
| `TerminationPolicy` + `ContinueWithoutTools` | 最终 JSON 不合法时，要求模型在禁止工具、带格式约束的 Step 中重新输出一次；仍不合法则 fail-closed。 |
| `ModelRetryPolicy` + `ClassifyProviderErrors` | 503/429/超时归类为可重试并按指数退避重试；401/403 直接终止。 |
| `StreamingWithNonStreamingFallbackOnEmpty` | 流式返回为空时，用非流式重发同一轮。 |
| `RuntimeContextOptions.ForModelWindow` + `FailClosedOnInitialMessages` | 放宽上下文预算，首批消息（规则与证据）被截断时拒绝而不是静默裁剪。 |
| `RuntimeJsonlAuditStore` | 审计写入宿主指定目录，`--trace-data` 选择数据级别；非 public 级别可做纯数据回放。 |

## 运行

```bash
dotnet run --project examples/EmbeddedStructuredExtraction
```

离线模式不访问网络：模型是挂在真实 `MeaiRuntimeModelClient` 之后的脚本化 `IChatClient`，按顺序返回
一次 HTTP 503、三轮工具调用（其中一轮先返回空流，再由非流式重发得到结果）、一个不合法的 JSON、以及修复后的 JSON。
程序逐项断言：错误分类与退避重试、空流降级、预算收尾、schema 只出现在无工具 Step、JSON 修复一次、审计中有收尾与
`continue_without_tools` 事件、回放结果一致。

可选参数：

| 参数 | 默认 | 说明 |
|---|---|---|
| `--audit-dir <dir>` | 系统临时目录下 `qre-embedded-extraction` | 审计根目录，文件位于 `<dir>/.qre/v2/runs/<run-id>/`（private 级别在 `.qre/v2/private/runs/`）。 |
| `--trace-data public\|sanitized\|private` | `sanitized` | `public` 只保存计数与分类（不可回放）；`sanitized` 保存完整载荷，适用于本例这样的合成数据；`private` 用于真实数据的本地诊断。 |
| `--live`、`--endpoint`、`--model`、`--key` | — | 与其他示例相同，见 [`../Shared/LiveExample.cs`](../Shared/LiveExample.cs)。在线模式只断言完成状态与 JSON 形状，是否触发收尾取决于模型。 |

在线示例：

```powershell
$env:QRE_API_URL = 'https://dashscope.aliyuncs.com/compatible-mode/v1'
$env:QRE_MODEL = 'qwen3.8-flash'
$env:QRE_API_KEY = $env:VLLM_ALIYUN_API_KEY
dotnet run --project examples/EmbeddedStructuredExtraction -- --live --trace-data private
```

在线模式的 `HttpClient` 由 `QreModelHttpErrorCaptureHandler.CreateHttpClient()` 创建：`VllmChatClient` 会把 HTTP
错误统一抛成不带状态码的 `InvalidOperationException`，该 handler 记录本次调用的状态码与 `Retry-After`，
分类器据此区分 429/5xx/401。

本例通过 `VllmChatOptions` 显式关闭 thinking 和自动 skill 注入，使用宿主声明的工具与 1024-token 输出预算。

## 目录结构

```text
EmbeddedStructuredExtraction/
├── EmbeddedStructuredExtraction.csproj
├── Program.cs
└── README.md
```
