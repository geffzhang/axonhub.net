# §144 限流状态迁入 ChannelGrain（A1）

日期：2026-11-08
参照：`internal/server/orchestrator/channel_request_tracker.go`、
`rate_limit_admission.go`、`rate_limit_tracking.go`、`lb_strategy_rate_limit.go`。

## 背景

此前只有两个 grain：`ChannelGrain`（每通道指标）与 `ChannelPoolGrain`（每模型排序）。
RPM/TPM/冷却状态仍留在 API 进程的 `ChannelRequestTracker` 单例里，池评分从该单例读、
中间件向该单例写——同一通道的运行时状态有两个宿主（进程单例与未来的 grain 化路径）。
A1 把这份状态按通道收进 `IChannelGrain`，让它只有一个写入口。

## 交付

- `ChannelRequestTracker` 保留为状态机，新增带显式 `now` 的重载
  （`TryAcquireRequest` / `AddTokens` / `GetRequestCount` / `GetTokenCount` /
  `IsCoolingDown` / `TryGetCooldownUntil`）与 `Clear`，使一个 grain 回合内的读与快照
  观察同一时刻；单参重载继续服务直接构造与内存 recorder。
- `ChannelMetricsSnapshot` 增加 `RateLimitRequests`（Id 7）、`RateLimitTokens`（Id 8）、
  `CooldownUntil`（Id 9），并新增 `ChannelRateLimitState` 快照切片。
- `IChannelGrain` 增加 `TryAcquireRequestAsync`、`AddTokensAsync`、`SetCooldownAsync`、
  `ResetRateLimitAsync`；`ChannelGrain` 组合一个按通道分片的 `ChannelRequestTracker`，
  `GetMetricsAsync` 返回限流切片并惰性清理过期冷却。
- `RateLimitAwareScorer` 增加快照重载；`LoadBalancerScoring.StrategyScore` 改从
  `ChannelMetricsSnapshot` 取限流状态，`ChannelPoolGrain` 不再依赖 tracker。
- 新增 `IChannelRateLimitRecorder`：生产注册 `GrainChannelRateLimitRecorder`（同步桥接，
  与 `GrainChannelMetricsRecorder` 同一模式），`RateLimitAdmissionMiddleware` /
  `RateLimitTrackingMiddleware` 全部经 recorder；直接构造的测试仍走
  `InMemoryChannelRateLimitRecorder`，由 tracker 自己的 `TimeProvider` 裁决。
- `Program.cs` 删除 `ChannelRequestTracker` 单例注册，改为注册 recorder；`ChannelLimiterManager`
  保持每 silo 单例（C042）。

## 行为要点

- 单 silo 下与参照逐项等价：自然分钟桶、`limit <= 0` 旁路、TPM 非正值忽略、
  冷却只延长且过期惰性清理、评分 `min(RPM/TPM, 并发)`、耗尽 -10000。
- Orleans 的单 activation 串行化替代了参照的进程锁；200 个并发准入恰好放行 100。
- 池评分使用它本来就要取的 `GetMetricsAsync` 快照，不多一次 grain 调用；
  限流器本身仍在 `ChannelLimiterManager`（阻塞 FIFO 门不能进 grain 调度器）。
- 多 silo 语义登记为 C225：同一通道的预算变成集群全局（参照是每进程一份）。

## 测试

- `tests/AxonHub.Grains.Tests/ChannelGrainRateLimitTests.cs`（8）：RPM 准入与超限、
  非正上限旁路、TPM 累加与非法值、分钟边界重置、冷却只延长与过期、Reset、
  并发串行、pool 从快照把 9/10 的忙通道排在 1/10 之后。
- `tests/AxonHub.Grains.Tests/RateLimitAwareScorerTests.cs`（追加 4）：快照 RPM/TPM
  取 max ratio、快照冷却耗尽、快照 RPM 耗尽、无 tracker 的 scorer 只能走快照重载。
- `tests/AxonHub.Api.Tests/ChannelRateLimitRecorderTests.cs`（2）：grain recorder 写穿后
  metrics 可见 2 请求 / 300 token / 冷却；内存 recorder 忽略传入时刻并保持 tracker 时钟。

静态计数：`AxonHub.Grains.Tests` 1680 → 1692（+12），`AxonHub.Api.Tests` 5430 → 5432（+2），
四项目合计 8942 → 8956（`AxonHub.Llm.Tests` 1501、`AxonHub.Persistence.Tests` 331 不变）。
**按项目规则未运行 lint / build / test。**

台账：基线 §144、验收 §144 已写入；差异台账新增 C225（accepted），并更新 C035 的接线描述；
总数 225 条（162 accepted / 54 resolved / 9 gap），`diff_register.py lint` 通过、
`02-differences.md` 已重新生成。
