# §138 通道限流可观测性指标（C044 闭合）

日期：2026-11-08
参照：`internal/server/orchestrator/channel_limiter_metrics.go`、`connection_tracking.go:77-100`、
`orchestrator.go:41-46`、`internal/metrics/provider.go:27`。

## 交付

- `src/AxonHub.Grains.Abstractions/ChannelLimiterMetrics.cs`：System.Diagnostics.Metrics 的
  `Meter("axonhub")` 上注册五个同名、同单位、同描述的 instrument：
  - `axonhub_channel_inflight`（ObservableGauge，requests）
  - `axonhub_channel_queue_waiting`（ObservableGauge，requests）
  - `axonhub_channel_queue_full_total`（Counter，requests）
  - `axonhub_channel_queue_timeout_total`（Counter，requests）
  - `axonhub_channel_queue_wait_seconds`（Histogram，s）
  gauge 回调从 `ChannelLimiterManager.Snapshot()` 返回 `Measurement<long>` 列表；counter /
  histogram 带 `channel_id` / `channel_name` 标签；null meter 时全部为 no-op；
  `CreateDefault(manager)` 供宿主注册进程级单例。
- `src/AxonHub.Api/Orchestration/RateLimitMiddlewares.cs`：`ChannelLimiterMiddleware` 新增可选
  `ChannelLimiterMetrics` 与 `TimeProvider` 参数；`Acquire` 前取时间戳，按
  `ChannelQueueException.Reason` 递增 full / timeout counter，成功获准后记录等待秒数。
- `src/AxonHub.Api/Orchestration/ChatCompletionOrchestrator.cs`：新增可选
  `ChannelLimiterMetrics` 构造参数并传给中间件。
- `src/AxonHub.Api/Program.cs`：单例注册 `ChannelLimiterMetrics.CreateDefault(ChannelLimiterManager)`。

## 行为要点

- 五个 instrument 的名称、单位、描述、标签与参照一致；OTel .NET 按 Meter 名 `axonhub`
  收集，部署方用任一 `MeterListener` / OTel collector 得到与 Go 侧相同的 measurement 集合。
- 参照用一个 `RegisterCallback` 同时 Observe 两个 gauge，端口用两个 ObservableGauge 回调；
  逐次抓取的观测值一致。
- 无监听器、null meter 或 orchestrator 未注入 metrics 时，指标记录不影响准入语义。
- 等待计时覆盖快路径（立即获准约 0）与排队路径，只在成功获准时记录；队列拒绝只递增
  full / timeout counter 并保持原有类型化错误。

## 测试

- `tests/AxonHub.Api.Tests/ChannelLimiterMetricsTests.cs`：5 例——
  `NilMeterIsNoop`、`GaugeCallbackEmitsLimiterStats`（channel 42 "kimi"，两次 Acquire →
  inflight 2 / waiting 0）、`CountersAndHistogram`（full 2 / timeout 1 / wait 1）、
  `QueueFullRejectionIncrementsTheCounter`、`SuccessfulAcquireRecordsTheWait`（固定 7ms → 0.007s）。

`AxonHub.Api.Tests` 由 5371 增至 5376（+5），四项目合计由 8850 增至 8855
（`AxonHub.Llm.Tests` 1468、`AxonHub.Persistence.Tests` 331、`AxonHub.Grains.Tests` 1680 不变）。
**按项目规则未运行 lint / build / test，上述为静态计数。**

## 台账

- `porting/differences.jsonl`：C044 → resolved（what / cause / decision / test 重写为已闭合状态）；
  新增 C223（gap）：参照通用 metrics 子系统（`internal/metrics` provider/config 与
  `WithMetrics()` HTTP 指标中间件）尚未移植，待决定移植或裁剪。
- `lint` 通过；`render` 重新生成 `02-differences.md`（223 条：160 accepted / 52 resolved / 11 gap）。
- `porting/01-baseline.md`：新增 §138。
- `porting/03-acceptance.md`：追加 §138 静态计数段落。

## 下一步

- 决策 C223（通用 metrics provider/config + HTTP 中间件）。
- 剩余真实缺口：C014、C022、C072、C091、C092、C094、C096、C097、C126、C135、C223。
