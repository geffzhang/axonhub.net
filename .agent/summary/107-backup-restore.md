# §107 备份 / 恢复与自动备份管理面 GraphQL（backup / restore / autoBackupSettings / updateAutoBackupSettings / triggerAutoBackup）

日期：2026-11-06
参照：`b8bb3421:internal/server/backup/types.go:11-244`、
`internal/server/backup/backup_ops.go:26-132` / `:124-440`、
`internal/server/backup/restore.go:29-146` / `:148-259` / `:482-535` / `:536-699` /
`:700-790` / `:791-870` / `:871-960` / `:961-1555`、
`internal/server/backup/autobackup.go:22-222`、
`internal/server/biz/system.go:188-235` / `:1616-1676` / `:1975-1986`、
`internal/server/biz/system_default.go:118-129`、
`internal/server/gql/backup.graphql:1-97`、`internal/server/gql/backup.resolvers.go:20-146`、
`internal/ent/channel_update.go:1149-1155`

## 交付

- `src/AxonHub.Persistence/BackupSystemConfigKeys.cs`：备份白名单 17 个系统配置键。
- `src/AxonHub.Persistence/Stores/BackupStore.cs`：Dapper 读——system / project /
  channel / model / channel_model_price / api_key / usage request / usage log 的
  id 键集分页（`BatchSize = 500`），只读活行并解析活边（项目名、通道名、API key
  键映射按 `includeApiKeys` 门控）。
- `src/AxonHub.Persistence/Stores/BackupRestoreStore.cs`：EF 写 + 环境事务——
  `Begin` / `Commit` / `Rollback`、系统配置 upsert、通道按名查找与全列更新
  （`BaseUrl = null` 保留旧值，复刻 ent `SetNillableBaseURL`）、项目 / 模型 /
  API key / 价格 CRUD、价格版本归档、用量请求查找与 500 一批的用量日志写入。
- `src/AxonHub.Api/Management/BackupModels.cs`：备份文档 DTO（Go JSON 键名 /
  指针语义）、Options / ConflictStrategy / RestoreOptions、`AutoBackupSettings`
  与 `AutoBackupSettingsCodec`（三个 `*bool` 指针字段回退默认值、其余 Go 零值、
  行缺失取默认、键 `system_auto_backup_settings`）。
- `src/AxonHub.Api/Management/BackupJson.cs`：Go `encoding/json` 兼容写出——
  固定根成员顺序、`omitempty`、`time.Time` RFC3339Nano、`{}` 空对象与
  `timestamp` 用 `DateTime.Now`（本地时间）。
- `src/AxonHub.Api/Management/BackupService.cs`：`BackupAsync` /
  `BackupWithoutAuthAsync` / `RestoreAsync` / `Reschedule`（no-op）/
  `TriggerAutoBackupAsync` / `ShouldRunBackup` / `RunBackupNowAsync`，owner 门与
  逐段还原逻辑。
- `src/AxonHub.Api/GraphQL/BackupTypes.cs`：备份枚举 / 输入 / 输出、`Upload`
  标量映射（HotChocolate `IFile`）、`AutoBackupSettings` 输出映射与
  `BackupFrequency` 转换。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：`autoBackupSettings` 根字段；
  `AdminMutation.cs`：`backup` / `restore` / `updateAutoBackupSettings` /
  `triggerAutoBackup`；`GraphQLSetup.cs`：`AddUploadType()`；`Program.cs`：
  服务注册。
- `src/AxonHub.Api/Settings/SystemSettingsAdminService.cs`：Go 兼容的
  Get / Update 自动备份设置与 `UpdateAutoBackupLastRun`（UTC）。
- 支撑改动：`ChannelWriteModels.cs` 增加 `AzureCredentialDraft`（restore 解码
  `objects.AzureCredential`）、`ChannelWriteSerialization.cs` 抽出
  `NormalizeSettings` / `NormalizeCredentials` 供 restore 的 Go 解码-重编码往返
  复用、`ApiKeyProfilesCodec.cs` 的 `Cost` 加 `GoNullableDecimalConverter`
  （restore 走 `LlmJson` 解码存量 profile JSON）。

## 行为要点

- 备份文档：`version` / `timestamp` / `channels` / `models` 固定顺序；禁用段中
  `channels` / `models` 因 Go 指针输出 `null`，其余 `omitempty` 段整体省略；
  `channels.base_url` 为 NULL 时省略、`edges` 输出 `{}`；分页每批 500。
- owner 门：`user not found in context` → `only owners can perform backup
  operations` / `only owners can perform restore operations`；自动备份变更与
  trigger 走 `permission denied: owner access required`。
- 恢复：版本必须 `1.4`（`backup version mismatch: expected 1.4, got <v>`），
  版本检查在开事务之前；事务内按系统配置（白名单 17 键，逐键错误
  `failed to restore system configuration %q: %w`）→ 通道（先按名查找，再按
  `skip` / `overwrite` 处理，缺失凭据的旧行跳过）→ 通道 id 映射 → 旧配额路由 →
  价格（校验失败先于归档、空引用报错、归档活跃版本后写入新活跃版本）→ 模型 →
  项目 → API key（缺项目跳过、项目名默认 `Default`、UserId 取身份）→ 用量；
  用量请求按 id + 两个指纹去重，用量日志先建请求壳、缺请求 / 项目跳过。
- 自动备份：`ShouldRunBackup` 覆盖 daily / weekly / monthly 的日期边界；
  `RunBackupNowAsync` 设置读取失败包装为 `failed to get auto backup settings:
  %w`，`DataStorageId == 0` 报 `data storage not configured for backup`，失败时
  仍记录 last run 并重抛原错误；`TriggerAutoBackupAsync` 对设置错误静默返回。
- 设置编解码：只写 Go 结构体字段；三个 include 指针字段缺省时取默认
  （channels / models / modelPrices 为 true，其余 false），`retention_days`
  缺省 30，未知 frequency 原样保留。
- GraphQL：`backup` 返回 JSON 字符串（`success` / `data` / `message`）；`restore`
  读取 multipart Upload 后整段解码；`updateAutoBackupSettings` 返回布尔；
  `triggerAutoBackup` 以 `Task.Run` + 新 DI scope 异步执行并立即返回成功。

## 测试

- `tests/AxonHub.Api.Tests/BackupServiceTests.cs`：43 例——owner 门 6、备份文档 3、
  系统配置 1、通道 5、模型 2、项目 2、API key 2、价格 4、用量 4、自动备份
  `ShouldRunBackup` 理论 6 + 8 例。
- `tests/AxonHub.Api.Tests/AdminBackupEndpointTests.cs`：14 例——设置默认值 /
  存量文档 / 未知 frequency 输出报错 / 输入枚举拒绝 / 更新合并 / owner 门、
  备份金样与禁用段 null、恢复 Upload 提交与版本拒绝与 owner 门、trigger 成功与
  owner 门。
- `tests/AxonHub.Persistence.Tests/BackupStoreTests.cs`：8 例——白名单与软删、
  游标分页、活边解析、`includeApiKeys` 门控、API key 键映射。
- `tests/AxonHub.Persistence.Tests/BackupRestoreStoreTests.cs`：16 例——事务提交 /
  回滚 / 环境事务、通道查找与全列更新（BaseUrl null 保留）、设置与时间戳、
  项目 / 模型 / API key / 价格 CRUD、版本归档、用量查找与 500 批、日志批量写入。
- 装机计数：`AxonHub.Api.Tests` 2680 → 2737（+57），`AxonHub.Persistence.Tests`
  252 → 276（+24），Llm 3032、Grains 95，四项目合计 6059 → 6140，0 失败 0 跳过
  （静态口径，本轮未运行）。

## 差异

- C197（gap）：仅 database 存储分支（与参照 `doBackup` + `SaveData` 纯 nil 等价）；
  fs / s3 / gcs / webdav 后端、cron `0 2 * * *` 调度（`Reschedule` 为 no-op）、
  `cleanupOldBackups` 保留清理与 system value 缓存失效未移植；非 database 存储的
  自动备份确定性失败 `failed to write backup file: unsupported storage type: <type>`，
  当前只能手动 `triggerAutoBackup`。
- C198（accepted）：HotChocolate `IFile` / `AddUploadType` 对接 multipart；
  未声明 frequency 输出报字段错误（gqlgen 原样透出字符串）；输入枚举在校验阶段
  被拒绝（gqlgen 接受任意字符串落库）；trigger 用 `Task.Run` + 新 scope；
  GraphQL Int 32 位（C190）、解码文案（C002 / C141）与取消语义（C155）沿用既有登记。
