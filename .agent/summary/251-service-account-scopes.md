# §251 服务账号 API key 的作用域授予校验

## 背景

live 提交 `e5c904ef`（#2741 / #2724）修复了一个越权路径：项目成员只要持有
`write_api_keys`，就能创建携带 `read_channels` 等 system-only 作用域的
service_account key，而 API key 作用域在鉴权时不带项目维度，等于把项目级
授权放大成系统级。端口此前已移植该提交的 DataStoragePrivacy（要求
`read_data_storages`）与 TracePrivacy 的项目过滤，但 create / update /
rotate 三条写入路径直接保存调用方提供的 scopes。

## 实现

- `ScopeCatalog`
  - `IsValidScope`：与静态 catalog 的 slug 精确比较。
  - `IsSystemOnlyScope`：仅声明 system 级别、未声明 project 级别。
- `PermissionValidator`
  - `CanGrantApiKeyScopesForUser`：先逐项校验合法性与 system-only 的系统级持有，
    再调用既有 `CanGrantScopesForUser` 做项目级授予检查；错误文案与参照一致：
    - `invalid scope '<slug>'`
    - `insufficient permissions: cannot grant system scope '<slug>' that you
      don't possess at system level`
    - 其余沿用 `cannot grant scope '<slug>' that you don't possess`。
- `ApiKeyManagementService`
  - create：service_account 在生成 key 前校验 `command.Scopes`；
  - update：按 `existing + Scopes + AppendScopes` 去重计算可能保留的集合，
    `ClearScopes` 不触发校验；
  - rotate：校验 key 已携带的 scopes；
  - 校验失败统一包装 `insufficient permissions to grant the requested API key
    scopes: ...`。
  - 提取 `LoadCreatorPermissionsAsync`，项目管理员检查与作用域授予检查共用。

## 测试

- `CreateUserKeysRequiresProjectAdminButServiceAccountsDoNot`：项目成员仍不能
  创建 user key；无 scopes 的 service_account 可建；system-only scopes 与
  非法 scope 被拒绝。
- `CreateServiceAccountGrantsScopesHeldByTheCreator`：系统级持有的
  `read_channels` + 项目级持有的 `write_api_keys` 可通过。
- `UpdateServiceAccountValidatesTheResultingScopeSet`：替换 scopes 时仍校验
  Ent 合并后保留的既有 system scope。
- `RotateRejectsAServiceAccountKeyWithElevatedScopes`：轮换携带 system scope
  的 key 被拒绝，旧 key 不变。
- `ApiKeyManagementServiceTests` 22/22；service-account 相邻 Api 回归
  102/102。未跑 Api 全量，新增 Api 4 例待下次全量计入。

## 台账

- 新增 C313（resolved）。
- 313 条（190 accepted / 123 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- request / thread / trace / usage log 的项目过滤与 data storage scope 已由
  既有切片实现，本次只补写入侧的授予校验。
- 无效 scope 的检查顺序为 system-only 之前的逐项合法性，和参照一致。
