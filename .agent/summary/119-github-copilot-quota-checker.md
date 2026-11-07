# §119 GitHub Copilot 供应商配额检查器（copilot_internal/user）

日期：2026-11-07
参照：`f272549f:internal/server/biz/provider_quota/github_copilot_checker.go`（`CheckQuota`:40-71、
`buildLimits`:73-117、`copilotLimit`:119-128、`isFiniteCopilotNumber`:130-132、`getAccessToken`:134-155、
`fetchUserPayload`:157-180、`mapPlanType`:182-199、`prepareRawData`:201-230、`getNumber`:232-243、
`calculateStatus`:245-291、`parseResetDate`:293-310、`SupportsChannel`:312-314）、`provider_quota.go:430-432`
的 `registerGithubCopilotSupport`、`llm/transformer/openai/copilot/outbound.go:160-168` 的
`SetCopilotHeaders`，以及 `provider_contract_fixtures_test.go` 的 github_copilot 用例与
`period_window_test.go:389-420` 的 `TestGithubCopilotQuotaChecker_PreservesMonthlyResetWithoutPeriod`。

## 交付

- `src/AxonHub.Api/Orchestration/GithubCopilotQuotaChecker.cs`：`IQuotaChecker` 实现 + `CopilotUserPayload`
  的 Go 语义 JSON 解码。
  - 凭据：typed `oauth` 对象存在 → 直接返回其 access token（不 trim、不校验空值，空 token 发出
    `Authorization: token `）；否则 legacy `apiKey` 当 OAuth JSON 解析，`err == nil && AccessToken != ""`
    才采用；最后回退 `strings.TrimSpace(apiKey)`。无 oauth 且 trim 后 apiKey 为空报
    `channel has no credentials`；回退 token 为空报 `GitHub access token is missing`。
  - 采集：GET `https://api.github.com/copilot_internal/user`，头 `Authorization: token <accessToken>`、
    `Accept: application/json` 与 `SetCopilotHeaders` 的 7 个头（Editor-Version / Editor-Plugin-Version /
    User-Agent / Copilot-Integration-Id / Openai-Intent / X-Github-Api-Version /
    X-Vscode-User-Agent-Library-Version）；无 body，不带 Content-Type。`settings.proxy` 存在时**一次**解析
    代理 executor；畸形 settings 视为无代理（C013）。
  - 错误：执行错误包装为 `failed to fetch copilot user info: %w`；状态不在 [200,300)（executor 只对 ≥400
    抛错，故覆盖 1xx/3xx）报 `failed to fetch copilot user info, status: %d`；解码失败包装为
    `failed to parse copilot user response: %w`。
  - 解码：根 `null` 对 struct 是 no-op；重复对象键对 map 字段合并、对标量 last-wins；键匹配先精确后
    ASCII 大小写折叠；数字统一 `float64`（溢出报 `cannot unmarshal number <literal> into Go value of type
    float64`）；字段类型不匹配按 Go 文案报错（与 .NET 解码器文案差异沿用 C093）。
- `src/AxonHub.Llm/Transformer/OpenAI/Copilot/CopilotOutboundTransformer.cs`：`SetCopilotHeaders` 由
  internal 提升为 public（AxonHub.Api 无法访问 Llm 模块 internal；参照侧本就是导出函数）。
- `src/AxonHub.Api/Program.cs`：在 xai_subscription 之后注册 `GithubCopilotQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("github_copilot", …)`，与参照注册顺序一致。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档改为「Claude Code、Codex、
  Antigravity、xAI Subscription 与 GitHub Copilot 已注册，其余 14 个后续切片」。
- `tests/AxonHub.Api.Tests/GithubCopilotQuotaCheckerTests.cs`：28 个 `[Fact]` + 11 个 `[Theory]`
  （47 个 InlineData），共 75 例。

## 行为要点

- 窗口：先按排序后的 `limited_user_quotas` 键生成 limit（`monthly_quotas` 嵌套值为 total；缺失 / 非数字 /
  非有限 / 负数回退 remaining；`total == 0 && remaining != 0` 跳过；ratio = (total−remaining)/total，total
  为 0 时 0）；再按排序后的 `quota_snapshots` 键生成（非对象跳过；`unlimited == true` 仅对 bool 成立，
  非 bool 计数；`percent_remaining` 必须为 0..100 的有限数字；ratio = 1 − remaining/100）。
- 状态映射：ratio ≥ 1 exhausted、≥ `WarningThresholdRatio`（0.8）warning、其余 available；
  `WithWindow(window, 0)` 只设标签，`PeriodStart` 恒为 nil（`PeriodStartFromReset(nil-窗口)`）。
- 整体状态：取所有窗口最低百分比（limited 用 `remaining/total*100`，monthly total > 0 才覆盖；snapshots
  用 `percent_remaining`，`unlimited == true` 跳过且不校验 0..100），起始 100；≤0 exhausted、<20 warning、
  其余 available。
- 重置：`quota_reset_date_utc`（RFC3339，不 trim；Z / ±offset / 小数秒 / 一位小时）→ `quota_reset_date`
  （`2006-01-02`）→ `limited_user_reset_date`；空串或解析失败继续回退，最终 null（C123 的年份 0 限制）。
  `NextResetAt` 与每个 limit 的 `NextResetAt` 同取该值（不过滤未来，归一会清空过期值）。
- rawData：`copilot_plan` 恒存在（缺省 JSON null）；`access_type_sku` + 映射后的 `plan_type`（未知 SKU 不写）；
  三个重置日期按非 null 写入；`limited_user_quotas`、`total_quotas`（即 `monthly_quotas`）、
  `quota_snapshots` 按非 null 写入；序列化沿用 Go 键排序与 null 输出（`GoJson`）。

## 测试

- `tests/AxonHub.Api.Tests/GithubCopilotQuotaCheckerTests.cs`：28 例 + 47 个 InlineData，共 75 个用例。
  - 线路形状：URL、`Authorization: token`、Accept、7 个 Copilot 头、无 body / 线上无 Content-Type；
    请求数据仍保留全部头且 `Auth` 为 null（脚本化 executor 断言）。
  - 凭据：typed oauth 优先 / legacy apiKey 内的 OAuth JSON / 空 token 回退原始 JSON 串 / 不可解析回退 /
    空 oauth token 原样透传 / 三种空凭据拒绝。
  - 错误：执行错误逐字包装、1xx/3xx 状态、不可解码 body 与错误字段类型包装。
  - 窗口：两族排序、monthly total 回退三种、`total == 0` 规则、负数 / 非数字 / null 跳过、snapshots 的
    unlimited / 越界 / 非对象跳过、百分比→ratio 与状态边界、告警阈值边界、月度重置无 period
    （period_window_test）。
  - 整体状态：八组最低百分比用例、非 bool unlimited 计数。
  - 重置：RFC3339 优先与三种写法、偏移保留、日期字段回退链、空值与非法日期拒绝。
  - rawData：缺省 `{"copilot_plan":null}`、全字段 Go 键序、未知 SKU 不写 plan_type、SKU→plan 八组。
  - 解码边界：根 null、非对象根、重复 map 键合并、重复标量 last-wins。
  - `SupportsChannel`：仅 `github_copilot`。

`AxonHub.Api.Tests` 由 3354 增至 3429（+75），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由 6800
增至 6875。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态计数；
下一次全量运行应以 3429 / 319 / 6875 复核。**

差异：C182 由剩余 15 个 checker 收窄为 14 个；无新增差异（解码文案沿用 C093、RFC3339 / 日期解析沿用
C123、畸形 settings 沿用 C013）。
