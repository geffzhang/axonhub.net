# §128 MiniMax 供应商配额检查器（BaseURL 派生 /v1/token_plan/remains + general 模型 5h / weekly 窗口）

日期：2026-11-08
参照：`f272549f:internal/server/biz/provider_quota/minimax_checker.go`（`CheckQuota`:69-96、
`SupportsChannel`:98-100、`buildMinimaxQuotaURL`:102-114、`minimaxPeriodStart`:119-127、
`minimaxTotalPercent`:131-136、未被调用的 `minimaxUtilization`:138-147、`minimaxStatusForRatio`:151-165、
`parseMinimaxResponse`:167-302、局部 `worseStatus`:304-310、报文类型:16-59），`provider_quota.go:387`
的 `registerMinimaxSupport`（定义见 `provider_quota.go:466-468`，在 `registerKimiCodeSupport` 之后、
`registerZhipuSupport` 之前调用），以及 `minimax_checker_test.go` 的 15 个测试函数。

## 交付

- `src/AxonHub.Api/Orchestration/MinimaxQuotaChecker.cs`：`IQuotaChecker` 实现 +
  `MinimaxResponse` / `MinimaxBaseResp` / `MinimaxModelRemain` 的 Go 语义 JSON 解码 +
  `buildMinimaxQuotaURL` 的 URL 重建 + `MinimaxModelRow` 的 rawData 形状（GoDoubleConverter 逐属性生效）。
  - 凭据：legacy `APIKey` 先 `TrimSpace`；为空且 `APIKeys` 非空时取首个 `APIKeys[0]` 并 `TrimSpace`
    （不跳过空白项）；仍为空（含无凭据、空白、`apiKeys` 首项空白、仅 `oauth`、畸形 JSON）时抛出
    `channel has no API key`，不发起请求。
  - 采集：GET `buildMinimaxQuotaURL(ch.BaseURL)`，头为 `Authorization: Bearer <key>` 与
    `Content-Type: application/json`（参照不设 Accept），无 body；`Settings.Proxy` 非空时经
    `IProxyExecutorFactory` 派生执行器，否则用共享执行器。
  - URL 重建：`TrimSpace`；空、解析失败、scheme 为空或 host 为空时回退
    `https://www.minimaxi.com/v1/token_plan/remains`；否则重建 `scheme://parsed.Host + /v1/token_plan/remains`
    （userinfo / path / query / fragment 全部丢弃；scheme 小写、host 大小写保留、端口与 IPv6 保留）。
  - 响应：参照无显式状态码检查（Do 已对 >=400 报错），端口把 >=400 的 `HttpClientError` 包装为
    `minimax quota request failed: <inner>`，1xx / 3xx / 非 200 2xx 照常解析；解码失败包装为
    `failed to parse minimax quota response: <inner>`；`base_resp.status_code != 0` 时报
    `minimax API error: <status_msg> (code <status_code>)`（先于模型列表检查）；空列表报
    `minimax quota response contains no model remains`；无 `model_name == "general"` 报
    `minimax quota response contains no general model`。
  - 窗口：5h 窗口恒存在；`intervalTotalPercent = boost>0 ? boost/10.0 : 100.0`，
    `intervalUsedPercent = 100 - remaining`（不做非负夹紧），`intervalPercent = used/total*100`，
    ratio = intervalPercent/100；weekly 窗口仅在 `current_weekly_status == 1` 时加入（0 / 2 / 3 均跳过），
    口径同上（weekly_boost_permille）；窗口标签 `QuotaWindows.FiveHours` / `QuotaWindows.Weekly`，
    类型 `QuotaLimitTypes.Token`。
  - 状态与重置：`minimaxStatusForRatio(apiStatus, ratio)`：apiStatus==3 → exhausted；ratio>=1 →
    exhausted；ratio>=0.8 → warning；apiStatus==1 → available；其余 unknown。总体状态由局部
    `WorseStatus`（available 0 / warning 1 / exhausted 2，非表内值按 0）从 available 起取最差；
    `Ready` 跟随总体状态。`NextResetAt` 取 `end_time` / `weekly_end_time`（毫秒 > 0）中最早者
    （严格 `Before`，同刻保留先者）；`PeriodStart` 取 `start_time` / `weekly_start_time`
    （毫秒 > 0，否则 null）。
  - rawData：`{"rows": [...]}`，行字段与顺序 `modelName, intervalUsedPercent, intervalTotalPercent,
    intervalPercent, intervalStatus, intervalResetAt(omitempty), weeklyUsedPercent,
    weeklyTotalPercent, weeklyPercent, weeklyStatus, weeklyResetAt(omitempty),
    weeklyBoostPermille(omitempty)`；随后 `Normalize` 归一化（负 ratio limit 丢弃、ratio>1 截断、
    过期 reset 清除、rawData 保留原值）。
- `src/AxonHub.Api/Program.cs`：在 kimi_code 之后注册 `MinimaxQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("minimax", …)`，与参照 `registerMinimaxSupport` 的顺序一致；
  「剩余 checker」注释由 6 改为 5。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档同步为「… OpenCode Go、
  Kimi Code 与 MiniMax 已注册，其余 5 个后续切片」。
- `tests/AxonHub.Api.Tests/MinimaxQuotaCheckerTests.cs`：37 个 `[Fact]` + 19 个 `[Theory]`
  （124 个 InlineData），共 161 例。

## 行为要点

- 参照报文完整映射：weekly boost 1500 → total 150、remaining 90 → used 10 / percent 6.67 / available；
  interval boost 800 → total 80、remaining 68 → used 32 / percent 40；weekly status 0 / 2 / 3 跳过、
  1 加入；warning（ratio 0.9）、exhausted（remaining 0）、boost 后仍 available（100/1200）、
  api status 3、`base_resp.status_code=1001` → `minimax API error: invalid api key (code 1001)`；
  最早 reset 取 interval 与 weekly 的较小者；首个 general（其余模型不入 limits / rows）。
- URL：28 组数据表覆盖参照的 4 组与扩展边界——userinfo / path / query / fragment 丢弃、
  scheme 小写而 host 大小写保留、端口 / `:99999` / IPv6 保留、任意 scheme、非法输入（空、`https://`、
  `https:example.com`、`https:/example.com`、`//example.com`、空格与 `%zz`、`:bad` 端口）回退缺省端点；
  转义宿主 `caf%C3%A9.example` 逐字节还原为 `cafÃ©.example`（C216）。
- 状态口径：阈值 11 组、总百分比 6 组（0 / 负数 / ≤1000 / >1000）、`PeriodStart` 3 组、本地排名 6 组、
  `WorseStatus` 7 组（含 unknown 与 warning→exhausted）、负 ratio limit 丢弃但 rawData 保留、
  ratio>1 被 Normalize 截断、unknown 状态、无起点、过期 reset 清除、不可表示或非正 end_time 3 组。
- 解码与错误：null 根、非对象根 6 组（`provider_quota.minimaxResponse`）、类型不匹配 18 组
  （含小数 / 指数 / 溢出为 `number <字面量>` 的 Go 文案与 `model_remains` 元素路径）、null no-op、
  显式 null 重置切片、重复切片键按索引合并、重复字段与 ASCII 大小写折叠、未知键忽略、int64 边界、
  非法 UTF-8 → U+FFFD、null 元素保留；畸形 4 组 / 空 body 前缀；执行器 >=400（401 / 403 / 429 / 500）、
  transport 错误包装；201 / 202 / 204 仍按报文解析。
- 端口差异见 C216（HTTP 接缝、状态码分类、缺失 key 的错误路径、Go 解码语义、UTC 与本地偏移的
  rawData 文本、超出 DateTimeOffset 范围的时刻、百分号转义宿主还原、未移植的死代码 minimaxUtilization）。

## 测试

- `tests/AxonHub.Api.Tests/MinimaxQuotaCheckerTests.cs`：37 例 + 124 个 InlineData，共 161 个用例。
  - 线路形状（缺省端点 / BaseURL 派生 / Authorization / 仅 Content-Type / 无 body）、参照报文完整映射、
    rawData 全文与字段顺序。
  - URL 重建 28 组、缺省端点回退 3 组、转义宿主逐字节还原、路径常量。
  - 凭据 11 组（legacy trim / apiKeys 首项 / legacy 优先 / 空白回退 / 只读首项 / 无可用 key 6 组）。
  - weekly boost、interval boost 与 status 3 跳过、weekly 状态门 3 组、warning / exhausted /
    apiStatus 3 / boost 后 available、API 错误先于模型列表、最早 reset、首个 general、精确 general 4 组。
  - 阈值 11 组、总百分比 6 组、`PeriodStart` 3 组、本地排名 6 组、`WorseStatus` 7 组、负 ratio 丢弃、
    ratio 截断、unknown 状态、无起点、过期 reset、不可表示 / 非正 end_time 3 组、空列表 2 组。
  - Go 解码：null 根、非对象根 6 组、类型不匹配 18 组、null no-op、显式 null 重置、重复切片合并、
    重复字段与大小写折叠、未知键、int64 边界、非法 UTF-8、null 元素、解析前缀。
  - 畸形 / 空 body、执行器 >=400 4 组 / transport 1 组 / 2xx 3 组、代理路由 3 组、`SupportsChannel` 6 组。

`AxonHub.Api.Tests` 由 4649 增至 4810（+161），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
8095 增至 8256。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 4810 / 319 / 8256 复核。**
