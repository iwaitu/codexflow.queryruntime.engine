# Skills Workflow Example (技能系统与工作流示例)

本示例演示如何在 QRE 与 `VllmChatClient` 体系中配置和使用 **Skill（领域技能包）**。

## 核心机制

1. **两阶段按需加载 (Two-Phase Progressive Loading)**：
   - 客户端（`VllmBaseChatClient`）自动扫描指定技能目录（`SkillDirectoryPath`）；
   - 提取每个技能的 `SKILL.md` 元数据（`name` 与 `description`），以轻量级 `# Skills` 目录注入系统提示词；
   - 保持极低的常驻 Token 开销，避免上下文窗口被冗长的规程污染。
2. **QRE 工具调度**：
   - 宿主把 SDK 的 `ListSkillFiles` 与 `ReadSkillFile` 映射为 QRE 工具声明，并通过 `IRuntimeToolExecutor` 执行；仅设置 `EnableSkills` 并不能完成 QRE 工具执行接线。
   - 示例通过反射访问锁定版本 VllmChatClient 2.0.25 的私有方法，升级 SDK 时需重新验证。SDK 还提供 `CreateSkillFile`，本示例没有授权其执行。
   - 在线测试显式要求模型列出并读取技能，验证调用链，不衡量模型自主选择技能的能力。

## 运行方式

### 离线自检模式（零外部依赖，即刻运行）

```bash
dotnet run --project examples/SkillsWorkflow
```

### 在线模式（连接本地 vLLM 或 OpenAI 兼容服务）

```bash
dotnet run --project examples/SkillsWorkflow -- --endpoint http://localhost:8000/v1 --model qwen-2.5
```

也可使用 PowerShell 环境变量（只有 `--live` 或 `--endpoint` 才启用网络）：

```powershell
$env:QRE_API_URL = 'https://dashscope.aliyuncs.com/compatible-mode/v1'
$env:QRE_MODEL = 'qwen3.8-27b'
$env:QRE_API_KEY = $env:VLLM_ALIYUN_API_KEY
dotnet run --project examples/SkillsWorkflow -- --live --evidence-dir artifacts/live-examples/skills
```

在线模式断言真实 HTTP 200、首次仅注入元数据、两个工具实际执行、正文回传以及最终审查结果。失败返回非零退出码，不降级为模拟响应。证据目录保存请求 JSON、原始响应和最终文本，不保存认证头；证据模式会缓冲响应，不适合测流式首字延迟。无参数运行仍是离线自检，不代表 API 验证通过。

实测结论见 [真实 API 验证记录](../live-api-validation.md)。

## 目录结构

```text
SkillsWorkflow/
├── SkillsWorkflow.csproj
├── Program.cs
├── README.md
└── skills/
    └── code-reviewer/
        └── SKILL.md       # 标准技能定义：Frontmatter 元数据 + 审查规程
```
