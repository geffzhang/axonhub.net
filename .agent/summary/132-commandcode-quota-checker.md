# §132 Command Code 供应商配额检查器（alpha key / Studio cookie 双计费面 + plan allowance 表）

日期：2026-11-08
参照：`f272549f:internal/server/biz/provider_quota/commandcode_checker.go`（`commandCodeProviderType`:19、
双计费面端点常量:24-30、`commandCodeQuotaUserAgent`:31、`commandCodeMaxResetEpochMillis`:32、
`commandCodeCookieNames`:38-45、`commandCodePlanAllowance`:49-53、`commandCodePlanAllowances`:61-70、
`commandCodeTeamProAllowance`:74、`commandCodeAPIKey`:80-90、`commandCodeCookie`:93-104、
`HasCommandCodeQuotaCredentials`:110-121、`SupportsChannel`:135-141、`CheckQuota`:143-175、
`commandCodeFetchQuota`:177-192、`commandCodeGet`:196-224、`commandCodeStatusError`:229-240、
`parseCommandCodeCredits`:326-478、`commandCodeWindow`:480-541、`earliestCommandCodeReset`:543-556、
`parseCommandCodeReset`:558-582、`commandCodeResetFromEpoch`:584-594、`commandCodeMatchPlan`:600-620、
`parseJSONObject` 与折叠辅助件:622-703），`provider_quota.go:392` 的 `registerCommandCodeSupport`
（定义见 `provider_quota.go:410-412`，在 `registerCharmHyperSupport` 之后、`registerOllamaSupport`
之前调用），以及 `commandcode_checker_test.go` 的测试函数。

## 交付

- `src/AxonHub.Api/Orchestration/CommandCodeQuotaChecker.cs`：`IQuotaChecker`，两条计费面。
  - 账号 API key（`GetAllApiKeys` 第一个 TrimSpace 非空者）走 `/alpha/billing/credits` 与
    `/alpha/billing/subscriptions`（Bearer）；无 key 时用
    `Settings.ProviderQuota.CommandCode.AuthCookie`（TrimSpace + `NormalizeCommandCodeCookie`）走
    `/internal/billing/*`（仅 `Cookie` 头）。
  - key 面 401/403 且 cookie 非空时静默回退到 cookie 面；cookie 为空或错误非凭据类时照常抛出。
  - credits 失败即失败，subscriptions 失败只丢 plan 信息（fail-open）。
  - 本地 plan allowance 表 + Team Pro label 匹配；可信 monthly 分母需窗口、cap 与剩余三项同时满足。
  - Go `map[string]interface{}` 解码语义（折叠 / 重复键 / float64 / 溢出文案 / root null）。
  - 重置解析（Unix 秒 / 毫秒 / RFC3339，1e15 上限）与 UTC `yyyy-MM-dd'T'HH:mm:ss'Z'` raw 文本。
- `src/AxonHub.Api/Program.cs`：在 charm_hyper 之后注册 `CommandCodeQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("commandcode", …)`，与参照 `registerCommandCodeSupport` 的顺序
  一致；「剩余 checker」注释由 2 改为 1，并补 Command Code 说明。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档同步为「… Zai、Charm Hyper 与
  Command Code 已注册，其余 1 个后续切片」。
- `src/AxonHub.Api/Orchestration/ProviderQuotaService.cs`：`HasCredentialsForProvider` 的
  Command Code 分支改为调用 `CommandCodeQuotaChecker.HasQuotaCredentials`，删除服务内重复的
  `HasCommandCodeQuotaCredentials` / `CommandCodeApiKey` / `CommandCodeCookie`。
- `src/AxonHub.Api/Orchestration/ProviderQuotaData.cs`：`InvalidProviderQuotaCredentialsException`
  改为显式双构造函数（保留 sentinel 文案，detail 以 `: ` 追加），供 checker 携带凭据错误详情。
- `tests/AxonHub.Api.Tests/CommandCodeQuotaCheckerTests.cs`：41 个 `[Fact]` + 9 个 `[Theory]`
  （75 个 InlineData），共 116 例。

## 行为要点

- 线路：两族端点都是 GET，无 body；请求头 `Accept: application/json, text/plain, */*`、
  `Accept-Language: en-US,en;q=0.9`、Chrome 126 UA、`Origin` / `Referer: https://commandcode.ai/`；
  key 面 `Authorization: Bearer <key>`，cookie 面仅 `Cookie`。`Settings.Proxy` 非空时经代理工厂
  派生执行器，直连执行器不使用。
- 凭据：key 优先且不依赖 cookie；损坏 cookie 不阻塞 key 面（错误留给 cookie 面）；两者都缺报
  `channel has no Command Code API key or quota cookie`；损坏 cookie 且无 key 报
  `invalid Command Code auth cookie: <inner>`；两种情况都不发起请求。
- 判定：401/403 报 `Command Code billing API returned <code> (invalid API key?)` /
  `(expired session cookie?)` 并包装为 invalid-credentials；其余非 2xx（含 3xx）报
  `Command Code billing API returned <code>`；transport 错误包装为
  `Command Code billing request failed: <inner>`；2xx 的任意状态码（201 / 204）仍解析 body。
- 解码：root 必须是对象（null → `empty JSON object`；其它 → `json: cannot unmarshal <kind> into
  Go value of type map[string]interface {}`）；数字统一 float64（溢出报 `json: cannot unmarshal
  number <literal> into Go value of type float64`）；属性名 ASCII 折叠；完全重复键最后者胜，
  折叠重复键按文档顺序取首个；credits 对象优先取嵌套 `credits`，窗口在 root 或 credits 内
  （root 优先）；subscriptions 取 `data` / `subscription` 包裹。
- 窗口：five_hour（别名 5h / fiveHour）与 weekly；usage_percent 优先，否则 used/cap；按
  0.8 / 1.0 阈值生成 subscription-cycle 限额并取最差整体状态。
- 月度分母：仅当 plan_id 命中 allowance 表、该行 5h/weekly cap 与线上相等、且
  `monthlyRemaining <= allowance` 时才写 `monthly_limit_usd`（线上优先、否则表值）并追加 monthly
  窗口；表值 individual-go 10/3/6、individual-goat 70/14/35、individual-pro 80/16/40 与 legacy
  30/6/15、individual-pro-v1 80/16/40、individual-max 150/45/90、individual-ultra 300/90/180，
  Team Pro 按 label 大小写不敏感匹配 40/12/24。
- 按量：无窗口时按余额 / 已购任一 > 0 → available，否则 exhausted；单一 monthly 限额、
  usageRatio 0、无 reset。
- 重置：Unix 秒、Unix 毫秒（>= 1e12）与 RFC3339；`<= 0` 或 `> 1e15` 丢弃；nextResetAt 取两窗
  最早者；过去的 reset 由 `Normalize` 清空但 rawData 保留；rawData 的 reset_time 按 UTC
  `yyyy-MM-dd'T'HH:mm:ss'Z'` 渲染。
- 通道门：`commandcode` / `commandcode_anthropic` 且 `HasQuotaCredentials`（有 key，或已配置
  cookie，即使损坏）；null 通道 false。

## 测试

- `tests/AxonHub.Api.Tests/CommandCodeQuotaCheckerTests.cs`：41 例 + 75 个 InlineData，共 116 个用例。
  - 双路径：cookie 面可信 monthly、key 面 Bearer、key 被拒回退、无 cookie 时不回退、
    损坏 cookie 不阻塞 key、缺凭据 / 空白凭据不发起请求。
  - 线路：请求头与 Bearer、cookie 面仅 Cookie、代理路由。
  - 状态：401 cookie 文案、403 key 文案、429 / 5xx 不回退、transport 包装、畸形 JSON 前缀、
    201 / 204 仍解析、3xx 通用文案、空 body。
  - 解码：7 组 Go 文案、重复键、折叠键文档顺序、root 窗口优先。
  - plan / 月限：11 组 plan 行选择、线上 limit 优先、超额跳过分母、负余额夹取、数字格式化、
    rawData 省略、nextResetAt、过去 reset 清理。
  - 阈值 / 按量：monthly 5 组、window 3 组、按量余额 5 组。
  - 重置：14 组秒 / 毫秒 / RFC3339、非标量忽略。
  - 通道门：`SupportsChannel` 13 组、`HasQuotaCredentials` 9 组、null 通道。

`AxonHub.Api.Tests` 由 5049 增至 5165（+116），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
8495 增至 8611。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 5165 / 319 / 8611 复核。**

## 台账

- `porting/differences.jsonl`：C182 收窄为「剩余 1 个 checker（ollama）」，补 Command Code 注册说明
  与 116 例；新增 C220（双计费面回退、plan allowance 表、Go map 解码语义、UTC 重置文本，含折叠
  重复键顺序与时区渲染两项既有差异族）；`lint` 通过、`render` 重新生成 `02-differences.md`
  （220 条）。
- `porting/01-baseline.md`：新增 §132；「配额路由与供应商配额采集」覆盖行补 Command Code 并改为
  「其余 1 个」。
- `porting/03-acceptance.md`：追加 §132 静态计数段落。

## 下一步

- `ollama` checker（参照 `provider_quota.go:393 / 414-416` 的 `registerOllamaSupport`），落地后
  C182 可关闭。
