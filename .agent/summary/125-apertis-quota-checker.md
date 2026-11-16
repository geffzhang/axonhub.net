# §125 Apertis 供应商配额检查器（固定 /v1/dashboard/billing/credits 端点 + 订阅周期 / PAYG / credits 三族额度）

日期：2026-11-07
参照：`f272549f:internal/server/biz/provider_quota/apertis_checker.go`（`CheckQuota`:77-121、
`parseResponse`:123-158、`SupportsChannel`:160-162、`buildApertisQuotaURL`:165-189、
`determineApertisStatus`:192-229、`determineSubscriptionStatus`:231-250、`determinePaygStatus`:252-281、
`betterStatus`:283-288、`buildApertisLimits`:290-387、`parseApertisCycleStart`:389-401、
`convertApertisResponseToMap`:403-450、`toFloat64`:452-477、报文类型:27-61、`betterStatusRank`:18-25、
`apertisAvailabilityGroup`:67）、`provider_quota.go:454-456` 的 `registerApertisSupport`（在
`registerNeuralWattSupport` 之后、`registerOpenCodeGoSupport` 之前调用，见 `provider_quota.go:385`），
以及 `apertis_checker_test.go` 的 24 个测试函数。

## 交付

- `src/AxonHub.Api/Orchestration/ApertisQuotaChecker.cs`：`IQuotaChecker` 实现 +
  `ApertisBillingCreditsResponse` / `ApertisPayg` / `ApertisSubscription` 的 Go 语义 JSON 解码 +
  `fmt.Sscanf("%f")` 的 token 扫描近似 + `time.Parse(time.RFC3339)` 复用共享 `GoTimeParsing` 解析器。
  - 凭据：`strings.TrimSpace(credentials.apiKey)`；为空且 `apiKeys` 非空时直接取 `APIKeys[0]`
    （**不 trim**）；都为空（无凭据 / 非字符串 / 空白）时返回 unknown 行（RawData
    `{"error":"missing API key"}`、Ready=false、NextResetAt=null）且 **error 为 nil**、不经过
    Normalize（C211）。
  - 采集：GET `buildApertisQuotaURL(BaseURL)`，头为 `Content-Type: application/json` 与
    `Authorization: Bearer <key>`，无 body；参照不使用 proxy，端口同样不解析代理。
  - URL：trim 后为空或 `url.Parse` 失败回退 `https://api.apertis.ai`；空 scheme 补 https；只保留
    `scheme://host`，固定 path `/v1/dashboard/billing/credits`（base 的 path / query 丢弃）；不做
    http→https 升级、空 host 不回退。
  - 响应：共享执行器把 >=400 抛为 `HttpClientError`，端口捕获后还原为参照的
    `apertis API returned status <code>`；非 200 且 <400（1xx / 2xx / 3xx）按同一文案抛出；transport
    错误原样传播；成功响应解码后进入 `ParseResponse`。
  - 状态：`betterStatusRank` available 3 / warning 2 / exhausted 1 / unknown 0。订阅状态先算：
    suspended / cancelled → exhausted；`cycle_quota_limit <= 0` → unknown；ratio 为 `used / limit`，
    remaining <= 0 → exhausted，ratio >= 0.8 → warning，否则 available。PAYG 状态：
    `token_is_unlimited` → available；`account_credits > 0` 时 total 有效且 > 0 则 ratio >= 1 →
    available、>= 0.8 → warning、其余 available；credits <= 0 时 total 无效或 <= 0 → unknown、否则
    exhausted。`shouldSkipPAYG` 仅当 `is_subscriber && subscription != nil &&
    !payg_fallback_enabled && status == "active"`（**区分大小写**）且 `cycle_quota_remaining > 0`；
    `shouldCheckPayg` 在非订阅、回退启用、suspended / cancelled、周期额度耗尽时为 true；最终状态取
    订阅与 PAYG 中排名更高者。
  - 限额：PAYG token limit（除非被 shouldSkipPAYG 跳过）、`account_credits > 0` 时的 credits limit、
    订阅周期 limit（subscriber 且 subscription 非空）三族，group 均为 `apertis_capacity`，window 分别
    为 `pay_as_you_go` / `credits` / `cycle`；订阅 limit 的 Type 显式设为 `subscription_cycle`，
    PeriodStart 取自 `cycle_start`（RFC3339，失败忽略）；`cycle_end` 解析成功时作为 NextResetAt；
    随后 `Normalize` 归一化（ratio 截断、负 ratio limit 丢弃、过期 reset 清除、显式状态在 limit 被
    丢弃或 exhausted 后仍保留）。
  - rawData：`is_subscriber`；`payg` 恒有 account_credits / token_used / token_total / token_remaining /
    token_is_unlimited 与可选的 token_monthly_limit_usd / token_monthly_used_usd / monthly_reset_day；
    `subscription` 恒有 plan_type / status / cycle_quota_limit / cycle_quota_used /
    cycle_quota_remaining / cycle_start / cycle_end / payg_fallback_enabled 与可选的 payg_spent_usd /
    payg_limit_usd；按 Go map 排序输出（`GoJson`；double 格式沿用 C067）。
- `src/AxonHub.Api/Program.cs`：在 neuralwatt 之后注册 `ApertisQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("apertis", …)`，与参照 `registerApertisSupport` 的顺序一致。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档同步为「Claude Code、Codex、
  Antigravity、xAI Subscription、GitHub Copilot、NanoGPT、ZenMux、Cline、Synthetic、NeuralWatt 与
  Apertis 已注册，其余 8 个后续切片」。
- `tests/AxonHub.Api.Tests/ApertisQuotaCheckerTests.cs`：54 个 `[Fact]` + 12 个 `[Theory]`
  （82 个 InlineData + 3 个 MemberData 共 51 行），共 187 例。

## 行为要点

- PAYG-only happy path：URL、Bearer、无 body、token limit 与 credits limit 两族、`apertis_capacity`
  group、rawData 逐字节排序输出均被断言；缺口 key 的 unknown 行不发起请求。
- 订阅优先：active 且周期额度未耗尽时跳过 PAYG 限额，只留订阅周期 limit（Type 为
  `subscription_cycle`、PeriodStart 为 cycle_start、NextResetAt 为 cycle_end）；`"Active"` 这类
  大小写变体不触发跳过（参照区分大小写）。
- 回退矩阵：suspended / cancelled 且 PAYG 有 credits → available；无 credits → exhausted；周期额度
  耗尽且 `payg_fallback_enabled` → 订阅 limit exhausted 但 PAYG 提供 available / warning；无回退 →
  exhausted；unlimited PAYG 不能救活 active 订阅（订阅被跳过时 PAYG 不参与）；非订阅者携带
  subscription 对象时订阅状态参与总体状态但不产出周期 limit。
- 状态优先级：订阅与 PAYG 取 `betterStatusRank` 更高者；`DetermineStatus` / `DetermineSubscriptionStatus` /
  `DeterminePaygStatus` / `BetterStatus` 各有独立数据表（19 + 11 + 21 + 4 组）。
- 归一化复核：负 token usage 使 PAYG limit 被丢弃而 credits limit 保留；`used > total` 时 ratio 截断
  为 1 且 limit exhausted；订阅 ratio > 1 同样截断；remaining 为 0 且 used < limit → exhausted；
  PAYG token limit 携带订阅周期 reset。
- 周期时间：RFC3339 解析成功才设置 NextResetAt；`.123456789Z` 截断为 1234567 tick；`+05:30` 保留
  offset；非法 / 空 cycle_end 清除 reset 与 PeriodStart；过期 cycle_end 被 Normalize 清除。
- 错误路径：401 / 403 / 429 / 500 由共享执行器抛错并还原为 `apertis API returned status <code>`；
  1xx / 3xx / 非 200 2xx 走同一文案；transport 错误原样传播；畸形 JSON 与 Go 风格类型不匹配文案；
  错误路径的配额数据在参照中与错误一起返回但调用方丢弃，端口只抛异常（C211）。
- 端口差异见 C211（共享执行器接缝、>=400 还原、缺失 key 的未归一化数据、默认 Accept / Content-Type
  C206、GoUrl 子集、Go 解码语义与报文文案、Sscanf 近似、嵌套 any 的 1e400、100ns 截断）。

## 测试

- `tests/AxonHub.Api.Tests/ApertisQuotaCheckerTests.cs`：54 例 + 82 个 InlineData + 51 个 MemberData
  行，共 187 个用例。
  - 线路形状（URL / Authorization / Content-Type / 默认 Accept / 无 body）与请求数据形状。
  - PAYG-only available / warning / exhausted、空源 unknown、订阅五种状态、suspended / cancelled
    回退、unlimited PAYG、非订阅 subscription 对象、大小写 `Active`、空 subscription 对象。
  - 直接状态函数 19 + 11 + 21 + 4 组；归一化与 ratio 截断 5 个基线。
  - 周期时间 6 组（含亚 tick、偏移、非法、空、过期）；rawData 全文 / 空 payg / 重复键合并 /
    显式 null 重置 / 未知字段。
  - 凭据 4 组、畸形 JSON、类型不匹配全文 19 组、执行器 >=400 4 组 / 1xx-3xx-2xx 4 组 / 传输 1 组、
    自定义 base、`buildApertisQuotaURL` 20 组、`SupportsChannel` 与 `SupportsChannelIgnoresTheBaseUrl`。
  - Go 解码：根 null、非对象根 4 组、结构体指针类型不匹配、重复对象合并、null no-op / 指针重置、
    ASCII 键折叠、any 形状、64 位 int、未知键；`TryToFloat64` / Sscanf 边界 5 组。

`AxonHub.Api.Tests` 由 4122 增至 4309（+187），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
7568 增至 7755。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` /
`MemberData` 的静态计数；下一次全量运行应以 4309 / 319 / 7755 复核。**
