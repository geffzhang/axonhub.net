# §206 Model / Prompt / PromptProtectionRule / DataStorage / System / Trace / Thread where 输入（8 例持久化 + 6 例 introspection）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/gql/ent.graphql` 的七张
WhereInput 字段清单、`internal/ent/gql_where_input.go` 的谓词定义与空列表语义、ent 生成的
`internal/ent/{model,prompt,promptprotectionrule,datastorage,system,trace,thread}/*.go` 中的
`HasNeighbors` / `HasNeighborsWith` SQL，以及 `internal/ent/migrate/schema.go` 的边外键列
（`traces.project_id` / `traces.thread_id` / `requests.trace_id` /
`requests.data_storage_id` / `request_executions.data_storage_id` / `prompts.project_id` /
`threads.project_id`）。

## 背景

§199–§205 已补齐 Channel / User / Role / Project / APIKey / UserProject / ChannelProbe /
ChannelModelPrice / ChannelModelPriceVersion / ProviderQuotaStatus / Request /
RequestExecution / UsageLog / ChannelOverrideTemplate / OIDCIdentity 的 where 输入面。剩余
七张输入仍只暴露前端实际使用的子集：Model（C177 第 (2) 项）、Prompt（C188 第 (1) 项）、
PromptProtectionRule（C231）、DataStorage（C196 第 (5) 项）、System（C269）、Trace / Thread
（C189 第 (1) 项）。本刀一次性补齐，关闭全部 where 字段缺口。

## 交付

- `src/AxonHub.Persistence/Stores/ConnectionSql.cs`
  - 新增 `AppendTimePredicates`（Eq / NEQ / In / NotIn / GT / GTE / LT / LTE 共 8 个时间谓词）。
- `src/AxonHub.Api/GraphQL/ModelTypes.cs`
  - `ModelWhereInput` 扩为 115 个字段：not / and / or；id / createdAt / updatedAt 各 8；
    developer 13；modelID 13；type 4；name 13；icon 13；group 13；remark 15（含 IsNil /
    NotNil）；status 4。`ModelMapping.ToFilter` 全量映射。
- `src/AxonHub.Persistence/Stores/ModelStore.cs`
  - `ModelListFilter` 扩为 115 字段；`BuildFilter` 用 ConnectionSql helper 翻译 id / 时间 /
    字符串 / 枚举 / remark；保留空 Or → FALSE 的既有约定。
- `src/AxonHub.Api/GraphQL/PromptTypes.cs`
  - `PromptWhereInput` 扩为 97 个字段：projectID 4；name / description / role / content 各 13；
    status 4；order 8（SDL 为 Int，映射时 `(long)order`）；hasProject / hasProjectWith。
- `src/AxonHub.Persistence/Stores/PromptStore.cs`
  - `PromptListFilter` 扩为 97 字段；`BuildFilter` 翻译全部标量族与 hasProject 边
    （`EXISTS (SELECT 1 FROM projects pj2 WHERE pj2.id = prompts.project_id [AND ...])`）。
- `src/AxonHub.Api/GraphQL/PromptProtectionRuleTypes.cs` /
  `src/AxonHub.Persistence/Stores/PromptProtectionRuleStore.cs`
  - 扩为 70 字段：id / createdAt / updatedAt 各 8；name / description / pattern 各 13；
    status 4。
- `src/AxonHub.Api/GraphQL/DataStorageTypes.cs` /
  `src/AxonHub.Persistence/Stores/DataStorageStore.cs`
  - 扩为 67 字段，新增 hasRequests / hasRequestsWith（`requests.data_storage_id`）与
    hasExecutions / hasExecutionsWith（`request_executions.data_storage_id`）；
    `RequestSegmentStore.BuildFilter` / `RequestExecutionStore.BuildFilter` 供 With 复用。
- `src/AxonHub.Api/GraphQL/SystemAdminTypes.cs` /
  `src/AxonHub.Persistence/Stores/SystemStore.cs`
  - 扩为 53 字段：id / createdAt / updatedAt 各 8；key 13；value 13。
- `src/AxonHub.Api/GraphQL/TraceTypes.cs`
  - `TraceWhereInput` 扩为 60 字段，新增 hasProject / hasProjectWith、hasThread /
    hasThreadWith、hasRequests / hasRequestsWith；`ThreadWhereInput` 扩为 52 字段，新增
    hasProject / hasProjectWith、hasTraces / hasTracesWith。
- `src/AxonHub.Persistence/Stores/TraceAdminStore.cs` /
  `src/AxonHub.Persistence/Stores/ThreadAdminStore.cs`
  - `TraceListFilter` / `ThreadListFilter` 新增上述 10 个边字段；EXISTS 分别指向
    `projects.id = traces.project_id`、`threads.id = traces.thread_id`、
    `requests.trace_id = traces.id`、`traces.thread_id = threads.id`。

## 语义

- `hasX: true` → `EXISTS (...)`；`hasX: false` → `NOT (EXISTS (...))`；`hasXWith` 为空列表
  不生成谓词；With 谓词 AND 进同一个 EXISTS；不重新应用目标实体的软删除。
- 字符串谓词沿用 PostgreSQL 字节序；`<>` 与 `NOT IN` 排除 NULL；空 `In` / `NotIn` 忽略。
- 三处本地空谓词约定保持不动：DataStorage 的嵌套空 not / and / or 抛
  `ent: empty predicate DataStorageWhereInput: field '…'`；Model / Prompt /
  PromptProtectionRule / System 的空 Or 列表渲染为 FALSE；TraceAdminStore /
  ThreadAdminStore 忽略空 Or 列表。
- HotChocolate 默认仅小写首字母：含 `ID` 的字段显式 `[GraphQLName]`；其余
  `IdGT` / `OrderGT` / `HasRequestsWith` 等天然映射为 `idGT` / `orderGT` /
  `hasRequestsWith`。

## 测试

- `tests/AxonHub.Persistence.Tests/ModelStoreTests.cs`（新增 1 例）：
  `ListAppliesTheFullScalarPredicateSet`——id / 时间 / developer / modelID / name / icon /
  group / remark（NULL 语义）/ type / status 的算子与 And 组合。
- `tests/AxonHub.Persistence.Tests/PromptStoreTests.cs`（新增 1 例）：
  `ListAppliesTheFullScalarPredicateSetAndProjectEdge`——projectID / order / 四个字符串族 /
  status 与 hasProject true / false / With / 空 With（含软删除 project 仍匹配）。
- `tests/AxonHub.Persistence.Tests/PromptProtectionRuleStoreTests.cs`（新增 1 例）：
  `ListAppliesTheFullScalarPredicateSet`——id / 时间 / name / description / pattern /
  status / Not / Or。
- `tests/AxonHub.Persistence.Tests/DataStorageStoreTests.cs`（新增 2 例）：
  `ListAppliesTheFullScalarPredicateSet`（id / 时间 / name / description / primary / type /
  status / Or + Not）与 `ListAppliesRequestsAndExecutionsEdgePredicates`（两组边的
  true / false / With / 空 With / Not）。
- `tests/AxonHub.Persistence.Tests/SystemStoreTests.cs`（新增 1 例）：
  `ListAppliesTheFullScalarPredicateSet`——id / 时间 / key / value / 组合谓词。
- `tests/AxonHub.Persistence.Tests/TraceAdminStoreTests.cs`（新增 1 例）：
  `ListAppliesProjectThreadAndRequestsEdgePredicates`——三组边的 true / false / With /
  空 With；悬空 project id 与 NULL thread_id 用于 false 分支。
- `tests/AxonHub.Persistence.Tests/ThreadAdminStoreTests.cs`（新增 1 例）：
  `ListAppliesProjectAndTracesEdgePredicates`——两组边的 true / false / With / 空 With。
- `tests/AxonHub.Api.Tests`（新增 6 例 introspection）：`AdminModelsEndpointTests` 115、
  `AdminPromptsEndpointTests` 97、`AdminPromptProtectionRulesEndpointTests` 70、
  `AdminDataStoragesEndpointTests` 67、`AdminSystemsOidcIdentitiesEndpointTests` 53、
  `AdminTracesEndpointTests` Trace 60 + Thread 52。
- 定向运行：七个持久化测试类 78 通过；七个 Api 端点测试类 106 通过。未跑 Api 全量回归
  （用户此前确认全量 Api 回归耗时过长）。

## 台账

C177 / C188 / C231 / C196 / C269 / C189 更新（where 子集缺口闭合，其余已接受差异保留；
C231 的枚举严格度仍为 accepted）。`01-baseline.md` / `03-acceptance.md` 已插入 §206 段落；
lint 通过，`02-differences.md` 已重新生成；283 条（205 accepted / 1 gap / 77 resolved）。
