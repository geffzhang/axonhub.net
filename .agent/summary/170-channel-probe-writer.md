# §170 探针 writer / 分钟调度 / GC 清理（C249 收口，30 例）

日期：2026-11-11
参照（post-baseline 漂移，按工作仓库 `f272549f`）：
`internal/server/biz/channel_probe.go`、`channel_probe_internal.go`、
`internal/server/gql/qb/throughput.go`、`internal/server/gc/gc.go`、
`internal/server/gql/channel_performance_helpers.go`、`dashboard.resolvers.go`。

## 背景

§162 只移植了 `channelProbeData` 读取面；writer、每分钟调度、GC 的
`cleanupChannelProbes` 与 dashboard 的 probes 分支登记为 C249 gap。本切片把采集链路
整体接回，并为 Orleans 增加探针单例 grain。

## 交付

- `src/AxonHub.Persistence/Stores/ChannelProbeStore.cs`：`ComputeStatsAsync`
  （`qb.BuildProbeStatsQuery` 的 PostgreSQL 形态，Dapper）与 `CreateRangeAsync`
  （EF Core 批量插入）；`EffectiveLatencyMs` 可空，复现 `rows.Scan` 对 NULL 的失败。
- `src/AxonHub.Persistence/Stores/GcStore.cs`：`ListOldChannelProbeIdsAsync`
  （`timestamp < cutoff` 的 Unix 秒、id 升序、LIMIT）与复用 `DeleteByIdsAsync` 的
  `DeleteChannelProbesAsync`。
- `src/AxonHub.Persistence/Stores/DashboardStore.cs`：
  `GetDailyChannelPerformanceFromProbesAsync`（本地日期 + channel 分组、`@StartUnix`
  下界、`avg_tokens_per_second <= 2000`、按请求数加权）。
- `src/AxonHub.Persistence/Stores/DataStorageStore.cs`：拆分 `GetPrimaryAsync`（live）
  与 `GetPrimaryIncludingDeletedAsync`（GC 的 SkipSoftDelete 上下文），`GcWorker`
  改用后者。
- `src/AxonHub.Api/Management/ChannelProbeService.cs`：`RunProbeAsync` 全量移植
  （设置回退、禁用跳过、对齐、先占区间、SQL 聚合、TPS/TTFT、NULL 中止、失败只记日志）。
- `src/AxonHub.Api/Management/ChannelProbeRunState.cs`：进程内单例去重哨兵。
- `src/AxonHub.Grains.Abstractions/ChannelProbe.cs` 与
  `src/AxonHub.Grains/ChannelProbeGrain.cs`：单例 grain + `IRemindable`，60s reminder，
  先 arm 后运行、stale tick 跳过。
- `src/AxonHub.Api/Management/GrainChannelProbe.cs`：runner 通过
  `IServiceScopeFactory` 解析 scoped 服务。
- `GrainSchedulerActivator` / `Program.cs`：启动 touch 与注册。
- `GcWorker`：固定三天 probe 清理（失败隔离、继续 VACUUM）。
- `DashboardService`：probes 优先，查询错误不回退，空结果才回退 executions。

附带修正（既有缺陷，非新差异）：`GcStore` 的两处 payload 查询把 `AND` 与
`NOT EXISTS` 用不带空格的 StringBuilder 拼接，生成 `ANDNOT`（PostgreSQL 语法错误）；
`IN @ids` 改为 `= ANY(@ids)`——Dapper 在 Npgsql 上把数组参数按原生数组传递，不会展开
`IN` 列表。

## 行为要点

- 设置读取任何错误回退默认（`probe.enabled=true`、`frequency=5m`），因此损坏设置下
  probe 仍会运行。
- 去重槽在 DB 工作前占用且失败不释放：同区间只尝试一次。
- probe SQL 是 attempt-based：failed 只计入 total；tokens 仅 completed 且同
  request/channel 的 usage_logs；streaming 有效延迟扣 TTFT，`TTFT >= latency` 归零；
  TTFT 均值为 0 时不写字段。
- `effective_latency_ms` 为 NULL 时整次 pass 中止（等价 database/sql 扫描失败）。
- GC 窗口固定 3 天且 manual 不覆盖；清理失败只记日志，VACUUM 照常执行。
- dashboard probes 查询错误包裹 `failed to get channel performance stats from
  probes: %w` 且不回退；空结果才走 executions。

## 测试

- `tests/AxonHub.Grains.Tests/ChannelProbeGrainTests.cs`（6）：注册 arm 不运行、
  下一分钟边界、Ensure 保持 pending、Fire 先 re-arm、stale tick、失败保留调度。
- `tests/AxonHub.Api.Tests/ChannelProbeServiceTests.cs`（22：10 读取面 + 12 writer）。
- `tests/AxonHub.Api.Tests/AdminDashboardEndpointTests.cs`（15，新增 probes 优先 /
  错误不回退）。
- `tests/AxonHub.Api.Tests/GcWorkerTests.cs`（13，新增固定窗口 / manual / 失败隔离）。
- `tests/AxonHub.Persistence.Tests/ChannelProbeStoreTests.cs`（7）、
  `DashboardProbeStatsStoreTests.cs`（2）、`GcStoreTests.cs`（10）。

定向运行：Persistence 28/28、Api 50/50、Grains 6/6；构建 0 警告 0 错误。全量运行
结果见 `porting/03-acceptance.md` §170。

## 台账

- C249 → resolved；C195 / C203 的「probes 分支 / 清理缺席」措辞更新；
  `porting/02-differences.md` 重新生成（267 条：197 accepted / 7 gap / 63 resolved），
  lint 通过。
