# §156 缓存诊断导出与清缓存（1 查询 / 1 变更）

日期：2026-11-09
参照：`internal/server/gql/channel_model_cache_diagnostics.go:22-123`（导出结构：根 /
database / cache / 关联缓存条目 / enabled 缓存快照 / DB 与缓存通道 / settings /
modelMapping / modelEntry / 模型视图 / 候选预览的 JSON 名、字段序与 omitempty）、
`125-134`（normalizeDiagnosticsTargets：`lo.Uniq` + `slices.Sort`）、`136-179`
（buildChannelModelCacheDiagnosticsExport：channels 按 ordering_weight 降序再 id 升序、
models 按 id 升序、两次查询后读 `time.Now().UTC()`、`GetEnabledChannels()`）、
`181-203`（buildCacheDiagnostics / buildAssociationCacheSnapshot）、`205-249`
（enabled 快照与 DB / 缓存通道快照）、`251-269`（settings 快照）、`271-286`
（buildSortedEntries）、`288-333`（buildModelDiagnostics：仅 enabled 选择、错误静默、
settings nil 的关联形状）、`335-350`（buildCandidatePreview）、`352-359`
（json.MarshalIndent 与 `failed to marshal diagnostics export`）、
`internal/server/gql/system.graphql:459-509`（SDL）、
`internal/server/gql/system.resolvers.go:690-726`（getCacheDiagnostics）、`383-408`
（clearCache）、`internal/server/gql/resolver.go:22-23`（ErrNotOwner）、
`internal/server/orchestrator/diagnostics.go:12-57`（ReadAssociationCache）、
`internal/server/biz/channel.go:1292-1294` 与 `channel_llm.go:132-143`
（cachedEnabledAPIKeys 的装载期计算）。

## 背景

管理面的缓存诊断页依赖 `getCacheDiagnostics` 导出可下载的 JSON 快照，
`clearCache` 用于强制重载启用通道缓存。§152–§155 已覆盖 system-settings 的
webhook / 版本 / 目录批次，本切片补齐这两个根字段及其导出管线。

## 交付

- `src/AxonHub.Api/Diagnostics/DiagnosticsTargets.cs`：`DiagnosticsTarget`
  （`CHANNEL_CACHE` / `MODEL_ASSOCIATION_CACHE`）与 `Normalize`（去重后按枚举名排序，
  空输入回落 `CHANNEL_CACHE`）。
- `src/AxonHub.Api/Diagnostics/CacheDiagnosticsExport.cs`：导出根结构与逐节点构建
  （字段序、omitempty、nil vs 空数组、通道 / 模型排序、settings / modelEntries /
  候选预览、条件值与关联的 Go 兼容序列化）。
- `src/AxonHub.Api/Diagnostics/GoJsonIndent.cs`：`json.MarshalIndent` 的缩进遍历
  （嵌套容器换行、空容器内联、空白跳过、字符串原样）。
- `src/AxonHub.Api/Diagnostics/CacheDiagnosticsService.cs`：两次查询 → exportedAt →
  快照 → 构建 → 缩进 → 文件名（第二次时钟读取），查询 / 构建 / 序列化错误前缀与参照一致。
- `src/AxonHub.Api/Diagnostics/CacheDiagnosticsSelector.cs`：owner-bypass 候选选择器，
  仅供诊断服务构造。
- `src/AxonHub.Api/GraphQL/DiagnosticsTypes.cs`：GraphQL 枚举、输入与两个 payload 类型。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：`getCacheDiagnostics`（owner 门、构建前缀、
  文件名与 content）；`AdminMutation.cs`：`clearCache`（owner 门、targets 归一化、
  逐目标重载与错误链）。
- `src/AxonHub.Api/Program.cs`：注册 `IEnabledChannelCacheSnapshotProvider`、
  `ICacheDiagnosticsCandidateSelector`、`CacheDiagnosticsService` 与
  `IEnabledChannelsCacheRefresher`。
- 测试侧：`CacheDiagnosticsTestDoubles.cs`（快照提供方 / 候选选择器 / 缓存刷新器）、
  `FakeChannelAdminStore.ListAllFailure`（无界 ListAllAsync 的失败注入）。

## 行为要点

- getCacheDiagnostics：`input` 可为 null；targets 归一化；content 为两空格缩进的
  JSON 文本；fileName 为 `axonhub-channel-model-cache-diagnostics-<时间戳>.json`，
  时间戳来自第二次时钟读取，可与 exportedAt 跨秒。
- 导出形状：根字段序 exportedAt / targets / database / cache；database 为
  channels / models；cache 为 enabledChannelCache / modelAssociationCache；
  `lo.Map(nil)` 得到 `[]`，`append([]T(nil), empty...)` 得到 `null`；settings 为
  nil 时只输出零值的 hide / lowercase 字段。
- 模型视图：仅 enabled 行选择候选，选择失败静默丢弃；非 enabled 行预览 `[]`；
  settings 为 nil → associationCount 0、associations null；预览保留
  channelId / channelName / priority 与逐模型 requestModel / actualModel / source。
- 时钟与计数：exportedAt 在两次查询后生成并复用为 capturedAtUtc；enabledApiKeyCount
  读通道装载时的启用 key 缓存，disabledApiKeyCount 为解码后数组长度（含已到期项）。
- 编码：嵌套容器换行、空容器内联；条件值整数定点、浮点 Go 格式（[1e-6,1e21) 定点，
  其余指数）、溢出浮点回落原始文本、map 键排序。
- clearCache：targets 缺省 / 空 → `CHANNEL_CACHE`；强制重载，失败链
  `failed to clear cache for CHANNEL_CACHE: failed to reload enabled channels cache: ...`；
  成功 `cache cleared successfully`。
- owner 门：两个字段均返回 `permission denied: owner access required`，非 owner 不会
  触发重载。

## 测试

- `tests/AxonHub.Api.Tests/CacheDiagnosticsTests.cs`（16）：空导出逐字节 golden；
  targets 归一化；DB 通道排序与字段形状；模型视图字段序与关联的 `{}` / `null` /
  `[]` 变体；关联字段序；条件值 Go 数字与键序；候选预览 / 非 enabled 不选择 /
  选择失败；enabled 快照的 version / 状态 / 计数 / settings / modelEntries；
  两次时钟读取与到期 key 计数；查询 / 畸形 settings / 快照失败的构建前缀。
- `tests/AxonHub.Api.Tests/GoJsonIndentTests.cs`（4）：嵌套换行、空容器内联、
  字符串原样、token 间空白丢弃。
- `tests/AxonHub.Api.Tests/AdminDiagnosticsEndpointTests.cs`（6）：两个字段的 owner 门、
  owner 查询的 fileName / targets / content、默认 target、clearCache 成功 payload 与
  重载计数、重载失败错误链。

静态计数：`AxonHub.Api.Tests` 5659 → 5685（+26），`AxonHub.Grains.Tests` 1746、
`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 353 不变，四项目合计 9264 → 9290。
**按项目规则未运行 lint / build / test。**

台账：基线 §156、验收 §156 已写入；新增 C239（accepted：owner-bypass 候选预览、
`modelAssociationCache` 恒为 `[]`）与 C240（accepted：两次时钟读取、导出时重算启用
key 计数、clearCache 重载不可取消）。`02-differences.md` 重新生成（240 条：
175 accepted / 55 resolved / 10 gaps），lint 通过。
