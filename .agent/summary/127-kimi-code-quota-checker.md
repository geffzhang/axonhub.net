# §127 Kimi Code 供应商配额检查器（BaseURL 派生 /v1/usages + usage / limits / boosterWallet）

日期：2026-11-08
参照：`f272549f:internal/server/biz/provider_quota/kimi_code_checker.go`（`CheckQuota`:50-77、
`SupportsChannel`:79-81、`buildKimiCodeUsageURL`:83-102、`parseKimiCodeUsageResponse`:104-180、
`parseKimiCodeUsageRow`:182-216、`kimiCodeWindowLength`:218-238、`kimiCodeLimitLabel`:240-267、
`kimiCodeResetAt`:269-276、`kimiCodeResetAfterSeconds`:278-285、`parseKimiCodeBoosterWallet`:287-315、
`kimiCodeMoney`:317-325、`kimiCodeFixedPointToCents`:327-332、`kimiCodeInt`:334-344、
`firstKimiCodeInt`:346-353、`firstKimiCodeString`:355-362、`kimiCodeStatusForUsageRatio`:364-372、
`worseKimiCodeStatus`:374-381、报文类型:19-40）、`provider_quota.go:387` 的 `registerKimiCodeSupport`
（定义见 `provider_quota.go:462-464`，在 `registerOpenCodeGoSupport` 之后、`registerMinimaxSupport`
之前调用），以及 `kimi_code_checker_test.go` 的 3 个测试函数。

## 交付

- `src/AxonHub.Api/Orchestration/KimiCodeQuotaChecker.cs`：`IQuotaChecker` 实现 +
  `KimiCodeUsageResponse` / `KimiCodeUsageRow` / `KimiCodeBoosterWallet` 的 Go 语义 JSON 解码 +
  `KimiCodeInt`（float64 / 字符串 ParseFloat / amd64 饱和转换）+ `buildKimiCodeUsageURL` 的 URL 重建。
  - 凭据：legacy `APIKey` 先 `TrimSpace`；为空且 `APIKeys` 非空时取首个 `APIKeys[0]` 并 `TrimSpace`
    （不跳过空白项）；仍为空（含无凭据、空白、`apiKeys` 首项空白、仅 `oauth`、畸形 JSON）时抛出
    `channel has no API key`，不发起请求。
  - 采集：GET `buildKimiCodeUsageURL(ch.BaseURL)`，头为 `Authorization: Bearer <key>` 与
    `Accept: application/json`，无 body；`Settings.Proxy` 非空时经 `IProxyExecutorFactory` 派生执行器，
    否则用共享执行器。
  - URL 重建：`TrimSpace`；空、解析失败、scheme 为空或 host 为空时回退
    `https://api.kimi.com/coding/v1/usages`；清除 query 与 fragment；路径去尾斜杠，缺 `/v1` 时补上，
    再追加 `/usages`；空查询保留 `?`（ForceQuery）；路径按 `encodePath` 解码再编码；authority 保留
    原始拼写（C213）。
  - 响应：参照无显式状态码检查（Do 已对 >=400 报错），端口把 >=400 的 `HttpClientError` 包装为
    `kimi code usage request failed: <inner>`，1xx / 3xx / 非 200 2xx 照常解析；解码失败包装为
    `failed to parse kimi code usage response: <inner>`。
  - 行解析：usage 行（label 缺省 `Weekly limit`、窗口 7d）先加入；limits 每项的 `detail` 为对象时作为
    行源、否则用项本身；limit / used 至少一个为数字（字符串按 ParseFloat，float64 转 int64 截断），
    仅有 limit 时用 `remaining` 推导 used（不做非负检查）；行标签取 name / title（不含 scope）；
    ResetAt 键 reset_at / resetAt / reset_time / resetTime，ResetAfterSeconds 键 reset_in / resetIn /
    ttl / window（>0）；无数字的行跳过，全部跳过时报 `kimi code usage response contains no quota limits`。
  - 窗口与标签：window.duration / item.duration / detail.duration 取首个正数，单位取 window.timeUnit /
    item.timeUnit / detail.timeUnit，MINUTE / HOUR / DAY（大小写敏感）换算，其余按秒；标签按
    name / title / scope 逐键在 item 与 detail 中查找（键优先），缺失时按窗口时长生成
    `5h limit` / `90m limit` / `2d limit` / `45s limit`，仍无则 `Limit #<n>`；limit 的窗口标签命中
    `NormalizeQuotaWindowLabel`（5h / daily / 7d / 30d）时用归一化值，否则用行标签。
  - 状态与重置：ratio = used / limit（limit<=0 时为 0），>=1 exhausted、>=0.8 warning、其余 available；
    总体状态取本地排名（available 0 / warning 1 / exhausted 2）最高者，Ready 跟随总体状态；
    `NextResetAt` 取最早的重置时刻；resetAt 字符串按 RFC3339（含纳秒小数秒与 offset）解析，存在但
    不可解析时抑制 reset_in；reset_in 按注入时钟回推（C214）。
  - 钱包与 rawData：`balance.type == "BOOSTER"` 且 amount > 0 时换算 `balanceCents` / `totalCents`
    （amountLeft 缺省 0；`kimiCodeFixedPointToCents`：`0 < value < 1_000_000` → 1，否则
    `(value + 500_000) / 1_000_000`），`monthlyChargeLimitEnabled` 仅接受 JSON `true`，
    `monthlyChargeLimitCents` / `monthlyUsedCents` 与币种（charge limit → used → `USD`）；rawData 为
    `{"rows": [...], "boosterWallet": {...}}`（钱包仅在解析成功时出现）；随后 `Normalize` 归一化
    （ratio 截断、负 ratio limit 丢弃、过期 reset 清除、rawData 保留原值）。
- `src/AxonHub.Api/Program.cs`：在 opencode_go 之后注册 `KimiCodeQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("kimi_code", …)`，与参照 `registerKimiCodeSupport` 的顺序一致；
  「剩余 checker」注释由 7 改为 6。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档同步为「… Apertis、OpenCode Go
  与 Kimi Code 已注册，其余 6 个后续切片」。
- `tests/AxonHub.Api.Tests/KimiCodeQuotaCheckerTests.cs`：56 个 `[Fact]` + 29 个 `[Theory]`
  （162 个 InlineData），共 218 例。

## 行为要点

- 参照报文完整映射：usage 80 / 100（resetAt 带亚 tick 小数秒）→ weekly `7d` warning；limits 的
  `remaining: "0"` / `limit: "20"` / 窗口 300 MINUTE → `5h` exhausted；`detail` 中的 used / limit /
  title → `Daily limit` available；钱包 250 / 1250 / true / 5000 / 1234 / USD；rawData 逐字节排序输出；
  总体 exhausted、`NextResetAt` 取最早重置。
- URL：26 组数据表覆盖参照的 4 组与扩展边界——ForceQuery（`kimi?` / `kimi?#frag` / `kimi#frag?` /
  `kimi??`）、`encodePath`（`%2F` → `/`、`%3F` 保留、`%3B` → `;`、`café` → `%C3%A9`、空格 → `%20`、
  `%5C` 保留、`!` → `%21`）、scheme 小写而 host 大小写保留、多重尾斜杠、`/v1` 已存在、缺省端点回退
  3 组、userinfo 原始拼写（C213）。
- 行与标签：limit / used 的 double、数字字符串、截断、负值；仅有 limit 时 remaining 推导（含 unchecked
  负值）；name / title 标签与 scope 排除；detail name 覆盖 item title；detail 回退；空字符串跳过；
  时长标签与 `Limit #n`；usage 行回退 `Weekly limit` / `7d`；item 与 detail 标签来源分歧；无数字行
  跳过且索引不重排；零限额 3 组报文报错。
- 重置与状态：reset_at 四键与优先级、reset_in 四键与别名、reset_at 存在但不可解析时抑制 reset_in、
  注入时钟锚点、最早 reset、`.123456789` → 1_234_567 ticks、`+02:00` offset 保留、过期 reset 被
  Normalize 清除；阈值 6 组、`WorseStatus` 5 组（含 unknown 排名 0）、本地排名 5 组、warning 时 Ready。
- 钱包：BOOSTER 判定 7 组（GIFT / 无金额 / 0 / 负值 / 非数字 / 数组 / null）、固定点换算 9 组、
  字符串金额、币种回退 5 组、仅 JSON true 生效、字符串 cents。
- 解码与错误：根 null、非对象根 4 组（`provider_quota.kimiCodeUsageResponse`）、类型不匹配 6 组
  （含 1e400 的原始字面量文案与 `limits` 元素路径）、重复键合并（含大小写折叠）、嵌套对象替换而非合并、
  limits 按索引合并并在更短时截断、显式 null 重置、null 元素保留、未知键忽略、非法 UTF-8 → U+FFFD；
  畸形 / 空 body 前缀；执行器 >=400（401 / 403 / 429 / 500）与 transport 错误包装；201 / 202 / 204
  仍按报文解析。
- 端口差异见 C213（authority 原始拼写）、C214（reset_in 锚点时区与读取时机）与 C215（HTTP 接缝、
  状态码分类、缺失 key 的错误路径、凭据与 Go 解码语义）。

## 测试

- `tests/AxonHub.Api.Tests/KimiCodeQuotaCheckerTests.cs`：56 例 + 162 个 InlineData，共 218 个用例。
  - 线路形状（缺省端点 / BaseURL 派生 / Authorization / Accept / 无 body）与请求数据形状。
  - 参照报文完整映射、rawData 全文、URL 重建 26 组、缺省端点回退 3 组。
  - usage 行 / limits 行 / detail 包裹、标签优先级、窗口单位 8 组、时长标签 7 组、`Limit #n` 3 组。
  - 重置 4 + 4 键、别名 4 组、优先级、抑制、注入时钟、最早 reset、小数秒、offset、过期 reset。
  - 阈值 6 组、`WorseStatus` 5 组、本地排名 5 组、钱包判定 / 固定点 / 币种 / 字符串金额。
  - Go 解码：null 根、非对象根 4 组、类型不匹配 6 组、合并 / 替换 / 截断 / null 重置 / 未知键 /
    非法 UTF-8。
  - 凭据 11 组、畸形与空 body、类型不匹配 6 组、执行器 >=400 4 组 / transport 1 组 / 2xx 3 组。
  - 代理路由 3 组、`SupportsChannel` 6 组。

`AxonHub.Api.Tests` 由 4431 增至 4649（+218），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
7877 增至 8095。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 4649 / 319 / 8095 复核。**
