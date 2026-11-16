# §147 previous-channel 粘性缓存迁入 PreviousChannelGrain（A4）

日期：2026-11-09
参照：`internal/server/biz/request.go:1733-1801`、`internal/pkg/xcache/config.go` /
`entry.go`（`cache.mode` 默认 `memory`）、`internal/server/orchestrator/candidates.go:815-870`。

## 背景

§91 已把 trace / thread 通道粘性路由的读写语义对齐参照，但 `PreviousChannelCache` 是进程内
字典：多实例（多 silo）部署时每个进程一份亲和，C181 登记为「接受 + 真实缺口」。A4 把这份按
缓存键的状态收进 `PreviousChannelGrain`，让同一集群内的读写落到同一激活。

## 交付

- 新增 `PreviousChannelOptions` / `IPreviousChannelGrain`（AxonHub.Grains.Abstractions）：
  string key 即参照键 `axonhub:routing:previous-channel:v1:trace|thread:<id>`；
  `RememberAsync(channelId)` 覆盖并重置 TTL，`GetAsync()` 返回通道 id 或 0。
- 新增 `PreviousChannelGrain`（AxonHub.Grains）：30 分钟 TTL（`PreviousChannelOptions.DefaultTtl`）、
  读取时惰性过期；`DelayDeactivation(TTL + 1 分钟)` 保证激活活到条目过期（Orleans 默认回收年龄
  15 分钟短于 TTL）。
- 新增 `GrainPreviousChannelProvider`（AxonHub.Api）：生产 `IPreviousChannelProvider`；把 trace /
  thread id 映射为参照键，写回规则与参照一致（traceId / channelId 零值过滤、thread > 0 才写）；
  同步桥接 grain 调用（中间件与选择器同步，C018）；读取失败记 warning 并返回 0，写入失败丢弃。
- 接线：`Program.cs` 注册 grain 提供者与默认 `PreviousChannelOptions`；`PreviousChannelCache`
  保留为直接构造 / 测试用实现，TTL 常量引用 `PreviousChannelOptions.DefaultTtl`。
- 测试基建：两个 `GrainClusterFixture` 注册 `PreviousChannelOptions`；Grains 夹具把 TTL
  缩短为 1 秒，过期可观察。

## 行为要点

- 主链等价：键形状、trace / thread 双写条件、每键覆盖、30 分钟 TTL、惰性过期、未命中返回 0
  且不查历史请求、写入错误丢弃、读取错误按未命中处理。
- 集群语义：一个键一个激活，Orleans 串行化同一键的读写；参照默认 `cache.mode=memory` 也是
  进程内，端口在集群内共享，强于默认模式。
- 残余差异：Redis / two-level xcache 后端不支持（与 C171 同源）；grain 状态在 silo 重启后
  丢失（与参照 memory 模式相同）。
- 延迟：每次粘性解析与写回多一次 grain 调用（单 silo 为进程内）。

## 测试

- `tests/AxonHub.Grains.Tests/PreviousChannelGrainTests.cs`（4）：未写键返回 0、按键独立存储、
  后写覆盖、TTL 过期（夹具 TTL 1 秒）。
- `tests/AxonHub.Api.Tests/GrainPreviousChannelProviderTests.cs`（5）：trace / thread 双写、
  按作用域覆盖、零值忽略、null thread 仅 trace、未命中返回 0。
- 既有 `PreviousChannelCacheTests`（6，进程内实现）与 `StickyChannelSelectionTests`（9）不变。

静态计数：`AxonHub.Grains.Tests` 1712 → 1716（+4），`AxonHub.Api.Tests` 5440 → 5445（+5），
`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 336 不变，四项目合计 8994 → 9003。
**按项目规则未运行 lint / build / test。**

台账：基线 §147、验收 §147 已写入；C181 由 accepted 转为 resolved（残余：Redis 后端、
silo 重启丢失）；`02-differences.md` 重新生成。
