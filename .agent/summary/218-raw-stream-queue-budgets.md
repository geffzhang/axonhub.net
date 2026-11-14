# §218 raw stream queue 的预算与背压（C291）

## 背景

§217（C290）移植 Responses transport adaptation 时，把 live `raw_stream_queue.go` 的预算层登记为
残余缺口：端口仍沿用基线 C091 的 `rawStreamBacklog`（attach 前 1024 条 / 8 MiB），attach 后只靠
容量 64 的原始通道施加背压。live f272549f 把预算分成两段：

- attach 前：1024 条 / 64 MiB，超过即让 attempt 显式失败；
- attach 后：队列事件继续记账，总字节上限 128 MiB、单事件上限 32 MiB；达到上限时生产者阻塞，
  消费者读取后释放。这样慢客户端会反压上游，而不是无限占用内存。

本切片按 live `pass_through.go:893-999` 与 `raw_stream_queue.go` 补齐该层。

## 参照行为

- **rawStreamBacklog**：`hold` 在 attach 前按 `count >= 1024 || size > 64 MiB - bytes` 失败，
  错误为 `pass-through pre-attachment raw event budget exceeded` 包装
  `pipeline.ErrPreCommitBufferExceeded`；`attach` 只标记 attached 并交出事件，**count/bytes 不减少**，
  由队列读取时 `release`。
- **reserve**：单事件 > 32 MiB 立即失败；attach 前预算不足立即失败；attach 后预算不足时等待
  `waitCh` 或 `ctx.Done()`，`release` 关闭 `waitCh` 唤醒等待者。
- **rawStreamQueue**：通道容量 64；`attach` 把 backlog 事件移入 `held` 并记录尺寸；`enqueue`
  先 `reserve` 再阻塞写通道，取消时 `release`；`next` 先取 held 再阻塞读通道，读取时按
  `Data + Type + LastEventID` 释放；`nextBuffered` 只排干已缓冲事件，从不阻塞生产者。
- **接线**：扇出线程对每个事件先 `hold`（attach 前），attach 后 `enqueue`；`applyPassThroughStream`
  通过 `queue.attach()` 接管 held，消费者按 held → 通道顺序读取。

## 端口实现

- `src/AxonHub.Api/Orchestration/PassThroughStreamMiddlewares.cs`
  - `RawStreamBacklog`：常量升级为 `MaxRawEventBytes = 32 MiB` / `MaxRawPreAttachEvents = 1024` /
    `MaxRawPreAttachBytes = 64 MiB` / `MaxRawQueueBytes = 128 MiB`；新增 `_count`、`Reserve`、
    `Release` 与 `TaskCompletionSource` 等待者；`Hold` 使用 `_count` 记账，`Attach` 保持
    count/bytes 不变。
  - 新增 `RawStreamQueue`：64 槽 `BlockingCollection`、held 列表与尺寸表；`Attach`、`Enqueue`、
    `Next`、`NextBuffered`、`TryTakeHeld`。`Enqueue` 的任何失败都会释放预留。
  - `PassThroughStreamState` 新增可选 `RawQueue`；`PassThroughChannelStream` 新增队列路径，
    取消后仍先排干 held + 通道缓冲。
  - `CaptureRawProviderStreamMiddleware` 创建 queue 并写入 state，扇出线程在 attach 后
    `queue.Enqueue`；预算错误直接写入 per-attempt `errorRef`，流 `Err` 暴露
    `PreCommitBufferExceeded`。
  - `PassThroughStreamMiddleware` 在 queue 存在时 `queue.Attach()` 并构造队列流；旧的
    channel + `PrependStream` 路径保留给只构造 state 的既有测试。
- `tests/AxonHub.Api.Tests/PassThroughStreamTests.cs`：新增 8 例，覆盖 live
  `pass_through_budget_test.go` 的预算与背压行为，以及生产 queue 路径。

## 差异

- C291（resolved）：本切片主登记项。`waitCh` 用 `TaskCompletionSource`（异步延续）实现；队列是
  per-attempt 句柄而非 `IDisposable`（通道由扇出线程 `CompleteAdding`，与 Go channel 只关闭不释放
  一致）；`Enqueue` 的取消沿用 C091 的静默结束，`Err` 由 requestToken 兜底为
  `OperationCanceledException`。
- C290：删除“raw_stream_queue 未移植”的残余缺口表述。
- C091：追加 §218 更新，说明 8 MiB 预算已被 64 MiB pre-attach + 128 MiB queue 取代。
- C255：仍为 accepted；`preserveDeliveryCapabilities` 未移植。

## 测试

- `QueueRejectsASingleEventAboveTheLiveEventLimit`：单事件 32 MiB 上限与哨兵。
- `QueuePostAttachByteBudgetWaitsForTheConsumerToRelease`：128 MiB 后阻塞，`Release` 后继续。
- `QueueReserveStopsWaitingWhenTheAttemptIsCanceled`：等待中取消。
- `QueueAttachKeepsHeldEventsChargedUntilTheyAreRead`：held 事件读取前继续占预算。
- `QueueBlocksLiveSendsUntilTheBoundedChannelDrains`：满通道背压而非丢弃（live
  `TestRawStreamQueue_BlocksLargeLiveEventsUntilConsumerReads`）。
- `QueueNextReturnsHeldEventsBeforeLiveEvents`：held 优先与顺序。
- `CaptureStreamReportsThePreAttachmentBudgetAsItsError`：pre-attach 溢出显式失败（live
  `TestPassThroughAnthropicStream_RawBudget`）。
- `ApplyStreamReadsHeldEventsFromTheQueue`：生产路径经 queue attach 读取。
- 既有 `PassThroughStreamTests` 全量通过；定向运行：Api `~Responses|~PassThrough` 354/354、
  Api `~WebSocket|~Codex|~ChannelTest` 339/339；未跑 Api 全量回归。

## 后续

- delivery capability（C255）仍是缺口，需要按 f272549f 的 `stream_delivery_capabilities.go`
  单独评估。
- Api 全量回归仍待择机执行。
