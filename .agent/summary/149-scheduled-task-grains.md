# §149 自动备份与 GC 调度迁入 reminder grain（B3）

日期：2026-11-09
参照：`internal/server/backup/service.go:39-55`（备份 cron `0 2 * * *`，系统时区）、
`internal/server/backup/autobackup.go:22-32`（`Reschedule` 取消并重建）、
`internal/server/backup/autobackup.go:42-85`（enabled / frequency 闸门）、
`internal/server/gc/gc.go:77-84`（GC cron `gc.cron`，UTC）。

## 背景

端口此前用两个每分钟轮询的 `AutoBackupScheduler` / `GcScheduler` BackgroundService 模拟
参照的两条 cron：触发最多晚 1 分钟，`updateAutoBackupSettings` 后的 `Reschedule` 是 no-op。
B3 把两条 cron 收进集群单激活的 reminder grain，触发时刻、重排与失败语义对齐参照。

## 交付

- 新增 `IAutoBackupGrain` / `IGcGrain`（Grains.Abstractions，string key 单例
  `"auto-backup"` / `"gc"`）与 `AutoBackupGrain` / `GcGrain`（Grains）：
  - 激活内 `_next` 保存待触发时刻，同名 Orleans reminder 承载（周期取最小值 1 分钟）；
  - `EnsureScheduledAsync` 只装填不运行（启动注册语义）；
  - `FireScheduledAsync` 先 `RegisterOrUpdateReminder` 装填下一次、再执行任务，任务失败
    不丢调度；`_next` 比较使已被消费的过期 tick 成为 no-op；
  - `RescheduleAsync` 丢弃 pending 并从调用时刻重装；
  - `ReceiveReminder` 用注入的 `TimeProvider` 取当前时刻。
- 新增接缝（Grains.Abstractions）：`IAutoBackupScheduleSource`（系统时区下一次 02:00）、
  `IAutoBackupRunner`（`triggerAutoBackup`）、`IGcScheduleSource`（`gc.cron` 下一次触发）、
  `IGcCleanupRunner`（`runAutomaticCleanup`）。grains 不引用 Api 类型。
- 新增 Api 适配器（`Management/GrainAutoBackup.cs`、`Management/GrainGc.cs`）：
  `GrainAutoBackupScheduleSource`（每次装填重读 `system_general_settings` 的 timezone，
  读取失败回退 UTC）、`GrainAutoBackupRunner`（新 DI scope 调
  `BackupService.TriggerAutoBackupAsync`）、`GrainAutoBackupRescheduleSignal`（转调 grain，
  失败只记日志）、`GrainGcScheduleSource`（复用 `GcCronSchedule`）、`GrainGcCleanupRunner`
  （新 DI scope 调 `IGcCleanupService.RunAutomaticCleanupAsync`）。
- 新增 `GrainSchedulerActivator`（IHostedService）：宿主启动时依次装填 GC 与备份单例；
  失败记日志并抛 `InvalidOperationException("failed to register the scheduled tasks")`。
  `Program.cs` 在 `UseOrleans` 之后注册，确保 silo 先启动。
- `BackupService.Reschedule()`（原 no-op）改为 `RescheduleAsync`，经可选
  `IAutoBackupRescheduleSignal` 转发；`AdminMutation.UpdateAutoBackupSettingsAsync` 等待它。
  信号吞掉调度失败，与参照只记日志一致。
- 删除 `AutoBackupScheduler.cs` / `GcScheduler.cs`；silo 配置启用
  `UseInMemoryReminderService()`；项目新增 `Microsoft.Orleans.Reminders` 包
  （`IRemindable` 与 `RegisterOrUpdateReminder` 不在 Server 包内），四个项目均声明。

## 行为要点

- 单 silo 等价：启动只装填、02:00 / gc.cron 触发、任务失败后继续、设置变更后从调用时刻
  重算、停机错过不补跑均与参照一致。
- 集群语义：整个集群只跑一遍；参照每进程各注册 cron，多 silo 下各跑一遍（有意收窄，
  登记 C229）。
- 重启：内存 reminder 表随 silo 停止丢失，启动激活器从当前时刻重装，与参照重启后重新
  注册 cron 等价（不补跑）。
- 投递延迟：冷启动 reminder 表刷新约 10–20 秒（上限 80 秒）；此后按到期时刻投递。
  极端早到的 tick 被跳过，最坏晚 1 分钟补上（reminder 最小周期）。

## 测试

- `tests/AxonHub.Grains.Tests/AutoBackupGrainTests.cs`（6）：注册只装填不运行、Ensure
  复用 pending、触发前先装填下一次、过期 tick 去重、Reschedule 从调用时刻重装、任务失败
  保持调度。
- `tests/AxonHub.Grains.Tests/GcGrainTests.cs`（4）：注册只装填不运行、Ensure 复用
  pending、触发前先装填下一次、过期 tick 去重。
- `tests/AxonHub.Api.Tests/GrainScheduledTaskTests.cs`（3）：GC source 的 cron 推进、
  备份 source 解析系统时区、读取失败回退 UTC。
- `tests/AxonHub.Api.Tests/GrainSchedulerBridgeTests.cs`（1）：启动激活器装填两个单例、
  设置信号重装备份 grain。
- `tests/AxonHub.Api.Tests/BackupServiceTests.cs`（+1）：`RescheduleSignalsTheScheduledTask`。

静态计数：`AxonHub.Grains.Tests` 1721 → 1731（+10），`AxonHub.Api.Tests` 5449 → 5454（+5），
`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 336 不变，四项目合计 9012 → 9027。
**按项目规则未运行 lint / build / test。**

台账：基线 §149、验收 §149 已写入；C197 / C199 / C203(3) 的轮询与 `GcScheduler` 描述已更新；
新增 C229（accepted）；`02-differences.md` 重新生成（229 条）。
