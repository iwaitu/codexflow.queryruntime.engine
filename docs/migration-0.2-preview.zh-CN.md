# QueryRuntime 0.2 Preview 迁移指南

本文说明如何从稳定 v1 `0.1.2` 接口迁移到 `0.2.0-preview.*` 的 v2-only Runtime。

## 兼容边界

- 不重发或修改 `0.1.2` 包和 v1 trace schema。
- CLI 仅允许 v2 执行；`--runtime v2` 是可选兼容写法，`--runtime v1` 会在执行前失败。
- CodexFlow 只接受 `Runtime:QueryRuntime:Backend=qre-v2`，`core` 或 `qre` 会导致启动失败。
- v1 trace summary reader 仅用于历史只读检查；未知 v2 audit schema 显式失败。

## API 变化

v2 使用 `CodexFlow.QueryRuntime.Protocol` 下的 provider-neutral contract，以及
`CodexFlow.QueryRuntime.Engine.V2` 下的 Hosting/Runtime 类型。主要入口是
`IAgentRuntime.RunAsync(RuntimeRunRequest, ...)`；从 `0.2.0-preview.21` 起，H1 恢复入口为
`IResumableAgentRuntime.ResumeAsync(RuntimeResumeRequest, ...)`。Session、Turn、Step、invocation、audit identity 均为
typed ID；model stream 分离 text、reasoning、usage、warning、tool call 和 completion；工具执行必须经过
冻结 ToolRegistry 与 execution plan。

旧 v1 public types 暂时继续编译以支持源码迁移和历史消费者，但 CLI 与 CodexFlow 生产入口均不再调度到
重复的 v1 Agent Loop。新代码统一使用 v2 Hosting facade 和 Engine-owned policy、context、audit contract。

## CLI 迁移

```bash
qre run --profile readonly --response "offline smoke" --json "inspect this repository"
qre replay latest --summary --json
qre resume latest --workspace . --response "continue" --json
```

v2 是唯一执行路径，支持 `none`、`readonly`、`verify`、`repair`；写入和高风险执行仍需
绑定冻结计划的审批。公开审计默认脱敏且仅支持 summary；`--trace-data sanitized` 只用于经审查 fixture，
`--trace-data private` 用于访问受控诊断。
H1 checkpoint 只在这两种模式写入。恢复要求 Runtime contract、冻结请求、workspace 和宿主
`RecoveryCompatibilityId` 一致；public run、terminal checkpoint、动态工具目录和不确定工具结果都会在执行前失败。

## 宿主迁移

1. 升级到 `0.2.0-preview.21` 或更高版本，并把 backend 设为 `qre-v2`。
2. 运行宿主 contract kit，先验证 readonly、verify。
3. 只有写审批、sandbox/write-back 门禁通过后才启用 repair。
4. 对 policy decision、tool order、归一化 terminal reason、side-effect count 零容忍；final text 单独应用容差。
5. 如需回退 v1，必须重新部署旧 package/应用版本；不再提供进程内 feature flag 回退。

非法 backend 名称和非正数 model stream timeout 必须让宿主启动失败，不能静默落到其他引擎。

## 切换决策

项目 owner 于 2026-08-24 明确豁免“两次 preview＋观察窗口”门禁并批准 v2-only 切换。自动化测试、Native
AOT 和真实 E2E 仍为强制项，详见 ADR-007。运营回退单元改为上一应用部署版本，不再是进程内 backend flag。
