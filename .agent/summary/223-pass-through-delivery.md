# §223 pass-through 交付残余（C255 resolved）

## 参照

- live f272549f `internal/server/orchestrator/pass_through.go:634,657,1133-1144`：
  `captureRawProviderStream` 返回的流带 `owner: true` 与 `cancel: closeStream`；
  `applyPassThroughStream` 的客户端分支不带 cancel，只快照 `state.RawStreamDetach`；
  `passThroughChannelStream.Close` 仅在 owner 时 cancel，随后执行 detach（生产为 nil）。
- `pass_through.go:678-696`：`applyPassThroughStream` 在后台 drain 它收到的流，客户端读取
  raw provider 事件；`pass_through.go:765-803`：交付门关闭时先关闭内层持久流（`stream.Close`）
  再等待 drain，最后关闭 raw 消费流。
- `internal/server/orchestrator/inbound.go:527-545`：`InboundPersistentStream` 由
  `PersistentInboundTransformer.TransformStream` 创建，位于所有 inbound raw 中间件之内，因此
  pass-through 的 drain 才是 request chunks 的观察者。

## 端口实现

- `src/AxonHub.Api/Orchestration/PassThroughStreamMiddlewares.cs`：
  - `PassThroughStreamState` 新增 `Detach`（对应 `state.RawStreamDetach`；参照声明但生产从不赋值）。
  - `PassThroughChannelStream` 构造函数新增 `owner` / `detach`；`Dispose` 仅在 `owner` 时调用
    attempt closer，随后执行 detach。`captureRawProviderStream` 的返回值传 `owner: true`；
    `applyPassThroughStream` 的两条客户端分支传 `cancel: null, detach: state.Detach`，不再取消扇出。
  - `PassThroughStreamMiddleware` 的 remarks 说明 drain 必须读取持久流。
- `src/AxonHub.Api/Orchestration/ChatCompletionOrchestrator.cs`：`PassThroughStreamMiddleware`
  改在 `RequestChainPersistenceMiddleware` 之后注册。inbound raw 中间件按列表顺序应用，因此
  `RequestRawStream` 位于 pass-through 之内，drain 读取它并记录转换后事件；客户端仍通过交付门读取
  raw provider 事件。`PassThroughResponseMiddleware` 仍位于持久化之前（非流式响应保存 raw 响应）。
- 交付门关闭顺序不变：`PassThroughDeliveryStream.Dispose` 未 ack 时先关闭内层持久流（现在是
  `RequestRawStream.Dispose`），再等待 drain，最后关闭 raw 消费流；这与 live
  `withPassThroughDeliveryGateAndDrain` 的 close 参数一致。

## 差异

- C255 由 accepted 改为 resolved；C295 / C296 保持 resolved。
- 设计差异：无新增。历史上 `InboundPersistentStream` 在端口是 inbound raw 中间件、在参照是 inbound
  transformer 的一部分；本切片通过列表顺序复现其相对位置，行为一致。

## 测试

- `tests/AxonHub.Api.Tests/PassThroughStreamTests.cs`：
  - `ChannelStreamStopsAtEmptyBufferAfterCancel` 改为断言 detach 为真、owner cancel 为假
    （镜像 `TestPassThroughChannelStream_StopsAtEmptyBufferAfterCancel`）。
  - 新增 `ChannelStreamCloseDetachesWithoutCancellingTheRawOwner`
    （镜像 `TestPassThroughDetachKeepsRawOwner`）。
- `tests/AxonHub.Api.Tests/ChatCompletionOrchestratorTests.cs`：新增
  `PassThroughStreamPersistsTheDrainedTransformedChunks`——通道开启 `passThroughBody`，raw 事件带
  `provider_marker`；客户端收到 raw 事件（含 marker），request chunks 为转换后事件（不含 marker，
  末项 `[DONE]`）。旧顺序下该测试失败，锁定本切片。
- 定向运行：PassThroughStreamTests + ChatCompletionOrchestratorTests 56 通过；request-chain /
  pass-through / transport / cancellation / live-preview / timeout 等 10 类 244 通过；
  未跑 Api 全量回归。

## 台账

- C255 转 resolved；296 条（198 accepted / 0 gap / 98 resolved）；`diff_register.py lint` 通过，
  `02-differences.md` 已重新生成。

## 后续

- Api 全量回归仍待择机执行。
