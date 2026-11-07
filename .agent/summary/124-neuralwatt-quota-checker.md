# §124 NeuralWatt 供应商配额检查器（固定 /v1/quota 端点 + 月度 kWh 订阅窗口）

日期：2026-11-07
参照：`f272549f:internal/server/biz/provider_quota/neuralwatt_checker.go`（`CheckQuota`:50-84、
`parseResponse`:86-150、`SupportsChannel`:152-158、`isNeuralWattLowRemaining`:160-166、
`buildNeuralWattQuotaURL`:168-185、两个 map 转换器:187-237、报文类型:19-38、`warningRemainingFraction`:17）、
`provider_quota.go:450-452` 的 `registerNeuralWattSupport`（在 `registerSyntheticSupport` 之后、
`registerApertisSupport` 之前调用，见 `provider_quota.go:384`），以及 `neuralwatt_checker_test.go` 的
12 个测试函数。

## 交付

- `src/AxonHub.Api/Orchestration/NeuralWattQuotaChecker.cs`：`IQuotaChecker` 实现 + `NeuralWattUsageResponse`
  系列的 Go 语义 JSON 解码 + `time.Parse(time.RFC3339)` 复用共享 `GoTimeParsing` 解析器。
  - 凭据：`strings.TrimSpace(credentials.apiKey)`；为空且 `apiKeys` 非空时直接取 `APIKeys[0]`
    （**不 trim**），都为空（无凭据 / 非字符串 / 空白）报 `channel has no API key`（C013）。
  - 采集：GET `buildNeuralWattQuotaURL(BaseURL)`，头为 `Content-Type: application/json`、
    `Authorization: Bearer <key>`，无 body；`settings.proxy` 存在时**一次**解析代理 executor，畸形
    settings 视为无代理（C013）。
  - URL：trim 后为空、`url.Parse` 失败或无 scheme + host 回退 `https://api.neuralwatt.com/v1/quota`；
    `http` 升级 https；只保留 `scheme://host`，固定 path `/v1/quota`（base 的 path / query 丢弃）。
  - 响应：`Do` 错误包装为 `quota request failed: %w`；`StatusCode != 200` 报 `HTTP <code>: <body>`；
    解码失败报 `failed to parse neuralwatt usage response: %w`（Go 解码文案沿用 C003 / C093）。
  - 状态：无 subscription 或关键字段不足 → unknown；`in_overage == true` → exhausted（优先于正
    remaining）；`kwh_remaining <= 0` → exhausted；`kwh_remaining < kwh_included × 0.2`（严格小于且
    `included > 0`）→ warning；其余 `kwh_remaining` 非空 → available。
  - 用量比例：仅当 `kwh_included > 0`；`kwh_used` 非空时 `used / included`（优先），否则
    `kwh_remaining` 非空时 `1 - remaining / included`，否则 0。
  - 限额：恒为**一个** token limit，window 为 `kwh`；`kwh_reset_date` 解析失败忽略，`NextResetAt`
    与 `PeriodStartFromMonthlyReset`（上月同日、按上月最后一天夹紧并保留 offset 与亚毫秒 tick），
    随后 `NormalizeQuotaData` 归一化（ratio 截断、负数 limit 丢弃、过期 reset 清除、显式 available
    在 limit 被丢弃或 exhausted 后仍保留）。
  - rawData：仅在指针非空时输出 `balance` / `subscription`，字段按 snake_case 键名逐项省略 nil，
    保留零值、`false` 与空字符串，按 Go map 排序输出（`GoJson`；double 格式沿用 C067）。
- `src/AxonHub.Api/Orchestration/ProviderQuotaData.cs`：修正共享 `PeriodStartFromMonthlyReset`，改用
  构造后 `.Add(reset.TimeOfDay)`，保留亚毫秒 tick（此前 `reset.Millisecond` 会截断；Go 的
  `time.Date(..., Nanosecond, ...)` 保留完整纳秒）。该 helper 同时服务 xAI Subscription 的月度窗口。
- `src/AxonHub.Api/Program.cs`：在 synthetic 之后注册 `NeuralWattQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("neuralwatt", …)`，与参照 `registerNeuralWattSupport` 的顺序一致。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档同步为「Claude Code、Codex、
  Antigravity、xAI Subscription、GitHub Copilot、NanoGPT、ZenMux、Cline、Synthetic 与 NeuralWatt
  已注册，其余 9 个后续切片」。
- `tests/AxonHub.Api.Tests/NeuralWattQuotaCheckerTests.cs`：31 个 `[Fact]` + 12 个 `[Theory]`
  （96 个 InlineData），共 127 例。

## 行为要点

- 固定端点 happy path：URL、Bearer、无 body、单一 kWh limit、`used / included` 比例、无 reset 时
  PeriodStart 为 nil、rawData 逐字节排序输出均被断言。
- 线上形状与请求数据分离：请求数据保留 `Content-Type: application/json` 与 Bearer Auth；bodyless GET
  在线上无 Content-Type（C206），共享执行器补默认 `Accept: application/json`（C210）。
- 状态优先级：overage 优先于正 remaining；`remaining <= 0` 为 exhausted；20% 边界为严格小于
  （`remaining == included × 0.2` 仍为 available）；`included <= 0` 不触发 warning；缺 remaining 时
  为 unknown（即使 plan / status 存在）。
- 归一化复核：`used > included` 时 ratio 截断为 1 且 limit exhausted，但显式 available 在总体状态
  中胜出；负 ratio 的 limit 被丢弃而显式 available 保留；过期 / 非法 reset 被清除。
- 月度周期起点：1 月 31 日回退到 12 月 31 日；3 月 30 / 31 日夹紧到 2 月 28 / 29 日（2096 闰年、
  2100 非闰年）；`+05:30` 与 `+02:00` 保留 offset；`.123456789Z` 截断为 1234567 tick，PeriodStart
  同步保留。
- 错误路径：401 / 403 / 429 / 500 由共享执行器抛错并包装为 `quota request failed: ...`；1xx / 3xx /
  非 200 2xx 走 `HTTP <code>: <body>`；transport 错误包装；畸形 JSON 报解码前缀。
- 端口差异见 C210（共享执行器接缝、>=400 包装、非法 UTF-8 body、默认 Accept、GoUrl 子集、亚 100ns
  截断与 DateTimeOffset 范围、Go 解码文案）。

## 测试

- `tests/AxonHub.Api.Tests/NeuralWattQuotaCheckerTests.cs`：31 例 + 96 个 InlineData，共 127 个用例。
  - 线路形状（URL / Authorization / 线上无 Content-Type / 默认 Accept / 无 body）与请求数据形状。
  - available / warning / missing reset / overage exhausted / zero remaining / unknown 六种基线；
    状态优先级 11 组；usageRatio 优先级 7 组（含科学计数法）；ratio>1 截断；负 ratio 丢弃。
  - 月度 PeriodStart 7 组（含闰年、非闰年、偏移）、100ns tick、offset 保留、非法 reset 5 组、
    过期 / 当前 reset 2 组。
  - rawData 全文 / 空 balance / 空 subscription / 零值与 false / 重复 balance 合并 / 显式 null。
  - 凭据 4 组 + 拒绝矩阵 7 组、畸形 JSON、执行器状态 4 组 / 传输 1 组 / 1xx-3xx-2xx 4 组、自定义 base、
    `buildNeuralWattQuotaURL` 19 组、`SupportsChannel` 12 组。
  - Go 解码：根 null / 非对象 4 组、类型不匹配 14 组、重复键合并、null 重置、未知字段；代理路由 3 组。

`AxonHub.Api.Tests` 由 3995 增至 4122（+127），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
7441 增至 7568。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]`
的静态计数；下一次全量运行应以 4122 / 319 / 7568 复核。**

差异：C182 由剩余 10 个 checker 收窄为 9 个；新增 C210（共享执行器接缝、>=400 包装为
`quota request failed`、非法 UTF-8 错误 body 变为 U+FFFD、默认 Accept、bodyless GET 的 Content-Type
沿用 C206、URL host 为 GoUrl 子集、reset 亚 100ns 截断与 DateTimeOffset 范围沿用 C123、
`PeriodStartFromMonthlyReset` 的 offset 与亚毫秒 tick 语义）。
