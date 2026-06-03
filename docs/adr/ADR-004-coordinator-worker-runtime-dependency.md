# ADR-004: Coordinator/Worker 蓝图与 Query Runtime 升级的依赖关系

> **状态**: Accepted
> **日期**: 2026-04-12
> **决策者**: CTO Review + Claude Code
> **适用范围**: coordinator-worker-runtime-upgrade-blueprint.md 与 query-runtime-upgrade.md 的时间线对齐

---

## 背景

当前仓库有两份并行演进的升级蓝图：

1. **Query Runtime 升级**（[query-runtime-upgrade.md](../archived-blueprints/query-runtime-upgrade.md)）
   - Phase 0A-4A 已完成
   - 稳定期观察进行中
   - Phase 5（tool result budget）未开始
   - Phase 6（Prompt/Context 协同增强）未开始

2. **Coordinator/Worker 升级**（[coordinator-worker-runtime-upgrade-blueprint.md](../archived-blueprints/coordinator-worker-runtime-upgrade-blueprint.md)）
   - Phase 0 执行中
   - 第一阶段范围：Phase 0-3 + Milestone M1 评审门控

两份蓝图的目标互补但执行上可能冲突——它们都修改 Runtime 核心路径。本 ADR 明确依赖关系和时间线约束。

---

## 决策

### 依赖矩阵

| Coordinator/Worker Phase | 依赖的 Query Runtime 状态 | 依赖类型 | 理由 |
|---|---|---|---|
| **Phase 0**（命名冻结） | 无硬依赖 | — | 纯文档工作 |
| **Phase 0.5**（技术 Spike） | 稳定期观察通过 | **硬前置** | spike 需要在稳定的 runtime 上运行 |
| **Phase 1**（通知协议） | 稳定期观察通过 | **硬前置** | 通知格式的设计依赖 runtime 事件模型稳定 |
| **Phase 1.5**（Hook 落点） | 稳定期观察通过 | **硬前置** | hook 插入 runtime 循环，需要 loop 稳定 |
| **Phase 2**（Worker 类型） | 稳定期观察通过 | **硬前置** | worker 复用 runtime engine |
| **Phase 3**（派工接口） | 稳定期观察通过 | **硬前置** | spawn_worker 创建的 job 内部使用 runtime |
| **Phase 4**（Forge Worktree） | Runtime Phase 5 已启动 | **推荐** | forge worker 的长任务可能受 tool result budget 影响 |
| **Phase 5**（Verify 证据化） | Runtime Phase 5 已完成或接近完成 | **强依赖** | verify worker 产生长报告，需要 tool result budget 支持截断 |
| **Phase 6**（Checklist 收口） | Runtime Phase 6 已启动 | **推荐** | checklist repair prompt 的上下文治理依赖 prompt/context 协同 |
| **Phase 7-8**（UI/恢复） | Runtime Phase 5 已完成 | **推荐** | gateway 适配和恢复策略需要在完整 runtime 能力上构建 |

### 时间线规则

```text
Query Runtime 稳定期观察
  ├─ 通过 → Coordinator/Worker Phase 0.5-3 可启动
  │         ├─ Milestone M1 通过 → Phase 4+ 可评估
  │         │                      └─ 但 Phase 5+ 等 Runtime Phase 5
  │         └─ Milestone M1 未通过 → Phase 4+ 暂停
  └─ 未通过 → Coordinator/Worker Phase 0.5+ 全部暂停
```

### 稳定期观察通过标准

沿用 [query-runtime-upgrade.md](../archived-blueprints/query-runtime-upgrade.md) 中定义的稳定期要求：

- 至少 50+ 真实任务通过 runtime 路径执行
- 或连续 1-2 周无结构性异常
- termination reason 分布稳定（无新增未知类型）
- 无 runtime / legacy 行为漂移报告

### 冲突规避

两份蓝图在以下区域存在潜在修改冲突：

| 文件 / 区域 | Query Runtime 可能修改 | Coordinator/Worker 可能修改 |
|---|---|---|
| `QueryRuntimeEngine.cs` | Phase 5 加 tool result budget | Phase 1.5 加 hook 调用点 |
| `BackgroundJobRunner.cs` | 不涉及 | Phase 0.5/3 加新 JobType handler |
| `GatewayRuntimeEventAdapter.cs` | Phase 5 加 event 截断 | Phase 7 加 worker 事件 |
| `DefaultCodexKernel.cs` | Phase 5/6 context 治理 | 不直接修改（通过 hook 扩展） |

规避策略：
1. Coordinator/Worker 的 Phase 1.5 hook 落点设计为**插入式**（新增调用点），不重构 runtime 主循环
2. 两边对 `QueryRuntimeEngine.cs` 的修改如有冲突，Query Runtime 优先（它是基础层）
3. 同一个 sprint 内不同时推进两份蓝图对同一文件的修改

---

## 理由

1. **不在不稳定的地基上盖楼**：Runtime 是 worker 的执行引擎，如果 loop 本身还有问题，worker 只会放大问题
2. **第一阶段可以先行**：Phase 0-3 的核心是协议层、类型系统和只读 worker 并行，这些不依赖 tool result budget 和 context 高级治理
3. **Phase 5+ 必须等 Runtime 补齐**：verify worker 的长报告和 checklist repair 的上下文管理，没有预算治理会导致 context 溢出

---

## 影响

- Phase 0 可以立即执行（当前正在进行）
- Phase 0.5 的启动需要确认稳定期观察状态
- 两份蓝图的负责人需要在每次 sprint 规划时对齐：本周谁改哪些文件
