# §154 Webhook 通知器配置与版本检查管理面（2 查询 / 1 变更）

日期：2026-11-09
参照：`internal/server/biz/system.go:416-434`（WebhookNotifierConfig 族与 JSON 标签）、
`1174-1186`（normalizeWebhookNotifierConfig）、`1188-1209`（WebhookNotifierConfig 读）、
`1225-1234`（SetWebhookNotifierConfig）、`internal/server/biz/version.go:39-63`
（CheckForUpdate）、`65-134`（FetchLatestGitHubRelease 的请求与错误）、`136-186`
（selectLatestGitHubRelease 过滤）、`188-393`（标签判定、IsNewerVersion / CompareVersions
与自定义 prerelease 比较）、`internal/build/info.go:14-54` 与 `internal/build/VERSION`
（build.Info 六字段、冻结版本 v1.0.0-beta9）、`internal/server/gql/system.graphql:69-114、
443-457、488-493`（SDL）、`internal/server/gql/system.resolvers.go:69-77、524-527、
589-606`（根字段与逐层前缀）。

## 背景

前端“系统 -> Webhook”页面依赖 `webhookNotifierConfig` 查询与 `updateWebhookNotifierConfig`
变更；首页版本徽标与“检查更新”依赖 `systemVersion` 与 `checkForUpdate`。§152/§153 只覆盖了
system-settings 的前三批，本切片补齐这四个根字段。

## 交付

- `src/AxonHub.Api/Settings/WebhookNotifierSettings.cs`：`WebhookNotifierConfig` /
  `WebhookTarget` / `WebhookHeaderEntry` / `WebhookSubscription` 域记录与
  `WebhookNotifierConfigCodec`——Deserialize（nil 元素 → 零值、null 标量 → 零值、顶层
  null → 默认值，随后 Normalize）、Serialize（Go 字段序 targets → subscriptions，target 序
  name/enabled/url/proxy/timeout_ms/headers/body；proxy 内 type 恒输出、其余 omitempty、
  `disableConnectionReuse` camelCase；headers / target_names nil 写 `null`；经
  `GoJson.Escape`）、Normalize（仅 nil 列表 → 空数组）与 Default。
- `src/AxonHub.Api/Settings/SystemVersionService.cs`：`SystemBuildInfo` /
  `VersionCheckResult` / `ISystemVersionService`；`SystemVersionService` 提供固定
  `v1.0.0-beta9`、空 commit/buildTime、.NET 运行时描述、宿主平台到 GOOS/GOARCH 的映射与
  Go `time.Duration.String()` 布局的 uptime；`CheckForUpdateAsync` 复刻 GET
  `https://api.github.com/repos/looplj/axonhub/releases?page=1&per_page=100`（键序由
  url.Values.Encode 决定）、Accept / User-Agent 常量、10 秒超时、`failed to fetch
  releases` / `GitHub API returned status %d` / `failed to decode releases` 包装与
  `failed to fetch latest release` 外层；内部 `SelectLatestGitHubRelease` 复刻 draft、
  小写 v 前缀、beta / 非 beta 预发布、30 分钟冷却与最高版本选择；`TryParseVersion` /
  `CompareVersions` / `IsNewerVersion` 复刻 Masterminds 解析（1-3 段、仅小写 v、metadata
  校验但不参与比较）与数字串比较（beta10 == beta.10、beta9 < beta10、长计数不溢出）；
  `GoDuration` 复刻 Go 的 ns/µs/ms 与 h/m/s 分支。
- `src/AxonHub.Api/Settings/SystemSettingsAdminService.cs`：新增
  `GetWebhookNotifierConfigAsync`（缺行 → 归一化零值；读错误
  `failed to get webhook notifier config: failed to get system value: ...`；解码错误
  `failed to unmarshal webhook notifier config: ...`）与 `SetWebhookNotifierConfigAsync`
  （先归一化 → 序列化，marshal 错误 `failed to marshal webhook notifier config` →
  `failed to create system setting`）。
- `src/AxonHub.Api/GraphQL/WebhookNotifierTypes.cs`：HeaderEntryInput、WebhookTarget /
  WebhookTargetInput、WebhookSubscription / WebhookSubscriptionInput、
  WebhookNotifierConfig / WebhookNotifierConfigInput 与域 ↔ GraphQL 映射；nil
  headers / targetNames / targets / subscriptions 输出为 `[]`（gqlgen 的列表 marshaler
  对 nil 切片执行 `make(graphql.Array, len(v))`，不报非空违规），未知 ProxyType 抛
  GraphQLException（C139 家族）。
- `src/AxonHub.Api/GraphQL/SystemVersionTypes.cs`：SystemVersion / VersionCheck 输出类型
  与映射。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：`webhookNotifierConfig`、`systemVersion`、
  `checkForUpdate`（`failed to check for update: ...`，includeBeta 默认 false）；
  `AdminMutation.cs`：`updateWebhookNotifierConfig`（`failed to update webhook notifier
  config: ...`，成功返回 true）。
- `src/AxonHub.Api/Program.cs`：`ISystemVersionService` 注册为单例（TimeProvider 由宿主
  注入；测试可传脚本化 `HttpMessageHandler`）。

## 行为要点

- webhook 读：缺行 → `{targets:[],subscriptions:[]}`；存储文档按 Go 字段序解码，nil
  元素成零值，随后只归一化两个顶层列表；headers / target_names 为 null 时输出 `[]`。
- webhook 写：gqlgen 直接解码输入，缺省列表为 nil → 归一化；文档 target 缺省 proxy 省略、
  缺省 headers 写 `null`；`input: {}` 写 `{"targets":[],"subscriptions":[]}`；错误前缀
  `failed to update webhook notifier config: ...`。
- systemVersion：六字段固定形状；version = v1.0.0-beta9，commit / buildTime 为空串，
  goVersion / platform / uptime 为环境相关（C235）。
- checkForUpdate：稳定检查跳过 beta 与 rc；includeBeta 只在标签含 `-beta` 时保留；
  发布后 30 分钟内不可见；beta 计数按数值比较；错误链为
  `failed to check for update: failed to fetch latest release: <内层>`，内层文本随 .NET
  异常变化（C235）。

## 测试

- `tests/AxonHub.Api.Tests/AdminSystemSettingsEndpointTests.cs`（3）：webhook 缺行默认、
  存储文档读取（proxy 五字段、headers、subscriptions）、最小 target 零值与
  `headers:null` → `[]`、显式 null 列表归一；变更写入的 Go 字段序文档（proxy omitempty、
  headers null、camelCase）与 `input:{}`；读失败 / 解码失败 / 写失败三层错误前缀。
- `tests/AxonHub.Api.Tests/AdminSystemVersionEndpointTests.cs`（67）：systemVersion 六字段
  与 Go 时长布局；稳定版选择与出站请求线路形状；includeBeta 的 beta10 数值选择与 rc
  排除；非 200 / 解码 / 无可用版本 / 传输失败四条错误链；IsNewerVersion 31 例、
  CompareVersions 16 例（含反对称）、非法输入（大写 V、坏 metadata）、release 选择参照
  表、GoDuration 14 例。测试宿主注册脚本化 `HttpMessageHandler` 的
  `SystemVersionService(TimeProvider.System, handler)`。

静态计数：`AxonHub.Api.Tests` 5525 → 5595（+70），`AxonHub.Grains.Tests` 1746、
`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 353 不变，四项目合计 9130 → 9200。
**按项目规则未运行 lint / build / test。**

台账：基线 §154、验收 §154 已写入；新增 C235（accepted：systemVersion 构建元数据与
checkForUpdate 的 .NET 错误内层文本）与 C236（gap：webhook 通知器运行时未移植）。
`webhook_notifier_config` 已在备份键清单中，备份语义不变。`02-differences.md` 重新生成
（236 条：171 accepted / 55 resolved / 10 gaps），lint 通过。
