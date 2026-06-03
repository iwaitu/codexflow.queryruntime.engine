# QueryRuntime Tool Use Contract 升级实施蓝图

## 1. 文档目标

本文定义 QueryRuntime 的下一步升级方案：参考 Claude Code 的主循环设计，把“真实工具调用 block”作为每轮是否继续的唯一工具信号，并为 worker 引入可执行的工具强制契约，避免模型只在 thinking 或正文中描述“将要执行工具”但实际没有发起工具调用。

目标不是扩大文本解析，而是建立运行时硬约束：

- 有真实工具调用就执行工具并继续下一轮。
- 没有真实工具调用只代表“候选结束”，必须先经过 recovery、stop hook 和 required-tool contract。
- worker 在结束前必须满足自己的最低工具证据契约。
- 必须工具场景优先用 API 层 `tool_choice` / `ChatToolMode.RequireSpecific(...)`，而不是只靠 prompt 文案。

---

## 2. 参考结论

Claude Code 的关键做法：

1. 不信任 `stop_reason === "tool_use"` 作为循环依据。
2. 只要流中出现实际 `tool_use` content block，就设置 `needsFollowUp = true`。
3. `needsFollowUp == true` 时执行工具，追加 `tool_result`，然后进入下一轮。
4. 没有 `tool_use` block 时进入候选结束流程。
5. 对必须工具输出的场景，使用 `tool_choice` 或 Stop Hook 注入反馈并继续。

CodexFlow 中对应的抽象是 `FunctionCallContent`：

```text
FunctionCallContent.Count > 0
  => execute tools
  => append FunctionResultContent
  => continue

FunctionCallContent.Count == 0
  => candidate final
  => required-tool contract / stop hook / recovery may veto
  => no veto then terminate
```

---

## 3. 当前问题

当前 QueryRuntime 已具备多项恢复能力，但 worker 仍可能失败在以下路径：

1. 模型在 thinking 中写“我要执行命令/读取文件/修改文件”，但没有真正发出工具调用。
2. runtime 只能通过启发式的 unexecuted intent recovery 尝试纠偏。
3. worker 最终可能以 `NoToolCalls` 或 `MaxRoundsReached` 结束，但业务上并未获得任何工具证据。
4. Forge / Verify / Explore 等 worker 没有各自的最低工具执行契约。
5. Stop Hook 已存在，但还没有内建的“必须工具执行”契约 hook。

---

## 4. 目标态

### 4.1 主循环不变量

QueryRuntime 必须稳定遵守：

| 条件 | runtime 行为 |
|---|---|
| 本轮存在 `FunctionCallContent` | 执行工具，记录结果，继续下一轮 |
| 本轮没有 `FunctionCallContent` 且无拦截 | 接受为最终候选，结束 |
| 本轮没有 `FunctionCallContent` 但契约未满足 | 注入反馈，继续下一轮 |
| 契约恢复轮有指定工具 | 设置 `ChatToolMode.RequireSpecific(toolName)` 并关闭 thinking |
| 契约恢复耗尽 | `RecoveryExhausted`，detail 为 `required_tool_contract_violation` |

### 4.2 Worker 默认契约

| Worker | 最低契约 | 推荐恢复工具 |
|---|---|---|
| Explore | 至少成功执行一个读/搜索/分析工具 | `search_file_index` |
| Plan | 至少成功执行一个读/搜索/分析工具 | `search_file_index` |
| Forge | 至少成功执行一个写入类工具 | `apply_patch` |
| Verify | 至少成功执行一个验证证据工具 | `exec_cmd` |

### 4.3 契约满足条件

默认使用 `RequireSuccessfulResult = true`：

- 只发起工具但工具失败，不满足契约。
- 工具被权限、校验、去重或 runtime 干预拦截，不满足契约。
- 成功执行任意 `AnyOfToolNames` 中的工具，满足契约。

---

## 5. 设计方案

### 5.1 新增模型

在 runtime 请求中增加：

```csharp
public RequiredToolExecutionContract? RequiredToolContract { get; init; }
```

契约模型：

```csharp
public sealed record RequiredToolExecutionContract
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> AnyOfToolNames { get; init; }
    public bool RequireSuccessfulResult { get; init; } = true;
    public int MaxContinuationAttempts { get; init; } = 2;
    public string? PreferredRecoveryToolName { get; init; }
    public string? Feedback { get; init; }
}
```

### 5.2 Runtime 状态记录

在 `QueryRuntimeState` 中记录：

- `ExecutedToolNames`
- `SuccessfulToolNames`
- `RequiredToolContractContinuationCount`

工具执行完成后更新：

```csharp
state.ExecutedToolNames.Add(result.ToolName);
if (result.Success)
{
    state.SuccessfulToolNames.Add(result.ToolName);
}
```

### 5.3 Stop Hook 拦截

新增 `RequiredToolExecutionRuntimeHook`：

1. 读取 `context.Request.RequiredToolContract`。
2. 检查 `ExecutedToolNames` 或 `SuccessfulToolNames`。
3. 已满足则返回 `BeforeStopHookResult.None`。
4. 未满足则返回 `Continue = true`。
5. 如果能解析出可用恢复工具，则设置 `RequiredToolNameForNextRound`。
6. 恢复耗尽后以 `RecoveryExhausted` 失败结束。

### 5.4 恢复轮强制工具

在 `BeforeStopHookResult` 中新增：

```csharp
public string? RequiredToolNameForNextRound { get; init; }
public int? MaxContinuationAttempts { get; init; }
public string? ExhaustionDetailCode { get; init; }
```

`QueryRuntimeEngine.TryHandleStopHookContinuationAsync` 收到指定工具后：

- `state.RequiredToolNameForNextRound = toolName`
- `state.NextRoundOptionOverrides["ToolMode"] = ChatToolMode.RequireSpecific(toolName)`
- `state.NextRoundOptionOverrides["ThinkingEnabled"] = false`

---

## 6. 实施阶段

### Phase 1: 核心闭环

- 新增 `RequiredToolExecutionContract`。
- 扩展 `QueryRuntimeRequest`、`QueryRuntimeState`、`BeforeStopContext`、`BeforeStopHookResult`。
- 新增 `RequiredToolExecutionRuntimeHook`。
- 注册内建 hook。
- 为 worker 定义默认契约。
- worker runtime request 传入契约。
- 增加单元测试覆盖无工具候选结束、强制恢复、成功满足、耗尽失败。

### Phase 2: 诊断与观测

- 在 telemetry 中记录契约名称、恢复工具、恢复次数。
- 在 worker notification 中输出 contract recovery 信息。
- 在日志中明确区分 narrated-intent recovery 与 contract recovery。

### Phase 3: 收敛启发式恢复

- 保留严格的 legacy tool-call 兼容解析。
- 收窄 narrated-intent recovery 的触发范围。
- worker 场景优先依赖 required-tool contract。

---

## 7. 验收标准

1. Forge worker 第一轮无工具结束时，不允许 Completed。
2. Verify worker 没有 `exec_cmd` / `run_tests` / diagnostics 证据时，不允许 Completed。
3. Explore / Plan worker 没有读/搜索证据时，不允许 Completed。
4. 契约恢复轮应使用 `RequireSpecific`，并关闭 thinking。
5. 工具成功后允许最终总结。
6. 恢复耗尽后返回 `RecoveryExhausted` 和 `required_tool_contract_violation`。
7. 现有 non-worker QueryRuntime 行为不变。

