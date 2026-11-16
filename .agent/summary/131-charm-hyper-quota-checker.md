# §131 Charm Hyper 供应商配额检查器（BaseURL 派生 /v1/credits + 独立 credits 采集）

日期：2026-11-08
参照：`f272549f:internal/server/biz/provider_quota/charm_hyper_checker.go`（`charmHyperBaseline`:17、
`charmHyperWarningThreshold`:20、`CharmHyperCreditsResponse`:23-25、`CharmHyperQuotaChecker`:28-30、
`NewCharmHyperQuotaChecker`:32-34、`CheckQuota`:37-71、`parseResponse`:73-94、`computeStatus`:96-109、
`SupportsChannel`:111-116、`extractAPIKey`:118-127、`buildCharmHyperQuotaURL`:129-140），
`provider_quota.go:391` 的 `registerCharmHyperSupport`（定义见 `provider_quota.go:478-480`，在
`registerZaiSupport` 之后、`registerCommandCodeSupport` 之前调用），以及 `charm_hyper_checker_test.go`
的 14 个测试函数。

## 交付

- `src/AxonHub.Api/Orchestration/CharmHyperQuotaChecker.cs`：独立 `IQuotaChecker`（不复用 ZhiPu 族采集器）。
  - `BaseURL == ""` 时先取默认 `https://hyper.charm.land`，再按 Go `url.Parse` 重建 `/v1/credits`；
    GET、`Bearer <key>`、仅 `Accept: application/json`，无 Content-Type、无 body。
  - 严格 200 判定：>=400 走执行器错误包装，<400 非 200 报供应商自定义文案。
  - Go `encoding/json` 单字段解码语义（折叠 / 重复键 / null / 类型文案 / 溢出 / 下溢）。
  - `charmHyperBaseline = 100`、`charmHyperWarningThreshold = 20`；单一 `credits` 窗口 token limit；
    rawData `{"balance": <float64>}`。
  - `SupportsChannel` 匹配 `openai` / `openai_responses` 且主机属于 `hyper.charm.land`。
- `src/AxonHub.Api/Program.cs`：在 zai 之后注册 `CharmHyperQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("charm_hyper", …)`，与参照 `registerCharmHyperSupport` 的顺序一致；
  「剩余 checker」注释由 3 改为 2，并补 Charm Hyper 说明。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档同步为「… Zai 与 Charm Hyper
  已注册，其余 2 个后续切片」。
- `tests/AxonHub.Api.Tests/CharmHyperQuotaCheckerTests.cs`：14 个 `[Fact]` + 10 个 `[Theory]`
  （53 个 InlineData），共 67 例。

## 行为要点

- 线路：缺省与自定义主机都是 GET `/v1/credits`，只带 `Accept` 与 Bearer；`Settings.Proxy` 非空时
  走代理工厂派生执行器，直连执行器不使用。缺失 key 报
  `missing API key for Charm Hyper channel` 且不发起请求；`apiKeys` 只取第一个元素 TrimSpace，
  不扫描后续 key。
- URL 重建复刻 `buildCharmHyperQuotaURL`：TrimSpace → `url.Parse`，解析失败回退
  `https://hyper.charm.land/v1/credits`；成功后清空查询 / userinfo / fragment、Path 设为 `/v1/credits`，
  再由 `URL.String()` 输出。覆盖 ForceQuery（尾随 `?`）、opaque（`https:opaque`）、相对 URL、
  协议相对、端口与 userinfo 等 13 组。
- 判定：transport / >=400 包装为 `executing Charm Hyper quota request: <inner>`（401 的完整文本为
  `executing Charm Hyper quota request: GET - https://hyper.charm.land/v1/credits with status 401 Unauthorized`）；
  201 / 204 / 302 报 `Charm Hyper quota API returned status <code>: <body>`。
- 解码：畸形 JSON 包装 `parsing Charm Hyper quota response: `；root 非对象、字段类型错误与数字溢出
  都使用 Go 的 `json: cannot unmarshal …` 文案（`provider_quota.CharmHyperCreditsResponse` /
  `CharmHyperCreditsResponse.balance of type float64`）；`1e-400` 下溢按 strconv 归零；缺失 / null
  余额报 `Charm Hyper quota response missing balance field`。
- 状态：`usageRatio = 1 − balance/100`（负值夹 0）；0 → exhausted / ratio 1 / not ready；≤20 → warning；
  >20 → available。负余额经 `Normalize` 后整体仍为 warning / ready，limit ratio 夹到 1 并标 exhausted。
- 通道门：`openai` / `openai_responses` + `DetectProviderFromURL == "charm_hyper"`（host 等于
  `hyper.charm.land` 或以 `.hyper.charm.land` 结尾、大小写不敏感、允许端口）。

## 测试

- `tests/AxonHub.Api.Tests/CharmHyperQuotaCheckerTests.cs`：14 例 + 53 个 InlineData，共 67 个用例。
  - 线路：GET、缺省 / 自定义 URL、Bearer、仅 Accept、无 body、代理路由。
  - 阈值：80 / 100 / 150 / 20.000001 / 1 / −5 六组边界、20 的 warning 边界、0 exhausted、
    负余额夹取与 limit exhausted、`1e-400` 下溢。
  - 解码：缺失余额 3 组、重复键 / null 2 组、7 组 Go 文案、畸形 JSON 包装。
  - URL：`BuildQuotaUrlFollowsGoUrlSemantics` 13 组。
  - 状态码：401 执行器文案、transport 失败包装、201 / 204 / 302 供应商文案 3 组。
  - 凭据与通道门：缺 key 3 组、key 优先级 3 组、`SupportsChannel` 8 组。

`AxonHub.Api.Tests` 由 4982 增至 5049（+67），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
8428 增至 8495。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 5049 / 319 / 8495 复核。**

## 台账

- `porting/differences.jsonl`：C182 收窄为「剩余 2 个 checker（commandcode / ollama）」，补 Charm Hyper
  注册说明与 67 例；新增 C219（独立 credits 检查器、严格 200 判定、Go 解码与 net/url 重建边界，
  含唯一残余差异 authority 百分号转义拼写，与 C213 同族）；`lint` 通过、`render` 重新生成
  `02-differences.md`（219 条）。
- `porting/01-baseline.md`：新增 §131；「配额路由与供应商配额采集」覆盖行补 Charm Hyper 并改为
  「其余 2 个」。
- `porting/03-acceptance.md`：追加 §131 静态计数段落。

## 下一步

- `commandcode` checker（参照 `provider_quota.go:392 / 410-…` 的 `registerCommandCodeSupport`）。
- 之后是 `ollama` checker（参照 `provider_quota.go:393` 的 `registerOllamaSupport`），落地后
  C182 可关闭。
