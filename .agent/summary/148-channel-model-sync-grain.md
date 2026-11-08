# §148 模型同步调度与执行窗口迁入 ChannelModelSyncGrain（B1）

日期：2026-11-09
参照：`internal/server/biz/channel.go:204-212`（cron `11 * * * *` UTC 注册）、
`internal/server/biz/channel_internal.go:23-59`（周期入口、`shouldRunModelSync`、频率 → 分钟）、
`internal/server/biz/channel_model_sync.go:15-67`（同步遍）。

## 背景

端口已移植 `ChannelModelSyncService` 的抓取 / 合并 / 事务 / 补价与 `ShouldRunModelSync`
的窗口算法，但 `RunSyncChannelModelsPeriodicallyAsync` 没有调用方（C158 ①），窗口状态是
scoped 服务里的进程锁，多 silo 下各算各的。B1 把周期注册接上，并把窗口收进单例 grain。

## 交付

- 新增 `IChannelModelSyncGrain`（Grains.Abstractions，string key `"model-sync"`）与
  `ChannelModelSyncGrain`（Grains）：`TryBeginRunAsync(now, intervalMinutes)` 复刻
  `shouldRunModelSync`（UTC 绝对时间截断、同一对齐窗口只放行一次）；grain turn 串行化替代
  `modelSyncMu`；`DelayDeactivation(2 小时)` 跨过每小时 tick（默认回收年龄 15 分钟会丢失窗口，
  使 6 小时 / 1 天频率退化为每小时运行）。
- 新增 `IChannelModelSyncGuard`（Api）与 `GrainChannelModelSyncGuard`：频率 → 60/360/1440
  分钟并调用单例 grain；分派失败记 warning 并跳过本 tick（参照的进程内锁不会失败）。
- `ChannelModelSyncService` 增加可选 `IChannelModelSyncGuard`：生产用 grain 守卫；
  直接构造 / 测试回退 `_modelSyncGate` + `ShouldRunModelSync`。
- 新增 `ChannelModelSyncScheduler`（BackgroundService）：按 cron `11 * * * *` UTC 触发，
  注册时不运行、停机错过不补跑，作用域解析 `ChannelModelSyncService`；复用
  `GcCronSchedule` 的五字段解析（C132）。
- 接线：`Program.cs` 注册 `IChannelModelSyncGuard` 单例与 hosted service。

## 行为要点

- 单 silo 等价：先读系统设置频率，再判断窗口；窗口对齐、每窗口一次、注册不立即运行、
  错过不补跑与参照一致。
- 集群语义：整个集群一个窗口；参照每个进程各注册 cron、各有一份窗口，多 silo 下每个进程
  每个窗口都可能各跑一遍（有意收窄，登记 C228）。
- 失败面：grain 分派失败跳过本 tick；参照无此失败面。
- 状态：窗口在激活内存中，silo 重启丢失，与参照进程重启等价。

## 测试

- `tests/AxonHub.Grains.Tests/ChannelModelSyncGrainTests.cs`（5）：同窗口仅首次放行、
  跨窗口再次放行、6 小时 / 1 天对齐、并发调用只有一个赢家。
- `tests/AxonHub.Api.Tests/GrainChannelModelSyncGuardTests.cs`（1）：生产单例键、跨桥实例
  共享窗口、三个频率到间隔的映射。
- `tests/AxonHub.Api.Tests/ChannelModelSyncTests.cs`（+3）：周期入口先问守卫、守卫放行才同步、
  无守卫时回退进程内窗口。

静态计数：`AxonHub.Grains.Tests` 1716 → 1721（+5），`AxonHub.Api.Tests` 5445 → 5449（+4），
`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 336 不变，四项目合计 9003 → 9012。
**按项目规则未运行 lint / build / test。**

台账：基线 §148、验收 §148 已写入；C158 ① 关闭（仅剩按通道代理与 live-only 分支）；
新增 C228（accepted）；`02-differences.md` 重新生成（228 条）。
