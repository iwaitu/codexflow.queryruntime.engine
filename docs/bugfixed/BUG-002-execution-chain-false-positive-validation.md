# BUG-002: 执行链误验收与前置依赖失真

| 字段 | 值 |
|------|-----|
| **编号** | BUG-002 |
| **标题** | 执行链误验收与前置依赖失真 |
| **严重等级** | 🔴 Critical |
| **发现时间** | 2026-04-11 |
| **最近校准** | 2026-04-11 |
| **状态** | 🔴 已确认，待系统修复 |

---

## 1. 结论

这次失败**不应归因于某个具体项目**，而是执行链存在系统性缺陷：

1. `plan` 能生成任务，但**没有把前置依赖做成硬约束**
2. `execute` 能提交后台任务，但**没有产出稳定的任务完成证据包**
3. `validator` 的 fallback 规则存在**误验收**，会把“未完成任务”记成成功
4. 后续任务会基于这个错误成功状态继续执行，形成**前置依赖失真**

本次链路中，`TASK_ARCH_002` 明明未完成，却被 `IsFallback=true Success=true`（日志格式为 `validatorFallback=True validationPassed=True`）放行，任务状态被设为 `CompletedWithWarnings`；随后 `TASK_ARCH_003` 在错误前提上执行并失败。

> **术语说明**：日志中出现的 `PassedWithFallback` 仅为日志描述文案（`CodexOrchestrator.cs:1720`），并非 `CodexTaskStatus` 枚举成员。实际状态枚举为 `CompletedWithWarnings` + `ValidationResult.IsFallback=true`。

因此，本 bug 的主根因排序如下：

1. **Validator / Fallback 设计缺陷**
2. **Execute 缺少结构化完成证据**
3. **Plan 缺少强前置依赖建模**

---

## 2. 影响范围

受影响的不是单个工具，而是整条执行链：

- 规划阶段：任务拆分与依赖声明
- 执行阶段：后台任务提交、原子任务执行、命令证据采集
- 验证阶段：LLM validator、deterministic fallback、任务通过判定
- 调度阶段：后续任务是否允许启动
- 记忆阶段：`LastExecutionOutcome` / `VerificationBaseline` 写回后的事实污染

这意味着：

- 某个任务可以**真实未完成但被记录为成功**
- `CompletedWithWarnings`（含 fallback 通过）被调度器视为终态成功，允许下游任务启动
- 项目记忆会学习到错误状态，进一步污染后续规划与验证

---

## 3. 关键事实

### 3.1 TASK_ARCH_002 在验证阶段多次被明确判定未完成

日志中多次出现明确失败结论：

- `MongoFileService.cs` 仍在 `Core/Services`
- `Infrastructure` 层没有 `MongoFileService.cs`
- `MongoDB.Driver` 仍在 `Core.csproj`

关键日志证据：

- `service-2026-04-11.log:4096`
- `service-2026-04-11.log:4431`
- `service-2026-04-11.log:4583`
- `service-2026-04-11.log:5195`
- `service-2026-04-11.log:5319`

### 3.2 但 TASK_ARCH_002 最终被 fallback 判定为成功

尽管前面已有明确的未完成证据，最终仍出现：

- `validatorFallback=True validationPassed=True`（对应代码：`IsFallback=true, Success=true`，日志模板在 `CodexOrchestrator.cs:1386`）
- `Task completed via deterministic fallback validation`
- `ExecuteCodeTask completed ... success=True`

关键日志证据：

- `service-2026-04-11.log:5327`
- `service-2026-04-11.log:5337`
- `service-2026-04-11.log:5358`
- `service-2026-04-11.log:5504`

这说明当前 fallback 规则对“关键文件状态”检查不足，可能只凭命令证据或不完整证据放行。

### 3.3 TASK_ARCH_003 的失败是合理结果，而不是主根因

`TASK_ARCH_003` 的任务目标是：

- 更新 `Program.cs` 中的 DI 配置
- 指向迁移后的 `Infrastructure` 实现
- 执行 `dotnet build`
- 执行 `dotnet test`

但其前置条件是：`TASK_ARCH_002` 必须真实完成迁移。

日志显示：

- `TASK_ARCH_003` 首次提交时被正确阻塞，因为 `TASK_ARCH_002` 仍在执行
- 等 `TASK_ARCH_002` 被错误标记成功后，`TASK_ARCH_003` 才真正启动
- 启动后 3 次验证都失败，最终 `success=False`

关键日志证据：

- 阻塞：`service-2026-04-11.log:4214`
- 启动：`service-2026-04-11.log:5505` - `5517`
- 三次失败：`service-2026-04-11.log:5774`、`5843`、`5926`
- 最终失败：`service-2026-04-11.log:5948`、`5949`

### 3.4 实际工作区证明 TASK_ARCH_002 没有真实完成

在执行完成后的工作区中，真实代码状态如下：

- `src/CleanApp.Core/Services/MongoFileService.cs` 仍存在
- `src/CleanApp.Infrastructure/Services/MongoFileService.cs` 不存在
- `src/CleanApp.Core/CleanApp.Core.csproj` 仍引用 `MongoDB.Driver`
- `src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj` 没有 `MongoDB.Driver`
- `src/CleanApp/Program.cs` 仍直接实例化 `CleanApp.Core.Services.MongoFileService`

关键文件证据：

- `CodexFlow/workspaces/ed7177e4-5cd2-40b0-95a5-782782c461db/176a7d1397724587b4e2ea65a6fd5353/cleanapp-bug/src/CleanApp/Program.cs`
- `CodexFlow/workspaces/ed7177e4-5cd2-40b0-95a5-782782c461db/176a7d1397724587b4e2ea65a6fd5353/cleanapp-bug/src/CleanApp.Core/Services/MongoFileService.cs`
- `CodexFlow/workspaces/ed7177e4-5cd2-40b0-95a5-782782c461db/176a7d1397724587b4e2ea65a6fd5353/cleanapp-bug/src/CleanApp.Core/CleanApp.Core.csproj`
- `CodexFlow/workspaces/ed7177e4-5cd2-40b0-95a5-782782c461db/176a7d1397724587b4e2ea65a6fd5353/cleanapp-bug/src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj`

---

## 4. 执行链关键证据时间线

### 4.1 规划成功生成了三项任务

规划器生成：

- `TASK_ARCH_001`
- `TASK_ARCH_002`
- `TASK_ARCH_003`

关键证据：

- `fulltext.log:31`

### 4.2 TASK_ARCH_001 完成

- 后台作业成功启动并完成
- 最终 `success=True`

关键证据：

- `service-2026-04-11.log:393` - `403`
- `service-2026-04-11.log:2980`
- `service-2026-04-11.log:2981`

### 4.3 TASK_ARCH_002 多轮失败后被错误 fallback 放行

- validator 多次指出迁移未完成
- orchestrator 在第 3 轮把它记成 `validatorFallback=True validationPassed=True`
- 后台作业最终 `success=True`

关键证据：

- `service-2026-04-11.log:4096`
- `service-2026-04-11.log:4590`
- `service-2026-04-11.log:5327`
- `service-2026-04-11.log:5337`
- `service-2026-04-11.log:5358`

### 4.4 TASK_ARCH_003 依赖错误状态继续执行并失败

- 首次因前序任务仍在执行被阻塞
- 前序任务被错误标绿后重新入队
- 3 轮验证均失败
- 最终 `success=False`

关键证据：

- `service-2026-04-11.log:4214`
- `service-2026-04-11.log:5505`
- `service-2026-04-11.log:5774`
- `service-2026-04-11.log:5843`
- `service-2026-04-11.log:5926`
- `service-2026-04-11.log:5948`

### 4.5 Validator 实际拿到过 build/test 成功证据，但仍未闭环

fulltext 显示 `TASK_ARCH_003` 验证过程中，validator 曾获取：

- `dotnet build` 成功
- `dotnet test` 成功

但随后仍继续追问代码变更证据：

- `Program.cs` 是否更新
- `MongoFileService` 的位置是否正确

并且 `run_command` 参数格式还在摇摆：

- “需要使用数组格式的 command”
- “需要修正参数格式”

关键证据：

- `fulltext.log:212`
- `fulltext.log:214`
- `fulltext.log:215`
- `fulltext.log:216`

这证明问题不是“没有命令证据”，而是“命令证据无法独立证明任务目标已完成”。

---

## 5. 根因分析

### 5.1 Plan 层缺陷：依赖声明未被运行时强制执行

当前 `CodexTask` 模型（`CodexModels.cs`）已声明以下字段：

- `Dependencies`（`Collection<string>`，任务 ID 列表）— 第 24 行
- `Inputs`（`Collection<string>`）— 第 25 行
- `Outputs`（`Collection<string>`）— 第 26 行

但 `FindNextExecutableTask`（`CodexOrchestrator.cs:900-905`）在选取下一个任务时，**仅检查 `Status == Pending || Failed`，完全不检查 `Dependencies` 是否已全部完成**。`Dependencies` 由规划器 LLM 填充，却从未被调度器当作硬约束。

此外，以下约束尚不存在：

- 若前序任务状态为 `CompletedWithWarnings` 且 `IsFallback=true`，后续高风险任务默认禁止启动
- `Inputs`/`Outputs` 未参与调度决策（无法表达”关键产物必须落地”）

结果：

- 后续任务只能依赖”任务状态枚举”，不能依赖”前置关键产物状态”
- `Dependencies` 字段形同虚设，顺序依赖完全靠规划器生成的任务列表顺序隐式保证

### 5.2 Execute 层缺陷：没有结构化完成证据包

执行器目前会：

- 调用工具
- 修改文件
- 运行命令

但不会稳定输出一份统一的、可校验的 evidence：

- 修改了哪些文件
- 删除了哪些旧文件
- 新增了哪些目标文件
- 关键字符串是否存在/不存在
- 对应 build/test 证据是什么

结果：

- validator 只能从零散日志和工具输出中拼装事实
- fallback 更容易在证据缺失时误判

### 5.3 Validator 层缺陷：fallback 检查逻辑基于关键字匹配，无法感知迁移类任务的文件约束

这是本次主根因。

已知事实：

- validator 已明确发现关键文件状态不满足
- 但 fallback 仍判定通过

`TryDeterministicValidation`（`DefaultCodexValidator.cs:384-536`）的实际实现逻辑为：

1. 对 analysis 类任务，检查是否有只读证据
2. 对任务描述文本做**关键字匹配**（如 `taskText.Contains(“ulid”)`、`taskText.Contains(“工厂”)`），命中后检查特定文件
3. 对包含 `”dotnet build”` / `”测试”` 等关键字的，检查 build/test 证据
4. 对 code 类任务，检查是否有写入证据

**致命缺陷**：当任务描述不包含硬编码关键字时，`checksAttempted` 保持为 0，方法进入第 482 行分支直接返回 `null`（即”不阻断”），被上游等价于”无反驳证据即通过”。`TASK_ARCH_002` 的迁移任务描述不包含 `”ulid”` 或 `”工厂”` 等硬编码关键字，因此 fallback 完全跳过了文件状态检查。

这说明当前 deterministic fallback 逻辑存在系统性缺陷：

1. **文件状态断言是硬编码的特例，不是通用的结构化断言机制**——只有 `ulid`、`工厂` 等少数关键字能触发文件检查
2. 对命令证据（build/test）过度信任——即使 build 通过也不能证明迁移已完成
3. 当 `checksAttempted == 0` 时，默认返回 `null`（不阻断），等价于”无检查即通过”
4. 没有把”已观察到的明确失败事实”（前几轮 LLM validator 指出的文件状态问题）作为 fallback 的硬阻断条件

---

## 6. 推荐修复总思路：任务契约贯穿执行链

建议不要只修某个 validator 分支，也不要只在某个项目上加 prompt 约束，而是把 `plan → execute → validator → scheduler` 统一到一套**任务契约（Task Contract）** 上。

核心原则：

1. `plan` 负责定义验收契约，而不是只生成自然语言任务描述
2. `execute` 负责产出契约要求的结构化证据，而不是只产生日志
3. `validator` 只按契约验收，LLM 解释不能覆盖硬断言
4. `scheduler` 只允许依赖“契约满足”的前序任务继续驱动下游任务

### 6.1 Plan 阶段应输出的契约字段

建议在任务模型中新增或正式启用以下字段：

- `AcceptanceCriteria`
- `RequiredArtifacts`
- `ForbiddenStates`
- `ValidationCommands`
- `DependsOn`
- `UnsafeIfDependencyFallbackPassed`

其中：

- `AcceptanceCriteria`：任务完成必须满足的最终状态
- `RequiredArtifacts`：必须存在/不存在/包含/不包含的文件与内容断言
- `ForbiddenStates`：一旦命中即直接失败的状态
- `ValidationCommands`：需要执行并留存结果的命令
- `DependsOn`：前置任务
- `UnsafeIfDependencyFallbackPassed`：若前序任务仅 fallback 通过，是否禁止本任务继续执行

### 6.2 以 TASK_ARCH_002 为例的契约表达

不应只写：

- “迁移 MongoFileService 到 Infrastructure 层”

而应结构化表达为：

- `RequiredArtifacts`
  - `src/CleanApp.Infrastructure/Services/MongoFileService.cs` must exist
  - `src/CleanApp.Core/Services/MongoFileService.cs` must not exist
  - `src/CleanApp.Core/CleanApp.Core.csproj` must not reference `MongoDB.Driver`
  - `src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj` must reference `MongoDB.Driver`
- `ForbiddenStates`
  - `Program.cs` still instantiates `CleanApp.Core.Services.MongoFileService`
- `ValidationCommands`
  - `dotnet build`

这样 validator 校验的是契约，不是猜测任务目标。

### 6.3 Execute 阶段应交付的证据包

每个原子任务执行结束时，都应输出统一 evidence，例如：

- `ChangedFiles`
- `CreatedFiles`
- `DeletedFiles`
- `AssertionsEvaluated`
- `AssertionResults`
- `CommandResults`
- `TaskScopedReads`

其中 `AssertionResults` 至少包含：

- 断言名称
- 断言类型
- 断言结果
- 对应文件/命令证据

这样 validator 不需要从零散日志中回溯事实。

### 6.4 Validator 的正确职责

validator 应按以下顺序工作：

1. 先验证 `RequiredArtifacts` / `ForbiddenStates`
2. 再验证 `ValidationCommands`
3. 最后由 LLM 生成简洁解释或补充风险说明

换句话说：

- 文件存在/不存在
- 包引用是否迁移
- 关键 DI 是否指向新实现
- build/test 是否成功

这些必须是 deterministic hard checks。  
LLM 只能解释，不应覆盖硬失败。

### 6.5 Scheduler 的正确放行条件

调度器不能只看前序任务是否是成功态，而应看：

1. 前序任务是否 `Success`
2. 前序任务契约是否全部满足
3. 若前序任务是 `CompletedWithWarnings + IsFallback=true`，本任务是否允许在 fallback 前提下继续执行

这能避免“状态看似成功，产物其实未落地”的假通过扩散到下游任务。

---

## 7. 系统级修复建议

### 7.1 P0：收紧 validator fallback

必须新增规则：

- 一旦本轮或前几轮验证已观察到明确失败事实，fallback **禁止判通过**
- 将 `TryDeterministicValidation` 的文件状态检查从硬编码关键字匹配改为通用的结构化断言机制：
  - 支持声明"旧文件不存在"、"新文件存在"、"包引用迁移完成"、"DI 指向新实现"等断言
  - 断言来源应从任务描述的 `Inputs`/`Outputs` 字段自动推导，而非依赖关键字
- `CompletedWithWarnings` 且 `IsFallback=true` 的任务不得用于关键结构性重构任务的放行，除非所有文件状态断言满足

### 7.2 P0：将现有 TaskExecutionEvidence 提升为任务输出的结构化证据包

当前 `DefaultCodexValidator` 内部已有 `TaskExecutionEvidence` 记录（`DefaultCodexValidator.cs:21-33`），包含：

- `HasReadOnlyEvidence`、`HasWriteEvidence`
- `HasSuccessfulBuildEvidence`、`HasSuccessfulTestEvidence`
- `HasRunCommandContractError`、`HasValidatorEmptyResponse`

但该记录仅在 validator 内部通过日志扫描（`CollectTaskExecutionEvidence`）生成，不对外暴露。

需要做的是：

1. 将 `TaskExecutionEvidence` 从 validator 内部类型提升为任务执行结果的标准字段
2. 扩展以覆盖当前缺失的维度：
   - `ChangedFiles` — 实际变更的文件列表（可复用 `GitService.GetChangedFilesAsync`）
   - `DeletedFiles` — 删除的旧文件
   - `CreatedFiles` — 新增的目标文件
   - `RequiredAssertionsPassed` — 任务声明的文件状态断言是否满足
   - `CommandEvidence` — build/test 命令结果
3. validator 只消费这份 evidence 和必要的最终读文件结果，避免靠零散日志拼接事实

### 7.3 P1：激活现有依赖建模并增强调度约束

`CodexTask` 模型已有 `Dependencies`、`Inputs`、`Outputs` 字段（`CodexModels.cs:24-26`），但均未被调度器使用。需要：

1. **`FindNextExecutableTask` 必须检查 `Dependencies`**：前置任务全部处于终态成功（`Success`）才允许启动，`CompletedWithWarnings` 且 `IsFallback=true` 不算满足
2. **新增 `UnsafeIfDependencyFallbackPassed` 标记**（布尔属性）：高风险任务可声明"前置任务经 fallback 通过时，默认阻止启动"
3. **将 `Inputs`/`Outputs` 参与调度决策**：前置任务的 `Outputs` 必须在后继任务的 `Inputs` 路径中实际存在

### 7.4 P1：收紧后台调度放行规则

当某任务依赖的前序任务为：

- `Failed`
- `CompletedWithWarnings` 且 `IsFallback=true`（即日志中的 `PassedWithFallback`）
- `Success but required artifacts missing`

则默认阻止下游任务入队，除非显式 override。

### 7.5 P1：统一 validator 的证据优先级

证据优先级应固定为：

1. 关键文件状态断言
2. 任务范围内的实际变更
3. build/test 命令证据
4. LLM 解释性判断

不能让第 3、4 项覆盖第 1 项。

---

## 8. 验收标准

修复完成后，应满足：

1. 类似 `TASK_ARCH_002` 这种未迁移完成的任务，**绝不允许**被 fallback 判为成功
2. `TASK_ARCH_003` 这类依赖型任务，若前置产物未落地，系统应明确阻止启动
3. 每个原子任务完成后，都能拿到结构化 evidence
4. `CompletedWithWarnings` 且 `IsFallback=true` 不再被调度器等价于 `Success`，也不得污染 `LastExecutionOutcome` 为普通成功
5. 项目记忆写回前，必须区分以下状态（`BlockedByDependency` 为待新增枚举值）：
   - `Success` — 真正通过
   - `CompletedWithWarnings`（含 `IsFallback=true`）— 需标注 fallback 来源
   - `Failed` — 明确失败
   - `BlockedByDependency`（**待新增**）— 因前置依赖不满足而被阻止

---

## 9. 本报告与其它 BUG 的关系

### BUG-001（工具参数解析失败）

`BUG-001` 关注的是**工具参数解析兼容性风险**。
本报告关注的是**执行链系统性误验收**。

两者有关联，但层级不同：

- `BUG-001` 是局部工具协议/兼容问题
- `BUG-002` 是 plan/execute/validator 协同失败导致的系统级错误成功判定

本次链路中，`run_command` 参数格式摇摆属于 `BUG-001` 范畴的次级干扰项；
真正导致错误任务状态传播的是本报告描述的 `BUG-002`。

### BUG-002a（Query Runtime 工具协议分裂）

`docs/bugfixes/BUG-002a-query-runtime-tool-schema-divergence.md`（原 BUG-002）关注的是 **DefaultCodexKernel 接入 runtime 时的工具 schema 分裂**问题，状态为已修复。

该问题与本报告（执行链误验收）是完全不同的缺陷，已重编号为 BUG-002a 以避免混淆。

---

## 10. 任务契约结构示例

若后续要正式落地，建议在任务模型层显式增加如下结构：

```json
{
  "Id": "TASK_ARCH_002",
  "Title": "[P0] 重构 Core 层架构依赖 - 迁移 MongoFileService 到 Infrastructure 层",
  "DependsOn": ["TASK_ARCH_001"],
  "AcceptanceCriteria": [
    "MongoFileService 实现位于 Infrastructure 层",
    "Core 层不再直接依赖 MongoDB.Driver"
  ],
  "RequiredArtifacts": [
    {
      "type": "file_exists",
      "path": "src/CleanApp.Infrastructure/Services/MongoFileService.cs"
    },
    {
      "type": "file_not_exists",
      "path": "src/CleanApp.Core/Services/MongoFileService.cs"
    },
    {
      "type": "file_not_contains",
      "path": "src/CleanApp.Core/CleanApp.Core.csproj",
      "text": "MongoDB.Driver"
    },
    {
      "type": "file_contains",
      "path": "src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj",
      "text": "MongoDB.Driver"
    }
  ],
  "ForbiddenStates": [
    {
      "type": "file_contains",
      "path": "src/CleanApp/Program.cs",
      "text": "new MongoFileService(config)"
    }
  ],
  "ValidationCommands": [
    {
      "command": ["dotnet", "build"]
    }
  ],
  "UnsafeIfDependencyFallbackPassed": true
}
```

这类结构不要求一步到位全部实现，但至少应先让 `plan` 能产出，`validator` 能消费，`scheduler` 能识别高风险依赖。

---

## 11. 建议实施顺序

1. 先修 `validator fallback`
2. 再给 `execute` 增加结构化 evidence
3. 最后增强 `plan` 和调度器的依赖建模

如果顺序反过来，只修 `plan` 或只修工具 schema，仍然挡不住“未完成任务被错误标绿”的主问题。

---

## 12. 按代码文件落地的修复清单

以下清单按“先止血、后收敛、再增强”的顺序编排。目标不是单点修补，而是把任务契约、执行证据、验证规则、调度放行统一起来。

### 12.1 P0：Validator 与 Fallback 止血

#### `CodexFlow.Core/Agents/DefaultCodexValidator.cs`

需要修改：

- 重构 `TryDeterministicValidation(...)`
- 禁止 `checksAttempted == 0` 时返回 `null` 并被上游等价放行
- 增加通用文件断言执行器，替代当前基于关键字的硬编码分支
- 把“前几轮已观察到明确失败事实”纳入 fallback 的硬阻断条件
- 将现有 `TaskExecutionEvidence` 扩展为可承载文件断言、命令断言、变更断言的标准证据结构

具体改动：

- 新增 `EvaluateRequiredArtifacts(...)`
- 新增 `EvaluateForbiddenStates(...)`
- 新增 `EvaluateValidationCommands(...)`
- 若任一硬断言失败，直接返回失败 verdict，不允许 fallback 改判成功
- 若任务没有可执行断言，返回“验证配置不足”，而不是默认不阻断

验收点：

- 像 `TASK_ARCH_002` 这类迁移任务，在旧文件仍存在、新文件不存在时，必须稳定失败
- `CompletedWithWarnings + IsFallback=true` 不得由“无检查”导出

#### `CodexFlow.Core/Agents/CodexOrchestrator.cs`

需要修改：

- 收紧 fallback 放行逻辑
- 明确区分：
  - 正常通过
  - fallback 通过
  - 验证配置不足
  - 明确失败
- 若 validator 返回“配置不足”或“存在已知失败事实”，不得进入成功分支

具体改动：

- 调整 `Forge attempt summary` 后的成功判定
- 对 `ValidationResult.IsFallback=true` 增加更严格的状态分流
- 禁止将 `CompletedWithWarnings + IsFallback=true` 视为可安全依赖的成功态
- 进度日志中明确输出 fallback 的依据，不再只给出“关键目标与命令证据满足”这种宽泛摘要

验收点：

- `TASK_ARCH_002` 场景下不得再出现 `validatorFallback=True validationPassed=True`
- 明确失败事实不能被 fallback 覆盖

### 12.2 P0：Execute 输出结构化证据

#### `CodexFlow.Core/Agents/CodexOrchestrator.cs`

需要修改：

- 在每个原子任务执行结束时汇总结构化证据
- 将证据挂入任务执行结果，而不是只散落在日志中

建议新增字段：

- `ChangedFiles`
- `CreatedFiles`
- `DeletedFiles`
- `AssertionResults`
- `CommandResults`
- `TaskScopedReads`

具体现实来源：

- 文件变更：可结合 `GitService.GetChangedFilesAsync` 或 workspace diff
- 命令结果：从 `run_command` 结果标准化提取
- 读文件证据：从 `ReadFileTool` / hashline snapshot 中提取

#### `CodexFlow.Core/Agents/GitService.cs`

需要修改：

- 提供面向任务范围的变更摘要接口
- 支持按 workspace 返回：
  - 修改文件
  - 新增文件
  - 删除文件

验收点：

- validator 不需要再从 service/fulltext 日志反推文件状态
- 单个任务结束后，可直接读取到结构化 evidence

### 12.3 P0：任务模型显式承载契约

#### `CodexFlow.Core/Models/CodexModels.cs`

需要修改：

- 在 `CodexTask` 上正式增加或启用以下字段：
  - `AcceptanceCriteria`
  - `RequiredArtifacts`
  - `ForbiddenStates`
  - `ValidationCommands`
  - `UnsafeIfDependencyFallbackPassed`

说明：

- 当前已有 `Dependencies` / `Inputs` / `Outputs`
- 但还缺少足够表达验收硬条件的结构

建议：

- `RequiredArtifacts` 定义为结构化对象列表，而不是字符串
- `ForbiddenStates` 同样使用结构化对象
- `ValidationCommands` 使用标准命令数组表示，避免再落回文本解析

验收点：

- planner 输出的任务 JSON 能完整描述“如何判断任务完成”

### 12.4 P1：Planner 产出任务契约

#### `CodexFlow.Core/Agents/DefaultCodexPlanner.cs`

需要修改：

- 让 planner 除 `Title/Description` 外，同时输出任务契约字段
- 对迁移类、重构类、DI 类、编译类任务生成不同模板的契约

建议新增逻辑：

- 识别“迁移/移动/重构依赖”类任务时，自动生成：
  - 新文件存在
  - 旧文件不存在
  - 包引用迁移
  - DI 指向新实现
- 识别“编译/测试验证”类任务时，自动生成：
  - `dotnet build`
  - `dotnet test`
  - 必要的目标文件检查

#### `CodexFlow.Core/Constants/CodexPrompts.cs`

需要修改：

- planner prompt 明确要求输出结构化任务契约
- 不再只输出自然语言描述
- 约束模型为每个任务写明：
  - `DependsOn`
  - `RequiredArtifacts`
  - `ForbiddenStates`
  - `ValidationCommands`

验收点：

- 新生成的任务列表不再只有“描述”，而是带验收结构

### 12.5 P1：调度器真正执行依赖检查

#### `CodexFlow.Core/Agents/CodexOrchestrator.cs`

需要修改：

- `FindNextExecutableTask(...)` 必须检查 `Dependencies`
- 不能再只按 `Status == Pending || Failed` 选择任务

具体规则：

- 前置任务必须全部为可靠成功态
- 若前置任务为 `CompletedWithWarnings + IsFallback=true`
  - 默认不满足依赖
  - 除非当前任务显式允许依赖 fallback 结果
- 若前置任务的 `RequiredArtifacts` 未满足
  - 即便状态是成功，也不能放行

验收点：

- `TASK_ARCH_003` 这类任务在 `TASK_ARCH_002` 产物未落地时，必须稳定阻塞

#### `CodexFlow/Controllers/CodexController.cs`

需要修改：

- `execute_code_task` 提交前，对任务依赖状态做显式检查
- 返回更清晰的阻塞原因：
  - 前序任务执行中
  - 前序任务 fallback 通过但未满足高风险放行条件
  - 前序任务产物断言未满足

验收点：

- API 不再只说“正在执行中”，而能说清“因依赖契约未满足而阻塞”

### 12.6 P1：记忆与状态写回去污染

#### `CodexFlow.Core/Services/DefaultMemoryOrchestrator.cs`

需要修改：

- 写回 `LastExecutionOutcome` 前区分成功类型
- `CompletedWithWarnings + IsFallback=true` 不得等价写成普通成功

#### `CodexFlow.Core/Services/DefaultFactGuardService.cs`

需要修改：

- 对 `VerificationBaseline` 的冲突写入增加来源级别
- 区分：
  - deterministic pass
  - fallback pass
  - failed
  - blocked

#### `CodexFlow.Core/Services/DefaultDriftGovernor.cs`

需要修改：

- 仅在可靠成功或明确失败后触发对应的向量/记忆刷新策略
- fallback 成功应打上弱可信标记，避免污染后续任务的默认前提

验收点：

- 下游 recall 不再把 fallback 通过当成与真实通过同等级的事实

### 12.7 P1：Background Job 收尾语义更精确

#### `CodexFlow/Services/Background/BackgroundJobRunner.cs`

需要修改：

- job 收尾状态要保留更细粒度结果，而不是只映射成 `success=True/False`
- 至少要能区分：
  - 真正成功
  - fallback 成功
  - 验证失败
  - 依赖阻塞

验收点：

- 后台作业历史接口返回的不是模糊成功，而是可审计的执行结论

#### `CodexFlow/Controllers/BackgroundJobController.cs`

需要修改：

- 历史查询与 session jobs 查询接口暴露更精细的执行状态和原因

### 12.8 P2：测试补齐

#### `CodexFlow.Core.Tests/Agents/`

需要新增：

- `DeterministicValidationTests`
  - 迁移任务旧文件仍存在时必须失败
  - `checksAttempted == 0` 不能判通过
- `TaskDependencySchedulingTests`
  - 前置任务 fallback 成功时，下游高风险任务默认阻塞
- `TaskContractPlannerTests`
  - planner 输出契约字段完整
- `ExecutionEvidenceAggregationTests`
  - 执行结束后能收集到结构化 evidence

#### `CodexFlow.Tests/Controllers/`

需要新增：

- `ExecuteCodeTaskDependencyGuardTests`
  - API 提交下游任务时对依赖契约做阻塞
- `BackgroundJobStatusProjectionTests`
  - job 历史返回可靠区分 fallback 成功与真实成功

验收点：

- 本次 `TASK_ARCH_002 / TASK_ARCH_003` 这一类链路可以被稳定回归测试覆盖

### 12.9 P2：报告与文档同步

#### `docs/bug/BUG-002-execution-chain-false-positive-validation.md`

需要持续更新：

- 修复状态
- 各代码文件落地进度
- 回归测试结果

#### `docs/feature/` 新增设计文档

建议新增一份任务契约设计文档，说明：

- 任务契约结构
- planner 如何生成
- execute 如何产证据
- validator 如何消费
- scheduler 如何放行

---

## 13. 推荐实际施工顺序

1. 先改 [DefaultCodexValidator.cs](/Users/iwaitu/github/codexflow/CodexFlow.Core/Agents/DefaultCodexValidator.cs) 和 [CodexOrchestrator.cs](/Users/iwaitu/github/codexflow/CodexFlow.Core/Agents/CodexOrchestrator.cs)，堵住“误验收通过”
2. 再改 [CodexModels.cs](/Users/iwaitu/github/codexflow/CodexFlow.Core/Models/CodexModels.cs)、[DefaultCodexPlanner.cs](/Users/iwaitu/github/codexflow/CodexFlow.Core/Agents/DefaultCodexPlanner.cs)、[CodexPrompts.cs](/Users/iwaitu/github/codexflow/CodexFlow.Core/Constants/CodexPrompts.cs)，让 planner 输出契约
3. 然后补 execute evidence 聚合与调度依赖检查
4. 最后补 memory/job status 语义和回归测试

如果只做第 2 步，不做第 1 步，系统仍会继续错误放行。  
如果只做第 1 步，不做第 2/3 步，系统会更严格，但仍会缺少稳定的任务级证据闭环。
