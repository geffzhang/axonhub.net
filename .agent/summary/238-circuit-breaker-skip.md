# §238 模型熔断 tracker 的本地跳过与单探针租约

## 背景

C308：C036 已实现状态机、权重与记录，但 tracker 的本地跳过和探针租约尚未
接线。

## 实现

- 新增 `ModelCircuitBreakerSkipException`（message
  `skip candidate by circuit breaker`，与参照
  `errSkipCandidateByCircuitBreaker` 一致）。
- `ChannelMetricsMiddleware.TransformOutboundRawRequest`：
  - 策略为 circuit-breaker 且 (channel, model) 为 open 时先 `TryBeginProbe`；
  - 成功则记录 `WasProbe=true`；失败抛 skip 异常。
- `OnOutboundRawError` 对 skip 异常直接返回，不记指标/熔断失败。
- `PersistentOutboundTransformer.CanRetry` 对 skip 异常返回 false，管线切换
  下一候选。
- `Record` 在成功/失败/取消时统一 `EndProbe`；失败按 `WasProbe` 传给
  `RecordError`，探针失败应用 2^n 退避。

## 测试

- `PersistentOutboundRetryTests.CircuitBreakerSkipsNeverRetryTheSameChannel`。
- `PersistentOutboundRetryTests.CircuitBreakerSkipIsNotRecordedAndTheProbeLeaseIsClaimed`
  （skip 不计失败、租约标记、成功复位）。
- Grains 熔断用例 4/4；定向 `PersistentOutboundRetryTests` + tracking 21/21。
- 未跑 Api 全量；新增 Api 2 例待下次全量计入。

## 台账

- C308 → resolved。
- 308 条（195 accepted / 113 resolved / 0 gap）；`diff_register.py lint`
  通过；`02-differences.md` 已重新生成。
