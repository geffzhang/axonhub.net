# §201 User / Role 的 user_roles 边谓词与 UserRoleWhereInput（C135 第三刀，2 例）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/gql/ent.graphql:7453-7505`
（UserRoleWhereInput）、UserWhereInput / RoleWhereInput 的边字段，以及 ent schema
`internal/ent/schema/user_role.go`。

## 背景

C135 记录 Channel / User / Role 三个 where 输入只建模管理面前端实际使用的字段。§199 / §200
已补齐全部 292 个标量谓词，残余 16 组边谓词。本刀处理 User 与 Role 之间的 `user_roles`
双向往返：User.hasRoles / hasRolesWith / hasUserRoles / hasUserRolesWith 与 Role.hasUsers /
hasUsersWith / hasUserRoles / hasUserRolesWith，并为此新建参照中此前完全缺失的
UserRoleWhereInput。残余收窄为 12 组（Channel 6、User 5、Role 1）。

## 交付

- `src/AxonHub.Persistence/Stores/UserRoleFilter.cs`（新建）
  - `UserRoleListFilter`：与参照 GraphQL 一致，仅 id 8 个谓词、createdAt 8 个 + IsNil / NotNil、
    updatedAt 同、not / and / or；不含 user_id / role_id 与边（entgql 未暴露）。
  - `UserRoleFilterSql.Build(filter, alias, args)`：复用 `ConnectionSql` 的共享 helper；
    空 `and` 忽略、空 `or` 恒假。
- `src/AxonHub.Api/GraphQL/ManagementTypes.cs`
  - 新增 `UserRoleWhereInput`（31 个字段）：id 8、createdAt 10、updatedAt 10、not / and / or；
    ID 用 `IdType`、时间用 `GoTimeType`、列表显式 `ListType<NonNullType<...>>`。
  - `UserWhereInput` 增加 hasRoles / hasRolesWith / hasUserRoles / hasUserRolesWith（共 104 个
    字段），`RoleWhereInput` 增加 hasUsers / hasUsersWith / hasUserRoles / hasUserRolesWith
    （共 54 个字段）。
  - `ToFilter` 完成 8 个边字段的映射与 `ToFilter(UserRoleWhereInput?)` 全量转换（GID 解析、
    时间直传、IsNil 布尔）。
- `src/AxonHub.Persistence/Stores/UserAdminStore.cs`：`UserListFilter` 增加 4 个边字段；
  `BuildFilter` 改为 `internal static`；生成 EXISTS 子查询，`HasRolesWith` 复用
  `RoleStore.BuildFilter`（别名 `r2`），`HasUserRolesWith` 复用 `UserRoleFilterSql.Build`
  （别名 `ur`）。
- `src/AxonHub.Persistence/Stores/RoleStore.cs`：`RoleListFilter` 增加 4 个边字段；
  `BuildFilter` 改为 `internal static`；`HasUsersWith` 复用 `UserAdminStore.BuildFilter`
  （别名 `u2`），`HasUserRolesWith` 用别名 `ur2`。

## 语义（与 sqlgraph / 端口既有 C189 策略对齐）

- `hasX: true` → `EXISTS (...)`；`hasX: false` → `NOT (EXISTS (...))`。
- `hasXWith` 列表为空时不生成谓词；每个过滤器的谓词 AND 进同一个 EXISTS；空过滤器退化为
  「存在任意链接」。
- 与 `sqlgraph.HasNeighborsWith` 一致，不重新应用被连接实体的软删除谓词：软删除的 Role
  仍满足 `User.hasRoles`（测试锁定）。
- 边谓词内部的 `CreatedAtIsNil: false` 被忽略，但 EXISTS 的存在性要求仍然保留。

## 测试

- `tests/AxonHub.Persistence.Tests/UserAdminStoreTests.cs`（Postgres，新增 1 例）：
  `ListAppliesUserRoleEdgePredicates`——true / false、空 With 列表、空过滤器、按名匹配、
  软删除 Role、多过滤器 AND、HasUserRoles、CreatedAtIsNil 组合与 `Not` 嵌套；该类 22 通过。
- `tests/AxonHub.Persistence.Tests/RoleStoreTests.cs`（Postgres，新增 1 例）：
  `ListAppliesUserEdgePredicates`——true / false、空 With 列表、Email 精确 / ContainsFold、
  多过滤器 AND、HasUserRolesWith by Id、IsNil 组合与 `Not`；该类 13 通过。
  两个类合计 35 通过。
- `tests/AxonHub.Api.Tests/AdminUsersRolesEndpointTests.cs`：原 2 个 introspection 断言改名为
  `UserWhereInputExposesEveryScalarAndRoleEdgePredicate` /
  `RoleWhereInputExposesEveryScalarAndUserEdgePredicate` 并追加 4 个边字段；新增
  `UserRoleWhereInputExposesTheReferencePredicates`（31 个字段）、
  `QueryUsersMapsTheUserRoleEdgePredicates`、`QueryRolesMapsTheUserRoleEdgePredicates`；
  该类 29 通过。

## 台账

C135 保持 gap：`what` / `cause` / `decision` / `test` 改为 §201 后的状态，残余明确为 12 组
边谓词（Channel 6、User 5：hasProjects / hasAPIKeys / hasChannelOverrideTemplates /
hasOidcIdentities / hasProjectUsers，Role 1：hasProject）。`01-baseline.md` /
`03-acceptance.md` 已插入 §201 段落；lint 通过，`02-differences.md` 已重新生成；282 条
（206 accepted / 2 gap / 74 resolved）。
