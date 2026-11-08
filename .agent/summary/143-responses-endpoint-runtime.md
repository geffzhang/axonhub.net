# §143 Responses 端点运行时解析与传输保真

日期：2026-11-08
参照：live 树 `f272549f`（端点语义来自基线后的 `94cd05b1` / #2656，见
`porting/00-reference.md` 漂移记录）：`internal/server/biz/channel_endpoint_responses.go`、
`channel_endpoint.go:455-487`、`channel_llm.go:217-620`、`internal/server/orchestrator/tester.go:130-221`、
`internal/server/gql/axonhub.resolvers.go:34-41`、`llm/transformer/openai/responses/outbound.go:168-173`、
`llm/transformer/openai/codex/outbound.go:154-159`、`llm/transformer/orchestrator/outbound.go:838-840`。

## 交付

- `ChannelEndpoints.Resolve(channelType, endpointsJson, baseUrl)` 按 live 合并默认端点与归一化用户端点；
  新增 `IsResponsesEndpoint`、`EndpointTransport`、`NormalizeEndpoints`、`DefaultEndpoints`、
  `LegacyResponsesFormat`、`PrimaryEndpointTransport`、`FindUserEndpoint`。解析只读，原始 JSON 不被改写。
- 所有读取点传入通道基址：`ModelService`、`ModelCandidateSelector`（两处）、
  `PersistentOutboundTransformer.RefreshCandidateApiFormat`、`ChannelTestService`（三处）；
  `ChannelTestService.BuildCandidateAsync` 与 `ChannelEndpointDetectService` 的候选补带
  `Endpoints` / `Settings`，否则 tester 与检测路径看不到已存端点。
- `ChannelOutboundFactory` 每次尝试选取入站格式的用户端点，把端点 BaseURL / Path / Transport 交给
  codex/fenno 的 responses 与 alpha（alpha 路径只给 codex）、openai 的 h/w/compact、decisions、
  completion、chat、anthropic、xai responses、nanogpt_responses、openai_responses（无用户端点时用
  `PrimaryEndpointTransport`）、bailian_responses 与 zenmux/zenmux_responses；`SelectUserEndpoint`
  复刻参照的原始格式别名（原始 h 归一化为 w 且没有原始 w 时，h 查不到则回退归一化 w）。
- `ResponsesOutboundTransformer.ApiFormat` 与 `CodexOutboundTransformer.ApiFormat` 在 websocket
  transport 下返回 `openai/responses-ws`；`CodexOutboundParams.EndpointPath` 传入内层 Responses 配置。
- `ApiCapabilities.CapableApiFormats(RequestTypes.Chat)` 与 `ChannelTransformOptions.IsResponsesFormat`
  接纳 w；`ChannelEndpointSelection.ForcedApiFormats` 按 `normalizeModelProtocols` 把启用的 h 改写为 w
  （settings 原文不变）。
- `ChannelTestService.ChannelTestApiFormat` 顺序改为 decisions → chat → systemone → chat 回退；
  `UsesResponsesWebSocket` 先看 w，再对 h/compact 看 transport/scheme，全部经 BaseUrl 解析。
- `ChannelValidation` 的草稿 `EndpointTransport` 把 w 格式视为 websocket，先于显式 transport。
- GraphQL `Channel.defaultEndpoints` 改走 `ChannelMapping.DefaultEndpoints(type, endpointsJson, baseUrl)`，
  与 live 解析器一致；`ChannelType` 新增 `[GraphQLIgnore] EndpointsJson`，SDL 不变。
- 不新增表或 SQL，代码生成/工具链未变动。

## 行为要点

- 端点归一化与 live 完全一致：只处理 h/w；h + 旧式 websocket transport → w（BaseURL 不变）；
  h + 显式 http 时把 wss→https / ws→http（TrimSpace 后按前缀），随后清空 transport；幂等。
- codex 的默认端点按存储意图裁剪（非 codex 则把默认 h 替换为 `legacyResponsesFormat`），
  alpha 与两个图片默认项始终保留。
- 无用户端点时，openai_responses 的 transport 由 `PrimaryEndpointTransport` 决定：首个匹配的原始
  端点优先，任一原始 w 让 h 也推断 websocket，否则看通道基址 scheme。
- 端点级 BaseURL/Path/Transport 现在真正进入出站配置；原始格式别名只在该格式没有显式端点时生效。
- 残余：非 Responses 家族仍按通道类型与通道基址分派（zai/doubao 家族 chat 的端点 Path、
  minimax/gemini/jina/typesafe 的路径、xai_subscription 覆盖、fenno 图片门、codex/fenno 的 compact
  端点存在性），通道级连接复用仍缺（C094 item 1 / C072）；写入面端点规范化仍冻结（C144/C148）。

## 测试

- `tests/AxonHub.Llm.Tests/ChannelEndpointsTests.cs`（7 方法 / 19 例）：13 行 codex 默认端点矩阵
  （HTTPS/HTTP/wss/ws 基址、显式 h/http/websocket、端点基址、w 格式、混合清单、无关覆盖；原始文档
  不被改写）、显式 http 的 wss→https 规范化与幂等、w 格式优先于 legacy http 字段、primary transport
  的三种回退、`Resolve` 合并位置、chat 能力包含 w、`FindUserEndpoint` 只取每个格式最后一个匹配。
- `tests/AxonHub.Api.Tests/ResponsesEndpointDispatchTests.cs`（9）：legacy h+websocket+path 进入
  codex 出站（ApiFormat w、端点基址与路径）、alpha 端点路径、ws 基址的 openai_responses 传输、
  chat 端点基址+路径、forced protocols 的 h→w 改写且 settings 不变、无 ws 意图时保持 h、tester
  混合表面优先 chat、`UsesResponsesWebSocket` 读取归一化端点、GraphQL `defaultEndpoints` 跟随
  codex 存储意图。

静态计数：`AxonHub.Llm.Tests` 1482 → 1501（+19），`AxonHub.Api.Tests` 5421 → 5430（+9），
四项目合计 8914 → 8942（`AxonHub.Persistence.Tests` 331、`AxonHub.Grains.Tests` 1680 不变）。
**按项目规则未运行 lint / build / test。**

台账：`00-reference.md` 新增 live 端点漂移记录；基线 §143 与验收 §143 已写入；C094 item (1) 与
C072 残余 (a) 收窄，C144/C148 标注运行时读取已跟进、写入面仍冻结；差异总数 224 条不变
（161 accepted / 54 resolved / 9 gap，C094 与 C072 仍为 gap）。
