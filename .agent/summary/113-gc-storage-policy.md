# §113 GC 清理与存储策略（worker / cron / GraphQL）

日期：2026-11-06
参照：`f272549f:internal/server/gc/gc.go`（795 行，`RegisterScheduledTasks` / `runCleanup` /
`cleanupRequests` / `cleanupOldRequestExecutions` / `cleanupOldRequestsRecords` / `cleanupUsageLogs` /
`cleanupThreads` / `cleanupTraces` / `cleanupChannelProbes` / `runVacuum` / `RunCleanupNow` /
`PreviewCleanup`）、`gc_bodies.go`（484 行，`cleanupBodyPayloads` / `previewBodyCleanup` /
`stripRequestPayloads` / `stripExecutionPayloads` / `primaryStorageID` / `applyBodyPayloadPredicate`）、
`gc_internal.go`、`internal/server/biz/system.go:960-1044`（`StoragePolicy` / `SetStoragePolicy`）、
`system_default.go:11-77`（默认清理项、`mergeCleanupOptions`、`defaultStoragePolicy`）、
`internal/server/scheduler`（`TaskSpec` / `Register` / `CRONRule{Timezone:"UTC"}`）、
`internal/server/gql/system.graphql` 与 `system.resolvers.go`（85-92、301-321、500-515、591-593）。
实时上游的 GC 文件与冻结基线一致。

## 交付

- `src/AxonHub.Api/Management/GcWorker.cs`：`runCleanup` 的资源过滤与 manual 覆盖、requests 分支的
  executions → requests → threads → traces 顺序与错误隔离、500 分批删除、保留 trace 排除、外部存储对象
  删除、body 占位符清除与 skip-id 重试、`PreviewCleanup` 的窗口缓存。
- `src/AxonHub.Api/Management/GcCronSchedule.cs`：五字段 Vixie 解析、星期 / 月份名、7=周日、
  DOM/DOW OR 语义与下一次触发。
- `src/AxonHub.Api/Management/GcScheduler.cs`：`BackgroundService` 按 UTC 触发，错过不回补，
  单次运行失败只记录。
- `src/AxonHub.Api/Management/RequestDataKeys.cs`：`/{project}/requests/...` 与 execution / 目录键。
- `src/AxonHub.Api/Settings/StoragePolicy.cs`、`StoragePolicyCodec.cs`、`SystemSettingsAdminService.cs`：
  默认策略、清理项合并、Go 形状 JSON、缺键 body 标志兼容、读写包装。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`、`AdminMutation.cs`、`SystemSettingsTypes.cs`：
  `storagePolicy`、`updateStoragePolicy`、`previewGcCleanup`、`triggerGcCleanup` 与 SDL 映射。
- `src/AxonHub.Api/Management/AuthorizationScope.cs`：管理用户的系统 scope 判定（owner / 直接 scope /
  系统角色），供 `read:settings` / `write:settings` 门禁使用。
- `src/AxonHub.Persistence/Stores/GcStore.cs`：手写 Dapper SQL（保留行排除、id 升序、分批、payload 谓词、
  外部 NULL 子句、清除字面量、VACUUM）。
- `src/AxonHub.Persistence/Stores/DataStorageStore.cs`：`GetPrimaryAsync` 含软删行、
  `GetByIdIncludingDeletedAsync`。
- `src/AxonHub.Api/Program.cs`：启动时解析 cron（失败包装 `register task "gc": ...`）并注册
  `IGcStore` / `IGcCleanupService` / `GcScheduler`。

## 行为要点

- 配置：`0 2 * * *`、vacuum enabled=true、full=false；`Gc:*` 优先、`AXONHUB_GC_*` 回退。
- 资源过滤：策略读取失败只记录并返回；manual 过滤掉未提供的资源类型并覆盖天数；
  未知资源类型只 Warn；channel probes 无表（no-op，C195）；vacuum 在最后执行。
- 删除：批量 500、id 升序，排除 `traces.status='retained'`；每批先删外部对象再删行。
- 外部对象：null/0 跳过、含软删行缓存、primary 与 database 跳过；请求键 body/response/chunks，
  fs/webdav 追加 executions 目录与请求目录；execution 键 body/response/chunks，fs/webdav 追加目录；
  单个键失败只 Warn。
- body 清除：days<=0 返回；主存储缺失使整条 body 清理中止；请求与 execution 都执行、错误以 `\n` 合并；
  占位符与外部 NULL 子句按参照；失败 id 进入下一轮 skipIds；清除写 `{}` / `[]` 并刷新 `updated_at`。
- 预览：捕获一次 now，顺序 requests、usage_logs、request_bodies、response_bodies、response_chunks；
  body 窗口按天数缓存复用。
- 存储策略：默认五项（3 / 30 / 7 / 7 / 3，全部 disabled）；缺 body 键按 true；合并首见保序、
  后者胜、未知保留、缺失默认追加；Go 字段序序列化；非正天数拒绝。
- GraphQL：`storagePolicy` / `updateStoragePolicy` 无 scope 检查；`previewGcCleanup` 需
  `read:settings`；`triggerGcCleanup` 需 `write:settings`，新 DI scope 后台执行，立即返回 true。

## 测试

- `tests/AxonHub.Api.Tests/StoragePolicyTests.cs`：13 例——默认策略、合并顺序、缺键 body 标志、
  显式 false、Go 字段序、回环、读写包装、非正天数。
- `tests/AxonHub.Api.Tests/GcCronScheduleTests.cs`：14 + 10 例——默认表达式、UTC 转换、步长与范围、
  月份 / 星期名、7=周日、Vixie OR、10 组非法文案、永不匹配、配置默认与 env 回退。
- `tests/AxonHub.Api.Tests/GcWorkerTests.cs`：10 例——自动 / 手动资源过滤与天数覆盖、零天数、
  策略失败中止、主存储缺失、外部删除失败 skip、外部对象键序、VACUUM、预览窗口缓存与错误包装。
- `tests/AxonHub.Api.Tests/AdminGcEndpointTests.cs`：7 例——策略查询线形、更新输入零值、更新包装、
  预览线形与 Time、两个 scope 门禁与后台触发。
- `tests/AxonHub.Persistence.Tests/GcStoreTests.cs`：9 例——retained 排除与 id 升序、usage / execution
  排除、thread / trace status、payload 占位符与外部 NULL 子句、清除字面量、按 id 删除、主存储含软删、
  VACUUM 事务外。
- 静态计数：`AxonHub.Api.Tests` 2928 → 2982（+54），`AxonHub.Persistence.Tests` 301 → 310（+9），
  四项目合计 6356 → 6419。**本轮按项目规则未运行 lint / build / test**，下一次全量运行应以该数字复核。

## 差异

- C095 由 gap 转为 resolved：GC / 保留策略缺口关闭，残余 GC 实现差异转入 C203。
- C203 登记：channel_probes 清理 no-op、cron 为五字段 Vixie 子集、专用 hosted service 调度结构、
  detached 触发上下文、AddDate/AddDays 与本地时区、32 位预览计数、System.Text.Json 解码、
  数据存储缓存 / 主存储排序、手写 Dapper SQL 与 VACUUM 分支、日志接缝。
- C020 仍为请求链路的 `store_chunks` / `store_response_body` 消费缺口；本切片只接入策略读写与 GC。

## 残余风险

- 本轮未运行构建与测试，新增代码的编译 / 分析器正确性仅由人工检查保证。
- `GcScheduler` 的宿主级 Tick 没有真实时间测试；`GcCronSchedule` 的解析与下一次触发由 24 例直接覆盖。
- `triggerGcCleanup` 的 detached scope 只在进程内测试中等待 worker 被调用；进程退出时的在途清理没有
  额外排空保证（参照的 goroutine 同样不等待）。
- `channel_probes` 表未移植，probe 子系统落地前该分支保持 no-op。
