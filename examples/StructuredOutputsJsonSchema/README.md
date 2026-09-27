# Structured Outputs via JSON Schema Example (JSON Schema 结构化请求示例)

本示例演示如何在 QRE 与 `VllmChatClient` 体系中使用 **JSON Schema 结构化请求（Structured Outputs）**，并结合出站诊断探针验证语义传输的完整性。

## 能力定位说明

> [!NOTE]
> **底层驱动**：Logits 级别的严格文法状态机约束（Guided Decoding / Grammar-constrained Sampling）底层由 **`VllmChatClient`**（面向 OpenAI-compatible、vLLM、SGLang 等服务）驱动。
> **QRE 协同**：QRE 负责上层 Agent 循环编排、`QreThinkingPolicy` 思考策略协同，以及全链路出站诊断（Outbound Diagnostics）监控。

## 核心机制

1. **强约束 vs 弱提示**：
   - 传统 `ChatResponseFormat.Json` (`json_object`)：模型被告知输出合法 JSON，但无字段模式保障，可能出现字段缺失或类型错误；
   - 强模式 `ChatResponseFormat.ForJsonSchema(...)` (`json_schema`)：`VllmChatClient` 在采样解码层面使用状态机强行约束，保证生成的 Token 序列 100% 严格符合指定的 JSON Schema。
2. **出站诊断语义追踪**：
   - 使用 `QreSemanticProjector` 与 `QreOutboundDiagnostics` 追踪 `ChatOptions.ResponseFormat` 到传输层 HTTP Request Body，精准检测并防止字段被中间代理静默丢弃（`response_format_dropped`）。
3. **强类型 POCO 反序列化**：
   - 验证结构化输出能够无缝反序列化为 C# 强类型领域对象，提升工程稳健性。

## 运行方式

```bash
dotnet run --project examples/StructuredOutputsJsonSchema
```

## 目录结构

```text
StructuredOutputsJsonSchema/
├── StructuredOutputsJsonSchema.csproj
├── Program.cs
└── README.md
```
