# §219 交付能力与交付门（C292）

## 参照

- live f272549f `llm/streams/stream.go`：`DeliveryAcknowledger`、`DeliveryBoundary`、
  `FinalizationWaiter`、匿名 `interface{ ExpectedStreamChoices() int }`。
- `llm/pipeline/stream_capabilities.go`（`preserveStreamCapabilities`）、
  `internal/server/orchestrator/stream_delivery_capabilities.go`
  （`preserveDeliveryCapabilities`）。
- `internal/server/api/stream_delivery.go`：`writeStreamDelivery` / `acknowledgeStreamDelivery` /
  `streamExpectedChoices`；`stream_lifecycle.go`：`streamLifecycle.Deliver` / `AcknowledgeDelivery` /
  `Close`。
- `internal/server/orchestrator/inbound.go`：`InboundPersistentStream` 的 `Deliver` /
  `AcknowledgeDelivery` / `Close` 与 `streamOutcome.deliveryDecision`。
- `internal/server/orchestrator/pass_through.go`：`passThroughDeliveryStream`。
- 写侧：`chat.go`（SSE）、`chat_binary`（EOF flush）、Gemini（闭合 `]`）、
  `responses_websocket.go`（每帧，terminal 后继续到 EOF）；`tester.go` 的 terminal 判定与 ack。

## 端口实现

- `src/AxonHub.Llm/Streams/DeliveryCapabilities.cs`（新）：四个可选接口；`DeliverAsync` 扩展优先
  boundary，否则 `DeliverWithoutBoundaryAsync`（terminal 且无错误才 ack）；`AcknowledgeDelivery` /
  `ExpectedStreamChoicesOf` 扩展；`Preserve(source, adapted)` 仅在 source 是 acknowledger 时包装
  `CapabilityStream`，expected 保持为方法值（动态读取，不冻结包装时的计数）。
- Llm 转发：`PipelineStream.CancelOnCloseStream`（参照有意不暴露 ExpectedStreamChoices）、
  `ResponseHeadersStream`（构造捕获 observer/waiter/expected，boundary 在运行时查 wrapped stream）。
- Api 转发：`PrimedStream`（四接口全转发）、`SafeguardsStream`、`LivePreviewMiddleware` inbound、
  `ResponsesSessionStream`、`PassThroughDeliveryStream`、`RequestRawStream`。
- `RequestDeliveryState`（新）：closed / 一次性 ack / finalized TCS；`DeliverAsync` 在 closed 或 token
  取消时返回 `OperationCanceledException`，写后 terminal 且 err==null 才 ack；`MarkClosed` 释放 waiter。
- `StreamOutcome`：`deliveryDecision`——FinalDecision 为 completed 但未 ack → Incomplete +
  `ErrStreamIncomplete`；`deadlineBeforeTerminal` 优先 Failed。`RequestChainPersistenceMiddleware` 以
  deliveryDecision 决定 completed / incomplete / failed，`deliveryBlocked` 时跳过结果行。
- 写侧 gate：SSE 每事件、Binary 仅 EOF flush、Gemini 闭合 `]`、WS 每帧（terminal 后继续读到 EOF）；
  终端写成功后由 gate 或 writer 调用 `AcknowledgeDelivery`。
- `ChatCompletionEndpoint` finally 在 Dispose 后等待 `FinalizationWaiter`。
- `ChannelTestService.HandleStreamResponse`：`StreamTerminalTracker` 判定 terminal；有内容但无 terminal
  返回 `Stream ended without terminal event`（Message 保留已累积内容）；成功时 ack。
- pass-through 门：未交付先 close 生产者再无限等 drain；已交付最多等 1 秒 tail timeout 再 close；
  结束等价于 `errors.Join(closeErr, inner.Close())`。

## 差异

- C292（resolved）：本切片主登记项。
- C255 收窄为 accepted 残余：streamLifecycle 的 execution/shutdown 上下文分离、tail accounting 的
  1 秒 / 256 事件 / 1 MiB 限额、端口 Dispose 顺序（先 Finalize 再 close inner；live 相反）、
  pass-through 下 request chunk 来源（端口客户端读取 vs live drain 尾部）。
- `RequestDeliveryState.DeliverAsync` 在异步写期间不持锁（.NET 异步延续不并发改写自身状态），与 live
  在 mu 内写不同；closed/canceled 判定与 ack 时机一致。

## 测试

- `tests/AxonHub.Llm.Tests/DeliveryCapabilitiesTests.cs`：5 例（Preserve 转发 ack/boundary/waiter、
  expected 动态、普通流不包装、DeliverWithoutBoundary 仅 terminal 成功 ack、ResponseHeadersStream 转发）。
- `tests/AxonHub.Api.Tests/StreamDeliveryTests.cs`：12 例（RequestDeliveryState 一次性 / 仅 terminal /
  Close 后拒绝 / 释放 waiter、StreamOutcome deliveryDecision 三例、pass-through 门未交付先 close /
  已交付等 drain / 1 秒超时 / 幂等与 ack 转发、PrimedStream 能力转发）。
- `SseStreamWriterTests`：+2（终端写后 ack、无 terminal 不 ack）。
- `ResponsesWebSocketEndpointTests.KeepsWritingEventsAfterTheTerminalEventUntilTheStreamEnds`。
- `ChannelTestServiceTests.TestChannelReportsAStreamWithoutATerminalEvent`。
- `RequestChainPersistenceTests.StreamSuccessFinalizesRequestAndExecutionOnDispose` 按新语义显式
  `AcknowledgeDelivery()`（裸消费者不再算作已交付）。
- 定向运行：Llm `~Stream|~Pipeline|~ResponseHeaders` 390/390；Api
  `~Stream|~Orchestrat|~Chat|~Responses|~WebSocket|~Codex|~ChannelTest|~Safeguard|~LivePreview|~Pipeline|~RequestChain`
  942/942；未跑 Api 全量回归。

## 台账

- 新增 C292（resolved），C255 收窄（仍 accepted）；292 条（199 accepted / 0 gap / 93 resolved）；
  `diff_register.py lint` 通过，`02-differences.md` 已重新生成。

## 后续

- C255 残余如需闭合，需要引入独立的 streamLifecycle（execution/shutdown 上下文）与 tail accounting 限额。
- Api 全量回归仍待择机执行。
