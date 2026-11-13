# §210 残余实体反向边（APIKey / Channel / DataStorage / Role 十条边 + 5 例 Api + 3 例 Persistence）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/gql/ent.graphql` 的
APIKey.requests；Channel.requests / executions / usageLogs / channelProbes；
DataStorage.requests / executions；Role.users / project / userRoles；
`internal/ent/gql_edge.go:27-46`（APIKey.requests）、`:56-117`（Channel 三条连接边）、
`:119-129`（channelProbes 列表）、`:195-235`（DataStorage 两条连接边）、`:577-604`
（Role.users / project）与 `internal/server/gql/ent.resolvers.go:832-834`（Role.userRoles 列表）。

## 背景

§208 收口 node(id:)，§209 收口 Project / User 的反向边后，C165 / C196 / C176 仍各自记着
未接线的边：C196 (4) 的 DataStorage.requests / executions、C176 (6) 的 APIKey.requests、
C014 里归给 C165 的 Role.users 等。这十条边前端不读，但参照的 entgql 类型全部暴露；端口
缺失时查询得到 GraphQL 校验错误而非数据。本刀一次接线并同步台账。

## 实现

- 新增四个扩展类型并在 `GraphQLSetup.AddAxonHubGraphQL()` 显式 `AddTypeExtension`：
  `ApiKeyTypeExtensions`、`ChannelTypeExtensions`、`DataStorageTypeExtensions`、
  `RoleTypeExtensions`。
- 连接边按参照 `Paginate(ctx, after, first, before, last, WithXOrder(orderBy),
  WithXFilter(where.Filter))` 语义：不做根解析器的 CREATED_AT→id 改写，未给 orderBy 按
  id（C164），CREATED_AT / UPDATED_AT 保持时间列 + id 平局游标；排序映射复用
  `ToEdgeRequestSortField` / `ToEdgeExecutionSortField` / `ToEdgeUsageLogSortField` /
  `ToUserEdgeSortField`，连接由对应 `BuildConnection` 组装。
- `channelProbes` 与 `userRoles` 在 SDL 是没有参数的列表，参照用 `Query...().All(ctx)`
  （不分页、不排序、无隐私）；端口按父行 id 升序（C164）：
  - `ChannelProbeStore.ListByChannelIdAsync`（`channel_id` 过滤，id 升序）
  - `RoleStore.ListUserRolesForRoleAsync`（`role_id` 过滤，id 升序）
- 外键谓词一律 AND 进调用方 where，不走 Scope：管理服务会用隐私结果覆盖 `ProjectScope`，
  外键放进 Scope 会被覆盖。Request 用 ApiKeyId / ChannelId / DataStorageId，
  RequestExecution 用 ChannelId / DataStorageId，UsageLog 用 ChannelId，
  Role.users 用 `UserListFilter.HasRolesWith = [RoleListFilter{Id}]`（单 EXISTS，同一 role
  满足全部谓词），Role.project 读取记录的 `project_id`。
- `RequestExecutionListQuery.RequestScope` 改为 `long?`：Request.executions 仍传父 id，
  Channel / DataStorage 反向边不传；`IRequestExecutionStore.ListAsync` 在 null 时不渲染
  `request_id` 条件。
- Role.project 是 eager edge + MaskNotFound：project_id 为 null、行缺失或软删时返回 null，
  但 Project 查询策略仍先求值，deny 即使外键为空也传播（`RoleType.ProjectDbId` 以
  `[GraphQLIgnore]` 携带记录值）。
- Channel.executions / DataStorage.executions 直连无 Policy 的 RequestExecution；其余三条
  连接边经 RequestManagementService / UsageLogManagementService / UserManagementService，
  由其隐私结果与 where 合并。

## 测试

新增 `tests/AxonHub.Api.Tests/AdminResidualEdgesEndpointTests.cs` 五例（合成 DI 图：11 个假
store + 6 个管理/隐私服务）：

- `ApiKeyRequestsEdgeScopesToTheKeyAndKeepsCreatedAtOrdering`：key 1 只见 request 1/2，
  CREATED_AT DESC 先 request 2，`where { status: completed }` 与 FK AND 后只剩 request 1。
- `ChannelReverseEdgesScopeToTheChannel`：requests / executions / completedExecutions /
  usageLogs / channelProbes 五条边按 channel 隔离，executions 的最新行在前，探针列表按 id。
- `DataStorageReverseEdgesScopeToTheStorage`：primary / archive 两个存储的 requests 与
  executions 各按 `data_storage_id` 隔离。
- `RoleEdgesExposeUsersProjectAndTheUserRolesList`：users 连接边按 role 过滤并支持 where，
  project 解析出 Alpha，project_id 为 null 与指向缺失行的 role 都返回 null，userRoles 列表
  返回该 role 的两条链接。
- `OrderWithoutFieldIsRejectedOnTheNewEdgesLikeTheReferenceSchema`：连接边 `orderBy
  { direction: DESC }` 缺 `field` 被 HotChocolate 拒绝（列表边没有该参数）。

新增三个 Persistence 用例（Testcontainers Postgres）：

- `RequestExecutionStoreTests.ListWithoutRequestScopeFiltersByChannelAndDataStorage`
- `RoleStoreTests.ListUserRolesForRoleReturnsOnlyThatRolesLinksInIdOrder`
- `ChannelProbeStoreTests.ListByChannelIdReturnsTheChannelsBucketsInIdOrder`

配套修正测试替身：`RequestsSliceExecutionStore.ListAsync` 支持 nullable RequestScope；
`RecordingChannelProbeStore` 增加 `ListByChannelIdAsync`；`FakeRoleStore` 增加
`ListUserRolesForRoleAsync`；`FakeUserAdminStore.Matches` 支持 `HasRolesWith`；
`FakeRequestSegmentStore.Matches` 支持 ApiKeyId / ChannelId / DataStorageId；
`UsageLogSliceStore.Matches` 支持 ChannelId。

定向运行：Api 新类 5 通过、Persistence 三个新例 3 通过、受影响 8 个 Api 测试类 147 通过；
未跑 Api 全量回归。

## 台账

新增 C284（resolved）：记录十条边的参照位置、边语义、FK 走 Where、RequestScope 可空、
两个按父行读取的存储方法与 Role.project 的空外键策略求值；C196 (4)、C176 (6) 与 C014 的
过时表述同步更新。`01-baseline.md` / `03-acceptance.md` 已插入 §210 段落；lint 通过，
`02-differences.md` 已重新生成；284 条（203 accepted / 0 gap / 81 resolved）。
