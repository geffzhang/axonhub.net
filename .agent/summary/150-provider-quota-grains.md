# §150 供应商配额采集迁入协调器 + 每通道 grain（B2）

日期：2026-11-09
参照：`internal/server/biz/provider_quota.go:346-359`（`svc.mu` 与 `quotaCache`）、
`401-409`（注册 `provider-quota-check`，`*/5 * * * *`，UTC）、`534-549`（固定 5 分钟区间）、
`561-593`（同步读缓存 / 更新缓存）、`595-623`（失效与手动检查取 `svc.mu`）、`665-717`
（重置后刷新）、`719-790`（整轮采集，`min(8, 组数)` 并发）、`792-864`（组检查）、
`provider_quota_account.go:39-64`（按账号分组）。

## 背景

端口此前用 `ProviderQuotaScheduler`（每分钟轮询的 BackgroundService）模拟参照的
`*/5 * * * *` cron，`svc.mu` 是单进程 `SemaphoreSlim`。B2 把整轮采集、手动检查、失效与
重置后刷新收进集群单激活的 `ProviderQuotaCoordinatorGrain`（reminder 承载调度），并把每个
due 组派给组长通道的 `ProviderQuotaGrain`，让通道级检查也由 actor turn 串行化。

## 交付

- 新增 `ProviderQuota.cs`（Grains.Abstractions）：八个 `[GenerateSerializer]` DTO
  （通道快照、持久状态、组、限制、缓存状态、成员状态、组检查结果、刷新请求）与五个接缝：
  `IProviderQuotaGrain`（integer key）、`IProviderQuotaGroupRunner`、
  `IProviderQuotaScheduleSource`、`IProviderQuotaCoordinatorOperations`、
  `IProviderQuotaCoordinatorGrain`（string key `"provider-quota"`，reminder
  `"provider-quota-check"`）。
- 新增 `ProviderQuotaGrain`（Grains）：`CheckGroupAsync` 以本通道为组长调 runner，保存组长
  结果到激活内 `_status`；runner 意外抛错记 `ProviderQuotaGroupFailed` 并返回空结果（保留
  旧条目）；`ApplyStatusAsync` / `ClearStatusAsync` 供成员镜像与失效路径使用。镜像从不参与
  决策。
- 新增 `ProviderQuotaCoordinatorGrain`（Grains）：reminder 周期取 1 分钟；`EnsureScheduled`
  只装填、`FireScheduled` 先重装下一次再 `RunAsync(force:false)`、`_next` 去重过期 tick、
  `RunManualAsync` 执行 `force:true`、`RefreshChannelAsync` 转发刷新、`InvalidateAsync`
  转发并传播删除错误；前三类失败只记日志（与参照 `runQuotaCheck` 只记录一致）。
- 新增 Api 适配器 `Management/GrainProviderQuota.cs`：`GrainProviderQuotaCoordinator`
  （客户端 → 单例 grain）、`GrainProviderQuotaOperations`（三路调用 `ProviderQuotaService`，
  失效后在 `finally` 清通道 grain，清镜像失败只记 `ProviderQuotaGrainClearFailed`）、
  `GrainQuotaGroupExecutor`（派给 `Members[0]` 的 grain，再把各成员状态镜像到其余通道
  grain，单点失败不中断本轮）、`GrainProviderQuotaScheduleSource`（复用 `GcCronSchedule`）。
- `ProviderQuotaService`：可选注入 `IQuotaGroupExecutor` / `IProviderQuotaCoordinatorClient`；
  生产环境手动检查、失效与重置后刷新转发协调器；`RunQuotaCheckAsync` 保留
  `Parallel.ForEachAsync` 与 `min(8, 组数)` 并发，组体经 `ExecuteGroupAsync` 派发；新增
  `RunGroupAsync`（组长 grain 的 API 半程）与 `RefreshChannelQuotaAsync`；直接构造时保留
  原 `_mutex` 路径（测试 / 工具语义不变）。
- `Program.cs`：删除 `ProviderQuotaScheduler` 宿主与注册，新增五个接缝注册；
  `GrainSchedulerActivator` 启动时第三个装填配额协调器；`ChannelModelSyncScheduler` 注释
  不再引用已删除的类型。

## 行为要点

- 单 silo 等价：5 分钟 UTC 触发、注册不运行、错过不补跑、设置变更不重排、四类入口互斥、
  组间并发 8、组内单次请求逐成员落库、失败只记录且不丢调度、失效错误返回给调用方。
- 多 silo：整个集群只采集一轮，非承载 silo 的进程内读缓存不会更新；参照每进程各跑自己的
  cron。有意收窄，项目只支持单 silo（登记 C230）。
- 读模型：`ProviderQuotaService._quotaCache` 仍是同步路由的唯一读模型与唯一写入口；通道
  grain 的 `_status` 只是镜像，启动为空、随检查懒填，从不参与决策。
- 防御性收窄：runner 的意外异常被组长 grain 捕获并记录；参照的组 goroutine 无 recover，
  同类 bug 会 panic 进程。

## 测试

- `tests/AxonHub.Grains.Tests/ProviderQuotaGrainTests.cs`（5）：组长执行组、只存组长条目；
  runner 抛错保留旧条目；Apply/Clear；空成员组不覆盖；同通道并发被 turn 串行化。
- `tests/AxonHub.Grains.Tests/ProviderQuotaCoordinatorGrainTests.cs`（10）：注册只装填、
  Ensure 复用 pending、触发前先装填、过期 tick 去重、手动 force 不动调度、三类失败被吞、
  刷新转发、失效传播错误。
- `tests/AxonHub.Api.Tests/ProviderQuotaGrainBridgeTests.cs`（8）：执行器派发与镜像、
  null 清镜像、组长失败不动成员、客户端三路转发、失效错误传播、操作适配器清 grain
  （删除失败也清）、`*/5` UTC 调度源。
- `tests/AxonHub.Api.Tests/ProviderQuotaServiceTests.cs`（+5）：手动检查 / 失效 / 重置后
  刷新转发协调器、执行器逐组派发、`RunGroupAsync` 单请求双成员落库回传。

静态计数：`AxonHub.Grains.Tests` 1731 → 1746（+15），`AxonHub.Api.Tests` 5454 → 5467（+13），
`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 336 不变，四项目合计 9027 → 9055。
**按项目规则未运行 lint / build / test。**

台账：基线 §150、验收 §150 已写入；新增 C230（accepted）；`02-differences.md` 重新生成
（230 条，166 resolved / 9 gaps / 55 accepted），lint 通过。
