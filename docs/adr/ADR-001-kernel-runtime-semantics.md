# ADR-001: DefaultCodexKernel Runtime 路径语义决策

> **状态**: Accepted
> **日期**: 2026-04-09
> **决策者**: Claude Code + User
> **适用范围**: CodexFlow.Core/Agents/DefaultCodexKernel runtime 集成

---

## 背景

Phase 4A 将 `DefaultCodexKernel` 接入统一 `IQueryRuntimeEngine`，实现了 guardrail + critique 闭环。在实施过程中，我们有意改变了一些行为语义，这与旧实现存在差异。本文档记录这些决策及其理由。

## 决策

### 1. Guardrail 保守放行原则

**决策**: 当 guardrail 检查无法确定时，采用"允许执行"策略。

**具体行为**:
- 非危险工具（非 write_file/smart_patch/delete_file/ApplyPatchTool）直接放行
- 无法从参数中提取目标路径时放行
- `ICodeAnalysisService` 异常时放行

**理由**:
- Guardrail 是安全护栏，不是业务逻辑门禁
- 过度阻止会导致正常任务无法完成
- 真正的风险通过 critique 后置审查兜底

**与旧实现的等价性**: ✅ 完全等价。旧实现同样只在能确定风险时才阻止。

---

### 2. Critique 闭环语义重构

**决策**: Critique reject 通过 `IQueryRuntimeInterventionHook` 实现，而非直接操作 messages 列表。

**旧实现行为**:
```
Tool executed → Critique review → Reject
  → Add feedback to messages
  → Continue reasoning loop (same round)
```

**新实现行为**:
```
Tool execution completed → OnToolExecutionCompletedAsync
  → Critique review → Reject
  → QueryRuntimeIntervention.SkipToolResultWithFeedback
  → Skip appending tool result to messages
  → Inject user feedback message
  → Next round sees feedback, not original tool result
```

**差异分析**:

| 方面 | 旧实现 | 新实现 |
|------|--------|--------|
| Tool result 处理 | 保留在 messages 中 | 跳过不追加 |
| Feedback 角色 | User message 追加 | User message 追加 |
| 触发时机 | 同一 round 内 | Round 边界 |
| Retry 计数 | 手动管理 | Adapter 内部管理 |

**等价性判断**: ⚠️ **语义近似等价，非逐字节兼容**

新实现更符合"tool result 被审查拒绝，不应污染上下文"的设计意图。旧实现保留 tool result 是实现便利，不是刻意设计。

**接受理由**:
1. 新实现语义更清晰（rejected result 不进入 history）
2. Feedback 注入机制保证了 LLM 能收到调整指令
3. 专项测试验证了闭环有效性

---

### 3. Guardrail 仅对 Forge 角色启用

**决策**: `ICodexGuardrail` 检查只在 `CodexAgentRole.Forge` 角色下触发。

**理由**:
- Forge 是唯一具有写文件权限的角色
- Architect/Security 角色的工具权限已被 tool access policy 限制
- 避免不必要的图构建开销

**与旧实现的等价性**: ✅ 完全等价。旧实现同样只在 Forge 路径执行 guardrail 检查。

---

### 4. Critique 对 Security 角色禁用

**决策**: Critique review 在 `CodexAgentRole.Security` 角色下跳过。

**理由**:
- Security 角色的输出是审计报告，不需要代码质量审查
- 避免 critique 逻辑与 security audit 冲突
- 保持 Security 角色的独立判断能力

**与旧实现的等价性**: ✅ 完全等价。旧实现中 critique 只在 Forge/Architect 角色启用（且当前被 bypass）。

---

### 5. Intervention Hook 机制设计

**决策**: 使用 `IQueryRuntimeInterventionHook` 接口让入口层干预 runtime 行为，而非在 runtime 内部硬编码策略。

**理由**:
1. **特性保留**: Kernel 在使用 runtime 共性 loop 的同时保留自己的策略
2. **解耦**: Runtime 不需要了解 guardrail/critique 的业务语义
3. **可测试**: Adapter 可以独立测试干预逻辑
4. **可扩展**: 其他入口（如 Gateway）可以选择性地实现自己的干预逻辑

**设计模式**: Strategy Pattern + Observer Pattern 的结合。

---

## 后果

### 正面

- Guardrail + Critique 形成真正的闭环，而非只是事件发射
- 代码结构更清晰，职责分离更明确
- 通过 `KERNEL_DISABLE_RUNTIME` 环境变量可随时回退旧路径
- 新增 9 项专项测试覆盖关键场景

### 风险

- Critique 语义变更可能导致 LLM 行为差异（需要真实流量验证）
- 新路径未经大规模生产验证
- 旧测试套件存在历史失败项，不是完全干净的回归基线

### 缓解措施

1. **灰度策略**: 通过 `KERNEL_DISABLE_RUNTIME=true` 可随时回退
2. **日志对比**: 生产环境先开启旧路径，并行记录新路径日志对比
3. **Transcript 对照**: 在隔离环境中对比新旧路径的完整对话 transcript
4. **监控**: 在 telemetry 中标记 runtime/legacy 路径，监控行为差异

---

## 验收标准

Phase 4A 被认定为"工程上可接受的完成度"，满足以下条件：

- [x] Runtime 路径已打通，具备 fallback 机制
- [x] Guardrail 闭环已实现并通过测试
- [x] Critique 闭环已实现并通过测试
- [x] 专项测试 9/9 通过
- [x] 行为差异已文档化（本文档）

---

## 后续工作

在进入 Phase 4B/5 或更激进的 context governance 之前，建议：

1. **稳定期观察**: 让 runtime kernel 路径在真实任务中运行
2. **日志对比**: 收集 `KERNEL_DISABLE_RUNTIME=true/false` 下的 transcript 对照
3. **修复历史测试**: 清理现有测试套件中的历史失败项
4. **生产验证**: 在低风险任务中逐步启用 runtime 路径

---

## 参考

- [query-runtime-upgrade.md](../archived-blueprints/query-runtime-upgrade.md) - 统一 Runtime 升级蓝图
- [KernelRuntimeEventAdapter.cs](../../CodexFlow.Core/Agents/Adapters/KernelRuntimeEventAdapter.cs)
- [DefaultCodexGuardrail.cs](../../CodexFlow.Core/Agents/DefaultCodexGuardrail.cs)
- [KernelRuntimeIntegrationTests.cs](../../CodexFlow.Core.Tests/Runtime/KernelRuntimeIntegrationTests.cs)
