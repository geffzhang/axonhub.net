# §202 Project / APIKey / UserProject 的 where 输入与 User / Role 的 projects / apiKeys 边谓词（C135 第四刀，4 例）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/gql/ent.graphql` 的
ProjectWhereInput / APIKeyWhereInput / UserProjectWhereInput，以及 ent 生成的
`internal/ent/{project,apikey,user,role}/*.go` 中的 `HasNeighbors` / `HasNeighborsWith` SQL。

## 背景

C135 记录 Channel / User / Role 三个 where 输入只建模管理面前端实际使用的字段。§199 / §200
已补齐全部 292 个标量谓词，§201 完成 UserRoleWhereInput 与 user_roles 双向往返，残余 12 组
边谓词。继续边谓词前需要先补齐被连接实体的 where 输入；本刀处理 Project / APIKey /
UserProject 三张输入，并顺带接入因此解锁的 4 组消费侧边谓词：Role.hasProject /
hasProjectWith、User.hasProjects / hasProjectsWith、User.hasAPIKeys / hasAPIKeysWith、
User.hasProjectUsers / hasProjectUsersWith。残余收窄为 8 组（Channel 6、User 2）。

## 交付

- `src/AxonHub.Persistence/Stores/ProjectStore.cs`
  - `ProjectListFilter` 扩到 54 个标量谓词：id 8、createdAt 8、updatedAt 8、name 13、
    description 13、status 4，另加 not / and / or。
  - `BuildFilter` 改为 `internal static`，供跨实体 EXISTS 复用；所有谓词走
    `ConnectionSql.AppendComparison` / `AppendIdIn` / `AppendTimeIn` /
    `AppendStringPredicates` / `AppendStringIn`。
- `src/AxonHub.Persistence/Stores/ApiKeyStore.cs`
  - `ApiKeyListFilter` 扩到 68 个标量谓词：id 8、createdAt 8、updatedAt 8、userID 6、
    projectID 4、key 13、name 13、type 4、status 4，另加 not / and / or。
  - `BuildFilter` 改为 `internal static`；`TypeNotIn` / `StatusNotIn` 改走
    `AppendStringIn(..., negate: true)`，空列表语义与其余输入一致。
- `src/AxonHub.Persistence/Stores/UserProjectFilter.cs`（新建）
  - `UserProjectListFilter`：id 8、createdAt 8、updatedAt 8、isOwner / isOwnerNEQ，另加
    not / and / or；与参照一致不暴露 user_id / project_id / scopes。
  - `UserProjectFilterSql.Build(filter, alias, args)`：复用 `ConnectionSql`；空 `and` 忽略、
    空 `or` 恒假。
- `src/AxonHub.Persistence/Stores/UserAdminStore.cs`
  - `UserListFilter` 增加 HasProjects / HasProjectsWith、HasAPIKeys / HasAPIKeysWith、
    HasProjectUsers / HasProjectUsersWith。
  - `BuildFilter` 新增三段 EXISTS：projects 经 `user_projects` JOIN `projects`、
    apiKeys 经 `api_keys`、projectUsers 直接查 `user_projects`；With 分别复用
    `ProjectStore.BuildFilter`（别名 `p2`）、`ApiKeyStore.BuildFilter`（别名 `ak`）、
    `UserProjectFilterSql.Build`（别名 `up`）。
- `src/AxonHub.Persistence/Stores/RoleStore.cs`
  - `RoleListFilter` 增加 HasProject / HasProjectWith；`BuildFilter` 新增
    `projects pj WHERE pj.id = alias.project_id` 的 EXISTS，With 复用
    `ProjectStore.BuildFilter`（别名 `pj`）。
- `src/AxonHub.Api/GraphQL/ProjectTypes.cs`
  - `ProjectWhereInput` 扩到 57 个字段（54 标量 + not / and / or）；`ProjectMapping.ToFilter`
    全量映射（GID → long、Time 直传、status 枚举 → wire 值）。
- `src/AxonHub.Api/GraphQL/ApiKeyTypes.cs`
  - `ApiKeyWhereInput` 扩到 71 个字段（68 标量 + not / and / or）；`userID*` / `projectID*`
    全部显式 `[GraphQLName]` 保持参照大小写；`ApiKeyMapping.ToFilter` 全量映射
    （`UserIDIsNil` / `UserIDNotNil` → 普通 bool）。
- `src/AxonHub.Api/GraphQL/ManagementTypes.cs`
  - `UserWhereInput` 增加 6 个边字段（共 110 个字段），`RoleWhereInput` 增加 2 个边字段
    （共 56 个字段）。
  - 新增 `UserProjectWhereInput`（29 个字段）：id 8、createdAt 8、updatedAt 8、
    isOwner / isOwnerNEQ、not / and / or。
  - `ToFilter` 完成边映射（Project / APIKey / UserProject），并新增
    `ToFilter(UserProjectWhereInput?)` 全量转换。

## 语义（与 sqlgraph / 端口既有 C189 策略对齐）

- `hasX: true` → `EXISTS (...)`；`hasX: false` → `NOT (EXISTS (...))`。
- `hasXWith` 列表为空时不生成谓词；每个过滤器的谓词 AND 进同一个 EXISTS；空过滤器退化为
  「存在任意链接」。
- 与 `sqlgraph.HasNeighborsWith` 一致，不重新应用被连接实体的软删除谓词：软删除的 Project
  仍满足 `User.hasProjectsWith` / `Role.hasProjectWith`，软删除的 APIKey 仍满足
  `User.hasAPIKeysWith`（测试锁定）。
- `User.hasProjects` 与 `User.hasProjectUsers` 指向同一张 `user_projects` 表，EXISTS 形状
  相同；`Role.hasProject` 通过 `roles.project_id` 连接，project_id 为 NULL 或悬空时
  `hasProject: false`。

## 测试

- `tests/AxonHub.Persistence.Tests/ProjectStoreTests.cs`（Postgres，新增 1 例）：
  `ListAppliesEveryScalarPredicate`——id / createdAt / updatedAt / name / description / status
  的全部操作符、空列表 IN / NOT IN、NULL 语义与 Not / And / Or 嵌套。
- `tests/AxonHub.Persistence.Tests/ApiKeyStoreTests.cs`（Postgres，新增 1 例）：
  `ListAppliesEveryScalarPredicate`——含 userID 的 IsNil / NotNil（false 忽略）与
  `user_id <> 1` / `NOT IN` 排除 NULL 行。
- `tests/AxonHub.Persistence.Tests/UserAdminStoreTests.cs`（Postgres，新增 1 例）：
  `ListAppliesProjectAndApiKeyEdgePredicates`——HasProjects / HasAPIKeys / HasProjectUsers
  的 true / false、空 With 列表、空过滤器、精确匹配、软删除 Project / APIKey 仍匹配、
  多过滤器 AND 与 Not 嵌套。
- `tests/AxonHub.Persistence.Tests/RoleStoreTests.cs`（Postgres，新增 1 例）：
  `ListAppliesProjectEdgePredicates`——hasProject 的 true / false（含 NULL 与悬空
  project_id）、空 With 列表、空过滤器、按名 / 状态匹配、软删除 Project 与 Not。
  四个类合计 60 通过。
- `tests/AxonHub.Api.Tests/AdminProjectsEndpointTests.cs`：新增
  `ProjectWhereInputExposesEveryScalarPredicate`（57 个字段）与
  `QueryProjectsMapsEveryScalarPredicate`（逐族映射）。
- `tests/AxonHub.Api.Tests/AdminApiKeysEndpointTests.cs`：新增
  `ApiKeyWhereInputExposesEveryScalarPredicate`（71 个字段）与
  `QueryApiKeysMapsEveryScalarPredicate`（逐族映射）；`FakeApiKeyStore` 增加
  `Queries` 捕获。
- `tests/AxonHub.Api.Tests/AdminUsersRolesEndpointTests.cs`：`UserWhereInput` introspection
  追加 6 个边字段（110 个字段），`RoleWhereInput` 追加 2 个（56 个字段）；新增
  `UserProjectWhereInputExposesTheReferencePredicates`（29 个字段）、
  `QueryUsersMapsTheProjectAndApiKeyEdgePredicates`、`QueryRolesMapsTheProjectEdgePredicate`。
  三个类合计 61 通过。

## 台账

C135 保持 gap：`what` / `decision` / `test` 改为 §202 后的状态，残余明确为 8 组边谓词
（Channel 6：hasRequests / hasExecutions / hasUsageLogs / hasChannelProbes /
hasChannelModelPrices / hasProviderQuotaStatus；User 2：hasChannelOverrideTemplates /
hasOidcIdentities）。`01-baseline.md` / `03-acceptance.md` 已插入 §202 段落；lint 通过，
`02-differences.md` 已重新生成；282 条（206 accepted / 2 gap / 74 resolved）。
