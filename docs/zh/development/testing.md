# 测试

测试按项目分层，既包含与参照逐条对照的契约测试，也包含真实 HTTP/数据库
链路。默认测试不依赖外部 AI 服务。

## 测试项目

| 项目 | 覆盖 |
| --- | --- |
| `AxonHub.Llm.Tests` | transformer、pipeline、SSE、错误与超时 |
| `AxonHub.Grains.Tests` | 选路、限流、令牌、调度 |
| `AxonHub.Persistence.Tests` | EF/Dapper、schema、事务、并发 |
| `AxonHub.Api.Tests` | 端点、管理面、鉴权、编排、E2E |

测试数量随代码变化，请以测试运行输出为准。真实 provider E2E 为 opt-in；
默认没有启动 LlmTestServer 时相关用例会跳过。

## 常用命令

```bash
# 单个项目
dotnet test tests/AxonHub.Llm.Tests/AxonHub.Llm.Tests.csproj
dotnet test tests/AxonHub.Grains.Tests/AxonHub.Grains.Tests.csproj

# 需要 Docker（Testcontainers PostgreSQL）
dotnet test tests/AxonHub.Persistence.Tests/AxonHub.Persistence.Tests.csproj

# Api 全量，约 35–45 分钟
dotnet test tests/AxonHub.Api.Tests/AxonHub.Api.Tests.csproj

# 单个测试类
dotnet test tests/AxonHub.Api.Tests/AxonHub.Api.Tests.csproj \
  --filter FullyQualifiedName~ChatCompletionEndpointTests
```

持久化测试每个测试使用独立 schema，容器镜像为 `postgres:17-alpine`。

## 真实 provider E2E

矩阵覆盖 OpenAI Chat/Responses、Anthropic Messages、Gemini、Groq、Ollama、
DeepSeek、OpenRouter 上游，以及非流式/流式、Embeddings、Images、工具调用和
429 换渠道。韧性用例还验证重试预算及最后错误、认证/配额/内容过滤错误不回退、
deadline、流中断与终止标记、熔断、自动禁用、trace 粘性选路和最终 usage 归属。

```bash
docker run --rm -d --name llm-test-server \
  -p 8080:8080 \
  -e LlmTestServer__RequiredBearerToken=test-key \
  ghcr.io/managedcode/llm-test-server:1.0.6

AXONHUB_E2E_LLM_TEST_SERVER_URL=http://localhost:8080 \
AXONHUB_E2E_LLM_TEST_SERVER_KEY=test-key \
  dotnet test tests/AxonHub.Api.Tests/AxonHub.Api.Tests.csproj \
  --filter FullyQualifiedName~LlmTestServer
```

未设置 URL 时真实 provider E2E 自动跳过，常规 CI 不依赖外部进程。CI 的 `e2e-llm`
job 使用固定 digest 的同一镜像运行它们。

## 差分语料

`corpus/` 保存上游录制/构造的请求、响应和流式语料，由
`tools/AxonHub.CorpusRunner` 回放：

```bash
dotnet run --project tools/AxonHub.CorpusRunner -- --mode unified-request
python -I porting/scripts/diff_corpus.py verify \
  --cmd "dotnet run --project tools/AxonHub.CorpusRunner -- --mode unified-request" \
  --corpus corpus/unified-request
```

## 移植守护

```bash
python -I porting/scripts/diff_register.py lint
python -I porting/scripts/diff_register.py render
python -I porting/scripts/http_route_audit.py --port src/AxonHub.Api
python -I porting/scripts/reference_lock.py check \
  --repo ../axonhub-ref --dest porting/reference-lock.json
```

CI 中 `porting-guard.yml` 会运行差异台账 lint/render、HTTP 路由审计、四类
差分语料和参照物锁定。

## 失败排查

- 先确认失败是本次切片引入，还是参照/环境既存问题；
- 持久化失败检查 Docker daemon 和 5432 端口占用；
- 并发/时序测试在慢机器上可能暴露竞态，优先改成同步/取消令牌等待；
- 涉及时间、随机数、文件系统时使用可注入的 `TimeProvider` 和测试接缝；
- 不要把 `bin/`、`obj/`、`Properties/` 或 `_ledger_*` 转储提交到仓库。
