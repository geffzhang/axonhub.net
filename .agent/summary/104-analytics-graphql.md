# §104 Analytics 页 GraphQL（四个根字段）

日期：2026-11-05
参照：`2851a192:internal/server/gql/analytics.graphql`、`analytics.resolvers.go`、
`analytics_helpers.go`、`internal/authz/scope.go`、`internal/server/biz/performance.go`、
`internal/server/biz/system.go:1499-1546`（仅 analytics 切片局部跟进 2851a192，
其余子系统仍以 `b8bb3421` 为基线，见 00-reference.md 与 C194）

## 交付

- `src/AxonHub.Persistence/Stores/AnalyticsStore.cs`：overview / daily / dimension /
  performance 四组 Dapper 聚合；`AppendFilter` 复刻 buildAnalyticsWhere（含
  `apiKeyIDs` 为空 + 用户过滤 → SQL FALSE）；日分组按参数绑定的时区名
  `to_char(created_at AT TIME ZONE @tz::text, 'YYYY-MM-DD')`；维度 join 与
  `::text` id、性能取每 (request, channel) 最新 completed 尝试。
- `src/AxonHub.Api/Settings/SystemTimeLocations.cs`：共享时区解析（缺失 / 畸形 /
  未知回退 UTC；`Local` → TimeZoneInfo.Local 且名称 "Local"），配额切片复用。
- `src/AxonHub.Api/Management/AnalyticsService.cs`：`read_dashboard` 作用域决策
  （测试 Principal / API key scopes / owner 与系统 role / 项目 scope / default
  deny）、过滤解析（显式 ∩ 用户 key，解析失败吞掉并按未命中处理）、维度名称
  合成、日桶 startOfDay 补零（缺省今天 - 29 天）。
- `src/AxonHub.Api/GraphQL/AnalyticsTypes.cs`：AnalyticsFilter 输入与四个输出类型、
  映射；`startTime` / `endTime` / `earliestTime` 使用 `GoTimeType`。
- `src/AxonHub.Api/GraphQL/GraphQLSetup.cs`：显式注册
  `InputObjectType<AnalyticsFilterInput>` 名称为 `AnalyticsFilter`
  （HotChocolate 对解析器参数推断的输入对象忽略 `[GraphQLName]`）。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：四个根字段接线。
- `src/AxonHub.Api/Program.cs`：`IAnalyticsStore` / `AnalyticsService` 注册。

## 行为要点

- 边界：下界 `>=`、上界 `<=`（毫秒精度）；无过滤条件不加谓词。
- metadata 不做 scope 决策，走 `TracePrivacy.EvaluateUsageLogQueryAsync`
  （项目作用域 + 逐行可见性）；overview / daily / dimension 被允许的调用绕过
  UsageLog 隐私链（无项目过滤），被拒绝时统一附加 `ent/privacy: deny rule`，
  GraphQL 错误扩展 `code = FORBIDDEN`。
- dimension 先在作用域判定前解析过滤并校验维度名（与参照的解析顺序一致），
  未知维度文案 `unsupported dimension: <name>, must be one of: channel, model,
  apiKey, user`。
- 维度排序均为 `total_tokens DESC`；apiKey 名称按未过滤删除的 `api_keys` 读取，
  缺失回退 `API Key #<id>`；user 名称 `TrimSpace(first + " " + last)`，空则 email
  （内部空格保留）。
- 性能：生成延迟 = 流式且有 TTFT ? latency - ttft : latency，下限 10ms；
  tokensPerSecond = SUM(completion_tokens) * 1000 / SUM(生成延迟)；
  ttftMs = 流式且 ttft > 0 的均值，无样本为 null。

## 顺带修复

- overview 聚合最初复用分组查询的 `request_count` / `input_tokens` 别名，
  `AnalyticsOverviewRow` 的 Total* 属性无法映射（结果为 0）；改为专用
  `total_*` 别名。
- `GetPerformanceAsync(query, dimension)` 参数顺序修正。
- 用户维度名称测试期望改为保留内部双空格（参照 TrimSpace 只裁剪两端）。

## 测试

- `tests/AxonHub.Api.Tests/AdminAnalyticsEndpointTests.cs`：10 例（四个根字段线形、
  最早时间与隐私链、过滤器映射与交集、空用户过滤 FALSE、时区日桶与缺省 30 天、
  维度名称合成与性能合并、未知维度文案、read_dashboard 拒绝 FORBIDDEN、
  存储失败前缀）。
- `tests/AxonHub.Persistence.Tests/AnalyticsStoreTests.cs`：4 例（闭区间与毫秒
  边界、空聚合零值、维度 join / 名称 / key 解析、性能最新尝试与 10ms 下限）。
- 定向复跑：Api 10/10、Persistence 4/4 通过。
- 整解决方案复跑：Llm 3032、Grains 95、Persistence 244、Api 2653，合计 6024，
  0 失败 0 跳过；本轮未复现 §103 记录的 Api testhost 随机崩溃。

## 差异

- C194（accepted）：analytics 切片局部跟进 2851a192；ent selector 改为直接
  Dapper SQL；仅保留 PostgreSQL 日期分支；时区名参数绑定；`::text` id 转换；
  `checked((int))` 取代 safeIntFromInt64 的静默截断；作用域在查询前显式求值；
  时区按请求读取（C186 同源）。
