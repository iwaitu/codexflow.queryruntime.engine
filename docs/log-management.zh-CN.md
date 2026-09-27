# 生产环境日志定位与清理

`qre logs` 管理指定 workspace 下的 v2 运行审计、私有审计和 SDK 出站诊断目录。
每行是一整次运行记录，包含开始时间、最后更新时间、类别、运行 ID、状态、目录总字节数和绝对路径。
默认按开始时间升序排列；相同时间按路径稳定排序。不会输出提示词、模型响应或密钥。
旧版 v1 trace、应用宿主自己的日志和导出到其他目录的诊断包不在此命令范围内。

## 按应用日志中的故障时间定位

```sh
qre logs list --workspace /app/data --from 2026-09-27T14:00:00+08:00 --to 2026-09-27T14:30:00+08:00
qre logs list --workspace /app/data --from 2026-09-27T14:00:00+08:00 --to 2026-09-27T14:30:00+08:00 --json
```

时间必须包含秒和时区（`Z` 或 `+08:00` 等），输出统一为 UTC。
范围包含起点、不包含终点。筛选与这个范围重叠的运行：开始时间早于终点，最后更新时间不早于起点。
因此会包含故障前启动、故障期间仍在执行的记录。`active` / `in_progress` 按尚未结束处理，
进程异常退出留下的活动状态也可能匹配，不能据此断言进程仍存活。
可以只提供 `--from` 或 `--to`。这里筛选的是运行区间，不是逐条事件；打开对应 JSONL 可进一步定位事件。

```sh
qre logs list --workspace /app/data --kind audit --descending --skip 0 --take 100
qre logs list --workspace /app/data --date 2026-09-27
```

`--kind` 支持 `all`（默认）、`audit`、`private`、`diagnostics`。
默认列出全部匹配记录；分页只限制输出，当前仍需扫描目录及文件元数据，没有持久化查询索引。
`--date` 按 UTC 创建日期筛选，不能与 `--from` / `--to` 混用。
缺失、损坏或包含链接的记录会报告为不可读并保留；即使无法判定时间，也会在 JSON 的 `warnings` 中列出。

## 回放选中的历史审计

把列表中的审计目录加上 `audit.v1.jsonl`，传入回放命令：

```sh
qre replay latest --workspace /app/data --audit-file /app/data/.qre/v2/runs/RUN_ID/audit.v1.jsonl --json
```

`--audit-file` 覆盖 `latest` 的自动选择，路径必须位于指定 workspace 内，不允许链接路径。
公共脱敏日志只能使用 `--summary` 查看摘要；可记录回放的数据仍通过原有回放校验。
SDK 诊断目录需使用 `qre diagnose --help` 中的诊断命令，不能当作运行审计回放。
记录回放不会重新调用模型或执行真实工具。

## 按日期清理

```sh
# 预览：只匹配创建日期为该 UTC 日期的记录
qre logs delete --workspace /app/data --date 2026-09-20
# 预览：创建时间严格早于该 UTC 日期零点
qre logs delete --workspace /app/data --before 2026-09-20
# 实际执行
qre logs delete --workspace /app/data --before 2026-09-20 --execute --json
```

删除必须明确指定 `--date` 或 `--before`，默认仅预览；不支持用分页或重叠时间段执行删除。
清理以整个运行目录为单位，包含检查点、回放 blobs、产物和补丁，删除后这些记录无法继续回放或恢复。
仅接受终态审计和 `complete` SDK 诊断；活动状态、`incomplete` 诊断、未知状态和损坏记录保留。
执行前重新读取状态、校验链接并尝试锁定已有文件；遇到正在写入的文件则失败并保留。
文件系统删除不是事务，权限变化或并发外部修改可能导致部分删除，结果逐条报告 `failed`，退出码为 1。
预览/成功退出码为 0；不可读记录、参数错误和删除失败返回 1。原有自动保留策略保持不变。
