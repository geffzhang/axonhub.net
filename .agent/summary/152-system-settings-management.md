# §152 系统设置管理面第一批（6 查询 / 6 变更）

日期：2026-11-09
参照：`internal/server/biz/system.go:877-941`（BrandName / BrandLogo / Title 直查 ent，
缺失 `""`，读取错误逐字段前缀）、`962-978`（setSystemValue 的 upsert 与错误前缀）、
`1293-1317`（normalize / validate：未知 auto-sync → 1h、空白提示词恢复默认、非空白不裁剪、
系统提示词先校验、4096 rune）、`1319-1338`（ChannelSetting 读取与归一化）、`1385-1462`
（UpdateChannelSetting 的五次 compare-and-set）、`1464-1477`（applySystemChannelSettingsPatch）、
`1544-1588`（GeneralSettings / SetGeneralSettings，含 `timeLocation` 缓存失效）、
`1743-1817`（三个布尔开关）、`503-573`（AutoSyncFrequency 的 MarshalGQL / UnmarshalGQL /
永不失败的 UnmarshalJSON）、`576-648`（ProbeFrequency 与未知存储值输出 ONE_MINUTE）、
`internal/server/biz/system_default.go:6-8、101-116`（默认提示词、4096、默认 channel/general
设置）、`internal/server/gql/system.graphql:21-45、271-361`（SDL）、
`internal/server/gql/system.resolvers.go:24-47、143-162、308-336、491-512、609-622、654-688`
（12 个根字段的薄转发与逐层前缀）。

## 背景

§63 只接入了 system-settings 的第一批（retryPolicy / modelSettings / securitySettings /
storagePolicy 等 4 查询 / 3 变更）。未改动的前端还有 brand / general / channel /
pass-through 三类页面，本切片补齐它们发送的全部 GraphQL 契约。

## 交付

- 新增 `src/AxonHub.Api/Settings/SystemSettingsModels.cs`：域记录、`SystemSettingsDefaults`
  （复用 `ChannelTestPrompts` 与 USD/UTC）、`SystemSettingsCodec`（Go 字段序 + `GoJson.Escape`
  的 HTML 转义、`AutoSyncFrequency` 永不失败的 JSON 解码、Normalize / Validate / ApplyPatch）。
- `src/AxonHub.Api/Settings/SystemSettingsAdminService.cs`：新增 16 个方法——general 读写、
  channel 读 + CAS 循环、brand 三读三写、三个开关读写；general 写入成功后通知
  `IAutoBackupRescheduleSignal`。
- `src/AxonHub.Persistence/Stores/SystemStore.cs`：新增 `TryCreateAsync`（EF 插入，SQLSTATE
  class 23 视为 `ent.IsConstraintError` 返回 false）与 `UpdateValueIfUnchangedAsync`
  （Dapper `UPDATE ... WHERE id / updated_at / value / deleted_at = 0`，
  `CURRENT_TIMESTAMP` 推进 `updated_at`，返回受影响行数）。
- `src/AxonHub.Api/GraphQL/SystemSettingsTypes.cs`：`ProbeFrequency` / `AutoSyncFrequency`
  两个小写值枚举、八个输出类型、八个输入类型与域 ↔ GraphQL 映射（未知存储枚举值按
  MarshalGQL 输出默认符号）。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：`brandSettings`、`systemChannelSettings`、
  `systemGeneralSettings`、`userAgentPassThroughSettings`、`passThroughSettings`、
  `usageCostInjectionSettings` 六个查询；`AdminMutation.cs`：对应六个变更。
- 测试假件：`FakeSystemStore` 增 `TryCreateAsync` / `UpdateValueIfUnchangedAsync` 与脚本化
  覆盖；新增 `RecordingAutoBackupRescheduleSignal`。

## 行为要点

- general 读：缺失 → USD / UTC；空串逐字段回退；解码失败 `failed to get general settings:
  failed to unmarshal general settings: ...`。写：Go `json.Marshal` 全量输出，错误双层前缀
  `failed to set general settings: failed to create system setting: ...`；成功后重排自动备份。
- channel 读：缺失 → 默认（probe true/5m、autoSync 1h、两条默认提示词）；读取后归一化；
  未知 probe 存储频率不归一、输出 ONE_MINUTE；未知 autoSync 归一为 1h 后输出 ONE_HOUR。
- channel 写：五次 CAS。缺失分支先 patch 再 normalize / validate；存在分支
  decode → normalize → patch → normalize → validate；重排后字符串相同则不写；CAS 谓词
  (id, updated_at, value) 且只命中 live 行；0 行重试；五次失败报
  `failed to update channel setting after 5 concurrent attempts`。
- 提示词：空白（Go TrimSpace）恢复默认，非空白不裁剪；系统提示词先校验；按 rune 计数，
  >4096 报 `test system prompt must not exceed 4096 characters`。
- brand：三个键各自直查、缺失 `""`；解析器层再包一次（双前缀）；部分更新按
  brandName → brandLogo → title 顺序，只写提供的字段。
- 三个开关：缺失 false；仅精确 `"true"` 为真（`"TRUE"`、前后空格为假）；写入 `"true"` /
  `"false"`。
- 备份重排：生产实现为 Orleans grain 信号，调度失败只记录（与参照的 Reschedule 一致）；
  无 Orleans 的宿主不注册，等同于参照的 nil scheduler。
- `timeLocation` 缓存失效在端口为空操作：`SystemTimeLocations` 每次读取时区（C186）。
- 枚举输入：HotChocolate 在输入校验阶段拒绝未知值，参照由 UnmarshalGQL 报
  `invalid ProbeFrequency: <x>`（C232）；两侧都拒绝且不写库。

## 测试

- `tests/AxonHub.Api.Tests/AdminSystemSettingsEndpointTests.cs`（22）：general 默认 / 存储 /
  空串归一 / 查询双层错误 / 写入文档与重排；channel 默认值、存储归一、解码错误、缺失分支
  建默认文档、部分 patch、空白提示词恢复、4096 rune 校验、未知枚举拒绝、五次 CAS 重试文案、
  并发写入后重放 patch、无变化不写；brand 缺失 / 存储 / 逐字段读前缀 / 部分写 / 写前缀；
  三个开关默认值、仅精确 `"true"`、写入字符串与错误前缀。
- `tests/AxonHub.Persistence.Tests/SystemStoreTests.cs`（3）：CAS 要求精确行状态并推进时间、
  忽略软删行、`TryCreateAsync` 对 live 与软删重复键返回 false 且不抛。

静态计数：`AxonHub.Api.Tests` 5485 → 5507（+22），`AxonHub.Persistence.Tests` 350 → 353（+3），
`AxonHub.Grains.Tests` 1746、`AxonHub.Llm.Tests` 1506 不变，四项目合计 9087 → 9112。
**按项目规则未运行 lint / build / test。**

台账：基线 §152、验收 §152 已写入；新增 C232（accepted：枚举输入校验文案）；C064（字段级
JSON 类型错误文案）与 C186（时区每次读取）沿用。`02-differences.md` 重新生成（232 条：
169 accepted / 55 resolved / 8 gaps），lint 通过。
