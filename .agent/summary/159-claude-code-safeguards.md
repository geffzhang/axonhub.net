# §159 Claude Code safeguards 设置（GraphQL 3 根字段，24 例）

日期：2026-11-09
参照（post-baseline 漂移，按工作仓库 `f272549f`）：`internal/server/biz/system_claude_code.go`
（设置读取 / 写入 / 重置）、`internal/server/safeguards/evaluator.go:17-33`
（DefaultTimeout / MaxTokens / transcriptBudget / DefaultPolicy）、`conf/conf.go:257-264`
（safeguards 默认配置）、`internal/server/gql/system.graphql:542-573`（类型与三个根字段）、
`internal/server/gql/system.resolvers.go:445-455,804-807`（resolver 直接委托 service）。

## 背景

Claude Code safeguards 是基线后新增的整套特性：冻结基线 `b8bb3421` 既没有
`internal/server/safeguards` 包，也没有 `claudeCodeSafeguards` schema。前端的管理设置页
已经调用 `claudeCodeSafeguardsSettings` / `updateClaudeCodeSafeguardsSettings` /
`resetClaudeCodeSafeguardsSettings` 三个根字段，本切片先接设置面（默认值、校验、持久化、
权限与 GraphQL），运行时评审登记为 C243。

## 交付

- `src/AxonHub.Api/Settings/ClaudeCodeSafeguardsSettingsService.cs`：
  `ClaudeCodeSafeguardsServerDefaults`、`ClaudeCodeSafeguardsConfig`（边界秒值）、
  `ClaudeCodeSafeguardsRuntimeConfig`（纳秒）、`ClaudeCodeSafeguardsSettings`、
  `ClaudeCodeSafeguardsStoredConfig`、`ClaudeCodeSafeguardsCodec` 与
  `IClaudeCodeSafeguardsSettingsService` / `ClaudeCodeSafeguardsSettingsService`。
- `src/AxonHub.Api/Safeguards/SafeguardsDefaults.cs`：`DefaultTimeoutNanoseconds`（30s）、
  `MaxTokens`（4096）、`TranscriptCharBudget`（24000）与 `DefaultPolicy` 全文（由
  `evaluator.go` 生成，逐字节一致）。
- `src/AxonHub.Api/GraphQL/ClaudeCodeSafeguardsTypes.cs`：
  `ClaudeCodeSafeguardsConfigType` / `ClaudeCodeSafeguardsSettingsType` 输出类型、
  `UpdateClaudeCodeSafeguardsInput`（全部非空字段）与 `ClaudeCodeSafeguardsMapping`。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs` / `AdminMutation.cs`：三个根字段。
- `src/AxonHub.Api/Program.cs`：注册 server defaults 与 service。

## 行为要点

- 无行或字面量 `"null"`：`usingServerDefaults: true`，classifierModel `""`、policy `""`、
  fallbackModels `[]`、escalateOnDenial false、attemptTimeoutSeconds 0、timeoutSeconds 30、
  maxTokens 4096、transcriptCharBudget 24000、defaultPolicy 为内置策略全文。
- YAML 服务器默认值的 timeout / maxTokens / transcriptCharBudget 为零时分别替换为
  30s / 4096 / 24000；其余字段原样暴露。
- 存储值直接读 `systems` 行（不经系统缓存，避免陈旧评审策略），解码后按 runtimeConfig
  校验并返回规整值（model / fallback 去空白），`usingServerDefaults: false`。
- 损坏存储值：`decode Claude Code safeguards settings: %w`（不静默禁用评审）；空对象 `{}`
  能解码但校验失败。
- 校验顺序与文案：timeout → attempt → maxTokens → transcriptCharBudget → fallback →
  「至少一纳秒」。NaN / Inf 先于范围判断；秒值按 `time.Duration(seconds*1e9)` 截断，
  0 < seconds < 1e-9 的 timeout 落到「at least one nanosecond」。
- 写入按 Go 字段顺序持久化，空 fallback 为 `[]` 而非 `null`；秒值以纳秒截断后回写。
- 重置写入字面量 `"null"`。
- 读取需 `read_settings`，写入 / 重置需 `write_settings`；运行时配置读取不加权限
  （`authz.RunWithSystemBypass` 的系统内部路径，端口直接读库）。
- 失败包装：读 `read Claude Code safeguards settings: %w`，写
  `failed to create system setting: %w`。

## 测试

`tests/AxonHub.Api.Tests/ClaudeCodeSafeguardsSettingsServiceTests.cs`（19 Fact）：缺行默认值、
零值修补、服务器默认值原样、存储值规整、`"null"`、空对象校验失败、损坏值 decode 前缀、
非法限额、Go 形状 JSON（字段顺序、`[]`、秒截断）、15 条校验文案、失败不落库、重置
`"null"`、三种 scope 组合与匿名、运行时读取免权限、读前缀、写前缀。

`tests/AxonHub.Api.Tests/AdminClaudeCodeSafeguardsEndpointTests.cs`（5 Fact）：GraphQL
默认值、更新-读取-重置往返（存储 JSON 精确比对）、VALIDATION_FAILED 信封、`read_settings`
门、`write_settings` 门。

静态计数：`AxonHub.Api.Tests` 5727 → 5751（+24），`AxonHub.Persistence.Tests` 359、
`AxonHub.Grains.Tests` 1746、`AxonHub.Llm.Tests` 1506 不变，四项目合计 9338 → 9362。
**按项目规则未运行 lint / build / test。**

## 台账

新增 C243（gap）：评审运行时（`internal/server/safeguards` 的 evaluator / response 与
`internal/server/api/anthropic.go` 的 reviewer 接线）未移植；设置面已完整并有 24 例锁定。
服务器默认 `classifierModel` 为空时评审关闭，默认部署的 /v1 行为不受影响。
`02-differences.md` 重新生成（243 条：176 accepted / 56 resolved / 11 gaps），lint 通过。
