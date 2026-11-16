# §237 模型级熔断计分与记录

## 背景

C036：参照的 ModelAwareCircuitBreakerStrategy 从 ModelCircuitBreakerProvider
读取 (channel, model) 的有效权重；端口没有模型级状态，circuit-breaker 策略
恒按 closed 权重计分。

## 实现

- `src/AxonHub.Grains/ModelCircuitBreaker.cs`
  - 状态 closed / half_open / open；3 次连续失败 half_open、5 次 open；
  - 30 分钟失败统计 TTL；5 分钟探针间隔；half_open 权重 0.3；
  - 探针失败按 2^probeAttempts 退避、上限 8 倍；
  - TryBeginProbe / EndProbe 租约原语。
- `LoadBalancerScoring.StrategyScore` 新增可选 breaker/model：
  closed 200 + 0-0.5 随机，half_open 60 + 随机，open 0。
- `ChannelPoolGrain` 以请求模型 key（grain primary key）读取有效权重。
- `ChannelPerformanceRecord.ModelId` 记录客户端请求模型；
  `ChannelMetricsMiddleware` 在有效策略为 circuit-breaker 时按
  (channel, model) 记录成功/失败。
- `ChatCompletionOrchestrator` 把 effectivePolicy.LoadBalancerStrategy 传入
  pipeline；Program 与两个测试 silo 注册 `ModelCircuitBreaker` 单例。

## 测试

- Grains `ModelCircuitBreakerTests`：4/4（状态迁移、探针窗口/成功恢复、
  TTL 自动恢复、探针失败退避）。
- Grains `ChannelSelectionTests.CircuitBreakerScoreUsesTheModelState`；
  定向 36/36。
- Api `ModelCircuitBreakerTrackingTests`：3/3（成功关闭、失败打开、
  非 circuit-breaker 策略不跟踪）；orchestrator 回归 36/36。
- 未跑 Api 全量；新增 Grains 4 例、Api 3 例待下次全量计入。

## 台账

- C036 → resolved；tracker 的本地跳过与并发单探针拆到 C308（accepted）。
- 308 条（196 accepted / 112 resolved / 0 gap）；lint 通过；
  `02-differences.md` 已重新生成。

## 边界

- C308：所有候选 open 时仍可能发出一次非探针请求；探针窗口内并发请求可能
  多于一个（状态机租约原语已就绪，tracker 未接入）。
