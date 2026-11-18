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

## Safeguards 预算回归

`SafeguardsFallbackTests.FallbackSharesTheOverallBudget` 使用可控 `TimeProvider`，
验证总预算 40ms：在 39ms 时回退 token 仍有效，在 40ms 时必须取消；主模型分别
消耗 0ms 和 20ms，以确认切换模型不重新开始总预算。另一个用例验证 10ms 单次
超时不会取消仍有 100ms 总预算的后续模型。

这些用例不把线程调度、JIT 或异步 continuation 的墙钟耗时当作 deadline。
`SafeguardsEvaluator.Create` 未传入时钟时仍使用 `TimeProvider.System`，保留
现有系统 timer、父子取消、纳秒换算与 timeout/error 归因；C267 的计时精度差异
仍然有效，不代表承诺响应延迟严格小于配置的毫秒数。

```powershell
dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj -c Release --filter FullyQualifiedName~Safeguards
```

## 真实 provider E2E

矩阵覆盖 OpenAI Chat/Responses、Anthropic Messages、Gemini、Groq、Ollama、
DeepSeek、OpenRouter 上游，以及非流式/流式、Embeddings、Images、工具调用和
429 换渠道。韧性用例还验证重试预算及最后错误、认证/配额/内容过滤错误不回退、
deadline、流中断与终止标记、熔断、自动禁用、trace 粘性选路和最终 usage 归属。

`AbortConnection` 的服务端 `emittedContentFrames` 只证明帧已写出，不保证网关在
连接重置前读到它。断流用例以网关记录的上游帧为依据：收到正文后必须保留前缀、
报告 `upstream_stream_interrupted`，且不得回退或补成功终止标记；未收到正文时
允许按预算回退，但不得拼接两个渠道的内容。首内容前、后两种路径另有确定性端点测试。

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

## 前端单测与浏览器 E2E

在 [`frontend`](../../../frontend) 目录运行：

```powershell
pnpm test:unit
pnpm build
pnpm lint
pnpm test:e2e --list --reporter=list
```

单测不需要启动 API；其中 playground transport 与 .NET 的
`AiSdkDataStreamTransformerTests.TransformStreamEmitsTextProtocolSequence` 共用
[`text.stream.jsonl`](../../../tests/AxonHub.Llm.Tests/testdata/aisdk/text.stream.jsonl)，
由后端测试校验真实 transformer 输出（仅归一化随机 text ID），不依赖上游 Go
仓库目录。渠道源码解析测试归一化 CRLF/LF，不改变模型列表或配额断言。

浏览器测试前，先启动使用**独立测试数据库**的 .NET API；测试入口不会自动创建
数据库或启动后端，也不再依赖不存在的 shell wrapper。在另一个终端启动 API，
并在运行 Playwright 的终端设置 `AXONHUB_API_URL`。默认 API 地址为
`http://localhost:8099`，Vite 页面为 `http://localhost:9527`；可用
`ASPNETCORE_URLS` 设置 API 监听地址。

```powershell
# 在 frontend 目录；API 必须已经启动并连接隔离测试数据库。
$env:AXONHUB_API_URL = 'http://localhost:8099'
pnpm test:e2e:setup
pnpm test:e2e
```

Linux/macOS 使用 `AXONHUB_API_URL=http://localhost:8099 pnpm test:e2e` 设置变量。
Playwright 自动启动并管理 Vite；本地允许复用已有的 9527 服务，执行前必须确认
该服务连接的是本轮测试 API，而非共享环境。

| 入口 | 作用 |
| --- | --- |
| `pnpm test:e2e` | 运行 setup，再运行依赖它的 Chromium 业务测试 |
| `pnpm test:e2e:setup` | 只运行系统初始化/管理员登录 |
| `pnpm test:e2e:headed` | 显示浏览器运行测试 |
| `pnpm test:e2e:ui` | 打开 Playwright UI |
| `pnpm test:e2e:debug` | 使用 Playwright Inspector 调试 |
| `pnpm test:e2e:report` | 查看已有 HTML 报告 |

测试凭据通过 `AXONHUB_ADMIN_EMAIL` / `AXONHUB_ADMIN_PASSWORD` 传入；未设置时
配置中的固定示例值仅供隔离本地测试。setup 会初始化未初始化的系统，并使用这些
凭据登录已初始化系统；业务测试会创建和修改数据，不得指向生产或共享数据库。
报告、截图、trace 和视频也应按测试数据处理，不提交真实令牌或敏感附件。测试后
仅停止本轮创建的后端进程，并按该隔离环境的生命周期清理数据。

发现测试不等于业务通过；`--list` 不启动浏览器或后端。若构建、单测或 lint
存在存量失败，应分别记录，不通过跳过测试或关闭规则消除。

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
