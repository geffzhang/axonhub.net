# §134 HTTP 重定向循环复刻与 HTTPS 降级拒绝策略

日期：2026-11-08
参照：`f272549f:llm/httpclient/client.go`（`WithRejectHTTPSDowngrade`:110-130、`WithRejectRedirects`:132-143、
`rejectHTTPSDowngrade`:146-151，`NewHttpClientWithProxy`:103-108 在派生代理客户端时保留降级策略）、
Go `net/http/client.go`（`do`:597-755、`redirectBehavior`:512、`checkRedirect`:502、`uerr`:622-638、
`defaultCheckRedirect`:833、`shouldCopyHeaderOnRedirect`:1005、`stripPassword`:1047、`refererForURL`:147），
四个调用点（`channel_llm.go:757-763`、`model_fetcher.go:542-548` 与 `:562`、
`ollama_checker.go:108-114`、`zenmux_checker.go:74-78`），以及三个参照测试
（`client_test.go:424-430`、`ollama_checker_test.go:325` / `:608`）。

## 交付

- `src/AxonHub.Llm/Http/HttpExecutor.cs`：重定向不再交给 `SocketsHttpHandler`，执行器内复刻 Go 的
  `Client.do` 循环。301/302/303 除 GET/HEAD 外改写为 GET 并丢弃 body 与四个 body 头；307/308 保留
  方法与 body；跨域敏感头粘性剥离（子域按 IDNA 规则保留）；Referer 生成（https→http 抑制、显式优先、
  否则上一跳 URL 去掉 userinfo）；无 Location 的 3xx 原样返回；Location 解析失败按 `uerr` 形状报错；
  CheckRedirect 拒绝时把 3xx 响应头交给 `OnResponseHeaders` 再包装为
  `<prefix><op> "<loc>": <reason>`；默认 10 跳上限，安装策略后无上限。
- `src/AxonHub.Llm/Http/HttpRedirectPolicy.cs`：`Follow` / `RejectHttpsDowngrade` / `RejectAll`。
- `src/AxonHub.Llm/Http/HttpRedirectException.cs`：策略拒绝时对调用方抛出的异常。
- `src/AxonHub.Llm/Http/HttpRedirectPolicies.cs`：`WithRejectHttpsDowngrade()` / `WithRejectRedirects()`
  扩展方法；脚本执行器（非 `HttpExecutor`）原样返回。
- 接线：
  - `PersistentOutboundTransformer.CustomizeExecutor` 对 commandcode / commandcode_anthropic 安装
    降级拒绝（C124 resolved）。
  - `ModelFetcher` 对命令码 / codex 通道安装（C158 第 ③ 项收窄为「按通道代理」）。
  - `OllamaQuotaChecker` / `ZenmuxQuotaChecker` 的 `ResolveExecutor` 在选择代理之后安装（C221 / C207）。
- `AllowAutoRedirect = false`：`Program.cs` 共享 handler、`ProxyExecutorFactory`、`VertexExecutor`
  默认 handler、`AntigravityExecutor` / `AntigravityVersion` / `AntigravityTransformer` 默认 handler；
  调用方自备 transport 不强制。
- `tests/AxonHub.Llm.Tests/HttpRedirectTests.cs`：16 个 `[Fact]`。

## 行为要点

- 方法改写：301/302/303 对非 GET/HEAD 改为 GET 并丢弃 body；307/308 保留方法与 body（Go 在
  `ireq.GetBody == nil && outgoingLength != 0` 时放弃跟随，端口以字节重放，等价于 `GetBody` 恒存在）。
- 头：初始请求头逐跳复制；首个跨域跳后 Authorization / Www-Authenticate / Cookie / Cookie2 /
  Proxy-Authorization / Proxy-Authenticate 粘性剥离；目标 host 等于初始 host 或是其子域时保留
  （IDNA 小写比较；host 含 `:` / `%` 不复制）；body 头在 body 被丢弃后不再复制。
- Referer：https→http 不写；显式 Referer 优先；否则取上一跳 URL 去掉 userinfo。
- 错误：解析失败 `failed to parse Location header "<loc>": parse "<loc>": <inner>`，URL 用当前请求
  地址并把密码替换为 `***`；策略拒绝 `refusing HTTPS to HTTP redirect` / `refusing redirect for
  credential-bearing request`，外层由执行器包装 `HTTP request failed: ` 或
  `HTTP stream request failed: `。
- 跳数：默认客户端 10 跳后报 `stopped after 10 redirects`；任一策略安装后无上限（Go 的策略钩子
  替换默认钩子）。
- 响应体：`ContentLength` 为 null 或 ≤2048 时排空后释放；拒绝时先观察 3xx 响应头再抛异常。

## 测试

- `tests/AxonHub.Llm.Tests/HttpRedirectTests.cs`：16 例，见 `01-baseline.md` §134 表格。
- `tests/AxonHub.Api.Tests/PersistentOutboundRetryTests.cs`：`CommandCodeCandidatesRefuseHttpsDowngradeRedirects`
  （2 组 InlineData）与 `OtherCandidatesKeepTheUnrestrictedExecutor`。
- `tests/AxonHub.Api.Tests/ModelFetcherTests.cs`：`FetchModelsCommandCodeRefusesHttpsDowngradeRedirect` 与
  `FetchModelsOpenAiChannelFollowsHttpsDowngradeRedirect`。
- `tests/AxonHub.Api.Tests/OllamaQuotaCheckerTests.cs`：`BalanceDowngradeRedirectIsRefused` 与
  `SettingsDowngradeRedirectIsRefused`（125 例）。
- `tests/AxonHub.Api.Tests/ZenmuxQuotaCheckerTests.cs`：`CheckQuotaRefusesHttpsDowngradeRedirects`（153 例）。

`AxonHub.Api.Tests` 由 5288 增至 5296（+8），`AxonHub.Llm.Tests` 新增 16 例，`AxonHub.Persistence.Tests`
保持 319，四个项目合计由 8734 增至 8750。**本切片按项目规则未运行 lint / build / test，上述数字为
`[Fact]` / `[InlineData]` 的静态计数；下一次全量运行应以 5296 / 319 / 8750 复核。**

## 台账

- `porting/differences.jsonl`：C124 resolved（降级拒绝落地）；C158 第 ③ 项收窄为「按通道代理」；
  C207 / C221 accepted（降级缺口关闭，Ollama 125 例）；新增 C222（Go 重定向循环的端口残差：
  URI 解析、错误文案、Referer 渲染与 Host 保留）；`lint` 通过、`render` 重新生成
  `02-differences.md`（222 条：160 resolved / 14 gap / 48 accepted）。
- `porting/01-baseline.md`：新增 §134。
- `porting/03-acceptance.md`：追加 §134 静态计数段落与「HTTP 重定向由 16 例锁定」协议行；
  C182 行的 ZenMux / Ollama 用例数更新为 153 / 125。

## 下一步

- 剩余真实缺口优先：C158 的按通道代理、C056 Vertex 流式、C014 通道管理面等。
- 第三方 codex 目录的 `WithRejectRedirects` 随该分支（C158 第 ④ 项）在 live-only 分支解冻时接入；
  策略扩展方法与 `HttpRedirectTests` 已就绪。
