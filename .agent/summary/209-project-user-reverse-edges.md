# §209 Project / User 反向连接边（14 条边解析器 + 4 例 Api）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/gql/ent.graphql` 的 Project
（users / roles / apiKeys / requests / usageLogs / threads / traces / prompts /
apiKeyProfileTemplates 九条连接边 + projectUsers）与 User（roles / projects / apiKeys /
channelOverrideTemplates / oidcIdentities 五条连接边 + projectUsers / userRoles 两个列表）、
`internal/ent/gql_edge.go:245-862` 的生成解析器、`internal/server/gql/gql_pagination.go:60-78`
的 first/last 校验。

## 背景

C165 在 §208 收口 node(id:) 后只剩最后一处缺口：Project / User 的反向连接边。前端不读这些
边，但参照的 entgql 类型把它们全部暴露，任意查询 `Project.users` 之类的字段都会命中；端口缺失
时得到 GraphQL 校验错误而非数据。本刀把 14 条边解析器一次接线。

## 实现

- `ProjectTypeExtensions` 增加九条连接边；`UserTypeExtensions` 增加四条连接边
  （projects / apiKeys / channelOverrideTemplates / oidcIdentities）与 `userRoles` 列表
  （`roles`、`projectUsers` 已有）。
- 外键谓词一律作为 Where 过滤器，经新增的文件级 `ProjectUserEdgeFilters.And`（12 个重载）
  AND 进调用方 where。原因：管理服务会用隐私结果覆盖自己的 `ProjectScope` 字段，外键放进
  Scope 会被覆盖掉。
  - `Project.users` → `UserListFilter.HasProjectUsersWithProjectId`
  - `User.projects` → `ProjectListFilter.HasUsersWith`（EXISTS user_projects JOIN users）
  - 其余按 `ProjectId` 或 `UserId`
  - `User.oidcIdentities` 另 AND `SystemPrivacy.EvaluateOidcIdentityQueryAsync` 的所有者过滤
    （UserOwnedQueryRule）。
- 新增边排序映射 `ToEdgeSortField`（ApiKey / Prompt / ChannelOverrideTemplate /
  ApiKeyProfileTemplate）、`ToUserEdgeSortField`、`ToRoleEdgeSortField`、`ToEdgeThreadSortField`
  （Request / UsageLog / Trace 的边映射此前已有）：CREATED_AT / UPDATED_AT 保持时间列 +
  id 平局游标，未给 orderBy 按 id；根映射保持 CREATED_AT→Id 的参照改写。
  `BuildConnection` 的游标按 sortField 输出 createdAt / updatedAt。
- `User.roles` 的既有边此前误用根排序映射，改为 `ToRoleEdgeSortField`。
- first/last 校验统一走 `ManagementValidation.ValidateFirstLast`（同 gql_pagination.go:60-78）。
- `projectUsers` / `userRoles` 列表保持参照的 `Query...().All(ctx)` 语义：不分页、不排序
  （C164）。

## 测试

新增 `tests/AxonHub.Api.Tests/AdminProjectUserEdgesEndpointTests.cs` 四例（合成 DI 图：17 个
假 store + 11 个管理服务 + 6 个隐私/基础设施服务）：

- `ProjectEdgesReturnOnlyTheProjectsRowsAndMergeTheWhereFilter`：九条 Project 连接边的 FK
  隔离、`where` 与 FK 的 AND（users emailContainsFold / prompts nameContainsFold）、
  CREATED_AT 边序（角色 createdAt 与 id 反序）。
- `UserEdgesReturnOnlyTheUsersRowsAndExposeTheJoinLists`：projects 的 HasUsersWith + where、
  apiKeys / channelOverrideTemplates / oidcIdentities 的 UserId 过滤、userRoles /
  projectUsers 列表。
- `ProjectUsageLogsEdgeKeepsCreatedAtOrdering`：DESC 按 created_at 而非 id。
- `OrderWithoutFieldIsRejectedLikeTheReferenceSchema`：`orderBy { direction: DESC }` 缺
  `field` 被 HotChocolate 拒绝，证明 CLR 初始化器没有变成 SDL 默认值（与 gqlgen 的非空
  `field` 一致；400 状态属 C109 的引擎替换）。

配套修正测试替身：`FakeUserAdminStore` / `FakeRoleStore` / `FakeProjectStore` /
`FakeApiKeyStore` / `FakePromptStore` / `FakeApiKeyProfileTemplateStore` /
`FakeChannelOverrideTemplateStore` 支持 CREATED_AT 排序与 `UserId` 过滤；
`FakeProjectStore.Matches` 支持 `HasUsersWith`；`FakeRoleStore` 增加
`ListUserRolesForUserAsync`；`UsageLogSliceStore.ListAsync` 应用 where。另补注册 §208 引入的
`IDataStorageStore` / `DataStoragePrivacy` 到 `AdminUsageLogsEndpointTests`（此前 DI 缺项）。

定向运行：12 个受影响测试类 189 通过（3m01s）；未跑 Api 全量回归。

## 台账

C165 由 accepted 转 resolved：`what` 记录 14 条边解析器与边语义，`decision` 记录 FK 走 Where、
OIDC 所有者过滤、边排序映射与 `User.roles` 修正。`01-baseline.md` / `03-acceptance.md` 已插入
§209 段落；lint 通过，`02-differences.md` 已重新生成；283 条
（203 accepted / 0 gap / 80 resolved）。
