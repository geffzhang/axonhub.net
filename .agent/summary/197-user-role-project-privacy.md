# §197 User / Role / Project / UserProject 的 schema 隐私策略（C014 残余收窄，39 例）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/ent/schema/user.go:100-115`、
`role.go:85-97`、`project.go:122-134`、`user_project.go:67-79`，`internal/scopes/rule_owner.go`、
`rule_user_scope.go`、`rule_user_project_scope.go`、`rule_user_owned.go`、`rule.go:18`、
`policy.go:51`，以及 `internal/server/biz/{user,role,project}.go` 中服务读写发生的先后位置。

## 背景

§196 之后，C014 的残余只剩「其余管理实体的 schema 查询 / 变更策略」。users / roles / projects /
userProjects 的 GraphQL 面（§64、§85、§86、§87、§101）已接入，但 ent 的 schema 策略在端口没有
对应实现，已登录管理员可以越过 scopes 读取 / 写入这些实体。

## 规则链（逐条复核）

- User 查询：`OwnerRule` → `ProjectOwnerReadUsersRule` → `ProjectMemberReadUsersRule(read_users)` →
  `UserReadScopeRule(read_users)` → `UserOwnedQueryRule`；变更：`OwnerRule` →
  `UserWriteScopeRule(write_users)`。
- Role 查询：`UserProjectScopeReadRule(read_roles)` → `OwnerRule` → `UserReadScopeRule(read_roles)`；
  变更：`UserProjectScopeWriteRule(write_roles)` → `OwnerRule` → `UserWriteScopeRule(write_roles)`。
- Project 查询：`UserProjectScopeReadRule(read_projects)` → `OwnerRule` →
  `UserReadScopeRule(read_projects)`；变更：**只有** `OwnerRule` → `UserWriteScopeRule(write_projects)`。
- UserProject 查询：`OwnerRule` → `UserProjectScopeReadRule(read_users)` → `UserOwnedQueryRule`；
  变更：`OwnerRule` → `UserProjectScopeWriteRule(write_users)`（没有 `UserWriteScopeRule`，无用户
  上下文因此以 `default deny` 结束）。

## 本刀纠正的一处参照误读

此前实现按「Project 变更以 `UserProjectScopeWriteRule(write_projects)` 开头」接线，并补了
`rule_user_project_scope.go:242-262` 的 `*ent.ProjectMutation` 分支。复核冻结基线后确认
`project.go:129-132` 只有 `OwnerRule` → `UserWriteScopeRule(write_projects)`：该分支对 Project
schema 不可达。端口已移除多接的规则与参数（`EvaluateMutationAsync(CancellationToken)`），
非 owner 即使拥有目标项目的成员关系或项目级 write_projects 也被 `default deny`；只有 owner 或
持系统级 write_projects 的调用者能创建 / 更新 / 删除项目。对应测试改为锁定这一行为。

## 交付

- 新增 `src/AxonHub.Api/Management/UserPrivacy.cs` / `RolePrivacy.cs` / `ProjectPrivacy.cs` /
  `UserProjectPrivacy.cs`：逐条复刻规则次序、谓词与拒绝文案（`%s: %w` 形状）。
- 管理服务在 ent 求值点应用策略并把谓词下推到存储：
  - `UserManagementService`：list / get / node 的查询策略（`UserListFilter.HasProjectUsersWithProjectId`）、
    create / update / status、项目成员入口的变更策略；
  - `RoleManagementService`：list（`RoleListFilter.ProjectId`）、名称查重、更新前重读、create /
    update / delete / 批量删除的变更策略与 `WhereP(project_id = ctx)`；
  - `ProjectManagementService`：list（`ProjectListFilter.Id`）、名称查重、create / update / status /
    profiles / delete 的变更策略；
  - `ProjectUserService` 与 `AdminQuery` / `ProjectUserTypes` 的根字段、node、边接线；
  - `UserProjectAccessFilter` 从 Api 移入 Persistence（存储层谓词接缝，Persistence 不引用 Api）。
- `Program.cs` 与 `AdminGraphqlGateServices.Register` 注册四个隐私服务。
- 拒绝经 `AxonHubGraphQLErrorFilter` 统一报告 `permission denied` / `FORBIDDEN`。

## 测试

- `tests/AxonHub.Api.Tests/ManagementPrivacyTests.cs`（新增，30 例）：四类策略链与决策次序、
  owner / test 身份、项目 owner / 成员与 system scope、Project 变更不读取项目成员关系、
  可见性助手（`GetVisibleUserOrThrowAsync` / `OrNull` / role / project）、UserProject 自助过滤。
- `tests/AxonHub.Api.Tests/AdminUsersRolesProjectPrivacyEndpointTests.cs`（新增，9 例）：
  非 owner 与 service account 的根查询拒绝、带 scope 放行、X-Project-ID 对 projects / roles /
  users 的收窄、`node(User)` 的项目过滤、`Project.projectUsers` 回退本人、嵌套 `User.roles`
  的项目过滤、`createUser` 变更拒绝、`updateProject` 的 owner / 非 owner 分支。
- 实测（2026-11-13）：定向 39 通过 / 0 失败；相关管理面回归 174 通过 / 0 失败
  （AdminProjects / AdminUsersRoles / ManagementService / PermissionValidator / ProjectUserService）。
  `AxonHub.Api.Tests` 未跑全量。

## 台账

C014 保持 gap，残余收窄为：Role.users 等反向边（C165）、AddUserToProject 的策略求值在端口
早于存储事务（参照在事务内，多步骤同时失败时错误优先级可能不同）与通道管理面的已接受细节。
`01-baseline.md` 已插入 §197 段落，`03-acceptance.md` 已插入 §197 小节并重写 C014 风险行。
lint 通过，`02-differences.md` 已重新生成；282 条（206 accepted / 3 gap / 73 resolved）。
