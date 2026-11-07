# §118 xAI Subscription 供应商配额检查器（weekly / monthly billing）

日期：2026-11-07
参照：`f272549f:internal/server/biz/provider_quota/xai_subscription_checker.go`（`CheckQuota`:24-58、
`SupportsChannel`:57-59、`fetchBilling`:61-85、`xaiBillingQuotaData`:87-133、`xaiBillingWindowStatus`:135-143、
`parseXAIReset`:145-151、`earliestReset`:153-161）、`provider_quota.go:426-428` 的注册，以及
`xai_subscription_checker_test.go` 与 `provider_contract_fixtures_test.go` 中的 xai 用例。

## 交付

- `src/AxonHub.Api/Orchestration/XaiSubscriptionQuotaChecker.cs`：`IQuotaChecker` 实现。
  - 采集：同一 executor 先 GET `https://cli-chat-proxy.grok.com/v1/billing?format=credits`（weekly），
    再 GET `https://cli-chat-proxy.grok.com/v1/billing`（monthly）；Bearer access token，头
    `Accept: application/json`、`Content-Type: application/json`、`x-xai-token-auth: xai-grok-cli`、
    `x-grok-client-version: 0.2.114`、
    `User-Agent: grok-pager/0.2.114 grok-shell/0.2.114 (macos; aarch64)`。无 body，线上不带 Content-Type（C206）。
  - 凭据：`OAuthCredentialsJson.ResolveChannelCredentials`（`internal/objects/channel.go:631-639` 的端口）：
    typed `oauth` 对象的 token 经 `strings.TrimSpace` 后非空则优先，否则把 legacy `apiKey` 当 OAuth JSON 解析。
    解析失败包装为 `parse xAI subscription credentials: %w`；trim 后为空报
    `xAI subscription channel has no OAuth access token`。
  - 代理：`settings.proxy` 存在时**一次**解析代理 executor 供两个请求共用；畸形 settings 视为无代理（C013）。
  - 错误：weekly / monthly 执行错误分别包装为 `fetch xAI weekly billing: %w` / `fetch xAI monthly billing: %w`；
    `StatusCode` 不在 [200,300)（executor 只对 ≥400 抛错，故覆盖 1xx/3xx）报 `HTTP %d` 并同受包装；
    合并报文失败包装为 `parse xAI billing responses: %w`（无 config 报
    `xAI billing responses contain no config`）。
- `src/AxonHub.Llm/OAuth/OAuthCredentialsJson.cs`：新增 `ResolveChannelCredentials`；`ParseCredentialsJson`
  的输入 trim 改为 `GoStrings.TrimSpace`（与 Go `strings.TrimSpace` 的空白集合一致）。
- `src/AxonHub.Api/Management/ModelFetcher.cs` 与 `src/AxonHub.Api/Orchestration/ChannelOutboundFactory.cs`：
  两份私有的 `ResolveOAuthCredentials` 副本改为委托，避免三处漂移。
- `src/AxonHub.Api/Program.cs`：在 antigravity 之后注册 `XaiSubscriptionQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("xai_subscription", …)`，与参照 `registerXAISubscriptionSupport`
  （provider_quota.go:426-428）的顺序一致。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档改为「Claude Code、Codex、Antigravity
  与 xAI Subscription 已注册，其余 15 个 checker 后续切片」。
- `tests/AxonHub.Api.Tests/XaiSubscriptionQuotaCheckerTests.cs`：25 个 `[Fact]` + 5 个 `[Theory]`
  （21 个 InlineData），共 46 例。

## 行为要点

- 窗口：weekly（非 null 时）状态 = `xaiBillingWindowStatus(percent)`、ratio = percent/100、
  `WithWindow("weekly", 7d)`（period start = reset − 7d）；monthly（非 null 时）同状态函数、
  `Window="monthly"`、`PeriodStartFromMonthlyReset(reset)`（按上月天数截断日）。
- 状态：≥100 exhausted、≥80（`WarningThresholdRatio*100`）warning、其余 available；整体
  `usagePercent` 取 weekly 与 monthly 的最大值（起始 −1，NaN 传播），≥0 才覆盖 unknown；ratio > 1 由归一化
  截断为 1，负 ratio 的 limit 被丢弃，但整体状态仍按原始百分比计算。
- 重置：`parseXAIReset` 对 TrimSpace 后的值做 `time.Parse(RFC3339)`（Z / ±offset / 小数秒 / 一位小时；
  失败或 DateTimeOffset 不可表示 → null，沿用 C123）；`NextResetAt` 取两个重置的最早值（不过滤未来），
  归一化清空过期值与对应 period。
- rawData：`plan_type` 恒存在（缺省空串），`PlanFromAccessToken` 的非空结果覆盖月度 limit 推出的 plan；
  `billing` 恒为对象（两个窗口都缺失时为空对象），仅非 null 的窗口以 Go `BillingWindow` 形状加入
  （`usage_percent` 恒出现，`reset_at` / `limit_usd` / `used_usd` 按 omitempty 省略）。

## 测试

- `tests/AxonHub.Api.Tests/XaiSubscriptionQuotaCheckerTests.cs`：25 例 + 21 个 InlineData，共 46 个用例。
  - 线路形状：weekly → monthly 顺序、两条 URL、Bearer、Accept、`x-xai-token-auth`、`x-grok-client-version`、
    CLI User-Agent、线上无 Content-Type（C206）；请求数据仍保留 Content-Type 与全部头（脚本化 executor 断言）。
  - 凭据：OAuth 对象 / legacy apiKey / OAuth token 空白时回退 legacy / 空凭据三种 / legacy token 空白被
    TrimSpace 拒绝 / 两种 legacy 解析失败包装。
  - 错误：weekly / monthly 传输错误逐字文案、`HTTP 302` / `HTTP 199`、无 config 与畸形报文包装。
  - 窗口：双窗口合并（0.825 / 0.5、period、NextResetAt）、monthly-only、weekly-only、空 summary、
    月度百分比决定整体 warning、负值丢弃、超 1 截断、7 组状态边界。
  - 重置：RFC3339 的 7 组输入、偏移保留、过期清空、最早值。
  - rawData：Go `BillingWindow` 字段形状（含 omitempty 的两个金额）、`plan_type` 空串与 JWT tier 覆盖。
- 静态计数：`AxonHub.Api.Tests` 3308 → 3354（+46），`AxonHub.Persistence.Tests` 保持 319，四项目合计
  6754 → 6800。**本轮按项目规则未运行 lint / build / test**，下一次全量运行应以该数字复核。

## 差异

- C182 由剩余 16 个 provider checker 收窄为 15 个（移除 xai_subscription）；测试清单加入本切片 46 例。
- 无新增差异：无 body GET 的 Content-Type 丢失沿用 C206（本切片三条断言锁定「请求数据有 / 线上无」），
  RFC3339 解析沿用 C123，畸形 settings 宽松处理沿用 C013。

## 残余风险

- 其余 15 个 provider 的 checker 未移植：这些通道仍不发起配额请求，保留既有行。
- 本轮未运行构建与测试；编译 / 分析器正确性仅由人工检查保证。线路形状由真实 `HttpExecutor` +
  记录型 `HttpMessageHandler` 锁定，脚本化响应分支由 `ScriptedExecutor` 覆盖。
- Grok CLI 的 billing 报文为供应商私有协议；未知字段被忽略，类型不匹配按 Go 解码错误包装（与参照一致）。
