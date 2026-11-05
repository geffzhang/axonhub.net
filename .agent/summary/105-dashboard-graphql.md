# §105 Dashboard 首页 GraphQL（21 个根字段）

日期：2026-11-05
参照：`f272549f:internal/server/gql/dashboard.graphql`、`dashboard.resolvers.go`、
`dashboard_cache.go`、`dashboard_helpers.go`、`dashboard_usage_aggregates.go`、
`internal/pkg/xtime`（仅 dashboard 切片局部跟进 f272549f；`dashboard.graphql`
与基线逐字节相同，其余子系统仍以 `b8bb3421` 为基线，见 00-reference.md 与 C195）

## 交付

- `src/AxonHub.Persistence/Stores/DashboardStore.cs`：17 个读取方法——状态计数、
  时间窗计数、usage 聚合（四个维度）、窗口 / all-time token 求和、API key 用量、
  日统计、top projects、通道执行、通道详情、fastest 通道 / 模型（PostgreSQL
  `DISTINCT ON`）、模型 / 通道性能、按用户用量。
- `src/AxonHub.Api/Management/DashboardService.cs`：21 个根字段的语义（缓存键、
  归一化、排序、置信度、日历周期、容错路径与错误前缀）。
- `src/AxonHub.Api/Management/DashboardCache.cs`：`cachedDashboardQuery` 的移植。
  作用域在读写缓存前求值；`now <= expiresAt` 命中；按 key 单飞，等待者按自身
  请求取消退出；计算体在独立 DI 作用域内以 2 分钟超时执行并二次查缓存。
- `src/AxonHub.Api/Management/DashboardTokenStatsCache.cs`：all-time 的
  soft TTL 1h / hard TTL 24h 快照。
- `src/AxonHub.Api/Management/CalendarPeriods.cs`：`xtime.GetCalendarPeriods` 的
  本地午夜 / 周一起点 / 月首月末。
- `src/AxonHub.Api/GraphQL/DashboardTypes.cs`：输出 / 输入类型与映射；
  `APIKeyTokenUsageStatsInput` 显式线缆名。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：21 个根字段接线。

## 行为要点

- TTL：1m（usage 聚合 / dailyRequestStats / channelSuccessRates）、5m
  （fastestChannels / fastestModels / 模型与通道性能）；进程内缓存，多副本各自冷算。
- 容错：DashboardOverview / RequestStats 失败返回零值；TokenStats 的 all-time
  hard TTL 内返回快照与 `lastUpdated`，同步重算失败追加非终止错误
  `Failed to aggregate all-time token statistics` +
  `extensions.code = TOKEN_STATS_ALL_TIME_AGG_FAILED`，三项归零且
  `lastUpdated: null`，失败不缓存。空表语义用 `RowCount == 0` 复现 Go 的
  SQL NULL → int 扫描错误。
- all-time 的 soft TTL 异步刷新使用 `context.Background()`，无用户上下文恒被
  隐私拒绝，端口省略（不可观察）。
- 归一化：channelSuccessRates 的 null / 空串 → day、精确 day/week/month 保留、
  其余 allTime、limit ≤ 0 不限量、详情含软删通道、`ChannelDisabled = status !=
  "enabled"`；fastest* 未知值 → day、缺省 5、上限 100、`sqlLimit = max(limit*4, 20)`。
- 置信度：请求数中位数；高 ≥ 1.5× 且 ≥ 500、中 ≥ 0.5×、否则低；分值降序再按
  吞吐降序；`filtered.Count >= limit` 时仅保留高 / 中；`topPerformersLimit = 6`。
- usage 聚合：窗口 null → allTime，键 `dashboard-usage:<dim>:<window>:<location>`，
  score 降序 → ID 升序 → Name 升序，top 10，totalTokens = Input + Output。
- 性能：模型 30 秒内部超时、30 天本地窗口、按请求量排名、TTFT 仅 > 0、空吞吐
  null；通道优先 `channel_probes`（端口缺席，见 C195）→ executions 回退、
  通道 id 升序、名称回退 `channel-<id>`。
- 其余：APIKeyTokenUsageStats 三条校验文案与可访问 key 过滤（每 key 前 3 模型）；
  UsageStatsByUser 的 owner / 项目校验与 custom RFC3339 窗口；
  TopRequestsProjects 的缺失项目跳过与 privacy 拒绝映射（`permission denied` /
  FORBIDDEN）。

## 测试

- `tests/AxonHub.Api.Tests/AdminDashboardEndpointTests.cs`：13 例——overview 计数
  与拒绝容错、all-time 失败错误与窗口和、hard TTL 内快照、channelSuccessRates
  归一化与软删详情、fastest 置信度与过量采样、usage 聚合窗口规范化与共享缓存、
  daily 最近 30 个本地日、模型性能排名、通道性能 id 升序与名称回退、
  usageStatsByUser owner 与 custom 窗口、apiKeyTokenUsageStats 校验与访问过滤、
  topRequestsProjects 跳过缺失与 FORBIDDEN、缺 read_dashboard 时缓存解析器全部
  失败。
- 定向复跑：Api 13/13 通过。
- 整解决方案复跑：Llm 3032、Grains 95、Persistence 244、Api 2666，合计 6037，
  0 失败 0 跳过。

## 差异

- C195（accepted / 真实缺口一处）：dashboard 切片局部跟进 f272549f；缓存 key
  省略 ent client 指针；省略恒失败的异步 soft 刷新；`channel_probes` 分支缺席
  （随 probe 子系统补齐）；仅支持 PostgreSQL `DISTINCT ON`；`checked((int))`
  32 位截断（C190）；7 位小数时间精度（C137）。
