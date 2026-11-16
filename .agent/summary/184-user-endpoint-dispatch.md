# §184 用户端点按 API format 分派出站（C094 (1) 关闭，13 例）

日期：2026-11-12
参照（冻结基线 `b8bb3421` / v1.0.0-beta11，端点的 live 语义见 §143 的漂移记录）：
`internal/server/biz/channel_llm.go` 的 `buildChannelWithOutbounds`（:236-251）、
`buildNonDefaultEndpointOutbound`（:431-616）、`newProviderChatOutbound`（:573-608）、
`providerChatEndpointUsesFamilyVersion`（:610-628）、`endpointTransport`（:263-274）与
`buildChannelWithTransformer` 的 ZenMux 空基址默认值（:695-705）。

## 背景

参照先按通道类型建主出站与默认端点表，再对每个用户端点调用
`buildNonDefaultEndpointOutbound`，以 API format 为键覆盖默认表；opencode 的两个通道类型
还会把用户端点出站再包一层会话头。端口此前只在 Responses 家族的若干分支消费端点的
BaseURL/Path/Transport，其余（zai/doubao 家族 chat、minimax 图片、gemini、jina、
typesafe、anthropic 变体、xai_subscription 覆盖、compact 等）仍按通道类型与通道基址
分派，端点的 path/base 被忽略。

## 交付

- `ChannelOutboundFactory.Create`：选中用户端点且格式非空时，改走新的
  `CreateUserEndpointOutbound`，不再进入通道类型 switch。opencode_go /
  opencode_go_anthropic 的结果再包 `OpenCodeSessionHeader.WithSessionHeader`。
- `CreateUserEndpointOutbound`（`buildNonDefaultEndpointOutbound` 的完整移植）：
  - 端点 BaseURL 优先，空则继承通道 BaseURL；ZenMux 空基址先套用
    `ZenmuxBaseUrl`（参照在端点循环前改写 c.BaseURL）。transport 由继承后的基址或
    显式 transport 推断（`ChannelEndpoints.EndpointTransport`）。
  - Command Code 的两个类型校验端点基址 HTTPS；websocket transport 仅允许 Responses
    家族（否则抛参照文案）。
  - chat：Cline 保信封；zai/zhipu/xiaomi/doubao 家族仅在显式 path 或
    `providerChatEndpointUsesFamilyVersion` 时保留家族 transformer，否则通用 OpenAI。
  - responses/ws/compact：codex/fenno 的 Response/WS 保 codex 出站（带端点 path），
    其余走 Responses（带 path/transport）。
  - alpha search：codex 保 codex 出站，其余通用 OpenAI。
  - anthropic/messages：commandcode/commandcode_anthropic → CommandCode（Bearer），
    其余 Direct（X-API-Key），均带端点 path。
  - minimax 的 image_generation → MiniMax；codex/fenno 的 image gen/edit → codex；
    其余 embedding/moderation/图片/音频/视频 → 通用 OpenAI，均带 path。
  - zenmux video 仅允许 ZenMux 类型；gemini contents/embedding（vertex 平台）、
    jina rerank/embedding、typesafe systemone 均带 path。
  - 未知 format 抛 `unsupported api_format "..."`。
- `CreateNonDefaultEndpointOutbound`（端点探测）改为调用同一分派：合成端点只带
  format、继承通道基址、path 为空。
- `CreateZaiOutbound` / `CreateDoubaoOutbound` / `CreateZenmuxOutbound` 增加可选
  baseUrl / endpointPath；删除不再使用的 `InferTransport`。

## 测试

`tests/AxonHub.Api.Tests/ChannelEndpointDispatchTests.cs` 13 例：

- zai 家族 chat：显式 path 保家族、空 path + /v1 基址回退通用 OpenAI、v4 基址保家族。
- doubao chat path、minimax 图片 path、gemini/jina/typesafe path。
- zai_anthropic 的 anthropic 端点走 Direct（X-API-Key、端点 base+path）。
- Command Code 的 anthropic 端点保持 Bearer。
- xai_subscription 的 responses 用户端点覆盖订阅包裹，走 Responses。
- opencode_go 的用户端点包会话头（X-Opencode-Session 来自 RawRequest.SessionId）。
- chat 端点 + websocket transport 被拒绝（参照文案）。

## 本轮实测

- 新增 13 例通过。
- 定向 378 例通过（Api：ResponsesEndpointDispatch / 24 个 *WiringTests /
  ChannelTestService / PersistentOutboundRetry / ChannelTransformOptions）。
- 定向 89 例通过（dispatch + zai/zenmux/detect，含 ZenMux 空基址回退改动后的复跑）。
- `AxonHub.Api.Tests` 未跑全量（本刀虽横跨通道家族，但改动集中在出站工厂；
  本轮以 378 例的跨家族接线集合替代），静态计数 6195 → 6208；四项目合计
  9870 → 9883（Llm 3122 / Persistence 376 / Grains 177 未重跑）。

## 台账

- `differences.jsonl`：C094 (1) 关闭并补记 13 例，C094 仍保留 (10)(11) 与 §146 的
  fenno / github_copilot / antigravity provider 复用残余；C096 (2)(5) 关闭，残余只剩
  (8) 的凭据文案。
- `01-baseline.md`：§143 追加 2026-11-12 更新段；Codex 残余汇总行更新。
- `03-acceptance.md`：新增 §184 段；修正 §183 的 Api 静态计数 6202 → 6195（§183
  摘要文件同步修正）。
- 269 条（196 accepted / 6 gap / 67 resolved），`diff_register.py lint` 通过，
  `02-differences.md` 已重新生成。
