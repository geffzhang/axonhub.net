# §130 Zai 供应商配额检查器（固定 api.z.ai 端点 + 复用 ZhiPu 族采集器）

日期：2026-11-08
参照：`f272549f:internal/server/biz/provider_quota/zai_checker.go`（`zaiDefaultQuotaBaseURL`:11、
`ZaiQuotaChecker` / `NewZaiQuotaChecker`:13-19、`CheckQuota`:21-23、`SupportsChannel`:25-28、
`parseZaiQuotaResponse`:30-33），`provider_quota.go:390` 的 `registerZaiSupport`（定义见
`provider_quota.go:474-476`，在 `registerZhipuSupport` 之后、`registerCharmHyperSupport` 之前调用），
以及 `zai_checker_test.go` 的 5 个测试函数。

## 交付

- `src/AxonHub.Api/Orchestration/ZaiQuotaChecker.cs`：`IQuotaChecker` 薄包装，组合
  `ZhipuQuotaChecker` 的共享 `CollectFamilyQuota`。
  - 固定 `https://api.z.ai/api/monitor/usage/quota/limit`（忽略通道 BaseURL），providerType `"zai"`。
  - `SupportsChannel` 只匹配 `zai` / `zai_anthropic`。
  - `ParseZaiQuotaResponse(body, now)` 委托 `ParseFamilyQuotaResponse(body, "zai", now)`。
  - 采集、错误包装、禁用 32 上限、Go 解码与聚合全部沿用 §129 的共享采集器；共享文案硬编码
    `zhipu` 前缀（`zhipu quota check failed for all …`、`failed to parse zhipu quota response: …`、
    `zhipu API error: …`、禁用截断 warning），多 key 的 availability group 仍是 `zhipu_accounts`。
- `src/AxonHub.Api/Program.cs`：在 zhipu 之后注册 `ZaiQuotaChecker` 与
  `ProviderQuotaCheckerRegistration("zai", …)`，与参照 `registerZaiSupport` 的顺序一致；
  「剩余 checker」注释由 4 改为 3，并补 Zai 说明。
- `src/AxonHub.Api/Orchestration/ProviderQuotaCheckerRegistry.cs`：文档同步为「… MiniMax、ZhiPu 与
  Zai 已注册，其余 3 个后续切片」。
- `tests/AxonHub.Api.Tests/ZaiQuotaCheckerTests.cs`：10 个 `[Fact]` + 4 个 `[Theory]`
  （18 个 InlineData），共 28 例。

## 行为要点

- 参照报文完整映射：TOKENS_LIMIT 两条 → 5h / weekly 两窗口与 rawData；TIME_LIMIT 忽略；
  providerType 为 `zai`；`zai_anthropic` 使用同一固定端点；通道 BaseURL 6 组变体都不影响 URL。
- API 错误：`success:false` + msg 报
  `zhipu quota check failed for all 1 keys: zhipu API error: Authentication parameter not received …`
  （共享采集器文案）；缺失 key 直接抛 `channel has no API key` 且不发请求。
- 共享采集器：transport 失败报
  `zhipu quota check failed for all 1 keys: zhipu quota request failed: boom`；多 key 单账户耗尽被
  聚合跳过（overall available），两个账户的限额都带 `zhipu_accounts`；40 个禁用 key 截断为 32 并
  打印精确 warning `zhipu quota check truncates disabled keys: channel_id=42 disabled_keys=40 checked=32`。
- 执行器：>=400（401 / 403 / 429 / 500）包装为 `zhipu quota request failed: <status>`；201 / 202 / 204
  仍按报文解析；`Settings.Proxy` 路由到族代理执行器，直连执行器不使用。
- 解码：`ParseZaiQuotaResponse` 的畸形 body 保留 `failed to parse zhipu quota response: ` 前缀。

## 测试

- `tests/AxonHub.Api.Tests/ZaiQuotaCheckerTests.cs`：10 例 + 18 个 InlineData，共 28 个用例。
  - 线路：GET、固定 api.z.ai URL、raw Authorization、Content-Type、Accept-Language、无 body。
  - 报文映射、anthropic 类型、BaseURL 6 组、API 错误、缺失 key、通道类型门 5 组。
  - 共享采集器：全失败族前缀、多 key 分组、禁用 32 截断 warning、ParseZai 委托与解码前缀。
  - 执行器：>=400 4 组、2xx 3 组、代理路由。

`AxonHub.Api.Tests` 由 4954 增至 4982（+28），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
8400 增至 8428。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 4982 / 319 / 8428 复核。**

## 台账

- `porting/differences.jsonl`：C182 收窄为「剩余 3 个 checker（charm_hyper / commandcode / ollama）」，
  补 Zai 注册说明与 28 例；新增 C218（Zai 复用族采集器的固定主机、provider 标识、共享错误文案与
  availability group）；`lint` 通过、`render` 重新生成 `02-differences.md`（218 条）。
- `porting/01-baseline.md`：新增 §130；「配额路由与供应商配额采集」覆盖行补 Zai 并改为「其余 3 个」。
- `porting/03-acceptance.md`：追加 §130 静态计数段落。

## 下一步

- `charm_hyper` checker（参照 `provider_quota.go:391 / 478-480` 的 `registerCharmHyperSupport`，
  同样复用 ZhiPu 族采集器或按其自身报文接线）。
