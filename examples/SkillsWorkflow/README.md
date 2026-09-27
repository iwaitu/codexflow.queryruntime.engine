# Skills Workflow Example (技能系统与工作流示例)

本示例演示如何在 QRE 与 `VllmChatClient` 体系中配置和使用 **Skill（领域技能包）**。

## 核心机制

1. **两阶段按需加载 (Two-Phase Progressive Loading)**：
   - 客户端（`VllmBaseChatClient`）自动扫描指定技能目录（`SkillDirectoryPath`）；
   - 提取每个技能的 `SKILL.md` 元数据（`name` 与 `description`），以轻量级 `# Skills` 目录注入系统提示词；
   - 保持极低的常驻 Token 开销，避免上下文窗口被冗长的规程污染。
2. **自动内置工具调度**：
   - 自动注册 `ListSkillFiles`（列出可用技能清单）与 `ReadSkillFile`（按需读取特定技能的完整指导指令）；
   - 模型在需要时主动调用 `ReadSkillFile` 加载深层次领域指令。

## 运行方式

### 离线自检模式（零外部依赖，即刻运行）

```bash
dotnet run --project examples/SkillsWorkflow
```

### 在线模式（连接本地 vLLM 或 OpenAI 兼容服务）

```bash
dotnet run --project examples/SkillsWorkflow -- --endpoint http://localhost:8000/v1 --model qwen-2.5
```

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
