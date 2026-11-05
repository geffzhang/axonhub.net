# §102 请求管理面（requests / node(Request) / 请求详情 / executions 与 usageLogs）

日期：2026-11-05
参照：`internal/server/gql/ent.graphql:4477-4606 / 4641-4711 / 6643-6718`、
`cost.graphql:1-12`、`ent.resolvers.go:496-511 / 600-935`、
`gql_edge.go:471-560 / 553-575 / 693-715`、`gql_pagination.go`（DefaultRequestOrder）、
`internal/server/biz/request.go:124-165 / 1532-1876`

## 交付

- `src/AxonHub.Api/GraphQL/RequestTypes.cs`：RequestExecution / UsageLog 类型与
  连接、四个 Order 输入、三个 Where 输入（Execution / UsageLog 全量、Request
  扩展子集）、TierCost / CostItem、RequestType 的正文与边扩展；`RequestMapping`
  负责 filter / sort / cursor / 连接构建、GID / Int / 枚举 / 时间映射与正文、
  chunk、成本解析。
- `src/AxonHub.Api/Management/RequestQueryService.cs`：`node(Request)`、
  Request.executions / usageLogs 边的请求作用域查询、apiKey / project / trace /
  channel 四个隐私边（`failed to load channel: ` 包装、`ent: project not found`）。
- `src/AxonHub.Api/GraphQL/TraceTypes.cs`：`TraceMapping.ToFilter(RequestWhereInput)`
  扩展 request 侧全部谓词；新增 `ToRootRequestSortField`（CREATED_AT→id 保留
  方向）与 `RequestSourceValue` 的线格式重载。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：`requests` 根查询与 `node(id)` 的
  Request 分支。
- `Program.cs`：注册 `RequestQueryService`（另含三个存储与
  `RequestManagementService`）。

## 行为要点

- 根查询：first / last 经 validatePaginationArgs / validateFirstLast；仅
  `CREATED_AT` 重写为 id 并保留方向，`UPDATED_AT` 按 updated_at；项目作用域与
  逐行可见性来自 Request 查询策略。
- 边：`node(Request)` 缺失抛 `ent: request not found`；apiKey / trace 缺失或拒绝
  → null；project 非空字段拒绝或缺失抛 `ent: project not found`；channel 缺失 /
  软删 / 拒绝 → null，其余错误 `failed to load channel: `；channelAPIKeyIndex /
  Suffix 仅在通道可见且列非空时返回。
- 正文与 chunk：requestBody 空 → `{}`；responseBody 仅 completed，空或压缩后等于
  `{"_ext":1}` → `{}`；responseChunks 仅 stream 且 completed / failed / canceled，
  单元素 `_ext` 标记折叠为 `[]`；执行使用同一组规则。
- 嵌套连接：游标为 id + created_at / updated_at，默认按 id；where 映射到既有持久化
  过滤器（空 In 列表丢弃）；执行 projectID 在 SDL 为 Int、内部 long 读写并 checked
  截断；用量成本由 `BillingJson` 解码 CostItem / TierCost。

## 顺带修复

- `TracePrivacy` 的方法名拼写统一（`EvaluateRequestQueryAsync` → 服务内的
  `requestPrivacy.EvaluateQueryAsync`），消除 CS0103。
- `FakeRequestExecutionStore` / `InMemoryUsageLogStore` / `RecorderUsageLogStore`
  补齐 `ListAsync`，`FakeRequestSegmentStore` 补齐 `GetByIdAsync`（新接口成员的
  测试替身，编译期一次暴露）。
- `RequestWhereInput` 的 apiKeyID 家族改走 `ToLongList`（int → long 筛选值）。

## 测试

- `tests/AxonHub.Api.Tests/AdminRequestsEndpointTests.cs`：5 例（根查询形状、节点
  详情、chunk 规则、executions / usageLogs 连接、排序回退与 where 谓词、成本明细、
  node 错误文案）＋ `RequestQueryServiceVisibilityTests` 3 例（隐私拒绝 → null、
  加载失败包裹、空 / 零通道 id 短路）。
- 整解决方案复跑：Llm 3032、Grains 95、Persistence 239、Api 2635，合计 6001，
  0 失败 0 跳过；新增 8/8 单独复跑通过。

## 差异

- C192（accepted）：三个 where 输入为 entgql 子集（Request 缺 clientIP / userAgent /
  stream；Execution 缺 projectID 的比较运算与 format / externalID 的额外运算；
  UsageLog 缺 apiKeyID 比较运算与 modelID 额外运算）；缺 `Request.dataStorage` /
  `RequestExecution.dataStorage` 边与 DataStorage 类型；缺 stream + processing 的
  实时 chunk 预览（只读落库 chunk）。
- C165 更新：node() 已解析 Request，其余 ent 类型仍为缺口。
