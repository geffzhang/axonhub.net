# §155 模型目录（catalog）管理面（2 查询 / 2 变更）

日期：2026-11-09
参照：`internal/server/biz/catalog_settings.go:16-29`（键、默认 URL、间隔 3600/60/604800）、
`31-42`（normalizeCatalogSettings）、`44-63`（validateCatalogSettings 与 `url.Parse`）、
`65-78`（EffectiveUpstreamURL / default）、`80-125`（CatalogSettings /
CatalogSettingsOrDefault / SetCatalogSettings）、`internal/server/biz/catalog.go:36-41`
（CatalogSnapshot）、`43-59`（cache / service）、`68-115`（RegisterScheduledTasks /
Reschedule / taskSpec / refreshPeriodically）、`117-133`（Snapshot / Refresh）、
`135-195`（ensureCache / freshCache / fallbackOrCached）、`197-227`（fetchUpstream）、
`229-261`（snapshotFromCache）、`263-296`（storeCache / cached / fallbackCache）、
`298-303`（Invalidate）、`internal/server/biz/catalog_filter.go:112-158`
（filterCatalogProviders 与各后处理开关）、`159-198`（meta/llama）、`200-239`（ibm）、
`241-265`（doubao→bytedance）、`267-317`（tencent 计划）、`319-363`（xiaomi 计划）、
`365-435`（KAT→kwaipilot）、`437-456`（nvidia）、`458-558`（extra models）、
`560-...`（排序）、`internal/server/gql/system.graphql:392-410、433-440`（SDL）、
`internal/server/gql/system.resolvers.go:339-364、366-373、442-462、464-475`
（四个根字段与逐层前缀）。

## 背景

管理面的“模型”页面依赖 `providersCatalog` 与 `refreshProvidersCatalog`，系统设置页依赖
`catalogSettings` 与 `updateCatalogSettings`。§152/§153/§154 已覆盖 system-settings 的
前三批，本切片补齐模型目录的四个根字段及其后台刷新运行时。

## 交付

- `src/AxonHub.Api/Settings/CatalogSettings.cs`：`CatalogSettings` 记录、
  `CatalogSettingsRules`（默认 URL、默认 / 最小 / 最大间隔、Normalize、
  EffectiveUpstreamUrl、Validate）与 `CatalogSettingsCodec`（Go 字段序
  `upstream_url` → `refresh_seconds`）。`Validate` 逐字符复刻 Go 1.26.3
  `net/url.Parse`：fragment 剥离、控制字符、scheme 小写、authority 判定、userinfo、
  host 方括号 / IPv6 / zone、端口、百分号转义、相对与 opaque URL，错误文本与引号
  形状对齐（C237）。
- `src/AxonHub.Api/Settings/SystemSettingsAdminService.cs`：新增
  `GetCatalogSettingsAsync`（缺行默认、读错误双层前缀、解码
  `failed to unmarshal catalog settings`）、`GetCatalogSettingsOrDefaultAsync`
  （吞掉全部异常）、`SetCatalogSettingsAsync`（归一化 → 校验 → 序列化 → 写存储）。
- `src/AxonHub.Api/Catalog/CatalogJson.cs`：`CatalogFile` / `CatalogProvider` /
  `CatalogParser` 与 Go 键序序列化。
- `src/AxonHub.Api/Catalog/CatalogFilter.cs`：22 个 `DefaultDeveloperIds`、
  `FilterProviders` 与 meta/llama、ibm granite、doubao→bytedance、tencent / xiaomi
  计划合并、KAT→kwaipilot、nvidia 前缀过滤、thinkingmachines 删除，
  `MergeExtraModels`、`SortCatalogModels` / `SortModelsByReleaseDate`。
- `src/AxonHub.Api/Catalog/CatalogService.cs`：单例缓存（lock）+ 共享
  `_refreshInFlight` 复刻 singleflight；鲜度窗口、20s 抓取超时、失败回落
  last-good / 内嵌数据、filtered 快照（过滤 + extra models + 排序）、source 空回落
  fallback、`fetch catalog` / `parse catalog` / `catalog is missing providers` 文本；
  settings 经 `IServiceScopeFactory` 读取。
- `src/AxonHub.Api/Catalog/CatalogRefreshScheduler.cs`：`BackgroundService` 复刻
  FixRate 定时任务与分离的初始刷新；`ICatalogRescheduleSignal` 用唤醒 TCS 从当前时刻
  重新计时。
- `src/AxonHub.Api/Catalog/Data/providers.json` / `models.json`：与参照
  `catalogdata/` 两个文件 SHA-256 一致。
- `src/AxonHub.Api/GraphQL/CatalogTypes.cs`：CatalogSettings / UpdateCatalogSettingsInput /
  ProvidersCatalog 与域映射（JsonRawMessage、GoTimeType）。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：`providersCatalog`（filtered 默认 true、
  read_channels、`catalog service is not configured` / `failed to load providers
  catalog: ...`）、`catalogSettings`（read_settings、读前缀）；`AdminMutation.cs`：
  `updateCatalogSettings`（write_settings、OrDefault patch、成功后 Invalidate +
  可选 Reschedule）、`refreshProvidersCatalog`（write_settings、未配置与失败前缀）。
- `src/AxonHub.Api/Program.cs`：CatalogService / ICatalogService 单例、
  CatalogRefreshScheduler 单例 + hosted service、ICatalogRescheduleSignal 映射。

## 行为要点

- catalogSettings 读：缺行 → `{upstreamURL:"", refreshSeconds:3600}`；存储文档解码后
  TrimSpace、<=0 → 3600、<60 → 60、>604800 → 604800；存储失败为
  `failed to get catalog settings: failed to get catalog settings: failed to get
  system value: ...`。
- catalogSettings 写：`input:{}` 从当前存储值出发只 patch 非 nil 字段；空 URL 放行；
  Go `url.Parse` 失败 → `invalid catalog upstream url: parse %q: ...`；非 http/https
  与缺 host 各有固定文案；成功返回 true 并触发 Invalidate + Reschedule（service 为
  nil 时跳过副作用但写库成功）。
- providersCatalog：`filtered` 默认 true；快照四字段；失败前缀
  `failed to load providers catalog: ...`；服务账号 API key 不通过 read_channels。
- refreshProvidersCatalog：强制抓取，失败 `failed to refresh providers catalog: ...`
  且不回落缓存。
- 快照过滤：允许列表 + meta/llama + ibm + bytedance + tencent + xiaomi + kwaipilot +
  nvidia + thinkingmachines 删除；extra models 合并；release_date 降序、同日 id 升序。

## 测试

- `tests/AxonHub.Api.Tests/CatalogFilterTests.cs`（36）：允许列表与特殊合并、meta/llama
  来源、参照接受 / 拒绝四例、Go `net/url` 28 例 oracle（8 接受 / 20 逐字错误）、
  内嵌 JSON 解析与合并、20 轮确定性顺序、release_date 排序、开发者 id 与前端
  constants.ts 一致。
- `tests/AxonHub.Api.Tests/AdminCatalogEndpointTests.cs`（19）：设置默认 / 归一 / 双层
  前缀 / read_settings；update 逐字段 patch、间隔归一、非法 URL 不写库、write_settings、
  Invalidate/Reschedule 计数；providersCatalog 的 filtered 默认与显式 false、失败前缀、
  read_channels、服务账号拒绝；refresh 强制快照与错误前缀；未注册服务时的 not
  configured 与 update 仍写库。
- `tests/AxonHub.Api.Tests/CatalogServiceTests.cs`（9）：回落 + 刷新 + Invalidate；
  8 并发冷读只抓一次；请求线路形状（GET、URL、Accept）；鲜度窗口；502 / 缺 providers /
  解码 / 传输四条错误链；无 client 未配置。

静态计数：`AxonHub.Api.Tests` 5595 → 5659（+64），`AxonHub.Grains.Tests` 1746、
`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 353 不变，四项目合计 9200 → 9264。
**按项目规则未运行 lint / build / test。**

台账：基线 §155、验收 §155 已写入；新增 C237（accepted：Go `net/url` 复刻的两处近似）
与 C238（accepted：进程级单例、singleflight 与 BackgroundService 调度实现细节）。
`02-differences.md` 重新生成（238 条：173 accepted / 55 resolved / 10 gaps），lint 通过。
