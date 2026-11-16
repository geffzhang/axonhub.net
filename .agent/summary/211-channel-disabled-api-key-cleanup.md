# §211 每分钟清理过期禁用凭证并恢复自动禁用通道（C285）

## 背景

参照在 `ChannelService.RegisterScheduledTasks` 注册 `channel-disabled-api-key-cleanup`
（`internal/server/biz/channel.go:204-223`，`* * * * *`、UTC，注释说明按分钟 tick 以便
`disable_until_cron` 规则在到期后一分钟内恢复），回调是
`cleanupExpiredDisabledAPIKeys`（`channel_apikey.go:393-436`）。端口此前没有任何到期恢复
运行时：过期 temporary disabled key 不会被清除，因全部 key 不可用而禁用的通道不会自动恢复，
`auto_disable_expires_at` 到期的通道会一直保持 disabled 直到人工操作。台账 C132 把
“自动禁用定时恢复的运行时仍未移植”列为残余，本切片闭合。

## 参照行为

- `cleanupExpiredDisabledAPIKeys`：`context.WithTimeout(30s)` +
  `authz.WithSystemBypass(ctx, "channel-cleanup-expired-disabled-api-keys")`；查
  `StatusIn(enabled, disabled)` 的通道 id；逐 id 调
  `cleanupChannelExpiredDisabledAPIKeys`，失败仅记日志并继续；然后
  `cleanupExpiredAutoDisabledChannels`；有变化时 `asyncReloadChannels()`。
- `cleanupChannelExpiredDisabledAPIKeys`（`:491-516`）：`apiKeyOpsLock` 内 `Channel.Get`，
  过滤 `!dk.IsExpired()`，`removed == 0` 直接返回；否则
  `SetDisabledAPIKeys(active)` + `applyRecoveredChannelStatus` 后 `Save`；错误前缀
  `failed to get channel:` / `failed to update channel:`。
- `applyRecoveredChannelStatus`（`:372-391`）：仅当 `status=disabled`、`error_message` 非空
  且以 `All API keys disabled` 开头、恢复后仍存在启用凭证引用时，置 enabled 并清
  errorMessage / autoDisabledAt / autoDisableExpiresAt。
- `cleanupExpiredAutoDisabledChannels`（`:438-489`）：查 `status=disabled` 且
  `auto_disabled_at`、`auto_disable_expires_at` 非空、`expires_at <= now` 的 id；逐条条件
  更新（相同谓词）为 enabled 并清三列；`affected == 0` 跳过；返回是否有恢复。

## 端口实现

- 持久化（`IChannelWriteStore` / `ChannelWriteStore`，EF Core）：
  - `ListCredentialCleanupChannelIdsAsync`：live 且 status ∈ {enabled, disabled}，id 升序。
  - `ListExpiredAutoDisabledChannelIdsAsync(now)`：live、disabled、两个标记非空、
    `expires_at <= now`，id 升序。
  - `RecoverExpiredAutoDisabledChannelAsync(id, now)`：`ExecuteUpdateAsync` 重新校验同一组
    谓词，写 enabled + errorMessage/autoDisabledAt/autoDisableExpiresAt = NULL +
    updatedAt；返回 affected > 0。
- 服务（`ChannelLifecycleService`）：
  - `CleanupExpiredDisabledApiKeysAsync`：linked CTS + `CancelAfter(30s)`；捕获一个
    `DateTime.UtcNow`；候选查询失败记日志并整体返回；逐通道清理，异常记日志并继续；再跑
    auto-disable 恢复；`needsReload` 时 `RequestEnabledChannelsRefresh()`。
  - 私有 `CleanupChannelExpiredDisabledApiKeysAsync`：静态 `ApiKeyOpsLock`（复用
    `DisableApiKey` 的进程级互斥）、`GetChannelForApiKeyOpAsync`、`ParseDisabledKeys`、
    `BuildEnableState(..., now)`、`SaveApiKeyStateAsync("failed to update channel")`。
  - `BuildEnableState` 增加可选 `now` 参数并补上
    “Re-enabled channel after API key availability recovered” 日志（四条调用路径共用）。
- 调度：
  - `AxonHub.Grains.Abstractions/ChannelCredentialRecovery.cs`：
    `IChannelCredentialRecoveryRunner`、`IChannelCredentialRecoveryGrain`（SingletonKey /
    ReminderName = `channel-disabled-api-key-cleanup`）。
  - `AxonHub.Grains/ChannelCredentialRecoveryGrain.cs`：与 `ChannelProbeGrain` 相同的分钟
    reminder 模式（先武装下一分钟再执行、stale tick 跳过、失败保留调度）。
  - `AxonHub.Api/Management/GrainChannelCredentialRecovery.cs`：新建 scope 解析 scoped
    `ChannelLifecycleService` 并调用清理。
  - `Program.cs` 注册 runner；`GrainSchedulerActivator` 启动时 `EnsureScheduledAsync`。

## 差异

新增 C285（resolved）。与参照的实现差异：候选 id 按 id 升序（参照 ent `IDs()` 无 ORDER BY，
逐通道工作与顺序无关）；一次 pass 捕获一个 UTC now；30 秒超时用 linked CTS；恢复日志并入共享
`BuildEnableState`；其余语义（system bypass、锁、错误前缀、条件更新、单次刷新）逐条对齐。
C132 更新：`GcCronSchedule` 已实现五字段 Next（`disable_until_cron` 使用），恢复运行时由本切片
接线。

## 测试

- `tests/AxonHub.Api.Tests/ChannelCredentialRecoveryTests.cs`（11 例）。
- `tests/AxonHub.Grains.Tests/ChannelCredentialRecoveryGrainTests.cs`（6 例）。
- `tests/AxonHub.Persistence.Tests/ChannelWriteStoreTests.cs` 新增 3 例。
- 定向运行：Persistence `ChannelWriteStoreTests` 56 通过；Api 新类 11 通过；Grains 新类 6
  通过；未跑 Api 全量回归。

## 台账

285 条（203 accepted / 0 gap / 82 resolved）；lint 通过；`02-differences.md` 已重新生成；
`01-baseline.md` 与 `03-acceptance.md` 已追加 §211 段。
