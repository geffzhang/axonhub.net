# §115 Antigravity 供应商配额检查器（quota summary / legacy 回退）

日期：2026-11-07
参照：`f272549f:internal/server/biz/provider_quota/antigravity_checker.go:36-267`
（`CheckQuota`、`SupportsChannel`、`credentials`、摘要 404/501 回退、legacy 解析与 pool 折叠）、
`antigravity_summary.go:1-169`（envelope 优先级、pool / 窗口归属、剩余额度回退链、顺序 limits、
RawData）、`provider_quota.go:422-424` 的 `registerAntigravitySupport`，以及
`antigravity_checker_test.go`、`antigravity_summary_test.go`。

## 交付

- `src/AxonHub.Api/Orchestration/AntigravityQuotaChecker.cs`：`IQuotaChecker` 实现。
  - 两段式请求：先 POST `https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary`，
    404 / 501 或 200 但响应体无法解析时静默回退到 `/v1internal:fetchAvailableModels`；其余摘要错误
    按 `fetch Antigravity quota summary: %w` / `… status %d` 抛出，legacy 错误按
    `fetch legacy Antigravity quota: %w` / `… status %d` 抛出。
  - 凭据：OAuth 对象优先；legacy 为 `refreshToken|projectID`；access token 可用时直接返回，否则克隆
    并补齐 RefreshToken / ClientId / Scopes 后经 `AntigravityTokenProviderFactory`（client_secret form
    grant）刷新；无 refresh token 报 `channel has no Antigravity OAuth credentials`。
  - 两份解析器：legacy（模型排序、过滤、截断、状态阈值、最早 reset、RawData 五字段、pool 折叠为
    5h 窗口）与 summary（envelope 优先级、别名窗口、三级 remaining 回退、最大用量、
    `[gemini, claude_gpt] × [5h, 7d]` 顺序、整包 Go 形状 RawData）。
  - 重置时间：epoch 秒 / 毫秒数字与 RFC3339 字符串，按 Go `time.Parse` 的宽度与范围规则实现。
- `src/AxonHub.Api/Orchestration/ProviderQuotaData.cs`：新增公开
  `AntigravityPoolAvailabilityGroup(string pool)`（前缀仍私有，模型过滤逻辑不变）。
- `src/AxonHub.Api/Program.cs`：注册 `AntigravityQuotaChecker` 单例与
  `ProviderQuotaCheckerRegistration("antigravity", …)`；注释同步。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档改为「仅 Antigravity 已注册，
  其余 18 个 checker 后续切片」。
- `tests/AxonHub.Api.Tests/AntigravityQuotaCheckerTests.cs`：17 个 `[Fact]` + 4 个 `[Theory]`
  （44 个 InlineData）。

## 行为要点

- 采集：摘要成功即返回 4 条（或实际测得的）pool × 窗口 limits；404 / 501 / 解析失败回退 legacy；
  两次请求复用同一 executor（含通道代理）与同一组 `AntigravityVersion` 客户端头。
- 凭据：OAuth 对象覆盖 legacy API key；access token 非空且（未过期或两个 refresh token 都空）时不刷新；
  刷新失败包装为 `refresh Antigravity OAuth token: …`；legacy 凭据的 projectID 进入请求 body 的
  `project` 字段。
- Legacy 归一化：remaining 截断 0..1；≥1 exhausted、≥0.8 warning；跳过 internal / 缺字段 / NaN /
  Inf / 负数；RawData.models 为 `{displayName, remainingPercentage, status, resetAt}`；同 pool 的
  effort 变体折叠为用量最高的 5h 窗口并挂 `antigravity_pool:<pool>` 可用性分组。
- Summary 归一化：`response` > `summary` > 顶层 `groups`；pool 由 `id + 组名` 判定；窗口由
  `window` / `id` / `displayName` 的别名 token 判定；同 pool+window 取最大用量；未知 / 禁用 / 越界
  条目跳过；无可用窗口报 `Antigravity quota summary has no measured pool windows`；RawData 为整包。
- 注册：`antigravity` 通道不再走 “No checker for provider” 分支；采集循环、退避、状态归一化与持久化
  沿用 §114 核心，GraphQL 无需改动。

## 测试

- `tests/AxonHub.Api.Tests/AntigravityQuotaCheckerTests.cs`：17 例 + 44 个 InlineData，共 61 个用例。
  - 线路形状：两条 URL、Authorization、`X-Client-Name` / `X-Client-Version` / `X-Goog-Api-Client` /
    `Client-Metadata`、由版本派生的 User-Agent、`Content-Type` / `Accept`、`{"project":"project-id"}`。
  - 流程：摘要优先（1 次调用 / 4 条 limits）、404 回退（2 次调用 / 1 条 legacy limit）、403 包装错误、
    解析失败静默回退、脚本化非 2xx 的两条错误文案、摘要 500 时仅 1 次调用、legacy 500 时 2 次调用。
  - 凭据：空凭据、OAuth 刷新优先于 legacy、刷新失败包装、legacy `refreshToken|projectID`、通道代理
    接线（ProxyConfig 字段、共享 executor 不被使用）与畸形设置不建代理。
  - 解析：摘要窗口 / 周期 / RawData、19 条窗口别名、不虚构窗口与未测量错误、envelope 优先级、
    legacy effort 折叠与 RawData、pool 可用性分组（含 zhipu / apertis 字面量）、耗尽 pool 的路由分组、
    未分组 pool 折叠、五条错误文案、状态阈值、重置时间接受形状（一位小时 / 小数 / 逗号 / epoch /
    毫秒 / 负数 / 正号）与 20 条拒绝形状（含年份 0000、+15:00、9999 溢出、空白、下划线、bool /
    null / 数组 / 对象）。
- 静态计数：`AxonHub.Api.Tests` 3084 → 3145（+61），`AxonHub.Persistence.Tests` 保持 319，四项目合计
  6530 → 6591。**本轮按项目规则未运行 lint / build / test**，下一次全量运行应以该数字复核。

## 差异

- C123 由真实缺口转为 resolved：摘要优先 / 旧式回退、凭据与刷新、两份解析器、注册接线均已落地。
  子差异：(1) 重置时刻按 UTC 呈现（Go 按进程本地时区，绝对时刻一致）；(2) 年份 0000、±24h 偏移与
  超出 `DateTimeOffset` 范围的 epoch 返回无重置时间（Go 可表示）；(3) RFC3339 小数秒超过 100ns 截断；
  (4) `resetTime` 为超出 double 范围的 JSON 数字时按“无重置时间”继续（Go 整包解码报错）；
  (5) 摘要 RawData 经 Go 形状 map 重建，键序 / 转义按 C205。
- C182 由 19 个 provider checker 收窄为剩余 18 个（移除 antigravity）；测试清单加入本切片 61 例。

## 残余风险

- 其余 18 个 provider 的 checker 未移植：这些通道仍不发起配额请求，保留既有行。
- 本轮未运行构建与测试；编译 / 分析器正确性仅由人工检查保证，线路形状由真实 `HttpExecutor` +
  记录型 `HttpMessageHandler` 锁定，脚本化非 2xx 分支由 `ScriptedExecutor` 覆盖。
- Antigravity 摘要接口为供应商私有协议，字段缺失 / 新增时按参照的宽容规则跳过（不会失败）。
