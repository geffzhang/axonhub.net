# §133 Ollama 供应商配额检查器（API key 余额 / 会话 cookie 计量页双面采集）

日期：2026-11-08
参照：`f272549f:internal/server/biz/provider_quota/ollama_checker.go`（常量 `ollamaProviderType`:22、
`ollamaBalanceURL`:25、`ollamaSettingsURL`:28、`ollamaQuotaUA`:29、`ollamaCookieName`:30、
`ollamaCookiePrefix`:31、`ollamaMinCookieLen`:35；`OllamaQuotaChecker`:43-46、`NewOllamaQuotaChecker`:49-54、
`ollamaAPIKey`:56-66、`ollamaRawCookie`:68-74、`HasOllamaQuotaCredentials`:80-86、`SupportsChannel`:90-96、
`CheckQuota`:101-132、`fetchBalance`:134-154、`ollamaBalanceStatusError`:156-162、`fetchSettings`:164-191、
`NormalizeOllamaCookie`:201-241、三个正则:243-247、`ollamaUsageWindow`:249-254、
`ollamaBalanceResponse`:259-273、`ollamaBalanceWindow`:275-278、`parseBalance`:280-340、
`clampOllamaPercent`:342-344、`parseResponse`:346-405、`buildOllamaQuotaData`:407-465、
`ollamaWindowDuration`:469-478、`normalizeOllamaWindowStatus`:480-488、`ollamaLooksLikeLoginPage`:498-514），
`provider_quota.go:393` 的 `registerOllamaSupport`（定义见 `provider_quota.go:414-416`，在
`registerCommandCodeSupport` 之后调用），以及 `ollama_checker_test.go` 的 24 个测试函数（其中
`TestOllama_CheckQuota_RejectsHTTPSDowngrade`:325 与 `TestOllama_CheckQuota_BalanceRejectsHTTPSDowngrade`:608
未移植，见 C221）。

## 交付

- `src/AxonHub.Api/Orchestration/OllamaQuotaChecker.cs`：`IQuotaChecker`，两条凭据面。
  - 账号 API key（`GetAllApiKeys` 第一个 TrimSpace 非空且未被禁用者）走
    `https://ollama.com/api/balance`（`Authorization: Bearer <key>`、`Accept: application/json`）。
  - key 面 401/403 且已配置 `Settings.ProviderQuota.Ollama.AuthCookie` 时静默回退到 cookie 面；
    cookie 经 `NormalizeOllamaCookie` 只保留 `__Secure-session`（UTF-8 字节长度 >= 30），GET
    `https://ollama.com/settings`（`Cookie`、`Accept: text/html,application/xhtml+xml`、Chrome 126 UA）。
  - balance 报文按 Go 匿名结构体解码（reflect 类型文本、`time.Time` 错误、ASCII 折叠、重复键合并、
    null 指针 / 值字段、下溢归零）；HTML 计量页按字节定位关键字 + 4000 字节窗口抓取。
  - 月额度 / legacy session / weekly 窗口、purchased credits 与登录页分类。
- `src/AxonHub.Api/Program.cs`：在 commandcode 之后注册 `OllamaQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("ollama", …)`，与参照 `registerOllamaSupport` 的顺序一致。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档同步为「全部 19 个
  provider checker 已注册」。
- `src/AxonHub.Api/Orchestration/ProviderQuotaService.cs`：`HasCredentialsForProvider` 的
  ollama / ollama_anthropic 分支改为调用 `OllamaQuotaChecker.HasQuotaCredentials`。
- `tests/AxonHub.Api.Tests/OllamaQuotaCheckerTests.cs`：30 个 `[Fact]` + 18 个 `[Theory]`
  （93 个 InlineData），共 123 例。

## 行为要点

- 线路：balance 面 GET、`Authorization: Bearer <key>` 字面头、`Accept: application/json`，无 UA、
  无 body；settings 面 GET、仅 `Cookie` / `Accept` / `User-Agent`，无 body。`Settings.Proxy` 非空时
  经代理工厂派生执行器。
- 凭据：双面都缺报 `channel has no Ollama API key or quota cookie`；损坏 cookie 报
  `invalid Ollama auth cookie: <inner>`；两种情况都不发起请求。key 优先且不依赖 cookie；损坏
  cookie 不阻塞 key 面（错误留给回退后的 cookie 面）。
- 判定：balance 401/403 报 `Ollama balance API returned <code>`（invalid-credentials），其余非 2xx
  （含 1xx / 3xx）报同名通用错误；settings 401/403 报
  `Ollama settings page returned <code> (expired session cookie?)`，其余非 2xx 报
  `Ollama settings page returned <code>`；transport 错误分别包装为
  `Ollama balance request failed: <inner>` / `Ollama settings request failed: <inner>`；执行器的
  >=400 `HttpClientError` 走同一状态分类（C159）。
- cookie 归一化：TrimSpace → 可选 `Cookie:` 标签（大小写不敏感）→ CR/LF 拒绝 → 按 `;` 分段，
  Cut 首个 `=`，空名 / 空值报 `invalid cookie segment: expected name=value`；只保留
  `__Secure-session`（名比较大小写不敏感），值 TrimSpace 后按 UTF-8 字节长度 < 30 报
  `Ollama cookie is too short to be a valid session`，其余报 `no __Secure-session Ollama cookie found`。
- HTML 抓取：在首个 `Session usage` / `Weekly usage` 关键字之后的 4000 字节窗口内匹配
  `aria-label="…"`，再取 `([0-9.]+)\s*%\s*used` 与 `data-time="…"`；Go 的 `\s` 是 ASCII 集合
  （NBSP 不作分隔）；百分比按 `strconv.ParseFloat` 语义，失败丢弃窗口；`data-time` 按
  RFC3339Nano 解析，失败只丢 reset（Go 接受但 DateTimeOffset 不可表示的值同样丢弃，C123 同族）。
  无窗口时：命中登录页标记（`<title>sign in`、`sign-in`、`signin`、`wos-session`、
  `log in to your account`、`/signin`）报
  `ollama.com/settings returned the sign-in page (expired session cookie?)`（invalid-credentials），
  否则报 `no Ollama usage windows found in settings page (expired cookie or markup change)`。
- 窗口：allowance_usd > 0 → monthly 百分比窗口（`(1 − balance/allowance)×100`，reset / period
  start 取 period.until / period.from）；legacy session / weekly 的 remaining_percent →
  5h / weekly 窗口；窗口为空且 purchased <= 0 报 `no Ollama quota found in balance response`；
  purchased > 0 追加 available 的 `credits` 限额、rawData 写 `credits.purchased_usd` 并在 Normalize
  前清空整体 status（耗尽额度 + 已购余额 → 整体 available）。
- 阈值：usageRatio = percent/100，0.8 与 1.0 阈值生成 warning / exhausted，整体取最差；
  nextResetAt 取最早 reset；rawData 每窗口写 usage_percent / status / percent_remaining 与
  reset_time（保留输入偏移的 RFC3339 文本，亚秒丢弃）。
- 通道门：`ollama` / `ollama_anthropic` 且 `HasQuotaCredentials`（有 key 或已配置 cookie，即使
  损坏）；null 通道 false。

## 测试

- `tests/AxonHub.Api.Tests/OllamaQuotaCheckerTests.cs`：30 例 + 93 个 InlineData，共 123 个用例。
  - settings 面：双窗口线路与 rawData、85% / 100% 阈值、缺凭据 3 组、403 与登录页 200、登录页
    标记、仅 session 窗口、无窗口、空 body、settings 状态分类 4 组。
  - cookie：保留 __Secure-session 6 组、错误文案 10 组、UTF-8 字节长度。
  - balance 面：月度 / credits / 免费额度耗尽 / legacy 窗口 / 无配额、401/403 2 组、key→cookie
    回退、5xx 不回退、禁用 key 6 组、legacy 耗尽 + credits 2 组、transport 包装、执行器映射。
  - 解码：Go 文案 29 组、root null 2 组、下溢、重复键、null 指针 / 值字段、ASCII 折叠 4 组、
    未知字段、时间拼写 4 组、亚秒截断、不可表示时间 2 组、偏移保留 3 组、过去 reset。
  - 抓取：百分比夹紧 2 组、4000 字节窗口 2 组、ASCII 分隔符 2 组、首个关键字、非法 reset 2 组、
    非法百分比、非法 UTF-8。
  - 通道门：`SupportsChannel` 8 组、settings 为 null。

`AxonHub.Api.Tests` 由 5165 增至 5288（+123），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
8611 增至 8734。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 5288 / 319 / 8734 复核。**

## 台账

- `porting/differences.jsonl`：C182 关闭（全部 19 个 provider checker 已落地）；新增 C221（Ollama
  双面凭据回退、HTML 计量抓取、Go 匿名结构体解码语义与 HTTPS 降级拒绝缺口）；`lint` 通过、
  `render` 重新生成 `02-differences.md`（221 条：157 accepted / 17 gap / 47 resolved）。
- `porting/01-baseline.md`：新增 §133；「配额路由与供应商配额采集」覆盖行补 Ollama 并改为全部
  checker 完成（C182）。
- `porting/03-acceptance.md`：追加 §133 静态计数段落；C182 风险行改为已关闭；C203 行移除过期的
  「C182 切片」表述。

## 下一步

- 供应商配额采集子系统（C182）至此全部落地。后续切片转向其余管理面 / 通道生命周期缺口
  （C013 / C014 等）与既有真实缺口（C124 / C207 的 HTTPS 降级拒绝、C056 Vertex 流式等）。
