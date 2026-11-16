# §212 端点探测使用按通道代理 client（C286）

## 背景

参照 `ChannelService.DetectChannelEndpoints` 先用 `buildChannelWithOutbounds` 构建通道
（`channel_llm.go:208-212` → `buildChannelWithTransformer`），随后 `detectChannelEndpoint` 的三个
probe 统一调用 `ch.HTTPClient.Do(probeCtx, httpReq)`（`channel_endpoint_detect.go:166`）。该 client
由 `getHttpClient(c.Settings)` 决定（`channel_llm.go:121-127`、`:707`）：settings 或 proxy 为 nil 时
返回进程共享 client，否则 `httpClient.WithProxy(settings.Proxy)` 新建按通道 client；两个 Command
Code 类型还会附加 `WithRejectHTTPSDowngrade`（`:708-713`）。端口此前三个 probe 一律走共享
`IExecutor`，台账 C150 的差异项 (1) 与 C158 的 decision 都把该残余记为“端点探测仍走共享
executor”。本切片闭合。

## 参照行为

- `buildChannelWithTransformer` 在构建 outbound 前先 `getHttpClient`，并把 Command Code 的
  HTTPS 降级拒绝挂到 client 上；outbound 与检测共用同一个 `ch.HTTPClient`。
- `detectChannelEndpoint` 复用 `ch.Outbounds[apiFormat]` 或惰性构建 generic outbound，但请求一律
  经 `ch.HTTPClient` 发出。
- 检测路径每调用一次就新建一个 `WithProxy` client（参照不做跨请求缓存）。
- 代理 URL 配置错误不会让 client 构建失败，而是在请求时由 `getProxyFunc` 闭包报告。

## 端口实现

- `ChannelEndpointDetectService` 新增可选构造参数 `ChannelProxyExecutors? channelProxyExecutors`。
- `DetectAsync` 在三个 probe 启动前解析一次 executor：
  `channelProxyExecutors?.Resolve(candidate) ?? executor`；无 proxy 时 `Resolve` 返回共享 executor，
  有 proxy 时返回按 channelId + proxy 指纹缓存的代理 executor（与推理 / token 刷新共用缓存条目）。
- 两个 Command Code 类型（`commandcode` / `commandcode_anthropic`）对该 executor 应用
  `WithRejectHttpsDowngrade()`，对应参照 `channel_llm.go:708-713`。
- `Probe` 接收解析后的 executor 并用它发出请求；三个并发 probe 共用同一个实例，对应参照的单个
  `ch.HTTPClient`。
- 更新 `ChannelOutboundFactory` 中关于降级策略落地位置的注释（推理与检测两处）。
- 生产 DI 已有 `ChannelProxyExecutors` 单例注册（Program.cs），无需新增注册。

## 差异

新增 C286（resolved）。与参照的实现差异：参照每次检测都经 `WithProxy` 新建 client，端口复用通道
缓存中的代理 executor（同一 proxy 配置，缓存命中与否不影响请求内容、URL、鉴权或结果分类）；代理
配置错误与参照一样在请求时报告（C202）。C150 的差异项 (1) 与 C158 的 decision 已更新为闭合。
台账 286 条（203 accepted / 0 gap / 83 resolved）。

## 测试

`tests/AxonHub.Api.Tests/AdminChannelEndpointDetectEndpointTests.cs`（31 例）新增 3 例：

- `DetectChannelEndpointsProbesThroughTheChannelProxyClient`：settings.proxy 存在时三条探测都经
  proxy executor，代理配置（url / username / password）取自 settings。
- `DetectChannelEndpointsWithoutAChannelProxyKeepsTheSharedExecutor`：无 proxy 时使用共享 executor，
  不构建代理。
- `DetectChannelEndpointsRefusesHttpsDowngradeRedirectsForCommandCodeChannels`：真实 `HttpExecutor`
  配 302 重定向 handler，Command Code 通道的三个 probe 都拒绝 HTTPS→HTTP 跳转，结果为
  unreachable / status 0，handler 只收到三次 HTTPS 请求。

定向运行该类 31 通过；未跑 Api 全量回归。
