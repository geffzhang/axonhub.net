# §205 ChannelOverrideTemplate / OIDCIdentity where 输入与 User / Project / APIKey 边谓词（C135 第七刀，5 例）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/gql/ent.graphql` 的
ChannelOverrideTemplateWhereInput / OIDCIdentityWhereInput / UserWhereInput /
ProjectWhereInput / APIKeyWhereInput 字段清单，`internal/ent/gql_where_input.go` 的谓词定义，
以及 ent 生成的 `internal/ent/{channeloverridetemplate,oidcidentity,user,project,apikey}/*.go`
中的 `HasNeighbors` / `HasNeighborsWith` SQL（含 users ↔ user_projects M2M）。

## 背景

C135 的 §199–§204 已补齐 Channel / User / Role / Project / APIKey / UserProject /
ChannelProbe / ChannelModelPrice / ChannelModelPriceVersion / ProviderQuotaStatus / Request /
RequestExecution / UsageLog 的输入面，残余 User 的 2 组边谓词
（hasChannelOverrideTemplates / hasOidcIdentities），依赖尚未建模的两张被连接实体输入。
C162 记录 ChannelOverrideTemplateWhereInput 只暴露前端子集；C269 记录 OIDCIdentityWhereInput
只暴露子集；C176 的第 (5) 项记录 APIKeyWhereInput 的 where 子集。本刀同时关闭这四处：
先全量建模模板（76 字段）与 OIDC（99 字段）输入，再接入 User 的最后 2 组边，并补齐
Project 的 10 组边（20 字段）与 APIKey 的 3 组边（6 字段）。

## 交付

- `src/AxonHub.Api/GraphQL/ChannelOverrideTemplateTypes.cs`
  - `ChannelOverrideTemplateWhereInput` 扩为 76 个字段：not / and / or；id / createdAt /
    updatedAt 各 8；userID 6（含 IsNil / NotNil）；name 13；description 15（含 IsNil / NotNil）；
    overrideParameters 13；hasUser / hasUserWith。
  - `ToFilter` 全量映射到 `ChannelOverrideTemplateListFilter`。
- `src/AxonHub.Api/GraphQL/SystemAdminTypes.cs`
  - `OidcIdentityWhereInput` 扩为 99 个字段：not / and / or；id / createdAt / updatedAt 各 8；
    issuer 13；subject 13；email 15（含 IsNil / NotNil）；idpName 15（含 IsNil / NotNil）；
    lastLoginAt 10（含 IsNil / NotNil）；userID 4；hasUser / hasUserWith。
  - `ToFilter` 全量映射到 `OidcIdentityListFilter`。
- `src/AxonHub.Api/GraphQL/ManagementTypes.cs`
  - `UserWhereInput` 追加 hasChannelOverrideTemplates / hasChannelOverrideTemplatesWith 与
    hasOidcIdentities / hasOidcIdentitiesWith（现 114/114 个字段），映射到 `UserListFilter`。
- `src/AxonHub.Api/GraphQL/ProjectTypes.cs`
  - `ProjectWhereInput` 追加 10 组边谓词（hasUsers / hasRoles / hasAPIKeys / hasRequests /
    hasUsageLogs / hasThreads / hasTraces / hasPrompts / hasAPIKeyProfileTemplates /
    hasProjectUsers，20 个字段，现 77/77），映射到 `ProjectListFilter`。
  - `hasRequestsWith` 使用 `TraceMapping.ToFilter`（RequestWhereInput → RequestListFilter）；
    `RequestMapping.ToFilter` 的重载面向 RequestExecution / UsageLog，方法组无法推断。
- `src/AxonHub.Api/GraphQL/ApiKeyTypes.cs`
  - `ApiKeyWhereInput` 追加 hasUser / hasUserWith、hasProject / hasProjectWith、
    hasRequests / hasRequestsWith（6 个字段，现 77/77），映射到 `ApiKeyListFilter`。
- `src/AxonHub.Persistence/Stores/ChannelOverrideTemplateStore.cs`
  - `ChannelOverrideTemplateListFilter` 扩为全量谓词；`BuildFilter` 按 ent 语义翻译全部
    标量族与 `hasUser` 边（`EXISTS (SELECT 1 FROM users u WHERE u.id = cot.user_id [AND ...])`）。
- `src/AxonHub.Persistence/Stores/OidcIdentityAdminStore.cs`
  - `OidcIdentityListFilter` 扩为全量谓词；`BuildFilter` 覆盖 99 字段语义与 `hasUser` 边；
    `BuildFilter` 由 private 改为 internal，供 `UserAdminStore` 的 hasOidcIdentitiesWith 复用。
- `src/AxonHub.Persistence/Stores/UserAdminStore.cs`
  - `UserListFilter` 追加两组边谓词；EXISTS 分别指向 `channel_override_templates.user_id` 与
    `oidc_identities.user_id`，With 谓词复用 `ChannelOverrideTemplateStore.BuildFilter` /
    `OidcIdentityAdminStore.BuildFilter`。
- `src/AxonHub.Persistence/Stores/ProjectStore.cs`
  - `ProjectListFilter` 追加 10 组边谓词；hasUsers / hasProjectUsers 走 M2M
    `user_projects`（非 With 版只查 join 行存在性，With 版把 users 谓词 AND 进同一个
    EXISTS）；其余边按目标表外键 EXISTS；With 谓词复用 RoleStore / ApiKeyStore /
    RequestSegmentStore / UsageLogStore / TraceAdminStore / ThreadAdminStore / PromptStore /
    ApiKeyProfileTemplateStore 的 BuildFilter。
- `src/AxonHub.Persistence/Stores/ApiKeyStore.cs`
  - `ApiKeyListFilter` 追加 hasUser / hasProject / hasRequests；EXISTS 指向
    `users.id = api_keys.user_id`、`projects.id = api_keys.project_id`、
    `requests.api_key_id = api_keys.id`。
- `src/AxonHub.Persistence/Stores/PromptStore.cs` /
  `src/AxonHub.Persistence/Stores/ApiKeyProfileTemplateStore.cs`
  - `BuildFilter` 由 private 改为 internal（Project 的 hasPromptsWith /
    hasAPIKeyProfileTemplatesWith 复用）。

## 语义

- `hasX: true` → `EXISTS (...)`；`hasX: false` → `NOT (EXISTS (...))`。
- `hasXWith` 列表为空时不生成谓词；每个过滤器的谓词 AND 进同一个 EXISTS；空过滤器退化为
  「存在任意链接」。
- 与 `sqlgraph.HasNeighborsWith` 一致，不重新应用被连接实体的软删除谓词：已软删除的 role /
  prompt / template / api_key / user / OIDC identity / project 行仍满足对应的 hasX / hasXWith。
- `Project.hasUsers` / `hasProjectUsers` 是 M2M through `user_projects`；`user_projects`
  本身无软删除列。
- `APIKey.hasProject` 只检查 `projects.id` 存在；project 行软删除不影响匹配。
- 字符串谓词沿用 PostgreSQL 字节序（`"Alpha" < "Gamma" < "beta"`）；`<>` 与 `NOT IN`
  排除 NULL；`IsNil` / `NotNil` 传 false 时忽略（不反转）。
- 空 `or` 仍渲染为恒假（全仓一致约定）。

## 测试

- `tests/AxonHub.Persistence.Tests/ChannelOverrideTemplateStoreTests.cs`（新增 1 例）：
  `ListAppliesEveryScalarAndUserEdgePredicate`——三行模板覆盖 id / createdAt / updatedAt /
  userID（含 IsNil / NotNil）/ name 13 / description 15 / overrideParameters 13 的每个算子，
  以及 hasUser / hasUserWith（软删除的 user 仍匹配）、Not / And / Or、`Or = []`。
- `tests/AxonHub.Persistence.Tests/OidcIdentityAdminStoreTests.cs`（新增 1 例）：
  `ListAppliesEveryScalarAndUserEdgePredicate`——三行身份覆盖 id / createdAt / updatedAt /
  issuer / subject / email（IsNil / NotNil）/ idpName（IsNil / NotNil）/ lastLoginAt
  （IsNil / NotNil；`<>` 排除 NULL）/ userID / hasUser / hasUserWith / Not / And / Or。
- `tests/AxonHub.Persistence.Tests/UserAdminStoreTests.cs`（新增 1 例）：
  `ListAppliesOverrideTemplateAndOidcIdentityEdgePredicates`——两组边 true / false /
  With（空、Name、NameContainsFold、SubjectContainsFold、Issuer）/ 软删除目标仍匹配 / Not。
- `tests/AxonHub.Persistence.Tests/ProjectStoreTests.cs`（新增 1 例）：
  `ListAppliesEveryEdgePredicate`——10 组边的 true / false / With / 空 With / 软删除目标 /
  Not；M2M 的 hasUsers 与 hasProjectUsers 分别断言 join 行存在性。
- `tests/AxonHub.Persistence.Tests/ApiKeyStoreTests.cs`（新增 1 例）：
  `ListAppliesEdgePredicates`——hasUser / hasProject / hasRequests 的 true / false / With /
  软删除目标 / Not；悬空 project id 用于 hasProject=false。
- `tests/AxonHub.Api.Tests/AdminChannelOverrideTemplateEndpointTests.cs`：schema 锁定测试改名为
  `SchemaExposesTheTemplateSurfaceAndEveryWherePredicate`（76 字段），新增
  `TemplatesWhereMapsEveryPredicateGroup`。
- `tests/AxonHub.Api.Tests/AdminSystemsOidcIdentitiesEndpointTests.cs`：新增
  `OidcIdentityWhereInputExposesEveryScalarAndUserEdgePredicate`（99 字段）与
  `QueryOidcIdentitiesMapsEveryPredicateGroup`。
- `tests/AxonHub.Api.Tests/AdminUsersRolesEndpointTests.cs`：`UserWhereInputExposesEveryScalarAndEdgePredicate`
  增加到 114 字段，新增 `QueryUsersMapsTheOverrideTemplateAndOidcIdentityEdgePredicates`。
- `tests/AxonHub.Api.Tests/AdminProjectsEndpointTests.cs`：`ProjectWhereInputExposesEveryScalarAndEdgePredicate`
  增加到 77 字段，新增 `QueryProjectsMapsEveryEdgePredicate`（一次查询传全部 20 个边变量）。
- `tests/AxonHub.Api.Tests/AdminApiKeysEndpointTests.cs`：`ApiKeyWhereInputExposesEveryScalarAndEdgePredicate`
  增加到 77 字段，新增 `QueryApiKeysMapsTheEdgePredicates`。
- 定向运行：持久化 Template + OIDC 11 通过、Project / ApiKey / User 49 通过；Api 改动过的
  5 个测试类 94 通过。未跑全量回归（用户此前确认全量 Api 回归耗时过长）。

## 台账

C135 转 resolved（User 114/114，十张 where 输入无残余缺口）；C162 转 resolved（模板 76 字段
闭合）；C269 更新（OIDC 99 字段闭合，SystemWhereInput 子集仍保留）；C176 的第 (5) 项
where 子集缺口关闭。`01-baseline.md` / `03-acceptance.md` 已插入 §205 段落；lint 通过，
`02-differences.md` 已重新生成；283 条（205 accepted / 1 gap / 77 resolved）。
