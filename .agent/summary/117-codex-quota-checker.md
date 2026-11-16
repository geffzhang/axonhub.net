# §117 Codex 供应商配额检查器（wham usage / reset credits）

日期：2026-11-07
参照：`f272549f:internal/server/biz/provider_quota/codex_checker.go`（`Reset`:75-87、`ListResets`:89-115、
`listResetCredits`:117-147、`parseCodexResetTime`:149-160、`consumeResetCredit`:162-196、
`extractCodexCredentials`:198-224、`CheckQuota`:226-251、`parseResponse`:253-304、
`codexRateLimitExhausted`:306-309、`buildCodexQuotaLimit`:311-346、`codexWindowDuration`:348-359、
`codexWindowResetAt`:361-373、`codexAggregateStatus`:375-391、`codexEarliestFutureReset`:406-419、
`SupportsChannel`:421-423、`convertRateLimitToMap`:425-445、`convertWindowToMap`:447-466）、
`provider_quota.go:418-420` 的注册顺序，以及 `codex_checker_test.go`、`codex_quota_regression_test.go`、
`period_window_test.go` 与 `provider_contract_fixtures_test.go` 中的 codex 用例。

## 交付

- `src/AxonHub.Api/Orchestration/CodexQuotaChecker.cs`：`IQuotaChecker` + `IQuotaResetter` 实现。
  - 采集：GET `https://chatgpt.com/backend-api/wham/usage`，Bearer access token，
    `Content-Type: application/json`；无 body，线上不带 Content-Type（C206）。
  - 凭据：OAuth 对象优先（存在即不回落 legacy apiKey），否则把 legacy apiKey 当 OAuth JSON 解析；
    无凭据报 `channel has no credentials`，空 token 报 `OAuth missing access_token`（与 claudecode
    的文案不同），JWT 缺 `chatgpt_account_id` 报
    `failed to extract ChatGPT account id from access token`。
  - 解析：`plan_type` 恒进 rawData；`rate_limit` / `code_review_rate_limit` 仅在非 null 时以 Go map
    形状加入；limits 只来自 `rate_limit` 的 primary / secondary 窗口。
  - 重置：`IQuotaResetter` 的 `ListResetsAsync` / `ResetAsync` 走
    `…/rate-limit-reset-credits` 与 `…/rate-limit-reset-credits/consume`，包含 `ChatGPT-Account-Id`
    头与 `{"credit_id":…,"redeem_request_id":<v4 UUID>}` body。
- `src/AxonHub.Llm/Json/GoTimeParsing.cs`：从 Antigravity 的私有解析器提取 `TryParseRfc3339`
  （Go `time.Parse(RFC3339)` 的宽度 / 范围规则），Antigravity 与 Codex 共用。
- `src/AxonHub.Llm/Json/GoTime.cs`：新增 `Rfc3339Nano(DateTimeOffset)` 与
  `GoNullableRfc3339NanoConverter`，用于重置清单 `grantedAt` / `expiresAt` 的 Go 时间线形。
- `src/AxonHub.Api/Orchestration/ProviderQuotaData.cs`：`QuotaReset.GrantedAt` / `ExpiresAt`
  应用上述转换器（省略 null，非字符串 / 非法时间按 Go 报错）。
- `src/AxonHub.Api/Program.cs`：注册 `CodexQuotaChecker` 单例与
  `ProviderQuotaCheckerRegistration("codex", …)`，位于 claudecode 与 antigravity 之间，与参照
  `registerProviderCheckers` 的顺序一致。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档改为「Claude Code、Codex 与
  Antigravity 已注册，其余 16 个 checker 后续切片」。
- `tests/AxonHub.Api.Tests/CodexQuotaCheckerTests.cs`：27 个 `[Fact]` + 12 个 `[Theory]`
  （56 个 InlineData）。
- `tests/AxonHub.Api.Tests/ProviderQuotaDomainTests.cs`：既有 merge round-trip 断言扩展为锁定
  `_resets` 的完整 JSON 形状（supported / id / status / type / grantedAt / expiresAt / title / 无 error）。

## 行为要点

- 窗口：`limit_window_seconds` 缺省按 0 处理；≤0 或 >9223372036（`int64(time.Second)` 的秒上限）
  视为无效并丢弃该窗口；`used_percent` 为 NaN / ±Inf / 负值时丢弃；ratio = min(percent/100, 1)，
  ≥1 exhausted、≥0.8 warning。
- 账户级标志：`limit_reached` 或 `allowed=false` 只在本窗口没有 `used_percent` 时推断 100% exhausted；
  有百分比的窗口保留自己的用量。
- 状态：起始 unknown；`allowed=true` → available；随后取 limits 的最大 rank（exhausted 3 /
  warning 2 / available 1）。`NextResetAt` 取严格大于 now 的最早窗口重置；归一化会清空过期重置与
  对应 period。
- 窗口标签：按时长归一化为 5h / 7d / daily / 30d，其余回退 "primary" / "secondary"；无时长时
  period 为空。
- 重置清单：只保留 `status == "available"` 的 credit；`granted_at` / `expires_at` 按
  Go `time.Parse(RFC3339)`（空或失败 → null）；无可用 credit 时 `Reset` 报
  `no available codex reset credit`。
- 重置 body：Go map 键序 `credit_id` 在前、`redeem_request_id` 在后；UUID 为小写规范 v4。
- 通道代理：`settings.proxy` 存在时每个私有步骤各自解析代理 executor（清单 + consume 共两次），
  否则共享 executor；畸形 settings 视为无代理。
- 时间序列化：端口 `DateTimeOffset` 以 Go RFC3339Nano 输出（秒以下小数去掉尾随 0），offset 保留；
  Go 可表示而 `DateTimeOffset` 不可表示的年份 / 偏移 / 极端 epoch 返回 null（沿用 C123）。

## 测试

- `tests/AxonHub.Api.Tests/CodexQuotaCheckerTests.cs`：27 例 + 56 个 InlineData，共 83 个用例。
  - 线路形状：usage 与 reset-credits 两条 GET 的 URL、Bearer、User-Agent、无 Originator、
    `ChatGPT-Account-Id`（清单 / consume）与线上无 Content-Type（C206）；请求数据仍保留
    Content-Type（脚本化 executor 断言）。
  - 凭据：OAuth 对象 / legacy apiKey / 双凭据优先级 / 空凭据 / OAuth 空 token / legacy 解析失败
    包装 / 五种 JWT account id 失败。
  - 错误：`quota request failed:`、`list codex reset credits failed:`、
    `consume codex reset credit failed:` 与三个解析包装的逐字文案。
  - 解析：JSON null / `{}` 的零结构体、rawData 形状（bool / double / long / 空嵌套 map）、
    `code_review_rate_limit` 只进 rawData、四组拒绝请求的窗口状态矩阵（含 EffectiveStatus 与
    Routing）、过期重置聚合、无效窗口 4 组、时长边界 5 组、`reset_after_seconds` 回退、无时长时无
    period。
  - 重置：清单过滤 / 空列表非 null / null credit / JSON null / 畸形报文 / 传输错误、consume 的
    Go 键序与 v4 UUID 正则、无 credit 文案、consume 错误与解析包装、两次请求均走通道代理。
  - `parseCodexResetTime`：16 组（空串、Z / ±offset、1-9 位小数、逗号小数、一位小时、年份 0、
    非法日期 / 小时 / 偏移、小写 t/z、缺 offset）。
- 静态计数：`AxonHub.Api.Tests` 3225 → 3308（+83），`AxonHub.Persistence.Tests` 保持 319，四项目合计
  6671 → 6754。**本轮按项目规则未运行 lint / build / test**，下一次全量运行应以该数字复核。

## 差异

- 新增 C206：无请求体的出站请求在端口丢掉 Content-Type 头（.NET 把 Content-Type 归为 content
  header，无 body 时不创建 HttpContent），参照的 Go net/http 照常发送。已接受，由本切片三条断言
  锁定「请求数据有 / 线上无」与「重置清单 GET 同样无」。
- C182 由 17 个 provider checker 收窄为剩余 16 个（移除 codex）；测试清单加入本切片 83 例。
- C123 仍为 resolved；codex 复用同一套 `DateTimeOffset` 无法表示的极端 reset 处理，未新增差异。

## 残余风险

- 其余 16 个 provider 的 checker 未移植：这些通道仍不发起配额请求，保留既有行。
- 本轮未运行构建与测试；编译 / 分析器正确性仅由人工检查保证。线路形状由真实 `HttpExecutor` +
  记录型 `HttpMessageHandler` 锁定，脚本化响应分支由 `ScriptedExecutor` 覆盖。
- ChatGPT backend 的重置 credit 报文为供应商私有协议；未知字段被忽略，类型不匹配按 Go 解码错误
  包装（与参照一致）。
