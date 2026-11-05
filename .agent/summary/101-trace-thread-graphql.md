# §101 追踪与线程管理面（traces / threads / 八个状态变更 / 段与用量解析）

日期：2026-11-05
参照：`internal/server/gql/ent.graphql:4477-4520 / 5747-5810 / 5940-6030`、
`axonhub.graphql:822-843`、`ent.resolvers.go:541-570 / 856-896`、
`axonhub.resolvers.go:786-810 / 999-1085`、`gql_edge.go:652-688`、
`gql_pagination.go`、`internal/server/biz/trace.go:117-256 / 448-1919`、
`thread.go:120-222`、`usage_metadata.go`、`request.go:1872-1897`、
`internal/ent/schema/trace.go:80-107`、`thread.go:61-79`

## 交付

- `src/AxonHub.Api/GraphQL/TraceTypes.cs`：Trace / Thread / Request / Segment / Span /
  UsageMetadata 等 17 个类型、4 个枚举、TraceWhereInput / ThreadWhereInput /
  RequestWhereInput 与三个 Order 输入；`TraceMapping` 负责 filter / sort / cursor /
  连接构建与 GID / Int 映射。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：`traces` / `threads` 根查询与
  `node(id)` 的 Trace / Thread 分支（缺失抛 `ent: trace|thread not found`）。
- `src/AxonHub.Api/GraphQL/AdminMutation.cs`：`archiveTrace` / `unarchiveTrace` /
  `retainTrace` / `unretainTrace` 与四个 thread 变更。
- `src/AxonHub.Api/Management/TracePrivacy.cs`：Trace / Thread 查询与变更策略、
  Request 查询可见性谓词的显式求值。
- `src/AxonHub.Api/Management/TraceManagementService.cs`：列表 / node / 首段 /
  rawRootSegment / 用量 / 状态变更（含 `cannot ...` 与 `failed to ...` 文案）。
- `src/AxonHub.Api/Management/ThreadManagementService.cs`：列表 / node /
  firstUserQuery / 用量 / archivedTracesCount / 级联状态变更。
- `src/AxonHub.Api/Traces/TraceSegmentService.cs` + `TraceSegmentJson.cs`：
  `GetRootSegment` / `GetTraceFirstSegment`、10 路并发正文加载的顺序复现、
  上一 trace 去重、tool_call 父子挂接、Go JSON 序列化。
- `src/AxonHub.Persistence/Stores/TraceAdminStore.cs`、
  `ThreadAdminStore.cs`、`UsageMetadataStore.cs`，`RequestSegmentStore` 扩展
  completed 读取、时间谓词与可见性谓词。
- `Program.cs`：注册 `TracePrivacy`、`TraceSegmentService`、
  `TraceManagementService`、`ThreadManagementService`。

## 行为要点

- 根查询把 `CREATED_AT` 重写为 ent 默认的 id 排序并保留方向；Thread.traces 与
  Trace.requests 边保留 created_at 排序与 time 游标；Trace.requests 带隐式
  `TraceIDEQ`。
- GID：`Trace.threadID` 空列为 `Thread/0`，`Request.apiKeyID` / `traceID` 空列为
  `APIKey/0` / `Trace/0`，`Request.dataStorageID` 的 nil / 0 均为 null（C190）。
- `Trace.project` / `Thread.project` 缺失抛 `ent: project not found`，
  `Trace.thread` 缺失按 MaskNotFound 返回 null。
- 段构建覆盖 OpenAI Chat / Responses / Compact / Decisions、Anthropic、Gemini、
  图片、moderation、alpha search；变换失败跳过受影响 span（C191）。
- 状态变更谓词为 id + from 状态 + 隐私项目谓词；thread 变更在同一事务内级联
  同状态 trace（同样受 Trace 变更策略的 project 谓词约束），失败回滚并以
  `failed to cascade <op> traces: ` 报错。
- 隐私：Trace / Thread 共用查询（read_requests / owner / default deny）与变更
  （write_requests）策略；Request 查询策略额外给出逐行可见性谓词；service
  account 拒绝经呈现层映射为 `permission denied`（C189）。

## 顺带修复

- `HttpContextManagementIdentity` 不再让管理 JWT 主体回落到 /v1 的
  `RequestIdentity.Default`（项目 1），修复 owner 查询被意外限定到项目 1。
- `RequestSegmentStore.GetFirstCompletedByTraceAsync` 补 `LIMIT 1`，消除
  `QuerySingleOrDefault` 多行异常。
- `ThreadAdminStore` 级联 SQL 补上 Trace 变更策略的 project 谓词。
- 提示词端点夹具改为发送 `X-Project-ID`（与前端一致，满足参照的上下文项目要求）。

## 测试

- `tests/AxonHub.Api.Tests/AdminTracesEndpointTests.cs`：23 例（查询形状、排序回退、
  where 子集与时间谓词、段 / 用量 / rawRootSegment、边错误、零值 GID、threads 查询、
  node、八个变更与文案、级联错误、项目作用域、service account 拒绝）。
- `tests/AxonHub.Persistence.Tests/TraceAdminStoreTests.cs` 4 例、
  `ThreadAdminStoreTests.cs` 5 例、`UsageMetadataStoreTests.cs` 3 例、
  `RequestSegmentStoreTests.cs` 3 例，共 15 例。
- 整解决方案复跑：Llm 3032、Grains 95、Persistence 239、Api 2627，合计 5993，
  0 失败 0 跳过；改动后的 trace 端点 23/23 单独复跑通过。

## 差异

- C189（accepted）：显式隐私求值替代 ent interceptor；where 子集（缺
  hasProjectWith / hasThreadWith / hasRequestsWith 等边谓词）；空列表按 len>0
  丢弃；嵌套空 where 等价于不过滤。
- C190（accepted）：Go int → GraphQL Int 的 checked 截断；`Thread/0`、`APIKey/0`、
  `Trace/0` 与 `dataStorageID` 的零值例外。
- C191（accepted）：无外部对象存储（正文列权威、`_ext` 标记）；正文顺序加载替代
  10 路 errgroup；变换失败无日志接缝。
- C165 更新：node() 已解析 Trace / Thread，Project / User 反向边仍为缺口。
