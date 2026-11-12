# §191 playground chat（C272 闭合，C278 新增，61 例）

日期：2026-11-12
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/api/playground.go:74-331`、
`llm/transformer/aisdk/datastream.go`、`convert_request.go`、`convert_stream.go`、
`model.go`、`steam.go`、`internal/server/routes.go:134-140`。

## 背景

§190 后 C272 只剩一条 `POST /admin/playground/chat`。该路由位于 admin 组
（AdminJwtAuthMiddleware + ProjectIdMiddleware），入站是 aisdk DataStream
transformer，出站要求 AI SDK data stream 协议。本切片接通端点、首次编译
aisdk transformer 五文件，并以行为测试锁定 playground 信封。

## 交付

- `src/AxonHub.Api/Endpoints/PlaygroundEndpoint.cs`：`HandleAsync` 复刻
  playground.go:213-331——ReadHTTPRequest 失败直接 400（不经 HandleError）；
  `channel_id` / `project_id` 取 query 首值、为空回退 `X-Channel-ID` /
  `X-Project-ID`；project 解析失败 → `Invalid project ID: <err>`；覆盖 JWT 身份
  的 ProjectId；channel 解析失败 → 400 err.Error()；成功则
  `WithChannelSelector(new SpecifiedChannelSelector(...))`。处理器以
  `WithInbound(new AiSdkDataStreamTransformer())` 派生；请求行
  Source=playground、UserId=JWT 用户、ApiKeyId=null。`HandleError` 五级分类
  （QuotaExhausted→503、HttpClientError→上游 message 或 StatusText、
  InvalidRequest→400 "Bad Request"、ResponseError→数字 code（GoAtoi）或
  status、兜底 500）。流式响应写 `text/event-stream`、`no-cache`、
  `keep-alive`、`Access-Control-Allow-Origin: *`、
  `X-Vercel-AI-Data-Stream: v1` 五头，200，默认 SSE writer（FormatStreamError，
  无 upstream policy，结尾 `data:[DONE]`）。`tryExtractUpstreamErrorMessage`
  复刻三种 JSON 形状；`GoAtoi` 复刻 strconv.Atoi（语法错 0、溢出钳制）。
- `src/AxonHub.Llm/Transformer/AiSdk/`：`AiSdkDataStreamTransformer`（校验 +
  请求转换 + 流转换 + UIMessage 聚合 + 错误体）、`AiSdkConvertRequest`、
  `AiSdkConvertStream`（start / start-step / text-* / reasoning-* /
  tool-input-* / finish-step / finish 状态机）、`AiSdkModels`、`AiSdkJson`。
  修正 TransformError 参数名（CA1725）与 tool-call 累加（CA1854）。
- `src/AxonHub.Api/Orchestration/SpecifiedChannelCandidateFactory.cs` /
  `SpecifiedChannelSelector.cs`：把 ChannelTestService 里的指定通道候选构建与
  通道读取抽出共享，playground 与通道测试复用。
- `ChatCompletionOrchestrator.WithChannelSelector`：不可变派生新编排器；
  `RequestIdentity.SourcePlayground` / `UserId`；请求行仅在 playground 来源写
  user_id；`Program.cs` 挂 MapPost 与 admin 组 UseWhen 门禁。

## 测试

- `tests/AxonHub.Llm.Tests/AiSdkDataStreamTransformerTests.cs` 21 例：请求转换
  6（UI messages/tools、根 null、坏 JSON、非 object 根、未知 role、非流式拒绝）、
  流序列 4（text、reasoning→text、tool-input、[DONE]）、聚合 5（UIMessage、
  parts null、omitempty、跳过非 JSON、TransformStream 输出）、错误 6
  （nil/HttpClientError/InvalidRequest/ResponseError/普通异常/不转义）。
- `tests/AxonHub.Api.Tests/PlaygroundEndpointTests.cs` 18 个方法 / 40 例：
  请求级 4（空 body→400 Bad Request、坏 project/channel、未知通道 500）、
  流式成功 1（五头、事件序列、[DONE]、请求行身份）、header 优先级 1、上游 429
  1、HandleError 8、tryExtract 10、GoAtoi 15（Theory 逐例计数）。
- 实测：Llm 21 通过 / 0 失败；Api 40 通过 / 0 失败（2026-11-12）；
  `AxonHub.Api.Tests` 未跑全量。

## 台账

C272 → resolved；新增 C278（accepted，GoAtoi 钳制、TransformError 不转义、
未知 role 异常类型、null 字段、中间件等价、随机事件 ID、每请求编排器派生、
请求行身份、LiveStreamRegistry 仍见 C192）。`diff_register.py lint` 通过；
278 条（203 accepted / 5 gap / 70 resolved）。
