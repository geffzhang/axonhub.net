# §226 真实 provider E2E：LlmTestServer

## 背景

用户指定：真实 provider 的端到端验证统一使用本地 `E:\GitHub\LlmTestServer`，
不依赖外部云 provider。此前“真实宿主”冒烟只覆盖了上游拒绝连接的失败路径，
成功路径一直由 `ScriptedExecutor` 模拟。

## 实现

新增 `tests/AxonHub.Api.Tests/LlmTestServerE2ETests.cs`：

- 入站请求 → `ChatCompletionOrchestrator` → 出站变换器 → 真实
  `HttpExecutor`（`SocketsHttpHandler`，`AllowAutoRedirect=false`，无总超时）
  → LlmTestServer → 入站变换 → SSE/JSON 写出，全程无脚本化 transport。
- 通道由 `TestChannels.OpenAi` 构造，`baseUrl` 指向
  `{LlmTestServer}/openai/v1`，API key 指向 `test-key`。
- 每个用例先 `POST /admin-api/configure` 注册模型与单条响应，配置替换运行时
  状态，因此每个用例的响应队列都是新的。
- 覆盖：
  1. 非流式 chat：状态/Content-Type/`chat.completion` 信封/content 逐字。
  2. 流式 chat：SSE 以 `data:[DONE]\n\n` 结束，三个 provider chunk 的 delta
     拼接后与配置文本逐字相等。
  3. Anthropic 入站 → OpenAI 上游：返回 `type=message`、`role=assistant`、
     `content[0].text` 与 `stop_reason=end_turn`。
- 采用 `LlmTestServerFactAttribute` 在未设置
  `AXONHUB_E2E_LLM_TEST_SERVER_URL` 时静态跳过，避免常规 `dotnet test`
  依赖外部进程；`AXONHUB_E2E_LLM_TEST_SERVER_KEY` 可选，默认 `test-key`。

## 运行方式

```sh
# 1. 启动 LlmTestServer（仓库 E:\GitHub\LlmTestServer）
LlmTestServer__RequiredBearerToken=test-key dotnet run \
  --project samples/ManagedCode.LlmTestServer.Service --urls http://localhost:8080

# 2. 定向运行
AXONHUB_E2E_LLM_TEST_SERVER_URL=http://localhost:8080 \
  dotnet test tests/AxonHub.Api.Tests/AxonHub.Api.Tests.csproj \
  --filter "FullyQualifiedName~LlmTestServerE2ETests"
```

## 验证

- 设置 env：3/3 通过（485 ms）。
- 不设置 env：3/3 跳过，0 失败。
- 未跑 Api 全量。

## 边界

- 该用例验证真实出站 HTTP 与 provider wire 行为，但不启动 Kestrel、
  PostgreSQL 与 Orleans silo；宿主级链路仍由既有“真实宿主”与容器冒烟记录覆盖。
- 测试不会自动拉起 LlmTestServer；服务生命周期由开发者控制。
- 未新增台账条目（这是验证手段，不是行为差异）。
