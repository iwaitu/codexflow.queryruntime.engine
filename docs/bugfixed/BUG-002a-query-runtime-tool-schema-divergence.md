# BUG-002a: Query Runtime 工具协议分裂导致 Forge 后台任务空转

| 字段 | 值 |
|------|-----|
| **编号** | BUG-002a |
| **标题** | Query Runtime 工具协议分裂导致 Forge 后台任务空转 |
| **严重等级** | 🔴 Critical（已确认阻塞后台代码任务真实执行） |
| **发现时间** | 2026-04-11 |
| **最近校准** | 2026-04-11 |
| **状态** | 🟢 已修复 |

---

## 1. 结论

这是一个已经确认的设计级缺陷。

问题不在 `QueryRuntimeEngine` 的轮询状态机本身，而在于：

- `DefaultCodexKernel` 接入 runtime 时，把所有工具统一暴露成 `input_params` 包裹 schema
- `GatewayMessageProcessor` / `CodexController` 暴露的却是 typed / flat schema
- Forge prompt 又明确要求模型不要再包 `input_params` / `args` / `arguments`

最终结果是同一个系统里出现两套互相冲突的“模型可见协议”，导致 Forge 在后台任务中反复纠结工具调用格式，而不是进入真实代码修改。

这应被定义为：

> **Query Runtime 接入层的工具协议统一失败，导致 Forge runtime 路径与其它入口的 schema 不一致，进而触发后台作业空转。**

---

## 2. 已确认事实

### 2.1 Kernel runtime 路径暴露的是 `input_params` schema

**文件**：`CodexFlow.Core/Agents/DefaultCodexKernel.cs`

当前 runtime 接入代码：

```csharp
.Select(t => AIFunctionFactory.Create(async (Dictionary<string, object?> input_params, CancellationToken ct2) =>
{
    var args = ToolArgumentNormalizer.NormalizeCopy(input_params);
    return await t.ExecuteAsync(args, ct2).ConfigureAwait(false);
},
    name: t.Name ?? "unknown",
    description: t.Description ?? ""))
```

这意味着 Forge 在 runtime 中看到的函数签名天然偏向：

```json
{ "input_params": { ... } }
```

而不是：

```json
{ "path": "...", "start_line": 1 }
```

### 2.2 Gateway / Controller 路径暴露的是 typed / flat schema

**文件**：

- `CodexFlow/Gateway/GatewayMessageProcessor.cs`
- `CodexFlow/Controllers/CodexController.cs`
- `CodexFlow/Controllers/SimpleCodexController.cs`

示例：

- `Gateway` 的 `ivilson_read`：

```csharp
async (string path, int? start_line = null, int? end_line = null, CancellationToken ct = default) =>
```

- `Controller` 的 `ivilson_read`：

```csharp
async (string path, int? start_line = null, int? end_line = null, CancellationToken ct = default) =>
```

- `SimpleCodexController` 的 `run_command`：

```csharp
async (object command, string? cwd, CancellationToken ct) =>
```

结论：

- `Kernel runtime` 路径与 `Gateway/Controller` 路径的模型可见 schema 不一致
- 这是当前日志中“Forge 对参数格式反复摇摆”的直接根因

### 2.3 Forge prompt 明确禁止 `input_params`

**文件**：`CodexFlow.Core/Constants/CodexPrompts.cs`

Forge prompt 当前已经明确写了：

- 禁止包 `args`
- 禁止包 `arguments`
- 禁止包 `input_params`

因此当前系统实际状态是：

- prompt 要求模型不要包
- runtime schema 却让模型看到“应该包”

这是协议层自相矛盾，不是单纯模型能力问题。

### 2.4 真实日志已经证明后台 job 在空转

**日志**：`CodexFlow/bin/Debug/net10.0/logs/fulltext.log`

在 session `29f1acefc0b845929bd092805a1b69e3`、job `01KNXSV2PK4E56XB951YR678XW` 的本次重跑中，Forge 明确出现以下行为：

1. 先说“参数格式不对，不是 `input_params`，应直接传参数”
2. 下一轮又改口说“应该使用 `input_params` 这样的格式”
3. 持续围绕 `run_command` / `list_workspace` / 工作区路径打转
4. 没有进入有效代码修改、构建验证、任务完成阶段

这不是偶发 hallucination，而是工具协议冲突引发的系统性空转。

### 2.5 `ZeroToolCalls` 误判问题已修复，但不等于工具链已健康

本次之前已经修复：

- runtime 的 `TotalToolCalls`
- runtime 的 `WriteToolCalls`
- `CodexResponse` 回传计数

因此现在的核心问题已从：

> “明明调用了工具却被误判成 0 次”

转移为：

> “模型确实在调用工具，但调用协议不稳定，导致无法进入有效执行”

---

## 3. 影响范围

### 3.1 直接影响

1. `ExecuteCodeTask` 类型后台任务
2. `DefaultCodexKernel` 走 runtime 路径的 Forge 执行
3. 原子代码任务的真实落盘修改
4. `run_command` / `ivilson_read` / `list_workspace` 等工具的稳定调用

### 3.2 间接影响

1. Orchestrator 重试次数被无意义消耗
2. 后台 job 长时间停留在 `Running`
3. 用户误以为任务在施工，实际只有协议层空转
4. Telemetry 中 `tool_call_count > 0` 但业务无进展，污染 runtime 稳定性评估

### 3.3 潜在扩散面

若不修复统一适配层，后续新增工具将继续出现：

- 某些入口 typed
- 某些入口 wrapped
- prompt 又只写其中一种

最终会让 runtime 升级本身失去可信度。

---

## 4. 根因分析

### Root Cause 1：工具可见协议没有统一定义

当前系统缺少一个“模型可见 schema 的单一事实来源”。

后果：

- 同名工具在不同入口由不同开发方式暴露
- 参数 schema 不同
- 说明文案不同
- few-shot 不同

### Root Cause 2：内部包装细节泄漏给模型

`input_params` 本应是服务端内部兼容细节，但现在通过 runtime 接入代码直接暴露给了模型。

后果：

- 模型把内部适配协议当作业务协议来推理
- 和 prompt 的“直传参数”要求发生冲突

### Root Cause 3：缺少跨入口 schema 一致性测试

当前没有测试保障：

- `Kernel runtime`
- `Gateway`
- `Controller`

对同名工具暴露出的是同一套 schema。

### Root Cause 4：工具文档与实际入口不共享

工具 description、prompt few-shot、AIFunction 暴露方式分别维护。

后果：

- 修 prompt 不会自动修 schema
- 修 schema 不会自动修文档
- 系统容易再次漂移

---

## 5. 修复目标

本次修复必须同时达成以下目标：

1. 同名工具在三条入口暴露完全一致的模型可见 schema
2. `input_params` / `args` / `arguments` 不再出现在模型主路径协议中
3. 服务端内部仍兼容历史容器格式
4. Forge prompt、repair prompt、tool description 与实际 schema 完全一致
5. 废弃工具名从主链路中彻底移除，避免模型继续学习旧名称
6. 后台 `ExecuteCodeTask` 能进入真实读文件、改代码、验证，而不是协议空转

---

## 6. 实施方案

### Phase A：统一工具暴露工厂

#### A.1 新增共享适配层

新增统一工厂，例如：

- `ToolFunctionFactory`
- 或 `CodexToolFunctionAdapterFactory`

职责：

1. 从 `ICodexTool` 生成标准 `AIFunction`
2. 统一工具名、描述、参数 schema
3. 隐藏内部包装细节
4. 在服务端内部把 flat 参数映射回 `Dictionary<string, object?>`

#### A.2 三条入口全部迁移到共享工厂

迁移范围：

- `DefaultCodexKernel`
- `GatewayMessageProcessor`
- `CodexController`
- `SimpleCodexController`

目标：

- 不再在每个入口手写 `AIFunctionFactory.Create(...)`
- 同名工具自动得到一致 schema

### Phase B：取消模型可见的 `input_params` 包装

#### B.1 Kernel runtime 改为 flat schema 暴露

把当前：

```csharp
async (Dictionary<string, object?> input_params, CancellationToken ct2) =>
```

替换为共享工厂生成的 flat/typed schema。

#### B.2 保留服务端兼容层

工具执行层仍允许兼容以下历史格式：

- `input_params`
- `args`
- `arguments`
- `parameters`
- `params`

但这只能存在于服务端归一化逻辑里，不能继续暴露给模型。

### Phase C：统一读文件工具主协议并清退旧名称

#### C.1 设定唯一主入口

Forge 主路径只允许一个读工具：

- `ivilson_read`

扩展方案：

- 支持 `mode: "hashline"`
- 支持范围读取
- 支持普通浏览

#### C.2 移除 `read_file_content`

要求：

- 从 Forge prompt、repair prompt、few-shot、tool description 中删除 `read_file_content`
- 从主工具注册表中移除 `read_file_content`
- Hashline 场景统一改为 `ivilson_read({ "path": "...", "mode": "hashline" })`
- 任何旧名称只允许出现在迁移说明或历史文档中，不得继续出现在模型可见主路径

### Phase D：统一写工具与命令工具协议

重点工具：

- `run_command`
- `list_workspace`
- `ivilson_ls`
- `ivilson_read`
- `write_file`
- `smart_patch`
- `apply_patch`

要求：

- 在三个入口参数名一致
- 必填/可选规则一致
- description 示例一致

### Phase E：修正文档、prompt 与修复提示

统一更新：

1. Forge prompt
2. Gateway prompt
3. tool description
4. `BuildZeroToolCallsRepairPrompt`
5. `BuildHashlineMismatchRepairPrompt`
6. 其它 few-shot 示例

禁止再出现：

- 一处教 `input_params`
- 一处教直传参数
- 一处还在教废弃工具名

---

## 7. 代码任务拆分

### 7.1 按文件落地的改造清单

以下清单按“必须改动的代码文件”组织，目标是让实现可以直接排期和分配。

#### A. Kernel / Runtime 主链路

**1. `CodexFlow.Core/Agents/DefaultCodexKernel.cs`**

改造目标：

- 移除 runtime 路径中面向模型的 `Dictionary<string, object?> input_params` 暴露方式
- 统一改走共享 `ICodexTool -> AIFunction` 适配工厂
- 保证 runtime 返回的 tool schema 与 Gateway / Controller 一致

具体改动：

- 重构 `RunLoopWithRuntimeAsync()` 中 `AvailableTools` 的构造逻辑
- 删除直接在该文件内手写 `AIFunctionFactory.Create(async (Dictionary<string, object?> input_params, ...))`
- 改为调用统一工厂生成 `AIFunction`
- 保留服务端内部参数归一化，但不再把 `input_params` 作为模型可见签名

验收点：

- runtime 暴露给 Forge 的 `ivilson_read`、`run_command`、`list_workspace` 为 flat/typed schema
- 日志中不再出现模型围绕 `input_params` 自我纠缠

**2. `CodexFlow.Core/Runtime/QueryRuntimeEngine.cs`**

改造目标：

- 确认 runtime 执行层只消费统一 schema，不再隐式依赖旧包装格式

具体改动：

- 核查工具调用请求进入执行器时的参数形态
- 若仍存在针对 `input_params` 的特殊处理，收敛到统一归一化入口
- 保持 `ToolClassification`、`TotalToolCalls`、`WriteToolCalls` 统计逻辑不被本次改造破坏

验收点：

- runtime 工具执行前后的参数与 telemetry 正常
- 不引入新的 `ZeroToolCalls` / `WriteToolCalls` 回归

**3. `CodexFlow.Core/Runtime/DefaultToolExecutionCoordinator.cs`**

改造目标：

- 将历史包装兼容限制在执行协调层，不再泄漏为模型协议

具体改动：

- 保留对 `args` / `arguments` / `input_params` 等旧容器的兼容解包
- 明确注释：这里只是服务端兼容层，不是模型主协议
- 如果当前 fallback 逻辑会影响新 schema，可收敛为统一的 `ToolArgumentNormalizer` 入口

验收点：

- 旧调用数据仍可执行
- 新调用路径不需要依赖 fallback 才能成功

#### B. 统一工具适配层

**4. 新增共享适配文件**

建议新增文件：

- `CodexFlow.Core/Agents/ToolFunctionFactory.cs`
- 或 `CodexFlow.Core/Agents/CodexToolFunctionAdapterFactory.cs`

改造目标：

- 建立唯一的 `ICodexTool -> AIFunction` 转换层

职责：

- 统一工具名、描述、参数 schema
- 对模型暴露 flat/typed schema
- 在服务端内部完成 `Dictionary<string, object?>` 映射
- 为多个入口复用，消除手写 schema 漂移

验收点：

- `DefaultCodexKernel`、`GatewayMessageProcessor`、`CodexController`、`SimpleCodexController` 均可复用

**5. `CodexFlow.Core/Abstractions` 或相关公共目录**

如需新增公共契约，建议补充：

- 工具 schema 描述模型
- 参数定义元数据
- 共享 helper / normalizer

目标：

- 避免不同入口重复拼接参数定义

#### C. 读文件工具收敛

**6. `CodexFlow.Core/Agents/Tools/ReadFileTool.cs`**

改造目标：

- 清退 `read_file_content` 这个工具名
- 由统一主工具 `ivilson_read` 承担普通读取与 `mode="hashline"` 快照读取

具体改动：

- 将工具名从 `read_file_content` 收敛到 `ivilson_read`，或将 `ReadFileTool` 下沉为 `ivilson_read` 的实现
- 保留 `mode="hashline"`、`window_start_line`、`window_line_count` 能力
- 更新 description，删除所有“调用 `read_file_content(...)`”示例
- 错误恢复说明统一改为重新调用 `ivilson_read({ "path": "...", "mode": "hashline" })`

验收点：

- 模型可见工具列表中不再出现 `read_file_content`
- Hashline 能力完整保留

**7. `CodexFlow.Core/Agents/Tools/ApplyPatchTool.cs`**

改造目标：

- 修正 hashline 编辑文案，避免继续引用废弃工具名

具体改动：

- 将说明、错误恢复、示例中的 `read_file_content(mode="hashline")` 全部替换为 `ivilson_read(..., mode="hashline")`
- 确保对高风险文件的编辑前置要求仍保留

验收点：

- apply-patch 相关说明与新主读工具完全一致

**8. `CodexFlow.Core/Agents/SmartPatchTool.cs`**

改造目标：

- 与 `ApplyPatchTool` 同步收敛读工具名称

具体改动：

- 更新所有 `read_file_content` 相关说明、示例、错误恢复文案

验收点：

- smart-patch 文档与 apply-patch 一致

**9. `CodexFlow.Core/Hashline/Constants/HashlineErrorCodes.cs`**

改造目标：

- 统一错误提示文案，避免错误恢复路径继续教旧名称

具体改动：

- 将 `必须重新 read_file_content(mode="hashline")` 改为 `必须重新 ivilson_read({ "path": "...", "mode": "hashline" })`

验收点：

- 用户和模型从错误信息中只能学到新工具名

#### D. Prompt / Repair Prompt / Shared Prompt

**10. `CodexFlow.Core/Constants/CodexPrompts.cs`**

改造目标：

- 让 Forge 主提示词与实际工具链严格一致

具体改动：

- 删除 `read_file_content` 作为可调用工具名的描述
- 将“两个读文件工具”收敛为“唯一读文件工具 `ivilson_read`”
- 明确 `ivilson_read` 同时支持普通读取和 `mode="hashline"`
- 保留“禁止 `args` / `arguments` / `input_params` 包裹”的约束

验收点：

- Forge prompt 不再教旧工具名

**11. `CodexFlow.Contracts/SharedCodexPromptFragments.cs`**

改造目标：

- 收敛共享 Hashline 契约文案

具体改动：

- 将 `read_file_content({ "path": "<file>", "mode": "hashline" })` 替换为 `ivilson_read({ "path": "<file>", "mode": "hashline" })`
- 更新错误恢复与示例代码

验收点：

- Shared fragment 与 Forge prompt、Tool description 三者一致

**12. `CodexFlow.Core/Agents/CodexOrchestrator.cs`**

改造目标：

- 修复 repair prompt 与实际工具协议不一致的问题

具体改动：

- 更新 `BuildZeroToolCallsRepairPrompt`
- 更新 `BuildHashlineMismatchRepairPrompt`
- 更新其它仍引用旧工具名或旧参数包装方式的修复提示

验收点：

- orchestrator 自愈提示不再把模型带回旧协议

#### E. Gateway / Controller 入口对齐

**13. `CodexFlow/Gateway/GatewayMessageProcessor.cs`**

改造目标：

- 改走共享工具工厂，移除 Gateway 与 Kernel 的 schema 漂移

具体改动：

- 将工具定义从手写 `AIFunctionFactory.Create(...)` 迁移到统一工厂
- 调用前统一走 `ToolArgumentNormalizer.NormalizeCopy(...)`
- 对 `ivilson_read`、`run_command`、`search_in_files`、`list_workspace` 保持与 runtime 同一 schema

验收点：

- Gateway 与 runtime 暴露的同名工具 schema 一致

**14. `CodexFlow/Controllers/CodexController.cs`**

改造目标：

- 与 Gateway / Kernel 共用同一套工具暴露逻辑

具体改动：

- 将现有手写工具定义逐步迁移到统一工厂
- 保留控制器特有的授权/守卫逻辑
- 修复与读工具相关的旧名称引用
- 复核 scope/path 提取，确保不再出现 `.csproj -> .cs` 投影错误

验收点：

- Controller 不再成为独立的 schema 分叉源
- 作用域日志中的项目文件路径正确

**15. `CodexFlow/Controllers/SimpleCodexController.cs`**

改造目标：

- 避免简单入口继续暴露旧工具名称和旧 schema

具体改动：

- 将 `read_file_content` 的入口文案与注册改为 `ivilson_read`
- `run_command`、`write_file` 等签名对齐统一工厂

验收点：

- Simple controller 与主控制器工具协议一致

#### F. 工具注册与分类

**16. `CodexFlow.Core/Agents/ToolRegistryBootstrapper.cs`**

改造目标：

- 从注册层清退废弃工具名

具体改动：

- 移除或重命名 `ReadFileTool` 的注册方式，确保注册到模型侧的是 `ivilson_read`
- 核查 always-on 工具清单，避免同时出现两个读文件主工具

验收点：

- 工具注册表中只有一个读文件主入口

**17. `CodexFlow.Core/Agents/ToolClassification.cs`**

改造目标：

- 确保工具名清退后读写分类仍正确

具体改动：

- 更新分类规则，移除对废弃工具名的主路径依赖
- 保证 `ivilson_read(mode="hashline")` 仍归类为读工具

验收点：

- telemetry 分类稳定，不受工具重命名影响

#### G. 测试与回归

**18. `CodexFlow.Core.Tests/Runtime/RuntimeModelTests.cs`**

改造目标：

- 更新 runtime 相关断言，适配新读工具名与统一 schema

具体改动：

- 将 `read_file_content` 相关断言切换到 `ivilson_read`
- 增补 flat 参数与旧容器兼容测试

**19. `CodexFlow.Core.Tests/Hashline/HashlineToolIntegrationTests.cs`**

改造目标：

- 覆盖新主工具的 hashline 行为

具体改动：

- 将原 `ReadFileTool` / `read_file_content` 场景迁移到 `ivilson_read(mode="hashline")`
- 保证快照、窗口化读取、后续 patch 链路仍然通过

**20. `CodexFlow.Core.Tests/Tools/ToolsIntegrationTests.cs`**

改造目标：

- 回归基础读写工具名称与签名

具体改动：

- 更新读工具名称
- 补齐 `run_command`、`list_workspace`、`ivilson_read` 的 schema/行为一致性测试

**21. 新增跨入口 schema 一致性测试**

建议新增测试文件：

- `CodexFlow.Core.Tests/Agents/ToolSchemaConsistencyTests.cs`
- 或按现有测试目录归类

目标：

- 断言 `DefaultCodexKernel`、`GatewayMessageProcessor`、`CodexController` 对同名工具暴露完全一致的 schema

至少覆盖：

- `ivilson_read`
- `run_command`
- `list_workspace`
- `search_in_files`

**22. ExecuteCodeTask 集成回归**

建议补充或增强：

- `OrchestratorRetryLoopTests`
- `KernelRuntimeIntegrationTests`
- 后台 job 真实链路测试

目标：

- 验证 job 不再因工具协议冲突空转
- 验证后台任务会进入真实读文件、写入、命令执行、验证流程

### Task 1：抽共享工具适配工厂

**目标**：建立统一的 `ICodexTool -> AIFunction` 转换层。

**输出**：

- 新增共享工厂类
- 支持 typed / flat schema 生成
- 支持服务端兼容解包

**验收**：

- `DefaultCodexKernel` / `Gateway` / `Controller` 都能复用

### Task 2：迁移 `DefaultCodexKernel` runtime 工具暴露

**目标**：移除 runtime 路径中的 `input_params` 模型可见 schema。

**输出**：

- `RunLoopWithRuntimeAsync()` 改走统一工厂

**验收**：

- Forge runtime 暴露 schema 与 Gateway 一致

### Task 3：迁移 `GatewayMessageProcessor`

**目标**：消除 Gateway 和 Kernel 的工具 schema 漂移。

**输出**：

- `BuildGatewayTools()` 改走统一工厂或统一 schema 组装器

**验收**：

- `ivilson_read` / `run_command` / `search_in_files` schema 对齐

### Task 4：迁移 `CodexController` / `SimpleCodexController`

**目标**：让所有入口都由同一套工具暴露逻辑控制。

**验收**：

- 同名工具 schema 不再分叉

### Task 5：收敛读文件主协议

**目标**：Forge 主路径只保留一个读文件工具名称，不再保留历史别名。

**输出**：

- `ivilson_read` 支持 `mode="hashline"`（推荐）
- `read_file_content` 从主工具链中移除
- 所有 prompt / repair prompt / tool description / 示例统一改为 `ivilson_read`

**验收**：

- Forge prompt、tool schema、日志中不再出现 `read_file_content` 作为可调用工具名

### Task 6：修复作用域文件投影异常

**目标**：修复任务范围中 `.csproj -> .cs` 的错误投影。

**原因**：

- 当前日志仍出现 `src/CleanApp.Core/CleanApp.Core.cs`

**验收**：

- 作用域日志正确显示 `.csproj`

---

## 8. 测试计划

### 8.1 Schema 一致性测试

新增测试，断言同名工具在以下入口生成的 schema 完全一致：

- `DefaultCodexKernel`
- `GatewayMessageProcessor`
- `CodexController`

至少覆盖：

- `ivilson_read`
- `run_command`
- `search_in_files`
- `list_workspace` / `ivilson_ls`

### 8.2 兼容解包测试

验证服务端内部仍兼容：

- flat 参数
- `input_params`
- `args`
- `arguments`

但模型可见 schema 只保留 flat。

### 8.3 Forge runtime 集成测试

模拟 Forge 在 runtime 中调用：

- `ivilson_read({"path":"..."})`
- `run_command({"command":["dotnet","build"]})`

验证：

- 工具成功执行
- 不再出现参数协议争执

### 8.4 ExecuteCodeTask 真实链路回归

回归目标：

- job 不再长期卡在 `Running`
- 会进入真实工具执行
- 能产生有效写操作或真实失败

### 8.5 Telemetry 回归

验证：

- `tool_call_count`
- `TotalToolCalls`
- `WriteToolCalls`

在 runtime 路径下保持正确

---

## 9. 验收标准

修复完成后，应满足以下标准：

1. Forge 不再在日志中反复讨论 `input_params` / `args` / `arguments`
2. 后台 job 能进入真实读文件、改代码、构建验证流程
3. 同名工具在三条入口只有一套模型可见协议
4. `read_file_content` 不再作为可调用工具名暴露给模型
5. prompt / tool description / runtime schema 完全一致
6. 作用域日志中的项目文件路径不再错误投影
7. 真实后台任务不再因为协议冲突而空转

---

## 10. 优先级建议

建议按以下顺序修复：

1. **P0**：统一 Kernel runtime 工具暴露，移除模型可见 `input_params`
2. **P0**：补 schema 一致性测试
3. **P1**：统一 Gateway / Controller 工具工厂
4. **P1**：清退 `read_file_content`，统一到 `ivilson_read`
5. **P1**：修复 `.csproj` 作用域投影问题
6. **P2**：清理 legacy prompt 与旧工具文案

---

## 11. 本文档不解决的问题

本计划聚焦工具链协议统一与废弃工具名清退，不直接覆盖以下问题：

1. LLM 自身任务理解偏差
2. 某些具体工具内部可靠性问题（例如 `.csproj` patch 成功率）
3. validator / auditor 的独立缺陷
4. PostgreSQL / Redis / Mongo 等基础设施故障

这些问题仍然需要单独跟踪，但不应掩盖当前已确认的 runtime 工具协议缺陷。
