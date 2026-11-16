# §129 ZhiPu 供应商配额检查器（固定 open.bigmodel.cn 端点 + TOKENS_LIMIT / CREDIT_LIMIT 窗口）

日期：2026-11-08
参照：`f272549f:internal/server/biz/provider_quota/zhipu_checker.go`（`CheckQuota`:130-132、
`SupportsChannel`:134-137、`collectZhipuFamilyQuota`:142-240、`fetchZhipuAccountQuota`:242-262、
`buildZhipuQuotaData`:269-290、`zhipuAggregateStatus`:292-322、`zhipuChannelLimits`:324-332、
`zhipuWindowLimits`:334-353、`zhipuBindingLimits`:355-378、`zhipuBindingRow`:380-403、
`zhipuWorstRows`:405-430、`zhipuAggregateLevel`:432-440、`zhipuEarliestReset`:442-459、
`zhipuRowResetAt`:461-473、`parseZhipuQuotaResponse`:475-481、`parseZhipuFamilyQuotaResponse`:483-495、
`parseZhipuAccountSnapshot`:497-537、`zhipuResponseWindow.toRow`:544-568、`zhipuResponseWindows`:570-583、
`zhipuBuildResponseWindows`:585-605、`zhipuCreditWindowSpec`:607-624、`zhipuWindowSpecForLength`:626-635、
`zhipuWindowSpecByName`:637-646、`zhipuPositionalSpec`:648-659、`zhipuChannelAPIKeys`:661-682、
`zhipuDisabledKeySet`:684-698、`zhipuKeyRef`:700-706、`zhipuKeySuffix`:708-715、
`buildZhipuQuotaURL`:717-723、`zhipuQuotaBaseFromChannelURL`:725-727、`zhipuStatusForRatio`:729-739、
`worseZhipuStatus`:741-752、`orderZhipuBuckets`:754 起），`provider_quota.go:389` 的
`registerZhipuSupport`（定义见 `provider_quota.go:470-472`，在 `registerMinimaxSupport` 之后、
`registerZaiSupport` 之前调用），以及 `zhipu_checker_test.go` 的测试函数。

## 交付

- `src/AxonHub.Api/Orchestration/ZhipuQuotaChecker.cs`：`IQuotaChecker` 实现 +
  `ZhipuQuotaResponse` / `ZhipuQuotaData` / `ZhipuLimitEntry` 的 Go 语义 JSON 解码 +
  `ZhipuWindowRow` / `ZhipuAccountQuota` 的 rawData 形状。
  - 族共享采集器：`CollectFamilyQuota(channel, providerType, quotaUrl, ct)` 与
    `ParseFamilyQuotaResponse(body, providerType, now)` 供后续 zai / charm_hyper 切片复用；
    `CheckQuotaAsync` 以 `providerType = "zhipu"` 与固定 URL 调用。
  - 凭据：`ChannelCredentials.GetAllApiKeys`（legacy `APIKey` + `APIKeys` 全部条目、`TrimSpace`、
    去重、跳过空白，C149）；`DisabledApiKeys` 按 `ParseDisabledApiKeys` 宽松解析（畸形 JSON
    为空集，C013 同族），条目 key 经 Go 语义 `TrimSpace` 后标记禁用，过期条目按注入时钟视为可用；
    无 key 时抛出 `channel has no API key`，不发起请求。
  - 采集：GET 固定 `https://open.bigmodel.cn/api/monitor/usage/quota/limit`（参照
    `buildZhipuQuotaURL` → `zhipuQuotaBaseFromChannelURL` 恒返回默认 host，忽略通道 BaseURL）；
    头为原样 key 的 `Authorization`（无 Bearer 前缀）、`Content-Type: application/json` 与
    `Accept-Language: en-US,en`，无 body；`Settings.Proxy` 非空时经 `IProxyExecutorFactory`
    派生执行器，否则用共享执行器。
  - 逐 key 顺序：启用 key 全量在前、禁用 key 在后串行采集；禁用池超过 32 个时截断并打印
    `zhipu quota check truncates disabled keys: channel_id=… disabled_keys=… checked=32`。
    单账户失败记为 `status=unknown` + `error=<inner>`，不影响其他账户；全部 key 失败报
    `zhipu quota check failed for all <len(keys)> keys: <首个错误>`（未截断的 key 总数）；
    启用 key 全失败（禁用 key 成功也不能掩盖）报 `… all <len(enabledKeys)> enabled keys: …`。
  - 响应：>=400 的 `HttpClientError` 包装为 `zhipu quota request failed: <inner>`；解码失败包装为
    `failed to parse zhipu quota response: <inner>`；`success == false` 报
    `zhipu API error: <msg>`（msg 为空时 `zhipu API error: API error code <code>`）；`data` 为空报
    `zhipu quota response contains no data`；既无 `CREDIT_LIMIT` 也无 `TOKENS_LIMIT` 条目报
    `zhipu quota response contains no TOKENS_LIMIT or CREDIT_LIMIT entries`；1xx / 3xx / 非 200 2xx
    照常解析（执行器只管 >=400）。
  - 窗口：`CREDIT_LIMIT` 条目优先（`EqualFoldAscii`），按 unit/number 以 Go int64 纳秒乘法
    （`unchecked` 回绕）识别 5h / 7d 规格，无法识别时按索引位置回退（0 → 5h、1 → weekly）；
    否则读取 `TOKENS_LIMIT` 条目，先按 `nextResetTime` 缺失 / 非正者优先排序，再按索引映射
    5h / weekly。行字段 `window` / `usedPercent`（percentage/100）/ `status` / `resetAt` /
    `usage` / `used`（currentValue）/ `remaining`；状态阈值 >=1 exhausted、>=0.8 warning、
    否则 available。
  - 聚合：单个可用账户（启用、无错误、有行）时按窗口给出限额；多账户时汇总为 `zhipu_accounts`
    availability group 的绑定限额（各账户最紧的已知窗口，严格 `>` 保留先者）；总体状态取可用
    账户最差、全部不可用时 exhausted 且 not ready；`NextResetAt` 取最早非空重置（严格 `<`）；
    rawData 为 `{"level": …, "rows": [...], "accounts": [...]}`，账户含 ref（`SHA-256("zhipu:"+key)`
    前 8 位十六进制）、suffix（末尾 4 个 rune，≤4 时取全串）、disabled / status / ready /
    level / error / rows。
  - 重置文本：行 `resetAt` 与派生 `NextResetAt` 按 UTC `yyyy-MM-dd'T'HH:mm:ss'Z'` 写（亚秒丢弃）；
    epoch 毫秒超出 `DateTimeOffset` 范围时行 ResetAt 为 null 且不派生重置（C123 / C217）。
    最后 `Normalize` 归一化（负 ratio limit 丢弃、ratio>1 截断、过期 reset 清除、rawData 保留原值）。
- `src/AxonHub.Api/Program.cs`：在 minimax 之后注册 `ZhipuQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("zhipu", …)`，与参照 `registerZhipuSupport` 的顺序一致；
  「剩余 checker」注释由 5 改为 4，并补 ZhiPu 说明。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档同步为「… Kimi Code、
  MiniMax 与 ZhiPu 已注册，其余 4 个后续切片」。
- `tests/AxonHub.Api.Tests/ZhipuQuotaCheckerTests.cs`：40 个 `[Fact]` + 18 个 `[Theory]`
  （104 个 InlineData），共 144 例。

## 行为要点

- 参照报文完整映射：TOKENS_LIMIT 两条（percentage 25.5 / 10）→ 5h / weekly 两窗口与 rawData 全文；
  单窗口只保留 5h；90% warning 仍 ready；100% exhausted；API 错误保留中文 msg 与
  `failed for all 1 keys` 包装；无 msg 时回退 code；缺失 key 不发请求。
- CREDIT_LIMIT：unit 3 / number 的数量映射到 5h、unit 6 / number 映射到 weekly；usage / currentValue /
  remaining 原样进入行；unit/number 无法识别时按索引回退；`unit3` 溢出字面量
  `2251799813685253`（5 + 2^51）回绕为 5h@index 1、`unit6` 溢出字面量 `281474976710657`
  （1 + 2^48）回绕为 weekly@index 0。
- 多 key：每账户的 ref / suffix / status / rows 全部入 rawData；单账户限额时 One limit per window；
  多账户时限制为 `zhipu_accounts` 组的绑定窗口（测试断言 availability group、routing Open /
  Exhausted、全禁用单 key 无 limit）；禁用账户保留行但不贡献 limit；启用 key 失败不能被禁用 key
  的成功掩盖；单账户失败不影响其余账户。
- 禁用簿记：过期条目按可用处理；条目 key 先去空白再匹配；40 个禁用 key 只发 32 个请求并打印
  warning `"zhipu quota check truncates disabled keys: channel_id=42 disabled_keys=40 checked=32"`；
  40 key 全失败时请求数仍为 32、外层文案用未截断的 40；畸形 disabled 列视为无禁用。
- Go 解码：重复 `data` 键字段级合并、重复 `limits` 键按索引合并 / 截断 / 增长；显式 null 重置
  指针与切片；字段名 ASCII 大小写折叠且 null 不改旧值；int64 边界 2 组；浮点 `1e308` / `1e-4000`
  → 0 / `5e-324` → `double.Epsilon`；类型错误 21 组复刻 `provider_quota.` 包名、结构体点路径与
  `number 1.5` / `number 1e400` 字面量；缺失 `success` 的 3 组按 API 错误降级（code 0 / 200）。
- 状态：阈值 8 组 + NaN → available；`WorseStatus` 6 组；本地排名 6 组；Normalize 截断 ratio>1；
  聚合跳过 exhausted 账户；绑定限额取使用率最高的已知窗口（平局保留 5h）；最差行平局保留先出现
  账户；level 与最早重置沿账户列表遍历。
- 重置：亚秒丢弃并可按 `'Z'` 文本解析；`long.MaxValue` 越界丢弃；非正 2 组忽略；不可解析 3 组
  视为无重置。
- 凭据 10 组（legacy / apiKeys / trim / 去重 / 空白 / 畸形 / OAuth-only）；suffix 5 组
  （`test-key` → `-key`、`abcd`、`键三键四` 全串、`一键二键三四` → `二键三四`、`bbbb-account-0002`
  → `0002`）；`SupportsChannel` 6 组；`BuildQuotaUrl` 6 组（null / `z.ai` / 非 URL 等全部返回固定
  端点）；代理路由 3 组；畸形 settings 回退直接执行器；transport 包装；401 / 403 / 429 / 500 4 组；
  201 / 202 / 204 仍按报文解析。

## 测试

- `tests/AxonHub.Api.Tests/ZhipuQuotaCheckerTests.cs`：40 例 + 104 个 InlineData，共 144 个用例。
  - 线路：GET、固定 URL、`Authorization == "test-key"`（无 Bearer）、`Content-Type`、
    `Accept-Language: en-US,en`、无 body。
  - 报文：HappyPath 全字段与 rawData 全文、单窗口、warning、exhausted、API 错误（含中文 msg 与
    code 回退）、缺失 key、apiKeys 回退、空白跳过、畸形 JSON 4 组、缺 data 2 组、缺窗口条目 3 组、
    无 reset 的 5h 桶、两个 reset 都保留顺序、最早 reset（Unix 4087947600）、总体取最差。
  - CREDIT_LIMIT：窗口映射 11 组、金额 longs、多 key 全量账户 / limits / availability group /
    路由 Open / Exhausted、禁用账户、启用失败、全禁用单 key、单账户失败。
  - 禁用簿记：过期按可用、条目 trim、32 截断 + 精确 warning、全失败 40 计数、畸形 disabled 列。
  - Go 解码：重复 data / 重复 limits、null 重置、大小写折叠 + null 不改旧值、int64 边界 2 组、
    浮点 3 组、类型错误 21 组、缺失 success 3 组。
  - 状态与聚合：阈值 8 组 + NaN、`WorseStatus` 6 组、排名 6 组、Normalize、聚合跳过 exhausted、
    绑定限额平局、最差行平局、level / 最早重置。
  - 重置：亚秒丢弃、越界丢弃、非正 2 组、不可解析 3 组。
  - 凭据 10 组、suffix 5 组、`SupportsChannel` 6 组、`BuildQuotaUrl` 6 组、代理路由 3 组、
    畸形 settings、transport、状态失败 4 组、非 200 2xx 3 组。

`AxonHub.Api.Tests` 由 4810 增至 4954（+144），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
8256 增至 8400。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 4954 / 319 / 8400 复核。**

## 台账

- `porting/differences.jsonl`：C182 收窄为「剩余 4 个 checker（zai / charm_hyper / commandcode /
  ollama）」，补 ZhiPu 注册说明与 144 例；新增 C217（HTTP 接缝、raw Authorization、Go 解码语义、
  UTC 重置文本、int64 纳秒回绕窗口、shared collector 复用）；`lint` 通过、`render` 重新生成
  `02-differences.md`（217 条）。
- `porting/01-baseline.md`：新增 §129（交付、行为、测试与计数 4810 → 4954 / 8256 → 8400）；
  「配额路由与供应商配额采集」覆盖行补 ZhiPu 并改为「其余 4 个」。
- `porting/03-acceptance.md`：追加 §129 静态计数段落。

## 下一步

- `zai` checker 复用本切片的 `CollectFamilyQuota` / `ParseFamilyQuotaResponse`（参照
  `provider_quota.go:390 / 474-476` 的 `registerZaiSupport`），随后 `charm_hyper`
  （`provider_quota.go:391 / 478-480`）。
