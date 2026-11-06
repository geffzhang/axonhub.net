# §114 供应商配额采集核心（ProviderQuotaService / provider_quota_status / GraphQL）

日期：2026-11-07
参照：`f272549f:internal/server/biz/provider_quota.go`（`loadQuotaCache`、`registerProviderQuotaSupport`、
`RegisterScheduledTasks`、`getWarningCheckInterval` / `getCheckInterval`、`GetQuotaStatus`、
`InvalidateChannelQuota`、`getProviderType`、`hasCredentialsForProvider`、`extractLimitsFromQuotaData`、
`maxConcurrentQuotaChecks`、`providerQuotaChannelTypes`）、`internal/server/biz/provider_quota/`
（`types.go`、归一化与聚合、`provider_quota_account.go` 的 `quotaAccountKey` /
`groupChannelsByQuotaAccount`、`url_detection.go`、`ListResets` / `ResetChannelQuotaNow` 与
checker / resetter 接口）、`provider_quota_settings.go:12-174`、`provider_quota_cost.go`、
`channel_provider_quota_hook.go`、`internal/server/db/removed_provider_quotas.go`、
`internal/ent/schema/provider_quota_status.go`、`internal/server/gql/ent.graphql:3729-3758`、
`ent.resolvers.go:98-142` 以及 `axonhub.graphql` 的四个配额面。

## 交付

- `src/AxonHub.Api/Orchestration/ProviderQuotaService.cs`：`Initialize` 缓存加载、`CollectAsync` 采集
  （force / 到期判定 / account 分组 / 组间并行 8）、`CheckChannelQuotaAsync`、`SaveQuotaStatusAsync`、
  `SaveQuotaErrorAsync`（1/2/4/8 退避与 provider 变更 / 凭据失效 reset）、`ListResetsAsync`、
  `ResetChannelQuotaNowAsync`、`FillPeriodQuotasAsync`、`InvalidateChannelQuotaAsync`、
  `GetQuotaStatus` 同步读缓存（设置快照门禁）、`ManualCheckAsync`。
- `ProviderQuotaStatus.cs`：`QuotaLimitStatus` / `QuotaChannelStatus` / `QuotaData` / `ResetList` 与
  状态、窗口、限额类型常量。
- `ProviderQuotaData.cs`：`Normalize`（ratio 截断、过期时间清除、分组就绪 OR、状态推导）、
  `FilterLimitsForModel`、`CloneLimit(s)`、`MergeQuotaErrorData`、`QuotaErrorBackoff` /
  `NextQuotaErrorCount`、`IntervalToCronExpr`、`QuotaAccountKey`、`ProviderQuotaUrlDetection`。
- `ProviderQuotaCheckerRegistry.cs`：`ProviderQuotaCheckerRegistration`、`IProviderQuotaCheckerRegistry`；
  当前注册为空，所有通道走参照的 “No checker for provider” 分支。
- `ProviderQuotaCollectionSettingsService.cs` / `ProviderQuotaSettingsCache.cs`：
  `provider_quota_collection_settings` 的严格读取、OrDefault 回退、19 项默认全启用、归一化（保留升级
  新增默认项、丢弃未知键）、写入校验（未知 / 重复 provider）、Go 形状 JSON（键排序）与同步快照。
- `src/AxonHub.Api/Management/ProviderQuotaInitializationHostedService.cs`：启动时加载配额缓存并预热
  设置快照（失败只记录）。
- `src/AxonHub.Api/Management/ProviderQuotaScheduler.cs`：`*/5 * * * *` UTC 调度，错过不回补，失败只记录。
- `src/AxonHub.Persistence/Stores/ProviderQuotaStatusStore.cs`：`ListAllAsync`、`GetByChannelIdAsync`、
  `UpsertAsync`（`ON CONFLICT (channel_id) DO UPDATE` 只改插入列）、`CreateAsync`（唯一约束）、
  `ResetForProviderErrorAsync`、`UpdateErrorAsync`、`HardDeleteByChannelIdAsync`。
- `src/AxonHub.Persistence/DatabaseSchema.cs`：`provider_quota_status` 表、三个索引与启动清除
  `provider_type IN ('wafer')`（含软删行）。
- `src/AxonHub.Persistence/Stores/ChannelStore.cs`：`ListEnabledByTypesAsync`；
  `UsageLogStore.SumCostByChannelAsync`（半开窗口、NULL 合并）。
- `src/AxonHub.Api/Management/ChannelLifecycleService.cs`：provider 身份变化或配额 cookie 清空时调用
  `InvalidateChannelQuota`，失败只记录。
- GraphQL：`AdminQuery.providerQuotaCollectionSettings`、`AdminMutation.checkProviderQuotas` /
  `resetChannelQuotaNow` / `updateProviderQuotaCollectionSettings`、`Channel.providerQuotaStatus`、
  `ProviderQuotaStatus` 类型、`Map` 标量（`MapScalarType : JsonRawMessageType`）与
  `ProviderQuotaCollectionSettings` 类型。
- `src/AxonHub.Api/Program.cs`：注册 `ProviderQuotaService`、设置服务与快照、空注册表、
  初始化 hosted service、调度器与 `IProviderQuotaStatusStore`；路由闸门读取 `ProviderQuotaService`。

## 行为要点

- 受支持集合：`ProviderQuotaChannelTypes` 28 项；`GetProviderType` 折叠到 19 个 checker 键
  （`moonshot_coding` → `kimi_code`、五类 `zenmux_*` → `zenmux`、`openai(_responses)` 按 base URL
  识别 `synthetic` / `neuralwatt` / `apertis` / `charm_hyper`）。
- 采集：只选启用、类型可折叠且设置启用的通道；无凭据时硬删配额行并清缓存；无 checker 保留旧行；
  `force` 跳过到期判定；组间并行 8；设置读取失败告警后继续。
- 状态：`Normalize` 后 upsert；`warning` 20 分钟、其余 5 分钟；`_resets` 由 resetter 填入
  （失败只写 `error`）；错误计数按 1/2/4/8 倍退避并走 reset 或 error 更新。
- 失效钩子：类型 / base URL / 端点 / Command Code 设置变化或 cookie 清空触发硬删 + 清缓存；
  钩子异常不阻断通道更新。
- 持久化：upsert 冲突不改 `created_at` / `updated_at` / `deleted_at`；create 依赖 `channel_id`
  唯一约束（含软删行）；启动清除退役 provider。
- GraphQL：`providerQuotaStatus` 缺行 / 退役类型 / 设置禁用返回 null；`checkProviderQuotas` 在互斥锁内
  同步完成；`resetChannelQuotaNow` 需 `write:channels` 用户 scope；错误按参照包裹。

## 测试

- `tests/AxonHub.Api.Tests/ProviderQuotaServiceTests.cs`：31 例——初始化 / 设置门禁 / 缓存副本 / 失效 /
  采集遍历 / 状态持久化 / 间隔退避 / 失败与 reset / 周期成本 / zenmux 分组 / 无 checker / 无凭据 /
  设置读失败 / 调度到期。
- `tests/AxonHub.Api.Tests/ProviderQuotaDomainTests.cs`：21 例 + 27 个 InlineData——归一化边界、状态
  推导、model 过滤、退避、cron、limit 合并与提取、legacy 错误键、provider 键与 URL 检测、zenmux
  account key、RFC3339。
- `tests/AxonHub.Api.Tests/ProviderQuotaCollectionSettingsServiceTests.cs`：11 例——默认 / 归一化 /
  严格读取 / 回退 / 写入校验 / 错误包装 / 快照。
- `tests/AxonHub.Api.Tests/AdminChannelsEndpointTests.cs`：+2——真实行形状与退役 / 禁用隐藏。
- `tests/AxonHub.Api.Tests/AdminSystemSettingsEndpointTests.cs`：+6——设置查询 / 更新 / 错误、路由设置
  写入、强制采集、立即重置。
- `tests/AxonHub.Api.Tests/AdminChannelUpdateEndpointTests.cs`：+4——失效钩子的四种路径。
- `tests/AxonHub.Persistence.Tests/ProviderQuotaStatusStoreTests.cs`：6 例——upsert 冲突列、软删谓词、
  create 唯一约束、reset / error 更新、硬删。
- `tests/AxonHub.Persistence.Tests/SchemaTests.cs` +1、`ChannelStoreTests.cs` +1、
  `UsageLogStoreTests.cs` +1。
- 静态计数：`AxonHub.Api.Tests` 2982 → 3084（+102），`AxonHub.Persistence.Tests` 310 → 319（+9），
  四项目合计 6419 → 6530。**本轮按项目规则未运行 lint / build / test**，下一次全量运行应以该数字复核。

## 差异

- C182 由「子系统未移植」收窄为「19 个 provider checker 与供应商专属重置未移植」；核心、状态表、
  调度与 GraphQL 面已落地并由 111 例锁定。条内另记两项时序差异：启动 hosted service 晚于参照构造函数
  加载缓存；同步设置快照对外部改库最多延迟到下一次读取。
- C123 仅剩 antigravity checker（注册表为空期间 antigravity 通道不发起配额请求）。
- C139 转为 resolved：`Channel.providerQuotaStatus` 返回真实行 / null 的门禁与参照 resolver 一致。
- 新增 C204：`QuotaLimitStatus.Type` 的 C# 初始化器为 `token`、Go 零值为空串；解码与 `NewTokenLimitStatus`
  已补偿。
- 新增 C205：`Map` 标量的 `quotaData` 沿用 jsonb 文本的键序 / 空白 / 不转义 HTML，Go 经 `json.Marshal`
  输出字典序与 `<` 转义；JSON 语义一致。

## 残余风险

- 19 个 checker 未移植：开启采集时不会向任何供应商发出请求，管理面显示空或陈旧的配额状态。
- 启动到初始化 hosted service 完成之间，路由闸门看不到已存配额行（参照在开始服务前同步加载）。
- 同步设置快照可能在外部直接改库后短暂滞后（采集循环与 GraphQL 读取始终实时读库）。
- 本轮未运行构建与测试，编译 / 分析器正确性仅由人工检查保证；SQL 由真实 PostgreSQL 用例锁定。
