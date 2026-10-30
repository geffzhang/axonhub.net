# 行为基线账（第一本账）

把"参照实现的行为"变成可执行证据。四类证据缺一不可。

参照物：`E:\GitHub\axonhub-ref`（冻结工作树），基线提交 `b8bb3421e1fe946cfc4f0d86e8412098738f2793`（v1.0.0-beta11）。

## 1. 测试移植

| 上游测试 | 本实现测试 | 状态 | 说明 |
|---|---|---|---|
| `llm/pipeline/pipeline_retry_test.go`（4 个子测试 + 11 个顶层测试 = 15 例） | `tests/AxonHub.Llm.Tests/PipelineRetryTests.cs`（15 个 `[Fact]`） | 15/15 已移植 | 逐条对照：同通道重试、跨通道切换、混合重试、预算耗尽、原始流意图保持、元数据不泄漏、提交前错误重试、内容提交后不重试、预提交缓冲上限（事件数/字节数）、大内容首事件放行、超大 SSE 事件绕过重试。`preReadLlmStream` 通过 `InternalsVisibleTo` 直接驱动，等价于 Go 的同包测试。 |
| `llm/pipeline/empty_response_test.go`（23 个可执行用例） | `tests/AxonHub.Llm.Tests/EmptyResponseTests.cs`（9 个 `[Fact]`） | 9/23 已移植，14 例顺延 | 已移植：`TestHasResponseContent` 的 4 个 chat 子测试 + 独立签名用例、流式空响应检测 2 例、非流式空响应检测 2 例。顺延的 14 例依赖 decisions / alpha-search / speech / transcription / embedding / moderation 等尚未进入统一模型的响应类型（见"覆盖缺口"），属于范围裁剪，非行为差异。 |
| `internal/server/orchestrator/lb_strategy_rr_test.go`（RoundRobinStrategy 路径）+ `load_balancer_test.go`（排序/健康/topK 子集） | `tests/AxonHub.Grains.Tests/ChannelSelectionTests.cs`（14 个 `[Fact]`） | 14 例已改写（子集） | 覆盖：模型过滤、零请求满分 150、请求数拉低分数、同分保持输入顺序（`WithoutWeightTieBreaker`）、健康通道降级与冷却边界、topK 排除不健康通道、模型别名解析、10 分钟窗口过期与迟到事件丢弃、不计数模式。顺延：WeightRoundRobinStrategy、rate-limit 策略、error-aware / additive scoring、真实数据库用例，均依赖尚未移植的策略链（C012）。 |
| `llm/transformer/openai/model.go` 的 `encoding/json` 解码语义（上游无对应单测） | `tests/AxonHub.Llm.Tests/OpenAiRequestTypeCheckTests.cs`（101 例） | 已建立（探针证据） | 用独立 Go 探针（`E:\tmp\typrobe`，直接编译参照模块）与 C# 探针对同一 106 条输入逐条比对；锁定错误文案 67 例、Go 接受 32 例、null 零值/tool_choice 回退 2 例。覆盖结构名与标签路径、整数/浮点字面量规则、大小写不敏感匹配、重复键、未知字段、硬错误覆盖软错误。残余差异见 C022。 |
| `internal/server/orchestrator/retry_test.go` 的 `TestIsRetryableErrorForChannel`（10 个断言） | `tests/AxonHub.Api.Tests/ChannelRetryPredicateTests.cs`（10 个用例 + 6 个解析/降级用例） | 10/10 已移植 | 逐条对照：nil、默认 5xx、包装的传输错误、配置 400 / 未配置 401、子串匹配、正则匹配、未匹配文本、空 settings、ResponseError 详情文本。另锁定 settings jsonb 解析、损坏 settings 降级、非法正则不匹配（Go 行为）。`Normalize*`（写入侧校验）未移植：本切片无通道写 API。 |
| `internal/server/middleware/header_test.go`、`auth_test.go` 的鉴权分支 | `tests/AxonHub.Api.Tests/{ApiKeyAuthenticationTests,ClientAddressTests,ApiKeyAuthMiddlewareTests}.cs`（73 例） | 已改写（子集） | `TestExtractAPIKeyFromRequest` 的默认配置分支逐条覆盖：7 个 header 优先级、四种前缀、裸 key、TrimSpace、空值/纯空白跳过（14 例）。`TestIsAnyAllowedIP` 9 例与 `TestIPMatchersMappedIPv4` 的 17 个 allowlist 用例逐条移植（含 `::ffff:` 映射地址、host bits、`/95` 宽前缀与 `::/0` 家族隔离）。`TestIPMatchersWithTrustedProxy` 改写为 `ClientAddress` 的受信/不受信转发用例：默认 `trusted_proxies=[]` 时忽略转发头，受信对端按 gin 的右起“首个不受信跳”规则取客户端地址。`auth_test.go` 的 magic-value 401 与无 header 语义由鉴权器/中间件用例覆盖。JWT/密码/缓存用例顺延（C014）；`ExtractAPIKeyFromHeader`（旧单 header 辅助函数）不在 `WithAPIKeyConfig` 路径，未移植。 |
| `internal/server/middleware/ip_blocklist_test.go`（`TestIsBlockedIP` 9 例、`TestClientIPCandidates*` 3 例）与 `internal/server/biz/system.go` 的 `SecuritySettingsOrDefault` / `normalizeSecuritySettings` 分支（上游无对应单测） | `tests/AxonHub.Api.Tests/IpBlocklistMiddlewareTests.cs`（14 例）+ `SystemSettingsServiceTests.cs`（15 例）+ `ClientAddressTests` 的前缀边界 6 例 | 已改写（9/9 + 3/3 已移植） | `TestIsBlockedIP` 的 9 个子测试逐条覆盖（精确 / CIDR / Trim / IPv6 / 多 candidate / 非法 candidate / 非法条目 / 不匹配）；`TestClientIPCandidates*` 的三种场景改写为中间件与 `ClientAddress` 用例（不受信对端忽略转发头、受信对端取右起首个不受信跳、单一 candidate 的等价去重）。系统设置无上游单测，按源码分支建立：缺行 / 非法 JSON / JSON null / 空对象 / 缺图标 / 大小写键 / 未知字段 / 重复键 / 存储失败 / 软删除全部回退默认，`blocked_ips` 的 Trim / 去空 / 精确去重 / 保序归一化，以及图标开关覆盖。另补 `netip.ParsePrefix` 的 1-3 位十进制、无前导零 / 符号 / 空白、拒绝 zone 的边界 6 例。`WithIPBlocklist` 的 403 响应体与「ClientIP 为空不读设置」由中间件用例锁定。 |
| `internal/server/biz/channel_metrics_test.go` 的 `TestPerformanceRecord_Methods` + `internal/server/orchestrator/outbound.go` 的 `markPerformanceTerminal` 分支 | `tests/AxonHub.Api.Tests/ChannelPerformanceRecordTests.cs`（7 例）+ `ChatCompletionOrchestratorTests` 流式语义 3 例 | 已建立 | 逐条对照 MarkSuccess / MarkFailed / MarkCanceled / IsValid；终态四态映射：completed/incomplete → 成功、canceled → 取消、failed → 失败（500 + 终态文案）、none → 无操作，首次终态后不再改写。端到端锁定：无终态传输错误既不记成功也不记失败；无终态但聚合完整记成功；终态 `[DONE]`/finish_reason 记成功。性能明细字段（首 token/TPS/状态码/错误文本/API key/完成 token）、`extractErrorMessageForMatching` 的 8 KiB 截断、异步 worker 与自动禁用未移植，均不参与当前计数（C017）。 |

统计：pipeline 编排层共 38 个可执行用例（15 + 23），已移植 24 个、顺延 14 个（均为非 chat 响应类型，待对应请求类型落地后补测）。

## 2. 差分语料

语料只由参照实现生成（`E:\GitHub\axonhub-corpus`，Go 程序读 stdin、写缩进 JSON），
本实现跑同一批输入并逐字节比对。

运行方式：

```
python -I C:\Users\geffz\.claude\skills\cross-lang-porting\scripts\diff_corpus.py verify \
  --cmd "dotnet run --project tools/AxonHub.CorpusRunner --no-build -- --mode <mode>" \
  --corpus corpus/<mode>
```

| 语料目录 | 守护的行为 | 用例数 | 结果 |
|---|---|---|---|
| `corpus/upstream-request` | 客户端 HTTP 请求 → 最终发往上游的 HTTP 请求（方法、URL、合并后的头、序列化后的 body 字节） | 7 | 7/7 通过 |
| `corpus/unified-request` | 客户端 HTTP 请求 → 统一 `llm.Request` JSON（字段顺序、omitempty、raw_request 结构、map 键序） | 7 | 7/7 通过 |
| `corpus/client-response` | 上游 HTTP 响应 → 返回给客户端的 HTTP 响应（状态码、头、body 字节；含上游错误与畸形 JSON） | 9 | 9/9 通过 |
| `corpus/client-stream` | 上游 SSE → 返回给客户端的 SSE 事件流（分帧、逐块换算、终态补全、流内错误） | 12 | 12/12 通过 |

覆盖的输入维度：纯文本、多模态（图/音/文件）、tools + tool_choice、结构化输出（response_format/json_schema）、
stop / stream_options、缺 model、错误 Content-Type、截断 JSON、根值非对象、根值为 null、空 body、HTTP 5xx。

另有一批不进入语料目录的畸形输入（38 条语法错误 / 根值类型错误 / 空串转义）作为快速差分回归使用，全部与参照实现逐字节一致。

字符串内的非法 UTF-8 与孤立代理项转义（C008）由 `tests/AxonHub.Llm.Tests/GoJsonUtf8Tests.cs`（6 例）
与 `GoJsonSyntaxTests.cs`（3 例）锁定：期望值取自参照 Go 解码器对同一字节序列的探针输出
（每字节一个 U+FFFD、合法代理对保留、字符串外仍报语法错误、高位字节的 `quoteChar` 文案）。

尚未覆盖：真实传输层错误、首个事件超时。超长 SSE 事件上限由端口测试锁定（`StreamEventTooLargeBypassesAllRetries`），不另入语料。

## 3. 线路形状测试

| 协议 / 接口 | 锁定的内容 | 测试位置 |
|---|---|---|
| OpenAI `POST /chat/completions` 出站 | 方法/URL 拼接（`NormalizeBaseURL` + `EndpointPath` + `##` 原生 URL）、头部集合与合并规则、`Authorization: Bearer`、默认 `User-Agent: axonhub/1.0`、body 字节 | `corpus/upstream-request` |
| 统一 `llm.Request` | 字段顺序（结构体声明序）、`omitempty`/`omitzero` 语义、`json.RawMessage` 重压缩、map 键序 | `corpus/unified-request` |
| OpenAI 非流式响应出站 | 上游 JSON → `llm.Response` → 客户端 JSON 的字段顺序与零值处理 | `corpus/client-response` |
| OpenAI 流式 | SSE 分帧规则、`[DONE]` 识别、空 `choices` 过滤、`include_usage` 终块保留、流内错误事件、缺 `[DONE]` 时的终态补全 | `corpus/client-stream` |

## 4. 示例入编译

| 示例 | 是否在构建内 | 覆盖的接缝 |
|---|---|---|
| `tools/AxonHub.CorpusRunner` | 是（`AxonHub.slnx` 的 `/tools/`） | 入站变换、出站变换、HTTP 头合并、URL 归一化 |

## 5. 持久化 schema 快照、EF 模型与仓储回环（50 例）

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 八张表（`channels` / `projects` / `users` / `api_keys` / `requests` / `request_executions` / `usage_logs` / `systems`）的表名、列名与列顺序 = 参照 `internal/ent/migrate/schema.go` | `tests/AxonHub.Persistence.Tests/SchemaTests.cs` | 1 |
| 列类型 = Ent PostgreSQL 映射（`bigint` / `timestamptz` / `jsonb` / `boolean` / `varchar` / `double precision`，`id` 为 identity） | 同上 | 1 |
| 23 个索引的名称、唯一性、列顺序 = 参照 Ent 定义（另校验 `<table>_pkey`；含 `projects_by_name`、`user_email_deleted_at`、`api_keys_by_user_id`、`api_keys_by_project_id`、`api_keys_by_key`、`systems_key_key`） | 同上 | 1 |
| 参照以 `migrate.WithForeignKeys(false)` 建表，库内无外键约束 | 同上 | 1 |
| 迁移幂等：单事务 DDL 重复执行，不新增版本表 | 同上 | 1 |
| EF Core 模型与迁移后的库结构一致：8 张表的全部列名 | `EfCoreModelTests.cs` | 1 |
| EF Core 模型的 23 个索引与库内索引一致：名称、唯一性、列顺序 | 同上 | 1 |
| EF 写侧与 Dapper 读侧共享连接/事务：事务内可见、回滚后不可见 | `EfDapperInteropTests.cs` | 1 |
| 仓储加入调用方事务：`ChannelStore` 写入后其 Dapper 读可见未提交行，回滚后另一 context 读不到 | 同上 | 1 |
| 连接语义：`search_path` 固定到测试 schema，Dapper 下划线列映射开启 | `PostgresConnectionFactoryTests.cs` | 2 |
| 基础表仓储（EF 写 / Dapper 读）：全列读写回环、状态更新、软删除过滤、`(name, deleted_at)` 唯一索引；request / execution 的 finalized 与 status-only 更新分离（不互相清除已捕获字段） | `ChannelStoreTests.cs` / `RequestStoreTests.cs` / `RequestExecutionStoreTests.cs` / `UsageLogStoreTests.cs` | 23 |
| 鉴权三表仓储：project 首行按 id 升序、owner 用户查询（无 ORDER BY）、`api_keys.key` 查找、软删除不可见、唯一键冲突映射为 `DbUpdateException` + `PostgresException(UniqueViolation)` | `ProjectStoreTests.cs` / `UserStoreTests.cs` / `ApiKeyStoreTests.cs` | 12 |
| 系统设置仓储：`systems` 全列读写回环、缺失 key、软删除行对读侧不可见、软删除后 `key` 仍不能复用（`systems_key_key` 只含 key） | `SystemStoreTests.cs` | 4 |
| Go 零值 / Ent 默认值：`request_body` 的 `null`、`project_id=1`、未提供的 token 明细落 0、`cost_items=[]` | `RequestStoreTests` / `UsageLogStoreTests` | 计入上项 |

持久化差异本身登记为 C011（Ent → EF Core 写 + Dapper 读 + PostgreSQL），schema 与运行时语义由上述测试锁定。
写侧 create/update 走 EF Core（`SaveChanges` 管理一致性，驱动错误包装为 `DbUpdateException`，内层为
`PostgresException`）；读侧 get/list 走 Dapper。仓储注入 scoped `AxonHubDbContext` 作为工作单元，
两侧共用该 context 的连接与当前事务（`OpenDapperConnectionAsync` / `CurrentDbTransaction`）。
测试由 Testcontainers 启动 `postgres:17-alpine`，每个测试在独立 schema 中运行并以 `DROP SCHEMA ... CASCADE` 清理；
jsonb 的文本格式由 PostgreSQL 归一化（键序、空白），回环断言按 JSON 语义比较。

## 6. 请求链路落库（5 例）

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 非流成功：request/execution 各创建一行（request 记录客户端模型，execution 记录路由实际模型与通道），通道回填、掩码后的请求头、完成态 external id / 上游模型 / 响应体 / 延迟指标、usage log（api_key_id=0、cost_items=[]） | `tests/AxonHub.Api.Tests/RequestChainPersistenceTests.cs` | 1 |
| 重试：每次尝试一行 execution；首次失败写 failed + 状态码 500 + 错误消息且不写指标（参照传 nil metrics），第二次 completed；usage 记到最终通道；请求通道先后回填 5201 → 5202 | 同上 | 1 |
| 最终失败：raw-error 钩子与 orchestrator 的 `persistRequestExecutionFailure` 各写一次 failed（503 / "boom" / 无指标），request 只改 status，不产生 usage / 响应归档 | 同上 | 1 |
| 流式成功：消费并 Dispose 后 execution / request 均 completed；Close 时聚合缓冲分块，回填 external id（`chatcmpl-9`）与完整响应体；上游模型取自首个上报模型的 chunk，首 token 延迟写入，usage 取自聚合 meta；`response_chunks` 不写入（默认 store_chunks=false，C020） | 同上 | 1 |
| 鉴权身份传播：`RequestIdentity(project 9, key 77)` 写入 request 的 `project_id` / `api_key_id`、execution 的 `project_id`、usage 的 `api_key_id` / `project_id`；未鉴权时 request.`api_key_id` 为 null、usage 为 0 | 同上 | 1 |

这组测试用真实 Orleans `TestCluster` + 脚本化 `IExecutor` + 内存版 `IRequestChainRecorder`，逐条对照
`request.go` / `request_execution.go` 的写入时机与字段集合；不依赖数据库（SQL 语义由第 5 节锁定）。

## 7. Orleans 通道选择（14 例）

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 模型过滤：只有 `supported_models` 解析出该请求模型的通道参与选择 | `ChannelSelectionTests.cs` | 1 |
| round-robin 打分：零请求 150 分，请求数按 `150/(1+count/150)` 拉低；20 次带计数选择在两个同权重通道间 10/10 均摊 | 同上 | 2 |
| 不计数模式（`trackSelection=false`）不写入选择数 | 同上 | 1 |
| 健康分区：连续失败 3 次且最近 5 分钟内失败的通道排到健康通道之后；topK=4 时被挤出候选 | 同上 | 2 |
| 冷却：超过 5 分钟恢复为健康，恰好 5 分钟即视为健康（`<` 边界） | 同上 | 2 |
| 成功清零连续失败，但窗口内已计入的失败数保留 | 同上 | 1 |
| 窗口：10 分钟内计数保留，超过即过期；窗口推进后迟到的旧事件被丢弃 | 同上 | 2 |
| `GetModelEntries` 别名：prefix / auto_trim / modelMappings 解析出正确的 ActualModel；hideOriginalModels + hideMappedModels；lowercaseModelId | 同上 | 3 |
| 同分保持 `ordering_weight DESC` 的输入顺序（无权重 tie-breaker） | 同上 | 1 |

测试用真实 `TestCluster`（Orleans 10.4.1）+ 假 `IEnabledChannelProvider` 驱动，不依赖 Docker 或数据库；
grain 键按请求模型/通道 ID 隔离，各用例使用独立 ID 避免状态串扰。差异登记为 C012（adaptive 策略链未移植、
指标为 grain 内存态、池每次选择重读快照）。

## 8. HTTP 纵向切片（42 例）

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 候选选择 → 出站变换：实际模型改写、通道 BaseUrl 拼接、`Authorization: Bearer <首个 key>`、客户端响应 body | `tests/AxonHub.Api.Tests/ChatCompletionOrchestratorTests.cs` | 10 |
| 重试语义：500 切通道、429 跳过同通道重试、同通道预算耗尽、策略关闭不重试、逐通道成功/失败计数、池初始选择计数 | 同上 | 计入上项 |
| 超时语义：非流式超时按取消处理（不记失败）且跳过同通道重试；首包超时记失败、跳过同通道重试并切换通道 | 同上 | 计入上项 |
| 无候选模型：422 `invalid_model_error` 信封，且未触达传输层 | 同上 | 计入上项 |
| 流式：SSE 事件透传到 `[DONE]`，终态后记成功 | 同上 | 计入上项 |
| SSE 写帧：gin-contrib/sse 兼容字节（`data:` 无空格、`\n`→`\ndata:`、`\r`→`\\r`）、响应头、终态后的错误抑制、classified 流错误事件（502 `upstream_stream_interrupted`）、缺终态的不完整流错误、客户端取消时的错误抑制 | `SseStreamWriterTests.cs` | 7 |
| 请求读取：头部规范化、URL 重建、`X-Forwarded-For` 优先、`Content-Type` 回退、gzip / deflate / zstd 解压并移除编码头、拒绝未知编码；gzip 头错误按 Go 文案、保留 FLG 位与 FNAME 跳过、raw DEFLATE 回退、大小写不敏感；zstd/raw DEFLATE 样本由参照 Go 程序产出 | `HttpRequestReaderTests.cs` | 12 |
| 端点胶水：空 body → 400 `JSONError`、无效模型 → 422、非流成功转发状态/内容类型/body、流式响应写 SSE 并带 CORS 头、非流与流式响应头在边界回填 request（C021）、消息内容中的非法 UTF-8 在转发前修复为 U+FFFD（C008） | `ChatCompletionEndpointTests.cs` | 7 |
| 期望流选择：`n` 与 `stream` 从含非法 UTF-8 的原始请求体读取（gjson 不校验 UTF-8） | `ExpectedStreamChoicesTests.cs` | 2 |

说明：参照的 `internal/server/orchestrator/*_test.go` 与 `internal/server/api/*_test.go` 依赖 Ent、真实数据库与 gin，
不能逐条直接移植；本切片用真实 Orleans `TestCluster` + 假通道快照 + 脚本化 `IExecutor` 复现同一调用链，
断言参照测试同样关心的可观察结果（上游请求、重试次数、客户端错误体、SSE 字节）。
这条链路的范围裁剪登记为 C014、C013–C021（多 key 粘性；限流/成本与通道级改写（API key 鉴权已接入，C023）；通道级重试配置（已解决，C016）、
性能记录简化（计数语义已解决，C017；性能明细与自动禁用属后续里程碑）、同步 recorder 工作单元、
流式归档与终态分类（已解决，C019）、response_chunks 与存储策略（C020）、
响应头捕获差异（C021）；gzip/deflate/zstd 请求体解码已接入（C015，已解决，残余错误文案差异见登记）；
字符串内非法 UTF-8 与孤立代理项转义已按 Go 规则修复（C008，已解决，残余 RawMessage 片段差异见登记））；
请求链路落库本身已由第 6 节的 5 例锁定。

## 8.1 系统设置与 IP 黑名单（29 例）

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 系统设置回退：缺行、`systems` 读取失败、JSON 语法/类型错误、JSON `null`、空对象均返回默认值 `{blocked_ips:[], show_request_log_ip_ban_icon:true}`；软删除行按缺行处理 | `SystemSettingsServiceTests.cs` | 9 |
| 系统设置解析：`blocked_ips` 的 Trim / 丢弃空串 / 精确去重 / 保持顺序，缺图标字段默认 true，键大小写不敏感，未知字段忽略，重复键后者胜出 | 同上 | 6 |
| IP 黑名单放行与跳过：空黑名单；ClientIP 为空时不读系统设置；非法条目；`+8` / `08` / 带空格位数；带 zone 前缀；不匹配 | `IpBlocklistMiddlewareTests.cs` | 6 |
| IP 黑名单命中：精确地址、CIDR、Trim 后条目、IPv6、IPv4 映射前缀；精确地址用例同时逐字节锁定 403 信封 `{"error":{"type":"Forbidden","message":"IP address is blocked"}}` 与 `application/json; charset=utf-8` | 同上 | 5 |
| ClientIP 与 gin 受信代理：默认 `trusted_proxies=[]` 时忽略 `X-Forwarded-For` / `X-Real-IP`；受信对端按右起首个不受信跳取客户端；被封的 TCP 对端优先于其自带转发头 | 同上 | 3 |
另有 `netip.ParsePrefix` 边界 6 例位于 `ClientAddressTests.cs`（计入第 1 节鉴权的 73 例）：位数只接受 1-3 位
十进制（`+8` / `08` / ` 8` / `8.0` 拒绝）、带 zone 的前缀拒绝、条目先 Trim 再解析。

默认安全设置与系统设置读取策略登记为 C028/C029。
黑名单中间件挂在 `/v1` 分支上，顺序为 `IpBlocklistMiddleware` → `ApiKeyAuthMiddleware`，与参照的
`WithIPBlocklist` → `WithAPIKeyConfig` 一致。

## 9. 真实宿主启动冒烟（2026-10-30）

拓扑：宿主进程 `dotnet AxonHub.Api.dll`（Kestrel 监听 `127.0.0.1:8091` + Orleans silo）直连容器
`postgres:17-alpine`（宿主端口 55432，库 `axonhub_smoke`）。

| 观察点 | 结果 |
|---|---|
| 启动 DDL | 幂等 DDL 建出当时切片的 `channels` / `requests` / `request_executions` / `usage_logs` 四表（C023 新增 `projects` / `users` / `api_keys`，C028 新增 `systems`，总数 8；本次冒烟未重跑） |
| 健康检查 | `GET /health` → `200 {"status":"ok"}` |
| 未知模型 | `POST /v1/chat/completions` → 422 `invalid_model_error`，未触达传输层 |
| 上游失败链路 | 插入指向 `http://127.0.0.1:1` 的启用通道后，3 次尝试（首次 + 同通道预算 2）→ 502 `upstream_error`（`upstream_stream_interrupted`，约 10.6s） |
| request 落库 | 1 条 `failed`：`channel_id` 已回填，请求体以 jsonb 原样保存 |
| execution 落库 | 3 条 `failed`（每次尝试一行）：`response_status_code=502`、有 `error_message`、无延迟指标；最后一条被 orchestrator 终态覆盖为带 `failed to do request:` 前缀的文案 |
| usage | 0 条（失败链路不写用量） |
| 掩码 | execution 请求头中的 `Authorization` 为 `******` |
| 日志 | 无未处理异常；冒烟后停止宿主进程并删除容器 |

容器内运行（应用镜像 + PostgreSQL 容器）随后验证：多阶段 `Dockerfile`（`sdk:10.0` → `aspnet:10.0`，监听 8080）
构建的镜像与 `postgres:17-alpine` 在同一容器网络启动，启动 DDL、`/health`、未知模型 422 与 502 失败链路、
请求/执行落库与掩码均与上述宿主进程冒烟一致（耗时约 3.1s，容器内拒绝连接无宿主端口映射的额外等待）；
详见 `03-acceptance.md`「端到端」。

## 覆盖缺口

| 未覆盖部分 | 原因 | 风险 | 计划 |
|---|---|---|---|
| pipeline 重试、空响应检测、SSE 事件上限 | 已由 24 个端口测试锁定 | — | 完成（M2） |
| 首包超时、非流式超时 | 实现已对等（`FirstEventTimeoutGuard` / `NonStreamTimeoutScope`），参照侧无独立单测可移植；端口在 orchestrator 层补了 2 例集成测试（超时 → 跳过同通道重试 → 切通道，取消不计失败、首包超时计失败） | — | 完成（M3） |
| 数据库接线的端到端启动 | 已用真实宿主冒烟与应用镜像冒烟（第 9 节）：Kestrel + Orleans silo + `postgres:17-alpine` 启动、幂等 DDL 建表、`/health` 200、422/502 失败链路与请求/执行落库均通过；多阶段 Dockerfile 构建的镜像在同一容器网络内复现同一结果 | 低 | 完成（M4） |
| 非 chat 响应类型的空响应探测（speech / transcription / embedding / moderation / decisions / alpha-search） | 统一模型尚未包含这些请求类型，参照侧对应 14 个用例顺延 | 中 | 随对应请求类型落地 |
| Anthropic 入站/出站、Responses API、Responses 流 | 首期纵向切片只做 OpenAI Chat | 中 | 后续阶段 |
| GraphQL / 前端 | Orleans 侧另建，不复用 Ent / GraphQL | 中 | 后续阶段 |
| 负载均衡：adaptive 策略链、WeightRoundRobin、rate-limit、error-aware / additive scoring | 首个切片只移植 round-robin 路径并由 14 例锁定；默认策略 `adaptive` 的排序尚未实现 | 高 | 补齐策略链后按参照用例逐条扩展（C012） |
| 持久化（channels / projects / users / api_keys / requests / request_executions / usage_logs / systems） | 已实现 EF Core 写 + Dapper 读 + PostgreSQL 层并由 50 例锁定；生产宿主用 `PostgresEnabledChannelProvider` 读取通道快照，请求/执行/用量经 `RequestChainPersistenceMiddleware` 落库并由 5 例锁定。流式归档（external id / body）、终态分类与响应头回填已按参照补齐（C019）；`response_chunks` 与存储策略未接入（C020），响应头捕获点差异已登记（C021） | 低 | 容器化验收；存储策略随对应功能落地 |
| API key 鉴权（/v1 中间件、header 提取、ClientIP/allowlist、no-auth 懒建）与 IP 黑名单 | 已按参照实现并由 73 例鉴权 + 35 例黑名单/系统设置锁定；管理面（GraphQL API key 创建）与 JWT/OIDC/角色未接入 | 中 | 管理面随 GraphQL/前端里程碑 |
| 其余 Ent 表（data_storages / traces / prompts / roles 等） | 请求链路四表、鉴权三表与 `systems` 已落；其余表未移植 | 中 | 随对应功能落地 |
