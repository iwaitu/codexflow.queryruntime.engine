# QueryRuntime 0.2 Preview 迁移指南

本文说明如何从稳定 v1 `0.1.2` 接口迁移到 `0.2.0-preview.*` 中显式启用的 v2 Runtime。

## 兼容边界

- 不重发或修改 `0.1.2` 包和 v1 trace schema。
- v2 必须显式启用：CLI 使用 `--runtime v2`；CodexFlow 使用
  `Runtime:QueryRuntime:Backend=qre-v2`。
- v1 reader 在 preview 窗口内继续保留；未知 v2 audit schema 显式失败。
- 一个 Turn 始终属于启动它的 backend。切换 feature flag 只影响后续请求，不允许把 in-flight Turn
  跨 backend 恢复。

## API 变化

v2 使用 `CodexFlow.QueryRuntime.Protocol` 下的 provider-neutral contract，以及
`CodexFlow.QueryRuntime.Engine.V2` 下的 Hosting/Runtime 类型。主要入口是
`IAgentRuntime.RunAsync(RuntimeRunRequest, ...)`。Session、Turn、Step、invocation、audit identity 均为
typed ID；model stream 分离 text、reasoning、usage、warning、tool call 和 completion；工具执行必须经过
冻结 ToolRegistry 与 execution plan。

preview 期间旧 `IQueryRuntimeEngine` 和 Experimental harness 继续用于回退。新代码不应依赖 Experimental
Agent Loop 内部实现，而应使用 v2 Hosting facade 以及 Engine-owned policy、context、audit contract。

## CLI 迁移

```bash
qre run --runtime v2 --profile readonly --response "offline smoke" --json "inspect this repository"
qre replay latest --runtime v2 --summary --json
```

观察窗口完成前，CLI 默认仍为 v1。v2 支持 `none`、`readonly`、`verify`、`repair`；写入和高风险执行仍需
绑定冻结计划的审批。公开审计默认脱敏且仅支持 summary；`--trace-data sanitized` 只用于经审查 fixture，
`--trace-data private` 用于访问受控诊断。

## 宿主迁移与回退

1. 先升级 package，不修改 backend flag。
2. 对 `qre-v2` 运行宿主 contract kit，先验证 readonly、verify。
3. 只有写审批、sandbox/write-back 门禁通过后才启用 repair。
4. 对 policy decision、tool order、归一化 terminal reason、side-effect count 零容忍；final text 单独应用容差。
5. 回退时把新请求的 backend 改为 `qre`（打包 v1）或 `core`；禁止把 active Turn 或 v2 audit state 复制到 v1。

非法 backend 名称和非正数 model stream timeout 必须让宿主启动失败，不能静默落到其他引擎。

## 默认切换门禁

把 v2 设为默认并删除重复 v1 Agent Loop，必须同时满足全部自动门禁、至少两个 preview 发布，以及约定
观察窗口内无 Critical/High 执行语义回归。代码构建完成和本地 preview 包本身不能替代时间型观察门禁。
