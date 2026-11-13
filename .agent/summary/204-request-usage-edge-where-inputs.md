# §204 Request / RequestExecution / UsageLog 三张 where 输入与 Channel 最后 3 组边谓词（C135 第六刀，9 例 + 空 In 语义修复）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/ent/gql_where_input.go` 的
RequestWhereInput / RequestExecutionWhereInput / UsageLogWhereInput，`internal/server/gql/ent.graphql`
的字段清单，以及 ent 生成的 `internal/ent/{request,requestexecution,usagelog,channel}/*.go`
中的 `HasNeighbors` / `HasNeighborsWith` SQL。

## 背景

C135 的 §199–§203 已补齐 Channel / User / Role / Project / APIKey / UserProject /
ChannelProbe / ChannelModelPrice / ChannelModelPriceVersion / ProviderQuotaStatus 的输入面，
残余 5 组边谓词：Channel 3 组（hasRequests / hasExecutions / hasUsageLogs）与 User 2 组
（hasChannelOverrideTemplates / hasOidcIdentities）。本刀先建模三张被连接实体的完整 where
输入（Request 228 字段、RequestExecution 206 字段、UsageLog 226 字段），再把 Channel 的
3 组边谓词接入；残余收窄为 User 的最后 2 组。

## 交付

- `src/AxonHub.Api/GraphQL/RequestTypes.cs`
  - `RequestWhereInput` 扩为 228 个字段：id 8、createdAt / updatedAt 各 8、source 4、
    modelID 15、format 15、status 4、stream 2、clientIP 15、userAgent 15、requestBody /
    responseBody / responseChunks 等可空字符串 15、channelID / dataStorageID ID 4 与
    IsNil / NotNil、metrics 数值 10、7 组边谓词 ×2、not / and / or。
  - `RequestExecutionWhereInput` 扩为 206 个字段：id 8、createdAt / updatedAt 各 8、
    projectID 4、channelID 4、dataStorageID ID 4、requestID 4、externalID 15、modelID 15、
    upstreamModelID 15、format 15、reasoningEffort 15、errorMessage 15、responseStatusCode 10、
    status 4、stream 2、metrics×3 各 10、requestURL 15、passThroughApplied 2、3 组边谓词 ×2、
    not / and / or。
  - `UsageLogWhereInput` 扩为 226 个字段：id 8、createdAt / updatedAt 各 8、apiKeyID 8、
    channelID 4、projectID 4、requestID 4、modelID 15、source 4、format 15、totalCost 10、
    costPriceReferenceID 15、prompt / completion / total token 族（3 个必需 int8 + 9 个可空
    int10）、3 组边谓词 ×2、not / and / or。
  - wire 映射沿既有约定：id → string + IdType，int → int?，time → DateTime? + GoTimeType，
    枚举沿用 RequestStatusValue / RequestSourceValue / RequestExecutionStatusValue /
    UsageLogSourceValue；apiKeyID / modelID / channelID / requestID / upstreamModelID /
    costPriceReferenceID / clientIP / userAgent / requestURL / contentStorageID 等显式
    `[GraphQLName]` 保持参照大小写，未标注字段按 HotChocolate camelCase（source → source）。
- `src/AxonHub.Persistence/Stores/RequestSegmentStore.cs` / `RequestExecutionStore.cs` /
  `UsageLogStore.cs`
  - 扩展 `RequestListFilter` / `RequestExecutionListFilter` / `UsageLogListFilter` 与
    `BuildFilter`，接入全部标量族与边谓词；边别名 rqak / rqpr / rqtr / rqds / rqex / rqch /
    rqul、exrq / exch / exds、ulrq / ulpr / ulch。
- `src/AxonHub.Persistence/Stores/ChannelAdminStore.cs`
  - `ChannelListFilter` 增加 HasRequests / HasRequestsWith、HasExecutions / HasExecutionsWith、
    HasUsageLogs / HasUsageLogsWith；`BuildFilter` 新增三段 EXISTS：
    `requests.channel_id`、`request_executions.channel_id`、`usage_logs.channel_id` → channels.id。
- `src/AxonHub.Api/GraphQL/ChannelTypes.cs` / `ChannelMapping.cs`
  - `ChannelWhereInput` 追加 6 个边字段（现 163/163 个字段）并接入 ListFilter 映射。
- `src/AxonHub.Persistence/Stores/ConnectionSql.cs`
  - 空 In / NotIn 语义修复（见下）。

## 语义

- `hasX: true` → `EXISTS (...)`；`hasX: false` → `NOT (EXISTS (...))`。
- `hasXWith` 列表为空时不生成谓词；每个过滤器的谓词 AND 进同一个 EXISTS；空过滤器退化为
  「存在任意链接」。
- 与 `sqlgraph.HasNeighborsWith` 一致，不重新应用被连接实体的软删除谓词：已软删除的
  request / channel / data storage 行仍满足对应的 hasX / hasXWith。
- Request.executions / usageLogs 的连接查询始终带 `request_id = <parent>` 作用域（与参照的
  Request.executions / usageLogs 边一致），测试中通过 `RequestScope` 显式传父请求 id。
- 枚举谓词与参照一致：只有相等 / NEQ / In / NotIn，没有 GT / GTE / LT / LTE（C192 早期描述
  里的枚举比较算子已在本刀更正）。

## 空 In / NotIn 语义（C283，resolved）

entgql 生成的 WhereInput 只在 `len(list) > 0` 时安装 In / NotIn 谓词；端口此前把空 In
渲染为 `FALSE`、空 NotIn 渲染为 `TRUE`。本刀把 `ConnectionSql.AppendIdIn` /
`AppendStringIn` / `AppendTimeIn` / `AppendDoubleIn` 的空列表分支改为直接返回（忽略谓词），
并同步更新 7 个旧 store 测试类的断言。直接 ent 查询（`channel.IDIn(ids...)`、
`APIKeyIDIn`、`role.IDIn` 等）仍是恒假语义，相关方法在空列表时提前返回空集
（ChannelOverrideTemplateStore.ListChannelsAsync、DashboardStore 的两个 token usage 查询；
RoleStore / Dashboard.GetChannelsByIdsAsync / ModelStore.BulkUpdateStatusAsync 原本已有
提前返回）。空 Or / And 仍按 C177 渲染为恒假，不在本刀范围。

## 测试

- `tests/AxonHub.Persistence.Tests/RequestSegmentStoreTests.cs`（新增 2 例）：
  `ListAppliesTheExtendedScalarPredicates`（stream / clientIP / userAgent / metrics×3 /
  contentSaved / contentStorageID / contentStorageKey / contentSavedAt 全族 + 空 In 忽略）、
  `ListAppliesTheEdgePredicates`（7 组边：HasApiKey / HasProject / HasTrace / HasDataStorage /
  HasExecutions / HasChannel / HasUsageLogs，含 With 标量、嵌套边、软删除目标仍匹配、
  空 With 忽略、Not / Or）。
- `tests/AxonHub.Persistence.Tests/RequestExecutionStoreTests.cs`（新增 2 例）：
  `ListAppliesEveryScalarPredicate`（RequestId / ProjectId / ModelId / Format / Status /
  ExternalId / UpstreamModelId / ReasoningEffort / ErrorMessage / ResponseStatusCode / Stream /
  metrics×3 / RequestUrl / PassThroughApplied + 空 In）、`ListAppliesTheEdgePredicates`
  （HasRequest / HasChannel / HasDataStorage，含嵌套 HasRequestWith.HasChannelWith、软删除
  channel、空 With、Not / Or；按 RequestScope 分别断言 101 / 201 两个父请求）。
- `tests/AxonHub.Persistence.Tests/UsageLogStoreTests.cs`（新增 2 例）：
  `ListAppliesEveryScalarPredicate`（ApiKeyId 10 操作符、ModelId、3 个必需 token、9 个可空
  detail token 的 IsNil / NotNil、Source / Format 枚举、TotalCost 10、CostPriceReferenceId 15
  + 空 In）、`ListAppliesTheEdgePredicates`（HasRequest / HasProject / HasChannel，含嵌套、
  软删除目标、空 With、Not / Or）。
- `tests/AxonHub.Persistence.Tests/ChannelAdminStoreTests.cs`（新增 1 例）：
  `ListAppliesTheRequestExecutionAndUsageLogEdges`（HasRequests / HasExecutions / HasUsageLogs
  三组边，含 With 标量、嵌套 HasExecutions / HasRequestWith、空 With、Not / Or）。
- `tests/AxonHub.Api.Tests/AdminRequestsEndpointTests.cs`（新增 1 例）：
  `WhereInputsExposeEveryReferencePredicate`——introspection 锁定 Request / RequestExecution /
  UsageLog 的 228 / 206 / 226 个字段（排序后逐名比较）。
- `tests/AxonHub.Api.Tests/AdminChannelsEndpointTests.cs`：`ChannelWhereInputExposesEveryScalarPredicate`
  的期望字段增加 6 个（163 字段）。
- 持久化工程全量：400 通过 / 0 失败；两个 introspection 测试通过。

## 台账

C135 更新：ChannelWhereInput 163/163 闭合，残余明确为 User 的 2 组边谓词
（hasChannelOverrideTemplates / hasOidcIdentities，依赖尚未建模的
ChannelOverrideTemplateWhereInput / OIDCIdentityWhereInput）。C192 更新：三张输入的投影子集
缺口关闭，剩余 dataStorage 输出边（request / requestExecution 的 dataStorage 对象字段）。
新增 C283（resolved）：空 In / NotIn 列表改为忽略。`01-baseline.md` / `03-acceptance.md`
已插入 §204 段落；lint 通过，`02-differences.md` 已重新生成；283 条
（206 accepted / 2 gap / 75 resolved）。
