# §122 Cline 供应商配额检查器（ClinePass 用量限额 + 账本回退）

日期：2026-11-07
参照：`f272549f:internal/server/biz/provider_quota/cline_checker.go`（`CheckQuota`:248-320、
`getJSON`:322-355、`clineNativeHTTPClient`:357-362、`clineHTTPError`:364-377、`clineAPIKey`:379-392、
`buildClineQuotaURL`:394-423、`classifyClineModelScope`:425-457、`selectClinePassThreshold`:459-482、
`fetchUsageLimits`:484-503、`fetchUsageItems`:505-553、`clineLedgerErrorCode`:555-565、
`oldestClineUsageTime`:567-581、`parseClineTime`:583-595、`parseClineUsageLimits`:597-673、
`clineUsageLimitWindowKey`:675-686、`buildClineQuotaData`:688-747、`buildClinePassUnavailableQuota`:749-779、
`buildClineDirectOnlyQuota`:781-795、`buildClineWindow`:797-919、`alignClineWindowStart`:921-945、
`clineWindowStatus`:947-960、`worstClineStatus`:962-968、`worseQuotaStatus`:970-977、`clineWindowLabel`:979-990、
`clineLimitStatuses`:992-1018、`earliestClineWindowReset`:1020-1034、`clineWindowsRawData`:1036-1075、
`clineUsageLimitsFetchRawData`:1077-1085、`clineBalanceRawData`:1087 起、报文类型:64-235）、
`provider_quota.go:442-444` 的 `registerClineSupport`，以及 `cline_checker_test.go` 的 30 个测试函数。

## 交付

- `src/AxonHub.Api/Orchestration/ClineQuotaChecker.cs`：`IQuotaChecker` 实现 + `clineEnvelope<T>`
  系列的 Go 语义 JSON 解码 + `time.Parse(time.RFC3339)` 复用共享 `GoTimeParsing` 解析器。
  - 凭据：`strings.TrimSpace(credentials.apiKey)`；为空时遍历 `credentials.apiKeys` 取首个 trim
    非空项，都为空（无凭据 / 非字符串 / 空白）报 `channel has no API key`（C013）。
  - 采集：GET `/api/v1/users/me` → `/api/v1/plans` → `/api/v1/users/{id}/balance`（id 经
    `url.PathEscape`）；direct_only 提前返回；其余范围无 active ClinePass threshold 报
    `Cline plans response does not include an active ClinePass threshold`；随后 GET
    `/api/v1/users/me/plan/usage-limits`（404 → pass-unavailable），再 GET
    `/api/v1/users/{id}/usages?limit=200`（cursor 非空才带 `cursor`）。请求为 GET，头为
    `Accept: application/json`、`Authorization: Bearer <key>`、`User-Agent: axonhub/1.0`，无 body；
    `settings.proxy` 存在时**一次**解析代理 executor，畸形 settings 视为无代理（C013）。
  - URL：trim 后为空或 `url.Parse` 失败 / 无 host 回退 `https://api.cline.bot`；scheme 空或 `http`
    升级 https；只保留 scheme + host（base 的 path / query 丢弃）；path 按 Go `url.URL{Path}` 转义
    （保留字符转义、`%` 二次转义），query 经 `url.Values.Encode` 按键排序（复用 `GoUrl.TryParse`）。
  - getJSON：请求构造失败 `failed to create Cline quota request`；transport / nil 响应
    `Cline quota request failed during transport`；状态不在 [200,300) 丢弃 body 并报
    `HTTP <code> <StatusText>`（无文案时 `HTTP <code>`）；读取失败 `failed to read Cline quota response`；
    解码失败 `failed to parse Cline quota response`（Go 解码文案沿用 C003 / C093）。identity / plans /
    balance 错误分别包装为 `failed to read Cline user identity|plans|balance: %w`；id trim 后为空报
    `Cline user identity response missing id`。
  - 范围 / threshold：`supportedModels` + `manualModels` 逐个 trim，`cline-pass/` 前缀（大小写敏感）
    计入 pass 池，其余计入 direct 池；mixed / cline_pass_only / direct_only / unknown 四态。plans 中
    跳过 type 非 `cline_pass`、disabled、threshold nil 或 inactive 的条目，取首个符合条件的 threshold；
    summaries 为每个符合条件 plan 的 `{type, interval}`。
  - usage limits：三窗口（last5h / last7d / last30d）各取首个有效 ratio（percent/100 截断到 [0,1]）；
    reset 状态机 missing → unavailable、null / 空白 → inactive、非字符串 → invalid，且只在空 /
    unavailable / invalid 时被后值覆盖；inactive 且 ratio 为 0 / 缺失保持 inactive，inactive 但
    ratio > 0 降为 unavailable；active reset 用 RFC3339 解析，失败标 invalid；只有 ratio 非 null 或
    状态非 unavailable 的条目进入结果。无可用数据报
    `failed to read Cline usage limits: response contains no usable window data`。
  - 账本：cutoff = now − 30 天，每页 `limit=200`，最多 100 页；空 cursor / 空页 / 最旧条目早于 cutoff
    停止，打满 100 页置 `truncated`；按 trim 后的 `aiModelTypeName` 计数 cline-pass / 空（unclassified）/
    其他（direct），无效 `created_at` 另行计数。账本失败非致命：`ledger_unavailable=true` 且
    `ledger_error_code` 为 `rate_limited` / `http_<code>` / `transport`，cost 标为不可用，官方窗口仍生效。
  - 窗口 / 组装：官方 ratio 与 reset 优先；active reset 必须 `reset > now &&
    reset <= now + duration + 2s`；cost 仅在未 truncated / 账本可用时计算，起点对齐到官方窗口起点
    ±2s 内最近的 cline-pass 条目，只累计起点之后、reset 之前且模型为 `cline-pass` 的条目（空模型
    清零并停止，其他模型忽略）；costRatio 仅 limit > 0 时给出，官方 ratio 缺省时用 costRatio 补。
    三个 token limit（5h / 7d / 30d），`PeriodStart = reset − duration`，整体取三窗口最差；pass_only
    允许 exhausted，mixed / direct 范围的 pass 池耗尽降为 warning 且 basis 为
    `mixed_pool_pass_exhausted`，随后经 `ProviderQuotaData.Normalize` 复核。pass-unavailable 分支
    **不归一化**：pass_only → exhausted、mixed → warning + `cline_pass_unavailable_mixed_pool`、
    其余 → warning + `cline_pass_unavailable`，rawData 无 `cost_scale` / `windows` / `usage_fetch`，
    另加 `pass_state=unavailable`。direct-only 为 available，rawData 只含 `model_scope` /
    `status_basis` / `pool` / `pool_note` / `balance` / `plans`。
  - rawData：`model_scope`、`status_basis`、`pool`、`pool_note`、`cost_scale`（100000000）、`balance`、
    `plans`、`windows`、`usage_fetch`、`usage_limits_fetch`，按 Go map 排序输出（`GoJson`；double
    格式沿用 C067）。
- `src/AxonHub.Api/Program.cs`：在 zenmux 之后注册 `ClineQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("cline", …)`，与参照 `registerClineSupport` 的顺序一致。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档同步为「Claude Code、Codex、
  Antigravity、xAI Subscription、GitHub Copilot、NanoGPT、ZenMux 与 Cline 已注册，其余 11 个后续切片」。
- `tests/AxonHub.Api.Tests/ClineQuotaCheckerTests.cs`：44 个 `[Fact]` + 13 个 `[Theory]`
  （56 个 InlineData），共 100 例。

## 行为要点

- 五请求 happy path：三条 token limit 的官方 ratio / reset、PeriodStart（reset − duration）与
  rawData 的 `cost_scale` / 账本 cost 字段逐值一致；usage_fetch 的 meta 计数（pages / items_seen /
  cline_pass_items_seen / direct_items_seen / unclassified_items_seen / invalid_timestamp_items /
  truncated / ledger_unavailable）与官方 usage_limits_fetch 状态（complete 3/3/3/6）均被断言。
- usage-limits 404 按 scope 分支：pass_only → exhausted / Ready=false；mixed / unknown → warning /
  Ready=true，basis 分别为 `cline_pass_unavailable_mixed_pool` / `cline_pass_unavailable`，且无
  windows / usage_fetch / cost_scale。identity 404 与 usage-limits 其它非 404 状态仍使检查失败。
- direct_only 只发三个请求（不取 usage-limits / usages），balance 仅作信息保留；直接构造的
  direct-only rawData 与参照逐字节一致。
- 账本失败（transport / 429）不影响官方窗口：检查仍 available，`ledger_unavailable` 与
  `ledger_error_code` 记录原因，错误文本不含 key / 用户 id / cursor。
- 端口差异见 C208（共享执行器接缝、1 MiB 截断近似、GoUrl 子集、亚 100ns 截断与 DateTimeOffset
  范围、organizations 数字范围）。

## 测试

- `tests/AxonHub.Api.Tests/ClineQuotaCheckerTests.cs`：44 例 + 56 个 InlineData，共 100 个用例。
  - 五请求线路形状（URL / 头 / 无 Content-Type / body）、三条 limit、PeriodStart、rawData 全键。
  - 404 三 scope、identity 404、usage-limits 非 404 四状态、503 安全错误、partial fallback、
    畸形字段、direct-only、凭据矩阵（8 组）、缺 threshold、空 limits、missing id 三行。
  - 账本：transport 失败（6 次调用，检查仍可用）、429 rate_limited（官方值保留、无 cost 字段）。
  - getJSON：403 body 省略、transport、读取失败、解码失败、1xx / 3xx、1 MiB 上限后解析失败。
  - 解析器：meta complete / partial / unusable、reset 状态机理论、重复键与精确键、inactive-at-zero、
    me 信封合并、organizations 非数组、balance 指针、plans nil / 空。
  - 构建器：官方值、桶边界对齐、零 percent、每种 duration、unclassified 隐藏 cost、inactive、
    过期 reset、truncated、窗口状态理论、最差状态、80% warning、100% exhausted、mixed 降级、
    direct-only 精确 JSON。
  - URL（含 `%3F` → `%253F` 与排序 query）、scope 分类、threshold 选择、`parseClineTime` 8 组、
    账本错误码、分页（cursor + 100 页上限）、代理路由（代理 / 畸形 settings / 无 settings）、
    `SupportsChannel` 理论。

`AxonHub.Api.Tests` 由 3686 增至 3786（+100），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
7132 增至 7232。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]`
的静态计数；下一次全量运行应以 3786 / 319 / 7232 复核。**

差异：C182 由剩余 12 个 checker 收窄为 11 个；新增 C208（共享执行器接缝、读取错误 / 取消分类、
1 MiB 截断近似、URL host 为 GoUrl 子集、reset 亚 100ns 截断与 DateTimeOffset 范围沿用 C123、
organizations 数字范围不校验）。
