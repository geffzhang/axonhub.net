# §239 LatencyAware 的流式/非流式延迟 EWMA

## 背景

C034：参照的 LatencyAwareStrategy 从 AggregatedMetrics 读取流式首包/TPS EWMA
或非流式端到端 EWMA；端口只有计数，LatencyAware 恒为 40。

## 实现

- `ChannelPerformanceRecord` 增加 `FirstTokenTime` / `CompletionTokens` 与
  `MeasureLatency`（最小延迟 10ms；流式 TPS 用总延迟减首包；无首包的流式
  请求落入非流式桶）。
- `ChannelGrain.RecordLatencyAsync`：alpha 0.3 维护
  StreamingFirstTokenLatencyEWMA、StreamingTokensPerSecondEWMA 与
  NonStreamingLatencyEWMA；首个样本直接落值。
- `ChannelMetricsSnapshot` 增加五个延迟字段；`ChannelLatencySample` 为
  Orleans 序列化参数。
- `ChannelMetricsMiddleware`：流式首个有意义输出与最终 usage 处采集；成功
  后通过 `IChannelMetricsRecorder.RecordLatency`（默认 no-op）上报。
- `LoadBalancerScoring.LatencyScore`：流式
  `80*(0.7*首包分+0.3*TPS 分)`（TPS 5-100，无 TPS 用 0.5），非流式
  `80*端到端分`（3s 上限），无样本 40。
- `ChannelPoolGrain.SortAsync` 增加 `stream` 参数，orchestrator 传入请求流式
  意图。

## 测试

- Grains `ChannelGrainLatencyTests`：1/1（流式/非流式 EWMA 与重置）。
- Grains `ChannelSelectionTests.LatencyScoreUsesTheMatchingSampleBucket`；
  定向 34/34。
- Api `ChannelLatencyRecordingTests`：3/3（首包/TPS、无首包回退、middleware
  上报）；定向 109/109。
- 未跑 Api 全量；新增 Grains 1 例、Api 3 例待下次全量计入。

## 台账

- C034 → resolved。
- 308 条（194 accepted / 114 resolved / 0 gap）；`diff_register.py lint`
  通过；`02-differences.md` 已重新生成。

## 边界

- Orleans grain 失活会丢弃 EWMA，与参照进程重启丢内存指标同型。
- 首包判定复用 `ResponseContent.HasMeaningfulStreamOutput`，与请求链记录一致。
