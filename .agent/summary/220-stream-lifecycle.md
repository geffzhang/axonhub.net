# §220 streamLifecycle / tail accounting（C293）

## 参照

- live f272549f `internal/server/orchestrator/stream_lifecycle.go`：
  `newStreamLifecycle` / `watch` / `downstreamEnded` / `registerTailOwner` / `startTail` /
  `waitForAccounting` / `AcknowledgeDelivery` / `Deliver` / `Close` / `WaitForFinalization`、
  `collectStreamTail`、`streamTailOwner.run`、`awaitTailBarrier`。
- `internal/server/orchestrator/outbound.go`：`ExecutionRawStream`（HoldFinalization、
  consumeTailEvent、Next 写 sourceEnd、Current 的 `[DONE]` semantic、Close/finalizeClose、
  accountingSourceEnd）。
- `internal/server/orchestrator/inbound.go:286-417`：`InboundPersistentStream.Close` 的顺序与
  ack 观察。
- `internal/server/orchestrator/orchestrator.go:186-188`、`:232-233`、`:298-299`、`:368`：
  lifecycle 创建、execution ctx 贯穿、失败先 Close 再持久化。
- `internal/server/orchestrator/pass_through.go:636-836`：pass-through 门的 observer 与 Close 顺序。
- `llm/model.go:829-864`：`SourceEndReason` 与 `AccountingEvidence`（后者归 C294）。

## 端口实现

- `src/AxonHub.Api/Orchestration/StreamLifecycle.cs`（新）：
  - 常量 `TailTimeout = 1s`、`TailEventLimit = 256`、`TailPayloadLimit = 1 MiB`。
  - execution token 与下游取消分离；watcher 监听 downstream/shutdown/stopWatch，取消回调只
    发布事件，状态转换在 watcher task 上执行。
  - `DownstreamEnded`：已 ack、cause 非空、非 shutdown、非 deadline → `StartTail(tail, nil)`；
    否则置 canceled、以 cause 取消 execution 并 `StartTail(tail, cause)`。
  - `AcknowledgeDelivery` 在 closed/acknowledged/canceled/下游已取消时忽略；`DeliverAsync` 用
    `SemaphoreSlim` 跨异步写串行化，closed/canceled 返回 `OperationCanceledException`，检查调用
    token 与 shutdown cause，成功 terminal 在门内直接置 ack。
  - `Close`：`!canceled && (!acknowledged || tail == nil)` 时取消 execution；ack+tail → 只等尾部
    记账；未 ack+tail → abort、等 `ReaderDone` 与 `ReaderDoneFunc` 双屏障、以 OCE 启动 tail 并
    等待；随后 stopWatch/done/finalized。
  - `WaitForAccounting` 启动 tail(OCE) 并等 `accountingDone`。
  - `StreamTailOwner.Run`：cause 非空先 abort；双屏障；1 秒收集窗口；超时先 abort 再等 collector
    返回并强制 Canceled；finalize 把非空 err 映射 Error（取消类 Canceled），CleanEof 且
    `sourceEnd() == Semantic` 时提升语义终态。
  - `CollectTail`：256 事件 / 1 MiB（Data + UTF8(Type) + UTF8(LastEventId)），先查 ctx；Next false
    时 Err 为空 → CleanEof、取消类 → Canceled、否则 Error；预算 → Budget + `IOException("short
    buffer")`；onEvent 失败 → Error；自然结束 → Budget。
- `src/AxonHub.Llm/Model/LlmResponse.cs`：`SourceEndReason`（unknown/clean-eof/semantic/error/
  canceled/budget）。
- `src/AxonHub.Api/Orchestration/RequestChainPersistenceMiddleware.cs`：
  - `ExecutionRawStream` 注册 tail owner（HoldFinalization；ReaderDone=consumerDone，
    ReaderDoneFunc=进行中的 activeReader，Abort=释放 consumerDone 并关闭源，OnEvent 只追加 chunk
    摘要与 terminal 证据，Finalize=FinalizeAccounting）。
  - `Next` 维护 activeReader 并写 SourceEnd；`Current` 对 `[DONE]`+non-choice terminal 置
    Semantic；`Dispose` 在 held 时只释放 consumerDone；`FinalizeAccounting` 写 sourceEnd 后
    finalize 并关闭源。
  - `FinalizeExecutionStream` 接收 tail sourceEnd；`AccountingSourceEnd` 按 Error >
    Semantic/CleanEof/Budget > Canceled > Unknown 记账；`attempt.AccountingSourceEnd` 已按 live
    记录，usage 门仍归 C294。
  - `RequestRawStream`：ack/DeliveryAcknowledged/WaitForFinalization 优先 lifecycle；`Dispose`
    用 closeMu 幂等且重复关闭等待首个完成；lifecycle 路径先关 inner、再 `lifecycle.Close()`、
    随后才观察错误并 finalize，最后再次关 inner。
- `src/AxonHub.Api/Orchestration/ChatCompletionOrchestrator.cs`：仅在有 request recorder 时创建
  `StreamLifecycle`，`ExecutionToken` 贯穿 pipeline/transport/limiter/raw capture/pass-through/
  persistence；错误路径先 `CloseRequestDelivery()` 再 `FinalizeFailure`；非流式 accept 后
  `CloseRequestDelivery()`；流式返回不 Close。
- `src/AxonHub.Api/Orchestration/PersistentOutboundTransformer.cs`：`TryNextChannel` /
  `TryPrepareForRetry` 先 `WaitForAccounting()`。
- `src/AxonHub.Api/Http/StreamTerminalTracker.cs`：`binary.done` 与
  `IsNonChoiceTerminalEvent`。
- `src/AxonHub.Api/Orchestration/PassThroughStreamMiddlewares.cs`：门 observer 改为
  `StreamLifecycle`，`ownsClose = lifecycle is null`。
- 删除 `src/AxonHub.Api/Orchestration/RequestDeliveryState.cs`。

## 差异

- C293（resolved）：本切片主登记项。
- C294（accepted）：usage 记账 provenance 门未移植——live 的 `ResponseMeta.AccountingEvidence`
  与 `AccountingFinal(sourceEnd)`、三个 aggregator 的证据分类、`accountingMayBeFinal`
  （OpenAI/Anthropic 只信终值证据，其他格式回退 usage 非空）与 "Authoritative usage unavailable"
  警告。端口仍无条件 `LogUsage`；`attempt.AccountingSourceEnd` 已打底。
- C255 收窄（仍 accepted）：host shutdown 未接线（生产不传 shutdown token）；pass-through
  client 分支 Dispose 经 attempt closer 取消扇出（live 只 detach，`TestPassThroughDetachKeepsRawOwner`
  不镜像）；pass-through 下 request chunk 来源为客户端读取而非 drain 尾部。
- 设计差异：Go mutex 跨 await → `SemaphoreSlim`；取消回调只发布事件、watcher task 转换状态；
  端口 execution 不重施加 deadline（RequestTimeoutMiddleware 取消下游 token + downstreamCause
  保持 `TimeoutException` 区分，比 Go 多一次调度跳）；lifecycle 仅在存在 recorder 时创建。

## 测试

- `tests/AxonHub.Api.Tests/StreamLifecycleTests.cs`（新）：镜像 live `stream_lifecycle_test.go`
  的 11 个测试函数（CancelRace 3 子测试、Tail 3 子测试、BudgetAbortsSource、SourceEndReasons
  Theory 2 例、AbortsBlockedNext、LifecycleTailOwner、RetryWaitsForPendingAccounting、
  UnacknowledgedCloseWaitsForReader、ShutdownCancelsAcknowledgedExecution、
  TailOwnerWaitsForReader、TailOwnerWaitsForConsumerQuiescence）；`TestPassThroughDetachKeepsRawOwner`
  按 C255 不镜像。共 15 个方法 / 16 个用例。
- `tests/AxonHub.Api.Tests/StreamDeliveryTests.cs`：前 4 例改用 `StreamLifecycle`，pass-through
  门三例改用 lifecycle observer；共 12 例。
- 定向运行：`StreamLifecycleTests|StreamDeliveryTests` 28/28；
  `RequestChainPersistenceTests|PassThroughStreamTests|ResponsesTransportPassThroughTests|
  PersistentOutboundCancellationTests|LivePreviewOrchestratorTests|RequestTimeoutTests` 125/125；
  未跑 Api 全量回归。

## 台账

- 新增 C293（resolved）与 C294（accepted）；C255 收窄（仍 accepted）；294 条
  （200 accepted / 0 gap / 94 resolved）；`diff_register.py lint` 通过，`02-differences.md` 已重新生成。

## 后续

- C294：ResponseMeta 契约 + 三个 aggregator 的 AccountingEvidence 分类 + usage 门。
- C255：host shutdown 接线（生命周期管理器）、pass-through detach 与 request chunk 来源。
- Api 全量回归仍待择机执行。
