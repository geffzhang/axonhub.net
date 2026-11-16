# §126 OpenCode Go 供应商配额检查器（固定 /zen/go/v1/usage 端点 + rolling / weekly / monthly 百分比窗口）

日期：2026-11-07
参照：`f272549f:internal/server/biz/provider_quota/opencode_go_checker.go`（`CheckQuota`:82-106、
`SupportsChannel`:109-111、`parseResponse`:113-210、`parseOpenCodeGoResetsAt`:215-246、
`opencodeGoWindowLabel`:251-262、`opencodeGoPeriodStart`:268-279、`normalizeOpenCodeGoWindowStatus`:281-289、
`quotaStatusRank`:291-302、`opencodeGoAPIKey`:70-78、报文类型:23-50）、`provider_quota.go:458-460` 的
`registerOpenCodeGoSupport`（在 `registerApertisSupport` 之后、Kimi 之前调用，见 `provider_quota.go:386`），
以及 `opencode_go_checker_test.go` 的 16 个测试函数。

## 交付

- `src/AxonHub.Api/Orchestration/OpenCodeGoQuotaChecker.cs`：`IQuotaChecker` 实现 +
  `OpenCodeGoUsageResponse` / `OpenCodeGoUsageWindows` / `OpenCodeGoUsageWindow` 的 Go 语义 JSON 解码 +
  `resetsAt` 的 RFC3339 / Unix 秒 / epoch 毫秒三态解析 + 共享 `GoTimeParsing` 与周期起点辅助函数。
  - 凭据：遍历 `ChannelCredentials.GetAllApiKeys`（legacy `apiKey` 优先，OAuth 时排除 legacy；不含
    VertexAPIKeys，沿用 C149），逐个 `strings.TrimSpace`，取第一个非空项；全部为空（含无凭据、空白、
    畸形 JSON）时抛出 `channel has no API key`，不发起请求。
  - 采集：GET 固定 `https://opencode.ai/zen/go/v1/usage`（不读 BaseURL），头为
    `Authorization: Bearer <key>` 与 `Accept: application/json`，无 body；`Settings.Proxy` 非空时经
    `IProxyExecutorFactory` 派生执行器，否则用共享执行器。
  - 响应：参照无显式状态码检查（Do 已对 >=400 报错），端口把 >=400 的 `HttpClientError` 包装为
    `opencode go usage request failed: <inner>`，1xx / 3xx / 非 200 2xx 照常解析；transport 错误同样包装。
  - 窗口：rolling / weekly / monthly 三个指针窗口；`percent` 缺省 0；`resetsAt` 为空、null、负值、
    大于 1e15、非法字符串或非字符串 / 数字类型时跳过该窗口；全部跳过时报
    `could not parse OpenCode Go usage windows`；`resetInSec` 为 reset 与 now 之差并夹到 >= 0。
  - `resetsAt` 解析：字符串走 `GoTimeParsing.TryParseRfc3339`（保留 offset，纳秒按 100ns 截断，C123）；
    数字按 double 读取后拒绝 `< 0` 或 `> 1e15`，`>= 1e12` 视为 epoch 毫秒（`time.UnixMilli`），否则
    Unix 秒（`time.Unix`）；Go 用 Local 时区呈现，端口返回 UTC 同一瞬间（C123 / C212），超出
    DateTimeOffset 年份范围的值返回 null（Go 中合法，约公元 33658 年）。
  - 状态：`usageRatio = percent / 100`，`>= 1` exhausted、`>= 0.8` warning、其余 available；总体状态取
    三个窗口排名最高者（exhausted 2 / warning 1 / available 0 / 其他 -1），Ready 跟随总体状态；
    `NextResetAt` 取最早窗口 reset（Normalize 再按 now 过滤并重算）。
  - 限额与 rawData：每个窗口一个 token limit，window 为 `5h` / `weekly` / `monthly`，`PeriodStart` 分别为
    reset-5h、reset-7d、上一日历月同日（月末夹紧、保留 offset）；rawData 为 `plan_type:"go"` 与按窗口名
    索引的 `usage_percent` / `reset_in_seconds` / `reset_time`（秒级 RFC3339，零 offset 写 Z）/
    `status` / `percent_remaining`（`100 - percent`）与可选的 `api_status`（原样透传，不参与状态判定）；
    随后 `Normalize` 归一化（ratio 截断、负 ratio limit 丢弃、过期 reset 清除、rawData 保留原值）。
- `src/AxonHub.Api/Program.cs`：在 apertis 之后注册 `OpenCodeGoQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("opencode_go", …)`，与参照 `registerOpenCodeGoSupport` 的顺序一致；
  「剩余 checker」注释由 8 改为 7。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档同步为「… Apertis 与 OpenCode Go
  已注册，其余 7 个后续切片」。
- `tests/AxonHub.Api.Tests/OpenCodeGoQuotaCheckerTests.cs`：34 个 `[Fact]` + 16 个 `[Theory]`
  （88 个 InlineData），共 122 例。

## 行为要点

- 三窗口完整映射：rolling 12% / weekly 85% / monthly 35% → 总体 warning；三个 token limit 的窗口标签、
  PeriodStart（reset-5h / reset-7d / 上一日历月）与最早的 NextResetAt 均被断言；rawData 逐字节排序输出。
- 阈值：0 / 79.9 available、80 / 99.9 warning、100 / 120 exhausted；`NormalizeWindowStatus` 与
  `QuotaStatusRank` 各有独立数据表（6 + 6 组）。负 percent 的 limit 被 Normalize 丢弃但 rawData 保留
  -5 / 105；120% 的 limit ratio 截断为 1 而 rawData 保留 120 / -20。
- `resetsAt`：RFC3339 字符串（含 `.905` 与纳秒截断、`+02:00` offset）、Unix 秒、epoch 毫秒、阈值
  999999999999 秒 / 1e15 毫秒（Go 合法但超出 DateTimeOffset，端口返回 null）、1e300、负值、null、空串、
  空白串、布尔值、对象、无时区字符串；毫秒小数秒在 raw `reset_time` 中按秒级格式丢弃；数字秒与毫秒
  产生同一 UTC 瞬间；`4086082800000` 与参照 fixture 对齐到 `2099-06-25T15:00:00Z`。
- 窗口选择：仅 rolling 可解析时只保留该 limit；rolling 非法 / weekly null / monthly 负值全部跳过时报
  `could not parse OpenCode Go usage windows`；`{"error":"unauthorized"}` 与缺失 resetsAt 同样报错；
  `api_status` 原样出现在 rawData 中且不覆盖 percent 派生的 status。
- 时间与归一化：过期 reset 的 `reset_in_seconds` 夹到 0、limit 与总体 NextResetAt / PeriodStart 被清除但
  rawData 保留原 reset_time；最早窗口 reset 胜出；月度 PeriodStart 对 3 月 31 日 / 闰年 2 月 /
  跨年 1 月 / `+05:30` 偏移做日历月夹紧。
- 解码：根 null 为空结构体；非对象根与指针类型不匹配报 `provider_quota.*` 包名文案；结构体字段错误用
  裸结构体名与点路径；重复键 / ASCII 大小写折叠合并；null 对非指针标量 no-op、对窗口指针清空；
  `resetsAt` 保留原文（显式 null 覆盖先前合法值并导致窗口跳过）；未知键忽略；非法 UTF-8 字节替换为
  U+FFFD（`GoJsonUtf8`）。
- 错误路径：畸形 / 空 body 报 `parse OpenCode Go usage response: <inner>`；类型不匹配同样带此前缀；
  执行器 >=400（401 / 403 / 429 / 500）与 transport 错误报 `opencode go usage request failed: <inner>`；
  201 / 202 / 204 仍按报文解析（参照不做状态码检查）。
- 端口差异见 C212（共享执行器接缝、>=400 包装、数字 resetsAt 的 UTC 呈现、DateTimeOffset 年份范围、
  Go 解码语义与 UTF-8 修复、C149 凭据边界、注入时钟的读取时机）。

## 测试

- `tests/AxonHub.Api.Tests/OpenCodeGoQuotaCheckerTests.cs`：34 例 + 88 个 InlineData，共 122 个用例。
  - 线路形状（固定 URL / Authorization / Accept / 无 body）与请求数据形状。
  - 三窗口完整映射、rawData 全文、阈值 6 组、负 percent、>100%、状态排名 6 组、窗口标签 5 组、
    `FormatRfc3339` 4 组。
  - `ParseResetsAt` 合法 9 组 / 非法 11 组；毫秒小数秒、offset、Unix 秒 / 毫秒等价 2 组、epoch 毫秒参考值。
  - 部分窗口、全部不可解析、无 usage、缺失 resetsAt、`api_status`、过期 reset、最早 reset、月度周期
    起点 4 组。
  - 凭据 8 组（legacy trim / apiKeys 首个非空 / legacy 优先 / 空白回退 / OAuth 排除 / OAuth legacy +
    apiKeys / 无可用 key）、畸形与空 body、类型不匹配 4 组、执行器 >=400 4 组 / transport 1 组 /
    2xx 3 组。
  - Go 解码：null 根、非对象根 4 组、类型不匹配 6 组、空窗口对象、重复 / 大小写折叠合并、null 指针重置、
    未知键、显式 null resetsAt、非法 UTF-8。
  - 代理路由 3 组、`SupportsChannel` 6 组。

`AxonHub.Api.Tests` 由 4309 增至 4431（+122），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
7755 增至 7877。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 4431 / 319 / 7877 复核。**
