# §163 updateChannelSettings（通道设置局部合并变更，25 例）

日期：2026-11-10
参照（漂移基线 `f272549f`；冻结基线 `b8bb3421` 无此成员）：
`internal/server/gql/axonhub.graphql:331-352`（`UpdateChannelSettingsInput`）、`:1011`
（根字段）、`internal/server/gql/axonhub.resolvers.go:222-224`（resolver）、
`internal/server/biz/channel_settings.go:17-50,52-127,129-141,143-195`（输入、`Apply`、
`withChannelExpectedVersion`、8 次合并重试）、`internal/server/biz/channel_settings_test.go`、
`internal/server/biz/channel.go:900-927,1110-1114,1194-1197`（expected-version 守卫）、
`internal/server/biz/errors.go:27`（`ErrChannelUpdatedConcurrently`）、
`internal/objects/channel.go:155-288`（`ChannelSettings` 字段与 omitempty 标签）。

## 背景

前端通道编辑页以 `UpdateChannelSettings` 对设置做局部变更（channels.ts 的
`useUpdateChannelSettings`），只需传被改动的字段；端口此前只有整块 `updateChannel`。
本切片按 live 补 `updateChannelSettings`：读取存储设置与 `updated_at`，在内存中合并
patch，再走完整的 `updateChannel` 写入，并以合并读取的版本做乐观锁，冲突时重新读取并
重新合并。

## 交付

- `src/AxonHub.Persistence/Stores/ChannelWriteStore.cs`：`ChannelSettingsVersion`
  记录与 `IChannelWriteStore.GetSettingsVersionAsync`（只投影 settings / updated_at，
  软删过滤，错误前缀 `failed to load channel settings: `）。
- `src/AxonHub.Api/Management/ChannelWriteModels.cs`：`UpdateChannelSettingsCommand`
  （20 字段）与 `Apply`（浅拷贝存储设置后逐字段 presence 替换）；
  `UpdateChannelCommand.ExpectedUpdatedAt`。
- `src/AxonHub.Api/Management/ChannelLifecycleService.cs`：`UpdateSettingsAsync`
  （至多 8 次：读取 → 解析 → `Apply` → `NormalizeSettings` → `UpdateAsync`，仅
  `ChannelUpdatedConcurrentlyException` 重试）；`UpdateAsync` 的守卫决策纳入
  `ExpectedUpdatedAt`，并以调用方版本覆盖第二次读取的版本；冲突改抛类型化异常
  （`SaveEndpointsAsync` 同步改用）。
- `src/AxonHub.Api/Management/ChannelUpdatedConcurrentlyException.cs`（新）。
- `src/AxonHub.Api/GraphQL/ChannelInputTypes.cs`：`UpdateChannelSettingsInput`。
- `src/AxonHub.Api/GraphQL/ChannelInputMapping.cs`：`ToCommand`（null = absent，
  空列表 = 清空；`quotaRoutingMode` INHERIT → `""`）；抽出 `ToProviderQuota` 复用。
- `src/AxonHub.Api/GraphQL/AdminMutation.cs`：`updateChannelSettings` 根字段。

## 行为要点

- patch 为 null → 保持存储设置；base 为 null → 零值设置；非 nil 标量 / 对象替换；
  nil 切片保留存储值；显式空切片清空。
- 守卫使用合并读取时的 `updated_at`；并发提交使写入失败，循环重新读取并重新合并，
  第 8 次仍冲突返回 `channel was updated concurrently; retry the operation`。
- 合并值经完整 `UpdateChannel`：设置校验、Command Code cookie 归一化、配额失效钩子
  与启用通道刷新均照常执行；只写 settings 列。
- 序列化按 Go tags：非 omitempty 空值恒出现（含 `autoTrimedModelPrefixes:null`、零值
  `transformOptions`），omitempty 空值写入后消失；立即响应等于存储 JSON 的读回。
- 缺失 / 软删通道与损坏 settings 的读取失败保持 `failed to load channel settings: `
  前缀；输入 GUID 不校验 type。

## 测试

- `tests/AxonHub.Api.Tests/ChannelSettingsMergeTests.cs`（8 Fact）：`Apply` 的
  presence 表、null base、apiKeyStrategy 对、替换与清空、每次返回新 draft。
- `tests/AxonHub.Api.Tests/AdminChannelSettingsEndpointTests.cs`（16 Fact）：前端形状
  与未触碰字段保留、空 patch 的逐字节 Go JSON、非 omitempty 空列存 `[]` / omitempty
  空列丢弃、确定性并发插入后的重读重合并、8 次冲突上限、UpdateChannel 校验器、
  Command Code providerQuota 保留、present-empty apiKeyStrategy（C250）、INHERIT
  清除、缺失通道 / 读取失败 / 损坏 settings / 非法 GID 文案、GUID type 不校验、
  变更与 20 个输入字段 introspect。
- `tests/AxonHub.Persistence.Tests/ChannelWriteStoreTests.cs`（+1 Fact）：
  settings / updated_at 投影，软删与缺失返回 null。
- `tests/AxonHub.Api.Tests/AdminChannelLifecycleEndpointTests.cs`：假存储补
  `GetSettingsVersionAsync`、`BeforeUpdate`、`UpdateAttempts` 与读取失败注入。

静态计数：`AxonHub.Api.Tests` 5820 → 5844（+24），`AxonHub.Persistence.Tests` 362 → 363
（+1），`AxonHub.Grains.Tests` 1746、`AxonHub.Llm.Tests` 1512 不变，四项目合计
9440 → 9465。**按项目规则未运行 lint / build / test。**

## 台账

C144 更新：live 的 `channelExpectedVersionKey` 已由本节接入本路径（写入面的
`normalizeModelProtocols` / `NormalizeEndpoints` 仍未采用）。新增 C250（accepted）：
`apiKeyStrategy` 的 present-empty 指针在端口折叠为缺失（Go 经 omitempty 保留 `""`），
覆盖 parse / 完整更新 / 合并三条路径。`02-differences.md` 重新生成（250 条：
180 accepted / 14 gaps / 56 resolved），lint 通过。
