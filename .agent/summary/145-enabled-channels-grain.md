# §145 启用通道缓存迁入 EnabledChannelsGrain（A2）

日期：2026-11-08
参照：`internal/server/biz/channel.go`（enabledChannelsCache / reloadEnabledChannels /
onEnabledChannelsSwap）、`internal/pkg/xcache/live/cache.go`、
`internal/server/biz/channel_apikey.go:107-141`。

## 背景

A1 之后 grain 仍只有三个（`ChannelGrain`、`ChannelPoolGrain`，加上 A1 的限流切片）。
参照的启用通道集合由进程内 `live.Cache` 持有：1 分钟周期刷新、`updated_at` 指纹跳过、
强制交换递增 `cacheVersion`、变更后 500ms 防抖重载、刷新失败保留旧快照。端口此前没有缓存，
选择路径每次直接读库（`PostgresEnabledChannelProvider` / `IChannelStore`），
因此 A2 把这份状态收进 `EnabledChannelsGrain`，让启用通道集合在集群里只有一个写入口。

## 交付

- 新增 `IEnabledChannelSource`（AxonHub.Abstractions）：指纹查询 + 启用通道投影。
  grains 不能引用 Persistence，所以数据库半经该接缝注入；生产实现
  `PostgresEnabledChannelSource` 调用 `ChannelStore`，新增
  `GetLatestUpdatedAtAsync`（无软删谓词，软删行也推进指纹）。
- 新增 `EnabledChannelsGrain`（string key `enabled-channels`）：
  - `GetSnapshotAsync` 首次以 force 懒加载（参照在构造时同步加载，失败 panic）；
  - `RefreshAsync(force)` 复用同一 reload：非强制且指纹未前进时跳过；
    强制时无论是否变化都交换并递增 `cacheVersion`；
  - `RequestRefreshAsync` 用一次性 grain timer 做 500ms 防抖，多次触发合并为一次强制刷新；
  - 每 1 分钟的非强制周期刷新；单次刷新 30s 上界；刷新失败记日志并保留旧快照。
- 新增 `EnabledChannelSnapshot` / `EnabledChannelsSnapshot`（Grains.Abstractions）：
  `ChannelDescriptor` 的 Orleans 序列化孪生，逐字段双向映射。
- API 侧：`GrainEnabledChannelProvider` 读 grain 快照，替换
  `PostgresEnabledChannelProvider`；`IEnabledChannelsCacheRefresher` 由
  `GrainEnabledChannelsCacheRefresher` 实现（`RequestRefresh` 异步防抖、
  `ReloadAsync` 同步强制）。`ChannelLifecycleService` 的全部变更入口在成功后请求刷新；
  API key 全部禁用路径先同步强制刷新（`WaitAsync(10s)`，失败记 warning）再请求防抖刷新。
- `ChannelModelSyncService` / `ChannelOverrideTemplateService` 的 reload hook 改为
  `GrainChannelModelSyncReloadHook`（C158 的两处 no-op 关闭）。
- 测试基建：Grain 测试的 cluster fixture 改为 collection fixture（静态注入的假 provider
  不能并发建 cluster），两个既有测试类标注 `[Collection]`；假 provider 同时实现
  `IEnabledChannelProvider` 与 `IEnabledChannelSource`。

## 行为要点

- 单 silo 下与参照逐项等价：指纹包含软删除、非强制跳过、强制交换与版本递增、
  失败保留旧快照、500ms 防抖、10s 同步重载上界。
- 延迟差异：初始加载改为首次读取时执行；读者在重载进行时排队（参照无锁读旧快照）；
  `OnSwap` 的资源清理（token provider、空闲连接）在端口没有对应对象。
- 未改动的路径：`ModelCandidateSelector` 的关联解析与 `ModelService` 仍实时读库
  （C149/C178/C179）；`CacheVersion` 尚未被任何消费者使用。
- 差异登记为 C226：集群单激活、懒加载、阻塞读、无资源清理、watcher / 自动禁用事件未移植。

## 测试

- `tests/AxonHub.Grains.Tests/EnabledChannelsGrainTests.cs`（11）：冷加载与版本 1
  （含读取不重复查询源）、指纹未变跳过、指纹前进重载、仅删除也重载、空库不重载、
  强制绕过指纹、空库强制仍递增版本、失败保留旧快照、冷加载失败传播且下次重试、
  防抖合并为一次强制重载、指纹 UTC 归一；同文件 `EnabledChannelSnapshotTests`（1）
  快照往返全字段。
- `tests/AxonHub.Api.Tests/EnabledChannelsGrainProviderTests.cs`（2）：provider 经 Orleans
  映射 grain 快照全字段；`ReloadAsync` 无指纹变化也强制交换。
- `tests/AxonHub.Persistence.Tests/ChannelStoreTests.cs`（追加 1）：
  `GetLatestUpdatedAtSeesSoftDeletedChannels`。
- 调用点断言（扩展现有测试）：`AdminChannelApiKeyEndpointTests` 的
  `DisableOfTheLastKeyDisablesTheChannel`（1 次同步重载 + 1 次防抖刷新）与
  `DisableAppendsTheRecordAndKeepsTheChannelEnabled`（仅防抖刷新）；
  `AdminChannelLifecycleEndpointTests` 的
  `UpdateChannelStatusClearsTheMarkersAndReturnsTheChannel`（变更后请求刷新）。

静态计数：`AxonHub.Grains.Tests` 1692 → 1704（+12），`AxonHub.Api.Tests` 5432 → 5434（+2），
`AxonHub.Persistence.Tests` 331 → 332（+1），四项目合计 8956 → 8971
（`AxonHub.Llm.Tests` 1501 不变）。**按项目规则未运行 lint / build / test。**

台账：基线 §145、验收 §145 已写入；差异台账新增 C226（accepted），并更新
C149 / C158 / C178 / C179 的缓存与刷新描述；总数 226 条（163 accepted / 54 resolved / 9 gap），
`diff_register.py lint` 通过、`02-differences.md` 已重新生成。
