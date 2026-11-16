# §103 用量日志管理面（usageLogs / node(UsageLog) / 可见性谓词）

日期：2026-11-05
参照：`internal/server/gql/ent.resolvers.go:572-586 / 334-342`、
`internal/ent/schema/usage_log.go:122-136`、
`internal/scopes/rule_user_project_scope_requests.go:20-75`、
`rule_owner.go:10`、`rule_user_scope.go:12`、
`internal/ent/gql_pagination.go:6772-6784`（DefaultUsageLogOrder = id ASC）

## 交付

- `src/AxonHub.Api/Management/UsageLogManagementService.cs`：`ListAsync` /
  `GetNodeAsync`，先求值隐私再查询；缺失或不可见统一抛
  `ent: usage_log not found`。
- `src/AxonHub.Api/Management/TracePrivacy.cs`：新增
  `EvaluateUsageLogQueryAsync`，返回 `RequestAccessFilter(ProjectId,
  VisibilityUserId)`；测试 Principal 直接放行，无用户拒绝
  `default deny: ent/privacy: deny rule`。
- `src/AxonHub.Persistence/Stores/UsageLogStore.cs`：`UsageLogListQuery` 增加
  `ProjectScope` / `VisibilityUserId`；新增 `GetByIdAsync(id, projectScope,
  visibilityUserId)`；`AppendScopes` 渲染 `m.project_id = ?` 与
  `EXISTS (SELECT 1 FROM requests r …)` 可见性谓词。
- `src/AxonHub.Api/GraphQL/RequestTypes.cs`：`UsageLogType` 实现 `INode`；
  新增 `ToRootUsageLogSortField`（CREATED_AT→Id，方向保留）。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：`usageLogs` 根查询接线
  （validatePaginationArgs / validateFirstLast、where、order、连接构建）与
  `node(id)` 的 `UsageLog` 分支。
- `src/AxonHub.Api/Program.cs`：注册 `UsageLogManagementService`。

## 行为要点

- 排序：仅 `CREATED_AT` 重写为 `DefaultUsageLogOrder` 的 `id` 并保留方向，
  `UPDATED_AT` 按 updated_at；缺省 id ASC。
- 隐私链：UserProjectScopeReadRequestsRule（owner 视为系统作用域；非 owner 在
  项目作用域下追加逐行可见性）→ OwnerRule → UserReadScopeRule；UsageLogFilter
  分支展开为 playground 行按 user_id、API 行按「key 缺失 / 非 personal / 本人
  personal」；SQL 子查询表达与 ent 的 WhereHasRequestWith 等价。
- 节点与边：`node(UsageLog)` 受同一隐私链约束，request / project / channel 边
  沿用 §102 的 RequestQueryService 语义；where 输入为 §102 的 UsageLogWhereInput
  子集。

## 顺带修复

- `AppendScopes` 的 EXISTS 子查询缺少一个右括号（Postgres 42601），补齐为六个。
- 三个 `IUsageLogStore` 测试替身补齐 `GetByIdAsync`（`RequestsSliceUsageLogStore`
  / `InMemoryUsageLogStore` / `RecorderUsageLogStore`，均抛 NotSupportedException）。
- 系统 owner 的隐私断言拆成「无项目头 → null/null」与「有项目头 → 项目谓词」两例，
  与 `HasSystemScope` 的求值顺序一致。

## 测试

- `tests/AxonHub.Api.Tests/AdminUsageLogsEndpointTests.cs`：3 例（根连接形状与
  CREATED_AT→id DESC、缺省 id ASC、成本明细 / 档位、channel 边；where 谓词下推与
  UPDATED_AT ASC；node 详情与 `ent: usage_log not found`）＋ `UsageLogPrivacyTests`
  5 例（成员 / 项目 owner / 系统 owner 无项目头 / 系统 owner 有项目头 / 无作用域
  成员拒绝）。
- `tests/AxonHub.Persistence.Tests/UsageLogStoreTests.cs`：新增
  `ListAndGetByIdApplyProjectAndUsageLogVisibility`（playground、缺失 / 共享 /
  他人 personal key、项目范围与 GetById）。
- 定向复跑：Api 8/8、Persistence 9/9 通过。
- 整解决方案复跑：Llm 3032、Grains 95、Persistence 240、Api 2643，合计 6010，
  0 失败 0 跳过。过程中 Api testhost 曾多次随机崩溃（退出码 1、无 stderr、内存
  平稳），排除新增测试类后仍出现；随后同一份代码连续通过 2640 过滤集、2643 全量
  与整解决方案，未再复现，判定为环境抖动而非 §103 引入。

## 差异

- C193（accepted）：显式隐私求值与 SQL 子查询表达；where 输入沿用 C192 的
  UsageLogWhereInput 子集；`Project.usageLogs` / `Channel.usageLogs` 反向边未接线
  （承接 C165 的真实缺口）。
