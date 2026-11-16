# §139 通用 HTTP 指标与 OTel metrics provider/config（C223 闭合）

日期：2026-11-08
参照：`internal/metrics/{config,metrics,provider}.go`、`internal/server/middleware/metrics.go`、
`internal/server/routes.go:64`、`conf/conf.go`、`cmd/axonhub/main.go`。

## 交付

- `src/AxonHub.Api/Metrics/MetricsOptions.cs`：`metrics.enabled` /
  `metrics.exporter.{type,endpoint,insecure}` 的配置绑定；`AXONHUB_METRICS_*` 优先于
  `Metrics:*`（Viper AutomaticEnv 语义）；布尔值按 spf13/cast 接受
  `1/t/T/TRUE/true/True`；启用时校验 exporter 类型，未知值抛出
  `unsupported metrics exporter type: "x"`。
- `src/AxonHub.Api/Metrics/ServerMetrics.cs`：在 `server.name` Meter（默认 `AxonHub`，
  `AXONHUB_SERVER_NAME` 优先）上注册九个同名、同类型、同单位、同描述的 instrument：
  `http_request_count` / `http_request_duration_seconds`、`graphql_request_count` /
  `graphql_request_duration_seconds`、`chat_request_count` / `chat_request_duration_seconds`、
  `chat_token_count`、`chat_success_count`、`chat_failure_count`；HTTP 标签
  `method` / `path` / `status_code`，GraphQL 标签 `operation`，chat 标签
  `channel_id` / `channel_name`。
- `src/AxonHub.Api/Metrics/MetricsProvider.cs`：OpenTelemetry .NET 1.20.1 的
  `Sdk.CreateMeterProviderBuilder()` 同时 `AddMeter(server.name)` 与 `AddMeter("axonhub")`；
  启用时挂 5 秒 `PeriodicExportingMetricReader`（导出超时 30 秒）与 stdout / OTLP gRPC /
  OTLP HTTP 导出器；关闭时构造无 reader provider。OTLP endpoint 支持 Go 风格 `host:port`，
  缺省 gRPC `localhost:4317` / HTTP `localhost:4318`，`insecure` 决定 http/https，
  已带 scheme 的输入按原样使用。
- `src/AxonHub.Api/Http/HttpMetricsMiddleware.cs`：挂在 `RequestTracingMiddleware` 之后；
  记录状态码与耗时；ASP.NET 路由模板转 gin FullPath（`{p}` → `:p`，`{*p}` / `{**p}` → `*p`），
  `StaticFallbackMetadata` 的 fallback 记空路径；无 try/finally，panic 不记录（与参照
  Recovery 位于 WithMetrics 外层一致）。
- `src/AxonHub.Api/Program.cs`：解析配置，单例注册 `ServerMetrics` 与 `MeterProvider`，
  `Build()` 后解析 provider 启动周期导出；Gemini 路由参数名改为 `{gemini-api-version}`
  以保持 metrics path 标签与参照逐字一致。
- 依赖：`Directory.Packages.props` / `AxonHub.Api.csproj` 增加 OpenTelemetry、
  OpenTelemetry.Exporter.Console、OpenTelemetry.Exporter.OpenTelemetryProtocol 1.20.1。

## 行为要点

- 参照默认 `metrics.enabled=false`：关闭时 instrument 存在但无 reader、不导出，端口一致。
- 参照 `internal/metrics` 没有任何测试文件；HTTP 指标的实际可观察面由端口 26 例守卫锁定。
- GraphQL / chat 记录函数在参照内没有调用点，端口保持同样表面（可调用但不接线）。
- 已接受子差异：stdout 输出格式、OTLP endpoint 的宽松处理、no-op provider 的实现差异。

## 测试

- `tests/AxonHub.Api.Tests/ServerMetricsTests.cs`：3 例——instrument 表面、HTTP 标签、
  GraphQL/chat 标签与成功/失败分支。
- `tests/AxonHub.Api.Tests/MetricsOptionsTests.cs`：14 例——默认值、环境变量、优先级、
  布尔语义、三种导出器、未知类型文案、endpoint 映射（6 组 theory）。
- `tests/AxonHub.Api.Tests/HttpMetricsMiddlewareTests.cs`：9 例——gin path 转换（6 组
  theory）、fallback 空 path、panic 不记录、状态码/耗时标签。

静态计数：`AxonHub.Api.Tests` 5376 → 5402（+26）；四项目合计 8855 → 8881。
**按项目规则未运行 lint / build / test。**

台账：C223 由 gap 改为 resolved（223 条：160 accepted / 53 resolved / 10 gap）。
