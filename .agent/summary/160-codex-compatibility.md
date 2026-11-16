# §160 Codex 兼容性设置与模型目录（GraphQL 5 根字段 + 目录服务，37 + 6 例）

日期：2026-11-09
参照（post-baseline 漂移，按工作仓库 `f272549f`）：`internal/server/biz/system_codex.go`
（`codex_compatibility` 键、`CodexCompatibilitySettings{Enabled, ChannelID}`、读取 / 启用判定 /
写入校验）、`internal/server/biz/codex_catalog.go`（`CodexCatalogError` / `CodexCatalog` /
`ParseCodexCatalog` / `FetchCodexCatalog` / `fetchCodexCatalog`）、
`internal/server/biz/channel_apikey_strategies.go`（多 key 策略分派）、
`internal/server/gql/system.graphql:409-440`、`system.resolvers.go:25-56,470-497`、
`models_gen.go:238-266`、`internal/server/api/codex_compatibility.go`、
`internal/server/codex_routes.go:10-29`。

## 背景

Codex 兼容性是基线后新增的特性：冻结基线 `b8bb3421` 没有 `system_codex.go` /
`codex_catalog.go`、没有 `codexCompatibility*` schema，也没有 `/codex/*` 运行时路由。
前端的管理设置页调用五个根字段（`codexCompatibilitySettings`、`codexCompatibilityEnabled`、
`codexCatalogChannels`、`updateCodexCompatibilitySettings`、`testCodexCatalog`），本切片
接入管理面与目录抓取服务；运行时路由登记为 C244。

## 交付

- `src/AxonHub.Api/Settings/CodexCompatibilitySettingsService.cs`：
  `CodexCompatibilitySettings`、`ICodexCompatibilitySettingsService` /
  `CodexCompatibilitySettingsService`、`CodexCompatibilitySettingsStored` 与
  `CodexCompatibilitySettingsCodec`（手写 Go 容忍解码）。
- `src/AxonHub.Api/Management/CodexCatalogService.cs`：`CodexCatalogException` /
  `CodexCatalog` / `ICodexCatalogService` 与单例 `CodexCatalogService`；singleflight
  （`ConcurrentDictionary<string, Lazy<Task>>`）、每次飞行自建 `IServiceScope` 与 30s
  分离预算、`ParseCodexCatalog`、`RewriteCatalogUrl`、代理 executor 选择。
- `src/AxonHub.Api/GraphQL/CodexCompatibilityTypes.cs`：`CodexCompatibilitySettingsType`、
  `UpdateCodexCompatibilitySettingsInput`、`CodexCatalogChannelType`、
  `CodexCatalogTestResultType` 与映射（`channelID` 按 Int 截断，状态复用
  `ChannelMapping.ToChannelStatusValue`）。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs` / `AdminMutation.cs`：三个查询与两个变更。
- `src/AxonHub.Api/Program.cs`：注册设置服务（scoped）与目录服务（singleton）。
- `src/AxonHub.Llm/Http/HttpRedirectPolicy.cs` / `HttpExecutor.cs` /
  `HttpRedirectPolicies.cs`：新增 `SameOrigin` 策略与 `WithSameOriginRedirects`
  （对比 `via[0]`、链上 previous、10 跳上限、跨源返回 3xx）。
- `tests/AxonHub.Api.Tests/VideoTaskTestDoubles.cs`：补上 `FakeChannelStore` 缺失的
  `GetLatestUpdatedAtAsync`（潜在编译缺口，非行为变更）。

## 行为要点

- 存储键 `codex_compatibility`；值按 Go 字段序 `{"enabled":bool,"channelID":int|null}`，
  `channelID` 无 omitempty（禁用时为 `null`）。
- 缺行或字面量 `"null"` → 零值结构。解码容忍：键大小写不敏感、重复键后者胜、
  `null` 进 bool 为 no-op、`null` 进 `*int` 清空、int 为 64 位；四类失败保留 Go 逐字
  文案并以 `decode Codex compatibility settings: ` 为前缀；损坏 JSON 同前缀。
- 读取需 `read_settings`；启用判定走系统旁路（匿名可读）。读失败前缀分别是
  `read Codex compatibility settings: ` 与 `read Codex compatibility enabled state: `，
  内层 `failed to get system value: `。
- 写入需 `write_settings`。`Enabled && ChannelID == nil` → VALIDATION_FAILED
  `a Codex channel is required when enabled`；有 ChannelID 时需 `read_channels`，
  `Channel.Get` 任何错误 → `Codex catalog channel is unavailable or inaccessible`，
  类型非 codex → `catalog channel must be Codex`；持久化为 Go 形状的 JSON，
  写失败 `failed to create system setting: `。
- `codexCatalogChannels`：需 `read_settings` 再 `read_channels`，返回全部 codex 行
  （含 archived，无显式排序）。
- `testCodexCatalog`：需 `write_settings` 再 `read_channels`；前置查询失败为普通 GraphQL
  错误；抓取失败返回 `{success:false, modelCount:0, error:<message>, upstreamStatus:<仅非 0>}`
  且 GraphQL error 为 nil；成功返回 `{success:true, modelCount:<数>}`。
- 目录服务：单飞键 `"%d:%s"`，调用方取消 → 502 `Codex catalog request was cancelled`，
  飞行本身有 30s 预算且与调用方取消解耦；实时行缺失 / 非 codex → 503
  `Codex catalog source is unavailable`；启用快照存在时用其类型判定（codex / fenno）并
  复用快照 Settings 的代理 executor；实时分支用行内 Settings；凭据构建失败 → 502
  `Codex catalog credentials are unavailable`；`wss://` / `ws://` 小写敏感改写；URL 查询串
  按 Go `url.Parse` + `Query()` + `Encode()` 回写（`client_version` 非空才覆盖）；上游
  HTTP 失败 → 502 `Codex catalog upstream HTTP %d` + UpstreamStatus；传输失败 → 502
  `Codex catalog transport failed`；目录解析严格（对象根、精确 `models` 数组、每项对象且
  `slug` 非空）→ 502 `invalid Codex model catalog`。
- 抓取执行器安装 same-origin 重定向策略：跨主机 / scheme / 端口时把 3xx 交给调用方；
  同源链继续跟随，10 跳封顶。

## 测试

`tests/AxonHub.Api.Tests/CodexCompatibilitySettingsServiceTests.cs`（16 Fact + 2 Theory /
6 InlineData = 22 例）：缺行零值、存储 `"null"`、大小写键、重复键与 null 清空、null bool
no-op、64 位 int、四类解码失败逐字文案、损坏值前缀、两处读前缀、启用判定免权限、写入
Go 形状与 null channelID、启用无通道 / 缺通道 / 非 codex 三类校验、`read_channels` 门、
写失败前缀、三种 scope 组合。

`tests/AxonHub.Api.Tests/AdminCodexCompatibilityEndpointTests.cs`（15 Fact）：设置精确形状、
enabled 免 scope、更新往返与存储 JSON、VALIDATION_FAILED 信封、`write_settings` 门、目录
通道仅 codex（含 archived）与 Where 断言、两个 scope 门、上游 404 不泄漏 body 与请求形状
（GET / URL / Authorization / Version / Accept）、成功计数、非法目录、前置普通错误、
非 codex 行 503、零启用 key 的凭据错误（C246）、缓存出站类型不匹配 502、testCatalog 两个
scope 门。

`tests/AxonHub.Llm.Tests/HttpRedirectTests.cs`（+6 Fact）：same-origin 跟随、跨主机 /
跨 scheme / 跨端口返回 3xx、10 跳上限、保留 3xx body。

静态计数：`AxonHub.Api.Tests` 5751 → 5788（+37），`AxonHub.Llm.Tests` 1506 → 1512（+6），
`AxonHub.Persistence.Tests` 359、`AxonHub.Grains.Tests` 1746 不变，四项目合计
9362 → 9405。**按项目规则未运行 lint / build / test。**

## 台账

- C244（gap）：`/codex/*` 运行时路由（Enabled 门、whoami、accounts/check、models 交集、
  images、responses 与 WS）未移植；管理面与目录服务已完整。
- C245（accepted）：same-origin 比较使用 .NET 规范化后的 Scheme / Authority（小写主机、
  省略默认端口），参照用 Go 的字面 `URL.Host`；只在同源字面变体上可见。
- C246（accepted）：零启用 key 且配置 key ≤ 1 时参照 `getAPIKeyProvider` panic，经
  singleflight `DoChan` 使整个进程退出；端口折叠为有界 502
  `Codex catalog credentials are unavailable`，并以端点测试锁定。
- C247（gap）：多 key `apiKeyStrategy`（random / round_robin / round_robin_success /
  priority / fixed）与共享选择状态未移植；端口保持基线 sticky / random。
