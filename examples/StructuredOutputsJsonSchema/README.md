# Structured Outputs via JSON Schema Example (JSON Schema 结构化请求示例)

本示例演示如何在 QRE 与 `VllmChatClient` 体系中使用 **JSON Schema 结构化请求（Structured Outputs）**，并结合出站诊断探针验证语义传输的完整性。

## 能力定位说明

> [!NOTE]
> **底层驱动**：`VllmChatClient` 负责序列化 schema 请求；是否实施严格解码约束取决于服务端及模型。
> **QRE 协同**：QRE 负责上层 Agent 循环编排、`QreThinkingPolicy` 思考策略协同，以及全链路出站诊断（Outbound Diagnostics）监控。

## 核心机制

1. **强约束 vs 弱提示**：
   - 传统 `ChatResponseFormat.Json` (`json_object`)：模型被告知输出合法 JSON，但无字段模式保障，可能出现字段缺失或类型错误；
   - `ChatResponseFormat.ForJsonSchema(...)` (`json_schema`)：请求服务端按 schema 输出，客户端仍需验证。HTTP 200 和 `strict=true` 不等于后端实际强制执行了 schema。
2. **出站诊断语义追踪**：
   - 本例通过 HTTP handler 检查最终出站请求的 `response_format.type`、`strict` 与完整 schema。QRE 多边界诊断用法另见 `SdkOutboundDiagnostics`；诊断只能观察，不能阻止远端忽略字段。
3. **强类型 POCO 反序列化**：
   - 验证结构化输出能够无缝反序列化为 C# 强类型领域对象，提升工程稳健性。

## 运行方式

```bash
dotnet run --project examples/StructuredOutputsJsonSchema
```

上面是离线模拟。真实调用使用：

```powershell
$env:QRE_API_URL = 'https://dashscope.aliyuncs.com/compatible-mode/v1'
$env:QRE_MODEL = 'qwen3.8-flash'
$env:QRE_API_KEY = $env:VLLM_ALIYUN_API_KEY
dotnet run --project examples/StructuredOutputsJsonSchema -- --live --evidence-dir artifacts/live-examples/schema
```

也可传 `--endpoint`、`--model`；密钥建议放入 `QRE_API_KEY`。无参数运行不会因环境变量而触发真实 API 调用。

在线模式使用真实 `MeaiRuntimeModelClient`，校验状态、最终请求和输出的全部示例 schema 约束（对象根节点、必填字段、额外字段、类型、枚举、数组元素）。没有模拟结果回退；失败返回非零退出码。`--evidence-dir` 保存不含认证头的请求、原始响应和最终 JSON，包括不合格输出；响应缓冲会影响流式时序，不用于性能测试。

2026-09-27 使用此 endpoint 的 `qwen3.8-flash`、`enable_thinking=false` 连续实测 5 次，全部通过请求、schema 与业务断言。该系列在[阿里云官方 JSON Schema 支持列表](https://help.aliyun.com/zh/model-studio/qwen-structured-output)中。

此前 `qwen3.8-27b` 的失败样本属于未列入 JSON Schema 支持范围的模型，不能用来否定阿里云受支持模型的能力。详见 [真实 API 验证记录](../live-api-validation.md)。

## 目录结构

```text
StructuredOutputsJsonSchema/
├── StructuredOutputsJsonSchema.csproj
├── Program.cs
└── README.md
```
