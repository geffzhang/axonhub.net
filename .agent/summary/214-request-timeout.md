# §214 请求超时面：配置、中间件、SSE 截止与 WS per-event 预算（C115 / C253 / C037）

## 背景

参照 `server.request_timeout` / `server.llm_request_timeout`（conf/conf.go:223-224）默认 30s / 600s，
由 `middleware.WithTimeout`（internal/server/middleware/timeout.go:10-18）实现：

```go
func WithTimeout(ts time.Duration) gin.HandlerFunc {
    return func(c *gin.Context) {
        ctx, cancel := context.WithTimeout(c.Request.Context(), ts)
        defer cancel()
        c.Request = c.Request.WithContext(ctx)
        c.Next()
    }
}
```

它只替换请求上下文，不自行写响应；截止后的可见行为由各 handler 决定。`routes.go` 按分组挂载：
public / unSecureAdmin / oauth 及 adminGroup 的 playground、request detail、admin GraphQL 用 30s；
playground chat、apiGroup、geminiGroup 与 codex HTTP 组把 600s 放在最前；Responses WebSocket 组不挂组
超时，per-event 预算在 `responses_websocket.go:267-283` 由每个 `response.create` 事件派生。SSE 截止分支
（chat.go:461-506）与固定 30s 写截止（chat.go:605-618）决定客户端最终看到什么。

差异台账中 C115 一直以“超时/来源/响应头中间件省略”为 accepted；本次核对确认 `WithSource`
（`RequestIdentity.Source` 默认 `"api"`、playground 为 `"playground"`）与 `WithResponseHeaders`
（`ChatCompletionEndpoint.RecordResponseHeaders`）早已等价，只剩超时面；C253 是同一缺口的 WS 表现。
C037 在核对时确认三个写校验器已随管理面切片落地，属台账陈旧。

## 参照行为

- **配置**：viper `AutomaticEnv` + 前缀 `AXONHUB` + `.`→`_`，环境变量为
  `AXONHUB_SERVER_REQUEST_TIMEOUT` / `AXONHUB_SERVER_LLM_REQUEST_TIMEOUT`；`time.ParseDuration` 解析，
  空字符串按 0；非正值使 `context.WithTimeout` 立即到期。
- **分组位置**：openapi 组的超时在 IPBlocklist / OpenAPIAuth 之后；admin GraphQL 在两条鉴权中间件之后；
  apiGroup / geminiGroup 的超时最先（在 IPBlocklist / APIKeyConfig 之前）；codex HTTP 组在 common
  七件套之后，WS 组无超时。
- **SSE 截止顺序**（chat.go:461-506）：terminalSeen 仅日志；`ctx.Err()==DeadlineExceeded` 且
  `streamErr` 为 nil / Canceled / DeadlineExceeded 时写 `ctx.Err()` 错误事件；`streamErr!=nil` 时若
  Canceled 或 `ctx.Err()==Canceled` 静默，否则写错误事件；`ctx.Err()==Canceled` 静默；最后
  `!terminalSeen` 写 `ErrStreamIncomplete`。写事件前刷新 30s 写截止且不绑定请求 ctx，因此 deadline 后
  仍能写出错误事件。
- **终态**：`ObserveContextError(DeadlineExceeded)` 无条件 Failed；Canceled 仅在 state=None 时设置；
  `terminal_stream_test.go` 期望请求/执行状态 failed。

## 端口实现

- 新增 `src/AxonHub.Api/Http/RequestTimeoutSettings.cs`：`FromConfiguration` 读
  `Server:RequestTimeout` / `Server:LlmRequestTimeout` 与两个 `AXONHUB_*` 别名；缺失取默认 30s / 600s，
  空白按 0。`GoDurationParser` 完整移植 Go `time.ParseDuration`（ns/us/µs/μs/ms/s/m/h、符号、小数、
  `0` 特例、溢出判定与逐字错误文案），供配置与测试复用。
- 新增 `src/AxonHub.Api/Http/RequestTimeoutMiddleware.cs`：`RequestTimeoutFeature` 用
  `ITimer` + 父 token 注册，状态 Active / TimedOut / ParentCanceled 三态互斥；`IsTimedOut` 只在定时器
  胜出时为真。中间件把 `context.RequestAborted` 换成 feature token，并在 finally 恢复原 token、移除
  feature、触发取消以释放计时器。
- `Program.cs`：`requestTimeouts` 启动时读取一次；三个短超时块与 API / codex / gemini / admin 各块按
  参照顺序注册。全部 `UseWhen` 位于全局 tracing / metrics 之后（gin 的全局中间件先于分组中间件）。
  `/v1/responses` 与 `/codex/responses` 的 GET 不挂组超时；`HandleResponsesWebSocketAsync` 把
  `LlmRequestTimeout` 传给端点。
- `RequestIdentity.RequestDeadlineExpired`：从 feature 暴露 deadline 探针；
  `RequestChainPersistenceMiddleware` 在取消时用 `ContextCancellationError()` 区分——deadline 合成
  `TimeoutException("context deadline exceeded")`（Go 的 `context.DeadlineExceeded`），父取消保持
  `OperationCanceledException`；前者走既有 `HasTimeoutCause` → Failed，后者保持 Canceled。
- `SseStreamWriter`：`SseWriteTimeout = 30s`，初始 flush 与每次写事件都新建独立的写截止 CTS；截止分支
  按参照五步顺序实现，并修正“token 已取消即静默”导致服务器 deadline 下真实 streamErr 被误吞的问题。
- `ResponsesWebSocketEndpoint`：`HandleAsync` 接受 `requestTimeout`；每个事件用 linked CTS（父为
  `_sessionCts`）包住 process 与结果写出，仅 `>0` 时 `CancelAfter`；超时只结束当前事件，连接继续服务，
  错误经 transformer，nil 时写参照的固定 500 信封。

## 差异

- C115 转 resolved：.NET 中取消与截止同为 `OperationCanceledException`，端口用
  `IRequestTimeoutFeature.IsTimedOut` 区分，并以合成的 `TimeoutException` 表示 Go 的
  `context.DeadlineExceeded`；错误格式化输出与 `StreamOutcome` 终态与参照一致。
- C253 转 resolved：WS per-event 预算已落地，超时不关闭连接。
- C037 转 resolved：`ChannelValidation` 的三个校验器已接线到 `ChannelLifecycleService.CreateAsync` /
  `UpdateAsync` 与 `ChannelOverrideTemplateService` 的创建/更新，台账陈旧。
- C027 补充两个超时键及默认值 / 别名说明。
- 台账 287 条（200 accepted / 0 gap / 87 resolved）。

## 测试

- 新增 `tests/AxonHub.Api.Tests/RequestTimeoutTests.cs`（31 例）：14 例 Go duration 解析、7 例错误文案、
  3 例设置（默认值 / 键优先级 / 空串）、中间件替换 token 与 deadline、父取消不是 deadline、非正超时
  立即到期（0 / -1）、identity 探针、deadline → Failed / 取消 → Canceled。测试内用 `ManualTimeProvider`
  手工触发定时器。
- `SseStreamWriterTests` 新增 2 例：deadline 后写出 `context deadline exceeded`；deadline 后真实
  streamErr 仍报告 `upstream_stream_interrupted`。
- `ResponsesWebSocketEndpointTests` 新增 1 例：200ms 预算下慢事件返回固定 500 信封，同连接的后续事件
  正常完成。
- 定向运行 RequestTimeoutTests + SseStreamWriterTests + ResponsesWebSocketEndpointTests：54 通过，
  0 失败 / 0 跳过；未跑 Api 全量回归。

## 后续

- C027 仍保留 `disable_ssl_verify` 等未建模键；未来配置里程碑统一处理。
- 全量 Api 回归仍待择机执行。
