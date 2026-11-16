# §164 多 API key 策略分派（apiKeyStrategy 六策略与共享选择状态，57 例）

日期：2026-11-10
参照（漂移基线 `f272549f`；冻结基线 `b8bb3421` 只有 trace-sticky 规则）：
`internal/server/biz/channel_apikey_strategies.go:1-344`（策略名、`channelAPIKeyStrategy` /
`channelAPIKeySwitchAfter`、`strategyKeepsSelectionState`、`newMultiKeyProvider`、`selectableKeys` /
`positionOf` / `firstSelectableFrom`、六种 provider）、`channel_apikey_state.go:1-354`
（共享每通道状态、rr 计数器、trace 粘性 LRU 1024、`isServing`、`onAPIKeySuccess`、
`forgetAPIKeySelectionState`）、`channel_llm.go:169-184,768`（多 key 分派条件与状态挂载）、
`channel_metrics.go:515-535` 与 `internal/server/orchestrator/performance.go:75-85`（成功回报与
`PerformanceRecord.APIKey`）、`channel.go:296,1331` / `channel_bulk.go:356`（发布与删除遗忘）、
`channel_apikey_strategies_test.go`（43 个 Test 函数，含表驱动子用例）。

## 背景

多 key 策略是基线后的新特性（#2548）。参照按 `ChannelSettings.APIKeyStrategy` 分派
sticky（默认，空值 / 未知值同义）、random、round_robin、round_robin_success、priority、fixed，
并把游标、rr 计数器与 trace→key 记忆保存在跨通道快照重建存活的每通道状态里；启用 key > 1
或配置 key > 1 就构造 multi-key provider。端口此前只有基线的 trace-sticky 规则。

## 交付

- `src/AxonHub.Api/Orchestration/ChannelApiKeyStrategies.cs`（新）：`StrategyOf` /
  `SwitchAfterOf` / `NormalizeSwitchAfter` / `KeepsSelectionState` / `Select` /
  `SelectableKeys` / `PositionOf` / `FirstSelectableFrom` / `SnapshotOf` / `Publish`。
- `src/AxonHub.Api/Orchestration/ChannelApiKeySelection.cs`（新）：`ChannelKeySnapshot`、
  `ChannelApiKeyState`（sticky LRU 1024、rr 计数器、fixed / rr_success 游标、成功计数）与
  `ChannelApiKeySelectionRegistry`（`StateFor` / `Publish` / `Forget` / `OnApiKeySuccess`）。
- `src/AxonHub.Api/Orchestration/ChannelApiKeyProvider.cs`：`ForSelection`（已选 key + 下标）；
  `SelectTraceSticky` 保留给单键 / 遗留路径。
- `src/AxonHub.Api/Orchestration/ChannelOutboundFactory.cs`：构建时按 settings 分派；多 key
  条件与参照一致；非默认端点同一分派（trace 为 null）；带 key 变换器回填
  `ChannelApiKeyIndex`，Gemini 补 `IApiKeyProvenance`。
- `src/AxonHub.Api/Orchestration/ChannelMetricsMiddleware.cs`：成功分支先
  `OnApiKeySuccess(channelId, apiKey)` 再 `RecordSuccess`。
- `src/AxonHub.Api/Orchestration/GrainEnabledChannels.cs`：`ListEnabledAsync` 为每个通道发布
  serving 快照（唯一发布点）。
- `src/AxonHub.Api/Management/ChannelLifecycleService.cs`：删除 / 批量删除后 `Forget(id)`。
- `src/AxonHub.Api/Program.cs`：registry 单例；orchestrator 与 endpoint detect 透传；
  `VideoService` 故意不接 registry（状态查询不读 key，避免轮询推进 round_robin）。

## 行为要点

- settings 或字段为 nil → `""`；空串等同 sticky；switchAfter 缺失 → 1、≥1 原样、<1 归一 1。
- 只有 `""` / sticky / round_robin / round_robin_success / fixed 注册共享状态。
- sticky：单键直用；有 trace 时查 LRU，命中且仍在启用集则复用，否则 rendezvous 并记忆；
  无 trace 时启用集随机。
- round_robin：`(rrCounter / per) % len(enabled)`，每次 Get 递增，与结果无关。
- priority / fixed / rr_success 的游标规则逐分支复刻；fixed 与 rr_success 只在 serving
  代际写回游标，stale 提供者只读不回退。
- rr_success 的成功回报只接受策略仍为 rr_success、key 等于当前游标、通道存在状态；
  达到 `max(per,1)` 后从 serving 快照的游标位置 +1 前向选择并清零。
- 启用 key 为空时回落 `Credentials.APIKeys[0]`；override key 恒静态。

## 测试

`tests/AxonHub.Api.Tests/ChannelApiKeyStrategiesTests.cs`（50 Fact + 2 Theory / 7 InlineData，
共 57 例）：

- A 分派 10：null / 空 / sticky / 未知回落 trace-sticky；六策略分派；5 种记忆策略注册、
  2 种无状态不注册。
- B priority 3；C fixed 14（含 own snapshot、stale 不回退、数组前插、删除遗忘）；
  D rr_success 12（含 per 归一、忽略其他 key / 策略 / 通道、禁用重置、own snapshot、stale）；
  E RecordPerformance 接线 3；F 并发 2；G 跨重建 6；H 发布与工厂 7（发布代际喂给成功回报、
  工厂按策略轮转、单 key 静态、仅一个启用但配置多 key 仍走策略、override 优先、sticky trace
  跨重建、Gemini key 与下标）。

静态计数：`AxonHub.Api.Tests` 5844 → 5901（+57），`AxonHub.Persistence.Tests` 363、
`AxonHub.Grains.Tests` 1746、`AxonHub.Llm.Tests` 1512 不变，四项目合计 9465 → 9522。
**按项目规则未运行 lint / build / test。**

## 台账

C247 由 gap 更新为 resolved（六种策略、共享状态、成功回报、发布与删除遗忘均已接线）。
新增 C251（accepted）：`isServing` 以代际内容等价（数组顺序 + 禁用集）替代参照的通道指针
相等，发布点随之改为每次启用列表读取；内容相同的代际写入值一致，仅 A→B→A 重发布时 stale
提供者的写回时机不同，结果不可观察。`02-differences.md` 重新生成（251 条：181 accepted /
13 gaps / 57 resolved），lint 通过。
