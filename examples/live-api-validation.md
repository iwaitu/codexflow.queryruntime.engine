# Skills / JSON Schema 真实 API 验证

日期：2026-09-27。基于提交 `7ecba73`，SDK `VllmChatClient 2.0.25`，.NET 10。

## 最新结论：官方支持模型验证通过

根据 2026-09-27 查阅的[阿里云结构化输出官方文档](https://help.aliyun.com/zh/model-studio/qwen-structured-output)，JSON Schema 支持 Qwen3.7-Plus、Qwen3.8-Flash、Qwen3.7-Flash、Qwen3.7-Max、Qwen3.8-Max 系列。`qwen3.8-27b` 属于 JSON Object 支持范围，不在 JSON Schema 支持列表；之前的模型选型不适合用于验收阿里云正式支持的 JSON Schema 能力。

切换到 `qwen3.8-flash` 后，保持同一 endpoint、提示词、schema、SDK、`ThinkingEnabled=false`、1024 输出 token 上限与温度 0.1，连续 5 次调用全部通过：

- HTTP 200、Runtime Completed、`finish_reason=stop`，每次退出码为 0。
- 实际 HTTP 请求包含顶层 `enable_thinking=false`、`response_format.type=json_schema`、`strict=true` 及完整 schema。
- 5 次均为对象根节点，满足必填字段、禁止额外字段、字段类型、枚举、数组元素约束，且业务断言 `approved=false`、至少一条违规项通过。
- 原始 SSE 拼接与 QRE 最终文本逐字一致，无修复、解包或模拟回退。

证据保存在 `artifacts/live-examples/schema-qwen38-flash-1/` 至 `schema-qwen38-flash-5/`；核对汇总为 `artifacts/live-examples/schema-qwen38-flash-summary.json`。这证明本示例在该组合的 5 次调用均成功，不是对所有输入的统计保证。官方建议生产结构化输出避免设置 `max_tokens` 以免截断；本轮为保持单变量对照保留原值，所有样本均正常 stop，没有截断。

## 首轮历史记录：未列入 Schema 支持范围的模型

- Endpoint：`https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions`。
- 模型：`qwen3.8-27b`，`ThinkingEnabled=false`。
- 凭据：从已有 `VLLM_ALIYUN_API_KEY` 映射到进程级 `QRE_API_KEY`，未写入文件。
- 仅发送示例自带技能与合成代码片段，没有发送业务源码。

| 验证项 | 观察结果 | 结论 |
| --- | --- | --- |
| Skill 元数据注入 | 第一次实际 HTTP 请求包含 `code-reviewer` 目录项，不含技能正文 | 通过 |
| Skill 工具执行 | 模型依次调用 `ListSkillFiles`、`ReadSkillFile`；QRE executor 执行并回传正文 | 通过 |
| Skill 最终输出 | 3 步、3 次 HTTP 200，指出 `.Result` 违反技能规则并给出 async/await 修复 | 通过；两次完整运行均成功 |
| JSON Schema 出站请求 | 实际 HTTP body 包含 `response_format.type=json_schema`、`strict=true` 和原始完整 schema | 通过 |
| JSON Schema 响应契约 | 同一请求多次 HTTP 200，但输出在对象与单元素数组之间变化 | 严格约束验收失败 |
| 原始响应对照 | 失败样本的 SSE `delta.content` 拼接与 QRE `FinalText` 完全一致，首字符为 `[` | 违规形状已存在于远端响应 |

Schema 共执行 7 次 `qwen3.8-27b` 调用：3 次符合示例 schema，4 次未通过根节点/属性校验。其中第一次失败未保存正文；其余 3 次失败保存了数组输出。这个小样本用于复现问题，不是稳定性统计。没有把失败响应拆包、修正或替换为模拟响应。

早期尝试的 `qwen3.5-plus` 和 `qwen-plus` 各返回一次 HTTP 403 `Model.AccessDenied`，不计入 schema 支持测试。后续选择 SDK 现有测试配置使用的 `qwen3.8-27b`，该模型成功返回 HTTP 200。

## 修正的示例问题

1. 原 SkillsWorkflow 即使设置 `--endpoint` 仍运行 `StaticRuntimeModelClient`；现在线模式接入真实 `MeaiRuntimeModelClient`。
2. `CreateBuiltInSkillTools` 是 SDK 私有静态方法，原反射使用 `Instance` 并静默跳过失败。现在校验工具发现，显式映射两个只读工具并接入 QRE executor。SDK 另有 `CreateSkillFile`，本示例未授权其执行。
3. 原 Schema 示例不使用配置好的 options 发请求，且缺少结果时回退到固定 JSON。现在真实调用携带 schema，验证运行状态、HTTP 请求、全部示例 schema 约束和业务断言，无模拟回退。
4. 修正文档对客户端“100% 保证 schema”的错误描述。SDK 能发送约束参数，不等于服务端实际执行；当前结果不能推广到其他后端或模型。

## 复现

```powershell
$env:QRE_API_URL = 'https://dashscope.aliyuncs.com/compatible-mode/v1'
$env:QRE_MODEL = 'qwen3.8-27b'
$env:QRE_API_KEY = $env:VLLM_ALIYUN_API_KEY
dotnet run --project examples/SkillsWorkflow -- --live --evidence-dir artifacts/live-examples/skills
$env:QRE_MODEL = 'qwen3.8-flash'
dotnet run --project examples/StructuredOutputsJsonSchema -- --live --evidence-dir artifacts/live-examples/schema
```

每次独立执行请使用不同证据目录。无参数执行保持离线。在线失败会以非零退出码退出；不会在失败后切换模型或自动降级成 `json_object`。证据模式缓冲原始响应，不用作流式延迟测试。

本次本地证据（`artifacts/` 已被 Git 忽略）：

- `artifacts/live-examples/skills-wire/`：完整成功链的三个请求、原始 SSE、最终文本。
- `artifacts/live-examples/schema-wire/`：合格对象输出及原始 SSE。
- `artifacts/live-examples/schema-wire-repro-1/`：携带对象 schema 的请求、原始数组 SSE、最终数组 JSON。
- `artifacts/live-examples/schema-qwen38-trial-1/`、`schema-qwen38-trial-3/`：另外两次数组响应。

相关离线回归：`python scripts/test-examples.py Examples.test_skills_workflow Examples.test_structured_outputs_json_schema`，2/2 通过。两个示例均构建为 0 警告、0 错误。

结论边界：Skill 验证显式指示模型读取技能，证明调用链而非自主路由能力。历史 Schema 失败已定位到实际远端响应，但该模型不在官方 Schema 支持范围；切换到受支持的 `qwen3.8-flash` 后本次 5/5 通过。
