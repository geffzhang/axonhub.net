# §116 Claude Code 供应商配额检查器（unified ratelimit headers）

日期：2026-11-07
参照：`f272549f:internal/server/biz/provider_quota/claudecode_checker.go:26-260`
（`CheckQuota`、`parseResponse`、`SupportsChannel`、`buildTokenLimit`、`getEndpointURL`、
`parseUnixTimestamp` / `parseFloat`）、`llm/transformer/anthropic/claudecode/constants.go` 的配额常量、
`provider_quota.go:406-407` 的 `registerClaudeCodeSupport`，以及 `period_window_test.go` 与
`provider_contract_fixtures_test.go` 中的 claudecode 用例。

## 交付

- `src/AxonHub.Api/Orchestration/ClaudeCodeQuotaChecker.cs`：`IQuotaChecker` 实现。
  - 请求：POST `getEndpointURL(BaseUrl)`，Bearer access token，携带 `anthropic-beta`（配额检查专用
    beta 串）、`anthropic-version`、`anthropic-dangerous-direct-browser-access`、`x-app: cli` 与
    `content-type: application/json`；body 为 one-token Haiku 请求
    `{"model":"claude-haiku-4-5","messages":[{"role":"user","content":"limit"}],"max_tokens":1}`。
  - 凭据：OAuth 对象优先（存在即不回落 legacy apiKey），否则把 legacy apiKey 当 OAuth JSON 解析；
    无凭据报 `channel has no credentials`，空 token 报 `channel credentials missing access token`，
    legacy 解析失败包装为 `failed to parse OAuth credentials: %w`。
  - 传输：`settings.proxy` 存在时经 `IProxyExecutorFactory` 建代理 executor，否则共享 executor；
    执行错误包装为 `HTTP request failed: %w`；非 200（低于 400 的分支）报 `HTTP %d: %s`。
  - 解析：unified status / representative claim / 5h / 7d / overage 三组窗口头 → 状态映射
    （allowed→available、throttled/rejected→exhausted、其余→unknown）、0.8 warning 阈值、5h / 7d
    token limits（带 5h / 7d period）、RawData 七字段，最后经 §114 归一化。
- `src/AxonHub.Llm/Json/GoCast.cs`：新增 `ParseFloatIgnoringError`，忠实还原 Go
  `v, _ := strconv.ParseFloat(s, 64)` 的“忽略错误”语义（语法错 0、上溢 ±Inf、下溢 ±0，保留特殊值 /
  十六进制浮点 / 下划线规则）。
- `src/AxonHub.Api/Program.cs`：注册 `ClaudeCodeQuotaChecker` 单例与
  `ProviderQuotaCheckerRegistration("claudecode", …)`，位置在 antigravity 之前，与参照注册顺序一致。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档改为「Claude Code 与
  Antigravity 已注册，其余 17 个 checker 后续切片」。
- `tests/AxonHub.Api.Tests/ClaudeCodeQuotaCheckerTests.cs`：13 个 `[Fact]` + 8 个 `[Theory]`
  （67 个 InlineData）。

## 行为要点

- 线路形状：base URL 为空时走默认 `https://api.anthropic.com/v1/messages`；仅去掉一个尾部 `/`，
  以 `/v1` 结尾补 `/messages`，否则补 `/v1/messages`。body 的 Go map 序列化按键排序。
- 凭据优先级：`oauth` 对象存在时即使 token 为空也不回退 legacy apiKey；legacy apiKey 内的
  `access_token` JSON 可直接使用。
- 解析：`Anthropic-Ratelimit-Unified-Status` 缺失或为空报 `missing quota headers`；overall available
  且 5h 或 7d utilization ≥ 0.8 升级为 warning（overage 不参与）；5h / 7d 窗口仅在三个头任一存在时
  生成 token limit（否则不虚构窗口）；utilization ≥ 1 为 exhausted、≥ 0.8 为 warning。
- 代表窗口：`five_hour`→`5h`、`seven_day`→`7d`，其余值按原字符串查 windows；claim 提供的重置时间
  在归一化时与 limit 重置取最早者，因此 overage（非 limit 窗口）claim 可独占最早重置，而 token
  窗口 claim 通常被更早的 limit 重置覆盖。
- 数值解析：`parseUnixTimestamp` 对应 `strconv.ParseInt` 忽略错误（语法错 0，范围错饱和到
  `long.MaxValue` / `long.MinValue`）；`parseFloat` 接受十六进制浮点（`0x1p-2`）、下划线（`1_0`）、
  `Inf` / `NaN` 与指数，拒绝空白与尾随字符。
- 注册：`claudecode` 通道不再走 “No checker for provider” 分支；采集循环、退避、状态归一化与
  持久化沿用 §114 核心，GraphQL 无需改动。

## 测试

- `tests/AxonHub.Api.Tests/ClaudeCodeQuotaCheckerTests.cs`：13 例 + 67 个 InlineData，共 80 个用例。
  - 线路形状：URL、Bearer、四个 Claude Code 头、`Content-Type` / `Accept` / 默认 User-Agent、
    body 逐字节形状；base URL 变体；OAuth 对象 / legacy apiKey / 空凭据 / 缺 token / 解析错误包装；
    HTTP 500 包装文案与 201 的 `HTTP 201: …` 文案。
  - 代理：通道 settings 解析 → ProxyConfig 字段（type / url / username / password /
    disableConnectionReuse）与 base executor 零调用；畸形 settings 视为无代理。
  - 解析：unified 状态映射 7 组（含 1.0 只升级 overall 为 warning、limit 为 exhausted，以及 unknown
    被 available limit 提升）；窗口存在性 6 组；代表窗口重置与归一化 6 组；RawData 三组窗口与
    fallback 字段；period 携带与无 reset 时无 period。
  - 数值：`ParseUnixTimestamp` 18 组（正负号、空白、下划线、小数、十六进制、int64 溢出饱和）；
    `ParseFloat` 18 组（NaN / ±Inf / 十六进制 / 下划线 / 上溢 / 下溢 / 空白）；`GetEndpointUrl`
    8 组（含仅去一个尾部斜杠）。
- 静态计数：`AxonHub.Api.Tests` 3145 → 3225（+80），`AxonHub.Persistence.Tests` 保持 319，四项目合计
  6591 → 6671。**本轮按项目规则未运行 lint / build / test**，下一次全量运行应以该数字复核。

## 差异

- C182 由 18 个 provider checker 收窄为剩余 17 个（移除 claudecode）；测试清单加入本切片 80 例。
- C123 仍为 resolved；claudecode 复用同一套 `DateTimeOffset` 无法表示的极端 reset 处理
  （年份 / 偏移 / 超范围 epoch 置空），未新增差异编号。

## 残余风险

- 其余 17 个 provider 的 checker 未移植：这些通道仍不发起配额请求，保留既有行。
- 本轮未运行构建与测试；编译 / 分析器正确性仅由人工检查保证。线路形状由真实 `HttpExecutor` +
  记录型 `HttpMessageHandler` 锁定，脚本化响应分支由 `ScriptedExecutor` 覆盖。
- Claude Code 的 unified ratelimit 头为供应商私有协议，缺失 / 新增头时按参照的宽容规则处理
  （缺 status 即报错，其他头缺失只影响对应窗口 / 字段）。
