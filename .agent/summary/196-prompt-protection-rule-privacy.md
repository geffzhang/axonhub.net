# §196 PromptProtectionRule 的 schema 隐私策略（C014 残余收窄，7 例）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/ent/schema/prompt_protection_rule.go:65-77`、
`internal/scopes/rule_apikey_scope.go:16-35`、`rule_owner.go:10-25`、`rule_user_scope.go:13-34`、
`rule.go:18`、`policy.go:51`，以及 `internal/server/biz/prompt_protection_rule.go:221-354` 中
服务读写发生的先后位置。

## 背景

C014 的残余一直写着「prompt protection 规则的 scopes 策略归 C014」：规则管理面（§151）已接入，
但 ent schema 的查询 / 变更策略在端口没有对应实现，任何已登录的管理员都能读写规则。
参考策略与通道相同：查询 `APIKeyScopeQueryRule(read_channels)` → `OwnerRule` →
`UserReadScopeRule(read_channels)` → 默认拒绝；变更 `OwnerRule` → `UserWriteScopeRule(write_channels)`
→ 默认拒绝。

## 交付

- 新增 `src/AxonHub.Api/Management/PromptProtectionRulePrivacy.cs`：逐条复刻上述规则
  （测试主体绕过、API key 无 scope 的拒绝文案、无用户上下文的 `no user in context`、
  跳过后的 `default deny`），拒绝异常沿用 `PrivacyDenyException` 与 ent 的 `%s: %w` 形状。
- `PromptProtectionRuleManagementService` 在 ent 实际求值的位置应用策略：
  - `CreateRule`：先校验 settings，再对名称查重（查询策略），最后在 Create 前做变更策略；
  - `UpdateRule`：重读（查询策略）→ 有效 settings 校验 → UpdateOneID 前变更策略；
  - delete / updateStatus / bulkDelete / bulkEnable / bulkDisable：写库前变更策略，
    空列表在策略之前短路（参考的 `len(ids)==0` 早退）；
  - `promptProtectionRules` 连接：读取前查询策略；
  - `previewPromptProtectionRule`：参考的 Preview 不触表，保持无策略。
- `Program.cs` 与 `AdminGraphqlGateServices.Register` 注册隐私服务。

## 测试

- `tests/AxonHub.Api.Tests/AdminPromptProtectionRulesEndpointTests.cs` 新增 7 例：
  1. 查询门禁：read_channels 成员可查；write_channels-only 与无 scope 成员被拒绝；
  2. service account 默认 scope（含 read_channels）可查；
  3. service account 缺 read_channels 被拒绝；
  4. write-only 成员：create / update 被内部读拒绝，status / bulkDelete 放行，空批量列表对
     无 scope 成员也返回 true；
  5. 只读成员：重复名创建先得到 DUPLICATE_NAME（查重先于变更策略），正常创建 / 更新 / 删除
     被拒绝，preview 无策略放行；
  6. 顺序：无 scope 成员的非法正则创建报 `invalid regex pattern`（校验先于查询策略），
     而更新在重读阶段即被拒绝；
  7. read_channels + write_channels 成员完成创建 / 更新 / 删除全流程。
- 实测（2026-11-13）：定向 25 通过 / 0 失败（含既有 18 例）；相关回归 122 通过 / 0 失败
  （PromptProtection / AdminPrompts / PromptManagementService）。`AxonHub.Api.Tests` 未跑全量。

## 台账

C014 保持 gap，但残余从「规则 scopes 策略」收窄为其余管理实体的 schema 查询 / 变更策略
（users / roles / projects 等根查询）。`03-acceptance.md` 的 C014 风险行已按当前状态重写
（登录、OIDC、权限校验器、通道隐私、覆盖模板与价格管理面均已接入）。lint 通过，
`02-differences.md` 已重新生成；282 条（206 accepted / 3 gap / 73 resolved）。
