# 行为基线账（第一本账）

把"参照实现的行为"变成可执行证据。四类证据缺一不可。

参照物：`E:\GitHub\axonhub-ref`（冻结工作树），基线提交 `b8bb3421e1fe946cfc4f0d86e8412098738f2793`（v1.0.0-beta11）。

## 1. 测试移植

| 上游测试 | 本实现测试 | 状态 | 说明 |
|---|---|---|---|
| `llm/pipeline/pipeline_retry_test.go`（4 个子测试 + 11 个顶层测试 = 15 例） | `tests/AxonHub.Llm.Tests/PipelineRetryTests.cs`（15 个 `[Fact]`） | 15/15 已移植 | 逐条对照：同通道重试、跨通道切换、混合重试、预算耗尽、原始流意图保持、元数据不泄漏、提交前错误重试、内容提交后不重试、预提交缓冲上限（事件数/字节数）、大内容首事件放行、超大 SSE 事件绕过重试。`preReadLlmStream` 通过 `InternalsVisibleTo` 直接驱动，等价于 Go 的同包测试。 |
| `llm/pipeline/empty_response_test.go`（23 个可执行用例） | `tests/AxonHub.Llm.Tests/EmptyResponseTests.cs`（14 个 `[Fact]`） | 14/23 已移植，9 例顺延 | 已移植：`TestHasResponseContent` 的 4 个 chat 子测试 + 独立签名用例、流式空响应检测 2 例、非流式空响应检测 2 例、音频空响应探测 5 例（speech 音频、transcription 文本/原始体、speech 流事件 audio_base64、speech 二进制块、transcription 流事件，见第 19 节）。顺延的 9 例依赖 rerank / video 等尚未进入统一模型的响应类型（见"覆盖缺口"），属于范围裁剪，非行为差异。 |
| `internal/server/orchestrator/lb_strategy_rr_test.go`（RoundRobinStrategy 路径）+ `load_balancer_test.go`（排序/健康/topK 子集） | `tests/AxonHub.Grains.Tests/ChannelSelectionTests.cs`（16 个 `[Fact]`） | 16 例已改写（子集） | 覆盖：模型过滤、零请求满分 150、请求数拉低分数、同分保持输入顺序（`WithoutWeightTieBreaker`）、健康通道降级与冷却边界、topK 排除不健康通道、`calculateTopK` 的 1/2/3 截断与非正/超大值归一为全部候选、模型别名解析、10 分钟窗口过期与迟到事件丢弃、不计数模式。C012 已补齐 WeightRoundRobin / error-aware / additive 等策略链（本文件另有 8 例，共 24 例），rate-limit 策略由 §7.1 的 18 例 `RateLimitAwareScorerTests` 单独覆盖；真实数据库用例仍顺延（C014）。 |
| `llm/transformer/openai/model.go` 的 `encoding/json` 解码语义（上游无对应单测） | `tests/AxonHub.Llm.Tests/OpenAiRequestTypeCheckTests.cs`（101 例） | 已建立（探针证据） | 用独立 Go 探针（`E:\tmp\typrobe`，直接编译参照模块）与 C# 探针对同一 106 条输入逐条比对；锁定错误文案 67 例、Go 接受 32 例、null 零值/tool_choice 回退 2 例。覆盖结构名与标签路径、整数/浮点字面量规则、大小写不敏感匹配、重复键、未知字段、硬错误覆盖软错误。残余差异见 C022。 |
| `internal/server/orchestrator/retry_test.go` 的 `TestIsRetryableErrorForChannel`（10 个断言） | `tests/AxonHub.Api.Tests/ChannelRetryPredicateTests.cs`（10 个用例 + 6 个解析/降级用例） | 10/10 已移植 | 逐条对照：nil、默认 5xx、包装的传输错误、配置 400 / 未配置 401、子串匹配、正则匹配、未匹配文本、空 settings、ResponseError 详情文本。另锁定 settings jsonb 解析、损坏 settings 降级、非法正则不匹配（Go 行为）。`Normalize*`（写入侧校验）未移植：本切片无通道写 API。 |
| `internal/server/middleware/header_test.go`、`auth_test.go` 的鉴权分支 | `tests/AxonHub.Api.Tests/{ApiKeyAuthenticationTests,ClientAddressTests,ApiKeyAuthMiddlewareTests}.cs`（73 例） | 已改写（子集） | `TestExtractAPIKeyFromRequest` 的默认配置分支逐条覆盖：7 个 header 优先级、四种前缀、裸 key、TrimSpace、空值/纯空白跳过（14 例）。`TestIsAnyAllowedIP` 9 例与 `TestIPMatchersMappedIPv4` 的 17 个 allowlist 用例逐条移植（含 `::ffff:` 映射地址、host bits、`/95` 宽前缀与 `::/0` 家族隔离）。`TestIPMatchersWithTrustedProxy` 改写为 `ClientAddress` 的受信/不受信转发用例：默认 `trusted_proxies=[]` 时忽略转发头，受信对端按 gin 的右起“首个不受信跳”规则取客户端地址。`auth_test.go` 的 magic-value 401 与无 header 语义由鉴权器/中间件用例覆盖。JWT/密码/缓存用例顺延（C014）；`ExtractAPIKeyFromHeader`（旧单 header 辅助函数）不在 `WithAPIKeyConfig` 路径，未移植。 |
| `internal/server/middleware/ip_blocklist_test.go`（`TestIsBlockedIP` 9 例、`TestClientIPCandidates*` 3 例）与 `internal/server/biz/system.go` 的 `SecuritySettingsOrDefault` / `normalizeSecuritySettings` 分支（上游无对应单测） | `tests/AxonHub.Api.Tests/IpBlocklistMiddlewareTests.cs`（14 例）+ `SystemSettingsServiceTests.cs`（15 例）+ `ClientAddressTests` 的前缀边界 6 例 | 已改写（9/9 + 3/3 已移植） | `TestIsBlockedIP` 的 9 个子测试逐条覆盖（精确 / CIDR / Trim / IPv6 / 多 candidate / 非法 candidate / 非法条目 / 不匹配）；`TestClientIPCandidates*` 的三种场景改写为中间件与 `ClientAddress` 用例（不受信对端忽略转发头、受信对端取右起首个不受信跳、单一 candidate 的等价去重）。系统设置无上游单测，按源码分支建立：缺行 / 非法 JSON / JSON null / 空对象 / 缺图标 / 大小写键 / 未知字段 / 重复键 / 存储失败 / 软删除全部回退默认，`blocked_ips` 的 Trim / 去空 / 精确去重 / 保序归一化，以及图标开关覆盖。另补 `netip.ParsePrefix` 的 1-3 位十进制、无前导零 / 符号 / 空白、拒绝 zone 的边界 6 例。`WithIPBlocklist` 的 403 响应体与「ClientIP 为空不读设置」由中间件用例锁定。 |
| `internal/server/biz/system_test.go` 的 `TestNormalizeRetryPolicy_LoadBalancerStrategy` / `TestNormalizeRetryPolicy_MigratesLegacyStatuses`、`system_retry_policy_test.go`，以及 `system.go:1042-1172` 的读取分支（上游单测仅覆盖归一化） | `tests/AxonHub.Api.Tests/SystemRetryPolicyServiceTests.cs`（49 例）+ `ChatCompletionOrchestratorTests` / `ChannelSelectionTests` 的 topK 3 例 | 已改写（子集） | 逐条对照：策略归一化 7 例（合法四种、`weighted`→failover、非法/空→adaptive）、legacy `statuses`→永久规则（含既有 `rules` 优先）、`custom` 空白消息→hidden；参照未覆盖的读取分支按源码建立：缺行/软删除/读库失败/非法文档回退 `defaultRetryPolicy`，`null` 与 `{}` 的零值策略，`null` 标量保持零值，超时夹取 `[0,600]`，其余字段不夹取，嵌套类型错误回退默认。参照的 API-key principal 回归用例（#2080）在端口无 Ent 隐私层对应物：读取路径不依赖身份，策略值由全字段用例锁定。 | 
| `internal/server/biz/channel_metrics_test.go` 的 `TestPerformanceRecord_Methods` + `internal/server/orchestrator/outbound.go` 的 `markPerformanceTerminal` 分支 | `tests/AxonHub.Api.Tests/ChannelPerformanceRecordTests.cs`（7 例）+ `ChatCompletionOrchestratorTests` 流式语义 3 例 | 已建立 | 逐条对照 MarkSuccess / MarkFailed / MarkCanceled / IsValid；终态四态映射：completed/incomplete → 成功、canceled → 取消、failed → 失败（500 + 终态文案）、none → 无操作，首次终态后不再改写。端到端锁定：无终态传输错误既不记成功也不记失败；无终态但聚合完整记成功；终态 `[DONE]`/finish_reason 记成功。性能明细字段（首 token/TPS/状态码/错误文本/API key/完成 token）、`extractErrorMessageForMatching` 的 8 KiB 截断、异步 worker 与自动禁用未移植，均不参与当前计数（C017）。 |
| `internal/server/orchestrator/override_test.go`（30 个顶层测试函数，含 `TestParseOverrideOperations`）+ `internal/server/biz/channel_override.go` 的解析/优先级分支 | `tests/AxonHub.Api.Tests/ChannelOverrideTests.cs`（87 例）+ `ChannelOverrideSettingsTests.cs`（47 例） | 134/134 已移植（C038 已闭合） | 逐条对照 30 个测试函数并补充分支用例：模板（`.Model` / `.RequestModel` / `.Metadata` 嵌套 JSON / `.RequestHeader` / `.PromptCacheKey` / user_id）、映射后的请求模型条件、header 模板与 JSON-like 值、legacy `overrideParameters` 的 set/delete/clear 与非法/空设置、AxonHubClear 5 例、新格式 set/delete/rename/copy/条件、legacy 兼容、`set_if_absent` 的现网与原始入站 body 判定、Issue 632、数组 append/prepend/insert/remove 与 splat、非 JSON/multipart 门槛；设置解析 47 例锁定新格式全字段、legacy map 的 GoPrintf 字符串化、新旧字段优先级、大小写不敏感与解码失败整体降级。参照的 `channel_override_template_test.go`（6 例）属通道管理面，未移植（C014）。 |
| `internal/server/orchestrator/{channel_limiter,channel_limiter_manager,channel_request_tracker,lb_strategy_rate_limit,rate_limit_tracking,rate_limit_admission,channel_queue_error,connection_tracking}_test.go`（91 个顶层测试函数） | `tests/AxonHub.Grains.Tests/{ChannelLimiterTests,ChannelLimiterManagerTests,ChannelRequestTrackerTests,RateLimitAwareScorerTests}.cs`（64 例）+ `tests/AxonHub.Api.Tests/{RateLimitAdmissionTests,RateLimitTrackingTests,ChannelQueueErrorTests,ChannelLimiterMiddlewareTests}.cs`（5 + 10 + 4 + 7 个函数 / 29 个用例） | 90/91 已移植 | 逐条对照：硬信号量容量、FIFO 让位、有界队列拒绝、队列超时、取消与无槽泄漏；manager 的配置归一化/相等/重建/Forget/Snapshot；tracker 的自然分钟窗口、RPM 原子扣减、TPM 累加、冷却只延长与惰性清理；RateLimitAware 的四个子分与硬耗尽；API 中间件的 RPM 准入、TPM/冷却追踪、队列错误信封与连接让位顺序。未移植/合并：`TestNoopRateLimitTracking`（端口无 nil tracker 分支，C045）；`NoChannel` 与 `NilChannel` 在端口合并为“无候选”状态；`channel_limiter_metrics_test.go` 的 3 例随指标子系统顺延（C044）。另补 1 例冷却只延长的边界（`CooldownOnlyEverExtends`），tracker 共计 24 例。 |
| `internal/server/biz/cost_calc_test.go`（13 个顶层测试函数） | `tests/AxonHub.Api.Tests/UsageCostCalculatorTests.cs`（26 例） | 13/13 已移植 | 逐条对照：缓存/写缓存 token 从 prompt 数量扣除、全缓存归零、schedule 的 daily time / 跨午夜 / weekdays / date range / priority / timezone / 未知时区回退 / 缺 `when` 恒匹配；另补 flat_fee、tiered 分段、volume 命中档、未知模式/缺失 payload、无 items 与未知 item code，以及 `usage_cost_test.go` 的 4 个写缓存变体用例（five_min / one_hour / 两者并存 / 无变体回退共享价）。 |
| `internal/server/biz/usage_cost_test.go`（9 个 Ent 集成用例：per-unit / tiered / volume / volume 首档 / 无价格 / 四个写缓存变体） | `tests/AxonHub.Api.Tests/{UsageCostServiceTests,UsageCostCalculatorTests,UsageCostInjectionTests}.cs` + `tests/AxonHub.Persistence.Tests/UsageLogStoreTests.cs` | 已改写（9/9 覆盖） | Ent 内嵌 SQLite 的整链路用例改写为分段锁定的行为测试：价格读取与未知模型/非法通道/损坏价格（Service 12 例）、金额与变体（Calculator 4 例）、响应注入与 usage 行落库（Injection 5 例）、5m/1h 列回环（UsageLogStoreTests）。参照断言的 `total_cost` / `cost_items` 形状 / reference id 与 5m/1h 列均由上述用例覆盖。 |
| `internal/server/biz/channel_apikey_test.go`（30 个顶层测试：20 个 key 选择/哈希 + 1 个 context provider + 7 个 DeleteDisabledAPIKeys + 2 个 cleanup）与 `request_channel_api_key_index_test.go`（4 个测试函数，含 6 个索引子用例） | `tests/AxonHub.Api.Tests/{ChannelApiKeyProviderTests,ChannelApiKeyAuditTests}.cs`（23 + 7 例） | 选择/审计可执行用例全部覆盖；`AddKey_Stable` 由 rendezvous 稳定性覆盖 | 逐条对照 20 个选择用例：单 key、无 trace 成员、全禁用回退、禁用 key 排除、同 trace 确定性（直接驱动 `RendezvousSelect`）、不同 trace、加 key ≤2/3 重映射、删 key 不影响未命中 trace、顺序无关、FNV-1a 向量；trace 分支由第 32 节接入并按同 trace 稳定、不同 trace 分散锁定。`AddKey_Stable` 依赖的 LRU 在端口不保留（通道重建会新建 provider 与空缓存），加 key 的稳定性由 rendezvous 性质覆盖。索引 4 个函数改写为 7 个端到端审计用例（多 key 位置、单 key 无索引、短 key 无后缀、禁用 key 仍计数、共享后缀按索引区分、OAuth 旧字段排除、每次重试各自审计）；7 个 DeleteDisabledAPIKeys 与 2 个 cleanup 属管理面，未移植（C014）。 |

| `llm/transformer/openai/responses/websocket_executor_test.go`（32 个顶层测试函数，其中 3 个表驱动） + `http_executor_test.go`（3 个） | `tests/AxonHub.Llm.Tests/ResponsesWebSocketExecutorTests.cs`（32 个方法 / 37 个用例）+ `ResponsesHttpTransportExecutorTests.cs`（3 例）+ `tests/AxonHub.Api.Tests/TransportRequestFinalizerTests.cs`（5 例） | 37/37 + 3/3 已移植，接缝另锁 5 例 | 逐条对照：执行器缓存、response.create 发送、终态停止、取消优先级（读阻塞/已关闭/不覆盖终态）、顶层 error、官方错误状态与 body、failed/cancelled/incomplete 聚合、会话复用与不池化、显式 previous_response_id 四种对账、payload 恢复、assistant 后缀重连、复用连接提前关闭、组织头分池、失败/取消逐出、仅新输入、输入超限、非追加重连、提前关闭逐出、trace 头不影响池键、后台清理与 Close、事件规范化、URL 映射、握手头观察。参照用 `httptest` + gorilla upgrader；端口用 `ResponsesWebSocketTestServer`（loopback TcpListener + 手写 101 握手）。编排层 `CustomizeExecutor` / `finalizeTransportRequest` 在参照中位于 responses 包的 outbound.go，端口位于 `PersistentOutboundTransformer`，故 5 个接缝用例放在 Api.Tests。 |

统计：pipeline 编排层共 38 个可执行用例（15 + 23），已移植 29 个、顺延 9 个（均为非 chat 响应类型，待对应请求类型落地后补测）。

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

## 5. 持久化 schema 快照、EF 模型与仓储回环（56 例）

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 九张表（`channels` / `projects` / `users` / `api_keys` / `channel_model_prices` / `requests` / `request_executions` / `usage_logs` / `systems`）的表名、列名与列顺序 = 参照 `internal/ent/migrate/schema.go` | `tests/AxonHub.Persistence.Tests/SchemaTests.cs` | 1 |
| 列类型 = Ent PostgreSQL 映射（`bigint` / `timestamptz` / `jsonb` / `boolean` / `varchar` / `double precision`，`id` 为 identity） | 同上 | 1 |
| 25 个索引的名称、唯一性、列顺序 = 参照 Ent 定义（另校验 `<table>_pkey`；含 `projects_by_name`、`user_email_deleted_at`、`api_keys_by_user_id`、`api_keys_by_project_id`、`api_keys_by_key`、`systems_key_key`、`channel_model_prices_by_channel_id_model_id`、`channel_model_prices_reference_id_key`） | 同上 | 1 |
| 参照以 `migrate.WithForeignKeys(false)` 建表，库内无外键约束 | 同上 | 1 |
| 迁移幂等：单事务 DDL 重复执行，不新增版本表 | 同上 | 1 |
| EF Core 模型与迁移后的库结构一致：9 张表的全部列名 | `EfCoreModelTests.cs` | 1 |
| EF Core 模型的 25 个索引与库内索引一致：名称、唯一性、列顺序 | 同上 | 1 |
| EF 写侧与 Dapper 读侧共享连接/事务：事务内可见、回滚后不可见 | `EfDapperInteropTests.cs` | 1 |
| 仓储加入调用方事务：`ChannelStore` 写入后其 Dapper 读可见未提交行，回滚后另一 context 读不到 | 同上 | 1 |
| 连接语义：`search_path` 固定到测试 schema，Dapper 下划线列映射开启 | `PostgresConnectionFactoryTests.cs` | 2 |
| 基础表仓储（EF 写 / Dapper 读）：全列读写回环、状态更新、软删除过滤、`(name, deleted_at)` 唯一索引；request / execution 的 finalized 与 status-only 更新分离（不互相清除已捕获字段） | `ChannelStoreTests.cs` / `RequestStoreTests.cs` / `RequestExecutionStoreTests.cs` / `UsageLogStoreTests.cs` | 23 |
| 鉴权三表仓储：project 首行按 id 升序、owner 用户查询（无 ORDER BY）、`api_keys.key` 查找、软删除不可见、唯一键冲突映射为 `DbUpdateException` + `PostgresException(UniqueViolation)` | `ProjectStoreTests.cs` / `UserStoreTests.cs` / `ApiKeyStoreTests.cs` | 12 |
| 系统设置仓储：`systems` 全列读写回环、缺失 key、软删除行对读侧不可见、软删除后 `key` 仍不能复用（`systems_key_key` 只含 key） | `SystemStoreTests.cs` | 4 |
| 通道价格仓储：全列读写回环、软删除过滤、仅启用且未删除通道的价格参与读取、`reference_id` 全局唯一、同通道 `(model_id, deleted_at)` 唯一，以及同步价格接缝（`PostgresChannelPricingSource`）读价格与系统值 | `ChannelModelPriceStoreTests.cs` | 6 |
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

## 7. Orleans 通道选择（24 例）

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 模型过滤：只有 `supported_models` 解析出该请求模型的通道参与选择；round-robin 零请求总分 250（`RoundRobin(0)=150` + 中性 `RateLimitAware=100`） | `ChannelSelectionTests.cs` | 1 |
| round-robin 打分：请求数按 `150/(1+count/150)` 拉低；20 次带计数选择在两个同权重通道间 10/10 均摊 | 同上 | 1 |
| 不计数模式（`trackSelection=false`）不写入选择数 | 同上 | 1 |
| 健康分区：连续失败 3 次且最近 5 分钟内失败的通道排到健康通道之后；topK=4 时被挤出候选 | 同上 | 2 |
| `calculateTopK`：topK=1/2/3 截断到策略预算；topK<=0 或 >= 候选数时保留全部 | 同上 | 2 |
| 冷却：超过 5 分钟恢复为健康，恰好 5 分钟即视为健康（`<` 边界） | 同上 | 2 |
| 成功清零连续失败，但窗口内已计入的失败数保留 | 同上 | 1 |
| 窗口：10 分钟内计数保留，超过即过期；窗口推进后迟到的旧事件被丢弃 | 同上 | 2 |
| `GetModelEntries` 别名：prefix / auto_trim / modelMappings 解析出正确的 ActualModel；hideOriginalModels + hideMappedModels；lowercaseModelId | 同上 | 3 |
| round-robin 同分保持输入顺序（`WithoutWeightTieBreaker`，与权重无关） | 同上 | 1 |
| adaptive 打分：`ErrorAware(200 - (30×连续失败 + 40)×冷却比例)` + `WeightRoundRobin(150/(1+(count/(weight/100))/150))` + `LatencyAware` 中性 40 + `RateLimitAware` 中性 100；零请求零失败 490，3 次失败在 4 分钟前 464（比例 0.2） | 同上 | 2 |
| adaptive 冷却边界：恰好 5 分钟外的失败不再惩罚，两通道同为 490 并按输入顺序 | 同上 | 1 |
| adaptive 同分按 `ordering_weight DESC`；未知策略名回退 adaptive | 同上 | 2 |
| failover：`Weight + Random(0-0.5) + RateLimitAware`，按权重降序，首候选分数落在 [200, 200.5) | 同上 | 1 |
| circuit-breaker：`Weight + 关闭态熔断 200 + Random(0-0.5) + RateLimitAware`，按权重降序，首候选分数落在 [400, 400.5) | 同上 | 1 |
| 策略切换：同一状态下 adaptive 与 round-robin 的首候选不同（健康分区只属于 round-robin） | 同上 | 1 |

测试用真实 `TestCluster`（Orleans 10.4.1）+ 假 `IEnabledChannelProvider` 驱动，不依赖 Docker 或数据库；
grain 键按请求模型/通道 ID 隔离，各用例使用独立 ID 避免状态串扰。C012 已解决（四条策略链、tie-breaker、
健康分区与 topK 按参照移植）；RateLimitAware 已接入真实限流状态（C035 已解决，见 §7.1）。
残余差异为 C034（延迟样本未记录，`LatencyAware` 恒中性 40）、C036（模型级熔断无状态，按关闭态计分），
以及指标为 grain 内存态、池每次选择重读快照（C012 决策中说明的既有范围裁剪）。

### 7.1 限流与并发准入（64 个 grain 用例 + 29 个 API 用例）

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| `ChannelLimiter`：容量内直接获准、有界队列满拒绝、FIFO 让位、队列超时（≥ 配置的等待时长）、调用方取消、无界队列、超时/取消后无槽泄漏、空释放 no-op、已取消 token 的快速路径 | `ChannelLimiterTests.cs` | 10 |
| `ChannelLimiterManager`：禁用/负值配置归一为不限额、`LimiterConfig` 相等判定、`GetOrCreate` 的 null/稳定/配置变更重建、`Forget`、`Stats`、stale 条目丢弃与按通道隔离 | `ChannelLimiterManagerTests.cs` | 12 |
| `ChannelRequestTracker`：自然分钟窗口的请求/令牌计数与滚动重置、RPM 原子扣减、TPM 累加、冷却设置/查找/过期、只延长不缩短、惰性清理（`ClearExpiredCooldown`） | `ChannelRequestTrackerTests.cs` | 24（23 参照 + 冷却只延长 1） |
| `RateLimitAwareScorer`：未配置/无状态的满分 100、冷却与 RPM/TPM 耗尽的 -10000、并发分 `30+70*(1-inFlight/capacity)`、有界队列等待衰减与耗尽边界、无界队列 `30/(1+waiting)`、RPM/TPM 与并发取最小值、details 字段 | `RateLimitAwareScorerTests.cs` | 18 |
| API 中间件：每次尝试的 RPM 准入（同通道重试不能绕过）、无限制旁路、TPM 追踪（非流响应/流 Current/nil usage/零令牌）、429 Retry-After 冷却（仅上游、仅带 header）、本地拒绝不冷却、队列错误信封与无 Retry-After、连接获准/释放/流让位顺序 | `tests/AxonHub.Api.Tests/{RateLimitAdmissionTests,RateLimitTrackingTests,ChannelQueueErrorTests,ChannelLimiterMiddlewareTests}.cs` | 29 |

限流状态为 silo 级单例（`Program.cs` 注入 `ChannelRequestTracker` 与 `ChannelLimiterManager`），
通道池的 RateLimitAware 打分与请求链路中间件共享同一实例；约束并发等待的阻塞模型登记为 C042，
错误包装的单 `InnerException` 结构登记为 C043，指标缺口 C044、noop 追踪 C045。参照的
`channel_limiter_metrics_test.go`（3 例）随指标子系统顺延。

## 8. HTTP 纵向切片（51 例）

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 候选选择 → 出站变换：实际模型改写、通道 BaseUrl 拼接、`Authorization: Bearer <通道 key>`、客户端响应 body；`retry_policy.load_balancer_strategy` 接线（failover 按权重选高权重通道、round-robin 按输入顺序忽略权重） | `tests/AxonHub.Api.Tests/ChatCompletionOrchestratorTests.cs` | 17 |
| 重试语义：500 切通道、429 跳过同通道重试、同通道预算耗尽、策略关闭不重试、逐通道成功/失败计数、池初始选择计数 | 同上 | 计入上项 |
| 超时语义：非流式超时按取消处理（不记失败）且跳过同通道重试；首包超时记失败、跳过同通道重试并切换通道 | 同上 | 计入上项 |
| 无候选模型：422 `invalid_model_error` 信封，且未触达传输层 | 同上 | 计入上项 |
| 流式：SSE 事件透传到 `[DONE]`，终态后记成功 | 同上 | 计入上项 |
| SSE 写帧：gin-contrib/sse 兼容字节（`data:` 无空格、`\n`→`\ndata:`、`\r`→`\\r`）、响应头、终态后的错误抑制、classified 流错误事件（502 `upstream_stream_interrupted`）、缺终态的不完整流错误、客户端取消时的错误抑制、策略变换器改写尾随错误事件、合成的不完整错误绕过策略（C031） | `SseStreamWriterTests.cs` | 9 |
| 请求读取：头部规范化、URL 重建、`X-Forwarded-For` 优先、`Content-Type` 回退、gzip / deflate / zstd 解压并移除编码头、拒绝未知编码；gzip 头错误按 Go 文案、保留 FLG 位与 FNAME 跳过、raw DEFLATE 回退、大小写不敏感；zstd/raw DEFLATE 样本由参照 Go 程序产出 | `HttpRequestReaderTests.cs` | 12 |
| 端点胶水：空 body → 400 `JSONError`、无效模型 → 422、非流成功转发状态/内容类型/body、流式响应写 SSE 并带 CORS 头、非流与流式响应头在边界回填 request（C021）、消息内容中的非法 UTF-8 在转发前修复为 U+FFFD（C008）、custom/hidden 上游错误策略的 3 条端到端路径（C031）；Anthropic Messages 两条路由的 7 例见 §10 | `ChatCompletionEndpointTests.cs`（11）/ `AnthropicMessagesEndpointTests.cs`（7） | 18 |
| 期望流选择：`n` 与 `stream` 从含非法 UTF-8 的原始请求体读取（gjson 不校验 UTF-8） | `ExpectedStreamChoicesTests.cs` | 2 |

说明：参照的 `internal/server/orchestrator/*_test.go` 与 `internal/server/api/*_test.go` 依赖 Ent、真实数据库与 gin，
不能逐条直接移植；本切片用真实 Orleans `TestCluster` + 假通道快照 + 脚本化 `IExecutor` 复现同一调用链，
断言参照测试同样关心的可观察结果（上游请求、重试次数、客户端错误体、SSE 字节）。
这条链路的范围裁剪登记为 C014、C013–C021（多 key 随机分配与执行 key 审计已接入，C013 已闭合（trace 粘性 key 见 §32）；成本计费已接入（C046–C049，见 §8.5；API key 鉴权已接入，C023；通道级请求改写已接入，见 §8.4；限流与并发准入已接入，C035，见 §7.1）；通道级重试配置（已解决，C016）、
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

## 8.2 重试策略系统设置（49 例）

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 回退：缺行、软删除行、读库失败、JSON 语法错误（`{` / 空串 / 非对象）、`null` 文档与 `{}` 的零值策略、顶层与 `auto_disable_channel` / `upstream_error_policy` 内部成员类型错误 → `defaultRetryPolicy`（enabled=true、3/2/1000ms、adaptive、prefer_previous_channel、passthrough） | `SystemRetryPolicyServiceTests.cs` | 16 |
| 解析：全字段 snake_case、缺成员保持 Go 零值、`null` 标量保持零值、键大小写不敏感、未知成员忽略、重复键后者胜出（含 C032 的重复键 + null 边界） | 同上 | 7 |
| 归一化：策略 7（合法值 / `weighted`→failover / 非法与空→adaptive）、超时夹取 5、trace 粘性 4、上游模式 5、负计数与延迟不夹取 1、`null` 切片元素按 Go 零值 1、legacy `statuses` 迁移 1、既有 `rules` 优先 1、`custom` 空白消息回 hidden 1 | 同上 | 26 |
| 端到端 topK（跨节引用，不重复计入）：策略 5 次通道重试 + 6 个候选时打满 6 次尝试（计入 §8 的 16 例）；池的 topK 截断与归一化（计入 §7 的 2 例） | `ChatCompletionOrchestratorTests.cs` / `ChannelSelectionTests.cs` | — |

端口每个请求读取一次策略（C029 的无缓存语义同样适用于本设置），并按 `1 + MaxChannelRetries`
（禁用时 1）计算 topK 交给池；池按参照 `calculateTopK` 在排序后截断。adaptive 策略链、trace/thread 粘性与
自动禁用规则的作用分别登记为 C012/C013/C017；`upstream_error_policy` 的应用由 §8.3 锁定，C031 已关闭。

## 8.3 上游错误策略的应用（34 例）

参照调用链：`transformOrchestratorError` = quota 包装 → `ClassifyUpstreamTransportError` →
`applyUpstreamErrorPolicy` → `Inbound.TransformError`；流式路径由 `upstreamErrorStream.Err` 先分类再套策略。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 非流 `ApplyAsync`：custom 改写消息并保留状态码 / type / code / request_id，hidden 用默认文案；空 type → `upstream_error`；消息按 Go `strings.TrimSpace` 处理、custom 空白回退默认文案；passthrough 与空模式恒等；本地错误（无 upstream 标记）不改写；无结构化详情的 upstream 标记错误 → 502；code 或 type 为 `quota_exhausted` 的 ResponseError 跳过改写 | `UpstreamErrorPolicyApplierTests.cs` | 11 |
| `HttpClientError` 响应体字段：gjson 语义的 `error.type` / `error.code` / 顶层 `request_id`（仅非空字符串；非对象、非字符串、空串、非 JSON 均视为缺失）；截断报文与重复键锁定 C033 的严格解析差异 | 同上 | 8 |
| 流式 `ApplyToStreamErrorAsync`：先分类再改写（IOException → 502 `upstream_stream_interrupted`，hidden/custom 保留 code）；passthrough 分类后原样返回；普通错误在 passthrough 下原样、在激活策略下变 502；取消原样；pipeline 的 ResponseError 改写；策略读取次数（passthrough 1 次、改写 2 次，复刻参照的两次读取）；策略存储失败回退 passthrough | 同上 | 10 |
| 端点胶水：非流 custom 策略改写 429 上游错误（type/code 保留、原始报文不泄漏）；hidden 不改写非流传输错误（参照先分类成裸 ResponseError，丢失 upstream 标记）；hidden 改写流尾错误事件（默认文案 + `upstream_stream_interrupted`） | `ChatCompletionEndpointTests.cs` | 3 |
| SSE 写出：合成的"流不完整"错误不经过策略（参照只对 `stream.Err()` 调用 `upstreamErrorStream.Err`）；注入的变换器改写尾随错误事件字节 | `SseStreamWriterTests.cs` | 2 |

非流与流式的不对称是参照的真实行为而非缺陷：非流路径的分类发生在策略之前且产出裸 `ResponseError`，
因此 hidden/custom 对传输错误无效果；流式路径先分类再由策略包装，502 与 code 得以保留。残余差异为
`quota_exhausted` 的包装来源（`QuotaExhaustedError` → `wrapQuotaExhaustedAsResponseError`）仍随限流/配额里程碑
（C014）；gjson 与 System.Text.Json 的报文解析差异登记为 C033。

## 8.4 通道级请求改写（134 例）

参照调用链：候选选择 → `applyOverrideRequestBody` / `applyOverrideRequestHeaders` 两个中间件改写出站请求
（位于出站变换之后、请求链路落库之前）→ 传输层。端口以 `ChannelOverrideApplier`（`OverrideJson` 树 +
`GoTextTemplate`）实现，注册顺序为 metrics → `ChannelOverrideBodyMiddleware` →
`ChannelOverrideHeaderMiddleware` → `RequestChainPersistenceMiddleware`，因此落库的请求体/请求头是改写后的值。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 模板与渲染上下文：`.Model` / `.RequestModel` / `.Metadata` 嵌套 JSON / `.RequestHeader`（规范化键与原始小写键）/ `.PromptCacheKey` / user_id；渲染结果为 JSON 字面量时按 `encoding/json` 解码（数字、布尔、对象、数组） | `ChannelOverrideTests.cs` | 计入 86 |
| 条件与映射模型：映射后请求模型条件、if/else + 嵌套 JSON + clear 指令、reasoning effort 条件改名、条件真假与缺失条件 | 同上 | 计入上项 |
| legacy 参数与中间件：`overrideParameters` 的 set/delete/clear、非法 JSON 与空设置降级、AxonHubClear 5 例按 JSON 语义逐字段比对、rename/copy、数字渲染 | 同上 | 计入上项 |
| 新格式操作：set/delete/rename/copy、模板值、legacy 兼容、`set_if_absent`（现网与原始入站 body 共 17 例）、Issue 632 的 `chat_template_kwargs` | 同上 | 计入上项 |
| 数组操作：append/prepend/insert（含负数与越界位置）/remove、splat true/false、缺失路径建数组、非数组不改写、嵌套 `cache_control`、模板值、空 eq 与 Trim 后 eq | 同上 | 计入上项 |
| 头操作：单/多值替换、清空、空 key、空 settings 与空数组、覆盖 `Authorization`、blocked header 9 例（参照不过滤敏感/黑名单头部） | 同上 | 计入上项 |
| 门槛与敏感头：非 JSON body 与 multipart 跳过、敏感请求头不进入 `.RequestHeader`、小写敏感头识别 | 同上 | 计入上项 |
| 设置解析 `ChannelOverrideSettings.Parse`：新格式全字段、legacy map 的 GoPrintf 字符串化 11 例、新旧字段优先级、空数组抑制 legacy、解码失败整体降级 8 例、字段大小写不敏感、null index/match/splat | `ChannelOverrideSettingsTests.cs` | 47 |
| 端到端接线：通道 settings 的 body/header 操作改写出站请求，execution 落库记录改写后的 body 与 header | `ChatCompletionOrchestratorTests.cs` | 1 |

说明：`PromptCacheKeyFallsBackToTheCompactRequest` 已随 `CompactRequest` 的移植取消 Skip 并通过
（C038 闭合：顶层键优先，缺失时回退 `Compact.PromptCacheKey`）。JSON 覆盖用树模型整体重序列化（C039），模板只支持实际使用的 Go 模板子集（C040），
sjson 低层失败时保留上一个 body（C041）；参照的复用覆盖模板 CRUD（`channel_override_template_test.go`）属管理面，未移植（C014）。

## 8.5 usage 成本计费与注入（43 例）

参照调用链：`ComputeUsageCost`（`cost_calc.go`）从通道价格缓存计算 CostItem 列表与总额 →
`CreateUsageLog` 写 `total_cost` / `cost_items` / `cost_price_reference_id`（始终执行）→
`injectUsageCost` 在非流响应与每个带 usage 的流式 chunk 上把总额写入 `usage.cost`
（仅 `system_inject_usage_cost=true` 时）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 计算器：per-unit / flat_fee / tiered 分段 / volume 命中档、缓存与写缓存 token 扣除与负值归零、schedule 的 daily time、跨午夜、weekdays、date range、最低 priority、timezone（含无效时区回退 UTC）、缺失 `when` 恒匹配、未知模式/缺失 payload/未知 item code、写缓存 5m/1h 变体取代共享项 | `UsageCostCalculatorTests.cs` | 26 |
| 价格读取：缺行、未知模型、非法通道 id、损坏价格整通道失效、JSON null 价格、大小写不敏感与未加引号数字、`system_inject_usage_cost` 仅精确 `"true"`、CostItem JSON 的 trimmed-decimal 字符串与省略字段 | `UsageCostServiceTests.cs` | 12 |
| 注入与落库：开启时响应 `usage.cost` 与 usage 行同价；关闭时只落库不注入；无价格时清除上游成本、usage 行 `total_cost=null` / `cost_items=null` / 无引用 id；流式 chunk 注入；真实服务 + 存储价格端到端 | `UsageCostInjectionTests.cs` | 5 |
| 表与仓储：`channel_model_prices` 列/索引/类型、价格仓储回环与同步接缝 | `SchemaTests` / `EfCoreModelTests` / `ChannelModelPriceStoreTests` | 计入第 5 节 |

注入顺序：`RequestChainPersistenceMiddleware.TransformOutboundLlmResponse` 先注入再写 usage 行；
流式由 `FirstTokenStream.Current` 在持有带 usage 的 chunk 时注入，早于入站 SSE 变换。
差异登记为 C046（decimal 精度）、C047（实时读库）、C048（管理面未移植）、C049
（未命中价格的 `cost_items` 为 JSON null 的推断）。

## 8.6 通道多 key 选择与执行 key 审计（30 例）

参照调用链：`getAPIKeyProvider` 在启用 key 多于一个时返回 `TraceStickyKeyProvider`（否则静态 provider）→
出站变换调用 `Get()` 取 key → `CreateRequestExecution` 从实际发送的 key 写
`channel_api_key_suffix`（最后 4 个 rune）与 `channel_api_key_index`（`GetAllAPIKeys` 的 1-based 位置，
仅当全量 key 多于一个）。端口由 `ChannelApiKeyProvider`（每次尝试一个实例）与
`OpenAiOutboundTransformer` 的 `IApiKeyProvenance` 实现同一条链路。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 选择算法：单 key 静态、无 trace 随机且仅限启用 key、全禁用回退首个 key、禁用 key 排除、rendezvous 同种子确定性、不同种子分布、加 key 至多重映射 2/3、删 key 不影响未命中 trace、key 顺序无关、FNV-1a 64 与参照向量一致；trace 分支的 7 例见第 32 节 | `ChannelApiKeyProviderTests.cs` | 23 |
| 审计列：多 key 的后缀与 1-based 索引、单 key 无索引、≤4 rune 无后缀、禁用 key 不发送但仍参与索引、共享最后 4 字符时靠索引区分、OAuth 旧字段不计入 key 列表、每次重试（含跨通道）各自审计实际发送的 key | `ChannelApiKeyAuditTests.cs` | 7 |

差异登记为 C013（第 32 节闭合 trace 路径；LRU 不保留的理由见该条）、C014（凭据解码失败降级与管理面）。
`ChannelApiKeyProvider` 在构造时选择而非 `Get` 时选择：端口每次尝试新建 provider 且只调用一次 `Get`，
同一尝试内启用集合与 trace id 都不变，因此可观察结果相同；全禁用 fallback 位于工厂已拒绝的不可达分支。

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

## 10. Anthropic Messages 入站（155 例）

参照路线：`llm/transformer/anthropic/inbound.go` + `inbound_convert.go` + `inbound_stream.go` + `aggregator.go` + `usage.go`。
两条路由 `POST /v1/messages` 与 `POST /anthropic/v1/messages` 与 `/v1` 同属参照的 apiGroup
（`routes.go`），端口把它们挂在同一段中间件之后，复用 `ChatCompletionEndpoint`，
只通过 `ChatCompletionOrchestrator.WithInbound` 把入站变换换成 `AnthropicInboundTransformer`。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 请求转换：string / array system、多模态块、tool_use / tool_result、thinking 与 redacted_thinking、特殊工具块（web_search_*）及 caller/type 元数据、stop_sequences、tool_choice、thinking/output_config、tools 过滤与 web_search 工具、cache_control 三处位置、MessageIndex / ReasoningItems / 聚合签名 | `tests/AxonHub.Llm.Tests/AnthropicInboundTransformerTests.cs` | 计入 127 |
| 校验顺序与文案：model / messages / max_tokens、system prompt 类型、thinking.type / budget_tokens / effort、tool_choice | 同上 | 10 |
| Go 解码语义（C003 扩展）：根值类型、每层字段类型错误 64 例、硬错误优先级（content cannot be null / invalid content type / invalid system prompt format）、大小写折叠、重复键、显式 null 接受集 16 例；期望值来自 `E:\tmp\anthprobe` 对真实参照模块（commit 见 00-reference）的逐条探针输出 | 同上 | 80 |
| 响应转换：文本 / 思考（签名生成）/ 工具调用 / 内联工具结果 / usage 映射与 stop_reason，响应头 `Content-Type: application/json` + `Cache-Control: no-cache` | 同上 | 计入 127 |
| 错误信封：nil → 500 `error`、InvalidModel → 422 `invalid_model_error`、ResponseError → 状态码 + detail、`HttpClientError` 原样 body、InvalidRequest → 400、其它 → 500 | 同上 | 计入 127 |
| 入站流式状态机：message_start / content_block_start 生命周期、thinking（含随机签名占位）/ text / tool_use 块、citations_delta 在 block_stop 之前、Read 工具参数缓冲与空 `pages` 清理、终止时工具参数校验（多条非法调用按 index 升序）、usage 只在终止块选择、重复终止事件抑制、源错误不 finalize；4 组 xtest fixture（stop / parallel / think / or-tool）逐事件对比后再用实际事件聚合校验 | `tests/AxonHub.Llm.Tests/AnthropicInboundStreamTests.cs` | 17 |
| 聚合器：文本 / 思考 / 签名 / input_json_delta 拼接、缺失内容块补齐、message_delta 与 message_start 的 usage 合并、默认 `msg_unknown` 消息、citations、3 组 fixture 与 `usage_test.go` 的 15 项表、负 index 跳过与非法工具输入错误 | `tests/AxonHub.Llm.Tests/AnthropicAggregatorTests.cs` | 12 |
| 端点胶水：成功 → Anthropic message 信封；校验失败 → `invalid_request_error`；`stream:true` → 200 `text/event-stream` 且以 `message_stop` 结尾（无 `event:error` 尾随）；无效模型 → 422；空 body 仍为 OpenAI 风格 `JSONError`（`api.JSONError` 在变换器之前）；上游 429 经出站解析后以 Anthropic 信封返回；ResponseError 信封 | `tests/AxonHub.Api.Tests/AnthropicMessagesEndpointTests.cs` | 7 |

说明：C050（流式与聚合器缺口）已闭合，事件序列由 4 组 fixture 锁定；写入的残余差异为
C052（工具参数校验顺序确定化）、C053（负 index 跳过）、C054（无效工具输入的错误时机/文案）与
C055（省略死字段）。`input_schema` / `caller` 的显式 null 与缺失合并登记为 C051；
解码文案机制是 C003 的协议扩展。出站方向见第 11 节。

## 11. Anthropic Messages 出站（67 例）

参照路线：`llm/transformer/anthropic/outbound.go` + `outbound_convert.go` + `outbound_stream.go` +
`ensure_cache_control.go`（含 `config.go` 的 URL/认证形状）。端口由
`AnthropicOutboundTransformer`（`IOutboundTransformer`）实现；请求/响应按参照逐字段映射，
流式事件经 `AnthropicOutboundStream` 转为统一 chunk，映射为空的事件按 `streams.NoNil` 的等价语义跳过。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 请求转换：method/URL/headers、`api_key` + `X-API-Key`、model/max_tokens/messages/system 与省略字段、`ResolveMaxTokens` 优先级表、模型卡默认值、`EndpointPath` 覆盖、DeepSeek `output_config.effort`、Ollama bearer 与本地 URL、Vertex 构造期拒绝 | `tests/AxonHub.Llm.Tests/AnthropicOutboundTransformerTests.cs` | 计入 28 |
| 校验：nil 请求、model 必填、messages 必填（含 system-only 会话文案）、max_tokens 正值、compact 仅 Responses；错误类型统一为 `InvalidRequest` | 同上 | 计入 28 |
| 工具：函数工具过滤、空工具省略、null schema → `{"type":"object","properties":{}}`、strict 保留、native web_search 与 `Anthropic-Beta`、tool_choice auto/none/required/具名、parallel disable、非法参数修复、空参数 `{}` | 同上 | 计入 28 |
| 响应与错误：message → `chat.completion`、文本块拼接、错误响应（空 body / 非 2xx / 非法 JSON）与 `TransformError` 兜底（嵌套信封、顶层 message 回退、纯文本状态码短语） | 同上 | 计入 28 |
| 出站流式 fixture：stop / tool / think 三组 Anthropic SSE → 期望 LLM chunk 逐条对比（id/object/model/created/choice/delta/工具调用/内联结果/usage），`[DONE]` 哨兵；错误事件（zai 中文消息、code）；Zhipu 最终 prompt token 合并；finish_reason 块必带 delta；`server_tool_use` / `web_search_tool_result` 无 panic 回放；错误事件解析三例 | `tests/AxonHub.Llm.Tests/AnthropicOutboundStreamTests.cs` | 14 |
| 缓存断点优化器：计数、零断点注入（tools/system/消息尾部）、预算内保留、超限裁剪（消息优先/结构兜底）、自适应密度（≥20 块两个锚点、窗口边界 29/49）、system string 形式归一、结构锚点补齐与不重复、thinking/空文本清洗、raw 嵌套断点计数/裁剪与 1h 锚点保护 | `tests/AxonHub.Llm.Tests/AnthropicCacheControlOptimizerTests.cs` | 25 |

说明：出站路径发现并修复了 `GoSortedMapConverterFactory` 的缺陷（`TValue = object` 时直接调用元素
转换器的 `Write` 跳过运行时类型分派，`object` 值被写成 `{}`），raw 断点裁剪用例锁定该回归。
残余差异为 C056（Vertex 执行器未移植，Bedrock 仅直连报文形状）、C057（缺失 `delta.type` 时跳过而非
panic）与 C058（流错误事件的 gjson 宽松解析 vs 严格 JSON）。

## 12. Responses API 非流式双向变换（158 例，2026-10-31）

参照路线：`llm/transformer/openai/responses/` 的 `model.go`、`usage.go`、`usage_json.go`、
`inbound.go`、`inbound_convert.go`、`outbound.go`、`outbound_convert.go`、`compact_inbound.go`、
`compact_outbound.go`、`compact.go`。端口由 `ResponsesInboundTransformer`、
`ResponsesCompactInboundTransformer` 与 `ResponsesOutboundTransformer` 实现；路由
`POST /v1/responses` 与 `POST /v1/responses/compact` 复用 `ChatCompletionEndpoint`，只通过
`ChatCompletionOrchestrator.WithInbound` 替换入站变换（`Program.cs`）。本切片覆盖非流式路径；
流式状态机与聚合器由第 13 节补齐（C063 已闭合），图片请求/响应由第 14 节补齐（C059 已闭合），
WebSocket 传输由第 15 节补齐（C061 已闭合；生产接线缺口见 C072）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 入站请求：string / item 数组 input、instructions、采样与 token 上限、function/namespace/image_generation/web_search/google_search 工具、strict 工具、reasoning（effort/budget/summary）、tool_choice、metadata/store/service_tier、text.format、previous_response_id、additional_tools 原样片段 | `tests/AxonHub.Llm.Tests/ResponsesInboundTransformerTests.cs` | 计入 38 |
| 入站校验与 Go 解码语义：nil / 空 body、Content-Type、`stream_id` → 400（C060）、Go 语法错误与顶层根类型文案、字段级 STJ 文案（C064）、model 必填 | 同上 | 计入 38 |
| 入站响应与错误：message 信封、output item、finish_reason→status、usage 明细、previous_response_id、错误信封（nil / InvalidRequest / InvalidModel / ResponseError / HttpClientError） | 同上 | 计入 38 |
| 出站请求：URL/认证形状、compact 路由、省略 temperature、system→instructions、工具映射（function/namespace 聚合、image_generation、web_search/google_search、不支持类型跳过）、strict schema 归一（C065/C066）、tool_choice、raw 工具/输入项回放、client_metadata、Responses-Lite parallel_tool_calls、共享会话回退（C062） | `tests/AxonHub.Llm.Tests/ResponsesOutboundTransformerTests.cs` | 54 |
| 出站响应与错误：文本 / 图片生成结果 / 加密推理输出、status→finish_reason、工具调用优先、usage、上游错误信封与兜底、紧凑响应路由 | 同上 | 计入 54 |
| 压缩入站：nil / 空 body、model 必填、text input、instructions / prompt_cache_key、compaction summary 响应、流式拒绝、错误委托 | `tests/AxonHub.Llm.Tests/ResponsesCompactTransformerTests.cs` | 16 |
| 压缩出站：compact 请求与 URL、官方可选字段透传、压缩响应转换、HTTP 错误信封、空 body | 同上 | 计入 16 |
| usage 与 model：ResponsesUsage↔统一 Usage、cost 的 Go float 兼容（`xjson.ParseOptionalFloat64` 语义与 `GoJsonFloat` 的 Go 数字格式）、created_at 兼容表（整数/科学计数/极值/拒绝集） | `tests/AxonHub.Llm.Tests/ResponsesUsageAndModelTests.cs` | 50 |

合计 38 + 54 + 16 + 50 = 158 例（流式另见第 13 节的 86 例，图片路径另见第 14 节的 26 例）。
本切片登记的差异为 C059（图片路径，已由第 14 节闭合）、C060（stream_id 探测的 gjson 宽松度）、
C061（WebSocket 传输，已由第 15 节闭合）、C062（会话 ID/作用域接缝）、
C063（流式与聚合，已由第 13 节闭合）、C064（字段级解码文案）、
C065（strict required 顺序）、C066（工具 schema 数字词法）、C067（cost 之外的 double 仍用
STJ 数字格式，缺口）与 C068（并行工具调用收尾顺序与元数据编码）。
测试同时暴露并修复了 `ConvertToResponsesApiResponse` 的 `resp.Output` 未回写缺陷
（Go 直接 append 到 `resp.Output`；端口此前只改局部列表）。

## 13. Responses API 流式状态机与聚合（86 例，2026-10-31）

参照路线：`llm/transformer/openai/responses/stream_event.go`、`aggregator.go`、
`inbound_stream.go`、`outbound_stream.go` 与对应 `*_test.go`。端口由 `ResponsesStreamEvent`
（事件模型与状态转换）、`ResponsesInboundStream`、`ResponsesOutboundStream`、
`ResponsesAggregate` 与 `ResponsesStreamMetadata`（元数据编码）实现；
`ResponsesInboundTransformer.TransformStream` / `AggregateStreamChunks` 与
`ResponsesOutboundTransformer.TransformStream` / `AggregateStreamChunks` 全部接通。
fixture 取自参照 `testdata/`（16 个文件，`tests/AxonHub.Llm.Tests/testdata/responses/`），
入站与出站各做 3 组逐事件回环，聚合器做 2 组最终响应逐字段对比。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 入站流状态机：chunk → `response.*` 事件（消息内容与内容分片、推理摘要/推理文本/加密内容、function 与 custom 工具调用、web_search 元数据、注解、`response.completed/incomplete/failed`、取消映射）、无终态 EOF / 超时 / 取消 / `HttpClientError` / `StreamIncomplete` 的错误分类、错误事件在 start 前与 failed 在 start 后的位置、usage-only 块、8 行 finish_reason→终态表 | `tests/AxonHub.Llm.Tests/ResponsesInboundStreamTests.cs` | 24 |
| 出站流状态机：上游 SSE → 统一 chunk（3 组 fixture 逐条对比与最终 model/id）、`response.metadata` 回环、`[DONE]` 仅在语义终态后合成（6 行不完整表、重复终态、迟到源错误）、error 事件 → `ResponseError`、推理加密内容按 item 归属、推理摘要关联、推理文本、压缩摘要回环、取消、仅 done 的 arguments、等价 arguments、大数拒绝、迟到工具身份与延迟发出、custom tool 输入对账与冲突、注解、web 搜索元数据（有/无注解）、previous_response_id、状态/原因映射、created_at 整数与浮点、3 行 WebSocket error 事件 | `tests/AxonHub.Llm.Tests/ResponsesOutboundStreamTests.cs` | 34 |
| 聚合器：空/ nil 事件流拒绝、取消态规范化、9 行完成快照表（status/id/model/created_at/previous_response_id/error/incomplete_details/usage 与 ResponseMeta）、2 组 fixture、基础事件合成、注解保留与回退、previous_response_id、非法 JSON 与 `[DONE]` 跳过、多段推理摘要、推理文本、非法 content index（4 行）、图片生成调用 | `tests/AxonHub.Llm.Tests/ResponsesAggregatorTests.cs` | 28 |

合计 24 + 34 + 28 = 86 例。C063 闭合；新增 C068（Go map 随机的并行工具收尾顺序改为插入序、
元数据经 JSON 字符串/对象往返）。图片流式聚合已随 C059（第 14 节）闭合；WebSocket 传输已由
第 15 节补齐（C061）；WebSocket 的 `error` 事件在流层可移植，已由出站表锁定。16 个 fixture 同时覆盖
`response.metadata`、`error` 与 `stop` 三个小型事件流。

## 14. Responses 图片请求/响应路径（26 例，2026-10-31）

参照路线：`llm/transformer/openai/responses/image_request.go`、`image_response.go`、
`image_stream_test.go`、`image_roundtrip_test.go` 与 `outbound_test.go` 的图片用例。
端口新增统一模型 `Model/Image.cs`（`ImageRequest` / `ImageResponse` / `ImageData`）、
`LlmRequest.Image` / `LlmResponse.Image` 与 `api_format`（`openai/image_generation`、
`openai/image_edit`、`openai/image_variation`）；请求构造与响应还原由 `ResponsesImageConvert`
（`BuildImageToolRequest`、`BuildImageResponse`、`ImageDataUrls`、`MaskDataUrl`、
`ImageUrlsFromRawJson`、`BytesToImageDataUrl`）实现，`ResponsesOutboundTransformer` 在
`RequestType=image` 时改走图片工具请求，并把 `image` 回写到线路请求以选择响应分支。
图片流式聚合（codex 的 `response.output_item.done` 携带 `result`）由现有
`ResponsesAggregate` 与 `BuildImageResponse` 组合覆盖。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 图片工具请求构造：默认系统指令 + 保留系统消息 + user 文本/图片分片；nil 请求、非法 api_format、空 prompt、`n > 1`、编辑无图五类校验与文案；原始 JSON `image`/`mask` 的 `data:image/` 取值、字节图片的 Go `DetectContentType` 子集（PNG、JPEG 与 `text/plain`→`image/png` 回落）、工具模型覆盖（trim、空白回退）与 `codex_image_*` 元数据 | `tests/AxonHub.Llm.Tests/ResponsesImageTests.cs` | 9 |
| 图片响应还原：nil 与上游 error、`image_generation_call` 结果收集与空值跳过、`data:` 前缀剥离、默认 `png` 与质量/尺寸/背景回显、`codex_image_model` 覆盖、无结果文案 | 同上 | 11 |
| 出站接线：线路 `RequestType` 回写、`/v1/responses` payload（model/tool_choice/instructions/tools/输入 data URL）、编辑往返（`image.generation`、webp/high/1024x1024、b64_json）、HTTP ≥400 与空 body/非对象根的解码错误文案 | 同上 | 4 |
| codex 图片流：13 个事件（`partial_image` 与 `output_item.done` 的 `result`、裸字符串 action）聚合后还原为单图响应 | 同上 | 1 |
| `ItemAction` 三态解码：裸字符串、web_search 对象、`action: null`（线路指针语义） | 同上 | 1 |

合计 9 + 11 + 4 + 1 + 1 = 26 例。C059 闭合；客户端面向的 OpenAI Images 入站/出站变换器
（`image_inbound.go` / `image_outbound.go`）已由第 16 节补齐（C069 闭合）。

## 15. Responses WebSocket 传输（45 例，2026-10-31）

参照路线：`llm/transformer/openai/responses/websocket_executor.go`、`websocket_executor_test.go`、
`http_executor.go`、`http_executor_test.go`，以及 `internal/server/orchestrator/outbound.go:880-925`
的 `CustomizeExecutor` / `finalizeTransportRequest` 接缝。端口由 `WebSocketExecutor`
（执行器、连接池、续传对账、Do 聚合）、`WebSocketStream`、`PooledWebSocketConnection`、
`WebSocketLease`、`ResponsesWebSocketClient`（手写 RFC 6455 握手）与 `HttpTransportExecutor`
实现；`ResponsesOutboundTransformer.CustomizeExecutor/FinalizeTransportRequest/Stop` 提供传输选择、
HTTP 清理与关闭，编排层由 `PersistentOutboundTransformer` 的对应方法委托。
参照测试用 `httptest` + gorilla upgrader；端口用 `ResponsesWebSocketTestServer`
（loopback `TcpListener` + 手写 101 握手 + 逐连接处理线程）替代，握手头、升级前的 HTTP 拒绝
与连接关闭都可观察。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 执行器、握手与 URL：CustomizeExecutor 按内层执行器缓存单例；`response.create` 帧（删除 stream/background、beta 头、Go 的换行结尾）；http/https→ws/wss 映射与非法方案；握手响应头在成功与拒绝时都交给 `ObserveResponseHeaders` | `tests/AxonHub.Llm.Tests/ResponsesWebSocketExecutorTests.cs` | 4 |
| 流生命周期与取消：终态后停止且无需 close 帧；读阻塞中取消、已关闭时取消、取消不覆盖终态；复用连接在事件前关闭报错；连接提前关闭逐出池 | 同上 | 6 |
| 错误事件与聚合：顶层 `error` → 传输错误；官方 error 的状态码/body/头原样保留（3 行）；Do 聚合 failed、cancelled、incomplete 为 200（3 行）；事件规范化的嵌套 error 展平与已展平字段保留 | 同上 | 9 |
| 连接池与续传对账：同会话复用、无会话不池化、显式 previous_response_id 的保持/重连/匹配（4 例）、payload 恢复、assistant 输出后缀重连、组织头分池、failed/cancelled 逐出（3 行）、仅发送新输入、输入超限不保留、非追加输入重连、trace 头不进入池键、后台清理与 Close 关闭空闲连接 | 同上 | 18 |
| HTTP 传输包装：剥离 `responses_websockets` beta 元数据、保留 previous_response_id/stream/input、HTTP 路径原样透传、原始请求不被修改 | `tests/AxonHub.Llm.Tests/ResponsesHttpTransportExecutorTests.cs` | 3 |
| 编排接缝：`CustomizeExecutor` 委托/透传、`FinalizeTransportRequest` 委托/透传、中间件调用编排层 | `tests/AxonHub.Api.Tests/TransportRequestFinalizerTests.cs` | 5 |

合计 4 + 6 + 9 + 18 + 3 + 5 = 45 例。C061 闭合；拨号实现（C070）、JSON 往返（C071）、
生产接线与 `Stop` 生命周期（C072）、URL 归一化（C073）分别登记。测试同时暴露并修复了
连接池 in-flight 槽的释放缺陷：Go 的 `select { case <-pc.inFlight: default: }` 在语义上等价于
“占有令牌时释放信号量”，端口此前写成“信号量空闲时先获取再释放”，净效果为零，导致池化连接
永远被清理与关闭逻辑跳过；`ReleaseInFlight` 改为“释放已持有的槽，已空闲时忽略
`SemaphoreFullException`”，由复用、后台清理与 Close 三组用例锁定。

## 16. OpenAI Images 客户端变换器（71 例，2026-10-31）

参照路线：`llm/transformer/openai/image_inbound.go`、`image_outbound.go`、
`image_inbound_test.go`、`image_outbound_test.go`、`image_edit_outbound_test.go`（共 69 个测试函数）
与 `outbound.go` 的 image 分支。端口新增 `ImageInboundTransformer`（`Generation`/`Edit`/`Variation`
三个工厂）与 `OpenAiOutboundImage`（`OpenAiOutboundTransformer` 的 partial：三个请求构造器与响应解码），
并在 `TransformRequest` / `TransformResponse` 按 `RequestType=image` 与线路 `ApiFormat` 分派。
共享基础设施新增 `GoMultipartReader` / `GoMultipartWriter`（Go `mime/multipart` 的读写子集：30 字节
hex boundary、header 按名排序、`escapeQuotes`、`FormName`/`FileName` 与 `filepath.Base` 语义）、
`GoMimeTypes.TryParseMediaType` 与 `GoHttpSniff.DetectContentType`；请求体校验复用
`OpenAiRequestTypeCheck` 的 Go 字段级类型错误文案。测试通过 `GoMultipartWriter` 构造 multipart
请求体，并以序列集合串行化可变上限（`MaxImageFileSize` / `MaxImageBodySize`）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 入站：生成 JSON（单图/多图/非法 `image` 字段/非 data URL/非 base64）、生成往返（`image` 以单条 data URL 回写）、编辑 multipart（mask、`image[]`、混合字段名、5 MiB 图片）、编辑 JSON（多图与 mask、prompt/图片/流式/非 data URL 校验、body 超限、数量上限、`images` 对象与 data URL 数组、`image` 优先与空数组回退、`images[i]:` 错误前缀、空数组无图）、响应还原为 `ImagesResponse` | `tests/AxonHub.Llm.Tests/OpenAiImageInboundTests.cs` | 36 |
| 出站：路由（image 请求/纯文本）、生成请求字段与 `dall-e-*` 默认 `b64_json`、单/多图 data URL、无图省略字段、gpt-image 省略 response_format、编辑 multipart 与 JSONBody、变体、自定义 EndpointPath（3 行表）、响应解码（基础、usage/cached/output details、多图、模型元数据）、`extractFile` 四例、集成往返 | `tests/AxonHub.Llm.Tests/OpenAiImageOutboundTests.cs` | 33（30 个函数 + 3 行 Theory） |
| 编辑线路形状：model 字段先于图片 part；JPEG 元数据在 JSONBody 与 multipart 头之间一致 | `tests/AxonHub.Llm.Tests/OpenAiImageEditOutboundTests.cs` | 2 |

合计 36 + 33 + 2 = 71 例。C069 闭合；入站解码与 multipart 的跨语言边界登记为 C074。

## 16.1 `/v1/images/*` 路由接线（8 例，2026-10-31）

参照 `internal/server/routes.go:215-216` 只注册 `POST /v1/images/generations` 与
`POST /v1/images/edits`，variations 路由被注释停用（“DO NOT SUPPORT IMAGE VARIATION”）；
`api.OpenAIHandlers.CreateImage` / `CreateImageEdit` 直接委托 `ChatCompletion`，只替换入站变换器
（`openai.NewImageGenerationInboundTransformer` / `NewImageEditInboundTransformer`）。
端口在 `Program.cs` 以 `orchestrator.WithInbound(ImageInboundTransformer.Generation()/Edit())`
复刻同一路径：`HttpRequestReader` 原样透传 multipart body 与 Content-Type，错误信封由
`OpenAiInboundTransformer.TransformError` 生成，响应由图片入站变换器序列化为
`ImagesResponse`。同时补齐 `ResponseContent.HasResponseContent` 的 `resp.Image` 分支：
`empty_response.go` 在 chat choices 之前检查图片数据，否则开启空响应检测时图片响应会被
当作空响应重试（`GenerationCountsImageResponseAsContentWhenEmptyDetectionIsOn` 锁定）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 路由接线：空 body 400、生成 JSON 透传与上游 `/v1/images/generations` 线路形状、缺省模型回退 `dall-e-2`、空响应检测下的图片响应计为内容、编辑 multipart 透传（model 先于图片 part、文件名由嗅探类型生成）、缺图/缺 prompt/非 JSON 的 400 信封 | `tests/AxonHub.Api.Tests/ImageEndpointTests.cs` | 8 |

合计 8 例。C075 闭合；参照停用的 variations 路由在端口同样不注册。

## 17. OpenAI Embeddings / Moderations 客户端变换器与路由接线（71 例，2026-10-31）

参照路线：`llm/embedding.go`、`llm/moderation.go`、`llm/transformer/openai/embedding.go`、
`embedding_inbound.go`、`moderation.go`、`moderation_inbound.go` 与
`embedding_test.go`（10 个函数）、`moderation_test.go`（6 个函数），以及
`internal/server/routes.go:211-212` 的两条路由与 `internal/server/api/openai.go` 的
`CreateEmbedding` / `CreateModeration`。端口新增统一模型 `EmbeddingInput` /
`EmbeddingRequest` / `EmbeddingResponse` / `EmbeddingData` / `Embedding` 与
`ModerationInput` / `ModerationMultiModalPart` / `ModerationRequest` /
`ModerationClassification` / `ModerationResponse`，两者均以自定义 `JsonConverter`
复刻 Go 的 `MarshalJSON`/`UnmarshalJSON`：嵌入输入按 string → []string → []int64 →
[][]int64 顺序解码（首个成功者生效，空值输出 `null`），审核输入按 string → []string →
parts 顺序解码（零值先置位），审核分类的 map 按 Go 的键排序输出且
`category_applied_input_types` 保留 omitempty。入站变换器保持 Go 的分支顺序与逐字文案：
嵌入先校验 model 再校验 input；审核先校验 input（parts → string array → string），
再把空 model 归一化为 `omni-moderation-latest`。出站侧
`OpenAiOutboundEmbeddingModeration` 构造 `/embeddings` 与 `/moderations` 请求
（`EndpointPath` 覆盖、`#` 自动 raw-URL、`##` 显式 raw-URL），并按线路 ApiFormat 解码
上游响应。`Program.cs` 按 routes.go 注册 `POST /v1/embeddings` 与 `POST /v1/moderations`，
两者都复用 chat 处理器、只替换入站变换器；`ResponseContent.HasResponseContent` 增加
moderation 与 embedding 分支（检查顺序与 `empty_response.go` 一致）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 嵌入入站：字符串/数组/token ids/嵌套 token ids、缺 model、缺 input、空字符串、空数组、纯空白、nil 请求、空 body、非 JSON 类型、空嵌套数组、非法 JSON 的解码文案；嵌入出站：请求线路形状、nil 请求、缺 embedding 载荷；上游响应：正常/无 id/400/500/空 body/非法 JSON；入站响应：往返、usage cost、无 usage 的零值 usage、nil 响应；APIFormat、TransformError 三例、流式拒绝两例、URL 构造 5 行表、raw URL 3 行表、自定义 EndpointPath | `tests/AxonHub.Llm.Tests/OpenAiEmbeddingTransformerTests.cs` | 43 |
| 审核入站：字符串/默认模型/字符串数组/多模态、空输入、空数组、缺 image url、非 JSON、空 body；入站响应往返；出站往返（URL、RequestType/ApiFormat、线路 body、上游解码）、自定义 EndpointPath、缺 moderation 载荷；`ModerationInput` 的 string/string array/parts 三种 JSON 形态 | `tests/AxonHub.Llm.Tests/OpenAiModerationTransformerTests.cs` | 16 |
| 空响应探测：嵌入 data 计为内容、审核 results 计为内容、空审核响应不计为内容；pipeline 在开启检测时接受非流式嵌入与审核响应（各 1 次执行） | `tests/AxonHub.Llm.Tests/EmptyResponseTests.cs` | 5 |
| 路由接线：空 body 400、嵌入透传与上游 `/v1/embeddings` 线路形状、缺 input 的 400 信封、空响应检测下嵌入响应计为内容、审核默认模型与往返、非法 input 的 400 信封、空响应检测下审核响应计为内容 | `tests/AxonHub.Api.Tests/EmbeddingModerationEndpointTests.cs` | 7 |

合计 43 + 16 + 5 + 7 = 71 例。C076 闭合；C010 的 embedding/moderation 分支随之闭合，
剩余非 chat 响应类型仍在缺口清单中。

## 18. OpenAI Alpha Search / Decisions 客户端变换器与路由接线（137 例，2026-10-31）

参照路线：`llm/transformer/openai/alpha_search_inbound.go`、
`alpha_search_outbound.go` 与 `decisions/{inbound,outbound}.go`，测试
`alpha_search_test.go` 与 decisions 的 5 个测试文件，路由
`internal/server/routes.go:213-214` 以及 `empty_response.go` 的两个非 chat 分支。
端口新增 `AlphaSearchRequest` / `AlphaSearchResponse`、`DecisionsRequest` /
`DecisionsUsage` / `DecisionsResponse` 与 `LlmRequest` / `LlmResponse` 的对应字段，
`AlphaSearchInboundTransformer`、`DecisionsInboundTransformer`、
`DecisionsOutboundTransformer`，`OpenAiOutboundTransformer` 按 `RequestType` 的
alpha 分派，以及 `RawJsonModelPatch`：只改写顶层 `model`、其余字节原样保留，
缺键时在闭括号前追加，与 sjson 的 `SetBytes` 语义一致。decisions 入站与出站都
做严格信封校验（对象根、model/input/questions/answers 的逐层类型检查），逐字段
错误文案按 Go 1.26.4 运行结果对齐，并复刻 `json.Decoder` 的首值与尾随二段扫描；
JSON 嵌套上限统一为 Go 的 10000 层。`Program.cs` 按 routes.go 注册
`POST /v1/alpha/search` 与 `POST /v1/decisions`，两者复用 chat 处理器、只替换
入站变换器；`ResponseContent.HasResponseContent` 增加 `AlphaSearch.Body` 与
`Decisions.Answers` 分支。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| alpha 入站/出站往返、缺 model（含纯空白）、model 补丁（替换/追加/自定义 EndpointPath）、非对象信封与非法 JSON 文案、Content-Type 校验、空 body/nil 请求、空成功响应、流式拒绝两例、嵌套深度超过 64、上游错误响应映射 | `tests/AxonHub.Llm.Tests/OpenAiAlphaSearchTransformerTests.cs` | 19 |
| decisions 请求解码（Go 结构体类型错误、根 null、字段大小写折叠与重复键、截断/空输入、语法错误优先于类型错误、尾随数据二段解码）、严格校验文案、item 键大小写敏感、流式拒绝、出站 model 补丁/URL/线路形状/配置校验、响应解码（answers/model/usage 逐字段类型错误、token 字面量、重复键合并、空 body 视图）、入站响应原样回传、`TransformError` 四分支与宽松解码 | `tests/AxonHub.Llm.Tests/OpenAiDecisionsTransformerTests.cs` | 110 |
| 空响应探测：alpha-search body 与 decisions answers 计为内容 | `tests/AxonHub.Llm.Tests/EmptyResponseTests.cs` | 2 |
| 路由接线：alpha-search 原样转发与上游 `/v1/alpha/search` 线路形状、缺 model 的 400 信封、上游错误的重新封套；decisions 原样转发与 `/v1/decisions` 线路形状、缺 questions 的 400 信封、上游错误的原始字节透传 | `tests/AxonHub.Api.Tests/AlphaSearchDecisionsEndpointTests.cs` | 6 |

合计 19 + 110 + 2 + 6 = 137 例。C077 闭合；C010 的 decisions / alpha-search
分支随之闭合，剩余 speech / transcription / rerank / video 仍在缺口清单中。

## 19. OpenAI Audio 客户端变换器与路由接线（102 例，2026-10-31）

参照路线：`llm/transformer/openai/audio_inbound.go`、`audio_outbound.go`，测试
`audio_inbound_test.go`（329 行）与 `audio_outbound_test.go`（547 行，全部 15 个
测试函数逐条移植），路由 `internal/server/routes.go` 的 `/v1/audio/*` 三条路由，
以及 `internal/server/api/chat.go:668-757` 的 `WriteBinaryStream` 与
`internal/server/api/openai.go:437-465` 的二进制流探测。端口新增
`OpenAiAudioInboundTransformer`（Speech / Transcription / Translation 三个工厂）、
`OpenAiOutboundAudio`（请求构造、响应变换与流聚合）、`AudioModels`
（事件模型、二进制解码器注册表）、`GoMultipartReader` / `GoMultipartWriter`、
`BinaryStreamWriter` 与 `SpeechEndpoint.ShouldUseBinarySpeechStream` 路由探测；
`Program.cs` 注册 `POST /v1/audio/speech`、`/v1/audio/transcriptions`、
`/v1/audio/translations`。speech 的 `stream_format`（trim + lower 后与
`sse` / `audio` 比较）决定三种传输形态：非流式二进制、SSE 流（`audio_bytes`
base64 事件）、二进制流（原始音频块 + `binary.done`，Accept `*/*`）；STT 支持
multipart（`file` + 文本字段）、JSON 与 SRT / verbose_json 响应透传。
`GoCast.TryParseFloat` 完整复刻 strconv.ParseFloat 64（hex 浮点负偏置、
零尾数短路、下划线规则、inf / nan、范围错误），差异登记 C079。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| speech 入站：JSON 校验链（语法 → 根类型 → 字段类型 → 解码）与逐字段错误文案、缺 model / input / voice、空 body、二进制响应透传、stream_format 三形态与非法值（GoQuote 原文）、缺省非流式 | `tests/AxonHub.Llm.Tests/OpenAiAudioTransformerTests.cs` | 10 |
| transcription 入站：multipart 解析（file 必填、重复 file 拒绝、extra 字段保留、非法 temperature）、非 multipart 拒绝、JSON / verbose_json 原始体 / SRT 响应透传 | `tests/AxonHub.Llm.Tests/OpenAiAudioTransformerTests.cs` | 10 |
| translation 入站：`api_format` 标记与基本请求 | `tests/AxonHub.Llm.Tests/OpenAiAudioTransformerTests.cs` | 2 |
| 出站：speech 请求线路形状（`/v1/audio/speech` URL、voice / speed / stream_format 字段）、STT multipart 线路形状（文件名、extra 字段）、translation URL、speech 响应变换（括号文本不误判为 JSON、缺 CT 非法 JSON 原样透传、缺 CT 合法 JSON 解析）、transcription 响应变换（text / language / duration 空值置 null） | `tests/AxonHub.Llm.Tests/OpenAiAudioTransformerTests.cs` | 12 |
| 出站：`TransformTranscriptionResponse` 的 SRT 原始体透传 | `tests/AxonHub.Llm.Tests/OpenAiAudioTransformerTests.cs` | 1 |
| 往返：speech 请求 → 统一模型 → 出站请求、响应 → 统一模型 → 出站响应 | `tests/AxonHub.Llm.Tests/OpenAiAudioTransformerTests.cs` | 1 |
| 流式：SSE speech 聚合（4 上游事件 → 3 收集、audio_bytes 拼接、usage、终态 `speech.audio.done`）、二进制 speech 块（3 块拼接、meta id `audio.speech.stream`、usage null）、transcription SSE（`name="stream"`、delta 聚合、usage）、错误事件传播（upstream broke / rate limited） | `tests/AxonHub.Llm.Tests/OpenAiAudioTransformerTests.cs` | 5 |
| 空响应探测：speech 音频、transcription 文本/原始体、speech 流事件 audio_base64（裸 done 不计）、speech 二进制块、transcription 流事件 | `tests/AxonHub.Llm.Tests/EmptyResponseTests.cs` | 5 |
| speech 路由探测：nil 请求、空 body、非 JSON 跳过解码、stream_format 判定表 8 例（大小写/空白/缺省/null）、解码错误文案 4 例（语法、根类型、字段类型含结构体路径） | `tests/AxonHub.Api.Tests/AudioEndpointTests.cs` | 15 |
| strconv.ParseFloat 保真表：36 例（普通/科学计数/尾随点、零尾数短路 `0e10000`、hex 浮点含负偏置 `0x_1p2`、下划线、nan / inf / Infinity / ±Inf、范围错误 `1e400` / `1e309`、非法语法） | `tests/AxonHub.Api.Tests/AudioEndpointTests.cs` | 36 |
| 二进制流写入器：原始块直写与首块 CT / Cache-Control / Connection / ACAO 头、无类型块回退 CT、binary.done 与空块吞掉、headers 前的分类错误 502 + 精确信封、ResponseError 状态码、headers 后错误只记日志、取消只记日志 | `tests/AxonHub.Api.Tests/AudioEndpointTests.cs` | 7 |

合计 39（transformer）+ 5（空响应）+ 58（端点：15 探测 + 36 保真表 + 7 写入器）
= 102 例。差异登记 C079 闭合；C010 的 speech / transcription 分支随之闭合，
剩余 rerank / video 仍在缺口清单中。

## 20. Jina Rerank / Embedding 客户端变换器与路由接线（88 例，2026-10-31）

参照路线：`llm/transformer/jina/rerank_inbound.go`（195 行）、
`embedding_inbound.go`（192 行）、`outbound.go`（437 行）、`model.go`、
`usage_json.go` 与测试 `embedding_test.go`（655 行，8 个测试函数逐条移植；
rerank 入站/出站在参照侧无独立单测，按源码行为锁闭），路由
`internal/server/routes.go:230,238-239` 与 `internal/server/api/jina.go`。
端口新增 `JinaRerankInboundTransformer`（TransformRequest 校验链：nil / 空
body → 语法校验 → 根类型探测 → 解码 → model / query / documents 逐项校验；
TransformResponse 只挂 Content-Type；TransformError 只特判 ResponseError，
其余一律 500 `api_error` 信封——参照 chat.go → transformOrchestratorError
全链路行为）、`JinaEmbeddingInboundTransformer`（含 Content-Type 默认与校验、
`validateEmbeddingInput` 分支序、响应 Cache-Control 头、TransformError 委托
rerank 入站；TransformStream 是 ErrInvalidRequest 包装而 rerank 是裸错误——
参照如此）、`JinaOutboundTransformer`（requestType 分派 rerank / embedding /
compact 拒绝 / 未知类型拒绝；`/rerank` 与 `/embeddings` URL、v1 规范化；响应按
`Request.ApiFormat` 分派解码；usage 只取 token 计数不落 cost；TransformError
解析 `{"error":{...}}` 信封、非 JSON 回退 GoHttpStatus 文案）、`JinaModels`
（wire 模型：top_n / return_documents / usage / cost 的 omitempty、
EmbeddingUsage cost 走 GoOptionalNumberConverter）、统一模型 `RerankRequest` /
`RerankResponse` / `RerankResult` / `RerankDocument`；`Program.cs` 注册
`POST /v1/rerank`、`/jina/v1/rerank`、`/jina/v1/embeddings`，
`ChannelOutboundFactory` 增加 `jina` 通道类型分派。差异登记 C080。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| rerank 入站：全字段请求与可选字段缺省 null、nil / 空 body、model / query / documents 校验序、语法 / 数组根 / 字符串根 / null 根 / 字段类型（C080 的 STJ 文案）解码错误文案、响应精确字节（document 省略、空 results 数组、usage 无 cost）、usage cost 0.000005 往返、nil 响应 / nil rerank、TransformError 三支（nil / 普通异常 → 500 信封、ResponseError → 状态码 + 详情）、流式拒绝 | `tests/AxonHub.Llm.Tests/JinaRerankTransformerTests.cs` | 23 |
| 出站：三档 baseURL 规范化 × rerank / embedding（6 理论例）、配置校验、ApiFormat、rerank 线路形状精确字节（含 top_n / return_documents 与省略）、nil 请求 / 缺 rerank / model / query / documents 纯文本校验、compact 与未知类型拒绝文案、响应解析（usage 无 cost）、nil Request 回退 rerank 解码（C080）、非法 JSON / 空 body / ≥400 jina 信封 / ≥400 非 JSON 回退状态文案、TransformError nil / 信封 / 非 JSON、流式拒绝 | `tests/AxonHub.Llm.Tests/JinaRerankTransformerTests.cs` | 29 |
| embedding 出站：task 缺省 text-matching 与显式 task 精确字节、缺 embedding / model 文案、响应解析、零 usage 丢弃、非法 JSON | `tests/AxonHub.Llm.Tests/JinaRerankTransformerTests.cs` | 7 |
| embedding 入站：字符串 / 数组输入、task 透传、缺 model / input、空字符串 / 空数组输入、nil / 空 body、Content-Type 拒绝、非法 JSON / 数组根解码文案、响应（Cache-Control、零 usage 仍输出、cost 往返）、nil 响应 / 缺 embedding、TransformError 委托 rerank、流式拒绝（InvalidRequest 包装） | `tests/AxonHub.Llm.Tests/JinaRerankTransformerTests.cs` | 20 |
| 空响应探测：rerank results 有内容 / 空 | `tests/AxonHub.Llm.Tests/EmptyResponseTests.cs` | 2 |
| 路由接线：空 body 400、校验错误与解码错误 → 500 api_error 信封（参照特有）、rerank 转发（上游 URL `/v1/rerank`、ApiFormat、Bearer 头、响应透传）、`/jina/v1/rerank` 同处理器、embedding 转发（URL `/embeddings`、task 默认 text-matching、usage 透传）、embedding 校验错误 500 | `tests/AxonHub.Api.Tests/RerankEndpointTests.cs` | 7 |

合计 23 + 29 + 7 + 20 + 2 + 7 = 88 例。差异登记 C080；C010 的 rerank 分支随之
闭合，非 chat 响应类型仅剩 video。

## 21. OpenAI Video 客户端变换器 Llm 层（57 例，2026-10-31）

参照路线：`llm/transformer/openai/video_inbound.go`、`video_inbound_multipart.go`、
`video_extensions.go`、`video_outbound.go`（343 行）与测试
`video_inbound_test.go`（8 个测试函数）、`video_outbound_test.go`（3 个测试函数）。
端口为 `OpenAiOutboundTransformer` 新增 partial `OpenAiOutboundVideo`
（`BuildVideoGenerationApiRequest` / `TransformVideoResponse` /
`BuildGetVideoTaskRequest` / `ParseGetVideoTaskResponse` /
`BuildDeleteVideoTaskRequest` / `NormalizeVideoStatusOpenAI`，实现
`IVideoTaskOutbound`）、`OpenAiVideoExtensions`（`ParseVideoJsonRequest` 的
Go 解码链与 unknown 字段扫入 extra_body、multipart 扩展字段、
`MergeVideoExtraBody`）、`OpenAiVideoInboundMultipart`（multipart 解析、
4MB/20MB 上限、input_reference 快照）、统一模型 `VideoRequest` /
`VideoResponse` / `VideoContent` / `VideoError` 与 wire 模型
`OpenAiVideoModels`，`EmptyResponse` 补齐 video 分支；差异登记 C081。
端点接线（routes + biz VideoService + 持久化）为下一节（Segment B）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 入站 JSON：全字段请求与可选字段缺省 null、extra_body 合并与 provider_hint 等 unknown 字段扫入、model / prompt 校验序、nil / 空 body / 默认 CT=JSON、非法 CT 文案、语法 / 数组根解码文案、extra_body 非对象拒绝 | `tests/AxonHub.Llm.Tests/OpenAiVideoTransformerTests.cs` | 10 |
| 入站 multipart：模型与提示字段、input_reference 文件（data URL 前缀、嗅探 MIME、未类型短类型拒绝、4MB 上限、多文件拒绝）、内容合成（text + image_url first_frame）、扩展字段（execution_expires_after / content / return_last_frame / tools 类型校验、native 字段 JSON 校验、extra_body 合并与覆盖）、缺 boundary | `tests/AxonHub.Llm.Tests/OpenAiVideoTransformerTests.cs` | 14 |
| 响应变换：status 映射（in_progress→running 线路回写、created_at、零 completed_at 省略）、usage 与 error 透传、nil video、流式拒绝 | `tests/AxonHub.Llm.Tests/OpenAiVideoTransformerTests.cs` | 6 |
| 出站 create：线路形状精确字节（model / prompt / input_reference / seconds / size 与省略）、URL `/videos`、Bearer 与 metadata（model / video_prompt）、缺 video / model / prompt 文案、marshal 失败 | `tests/AxonHub.Llm.Tests/OpenAiVideoTransformerTests.cs` | 6 |
| 出站响应：默认 model video-generation 与 metadata 回显、缺 id、provider error 400 信封、非法 JSON / 数组根文案、status 归一化表 7 例、ApiFormat 分派 | `tests/AxonHub.Llm.Tests/OpenAiVideoTransformerTests.cs` | 12 |
| 出站 get / delete：URL 拼接与 Bearer、缺 providerTaskID 文案、get 响应解析（seconds 字符串、状态归一化、时间戳、usage、error、非法 JSON） | `tests/AxonHub.Llm.Tests/OpenAiVideoTransformerTests.cs` | 8 |
| 空响应探测：video 分支（ID / Status / VideoURL / Error 任一非空即视为有内容） | `tests/AxonHub.Llm.Tests/EmptyResponseTests.cs` | 1 |

合计 10 + 14 + 6 + 6 + 12 + 8 + 1 = 57 例（`OpenAiVideoTransformerTests` 50 个测试
方法、1 个 7 行 Theory）。差异登记 C081；C010 的 video Llm 分支随之闭合，仅剩
video 端点接线（routes + biz VideoService + 持久化，Segment B）。

## 22. OpenAI Video 端点接线与视频任务查询/删除（36 例，2026-10-31）

参照路线：`internal/server/api/openai.go:489-578`（CreateVideo / GetVideo /
DeleteVideo）、`internal/server/biz/video.go`（227 行）与测试 `video_test.go`
（7 个测试函数）、`routes.go:217-219`。端口新增 `VideoService`（`GetTask` /
`GetTaskByExternalID` / `DeleteTask` / `DeleteTaskByExternalID` / `loadTask` /
`mapVideoStatusToRequestStatus` 的逐函数移植，通道 outbound 由
`ChannelOutboundFactory` 按通道类型重建）、`VideoEndpoint`（GET/DELETE 胶水，
GET 复用 `VideoInboundTransformer` 渲染统一响应）、`POST /v1/videos` 路由
（复用 chat handler）、`RequestIdentity.FromHttpContext`，以及持久化接缝
`IRequestStore.GetByExternalIdAndProjectAsync` /
`UpdateStatusExternalIdAndResponseBodyAsync` 与
`IRequestExecutionStore.ListByRequestIdDescAsync`。差异登记 C082–C085；
C010 的 video 端点接线随之闭合（doubao / zenmux 视频变换器仍为 C010 的既定裁剪）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 服务层（对照 `video_test.go` 的 7 个测试函数）：external_id + project 作用域查询（GET/DELETE 跨项目均 not found）、GET 任务并落轮询快照、最新执行格式优先、跳过无 provider 任务关联的执行、非 video 格式拒绝（错误文案逐字）、通道类型回退、doubao 回退在本实现不受支持（C082）、DELETE 上游删除 + 本地 canceled、缺 external_id / channel_id / 通道 / 请求、执行查询失败包装、快照写失败非致命、状态映射表 | `tests/AxonHub.Api.Tests/VideoServiceTests.cs` | 23 |
| GET/DELETE 端点胶水：200 线路回写（status 归一化、秒数、进度、时间戳）、400 `invalid id`、500 错误信封（`ent: request not found`）、DELETE 204 与本地 canceled | `tests/AxonHub.Api.Tests/VideoTaskEndpointTests.cs` | 6 |
| POST /v1/videos 路由：空 body 400、缺 model / prompt → 400 `invalid_request_error`（`invalid request: …` 文案）、成功创建（上游 `/videos` 线路形状、metadata 回显的 model/prompt、Bearer、200 渲染） | `tests/AxonHub.Api.Tests/VideoEndpointTests.cs` | 4 |
| 持久化接缝：external_id + project 作用域查询（同 id 跨项目）、轮询快照更新、desc 排序（id 决定顺序）、缺行报告 | `tests/AxonHub.Persistence.Tests/RequestStoreTests.cs`、`RequestExecutionStoreTests.cs` | 3 |

合计 23 + 6 + 4 + 3 = 36 例：`AxonHub.Api.Tests` 由 586 增至 619，
`AxonHub.Persistence.Tests` 由 56 增至 59。

## 23. OpenAI 旧版 Completions 客户端变换器与路由接线（69 例，2026-11-01）

参照路线：`llm/transformer/openai/completion.go`、`completion_inbound.go`（287 行）、
`completion_outbound.go`（242 行）、`completion_aggregator.go`（108 行）与测试
`completion_inbound_cost_test.go`（2 个测试函数）、`routes.go` 的
`openaiGroup.POST("/completions")`、`internal/server/api/openai.go` 的
`Completion` → `CompletionHandlers.ChatCompletion`（复用通用 chat handler）、
`internal/server/biz/channel_llm.go:445-451`。端口新增统一模型
`CompletionRequest` / `CompletionResponse` / `CompletionChoice`
（`LlmRequest.Completion` / `LlmResponse.Completion`）、wire 模型
`OpenAiCompletionModels`、`CompletionInboundTransformer`、
`CompletionOutboundTransformer`（含 `##` RawURL 与 `#` 版本抑制、
`TransformOpenAIError` 复用）、`CompletionAggregator`（Go 字段序与
`"choices":null` 的专用 DTO）、`OpenAiRequestTypeCheck.ValidateCompletion`
（Go 字段类型错误文案）、`ChannelOutboundFactory` 的 openai/completions 分支与
`POST /v1/completions` 路由（复用 chat handler）。差异登记 C086。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 入站请求变换：全字段与父级公共字段复制、stop 字符串/数组、nil / 空 body / 默认 CT / 大小写 CT、model 与 prompt 校验序、JSON `null` 零值、语法错误与 Go 字段类型 / 数组根文案 | `tests/AxonHub.Llm.Tests/OpenAiCompletionTransformerTests.cs` | 13 |
| 入站响应变换：usage 与 cost 透传（参照测试逐条移植）、缺 usage 仍输出零值 usage、空 choices 输出 `[]`、错误信封保留状态、nil / 缺数据文案 | `tests/AxonHub.Llm.Tests/OpenAiCompletionTransformerTests.cs` | 6 |
| 入站流：`[DONE]`、仅 usage 分块（`"choices":[]`，参照测试）、Completion 与 Usage 皆无时过滤、NoNil 过滤、nil 文案 | `tests/AxonHub.Llm.Tests/OpenAiCompletionTransformerTests.cs` | 5 |
| 入站错误映射：nil → 500 `unexpected_error`、InvalidModel 422、HTTP 错误状态原样透传（不做 chat 的 502 归一化）、InvalidRequest 400、ResponseError 状态与 detail、未知错误 500 | `tests/AxonHub.Llm.Tests/OpenAiCompletionTransformerTests.cs` | 6 |
| 出站请求：线路字节精确（Go 字段序与 omitempty）、全字段复制、空 suffix / user 省略、URL 归一化表、`##` / `#` RawURL、自定义 EndpointPath、配置校验、nil / 缺 Completion / 缺 model 文案 | `tests/AxonHub.Llm.Tests/OpenAiCompletionTransformerTests.cs` | 17 |
| 出站响应：字段映射、token 门槛决定 usage、JSON `null` 零结构、nil / 空 body、≥400 走 `TransformOpenAIError`（含非 JSON body）、非法 JSON 与数组根文案 | `tests/AxonHub.Llm.Tests/OpenAiCompletionTransformerTests.cs` | 9 |
| 出站流：`[DONE]` → Done、空 data 过滤、分块解析、nil 事件 | `tests/AxonHub.Llm.Tests/OpenAiCompletionTransformerTests.cs` | 4 |
| 聚合：空分块零值字节串、首 id / model / created 与文本拼接、finish_reason 缺省 `stop`（completed 预先计算）、仅 `[DONE]` 零值、跳过不可解码分块 | `tests/AxonHub.Llm.Tests/OpenAiCompletionTransformerTests.cs` | 5 |
| API 路由胶水：`POST /v1/completions` 成功往返（上游 `/v1/completions` 线路形状、Bearer、200 渲染）、缺 prompt 400、空 body 400、非法 CT 400 | `tests/AxonHub.Api.Tests/CompletionsEndpointTests.cs` | 4 |

合计 13 + 6 + 5 + 6 + 17 + 9 + 4 + 5 = 65 例 Llm，加端点 4 例共 69 例：
`AxonHub.Llm.Tests` 由 1125 增至 1190，`AxonHub.Api.Tests` 由 619 增至 623。
C086 记录通道能力表未建模导致的 completion 出站分派近似。

## 24. TypeSafe SystemOne 客户端变换器与两条 `/systemone` 路由（65 例，2026-11-01）

参照路线：`llm/systemone.go`（SystemOneRequest / Question / Answer / Response 与
Answer 的自定义 UnmarshalJSON / MarshalJSON）、`llm/transformer/typesafe/model.go`、
`inbound.go`（156 行）、`outbound.go`（223 行）、`llm/pipeline/empty_response.go:125`
的 systemone / compact / completion 分支、`routes.go:233` 的
`openaiGroup.POST("/systemone")` 与 `:244` 的 `typesafeGroup.POST("/systemone")`、
`internal/server/api/typesafe.go` 的 `TypeSafeHandlers.SystemOne`（复用共享 chat
handler，只替换入站变换器）、`internal/server/orchestrator/systemone_test.go`
（4 个测试函数）与 `internal/server/orchestrator/pass_through.go`。端口新增统一模型
`SystemOneRequest` / `SystemOneQuestion` / `SystemOneAnswer` / `SystemOneResponse`
（`Model/SystemOne.cs`：Answer 的无损 Extra、字段名大小写不敏感匹配但只对精确小写名
做删除、无 Extra 走结构序 / 有 Extra 走 map 序两种 marshal 形态、`GoAnyJson` 复刻
Go `any` 的键排序/重复键/数字字面量语义、`SystemOneJson` 把 null map 值归零）、
wire 模型 `TypeSafeModels`、入站 `SystemOneInboundTransformer`（Decoder 语义：
EOF / unexpected EOF / 尾随数据忽略 / 根类型错误、stream 拒绝、model → state →
questions 校验序、错误信封）、出站 `TypeSafeOutboundTransformer`（默认 BaseURL、
v1 归一、EndpointPath、`typesafe/systemone` APIFormat、Bearer、`ResponseError`
Cause 链、错误信封解析与原文回退）、`LlmRequest.SystemOne` / `LlmResponse.SystemOne`、
`hasResponseContent` 的 systemone / compact / completion 分支、
`ChannelOutboundFactory` 的 typesafe 分派与两条共用同一 handler 的路由。差异登记 C087。
顺带修正：`/jina/v1` 组此前未进入 IP 黑名单 / API key 鉴权分支，本次与 `/typesafe/v1`
一并接入（参照的所有原生组都挂在同一个带中间件的 apiGroup 下）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 统一模型：请求/响应解码与 marshal、Choice 线路形状、Answer 的 Extra 保真（未知字段、大小写变体同时置字段并保留原拼写）、无 Extra 的结构序、`any` 的键排序与 64 位整数/长小数字面量保真、Question 未知字段与 JSON null 零值、null map 值归零 | `tests/AxonHub.Llm.Tests/TypeSafeSystemOneTests.cs` | 8 |
| 入站请求变换：合法请求保留 `9007199254740993` 字面量与 RequestType/ApiFormat/RawRequest、nil / 空 body / 空白（EOF）/ 截断（unexpected EOF）/ 尾随数据忽略 / 数组根（`typesafe.systemOneWireRequest`）/ 字段类型错误、`stream:true`、缺 model / state / questions、空 questions、JSON `null` body、`state:null` 视为缺失 | `tests/AxonHub.Llm.Tests/TypeSafeSystemOneTests.cs` | 15 |
| 入站响应与错误：noul + usage 线路形状、无 usage 省略、nil 响应 / 缺 SystemOne、TransformError 的 nil（500 原文）、ResponseError 状态与 detail、通用错误的 Go `%q` 引号、流式拒绝 | `tests/AxonHub.Llm.Tests/TypeSafeSystemOneTests.cs` | 8 |
| 出站请求：线路字节（model / state / questions，无 stream 成员）、BaseURL 版本归一、默认 BaseURL、EndpointPath 覆盖、nil 请求 / 缺 SystemOne / 缺 model、配置校验、ApiFormat | `tests/AxonHub.Llm.Tests/TypeSafeSystemOneTests.cs` | 9 |
| 出站响应：model / answers / usage 映射（120 / 0 / 120）、数字精度（`0.95`、`9007199254740999` 与 answer 全字段重编码）、HTTP ≥400 → ResponseError、空 body、非法 JSON、根 null 零结构、数组根类型错误、`answers:null` 保持 null | `tests/AxonHub.Llm.Tests/TypeSafeSystemOneTests.cs` | 8 |
| 出站错误与流：`{"error":{message,type,code}}` 解析与 `errors.As` Cause 链、nil → 500 `Internal Server Error` / `api_error`、非 JSON 原文、缺 message 回退原文、缺 code 省略、流式拒绝 | `tests/AxonHub.Llm.Tests/TypeSafeSystemOneTests.cs` | 6 |
| 空响应探测：systemone 仅在有 answers 时计为内容、compact 与旧版 completion 分支 | `tests/AxonHub.Llm.Tests/TypeSafeSystemOneTests.cs` | 2 |
| API 路由胶水：`POST /v1/systemone` 成功往返（上游 `/v1/systemone` 线路形状、state/questions、无 stream、Bearer、RequestType/ApiFormat）、`/typesafe/v1/systemone` 同一 handler、空响应探测保留非空 answers、空 answers 切换通道重试、429 换用下一通道 key、401 不重试、缺 model / 缺 state 的 500 `api_error` 文案、空 body 400 | `tests/AxonHub.Api.Tests/SystemOneEndpointTests.cs` | 9 |

合计 8 + 15 + 8 + 9 + 8 + 6 + 2 = 56 例 Llm，加端点 9 例共 65 例：
`AxonHub.Llm.Tests` 由 1190 增至 1246，`AxonHub.Api.Tests` 由 623 增至 632。
C087 记录通道能力表未建模、`JsonElement` 承载 `any` 与字段级解码文案差异（pass-through
body 的请求半程已由第 26 节闭合，响应半程见 C091）；`hasResponseContent` 的
compact / completion 分支由本节的空响应探测一并闭合。

## 25. 模型清单与检索端点（55 例，2026-11-01）

参照路线：`internal/server/api/openai.go:575-917`（`OpenAIModel` / `Modalities` /
`Capabilities` / `Pricing`、`parseOpenAIModelInclude`、`convertModelFacadeToOpenAIModel`、
`convertModelToOpenAIExtended`、三个 `writeOpenAI*Error`、`RetrieveModel`、`ListModels`）、
`internal/server/api/anthropic.go:76-138`（`AnthropicModel` 与 `ListModels`）、
`internal/server/biz/model.go:646-743`（`ModelService.ListEnabledModels`）、
`internal/server/biz/system.go:1237-1291`、`system_default.go:91-99` 与
`model_settings_inheritance.go:12-32`（`ModelSettingsOrDefault` / `normalizeSystemModelSettings`）、
`internal/pkg/xregexp/match.go`、`internal/server/middleware/logging.go`（请求 ID 半）、
`routes.go:209-210` 的 `GET /v1/models`、`GET /v1/models/*model` 与 `:250` 的
`GET /anthropic/v1/models`。端口新增 `XRegexp`（`*` 通配、无元字符即序数精确比较、
其余编译为 `^(?:…)$` 并保留前置 `(?i)` 修饰符、编译失败永久 false、进程级缓存）、
`ModelSettings` 与 `SystemModelSettingsService`（`system_model_settings` 的 Go 语义解码：
缺行 / 读失败 / JSON 错误回退默认值，字面 `null` 与 `{}` 保持零值）、`ModelFacade` 与
`ModelService.ListEnabledModels`（按 `ChannelModelEntries` 的请求模型生成、
`ModelBlacklistRegex` 只作用于通道派生 ID、跨通道去重）、`ModelsEndpoint`（手写 Go 字节：
OpenAI 列表 `{"data":[…],"object":"list"}` 与检索对象、Anthropic 列表的
`data/first_id/has_more/last_id/object` 与 `time.Time` 的 RFC3339Nano、
`ResponseError` / `AnthropicError` 错误信封）、`RequestTracingMiddleware`
（`ar-<uuid>`、`server.trace.request_header` 默认 `AH-Request-Id`、写入请求上下文供错误体回显）、
`GoTime`（RFC3339Nano / Unix）、`ChannelDescriptor.CreatedAt` 与其持久化映射，并把
`ChannelModelEntries` 从 `AxonHub.Grains` 提到 `AxonHub.Abstractions` 共用
（顺带修正 `supported_models` 中 JSON `null` 元素按 Go 语义变为 `""`）。
差异登记 C088–C090。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| `time.Time` 线路形态：整秒 / 毫秒 / 微秒的有效位、零值、Unspecified 视为 UTC、Unix 截断与零值 `-62135596800` | `tests/AxonHub.Llm.Tests/GoTimeTests.cs` | 7 |
| xregexp：`*`、精确匹配及其大小写敏感、双向锚定与自带的 `^…$`、`(?i)` 修饰符保留、`(?:…)` 属主体、非法正则恒 false（含缓存）、空模式 | `tests/AxonHub.Api.Tests/XRegexpTests.cs` | 8 |
| 系统模型设置：缺行 / 读失败 / 软删 / 畸形 JSON 回退默认值、缺成员保持 Go 零值、字面 `null`、`{}`、大小写不敏感键、未知成员忽略、`developer_settings` 归一化（trim、null 元素跳过、associations 原样） | `tests/AxonHub.Api.Tests/SystemModelSettingsServiceTests.cs` | 15 |
| ListEnabledModels：facade 字段与排序、前缀 / 映射别名、跨通道去重取首通道、黑名单正则与无元字符精确匹配、`QueryAllChannelModels=false` 为空、`null` 支持模型变空 ID | `tests/AxonHub.Api.Tests/ModelServiceTests.cs` | 7 |
| 线路形状：OpenAI 列表 / 空列表 / HTML 转义、尾斜杠 404、检索（含 `deepseek/chat`）、模型不存在与空 ID 文案、`request_id` 回显、500 信封；Anthropic 列表 / 空列表 / 500（`request_id` 空与回显） | `tests/AxonHub.Api.Tests/ModelsEndpointTests.cs` | 14 |
| 请求 ID 中间件：`ar-{uuid}` 格式与响应头回显、处理管线可见同一 ID、自定义头名、每请求不同、无中间件时为空 | `tests/AxonHub.Api.Tests/RequestTracingMiddlewareTests.cs` | 4 |

合计 7 + 8 + 15 + 7 + 14 + 4 = 55 例：`AxonHub.Llm.Tests` 由 1246 增至 1253，
`AxonHub.Api.Tests` 由 632 增至 680。C088 记录 `models` 表与配置模型、`include`
扩展字段、API Key profile 与 privacy 403 的裁剪（没有 `models` 行时两条实现的响应
字节完全一致）；C089 记录 trace ID 链路未移植；C090 记录路由边界与顺序确定化。

## 26. pass-through 通道重放（请求半程，95 例，2026-11-01）

参照路线：`internal/server/orchestrator/pass_through.go`（`passThroughStreamAligned`、
`isPassThroughEnabled`、`applyPassThroughRequestBody`、
`mergePassThroughRequestBodyWithPromptProtection`、`passThroughBodySupported`、
`passThroughBodyNeedsModelPatch`、`applyPassThroughRequestHeaders`、
`applyUserAgentPassThrough`）、`orchestrator.go:260-310`（中间件注册顺序：
pass-through body → pass-through headers → override body → user agent → override
headers → finalize transport）、`internal/objects/channel.go:214-226`
（`settings.passThroughUserAgent` / `settings.passThroughBody`）、
`internal/server/biz/system.go:108,116`（`system_user_agent_pass_through` /
`system_pass_through`）、`llm/transformer/interfaces.go:64-67`
（`PassThroughBodyPolicy`）以及 `internal/transformer/{decisions,typesafe}` 的两个
`AllowPassThroughBody` 实现。参照测试 `pass_through_test.go`、
`pass_through_budget_test.go` 与 `pass_through_codex_test.go` 逐条改写为端口测试；
请求半程见本节，响应/流半程见第 27 节，提示词保护
（`pass_through_prompt_protection.go` 与两个 `_test.go`）见第 28 节。

端口新增：`ChannelPassThroughSettings`（`settings` JSON 的 Go 语义解码：键大小写
不敏感、重复键后者胜出、`null` / 非布尔类型视为继承、畸形 JSON 降级为两者均继承）、
`ChannelCandidate.PassThroughSettings`（grain 候选快照携带通道级开关）、
`SystemPassThroughSettingsService`（`system_pass_through` /
`system_user_agent_pass_through` 两行，缺行、软删、非 `"true"` 值、读失败均为关；
`IPassThroughSettingsProvider` 为测试保留静态实现）、`PersistentOutboundTransformer`
的 `RawProviderRequest` / `OriginalRequestStream` / `PassThroughApplied` 状态
（在 `NextChannel` 与 `PrepareForRetry` 复位，跟随 `request_executions.pass_through_applied`
落库）、`PassThroughMiddlewares.cs`（三个中间件与 `PassThrough` 静态端口）、
`IPassThroughBodyPolicy`（decisions 恒真、typesafe 仅 `RequestType=SystemOne`），
以及 `ChatCompletionOrchestrator` 的每请求设置快照和参照顺序的中间件注册。
sjson 的空文档语义单独处理：端口先按 Go `sjson` 的做法把空白 body 归一化为 `{}`
再写 model（`MergeTreatsBlankBodyLikeSjson` 锁定）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 开关判定：通道级覆盖全局、通道 `false` 覆盖全局 `true`、通道缺省继承、缺 `RawProviderRequest` / 空 APIFormat / 格式不一致 / 强制流式均关闭；`passThroughStreamAligned` 的 Go 真值相等（nil 与 false 等价、8 组组合） | `tests/AxonHub.Api.Tests/PassThroughTests.cs` | 17 |
| body 重放与 model 补丁：原始 body 复制、映射后 model 写回、10 种 JSON 路由格式、格式不一致保留出站 body、流参数变化跳过、非补丁格式保留原始 body、moderation / decisions / jina 补丁、空 model 不补丁、空白 body 视作 `{}` | `tests/AxonHub.Api.Tests/PassThroughTests.cs` | 31 |
| 格式支持与策略：transcription / translation / image_variation / multipart image_edit / multipart video 拒绝，JSON image_edit 与 speech 允许；`AllowPassThroughBody` 拒绝与放行 | `tests/AxonHub.Api.Tests/PassThroughTests.cs` | 12 |
| Codex 头：仅 responses 且 body 已重放时转发 8 个白名单头、非 responses / 未重放不转发、同名头先删后加不追加 | `tests/AxonHub.Api.Tests/PassThroughTests.cs` | 4 |
| User-Agent 策略：通道 / 全局开关、客户端 UA 复制、关闭且出站为空写 `axonhub/1.0`、provider 自带 UA 保留、无候选不动 | `tests/AxonHub.Api.Tests/PassThroughTests.cs` | 8 |
| 通道 settings 解码：显式值、显式 `false` 不算继承、键大小写与重复键、`null` / 错类型继承、6 种不可读 JSON 降级 | `tests/AxonHub.Api.Tests/PassThroughTests.cs` | 11 |
| 系统设置 provider：缺行全关、`"true"` 开启、其余值关闭、读失败关闭、软删视作缺行 | `tests/AxonHub.Api.Tests/PassThroughTests.cs` | 8 |
| 编排端到端（真实 pipeline + 假上游）：通道开启时重放客户端 body 且保留额外顶层字段、关闭时发送变换后 body、全局开关开启无覆盖通道、关闭时默认 UA | `tests/AxonHub.Api.Tests/PassThroughTests.cs` | 4 |

合计 17 + 31 + 12 + 4 + 8 + 11 + 8 + 4 = 95 例：`AxonHub.Api.Tests` 由 680 增至
775，`AxonHub.Llm.Tests` 保持 1253，`AxonHub.Grains.Tests` 保持 88。C091 记录请求
与响应/流半程已接入、提示词保护未移植，并取代 C087 中 pass-through body 缺口
的描述、修正 C014 中通道 settings 的 passThrough 开关未实现的表述。

## 27. pass-through 响应与流重放（26 例，2026-11-01）

参照路线：`internal/server/orchestrator/pass_through.go` 的响应半程
（`captureRawProviderResponse`、`applyPassThroughResponse`、
`captureRawProviderStream`、`applyPassThroughStream`、`passThroughChannelStream`、
`rawStreamBacklog`、`resetPassThroughStreamState`）、`outbound.go:823,832`
（两个重试入口复位流状态）、`orchestrator.go:258-263,306-309`（apply 响应/流注册在
`persistRequest` 之前；capture 响应/流最后注册，反向最先执行）以及
`pass_through_test.go` / `pass_through_budget_test.go` 的响应半程用例。提示词保护
（`pass_through_prompt_protection.go`）见第 28 节。

端口新增：`PassThroughStreamMiddlewares.cs` 的四个中间件与支撑类型
`PassThroughStreamState`（原始通道、backlog、per-attempt 错误引用、取消委托）、
`PassThroughErrorRef`（首次终态错误胜出，旧尝试的 goroutine 不能写入新尝试）、
`RawStreamBacklog`（attach 前最多 1024 事件 / 8 MiB，字节数含 Type 与 LastEventID，
超限错误 `%w` 包裹 `PreCommitBufferExceeded` 以触发不重试语义）、
`PassThroughChannelStream`（有界通道的流视图：取消后先非阻塞排干缓冲，空缓冲即关闭上游，
避免客户端断开时丢失已缓冲的 `[DONE]`）、扇出线程（`BlockingCollection` 双通道
容量 64、`TaskCreationOptions.LongRunning`、阻塞发送，`AsyncLocal` 不参与）、
`PassThroughAttemptCloser`（`sync.Once` + `WithCancel` 语义：幂等取消并关闭上游，
finally 中释放关联 CTS）。`PersistentOutboundTransformer` 增加
`RawProviderResponse` / `PassThroughStream` 与 `ResetPassThroughStreamState()`，
在 `NextChannel` 与 `PrepareForRetry` 调用；`ChatCompletionOrchestrator` 按参照顺序
注册四个中间件。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 非流响应捕获与应用：启用时保存并返回原始响应、关闭时不保存、应用端关闭/格式不一致/无尝试/未捕获时回退变换后响应 | `tests/AxonHub.Api.Tests/PassThroughStreamTests.cs` | 7 |
| 原始流捕获：关闭 / 无尝试 / 格式不一致返回原流；100 个预读事件经 backlog 扇出到 pipeline 与客户端；上游错误传播到流与状态；Dispose 关闭阻塞的上游并完成原始通道 | `tests/AxonHub.Api.Tests/PassThroughStreamTests.cs` | 6 |
| 原始流应用：关闭 / 无原始通道返回原流；返回原始事件；attach 前事件前置保序；原始错误透传；内层变换流在后台被读完 | `tests/AxonHub.Api.Tests/PassThroughStreamTests.cs` | 6 |
| `PassThroughChannelStream`：取消后先排干缓冲（含 `[DONE]`）；空缓冲立即结束并取消上游，不阻塞等待生产者 | `tests/AxonHub.Api.Tests/PassThroughStreamTests.cs` | 2 |
| `RawStreamBacklog`：1024 事件上限与 attach 后放行；8 MiB 字节上限含 Type 与 LastEventID，错误包裹 `PreCommitBufferExceeded` | `tests/AxonHub.Api.Tests/PassThroughStreamTests.cs` | 2 |
| 真实 pipeline 集成：启用时非流结果替换为原始响应、关闭时保留变换后响应；流式请求客户端收到原始事件且被丢弃的变换流仍经过 LLM 中间件 | `tests/AxonHub.Api.Tests/PassThroughStreamTests.cs` | 3 |

合计 7 + 6 + 6 + 2 + 2 + 3 = 26 例：`AxonHub.Api.Tests` 由 775 增至 801，
`AxonHub.Llm.Tests` 保持 1253，`AxonHub.Grains.Tests` 保持 88；合计 2142。C091
更新为请求与响应/流半程均已接入，codex 策略仍为缺口（提示词保护见第 28 节）。

## 28. pass-through 提示词保护（71 例，2026-11-01）

参照路线：`internal/server/orchestrator/prompt_protection.go`（`protectPrompts` 入站中间件、
`promptProtectionRejectedMessage`、`promptProtectionBodyCheck` 与 `promptProtectionTexts`）、
`internal/server/biz/prompt_protection_request.go`（`ApplyPromptProtectionRules` 的
Messages / Decisions / Compact 三路分派、作用域过滤、掩码与拒绝语义）、
`biz/prompt_protection_rule.go`（`MatchPromptProtectionRule` / `ReplacePromptProtectionRule`
与正则缓存）、`orchestrator/pass_through_prompt_protection.go`
（`rawPromptTextFields` 的五种线路格式、`mergePassThroughRequestBodyWithPromptProtection`、
Gemini `functionResponse.response` 的 `UseNumber` 递归掩码、body 回放校验）以及
`prompt_protection_rule_test.go`、`prompt_protection_request_test.go`、
`prompt_protection_test.go`、`pass_through_prompt_protection_test.go`、
`pass_through_prompt_protection_payload_test.go` 的参照用例。

端口新增：`PromptProtectionModels`（规则 / 设置 / 结果 / 作用域）、`PromptProtectionRegex`
（进程级正则缓存，编译失败也缓存，匹配/替换异常视为不匹配）、`PromptProtectionJson`
（gjson 语义的 `Exists` / `IsString` / `IsNull` / `ArrayLength` / `String`，`#` 仅数组长度）、
`PromptProtectionRules.Apply`（Messages：标量内容整体替换或仅掩码 `text` 分片；
Decisions：`input` 字符串与 `input_text` 分片；Compact：`instructions` 当 system、
`input` 当消息且写回 `Input = view.Messages[inputOffset..]`；空作用域=全部角色、
角色小写精确比较；拒绝返回 `MatchedRules` + `Rejected`）、`PromptProtectionRuleService` /
`PromptProtectionRuleProtector`（`IPromptProtecter` 与 `IPromptProtectionResultProvider`
双接缝，拒绝抛 `PromptProtectionRejectedException`）、`PromptProtectionTexts` /
`PromptProtectionBodyCheck`（按入站映射回放原始 body 并比较提示词快照）、
`ProtectPromptsMiddleware`（拒绝映射为 `ErrInvalidRequest` 的固定文案，其他失败保留原请求；
记录 `PromptProtectionMaskRules`；legacy protector 以文本快照差异决定是否启用回放校验；
Decisions 格式跳过校验）、`PassThroughPromptProtection`（按 APIFormat 分派的原始文本字段
收集器、逗号分隔的 `Text` 分片类型、Anthropic `tool_result` 嵌套 content、Gemini
role 归一与结构化 functionResponse、按 id 顺序的掩码规则；无字段命中即失败，回退生成的
已保护 body）。`PassThroughRequestBodyMiddleware` 在 model 补丁后应用掩码，
`PromptProtectionBodyCheck.Validate` 失败时同样回退生成 body；
`ChatCompletionOrchestrator` 每请求从 `IPromptProtectionRuleProvider` 取规则快照，
读取失败失败开放。顺带把 `ApiFormats.OpenAIResponseCompact` 修正为参照的
`openai/responses_compact`（原端口误用 HTTP 路径 `/responses/compact`），使压缩请求的
pass-through 补丁分派与 `request_executions.api_format` 与参照一致。规则读取无 30 秒
live 缓存、管理面/预览缺失、.NET Regex 语义与 Gemini/Ollama 载荷级对照延后登记为 C092。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 统一规则应用：Messages 掩码/拒绝/作用域/多规则顺序/多内容分片/未命中、无 settings 跳过、空请求/空规则、非法正则恒不匹配；Decisions 字符串与分片；Compact 的 instructions/input 隔离与拒绝 | `tests/AxonHub.Api.Tests/PromptProtectionTests.cs` | 18 |
| `ProtectPromptsMiddleware`：掩码、拒绝固定文案且不泄露规则、nil 结果保留原请求、legacy protector 启用回放校验、无变化不启用、Decisions 跳过校验、非拒绝异常保留原请求、无 protector 直通、RawRequest 保留 | `tests/AxonHub.Api.Tests/PromptProtectionTests.cs` | 11 |
| 规则服务：记录映射、空/空白/`null` settings、非法 settings 失败、读失败失败、空快照、掩码应用、拒绝哨兵 | `tests/AxonHub.Api.Tests/PromptProtectionTests.cs` | 9 |
| 原始 body 补丁：五种格式的文本路径、Responses Compact、未支持格式、Gemini functionResponse 的键/数字保真、Ollama chat 布局、规则未命中字段、缺失/对象 content 的 chat 布局、非法 JSON、model 补丁与规则合并 | `tests/AxonHub.Api.Tests/PassThroughPromptProtectionTests.cs` | 13 |
| 真实编解码回放：OpenAI chat / Responses / Compact / Anthropic / Decisions 的 10 组参照载荷只发送受保护文本且保留额外字段、映射后 model 与 Content-Type；legacy protector 无法恢复明文；Compact 拒绝在构造上游 body 前阻断 | `tests/AxonHub.Api.Tests/PassThroughPromptProtectionTests.cs` | 13 |
| body 校验：无入站映射、解码失败固定文案、快照一致通过、提示词不同拒绝 | `tests/AxonHub.Api.Tests/PassThroughPromptProtectionTests.cs` | 4 |
| 编排端到端：掩码后的重放 body 到达传输层、拒绝请求不触达传输层、规则读取失败失败开放 | `tests/AxonHub.Api.Tests/PassThroughPromptProtectionTests.cs` | 3 |

合计 18 + 11 + 9 + 13 + 13 + 4 + 3 = 71 例：`AxonHub.Api.Tests` 由 801 增至 872，
`AxonHub.Llm.Tests` 保持 1253，`AxonHub.Grains.Tests` 保持 88；三个项目合计 2213
（含 Docker 依赖的 `AxonHub.Persistence.Tests` 59 例则为 2272）。C092 登记
规则每请求读库（无 30 秒 live 缓存与启动期 panic）、管理面/GraphQL/预览未移植、
.NET Regex 与 regexp2 的语义差异、Gemini/Ollama 无编解码器导致载荷级对照延后，
并关闭 C091 中的提示词保护缺口（codex 出站 `AllowPassThroughBody` 仍在 C091）。

## 29. Codex 出站、OAuth 凭据与通道接线（111 例，2026-11-01）

参照路线：`llm/transformer/openai/codex/`（`outbound.go` 的 TransformRequest /
TransformResponse / TransformStream / AggregateStreamChunks、`AllowPassThroughBody`
的 token 上限拒绝、`CustomizeExecutor` / `FinalizeTransportRequest`、TurnMetadata 与
Session/Thread/Window 头、工具与图片工具注入、Responses Lite 语义；`headers.go`、
`models.go`、`alpha_search.go`、`token.go` 的 JWT 账号解析与 `DecodeAuthJson`、
`utils.go`、`constants.go`）、`llm/oauth/`（`credentials.go` 的凭据形状与解析校验顺序、
`token_provider.go` 的单飞刷新 / EnsureFresh / 自动刷新、`exchange_strategy.go`、
`pkce.go`、`device_flow_provider.go`），以及 `internal/server/biz/channel_llm.go` 的
`buildCodexOutbound` 与 `buildChannelWithOutbounds` 的 codex/fenno 分派。参照测试：
`outbound_executor_test.go`、`codex_simulator_test.go`、`outbound_additional_tools_test.go`、
`alpha_search_test.go`、`token_test.go`、`token_provider_test.go`、`channel_llm_fenno_test.go`
与 `llm/oauth/*_test.go`。

端口新增：`AxonHub.Llm/Transformer/OpenAI/Codex`（`CodexOutboundTransformer`、
`CodexHeaders`、`CodexModels`、`CodexToken`、`CodexAlphaSearch`、`CodexUtils`、
`CodexConstants`）、`AxonHub.Llm/OAuth`（`OAuthModels`、`OAuthCredentialsJson`、
`TokenProvider`、`ExchangeStrategy`、`Pkce`、`DeviceFlowProvider`）、
`AxonHub.Llm/Concurrency/SingleFlight`。`ChannelOutboundFactory` 增加 codex/fenno
分派与 `CreateCodexOutbound` / `CreateCodexTokenProvider`：oauth 对象（优先）或旧版
apiKey 内嵌 OAuth JSON 构造刷新型 `TokenProvider`，API key 通道构造
`ApiKeyTokenProvider`，ws/wss base URL 推断 websocket transport，`default_test_model`
作为图片主模型；缺凭据时 codex/fenno 报
`missing credentials: oauth or api key required for channel <name>`。
`ChannelPoolGrain` 按 `buildCodexOutbound` 的方式 Trim 并 `ChooseModel` 解析候选的
`DefaultTestModel`（`lowercaseModelId` 小写查找，未支持保留 Trim 值）；
`ChatCompletionOrchestrator` 把共享 executor 传给工厂作为令牌刷新通道；
`PersistentOutboundTransformer` 补上 `SupportsCodexResponseHeaders` 接缝（当前
transformer 为 codex 时恒真）。接线测试同时暴露并修复了
`ChannelCredentials.HasNonEmptyAccessToken` 只识别 Go 字段名 `accesstoken` 的问题：
Go 解码按字段名或 JSON tag 大小写不敏感匹配，`{"oauth":{"access_token":"..."}}`
必须判定为 OAuth 凭据。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| Codex 请求变换（模型/消息/identity 头、reasoning 默认、数组输入、工具与 additional tools 作用域、Responses Lite）、pass-through body 的 token 上限拒绝、图片生成/编辑工具、响应与流聚合、`CustomizeExecutor` 的非流聚合 / alpha-search 非流式 / WebSocket 绕过 / JSON 直通 / relay SSE 聚合 / WebSocket 错误事件、`FinalizeTransportRequest` 的 WebSocket beta 剥离与 continuation 字段保留 | `tests/AxonHub.Llm.Tests/CodexOutboundExecutorTests.cs` | 39 |
| Codex 模拟器身份头：最小头、入站 identity 覆盖、现代 codex 头直通、非 codex 入站默认值、session id 的四种来源与 UUID 回退 | `tests/AxonHub.Llm.Tests/CodexSimulatorTests.cs` | 10 |
| additional tools 作用域：官方 backend（含无 scheme）保留、三种中继域名形态移除 | `tests/AxonHub.Llm.Tests/CodexAdditionalToolsTests.cs` | 6 |
| alpha search session id 优先级：header > body，body > context | `tests/AxonHub.Llm.Tests/CodexAlphaSearchTests.cs` | 3 |
| `DecodeAuthJson`：JWT 账号信息解析与畸形输入 | `tests/AxonHub.Llm.Tests/CodexTokenTests.cs` | 1 |
| codex token provider：未过期不回调、过期刷新后回调 | `tests/AxonHub.Llm.Tests/CodexTokenProviderTests.cs` | 2 |
| OAuth 凭据文档：解析校验顺序（空文档 → access_token → refresh_token/expires_at 估算）、字段序与零值、token response 错误信封优先级、token 端点错误文案 | `tests/AxonHub.Llm.Tests/OAuthCredentialsJsonTests.cs` | 8 |
| OAuth token provider：Get / EnsureFresh / 提前 3 分钟过期窗口 / OnRefreshed | `tests/AxonHub.Llm.Tests/OAuthTokenProviderTests.cs` | 4 |
| 自动刷新：启停幂等、后台 Task 生命周期 | `tests/AxonHub.Llm.Tests/OAuthTokenProviderAutoRefreshTests.cs` | 2 |
| 设备码流程：设备授权请求、轮询节奏、slow_down / authorization_pending / 拒绝与超时 | `tests/AxonHub.Llm.Tests/OAuthDeviceFlowProviderTests.cs` | 31 |
| PKCE：verifier/challenge 生成与 S256 向量 | `tests/AxonHub.Llm.Tests/OAuthPkceTests.cs` | 5 |
| 通道接线：codex/fenno 的 7 组 APIFormat 分派、fenno 不支持 alpha search、fenno 的 `/v1/responses` 与 bearer、API key/OAuth 两种 token provider、旧版 apiKey 内嵌 OAuth、oauth 对象优先、两种缺凭据文案、`SupportsCodexResponseHeaders` 只对 codex 为真、ws/wss 与 HTTP 的 transport 推断 | `tests/AxonHub.Api.Tests/CodexChannelWiringTests.cs` | 20 |
| `default_test_model`：Trim、`ChooseModel` 映射、`lowercaseModelId`、未知模型保留 Trim 值 | `tests/AxonHub.Grains.Tests/ChannelSelectionTests.cs`（`DefaultTestModelIsTrimmedAndResolvedThroughModelEntries`） | 4 |

合计 61 + 50 = 111 例（另 Api 20 例、Grains 4 例）：`AxonHub.Llm.Tests` 由 1253 增至
1364，`AxonHub.Api.Tests` 由 872 增至 892，`AxonHub.Grains.Tests` 由 88 增至 92，
`AxonHub.Persistence.Tests` 保持 59；四个项目合计 2407。C093 登记 OAuth 的时间解析、
JSON 解码文案、日志与单飞原语差异；C094 登记 codex 的端点表、token provider 复用/
持久化/自动刷新、代理感知 client、API 层 codex 响应头转发等真实缺口，并关闭 C091
中的 codex `AllowPassThroughBody` 缺口。

## 30. trace 子系统基础（60 例，2026-11-01）

参照路线：`internal/tracing/tracing.go`（配置、`GenerateTraceID` / `GenerateRequestID`、
`AH-*` 头名回退）、`internal/server/middleware/logging.go`（`WithLoggingTracing` 的请求 ID
与 trace ID 生成）、`internal/server/middleware/trace.go`（`WithTrace` 的全部提取与持久化
分支）、`internal/server/biz/trace.go`（`GetOrCreateTrace`）、`internal/ent/schema/trace.go`
与 `internal/ent/migrate/schema.go`（`traces` 表与四个索引）、`conf/conf.go:227-234`
（三个客户端标记默认开启）、`internal/server/routes.go:63`、`:185-192`（中间件顺序）、
`llm/transformer/opencode/headers.go`、`llm/transformer/openai/codex/headers.go` 的会话头
读取、`llm/transformer/anthropic/claudecode/userid.go` 的解析半程。参照测试：
`middleware/logging_test.go`、`middleware/trace_test.go`、`biz/trace_test.go`、
`claudecode/userid_test.go`。

端口新增：`TracingOptions`（`FromConfiguration` 绑定 `Server:Trace`，空头名回退
`AH-Thread-Id` / `AH-Trace-Id` / `AH-Request-Id`，三个客户端标记默认 true）、
`TraceMiddleware`（`GetTraceIdFromHeader`、`TryExtractTraceIdFromClaudeCodeRequestAsync`、
`TryGetTraceIdFromBodyAsync`、`IsNonMessageEndpoint`）、`ITraceStore` / `TraceStore`
（Dapper 读 + EF 写，唯一索引冲突后按 `(trace_id, project_id)` 重读）、`traces` 表的幂等
DDL 与 EF 模型、`TraceContext` / `ThreadContext` 容器接缝、`RequestIdentity.TraceId`。
接线：`ChatCompletionEndpoint` 把解析出的 trace id 写入 HTTP 请求的 `SessionId` 接缝
（等价 `shared.WithSessionID`），codex 出站经 `RawRequest.SessionId` 写入 `Session-Id`；
`RequestChainPersistenceMiddleware` 把 trace 行 id 写入 `requests.trace_id`；
`Program` 在 `/v1` 族分支按参照顺序挂载 `WithThread` → `WithTrace`（thread 半程见第 31 节）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| trace 配置与请求 ID：`ar-` / `at-` 生成、配置头名、trace 头优先、无中间件无请求 ID | `tests/AxonHub.Api.Tests/RequestTracingMiddlewareTests.cs` | 7 |
| `TraceMiddleware`：Claude Code 元数据（legacy / v2 / 两条路径 / 仅 POST）、OpenCode 三头与优先级、Codex 三来源与优先级、trace 头与 extra 头、body 字段（扁平 / 嵌套 / 首个命中 / 畸形 JSON）、无 trace 源不落库、日志专用 id 不落库、身份 / 线程传递、trace 行与身份发布、会话发布、响应别名裁空白、同 trace 幂等、非消息端点、持久化失败 fail-open、body 读错误 400 信封、`IsNonMessageEndpoint` 7 例 | `tests/AxonHub.Api.Tests/TraceMiddlewareTests.cs` | 38 |
| Claude Code 用户 id 解析：legacy、v2 JSON、空 session、非法输入、首尾空白 | `tests/AxonHub.Llm.Tests/ClaudeCodeUserIDTests.cs` | 5 |
| OpenCode 会话头：当前头、`X-Session-Id`、affinity、优先级、空白回退、trim、缺失 | `tests/AxonHub.Llm.Tests/OpenCodeHeadersTests.cs` | 7 |
| `TraceStore`：默认列与时间戳、thread 往返、幂等、跨项目唯一与约束后重读、失败后 context 可用、并发单行 | `tests/AxonHub.Persistence.Tests/TraceStoreTests.cs` | 6 |
| `traces` schema 与 EF 模型：7 列、4 个索引（`trace_id` 唯一）、11 表 / 30 索引（第 31 节增至 12 / 33） | `tests/AxonHub.Persistence.Tests/SchemaTests.cs`、`EfCoreModelTests.cs` | —（既有用例扩充） |
| wire 会话与请求行 trace id：codex 出站消费 `RawRequest.SessionId`；`RequestRecord.TraceId` 来自身份 | `tests/AxonHub.Api.Tests/CodexChannelWiringTests.cs`、`RequestChainPersistenceTests.cs` | 1（另 2 处断言） |

合计 60 例（Llm 12、Api 42、Persistence 6）：`AxonHub.Llm.Tests` 由 1364 增至 1376，
`AxonHub.Api.Tests` 由 892 增至 934，`AxonHub.Persistence.Tests` 由 59 增至 65，
`AxonHub.Grains.Tests` 保持 92；四个项目合计 2407 增至 2467。C089（请求 ID 与 trace ID
链路）闭合；C095 登记无日志与 operation name、gjson 数字归一化与路径语法、
trace 查询 / GC / GraphQL、claudecode / opencode 出站生成等残余差异
（thread 中间件由第 31 节、trace 粘性 key 由第 32 节关闭）；
C013 与 C062 的 cause 更新为「trace 子系统已落地、provider / Responses 出站接线仍缺」（C013 由第 32 节闭合）。
## 31. thread 中间件与 threads 表（12 例，2026-11-01）

参照路线：`internal/server/middleware/thread.go`（`WithThread` 读取配置头、按项目
get-or-create、失败仅记日志）、`internal/server/biz/thread.go`（`GetOrCreateThread` 的
`(thread_id, project_id)` 查询、创建与约束冲突重读）、`internal/ent/schema/thread.go` 与
`internal/ent/migrate/schema.go`（`threads` 表与三个索引，`thread_id` 全局唯一）、
`internal/server/routes.go:185-192`（`WithThread` 在 `WithTrace` 之前）。参照测试：
`middleware/thread_test.go`、`biz/thread_test.go`。

端口新增：`ThreadMiddleware`（gin `GetHeader` 的首值语义、无头 / 无身份 / 持久化失败
fail-open、`ThreadContext` 发布线程行 id）、`IThreadStore` / `ThreadStore`（Dapper 读 +
EF 写，唯一索引冲突后按 `(thread_id, project_id)` 重读并 detach 失败实体）、`threads` 表的
幂等 DDL 与 EF 模型。接线：`Program` 在 `/v1` 族分支按参照顺序挂载
`ThreadMiddleware` → `TraceMiddleware`，带 `AH-Thread-Id` 的请求现在把线程行 id 写进
`traces.thread_id`。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| `ThreadMiddleware`：默认头建行并发布 id、配置头名、缺失 / 空头跳过、无身份跳过、持久化失败 fail-open、多值取首、thread → trace 链路写入 `thread_id` | `tests/AxonHub.Api.Tests/ThreadMiddlewareTests.cs` | 7 |
| `ThreadStore`：默认列与时间戳、已存在行原样返回、跨项目 thread_id 唯一与约束后重读、失败后 detach 可复用、并发单行 | `tests/AxonHub.Persistence.Tests/ThreadStoreTests.cs` | 5 |
| `threads` schema 与 EF 模型：6 列、3 个索引（`thread_id` 唯一）、12 表 / 33 索引 | `tests/AxonHub.Persistence.Tests/SchemaTests.cs`、`EfCoreModelTests.cs` | —（既有用例扩充） |

合计 12 例（Api 7、Persistence 5）：`AxonHub.Api.Tests` 由 934 增至 941，
`AxonHub.Persistence.Tests` 由 65 增至 70，`AxonHub.Llm.Tests` 保持 1376，
`AxonHub.Grains.Tests` 保持 92；四个项目合计 2467 增至 2479。C095 的 thread 中间件缺口
关闭，残余为 trace 查询 / GC / GraphQL、claudecode / opencode 出站生成与日志接缝
（trace 粘性 key 由第 32 节关闭）。

## 32. trace 粘性 API key 选择（8 例，2026-11-01）

参照路线：`internal/server/biz/channel_apikey_provider.go`（`TraceStickyKeyProvider.Get`：启用集合为空
回退 `Credentials.APIKeys[0]`、单 key 直取、有 trace 时 `rendezvousSelect` over `traceID|key` 并写入
1024 项 LRU、无 trace 随机选择）、`channel_llm.go:169-184`（`getAPIKeyProvider` 的 trace 分支）、
`middleware/trace.go:168-173`（`contexts.WithTrace` 与 `shared.WithSessionID`）。参照测试：
`biz/channel_apikey_test.go` 的 20 个选择/哈希用例。

端口新增：`ChannelApiKeyProvider` 增加 trace id 参数与 rendezvous 分支；`ChannelOutboundFactory`
从 `request.RawRequest.SessionId` 取 trace id（`TraceMiddleware` 发布、`ChatCompletionEndpoint`
写入的同一个值）。同一 trace 在重试与后续请求间固定同一 key，无 trace 仍随机，单 key 与全禁用
回退不变；`channel_api_key_index` 仍按全量 key 计算。参照的 1024 项 LRU 不单独保留：它只记忆固定
启用集合内的 rendezvous 结果，而通道重建会新建 provider 与空缓存，因此没有额外的可观察效果。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| trace 分支：rendezvous 结果、同 trace 跨次稳定、不同 trace 分散、单 key / 全禁用不受影响、审计索引、空 trace 随机 | `tests/AxonHub.Api.Tests/ChannelApiKeyProviderTests.cs` | 7（既有 16 例扩充至 23） |
| 端到端接线：同一 trace 两次请求发送同一 Authorization key | `tests/AxonHub.Api.Tests/ChatCompletionOrchestratorTests.cs` | 1 |

合计 8 例（Api 8）：`AxonHub.Api.Tests` 由 941 增至 949，`AxonHub.Llm.Tests` 保持 1376，
`AxonHub.Persistence.Tests` 保持 70，`AxonHub.Grains.Tests` 保持 92；四个项目合计 2479 增至 2487。
C013 闭合（残余 apiKeyOverride 与 trace_sticky_mode 通道粘性）；C095 的 trace 粘性 key 项关闭。

## 33. ClaudeCode / DeepSeek / OpenCode 出站与通道接线（154 例，2026-11-01）

参照路线：`llm/transformer/anthropic/claudecode/outbound.go`（`TransformRequest` 的
UA 捕获与清理、`proxy_` 前缀、系统消息注入、伪造 user id、13 个头与 `beta=true`，
`TransformResponse` 的 `strip_tool_prefix` 条件剥离，`TransformStream` 的逐行去前缀与
`AggregateStreamChunks` 只处理 `tool_use` 块）、`claudecode/utils.go` 与 `userid.go`
（legacy / v2 两种用户 id，`BuildUserID` 的 SHA-256 设备 id 与 URL 命名空间的 UUID v5）、
`llm/transformer/deepseek/outbound.go`（URL 归一、`/beta/completions` 分派、`json_schema`
降级为 `json_object`、`thinking` 开关与 `reasoning_content` 填充）、
`llm/transformer/opencode/{outbound,headers}.go`（模型路由、`WithSessionHeader` 的能力保留、
`resolveSessionID` 的三级回退）、`internal/server/biz/channel_llm.go:225-260,915-995`
（claudecode 的 OAuth / api-key 两路与 opencode 的三种通道类型）。参照测试：
`claudecode` 包测试、`deepseek/outbound_test.go`、`opencode/outbound_test.go`。

端口新增：`ClaudeCodeOutboundTransformer`（构造、`TransformRequest` / `TransformResponse` /
`TransformStream` / `AggregateStreamChunks`、`toolPrefixStripperStream`、`RemoveBillingSystemMessages`）、
`ClaudeCodeUserID`（解析 / 生成 / v5）、`DeepSeekOutboundTransformer`（chat 与 completion 两个子变换器）、
`OpenCodeOutboundTransformer`（路由元数据、按 metadata 的响应 / 流分派）、`OpenCodeSessionHeader`
（两种包裹形态）；`ChannelOutboundFactory` 增加 claudecode（OAuth → `ClaudeCodeTokenProvider` +
`IsOfficial=true`；api-key → `ApiKeyTokenProvider` + `IsOfficial=false`）、`opencode_go` 与
`opencode_go_anthropic` 分支。差异与残余缺口登记为 C096：会话值仍走 `RawRequest.SessionId`
（C062）、OAuth provider 每次尝试新建（C094）、计费系统消息移除中间件无注册点、opencode
用户端点与 `Stop` 转发未接线、claudecode 执行 key 审计列为空。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| `claudecode` 工具前缀与流处理：`stripToolPrefix`、`toolPrefixStripperStream`、`AggregateStreamChunks` 的 `tool_use` 过滤、计费系统消息移除 | `tests/AxonHub.Llm.Tests/ClaudeCodeUtilsTests.cs` | 55 |
| `ClaudeCodeOutboundTransformer`：api-key / OAuth 两路的头与 URL、UA 透传、`proxy_` 前缀、系统消息与 user id 注入、`beta`、`strip_tool_prefix`、响应条件剥离、流式逐行去前缀、错误信封 | `tests/AxonHub.Llm.Tests/ClaudeCodeOutboundTransformerTests.cs` | 23 |
| 用户 id：legacy 与 v2 解析、空 session、非法输入、空白、设备 id 与 UUID v5 的确定性、会话回退 | `tests/AxonHub.Llm.Tests/ClaudeCodeUserIDTests.cs` | 12（既有 5 例扩充至 12，其中生成 7 例） |
| `DeepSeekOutboundTransformer`：URL 归一、response_format 降级、thinking 开关、reasoning_content 填充、completion 分派、错误与构造校验 | `tests/AxonHub.Llm.Tests/DeepSeekOutboundTransformerTests.cs` | 20 |
| `OpenCodeOutboundTransformer` 与 `WithSessionHeader`：模型路由、会话头优先级与生成、两种包裹形态的能力保留、响应 / 流分派、错误与构造校验 | `tests/AxonHub.Llm.Tests/OpenCodeOutboundTransformerTests.cs` | 38 |
| 通道接线：claudecode 的 api-key / OAuth 凭据与头、opencode 三类型的路由与会话头、缺凭据文案 | `tests/AxonHub.Api.Tests/ClaudeCodeAndOpenCodeChannelWiringTests.cs` | 11 |

合计 154 例（Llm 143、Api 11）：`AxonHub.Llm.Tests` 由 1376 增至 1519，
`AxonHub.Api.Tests` 由 949 增至 960，`AxonHub.Persistence.Tests` 保持 70，
`AxonHub.Grains.Tests` 保持 92；四个项目合计 2487 增至 2641。C095 第 (7) 项
（claudecode / opencode 出站生成）关闭；C096 登记计费中间件、端点表、`Stop` 转发、
执行 key 审计与凭据文案的残余差异。

## 34. Ollama 原生出站、NDJSON 流与两个通道类型（59 例，2026-11-01）

参照路线：`llm/transformer/ollama/outbound.go`（`/api/chat` 报文、`stream` 恒显式、
`options` 映射、`images` 的 data-URL 剥离、`tool_calls` 的 `map[string]any` 往返、
`thinking` → `reasoning_content`、`ollama_error` 信封、`AggregateStreamChunks` 的
末块工具调用与非零 usage 覆盖）、`llm/transformer/ollama/decoder.go`（注册
`application/x-ndjson` 与 `; charset=utf-8` 变体，按 `\n` 分帧、去一个 `\r`、
EOF 残余行原样发出、错误走 `Err`）、`llm/httpclient/client.go:408-426`
（Content-Type 精确查表 → mime 解析 → 回退 SSE）、`internal/server/biz/channel_llm.go:677-680,1392-1428`
（两种类型跳过缺 key 校验；只有存在启用 key 或 override 时才安装 provider）、
`internal/server/biz/channel_endpoint.go:329-330`（`ollama/chat` 与
`anthropic/message` 各一个默认端点）、`internal/server/orchestrator/pass_through_prompt_protection.go:312`
（OllamaChat 与 OpenAI chat 同组）与 `candidates_stream_policy.go:81`
（OllamaChat 属于可自动聚合族）。参照测试：`ollama/outbound_test.go`（8 例）、
`internal/server/biz/channel_llm_ollama_test.go`（5 例，其中 apiKeyOverride 1 例属
C013 残余）。

端口新增：`OllamaOutboundTransformer`（Config、请求/响应线格式模型、
`TransformRequest` / `TransformResponse` / `TransformStream` / `TransformStreamChunk` /
`TransformError` / `AggregateStreamChunks`、`IApiKeyProvenance`）、`HttpNdjsonEventStream`
（NDJSON 解码器，读错误与取消进入 `Err` 而非异常）、`HttpExecutor.DoStream` 的
Content-Type 选择（二进制 → NDJSON → SSE）；`ChannelOutboundFactory` 增加 `ollama`
（原生出站）与 `ollama_anthropic`（`AnthropicPlatformType.Ollama` + Bearer）两个分支，
并把两种类型从缺 key 抛错中豁免：无启用 key 时不安装 provider，请求不带
`Authorization`；被禁用但已存储的 key 也不会被重新使用。另把
`RequestChainPersistenceMiddleware.EventModel` 的格式分派补齐为 OpenAI chat /
legacy completion / Ollama chat（参照 `llm/modelname.FromEvent`），使流式响应的
`upstream_model_id` 与参照一致（4 例）。差异与残余缺口登记为 C097：
NDJSON 取消以 `OperationCanceledException` 而非 `ctx.Err` 出现、`arguments` 数字
字面量保真（参照经 float64 重编码）、`arguments` 非对象报文的宽容度、STJ 与 Go 的
解码文案差异、能力/端点表未建模、`modelname` 响应模型名提取与 apiKeyOverride 未移植。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| `OllamaOutboundTransformer`：请求线格式（字段顺序、`stream` 恒显式、`options`、`images` 省略与 data-URL 剥离、工具与 `tool_calls` 的 Go map 排序往返、缺省 `{}` schema）、URL 与 `EndpointPath`、可选 Bearer、响应 / 流 / 聚合、`ollama_error`、校验与构造 | `tests/AxonHub.Llm.Tests/OllamaOutboundTransformerTests.cs` | 42 |
| NDJSON 解码器与传输选择：逐行、空行、CRLF、EOF 残余、无额外空事件、读错误、`application/x-ndjson` 及其 charset 变体的路由 | `tests/AxonHub.Llm.Tests/HttpNdjsonEventStreamTests.cs` | 10 |
| 通道接线：`ollama` / `ollama_anthropic` 的类型分派、无 key 建站、禁用 key 不发送、Bearer、`/v1/messages` 归一与 `num_predict` 映射 | `tests/AxonHub.Api.Tests/OllamaChannelWiringTests.cs` | 7 |
| 流式模型名分派：OllamaChat / OpenAICompletion 的顶层 `model`、其它格式为空、Cline 信封仅在 `success` 为真时解包 | `tests/AxonHub.Api.Tests/RequestChainModelNameTests.cs` | 4 |

合计 63 例（Llm 52、Api 11）：`AxonHub.Llm.Tests` 由 1519 增至 1571，
`AxonHub.Api.Tests` 由 960 增至 971，`AxonHub.Persistence.Tests` 保持 70，
`AxonHub.Grains.Tests` 保持 92；四个项目合计 2641 增至 2704。C097 登记本切片的
已接受差异与残余缺口（能力表 / 端点表、apiKeyOverride、其余 provider 通道）。

## 35. DeepSeek 通道接线（4 例，2026-11-01）

参照路线：`internal/server/biz/channel_llm.go:819-829`（`channel.TypeDeepseek` 安装
`deepseek.NewOutboundTransformerWithConfig`，传入 `BaseURL` 与
`getAPIKeyProvider(ch)`；DeepSeek 没有无 key 部署模式，因此沿用缺 key 抛错）。
第 33 节已移植出站本身（URL 归一、`/beta/completions` 分派、`json_schema` 降级、
`thinking` / `reasoning_content`），本次只补 `ChannelOutboundFactory` 的
`deepseek` 分支：`BaseUrl`、`ApiKeyProvider`、`ChannelApiKeyIndex` 按参照接线，
并与 `openai` 通道的类型分派保持隔离。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| `deepseek` 通道：构建 DeepSeek 出站、`/v1/chat/completions` 与 Bearer、完成请求走 `/beta/completions`、缺 key 拒绝、`thinking` 信封 | `tests/AxonHub.Api.Tests/DeepSeekChannelWiringTests.cs` | 4 |

`AxonHub.Api.Tests` 由 971 增至 975，`AxonHub.Llm.Tests` 保持 1571，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 2704 增至 2708。无新增差异条目：出站行为已由第 33 节与 C096 锁定，接线只
影响通道类型可用性，覆盖缺口表相应行同步更新。

## 36. Fireworks / Moonshot OpenAI 兼容出站与通道接线（25 例，2026-11-01）

参照路线：`llm/transformer/fireworks/outbound.go:35-58`（校验配置后取默认
`https://api.fireworks.ai/inference/v1`、`strings.TrimSuffix(baseURL, "/")` 去一个
尾部斜杠、`ReasoningFieldContent`，再原样返回 `openai.NewOutboundTransformerWithConfig`；
`internal/server/biz/channel_llm.go:729-739` 安装该出站）。`llm/transformer/moonshot/outbound.go:42-118`
（先用 `openai.Config{ReasoningField: Content}` 构造内嵌出站，失败时包一层
`invalid Moonshot transformer configuration`；`TransformRequest` 只接受 chat，
拒绝 compact 与其他类型，要求 messages 非空，`RequestFromLLM(Content)` 后把
`json_schema` 降级为 `json_object` 并清空 `json_schema`，URL 为
`NormalizeBaseURL(config.BaseURL, "v1") + "/chat/completions"`，Bearer 认证；
响应 / 流 / 错误 / 聚合沿用嵌入的 OpenAI 出站；`channel_llm.go:831-841` 安装）。

本次移植新增：
`src/AxonHub.Llm/Transformer/Fireworks/FireworksOutboundTransformer.cs`（配置、默认
URL、单斜杠裁剪、`Create` 返回 `OpenAiOutboundTransformer`）、
`src/AxonHub.Llm/Transformer/Moonshot/MoonshotOutboundTransformer.cs`（chat-only
请求路径、schema 降级、其余方法委托），以及 `ChannelOutboundFactory` 的
`fireworks` / `moonshot` 分支和类型说明。Moonshot 请求路径**不复用** OpenAI 出站的
model / document 校验与 tool-call extra content 清理，与参照的覆写范围一致；
Fireworks 的 `ReasoningFieldNone` 注释与代码冲突，以代码的 `Content` 为准。
已接受差异与残余裁剪见 C098。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| Fireworks：空 URL 默认端点、尾部斜杠裁剪与版本归一、`reasoning` 回填 `reasoning_content`、Bearer/API 格式、构造校验 | `tests/AxonHub.Llm.Tests/FireworksOutboundTransformerTests.cs` | 7 |
| Moonshot：`json_schema`→`json_object`（含 schema 清除）、URL 归一、请求形状、reasoning 回填、messages / 类型 / compact 拒绝、错误信封委托、构造校验 | `tests/AxonHub.Llm.Tests/MoonshotOutboundTransformerTests.cs` | 12 |
| `fireworks` 通道：默认端点、reasoning 字段、缺 key 拒绝 | `tests/AxonHub.Api.Tests/FireworksChannelWiringTests.cs` | 3 |
| `moonshot` 通道：URL 归一与 Bearer、schema 降级、缺 key 拒绝 | `tests/AxonHub.Api.Tests/MoonshotChannelWiringTests.cs` | 3 |

`AxonHub.Llm.Tests` 由 1571 增至 1590，`AxonHub.Api.Tests` 由 975 增至 981，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 2708 增至 2733。差异条目新增 C098（已接受）。

## 覆盖缺口

| 未覆盖部分 | 原因 | 风险 | 计划 |
|---|---|---|---|
| pipeline 重试、空响应检测、SSE 事件上限 | 已由 24 个端口测试锁定 | — | 完成（M2） |
| TypeSafe SystemOne（`/v1/systemone`、`/typesafe/v1/systemone`、`typesafe` 通道） | 已由第 24 节的 65 例锁定（统一模型与 Answer Extra、双向变换、错误信封、两条路由、空响应探测）；通道能力表与字段级解码文案为 C087 登记的裁剪，pass-through body 的请求与提示词保护半程已由第 26、28 节闭合 | 低 | 完成（C087、C091、C092） |
| pass-through 提示词保护 | 请求半程由第 26 节的 95 例锁定，响应/流半程（原始响应替换、双通道扇出、1024 事件/8 MiB 回放缓冲、取消后排干、上游错误传播）由第 27 节的 26 例锁定，提示词保护的统一应用、回放校验与原始 body 补丁由第 28 节的 71 例锁定；规则管理面/预览未移植（C092），codex 出站的 `AllowPassThroughBody` 策略由第 29 节的 61 例 codex 用例锁定（C091 缺口关闭，见 C094） | 中 | 管理面随 C013/C014 |
| Codex 出站与 OAuth 通道生命周期 | 出站变换、凭据解析、刷新单飞与通道接线由第 29 节的 61 例 codex + 50 例 OAuth + 20 例接线 + 4 例 `default_test_model` 锁定；端点表未建模（图片/alpha-search 按通道类型与入站格式分派）、OAuth `TokenProvider` 每次尝试新建、刷新不写回数据库、无后台自动刷新、无按通道代理 client、API 层 codex 响应头未转发、`DecodeAuthJson` 的消费方（模型拉取管理面）未移植（C094） | 中 | 随端点记录/管理面切片（C013/C014、C072） |
| 首包超时、非流式超时 | 实现已对等（`FirstEventTimeoutGuard` / `NonStreamTimeoutScope`），参照侧无独立单测可移植；端口在 orchestrator 层补了 2 例集成测试（超时 → 跳过同通道重试 → 切通道，取消不计失败、首包超时计失败） | — | 完成（M3） |
| 数据库接线的端到端启动 | 已用真实宿主冒烟与应用镜像冒烟（第 9 节）：Kestrel + Orleans silo + `postgres:17-alpine` 启动、幂等 DDL 建表、`/health` 200、422/502 失败链路与请求/执行落库均通过；多阶段 Dockerfile 构建的镜像在同一容器网络内复现同一结果 | 低 | 完成（M4） |
| 非 chat 响应类型的空响应探测（video） | 已由第 16.1、17、18、19、20 节先后闭合图片、embedding / moderation、decisions / alpha-search、speech / transcription 与 rerank 分支；video Llm 分支（统一模型与变换器）由第 21 节闭合（57 例，C081）；video 端点接线（`POST /v1/videos`、`GET/DELETE /v1/videos/:id`、`VideoService` 与持久化接缝）由第 22 节闭合（36 例，C082–C085），非 chat 响应类型已全部闭合 | — | 完成 |
| Responses API 非流式以外部分 | Anthropic Messages 双向链路已实现：入站（非流式与流式）由第 10 节的 155 + 7 例锁定，出站（含 SSE 与 cache_control 优化器）由第 11 节的 67 例锁定；Responses 非流式双向变换（含 compact）由第 12 节的 158 例锁定，图片请求/响应路径与图片流式聚合由第 14 节的 26 例锁定（C059 闭合），流式状态机与聚合由第 13 节的 86 例锁定（C063 闭合），WebSocket 传输与 HTTP 包装由第 15 节的 45 例锁定（C061 闭合；生产接线见 C072），客户端 OpenAI Images 入站/出站变换器由第 16 节的 71 例锁定（C069 闭合），`/v1/images/*` 路由接线由第 16.1 节的 8 例锁定（C075 闭合，参照本身停用 variations 路由），OpenAI Embeddings / Moderations 客户端变换器与两条路由由第 17 节的 71 例锁定（C076 闭合），OpenAI Alpha Search / Decisions 客户端变换器与两条路由由第 18 节的 137 例锁定（C077 闭合），OpenAI Audio 客户端变换器与三条 `/v1/audio/*` 路由由第 19 节的 102 例锁定（C079 登记，C010 的 speech / transcription 分支闭合），Jina Rerank / Embedding 客户端变换器与 `/v1/rerank`、`/jina/v1/rerank`、`/jina/v1/embeddings` 三条路由由第 20 节的 88 例锁定（C080 登记，C010 的 rerank 分支闭合；非 chat 响应类型仅剩 video），OpenAI Video 客户端变换器 Llm 层由第 21 节的 57 例锁定（C081 登记，C010 的 video Llm 分支闭合），OpenAI Video 端点接线（三条路由、biz VideoService 的任务查询/删除流程与持久化接缝）由第 22 节的 36 例锁定（C082–C085 登记，C010 的 video 端点接线闭合；doubao / zenmux 视频变换器仍为 C010 裁剪），OpenAI 旧版 Completions 客户端变换器与 `POST /v1/completions` 路由由第 23 节的 69 例锁定（C086 登记） | 中 | 后续阶段 |
| GraphQL / 前端 | Orleans 侧另建，不复用 Ent / GraphQL | 中 | 后续阶段 |
| 通道管理面：写入校验与覆盖模板 CRUD | 参照的 `ValidateBodyOverrideOperations` / `ValidateOverrideHeaders` / `ValidateRateLimit` 在写入时拒绝非法操作与限流参数，`channel_override_template_test.go` 的 6 例锁定模板 CRUD；端口只有读取/执行路径，非法配置按无覆盖/无限制处理（C037），`channel_override_templates` 表未建 | 中 | 随通道管理面落地（C014） |
| 负载均衡：adaptive 策略链、WeightRoundRobin、rate-limit、error-aware / additive scoring | 已由 §7 的 24 例与 §7.1 的 18 例锁定（C012 已解决；RateLimitAware 已接入真实限流状态，C035 已解决）；残余 C034（无延迟样本）与 C036（无模型级熔断状态） | 中 | 随延迟指标与模型级熔断落地 |
| 持久化（channels / projects / users / api_keys / channel_model_prices / requests / request_executions / usage_logs / systems / traces / threads） | 已实现 EF Core 写 + Dapper 读 + PostgreSQL 层并由 56 例锁定；生产宿主用 `PostgresEnabledChannelProvider` 读取通道快照，请求/执行/用量/成本经 `RequestChainPersistenceMiddleware` 落库并由 5 例锁定；`traces` 表与 `GetOrCreateTrace` 由第 30 节的 6 例锁定，`threads` 表与 `GetOrCreateThread` 由第 31 节的 5 例锁定。流式归档（external id / body）、终态分类与响应头回填已按参照补齐（C019）；`response_chunks` 与存储策略未接入（C020），响应头捕获点差异已登记（C021） | 低 | 容器化验收；存储策略随对应功能落地 |
| usage 成本管理面：`channel_model_prices` 写入、reference-id 轮换、版本表与自动定价 | 参照的 `SaveChannelModelPrices` / `channel_model_price_versions` / GraphQL 管理面未移植；端口只有读取路径与基础表（C048） | 中 | 随通道管理面落地 |
| API key 鉴权（/v1 中间件、header 提取、ClientIP/allowlist、no-auth 懒建）与 IP 黑名单 | 已按参照实现并由 73 例鉴权 + 29 例黑名单/系统设置锁定（另有 6 例 netip 前缀边界计入鉴权的 73 例）；管理面（GraphQL API key 创建）与 JWT/OIDC/角色未接入 | 中 | 管理面随 GraphQL/前端里程碑 |
| 其余 Ent 表（data_storages / prompts / roles 等） | 请求链路四表、鉴权三表、`systems`、`traces`（第 30 节）与 `threads`（第 31 节）已落；其余表未移植 | 中 | 随对应功能落地 |
| trace 子系统：查询 / GC、GraphQL | trace id 解析、响应别名、`traces` 幂等落库、唯一冲突重读、非消息端点与 fail-open 由第 30 节的 38 + 6 例锁定，thread 中间件与 `traces.thread_id` 由第 31 节的 7 + 5 例锁定，trace 粘性 API key 由第 32 节的 7 + 1 例锁定；claudecode / DeepSeek / OpenCode 出站生成由第 33 节的 154 例锁定（C096），查询 / GC / GraphQL 未移植（C095） | 中 | 随管理面 / 权限切片落地 |
| 其余供应商通道类型（Gemini 原生 / Vertex、Bailian、Cerebras、Xai、Zai/Zhipu、Doubao/Volcengine、Longcat、Modelscope、Nanogpt、Openrouter、Cline、Minimax、Xiaomi、Commandcode、Antigravity、GithubCopilot、Zenmux 系列、OpenaiResponses 等） | 参照 `channel_llm.go` 的类型分支为上述类型装配 provider 专用出站；端口已移植 openai、anthropic、codex、fenno、claudecode、opencode_go(_anthropic)、jina、typesafe、deepseek、fireworks、moonshot、ollama(_anthropic)；`ChannelOutboundFactory` 遇到其余类型会报 unsupported channel type。Ollama 两种类型由第 34 节闭合（C097），DeepSeek 通道接线由第 35 节闭合，Fireworks 与 Moonshot 由第 36 节闭合（C098） | 中 | 按 provider 分组后续切片（优先 Xai / Longcat 等 OpenAI 兼容族） |
| 模型列表的管理面来源 | 参照的 `ModelService.ListEnabledModels` 先取配置的 `models` 实体，再按 `QueryAllChannelModels` 合并通道模型，并以 profile（ChannelIDs / ChannelTags / ModelIDs）过滤、privacy 拒绝；OpenAI 的 `include` 扩展字段来自 `ent.Model`。端口没有 `models` 表与 profile，列表恒为通道派生模型（C088，第 25 节 55 例覆盖可观察部分） | 中 | 随管理面 / 权限切片落地 |
