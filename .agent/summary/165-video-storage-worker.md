# §167 视频存储扫描 worker（C234，34 例）

日期：2026-11-10
参照（`f272549f`）：`internal/server/video_storage/worker.go`（298 行）、`fx_module.go`、
`internal/server/scheduler/scheduler.go`、`internal/server/biz/data_storage.go`
（`SaveDataFromReader` / `GetDataStorageByID`）、`zhenzou/executors@v0.2.0`
（`ScheduleFuncAtFixRate`）。

## 背景

C234 此前登记为真实缺口：参照按 `system_video_storage_settings` 周期性扫描已完成的视频任务，
把生成结果下载到外部存储并回写请求行；端口只移植了设置读写与重排信号接缝，没有扫描循环，
生产也未注册重排信号。本切片闭合该缺口。

## 交付

- `src/AxonHub.Grains.Abstractions/VideoStorage.cs`：`IVideoStorageScheduleSource`（间隔读取）、
  `IVideoStorageScanRunner`（一次扫描）、`IVideoStorageGrain`（单例键 `video-storage`、
  reminder 名、`EnsureScheduled` / `Reschedule` / `FireScheduled`）。
- `src/AxonHub.Grains/VideoStorageGrain.cs`：Orleans reminder 承载 fix-rate。`EnsureScheduled`
  在无 pending 时从传入时刻按当前间隔装填；`FireScheduled` 先重排下一次再运行，过期 tick
  只重排不重复扫描；`Reschedule` 丢弃 pending 并从调用时刻重排；扫描异常记录
  "Video storage worker failed" 且不丢调度；间隔低于 1 分钟按 Orleans 周期下限收敛。
- `src/AxonHub.Api/Management/VideoStorageWorker.cs`：`ScanAndSaveAsync` /
  `ProcessOneAsync`。启用闸门、`data_storage_id` 必填、`GetDataStorageByID` 包装
  "failed to get data storage: …"、主存储 / database 拒绝、行查询限制非正数回落 50、
  ResponseBody 的 `video_url` 优先、视频任务 `succeeded` 回退、5 分钟下载超时、仅
  http/https、非 200 失败、512 MiB `io.LimitReader` 静默截断、Content-Disposition
  `filename=` 解析（TrimSpace + 去引号）、URL 去 query 的 `filepath.Base` 回退与
  `video-<unix>.mp4` 兜底、key `/{project_id}/requests/{request_id}/video/{basename}`、
  内容列 + `updated_at` 写入（`MarkContentSavedAsync` 返回 false 视为
  "ent: request not found"）、错误前缀 "failed to save video to storage: " /
  "failed to update request video saved status: "、逐行失败警告后继续。
- `src/AxonHub.Api/Management/GrainVideoStorage.cs`：
  `GrainVideoStorageScheduleSource`（每次装填都读设置，非正数 1 分钟）、
  `GrainVideoStorageScanRunner`（新建 scope + 10 分钟 linked timeout）、
  `GrainVideoStorageRescheduleSignal`（失败只记录 "Failed to reschedule video storage worker"）。
- `src/AxonHub.Persistence/Stores/RequestStore.cs`：Dapper 读
  `ListUnsavedVideoRequestsAsync`（`status IN ('processing','completed') AND format IN
  ('openai/video','seedance/video') AND content_saved = false ORDER BY id ASC LIMIT`）
  与 EF 写 `MarkContentSavedAsync`（content_saved / id / key / saved_at / updated_at）。
- 接线：`Program.cs` 注册 worker、source、runner、signal；`GrainSchedulerActivator`
  启动时 EnsureScheduled（无 pending 时装填，不扫描）。

## 行为要点

- 首次扫描在间隔之后；`scan_interval_minutes <= 0` 为 1 分钟；设置变更后从变更时刻重新计时；
  失败不丢调度；错过的时间点不补跑。
- 参照的池执行器可能并发扫描；grain 回合串行化，只减少同一行的重复下载。
- 下载使用独立的共享 `SocketsHttpHandler`（最多 10 次重定向、gzip/deflate），与 Go
  `http.DefaultClient` 的可观察配置一致；文件名不受重定向后的最终 URL 影响（用响应体/任务
  返回的 URL）。
- `response.Content.ReadAsStreamAsync` 使用扫描 token，下载 token 只约束到响应头；与参照
  worker.go:198 的「SaveDataFromReader 用扫描 context、请求用下载 context」一致。

## 测试

- `tests/AxonHub.Grains.Tests/VideoStorageGrainTests.cs`（8）：注册不扫描、Ensure 保留
  pending、fire 先重排后扫描、stale tick 去重、Reschedule 从调用时刻重排、扫描失败保调度、
  间隔 0 / 30 秒下限 1 分钟。
- `tests/AxonHub.Api.Tests/VideoStorageWorkerTests.cs`（21）：禁用跳过、缺 storage id、
  存储缺失包装、主库与 database 拒绝、查询失败前缀、响应体 URL 下载与 key / 字节 / 标记、Content-
  Disposition 文件名、空 body 与坏 JSON 回退 GetTask、非 succeeded 与无 URL 不下载、
  非 http scheme 只失败该行、非 200 只失败该行、更新失败继续扫描、ScanLimit=1、非正 limit
  回落 50、URL 无文件名时时间戳名、`GenerateVideoKey` 四种输入。
- `tests/AxonHub.Api.Tests/GrainScheduledTaskTests.cs`（+3）：视频间隔读取、非正下限、
  缺行走默认。
- `tests/AxonHub.Api.Tests/GrainSchedulerBridgeTests.cs`：启动激活器装填视频单例且不扫描。
- `tests/AxonHub.Persistence.Tests/RequestStoreTests.cs`（+2）：未保存视频行的筛选 / 排序 /
  limit；内容列写入与 updated_at、缺行返回 false。

实测：`dotnet build AxonHub.slnx` 0 警告 0 错误；`dotnet test tests/AxonHub.Grains.Tests` 169/169；
Api 定向 `VideoStorageWorkerTests|GrainSchedulerBridgeTests` 22/22、`GrainScheduledTaskTests`
6/6；`dotnet test tests/AxonHub.Persistence.Tests --filter RequestStoreTests` 14/14。
宽过滤运行命中 3 个与本切片无关的既有失败（`updateQuotaRoutingSettings` 等报
`AuthorizationScope` 未注册），未处理。

静态计数：`AxonHub.Grains.Tests` 1746 → 1754（+8），`AxonHub.Api.Tests` 5920 → 5944
（+24），`AxonHub.Persistence.Tests` 363 → 365（+2），`AxonHub.Llm.Tests` 1517 不变，
四项目合计 9546 → 9580。

## 台账

C234 由 gap 更新为 resolved；新增 C257（accepted）：Orleans reminder 与参照
`ScheduleFuncAtFixRate` 的调度机制差异（最小 1 分钟周期、grain 串行化、reminder 表随 silo
丢失后由启动激活器重装、多 silo 下整集群只跑一轮）。`porting/02-differences.md` 重新生成
（257 条：187 resolved / 10 gaps / 60 accepted），lint 通过。
