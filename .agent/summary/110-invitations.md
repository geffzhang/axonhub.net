# §110 邀请与注册 REST 面（invitations / 创建 / 查询 / 注册 / 角色删除撤销）

日期：2026-11-06
参照：`b8bb3421:internal/ent/schema/invitation.go:20-58`、
`internal/server/biz/invitation.go:66-335`、`internal/server/biz/role.go:181-268`、
`internal/server/api/invitation.go:70-138`、`internal/server/routes.go:87-88` / `:112`、
`internal/server/biz/invitation_test.go`（7 个顶层测试）

实时上游 `f272549f` 的 schema / biz / api / role 文件与冻结基线逐字节一致；邀请 schema 标注
`entgql.Skip(entgql.SkipAll)`，`internal/server/gql` 中没有任何 invocation 成员（grep 无命中），
因此本切片是纯 REST 面，不影响 GraphQL 覆盖统计。

## 交付

- `src/AxonHub.Persistence/Stores/InvitationStore.cs`：`invitations` 的 EF Core 写 + Dapper 读；
  软删 `deleted_at = 0`、`token_hash` 唯一索引与 `project_id` 索引、`GetByTokenHashAsync` 联表取
  project 名称（项目软删时名称为 null）、`RegisterAsync` 单事务（条件扣减 `used_count`、建用户 /
  成员 / 角色链接）、`DeleteByRoleIdsAsync` 加入调用方的环境事务。
- `src/AxonHub.Api/Management/InvitationService.cs`：CreateInvitation / GetInvitation /
  RegisterInvitation / canInvite；`InvitationInfo`、`CreatedInvitation`、`RegisteredInvitation`；
  expiry、token 生成与 SHA-256 哈希。
- `src/AxonHub.Api/Auth/InvitationHandlers.cs`：`MapInvitationRoutes` 映射
  `POST /admin/invitations`、`GET /auth/invitations/{token}`、
  `POST /auth/invitations/{token}/register`，gin 风格 400 / 403 / 404 / 500 分类与
  `InvitationResponse` 手写线形（token `omitempty`、`expiresAt` 为 RFC3339Nano 或 null）。
- `src/AxonHub.Api/Management/RoleManagementService.cs`：Delete / BulkDelete 在既有事务内先调用
  `IInvitationStore.DeleteByRoleIdsAsync`，失败包装 `failed to revoke role invitations: ` 并整体回滚。
- `src/AxonHub.Persistence/Records.cs` / `DatabaseSchema.cs` / `AxonHubDbContext.cs`：表、索引、
  `InvitationRecord` / `InvitationWithProject` / `InvitationRegistration`；
  `src/AxonHub.Api/Program.cs`：服务注册、路由挂载与 `/admin/invitations` 的
  AdminJwtAuthMiddleware + ProjectIdMiddleware 分支。

## 行为要点

- 创建：canInvite 先于一切校验——上下文用户缺失 `user not found in context`；系统 `write_users`、
  项目 owner、匹配成员 `user_projects.is_owner` 或成员 scopes / 项目角色 scopes 含 `write_users`
  放行；否则 `permission denied: project user management access required`（项目角色只在匹配
  membership 内被咨询）→ roleID 0 `project role is required` → maxUses 仅 0 / 1
  `max uses must be 1 or 0` → expiry（nil 7 天、<0 `expiration must not be negative`、0 永不过期、
  >100000 `expiration hours exceed maximum limit`）→ 6 字节 base64url token（8 字符）→
  项目读取（`failed to get project: `，缺失 `ent: project not found`）→ 角色 id + 同项目
  （任何查找错误折叠为 `project role not found`）→ CanGrantRole（`permission denied: `）→
  落库（`failed to create invitation: `）。maxUses 0 时 remainingUses 恒 0。
- 查询：token 取 SHA-256 小写 hex；未知 / 软删 / 存储错误 → `invitation not found`；过期或用量耗尽
  → `invitation is no longer valid`；项目名取 WithProject 边，项目被软删时参照 panic、端口返回空串（C201）。
- 注册：email TrimSpace，空 email / password → `email and password are required`；password 按 UTF-8
  字节数 < 7 → `password must be at least 7 characters`；单事务内重读邀请 → 过期 / 用量 →
  role_id 空（`invitation role is required`）→ 角色 live 且同项目（`invitation role is no longer
  available`）→ 邮箱占用（`a user with this email already exists`，查询失败
  `failed to check existing user: `）→ 条件更新 `used_count = used_count + 1`（带 expires_at /
  max_uses 谓词，影响 0 行视为失效，失败 `failed to use invitation: `）→ 建用户
  （`failed to create user: `）→ 建成员（`failed to add user to project: `）→ 绑角色
  （`failed to assign invitation role: `）；提交后重载用户及 roles / projectUsers /
  oidcIdentities 生成 UserInfo。
- 角色删除：DeleteRole / BulkDeleteRoles 在角色软删的同一事务内撤销邀请（只软删 live 且 role_id
  命中的行），撤销失败 `failed to revoke role invitations: ` 并回滚；默认 Developer 角色仍在撤销前拒绝。
- REST：`POST /admin/invitations` 绑定失败 400 `invalid invitation request`、服务错误 403、项目头缺失
  400；`GET /auth/invitations/{token}` 错误 404；注册绑定失败 400 `invalid registration request`、
  服务错误 400、JWT 签发失败 500 `failed to create session`；错误信封
  `{"error":{"type":…,"message":…}}`。

## 测试

- `tests/AxonHub.Api.Tests/InvitationServiceTests.cs`：33 例——参照的 7 个测试（单次 / 无限 /
  legacy / 不可授予角色 / 已删除角色 / 过期 / token 形状）加 canInvite 六条路径、创建参数校验与
  错误包装、查询 / 注册全部分支、提交后重载。
- `tests/AxonHub.Api.Tests/AdminInvitationEndpointTests.cs`：22 例——JWT 门与 Bearer 文案、项目头、
  绑定失败（roleID 0 / null / 缺失 / 坏 JSON）、响应形状与 null expiresAt、错误分类、gid 校验、
  查询剥离 token、注册建用户 + 会话、密码按 rune 计、缺系统密钥 500。
- `tests/AxonHub.Api.Tests/ManagementServiceTests.cs`：新增 4 例——角色删除 / 批量删除撤销、
  撤销失败回滚、默认角色提前拒绝。
- `tests/AxonHub.Persistence.Tests/InvitationStoreTests.cs`：14 例——列往返与 project 边、默认时间戳、
  软删过滤、软删项目空名、注册的用户 / 成员 / 角色 / 用量、二次使用拒绝、无限次、过期 / legacy /
  软删角色 / 跨项目角色 / 已占用邮箱、撤销只命中 live、加入环境事务与回滚。
- 静态计数：`AxonHub.Api.Tests` 2789 → 2848（+59），`AxonHub.Persistence.Tests` 280 → 294（+14），
  Llm 3032、Grains 95，合计 6196 → 6269。**本轮按项目规则未运行 lint / build / test**，
  下一次全量运行应以该数字复核。

## 差异

- C201（accepted）：软删项目边的 panic vs 空名、RFC3339Nano 与 100ns tick / Postgres 微秒、gin 与 STJ
  的 JSON 解码差异（尾随字节、显式 null roleID、maxUses 缺省）、`%w` 内层为 Npgsql 文本、
  密码哈希在事务外（不可观察）、提交后重载经 `IUserProfileStore`。
- C136 由 gap 转为 resolved：角色删除时邀请撤销不再是 no-op，且与角色软删同事务。

## 残余风险

- 本轮未运行构建与测试，新增代码的编译 / 分析器正确性仅由人工检查保证。
- `RegisterAsync` 的并发扣减依赖单条条件 UPDATE；参照同为条件更新，未额外加并发用例。
