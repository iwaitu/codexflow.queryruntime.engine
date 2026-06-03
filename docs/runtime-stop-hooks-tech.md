# Runtime Stop Hook 技术设计与使用指南

> **状态**: 已实现  
> **适用范围**: `CodexFlow.Core.Runtime` 中的 `IRuntimeHook.OnBeforeStopAsync` 与配置型 Stop hook  
> **核心文件**: `RuntimeHooks.cs`、`QueryRuntimeEngine.cs`、`RuntimeHookOptions.cs`、`ConfiguredStopRuntimeHook.cs`

---

## 1. 背景

CodexFlow 的 `QueryRuntimeEngine` 已经统一了承载 query/tool loop、工具执行、恢复策略和终止判断。此前在复杂代码任务中仍存在一个关键缺口：

- 模型在没有工具调用时可能过早给出最终回答
- runtime 只能按 `NoToolCalls` 结束当前 turn
- 外部策略无法在“即将停止”这一刻检查任务是否真的完成
- 用户或项目级约束无法把“还需要继续检查/继续执行”的判断注入主循环

Claude Code 的 Stop hook 提供了一个重要参考：当模型自然结束一轮响应时，运行时先给 hook 一个机会判断是否允许停止；如果 hook 要求继续，则把反馈重新送回模型，并允许下一轮继续调用工具。

CodexFlow 当前实现的是这套思想的最小稳定版本：在 `QueryRuntimeEngine` 接受 `NoToolCalls` 终止前，触发 `BeforeStop` hook；hook 可以返回 continuation 决策，让 runtime 注入反馈并继续下一轮。

---

## 2. 设计目标

### 2.1 需要解决的问题

1. **防止过早停止**  
   当模型给出“完成了”的文本但实际没有执行必要检查时，Stop hook 可以阻止本轮停止。

2. **把项目级完成标准外置**  
   不同项目可以通过 `.codexflow/hooks/stop.*` 定义自己的完成检查，例如是否运行测试、是否修改指定文件、是否生成验证证据。

3. **保持 runtime 主循环收口**  
   hook 不绕过 `QueryRuntimeEngine`，而是通过 runtime 的正式 continuation 通道继续执行。

4. **失败放行**  
   Stop hook 是质量增强能力，不是安全门禁。脚本失败、超时、输出非法时，runtime 记录日志并继续原本的停止决策。

### 2.2 非目标

当前版本不做以下事情：

- 不实现完整 Claude Code hook 体系中的所有事件
- 不让 hook 直接执行 CodexFlow 工具
- 不把 hook 失败升级为 query 失败
- 不在 hook 中修改历史消息列表
- 不要求项目 hook 必须存在

---

## 3. 运行时触发点

Stop hook 触发点位于 `QueryRuntimeEngine` 的自然停止路径：

```text
LLM response completed
  -> collect assistant text / thinking / tool calls
  -> if tool calls exist: execute tools
  -> if no tool calls:
       -> dispatch BeforeStop hook
       -> hook says continue: inject feedback and run next round
       -> hook says none: accept NoToolCalls termination
```

也就是说，Stop hook 只处理“模型没有工具调用，准备结束”的场景。它不会拦截以下路径：

- 工具调用正常执行路径
- 空响应恢复路径
- malformed protocol 恢复路径
- transport failure 恢复路径
- host cancellation

这样做的边界更清晰：Stop hook 专注于“是否允许停止”，其它异常仍由 runtime recovery 体系处理。

---

## 4. 核心接口

### 4.1 Runtime hook 接口

`IRuntimeHook` 当前包含两个节点：

```csharp
public interface IRuntimeHook
{
    ValueTask<AfterModelResponseHookResult> OnAfterModelResponseAsync(
        AfterModelResponseContext context,
        CancellationToken ct = default);

    ValueTask<BeforeStopHookResult> OnBeforeStopAsync(
        BeforeStopContext context,
        CancellationToken ct = default);
}
```

Stop hook 使用的是 `OnBeforeStopAsync`。

### 4.2 BeforeStopContext

Stop hook 收到的上下文包含：

| 字段 | 含义 |
|------|------|
| `Request` | 当前 `QueryRuntimeRequest`，包含 session、entry point、workspace metadata 等 |
| `Round` | 当前 runtime round |
| `LastAssistantMessage` | 模型刚刚生成、准备作为最终回答的文本 |
| `ThinkingText` | 当前轮 thinking 文本，如果有 |
| `StopHookActive` | 是否已经处于 Stop hook 触发后的 continuation 轮次 |
| `ContinuationCount` | 当前 Stop hook 已触发 continuation 的次数 |

### 4.3 BeforeStopHookResult

hook 返回：

| 字段 | 含义 |
|------|------|
| `Continue` | 是否阻止停止并继续下一轮 |
| `Message` | 注入给模型的反馈消息 |
| `Reason` | continuation 原因，写入 runtime 状态与日志 |
| `AllowToolCallsOnNextRound` | 下一轮是否允许工具调用，默认建议为 `true` |

当 `Continue=false` 或返回 `None` 时，runtime 接受原本的停止判断。

---

## 5. 配置型 Stop Hook

CodexFlow 提供 `ConfiguredStopRuntimeHook`，把 Stop hook 映射为本地命令或项目脚本。

### 5.1 appsettings 配置

默认配置位于 `CodexFlow/appsettings.json`：

```json
{
  "RuntimeHooks": {
    "Stop": {
      "Enabled": false,
      "TimeoutMs": 5000,
      "EnableProjectHooks": true,
      "Commands": []
    }
  }
}
```

字段说明：

| 字段 | 默认值 | 说明 |
|------|--------|------|
| `Enabled` | `false` | 是否启用配置型 Stop hook |
| `TimeoutMs` | `5000` | 单个 hook 命令的超时时间，最小按 100ms 处理 |
| `EnableProjectHooks` | `true` | 是否自动发现项目级 `.codexflow/hooks` 脚本 |
| `Commands` | `[]` | 显式配置的 hook 命令列表 |

### 5.2 显式命令配置

示例：

```json
{
  "RuntimeHooks": {
    "Stop": {
      "Enabled": true,
      "TimeoutMs": 5000,
      "EnableProjectHooks": true,
      "Commands": [
        {
          "Name": "repo-stop-check",
          "Enabled": true,
          "FileName": "powershell",
          "Arguments": "-NoProfile -ExecutionPolicy Bypass -File \"D:\\codeup\\codexflow\\scripts\\stop-check.ps1\"",
          "WorkingDirectory": "D:\\codeup\\codexflow"
        }
      ]
    }
  }
}
```

显式命令按配置顺序执行。第一个返回 `continue=true` 的命令会终止后续 hook 执行，并让 runtime 继续下一轮。

---

## 6. 项目级 `.codexflow/hooks` 约定

### 6.1 目录位置

当 `RuntimeHooks:Stop:Enabled=true` 且 `EnableProjectHooks=true` 时，runtime 会从项目根目录自动发现：

```text
<project-root>/.codexflow/hooks/
```

项目根目录候选来源包括：

- `request.Session.WorkspacePath`
- `request.PromptMetadata.WorkspacePath`
- `ToolPathResolver.ResolveProjectRoot(...)` 的解析结果

候选路径会去重，并且只使用真实存在的目录。

### 6.1.1 WebUI 初始化开关

工作空间详情弹窗提供 `Stop hook 脚本` 开关，用于为当前工作空间初始化项目级 hook：

- 后端只接收 `sessionId`，再校验该 session 是否属于当前用户。
- 脚本写入 `ToolPathResolver.ResolveProjectRoot(...)` 解析出的项目根目录。
- 容器/Linux 环境默认生成 `.codexflow/hooks/stop.sh`。
- Windows 环境默认生成 `.codexflow/hooks/stop.ps1`。
- 关闭开关时不会删除脚本，而是把已启用脚本重命名为 `.disabled`，避免丢失用户改过的检查逻辑。

注意：开关只负责初始化或停用项目脚本。Stop hook 真正参与运行时，还需要服务端配置 `RuntimeHooks:Stop:Enabled=true`，并保持 `EnableProjectHooks=true`。

### 6.2 支持的脚本文件名

当前按以下文件名顺序查找：

```text
stop.cmd
stop.bat
stop.ps1
stop.sh
stop
```

Windows 平台：

| 文件 | 执行方式 |
|------|----------|
| `stop.cmd` / `stop.bat` | `cmd.exe /c "<script>"` |
| `stop.ps1` | `powershell -NoProfile -ExecutionPolicy Bypass -File "<script>"` |

非 Windows 平台：

| 文件 | 执行方式 |
|------|----------|
| `stop.ps1` | `pwsh -NoProfile -File "<script>"` |
| `stop.sh` / `stop` | `/bin/sh "<script>"` |

项目 hook 的工作目录固定为项目根目录。

### 6.3 与显式命令的顺序

执行顺序：

```text
RuntimeHooks:Stop:Commands
  -> discovered .codexflow/hooks/stop.*
```

因此，全局或部署级命令可以先执行；项目级 hook 用于补充项目自己的完成标准。

---

## 7. Hook 输入协议

配置型 Stop hook 通过 stdin 接收 JSON。示例：

```json
{
  "hook_event_name": "Stop",
  "session_id": "session-123",
  "entry_point": "GatewayMessageProcessor",
  "workspace_path": "D:\\codeup\\codexflow",
  "project_root": "D:\\codeup\\codexflow",
  "round": 3,
  "stop_hook_active": false,
  "continuation_count": 0,
  "last_assistant_message": "修改已经完成。",
  "thinking_text": null
}
```

字段说明：

| 字段 | 说明 |
|------|------|
| `hook_event_name` | 固定为 `Stop` |
| `session_id` | 当前 query session ID |
| `entry_point` | runtime 入口点 |
| `workspace_path` | 当前 session 或 prompt metadata 的 workspace 路径 |
| `project_root` | runtime 解析出的项目根目录 |
| `round` | 当前 round |
| `stop_hook_active` | 是否已经由 Stop hook 触发过 continuation |
| `continuation_count` | 已触发 continuation 的次数 |
| `last_assistant_message` | 模型准备输出的最终文本 |
| `thinking_text` | 当前轮 thinking 文本 |

脚本可以忽略不需要的字段。

---

## 8. Hook 输出协议

hook 通过 stdout 输出 JSON。最小继续示例：

```json
{
  "continue": true,
  "message": "Stop hook 检测到还缺少测试验证。请继续运行相关测试并汇报结果。",
  "reason": "missing-test-evidence",
  "allow_tool_calls_on_next_round": true
}
```

字段说明：

| 字段 | 类型 | 说明 |
|------|------|------|
| `continue` | boolean | 是否要求 runtime 继续下一轮 |
| `prevent_stop` | boolean | `continue` 的兼容别名，任一为 true 即继续 |
| `message` | string | 注入给模型的反馈 |
| `reason` | string | continuation 原因 |
| `allow_tool_calls_on_next_round` | boolean | 下一轮是否允许工具调用，缺省为 true |

如果 stdout 中包含额外日志，runtime 会尝试提取第一个 JSON 对象范围：

```text
checking repository...
{"continue":true,"message":"请继续验证","reason":"needs-verification"}
done
```

推荐仍然只向 stdout 输出 JSON，把诊断日志写到 stderr。

---

## 9. 示例脚本

### 9.1 PowerShell 项目 hook

文件路径：

```text
.codexflow/hooks/stop.ps1
```

内容：

```powershell
$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json

if ($payload.last_assistant_message -notmatch "测试|build|验证") {
    @{
        continue = $true
        reason = "missing-verification-summary"
        message = "请继续检查本次修改是否已经构建或测试验证，并在最终回答中给出明确证据。"
        allow_tool_calls_on_next_round = $true
    } | ConvertTo-Json -Compress
    exit 0
}

@{ continue = $false } | ConvertTo-Json -Compress
exit 0
```

### 9.2 Shell 项目 hook

文件路径：

```text
.codexflow/hooks/stop.sh
```

内容：

```sh
#!/bin/sh
payload="$(cat)"

case "$payload" in
  *测试*|*build*|*验证*)
    printf '{"continue":false}\n'
    ;;
  *)
    printf '{"continue":true,"reason":"missing-verification-summary","message":"请继续完成构建或测试验证，并在最终回答中给出证据。","allow_tool_calls_on_next_round":true}\n'
    ;;
esac
```

### 9.3 Windows CMD 项目 hook

文件路径：

```text
.codexflow/hooks/stop.cmd
```

内容：

```bat
@echo off
echo {"continue":true,"reason":"project-stop-check","message":"请继续检查项目级 Stop hook 要求的完成条件。","allow_tool_calls_on_next_round":true}
```

CMD 示例适合做简单固定拦截。复杂判断建议使用 PowerShell。

---

## 10. Continuation 语义

当 hook 返回 `continue=true` 后，runtime 会：

1. 增加 Stop hook continuation 计数
2. 记录 `LastContinueReason`
3. 设置 `StopHookContinuationUsed` 状态
4. 把 hook message 注入当前消息序列
5. 根据 `allow_tool_calls_on_next_round` 决定下一轮是否保留工具调用能力
6. 必要时扩展 `MaxRounds`，避免刚触发 continuation 就撞上轮次上限
7. 回到下一轮模型调用

这保证 hook 不直接改写执行结果，而是让模型在正式 runtime loop 中继续完成任务。

### 10.1 continuation 上限

Stop hook continuation 会复用 runtime 的 recovery attempt 上限：

```text
request.AdapterHints.MaxRecoveryAttempts ?? 2
```

如果 hook 持续要求继续，超过上限后 runtime 会忽略后续 continuation 请求并接受停止，避免无限循环。

---

## 11. 失败语义

配置型 Stop hook 使用 fail-log-and-continue。

| 情况 | 行为 |
|------|------|
| hook 未启用 | 不执行 |
| 没有配置命令且没有项目脚本 | 不执行 |
| 命令返回非 0 | 记录 warning，继续原停止路径 |
| 命令超时 | 尝试 kill 进程树，继续原停止路径 |
| stdout 为空 | 视为无 continuation |
| stdout JSON 非法 | 记录 warning，继续原停止路径 |
| hook 抛异常 | 记录 warning，继续原停止路径 |

这种策略符合当前定位：Stop hook 用于增强完成度，而不是安全阻断。后续如果要引入安全类 hook，应使用单独事件和 fail-closed 语义。

---

## 12. 可观测性

当前实现记录以下日志：

- hook 是否执行
- hook 名称
- exit code
- timeout 状态
- 非 0 退出的 stderr 摘要
- hook 执行异常
- Stop hook continuation 原因

测试覆盖位于：

```text
CodexFlow.Core.Tests/Runtime/ConfiguredStopRuntimeHookTests.cs
CodexFlow.Core.Tests/Runtime/RuntimeHookDispatcherTests.cs
```

推荐排查顺序：

1. 确认 `RuntimeHooks:Stop:Enabled=true`
2. 确认 session 或 prompt metadata 中有可解析 workspace
3. 确认 `.codexflow/hooks/stop.*` 位于项目根目录
4. 确认脚本退出码为 0
5. 确认 stdout 中存在合法 JSON 对象
6. 查看应用日志中的 `Configured Stop hook executed`

---

## 13. 使用建议

### 13.1 适合放入 Stop hook 的检查

- 最终回答是否包含测试或构建证据
- 计划中的 checklist 是否全部完成
- 是否仍有明确 TODO 或占位描述
- 是否修改了不允许修改的路径
- 是否缺少项目约定的产物或报告

### 13.2 不适合放入 Stop hook 的逻辑

- 长时间运行的大型测试套件
- 需要交互输入的命令
- 会修改代码的自动修复脚本
- 安全关键阻断逻辑
- 与当前 query 无关的全仓扫描

Stop hook 应该保持轻量，最好在数秒内完成。重型验证更适合由 verify worker 或后续 streaming-first executor 承担。

---

## 14. 与后续 streaming-first executor 的关系

Stop hook 解决的是“模型准备停止时是否允许停止”的问题；streaming-first tool executor 解决的是“工具调用在流式响应中如何更早、更稳定地聚合和执行”的问题。

两者互补：

- Stop hook 是终止前补救层
- streaming-first executor 是主执行链路增强

因此当前 Stop hook 文档与实现不会阻塞后续 executor 改造。后续 executor 完成后，Stop hook 仍可作为最终完成标准检查点保留。

---

## 15. 参考实现位置

| 文件 | 说明 |
|------|------|
| `CodexFlow.Core/Runtime/RuntimeHooks.cs` | runtime hook 接口、上下文、dispatcher |
| `CodexFlow.Core/Runtime/QueryRuntimeEngine.cs` | Stop hook continuation 触发与主循环接入 |
| `CodexFlow.Core/Runtime/RuntimeHookOptions.cs` | `RuntimeHooks` 配置模型 |
| `CodexFlow.Core/Runtime/ConfiguredStopRuntimeHook.cs` | 配置命令与项目级 `.codexflow/hooks` 执行器 |
| `CodexFlow/Program.cs` | DI 注册 |
| `CodexFlow/appsettings.json` | 默认配置示例 |
| `CodexFlow.Core.Tests/Runtime/ConfiguredStopRuntimeHookTests.cs` | 配置命令与项目 hook 测试 |
| `CodexFlow.Core.Tests/Runtime/RuntimeHookDispatcherTests.cs` | dispatcher 行为测试 |
