# §121 ZenMux 供应商配额检查器（管理订阅端点）

日期：2026-11-07
参照：`f272549f:internal/server/biz/provider_quota/zenmux_checker.go`（`CheckQuota`:62-89、
`SupportsChannel`:91-102、`parseZenmuxQuotaResponse`:104-160、`parseZenmuxResetAt`:162-172、
`zenmuxWindowStatus`:174-182、`zenmuxAccountStatus`:184-195、`worstZenmuxStatus`:197-205、
`zenmuxStatusRank`:207-218、报文类型:16-52）、`provider_quota.go:438-440` 的 `registerZenmuxSupport`，
以及 `provider_contract_fixtures_test.go` 的 zenmux 返回体与 `provider_contract_test.go` 的五行
`{"zenmux", <type>, "", "zenmux_windows", 2}`（zenmux / zenmux_responses / zenmux_anthropic /
zenmux_gemini / zenmux_video）与 `TestQuotaCheckers_KeepZenmuxMonthlyQuotaInRawData`。

## 交付

- `src/AxonHub.Api/Orchestration/ZenmuxQuotaChecker.cs`：`IQuotaChecker` 实现 + `zenmuxSubscriptionResponse`
  系列的 Go 语义 JSON 解码 + `time.Parse(time.RFC3339)` 的分阶段解析器。
  - 凭据：`strings.TrimSpace(credentials.managementApiKey)`；为空（无凭据 / 非字符串 / 空白）报
    `channel has no management API key`，不读取 legacy `apiKey` / `apiKeys`。
  - 采集：固定 URL `https://zenmux.ai/api/v1/management/subscription/detail`，Bearer 经 `Auth`，
    Accept 由执行器补齐，无 body / Content-Type；`settings.proxy` 存在时**一次**解析代理 executor
    （畸形 settings 视为无代理，C013）。执行错误包装为 `zenmux quota request failed: %w`；状态不在
    [200,300)（executor 只对 ≥400 抛错，故覆盖 1xx/3xx）报 `zenmux quota request returned HTTP %d`；
    解码失败包装为 `failed to parse zenmux quota response: %w`。
  - 解码：根 `null` 对 struct 是 no-op；重复对象键合并（Go 复用非 nil 结构体 / 指针），标量
    last-wins，`data` 的 `null` 重置指针、`plan` / 两个窗口 / `quota_monthly` 的 `null` 是 no-op；
    键匹配按 ASCII 大小写折叠；非对象根与字段类型不匹配（含 `1e400`）按 Go 文案
    `json: cannot unmarshal <value> into Go struct field <Struct>.<FieldStack> of type <T>` 报错
    （文案差异沿用 C003 / C093）。`success=false` 时 `message` 非空报 `zenmux API error: %s`、
    为空报 `zenmux API returned success=false`；`data` 缺失（含 `null`）报
    `zenmux quota response contains no data`。
  - 状态：窗口 ratio ≥ 1 → exhausted、≥ `WarningThresholdRatio`（0.8）→ warning、其余 available；
    账户状态 `strings.ToLower(strings.TrimSpace(...))` 后 healthy / monitored → available、
    abusive → warning、suspended / banned → exhausted、其余 unknown；整体取三者最差，排名
    available 0 < warning 1 < exhausted 2 < unknown 3（unknown 比 exhausted 更差）。
  - 限额：固定两个 token limit，顺序 5h（`QuotaWindow5h`，5 小时）与 7d（`QuotaWindow7d`，7 天），
    `WithWindow` 由 `reset − 窗口长度` 得 `PeriodStart`；`NextResetAt` 取两个 reset 的最早者
    （nil 感知，不过滤过去）。归一化沿用共享 `ProviderQuotaData.Normalize`：ratio 截断到 1、
    ≥1 强制 exhausted、丢弃负数 / 非有限 ratio 的 limit、清空过期重置、按 ready 优先级推导总状态并
    重算总重置；未知账户状态通常被 ready 窗口掩盖，仅在两个窗口都被丢弃（ratio 为负）时可见。
  - reset：`time.Parse(time.RFC3339, *resets_at)`（不 trim）；nil → nil；失败报
    `invalid reset timestamp %q: %w`。分阶段解析器逐字复现 Go 的 `time.Parse` 文案（month / hour /
    minute / second / 时区偏移范围、`extra text`、`cannot parse ... as ...`），接受 Go 允许的小数秒
    （`.` / `,`）、一位或两位小时与 ±24:60 偏移；Go 接受但 `DateTimeOffset` 无法表示的值（年份 0000、
    超出 ±14:00 的偏移、UTC 时刻越界）报端口的范围兜底（C123 / C207）。
- `src/AxonHub.Abstractions/ChannelCredentials.cs`：新增 `GetManagementApiKey`，大小写折叠读取
  `managementApiKey`、重复键 last-wins、非字符串 / 非对象按 C013 视为缺失。
- `src/AxonHub.Api/Program.cs`：在 nanogpt 之后注册 `ZenmuxQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("zenmux", …)`，与参照注册顺序一致。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档同步为「Claude Code、Codex、
  Antigravity、xAI Subscription、GitHub Copilot、NanoGPT 与 ZenMux 已注册，其余 12 个后续切片」。
- `tests/AxonHub.Api.Tests/ZenmuxQuotaCheckerTests.cs`：32 个 `[Fact]` + 13 个 `[Theory]`
  （120 个 InlineData），共 152 例。

## 行为要点

- rawData：`plan`、`quota_monthly`、`account_status` 三个键，按 Go map 排序输出；`plan` 与
  `quota_monthly` 按 Go 字段序、null 原样输出（`GoJson`；double 格式的已接受差异见 C067）。
  `quota_monthly` 只作为原始数据保留，不生成 limit。
- 采集请求不依赖通道 base URL / 端点配置；五个 zenmux 通道类型都支持。
- 归一化后 limit 的 `Available` 字段由状态重新推导；状态为 unknown 时整体 `Ready=false`。

## 测试

- `tests/AxonHub.Api.Tests/ZenmuxQuotaCheckerTests.cs`：32 例 + 120 个 InlineData，共 152 个用例。
  - 线路形状：URL、`Authorization: Bearer`、`Accept: application/json`、无 Content-Type / body；
    请求数据 `Auth` 为 bearer 且头集合不含 Authorization（脚本化 executor 断言）。
  - 响应：两条窗口与 PeriodStart、最早 reset、空 data 的零值窗口、ratio 截断；`success=false`
    四组、缺 data 三组、reset 错误按窗口顺序包装（2 组）。
  - 管理 key：缺失矩阵 8 组、trim、优先于 legacy key、大小写折叠、重复键 last-wins；
    无凭据时构造直接拒绝。
  - 传输：错误逐字包装（内层为脚本异常）、1xx/3xx 状态四组、2xx 三组。
  - 解码：28 条 Go 错误文案（根类型、嵌套对象、各标量类型、`1e400`）、畸形 JSON、根 null、
    重复对象合并 / 标量 last-wins / null 重置 / 大小写折叠 / null 后重新赋值。
  - 状态：窗口状态 8 组、账户状态 10 组、最差状态排序、unknown 被 ready 窗口掩盖、两个负 ratio
    时 unknown 可见。
  - reset：5 组接受（偏移、小数秒）、亚 tick 截断、29 组 Go 错误文案、7 组 DateTimeOffset 范围
    （含 `+00:60` 可表示分支）。
  - rawData：完整响应逐字 JSON、契约 fixture（2 limit）、键数。
  - 代理：settings.proxy 路由到代理 executor、畸形 settings 直连、无 settings 直连。
  - `SupportsChannel`：五个 zenmux 类型接受，四类其他通道拒绝。

`AxonHub.Api.Tests` 由 3534 增至 3686（+152），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
6980 增至 7132。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]`
的静态计数；下一次全量运行应以 3686 / 319 / 7132 复核。**

差异：C182 由剩余 13 个 checker 收窄为 12 个；新增 C207（未移植 `WithRejectHTTPSDowngrade`、
transport 错误内层文本来自 .NET 栈、reset 的年份 0000 / ±24:60 与端口范围兜底沿用 C123）。
