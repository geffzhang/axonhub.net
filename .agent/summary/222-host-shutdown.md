# §222 host shutdown 接入（C295）与聚合完成性能上报（C296）

## 参照

- live f272549f `internal/server/lifecycle/lifecycle.go`：`Manager` 用
  `context.WithCancelCause` 建生命周期；`FromContext` 供 handler 取 manager。
- `internal/server/server.go:36-39,101-109`：`server.New` 把 manager 挂进 gin 的请求上下文
  （每个请求都继承 shutdown 上下文）；`Server.Shutdown` 先 `lifetime.Shutdown(context.Cause(ctx))`
  再关闭 HTTP（先取消进行中的请求，再等连接收尾）。
- `cmd/axonhub/main.go:141-153`：信号处理时以信号构造 cause。
- `internal/server/orchestrator/stream_lifecycle.go`：`newStreamLifecycle` 的
  `shutdown = lifecycle.FromContext(ctx).Context()`；watcher 同时监听 downstream / shutdown /
  stopWatch；已 acknowledged 的流在 shutdown 时仍取消 execution（最终状态 Failed）。
- `internal/server/orchestrator/outbound.go:241-249,294-306,314-315`：`enqueueCompletedPerformance`
  仅当 `state.Perf.RequestCompleted` 为真才上报；聚合完成分支在 `markPerformanceTerminal` 之后
  显式调用它（此时 performance stream 已关闭，关闭未标记的记录不上报任何东西）。

## 端口实现

- `src/AxonHub.Api/Lifecycle/ServerLifetime.cs`（新）：`Token` / `Cause` / 一次性
  `Shutdown(Exception? cause = null)`；首次调用生效（`sync.Once` 等价），nil cause 替换为
  `new InvalidOperationException("server shutdown")`（对应 `errors.New("server shutdown")`）。
- `src/AxonHub.Api/Program.cs`：`AddSingleton<ServerLifetime>()`；
  `app.Lifetime.ApplicationStopping.Register(() => serverLifetime.Shutdown())`——在 HTTP
  shutdown 之前运行，对应 live 先取消生命周期再关闭服务器。
- `src/AxonHub.Api/Orchestration/ChatCompletionOrchestrator.cs`：新增可选构造参数
  `ServerLifetime? serverLifetime = null`；创建 `StreamLifecycle` 时传
  `_serverLifetime?.Token ?? default` 与 `() => _serverLifetime?.Cause`；
  `WithInbound` / `WithChannelSelector` 透传。直接构造的 orchestrator 留空 token，
  等价于没有 manager 的 ctx。
- `src/AxonHub.Api/Orchestration/StreamLifecycle.cs`：仅注释更新（shutdown 参数来源）。
- C296：`ChannelPerformanceState` 增加 `OnCompleted`（由 `ChannelMetricsMiddleware` 绑定为
  `Record`，两条路径共享 reference 的单一 `AsyncRecordPerformance` 入口）与 `ReportCompleted()`
  （`Current is { IsValid: true }` 才上报）。`RequestChainPersistenceMiddleware` 的
  `aggregatedCompleted` 分支在 `MarkStreamTerminal(Completed)` 后调用；终态在流内已标记的路径
  仍由 `PerformanceStream.Dispose` 上报，两入口按 `TerminalState == None` 互斥，不会重复计数。

## 差异

- C295（resolved）：host 生命周期接入。
- C296（resolved）：聚合完成性能上报（enqueueCompletedPerformance 等价物）。
- C255 收窄：移除 host shutdown 项，仅保留 pass-through 客户端分支 detach 与 request chunks
  来源两项（仍 accepted）。
- 设计差异：Go 的 context cause 用 `Exception` 表达；默认 cause 的文案保持一致但类型为
  `InvalidOperationException`。

## 测试

- `tests/AxonHub.Api.Tests/ServerLifetimeTests.cs`（新，5 例）：Cause 初值 / Shutdown 取消并
  保存 cause / 首次调用生效 / 默认 "server shutdown" 文案 /
  `HostShutdownCancelsAcknowledgedStreamExecution`（ack 后 shutdown 取消 execution，
  execution 与 request 状态均为 failed）。
- `tests/AxonHub.Api.Tests/ChatCompletionOrchestratorTests.cs`：
  `AggregatedCompletionWithoutTerminalEventCountsSuccess` 改为先 `AcknowledgeDelivery()` 再
  `Dispose()`——未 ack 关闭会取消 execution（live 语义），n=2 只收到 1 个 choice finish 时
  聚合不成立；ack 后 ctxErr 为空，聚合完成成立并由 C296 上报。
- 定向运行：相关 9 类 188 通过；RequestChainPersistence|PassThroughStream|
  ResponsesTransportPassThrough|PersistentOutboundCancellation|LivePreviewOrchestrator|
  RequestTimeout 125 通过；未跑 Api 全量回归。

## 台账

- 新增 C295 / C296（均 resolved），C255 收窄；296 条（199 accepted / 0 gap / 97 resolved）；
  `diff_register.py lint` 通过，`02-differences.md` 已重新生成。

## 后续

- C255 残余：pass-through 客户端分支 detach、request chunks 来源（仅取消/断开路径）。
- Api 全量回归仍待择机执行。
