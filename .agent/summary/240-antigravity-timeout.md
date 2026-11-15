# §240 Antigravity 默认传输无总超时

## 背景

C119：`AntigravityExecutor` 的 nil 内部执行器使用 `new HttpClient()`，沿用
.NET 默认 100s 总超时；Go 的 `httpclient.NewHttpClient()` 构造
`&http.Client{}`，总超时为 0（无限等待）。

## 实现

- 默认传输改用 `HttpExecutor(HttpMessageHandler)` 重载：该重载构造
  `HttpClient` 时设置 `Timeout.InfiniteTimeSpan`。
- 显式传输保持不变：版本抓取 5s、token exchange 30s。
- 新增内部属性 `AntigravityExecutor.Inner` 与 `HttpExecutor.Client` 供测试断言。

## 测试

- `AntigravityExecutorTests.DefaultTransportHasNoTotalTimeout`：
  `HttpExecutor.Client.Timeout == Timeout.InfiniteTimeSpan`。
- Llm 定向 `AntigravityExecutorTests|HttpExecutorTests`：32/32。
- 未跑 Llm/Api 全量。

## 台账

- C119 → resolved。
- 308 条（193 accepted / 115 resolved / 0 gap）；`diff_register.py lint`
  通过；`02-differences.md` 已重新生成。

## 边界

- 重定向仍由 HttpExecutor 的 Go 循环处理；连接/握手阶段超时由
  SocketsHttpHandler 默认与上层 pipeline 负责，不在本条范围。
