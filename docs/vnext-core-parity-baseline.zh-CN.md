# QueryRuntime vNext C0 基线

日期：2026-08-24  
基线版本：v1 `0.1.2` 工作树 + 路线一安全加固

## 冻结面

| 表面 | 当前权威位置 | C0 冻结方式 |
| --- | --- | --- |
| Host public API | `CodexFlow.QueryRuntime.Abstractions` | public surface snapshot + clean consumer build |
| v1 Engine API | `CodexFlow.QueryRuntime.Engine/RuntimeContracts.cs` | v1 contract tests；C1 不原地改写 |
| CLI JSON | `qre run/tool/sandbox/trace/replay --json` | `QreCliSmokeTests` JSON 字段断言 |
| trace v1 | `QueryRuntimeTraceSchema`、JSONL reader/manifest | schema fixtures、replay/security tests |
| NuGet | Engine 包携带 Abstractions、README、icon | `scripts/qre-package-smoke.sh` clean consumer |
| AOT | `qre` CLI | `scripts/qre-aot-gate.sh` 与 release AOT matrix |

## 当前结构事实

- v1 Engine 直接引用 `Microsoft.Extensions.AI`，round loop、tool invocation、termination 和 host hooks
  集中在 `QueryRuntimeEngine.cs`。
- Abstractions 同时包含 host facade、MEAI 类型、trace/sandbox/tool contracts；C1 新 Protocol 不复制
  provider/host 细节，只承载 Runtime IR 和稳定 typed IDs。
- Experimental 当前承担 hosting、trace/replay 和内置工具；C3 之前保留，不把它误称为 v2 core。
- 路线一已将 untrusted repository、trace/replay、private diagnostic、Docker write-back 和 release
  门禁收紧；这些测试是路线二持续回归集。

## 基线命令

```powershell
# public/host contracts
dotnet test CodexFlow.QueryRuntime.UnitTests/CodexFlow.QueryRuntime.UnitTests.csproj -c Release --filter "FullyQualifiedName~QueryRuntimeContractTests|FullyQualifiedName~HostAdapterContractTestKitTests"

# CLI JSON、trace v1 与安全负向集
dotnet test CodexFlow.QueryRuntime.UnitTests/CodexFlow.QueryRuntime.UnitTests.csproj -c Release --filter "FullyQualifiedName~QreCliSmokeTests|FullyQualifiedName~TraceReplaySecurityTests|FullyQualifiedName~TraceDataSecurityTests"

# package/AOT
bash scripts/qre-package-smoke.sh
bash scripts/qre-aot-gate.sh linux-x64
```

## 性能与规模基线规则

3/10/25-step 使用 scripted model 和 fake tools，不访问 provider、网络或真实 workspace。每次记录：

- commit/ref、OS、RID、SDK、CPU、配置（Release/AOT）。
- elapsed time、process allocated bytes、peak working set、事件数和 trace bytes。
- 纯文本轨迹与 tool-heavy 轨迹分别记录，至少预热一次、测量五次，报告中位数与最大值。

C0 只冻结测量方法，不设无依据的 25 ms 硬阈值。C2 phase loop 完成后，以同一 fixture 对比；
执行语义必须一致，时间/分配/trace 大小的允许回退阈值由实测基线 ADR 修订确定。

首次记录环境：Windows `10.0.26200`、x64、.NET SDK `10.0.400`、Intel Core i9-13900K、Release；
每组预热一次后测量五次。下面是 scripted fake-tool 的进程内 v1 Engine 基线，不含 provider、磁盘和网络：

| Steps | elapsed 中位数/最大值 | allocated 中位数/最大值 | events | event projection bytes |
| ---: | ---: | ---: | ---: | ---: |
| 3 | 0.046 / 0.154 ms | 24,600 / 24,600 | 19 | 4,327 |
| 10 | 0.154 / 0.173 ms | 73,544 / 81,744 | 68 | 15,264 |
| 25 | 0.513 / 1.181 ms | 196,032 / 196,032 | 173 | 38,884 |

可重复命令：

```powershell
dotnet test CodexFlow.QueryRuntime.UnitTests/CodexFlow.QueryRuntime.UnitTests.csproj -c Release --filter "FullyQualifiedName~CoreParityBaselineTests" --logger "console;verbosity=detailed"
```

## C5 bounded context 实测

C5 使用同一 Windows/.NET/Release 环境，scripted model 与 fake tool，每组预热一次、测量五次。下面的
`max prepared tokens` 已同时包含每 Step 暴露的 tool schema；canonical history 不因 compaction 被改写。

| Steps | elapsed 中位数/最大值 | allocated 中位数/最大值 | canonical messages | preparations / compactions | max prepared tokens | blob bytes |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 3 | 0.220 / 0.248 ms | 123,000 / 123,000 | 6 | 3 / 0 | 372 | 618 |
| 10 | 1.877 / 2.812 ms | 1,151,544 / 1,153,512 | 20 | 10 / 7 | 472 | 618 |
| 25 | 7.162 / 8.889 ms | 6,174,320 / 6,177,840 | 50 | 25 / 22 | 472 | 618 |

25 Step 投影仍稳定受 512-token fixture 预算约束，且无需额外 provider 请求。因此 C5 采用确定性本地
summary，不引入 model compactor；若 C6/C7 的真实长轨迹质量数据证明语义损失不可接受，再以独立 ADR
评估模型压缩，而不是提前增加成本、失败模式和非确定性。

## 已知兼容边界

- `0.1.2` 消费者继续使用 v1 facade；C1/C2 不移除或重命名 v1 类型。
- `0.2.0-preview.*` 可以提供新的 v2 facade，但不得静默改变 v1 trace schema。
- public redacted trace 只支持 summary replay；sanitized/private 能力声明保持显式。
- in-flight Turn 不跨 v1/v2 backend 恢复。
