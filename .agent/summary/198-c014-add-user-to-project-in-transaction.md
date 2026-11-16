# §198 C014 收口：AddUserToProject 的 schema 策略移入存储事务（6 例）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/biz/user.go:420-452`、
`internal/ent/schema/user_project.go:23-31`（唯一索引 `user_projects_by_user_id_project_id`）。

## 背景

§197 之后 C014 只剩一项真实残余：端口在调用存储之前求值 AddUserToProject 的三条 schema
策略，参照在 `RunInTransaction` 内按位置求值。两侧最终都以 privacy deny 报告时不可观察，
但「插入失败」与「User 读策略拒绝」同时发生时错误优先级相反。

## 参照次序

1. `UserProject.Create().SetUserID().SetProjectID()...Save(ctx)` — UserProject 变更策略；
   失败包装 `failed to add user to project: %w`（user.go:434-437）。
2. 仅 roleIDs 非空：`client.User.Get(ctx, userID)` — User 查询策略；失败包装
   `failed to get user: %w`（user.go:443-446）。
3. `user.Update().AddRoleIDs(...).Exec(ctx)` — User 变更策略；失败包装
   `failed to add roles to user: %w`（user.go:447-449）。

第 1 步的插入受唯一索引约束：重复成员关系会在 User 读策略之前失败。端口此前把三条策略全部
前置，重复成员 + 无 read_users 的调用者会先看到读策略拒绝。

## 交付

- `IUserAdminStore.AddProjectUserAsync` 增加三个可选回调 `beforeCreate` /
  `beforeUserRead` / `beforeUserRoleUpdate`；`UserAdminStore` 在事务内按参照位置求值，
  以相同的 `%w` 文案包装为 `StoreFailureException`（与 §176 为 Remove/UpdateProjectUser
  建立的接缝一致）。
- `UserManagementService.AddUserToProjectAsync` 删除三处前置求值，改为传入策略委托
  （`UserProjectPrivacy.EvaluateMutationAsync(Create)`、`UserPrivacy.GetVisibleUserOrThrowAsync`、
  `UserPrivacy.EvaluateMutationAsync`）。
- GraphQL 呈现层不变：异常链中的 `PrivacyDenyException` 仍统一映射为
  `permission denied` / `FORBIDDEN`；唯一索引失败不是 privacy deny，按原消息浮出。

## 测试

- `tests/AxonHub.Persistence.Tests/UserAdminStoreTests.cs`（3 例）：create 策略拒绝先于插入；
  唯一索引失败先于 User 读策略（读策略的回调不会被调用到）；User 读与 User 变更策略在事务内
  的位置，失败后 user_project / user_roles 均回滚。
- `tests/AxonHub.Api.Tests/AdminUsersRolesProjectPrivacyEndpointTests.cs`（3 例）：重复成员在
  无 read_users 的调用者下报告插入失败；插入成功时读策略拒绝且回滚；create 策略拒绝无副作用。
- 实测（2026-11-13）：UserAdminStoreTests 19 通过 / 0 失败；两个 GraphQL fixture 合计
  34 通过 / 0 失败；管理面回归 182 通过 / 0 失败。

## 台账

C014 由 gap 改为 resolved，`decision` / `test` 追加 §198 内容；剩余反向边（Role.users 等）
继续由 C165 跟踪。`01-baseline.md` / `03-acceptance.md` 已插入 §198 段落并更新 C014 风险行；
lint 通过，`02-differences.md` 已重新生成；282 条（206 accepted / 2 gap / 74 resolved）。
