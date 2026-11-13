# §208 node() 剩余六个类型 + dataStorage 输出边（11 例 Api）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/gql/graphql.go:206-226` 的
`guidTypeToNodeType`（19 个类型）、`internal/ent/gql_node.go` 的 `noder()`、
`internal/ent/gql_edge.go` 的 `ChannelProbe.Channel`（187-194）、`Request.DataStorage`
（495-502）、`RequestExecution.DataStorage`（569-576）、`UserRole.User` / `UserRole.Role`
（880-900），以及 Role / UserProject / ChannelOverrideTemplate 的 ent 隐私规则。

## 背景

C165 的 node() 缺口与 C192 的 dataStorage 输出边缺口同属「只能经 node(id:) 或对象边到达」的
输出面：前端契约不读取它们，`§204`–`§207` 也只补了 where 输入。但参照的 gqlgen 会对
`guidTypeToNodeType` 中的每个类型进入对应表解析，未注册的端口类型返回
`unknown node type: <type>`，与参照行为不同；请求正文读取已由 `§135` 的
`RequestPayloadStorage` 走 `dataStorageID` 接缝，输出边则仍缺失。本刀把两者一次收口。

## 实现

- 六个输出类型实现 `INode`：`ChannelProbeType`、`ChannelOverrideTemplateType`、
  `RequestExecutionType`、`RoleType`、`UserProjectType`、`UserRoleType`。`node(id:)` 的
  19 个类型至此全部可达；`GraphQLSetup` 显式 `AddType<ChannelProbeType>()` /
  `AddType<UserRoleType>()`，因为二者无法从任何根字段到达。
- `AdminQuery.NodeAsync` 增加六个分支：
  - `ChannelProbe` / `RequestExecution`：按主键读取，缺失报 `ent: <label> not found`。
  - `UserRole`：按主键读取，`user` / `role` 边直接解析，缺失即报错（无 MaskNotFound）。
  - `Role`：`RolePrivacy.GetVisibleRoleOrThrowAsync`（用户作用域读规则）。
  - `UserProject`：`UserProjectPrivacy.EvaluateQueryAsync` → `GetProjectUserByIdAsync`
    （Owner / 项目作用域 / 用户查询规则），过滤掉的行报 `ent: user_project not found`。
  - `ChannelOverrideTemplate`：新增 `ChannelOverrideTemplateService.GetNodeAsync`，按当前
    用户作用域读取，缺失文案不带服务前缀（`ent: channel_override_template not found`）。
- `ChannelProbe.channel` 边按 `gql_edge.go:187-194` 直接解析，缺失即报错。
- `Request.dataStorage` / `RequestExecution.dataStorage`：新增
  `RequestQueryService.GetVisibleDataStorageOrNullAsync`，先求值 `DataStoragePrivacy` 再按 id
  读取活动行；null / 0 外键短路但不跳过隐私（复刻 ent 在 SQL 之前求值 policy、deny 即使外键
  为空也传播的行为）；缺失 / 软删行经 MaskNotFound 映射为 null。

## 测试

新增 11 例并更新既有断言：

- `AdminChannelProbeDataEndpointTests`：`NodeResolvesTheProbeRow`、
  `NodeReportsAMissingProbeAsEntNotFound`。
- `AdminChannelOverrideTemplateEndpointTests`：`NodeResolvesTheTemplatesOwnedByTheCurrentUser`、
  `NodeReportsAnotherUsersTemplateAsNotFound`、`NodeReportsMissingTemplatesWithoutTheServicePrefix`。
- `AdminRequestsEndpointTests`：`RequestExecutionNodeResolvesTheExecutionAndItsDataStorage`、
  `DataStorageEdgesAreMaskedWhenTheRowIsMissing`；
  `RequestQueryServiceVisibilityTests`：`DataStorageQueryDenyPropagatesEvenWithoutAForeignKey`、
  `NullDataStorageIdSkipsTheQueryForAnAllowedCaller`。
- `AdminUsersRolesEndpointTests`：`NodeResolvesRoleUserProjectAndUserRole`、
  `NodeReportsMissingRolesAndJoinRowsLikeTheReference`；
  `NodeReportsUnknownTypesAndMissingNodesLikeTheReference` 改以 `PromptProtectionRule`
  （不在参照映射表中）作为未映射类型。
- `ChannelProbeServiceTests` 的测试替身补齐 `GetByIdAsync`。

定向运行：五个测试类 94 通过（1m58s）；未跑 Api 全量回归。

## 台账

C165 更新为「node() 已覆盖 `guidTypeToNodeType` 全部 19 个类型（§208），仅余 Project / User
反向边」，仍为 accepted；C192 由 accepted 转 resolved，`what` / `cause` / `decision` / `test`
改为「输入面 §204 + dataStorage 输出边 §208 全部闭合」。`01-baseline.md` /
`03-acceptance.md` 已插入 §208 段落；lint 通过，`02-differences.md` 已重新生成；
283 条（204 accepted / 0 gap / 79 resolved）。
