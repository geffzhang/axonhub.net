# §123 Synthetic 供应商配额检查器（固定 /v2/quotas 端点 + 双周窗口 + search 计数）

日期：2026-11-07
参照：`f272549f:internal/server/biz/provider_quota/synthetic_checker.go`（`CheckQuota`:84-118、
`parseResponse`:120-193、`weeklyLimit`:200-220、`SupportsChannel`:222-228、`buildSyntheticQuotaURL`:230-247、
`buildSyntheticLimitStatuses`:249-290、`searchHourlyLimitStatus`:302-331、`weeklyTokenLimitStatus`:333-388、
`parseSyntheticCredits`:392-408、`findEarliestSyntheticResetAt`:410-444、七个 map 转换器:446-574、
报文类型:19-72）、`provider_quota.go:446-448` 的 `registerSyntheticSupport`（在 `registerClineSupport`
之后、`registerNeuralWattSupport` 之前调用，见 `provider_quota.go:383`），以及
`synthetic_checker_test.go` 的 22 个测试函数。

## 交付

- `src/AxonHub.Api/Orchestration/SyntheticQuotaChecker.cs`：`IQuotaChecker` 实现 + `SyntheticUsageResponse`
  系列的 Go 语义 JSON 解码 + `time.Parse(time.RFC3339)` 复用共享 `GoTimeParsing` 解析器。
  - 凭据：`strings.TrimSpace(credentials.apiKey)`；为空且 `apiKeys` 非空时直接取 `APIKeys[0]`
    （**不 trim**），都为空（无凭据 / 非字符串 / 空白）报 `channel has no API key`（C013）。
  - 采集：GET `buildSyntheticQuotaURL(BaseURL)`，头为 `Content-Type: application/json`、
    `Authorization: Bearer <key>`，无 body；`settings.proxy` 存在时**一次**解析代理 executor，畸形
    settings 视为无代理（C013）。
  - URL：trim 后为空、`url.Parse` 失败或无 scheme + host 回退 `https://api.synthetic.new/v2/quotas`；
    `http` 升级 https；只保留 `scheme://host`，固定 path `/v2/quotas`（base 的 path / query 丢弃）。
  - 响应：`Do` 错误包装为 `quota request failed: %w`；`StatusCode != 200` 报 `HTTP <code>: <body>`；
    解码失败报 `failed to parse synthetic usage response: %w`（Go 解码文案沿用 C003 / C093）。
  - 限额：先 five-hour（`limited=true` → exhausted / ratio 1.0；否则 `tickPercent` → ratio，>= 0.8 warning；
    reset 取 `nextTickAt`），再 weekly（ratio = 1 − `percentRemaining` / 100，>= 1 exhausted、>= 0.8 warning，
    reset 取 `nextRegenAt`；credits 可解析且 max > 0 时给出 `PeriodCost = max(used, 0)` 与
    `PeriodQuota = max`）；search hourly 只追加展示（`type=search`、window `search_hourly`），不参与状态
    升级与 reset 聚合。
  - weekly 合并：`weeklyLimit()` 在两种形状都存在时复制 `weeklyTokenLimit`，plan 的非空 `nextRegenAt` /
    `percentRemaining` 逐字段覆盖，credits 始终来自 `weeklyTokenLimit`。
  - 总状态：无任何 limit 时 unknown；否则 available，按 exhausted（break）/ warning 升级；`nextResetAt`
    取 subscription `renewsAt`、合并后 weekly `nextRegenAt`、five-hour `nextTickAt` 的最早有效值
    （search 排除），解析失败忽略；随后 `NormalizeQuotaData` 归一化。
  - rawData：仅在指针非空时输出 `subscription` / `search` / `freeToolCalls` / `weeklyTokenLimit` /
    `planWeeklyLimit` / `rollingFiveHourLimit`，值保留零值与 `limited:false`，按 Go map 排序输出
    （`GoJson`；double 格式沿用 C067）。
- `src/AxonHub.Api/Program.cs`：在 cline 之后注册 `SyntheticQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("synthetic", …)`，与参照 `registerSyntheticSupport` 的顺序一致。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档同步为「Claude Code、Codex、
  Antigravity、xAI Subscription、GitHub Copilot、NanoGPT、ZenMux、Cline 与 Synthetic 已注册，其余 10 个
  后续切片」。
- `tests/AxonHub.Api.Tests/SyntheticQuotaCheckerTests.cs`：40 个 `[Fact]` + 19 个 `[Theory]`
  （169 个 InlineData），共 209 例。

## 行为要点

- 固定端点 happy path：URL、Bearer、无 body、三条 limit 的顺序与类型（five-hour / weekly / search）、
  ratio 0.05 / 0.614、PeriodQuota 36 / PeriodCost 22.1、search reset 仅展示、`nextResetAt` 取
  `nextTickAt`、rawData 逐字节排序输出均被断言。
- 线上形状与请求数据分离：请求数据保留 `Content-Type: application/json` 与 Bearer Auth；bodyless GET
  在线上无 Content-Type（C206），共享执行器补默认 `Accept: application/json`（C209）。
- search 永不阻断：exhausted / warning 的 search 计数不参与总体状态与 `nextResetAt`，只有 five-hour
  与 weekly 升级状态。
- weekly 两种形状按字段合并：plan 只覆盖非空 `nextRegenAt` / `percentRemaining`，credits 保留；
  mixed shapes 的 channel 状态、Ready 与路由结果逐组断言。
- 归一化复核：`tickPercent=1` 先 warning 后经 `NormalizeQuotaData` 转为 exhausted；负 ratio 的 limit
  被丢弃但显式 available 保留；`percentRemaining > 100` 丢 limit 且保留显式 reset。
- 错误路径：401 / 403 / 429 / 500 由共享执行器抛错并包装为 `quota request failed: ...`；1xx / 3xx /
  非 200 2xx 走 `HTTP <code>: <body>`；transport 错误包装；畸形 JSON 报解码前缀。
- 端口差异见 C209（共享执行器接缝、>=400 包装、非法 UTF-8 body、默认 Accept、GoUrl 子集、亚 100ns
  截断与 DateTimeOffset 范围、Go 解码文案）。

## 测试

- `tests/AxonHub.Api.Tests/SyntheticQuotaCheckerTests.cs`：40 例 + 169 个 InlineData，共 209 个用例。
  - 线路形状（URL / Authorization / 线上无 Content-Type / 默认 Accept / 无 body）与请求数据形状。
  - 三条 limit、PeriodQuota / PeriodCost、search 只展示 / 阈值 / 不可用计数器、raw counter 零值与 false、
    credits 10 组与省略 20 组、`parseSyntheticCredits` 22 组。
  - plan weekly 三种形状、mixed shapes 3 组、`weeklyLimit` 合并 3 组、warning / exhausted、five-hour
    7 组、weekly 7 组、tick=1 归一化、负 ratio 丢弃、percent>100、空 builder。
  - reset 聚合 4 组 / 非法 1 组、逗号小数与 100ns 截断、DateTimeOffset 范围、<= now 清除。
  - 凭据 4 组 + 拒绝矩阵 7 组、畸形 JSON、执行器状态 4 组 / 传输 1 组 / 1xx-3xx-2xx 4 组、自定义 base、
    `buildSyntheticQuotaURL` 19 组、`SupportsChannel` 11 组。
  - Go 解码：根 null / 非对象 5 组、类型不匹配 29 组、重复键合并、null 重置、未知字段、空对象；代理
    路由 3 组。

`AxonHub.Api.Tests` 由 3786 增至 3995（+209），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
7232 增至 7441。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]`
的静态计数；下一次全量运行应以 3995 / 319 / 7441 复核。**

差异：C182 由剩余 11 个 checker 收窄为 10 个；新增 C209（共享执行器接缝、>=400 包装为
`quota request failed`、非法 UTF-8 错误 body 变为 U+FFFD、默认 Accept、bodyless GET 的 Content-Type
沿用 C206、URL host 为 GoUrl 子集、reset 亚 100ns 截断与 DateTimeOffset 范围沿用 C123）。
