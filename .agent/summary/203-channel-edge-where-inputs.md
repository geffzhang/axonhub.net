# §203 ChannelProbe / ChannelModelPrice / ProviderQuotaStatus 的 where 输入与 Channel 的 3 组边谓词（C135 第五刀，5 例）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/gql/ent.graphql` 的
ChannelProbeWhereInput / ChannelModelPriceWhereInput / ChannelModelPriceVersionWhereInput /
ProviderQuotaStatusWhereInput，以及 ent 生成的
`internal/ent/{channelprobe,channelmodelprice,channelmodelpriceversion,providerquotastatus,channel}/*.go`
中的 `HasNeighbors` / `HasNeighborsWith` SQL。

## 背景

C135 的 §199–§202 已补齐 Channel / User / Role / Project / APIKey / UserProject 的全部标量
谓词，并完成 user_roles、projects、api_keys 等 12 组边谓词中的 7 组，残余 8 组。本刀先补齐
Channel 侧三张被连接实体的 where 输入（ChannelProbe / ChannelModelPrice /
ChannelModelPriceVersion / ProviderQuotaStatus），再接入因此解锁的 3 组消费侧边谓词：
Channel.hasChannelProbes / hasChannelProbesWith、hasChannelModelPrices /
hasChannelModelPricesWith、hasProviderQuotaStatus / hasProviderQuotaStatusWith。残余收窄为
5 组（Channel 3：hasRequests / hasExecutions / hasUsageLogs；User 2：
hasChannelOverrideTemplates / hasOidcIdentities）。

## 交付

- `src/AxonHub.Persistence/Stores/ConnectionSql.cs`
  - 新增 `AppendComparison(..., double? value, ...)` 重载与 `AppendDoubleIn(...)`；
    空列表语义与 `AppendIdIn` 一致（空 IN 恒假、空 NOT IN 恒真）。
- `src/AxonHub.Persistence/Stores/ChannelProbeStore.cs`
  - 新增 `ChannelProbeListFilter`（61 个谓词）：id 8、channelID 4、totalRequestCount 8、
    successRequestCount 8、avgTokensPerSecond 10、avgTimeToFirstTokenMs 10、timestamp 8、
    hasChannel / hasChannelWith、not / and / or。
  - 新增 `ChannelProbeFilterSql.Build(filter, alias, args)`；探测表无 mixin，不涉及
    deleted_at / created_at / updated_at；嵌套 channel 过滤器别名 `chpc`，EXISTS 表别名 `chp`。
- `src/AxonHub.Persistence/Stores/ChannelModelPriceStore.cs`
  - 新增 `ChannelModelPriceListFilter`（61 个谓词）：id 8、createdAt 8、updatedAt 8、
    channelID 4、modelID 13、referenceID 13、hasChannel / hasChannelWith（别名 `mpch` /
    嵌套 `mpchf`）、hasVersions / hasVersionsWith（别名 `mpv`）、not / and / or。
  - 新增 `ChannelModelPriceVersionListFilter`（89 个谓词）：id 8、createdAt / updatedAt 各 8、
    channelID 8、modelID 13、channelModelPriceID 4、status 4、effectiveStartAt 8、
    effectiveEndAt 10、referenceID 13、hasChannelModelPrice / hasChannelModelPriceWith
    （别名 `vmp`）、not / and / or。
  - 新增 `ChannelModelPriceFilterSql.Build` 与 `ChannelModelPriceVersionFilterSql.Build`；
    价格 ↔ 版本 ↔ 通道三层互指递归生成 EXISTS。
- `src/AxonHub.Persistence/Stores/ProviderQuotaStatusStore.cs`
  - 新增 `ProviderQuotaStatusListFilter`（76 个谓词）：id 8、createdAt / updatedAt 各 8、
    channelID 4、providerType 4、status 4、nextResetAt 10、ready 2、nextCheckAt 8、
    accountKey 15、hasChannel / hasChannelWith（别名 `qch` / 嵌套 `qchf`）、not / and / or。
  - 新增 `ProviderQuotaStatusFilterSql.Build`。
- `src/AxonHub.Persistence/Stores/ChannelAdminStore.cs`
  - `ChannelListFilter` 增加 HasChannelProbes / HasChannelProbesWith、
    HasChannelModelPrices / HasChannelModelPricesWith、HasProviderQuotaStatus /
    HasProviderQuotaStatusWith。
  - `BuildFilter` 改为 `internal static`，新增三段 EXISTS：`channel_probes`、`channel_model_prices`、
    `provider_quota_status` 的 channel_id → channels.id。
- `src/AxonHub.Api/GraphQL/ChannelProbeTypes.cs`
  - 新增 `ChannelProbeWhereInput`（61 个字段）与 `ChannelProbeMapping.ToFilter`；
    `channelID*` 显式 `[GraphQLName]` + `IdType`，计数 / timestamp 为 Int（int → long）。
- `src/AxonHub.Api/GraphQL/PriceTypes.cs`
  - 新增 `ChannelModelPriceWhereInput`（61 个字段）、`ChannelModelPriceVersionWhereInput`
    （89 个字段）、`PriceMapping.ToFilter` 两个重载与 `ToWireVersionStatus`（active / archived）。
- `src/AxonHub.Api/GraphQL/ChannelTypes.cs`
  - 新增 `ProviderQuotaStatusWhereInput`（76 个字段），providerType / status 使用
    `ProviderQuotaStatusProviderTypeValue` / `ProviderQuotaStatusStatusValue`。
  - `ChannelWhereInput` 追加 6 个边字段（现 157/163 个字段）；注释明确 hasRequests /
    hasExecutions / hasUsageLogs 仍为 C135 gap。
- `src/AxonHub.Api/GraphQL/ChannelMapping.cs`
  - 新增 `ProviderQuotaTypeWires` / `ProviderQuotaStatusWires` 反向字典与
    `ToFilter(ProviderQuotaStatusWhereInput?)`；`ToFilter(ChannelWhereInput?)` 末尾接入
    三组边映射。

## 语义（与 sqlgraph / 端口既有 C189 策略对齐）

- `hasX: true` → `EXISTS (...)`；`hasX: false` → `NOT (EXISTS (...))`。
- `hasXWith` 列表为空时不生成谓词；每个过滤器的谓词 AND 进同一个 EXISTS；空过滤器退化为
  「存在任意链接」。
- 与 `sqlgraph.HasNeighborsWith` 一致，不重新应用被连接实体的软删除谓词：已软删除的
  ChannelModelPrice / ProviderQuotaStatus 行仍满足 `hasChannelModelPrices` /
  `hasChannelModelPricesWith` / `hasProviderQuotaStatus`（测试锁定）。
- 过滤器本身不加 `deleted_at = 0`；ChannelProbe 表没有 mixin，因此该输入没有
  createdAt / updatedAt / IsNil / NotNil 时间谓词，与参照一致。
- ChannelModelPriceVersion.channelID 是普通 Int（8 个比较操作符），但
  ChannelModelPriceVersion.channelModelPriceID 与 ChannelModelPrice.channelID 是 ent ID
  （只有 4 个操作符），GraphQL 侧用 IdType / 列表 IdType 保持 wire 形状。

## 测试

- `tests/AxonHub.Persistence.Tests/ChannelAdminStoreTests.cs`（Postgres，新增 1 例）：
  `ListAppliesEdgePredicatesWithoutReapplyingTheTargetSoftDelete`——三组边的 true / false、
  hasXWith 标量（TotalRequestCountGTE、AvgTokensPerSecondIsNil、ModelIdContains、
  EffectiveEndAtIsNil）、嵌套 hasChannelWith / hasVersionsWith、软删除价格行仍匹配与
  Not / Or 组合；新增 SeedProbeAsync / SeedPriceAsync / SeedVersionAsync / SeedQuotaAsync。
  该类 8 通过。
- `tests/AxonHub.Api.Tests/AdminChannelsEndpointTests.cs`：
  `QueryChannelsMapsEveryScalarWherePredicate` 增加三组边输入并断言映射；
  `ChannelWhereInputExposesEveryScalarPredicate` 追加 6 个边字段（157 个字段）；
  新增 `EdgeWhereInputsExposeEveryPredicate`（Theory，4 例：ChannelProbe 61、
  ChannelModelPrice 61、ChannelModelPriceVersion 89、ProviderQuotaStatus 76 个字段全量锁）。
  该类 45 通过。

## 台账

C135 保持 gap：`what` / `decision` / `test` 改为 §203 后的状态，残余明确为 5 组边谓词
（Channel 3：hasRequests / hasExecutions / hasUsageLogs；User 2：hasChannelOverrideTemplates /
hasOidcIdentities）。`01-baseline.md` / `03-acceptance.md` 已插入 §203 段落；lint 通过，
`02-differences.md` 已重新生成；282 条（206 accepted / 2 gap / 74 resolved）。
