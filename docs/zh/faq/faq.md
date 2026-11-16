# 常见问题

## AxonHub.NET 是官方 AxonHub 吗？

不是。它是社区维护的 .NET 10 + Orleans 行为等价移植，不代表上游团队。
上游项目是 [looplj/axonhub](https://github.com/looplj/axonhub)。

## 为什么不直接用 Go 版本？

如果你只需要部署，Go 版本是官方实现，通常是首选。这个仓库面向需要 .NET
技术栈、Orleans actor 模型、EF Core/Dapper 分层或希望参与移植验证的场景。

## 为什么选用 Orleans？有哪些优势？

Orleans 在这里不是“为了用 actor 而用 actor”，而是用来承载参照实现中那些
**有状态、需要串行化、需要在多实例下保持单一权威**的协作点。

### 1. 单写者语义替代进程内锁和缓存

Go 参照大量依赖进程内的启用通道缓存、API Key 状态、限流状态和
`sync.Mutex/RWMutex`。这些状态一旦跨实例就会分裂：每个副本看到不同的
渠道快照、限流窗口或令牌状态。

端口把这类状态放进按 key 寻址的 grain：

- `EnabledChannelsGrain` + `ChannelPoolGrain`：启用通道快照、打分和候选排序；
- `ChannelGrain`：通道级状态与并发/限流协调；
- `ChannelTokenGrain`：OAuth 刷新去重与乐观并发写回；
- `PreviousChannelGrain`：trace/会话的通道粘性；
- 备份、GC、模型同步、通道探测、Provider Quota、视频清理等定时 grain。

同一个 grain key 的调用默认串行执行，actor 的单线程模型自然地给出
“同一时刻只有一个写者”，避免在每个服务里重复实现锁、缓存失效和
check-then-act 竞态。

### 2. 状态与调用位置解耦

grain 调用方只持有接口，不关心实例在哪个 silo。默认部署是 API 进程内的
单 silo，不需要额外服务；需要多 silo 时，选路、限流和令牌状态的寻址与放置
由 Orleans 负责，而不是由业务代码自己拼分布式锁。

> 默认部署是单 silo（localhost clustering + 内存 reminder）。设置
> `OrleansCluster:Enabled=true` 后切换为 PostgreSQL AdoNet membership
> 和 durable reminder，支持 Kubernetes 多副本；见
> [Kubernetes 多副本](../getting-started/kubernetes.md)。

### 3. 定时任务和生命周期统一

上游的 GC、自动备份、模型同步、通道探测、Provider Quota 和视频清理都有
“按周期运行、同一时刻只跑一份”的诉求。Orleans 的 grain 激活、timer/reminder
和去激活生命周期把这些任务收拢到一个模型里，避免在 ASP.NET 后台服务里
自己维护字典、锁和重启后的注册。

当前 reminder 是内存实现，进程重启后会重新注册；任务本身仍以数据库为准。

### 4. 可测试性和可观测性

`Microsoft.Orleans.TestingHost` 让 Api 测试可以在进程内启动一个测试集群，
不需要外部 Orleans 服务；grain 的失败、停用和重新激活也能在测试中复现。
grain 调用、放置和异常有统一的运行时日志，便于定位状态问题。

### 5. 与 .NET 生态一致

Orleans 的序列化、依赖注入、`TimeProvider`、日志和 Microsoft.Extensions
宿主与 ASP.NET Core 一致；不需要自己实现一套 actor 运行时，也能继续使用
EF Core/Dapper、HotChocolate 和标准 .NET 诊断工具。

### 这不是没有代价

- **热点 grain 是串行瓶颈**：例如通道池快照这类高频 grain 不能承载过重的
  同步工作，端口只把选路协调放在里面，请求数据处理仍在 pipeline 中完成。
- **跨 grain 调用有序列化和调度成本**，所以 request/response body、流式事件
  等高频数据不进出 grain。
- **分布式一致性不是免费的**：多 silo 需要 clustering、reminder 存储和
  故障转移策略。端口提供 `OrleansCluster:Enabled` 开关和 PostgreSQL
  AdoNet 实现，但热点 grain、网络分区和数据库仍是运维面；默认单 silo
  没有这些问题，也没有横向扩展能力。
- **调试方式不同**：grain 的激活/停用、消息排队和重试需要看 Orleans 日志，
  不能只按普通方法调用理解。

结论：选 Orleans 是为了把参照里的“有状态协调点”变成可寻址、可串行化、
可测试的 actor，而不是宣称它比 Go 实现更快。对纯无状态、高吞吐的请求
转换路径，端口仍然使用普通的 .NET 对象和 pipeline。

相关阅读：[架构](../development/architecture.md)、
[移植台账](../development/porting.md)。

## 支持哪些数据库？

仅 PostgreSQL。参照实现支持的 SQLite/MySQL 尚未移植，`db.dialect` 设为其他
值会在启动时失败。持久化测试使用 PostgreSQL 17 Testcontainers。

## 可以直接接管已有的 Go 版 PostgreSQL 数据库吗？

表名、列名和索引按参照 schema 对齐，但跨实现接管生产库前必须先备份并在
隔离环境验证。SQLite 文件不能直接迁移。端口不会自动做版本化数据迁移。

## 为什么有 `config.yml` 又有 `appsettings.json`？

`config.yml` 是参照格式，端口通过 YAML 配置源兼容读取；`appsettings.json`
和 `AXONHUB_*` 环境变量是 .NET 原生配置方式。优先级为命令行 > 环境变量 >
appsettings > `config.yml`。详见[配置](../getting-started/configuration.md)。

## 端口是多少：8080 还是 8090？

- Docker Compose 默认 `127.0.0.1:8090`；
- 单镜像 `docker run -p 8080:8080` 使用 8080；
- 本地 `dotnet run` 按 `ASPNETCORE_HTTP_PORTS` 或 `config.yml` 的
  `server.port`。

以实际启动日志中的 `Now listening on` 为准。

## Docker 镜像里有管理界面吗？

有。镜像构建阶段会执行 `pnpm build` 并把 `frontend/dist` 复制到
`/app/frontend/dist`，无需运行时挂载。替换界面可覆盖
`Server:Static:Root`。

## 为什么真实 provider E2E 默认跳过？

这类测试需要独立的 LlmTestServer。默认没有设置
`AXONHUB_E2E_LLM_TEST_SERVER_URL` 时会自动跳过；配置 URL 和测试 Key 后，
可按[测试指南](../development/testing.md)中的命令运行。

## 支持哪些上游？

包括 OpenAI、Anthropic、Gemini/Vertex、Bedrock/GCP 上的 Anthropic、
DeepSeek、Moonshot、Zhipu/ZAI、MiniMax、Doubao/Volcengine、Bailian、
ModelScope、OpenRouter、Cerebras、Groq、Fireworks、xAI、OpenCode、Cline、
NanoGPT、ZenMux、Codex、Claude Code、GitHub Copilot、Antigravity、
Command Code、Jina、TypeSafe 等。完整分派见
`src/AxonHub.Api/Orchestration/ChannelOutboundFactory.cs`。

## 上游报错时会自动切换到其他渠道吗？

瞬时错误（例如可重试的 429）可在配置的重试预算内尝试其他候选渠道；重试次数
不会超过配置上限。认证/权限错误（HTTP 401/403 或 `invalid_api_key`、
`permission_denied`）、`insufficient_quota`、`insufficient_balance` 和
`content_filter` 属于终止错误，不会通过换渠道重试。所有候选渠道都失败时，
返回最后一次上游错误。

成功回退时，usage 和成本记录归属最终成功的渠道，而不是之前失败的渠道。

## 流式响应中途断开会切换渠道并接着输出吗？

不会在已经发送正文帧后把备用渠道的内容拼到同一个流里，以免组合出不一致的
回复。此时会报告流错误；如果上游没有发送正常终止标记，即使正文看起来已经完整，
也会作为不完整流处理。尚未开始输出时的错误仍按上游错误类型和重试预算处理。

## 与 Go 版行为完全一样吗？

目标是可观察行为等价，不是逐行翻译。已知差异编号为 `C###`，记录在
[差异台账](../../../porting/02-differences.md)；测试数字和验收记录在
[验收账](../../../porting/03-acceptance.md)。

## 为什么有些参照配置不生效？

`cache.*`（Redis）、`log.*` 文件轮转、`provider_quota.*`、
`server.safeguards` 运行时等尚未移植。端口会在配置文档和
`config.example.yml` 中明确标注；未列出的键不会报错但也不会生效。

## 遇到问题怎么排查？

1. 确认数据库连接和启动日志；
2. 用 `GET /health` 检查宿主；
3. 持久化问题确认 Docker daemon 和 5432 端口；
4. 对比差异台账中的已接受边界；
5. 提交问题时附上最小请求、响应、日志和是否有 `config.yml`。
