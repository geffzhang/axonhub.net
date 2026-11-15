# §280 真实 provider E2E 矩阵扩展（LlmTestServer）

## 目标

把原有 3 条 `LlmTestServerE2ETests` 扩展为跨入站协议、上游协议、流式形态与
模态的真实 provider 矩阵。网关、orchestrator、transformer 与 `HttpExecutor`
全部走真实代码，只有 provider 由本地 `E:\GitHub\LlmTestServer` 提供确定性
响应。

## 运行方式

```bash
# 1. 启动本地 server
cd E:\GitHub\LlmTestServer
LlmTestServer__RequiredBearerToken=test-key \
  dotnet run --project samples/ManagedCode.LlmTestServer.Service \
  --urls http://localhost:8080

# 2. 运行 E2E
cd E:\GitHub\axonhub.net
AXONHUB_E2E_LLM_TEST_SERVER_URL=http://localhost:8080 \
AXONHUB_E2E_LLM_TEST_SERVER_KEY=test-key \
  dotnet test tests/AxonHub.Api.Tests/AxonHub.Api.Tests.csproj \
  --filter FullyQualifiedName~LlmTestServer
```

未设置 `AXONHUB_E2E_LLM_TEST_SERVER_URL` 时，全部 E2E 通过
`LlmTestServerFact` 跳过，常规测试与 CI 不依赖外部进程。

## 矩阵

新增 `LlmTestServerProviderMatrixTests`，15 例；加上原有 3 例共 18 例：

| 上游协议 | 入站协议 | 覆盖 |
| --- | --- | --- |
| OpenAI Chat | OpenAI | 非流式、流式 chunk 拼接与 `[DONE]`（原有） |
| OpenAI Chat | Anthropic | Anthropic message 信封（原有） |
| Anthropic Messages | Anthropic | 非流式 message；流式 `content_block_delta` + `message_stop` |
| Anthropic Messages | OpenAI | 跨协议：OpenAI 请求进、Anthropic 上游出、OpenAI 响应回 |
| Gemini generateContent | OpenAI | 非流式；流式 chunk 拼接与 `[DONE]` |
| OpenAI Responses | Responses | 非流式 `object=response` / `completed`；流式 `response.output_text.delta` + `response.completed` |
| OpenAI Embeddings | OpenAI | 固定 embedding 向量与 usage |
| OpenAI Images | OpenAI | `b64_json` 响应与 PNG 字节 |
| OpenAI Chat | OpenAI | function tool call：声明、tool_calls 响应、arguments 解析 |
| Groq Chat | OpenAI | 非流式 |
| Ollama `/api/chat` | OpenAI | 非流式，原生 NDJSON/JSON 适配 |
| DeepSeek Chat | OpenAI | 非流式 |
| OpenRouter Chat | OpenAI | 非流式 |
| OpenAI Chat ×2 | OpenAI | 第一候选脚本化 429，第二候选成功，验证换通道重试 |

所有 chat 类用例同时断言确定性的 `usage.total_tokens > 0`；Anthropic /
Responses / Embeddings 断言各自 usage 字段。

## Fixture 配置

每个用例通过 `POST /admin-api/configure` 重置：

- `models`：chat / embedding / image 类型；
- `chatScenarios`：内容、`streamChunks`、`toolCalls`、脚本化 `error`；
- `defaultEmbeddingVector`；
- 默认 image data URI。

配置替换会清空上一用例的场景队列与请求证据，因此用例之间无队列串扰。

## 测试结果

- 启用本地 server：`LlmTestServer` 过滤 **18/18 通过**。
- 发布镜像 `ghcr.io/managedcode/llm-test-server:1.0.6`（digest
  `sha256:ca9d1…2d5b`）本地容器复跑：**18/18 通过**。
- 未启用：18/18 跳过，full Api 默认回归不受影响。
- Api 全量（默认跳过 E2E）：**7182 例（7164 通过 + 18 跳过，0 失败，
  35m29s）**。

## CI

`.github/workflows/tests.yml` 新增 `e2e-llm` job：

- 使用 GHCR 1.0.6 镜像（tag + 多架构 digest 双重固定）；
- 注入 `LlmTestServer__RequiredBearerToken=test-key`；
- runner 侧轮询 `/admin-api/models` 直到就绪；
- 设置 `AXONHUB_E2E_LLM_TEST_SERVER_URL` 后只跑
  `FullyQualifiedName~LlmTestServer`，不拖慢全量 Api job；
- PR、push、nightly、workflow_dispatch 都会运行。

远端首次运行：`LlmTestServer E2E` job 通过；同工作流的
`AxonHub.Llm.Tests` 出现一个既有的 30ms/150ms 计时竞态
（`NonStreamTimeoutUsesTheChannelOverride`）。已把该用例改为等待
pipeline 取消令牌（而非固定延迟），本地 Llm 全量 3437/3437。

## 边界

- 不覆盖 audio/video、Responses 原生工具、真实公网 provider、限流/取消等
  流投递故障；这些由契约测试和脚本化 executor 覆盖。
- 不启动 Kestrel/PostgreSQL/Orleans 宿主；宿主级链路仍由「真实宿主」与容器
  冒烟覆盖。
- LlmTestServer 的 fixture 是确定性模拟，不等价于真实 provider 的模型行为，
  只验证线路协议与转换链。
