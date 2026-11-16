# §162 channelProbeData（通道探针读取根字段，20 例）

日期：2026-11-10
参照（冻结基线 `b8bb3421`）：`internal/server/gql/channel_probe.graphql:1-19`（类型与根字段）、
`internal/server/gql/channel_probe.resolvers.go:15-26`（resolver）、
`internal/server/biz/channel_probe.go:345-465`（`BatchQueryChannelProbes` /
`QueryChannelProbes` / `generateTimestamps`）、`internal/authz/scope.go`
（`RunWithScopeDecision`）、`internal/server/biz/system.go:487-627`（设置与开关）、
`1280-1370`（`ChannelSettingOrDefault`）、`internal/ent/schema/channel_probe.go`、
`internal/ent/migrate/schema.go:225-265`（表与索引）。

## 背景

前端 channels 页以 `GetChannelProbeData` 拉取通道探针曲线（channels.ts:2361-2393），
但端口既没有 `channel_probes` 表也没有该根字段；这是剩余三个前端在用根字段中的第一个。
冻结基线已包含完整读取链路，因此本切片按基线补表、读取 seam、服务与 GraphQL 三类型一根
字段。写入面（writer / cron 调度器）不在基线读取路径上，登记为 C249。

## 交付

- `src/AxonHub.Persistence/DatabaseSchema.cs`：`channel_probes` 幂等 DDL
  （identity id、64 位计数、nullable 均值、Unix 秒时间戳、channel_id）与非唯一索引
  `channel_probes_by_channel_id_timestamp`。
- `src/AxonHub.Persistence/Records.cs`：`ChannelProbeRecord`。
- `src/AxonHub.Persistence/Stores/ChannelProbeStore.cs`：`IChannelProbeStore` 与 Dapper
  批量读取（`ChannelIDIn`、闭区间、`ORDER BY timestamp`、空 id 短路、沿用当前事务）。
- `src/AxonHub.Persistence/AxonHubDbContext.cs`：EF 模型与索引。
- `src/AxonHub.Api/Management/ChannelProbeService.cs`：`BatchQueryChannelProbesAsync`
  （空输入在读取设置前返回空）、`QueryChannelProbesAsync`（scope 决定 + 截断窗口 + 映射 /
  零填充 / 输入顺序）、`EffectiveProbeSettingAsync`（无 `read_channels` 或任何存储 / 解码
  错误都回退默认设置）、range / interval / truncate / timestamp 生成的静态方法。
- `src/AxonHub.Api/GraphQL/ChannelProbeTypes.cs`：`ChannelProbePoint` / `ChannelProbeData`
  / `GetChannelProbeDataInput` 与映射（Int 按 C190 `checked` 截断，`channelID` 恒为 Channel
  GID）。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：`channelProbeData` 根字段（GUID 解析先于
  service，结果无 wrapper）。
- `src/AxonHub.Api/Program.cs`：注册 `IChannelProbeStore` 与 `ChannelProbeService`。

## 行为要点

- `end = UTCNow.Truncate(intervalMinutes)`、`start = end − rangeMinutes`；frequency 映射
  1m→10/1、5m→60/5、30m→720/30、1h→1440/60、未知→10/1（默认 5m）。
- scope decision：持有 `read_channels` 的主体读存储 `system_channel_settings`；否则（或读取 /
  解码失败）使用 `defaultChannelSetting`，查询仍成功，无错误返回。
- 输入 GUID 只取数字 id，**不校验 type**；`gid://axonhub/Whatever/42` 与 `Channel/42`
  等价；输出按输入顺序、重复 id 重复输出。
- 每个对齐时间点输出一点；缺失行为零计数与 null 均值；同 `(channel_id, timestamp)` 的重复行
  后者胜出。
- 存储查询错误原样冒泡（GraphQL 报 `probe query failed`，无前缀）。
- Go `time.Truncate` 的零时偏移是 60/300/1800/3600 的整数倍，Unix 秒上的 floor 取模与之
  等价。

## 测试

- `tests/AxonHub.Api.Tests/ChannelProbeServiceTests.cs`（10 Fact）：空输入、默认 5m 窗口
  (13 点 / 09:05–10:05)、存储 1h (25 点 / 10:00 截断)、无 `read_channels` 回退、未知频率
  (11 点 / 1 分钟步进)、解码失败与读取失败回退、行映射与同桶后行胜出 / 窗口外忽略 / null
  均值、重复 id 顺序、存储错误原样冒泡。
- `tests/AxonHub.Api.Tests/AdminChannelProbeDataEndpointTests.cs`（7 Fact）：前端形状、
  存储频率、deny 回退仍成功、空 `channelIDs` 的 `[]` 且不查询、非法 GID 文案、type 不校验、
  存储错误无前缀。
- `tests/AxonHub.Persistence.Tests/ChannelProbeStoreTests.cs`（3 Fact）：通道过滤与闭区间、
  跨通道时间升序与同桶双行、空 id。
- `tests/AxonHub.Persistence.Tests/EfCoreModelTests.cs`：表 23→24、索引 51→52。

静态计数：`AxonHub.Api.Tests` 5803 → 5820（+17），`AxonHub.Persistence.Tests` 359 → 362
（+3），`AxonHub.Grains.Tests` 1746、`AxonHub.Llm.Tests` 1512 不变，四项目合计
9420 → 9440。**按项目规则未运行 lint / build / test。**

## 台账

新增 C249（gap）：探针 writer（`runProbe` / `computeAllChannelProbesStats`）、每分钟 cron、
`runProbePeriodically`、GC `cleanupChannelProbes` 与 dashboard probes 分支未移植；读取面以
`AuthorizationScope.HasScopeAsync` 近似参照的 scope decision，系统设置缓存缺席（C029）使
「denied 主体命中缓存」的边界在端口不可达；GraphQL Int 32 位截断沿用 C190。
C195 item (5) 与 C203 item (1) 的「未移植该表 / schema 无该表」措辞已按建表事实更新为
「已建表，writer / 调度未移植」。`02-differences.md` 重新生成（249 条：179 accepted /
14 gaps / 56 resolved），lint 通过。
