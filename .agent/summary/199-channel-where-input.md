# §199 ChannelWhereInput 标量谓词全量建模（C135 第一刀，5 例）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/gql/ent.graphql:1349-1576`。

## 背景

C135 记录 ChannelWhereInput 只建模了管理面前端实际使用的 14 个字段（id / idIn、
type / typeIn、name / nameIn / nameContainsFold、status / statusIn、errorMessageIsNil /
NotNil、and / or / not）。参照由 entgql 生成 163 个字段：148 个标量谓词、12 个边谓词
（hasRequests / hasExecutions / hasUsageLogs / hasChannelProbes / hasChannelModelPrices /
hasProviderQuotaStatus 及各自 With）与 not / and / or。未建模字段在 GraphQL schema 校验
阶段被拒绝，而不是像参照那样参与查询。

## 交付

- `src/AxonHub.Api/GraphQL/ChannelTypes.cs`：`ChannelWhereInput` 补齐 15 个字段族的 148 个
  标量谓词：id 8、createdAt 8、updatedAt 8、type 4、baseURL 15、name 13、status 4、
  autoSyncSupportedModels 2、autoSyncModelPattern 15、defaultTestModel 13、orderingWeight 8、
  errorMessage 15、autoDisabledAt 10、autoDisableExpiresAt 10、remark 15。`baseURL*` 全部
  显式 `[GraphQLName]`（HotChocolate 会把 `BaseURL` 推断成 `baseUrl`）；Time 用
  `GoTimeType`，ID 用 `IdType`，列表用 `ListType<NonNullType<...>>`；`orderingWeight` 保持
  32 位 `Int`，与参照 schema 一致。
- `src/AxonHub.Persistence/Stores/ChannelAdminStore.cs`：`ChannelListFilter` 扩展同名字段
  （orderingWeight 在存储层用 `long`，对应 bigint 列）；`BuildFilter` 按参照字段顺序生成
  谓词，IsNil / NotNil 只有 true 生效。
- `src/AxonHub.Api/GraphQL/ChannelMapping.cs`：`ToFilter` 完成 GID 解析（id 的
  NEQ / In / NotIn / GT / GTE / LT / LTE）、Time 直传、枚举 → wire 值、int → long、
  `bool?` → `bool`（`== true`）的映射。
- `src/AxonHub.Persistence/Stores/ConnectionSql.cs`：把 `ApiKeyProfileTemplateStore` 内部
  的 ent 谓词翻译 helper 上提并统一命名（`AppendStringPredicates` / `AppendStringIn` /
  `AppendIdIn` / `AppendTimeIn` / `AppendComparison`），`AppendIdIn` / `AppendStringIn`
  增加 `negate` 参数（空列表：IN 为 false、NOT IN 为 true）。`ApiKeyProfileTemplateStore`
  改为 `using static` 复用，删除 190 行私有副本，行为不变（4 例持久化测试通过）。

## 谓词语义（与 ent 对齐）

- Contains → `col LIKE %v%`；HasPrefix → `col LIKE v%`；HasSuffix → `col LIKE %v`。
- EqualFold → `LOWER(col) = LOWER(@p)`；ContainsFold → `LOWER(col) LIKE LOWER(@p)`。
- `<>` / `NOT IN` 与 SQL 相同，不返回 NULL 行；`IsNil` / `NotNil` 生成
  `IS NULL` / `IS NOT NULL`。
- 空列表 `IN` → false，`NOT IN` → true。

## 测试

- `tests/AxonHub.Persistence.Tests/ChannelAdminStoreTests.cs`（Postgres，新增 3 例）：
  `ListAppliesIdTimeAndOrderingWeightPredicates`（id / createdAt / updatedAt /
  orderingWeight 的全部操作符与空列表语义）、`ListAppliesNullableStringPredicates`
  （baseURL / name / autoSyncModelPattern / defaultTestModel / errorMessage /
  autoDisabledAt / autoDisableExpiresAt / remark 的字符串与 NULL 语义）、
  `ListAppliesEnumBooleanAndNestedLogicPredicates`（type / status / autoSyncSupportedModels
  与 Not / And / Or 组合）；该 fixture 7 通过。
- `tests/AxonHub.Api.Tests/AdminChannelsEndpointTests.cs`（新增 2 例）：
  `QueryChannelsMapsEveryScalarWherePredicate` 通过 GraphQL 传入每个字段族的代表谓词并断言
  捕获的 `ChannelListFilter`；`ChannelWhereInputExposesEveryScalarPredicate` 用
  introspection 断言字段名集合恰为 148 个标量 + not / and / or；该类 41 通过。
- `tests/AxonHub.Persistence.Tests` 的 ApiKeyProfileTemplate 过滤 4 通过。

## 台账

C135 保持 gap：`what` / `decision` / `test` 追加 §199 内容，残余明确为 6 组边谓词（依赖
ChannelModelPrice / ChannelProbe / ProviderQuotaStatus / UserProject 等尚未建模的 where 输入）
与 User / RoleWhereInput 的全量谓词。`01-baseline.md` / `03-acceptance.md` 已插入 §199 段落；
lint 通过，`02-differences.md` 已重新生成；282 条（206 accepted / 2 gap / 74 resolved）。
