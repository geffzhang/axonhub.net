# §217 Responses transport adaptation / identity preservation（C290）

## 背景

§215 / §216（C288 / C289）完成了 Responses continuation 的 store、lane 与 verify，但 pass-through 仍按
基线 b8bb3421 的语义工作：只有入站格式与出站格式相同且通道开启 pass-through 时才原样重放。live
f272549f 把它拆成两个独立机制：

- **transport adaptation**：入站与出站都是 Responses 但传输不同（HTTP ↔ WebSocket）。provider 的
  item ID、未知字段与原始 item 字节不能经统一模型往返，必须直接把客户端 body 适配成目标传输的
  线路形状，再把 provider 响应适配回来；
- **identity preservation**：入站与出站传输相同。通道可能把 `model` 改写成上游模型名，响应必须把
  `model` / `response.model` 改回客户端请求的模型，同时保持其余字节不变。

两者都独立于 `isPassThroughEnabled`，因此 capture / apply 的门控从单一 pass-through 扩成
`passThrough || adaptation || identity`。

## 参照行为

- **格式判定**（protocol_codec.go）：`IsResponsesFormat` 仅认 `openai/responses` 与
  `openai/responses-ws`；compact 与 native Images 既不算来源也不算目标。
- **AdaptRequestBody**：双方都必须是 Responses；先解码 `map[string]json.RawMessage`，再解码
  `{model,type,generate}` 信封；`model` 必填；非空 `type` 仅当来源是 WS 且等于 `response.create`，
  否则 `UnsupportedConversion("response.create is required")`；目标是 HTTP 且 `generate=false` 时
  `UnsupportedConversion("HTTP cannot perform WebSocket warmup")`；随后删除 `type` /
  `stream_id` / `generate`，目标是 WS 时补 `type=response.create`，原 `generate=false` 再补
  `generate=false`；最终 marshal 按 ordinal 键排序，raw 值 compact 且 HTML 转义。
- **AdaptResponseBody**：要求 `id` 非空，返回字节克隆；**AdaptStreamEvent**：事件 `type` 为空或与
  data 中的 `type` 不一致即报错，`response.completed` 必须带 `status=completed` 且 `id` 非空的
  response；克隆 `Type` / `Data` / `Headers`。
- **preserveResponsesModel**：客户端模型取自 `LlmRequest.RawRequest`（空则 state 的 RawRequest）
  的 body `model`；对 `model` 与 `response.model` 两个路径，只在 `gjson.Exists()` 时
  `sjson.SetBytes`，任一失败或客户端模型为空都返回原文副本。
- **接线顺序**（orchestrator.go:279-355）：`applyPassThroughRequestBody`（先写
  `RawProviderRequest`，门控 passThrough || adaptation）→ `applyResponsesTransportRequestBody` →
  Codex header 重放 → `applyResponsesContinuationBody` → override → user-agent → capture / apply。
  `applyPassThroughResponse` 在 passThrough || identity 时返回 raw 的浅拷贝（identity 打 model
  补丁），仅 adaptation 时 `AdaptResponseBody` + model 补丁并继承 `transformed.Request`；
  `applyPassThroughStream` 先包 transport（source=`RawProviderRequest.APIFormat`，
  target=`LlmRequest.APIFormat`）再包 identity。
- **outbound**：WS 只接受 `RequestTypeChat` / 空，否则
  `ErrUnsupportedConversion("WebSocket Responses requires a chat request")`；wire `APIFormat`
  取 transformer 的 `APIFormat()`（outbound.go:357），WS 端点因此发出 `openai/responses-ws`。

## 端口实现

- `src/AxonHub.Llm/Transformer/OpenAI/Responses/ResponsesProtocolCodec.cs`（新）：
  `IsResponsesFormat`、`AdaptRequestBody`、`AdaptResponseBody`、`AdaptStreamEvent`，外加
  信封 / protocol body 解码、`MarshalRawObject` 的键排序与 raw compact、`FoldFieldName`、头克隆。
  解析前用 `GoJsonUtf8.SanitizeStringLiterals` 兼容 Go 接受的非法 UTF-8 字符串（C008）。
- `src/AxonHub.Api/Orchestration/PassThroughMiddlewares.cs`：新增 `TransportAdaptationEnabled` /
  `IdentityPreservationEnabled` / `ResponsesClientModel` / `PreserveResponsesModel` /
  `PreserveResponsesStreamEventModel`；`ResponsesModelJsonValue` 按 sjson `mustMarshalString`
  的谓词决定直出还是走 `GoJson.Quote`（Go `json.Marshal(string)` 的 HTML 转义与小写十六进制）；
  `PreserveResponsesModel` 用 span splice 只替换目标值区间；`PassThroughRequestBodyMiddleware`
  先记录 raw 再按 passThrough || adaptation 重放；新中间件
  `ResponsesTransportRequestBodyMiddleware` 依次执行 policy、prompt-protection merge、body check、
  `AdaptRequestBody`、model 补丁，并设置 Body / Content-Type / `PassThroughApplied`。
- `src/AxonHub.Api/Orchestration/PassThroughStreamMiddlewares.cs`：capture / apply response / stream
  三条件门控；响应克隆、identity / adaptation 分支、transport → identity 双层流包装；transport 的
  target 读 `outbound.CurrentLlmRequest!.ApiFormat ?? string.Empty`。
- `src/AxonHub.Llm/Transformer/ITransformers.cs`：新增 `IPassThroughResponsePolicy`（让 transformer
  可否决原样响应/流；live 唯一实现属于未移植的 cloudflare SystemOne）。
- `src/AxonHub.Llm/Http/HttpTypes.cs`：`HttpResponseData.ShallowClone()` 复现 Go 的
  `cloned := *response`（字段全部复制，Headers / Body / 指针共享）。
- `src/AxonHub.Llm/Pipeline/Pipeline.cs`：`ErrUnsupportedConversion` 直接返回，不再重试。
- `src/AxonHub.Llm/Transformer/OpenAI/Responses/ResponsesOutboundTransformer.cs`：WS RequestType
  guard 与 wire `ApiFormat = ApiFormat`。
- `src/AxonHub.Api/Orchestration/PassThroughPromptProtection.cs`：raw 字段补丁支持
  `openai/responses-ws`（pass_through_prompt_protection.go:335）。
- `src/AxonHub.Api/Orchestration/ChatCompletionOrchestrator.cs`：按新顺序接入 transport body 中间件。

## 差异

- C290（resolved）：本切片主登记项。identity 用 span splice 保持其余字节不变，不做
  `OverrideJson.TrySet` 的整文档重写（C039）；codec 解析前兼容非法 UTF-8、返回体保留原始字节
  （C008）；`IPassThroughResponsePolicy` 仅有接口。
- C289：收窄，删除“transport adaptation 未移植”的表述；delivery capability 缺口仍随 C255。
- C255：仍为 accepted，`preserveDeliveryCapabilities` 未移植。
- 残余缺口（登记在 C290 decision）：`raw_stream_queue.go` 的后附着 128 MiB 预算、单事件 32 MiB
  reserve 与 pre-attach 64 MiB 上限未移植；端口保持基线 8 MiB / 1024 条且无 queue 层，仅影响
  超大流的背压与内存上限，不改变事件顺序、内容或错误类型。

## 测试

- `tests/AxonHub.Api.Tests/ResponsesTransportPassThroughTests.cs`（42 例）：谓词矩阵（格式对、
  无 attempt）、客户端模型回退、HTTP↔WS 请求适配、warmup 拒绝、policy 否决、prompt protection
  前置与不可补丁回退、中间件顺序、preserve model 双路径 / 缺失路径 / 空模型 / 引号与 UTF-8 /
  原始字节不变、响应克隆与 adaptation 的 Request 继承、响应与流的三条件 capture、transport 与
  identity 流包装顺序 / 首个坏事件短路 / 内层错误回退。
- `tests/AxonHub.Llm.Tests/ResponsesProtocolCodecTests.cs`（28 例）：四组传输对的请求 / 响应 /
  事件保真，Go 错误文案逐字，键排序 / compact / HTML 转义，字段名 fold 与 null 零值。
- `tests/AxonHub.Llm.Tests/ResponsesOutboundTransformerTests.cs` 新增 6 例：WS 拒 compact /
  image / `responses`，接受 null / 空 / chat，且 wire `ApiFormat` 为 `openai/responses-ws`。
- `tests/AxonHub.Api.Tests/PassThroughStreamTests.cs`：`ApplyResponseEnabledReturnsACloneOfRaw`
  锁定 `cloned := *response` 语义。
- 定向运行：Api `FullyQualifiedName~Responses|~PassThrough` 346/346、Api
  `~WebSocket|~Codex|~ChannelTest` 339/339、Llm `~Responses` 428/428；未跑 Api 全量回归。

## 后续

- delivery capability（C255）与 raw stream 预算 / queue 仍是缺口，需要按 f272549f 的
  `stream_delivery_capabilities.go` 与 `raw_stream_queue.go` 单独评估。
- 移植 cloudflare SystemOne outbound 时实现 `IPassThroughResponsePolicy`。
- Api 全量回归仍待择机执行。
