# §153 视频存储设置、代理预设与 onboarding 管理面（3 查询 / 6 变更）

日期：2026-11-09
参照：`internal/server/biz/system.go:1678-1695`（VideoStorageSettings：缺失默认
false/0/1/50、读取后 `<=0` 归一）、`1697-1740`（SetVideoStorageSettings：归一化 →
Enabled 校验 → 文档写入与逐层前缀）、`internal/server/biz/system_proxy.go:17-94`
（ProxyPreset 的 omitempty、空列表、解码错误、按 URL 替换 / 追加 / 删除）、
`internal/server/biz/system_onboarding.go:15-115`（OnboardingRecord 的 omitempty、
单一 `time.Now()`、仅自动完成缺失的通道模块、`failed to get existing onboarding info`
包装）、`internal/server/biz/system_default.go:131-136`（defaultVideoStorageSettings）、
`internal/server/video_storage/worker.go:1-140`（Reschedule 接缝；worker 本体未移植）、
`internal/server/gql/system.graphql:238-275、300-390、485-500`（SDL）、
`internal/server/gql/system.resolvers.go:113-140、165-173、285-306、557-590、624-652`
（9 个根字段的薄转发与逐层前缀）。

## 背景

§152 只接入 system-settings 的第二批（brand / general / channel / 三个开关）。前端剩余的
视频存储、代理预设与 onboarding 页面依赖 3 个查询与 6 个变更，本切片补齐其全部 GraphQL
契约。

## 交付

- `src/AxonHub.Api/Settings/SystemSettingsModels.cs`：`ProxyPreset` / `OnboardingModule` /
  `OnboardingRecord` / `VideoStorageSettings` 域记录与 `SystemSettingsDefaults.VideoStorage`
  （false/0/1/50）；`SystemSettingsCodec` 新增 ProxyPreset 数组（null 根 → 空列表、null
  元素 → 零值、name/username/password 空串省略、url 恒输出）、Onboarding（字段序 +
  omitempty + RFC3339Nano）、VideoStorage（四字段全量）编解码，全部经 `GoJson.Escape`。
- `src/AxonHub.Api/Settings/SystemSettingsAdminService.cs`：新增 9 个方法——视频设置读写、
  代理预设读取 / 保存 / 删除、onboarding 读取与三个完成方法；构造器新增可选
  `IDataStorageStore?`（生产 DI 注入，既有单参数构造保持可用）。
- `src/AxonHub.Api/Management/VideoStorageRescheduleSignal.cs`：`IVideoStorageRescheduleSignal`
  接缝；生产不注册（扫描 worker 未移植，C234）。
- `src/AxonHub.Api/GraphQL/SystemSettingsTypes.cs`：VideoStorageSettings / ProxyPreset 输出
  类型、OnboardingInfo 族三个输出类型、四个输入类型与域 ↔ GraphQL 映射（时间经 GoTimeType
  输出 UTC，C233）。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：`videoStorageSettings`、`proxyPresets`、
  `onboardingInfo`；`AdminMutation.cs`：`updateVideoStorageSettings`、`saveProxyPreset`、
  `deleteProxyPreset` 与三个 `complete*Onboarding`。
- 测试假件：新增 `RecordingVideoStorageRescheduleSignal`；测试宿主注册
  `IDataStorageStore` 与重排信号，并以 `FakeDataStorageStore` 驱动视频设置的存储校验。

## 行为要点

- 视频设置读：缺失 → false / 0 / 1 / 50；解码失败 `failed to unmarshal video storage
  settings: ...`（查询不再包前缀）；扫描值 `<=0` 归一为 1 / 50。
- 视频设置写：gqlgen 直接解码为 biz 结构，缺省字段为零值（重置而非保留）；Enabled 时
  依次拒绝缺 ID（`data_storage_id is required when video storage is enabled`）、存储不存在
  （`failed to get data storage: ent: data storage not found`）、Primary 或 database
  （`video storage must use a non-database data storage`）；错误前缀为
  `failed to update video storage settings: failed to set video storage settings:
  failed to create system setting: ...`；成功后调用可选重排信号，失败不调用。
- 代理预设读：缺失或存储 `null` → `[]`；解码失败服务层 `failed to unmarshal proxy presets`，
  查询再包 `failed to get proxy presets`（双层）。
- 代理预设写：Save 按 URL 就地替换否则追加；Delete 过滤后重写（空数组写 `[]`）；服务层不
  重包 ProxyPresets / setSystemValue 错误，变更层各包一次。
- onboarding 读：缺失 → null；解码失败服务层 `failed to unmarshal onboarding info`，查询再
  包 `failed to get onboarding info`。
- onboarding 写：CompleteOnboarding 共享一个 `DateTimeOffset.Now`（根记录与自动通道模块
  时间戳相同），仅在 AutoDisableChannel 为 nil 时自动完成；两个模块完成方法只覆盖自己的
  模块；错误经 `failed to get existing onboarding info` 与变更前缀逐层叠加。

## 测试

- `tests/AxonHub.Api.Tests/AdminSystemSettingsEndpointTests.cs`（18）：视频设置默认值、
  非正扫描值归一、查询错误前缀；写入文档与归一、Enabled 校验四分支（缺 ID / 不存在 /
  Primary / database）与合法 fs、写入错误前缀且不重排；代理预设缺失 / null → `[]`、存储
  文档读取（缺省字段为空串）、查询双层错误、按 URL 追加与就地替换、删除过滤与空数组、
  Save / Delete 双层错误前缀；onboarding 完成前为 null、CompleteOnboarding 共享时间戳并
  自动完成通道模块、已有模块时间戳保留、模块完成只写自身且互不覆盖、查询 / 变更逐层错误
  前缀。

静态计数：`AxonHub.Api.Tests` 5507 → 5525（+18），`AxonHub.Grains.Tests` 1746、
`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 353 不变，四项目合计 9112 → 9130。
**按项目规则未运行 lint / build / test。**

台账：基线 §153、验收 §153 已写入；新增 C233（accepted：onboarding 时间戳输出 UTC 与非法
`completed_at` 文案）与 C234（gap：视频存储扫描 worker 未移植）。`system_proxy_presets`
已在备份键清单中，无需改动。`02-differences.md` 重新生成（234 条：170 accepted /
55 resolved / 9 gaps），lint 通过。
