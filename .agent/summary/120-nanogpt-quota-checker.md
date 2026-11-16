# §120 NanoGPT 供应商配额检查器（订阅用量端点）

日期：2026-11-07
参照：`f272549f:internal/server/biz/provider_quota/nanogpt_checker.go`（`CheckQuota`:61-94、
`parseResponse`:96-202、`SupportsChannel`:204-209、`buildNanoGPTQuotaURL`:211-228、
`buildNanoGPTLimitStatuses`:230-283、`findEarliestResetAt`:285-302、`convertNanoGPTLimitsToMap`:304-320、
`convertNanoGPTPeriodToMap`:322-330、`convertNanoGPTWindowToMap`:332-352）、`provider_quota.go:434-436`
的 `registerNanoGPTSupport`，以及 `provider_contract_fixtures_test.go` 的 nanogpt 用例。

## 交付

- `src/AxonHub.Api/Orchestration/NanogptQuotaChecker.cs`：`IQuotaChecker` 实现 + `NanogptUsageResponse`
  的 Go 语义 JSON 解码。
  - 凭据：`strings.TrimSpace(credentials.apiKey)`；为空时回退 `apiKeys[0]`（不 trim、不校验空、不感知
    OAuth 形态）；仍为空报 `channel has no API key`。
  - URL：`BaseURL` 经 `strings.TrimSpace` 后 `url.Parse`；空串 / 解析失败 / scheme 与 host 均为空 →
    `https://nano-gpt.com/api/subscription/v1/usage`；`http` 升级为 `https`；否则输出
    `<scheme>://<host>/api/subscription/v1/usage`（保留端口、userinfo 丢弃、大小写保留）。
  - 采集：GET，Bearer 认证 + `Content-Type: application/json`，无 body；**一次**解析代理 executor
    （畸形 settings 视为无代理，C013）。执行错误包装为 `quota request failed: %w`；不校验状态码，
    非 2xx 只要 executor 不抛错就照常解析 body。
  - 解码：根 `null` 对 struct 是 no-op；非对象根、字段类型不匹配按 Go 文案报错（如
    `json: cannot unmarshal string into Go struct field NanoGPTUsageResponse.active of type bool`），
    外层包装 `failed to parse nanogpt usage response: %w`；重复键对标量 last-wins、对对象字段合并、
    `null` 重置指针；键匹配先精确后 ASCII 大小写折叠。`int64` 走 `ParseInt`（拒绝小数 / 科学计数 /
    溢出），`float64` 走 `ParseFloat`（拒绝 `1e400`）。
- `src/AxonHub.Llm/Transformer/Shared/GoUrl.cs`：新增 `TryParse` 公开包装，复用 Codex 内部的
  `net/url` 端口（scheme 小写、host/userinfo 校验与 unescape、IPv6 字面量）。
- `src/AxonHub.Abstractions/ChannelCredentials.cs`：新增 `GetApiKeys`，按 Go 原始语义返回
  `apiKeys` 数组（大小写折叠键匹配、重复键 last-wins、跳过非字符串元素），供 `apiKeys[0]` 回退使用；
  不复用会过滤 OAuth 形态并前置 legacy key 的 `GetAllApiKeys`。
- `src/AxonHub.Api/Program.cs`：在 github_copilot 之后注册 `NanogptQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("nanogpt", …)`，与参照注册顺序一致。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档改为「Claude Code、Codex、
  Antigravity、xAI subscription、GitHub Copilot 与 NanoGPT 已注册，其余 13 个后续切片」。
- `tests/AxonHub.Api.Tests/NanogptQuotaCheckerTests.cs`：26 个 `[Fact]` + 10 个 `[Theory]`
  （79 个 InlineData），共 105 例。

## 行为要点

- 窗口：先 image/daily/24h，再 token/daily/24h，最后 token/weekly/7d；只处理存在的窗口。
  `percentUsed` 存在即作 ratio（缺省 0）；`remaining <= 0` → exhausted 且 ratio 强制 1.0；
  否则 ratio ≥ 1 exhausted、≥ `WarningThresholdRatio`（0.8）warning、其余 available。
  resetAt > 0 经 `time.UnixMilli` + `WithWindow` 设置 `PeriodStart = reset − window`；
  非正或不可表示（C123）为 null。
- 状态映射：`active` → available、`grace` → warning、`inactive` → exhausted、其他 / 缺失 → unknown；
  仅当基态为 available / warning 时按 `quotaStatusRank` 逐窗口升级，state 驱动的 exhausted / unknown
  不被覆盖。
- 整体重置：取所有窗口 resetAt 中的最小值（仅正数）；没有时回退 `graceUntil`（RFC3339，Z / ±offset /
  小数秒 / 一位小时），空串或解析失败为 null。
- rawData：`active` / `provider` / `providerStatus` / `state` / `allowOverage` 按非 null 写入；
  `limits` 存在时总是写入（内部标量按非 null，可能为 `{}`）；`period` 存在时总是写入
  （`currentPeriodEnd` 按非 null，可能为 `{}`）；三个窗口各自存在才写入（内部字段按非 null）；
  序列化沿用 Go 键排序与 null / 空 map 输出（`GoJson`）。
- 归一化：交给共享 `ProviderQuotaData.Normalize` —— ratio 截断到 1、≥1 强制 exhausted、丢弃负数 /
  非有限 ratio 的 limit、清空过期重置、按 ready 优先级与 hasExhausted 推导总状态并重算总重置。

## 测试

- `tests/AxonHub.Api.Tests/NanogptQuotaCheckerTests.cs`：26 例 + 79 个 InlineData，共 105 个用例。
  - 线路形状：URL、`Authorization: Bearer`、`Content-Type`、无 body；请求数据 `Auth` 为 bearer 且
    头集合不含 Authorization（脚本化 executor 断言）。
  - URL 构造：21 组（空 / 空白 / 非法 / 仅 scheme / 仅 host / http 升级 / https 保留 / ftp 保留 /
    端口 / userinfo / IPv6 / 尾斜杠 / 路径与查询丢弃）。
  - 凭据：legacy `apiKey` trim 后优先 / `apiKeys[0]` 原样回退（含空串与不 trim）/ 空白 legacy key 让位
    数组 / 无 key 与仅 oauth 凭据拒绝，共 11 组。
  - 错误：执行错误逐字包装、401 状态包装（含 `401 Unauthorized` 文案）、302 仍解析 body。
  - 解码：16 条 Go 错误文案（根类型、嵌套对象、各标量类型）、畸形 JSON、根 null、重复对象合并 /
    标量 last-wins / 大小写折叠、null 重置、空嵌套对象、字段缺失。
  - 窗口：percentUsed→状态 / ratio 七组、`remaining <= 0` 五组、state 映射五组（含未知 state）、
    升级六组、窗口顺序、负 ratio 被归一化丢弃、重置取最早 / 非正忽略 / 过期清除、`graceUntil`
    四组接受（偏移与一位小时）/ 四组拒绝、窗口重置优先于 graceUntil。
  - rawData：完整响应逐字 JSON、契约 fixture（3 limit）、不可用窗口值仍原样保留。
  - 代理：settings.proxy 路由到代理 executor、畸形 settings 直连、无 settings 直连。
  - `SupportsChannel`：`nanogpt` / `nanogpt_responses` 接受，其余拒绝。

`AxonHub.Api.Tests` 由 3429 增至 3534（+105），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由 6875
增至 6980。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态计数；
下一次全量运行应以 3534 / 319 / 6980 复核。**

差异：C182 由剩余 14 个 checker 收窄为 13 个；无新增差异（解码文案沿用 C003/C093、RFC3339 与
Unix 毫秒年份限制沿用 C123、畸形 settings 沿用 C013、rawData 的双重 JSON 序列化沿用 C067）。
