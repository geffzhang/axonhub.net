# §200 UserWhereInput / RoleWhereInput 标量谓词全量建模（C135 第二刀，4 例）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/gql/ent.graphql:7506-7672`、
`:5531-5615`。

## 背景

C135 记录 Channel / User / Role 三个 where 输入只建模管理面前端实际使用的字段。§199 已把
ChannelWhereInput 的 148 个标量谓词全部建模；本刀补齐 UserWhereInput（97 个标量谓词）与
RoleWhereInput（47 个标量谓词）。参照的 16 组边谓词（Channel 6、User 7、Role 3）仍留作
C135 残余，因为它们依赖尚未建模（或仅建模子集）的被连接实体 where 输入。

## 交付

- `src/AxonHub.Api/GraphQL/ManagementTypes.cs`
  - `UserWhereInput`：id 8、createdAt 8、updatedAt 8、email 13、status 4、
    preferLanguage 13、firstName 13、lastName 13、avatar 15（含 IsNil / NotNil）、
    isOwner 2，加 not / and / or，共 100 个字段。
  - `RoleWhereInput`：id 8、createdAt 8、updatedAt 8、name 13、level 4、projectID 6
    （ID / NEQ / In / NotIn / IsNil / NotNil），加 not / and / or，共 50 个字段。
  - ID 字段显式 `[GraphQLName]` / `IdType`，时间用 `GoTimeType`，列表用
    `ListType<NonNullType<...>>`，`projectID*` 全部显式命名（HotChocolate 会把
    `ProjectID` 推断成 `projectId`）。
  - `ToFilter(UserWhereInput?)` / `ToFilter(RoleWhereInput?)` 全量映射：id 各谓词
    `GidValue.Parse(...).Id`、时间直传、枚举 → wire 值、`bool? == true`。
- `src/AxonHub.Persistence/Stores/UserAdminStore.cs`：`UserListFilter` 扩展同名谓词，
  `BuildFilter` 按参照字段顺序用 `AppendStringPredicates` / `AppendTimeIn` /
  `AppendIdIn` / `AppendComparison` 生成；`HasProjectUsersWithProjectId` 保持原样。
- `src/AxonHub.Persistence/Stores/RoleStore.cs`：`RoleListFilter` 扩展同名谓词；
  `ProjectIdIsNil` / `ProjectIdNotNil` 从 `bool?` 改为 `bool`——只有 true 生效、false 忽略，
  修正旧实现「false 反转成 IS NOT NULL / IS NULL」的语义偏差。
- `src/AxonHub.Persistence/Stores/ConnectionSql.cs`：`AppendComparison` 增加 `bool?` 重载
  （`is_owner` 列）。

## 谓词语义（与 ent 对齐）

- Contains → `col LIKE %v%`；HasPrefix → `col LIKE v%`；HasSuffix → `col LIKE %v`。
- EqualFold → `LOWER(col) = LOWER(@p)`；ContainsFold → `LOWER(col) LIKE LOWER(@p)`。
- `<>` / `NOT IN` 与 SQL 相同，不返回 NULL 行；`IsNil` / `NotNil` 生成
  `IS NULL` / `IS NOT NULL`，false 忽略。
- 空列表 `IN` → false，`NOT IN` → true。

## 测试

- `tests/AxonHub.Persistence.Tests/UserAdminStoreTests.cs`（Postgres，新增 2 例）：
  `ListAppliesIdAndTimePredicates`（id 8 个谓词、createdAt / updatedAt 的代表性操作符与
  空列表语义）、`ListAppliesEveryStringAndBooleanPredicate`（email / status / preferLanguage /
  firstName / lastName / avatar / isOwner 的全部操作符、NULL 行排除与 IsNil / NotNil）；
  该类 20 通过。
- `tests/AxonHub.Persistence.Tests/RoleStoreTests.cs`（Postgres，新增 2 例）：
  `ListAppliesIdAndTimePredicates`、`ListAppliesNameLevelAndProjectPredicates`（name / level /
  projectID 的全部操作符；`projectIDIsNil:false` / `projectIDNotNil:false` 被忽略）；
  该类 11 通过。
- `tests/AxonHub.Api.Tests/AdminUsersRolesEndpointTests.cs`（新增 4 例）：
  `QueryUsersMapsEveryScalarWherePredicate` 与 `QueryRolesMapsEveryScalarWherePredicate` 通过
  GraphQL 传入每个字段族的代表谓词并断言捕获的过滤器；`UserWhereInputExposesEveryScalarPredicate`
  与 `RoleWhereInputExposesEveryScalarPredicate` 用 introspection 断言字段名集合恰为 97 / 47
  个标量 + not / and / or；该类 26 通过。

## 台账

C135 保持 gap：`what` / `cause` / `decision` / `test` 改为 §199 + §200 后的状态，残余明确为
16 组边谓词（Channel 6、User 7、Role 3）。`01-baseline.md` / `03-acceptance.md` 已插入 §200
段落；lint 通过，`02-differences.md` 已重新生成；282 条（206 accepted / 2 gap / 74 resolved）。
