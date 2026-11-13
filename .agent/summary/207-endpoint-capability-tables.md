# §207 端点表与能力表收口审计 + 台账对账（3 例 Llm + 1 例 GraphQL）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11 与 live 树 `f272549f`）：`internal/server/biz/channel_endpoint.go`
的 `defaultEndpointsForChannelType`（默认端点表）、`llm/api_capability.go` 的
`CapableAPIFormats` / `RequestTypeForModelType`（能力表），以及 `internal/ent/channel/channel.go`
的通道类型常量。

## 背景

C097 是台账中最后一条 `status: gap` 的记录。逐项核对后，其第 (5) 项所列的「能力表未建模、
端点记录未移植、其余 provider 通道未接线」已在此前的切片中全部落地；第 (6) modelname 与
第 (7) apiKeyOverride 已分别由 §181、§182 关闭。本刀因此转为收口审计：把两张表与参照逐项
机械比对，修正端口的多余条目，并用全表测试把等价性固定下来，最后对账所有引用这两张表的
台账记录。

## 审计结果

- 默认端点表：参照 74 个通道类型，端口 74 个，逐类型格式序列全部一致。包含
  `cloudflare`（chat + embeddings）、`codex`（responses / responses-ws / alpha_search /
  image_generation / image_edit）、`ollama`（ollama/chat）、`jina`（rerank + embeddings）、
  `typesafe`（typesafe/systemone）与 `zenmux_gemini`（gemini contents + embeddings）。
- 能力表：14 个请求类型全部一致（chat 集含 openai/responses-ws；compact / completion /
  embedding / moderation / alpha_search / decisions / image / rerank / video / speech /
  transcription / translation / systemone 各自的集合）。
- 发现并修复一处内部不一致：端口曾为 `opencode_go_anthropic` 合成
  `anthropic/messages` 默认端点，而参照的 `defaultEndpointsForChannelType` 没有该条目
  （其出站来自通道类型分派，`PrimaryApiFormat` 也按参照返回 null）。这导致 GraphQL
  `defaultEndpoints` 对多显示一项。已删除该条目并加注释。

## 交付

- `src/AxonHub.Llm/Model/ChannelEndpoints.cs`
  - 删除 `["opencode_go_anthropic"] = AnthropicDefaultEndpoints`，注释指向
    `channel_endpoint.go` 与 `PrimaryApiFormat` 的同构空洞。
- `tests/AxonHub.Llm.Tests/ChannelEndpointsTests.cs`（新增 3 例）
  - `DefaultEndpointTableMatchesTheReferenceForEveryChannelType`：74 项有序全表比对，
    并断言 `opencode_go_anthropic` 与未知类型为空。
  - `CapableApiFormatsMatchesTheReferenceForEveryRequestType`：14 组集合比对（顺序无关），
    并断言未知请求类型返回 null。
  - `RequestTypeForModelTypeMapsTheConfiguredModelTypes`：六个模型类型映射与两种 systemone
    拼写，未知输入返回空串。
- `tests/AxonHub.Api.Tests/AdminChannelsEndpointTests.cs`
  - `DefaultEndpointsExposeTheLiveTables` 增加 `opencode_go_anthropic`（name "OpenCode"）
    种子与空 `defaultEndpoints` 断言。

## 测试

- `dotnet test tests/AxonHub.Llm.Tests --filter FullyQualifiedName~ChannelEndpointsTests`：
  22 通过（含 3 个新用例）。
- `dotnet test tests/AxonHub.Api.Tests --filter FullyQualifiedName~AdminChannelsEndpointTests`：
  45 通过。
- 未跑 Api 全量回归（用户此前确认全量 Api 回归耗时过长）。

## 台账

C097 由 `gap` 转 `resolved`，`what` / `cause` / `decision` / `test` 更新为 §207 口径；
C061 / C080 / C086 / C087 / C096 / C098 / C099 / C100 / C101 / C102 / C105 / C106 / C150 的
端点/能力表表述同步对账（已关闭项注明 §207，仍开放项保留原文）。`01-baseline.md` /
`03-acceptance.md` 已插入 §207 段落并更新相关验收行；lint 通过，`02-differences.md` 已重新
生成；283 条（205 accepted / 0 gap / 78 resolved）。
