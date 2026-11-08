# 行为基线账（第一本账）

把"参照实现的行为"变成可执行证据。四类证据缺一不可。

参照物：`E:\GitHub\axonhub-ref`（冻结工作树），基线提交 `b8bb3421e1fe946cfc4f0d86e8412098738f2793`（v1.0.0-beta11）。

## 1. 测试移植

| 上游测试 | 本实现测试 | 状态 | 说明 |
|---|---|---|---|
| `llm/pipeline/pipeline_retry_test.go`（4 个子测试 + 11 个顶层测试 = 15 例） | `tests/AxonHub.Llm.Tests/PipelineRetryTests.cs`（15 个 `[Fact]`） | 15/15 已移植 | 逐条对照：同通道重试、跨通道切换、混合重试、预算耗尽、原始流意图保持、元数据不泄漏、提交前错误重试、内容提交后不重试、预提交缓冲上限（事件数/字节数）、大内容首事件放行、超大 SSE 事件绕过重试。`preReadLlmStream` 通过 `InternalsVisibleTo` 直接驱动，等价于 Go 的同包测试。 |
| `llm/pipeline/empty_response_test.go`（23 个可执行用例） | `tests/AxonHub.Llm.Tests/EmptyResponseTests.cs`（14 个 `[Fact]`） | 14/23 已移植，9 例顺延 | 已移植：`TestHasResponseContent` 的 4 个 chat 子测试 + 独立签名用例、流式空响应检测 2 例、非流式空响应检测 2 例、音频空响应探测 5 例（speech 音频、transcription 文本/原始体、speech 流事件 audio_base64、speech 二进制块、transcription 流事件，见第 19 节）。顺延的 9 例依赖 rerank / video 等尚未进入统一模型的响应类型（见"覆盖缺口"），属于范围裁剪，非行为差异。 |
| `internal/server/orchestrator/lb_strategy_rr_test.go`（RoundRobinStrategy 路径）+ `load_balancer_test.go`（排序/健康/topK 子集） | `tests/AxonHub.Grains.Tests/ChannelSelectionTests.cs`（16 个 `[Fact]`） | 16 例已改写（子集） | 覆盖：模型过滤、零请求满分 150、请求数拉低分数、同分保持输入顺序（`WithoutWeightTieBreaker`）、健康通道降级与冷却边界、topK 排除不健康通道、`calculateTopK` 的 1/2/3 截断与非正/超大值归一为全部候选、模型别名解析、10 分钟窗口过期与迟到事件丢弃、不计数模式。C012 已补齐 WeightRoundRobin / error-aware / additive 等策略链（本文件另有 8 例，共 24 例），rate-limit 策略由 §7.1 的 18 例 `RateLimitAwareScorerTests` 单独覆盖；真实数据库用例仍顺延（C014）。 |
| `llm/transformer/openai/model.go` 的 `encoding/json` 解码语义（上游无对应单测） | `tests/AxonHub.Llm.Tests/OpenAiRequestTypeCheckTests.cs`（101 例） | 已建立（探针证据） | 用独立 Go 探针（`E:\tmp\typrobe`，直接编译参照模块）与 C# 探针对同一 106 条输入逐条比对；锁定错误文案 67 例、Go 接受 32 例、null 零值/tool_choice 回退 2 例。覆盖结构名与标签路径、整数/浮点字面量规则、大小写不敏感匹配、重复键、未知字段、硬错误覆盖软错误。数值与 null 元素语义见 C022（已闭合），字符串数组的 null 元素见 C224。 |
| `internal/server/orchestrator/retry_test.go` 的 `TestIsRetryableErrorForChannel`（10 个断言） | `tests/AxonHub.Api.Tests/ChannelRetryPredicateTests.cs`（10 个用例 + 6 个解析/降级用例） | 10/10 已移植 | 逐条对照：nil、默认 5xx、包装的传输错误、配置 400 / 未配置 401、子串匹配、正则匹配、未匹配文本、空 settings、ResponseError 详情文本。另锁定 settings jsonb 解析、损坏 settings 降级、非法正则不匹配（Go 行为）。`Normalize*`（写入侧校验）未移植：本切片无通道写 API。 |
| `internal/server/middleware/header_test.go`、`auth_test.go` 的鉴权分支 | `tests/AxonHub.Api.Tests/{ApiKeyAuthenticationTests,ClientAddressTests,ApiKeyAuthMiddlewareTests}.cs`（73 例） | 已改写（子集） | `TestExtractAPIKeyFromRequest` 的默认配置分支逐条覆盖：7 个 header 优先级、四种前缀、裸 key、TrimSpace、空值/纯空白跳过（14 例）。`TestIsAnyAllowedIP` 9 例与 `TestIPMatchersMappedIPv4` 的 17 个 allowlist 用例逐条移植（含 `::ffff:` 映射地址、host bits、`/95` 宽前缀与 `::/0` 家族隔离）。`TestIPMatchersWithTrustedProxy` 改写为 `ClientAddress` 的受信/不受信转发用例：默认 `trusted_proxies=[]` 时忽略转发头，受信对端按 gin 的右起“首个不受信跳”规则取客户端地址。`auth_test.go` 的 magic-value 401 与无 header 语义由鉴权器/中间件用例覆盖。JWT/密码/缓存用例顺延（C014）；`ExtractAPIKeyFromHeader`（旧单 header 辅助函数）不在 `WithAPIKeyConfig` 路径，未移植。 |
| `internal/server/middleware/ip_blocklist_test.go`（`TestIsBlockedIP` 9 例、`TestClientIPCandidates*` 3 例）与 `internal/server/biz/system.go` 的 `SecuritySettingsOrDefault` / `normalizeSecuritySettings` 分支（上游无对应单测） | `tests/AxonHub.Api.Tests/IpBlocklistMiddlewareTests.cs`（14 例）+ `SystemSettingsServiceTests.cs`（15 例）+ `ClientAddressTests` 的前缀边界 6 例 | 已改写（9/9 + 3/3 已移植） | `TestIsBlockedIP` 的 9 个子测试逐条覆盖（精确 / CIDR / Trim / IPv6 / 多 candidate / 非法 candidate / 非法条目 / 不匹配）；`TestClientIPCandidates*` 的三种场景改写为中间件与 `ClientAddress` 用例（不受信对端忽略转发头、受信对端取右起首个不受信跳、单一 candidate 的等价去重）。系统设置无上游单测，按源码分支建立：缺行 / 非法 JSON / JSON null / 空对象 / 缺图标 / 大小写键 / 未知字段 / 重复键 / 存储失败 / 软删除全部回退默认，`blocked_ips` 的 Trim / 去空 / 精确去重 / 保序归一化，以及图标开关覆盖。另补 `netip.ParsePrefix` 的 1-3 位十进制、无前导零 / 符号 / 空白、拒绝 zone 的边界 6 例。`WithIPBlocklist` 的 403 响应体与「ClientIP 为空不读设置」由中间件用例锁定。 |
| `internal/server/biz/system_test.go` 的 `TestNormalizeRetryPolicy_LoadBalancerStrategy` / `TestNormalizeRetryPolicy_MigratesLegacyStatuses`、`system_retry_policy_test.go`，以及 `system.go:1042-1172` 的读取分支（上游单测仅覆盖归一化） | `tests/AxonHub.Api.Tests/SystemRetryPolicyServiceTests.cs`（49 例）+ `ChatCompletionOrchestratorTests` / `ChannelSelectionTests` 的 topK 3 例 | 已改写（子集） | 逐条对照：策略归一化 7 例（合法四种、`weighted`→failover、非法/空→adaptive）、legacy `statuses`→永久规则（含既有 `rules` 优先）、`custom` 空白消息→hidden；参照未覆盖的读取分支按源码建立：缺行/软删除/读库失败/非法文档回退 `defaultRetryPolicy`，`null` 与 `{}` 的零值策略，`null` 标量保持零值，超时夹取 `[0,600]`，其余字段不夹取，嵌套类型错误回退默认。参照的 API-key principal 回归用例（#2080）在端口无 Ent 隐私层对应物：读取路径不依赖身份，策略值由全字段用例锁定。 | 
| `internal/server/biz/channel_metrics_test.go` 的 `TestPerformanceRecord_Methods` + `internal/server/orchestrator/outbound.go` 的 `markPerformanceTerminal` 分支 | `tests/AxonHub.Api.Tests/ChannelPerformanceRecordTests.cs`（7 例）+ `ChatCompletionOrchestratorTests` 流式语义 3 例 | 已建立 | 逐条对照 MarkSuccess / MarkFailed / MarkCanceled / IsValid；终态四态映射：completed/incomplete → 成功、canceled → 取消、failed → 失败（500 + 终态文案）、none → 无操作，首次终态后不再改写。端到端锁定：无终态传输错误既不记成功也不记失败；无终态但聚合完整记成功；终态 `[DONE]`/finish_reason 记成功。性能明细字段（首 token/TPS/状态码/错误文本/API key/完成 token）、`extractErrorMessageForMatching` 的 8 KiB 截断、异步 worker 与自动禁用未移植，均不参与当前计数（C017）。 |
| `internal/server/orchestrator/override_test.go`（30 个顶层测试函数，含 `TestParseOverrideOperations`）+ `internal/server/biz/channel_override.go` 的解析/优先级分支 | `tests/AxonHub.Api.Tests/ChannelOverrideTests.cs`（87 例）+ `ChannelOverrideSettingsTests.cs`（47 例） | 134/134 已移植（C038 已闭合） | 逐条对照 30 个测试函数并补充分支用例：模板（`.Model` / `.RequestModel` / `.Metadata` 嵌套 JSON / `.RequestHeader` / `.PromptCacheKey` / user_id）、映射后的请求模型条件、header 模板与 JSON-like 值、legacy `overrideParameters` 的 set/delete/clear 与非法/空设置、AxonHubClear 5 例、新格式 set/delete/rename/copy/条件、legacy 兼容、`set_if_absent` 的现网与原始入站 body 判定、Issue 632、数组 append/prepend/insert/remove 与 splat、非 JSON/multipart 门槛；设置解析 47 例锁定新格式全字段、legacy map 的 GoPrintf 字符串化、新旧字段优先级、大小写不敏感与解码失败整体降级。参照的 `channel_override_template_test.go`（6 例）属通道管理面，未移植（C014）。 |
| `internal/server/orchestrator/{channel_limiter,channel_limiter_manager,channel_request_tracker,lb_strategy_rate_limit,rate_limit_tracking,rate_limit_admission,channel_queue_error,connection_tracking}_test.go`（91 个顶层测试函数） | `tests/AxonHub.Grains.Tests/{ChannelLimiterTests,ChannelLimiterManagerTests,ChannelRequestTrackerTests,RateLimitAwareScorerTests}.cs`（64 例）+ `tests/AxonHub.Api.Tests/{RateLimitAdmissionTests,RateLimitTrackingTests,ChannelQueueErrorTests,ChannelLimiterMiddlewareTests}.cs`（5 + 10 + 4 + 7 个函数 / 29 个用例） | 90/91 已移植 | 逐条对照：硬信号量容量、FIFO 让位、有界队列拒绝、队列超时、取消与无槽泄漏；manager 的配置归一化/相等/重建/Forget/Snapshot；tracker 的自然分钟窗口、RPM 原子扣减、TPM 累加、冷却只延长与惰性清理；RateLimitAware 的四个子分与硬耗尽；API 中间件的 RPM 准入、TPM/冷却追踪、队列错误信封与连接让位顺序。未移植/合并：`TestNoopRateLimitTracking`（端口无 nil tracker 分支，C045）；`NoChannel` 与 `NilChannel` 在端口合并为“无候选”状态；`channel_limiter_metrics_test.go` 的 3 例由 §138 移植（C044 闭合）。另补 1 例冷却只延长的边界（`CooldownOnlyEverExtends`），tracker 共计 24 例。 |
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
错误包装的单 `InnerException` 结构登记为 C043，指标缺口 C044（后由 §138 闭合）、通用 metrics 缺口 C223（后由 §139 闭合）、noop 追踪 C045。参照的
`channel_limiter_metrics_test.go`（3 例）随指标子系统顺延；通用 metrics 参照无测试，端口补 26 例接线/行为守卫。

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
因此 hidden/custom 对传输错误无效果；流式路径先分类再由策略包装，502 与 code 得以保留。
`quota_exhausted` 的包装来源（`QuotaExhaustedError` → `wrapQuotaExhaustedAsResponseError`）
已由 §95 接入（非流 503 与流式 envelope，43 例）；gjson 与 System.Text.Json 的报文解析差异
登记为 C033。

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
C010 的 video 端点接线随之闭合（doubao 视频变换器由第 43 节闭合；zenmux 仍为 C010 的既定裁剪）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 服务层（对照 `video_test.go` 的 7 个测试函数）：external_id + project 作用域查询（GET/DELETE 跨项目均 not found）、GET 任务并落轮询快照、最新执行格式优先、跳过无 provider 任务关联的执行、非 video 格式拒绝（错误文案逐字）、通道类型回退、doubao 回退解析出 Seedance 出站（第 43 节，C105）、DELETE 上游删除 + 本地 canceled、缺 external_id / channel_id / 通道 / 请求、执行查询失败包装、快照写失败非致命、状态映射表 | `tests/AxonHub.Api.Tests/VideoServiceTests.cs` | 23 |
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

## 37. Longcat 强制数组内容与通道接线（10 例，2026-11-01）

参照路线：`llm/transformer/longcat/outbound.go:54-104`（以
`openai.Config{ReasoningField: Content}` 构造内嵌出站，失败时包一层
`failed to create longcat outbound transformer`；`TransformRequest` 先拒绝 nil
请求，再把每条消息的零值内容就地补成 `""`，委托 OpenAI 出站后把请求体反序列化为
`openai.Request`，再按 `longcat.Request` 重新序列化；`model.go:17-59` 的
`longcat.Request` / `Message` 嵌入 OpenAI 类型并重新声明 `messages` / `content`，
外层字段遮蔽内层字段，`MessageContent.MarshalJSON` 把字符串或空内容包成
`[{"type":"text","text":...}]`，非空多分片数组原样输出；
`internal/server/biz/channel_llm.go:1121-1131` 安装该出站）。

本次移植新增：
`src/AxonHub.Llm/Transformer/Longcat/LongcatOutboundTransformer.cs`（构造校验、
调用方内容补空、OpenAI 出站委托，以及基于 `JsonNode` 的请求体重写：根
`messages` 与每条消息的 `content` 移到末尾，字符串/空内容包装为单元素 text
数组，多分片数组保持原样），以及 `ChannelOutboundFactory` 的 `longcat` 分支
与类型说明。字段顺序按 Go 嵌入结构体的遮蔽规则复现，请求体其余字段沿用已锁定的
OpenAI 出站形状；调用方变更语义与参照一致。已接受差异与残余裁剪见 C099。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| Longcat：字符串/空串/null 内容强制数组、多分片保持、`messages` 与 `content` 末位键序、调用方内容变更、nil 请求、构造校验 | `tests/AxonHub.Llm.Tests/LongcatOutboundTransformerTests.cs` | 8 |
| `longcat` 通道：默认 chat 端点与 Bearer、数组内容证明未落到普通 OpenAI 分支、缺 key 拒绝 | `tests/AxonHub.Api.Tests/LongcatChannelWiringTests.cs` | 2 |

`AxonHub.Llm.Tests` 由 1590 增至 1598，`AxonHub.Api.Tests` 由 981 增至 983，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 2733 增至 2743。差异条目新增 C099（已接受）。

## 38. xAI / Cline OpenAI 兼容出站与通道接线（59 例，2026-11-01）

参照路线：`llm/transformer/xai/outbound.go:16-19`（`DefaultBaseURL =
"https://api.x.ai/v1"`）、`:52-74`（`validateConfig` 后以
`openai.Config{PlatformType: PlatformOpenAI, ReasoningField:
ReasoningFieldContent}` 构造内嵌出站，校验失败包
`invalid xAI transformer configuration`，内层失败包
`failed to create outbound transformer`）、`:77-91`（nil 配置、缺 provider、
空 URL 就地把 `config.BaseURL` 改为默认值）、`:94-126`（nil 请求、model 必填、
messages 非空；`grok-4` 清空 `reasoning_effort` 与 penalties/stop，`grok-3` /
`grok-3-mini` 清空 penalties/stop，其余模型原样委托）、`:128-184`
（`IsValidResponse` 的 done / choices / delta / 内容 / 分片 / 工具 / role /
finish reason / refusal / reasoning_content 判定）、`:186-201`（TransformStream
经 `streams.Filter` 过滤空事件）；`llm/transformer/cline/outbound.go:21-59`
（配置与内嵌 OpenAI 出站，`ReasoningFieldReasoning`，`EndpointPath` 透传）、
`:59-100`（chat 时加 `X-Client-Type: cline-cli`，再按统一消息索引把
user/system/developer 的空内容改写为 `[{"type":"text","text":""}]`）、
`:103-152`（响应信封 `{success,data,error,errors}`、状态码 ≥400 → 错误解析、
success 为假 → 502）、`:154-174`（流事件：`[DONE]` 终止、流内错误事件、
其余走响应转换）、`:176-184`（TransformError）、`:186-214`（带
`clineChunkTransform` 的聚合）；`model.go:9-84`（Response / Choice / Message
到 OpenAI 结构的转换，`reasoning_details` 文本拼接优先于 `reasoning`）、
`error.go:14-160`（状态码归一 0→502、string/map/数组错误映射、
message/type/code/param/request_id、缺省 `api_error` 与 HTTP 状态文本）；
`internal/server/biz/channel_llm.go:741-751`、`:843-853` 与
`channel_endpoint.go:238`、`:292-296` 的通道分支与默认端点。

本次移植新增：
`src/AxonHub.Llm/Transformer/Xai/XaiOutboundTransformer.cs`（构造校验、默认 URL
就地变更、Grok 字段裁剪、`IsValidResponse` 与 `FilterStream`、其余委托）。
`src/AxonHub.Llm/Transformer/Cline/ClineOutboundTransformer.cs`（配置、内嵌 OpenAI
出站、`X-Client-Type`、空内容补丁、信封解包、流分块转换、错误与聚合）、
`ClineModels.cs`（信封模型与到 OpenAI 结构的转换）、`ClineErrors.cs`（错误映射与
流内错误事件解析），以及 `ChannelOutboundFactory` 的 `cline` / `xai` 分支：
xAI 的 responses / responses_compact 入站落到 Responses 出站，其余落到 xAI chat
出站；Cline 恒为 chat 出站。空内容补丁以 `JsonNode` 复现 sjson 的索引补齐与
覆盖语义，参照的 `mergeSystemMessages` 索引错位被刻意保留。已接受差异与残余裁剪
见 C100。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| xAI：默认 URL 与 config 就地变更、自定义 URL、Bearer、Grok 三类模型的字段裁剪、`IsValidResponse` 13 组判定、`TransformStream` 的空 delta 过滤与 `[DONE]`、nil / model / messages 校验、构造校验、key 溯源 | `tests/AxonHub.Llm.Tests/XaiOutboundTransformerTests.cs` | 27 |
| Cline：`EndpointPath`、`cline-cli` 标识（3 组模型）、空内容归一的 8 条消息表、统一索引补丁（错位与 null 填充）、信封解包与裸 OpenAI body、502 错误与 errors 回退、HTTP 状态映射、空 / nil 响应、TransformError 三态、流内错误与 `[DONE]`、流转换、聚合的 reasoning 与 `[DONE]` / 错误块跳过、构造校验 | `tests/AxonHub.Llm.Tests/ClineOutboundTransformerTests.cs` | 24 |
| `xai` 通道：chat 出站与 Bearer、缺省 base URL、responses / compact 分派、缺 key 拒绝 | `tests/AxonHub.Api.Tests/XaiAndClineChannelWiringTests.cs` | 5 |
| `cline` 通道：`/chat/completions` 与 `X-Client-Type`、信封解包、缺 key 拒绝 | 同上 | 3 |

`AxonHub.Llm.Tests` 由 1598 增至 1649，`AxonHub.Api.Tests` 由 983 增至 991，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 2743 增至 2802。差异条目新增 C100（已接受）。

## 39. OpenRouter / Cerebras / NanoGPT OpenAI 兼容出站与通道接线（106 例，2026-11-01）

参照路线：`llm/transformer/openrouter/outbound.go:51-71`（构造：内嵌
`openai.Config{PlatformType: PlatformOpenAI, ReasoningField:
ReasoningFieldReasoning}`，校验失败包
`invalid OpenRouter transformer configuration`；`strings.TrimSuffix` 单尾部斜杠
裁剪出独立 `BaseURL`，与内嵌出站的版本归一互不影响）、`:74-141`
（`TransformRequest`：nil 拒绝、统一校验后按 `RequestType` 分派 chat / image /
embedding / speech / transcription / translation，compact 以
`compact is only supported by OpenAI Responses API` 拒绝，其余类型
unsupported）、`:143-233`（图片请求：`/images` 路由、`input_references` 上限
16、mask 拒绝、seed 透传、图片魔数嗅探与 data URL）、`:266-310`（聊天响应解码，
包装前缀 `failed to unmarshal chat completion response`）、`:312-374`（图片
响应：`ID = "img-"+Created`、`Object = "chat.completion"`、model 取自
`TransformerMetadata["model"]`（缺省 `image-generation`）、逐图
`buildImageDataURL`、首个可用媒体类型决定 `OutputFormat`、usage 转换）、
`:403-426`（`buildImageDataURL` 与 `imageFormatFromMediaType`：`image/svg+xml`
→ `svg`，其余裁 `image/` 前缀，未知 → 空串）、`:442-490`（流转换与分块）、
`:495-546`（错误信封：`metadata.raw`（`cast.ToString`）> `message` >
`http.StatusText(code)`，code 恒为字符串 `fmt.Sprintf("%d")`，nil 错误 → 500
+ `Internal Server Error`）；`model.go:64-140`（Response / Choice / Message 的
字段遮蔽、`reasoning_details` 文本优先、生成图片并入 content parts、空分片置
nil）；`llm/transformer/cerebras/outbound.go:35-96`（`DefaultBaseURL =
"https://api.cerebras.ai/v1"`、nil 配置与缺 provider 文案、空 URL 回退、包装
`invalid Cerebras transformer configuration`、`TransformRequest` 的 nil 拒绝与
浅拷贝 `Store=nil`，其余方法全部继承 OpenRouter）；`llm/transformer/nanogpt/outbound.go:43-56`
（内嵌 OpenAI 出站与构造包装）、`:62-110`（null 响应、状态 ≥400 的诊断
`{"error": string}` > 原始 body > 裸状态码、chat 解码进 `NanogptResponse`、
embedding / moderation / 图片 / 音频委托）、`:118-172`（流过滤 `[DONE]`、追加
`DoneResponse`、流内错误事件、分块转换）、`:174-210`（带
`nanoGPTChunkTransform` 的聚合）；`model.go:16-90`（reasoning →
reasoning_content、XML 工具调用提取与剩余内容、全消费时零
`MessageContent`、`ToOpenAIResponse` 恒安装非 nil choice 切片）；
`xml_parser.go:46-67`（`MaybeHasXMLToolCalls` 的 100KB 上限）、`:69-214`
（`ParseXMLToolCalls`：嵌套 → 工具调用 → 自闭合的候选收集、按起点稳定排序、
未识别匹配推进 `lastEnd`、`strings.TrimSpace` 剩余内容）、`:216-255`
（`normalizeXML`：unclosed → `</use_tool>` / `</Write_file>` 等补齐 →
mismatch → 自闭合补空格）、`:257-281`（工具名：标签名归一与
`name` 属性覆盖）、`:283-335`（参数：属性 + JSON 内容、Go `map[string]any` 的
UTF-8 字节键序与浮点格式）、`:337-345`（`nanogpt_` + SHA-256（name+args）前
16 位十六进制）；`internal/server/biz/channel_llm.go:753-799` 与
`channel_endpoint.go:284-294`、`:324`、`:326-327` 的通道分支与默认端点。

本次移植新增：
`src/AxonHub.Llm/Transformer/OpenRouter/`（`OpenRouterOutboundTransformer.cs`
的构造、chat / audio 委托、compact 拒绝与图片请求/响应、错误信封；
`OpenRouterModels.cs` 的图片与聊天信封以及到统一/OpenAI 结构的转换，字段遮蔽
与 `reasoning_details` 优先按参照保留）、`src/AxonHub.Llm/Transformer/Cerebras/`
（默认 URL、构造校验、浅拷贝剥离 `store`、其余委托）、
`src/AxonHub.Llm/Transformer/Nanogpt/`（`NanogptOutboundTransformer.cs` 的构造
包装、状态诊断、非聊天委托与流过滤/聚合，`NanogptModels.cs` 的推理与 XML 工具
调用消费，`NanogptXmlToolCalls.cs` 的归一化、正则抽取、确定性 ID 与 Go 风格
序列化）、`src/AxonHub.Llm/Transformer/Shared/GjsonErrorProbe.cs`（错误信封的
`metadata.raw` 探测），以及 `ChannelOutboundFactory` 的 `openrouter` /
`cerebras` / `nanogpt` / `nanogpt_responses` 分支：前两者恒为 OpenRouter chat
出站（音频等分支由出站自身按 `RequestType` 委托），后两者 chat 与 Responses
出站。已接受差异与残余裁剪见 C101。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| OpenRouter：构造校验与包装前缀、单斜杠 BaseURL、chat 请求形状与 `reasoning` 字段、`input_references` 上限 / mask / seed / data URL、图片响应 `img-<created>` / `chat.completion` / model 元数据 / `OutputFormat`、错误信封（raw / message / status text / code 恒字符串 / nil → 500）、响应与消息的字段遮蔽、流与聚合、key 溯源 | `tests/AxonHub.Llm.Tests/OpenRouterOutboundTransformerTests.cs` | 39 |
| nanoGPT：构造包装、status ≥400 三种诊断、chat 解码与 reasoning、非聊天委托、`[DONE]` 过滤与 `DoneResponse`、流内错误、聚合、零 `MessageContent`、`ToOpenAIResponse` 非 nil choices | `tests/AxonHub.Llm.Tests/NanogptOutboundTransformerTests.cs` | 16 |
| nanoGPT XML 工具调用：maybe 判定、自闭合 / 简单内容 / JSON 内容 / 错配闭合 / 未闭合 / 嵌套 / 无空格 / 多调用 / 无调用 / 未识别标签、工具名覆盖表、确定性 ID、JSON 数字与键序、未闭合贪婪回溯 | `tests/AxonHub.Llm.Tests/NanogptXmlToolCallsTests.cs` | 33 |
| Cerebras：nil 配置与缺 provider、默认 URL 回退、`store` 浅拷贝剥离（与 OpenRouter 对照）、nil 请求、OpenRouter chat 语义、key 索引溯源、响应与错误委托 | `tests/AxonHub.Llm.Tests/CerebrasOutboundTransformerTests.cs` | 8 |
| `openrouter` 通道：chat 出站与 Bearer、speech 由出站委托（`/audio/speech`）；`cerebras`：缺省 URL 与自定义 URL、`store` 剥离；`nanogpt`：chat URL；`nanogpt_responses`：Responses 出站与 `/responses` URL；四类缺 key 拒绝 | `tests/AxonHub.Api.Tests/OpenRouterCerebrasNanogptChannelWiringTests.cs` | 10 |

`AxonHub.Llm.Tests` 由 1649 增至 1745，`AxonHub.Api.Tests` 由 991 增至 1001，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 2802 增至 2908。差异条目新增 C101（已接受）。

## 40. Zai / Zhipu / Xiaomi / MiniMax 出站与通道接线（122 例，2026-11-01）

参照路线：`llm/transformer/zai/outbound.go:23-33`（Config：BaseURL /
APIKeyProvider / Version / EndpointPath）、`:53-71`（旧构造与
`NewOutboundTransformerWithConfig`：先以内嵌 `openai.Config{PlatformOpenAI,
ReasoningFieldContent}` 构造，失败包
`invalid Zai transformer configuration`；版本缺省 `v4`；`##` 后缀剥离为
rawURL）、`:74-100`（URL 三分支：raw → `TrimRight("/")`；EndpointPath → 以空版本
归一；否则以 Version 归一）、`:107-121`（`Request` 值嵌入 `openai.Request` 并在
其后声明 `user_id` / `request_id` / `thinking`，`Thinking{type}`）、`:123-201`
（GLM 版本正则 `(?i)(?:^|[/:])glm-([0-9]+)\.([0-9]+)`、≥5.2 原生
reasoning_effort、glm-5.3 恒思考、低/高/最高归并与 resolve 阶梯）、`:209-330`
（TransformRequest：nil / model / messages 校验，chat 与空类型直通、
image 分派、compact 与非支持类型拒绝、json_schema 降级为 json_object、metadata
取 user_id/request_id、rune 6-128 截断规则、request_id 回退
`shared.GetSessionID`、ToolChoice 归为 `"auto"`、Metadata 置 nil、thinking 与
reasoning_effort 赋值、`json.Marshal(zai.Request)`、Bearer 与 URL 三分支）、
`:337-360`（TransformResponse：≥400 → `HTTP error <code>`、空 body、按请求
APIFormat 分派图片、其余委托内嵌出站）；`llm/transformer/zai/image.go:22-52`
（图片入口：nil Image / 非 image_generation APIFormat / 带输入图或 mask 的
拒绝，写 `RequestType`、APIFormat 与 `TransformerMetadata["model"]`）、
`:54-124`（`map[string]any` 请求体：prompt 必填、quality high→hd 与 low/空→
standard、size 缺省 1024x1024、`watermark_enabled=false`、Image.User →
user_id、`/images/generations`）、`:126-180`（响应：解码
`ZAIImagesResponse`、model 读 `Request.Metadata["model"]`（注意与写入的
TransformerMetadata 不同字段）缺省 `image-generation`、`zai-img-<uuid>`、
`chat.completion`、逐图下载转 data URL 后 `strings.Cut("base64,")` 取 b64）、
`:182-235`（下载：data URL 原样（长度 > 5）、`http.DefaultClient` GET、
非 200 → `HTTP <code>`、Content-Type 缺省 image/png、StdEncoding base64）；
`llm/transformer/minimax/outbound.go:21-26`（Config）、`:39-56`（构造校验：
nil/空白 BaseURL → `base URL is required for MiniMax transformer`、缺 provider
→ `API key provider is required`，内嵌 OpenAI 失败包
`invalid MiniMax transformer configuration`，baseURL 以 v1 归一）、`:58-63`
（image 分派，其余委托）、`:65-145`（图片请求：Image/prompt 校验、model 缺省
规则（空或 dall-e-2 且未显式指定 → image-01）、subject_reference 解码再编码、
aspect_ratio、size 的 `strings.Cut('x')` + `strconv.ParseInt` 与 512-2048、
8 的倍数校验、显式 width/height 覆盖、seed/n/prompt_optimizer、
response_format b64_json→base64/空→url、路径 `/v1/` 与 base 尾部 `/v1` 去重、
Bearer、TransformerMetadata["model"]）、`:147-211`（响应：仅
`RequestType == image` 走图片，否则委托；≥400 → `MiniMax image API returned
HTTP <code>`；`base_resp.status_code` 非 0 → invalid response；created 为 0
时取当前秒；model 读 TransformerMetadata 缺省 image-01；url 在前 base64 在后；
空数据拒绝；id 缺省 `minimax-img-<created>`；`image.generation`）；
`internal/server/biz/channel_llm.go:489-496`（minimax 图片端点）、`:575-597`
（`newProviderChatOutbound`：zai/zhipu/zhipu_anthropic/zai_anthropic → zai
Version v4；xiaomi/xiaomi_anthropic → v1）、`:600-625`（家族版本路由，Xiaomi
恒用家族出站）、`:898-912`（xiaomi_anthropic / minimax_anthropic → Anthropic
Direct）、`:1029-1053`（zhipu_anthropic → PlatformZhipu、zai_anthropic →
PlatformZai）、`:1303-1317`（minimax 通道的家族出站）与
`channel_endpoint.go:209-215`、`:280-281`、`:290-291`、`:304-305` 的默认端点
集。

本次移植新增：`src/AxonHub.Llm/Transformer/Zai/ZaiOutboundTransformer.cs`
（构造与 URL 归一、TransformRequest 的完整阶梯、响应分派与委托，GLM 版本 /
恒思考 / effort 归并 / resolve 的静态表）、`ZaiImage.cs`（同类的图片 partial：
排序的 `map[string]any` 请求体、质量映射、响应与 data URL 下载；严格保留
“写入 TransformerMetadata、读取 Metadata”的参照字段错位）、
`src/AxonHub.Llm/Transformer/Minimax/MinimaxOutboundTransformer.cs`（构造校验、
图片请求/响应、subject_reference 经 `GoAnyJson` 重新编码、chat 委托与
SelectedApiKey 透传），以及 `ChannelOutboundFactory` 的 `zai` / `zhipu` /
`xiaomi` / `zai_anthropic` / `zhipu_anthropic` / `xiaomi_anthropic` /
`minimax` / `minimax_anthropic` 分支（chat 走 Zai v4/v1，anthropic/messages
走对应平台，`SessionIdProvider` 接 `RawRequest.SessionId`）。已接受差异与残余
裁剪见 C102。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| Zai：5 种 URL 归一、请求校验（nil / model / messages / compact / 非支持类型）、user_id 与 request_id 提取与 metadata 剥离、session 回退、thinking 阶梯（非 GLM、glm-5.3 none→low、effort 剥离）、response_format 三态、4 种 URL 覆盖（coding plan / 版本追加 / endpoint path / `##`）、user_id 6-128 rune 规则（含代理对计数）、版本/恒思考/归并/resolve 四张表、聊天响应委托、响应错误、构造校验 | `tests/AxonHub.Llm.Tests/ZaiOutboundTransformerTests.cs` | 59 |
| Zai 图片：排序请求体逐字节、quality 五态、size/user/watermark、Image/APIFormat/输入图/mask/prompt 拒绝、data URL 响应与 `zai-img-` ID、TransformerMetadata/Metadata 错位回退、解码与状态错误 | `tests/AxonHub.Llm.Tests/ZaiImageTests.cs` | 11 |
| MiniMax：构造三条文案、model 缺省四态、response_format 三态、尺寸解析三态、六种非法尺寸（Go `%q` 文案）、5 种路径、九键排序请求体（含 subject_reference 重排）、Image/prompt 拒绝、业务错误 1008、url→base64 顺序、created/id 回退、HTTP 400 / 空数据 / 解码错误、chat 请求与响应委托 | `tests/AxonHub.Llm.Tests/MinimaxOutboundTransformerTests.cs` | 31 |
| 通道接线：zai 家族 chat v4、xiaomi v1、四种 `*_anthropic` 的 anthropic/messages、session→request_id、minimax chat 与图片双路由、minimax_anthropic chat、八类缺 key 拒绝 | `tests/AxonHub.Api.Tests/ZaiMinimaxChannelWiringTests.cs` | 21 |

`AxonHub.Llm.Tests` 由 1745 增至 1846，`AxonHub.Api.Tests` 由 1001 增至 1022，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 2908 增至 3030。差异条目新增 C102（已接受）。

## 41. Bailian / Bailian-Responses / Bailian-Anthropic / Moonshot-Coding 出站与通道接线（52 例，2026-11-01）

参照路线：`llm/transformer/bailian/outbound.go:19-22`（Config：BaseURL /
APIKeyProvider）、`:33-40`（旧构造，静态 key）、`:43-61`
（`NewOutboundTransformerWithConfig`：nil → `invalid Bailian transformer
configuration: config is nil`；以内嵌 `openai.Config{PlatformOpenAI,
ReasoningFieldContent}` 构造，失败包 `invalid Bailian transformer
configuration`）、`:64-88`（TransformRequest：先 `mergeConsecutiveToolCallMessages`，
`reasoning_effort == "none"` 时值拷贝并清空 effort，委托内嵌出站后
`sjson.SetBytes(body, "enable_thinking", false)`，失败包
`failed to set Bailian enable_thinking`）、`:90-135`（合并：连续 assistant +
非空 tool_calls + 空 content，且 tool_call_id / name / refusal / message_index /
tool_call_name / tool_call_is_error / reasoning_content / reasoning_signature /
redacted_reasoning_content / cache_control 全空的相邻消息合并 tool_calls；仅在
发生过合并时返回浅拷贝，调用方请求不变）、`:137-155`（可合并判定）、
`:157-163`（空 content 判定：Content 无值且 MultipleContent 为空）、
`:166-177`（TransformStream：内嵌流再包 `bailianStreamFilter`）；
`llm/transformer/bailian/stream_filter.go:10-25`（toolCallKey{choiceIndex,
callIndex} 与过滤器字段）、`:27-32`（构造）、`:34-71`（Next：pending 优先、
nil 跳过、`llm.DoneResponse` 按指针透传、filter 后 pending 优先）、
`:85-131`（filterResponse：首个 tool call 之前整条直通；之后抽出并缓存所有
choice 的文本、记录最后文本 choice，finish 且缓存非空时合成文本 chunk 排在
finish 之前）、`:133-154`（filterToolCalls：空参数跳过；`TrimSpace(arg) == "{}"`
且该 (choice, call) 已累计非空时清空参数，否则累加）、`:156-187`
（extractTextDelta：清空 Content、抽出并移除 text parts，保留其它 part）、
`:189-212`（buildTextChunk：只复制 ID / Object / Created / Model /
SystemFingerprint / ServiceTier，单 choice 带文本 delta）；
`internal/ent/channel/channel.go:251-254`（四个类型常量）；
`internal/server/biz/channel_llm.go:1133-1143`（bailian → Bailian 出站）、
`:1145-1155`（bailian_responses → Responses 出站）、`:1157-1167`
（bailian_anthropic 与 moonshot_coding → Anthropic Direct）与
`channel_endpoint.go:313-316` 的默认端点集（chat / responses / messages）。

本次移植新增：`src/AxonHub.Llm/Transformer/Bailian/BailianOutboundTransformer.cs`
（构造包装、合并的浅拷贝语义、none 的浅拷贝与 `enable_thinking` 顶层字节补丁、
其余方法委托内嵌 OpenAI 出站，以及内部 `BailianStreamFilter`：pending 队列、
文本缓存、按 (choice, call) 的参数累计、Done 按引用透传、Go White_Space 的
TrimSpace 表）、`src/AxonHub.Llm/Json/RawJsonModelPatch.cs` 泛化出的
`RawJsonPatch.SetTopLevelRaw`（按顶层键替换或追加原始 JSON 值，替代 sjson
单键设置）与 `ChannelOutboundFactory` 的 `bailian` / `bailian_responses` /
`bailian_anthropic` / `moonshot_coding` 分支。已接受差异与残余裁剪见 C103。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| Bailian：构造两条文案、reasoning_effort 12 态映射与调用方请求不变、OpenAI 入站 none、连续工具调用合并与 14 类阻止合并且各段独立合并、chat 线路形状（URL / Bearer / ApiFormat）、响应 / 错误委托、流过滤端到端 | `tests/AxonHub.Llm.Tests/BailianOutboundTransformerTests.cs` | 34 |
| Bailian 流过滤：tool call 后文本缓存与 finish 前释放、重复 `{}` 参数、无 tool call 直通、多 choice 文本与非文本 part、最后 choice 索引、按 (choice, call) 累计、Done 引用透传、nil 跳过、未触碰响应引用透传 | `tests/AxonHub.Llm.Tests/BailianStreamFilterTests.cs` | 9 |
| 通道接线：bailian chat 出站与 none→`enable_thinking=false`、bailian_responses 的 `/responses`、bailian_anthropic / moonshot_coding 的 `/messages`、四类缺 key 拒绝 | `tests/AxonHub.Api.Tests/BailianChannelWiringTests.cs` | 9 |

`AxonHub.Llm.Tests` 由 1846 增至 1889，`AxonHub.Api.Tests` 由 1022 增至 1031，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 3030 增至 3082。差异条目新增 C103（已接受）。

## 42. ModelScope 异步图片任务出站与通道接线（48 例，2026-11-01）

参照路线：`llm/transformer/modelscope/outbound.go:16-21`（Config：BaseURL /
APIKeyProvider / HttpClient）、`:23-33`（OutboundTransformer 内嵌 OpenAI 出站与
BaseURL）、`:35-43`（旧构造，静态 key）、`:45-67`（`NewOutboundTransformerWithConfig`：
nil → `invalid ModelScope transformer configuration: config is nil`；以内嵌
`openai.Config{PlatformOpenAI, ReasoningFieldContent}` 构造，失败包 `invalid
ModelScope transformer configuration`）、`:69-87`（TransformRequest：RequestType ==
image → `buildImageRequest`，否则浅拷贝并清空 Metadata 后委托内嵌 OpenAI 出站）；
`image.go:20-36`（路径、图片任务 metadata key、默认尺寸 2048x2048 与 2s / 15m /
2m 三个超时）、`:38-52`（`imageSizeOrDefault`：空或 auto → 默认尺寸）、
`:54-76`（imageResponse：created / data / task_id / task_status / output_images /
message / errors）、`:78-157`（buildImageRequest：API format 仅
`openai/image_generation` 与 `openai/image_edit`；prompt TrimSpace 非空；mask
拒绝；输入图逐项要求 base64 data URL；请求体 map 排序 model/prompt/size 与可选
image_url，单图字符串、多图数组；`POST {base}/images/generations`，
`Accept: application/json`、`Content-Type: application/json`、
`X-ModelScope-Async-Mode: true`、Bearer；ApiFormat
`modelscope/image_generation`；metadata 写 model 与 api_key）、`:159-169`
（TransformResponse 按 `Request.ApiFormat` 分派图片任务）、`:171-212`
（transformImageResponse：任务成功后按 data > output_images > task_id 取结果，
`imageResponseFromData` 的 id `modelscope-image-<created>`、object
`chat.completion`、RequestType image、model 取 metadata 缺省
`image-generation`、created 0 → 当前秒）、`:214-261`（waitImageTask：默认 2s
轮询、15m 或调用方 deadline 预算，超时 `timed out`，取消优先）、`:263-314`
（getImageTask：`GET {base}/tasks/{url.PathEscape(id)}`、
`X-ModelScope-Task-Type: image_generation`、Bearer；SUCCEED / SUCCESS / FAILED
三种状态拼写）、`:316-324`（metadata api_key 回退）、`:326-333`（任务 body
解码）、`:335-388`（结果内联图片与 output_images 的转 base64）、`:390-424`
（parseOutputImages：元素先解码 any，空串报错、null 跳过、非对象报类型错误；
字符串或 `{url,b64_json}`；字段先精确后大小写不敏感匹配）、`:426-480`
（downloadImageBase64：data URL 仅接受 base64，否则下载，非 200 →
`failed to download ModelScope output image: HTTP %d`，2m 超时）、`:482-489`
（DetectContentType 的图片子集，未知回退 `image/png`）、`:491-505`
（`message` > `errors.message` > `unknown error`）；
`internal/server/biz/channel_llm.go:1096-1108`（modelscope → ModelScope 出站）、
`channel_endpoint.go:309-312`（chat 与图片端点分派）、
`internal/ent/channel/channel.go:250`（类型常量）。

本次移植新增：`src/AxonHub.Llm/Transformer/Modelscope/ModelscopeOutboundTransformer.cs`
（nil 构造文案、内嵌 OpenAI 出站的包装、chat 浅拷贝与 Metadata 清空、
Image 请求分派、图片响应分派与其余方法委托）、
`src/AxonHub.Llm/Transformer/Modelscope/ModelscopeImage.cs`（图片请求构造与
排序字段、任务轮询与调用方取消、状态与错误回退、output_images 两步解码、
URL 下载转 base64、data URL 嗅探）、`src/AxonHub.Llm/Transformer/Shared/GoUrl.cs`
（Go `url.PathEscape` 逐字节复刻）与 `src/AxonHub.Llm/Transformer/Shared/GoStrings.cs`
（Go `strings.TrimSpace` 的 White_Space 表），`ApiFormats.ModelScopeImage`
常量，`HttpRequestData.RequestCancellationToken` 调用方 token 接缝
（`Pipeline.NotStream` 写入），以及 `ChannelOutboundFactory` 的 `modelscope`
分支。已接受差异与残余裁剪见 C104。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| ModelScope 图片请求：排序请求体逐字节、size 四态、单图 / 多图 / 非 base64 / mask / prompt / ApiFormat 拒绝、三个头与 Bearer、任务 metadata；chat 委托与 Metadata 清空；响应委托与图片响应分派；内联 data / output_images 三种分支、失败文案、轮询状态拼写、PathEscape 任务 id、超时与取消、key 复用与构造校验 | `tests/AxonHub.Llm.Tests/ModelscopeOutboundTransformerTests.cs` | 44 |
| 通道接线：chat 与图片双路径、图片保持原始 BaseURL、异步头与 ApiFormat、缺 key 拒绝 | `tests/AxonHub.Api.Tests/ModelscopeChannelWiringTests.cs` | 4 |

`AxonHub.Llm.Tests` 由 1889 增至 1933，`AxonHub.Api.Tests` 由 1031 增至 1035，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 3082 增至 3130。差异条目新增 C104（已接受）。

## 43. Doubao / Volcengine 出站、Seedance 客户端面、通道接线与端点（108 例，2026-11-01）

参照路线：`llm/transformer/doubao/outbound.go:23-31`（Config）、`:52-68`
（旧构造：base URL / API key 为空文案）、`:70-115`（`NewOutboundTransformerWithConfig`：
nil → `invalid Doubao transformer configuration: config is nil`；raw URL 的
`##` 后缀与 EndpointPath 原样；默认 `NormalizeBaseURL(..., "v3")`；内嵌
`openai.Config{PlatformOpenAI, ReasoningFieldContent}`，失败包 `failed to create
Doubao outbound transformer`）、`:117-130`（Doubao Request / Thinking 扩展）、
`:132-237`（TransformRequest：按 RequestType 分派 chat / embedding / image /
video，compact 拒绝，其余 `<type> is not supported`；chat 委托内嵌 OpenAI 后
删除 metadata，metadata.user_id 取 6–128 rune 写入 user_id（不足 6 省略、超过
128 截断），metadata.request_id 缺省 `req_<unix>`，以 sjson 风格补丁追加到
请求体末尾，顺序 user_id 再 request_id）、`:239-337`（buildImageGenerationAPIRequest：
model/prompt/response_format `b64_json`/stream false，image 单图字符串或多图
data URL 数组，n、size、quality `hd` → `guidance_scale` 7.5 / `standard` → 2.5、
response_format 覆盖、user，POST `{base}/images/generations`，metadata model）、
`:339-350`（图片嗅探未知回退 `image/png`）、`:352-360`（TransformResponse 按
embedding / video / 默认委托分派）；`embedding.go:15-31`（多模态请求结构声明
顺序 model/input/encoding_format/dimensions）、`:34-78`（字符串与字符串数组
映射 text 项，POST `{base}/embeddings/multimodal`，Bearer）、`:81-104`（响应
结构）、`:107-165`（响应映射：data 单元素 index 0、usage 仅在 prompt>0 或
total>0、nil 响应与 HTTP≥400 分支、空 body 与 `failed to unmarshal embedding
response:` 前缀）、`:169-188`（buildMultimodalInput）；`video_outbound.go:18-136`
（Seedance create：Go map 排序请求体 model/content/duration 四舍五入/ratio/
resolution/frames/seed/generate_audio/camera_fixed/watermark/draft/service_tier/
execution_expires_after，POST `{base}/contents/generations/tasks`，metadata
model）、`:138-182`（size → ratio/resolution 推断表与 parseSize）、`:184-203`
（GET 任务）、`:205-295`（任务响应：status 小写去空格缺省 `queued`、
succeeded/failed 的 updated_at 缺省当前秒、content.video_url、usage、duration
取整、framespersecond、模型回退）、`:296-326`（DELETE 任务与
llmReqModelOrFallback）；`video_inbound.go:34-50`（Seedance 客户端 create 结构）、
`:52-113`（Content-Type 缺省 `application/json` 与子串校验、model/content 必填、
duration 数字转字符串、Messages `[]`、Modalities `["video"]`、Stream false、
RawRequest 原对象、RequestType video、ApiFormat seedance/video）、`:115-141`
（客户端响应结构）、`:143-219`（create 仅 `{id}`；get 结构顺序 id/model/status/
content/usage/created_at/updated_at/seed/resolution/ratio/duration/
framespersecond/service_tier，updated_at 缺省当前秒，duration 取整，Headers
Content-Type + Cache-Control）、`:221-232`（流式拒绝、错误委托 OpenAI 信封）；
`internal/server/biz/channel_llm.go:598-604`（四类通道的 family chat 出站）、
`:720-728`（doubao/volcengine 主出站）、`:1003-1015`（doubao_anthropic 主出站 =
Anthropic `PlatformDoubao`；volcengine_anthropic = Direct）、
`channel_endpoint.go:271-275`（doubao chat + seedance）、`:300-301`（volcengine
chat、volcengine_anthropic messages）、`internal/ent/channel/channel.go:223-224`
（类型常量）、`internal/server/biz/video.go:191`（doubao 任务缺省
seedance/video）、`internal/server/api/doubao.go:42-160` 与
`internal/server/routes.go:253-258`（`/doubao/v3` 的 create/get/delete；
gin `c.Data` 只回写 Content-Type，转换器的 Cache-Control 不落到客户端）。

本次移植补齐：`src/AxonHub.Llm/Transformer/Doubao/DoubaoOutboundImage.cs`
（Doubao 专用图片请求构造：恒 `/images/generations`、guidance_scale 两档、
b64_json、stream false、单图/多图 data URL、metadata model）、
`DoubaoOutboundEmbedding.cs` 的 nil 响应守卫，
`DoubaoOutboundTransformer.cs` 的 `GoStrings` 引用，以及测试侧替换
`VideoServiceTests` 的 doubao 回退用例（断言解析出
`DoubaoOutboundTransformer` 与 v3 任务 URL）。既有已实现对参照的
`outbound.go` / `embedding.go` / `video_outbound.go` / `video_inbound.go`、
`DoubaoVideoEndpoint`（`/doubao/v3` 三条路由）与 `ChannelOutboundFactory`
四类通道分支。已接受差异与残余裁剪见 C105。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| Doubao chat / 图片 / 构造：base URL 与 API key 文案、`##` raw URL、endpointPath、v3 归一、metadata 删除、user_id 的 6–128 rune 规则与截断、request_id 生成与追加顺序、图片排序请求体逐字节、guidance_scale 两档、单图/多图/未知字节嗅探、prompt 与 image 必填、chat 响应与错误委托 | `tests/AxonHub.Llm.Tests/DoubaoOutboundTransformerTests.cs` | 30 |
| 多模态 embedding 与 Seedance 出站：字段序请求体、字符串数组映射、token 输入空 input、响应映射与 usage 条件、错误分支；create 排序字节与时长取整、size 推断表与不可映射文案、GET/DELETE 线形状、任务响应状态归一与缺省时间、create 仅 `{id}` | `tests/AxonHub.Llm.Tests/DoubaoEmbeddingAndVideoOutboundTests.cs` | 42 |
| Seedance 客户端面：create 请求映射（duration 字符串化、Messages/Modalities/Stream/RawRequest）、Content-Type 三态、model/content 必填、解码与根类型文案、create 与 get 响应字节、updated_at 缺省、nil 与流式拒绝、OpenAI 错误信封 | `tests/AxonHub.Llm.Tests/DoubaoVideoInboundTransformerTests.cs` | 20 |
| 通道接线：doubao/volcengine chat 与 Seedance 共用出站、doubao_anthropic 的 Anthropic Doubao 平台与 chat 回退、volcengine_anthropic 的 Direct 平台与 chat 回退、缺 key 文案 | `tests/AxonHub.Api.Tests/DoubaoChannelWiringTests.cs` | 11 |
| `/doubao/v3` 端点胶水：GET 渲染 Seedance 任务字节、400 `invalid id`、500 错误信封、DELETE 204 与 v3 上游 URL；VideoService 的 doubao 回退解析 | `tests/AxonHub.Api.Tests/DoubaoVideoEndpointTests.cs`、`VideoServiceTests.cs` | 5 + 1 |

`AxonHub.Llm.Tests` 由 1933 增至 2025，`AxonHub.Api.Tests` 由 1035 增至 1051，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 3130 增至 3238。差异条目新增 C105（已接受），C010 由已解决状态更新，
C082 的 doubao 裁剪由本切片闭合。

## 44. ZenMux 原生视频任务出站与通道接线（87 例，2026-11-01）

参照路线：`llm/transformer/zenmux/outbound.go:17`
（`ErrVideoTaskDeleteUnsupported`）、`:19-23`（Config）、`:32-37`（旧构造
baseURL/apiKey）、`:39-79`（`NewOutboundTransformerWithConfig`：nil →
`config is nil`；缺 provider → `API key provider is required`；TrimSpace
base 为空 → `base URL is required`；EndpointPath TrimSpace 缺省 `/videos`
并补前导斜杠；`##` 后缀只剥离、默认路径按 v1 归一、自定义路径按空版本归一；
内嵌 `openai.Config{PlatformOpenAI, EndpointPath "/chat/completions"}`，失败包
`failed to create ZenMux chat transformer`）、`:81-83`（APIFormat =
zenmux/video）、`:85-91`（TransformRequest：非 video 委托内嵌 OpenAI 出站）、
`:93-99`（TransformResponse：仅当 `response.Request.APIFormat == zenmux/video`
时解析 create）、`:101-107`（流式与聚合全部委托）；`video_types.go:5-50`
（nativeContent / nativeMediaURL / nativeCreateRequest 的字段声明序与
`omitempty` 指针语义、nativeVideoResponse / nativeVideoContent）；
`video.go:15-104`（buildVideoRequest：video、model、content、service_tier、
execution_expires_after 的校验序与文案；ratio / resolution 皆空时由 size
推断；原生字段映射；extra_body 仅在 TrimSpace 非空时合并——非对象或 null 报
`extra_body must be a JSON object`、拒绝 service_tier /
execution_expires_after、解码原生 body、原生字段覆盖 extra、重新编码为排序键
并压缩值）、`:106-137`（buildNativeContent：text 非空且无 media；image_url
的 URL 非空、无 text/media、role ∈ {空, first_frame, last_frame,
reference_image}；video_url 非空、无 MIME、role == reference_video；
audio_url 非空、role == reference_audio；其余
`unsupported content type %q`）、`:139-146`（validImageRole）、`:148-154`
（parseCreateResponse）、`:156-168`（BuildGetVideoTaskRequest：空白 id 报
`providerTaskID is required`，GET `{base}{path}/{id}`，仅 Accept + Bearer）、
`:170-176`（ParseGetVideoTaskResponse）、`:178-180`（delete 恒返回哨兵错误）；
`video_response.go:14-23`（parseDuration：nil → nil，ParseInt 失败或 ≤0 →
`duration must be a positive integer`）、`:25-51`（parseNativeVideoResponse：
nil → `http response is nil`、解码失败包 `failed to unmarshal ZenMux video
<operation> response`、空白 id 报 `missing id in ZenMux video response`、
content 非空且非 `null` 时两步解码（失败包 `invalid ZenMux video content`）、
status 小写去空格后必须 ∈ {queued, running, succeeded, failed}，否则用原始
未 trim 的状态报 `unsupported ZenMux video status %q`）、`:53-78`
（toLLMVideoResponse：model 缺失时从请求 metadata 回退、content 仅在有
ParsedContent 时透出、error 结构化、Object `video`、Choices 空数组）；
`size.go:8-29`（size 推断表 256x256→1:1/480p、1280x720、720x1280、1920x1080、
1080x1920、640x480、480x640）、`:31-50`（parseSize：TrimSpace + ToLower、单
`x` 切分、Atoi 正整数）；通道与端点：`internal/server/biz/channel_llm.go:56-58`
与 `:695-705`（四类 ZenMux 的默认 base URL：openai/responses/video →
`https://zenmux.ai/api/v1`，anthropic → `https://zenmux.ai/api/anthropic`，
gemini → `https://zenmux.ai/api/vertex-ai`）、`:1269-1279`（plain zenmux 主
出站 = OpenAI）、`:789-799`（zenmux_responses = Responses）、`:898`
（zenmux_anthropic = Anthropic Direct）、`:1293-1302`（zenmux_video = ZenMux
原生）、`:511-520`（端点配置了 zenmux/video 的任一 ZenMux 类型构建原生出站，
否则 `api_format %q is only supported by ZenMux channel types`）、
`channel_endpoint.go:279-284`（四类 ZenMux 的端点集合）；
`internal/server/biz/video.go:118-145`（DeleteTask：delete 返回哨兵错误时本地
置 canceled，写入失败包 `failed to cancel video task locally: %w`；正常路径
上游 delete 后 best-effort 本地取消）、`:147-224`（loadTask：最新带
format + external id 的执行行、非视频格式拒绝、doubao 之外的缺省
openai/video）。

本次移植新增 `src/AxonHub.Llm/Transformer/Zenmux/ZenmuxOutboundTransformer.cs`
（配置与构造校验、base URL 归一、内嵌 OpenAI chat 出站与 `SelectedApiKey`
回退、原生 create/poll/delete、extra_body 合并、size 推断、字段序线路模型）与
`ZenmuxVideoResponse.cs`（create/poll 解码与统一视频响应），
`ChannelOutboundFactory` 的四类 ZenMux 分支与默认 base URL 助手，
`VideoService` 的 zenmux/video 解析与本地取消分支。已接受差异与残余裁剪见
C106 与 C082。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 原生 create/poll/delete：字段序请求体逐字节、Bearer 与 metadata model、默认 `/videos` 与 v1 归一、自定义 EndpointPath、`##` 原始 URL、size 推断与显式字段优先、不可映射文案、校验序与文案、extra_body 排序合并与拒绝、chat 委托、create 响应与 model 回退、nil/畸形/缺 id/未知状态、content 两步解码与失败任务、delete 哨兵、构造校验与 key 溯源 | `tests/AxonHub.Llm.Tests/ZenmuxOutboundTransformerTests.cs` | 62 |
| 通道接线：四类 ZenMux 的默认 base URL、plain zenmux 的 OpenAI 主出站与共享 openai/video、plain zenmux 的 Responses 分派、zenmux_responses、zenmux_anthropic 的 Direct 与 `/v1/messages`、zenmux_video 原生主出站与 chat 委托、zenmux/video 在四类通道的分派、anthropic 型的默认视频 URL、自定义 base URL、缺 key 文案 | `tests/AxonHub.Api.Tests/ZenmuxChannelWiringTests.cs` | 18 |
| VideoService：最新 zenmux/video 执行格式、跳过无外部 id 的新执行、持久化 openai/video 选择 OpenAI 出站、非视频格式拒绝、legacy 回退 openai/video、zenmux 本地取消零上游调用、本地取消写入失败的致命包装 | `tests/AxonHub.Api.Tests/VideoServiceTests.cs` | 7 |

`AxonHub.Llm.Tests` 由 2025 增至 2087，`AxonHub.Api.Tests` 由 1051 增至 1076，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 3238 增至 3325。差异条目新增 C106（已接受 + 端点门控残余），C010 的
zenmux 视频变换器项关闭（`zenmux_gemini` 随 Gemini/Vertex 切片），C082 的
zenmux 项闭合（delete-unsupported 本地取消已锁定）。

## 45. GitHub Copilot 出站、令牌交换与通道接线（131 例，2026-11-01）

参照路线：`llm/transformer/openai/copilot/outbound.go:26`（`^gpt-(\d+)`）、
`:28-49`（常量与默认头：base URL `https://api.githubcopilot.com`、`/chat/completions`、
`Editor-Version` / `Editor-Plugin-Version` / `User-Agent` / `Openai-Intent` /
`Copilot-Integration-Id` / `X-Github-Api-Version` / `X-Vscode-User-Agent-Library-Version` /
`Copilot-Vision-Request` / `X-Initiator`，以及 `vscode/1.95.0`、`copilot-chat/0.26.7`、
`GitHubCopilotChat/0.26.7`、`conversation-edits`、`vscode-chat`、`2025-04-01`、
`electron-fetch` 七个默认值）、`:69-95`（构造：nil provider 报 `token provider is
required`、空 base URL 取默认、`TrimSuffix` 只裁一个尾部斜杠、内嵌 Responses 出站用
空静态 key、失败包 `failed to create responses transformer: `）、`:97-99`（APIFormat
恒 `openai/chat_completion`）、`:101-158`（TransformRequest：nil / model / messages
三条校验；`usesResponsesAPI` 为真时走 Responses 路径；token 失败包 `failed to get
copilot token: `；`RequestFromLLM(ReasoningFieldAll)`；marshal 失败包 `failed to
marshal request: `；`base + /chat/completions`；Content-Type 与 Accept；七个小写化
头；视觉头 `true`；`X-Initiator`；Bearer）、`:160-168`（SetCopilotHeaders）、
`:170-177`（normalizeInitiator：TrimSpace + ToLower，仅 user/agent 有效）、
`:180-206`（inferCopilotInitiator：空消息 → user；最后一条的 Attribution 优先；
role != user → agent；最后一块 MultipleContent 为 tool_result → agent；否则 user）、
`:208-221`（resolveCopilotInitiator：RawRequest 的 `X-Initiator` 仅当归一化后有效才
覆盖）、`:223-243`（hasVisionContent：Content 的 data URL、part 的 image_url /
ImageUrl / 文本中的 data URL）、`:245-247`（isImageDataURL 大小写敏感前缀
`data:image/`）、`:249-322`（TransformResponse：nil 报 `http response is nil`；≥400
的三种 body 文案 `(empty body)` / `(first 100 chars: %s, total length: %d)` /
`(body: %s, length: %d)`；空 body 报 `response body is empty`；Responses 判定
`output` 存在或 `object == "response"`；包装 `{"response": ...}` 用 `.Raw` 解包后
重新判定；命中委托内嵌 Responses 出站；否则 OpenAI 解码并 `ToLLMResponse`，失败包
`failed to unmarshal response: `）、`:324-408`（TransformStream：窥视首事件，`type`
以 `response.` 开头即 Responses 流；非 Responses 流懒建 OpenAI 出站并委托，失败包
`failed to create openai transformer: `；Responses 流经 Map：空 Data 原样、
`[DONE]` 前缀原样、`convertCopilotStreamEvent` 返回 nil 即消费，再过滤空事件后委托
Responses 出站）、`:413-524`（convertCopilotStreamEvent：`response.output_item.added`
把 `item.call_id` 映射为自身并登记原 id、更新最近 call id、强制 `item.id = call_id`、
缺 name 补 `function`；`response.function_call_arguments.delta` 与 `.done` 按映射或
最近 call id 改写 `item_id`，done 另写 `call_id`；`response.output_item.done` 强制
`item.id`；其余或解析失败原样返回）、`:527-565`（TransformError：nil → 500 +
`Internal Server Error` + `api_error`；`error` / `errors` 任一有 message 即采用；
否则状态文本 + `api_error`）、`:568-594`（AggregateStreamChunks：
`isResponsesAPIStream` 命中四个 `"type":"response.*"` 字面量即走 Responses 聚合，
否则 OpenAI 聚合）、`:597-632`（transformResponsesRequest：委托 Responses 请求、
失败包 `failed to transform request for Responses API: `；Copilot token 覆盖
Bearer；七个 Copilot 头 + 视觉头 + initiator）、`:636-657`（usesResponsesAPI：
小写化；`grok-` 前缀恒真；`^gpt-(\d+)` 且 major ≥ 5 且非 `gpt-5-mini`）、
`:659-701`（prependedStream 首事件透传）；`token_exchanger.go:21-25`（默认端点
`https://api.github.com/copilot_internal/v2/token`、30s 超时、5m 提前过期缓冲）、
`:80-150`（Exchange / ExchangeWithClient：空 token；client 回退；SHA-256 小写十六
进制缓存键；缓存未过期直接返回；单飞 `key:ptr:endpoint`；`context.WithoutCancel`
下交换；成功后写缓存）、`:152-159`（tokenCacheKey 与 exchangeSingleflightKey 的
`%p`）、`:161-194`（exchange：GET + `Authorization: token <access>` + Accept JSON、
30s 超时、非 2xx 文案、解析失败包 `failed to parse token response: `、空 token 与
缺 expires_at 文案）；`token_provider.go:26-53`（设备流配置：GitHub device/token
端点、`Iv1.b507a08c87ecfe98`、`read:user`）与 `:55-60`（委托 DeviceFlowProvider）；
`internal/server/biz/channel_llm.go:666-668`（github_copilot 与 xai_subscription
共用 OAuth 准入，文案 `missing oauth credentials for channel %s`）、`:1218-1275`
（要求 `strings.TrimSpace(c.Credentials.APIKey)` 非空（`github_copilot channel %s
has no credentials`）；typed OAuth 重新编码时省略 `IdToken`，失败包 `failed to
encode github_copilot oauth credentials for channel %s: `；`ParseCredentialsJSON`
失败包 `github_copilot channel %s has invalid credentials: `；`NewTokenProvider`
失败包 `failed to create CopilotTokenProvider: `；`NewOutboundTransformer` 失败包
`failed to create github_copilot outbound transformer: `；随后
`setupAutoRefresh(5m/5m)`）、`internal/server/biz/channel_endpoint.go:322`（端点表
只有 `openai/chat_completion`）、`internal/ent/channel/channel.go:257`（类型常量）。

本次移植新增
`src/AxonHub.Llm/Transformer/OpenAI/Copilot/CopilotOutboundTransformer.cs`（构造与
base URL 归一、chat 与 Responses 两条请求路径、Copilot 头集合、initiator 推断与
覆盖、视觉判定、响应解包与错误文案、流首事件判定、call-id 修复与聚合分派）、
`CopilotTokenProvider.cs`（GitHub 设备流常量 + 令牌交换）、
`CopilotTokenExchanger.cs`（缓存、单飞、交换与错误文案），以及
`ChannelOutboundFactory` 的 `github_copilot` 分支与 OAuth 必经准入。已接受差异与
残余裁剪见 C107。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 出站变换器：构造四条路径与单斜杠裁剪、模型路由表 19 行、chat 线请求头/认证/请求体、nil/model/messages/token 失败、自定义 base URL、Responses 路由三模型与失败包装、视觉头 5 行、initiator 转发 6 行 / 推断 12 行、data URL 12 行、视觉内容 15 行、响应成功与会话解包、nil/400/500/空 body/非法 JSON/百字符前缀、错误信封三态与状态文本回退、四类流事件修复与畸形透传、Responses 流判定四例、chat 聚合与流委托 | `tests/AxonHub.Llm.Tests/CopilotOutboundTransformerTests.cs` | 110 |
| Copilot 令牌交换：请求形状（GET/端点/token 认证/Accept）、按 access token 缓存、5 分钟缓冲内重换、空 token、非 2xx、非法 JSON、空 token 响应、缺 expires_at、失败不入缓存 | `tests/AxonHub.Llm.Tests/CopilotTokenExchangerTests.cs` | 9 |
| Copilot 令牌提供者：GitHub 设备流常量下的交换、缺凭据文案、凭据替换 | `tests/AxonHub.Llm.Tests/CopilotTokenProviderTests.cs` | 3 |
| 通道接线：typed OAuth + 占位 apiKey 建出站并用通道执行器交换、OAuth 内嵌 apiKey、Responses 路由、自定义 base URL、无 OAuth 的早退文案、仅 typed OAuth 的 `has no credentials`、畸形 legacy OAuth 的 `invalid credentials`、畸形 typed `expires_at` 的编码错误、无执行器时的 `http client is nil` | `tests/AxonHub.Api.Tests/GithubCopilotChannelWiringTests.cs` | 9 |

`AxonHub.Llm.Tests` 由 2087 增至 2209，`AxonHub.Api.Tests` 由 1076 增至 1085，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 3325 增至 3456。差异条目新增 C107（已接受），C010 的 github_copilot 分支
闭合。

## 46. Gemini OpenAI 出站、extra_body thinking 配置与 Google 原生工具过滤（114 例，2026-11-01）

参照路线：`llm/transformer/gemini/openai/outbound.go:20-25`（Config）、`:37-75`
（ThinkingBudget 的 int/string 双态与两条方向相反的 JSON 语义）、`:78-106`
（ThinkingConfig / GoogleExtraBody / ExtraBody）、`:108-156`（legacy 与 config
两条构造：两条必填文案、`NormalizeBaseURL("v1beta/openai")`、`PlatformGoogle` +
`ReasoningFieldContent`、失败包 `failed to create Gemini OpenAI outbound
transformer: `）、`:168-272`（thinking_config → reasoning_effort 的优先级与映射、
isGemini25Model、reasoningEffortToThinkingBudget、fillThinkingConfigFromReasoningEffort
的 level 清 budget 与 2.5 回落）、`:274-374`（TransformRequest：nil / 类型 /
model / messages 的校验序、浅拷贝后清空 Metadata、google_* 工具过滤与空表时
ToolChoice 复位、extra_body 解析与 thinking 填充后清空 ReasoningEffort、
RequestFromLLM、thought signature 回填、extra_body 结构序追加、两条头、Bearer、
`base + /chat/completions`、SkipInboundQueryMerge、APIFormat）、`:376-449`
（fillGeminiThoughtSignatureForGeminiOpenAIRequest：按 ID、按下标、消息级
ReasoningSignature 填首个 tool call、raw 直通）、`:451-517`（TransformError：
nil → 500、`[]` 信封、object 信封、raw body、空 body 委托）；`llm/tools.go:241-271`
（ContainsGoogleNativeTools / IsGoogleNativeTool / FilterGoogleNativeTools）；
`internal/server/biz/channel_llm.go:1109-1118`（构造包装）、
`channel_endpoint.go:253`（默认端点仅 chat）、`internal/ent/schema/channel.go:48`
（类型常量）。

本次移植新增
`src/AxonHub.Llm/Transformer/Gemini/OpenAI/GeminiOpenAiModels.cs`（ThinkingBudget
与转换器、ThinkingConfig / GoogleExtraBody / ExtraBody、Go 结构解码语义的
ParseExtraBody）、`GeminiOpenAiOutboundTransformer.cs`（构造、请求变换、thinking
映射、thought signature 回填、错误信封与 OpenAI 出站委托）、`Model/Tools.cs` 的
`GoogleToolFilter`，以及 `ChannelOutboundFactory` 的 `gemini_openai` 分支。已接受
差异与残余裁剪见 C108。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 出站变换器：构造 9（两条路径、URL 归一、nil config）、请求基础与校验 9（线路形状、Metadata 剥离、nil/model/messages/compact/image、审计 key）、ThinkingBudget 11（双态 3 + 解码 3 + 拒绝 5）、thinking 映射 18（表 11 + 阶梯 5 + null/未知 2）、extra_body 15（保留/覆盖/填充 3 + 行 6 + 空对象 1 + Gemini 3 1 + 不可解码 4）+ ParseExtraBody 9、thought signature 4、Google 工具过滤 3、错误信封 11（数组 1 + 对象 3 + 不可解析 5 + 空 body 1 + nil 1） | `tests/AxonHub.Llm.Tests/GeminiOpenAiOutboundTransformerTests.cs` | 89 |
| Google 原生工具过滤：`google_` 前缀 10 行、包含判定 5 例、过滤顺序与空结果、len==0 原样返回 | `tests/AxonHub.Llm.Tests/GoogleToolFilterTests.cs` | 19 |
| 通道接线：base URL 归一与 key 溯源、已归一 URL 两种写法、空 base URL 的包装文案、缺 key 早退、Metadata 与 extra_body 经工厂透传 | `tests/AxonHub.Api.Tests/GeminiOpenAiChannelWiringTests.cs` | 6 |

`AxonHub.Llm.Tests` 由 2209 增至 2317，`AxonHub.Api.Tests` 由 1085 增至 1091，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 3456 增至 3570。差异条目新增 C108（已接受）。

## 47. 管理面前端原样复用（2026-11-01）

范围决策：管理面前端不重写、不改造，直接从 `E:\GitHub\axonhub\frontend` 原样移入
`frontend/`。源仓库工作树干净，HEAD 为 `f272549f1342`，前端最后一次改动是
`9142eb6a`（2026-10-29，per-channel response timeout）。共 829 个文件；`cp -a`
复制后 `diff -r` 全量比对无差异（逐字节一致），未修改任何源码、依赖清单、
构建配置或 `frontend/.gitignore`。

结构与接缝：Vite + React 19 + TanStack Router/Query + Zustand + Tailwind；开发
服务器仍为 5173，`/admin`、`/oauth`、`/v1` 通过 Vite proxy 转发到
`http://localhost:8090`（可由 `VITE_API_URL` 覆盖），与后端端口约定一致。前端
自带的 `frontend/.gitignore` 随目录带入，`node_modules`、`dist`、`.env`、日志等
不入库。

边界：本切片只做文件迁移，不运行 `pnpm install` / 构建 / Playwright（遵守不运行
构建命令的约束），也未在 ASP.NET 宿主中挂载静态文件或 SPA fallback、未改
Dockerfile。前端依赖的 GraphQL schema 与 REST 契约尚未由 Orleans 侧实现；后续
管理面切片以 `frontend/src/gql/` 的既有查询与变更为服务端契约，不回改前端。
前端工具链不进入 `dotnet build` / `dotnet test` 门禁。

## 48. 管理面 GraphQL 基础设施与 allScopes（HotChocolate 16.8.0-p.9，6 例，2026-11-01）

参照路线：`internal/server/routes.go:161-168`（adminGraphqlGroup 挂
WithAdminGraphqlAuth + WithProjectID 与请求超时，`POST /admin/graphql` 交给
gqlgen handler）、`internal/server/gql/scopes.graphql:1-11`（ScopeInfo 三字段与
`allScopes(level: String): [ScopeInfo!]!`）、`internal/server/gql/scopes.resolvers.go:15-42`
（nil level 不过滤；非 nil 原样转 ScopeLevel 后过滤）、
`internal/scopes/scopes.go:1-220`（19 个 slug、system/project 两级、声明顺序与
AllScopes 的成员过滤）。

本次移植新增 `src/AxonHub.Api/Scopes/ScopeCatalog.cs`（19 条静态目录与
AllScopes）、`src/AxonHub.Api/GraphQL/AdminQuery.cs`（ScopeInfo 记录与
allScopes 解析器）、`src/AxonHub.Api/GraphQL/GraphQLSetup.cs`（HotChocolate
注册与内省请求拦截器）、`Program.cs` 的 `AddAxonHubGraphQL()` 与
`MapGraphQL("/admin/graphql")`，以及 `Directory.Packages.props` 的
HotChocolate.AspNetCore 16.8.0-p.9。前端契约
（`frontend/src/gql/graphql.ts:36` 的 GRAPHQL_ENDPOINT）保持不变。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| POST /admin/graphql：19 条 scope 的顺序/描述/层级、system 全量、project 十条、未知层级空列表、null 层级不过滤、ScopeInfo 内省字段 | `tests/AxonHub.Api.Tests/AdminGraphQLEndpointTests.cs` | 6 |

`AxonHub.Llm.Tests` 保持 2317，`AxonHub.Api.Tests` 由 1091 增至 1097，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 3570 增至 3576。差异条目新增 C109（已接受）。

管理面鉴权（WithAdminGraphqlAuth / WithProjectID）与请求超时尚未移植，当前
`/admin/graphql` 未鉴权；其余 SDL 成员按覆盖缺口表逐项移植。

## 49. Gemini 原生基础切片（模型、共享转换、MIME 推断、thought signature、usage 双向，69 例，2026-11-02）

参照路线：`llm/transformer/gemini/model.go`（GenerateContentRequest/Response、
Candidate/Content/Part/Blob/FileData/FunctionCall/FunctionResponse/Tool/ThinkingConfig/
SafetySetting/ImageConfig 等全模型，Tool 的 camel/snake 双键解码——functionDeclarations
优先、function_declarations 仅在空时生效，ToolJsonConverter 重编码时移除自身避免递归）、
`usage_json.go`（UsageMetadata 的 Cost 数字/引号数字/空三态解析）、
`convert.go:12-42`（extractTextFromContent 的 thought 过滤与换行连接、
extractPartsFromLLMMessage）、`:44-70`（角色双向）、`:94-137`（完成原因双向：STOP→
tool_calls/stop、MAX_TOKENS→length、SAFETY/RECITATION→content_filter）、`:139-242`
（图片/视频/音频/文档 media part 双向：data URL → InlineData、普通 URL → FileData 与
MIME 推断）、`:276-318`（audioFormatToMIMEType / audioMIMETypeToFormat 的十行表与
mp3 回退）、`:320-394`（tool choice 双向：AUTO/ANY/NONE、单名 named、多名
allowed_tools）、`:396-454`（thinkingBudgetToReasoningEffort 的 1024/8192 阶梯、
shouldUseThinkingLevelForBudget 的 gemini-3- 前缀与 32768 上限、
reasoningEffortToThinkingBudget(WithConfig) 的自定义映射优先与 medium 回退）、
`:457-564`（usage 双向：candidates + thoughts、cached → PromptTokensDetails、
thoughts → CompletionTokensDetails.ReasoningTokens、modality 双向、反向减 thoughts）、
`:506-519`（modality 大小写双向）、`:567-600`（TransformerMetadataKeySafetySettings /
TransformerMetadataKeyImageConfig 与 map[string]any 类型断言的 nil 语义）、
`image_mime.go`（ImageMimeType 的显式 MIME 优先、MediaMimeType 与 is*MimeType 前缀判定、
url.Parse 与 path.Ext 的最小复刻）、`thinking_signature.go`
（ContextEngineeringThoughtSignature 与工具调用 thought signature 的 set/get）、
`shared/gemini.go`（EncodeGeminiThoughtSignature 直通、DecodeGeminiThoughtSignature 的
GuessSignatureProvider 门控）、`outbound.go:16-87`（DefaultBaseURL/DefaultAPIVersion/
PlatformVertex、Config 与 clenupConfig 的默认填充与 /v1beta、/v1 尾部裁剪的
TrimSuffix 语义）。

本次移植新增 `src/AxonHub.Llm/Transformer/Gemini/GeminiModels.cs`、`GeminiConvert.cs`、
`GeminiMime.cs`、`GeminiThoughtSignatures.cs`、`GeminiOutboundConfig.cs`、
`src/AxonHub.Llm/Transformer/Shared/GeminiSignatures.cs` 与
`GoMimeTypesByExtension.cs`（Go 1.26 内建 MIME 表冻结）。已接受差异与残余裁剪见 C110。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 共享 thought signature：Encode 直通 2、Decode 6 组（Gemini base64 放行、Anthropic JWT 拒绝、null/空/未知）、编码-解码往返 1 | `tests/AxonHub.Llm.Tests/GeminiSignaturesTests.cs` | 9 |
| MIME 推断：ImageMimeType 4 组（无扩展/未知扩展/文档/视频扩展 → 空）、MediaMimeType 7 组（显式 MIME 优先、视频/音频/Word/Excel/PPT 从 URL、模态不匹配 → 空） | `tests/AxonHub.Llm.Tests/GeminiMimeTests.cs` | 11 |
| 文档/音频 Part 与 MIME 判定：文档 6 + 音频 4 + IsDocumentMimeType 表 12 | `tests/AxonHub.Llm.Tests/GeminiConvertTests.cs`（部分） | 22 |
| thinkingLevel 预算选择：7 + 4 组与 gemini-3 边界 2 | 同上 | 13 |
| usage 双向：缓存命中 3 + 双向一致 3 | 同上 | 6 |
| TransformerMetadata 提取：safety settings 4（多/空/单/nil）+ image config 4（含错误类型 → null） | `tests/AxonHub.Llm.Tests/GeminiMetadataExtractionTests.cs` | 8 |

`AxonHub.Llm.Tests` 由 2317 增至 2386，`AxonHub.Api.Tests` 保持 1097，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 3576 增至 3645。差异条目新增 C110（已接受）。

出站/入站变换器（outbound.go / outbound_convert.go / inbound.go /
inbound_convert.go）、流式、聚合器、embedding、image 与 vertex 接线按覆盖缺口表
后续切片（含 `zenmux_gemini`）。

## 50. Gemini 原生出站转换切片（outbound_convert.go 全量，150 例，2026-11-02）

参照路线：`llm/transformer/gemini/outbound_convert.go`（829 行，12 个函数）——
`convertLLMToGeminiRequest`（:19-23，委托 WithConfig(nil)）、
`convertLLMToGeminiRequestWithConfig`（:24-322，MaxTokens/MaxCompletionTokens →
maxOutputTokens、temperature/top_p/presence_penalty/frequency_penalty/seed/stop、
extra_body thinking 配置优先、reasoning_effort → thinkingBudget 阶梯、response_format
的 response_mime_type 与 responseJsonSchema、safetySettings 与 ImageConfig 提取、
messages 批量转换、tools → functionDeclarations 的 hasStrictFunctionTool 门控）、
`hasStrictFunctionTool`（:323-333）、
`convertLLMMessageToGeminiContent`（:334-484，role 双向、text/thought、图片/视频/音频/
文档 media part、tool call 的 id/name/args 与 tool call thought signature 回填、
消息级 thought signature）、
`convertLLMToolResultToGeminiContent`（:485-519，tool 消息 → user role +
FunctionResponse、findToolNameByToolCallID 回填 name、VertexAI 的 response 全量路径）、
`findToolNameByToolCallID`（:520-533）、`isPreviousContentToolResponse`（:534-548）、
`convertGeminiToLLMResponse`（:549-556，委托 WithState(0)）、
`deriveGeminiAnnotations`（:557-638，citationMetadata → UrlCitation、
groundingMetadata → GroundingChunk/Support/Segment 注释）、
`convertGeminiToLLMResponseWithState`（:639-688）与
`convertGeminiCandidateToLLMChoiceWithState`（:689-819，text/thought part →
content/reasoning、tool call 与 toolCallIndexOffset 全局编号、finish reason 映射、
grounding 存入 TransformerMetadata、tool call thought signature 回填、
`chatcmpl-`/`tc_` 前缀随机 UUID 生成）、
`extractJSONSchema`（:820-829，{"schema": ...} 包装剥壳，解码失败或 schema 为
null/缺失时原样返回原始值）。

本次移植新增 `src/AxonHub.Llm/Transformer/Gemini/GeminiOutboundConvert.cs` 与
`tests/AxonHub.Llm.Tests/GeminiOutboundConvertTests.cs`，并把
`tests/AxonHub.Llm.Tests/testdata/gemini/` 的三个请求 fixture（llm-simple/
llm-tools/llm-thinking）随切片迁入。同时修正第 49 节的两处模型问题：
`ImageConfig.AspectRatio`/`ImageSize` 改回 Go 的非空 string + `WhenWritingDefault`
（omitempty 语义），消除 TransformerMetadata 经 JSON 往返后 null 与 "" 的语义漂移；
`MessageContentPart.VideoUrl` 的重复类型 `VideoUrl` 与忠实 `VideoURL` 统一为 Go
单一的 `llm.VideoURL`（`URL`/`MIMEType` 字段命名）。已接受差异与残余裁剪见 C111。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 请求转换：basic 20、video 2、remote audio 1、response format 6、tools 12、multiple tool messages 1、edge cases 4、testdata 3、safety settings 4、image config 5 | `tests/AxonHub.Llm.Tests/GeminiOutboundConvertTests.cs` | 58 |
| 消息与工具结果：message to content 8、tool message 6、VertexAI 1、extract parts 6、image url to part 4、message thought signature 5 | 同上 | 30 |
| 响应转换：basic 6、thought signature 4、finish reasons 7、edge cases 4、citation 1、grounding 6+4+1、tool call thought signature 1 | 同上 | 34 |
| 预算与角色：budget 6、effort→budget 5、with config 9、角色双向 4+4 | 同上 | 28 |

`AxonHub.Llm.Tests` 由 2386 增至 2536，`AxonHub.Api.Tests` 保持 1097，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 3645 增至 3795。差异条目新增 C111（已接受）。

出站变换器本体与 embedding/image（outbound.go / embedding.go / image.go 与
vertex/constants.go 的 TransformRequest/Response/Error、SetAPIKey/SetBaseURL、
嵌入单/批请求与响应、图片请求构造/响应还原/错误/尺寸映射与 MIME 嗅探）由第 51 节
锁定；流式与聚合器（outbound_stream.go / aggregator.go）、
`ChannelOutboundFactory` 的 gemini 分支接线、vertex 接线与入站
（inbound.go / inbound_convert.go，含 convertLLMToGeminiResponse 与 3 例
response testdata）按覆盖缺口表后续切片（含 `zenmux_gemini`）。

## 51. Gemini 原生出站变换器切片（outbound.go / embedding.go / image.go，71 例，2026-11-02）

参照路线：`llm/transformer/gemini/outbound.go`（330 行）——`TransformRequest`
（:104-192，embedding/image/chat 分派与 compact 拒绝、model/messages 校验、
ConvertLlmToGeminiRequestWithConfig 委托、contents 空守卫、VertexAI 函数 ID
清理、Go JSON 编组、`x-goog-api-key` 认证与 `alt` 查询参数剥离、
SkipInboundQueryMerge）、`buildFullRequestURL`（:195-235，EndpointPath 覆盖、
流式 `streamGenerateContent?alt=sse`、版本回退与 `gemini-api-version` 路由值、
Vertex `{base}/v1/publishers/google/models/{model}:{action}` 与 `/v1/` 包含判定）、
`TransformResponse`（:238-270，嵌入/图片/错误状态/空体/解码四类分派）、
`TransformError`（:273-303，GeminiError 信封与 UTF-8 原体回退）、
SetAPIKey/SetBaseURL（:306-313）与 `clearFunctionIDsForVertexAI`（:318-330）；
`embedding.go`（314 行）——EmbedContent/BatchEmbedContents 单/批请求构造
（`models/` 前缀、TaskType 映射、outputDimensionality）、`buildEmbeddingURL`
三分支、批优先的响应解析与单/批还原、embeddingInputToTexts、
mapEmbeddingTaskType 九态映射、float32→float64；`image.go`（302 行）——图片
请求的 InlineData 分派与 responseModalities、aspectRatio/imageSize 尺寸映射与
`%dx%d` 像素推断、MIME 魔数嗅探、响应候选 image/* part 提取与 usage 还原；
`vertex/constants.go` 的 ProviderConfURL/ProviderID 两个常量。

本次移植新增 `src/AxonHub.Llm/Transformer/Gemini/GeminiOutboundTransformer.cs`
（主分部，连同 `GeminiOutboundEmbedding.cs`、`GeminiOutboundImage.cs` 两个分部）
与 `GeminiVertex.cs`，测试 `tests/AxonHub.Llm.Tests/GeminiOutboundTransformerTests.cs`
71 例（12 Facts + 10 Theories 共 59 InlineData）；并新增两个端口接缝：
`HttpRequestData.RouteValues`（由 `HttpRequestReader` 从 ASP.NET 路由值填充，
承载 `gemini-api-version` 死分支）与 `ApiFormats.GeminiEmbedding` 常量。
已接受差异与残余裁剪见 C112。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| embedding：单/批请求、维度、任务类型、nil embedding、响应单/批/错误、URL 三分支 3、任务映射 7 | `tests/AxonHub.Llm.Tests/GeminiOutboundTransformerTests.cs` | 18 |
| image：请求生成 5、编辑 2、响应还原 2、错误 3、aspectRatio 表 10 | 同上 | 22 |
| 端口补充与接线：imageSize 像素推断 16、MIME 嗅探 9、图片 URL 2、embedding 边界 3、chat smoke 1 | 同上 | 31 |

`AxonHub.Llm.Tests` 由 2536 增至 2607，`AxonHub.Api.Tests` 保持 1097，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 3795 增至 3866。差异条目新增 C112（已接受）。

## 52. Gemini 出站流式与聚合切片（outbound_stream.go / aggregator.go + 出站测试与通道接线，2026-11-02）

参照路线：`llm/transformer/gemini/outbound_stream.go`（82 行）——
`TransformStream`（:21-36，AppendStream 追加 DONE 哨兵、streamState 跨事件
共享、MapErr 流错误）、`TransformStreamChunk`（:38-45）与
`transformStreamChunkWithState`（:47-82，nil/空 → null、[DONE] →
DoneResponse、解码失败 → 流错误、responseID 稳定化 `chatcmpl-`+uuid、
ConvertGeminiToLlmResponseWithState 流式转换与 toolCallIndex 累积）；
`aggregator.go`（166 行）——`AggregateStreamChunks`（:23-108，跳过
nil/空/[DONE]/非法 JSON 的 chunk、responseID/modelVersion 首非空捕获、
按 candidate.Index 聚合 thought/text/toolCalls/inlineData/finishReason/
groundingMetadata/usage、全非法输入回退零响应）与 `buildGeminiResponse`
（:110-166，reasoning→text→toolCalls→inlineData 部件序、tool-call arguments
重解析（空/非法 → 省略）、thoughtSignature 往返、finishReason 空 →
completed=false+STOP、meta{ID, Usage, Completed}）。

测试面：参照 outbound_stream_test.go（8 函数）、aggregator_test.go（16 用例）、
outbound_test.go（10 函数）、strict_test.go（3 函数）与
outbound_issue2505_test.go（3 函数）逐条移植——TransformStreamChunk 7、
TransformStream 4（稳定 ID、toolCallIndex 累积、单事件多工具调用）、
聚合（变换器方法）6、FinishReasons 5、WithTestData 4（gemini-*.stream.jsonl
四夹具回放，含聚合校验）、聚合器 16（含 grounding 三组）、Cleanup 8、
AccountIdentity/OmitsMetadata、BuildFullRequestUrl 7（含 nil stream 与路由值
回退死分支）、构造器 4+3、ExtraBody thinking 5（整型预算/字符串预算/优先级/
畸形/空）、ReasoningEffortFallback、TransformResponse 多工具调用 5（含缺失
ID 生成）、ClearFunctionIDsForVertexAI、strict 工具选择 6 选择 × 3 模型、
非严格工具保默认模式 4 组 × 2、required 名称往返 3 组 × 2、developer 消息 3；
另新增通道接线 `GeminiChannelWiringTests.cs` 6 例（gemini 原生变换器与
x-goog-api-key、自定义 base、zenmux_gemini 的 ZenMux 默认 base 与存储 base、
gemini_vertex 顶点 URL 形状、无 key 拒绝）。

本次移植新增 `src/AxonHub.Llm/Transformer/Gemini/GeminiOutboundStream.cs` 与
`GeminiAggregator.cs`（移除出站主分部中的两处流式占位异常、
`ClearFunctionIDsForVertexAI` 转 internal），`ChannelOutboundFactory` 新增
gemini / zenmux_gemini / gemini_vertex 三分支（channel_llm.go:1327-1346，
ZenMux 默认 base 常量 `zenmuxGeminiBaseURL`）。测试
`tests/AxonHub.Llm.Tests/GeminiOutboundStreamTests.cs`（26）、
`GeminiOutboundTransformerCoreTests.cs`（27）、`GeminiStrictTests.cs`（8）、
`GeminiIssue2505Tests.cs`（3）与 `tests/AxonHub.Api.Tests/GeminiChannelWiringTests.cs`
（6）。已接受差异见 C113。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 流式 chunk/整流转换、DONE 哨兵、稳定 ID、toolCallIndex、聚合与夹具回放 | `tests/AxonHub.Llm.Tests/GeminiOutboundStreamTests.cs` | 26 |
| 配置清理、URL 构建、构造器、ExtraBody thinking、多工具响应、Vertex 清 ID | `tests/AxonHub.Llm.Tests/GeminiOutboundTransformerCoreTests.cs` | 27 |
| strict 工具选择、非严格默认模式、required 名称往返 | `tests/AxonHub.Llm.Tests/GeminiStrictTests.cs` | 8 |
| developer 消息（#2505） | `tests/AxonHub.Llm.Tests/GeminiIssue2505Tests.cs` | 3 |
| 通道接线（gemini / zenmux_gemini / gemini_vertex） | `tests/AxonHub.Api.Tests/GeminiChannelWiringTests.cs` | 6 |

`AxonHub.Llm.Tests` 由 2607 增至 2671，`AxonHub.Api.Tests` 由 1097 增至 1103，
`AxonHub.Grains.Tests` 保持 92，`AxonHub.Persistence.Tests` 保持 70；四个项目
合计 3866 增至 3936。差异条目新增 C113（已接受）。

## 53. Gemini 原生入站转换切片（inbound_convert.go 全量，102 例，2026-11-02）

参照路线：`llm/transformer/gemini/inbound_convert.go`（754 行）——
`ConvertGeminiToLLMRequest`（generation config 归一、thinking config →
reasoning effort/budget + ExtraBody、response format 三级优先、工具声明
（xjson 类型小写归一）、Google 原生工具、ToolConfig → ToolChoice、安全
设置 / 图像配置 → TransformerMetadata）、`ConvertGeminiContentToLLMMessages`
/ `ConvertGeminiContentToLLMMessage`（functionResponse → tool 消息立即返回、
多函数响应拆分、thought → reasoning content、inline/file data →
image/video/audio/document、functionCall → tool call + thought signature
元数据、空 parts → null）、`GeminiJsonSchemaResponseFormat`、
`ConvertGeminiThinkingConfigToGeminiOpenAIExtraBody`、
`ConvertLLMToGeminiResponse` / `ConvertLLMChoiceToGeminiCandidate`
（reasoning → thought part、text/多部件、tool calls 参数重解析（失败 →
null）、thought signature 直通与逐工具元数据、grounding metadata 优先于
annotations、citation 回退、finish reason 映射、usage 双向）、
`CitationMetadataFromLLMAnnotations`、`FindMatchingFunctionCallId`。

测试面：参照 inbound_convert_test.go 逐条移植，102 例——请求转换基础 12
（简单文本 / 系统指令 / generation config / 单多 stop / thinking 8 组含
level 优先级与 budget 保留）+ 音频 / 视频文件 2 + 响应格式 6 + 工具 9 +
内容消息 10 + 多函数响应拆分 1 + thought signature 入站 4 + candidate
thought signature 8（Encode 直通；Go 的"non-gemini 保持空"用例实为
"encrypted_data" 直通断言）+ LLM→Gemini 响应 6（usage 的
CandidatesTokenCount = Completion − Reasoning）+ finish reasons 5 +
testdata 请求 4（含 tool-result 的 FindMatchingFunctionCallId 补全 ID）+
testdata 响应 3 + 四向往返 12（工具数为空的两个夹具在 Go 侧靠
`len(nil)==0` 通过，端口以 `?.Count ?? 0` 对齐）+ citation 回退 1 +
grounding 优先 1 + grounding 5 + 安全设置 4 + 图像配置 4 + grounding 附加 5。

本次移植新增 `src/AxonHub.Llm/Transformer/Gemini/GeminiInboundConvert.cs`、
`src/AxonHub.Llm/Json/XjsonSchema.cs`（xjson.Transform 移植）与
`src/AxonHub.Llm/Json/Xmap.cs`（xmap.GetPtr 移植）。测试
`tests/AxonHub.Llm.Tests/GeminiInboundConvertTests.cs`（102）。安全设置 /
图像配置的元数据提取 8 例此前已随 §49 移植于
GeminiMetadataExtractionTests.cs。已接受差异见 C114（JSON Schema 归一化）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| Gemini 入站请求 / 内容 / 响应三向转换、thinking 映射、grounding 与 thought signature | `tests/AxonHub.Llm.Tests/GeminiInboundConvertTests.cs` | 102 |

`AxonHub.Llm.Tests` 由 2671 增至 2773，其余三个项目不变；四个项目合计
3936 增至 4038。差异条目新增 C114（已接受）。

## 54. Gemini 原生入站变换器与流式切片（inbound.go / inbound_stream.go + 端点接线，2026-11-02）

参照路线：`llm/transformer/gemini/inbound.go`（216 行）——
`ErrInvalidRequestURL`、`extractRequestParams`（:29-50，末段按 `:` 拆分
模型与动作，generateContent / streamGenerateContent 之外一律 invalid
request URL）、`TransformRequest`（:53-88，空体 / contents 缺失校验、
解码失败包装 "failed to decode gemini request: "）、`TransformResponse`
（非流式信封）、`TransformError`（:115-190，哨兵顺序：invalid request
URL → 404 NOT_FOUND 信封、ResponseError → 上游状态 + Detail.Message、
transport error 原体透传、InvalidRequest / InvalidModel → 400、
兜底 500 internal_server_error）与 `mapHTTPStatusToGeminiStatus`
（:193-216，十态映射）；`inbound_stream.go`（363 行）——`geminiInboundStream`
的按选择索引的 tool-call 累积与 reasoning-signature 缓冲、`transformChunk`
（:104-326，无 choices 快速路径委托单块变换、tool-call 增量累积、
JSON 参数完整才冲刷 / finish_reason=tool_calls 全冲刷（缺参补 {}、坏 JSON
修复）、`hasNonToolContent || 冲刷 || finishReason` 三元发射条件）、
`TransformStreamChunk`（:329-355）与 `AggregateStreamChunks` 委托聚合器。

测试面：参照 inbound_stream_test.go（7 函数）逐条移植 25 例——
TransformStreamChunk 6（nil / [DONE] / 文本 / 思考 / 工具调用 / usage）、
TransformStream 3（跨 chunk 工具参数累积、finish-only chunk 携 reasoning
signature、双 chunk）、AggregateStreamChunks 5（空 / 纯文本 / 跳过空块 /
思考 / 工具调用）、六夹具回放 6（llm-stop / llm-tool / llm-tool2 /
llm-tool-parallel / llm-think / llm-parallel_tool 对照 gemini-* 金标）、
finish reason 映射 5（STOP / MAX_TOKENS / SAFETY 等）；integraion_test.go
（7 函数）移植 19 例（五形状主线含 system instruction 独立体、三请求夹具、
grounding 往返、图像响应 5、modalities 三组取值与往返、snake_case
google_search）；outbound_convert_parameters_test.go 移植 1 例
（parametersJsonSchema 新格式保留 const/enum）。TransformRequest /
ExtractRequestParams / TransformError 经集成与端点路径锁定（Go 侧同样
没有 inbound.go 的独立测试文件）。端点接线：GeminiKeyAuthMiddlewareTests
6 例与 GeminiEndpointTests 6 例（非流式 / 流式 / alt=sse 三形状、
ListModels 成败两分支、未知动作 404 信封）。

本次移植新增 `src/AxonHub.Llm/Transformer/Gemini/GeminiInboundStream.cs`
（流式状态机），`GeminiInboundTransformer.cs` 补齐 TransformRequest /
TransformError / TransformStreamChunk / AggregateStreamChunks，
`GeminiEndpoint.cs` 挂载
`/v1beta/models/{**action}`、`/gemini/{apiVersion}/models/{**action}`
与两条 ListModels（routes.go:262-289），另修三处线路保真：
Candidate.finishReason 空值省略（Go omitempty 语义，`NullIfEmpty` 赋值点
归零）、空集合省略（parts/candidates/args 空即 null）、
`ToolJsonConverter.Write` 重写为手工属性输出（原实现移除 options 转换器
后重入自身导致 StackOverflow——类级 [JsonConverter] 优先于 options 列表）。
测试 `tests/AxonHub.Llm.Tests/GeminiInboundTransformerTests.cs`（25）、
`GeminiIntegrationTests.cs`（19）、`GeminiOutboundConvertParametersTests.cs`
（1）与 `tests/AxonHub.Api.Tests/GeminiKeyAuthMiddlewareTests.cs`（6）、
`GeminiEndpointTests.cs`（6）。已接受差异见 C115（超时/来源/响应头中间件
省略）、C116（未匹配路由的框架默认 404 体）、C117（入站解码错误文案）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| TransformStreamChunk / TransformStream / AggregateStreamChunks、六夹具回放与 finish reason 映射 | `tests/AxonHub.Llm.Tests/GeminiInboundTransformerTests.cs` | 25 |
| 双向集成（system instruction、modalities、snake_case 工具、alt=sse 往返） | `tests/AxonHub.Llm.Tests/GeminiIntegrationTests.cs` | 19 |
| parametersJsonSchema 新格式保留 const/enum | `tests/AxonHub.Llm.Tests/GeminiOutboundConvertParametersTests.cs` | 1 |
| Gemini key 鉴权中间件 | `tests/AxonHub.Api.Tests/GeminiKeyAuthMiddlewareTests.cs` | 6 |
| 原生端点三形状、ListModels、未知动作 404 | `tests/AxonHub.Api.Tests/GeminiEndpointTests.cs` | 6 |

`AxonHub.Llm.Tests` 由 2773 增至 2818，`AxonHub.Api.Tests` 由 1103 增至
1115，其余两个项目不变；四个项目合计 4038 增至 4095。差异条目新增
C115–C117（均已接受）。

## 55. Antigravity Llm 层切片（router/executor/health/version/sanitizer/envelope/transformer + 129 例测试，2026-11-02）

参照路线：`llm/transformer/antigravity/` 全包——`router.go`（QuotaAntigravity /
QuotaGeminiCLI 配额偏好、`:antigravity|:gemini-cli` 后缀与 `antigravity-` 前缀
剥离、normalizeModelName、ShouldRetryWithDifferentEndpoint 的
429/403/404/500/502/503/504→true 与 200/400/401→false）；`executor.go`
（三端点 Daily→Autopush→Prod 回退、per-model 60s 冷却 10min TTL 的健康追踪、
ReplaceBaseURL、GetEndpointsInOrder 全模型先 Daily、cloneRequestForEndpoint
浅拷贝 + Headers/Query 深拷贝）；`health_tracker.go`
（RecordFailure/RecordSuccess/ShouldSkip/Stats/懒清理）；`version.go`
（changelog 2MB 抓取 `id="rel-ide-x.y.z"` → auto-updater 纯文本 → 硬编码
2.5.5 三级回退、once guard、NewerVersion 永不降级、SetClientHeaders 的
User-Agent/X-Client-Version）；`constants.go`（OAuth 客户端常量与三端点、
12 条 header 剥离名单、ANTIGRAVITY_SYSTEM_INSTRUCTION——注意：参照工作树为
CRLF（core.autocrlf 检出产物），git blob 为 LF，端口按 blob 的 LF 语义锁定，
工作树运行时与参照本机行为一致）；`token_provider.go`（refreshToken|projectID
拆分、TokenProvider 单次 Do 刷新）；`envelope.go`（{project, model, request,
requestType, userAgent, requestId} 信封）；`sanitizer.go`（$ref/const/enum/
additionalProperties/约束/anyOf/allOf/placeholder 四阶段消毒 + UPPERCASE
类型）；`transformer.go`（Gemini 出站 → 信封包裹、schema 消毒、VALIDATED 工具
模式 + 加固指令、Gemini 3 thinkingLevel vs budget、Claude Thought 剥离、
OAuth-only 鉴权无 API key 回退）；`streaming.go`（响应信封解包、[DONE] 透传）。

测试面：router_test.go 47 例（24 配额偏好 + 2 初始端点 + 1 回退端点 + 3 剥
后缀 + 3 剥前缀 + 4 归一 + 10 重试判定）、version_test.go 21 例（6 单函数 +
3 永不降级 + 12 畸形输入）、health_tracker_test.go 12 例、executor_test.go +
executor_retry_test.go 22 例（8 回退 + 3 URL 重写 + 4 端点顺序 + 4 冷却 + 1
DoStream 冷却 + 2 抛出错误）、sanitizer_test.go + sanitizer_uppercase_test.go
19 例（10 消毒 + 9 大写）、transformer_test.go 8 例（5 信封/修补 + 3 OAuth），
共 129 例逐条移植。关键测试双：`AntigravityTestExecutor`（脚本化 Do/DoStream、
按 "/v1internal" 截断记录端点，与参照 mockHTTPClient 一样原样返回非 2xx 状态
而不转错误；Go 的 nil 流经端口非空接缝以 `!` 原谅并注释说明）、
`ScriptedVersionExecutor`（按 URL 脚本化 changelog/updater 两源，替代 httptest
服务器）、xUnit `[Collection("antigravity-version-state",
DisableParallelization=true)]` 守护共享的 AntigravityVersion 静态状态（参照在
同一 Go 包内同享包级状态）。

本次移植新增 10 个源文件：AntigravityRouter.cs、AntigravityExecutor.cs、
AntigravityHealthTracker.cs、AntigravityVersion.cs、AntigravityConstants.cs、
AntigravityTokenProvider.cs、AntigravityEnvelope.cs、AntigravitySanitizer.cs、
AntigravityTransformer.cs、AntigravityStreamProcessor.cs；测试 6 文件。门禁
中修复五处源码问题：多行 raw string 闭合定界符须独占一行（C# 规则，CS8997）、
方法组前的 `static` 非法、SetVersion 提升 internal 供 fetcher 调用、
CloneRequestForEndpoint 参数非空化（Go 的 nil 请求在 clone 处 panic，端口用
非空签名前置化）、CA1725 参数名对齐接口（request/response/error）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 配额偏好、前缀/后缀剥离、模型归一化与重试判定 | `tests/AxonHub.Llm.Tests/AntigravityRouterTests.cs` | 47 |
| 版本解析三级回退、once、2MB 上限、永不降级、客户端头 | `tests/AxonHub.Llm.Tests/AntigravityVersionTests.cs` | 21 |
| 端点冷却状态机（TTL、懒清理、并发、per-model 隔离） | `tests/AxonHub.Llm.Tests/AntigravityHealthTrackerTests.cs` | 12 |
| 端点回退、冷却跳过、URL 重写与传输错误重试 | `tests/AxonHub.Llm.Tests/AntigravityExecutorTests.cs` | 22 |
| schema 消毒与 UPPERCASE 类型 | `tests/AxonHub.Llm.Tests/AntigravitySanitizerTests.cs` | 19 |
| 信封结构、schema/tool/thinking 修补、OAuth-only 鉴权 | `tests/AxonHub.Llm.Tests/AntigravityTransformerTests.cs` | 8 |

`AxonHub.Llm.Tests` 由 2818 增至 2947，其余三个项目不变；四个项目合计 4095
增至 4224。差异条目新增 C118–C120（均已接受）。通道接线
（ChannelOutboundFactory 的 antigravity 分支、refreshOAuthToken、
AntigravityHandlers 与 OAuth 路由、provider_quota 的 antigravity_checker）
随下一提交。

## 56. Antigravity 通道接线（ChannelOutboundFactory 的 antigravity 分支，5 例，2026-11-02）

参照路线：`internal/server/biz/channel_llm.go:670-674`（TypeAntigravity 凭据
校验：只消费单一 legacy APIKey 字段，TrimSpace 后为空则 "missing api key for
channel %s"，不参与 enabled keys 检查）与 `:1375-1391`
（buildChannelWithTransformer 的 antigravity 分支：
antigravity.NewTransformer(Config{BaseURL, APIKey}, WithHTTPClient,
WithOnTokenRefreshed(svc.onTokenRefreshed(c)))、错误包装 "failed to create
antigravity outbound transformer"、GetTokenProvider + setupAutoRefresh）；
`channel_endpoint.go:325`（antigravity 端点表条目
APIFormatGeminiContents——端口按通道类型 + 入站 ApiFormat 分派，无端点表，
该语义由 Gemini 入站格式天然满足）。

测试面：参照无独立接线测试（buildChannelWithTransformer 的 antigravity 分支
无 Go 单测）；端口按 GeminiChannelWiringTests 先例补 5 例接线锁定：信封
变换器 + OAuth-only 鉴权（Bearer 令牌来自脚本化 TokenURL 执行器、
X-Goog-Api-Key 恒缺省、信封 project 取自 legacy key 的 projectID 段、inner
contents 透传）、自定义 base URL 保持、缺 APIKey / 纯空白 APIKey 的拒绝
文案、enabled key 列表不满足 antigravity（参照只读 APIKey 字段）。

本次移植新增：ChannelOutboundFactory 的 AntigravityChannelType 常量、凭据
校验分支与 CreateAntigravityOutbound（错误包装对齐参照文案链 "failed to
create antigravity outbound transformer: failed to create internal gemini
transformer: …"）；测试 AntigravityChannelWiringTests.cs（5 例）。

未移植并登记（管理面/后台任务切片）：refreshOAuthToken 的 antigravity 分支
（extractProjectIDFromAntigravityCreds + APIKey 重写 refreshToken|projectID）
并入 C094（无 OnRefreshed 持久化的同一机制）；AntigravityHandlers 与
POST /admin/antigravity/oauth/start|exchange（C121）；
ModelFetcher.fetchAntigravityModels（C122）；provider_quota 的
AntigravityQuotaChecker 与 registerAntigravitySupport（C123）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| antigravity 分支的信封变换器、OAuth-only 鉴权、缺凭据文案与 key 列表语义 | `tests/AxonHub.Api.Tests/AntigravityChannelWiringTests.cs` | 5 |

`AxonHub.Api.Tests` 由 1115 增至 1120，其余三个项目不变；四个项目合计 4224
增至 4229。差异条目新增 C121–C123（真实缺口），C094 补记 antigravity 刷新
写回分支。至此 antigravity 通道的请求服务链路（出站变换 + 通道接线）闭合，
剩余为管理面（OAuth 交换、模型拉取、配额采集）。

## 57. OpenaiResponses 通道接线（ChannelOutboundFactory 的 openai_responses 分支，4 例，2026-11-02）

参照路线：`internal/server/biz/channel_llm.go:1313-1322`
（buildChannelWithTransformer 的 TypeOpenaiResponses 分支：
responses.NewOutboundTransformerWithConfig（Config{BaseURL, APIKeyProvider:
getAPIKeyProvider(ch), Transport: primaryEndpointTransport(c,
APIFormatOpenAIResponse)}），错误包装 "failed to create outbound
transformer"）；`channel_llm.go:276-292`（primaryEndpointTransport：遍历端点
表找 APIFormatOpenAIResponse 条目 → endpointTransport（端点 BaseURL 为空时
回填通道 BaseURL）；无匹配条目时 endpointTransport(ChannelEndpoint{BaseURL:
c.BaseURL})；endpointTransport 按 ws/wss scheme 推断 websocket 传输，否则空
串）；`channel_endpoint.go:233`（openai_responses 端点表条目：仅
APIFormatOpenAIResponse 一条）。凭据校验无专属分支（不在
channel_llm.go:661-693 的校验 switch 中），走通用 "missing api key for
channel %s" 检查。

测试面：参照有 `TestOpenAIResponsesEndpoint_InheritsWebSocketTransportFromBaseURL`
（wss base URL + 自定义 /custom/responses 端点 → CustomizeExecutor 返回
responses.WebSocketExecutor）与 `TestOpenAIResponsesCompactEndpoint_RejectsInheritedWebSocketTransport`
（compact 端点继承 websocket 传输 → 构建期拒绝 "websocket transport only
supports api_format \"openai/responses\""）。后者的拒绝发生在
ValidateEndpoints（channel_endpoint.go:65）——端点表校验子系统，端口无端点
表（C082），该分支不可达。端口按 NanogptResponses 接线先例补 4 例锁定：
单端点语义（Create 时入站格式无关，出站恒为 Responses 变换器、URL 与
Bearer 鉴权）、自定义 base URL 保持、缺 key 的拒绝文案、base URL 的 ws/wss
scheme 推断传输（websocket 传输下 FinalizeTransportRequest 原样返回，HTTP
传输剥掉 responses_websockets beta 头）。

本次移植新增：ChannelOutboundFactory 的 OpenaiResponsesChannelType 常量与
switch 分支（Transport 取自 InferTransport——与 CreateCodexOutbound 共用同一
helper；端口无端点表故 scheme 是唯一来源（C082）。参照的错误包装在凭据校验
通过后不可达——NewOutboundTransformerWithConfig 只校验 provider 非空——端口
不包装）。测试 OpenaiResponsesChannelWiringTests.cs（4 例）。

未移植并登记（沿用既有缺口，无新条目）：端点记录（自定义 Path、显式
Transport、ValidateEndpoints 的 websocket/compact 拒绝）——C082/C094 同一
机制；provider_quota 的 TypeOpenaiResponses 配额采集（C123）；ModelFetcher
的 openai_responses 模型拉取（C088）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| openai_responses 分支的单端点语义、缺凭据文案与 ws/wss 传输推断 | `tests/AxonHub.Api.Tests/OpenaiResponsesChannelWiringTests.cs` | 4 |

`AxonHub.Api.Tests` 由 1120 增至 1124，其余三个项目不变；四个项目合计 4229
增至 4233。无新增差异条目（传输推断沿用 C082 的端点记录裁剪口径，配额与
模型拉取沿用 C123/C088）。至此 openai_responses 通道的请求服务链路闭合，
剩余为 provider_quota 与 ModelFetcher 的后台切片。

## 58. Commandcode 通道接线（ChannelOutboundFactory 的 commandcode / commandcode_anthropic 分支，12 例，2026-11-02）

参照路线：`internal/server/biz/channel_llm.go:1195-1204`
（buildChannelWithTransformer 的 TypeCommandcode 分支：普通 openai.Config
{PlatformOpenAI, BaseURL, APIKeyProvider}，错误包装 "failed to create
outbound transformer"）、`:911-918`（TypeCommandcodeAnthropic 分支：anthropic
Config{Type: PlatformCommandCode, BaseURL, APIKeyProvider}，同款错误包装）、
`:640-647`（validateCommandCodeBaseURL：url.Parse(TrimSpace) 后 scheme https
且 host 非空，否则 "Command Code base URL must use HTTPS"）、`:683-688`
（凭据校验与默认分支同构：enabled keys 为空且无 key-test override 即
"missing api key for channel %s"，配额采集 cookie 永不作推理凭据）、
`:705-711`（两类通道在构建前校验 HTTPS 基址并把 HTTP client 换成
WithRejectHTTPSDowngrade）、`channel_endpoint.go:339-340`（默认端点：
commandcode = openAIChatOnlyDefaultEndpoints、commandcode_anthropic = 单一
AnthropicMessage 端点）。Llm 层无新代码：AnthropicPlatformType.CommandCode
（Bearer 而非 X-API-Key、signature 解码 / adaptive thinking / output_config
与 Direct 同组，outbound.go:251、outbound_convert.go:96、thinking.go）已在
Anthropic 出站切片中随枚举移植并锁定。

测试面：参照有 `TestCommandCodeOutboundRouting`
（channel_llm_commandcode_test.go）：chat 走 /chat/completions + Bearer、
OpenAI 格式模型全通过、anthropic 走 /messages + Bearer、anthropic 端点与
模型无关、普通 anthropic direct 保持 X-API-Key、缺 key 报错、apiKeyOverride
两条（禁用 key 可被 override、空白 override 不替换）。端口补 12 例接线
锁定：chat 出站与 Bearer、anthropic 出站与 Bearer 且 X-API-Key 恒缺省、
缺 key 文案、非 HTTPS 与垃圾 URL 拒绝、大写 scheme 通过、key 检查先于 URL
检查。apiKeyOverride 两条属通道 key 测试流（管理面），未移植，补记入 C014。

本次移植新增：ChannelOutboundFactory 的 CommandcodeChannelType /
CommandcodeAnthropicChannelType 常量、HTTPS 基址校验（先凭据后 URL 的顺序
对齐参照）、两个 switch 分支（commandcode 复用 OpenAiOutboundTransformer
.Create；commandcode_anthropic 以 AnthropicPlatformType.CommandCode 构造，
参照的错误包装在凭据校验通过后不可达——anthropic 构造只有 Vertex 分支会
失败——端口不包装）。测试 CommandcodeChannelWiringTests.cs（12 例）。

未移植并登记：WithRejectHTTPSDowngrade（HTTPS→HTTP 重定向拒绝，端口共享
executor 默认跟随重定向）登记为 C124 真实缺口；URL 解析严格性差异（Go
url.Parse 接受 https://host:bad，.NET Uri.TryCreate 拒绝）并入 C124；
channel_llm.go:527-529（commandcode 通道自定义 anthropic 格式端点 →
PlatformCommandCode + EndpointPath）属端点表裁剪（C082）；模型路由
isCommandCodeAnthropicModel（claude 前缀 → anthropic 通道）属 ModelFetcher
缺口（C088）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| commandcode / commandcode_anthropic 的出站分派、Bearer 鉴权、HTTPS 基址校验与校验顺序 | `tests/AxonHub.Api.Tests/CommandcodeChannelWiringTests.cs` | 12 |

`AxonHub.Api.Tests` 由 1124 增至 1136，其余三个项目不变；四个项目合计 4233
增至 4245。差异条目新增 C124（真实缺口：HTTPS 降级拒绝与 URL 校验裁剪），
C014 补记通道 key 测试流。至此 commandcode / commandcode_anthropic 通道的
请求服务链路闭合，剩余为 key 测试流（管理面）与 provider_quota / 模型拉取
（后台切片）。

## 59. 别名通道类型接线（ChannelOutboundFactory 的 19 个通道类型分支，40 例，2026-11-02）

参照路线：`internal/server/biz/channel_llm.go:1277-1292`（OpenAI 兼容别名组：
atlascloud、vercel、deepinfra、qiniu、ppio、siliconflow、aihubmix、burncloud、
github、evolink、groq 共用 openai.Config{PlatformOpenAI, BaseURL,
APIKeyProvider}）、`:855-863`（xai_responses = responses.Config{BaseURL,
APIKeyProvider}，无 Transport——不经过 openai_responses 的
primaryEndpointTransport scheme 推断，channel_endpoint.go:296 单一 responses
端点）、`:885-893`（longcat_anthropic = anthropic PlatformLongCat）、`:898-909`
（qiniu/aihubmix/evolink 三个 anthropic 变体与 anthropic 本体同组 =
PlatformDirect）、`:990-998`（deepseek_anthropic = PlatformDeepSeek）、
`:1016-1024`（moonshot_anthropic = PlatformMoonshot）、`:1056-1064`
（anthropic_aws = PlatformBedrock）；`channel_endpoint.go:236-237、251、268、
277、303、307、332`（每个 anthropic 变体都是单一 AnthropicMessage 端点）、
`:189-197、236、333-337`（别名组端点集：默认 OpenAI 兼容集、qiniu chat-only、
groq chat+音频）。`llm/transformer/anthropic/outbound.go:249-261`（Bearer 条件
= LongCat / Ollama / Bedrock / CommandCode，其余 X-API-Key——deepseek /
moonshot 变体保持 X-API-Key）。Llm 层无新代码：五个 anthropic 平台与
Responses 出站已在早期切片移植并锁定。

测试面：参照无这些通道的独立接线测试（行为由 transformer 层测试与端点表
测试覆盖）。端口补 40 例接线锁定：别名组 11 类型共用 chat 出站
（/chat/completions + Bearer + SelectedApiKey）、三个 anthropic 变体（Direct、
X-API-Key 与 HeaderKey 形状）、longcat（Bearer）、deepseek/moonshot（各自平台
+ X-API-Key，锁定 Go Bearer 条件的边界）、anthropic_aws（Bedrock URL
/model/{model}/invoke + Anthropic-Version: bedrock-2023-05-31 + Bearer）、
xai_responses（/responses + Bearer；无论 http/wss 基址都走 HTTP 传输——与
openai_responses 的 scheme 推断对比锁定）、19 类型缺 key 统一拒绝。

本次移植新增：ChannelOutboundFactory 的 19 个通道类型常量、7 个 switch 分支
（别名组 11 类型合一个 or 分支；anthropic 变体按平台分组，参照的错误包装在
凭据校验通过后不可达——anthropic 构造只有 Vertex 分支会失败——端口不包装）。
端点集差异（groq 的 speech/transcription/translation 在参照走端点表分派、
端口由 OpenAI 出站统一服务；不在端点集内的格式参照报错）并入 C094 分派模型
差异。测试 AliasChannelWiringTests.cs（40 例）。

未移植并登记：anthropic_gcp（GCP 凭据 + PlatformVertex + key 豁免，
channel_llm.go:1069-1085、:675）登记为 C125；anthropic_fake / openai_fake
（FakeTransformer 固件，:1089-1095、:675）登记为 C126；xai_subscription
（OAuth-only 校验 :666-669、xaisubscription 包 11 文件、5 分钟刷新与持久化
回写）登记为 C127，排期为独立里程碑。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 19 个别名通道类型的出站分派、平台与鉴权形状、Bedrock URL 信封、xai_responses 无传输推断、缺 key 拒绝 | `tests/AxonHub.Api.Tests/AliasChannelWiringTests.cs` | 40 |

`AxonHub.Api.Tests` 由 1136 增至 1176，其余三个项目不变；四个项目合计 4245
增至 4285。差异条目新增 C125–C127（真实缺口：anthropic_gcp、fake 通道、
xai_subscription）。至此除 C125–C127 登记的四个类型外，参照
`buildChannelWithTransformer` 的全部通道类型分支均已在端口闭合。

## 60. xai_subscription 通道与订阅 Llm 子包（7 文件 + 51 例，2026-11-02）

参照路线：`llm/transformer/xai/subscription/`（constants.go、token.go、sso.go、
sso_device.go、plan.go、billing.go、outbound.go；测试 billing_test.go 4、
plan_test.go 5、sso_test.go 5、sso_device_test.go 2、outbound_test.go 4）；
`internal/server/biz/channel_llm.go:666-669`（github_copilot 与 xai_subscription
共用 OAuth-only 准入，缺凭据报 "missing oauth credentials for channel %s"）、
`:867-884`（ResolveOAuthCredentials → xaisubscription.NewTokenProvider{Credentials,
HTTPClient, OnRefreshed: onTokenRefreshed} → NewOutboundTransformer →
setupAutoRefresh(oauth.AutoRefreshOptions{})；两处错误包装分别为
"xAI subscription channel %s has invalid credentials: %w" 与 "failed to create
xAI subscription outbound transformer: %w"）；`internal/objects/channel.go:460-468`
（oauth 对象且 TrimSpace(AccessToken) 非空优先，否则把 legacy apiKey 当凭据文档
解析）。

Llm 包移植：constants（授权/令牌/设备码端点、CLI client id、scope 集、
Grok CLI 的 BaseURL `https://cli-chat-proxy.grok.com/v1` 与 4 个身份头、
DefaultModels）；token.go（以 xAI 端点与 Grok CLI User-Agent 构造
oauth.TokenProvider）；sso.go（cookie 前缀剥离、sso/sso-rw 提取、CR/LF/NUL
清洗、16 KiB 上限——按 Go 的 UTF-8 字节数计长）；sso_device.go（设备授权流：
账户页校验（401 或跳 sign-in/sign-up 即 unauthorized）、device/code、打开验证页
→ verify → consent、approve → done、轮询 token；手工重定向解析（303 与
301/302 非 GET 改写为 GET）、显式 cookie jar、2 MiB 响应上限、90s 超时、
authorization_pending / slow_down(+5s) / access_denied / expired_token /
超时分支；interval/expires_in 默认 5s/1800s，轮询截止 min(expires_in, 75s)）；
plan.go（JWT 载荷 tier 声明：数字优先 json.Number → ParseUint，字符串先
ParseUint 再查名称表，含 x_premium+ 与 supergrok 系列）；billing.go（weekly
credits 与 monthly billing 合并、{"val": ...} 包装、raw number 与 TrimSpace
字符串、Go 解码的大小写不敏感键与 null 语义、math.Round 后的 SuperGrok /
SuperGrok Heavy 判定）；outbound.go（仅 chat 请求，委托 Responses 出站后覆写
Bearer 并加 4 个 CLI 头，APIFormat 恒 openai/responses）。

本次移植新增：ChannelOutboundFactory 的 XaiSubscriptionChannelType 常量、
OAuth-only 校验（与 github_copilot 同一分支）、switch 分支与
CreateXaiSubscriptionOutbound / ResolveOAuthCredentials helper；后者与
objects/channel.go 的 ResolveOAuthCredentials 同构（oauth 对象优先，空白
access_token 回退到 legacy apiKey 文档）。

测试面：参照的 20 个用例（billing 4、plan 4+1、sso 5、sso_device 2、
outbound 1+1+2）全部移植；端口补 22 例 Llm（billing 的 null config /
{"val": null} / raw number 与空白字符串 / 大小写不敏感键 / 无 val 的包装 /
非对象文档，plan 的其余 tier 编码（1、" 7 "、5.5、null、true、空对象、
x_premium+）与不可解码载荷，sso 的 UTF-8 字节上限，sso_device 的空令牌 /
不可信验证 URL / authorization_pending 继续 / slow_down 加 5s 区间，outbound
的逐请求令牌读取与缺 token provider 文案）与 9 例 Api 接线（OAuth-only 拒绝、
非法凭据包装、oauth 对象优先 / legacy apiKey 回退 / 空白 access_token 回退、
忽略通道 BaseURL、非 chat 拒绝、空凭据拒绝）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 订阅 Llm 包（billing / plan / sso / sso_device / outbound） | `tests/AxonHub.Llm.Tests/XaiSubscription*Tests.cs` | 42 |
| xai_subscription 通道接线（OAuth 校验、凭据解析、端点固定、方法门） | `tests/AxonHub.Api.Tests/XaiSubscriptionChannelWiringTests.cs` | 9 |

`AxonHub.Llm.Tests` 由 2947 增至 2989，`AxonHub.Api.Tests` 由 1176 增至 1185，
其余两个项目不变；四个项目合计 4285 增至 4336。

未移植并登记（C127 残余）：setupAutoRefresh 的 5 分钟周期与提前 5 分钟刷新、
OnRefreshed 的持久化回写（C094 同族；端口令牌只在请求路径刷新）；
ConvertSSOToBuild 的管理面宿主 internal/server/api/xai.go:192（C014）；
ParseBillingResponses / PlanFromAccessToken 的宿主
provider_quota/xai_subscription_checker.go（C123 同族）。差异条目 C127 由
「真实缺口」改写为「已解决（残余各自登记）」。

## 61. anthropic_gcp 通道：Vertex 执行器、VertexTransformer 与 GCP 令牌链（3 文件 + 44 例，2026-11-02）

参照路线：`llm/vertex/executor.go`（NewExecutor / NewExecutorFromJSON / Do /
DoStream / transformRequest 全量）；`llm/transformer/anthropic/vertex.go`
（CustomizeExecutor 返回内置执行器，其余方法透传内嵌 Outbound）；
`outbound.go:88-120`（Vertex 先构造执行器、再规范化 BaseURL，并用
VertexTransformer 包装；其余平台返回普通 outbound）；`internal/server/biz/
channel_llm.go:677-682`（anthropic_gcp 豁免 API key 校验）与
`:1069-1085`（缺 GCP 报 "GCP credentials are required for anthropic_vertex
channel"，以 Region/ProjectID/JSONData 构造 PlatformVertex，再包
"failed to create outbound transformer: %w"）；`internal/objects/channel.go`
的 GCPCredential（`gcp.region`、`gcp.projectID`、`gcp.jsonData`）；
`golang.org/x/oauth2@v0.36.0` 的 `google.CredentialsFromJSONWithParams`、
`jwt/jwt.go`、`internal/token.go`、`authhandler`、`google/error.go`。

本次新增：`src/AxonHub.Llm/Vertex/GcpCredentials.cs`（凭据检测顺序与 Go 风格
解码、两种令牌源、C128 显式拒绝）、`Vertex/GcpTokenSource.cs`（reuse 缓存、
RS256 JWT-bearer、refresh grant、form/JSON 响应解析、oauth2 精确错误文案）、
`Vertex/VertexExecutor.cs`（请求改写与 GcpAuthHandler）、
`Transformer/Anthropic/VertexTransformer.cs`；`Json/RawJsonModelPatch.cs`
增补顶层 gjson/sjson 语义（`RawJsonGjson`）；`AnthropicOutboundTransformer`
新增 `CreateWithConfig` 复刻参照工厂顺序（构造函数本身不再拒绝 Vertex）；
`ChannelCredentials.GetGcpCredential` / `GcpCredential` 与
`ChannelOutboundFactory` 的 `anthropic_gcp` 分支（keyless 规则、缺 GCP 文案、
**failed to create outbound transformer:** 包装）。

执行器语义：逐字段复制 Method/URL/Headers/Body/RequestID/OnResponseHeaders 并
把 Auth 置空；body 非空时按 sjson 追加 `anthropic_version`（已存在则保留，
键缺失或值为 null 视为缺失）；`/v1/messages` + POST 取 gjson 的 model（空即
"model field is required"）、按 sjson 删除 model、按 `stream` 选
`rawPredict`/`streamRawPredict`；`/v1/messages/count_tokens` + POST 只改 URL
不删 model；其余端点直接拼接 baseURL。baseURL 保留参照的结尾斜杠，因此
outbound 已生成的绝对路径（Vertex 的 BaseURL 为空）会得到与参照相同的
`...googleapis.com//v1/projects/...` 双斜杠——这是被测试锁定的原样行为。
OAuth 头由 `GcpAuthHandler`（DelegatingHandler）在 HttpClient 之下注入，因为
端口的 `BuildHttpRequest` 会剥离 Authorization；空 access_token 时不加头，
等价于 oauth2.Transport。

令牌链语义：凭据解码按 Go `encoding/json`（大小写不敏感、重复键后者胜、null
为 no-op、UnmarshalTypeError 文案）；`service_account` / `gdch_service_account`
走 RS256 JWT-bearer（header `{"alg","typ","kid"?}`、claims 顺序
iss/aud/exp/iat、iat=now-10s、exp=iat+1h、aud 默认取 token URL、
RawURLEncoding、每次取令牌重新解析私钥）；`authorized_user` 走 refresh grant
（client_id/client_secret 放表单、按 `mime.ParseMediaType` 选择
form/text-plain 或 JSON、响应 refresh_token 覆盖旧值、缺失则保留请求值、
`expires_in` 走 json.Number 且大于 MaxInt32 时截断）；两条流都由
reuseTokenSource 缓存（10 秒提前量、零过期永不过期、空 access_token 永不
有效）。错误文案保留参照原文：`oauth2: cannot fetch token: ...`、
`oauth2: %q %q %q`、`oauth2: cannot parse json: ...`、
`oauth2: server response missing access_token`、
`oauth2: token expired and refresh token is not set`、
`missing 'type' field in credentials`、`unknown credential type: %q`。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| Vertex 执行器（构造校验、FromJson 包装、URL 改写、sjson 字节语义、令牌注入与缓存） | `tests/AxonHub.Llm.Tests/VertexExecutorTests.cs` | 11 |
| VertexTransformer 与工厂（CreateWithConfig 顺序与错误、透传、端到端线上形状） | `tests/AxonHub.Llm.Tests/AnthropicVertexTransformerTests.cs` | 7 |
| GCP 令牌链（JWT 断言字节与签名、aud 默认/覆盖、reuse 与刷新、form/JSON、精确错误、C128） | `tests/AxonHub.Llm.Tests/GcpTokenSourceTests.cs` | 13 |
| anthropic_gcp 通道接线（keyless、gcp 必填、错误包装、Vertex URL） | `tests/AxonHub.Api.Tests/AnthropicGcpChannelWiringTests.cs` | 4 |

`AxonHub.Llm.Tests` 由 2989 增至 3020，`AxonHub.Api.Tests` 由 1185 增至 1189，
其余两个项目不变；四个项目合计 4336 增至 4371。

未移植并登记：external_account / external_account_authorized_user /
impersonated_service_account 与交互式 web/installed 客户端配置显式
NotSupportedException，私钥/jws/传输错误文本的底层后缀近似，统一登记为
C128；Bedrock 的 AWS EventStream 流式解码仍是独立缺口，C056 已收窄为
Bedrock 单项。差异条目 C125 由「真实缺口」改写为「已解决（残余 C128）」。

## 62. 静态托管与 SPA fallback：NoRoute 静态处理器（2 文件 + 26 例，2026-11-02）

参照路线：`internal/server/routes.go:59`（`server.NoRoute(static.Handler())`）；
`internal/server/static/embed.go:15-108`（`//go:embed all:dist/*`、API 前缀表、
isStaticAssetPath、serveAPINotFound、serveSPAIndex）；`static/embed_test.go` 的
3 例（API 前缀 JSON 404、SPA no-cache、缺失资产 404 不回退）；
`internal/objects/response.go` 的 ErrorResponse/Error 形状；gin
`AbortWithStatusJSON`（`application/json; charset=utf-8`）、`c.FileFromFS` 与
http.FileServer / http.ServeContent 的响应语义。

本次新增：`src/AxonHub.Api/Static/StaticHandler.cs`（三分支分派、Go 字节一致的
404 信封、资产文件/纯文本 404、SPA index 与原样 no-cache 头、根目录逃逸防护、
`StaticFallbackMetadata`），`Program.cs` 在全部 API 路由之后注册
`MapFallback("{**path}", ...)` 并读取 `Server:Static:Root`（缺省为内容根下的
`frontend/dist`，相对路径相对内容根解析）。

路由语义：`path == prefix || path.StartsWith(prefix + "/")` 的八个 API 前缀
（/admin、/anthropic、/doubao、/gemini、/jina、/openapi、/v1、/v1beta）→
`{"error":{"type":"Not Found","message":"path not found: <path>"}}`，键顺序
type/message，转义沿用 `GoJson.Marshal`；资产判定为 `/assets/`、`/images/`
前缀、精确 `/favicon.ico` 或 `.js/.css/.png/.jpg/.jpeg/.gif/.svg/.webp` 后缀
（大小写敏感）；缺失资产 `404 page not found\n` + text/plain + nosniff，绝不
回退 SPA；其余路径返回 index.html 并写
`Cache-Control: no-cache, no-store, must-revalidate`、`Pragma: no-cache`、
`Expires: 0`。text/* 资产按 Go mime.TypeByExtension 补 `charset=utf-8`。

接线语义：ASP.NET 默认 `MapFallback()` 使用 `{*path:nonfile}`，会漏掉带扩展名
的资产路径（实测直接 404），因此显式使用 `{**path}`；回退端点带
`StaticFallbackMetadata`，API 前缀上的 IP 黑名单与 API key / Gemini key
中间件分支据此跳过回退请求，复刻 gin 组中间件不作用于 NoRoute 的行为。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| API 前缀 JSON 信封（逐字节）、前缀边界（/v10、/administering）、SPA/资产/404 三分支、no-cache 原始头、favicon 与后缀大小写、映射路由优先、POST 回退、HEAD 无体、缺失 index | `tests/AxonHub.Api.Tests/StaticHandlerTests.cs` | 26 |

`AxonHub.Api.Tests` 由 1189 增至 1215，其余三个项目不变；四个项目合计 4371
增至 4397。

差异：C116 由「已接受」改写为「已解决」——原记录把参照 NoRoute 误记为 gin
默认纯文本 404，实际是静态处理器，未匹配的 API 前缀路径返回 JSON 信封；磁盘
根替代 go:embed、缺失 index 的目录列表与条件请求 / 嗅探差异登记为 C129。差异
总数为 129（76 接受 / 19 缺口 / 34 已解决）。

## 63. 管理面 system-settings 查询与变更（4 查询 + 3 变更，68 例，2026-11-02）

参照路线：`internal/server/gql/system.graphql:1-140,130-215,208-290,412-500`
（SystemStatus / RetryPolicy / SystemModelSettings / SecuritySettings 与三个
update mutation 的 SDL 类型、枚举和输入）、`system.resolvers.go`（四个查询与
三个变更的严格读取、错误包装、省略 developerSettings 时保留、security 合并）、
`internal/server/biz/system.go:280-345,396-415,437-490,1042-1174,1237-1291,1895-1973`
（默认值、JSON 解码、normalizeRetryPolicy、IsInitialized）、
`internal/server/biz/channel.go:759-846`（auto-disable 规则校验、状态码范围、
排序去重）、`model_settings_inheritance.go:1-130`（developer 规范化与校验）、
`internal/server/biz/model.go:50-353`（ModelAssociation 的 JSON 形状、
when / filter condition 校验）、`filter.graphql:1-30`、`objects/model.go:63-125`。

本次新增 `src/AxonHub.Api/GraphQL/SystemSettingsTypes.cs`（4 个输出类型、
13 个关联 / 条件类型、7 个输入类型、3 个枚举与 SystemSettingsMapping）、
`src/AxonHub.Api/GraphQL/AdminMutation.cs`（三个变更）；在 `AdminQuery.cs`
增加四个查询、`GraphQLSetup.cs` 注册 Mutation 类型、`Program.cs` 注册
`ISystemSettingsAdminService`。`CodedException.cs` 的错误过滤器对非编码异常
恢复 `err.Error()` 作为 message，对齐 `graphql.DefaultErrorPresenter`。
`RetryPolicyCodec` 补齐 Go custom unmarshaler（显式 null 的 trace_sticky_mode
报错、JsonFieldValue 区分缺省与 null、字面 `null` 文档得到零值策略）、
`SecuritySettingsCodec` 的 blocked_ips 可空列表写出、`ModelSettingsCodec`
的条件值归一化。

读写语义：查询走严格读取——`failed to check initialization status`、
`failed to get retry policy`、`failed to get system model settings`、
`failed to read current security settings` 等前缀直接进入 GraphQL errors，
不回落到运行时默认值；变更先规范化（策略 / 粘性模式 / 上游模式白名单、
超时 0..600 钳位、weighted→failover、legacy statuses 仅在 rules 为空时迁移）
再校验 auto-disable 规则（逐条文案、cron 与时区、状态码 100–599、排序去重、
pattern 裁剪去重），校验错误以 `CodedException`（VALIDATION_FAILED，且
resource / field / value 显式 null）返回。`updateSystemModelSettings` 在输入
省略 developerSettings 时读取当前值并保留；`updateSecuritySettings` 只覆盖
非 null 字段，未提供的字段沿用存储值。存储写入经 `ISystemStore.SetAsync` 的
key 冲突 upsert（保留 id / created_at，复活软删除行）。

线路形状：`[GraphQLName]` 固定类型、字段与枚举拼写（RetryPolicy、
APIKeyAutoDisableRule、APIKeyAutoDisableAction、FilterCondition、Any），
输出 DTO 的 nil 列表保持 GraphQL null（Go nil pointer 语义），非空列表保持
[]；`Any` 绑定 JsonElement 后由 NormalizeValue 复刻 Go 的 int64 / double
与字典序键。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 默认值与逐字节 JSON（策略 / 模型设置 / 安全设置）、TraceStickyMode 大小写 / null / 非字符串、legacy 迁移、归一化与钳位、auto-disable 逐条文案、cron 语料 86 例、字段顺序与 omitempty | `tests/AxonHub.Api.Tests/RetryPolicyCodecTests.cs` | 26 |
| 关联 / 条件类型解码、null 元素保留、developer 规范化与 modelId 清除、校验文案、Any 数值归一化、C130 的 null exclude | `tests/AxonHub.Api.Tests/ModelSettingsCodecTests.cs` | 11 |
| 严格读取 / 写入、错误前缀链、malformed 文档、期望归一化、规则校验、安全合并、存储失败 | `tests/AxonHub.Api.Tests/SystemSettingsAdminServiceTests.cs` | 22 |
| /admin/graphql 端到端：4 查询默认 / 存储值、nil 规则列表、3 变更加归一化 / 保留 / 合并、Any 绑定、编码错误信封、非编码异常消息、内省类型名 / 枚举值 / 字段顺序 / Any SCALAR | `tests/AxonHub.Api.Tests/AdminSystemSettingsEndpointTests.cs` | 9 |
| systems upsert：插入、冲突原地更新（id / created_at 保留）、软删除复活 | `tests/AxonHub.Persistence.Tests/SystemStoreTests.cs` | 3（新增） |

`AxonHub.Api.Tests` 由 1215 增至 1283（+68），`AxonHub.Persistence.Tests` 由
70 增至 73（+3），`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持
92；四个项目合计 4397 增至 4468，0 失败 0 跳过。

差异：新增 C130（exclude 列表中的 null 元素，参照 panic 而端口跳过）、C131
（时区后端 TimeZoneInfo vs Go tzdata）、C132（cron 解析器等价重实现，86 例
语料逐条锁定）、C133（Any 标量由 HotChocolate AnyType 承载）；C109 更新为
覆盖 system-settings 的类型 / 字段 / 枚举命名与非编码异常消息。差异总数
129 增至 133（80 接受 / 19 缺口 / 34 已解决）。

## 64. 管理面 users / roles 切片（用户 / 角色 CRUD、密码哈希、Cursor 与 Time 标量，48 例，2026-11-02）

参照路线：`internal/server/gql/ent.graphql:5419-5519`（Role / RoleConnection /
RoleEdge / RoleOrder / RoleLevel）、`:7107-7330`（User / UserConnection /
UserEdge / UserOrder / UserStatus）、`:2853-2905,5436,7190`（users / roles 的
连接参数与嵌套 roles 字段）、`ent.resolvers.go:503-516,579-592`（连接查询与
entgql 分页）、`axonhub.resolvers.go:526-579`（7 个变更与 GUID 解包）、
`internal/server/biz/user.go:51-244,590-636`（CreateUser / UpdateUser /
UpdateUserStatus / DeleteUser）、`internal/server/biz/role.go:44-312`
（CreateRole / UpdateRole / DeleteRole / BulkDeleteRoles / RoleNameExists）、
`internal/server/biz/auth.go:22-56`（HashPassword / VerifyPassword）、
`internal/server/gql/cursor.go:1-50`（msgpack + `base64.RawStdEncoding` 的
Cursor 标量）、`internal/objects/GUID.go`（`gid://axonhub/<Type>/<id>`）、
`internal/server/gql/pagination.go`（validatePaginationArgs）。

本次新增 `src/AxonHub.Api/GraphQL/ManagementTypes.cs`（5 个枚举、8 个输入
对象、7 个输出类型、校验与域映射）、`GraphQL/ManagementScalars.cs`
（`GidValue`、`CursorCodec`、Cursor 与 Time 标量）、
`Management/UserManagementService.cs`、`Management/RoleManagementService.cs`、
`Auth/PasswordHasher.cs`、`Persistence/Stores/UserAdminStore.cs`、
`Stores/RoleStore.cs`、`Stores/Listing.cs`（键集游标与排序）、
`Stores/ConnectionSql.cs`（方言无关的分页 SQL）、`Stores/StoreFailureException.cs`；
在 `AdminQuery.cs` 增加 `users` / `roles`，`AdminMutation.cs` 增加
`createUser` / `updateUser` / `updateUserStatus` / `deleteUser` / `createRole` /
`updateRole` / `deleteRole` / `bulkDeleteRoles`；`GraphQLSetup.cs` 注册两个
标量，`Program.cs` 注册 `IPasswordHasher` 与两个服务、两个存储；`CodedException`
新增 `DUPLICATE_NAME`。持久化层新增 `roles`、`user_roles`、`user_projects`
三表（列与索引逐一对齐 ent：`roles_by_project_id_name` 唯一、
`roles_by_level`、`user_projects_by_user_id_project_id` 唯一、
`user_projects_by_project_id`、`user_roles_by_user_id_role_id` 唯一、
`user_roles_by_role_id`）以及对应 EF 实体。

查询语义：顶层 `users` / `roles` 先走 `validatePaginationArgs`（`first` /
`last` 必须给出其一且在 1..1000，文案 `first must be greater than 0` /
`first cannot exceed 1000` / `either first or last must be provided`），再走
entgql 的 `validateFirstLast`（两者同时给报 ``Passing both `first` and `last`
to paginate a connection is not supported.``）。`User.roles` 嵌套连接只做
`validateFirstLast`，两个参数都省略时按 ID 升序返回全部角色——与 entgql 的
边解析器一致。默认订单恒为 ID 升序；`CREATED_AT` 重映射到 ID 顺序并保留
方向，`UPDATED_AT` 先按 `updated_at` 再按 `id` 排序（方向作用于两列）。
`totalCount` 是游标过滤前的命中数；`hasNextPage` / `hasPreviousPage` 由
first+1 / last+1 溢出置位，`startCursor` / `endCursor` 是首尾节点游标；
`before` / `after` 按元组键集比较。连接的 `id` 输出
`gid://axonhub/User/<id>`、`gid://axonhub/Role/<id>`，角色 `projectID` 输出
`gid://axonhub/Project/<id>`，`GidValue.Parse` 复刻 `GUID.UnmarshalGQL` 的
四条错误文案（`guid is empty`、`guid must start with gid://axonhub/`、
`guid must contain type and id`、`strconv.Atoi: parsing "...": invalid
syntax|value out of range`）。

变更语义：`createUser` 只消费 email / password / firstName / lastName /
scopes / roleIDs，密码存为 `hex(bcrypt cost 10)`（`$2a$` 版本、72 字节 UTF-8
上限、`!OIDC_SSO_ONLY!` 占位符原样存储），角色边去重后写入；`updateUser`
应用 email / firstName / lastName / isOwner / preferLanguage / avatar
（`clearAvatar` 优先）/ password（仅在提供时重哈希）/ scopes / appendScopes /
clearScopes / addRoleIDs / removeRoleIDs / clearRoles；`updateUserStatus` 在
同一事务内更新状态，`deactivated` 时把该用户仍启用的 personal API key 置为
disabled，失败文案为 `failed to disable user personal API keys: %w`
（`StoreFailureException` 原样上抛，服务不再加 `failed to update user
status: ` 前缀）；`deleteUser` 拒绝 `isOwner`（`cannot delete owner user,
transfer ownership first`），删除 user_projects / user_roles 关系、把
personal key 置为 archived（失败文案 `failed to archive user personal API
keys: %w`）并软删除用户。角色侧复刻 level / projectID 校验（`project ID is
not allowed for system roles`、`project ID is required for project roles`、
`invalid role level`）、按 level+project 的名称唯一性检查与
`CodedException(DUPLICATE_NAME)`（resource/field/value = role/name/<name>）、
`Developer` 默认角色保护（`cannot delete the default Developer role`）与批量
删除数量校验（`expected to find %d roles, but found %d`）；未提供的更新字段
不落库。

线路形状：`Time` 输出按 Go `time.RFC3339Nano` 语义（UTC、小数秒去尾零）；
`Cursor` 为不透明无填充 base64；`UserStatus` / `RoleLevel` / `OrderDirection` /
`UserOrderField` / `RoleOrderField` 的枚举值与 `UserOrder` / `RoleOrder` /
`UserWhereInput` / `RoleWhereInput` / `CreateUserInput` / `UpdateUserInput` /
`CreateRoleInput` / `UpdateRoleInput` 的类型名由 `[GraphQLName]` /
`[InputObjectType]` 固定为参照拼写。where 输入只建模前端契约用到的字段
（user：id / idIn / firstNameContainsFold / emailContainsFold / statusIn /
and / or / not；role：id / nameContainsFold / level / projectID / and / or /
not，见 C135）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| /admin/graphql 端到端：users / roles 查询形状与排序、gid、嵌套角色、Cursor 解码、键集分页与 PageInfo、where 过滤、内省枚举、7 个变更、DUPLICATE_NAME 扩展、owner / Developer 保护 | `tests/AxonHub.Api.Tests/AdminUsersRolesEndpointTests.cs` | 14 |
| 密码哈希：hex + `$2a$10$`、每次加盐、Verify 错误文案、OIDC 占位、72 字节与多字节 UTF-8 上限 | `tests/AxonHub.Api.Tests/PasswordHasherTests.cs` | 6 |
| 业务服务：not found 文案、密码仅在提供时重哈希、系统角色拒绝 projectID、BulkDelete 数量与空列表、重命名唯一性 / 改回自身、密钥禁用与归档失败不加前缀 | `tests/AxonHub.Api.Tests/ManagementServiceTests.cs` | 11 |
| users 存储：带角色创建与往返、过滤与键集分页、更新字段 / scope / 角色边与清空、停用联动禁用 personal key、删除归档 key 并清理关系、禁用 / 归档失败前缀与事务回滚 | `tests/AxonHub.Persistence.Tests/UserAdminStoreTests.cs` | 7 |
| roles 存储：全列往返、null project_id、按项目名称唯一与软删、仅更新提供字段、过滤 / 排序 / 键集分页、last 尾页、用户角色 join、删除清理边与软删、批量软删 | `tests/AxonHub.Persistence.Tests/RoleStoreTests.cs` | 10 |
| schema 快照与 EF 模型：三表列 / 类型 / 六个索引与 ent 逐项对齐（既有用例扩断言） | `tests/AxonHub.Persistence.Tests/SchemaTests.cs`、`EfCoreModelTests.cs` | — |

`AxonHub.Api.Tests` 由 1283 增至 1314（+31），`AxonHub.Persistence.Tests` 由
73 增至 90（+17），`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持
92；四个项目合计 4468 增至 4516，0 失败 0 跳过。

差异：新增 C134（Cursor 线路载荷 JSON vs msgpack，标量不透明性保留）、C135
（where 输入为前端契约子集）、C136（角色删除时邀请撤销为 no-op）、C137
（Time 输入解析比 Go 的 `time.Parse(time.RFC3339)` 宽松）、C138
（BCrypt.Net-Next 替代 golang.org/x/crypto/bcrypt，输出与错误文案已对齐）；
C014 补记为「users / roles 管理面已移植，权限校验器（CanEditUserPermissions
/ CanGrantScopes / CanEditRole / CanDeleteUser）与管理员 GraphQL 鉴权仍未
接入」。差异总数 133 增至 138（83 接受 / 21 缺口 / 34 已解决）。

## 65. 管理面 channels 读取切片（entgql channels / queryChannels、聚合查询与前端契约扩展，22 例，2026-11-02）

参照路线：`internal/server/gql/ent.resolvers.go:372-381`（`channels` 的 entgql
连接查询）、`:67-105`（Channel 的 id / policies / settings /
providerQuotaStatus 字段解析器）、`internal/server/gql/axonhub.resolvers.go:35-47`
（defaultEndpoints）、`:49-56`（allModelEntries）、`:58-79`（credentials，含
`ScopeWriteChannels` 门禁）、`:81-93`（disabledAPIKeys）、`:95-115`
（liveLimiterStats）、`:824-881`（allChannelSummarys 非项目分支）、
`:883-910`（allChannelTags）、`:912-942`（countChannelsByType）、
`:944-957`（queryChannels）、`internal/server/biz/channel_query.go:1-121`
（QueryChannels / queryChannelsWithModelFilter / buildConnectionInMemory）、
`internal/server/biz/channel_endpoint.go:189-260`（按通道类型的默认端点表）、
`internal/server/gql/ent.graphql:407-543,547-560,564-573,946-955,959-966,
1261-1265,1269-1344,1349-1576`（Channel / ChannelConnection / ChannelEdge /
ChannelOrder / ChannelOrderField / ChannelStatus / ChannelType /
ChannelWhereInput）、`internal/server/gql/axonhub.graphql:60-65,85-89,99-104,
196-215,217-222,263-267,309-330,669-672,809-826,1054-1101`（TransformOptions /
ProxyType / ChannelEndpoint / ChannelSettings / ChannelQuotaRoutingMode /
ChannelPolicies / ChannelRateLimit / ChannelCredentials / DisabledAPIKey /
ChannelTypeCount / ChannelLimiterStats / QueryChannelInput /
CountChannelsByTypeInput）。

本次新增 `src/AxonHub.Api/GraphQL/ChannelTypes.cs`（10 个枚举、4 个输入对象、
33 个输出类型与 `Map` 标量）、`GraphQL/ChannelMapping.cs`（域 ↔ GraphQL
映射、默认端点表、settings / credentials / policies / 限额统计解析）、
`Management/ChannelQueryService.cs`（5 个顶层查询）、
`Persistence/Stores/ChannelAdminStore.cs`（Dapper 读取，含键集分页与四条聚合
SQL）；在 `AdminQuery.cs` 增加 `channels` / `queryChannels` /
`allChannelSummarys` / `allChannelTags` / `countChannelsByType`，
`GraphQLSetup.cs` 注册 `Map` 标量，`Program.cs` 注册 `IChannelAdminStore` 与
`ChannelQueryService`。`CursorCodec` 的载荷增加 `ValueKind` 以承载
string / int 排序值（C134），`ListCursor.Value` 改为 `object?`；
`ConnectionSql.AppendCursor` 在游标不带排序值时退回仅按 id 比较；
`llm` 模块的 `ApiFormats` 补 `openai/responses-ws`（codex 默认端点）。

查询语义：`channels` 先走 entgql 的 `validateFirstLast`（两者同时给报
``Passing both `first` and `last` to paginate a connection is not supported.``），
`CREATED_AT` 重映射为 id 顺序并保留方向，`UPDATED_AT` / `TYPE` / `NAME` /
`STATUS` / `ORDERING_WEIGHT` 分别按 `updated_at` / `type` / `name` / `status` /
`ordering_weight` 做键集游标；非 id 字段追加同方向的 id 次序（entgql
`applyOrder`），`last` 时方向反转并在取回后倒序。`totalCount` 是游标过滤前的
命中数；`first+1` / `last+1` 溢出置 `hasNextPage` / `hasPreviousPage`，
`before` / `after` 本身也分别置 `hasNextPage` / `hasPreviousPage`；`first` /
`last` 为 0 时不查行、只保留计数推导的 PageInfo。`queryChannels` 先重映射
orderBy 与 where，`hasTag` 非空时按 `sqljson.ValueContains("tags", tag)` 过滤
（PostgreSQL 为 `tags @> '<json>'`）；`model` 为空时走上面的 Paginate，否则
拉取全部命中行、按 `ChannelModelEntries.Build` 的键集合做精确模型匹配，并
`buildConnectionInMemory`：Edges 恒非 null、PageInfo 两个标志恒 false、
StartCursor / EndCursor 取首尾边、TotalCount = 命中数，分页参数与其校验被
完全忽略（`first` + `last` 同时给也不报错）。`allChannelSummarys` 只取
enabled / disabled（`includeArchived` 时加 archived），按
`ordering_weight DESC` 排序；`allChannelTags` 取所有非 archived 行的 tags
列，按行序摊平后保留首次出现；`countChannelsByType` 在 `statusIn` 非空时按
它过滤、否则排除 archived，按 type 分组计数。五条查询都带软删谓词
`deleted_at = 0`，聚合失败分别返回 `failed to query channels: %w` 与
`failed to query channel type counts: %w`。

线路形状：`Channel` 的 `id` 输出 `gid://axonhub/Channel/<id>`，`createdAt` /
`updatedAt` 按 RFC3339Nano（UTC、小数秒去尾零），`supportedModels` 是普通
字符串（缺省 `"null"`，与 ent 列一致），`manualModels` / `tags` 是可空列表
（列为 null 时输出 null），`policies` 恒为对象且 `stream` 缺省 `unlimited`，
`settings` 在列为 null / `"null"` 时为 null，`transformOptions`、`rateLimit`、
`responseTimeout`、`providerQuota` 在 settings 非空时按字段生成；枚举按
`[GraphQLName]` 固定为参照拼写，未知存储值在输出时抛
`Enum "ChannelType" cannot represent value: "..."`（HotChocolate 的枚举
marshaler 语义）。`ProxyType` 在 SDL 中拼写为 DISABLED / ENVIRONMENT / URL，
但参照的 external 类型按原始小写字符串 marshaling，端口按前端实际读取的
小写值建模（C139）。`credentials` 复刻 OAuth / antigravity 规则（OAuth 或
类型为 antigravity 时只回 apiKey 原样、apiKeys 为 null；否则 apiKeys = 旧
apiKey + apiKeys 数组 + vertexApiKeys[i].apiKey，存在 vertex key 时丢弃全部
扁平字符串，`vertexApiKeys` 仅 gemini_vertex 回填，空数组输出 null）；
`disabledAPIKeys` 的零值时间按 `0001-01-01T00:00:00Z` 输出；`liveLimiterStats`
只在该通道配置了 positive `maxConcurrent` 时返回统计（无配置或无管理器返回
null）；`defaultEndpoints` 按 `biz.DefaultEndpointsForChannelType` 的表输出
（含前端实时契约的 cloudflare：chat_completions + embeddings；codex：
responses、responses-ws、alpha_search、image_generation、image_edit）；
`allModelEntries` 按请求模型名稳定排序；`providerQuotaStatus` 当前恒为
null（C139）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| /admin/graphql 端到端：channels 连接形状与 CREATED_AT 重映射、string / int 键集游标、first / last 校验、queryChannels 的 where 与 hasTag、模型分支忽略分页、四个聚合查询、Policies / settings / credentials / disabledAPIKeys / limiter 统计 / defaultEndpoints / allModelEntries、providerQuotaStatus 与未知枚举、损坏 JSON 列降级、错误前缀、内省枚举 | `tests/AxonHub.Api.Tests/AdminChannelsEndpointTests.cs` | 18 |
| channels 存储：statusIn / nameContainsFold / errorMessageIsNil / hasTag 过滤、键集分页与 last 倒序、ListAll 仅在给定 orderBy 时排序、摘要过滤与权重排序、tag 行与类型计数 | `tests/AxonHub.Persistence.Tests/ChannelAdminStoreTests.cs` | 4 |

`AxonHub.Api.Tests` 由 1314 增至 1332（+18），`AxonHub.Persistence.Tests` 由
90 增至 94（+4），`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持
92；四个项目合计 4516 增至 4538，0 失败 0 跳过。

差异：新增 C139（Channel 读取侧按管理面前端实时契约扩展，provider quota
子系统未移植，`providerQuotaStatus` 恒为 null）、C140（并列顺序补 id / type
升序）、C141（通道 JSON 列解码失败降级为空）；补记 C134（游标载荷新增
ValueKind 以承载 string / int 排序值）、C135（ChannelWhereInput 一并纳入前端
契约子集）、C014（channels 读取面已移植，写操作与管理员鉴权仍未接入）。差异
总数 138 增至 141（85 接受 / 22 缺口 / 34 已解决）。

## 66. 管理面 channels 生命周期写入切片（状态 / 删除 / 批量状态 / 批量删除 / 批量标签，23 例，2026-11-02）

参照路线：`internal/server/gql/axonhub.resolvers.go:203-303`（10 个通道生命周期
resolver）、`internal/server/biz/channel.go:1205-1220`（UpdateChannelStatus）、
`:1280-1290`（DeleteChannel）、`internal/server/biz/channel_bulk.go:163-204`
（bulkUpdateChannelStatus）、`:344-362`（BulkDeleteChannels）、`:365-432`
（BulkManageChannelTags）、`internal/ent/schema/schematype/soft_delete.go:84`
（软删除钩子）、`internal/server/gql/graphql.go:143`（entgql Transactioner 与两个
Skip 例外）、`internal/server/gql/axonhub.graphql:944-953`（10 个变更签名）。

本次新增 `src/AxonHub.Persistence/Stores/ChannelWriteStore.cs`（`IChannelWriteStore`
与 EF Core 实现）、`src/AxonHub.Api/Management/ChannelLifecycleService.cs`
（biz 层错误前缀与限流器失效）；`StoreFailureException.cs` 增加
`ExpectedRowsMismatchException`（携带期望 / 实际行数，复现 biz 层
`expected to find %d channels, but found %d`）；`AdminMutation.cs` 增加 10 个
resolver（`updateChannelStatus`、`deleteChannel`、`bulkArchiveChannels`、
`bulkDisableChannels`、`bulkEnableChannels`、`bulkRecoverChannels`、
`bulkDeleteChannels`、`bulkAddChannelTags`、`bulkRemoveChannelTags`、
`bulkManageChannelTags`），`Program.cs` 注册 `IChannelWriteStore` 与
`ChannelLifecycleService`。

写入语义：`updateChannelStatus` 等价于
`UpdateOneID(id).SetStatus(status).ClearAutoDisabledAt().ClearAutoDisableExpiresAt().Save`，
不动 `error_message`；缺失 / 已软删行返回 nil，resolver 报
`failed to update channel status: ent: channel not found`，其余失败加
`failed to update channel status: %w`。`deleteChannel` 是软删除
（`deleted_at = time.Now().Unix()` 秒、`updated_at` 同时更新），成功后调用
`forgetLimiter(id)`；未命中报 `failed to delete channel: ent: channel not found`，
其余失败加 `failed to delete channel: %w`。返回的 Channel 是 Ent 的已变更实体
（`updated_at` 为内存时间戳，而非存储截断值）。

批量状态（archive / disable / enable / recover）：空 ids 直接返回；先按
`IDIn(ids)` 统计活行数，与 ids 数量不符即报
`expected to find %d channels, but found %d`（重复 id 也会失败），全部在写任何行
之前；通过后统一置 status、清空自动禁用标记，recover 额外清空 `error_message`；
失败按动作词包成 `failed to archive|disable|enable|recover channels: %w`。
`bulkDeleteChannels` 不做数量校验：`Delete().Where(IDIn(ids))` 只软删命中的活行，
随后对每个请求 id（含未命中的）调用 `forgetLimiter`；失败包成
`failed to bulk delete channels: %w`。批量标签：`lo.Uniq(ids)` 保序去重，
`normalizeChannelTags` 逐项 Trim、丢弃空串、保序去重；ids 或两侧标签全空时
no-op；按 ids 载入活行并校验数量；逐通道先删 remove 集合、再追加未出现过的
add 标签，无变化则跳过；有变化时以 `updated_at = 载入值` 为乐观守卫更新，
守卫失效报 `channel %d was modified while managing tags; please retry: ent: channel not found`，
其它更新失败报 `failed to manage tags for channel %d: %w`，查询失败报
`failed to query channels for bulk tag management: %w`。

事务：参照的 entgql Transactioner 把整个 mutation 包在一个 Ent 事务里；端口每个
变更只调用一次存储方法，由存储方法内部的事务保证原子性（批量状态与标签管理显式
`BeginTransaction`，单行删除是单条 `SaveChanges`，批量删除是单条 `ExecuteUpdate`），
登记为 C142。管理员 GraphQL 鉴权与 `ScopeWriteChannels` 门禁仍未接入（C014），
本切片只复刻持久化行为与参照错误文案。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| /admin/graphql 端到端：状态变更清空自动禁用标记且保留 error、gid 类型段被忽略、缺失行文案、删除软删并清限流器、批量四动作与 recover 清 error、数量不符、批量删除容忍缺失 id、标签 Trim / 去重 / no-op / 双列表委派、存储失败前缀、空列表、内省 | `tests/AxonHub.Api.Tests/AdminChannelLifecycleEndpointTests.cs` | 14 |
| 存储：状态 / 标记 / updated_at、软删除 Unix 秒并隐藏行、数量校验不产生部分写、recover 清 error、批量删除只命中活行、标签增删与未变更行跳过、缺失行回滚、null tags 视为空数组 | `tests/AxonHub.Persistence.Tests/ChannelWriteStoreTests.cs` | 9 |

`AxonHub.Api.Tests` 由 1332 增至 1346（+14），`AxonHub.Persistence.Tests` 由
94 增至 103（+9），`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持
92；四个项目合计 4538 增至 4561，0 失败 0 跳过。

差异：新增 C142（GraphQL 变更的事务边界：entgql Transactioner 改为存储层单方法
事务）；更新 C014（通道生命周期写入已接入，createChannel / updateChannel 与
管理员鉴权仍缺）。差异总数 141 增至 142（86 接受 / 22 缺口 / 34 已解决）。

## 67. 管理面 channels createChannel 写入切片（写入校验、ent 默认值与凭据序列化，18 例，2026-11-02）

参照路线：`internal/server/gql/axonhub.resolvers.go:174-177`（CreateChannel resolver）、
`internal/server/biz/channel.go:633-671`（同名检查 → `xerrors.DuplicateNameError` →
`RunInTransaction { createChannel; ensureChannelModelPrices }` → 提交后重载）、
`:527-630`（`createChannel` 的归一化与校验顺序）、`:675-758`（retryable codes /
patterns 与 auto-disable 规则归一化）、`:1312`（Command Code cookie 规范化）、
`internal/server/biz/channel_endpoint.go:43-142`（端点与模型协议校验）、
`internal/server/biz/channel_override.go:181-266`（body / header 覆盖操作校验）、
`internal/server/biz/channel_rate_limit.go:15`（限流参数校验）、
`internal/server/biz/channel_price.go:265`（ensureChannelModelPrices，缺口见 C143）、
`internal/server/gql/ent.graphql:1616-1644`（CreateChannelInput）与
`internal/server/gql/axonhub.graphql:407-435`（live 凭据输入）。

本次新增 `src/AxonHub.Api/Management/ChannelWriteModels.cs`（Go 字段标签的写入草稿与
`CreateChannelCommand`）、`ChannelWriteSerialization.cs`（草稿 → 存储记录，复刻 ent
零值语义）与 `ChannelValidation.cs`（端点、模型协议、覆盖操作、限流、重试、命令码
cookie、顶点密钥与 auto-disable 校验）；`ChannelWriteStore.cs` 增加 `CreateAsync`
（活行同名检查、插入、`failed to create channel: %w`），
`ChannelLifecycleService.CreateAsync` 串起顺序；`AdminMutation.cs` 增加
`createChannel`，`ChannelInputTypes.cs` / `ChannelInputMapping.cs` 补齐 live 输入
字段（`oauth` 显式命名，`idToken` 不属于契约）。

写入顺序与参照逐句对齐：命令码 cookie 规范化（仅 commandcode；其它类型丢弃该块）→
xai_subscription 强制 BaseURL 为 `https://cli-chat-proxy.grok.com/v1` 并清空端点 →
命令码 BaseURL HTTPS 检查 → auto-disable 规则归一化 → settings 块（body / header
操作、rateLimit、responseTimeout、retryable codes、patterns、maxSingleChannelRetries、
model protocols，按 live 相对位置插入 live-only 校验器）→ 端点校验 → 构造 ent
create 输入（不设置 orderingWeight，始终为 0）。同名检查只命中活行（软删名可复用），
冲突返回 `DUPLICATE_NAME`，文案 `channel name '<name>' already exists`，extensions
`{resource:"channel", field:"name", value:<name>}`。

ent 默认值：未提供的 settings / manualModels 存 JSON `null`，policies 缺省
`{"stream":"unlimited"}`，tags / endpoints / disabledApiKeys 存 `[]`，status
`disabled`，autoSyncSupportedModels false，autoSyncModelPattern 空串，
orderingWeight 0，createdAt / updatedAt 同一时刻。settings 与 credentials 按 Go
声明序序列化，`omitempty` 字段缺省即省略，`overrideHeaders` 无 `omitempty` 因而
输出 `null`；responseTimeout 仅在 mode=custom 时保留。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| /admin/graphql 端到端：最小输入 ent 默认值、settings / credentials Go 字段序与 omitempty、空列表与省略的存储差异、xai 基址覆盖、命令码 cookie 规范化与其它类型丢弃、命令码错误包装、设置校验顺序与前缀、responseTimeout 继承丢弃、端点校验晚于设置块、顶点密钥校验、同名 DUPLICATE_NAME、软删名复用、存储失败前缀、内省 | `tests/AxonHub.Api.Tests/AdminChannelCreateEndpointTests.cs` | 14 |
| 存储：§66 的 9 例，另加 create 插入全列往返（jsonb 规范化）、同名活行返回 null、软删名可复用、插入失败前缀 | `tests/AxonHub.Persistence.Tests/ChannelWriteStoreTests.cs` | 13 |

`AxonHub.Api.Tests` 由 1346 增至 1360（+14），`AxonHub.Persistence.Tests` 由
103 增至 107（+4），`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持
92；四个项目合计 4561 增至 4579，0 失败 0 跳过。

差异：新增 C143（createChannel 未写入 channel_model_prices）、C144（写入面按管理面
实时契约扩展，端点规范化与部分校验位置保留冻结基线）、C145（并发同名的唯一索引
冲突文案）；更新 C014（createChannel 已接入，updateChannel 等仍缺）与 C139（写侧
消费 live 字段）。差异总数 142 增至 145（88 接受 / 23 缺口 / 34 已解决）。

## 68. 管理面 channels updateChannel 写入切片（部分列更新、乐观守卫与 live 校验器，24 例，2026-11-02）

参照路线：`internal/server/gql/axonhub.resolvers.go:188-190`（UpdateChannel resolver）、
`internal/server/biz/channel.go:849-1203`（身份快照、命令码 / xai 归一化、名称唯一、
设置同步与校验、端点 / 协议校验、事务内的部分列 builder 与 `UpdatedAtEQ` 守卫、
`ensureChannelModelPrices`）、`:1300-1404`（命令码 cookie 归一化 / 清空 / 空白判定）、
`internal/server/biz/channel_endpoint.go`（端点与模型协议校验）、
`internal/server/biz/channel_override.go`（覆盖操作校验）、
`internal/server/biz/channel_rate_limit.go`（限流参数校验）、
`internal/server/gql/ent.graphql:6185-6237`（UpdateChannelInput）与
`internal/server/gql/axonhub.graphql`（live settings / credentials 输入）。

本次新增 `ChannelUpdateRecord` 与 `IChannelWriteStore.GetForUpdateAsync` /
`NameExistsAsync` / `UpdateAsync`（`ChannelWriteStore.cs`）：快照读取沿用
SoftDeleteMixin 谓词（`failed to load channel provider identity: %w`）；同名检查为
`Name(...) AND IDNEQ(id)` 且只命中活行（`failed to check channel name: %w`）；
更新用单条 `ExecuteUpdateAsync` 把每列设为「调用方提供的值，否则保持自身」，
`remark` / `auto_sync_model_pattern` / `error_message` 的清空标志写 NULL，
`updated_at` 同时刷新；提供 `ExpectedUpdatedAt` 时在同一语句追加
`updated_at = 载入值`，0 行即 `channel was updated concurrently; retry the operation`，
其它失败包成 `failed to update channel: %w`。`ChannelLifecycleService.UpdateAsync`
串起参照顺序：身份快照 → effectiveType → guard（仅 Type / BaseURL / Endpoints /
命令码 settings 变更）→ 命令码空 cookie 清空、规范化与 live 的
`ValidateMaxSingleChannelRetries` → auto-disable 归一化 → xai_subscription 强制
官方 BaseURL 并清空端点（Type 缺省时按存储类型判定）→ 命令码 BaseURL HTTPS 校验 →
活行同名检查与 `DUPLICATE_NAME` → supportedModels 的协议覆盖裁剪（无输入 settings
时载入存储值，仅在确有裁剪时回写）→ body / header / rateLimit / live
responseTimeout / retryable 校验 → 模型协议校验 → 端点校验 → 类型离开命令码且未带
settings 时清理孤立 quota → 凭据解析、zenmux `managementApiKey` 保留与顶点密钥校验 →
保存。输入中参照服务不读取的字段（status、clearBaseURL、clearManualModels、
clearTags、clearPolicies、clearSettings、clearEndpoints、各 append* 与 errorMessage）
只保留在 SDL 中；ent 的 `ClearRemark` / `ClearAutoSyncModelPattern` /
`ClearErrorMessage` 均写 NULL，类型变更清理后的 settings 以 JSON `null` 落库。
返回值为「快照 + 已应用列」的投影，而非 ent 的已保存实体，见 C146。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| /admin/graphql 端到端：仅写提供的列并忽略 status / clearSettings / errorMessage、三个清空标志写 NULL、同名 DUPLICATE_NAME 与 extensions、软删名复用、xai 类型强制与存储 xai 类型的基址强制、命令码 cookie 规范化、空 cookie 清空但保留 ollama、类型切换清理孤立 quota、zenmux managementApiKey 保留、supportedModels 协议裁剪、live 重试上限先于设置校验、顶点密钥校验、并发守卫文案、身份读取失败文案、内省 | `tests/AxonHub.Api.Tests/AdminChannelUpdateEndpointTests.cs` | 16 |
| 存储：§67 的 13 例，另加快照只返回活行、同名检查排除自身与软删行、部分列写入并刷新 updated_at、清空标志写 NULL 与 jsonb null、版本守卫命中 / 失配、无守卫更新忽略旧版本、缺失 / 软删行返回 null、唯一索引冲突前缀 | `tests/AxonHub.Persistence.Tests/ChannelWriteStoreTests.cs` | 21 |

`AxonHub.Api.Tests` 由 1360 增至 1376（+16），`AxonHub.Persistence.Tests` 由
107 增至 115（+8），`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持
92；四个项目合计 4579 增至 4603，0 失败 0 跳过。

差异：新增 C146（单次快照读取与投影返回）；更新 C014（updateChannel 已接入，其余
通道写操作与管理员鉴权仍缺）、C139（写侧 updateChannel 同步消费 live 字段）、
C143（updateChannel 同样未调用 ensureChannelModelPrices）、C144（updateChannel 的
live 校验器相对位置与冻结基线）、C145（更新同名的唯一索引冲突与前置检查窗口同族）。
差异总数 145 增至 146（89 接受 / 23 缺口 / 34 已解决）。

## 69. 管理面 channels 批量写入切片（bulkUpdateChannelOrdering / bulkUpdateChannelAutoDisable，20 例，2026-11-02）

参照路线：`internal/server/gql/axonhub.resolvers.go:390-402`（BulkUpdateChannelOrdering
resolver）、`:404-415`（BulkUpdateChannelAutoDisable resolver）、
`internal/server/biz/channel_bulk.go:23-45`（排序逐项 `UpdateOneID(...).SetOrderingWeight(...).Save`）、
`:242-340`（自动禁用的动作校验、`normalizeAutoDisableRules(..., true)`、事务内
`IDIn` 查询与逐通道 `SetPolicies`）、`internal/server/gql/axonhub.graphql:445-477`
（两个输入、`BulkAutoDisableAction` 枚举与两个返回类型）。

本次新增 `ChannelOrderingItem` 记录与 `IChannelWriteStore.BulkUpdateOrderingAsync` /
`BulkUpdateAutoDisableAsync`（`ChannelWriteStore.cs`）。排序在一个事务里逐项按
`ID + 未软删` 读取活行，缺失时以 `failed to update channel {id}: ent: channel not
found` 失败（对应 ent `UpdateOneID` 的 NotFound 文案），逐项写 `ordering_weight` 与
`updated_at`，单项保存失败按 `failed to update channel {id}: %w` 包装，整批提交后
返回更新行（参照在事务提交后重新加载）。自动禁用同样在一个事务内先按
`IDIn(ids) AND 未软删` 查询，结果数不等于请求数时抛
`expected to find %d channels, but found %d`（查询失败包
`failed to query channels: %w`），随后逐个通道把存储的 policies 解析为 JSON 对象、
写 `apiKeyAutoDisableMode`：`custom` 复制归一化后的规则、`inherit` 删除规则键、
`off` 保留存储规则；单通道保存失败按
`failed to update auto-disable policies for channel %q (%d): %w` 包装。

`ChannelLifecycleService.BulkUpdateOrderingAsync` 只把存储错误转成
`InvalidOperationException`，resolver 再加 `failed to bulk update channel ordering: `
前缀；`BulkUpdateAutoDisableAsync` 在事务前完成参照的全部校验：空 ID 列表 →
`channel IDs are required`（VALIDATION_FAILED）、`write_rules` 必须有非空规则列表
（否则 `rules are required when writing auto-disable rules`），再经
`NormalizeAutoDisableRules(..., allowDelete: true)` 归一化，非法规则转
VALIDATION_FAILED；`inherit` / `off` 忽略提交的规则；未知动作保留参照的
`unsupported auto-disable action %q` 分支（GraphQL 枚举下不可达，由服务级测试
锁定）。自动禁用 resolver 与参照一样不加错误前缀，计数不匹配的文案原样返回。
输入映射把 GID 解析为内部 ID，并把枚举映射回参照的线上字符串
（`write_rules` / `inherit` / `off`）后再进入服务层的字符串 switch。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| /admin/graphql 端到端：排序逐项写入与返回顺序、缺失行的 resolver 前缀、空列表仍调用一次存储、write_rules 归一化（状态码排序去重、模式裁剪去空）、permanent_disable_delete、inherit 清规则并保留 stream、off 忽略输入并保留存储规则、空 ID / 缺规则 / 非法规则的写入前校验、服务级未知动作分支、计数不匹配无前缀、内省 | `tests/AxonHub.Api.Tests/AdminChannelBulkEndpointTests.cs` | 13 |
| 存储：§68 的 21 例，另加排序全量写入与 `updated_at` 刷新、缺失 / 软删行回滚整批、write_rules 保留 stream 与归一化规则、inherit 清规则、off 保留规则、null policies、ID 计数先于写入（重复与软删） | `tests/AxonHub.Persistence.Tests/ChannelWriteStoreTests.cs` | 28 |

`AxonHub.Api.Tests` 由 1376 增至 1389（+13），`AxonHub.Persistence.Tests` 由
115 增至 122（+7），`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持
92；四个项目合计 4603 增至 4623，0 失败 0 跳过。

差异：新增 C147（自动禁用写入直接编辑 policies JSON 对象，未知字段与显式空数组的
保留边界）；更新 C014（批量排序与批量自动禁用已接入，其余通道写操作与管理员鉴权
仍缺）。差异总数 146 增至 147（90 接受 / 23 缺口 / 34 已解决）。

## 70. 管理面 channels saveChannelEndpoints 写入切片（端点校验与版本守卫，14 例，2026-11-02）

参照路线：`internal/server/gql/axonhub.resolvers.go:194-197`（SaveChannelEndpoints resolver，原样转发错误）、
`internal/server/biz/channel.go:1243-1275`（读取通道 → `validateEndpointsForChannelType` →
`xai_subscription` 拒绝 → `ValidateModelProtocols(ch.Settings, ch.Type, input.Endpoints)` →
`UpdateOneID(...).Where(UpdatedAtEQ(ch.UpdatedAt)).SetEndpoints(...).Save` → reload）、
`internal/server/gql/axonhub.graphql:106-116`（`ChannelEndpointInput` / `SaveChannelEndpointsInput`）。

本次新增 `SaveChannelEndpointsCommand`、GraphQL `SaveChannelEndpointsInput`、
`ChannelInputMapping.ToCommand`、`ChannelLifecycleService.SaveEndpointsAsync` 与
`IChannelWriteStore.GetAsync` / `SaveEndpointsAsync`。服务层先按 `ID + 未软删`
读取活行（缺失 → `failed to get channel: ent: channel not found`），按通道类型校验端点
（`invalid endpoints: %w`），`xai_subscription` 一律拒绝（`xAI subscription channels do
not support custom endpoints`），再解析存储的 settings 并按已存 `modelProtocols` 校验
新端点（`invalid endpoints for configured model protocols: %w`），最后只写 `endpoints`
列并以读取时的 `updated_at` 做乐观守卫（0 行 → `channel was updated concurrently;
retry the operation`；存储失败 → `failed to update channel endpoints: %w`）。端点 JSON 按
Go 的 `api_format` / `path` / `base_url` / `transport` 字段序与 `omitempty` 语义序列化，
空列表写 `[]`。resolver 与参照一样原样转发错误。live 版本在该变更里额外做
`NormalizeEndpoints` / `normalizeModelProtocols` 与 settings 重写，端口按冻结语义延后
（C148）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| /admin/graphql 端到端：写入端点并只改 endpoints 列、空列表、重复 api_format、完整 URL path、xai_subscription、已存 modelProtocols 校验、缺失通道、并发守卫、存储失败透传、内省 | `tests/AxonHub.Api.Tests/AdminChannelEndpointsEndpointTests.cs` | 10 |
| 存储：§69 的 28 例，另加活行读取、端点 JSON 写入与 `updated_at` 刷新（settings 不动）、版本守卫 / 缺失与软删行、空数组 | `tests/AxonHub.Persistence.Tests/ChannelWriteStoreTests.cs` | 32 |

`AxonHub.Api.Tests` 由 1389 增至 1399（+10），`AxonHub.Persistence.Tests` 由 122 增至
126（+4），`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持 92；四个项目合计
4623 增至 4637，0 失败 0 跳过。

差异：新增 C148（saveChannelEndpoints 按冻结语义写端点，live 的端点规范化与 settings
重写延后）；更新 C014（端点保存已接入）、C144 与 C146（saveChannelEndpoints 沿用冻结
校验与单快照守卫写入）。差异总数 147 增至 148（91 接受 / 23 缺口 / 34 已解决）。

## 71. 管理面 channels API key 记账切片（禁用 / 启用 / 删除，29 例，2026-11-02）

参照路线：`internal/server/gql/axonhub.resolvers.go:418-462`（disableChannelAPIKey /
enableChannelAPIKey / enableAllChannelAPIKeys / enableSelectedChannelAPIKeys /
deleteDisabledChannelAPIKeys 五个 resolver）、`internal/server/biz/channel_apikey.go`
（`DisableAPIKey` / `EnableAPIKey` / `EnableAllAPIKeys` / `EnableSelectedAPIKeys` /
`DeleteDisabledAPIKeys` 与 `applyRecoveredChannelStatus`）、
`internal/objects/channel.go:315-470`（`DisabledAPIKey`、`OAuthCredentialRef`、
`GetAllCredentialRefs` / `GetEnabledCredentialRefs` / `filterDisabled`）、
`internal/server/gql/axonhub.graphql:932-935`（payload）与 `:966-974`（五个变更）。

本次新增 `DisabledApiKey` 抽象（RFC3339Nano 的 `disabledAt`，可选的 `reason` /
`expiresAt`）、`ChannelCredentials` 的凭据引用辅助（OAuth 通道固定哨兵
`__oauth__`，其余由 `apiKey` + `apiKeys` 组成）、
`ChannelWriteSerialization.ParseDisabledApiKeys` / `SerializeDisabledApiKeys`、
`IChannelWriteStore.UpdateApiKeyStateAsync`（始终写 `disabled_api_keys`，凭据仅在显式提供时
写，按 set / clear 标志写状态、错误、自动禁用标记）、
`ChannelLifecycleService` 的五个操作与 `DeleteDisabledApiKeysResult`，以及五个
GraphQL 变更与 `DeleteDisabledAPIKeysPayload`。禁用把记录追加到 `disabled_api_keys`
（过期的临时禁用先被清理），全部 key 都被禁用时把通道置为 `disabled` 并写
`All API keys disabled (last error: %d)`、`autoDisabledAt` 并清除 `autoDisableExpiresAt`；
启用按 key 移除禁用记录，若错误文案以 `All API keys disabled` 开头且仍有启用的凭据引用则
恢复 `enabled` 并清空错误与两个自动禁用时间（其他 key 仍禁用也恢复）。删除拒绝 OAuth
通道，从 `apiKey` / `apiKeys` 移除后若一个 key 都不剩则把首个 key 还原进 `apiKeys` 并返回
`ONE_KEY_PRESERVED`。服务层错误带 `failed to disable api key: ` 等前缀，resolver 再叠加
`failed to disable channel API key: ` 等前缀；读取失败统一为
`failed to get channel: ent: channel not found`。操作经静态 `SemaphoreSlim` 串行化
（参照的 `sync.Mutex` 不可取消，端口用 `CancellationToken.None` 等待）。live 的
Vertex key 参与、`ChannelAutoDisabledEvent` 与启用通道缓存刷新按冻结语义延后（C149）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| /admin/graphql 端到端：禁用追加记录且通道保持 enabled、第二个 key、最后一个 key 自动禁用（文案 / 状态 / 标记）、OAuth 哨兵、未知 key 与已禁用 key 无操作、过期记录清理、空 key、缺失通道的双前缀、损坏的禁用手账读取前缀、启用恢复、其他 key 仍禁用时的恢复、非前缀错误不恢复、启用缺失记录无操作、enableAll、enableSelected（含恢复）、空选择列表不读取、删除保留最后一个 key（`ONE_KEY_PRESERVED` 与 `apiKeys` 还原）、删除旧式 key、删除后的恢复、OAuth 拒绝、空删除列表、存储失败与缺失行的前缀叠加、内省 | `tests/AxonHub.Api.Tests/AdminChannelApiKeyEndpointTests.cs` | 25 |
| 存储：§70 的 32 例，另加 `disabled_api_keys` 与自动禁用列写入、未指定列不动、恢复标记清空、缺失与软删行返回 false | `tests/AxonHub.Persistence.Tests/ChannelWriteStoreTests.cs` | 36 |

`AxonHub.Api.Tests` 由 1399 增至 1424（+25），`AxonHub.Persistence.Tests` 由 126 增至
130（+4），`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持 92；四个项目合计
4637 增至 4666，0 失败 0 跳过。

差异：新增 C149（通道 API key 记账按冻结语义实现，live 的 Vertex key 删除与自动禁用
事件 / 缓存刷新延后）；更新 C014（API key 禁用 / 启用 / 删除记账已接入）。差异总数
148 增至 149（92 接受 / 23 缺口 / 34 已解决）。

## 72. 管理面 channels detectChannelEndpoints 探测切片（三格式并发探测，28 例，2026-11-02）

参照路线：`internal/server/gql/axonhub.resolvers.go:199-201`（DetectChannelEndpoints
resolver，原样转发错误）、`internal/server/biz/channel_endpoint_detect.go:72-240`
（`DetectChannelEndpoints` / `detectChannelEndpoint` / `buildEndpointDetectRequest` /
`resolveEndpointDetectModel`）、`internal/server/biz/channel_llm.go:208-260`
（`buildChannelWithTransformer`）、`:393-560`（`buildChannelWithOutbounds` 与
`defaultEndpointsForChannelType`）、`:573-638`（`buildNonDefaultEndpointOutbound`）、
`internal/server/biz/channel_endpoint.go:225-345`（`validateEndpointsForChannelType` 与
`ValidateEndpoints`）、`internal/server/gql/axonhub.graphql:118-134`
（`DetectChannelEndpointsInput` / `DetectedChannelEndpoint` /
`DetectChannelEndpointsPayload`）与 `:943`（变更声明）。

本次新增 `ChannelEndpointDetectService`（命令 / 结果 record、`supported` /
`not_found` / `auth_error` / `rate_limited` / `server_error` / `unreachable` /
`invalid_request` 七个 reason 常量、固定顺序的 `DetectableApiFormats` 与 20 秒
`ProbeTimeout`）、GraphQL `DetectChannelEndpointsInput` / `DetectedChannelEndpointType` /
`DetectChannelEndpointsPayload`、`AdminMutation.detectChannelEndpoints`，以及
`ChannelOutboundFactory.PrimaryApiFormat` / `CreateNonDefaultEndpointOutbound`。检测是只读
操作：按 `ID + 未软删` 读取通道（缺失 → `channel not found: ent: channel not found`，
存储失败 → `channel not found: <err>`），先用通道类型的主出站构建一次
（失败 → `failed to build channel <name>: <err>`），再解析探测模型（请求值 Trim 非空 →
`default_test_model` Trim → `supported_models` 首个 Trim 非空 → 空串），最后以
`Task.WhenAll` 并发探测 `openai/chat_completions`、`openai/responses`、
`anthropic/messages`，每个 probe 独立 20 秒超时、按索引写回结果，因此返回顺序与完成
顺序无关。探测请求恒携带 model（即使为空串）、`messages=[{role:user, content:ping}]`、
`max_completion_tokens=1`、`stream=false`；成功 → `supported` + 状态码，404/405 →
`not_found`，401/403 → `auth_error`，429 → `supported/rate_limited`，>=500 →
`server_error`，其余 4xx → `supported/supported`，非 HTTP 状态错误（传输失败 / 取消）→
`unreachable` + 0。主格式的 probe 复用主出站，其余格式经
`CreateNonDefaultEndpointOutbound` 按冻结的 `baseURL := c.BaseURL` 构建 generic 出站
（不做 Zenmux 默认替换），构建或请求变换失败记 `invalid_request`。`PrimaryApiFormat`
取各类型默认端点表的首项：仅 xai 存在可探测的非主默认端点（responses），
`opencode_go_anthropic` 没有默认端点（三个格式全走 generic）。同时修正工厂的三处保真
问题：非默认出站不再泄漏 Zenmux 默认基址、Command Code 对每个非默认出站校验 HTTPS、
chat 格式的 commandcode 通用 messages 走 Anthropic `PlatformCommandCode`（Bearer）而不是
OpenAI chat 出站。共享 pipeline 执行器替代按通道 proxy client、无端点记录、
零 key 不 panic（参照 `channel_llm.go:183` 会 panic）与非默认出站惰性构建登记为 C150。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| /admin/graphql 端到端：三格式顺序与 URL / 鉴权形状、9 组状态码分类、传输失败 unreachable/0、请求值 Trim、`default_test_model`、首个 `supported_models`、空 model 的 chat/anthropic `invalid_request` 与 responses 发送空串、缺失通道与存储失败前缀、主出站构建失败包装、`ollama_anthropic` Bearer 复用、`opencode_go_anthropic` 全 generic、Command Code generic messages 走 Bearer、typesafe 全 generic、xai 主 chat 与 generic responses、`zai_anthropic` v4 家族 chat URL、乱序完成仍按固定顺序、零 key 不发 panic 且三次请求、输入 / payload / statusCode 内省 | `tests/AxonHub.Api.Tests/AdminChannelEndpointDetectEndpointTests.cs` | 28 |

`AxonHub.Api.Tests` 由 1424 增至 1452（+28），`AxonHub.Persistence.Tests` 保持 130，
`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持 92；四个项目合计 4666 增至
4694，0 失败 0 跳过。

差异：新增 C150（detectChannelEndpoints 按冻结语义探测，共享执行器 / 无端点记录 / 零 key
不 panic）；更新 C014（端点自动检测已接入）。差异总数 149 增至 150（93 接受 / 23 缺口 /
34 已解决）。

## 73. 管理面 saveChannelModelPrices 定价写入切片（计划执行、版本表与校验，68 例，2026-11-02）

参照路线：`internal/server/gql/price.resolvers.go:17-24`（SaveChannelModelPrices
resolver，逐项解引用输入并原样转发错误）、`internal/server/biz/channel_price.go:374-508`
（重复 model_id 检查、Validate 包装、按通道读取既有价格、事务内执行计划、通道
`updated_at` 触碰、提交后刷新进程内价格缓存）、`:317-372`（`calculatePriceChanges`）、
`:214-260`（`createChannelModelPrice` / `createChannelModelPriceVersion`）、
`:534-544`（`generateReferenceID`）、`internal/objects/price.go`（`Validate` / `Equals`
家族）、`internal/server/gql/price.graphql:22-151`（定价输出 / 输入类型与变更声明）与
`internal/ent/schema/channel_model_price_versions.go`（active/archived 状态与
effective 起止列）。

本次新增 `ChannelModelPriceService`（`SaveAsync`：按输入顺序去重并校验，读取既有
价格，组装 create/update/skip/delete 计划，调用存储事务；`SerializePrice` /
`DeserializePrice` 经 `BillingJson` 与 shopspring 的带引号小数格式对齐；
`GenerateReferenceId` 取 8 个 ASCII 字母）、`IChannelModelPriceStore.ApplyAsync`
（单事务执行计划：create 写当前行与 active 版本；update 先归档 active 版本、再换
新 reference id 更新当前行并写新 active 版本；skip 原样返回既有行；delete 先归档
再软删当前行；最后强制触碰通道 `updated_at`，返回结果按计划序、删除项为 null），
GraphQL 定价输出 / 输入类型与 `PriceMapping`（GID、`channelID` / `modelID` /
`referenceID` 命名、GoTime 标量），以及 `AdminMutation.saveChannelModelPrices`
（`channelId: ID!` 解析 GID 后转发）。校验失败仍以 `invalid model price: model_id=<id>: <err>`
拒绝，重复输入为 `duplicate model price input: model_id=<id>`；既有价格读取失败为
`failed to query existing channel model prices: <err>`（含不可解码的已存 JSON）；
归档 / 创建 / 更新 / 软删与版本写入各自沿用参照前缀；缺失通道在提交前以
`ent: channel not found` 失败并回滚整个计划。价格相等时跳过且不换 reference id；
小数按值比较，未知定价模式只比较模式串；调度树、变体与条目顺序参与相等判定。
`DateRange` 用严格 `yyyy-MM-dd` 解析，`dailyTime` 保留参照的字节算术与 `%q` 文案，
`Local` 时区按 `time.LoadLocation` 的特例接受。落库 JSON 与参照一致：小数写成去掉
尾零的带引号字符串，字段名与顺序、`promptWriteCacheVariants`、`schedule` 树按 Go
结构序；读取时 jsonb 会重排空白与键序，测试按结构比较。缓存刷新在端口不存在（读取
直接走数据库），HotChocolate `Decimal` 标量的数值文本、`DateOnly` 拒绝公元 0 年与
软删谓词实现登记为 C151。自动定价任务与 `channelModelPrices` / versions 的读取边仍
属 C048 的剩余缺口；createChannel / updateChannel 仍不调用 `ensureChannelModelPrices`
（C143）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| /admin/graphql 端到端：create/update/skip/delete 计划顺序与结果顺序、空输入删除全部、重复 model_id、校验前缀、调度树校验、存储查询失败与不可解码价格、apply 失败透传、缺失通道、带 scale 与无 scale 小数相等、结构化差异触发 update 与新 reference id、Go 形状落库 JSON、返回价格树（末档 `upTo` 为 null）、畸形 GID、输入 / 输出 / 变更内省、服务层未知定价模式 | `tests/AxonHub.Api.Tests/AdminChannelModelPriceEndpointTests.cs` | 16 |
| Validate 文案与 Equals 语义：nil / 空条目、四种定价模式、tiers、变体、schedule 时区 / overrides / dailyTime / weekdays / dateRange 全部包装层、dailyTime 字节算术与 `%q`、DateRange 严格解析、nil 与空数组等价、小数按值、缺省 vs 零、条目顺序、未知模式仅比模式串 | `tests/AxonHub.Api.Tests/ModelPriceValidationTests.cs` | 45 |
| 持久层 ApplyAsync：create 写行与 active 版本并触碰通道、update 归档旧版本并新建 active 版本与 reference id、skip 不写、delete 软删并归档、混合计划按序写入并原序返回、缺失通道回滚全计划、缺失行的 update / delete 保留参照前缀 | `tests/AxonHub.Persistence.Tests/ChannelModelPriceStoreTests.cs`（7 个 Apply* 用例） | 7 |

`AxonHub.Api.Tests` 由 1452 增至 1513（+61：端点 16 + 校验 45），
`AxonHub.Persistence.Tests` 由 130 增至 137（+7），`AxonHub.Llm.Tests` 保持 3020，
`AxonHub.Grains.Tests` 保持 92；四个项目合计 4694 增至 4762，0 失败 0 跳过。

差异：新增 C151（定价写入的缓存刷新 / Decimal 线上文本 / DateOnly 年 0 / 软删实现）；
更新 C048（写入面已闭合，剩自动定价与读取边）、C143（写入接缝已具备，create / update
尚未接线）与 C014（定价写入已接入）。差异总数 150 增至 151（94 接受 / 23 缺口 /
34 已解决）。

## 74. 通道自动定价切片（models 表、模型卡换算与 create/update 事务接线，20 例，2026-11-02）

参照路线：`internal/server/biz/channel_price.go:45-93`（`uniqueNonEmptyModelIDs` 与
`modelCardToChannelModelPrice`）、`:95-140`（`createChannelModelPriceIfMissing` 的
`ON CONFLICT ... DO NOTHING`、候选 reference id 胜负判定与首个 active 版本）、
`:142-215`（`resolveChannelModelPriceTemplates` 的 500 一批模型库查询与
`applyChannelModelPriceTemplates` 的共用 `now`）、`:260-315`
（`ensureChannelModelPrices`：去空去重保序、事务内补齐缺失价格）、`:534-544`
（`generateReferenceID`，8 个 ASCII 字母）；`internal/server/biz/channel.go:633-671`
（CreateChannel 在 createChannel 后、提交前调用 ensure）、`:1072-1172`
（UpdateChannel 在 mut.Save 后仅当 `input.SupportedModels != nil` 调用 ensure，空列表
仍执行）；`internal/ent/schema/model.go` 与 `internal/ent/migrate/schema.go:327-361`
（`models` 表列、默认值与 `models_by_name` / `models_by_model_id` 唯一索引）。

本次新增 `models` 表（bigint 自增主键、软删 `deleted_at`、`type` 默认 `chat`、
`status` 默认 `disabled`、`model_card` / `settings` jsonb、`remark` 可空，两个
`(列, deleted_at)` 唯一索引）与 `ModelRecord` / EF 映射，SchemaTests 与
EfCoreModelTests 同步锁定表数 17 与索引数 42。`IChannelModelPriceStore` 新增
`EnsureAutoPricesAsync`：`ModelCardPriceJsonFactory` 与
`ChannelPriceReferenceIdFactory` 作为接缝注入，存储层不感知 Go 线格式；模型 ID
去空、去重且保留首次出现顺序，空集合直接返回 false 且不开事务；否则无环境事务时
自开并提交（对应参照 `RunInTransaction` 的嵌套复用），按 500 一批查询 live 价格与
live `enabled`/`disabled` 模型卡；卡换算按 `input`、`output`、`cacheRead`、
`cacheWrite` 顺序生成最多四个 `usage_per_unit` 项（`prompt_tokens` /
`completion_tokens` / `prompt_cached_tokens` / `prompt_write_cached_tokens`），
非正数、NaN 与 Inf 跳过，全部不可用则不建行；每个模板生成候选 reference id 后
`INSERT ... ON CONFLICT ("channel_id", "model_id", "deleted_at") DO NOTHING`，再读
live 行，仅当 reference id 等于候选项时写 active 版本（effective_start 为整批共用
的 `now`、effective_end 为 null），非赢家返回 false；错误前缀逐层保持
`failed to query existing channel model prices:` / `failed to query model prices
from model library:` / `failed to auto-configure channel model price: model_id=<id>:`
/ `failed to create channel model price:` / `failed to load channel model price
after upsert:` / `failed to create channel model price version:`，ensure 全程不触碰
通道行的 `updated_at`。

`ChannelWriteStore` 的 create / update 增加事务内回调：create 在 `SaveChanges` 后、
提交前运行 `afterInsert`，update 在 `ExecuteUpdate` 后、提交前运行 `afterUpdate`；
回调异常原样传播并回滚整个通道写入（对应参照在同一 `RunInTransaction` 内返回 ensure
错误）。`ChannelLifecycleService` 在 CreateChannel 无条件传入 ensure；UpdateChannel
仅在 `SupportedModels` 非 null 时传入（null = 不改列且不 ensure，空列表 = 仍执行）。
`ModelCardPricing` 把 `models.model_card` 解码为 `objects.ModelPrice`：JSON null /
`{}` / `cost` 缺失或全不可用返回 null；小数按 `double.ToString("R")` 的最短往返
文本再 `decimal.Parse`（对齐 shopspring `decimal.NewFromFloat`），价格经
`ChannelModelPriceService.SerializePrice` 写成去尾零的带引号字符串；自定义
`GoFloat64Converter` 让 1e309 与非数字类型按 Go `encoding/json` 语义报解码错误而
不是产生 Infinity。参照的 `channel_price_test.go` 只有 Save / Duplicate /
CalculatePriceChanges 三组测试，没有 `ensureChannelModelPrices` 单测，端口按源码
语义补行为测试。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 持久层 EnsureAutoPrices：按序补齐 enabled/disabled 且 live 的模型、archived/软删/无价/缺失/重复/空串排除、reference id 与 active 版本、既有价格与版本历史不变、空集合短路、加入调用方事务并在回滚后不留行、创建失败前缀（全局 reference id 冲突）与模型卡工厂失败按模型库查询前缀包装 | `tests/AxonHub.Persistence.Tests/ChannelModelPriceStoreTests.cs`（5 个 EnsureAutoPrices* 用例） | 5 |
| 持久层 create / update 事务回调：回调在事务内看到写入、回调失败回滚通道行、成功时一起提交、update 回调在保存后运行 | `tests/AxonHub.Persistence.Tests/ChannelWriteStoreTests.cs`（4 个 *After*Hook* 用例） | 4 |
| 模型卡换算与 Go 线格式：四项顺序与 item code、非正 / NaN / Inf 跳过、null 卡与 null cost、大小写不敏感与未知字段、落库 JSON 精确文本、最短往返小数 0.30000000000000004、1e309 与字符串 cost 的解码错误 | `tests/AxonHub.Api.Tests/ModelCardPricingTests.cs` | 7 |
| createChannel / updateChannel 接线：创建时按 supportedModels 调用 ensure 并传入新通道 id 与顺序、定价失败原样返回且回滚创建；更新时仅 `supportedModels` 出现才调用（空列表仍调用）、定价失败文案透传 | `tests/AxonHub.Api.Tests/AdminChannelCreateEndpointTests.cs`（2）、`AdminChannelUpdateEndpointTests.cs`（2） | 4 |

`AxonHub.Api.Tests` 由 1513 增至 1524（+11：模型卡 7 + create 2 + update 2），
`AxonHub.Persistence.Tests` 由 137 增至 146（+9：ensure 5 + 事务回调 4），
`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持 92；四个项目合计 4762
增至 4782，0 失败 0 跳过。

差异：C143 已解决（create / update 在事务内自动补价并整体回滚）；更新 C048（自动
定价已闭合，剩 GraphQL 读取边与进程内缓存）、C088（`models` 表已建并可被自动定价
读取，管理面 CRUD / include / profile 仍缺）与覆盖缺口表；新增 C152（模型卡解码的
错误文案、非有限值与 decimal 精度边界）。差异总数 151 增至 152（95 接受 / 22 缺口 /
35 已解决）。

## 75. 管理面 channels 重复 / 批量创建 / 批量导入切片（18 + 18 例，2026-11-02）

参照路线：`internal/server/gql/axonhub.resolvers.go:179-182`（duplicateChannel）、
`:184-186`（bulkCreateChannels）、`:375-388`（bulkImportChannels）；
`internal/server/biz/channel_duplicate.go:17-84`（源加载前缀、端点与 zenmux 管理
key 继承先于重名检查、createChannel 后在同一事务内逐行复制价格并 ensure、提交后
`reloadChannelsAfterCommit`）；`internal/server/biz/channel_bulk.go:67-161`（事务前
校验顺序、live 名称快照、编号命名与共享计数器、tags 回退、事务内一次
resolveChannelModelPriceTemplates、逐通道 create / ensure 的错误前缀）与
`:484-568`（逐行校验、raw ent builder、每行独立事务与 ensure、失败行追加
`Row %d (%s): %s` 后继续）；`internal/ent/schema/channel.go` 的列默认值与
`channels_by_name` 唯一索引（软删参与）。

本次新增 `IChannelWriteStore` 的四个写入面：

- `ListLiveNamesAsync`：只取 live 行的名称集合，失败前缀
  `failed to query existing channels:`（bulk create 的事务前快照）。
- `DuplicateAsync(sourceId, name, inherit, buildRecord, afterInsert)`：单事务；
  读取 live 源行（缺失为 `failed to get source channel: ent: channel not found`），
  先运行 `inherit`（输入端点为 null 时继承源端点；双方均为 zenmux 且输入
  managementApiKey 为空时继承源 key），再做 live 重名查询（命中返回 null，由服务层
  抛 `DUPLICATE_NAME`，因此重名判定先于 createChannel 的字段校验），随后才
  `buildRecord`；插入失败包 `failed to create channel: <驱动文本>`；`afterInsert`
  在提交前运行价格复制与 ensure，异常使通道与价格一起回滚。
- `CreateManyAsync(factories, beforeInsert, afterEachInsert)`：单事务；
  `beforeInsert` 在第一个插入前运行一次（resolve 模板）；逐工厂构建并插入，插入
  失败包 `failed to create channel '<name>': failed to create channel: <驱动文本>`，
  工厂校验失败原样上抛；`afterEachInsert` 在提交前逐通道运行（apply 模板），任一
  失败回滚整批。
- `ImportCreateAsync(record, afterInsert)`：每行自开事务；不做应用层重名预检（与
  参照一样由唯一索引裁决）；插入失败直接暴露驱动文本；`afterInsert` 运行 ensure，
  失败回滚该行并保留 ensure 的完整前缀。

`ChannelLifecycleService` 三个方法逐条复刻参照：

- `DuplicateChannelAsync`：存储层重名返回 null 后抛 `CodedException`（code
  `DUPLICATE_NAME`、resource channel、field name），文案
  `channel name '<name>' already exists`。
- `BulkCreateChannelsAsync`：事务前顺序固定为 `no API keys provided` →
  `base URL is required` → 类型校验
  `invalid channel type '<type>': channel: invalid enum value for type field: "<type>"`
  → live 名称快照；名称格式 `<name> - (<counter>)` 且跳过快照中已占用的编号，两个
  新名称共享同一计数器；空 tags 回退 `[name]`；每个通道的凭据为
  `{"apiKeys":[<key>]}`；所有通道共享同一 policies / settings 实例；事务内先
  resolve 一次模板、再逐通道创建并 apply，错误前缀分别为
  `failed to create channel '<name>':` 与
  `failed to auto-configure prices for channel '<name>':`。
- `BulkImportChannelsAsync`：逐行校验顺序为类型（`Row N: Invalid channel type
  '<type>'`）、base URL（`Row N (<name>): Base URL is required`）、API key
  （`Row N (<name>): API Key is required`）；`ToImportRecord` 复刻 raw ent
  builder（status disabled、空列表列、policies `{"stream":"unlimited"}`、settings
  `{"modelMappings":[]}`、credentials `{"apiKey":"<key>"}`、orderingWeight 0）；
  每行事务内插入并 ensure；失败追加 `Row N (<name>): <错误文本>` 后继续；
  `Success = failed == 0`，无失败时 `Errors` 为 null、`Channels` 为成功行列表。

GraphQL 接线：`duplicateChannel(sourceID: ID!, input: CreateChannelInput!)`、
`bulkCreateChannels(input: BulkCreateChannelsInput!)`（返回 `[Channel!]!`）、
`bulkImportChannels(input: BulkImportChannelsInput!)`（返回
`BulkImportChannelsResult!`）。HotChocolate 的输入对象命名约定会在 CLR 名后追加
`Input`，因此 `BulkImportChannelItemInput` 通过显式 `InputObjectType` 注册为 SDL 名
`BulkImportChannelItem`（与 `ChannelOrderingItem` 同一处理）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| GraphQL 三个变更：zenmux 端点 / 管理 key 继承与顺序、调用方覆盖、重名先于字段校验、自由名后校验、源缺失、复制失败整体回滚、编号跳过 live 名与 tags 回退、模板只 resolve 一次、共享 tags / settings 与 orderingWeight 0、空 key / 空 URL、未知类型、校验包装、apply 失败回滚、导入行序 / 默认值 / 失败后继续 / ensure 失败、全成功 `errors=null`、schema 顺序与类型名 | `tests/AxonHub.Api.Tests/AdminChannelDuplicateBulkEndpointTests.cs` | 18 |
| 持久层写入面：live 名称、duplicate 继承先于重名检查、缺失 / 软删源前缀、通道与价格同事务提交、复制失败回滚、插入失败前缀、批量 resolve 一次与逐通道回调、批量失败回滚、工厂校验原样传播、import 跳过预检并保留驱动文本、ensure 失败回滚该行 | `tests/AxonHub.Persistence.Tests/ChannelWriteStoreTests.cs`（11 个 ListLiveNames / Duplicate* / CreateMany* / ImportCreate* 用例） | 11 |
| 持久层价格复制与自动定价：Resolve 过滤与顺序、模板卡失败前缀、Apply 新建行与版本共用 `now`、既有行与版本不动、apply 失败回滚并入调用方事务、Copy 复制 live 行并生成新 reference、copy 失败回滚 | `tests/AxonHub.Persistence.Tests/ChannelModelPriceStoreTests.cs`（7 个 Resolve* / Apply* / Copy* 用例） | 7 |

`AxonHub.Api.Tests` 由 1524 增至 1542（+18），`AxonHub.Persistence.Tests` 由 146
增至 164（+11 写入面 + 7 价格），`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests`
保持 92；四个项目合计 4782 增至 4818，0 失败 0 跳过。

差异：新增 C153（插入失败时 Npgsql / EF 的驱动错误文本无法复刻 ent / pgx 文本，
外层前缀与行号形状保持一致）；更新 C014（重复 / 批量创建 / 批量导入已接入，剩
测试通道与模型同步）与覆盖缺口表。

## 76. 管理面 channels 通道测试切片（testChannel / testChannelAPIKeys / testChannelAPIKey，75 例，2026-11-03）

参照路线：`internal/server/gql/axonhub.resolvers.go:305-372`（三个 resolver、
`contexts.WithSource(ctx, request.SourceTest)` 与 404 专用文案）；
`internal/server/orchestrator/tester.go:51-71`（tester 的 orchestrator 构造）、
`:80-201`（测试请求构建、`channelTestAPIFormat`、`channelTestInbound`、
`parseDecisionsTestResponse`、`usesResponsesWebSocket`）、`:203-269`
（`buildChannelTestInput`）、`:271-378`（TestChannel）、`:485-577`
（TestChannelAPIKeys）、`:581-628`（TestSingleAPIKey）、`:751-757`
（byte 级 maskAPIKey）；`internal/server/biz/system.go:1317-1352`
（system_channel_settings 读取与默认提示词）；
`internal/llm/pipeline/stream/usage.go:12-24`（EnsureUsage）；
`internal/server/orchestrator/select_endpoints.go` 与 `channel_endpoint.go:448-463`
（端点解析与 modelProtocols 过滤）；`channel_llm.go:169-184`（apiKeyOverride）。

`ChannelTestService`（scoped）按参照的三条入口复刻：

- `TestChannelAsync`：加载通道 → 模型取 `modelId`，为空回退
  `DefaultTestModel` → 读全局提示词 → 由端点解析测试 API 格式（Decisions /
  TypeSafe SystemOne 优先，其余 chat）→ 按格式构建请求 → 通过
  `ChatCompletionOrchestrator.ProcessSpecifiedAsync` 走同一管线；管线异常经
  `inbound.TransformError` 后只取 `error.message`，取消统一为
  `context canceled`（C155）。非流式响应按格式解码：SystemOne 把 answers 以
  GoJson 重新序列化，Decisions 返回原始 body（缺 answers 报错），其余取
  `choices[0].message.content`；流式累加 `delta.content`，跳过 `[DONE]`，
  无内容报 `No content in stream response`，终止错误取 `stream.Err.Message`。
- `TestChannelAPIKeysAsync`：读取全部凭据 key 与原始禁用记录，`SemaphoreSlim`
  上限 8 并发，`Task.Run` 让每个 key 独立执行，结果按 key 顺序写回数组；
  每个失败不外抛，`SuccessCount` / `FailedCount` 汇总，`Disabled` 取原始
  禁用集合（不做过期过滤）。调用方 token 取消时每个排队 key 产出
  `context canceled` 失败（C155）。
- `TestSingleApiKeyAsync`：key 必须属于通道（`the provided API key is not
  configured for this channel`），否则走同样的单 key 测试。

请求构建逐字复刻 `buildChannelTestRequest`：chat 格式为 system + user 两条
消息、`max_completion_tokens = 256`、`stream` 取
`policies.stream == "require"`；SystemOne 把受保护的 system / user 以 `\n\n`
拼成 `state`，`questions.connection = {type: "noul", instructions: "Does the
state contain a test prompt?"}`，body 只有 state / questions / model；Decisions
body 为排序键的 model / input / questions（name `answer`、type `predicate`、
predicate `Is this a channel connectivity test?`）；Responses WebSocket 通道
改为单条 user 提示词 `ping`、去掉 `max_completion_tokens` 并强制
`stream = true`；SystemOne 不支持流式
（`systemone does not support streaming`）。提示词保护在拼接 state 之前运行
（mask / reject 与全局管线一致）。

支撑接缝：`ChatCompletionOrchestrator.ProcessSpecifiedAsync` 复用 scoped
orchestrator，挂载 `EnsureUsageMiddleware`（`stream.EnsureUsage`），跳过测试
链路本就没有的速率限制准入 / 记账，并把候选工厂的异常交给调用方走同一
`TransformError`；候选工厂每次尝试重新读通道（`SpecifiedChannelSelector`），
支持 `ApiKeyOverride`。`ChannelCandidate` 新增 `ApiFormat` / `ApiKeyOverride`，
`ChannelApiKeyProvider.ForOverride` 复刻参照的 forced key 分支，
`ChannelOutboundFactory` 按候选的 API 格式分派并允许 override key 在无启用 key
时仍发送认证。端点解析与 modelProtocols 过滤从 GraphQL 映射层移到
`ChannelEndpoints.Resolve` / `ChannelEndpointSelection`
（`ChannelModelEntries.BuildDirect` / `ForcedApiFormats`），GraphQL 映射与测试器
共用。`RequestIdentity` 新增 `Source`，测试请求以 `"test"` 写入
`requests.source` / `usage_logs.source`，其余路径保持 `"api"`。

`SystemChannelSettingsService` 读取 `system_channel_settings`：缺行、查询失败、
JSON null 或非对象 / 非法 JSON 回退默认提示词；键大小写不敏感、重复键后者胜；
任一提示词非字符串使整次解码回退默认；空白字符串按字段回退默认，其余首尾空白
保留。

GraphQL 接线：`testChannel(input: TestChannelInput!)` 返回
`TestChannelPayload!`（latency Float! / success Boolean! / message / error），
`testChannelAPIKeys(channelID: ID!, modelID: String)` 返回
`TestChannelAPIKeysPayload!`（channelID ID! / total / successCount /
failedCount / results [TestAPIKeyResult!]!），
`testChannelAPIKey(channelID: ID!, key: String!, modelID: String)` 返回
`TestAPIKeyResult!`（keyPrefix String! / success / latency / error / disabled）。
`TestChannelInput.proxy` 保留在 SDL 中但不生效（C154）；错误前缀分别为
`failed to test channel:`、`failed to test channel API keys:`、
`failed to test channel API key:`，`HttpClientError` 404 单独映射为
`URL not found, please check if the URL is correct and try again`。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 测试编排器：OpenAI / Anthropic / Gemini / SystemOne / Decisions 请求形状、模型覆盖、默认提示词、流式累加与空流 / 终止错误、上游错误文案、不支持模型、SystemOne 失败矩阵、批量成功 / 部分失败 / 禁用标记 / 取消、单 key 所有权、缺通道 / 存储失败、提示词 mask / reject、六个 API 格式场景、byte 级 key 掩码与内部构建 / 解析辅助 | `tests/AxonHub.Api.Tests/ChannelTestServiceTests.cs` | 49 |
| GraphQL 三个变更与 SDL：成功载荷、Authorization / 提示词线上形状、模型覆盖、404 映射、错误包装、批量 gid / 计数 / 前缀 / 禁用标记 / 调用次数、单 key、非本通道 key、字段顺序与可空性 | `tests/AxonHub.Api.Tests/AdminChannelTestEndpointTests.cs` | 9 |
| ChannelSetting 提示词读取：缺行 / 查询失败 / 软删、大小写与重复键、部分配置、空白回退、首尾空白保留、JSON null / 非法 / 非对象、非字符串整次回退 | `tests/AxonHub.Api.Tests/SystemChannelSettingsServiceTests.cs` | 17 |

`AxonHub.Api.Tests` 由 1542 增至 1617（+75），`AxonHub.Persistence.Tests` 保持
164，`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持 92；四个项目合计
4818 增至 4893，0 失败 0 跳过。

差异：新增 C154（`TestChannelInput.proxy` 保留但不生效）、C155（取消文案由端口
合成）、C156（缺 `choice.message` 时参照 panic、端口返回空消息）、C157（测试器
对 policies / disabled_api_keys 原始列宽松读取）；更新 C014（通道测试已接入，
剩模型同步）与覆盖缺口表。

## 77. 管理面 channels 模型同步切片（syncChannelModels / ModelFetcher，68 例，2026-11-03）

`ModelFetcher` 按 `model_fetcher.go` / `model_fetcher_antigravity.go` 逐分支移植：
默认模型表（`GetDefaultModelsByType`，claudecode / codex / xai_subscription 仅官方
来源）、antigravity 前置分支（JSON / `refresh|project` / 已保存 OAuth 复用、刷新
回写 `refreshToken|projectID` 或 OAuth JSON、目录为空或失败时不返回静态模型）、
cline 推荐目录（`recommended` / `free` / `clinePass` 合并去重、无可用 clinePass
时追加 11 个静态模型并置 Fallback）、apiKey 覆盖、ChannelId 快照（端点 / 类型
变更时拒绝存储凭据：`API key is required when channel type or base URL is
changed`）、空 key（qiniu `deepseek-v3`，官方类型默认模型，否则
`API key is required`）、OAuth JSON（codex 合成通道或默认模型）、类型白名单
（`invalid channel type: channel: invalid enum value for type field: %q`）、
commandcode HTTPS 校验、`PrepareModelsEndpoint` 的 46 行 URL 表与
Anthropic-Version 头、认证头选择（commandcode / anthropic / gemini / Bearer）、
header override（`headerOverrideOperations` 的原始存在性优先于
`overrideHeaders`）、gemini 分页（pageSize=1000、pageToken 去环、50 页上限、
`models/` 前缀裁剪）、anthropic Bearer 单次重试、`FetchModelsViaRequest`
（`unexpected status: <code> <reason>`、`failed to parse models response:`、首次
出现去重）、commandcode 过滤、qiniu / codex 回退。

provider-conf 缓存固定 1 小时（不可配置），双重检查锁；成功且非空时缓存副本，
失败时回退旧副本，成功但为空不缓存；copilot 与 gemini_vertex 共用。

`ChannelModelSyncService` 复刻 `channel_model_sync.go` / `channel_internal.go`：每个
通道新建 fetcher；fetch error → `model fetch returned error: …`，fallback →
`model fetch returned fallback models`；模式覆盖优先于 `AutoSyncModelPattern`，非法
正则跳过过滤（`XRegexp.ValidateRegex` 为 null 表示可编译），过滤按
`^(?:…)$` 全串锚定；事务内重读通道、`Uniq(manual + fetched)`、空合并不写；
`modelsChanged` 为集合差；仅 modelsChanged 时裁剪 `modelProtocols` 并按需写
settings；价格补齐在同一事务内；`changed = models | protocols | prices`；单通道
changed 或整批 changed 时调用一次 reload hook。`SyncAllAsync` 吞掉列表查询错误
并计数成功 / 失败 / changed；`ShouldRunModelSync` 按 UTC 对齐窗口（60 / 360 /
1440 分钟）跳过同一窗口。

GraphQL：`syncChannelModels(channelID: ID!, pattern: String)` 返回
`SyncChannelModelsPayload!`（channelID 回显调用方 GUID 的类型段、supportedModels、
manualModels 的 null 归一为空列表）；resolver 包装业务错误。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 模型发现：默认表、46 行端点表、Gemini 分页、Anthropic 重试、header override、Command Code、Cline 推荐 / 静态回退、provider-conf 缓存、凭据分支与错误前缀 | `tests/AxonHub.Api.Tests/ModelFetcherTests.cs` | 25 |
| Codex 目录、存储凭据替换与 relay | `tests/AxonHub.Api.Tests/ModelFetcherCodexTests.cs` | 10 |
| Antigravity 账户目录、OAuth / 刷新持久化、失败矩阵与不一致凭据 | `tests/AxonHub.Api.Tests/ModelFetcherAntigravityTests.cs` | 14 |
| 同步服务：合并、价格补齐、reload、回退、顺序稳定、协议裁剪、周期批、并发 manual、合并表与调度窗口 | `tests/AxonHub.Api.Tests/ChannelModelSyncTests.cs` | 14 |
| GraphQL 契约：载荷形状、gid 回显、模式覆盖、null 归一与错误透传 | `tests/AxonHub.Api.Tests/AdminChannelModelSyncEndpointTests.cs` | 5 |

`AxonHub.Api.Tests` 由 1617 增至 1685（+68），`AxonHub.Persistence.Tests` 保持
164，`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持 92；四个项目合计
4893 增至 4961，0 失败 0 跳过。

差异：新增 C158（模型同步切片的已知裁剪：通道缓存刷新 no-op、cron 调度未接入、
按通道代理与 live-only 分支未移植）、C159（模型拉取的传输 / URL 错误文本来自
.NET HTTP 栈）、C160（畸形 manual_models / supported_models / settings 的报错
文本）；更新 C014（模型同步已接入）与覆盖缺口表。

## 78. 管理面通道覆盖模板切片（channelOverrideTemplates + create/update/delete/apply/clear，65 例，2026-11-03）

`channel_override_templates` 表按 `internal/ent/schema/channel_override_template.go`
落地：`id bigint`、`created_at / updated_at / deleted_at timestamptz`、
`name / description / override_parameters / override_headers varchar`、
`header_override_operations / body_override_operations jsonb`、`user_id bigint`；
删除为软删，唯一索引 `channel_override_templates_by_user_name` 覆盖
`(user_id, name, deleted_at)`。EF Core 写侧模型与幂等 DDL 同步扩展，表 / 索引
断言原地更新为 18 表 / 43 索引。

`ChannelOverrideTemplateService` 按 `channel_override_template.go` 逐分支移植。
创建先校验 header 再校验 body 操作（前缀 `invalid header override operations: ` /
`invalid body override operations: `），两个操作列总是写入（缺省输入存 JSON `null`，
不走 schema 默认值），唯一冲突映射为 `xerrors.DuplicateNameError("Template", name)`。
更新按 SetHeader → AppendHeader → SetBody → AppendBody 顺序校验（append 前缀
`invalid ... to append: `）；`clearDescription` 优先，set 与 append 同时出现时
append 胜出，空 append 不写列，仅当 Name 非空时才映射唯一冲突，缺行返回
`failed to update channel override template: ent: channel_override_template not found`。
删除为软删，缺行同前缀 not found。Get / 列表带
`failed to get channel override template: `。Apply 先取模板
（`failed to fetch template: `，JSON 列在取模板时解析，畸形值先于通道查询失败），
再查通道（`failed to query channels: `），数量不一致返回
`some channels not found for provided IDs`；Merge 按 header / body 合并、Replace 直接
覆盖，写入时清空 `overrideHeaders` / `overrideParameters`，单事务提交后调用
`reloadChannelsAfterCommit`；Clear 同链路但跳过模板。权限沿用 ent privacy：owner
绕过所有权，非 owner 限定 `user_id = 当前用户`，缺用户拒绝（C161）。Apply/Clear 的
通道查询按 id 排序以固定响应顺序，重复 id 在 `IDIn` 下折叠后由数量校验拒绝（C163）。

GraphQL 契约：`channelOverrideTemplates(first, last, after, before, orderBy, where)`
返回 entgql 连接（`edges { node cursor }`、`pageInfo`、`totalCount`），where 为参照
投影的子集（C162）；`createChannelOverrideTemplate` /
`updateChannelOverrideTemplate` / `deleteChannelOverrideTemplate` /
`applyChannelOverrideTemplate` / `clearChannelOverrideTemplates` 五个变更返回模板类型或
`ApplyChannelOverrideTemplatePayload`（channels / success / updated）。模板类型在
`headerOverrideOperations` / `bodyOverrideOperations` 非空时直接渲染新列，否则把
`overrideHeaders` / `overrideParameters` 转换成操作列表；草稿输入始终回显
`overrideHeaders` / `overrideParameters`，新列只在非 null 时出现。gid / Time /
Cursor 标量与既有管理面一致，重复名错误携带
`DUPLICATE_NAME` / `Template` / `name` / 值扩展。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 模板存储：创建 JSON 精确值、更新 set / clear / append 优先级、按用户唯一冲突与软删、软删隐藏、列表作用域 / 过滤 / 键集分页、通道查询忽略缺失 / 删除行、批量设置写入提交与回滚 | `tests/AxonHub.Persistence.Tests/ChannelOverrideTemplateStoreTests.cs` | 7 |
| 模板服务：创建 / 更新 / 删除 / Get / 列表 / Apply / Clear 的全部前缀与时序、操作校验顺序、append 语义、唯一冲突、缺行、隐私作用域、JSON 列解析失败、Merge / Replace 写入形状、空批次与 reload | `tests/AxonHub.Api.Tests/ChannelOverrideTemplateServiceTests.cs` | 42 |
| GraphQL 契约：查询字段与 gid / Time / Cursor 形状、排序 / 过滤 / 分页、二选一校验、隐私拒绝、SDL 输入子集与五个变更、创建 / 更新 / 删除 / Apply / Clear 线上形状与错误扩展 | `tests/AxonHub.Api.Tests/AdminChannelOverrideTemplateEndpointTests.cs` | 16 |

`AxonHub.Api.Tests` 由 1685 增至 1743（+58），`AxonHub.Persistence.Tests` 由 164
增至 171（+7），`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持 92；
四个项目合计 4961 增至 5026，0 失败 0 跳过。

差异：新增 C161（管理面身份取用与 owner 默认）、C162（where 输入为参照投影子集）、
C163（Apply/Clear 通道顺序确定化）；更新 C158（Apply/Clear 同样调用
reloadChannelsAfterCommit，端口为 no-op）与覆盖缺口表。

## 79. 管理面自助身份切片（me / myProjects / updateMe / updateMyPassword / unlinkOIDCIdentity，41 例，2026-11-03）

`oidc_identities` 表按 `internal/ent/schema/oidc_identity.go` 落地：`id bigint`、
`created_at / updated_at timestamptz`、`deleted_at bigint`、`issuer / subject varchar`、
`email / idp_name varchar NULL`、`last_login_at timestamptz NULL`、`user_id bigint`；
删除为软删，唯一索引 `oidc_identities_by_issuer_subject_deleted_at` 覆盖
`(issuer, subject, deleted_at)`，另有 `oidc_identities_by_user_id`。EF Core 写侧模型与
幂等 DDL 同步扩展，表 / 索引断言原地更新为 19 表 / 45 索引。

`UserProfileService` 按 `me.resolvers.go` 与 `user.go` 的自助半程逐分支移植。五个入口
都先经 IManagementIdentity 取当前用户，缺用户返回 `user not found in context`（C161）。
`me` 经 GetUserByID 一并读取用户、角色、user_projects 与 oidc_identities，任何失败都带
`failed to get user details: failed to get user: ` 两级前缀，缺行返回
`...: ent: user not found`；ConvertUserToUserInfo 以 `project_id` 为 null 或 0 判定
系统角色（不看 level 标签），UserInfo.scopes 为用户自身 scopes 与系统角色 scopes 的并集，
项目角色按 project_id 分组挂到对应 UserProjectInfo，effectiveScopes 为成员 scopes 与该
项目角色 scopes 的并集，`avatar` 来自用户字符串地址因此永不返回 null，`hasPassword`
由密码是否等于 `!OIDC_SSO_ONLY!` 派生。`myProjects` 查询 `HasUsersWith(user)` 且
`status = active`，忽略归档、软删与非成员项目。`updateMe` 走 UpdateOwnProfile：
firstName / lastName / preferLanguage / avatar 仅在提供时写入（空 avatar 写入空串，
缺少 avatar 不清空），失败带 `failed to update user profile: `。`updateMyPassword`
对 OIDC-only 账号跳过旧密码校验，其余账号缺旧密码返回 `current password is required`，
任何 VerifyPassword 失败折叠为 `incorrect old password`；HashPassword 的失败不包装，
写入失败带 `failed to update user profile: `。`unlinkOIDCIdentity` 依次执行取行
（`failed to get identity: `）、所有权检查（`permission denied: this identity does not
belong to you`）、OIDC-only 最后身份保护
（`please set a local password before unlinking your last OIDC identity`，计数失败带
`failed to count identities: `）、软删（`failed to unlink identity: `）。

GraphQL 契约按 me.graphql 接入：`UserInfo` / `RoleInfo` / `UserProjectInfo` /
`OIDCIdentityInfo` 与 `Project`（仅标量，边与 profiles 为 C165）、`ProjectStatus` 枚举、
`UpdateMeInput` / `UpdateMyPasswordInput`，查询 `me`、`myProjects` 与三个变更；
id 一律为 `gid://axonhub/{Type}/{Id}`，时间走 Time 标量。参照的无序集合
（`lo.Keys` 的 scope 映射、eager-load 边、myProjects）在端口按确定性顺序返回（C164）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 自助存储：角色 join 的软删过滤与角色 id 顺序、user_projects / OIDC 身份按 id 顺序、myProjects 的成员 / 状态 / 软删过滤、UpdateOwnProfile 仅写提供字段与 updated_at、OIDC 取行 / 计数 / 软删、`(issuer, subject, deleted_at)` 唯一性 | `tests/AxonHub.Persistence.Tests/UserProfileStoreTests.cs` | 6 |
| 自助服务：UserInfo 投影（系统角色判定、scope 并集、项目角色分组、effectiveScopes、avatar 与 hasPassword）、五个入口的缺用户 / 前缀 / 缺行、updateMe 字段直传、updateMyPassword 的 OIDC-only / 旧密码缺失 / 校验失败折叠 / 哈希失败不包装、unlink 的完整守卫链 | `tests/AxonHub.Api.Tests/UserProfileServiceTests.cs` | 26 |
| GraphQL 契约：me 的完整线形与 gid / Time、空 avatar 为空串、myProjects 的 Project 标量与状态枚举、updateMe 的 User 载荷、updateMyPassword、unlinkOIDCIdentity 的 gid 解析与错误文案 | `tests/AxonHub.Api.Tests/AdminMeEndpointTests.cs` | 9 |

`AxonHub.Api.Tests` 由 1743 增至 1778（+35），`AxonHub.Persistence.Tests` 由 171
增至 177（+6），`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持 92；
四个项目合计 5026 增至 5067，0 失败 0 跳过。

差异：新增 C164（未定义顺序集合的确定性排序）与 C165（Project 仅标量）；C161 补记
自助入口同样经 IManagementIdentity 取用户。

## 80. 管理面登录切片（signin / refresh 与 JWT 会话，65 例，2026-11-03）

参照 `routes.go:97-98` 的两条未鉴权路由 `POST /admin/auth/signin` 与
`POST /admin/auth/refresh`、`api/auth.go` 的 AuthHandlers 与 `biz/auth.go` 的
用户 / JWT 方法。`JwtTokenCodec` 手写 golang-jwt v5 的可观察子集：HS256 header
字节 `{"alg":"HS256","typ":"JWT"}`、MapClaims 字母序 payload
（`auth_time,exp,iat,user_id`）、base64url 无填充、签名校验、exp / nbf 校验
（iat 不校验）与任意 HMAC 方法可接受；`auth_time` / `exp` 按 `jwtTimeClaim`
语义读取（数值、整秒截断、`auth_time` 需大于 0 否则视为缺失）。

`ManagementAuthService` 逐分支移植。`GenerateJWTTokenAt`：缺密钥返回
`system not initialized: secret key not found`，读库失败叠加两层
`failed to get secret key: ` 前缀；到期取 `min(now + 30d, auth_time + 90d)`。
`AuthenticateUser`：按 `email = ? AND status = activated` 查询并只 eager-load
roles；缺行返回 `invalid email or password: invalid password`，密码占位符或
登录到 OIDC_LOGIN_ONLY 提供方的用户返回
`OIDC user without password, please login via OIDC or set a password`，校验失败
返回 `invalid email or password invalid password`（参照此处缺冒号），其余查询
错误返回 `server internal error, please try again later`；身份查询失败降级为
不限制。`AuthenticateJWTToken`：`auth_time` 超过 90 天返回
`maximum session age exceeded`，`user_id` 非数值返回 `invalid token claims`，
取用户失败带 `invalid jwt token: failed to get user: `（缺行追加
`ent: user not found`），非 activated 返回 `user not activated`。
`RefreshJWTToken`：`exp` 剩余不少于 7 天不续期，缺 `auth_time` 不续期，
达到 90 天上限返回 `maximum session age exceeded`，`now + 30d` 与 `exp` 都
不早于上限时不续期；取用户仅 not-found 归入 InvalidJWT，其他读库错误保持
`failed to get user: ` 且不包装为令牌错误。

HTTP 面：signin 反序列化后按 gin 风格校验（email 必填且形如邮箱、password
必填），失败 400 `Invalid request format`；凭据错误 401 `Invalid email or
password`；OIDC-only 与其余异常 500 `Internal server error`；成功返回
`{user, token}`，user 为 objects.UserInfo 形状（id 为 `{"type":"User","id":N}`
对象、roles 仅 name、因只 eager-load roles 而 projects / oidcIdentities 为空
数组、avatar 空串、hasPassword）。refresh 只接受大小写敏感的 `Bearer ` 前缀，
401 `Invalid authorization token`，未续期 204，续期 200 `{"token":"..."}`，
其余 500。`/admin/graphql` 仍匿名可访问（C014）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| JWT 编解码：header / payload 字节与键序、HMAC 方法接受、篡改 / 过期 / nbf / 非 HMAC / 畸形段 / 非数值 exp、非正值与小数 auth_time、确定性 | `tests/AxonHub.Api.Tests/JwtTokenCodecTests.cs` | 14 |
| 认证服务：30/90/7 天窗口、密钥错误两级前缀、认证与 OIDC-only 分支、JWT 与刷新的全部错误分类、登录载荷仅带角色边 | `tests/AxonHub.Api.Tests/ManagementAuthServiceTests.cs` | 34 |
| 端点：登录线形与 GUID / 空 avatar、binding 400、凭据 401、OIDC-only 与存储失败 500、刷新的 401/204/200/500 | `tests/AxonHub.Api.Tests/AdminAuthEndpointTests.cs` | 17 |
| 登录查询：`email + status` 精确匹配活行、停用与软删行、大小写敏感 | `tests/AxonHub.Persistence.Tests/UserStoreTests.cs` | 1 |

`AxonHub.Api.Tests` 由 1778 增至 1843（+65），`AxonHub.Persistence.Tests` 由 177
增至 178（+1），`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持 92；
四个项目合计 5067 增至 5133，0 失败 0 跳过。

差异：新增 C166（手写 JWT 的内部文案与邮箱正则近似）；更新 C014（登录 / 刷新
已接入；OIDC 提供方配置、管理员 GraphQL 鉴权与权限校验仍缺）。

## 81. 管理面 GraphQL 鉴权切片（WithAdminGraphqlAuth / WithProjectID / apiKeyReadOnly，27 例，2026-11-03）

参照 `routes.go:161-168`：`adminGraphqlGroup` 先挂 `WithAdminGraphqlAuth`
（`middleware/auth.go:179-260`）再挂 `WithProjectID`
（`middleware/project.go:14-40`），且只注册 `POST /admin/graphql`；gqlgen 的
`apiKeyReadOnly` 作为 AroundOperations 钩子（`gql/graphql.go:187-204`）在解析与
校验之后拒绝 service_account 的非查询操作。端口新增
`AdminGraphqlAuthMiddleware`、`ProjectIdMiddleware` 与 `ApiKeyReadOnlyMiddleware`，
在 `Program.cs` 中以“路径 + POST”的 `UseWhen` 分支挂在
`MapGraphQL("/admin/graphql")` 之前；`MapGraphQL` 追加 POST-only 元数据，GET
因此不被端点匹配，落回静态 fallback 的 API 404 信封（与参照只注册 POST 路由
一致）。

鉴权门逐分支移植：

- header 提取等价于 `ExtractAPIKeyFromRequest`（Headers 仅 Authorization、
  RequireBearer）：无 Authorization 或空值 → 401 `API key is required`；不以
  大小写敏感的 `Bearer ` 开头 → 401
  `invalid token: Authorization header must start with 'Bearer '`；前缀后
  `TrimSpace` 为空 → 401 `invalid token: API key is required`；否则取首行并
  `TrimSpace` 后作为令牌。
- JWT 优先：`ManagementAuthService.AuthenticateJwtTokenAsync` 成功 → 身份为
  `AdminGraphqlIdentity(User, IsApiKey: false)` 并放行。只有
  `InvalidJwtException` 落入 API key 分支；取消异常重抛；其余异常（如密钥读取
  失败）→ 500 `Failed to validate token`，不降级为 API key。
- API key：`IApiKeyAuthenticator.AuthenticateAdminKeyAsync` 严格认证（无 no-auth
  回退、不查 allowed_ips），按 `AuthenticateAPIKey` 逐分支：缺行 / 停用 /
  项目缺失 / 项目非 active / noauth 类型 → `InvalidApiKeyException` → 401
  `Invalid token`；其他存储失败 → 500 `Failed to validate token`。随后类型必须
  为 `service_account`，否则 403
  `Only service account API keys may access the admin GraphQL API`；再查
  allowed_ips（类型先于 IP，`auth.go:231-242`），不匹配 → 403
  `IP address is not allowed for this API key`。成功身份为
  `AdminGraphqlIdentity(User: null, ApiKeyId, ProjectId, IsApiKey: true)`。
- 参照在成功后还调用 `withUserPrincipal` / `withAPIKeyPrincipal` 并带有 401
  `Invalid authentication context` 分支；这两个构造器只会在上下文已存在不同
  principal 时失败，中间件在新请求上不可达，端口未单列（C014）。

`WithProjectID`：`X-Project-ID` 为空放行；`objects.ParseGUID` 失败或类型不是
Project → 400 `Invalid project ID`；API key 的 ProjectId 与头部不同 → 403
`Project ID is not allowed for this API key`；否则把项目写入身份。

`apiKeyReadOnly`：只对 API key 身份生效；仅处理 `application/json`，按 gqlgen 的
JSON 信封读取 `query` 与 `operationName`，用 HotChocolate 的
`Utf8GraphQLParser` 解析后按名字选择操作；选中的操作是 Query（或文档非单操作且
无名字）时放行，其余（含 Subscription）→ 200 `application/json`
`{"errors":[{"message":"service account API keys are read-only"}]}`，无 `data`。
非 JSON、JSON 畸形与 GraphQL 语法错误交给 HotChocolate 回复；只读判定发生在
解析后、校验前，登记为 C167。

`HttpContextManagementIdentity` 从 `HttpContext.Items` 读回上述身份并实现
`IManagementIdentity`：JWT 请求得到 `ManagementUser`，API key 请求得到 null，
解析器沿用既有的 `user not found in context` / 隐私拒绝分支（C161）。原先仅供
测试的 `OwnerManagementIdentity` 删除；18 个管理面端点测试宿主改为先注册门依赖
并以 JWT 或 service_account 客户端发起请求。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 鉴权门：三类 header 文案与 token 修剪、JWT 成功、service_account 成功、user 类型 403、未知 / 停用 / 非 active 项目 / noauth → 401、类型先于 IP、IP 403、密钥读取失败与 API key 存储失败 → 500、API key 身份无用户 | `tests/AxonHub.Api.Tests/AdminGraphqlAuthTests.cs` | 16 |
| X-Project-ID：空值放行、畸形与非 Project 类型 400、API key 跨项目 403、本项目管理放行 | 同上 | 5 |
| 只读与路由：API key 变更拒绝（不落库）、查询放行、operationName 选择 Q/M、JWT 可变更、GET 404 | 同上 | 6 |

`AxonHub.Api.Tests` 由 1843 增至 1870（+27），`AxonHub.Persistence.Tests` 保持
178，`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持 92；四个项目
合计 5133 增至 5160，0 失败 0 跳过。

差异：C014 记管理员 GraphQL 鉴权已接入（OIDC 提供方配置、角色 / 权限校验与
session scope / authz principal 仍缺）；C109 的第 (5) 项收窄为请求超时；C161
改写为 HttpContext 身份接缝（`OwnerManagementIdentity` 已删除）；新增 C167
（只读规则在解析后、校验前执行等近似）。

## 82. OIDC 提供方配置、启动发现与 providers 列表（NewOIDCService 启动检查 / GET /oauth/oidc/providers，53 例，2026-11-03）

参照 `internal/server/biz/oidc.go:160-460` 与 `internal/server/api/oidc.go:57-71`，
路由 `routes.go:101-104`（`/oauth` 组无鉴权）。端口新增 `Auth/Oidc/` 五个文件：

- `OidcProviderConfig`：`oidc.providers` 的全部 25 个字段（snake_case 键，另接受
  PascalCase 别名，C168）、`normalize()` 的派生链（ID ← Name ← DisplayName、
  Name ← ID、DisplayName ← Name，全部 TrimSpace）、`providerID` /
  `providerDisplayName` / `issuer()`（Issuer 优先）与 `matchesIdentifier`
  （去空格 + 转小写后比较 ID 与 Name，空标识符恒 false）。
- `OidcHttpClient` / `IOidcHttpClient`：go-oidc `oidc.NewProvider` 的请求形状：
  `TrimSuffix(issuer,"/")` + `/.well-known/openid-configuration`、非 200 视为
  失败、响应按 snake_case 解码（issuer、authorization_endpoint、token_endpoint、
  userinfo_endpoint、jwks_uri、id_token_signing_alg_values_supported）、issuer
  精确回显校验；错误文案为近似（C170）。
- `OidcProviderRegistry`：`NewOIDCService` 的 provider 循环。空 ID →
  `OIDC provider at index %d requires id or name`；归一化 ID 重复 →
  `duplicate OIDC provider id %q conflicts with %q`；每个 provider 先把
  `lastCheck[id]` 记为当前 Unix 秒；`issuer_url` 非空时 discovery（失败只告警）；
  `auth_url` 与 `token_url` 同时非空则覆盖端点；`jwks_url` 非空视为有 verifier；
  缺端点 → 记错误并跳过（不进入运行时表，但仍留在配置列表）；图标经
  `OidcIconResolver` 内联（失败保留原值并记错误）；默认 redirect
  `/oauth/oidc/callback`（多 provider 时追加 `/{id}`）；默认 scopes
  `openid/profile/email`。运行时表按归一化标识符与 Name 匹配查询，
  `MarkProviderCheck` 以 60 秒为刷新窗口，`SetProvider` 备用于后续重试路径。
  参照在 DI 构图时同步执行；端口以 memoized Task 在
  `OidcInitializationHostedService.StartAsync`（以及首个请求）执行，配置错误同样
  使启动失败（C168）。
- `OidcService.GetProvidersAsync`：仅在用户上下文存在时查询 `oidc_identities`
  （失败被忽略），按 `issuer()` 关联；active 由运行时表决定，inactive 才输出
  `last_check`；`linked_identity_id` 形如 `gid://axonhub/OIDCIdentity/{id}`，
  空邮箱省略。
- `OidcHandlers.GetProvidersAsync`：可选 `Bearer ` 前缀（`strings.CutPrefix`
  语义：大小写敏感、不 TrimSpace），JWT 无效被忽略；输出 `{"data":[...]}`，
  零提供方时 Go 的 nil slice 序列化为 `{"data":null}`。
- `OidcLoginOnlyProviders` 替换测试用的 `EmptyOidcLoginOnlyProviders`：按
  `issuer()` 精确匹配且 `oidc_login_only=true` 时限制本地登录，接回
  `ManagementAuthService` 的既有分支；`Program.cs` 同时注册配置列表、
  `OidcHttpClient`（无限超时，等价 Go 默认客户端）与宿主启动服务。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 配置：normalize 派生、标识符匹配、issuer 优先级、snake_case / PascalCase 绑定、标量列表、默认值 | `tests/AxonHub.Api.Tests/OidcProviderConfigTests.cs` | 18 |
| 注册表：discovery、手工端点覆盖与回退、JWKS verifier、缺端点跳过、重复 / 空 ID 文案、redirect / scopes 默认、60 秒检查窗口、SetProvider、标识符查询、图标内联 / 嗅探 / 失败保留、幂等、login-only 匹配 | `tests/AxonHub.Api.Tests/OidcProviderRegistryTests.cs` | 20 |
| 端点：字段名与顺序、data 包装与 null、inactive last_check、linked 身份、空邮箱、异 issuer、无效 / 非 Bearer 令牌、身份查询失败 | `tests/AxonHub.Api.Tests/OidcProvidersEndpointTests.cs` | 9 |
| discovery 线路形状：well-known URL、尾斜杠回显、非 200、issuer 不匹配、畸形 JSON、传输错误 | `tests/AxonHub.Api.Tests/OidcHttpClientTests.cs` | 6 |

`AxonHub.Api.Tests` 由 1870 增至 1923（+53），`AxonHub.Persistence.Tests` 保持
178，`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持 92；四个项目
合计 5160 增至 5213，0 失败 0 跳过。

差异：C014 记 OIDC 提供方配置、启动发现、图标内联与 providers 列表已接入，
残余收窄为登录流程（authorize / callback / exchange / link）；新增 C168
（PascalCase 绑定与启动检查异步化）、C169（图标 MIME / 嗅探子集）、C170
（go-oidc discovery 文案近似）。

## 83. OIDC 登录流程切片（authorize / link / callback / exchange，143 例，2026-11-03）

参照 `internal/server/biz/oidc.go:509-1300` 与 `internal/server/api/oidc.go:73-277`，
公开路由 `routes.go:101-104`（`/oauth/oidc/authorize/:provider`、
`/oauth/oidc/callback[/:provider]`、`POST /oauth/oidc/exchange`）与
`routes.go:131`（`GET /admin/oidc/link/:provider`，JWT 门 + ProjectIdMiddleware）。
端口新增 `Auth/Oidc/` 的十一个文件与 `Persistence/Stores/OidcLoginStore.cs`：

- `OidcLoginOptions`：`server.public_url`（`server` 段优先，
  `AXONHUB_SERVER_PUBLIC_URL` 回退）、TrimSpace、Go `url.Parse` + http(s) 方案与
  host 校验、去掉一个尾斜杠；缺省 →
  `server.public_url must be configured when OIDC is enabled`，非 http(s) →
  `server.public_url must use http or https`，其余非法 → `invalid server.public_url`
  （URL 手写解析的残余近似见 C171）。
- `OidcCache`：`patrickmn/go-cache` 语义的进程内实现（过期判定为
  `now > expiresAt`，键按字节）；`cache.mode=redis|two-level` 未移植（C171）。
- `OidcTokenClient`：oauth2 v0.36.0 的 `Config.Exchange`：表单体按
  `url.Values.Encode` 字母序（`code`、`code_verifier`、`grant_type`、
  `redirect_uri`，空值省略）、先 Basic 头后 params 的认证样式探测与按
  (token_url, client_id) 缓存成功样式、1 MiB 读取上限、JSON / form 两种响应、
  `RetrieveError` 文案（`oauth2: "code"`，缺 code 时状态码与 body）。
- `OidcUserInfoClient`：discovery 路径为 go-oidc `Provider.UserInfo`（Bearer
  GET、空端点报 `oidc: user info endpoint is not supported by this provider`），
  手工路径为 10 秒上下文 / 15 秒客户端超时的普通 GET；subject 取 `sub`，
  否则 GitHub 数字 `id`（JSON number 原样）→ `login`。
- `OidcIdToken`：JWKS 文档解析与 go-oidc v3.18.0 / go-jose v4.1.4 顺序的
  RS256 校验（三段 compact、RawURLEncoding、受保护头的 alg、JWKS 取键与验签），
  以及 issuer / audience / exp 检查与 go-oidc 文案；密钥缓存与少数解码文案
  仍为近似（C170）。
- `OidcClaims` / `OidcGlob`：claims 解码（string / bool / number / groups）与
  `filepath.Match`（Unix 分隔符）规则匹配，错误回退为字面比较。
- `OidcLoginService`：`GetAuthorizeURL`（运行时 provider、60 秒重试窗口
  `Please wait %d seconds before retrying this provider`、启动检查失败的
  provider 仅按 issuer 重建、state 缓存 10 分钟、可选 PKCE S256 verifier 缓存）、
  `GetLinkAuthorizeURL`（`oidc_link_state:` 存用户 ID 10 分钟）、`Callback`
  （state 先取后删、PKCE verifier 单次消费、授权码换 token、ID-token 与
  UserInfo 合并、link 意图、resolveUser：既有身份 → 已验证邮箱自动关联 →
  JIT 创建 / 拒绝、用户停用 / 删除、JIT 用户名按首个空格拆分、
  `applyRoleMappings` 的 sync / merge / manual_first / create_only / always 五种
  策略与 glob / 正则规则、last_login 失败只告警、身份创建与返回）、
  `ExchangeCode`（`oidc_exchange:` 5 分钟、按 code 的进程内互斥、单次消费、
  删除用户报 `user not found: ...`）。
- `OidcLoginStore`：EF Core 写 + Dapper 读的登录查询（按邮箱取活用户、
  `oidc_identities` 读写与 touch、JIT 用户与身份创建、角色并集与整体替换、
  last_login 更新、exchange 取用户）。
- `OidcLoginHandlers` / `OidcLoginRoutes`：authorize 200 线形与 400 / 500；
  link 的 400 `Provider is required` 与 401 `Unauthorized`；callback 的
  provider 可选（单 provider）、error / code / state 校验、失败 302 到
  `/oauth/oidc/idp-callback?error=auth_failed&error_description=<QueryEscape>`、
  link 302 到 `/settings/profile?oidc_link=success`、登录 302 带 64 位 hex code；
  exchange 的 gin `ShouldBindJSON` 仿真（EOF / unexpected EOF / 字段类型 /
  required 校验 / 匿名结构体类型名）、400 分类与 ent.User JSON（字段序、
  omitempty、RFC3339Nano、edges 恒 `{}`、Go HTML 转义）。
- `OidcLinkAuthMiddleware` 与 `Program.cs` 的 `/admin/oidc/link` 分支：
  复用管理员 JWT 解析，401 文案与 `OidcLinkAuth.ContextKey` 注入。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 登录服务：authorize / PKCE / link / callback / token / userinfo / resolveUser / JIT / 角色映射五策略 / exchange 单次消费 | `tests/AxonHub.Api.Tests/OidcLoginServiceTests.cs` | 62 |
| 端点：authorize / link 门 / callback 重定向 / 单 provider / exchange 绑定与 ent JSON / JWT 失败 | `tests/AxonHub.Api.Tests/OidcLoginEndpointTests.cs` | 30 |
| public_url：来源优先级、TrimSpace、绝对 URL 校验与错误分类 | `tests/AxonHub.Api.Tests/OidcLoginOptionsTests.cs` | 26 |
| 令牌客户端：表单键序与 Basic / params 探测、样式缓存、JSON / form 响应、错误文案 | `tests/AxonHub.Api.Tests/OidcTokenClientTests.cs` | 9 |
| UserInfo：discovery / 手工两路径、端点与状态错误、subject 回退 | `tests/AxonHub.Api.Tests/OidcUserInfoClientTests.cs` | 9 |
| ID-token：RS256 成功、issuer / audience / exp、他键签名、alg、空 JWKS | `tests/AxonHub.Api.Tests/OidcIdTokenVerifierTests.cs` | 7 |

`AxonHub.Api.Tests` 由 1923 增至 2066（+143），`AxonHub.Persistence.Tests` 保持
178，`AxonHub.Llm.Tests` 保持 3020，`AxonHub.Grains.Tests` 保持 92；四个项目
合计 5213 增至 5356，0 失败 0 跳过。

差异：C014 记 OIDC 登录流程（authorize / link-authorize / callback / exchange）
与用户解析已接入，OIDC 部分闭合，残余收窄为角色 / 权限校验与通道管理面；
C170 扩展到 ID-token 校验的顺序与包装对齐（残余为密钥缓存与解码文案）；
新增 C171（缓存模式、glob 与 URL 解析的进程内近似）。

## 84. 权限校验切片（PermissionValidator 与 users / roles 写入接线，25 例，2026-11-03）

参照 `internal/server/biz/permission_validator.go`（267 行）与 user.go / role.go 的
四个调用点，端口新增 `Management/PermissionValidator.cs` 并把校验接入
`UserManagementService.UpdateAsync` / `DeleteAsync` 与
`RoleManagementService.CreateAsync` / `UpdateAsync`（参照中 CreateUser /
UpdateUserStatus / DeleteRole / BulkDeleteRoles 无权限校验，AppendScopes 同样
不校验，端口保持）：

- 六个校验方法：`CanGrantScopes` / `CanGrantRole` / `CanEditUserPermissions` /
  `CanEditRole` / `CanDeleteProject` / `CanDeleteUser`；`getUserScopes` 汇集
  直接作用域 + 系统角色作用域 +（指定项目时）项目成员作用域与项目角色
  作用域；`isProjectOwner` 检查全局 is_owner 或该项目的成员 is_owner。
- 服务接线顺序与参照一致：UpdateUser 先 `CanEditUserPermissions(id, nil)`，
  再 `input.Scopes != nil` 时 `CanGrantScopes(input.Scopes, nil)`，再逐个
  `AddRoleIDs` 调 `CanEditRole(roleID, nil)`，全部包 `permission denied: %w`
  （密码哈希移到权限检查之后，与参照的 mut 构建顺序一致）；DeleteUser 先
  `CanDeleteUser`（自删检查先于 owner 检查）；CreateRole 先
  `CanGrantScopes(input.Scopes, projectID)`（先于 level 校验，nil scopes 视为
  空）；UpdateRole 先 `CanEditRole(id, nil)`，`Scopes != nil` 时重取角色、
  非系统角色以 `role.ProjectID` 为 projectID 再 `CanGrantScopes`。
- 错误文案逐字对齐：`user not found in context`、
  `insufficient permissions: cannot grant scope '%s' that you don't possess`、
  `insufficient permissions: cannot edit owner users`、
  `insufficient permissions: target user has scope '%s' that you don't possess`、
  `insufficient permissions: only system owners can delete projects` /
  `insufficient permissions: only system owners can delete users`、
  `cannot delete yourself`、`failed to get target user: ent: user not found`、
  `failed to get role: ent: role not found`。
- 当前用户经 `IManagementIdentity`（Id + IsOwner，与参照中间件加载值同源，
  全局 owner 短路不触库），边数据（作用域 / 角色 / 项目成员）由新增的
  `IUserAdminStore.GetPermissionUserAsync`（WithRoles + WithProjectUsers 的
  等价查询）在校验时按需读取——参照在中间件预载边（C161 扩展）。
- 目标用户作用域在参照中按 Go map 遍历顺序检查（多条缺失时报告哪条随机），
  端口按加载顺序（直接 → 系统角色 → 项目成员 → 项目角色）报告第一条
  （新增 C172）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 校验器：CanGrantScopes 四例（owner / 拥有 / 缺失 / 角色作用域）、CanEditUserPermissions 四例、CanEditRole 三例（含项目角色有效作用域与取角色先于查用户的顺序）、项目级三例、与 RoleService 集成两例、无用户 / 自删 / 仅系统 owner 四个守卫 | `tests/AxonHub.Api.Tests/PermissionValidatorTests.cs` | 20 |
| 服务接线与顺序：缺目标先过权限检查、scope / role 拒绝、自删、仅系统 owner 可删用户、CreateRole / UpdateRole 拒绝 | `tests/AxonHub.Api.Tests/ManagementServiceTests.cs` | 5（新增） |
| 既有端点回归：gate owner 下 users / roles 的全部变更保持通过 | `tests/AxonHub.Api.Tests/AdminUsersRolesEndpointTests.cs` | 48（§64 用例，不重复计数） |

`AxonHub.Api.Tests` 由 2066 增至 2091（+25），其余三个项目保持；四个项目
合计 5356 增至 5381，0 失败 0 跳过。

差异：C014 补记权限校验器（PermissionValidator）与 users / roles 写入接线已
接入，残余收窄为通道管理面其余部分；C161 扩展记当前用户的边数据由校验器
按需读取；新增 C172（Go map 遍历顺序 vs 端口确定顺序）。

## 85. 项目成员切片（addUserToProject / removeUserFromProject / updateProjectUser 与 node() 项目成员查询，32 例，2026-11-03）

参照 `internal/server/biz/user.go` 的 AddUserToProject（394-461）、
RemoveUserFromProject（463-500）、UpdateProjectUser（502-579）与
`axonhub.resolvers.go` 的三个变更（610-632），端口在 `UserAdminStore` 增加
`user_projects` 的新增 / 删除 / 更新与两条列表存储方法、在
`UserManagementService` 增加三个服务方法，GraphQL 侧新增 `UserProject`
类型（实现 Node 接口）、
`Project.projectUsers` / `User.projectUsers` 扩展与 `node(id:)` 查询，对齐
entgql 生成的形状与前端 project-users 页面的实际查询：

- AddUserToProject：主体为 test principal（`IsTest`）时跳过全部权限检查
  （authz.GetPrincipal + IsTest 的旁路）；否则 scopes 先 CanGrantScopes
  （包 `permission denied: %w`），再逐个 roleID 按系统旁路加载角色
  （`Role.Query().Where(IDEQ, ProjectIDEQ)`）——缺失或跨项目报
  `project role %d not found`（不包 permission denied），其它失败报
  `failed to load project role %d: %w`——再 CanGrantRole(role.Scopes)
  （包 `permission denied: %w`）。随后事务内：建 UserProject（isOwner /
  scopes 仅非 nil 时写入，缺省 isOwner=false）→ `failed to add user to
  project: %w`；roleIDs 为空直接返回；取用户 → `failed to get user: %w`；
  加角色（去重且跳过既有）→ `failed to add roles to user: %w`；提交。
  存储失败原样转发（UpdateStatusAsync 先例），其余包装
  `failed to add user to project: `。
- RemoveUserFromProject：无权限检查、无事务；删除关系 → `failed to remove
  user from project: %w`；rowsAffected == 0 直接返回（不清理角色、不失效
  缓存）；查该用户在该项目下的角色边 → `failed to query user project
  roles: %w`；移除 → `failed to remove user project roles: %w`；系统角色与
  其它项目角色保留。
- UpdateProjectUser：三个权限检查先行（CanEditUserPermissions(userID,
  &projectID)、scopes → CanGrantScopes、逐个 addRoleID → CanEditRole，全部
  包 `permission denied: %w`）；关系缺失报 `failed to find user project
  relationship: ent: user_project not found`；写 isOwner / scopes →
  `failed to update project user: %w`；取用户 → `failed to get user: %w`；
  先加后移角色（同一角色同时出现在两列表时移除胜）→ `failed to update user
  roles: %w`。
- 线路形状：`UserProject` 的 id / createdAt / updatedAt / userID / projectID /
  isOwner / scopes / user / project 与 SDL（ent.graphql:7336-7352）一致；
  `AddUserToProjectInput` / `UpdateProjectUserInput` /
  `RemoveUserFromProjectInput` 按 axonhub.graphql:674-694 命名
  （roleIDs / addRoleIDs / removeRoleIDs 为 gid 数组）；`node(id:)` 解析
  `gid://axonhub/<Type>/<id>`（GUID 的四个解析文案照搬），Project / User
  分别返回实体或 `ent: project not found` / `ent: user not found`，其余
  18 个 ent 类型报 `unknown node type: %s`（C165）；Project / User 的
  projectUsers 无排序（参照 `QueryProjectUsers().All(ctx)`，C164 沿用）。
- 三个变更后的 `invalidateUserCache` 在端口为 no-op（C173）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 服务语义：Add（成功 / 带角色 / nil owner / 重复关系）、Remove（成功 / 缺失 / 项目角色清理）与 Update（scopes / 加角色 / 移角色 / 加移并施 / 缺失 / scopes+角色 / isOwner 成功 / isOwner 拒绝）的参照逐字文案 | `tests/AxonHub.Api.Tests/ProjectUserServiceTests.cs` | 15（新增） |
| 存储语义：关系创建与角色去重、isOwner 缺省与 scopes 缺省 null、重复关系前缀、缺用户回滚整事务、删除只清理该项目角色、无关系不动角色、先加后移、缺失关系前缀、列表无排序 | `tests/AxonHub.Persistence.Tests/UserAdminStoreTests.cs` | 9（新增） |
| 端点线形：node() 的 Project / User projectUsers 查询（前端 buildProjectUsersQuery 形状，含 `roles(where: { projectID })`）、未知类型与缺失节点文案、三个变更输入输出、跨项目角色拒绝、移除幂等、更新加移角色、缺失关系 | `tests/AxonHub.Api.Tests/AdminUsersRolesEndpointTests.cs` | 8（新增） |

`AxonHub.Persistence.Tests` 由 178 增至 187（+9），`AxonHub.Api.Tests` 由
2091 增至 2114（+23）；四个项目合计 5381 增至 5413，0 失败 0 跳过。

差异：C165 更新（projectUsers 已在 Project / User 接线；node() 只解析
Project / User，其余 18 个 ent 类型报 `unknown node type: %s`）；新增 C173
（三个变更后的 invalidateUserCache 为 no-op——管理面读取路径无 UserCache）；
C153 沿用（user_projects 唯一冲突的存储文案）；C161 沿用（UpdateProjectUser
的 isOwner 拒绝测试以目标持有调用方缺失的 scope 复现——生产上 ctx 用户经
GetUserByID 预载边）。

## 86. 项目管理切片（projects 查询与五个变更，41 例，2026-11-03）

参照 `internal/server/biz/project.go` 的 CreateProject（51-157）、UpdateProject
（159-188）、UpdateProjectProfiles（236-256）、ValidateProjectProfiles
（259-298）、UpdateProjectStatus（300-315）、DeleteProject（335-385）与
`internal/server/gql/ent.resolvers.go` 的 projects 查询（439-452）及
`axonhub.resolvers.go` 的五个变更（582-608），端口新增 `IProjectStore`
（EF 写 + Dapper 读）、`ProjectManagementService`、`ProjectProfilesCodec`、
GraphQL `ProjectOrder` / `ProjectWhereInput` / `ProjectConnection` /
`ProjectProfiles` / `ChannelTagsMatchMode`（标量）与 projects / createProject /
updateProject / updateProjectStatus / updateProjectProfiles / deleteProject
六个成员，对齐前端 projects.ts 的实际查询与变更：

- CreateProject：ctx 用户缺失报 `user not found in context`；重名检查
  `Project.Query().Where(NameEQ).Exist` → 包
  `failed to check project name uniqueness: %w`，重名抛
  `xerrors.DuplicateNameError("project", name)`（`project name '<name>'
  already exists` + resource/field/value 扩展）；建项目只写 Name 与非 nil
  Description，其余取 ent 默认（description ""、status "active"、profiles
  `{"activeProfile":"","profiles":null}`）；随后按序建 Admin / Developer /
  Viewer 三个 LevelProject 角色（失败前缀 `failed to create admin role: ` /
  `developer` / `viewer`，作用域与参照逐条一致），再以 IsOwner=true、
  Scopes=[] 写 owner 关系（失败 `failed to assign user to project: %w`）。
  参照无事务：任一步失败保留已写入行。CreateProjectInput 的 status /
  userIDs 绑定但不使用。
- UpdateProject：无权限检查；SetNillableName / SetNillableDescription、
  ClearUsers、AddUserIDs、RemoveUserIDs；存储失败统一包
  `failed to update project: %w`，缺失行报 `failed to update project:
  ent: project not found`；input status 不生效。ent 的 sqlgraph 顺序为节点
  UPDATE → 边 clear → 边 add，join 表新增为普通 INSERT（无 ON CONFLICT），
  重复成员触发唯一冲突并回滚整个更新，文案前缀
  `add m2m edge for table user_projects: `。
- UpdateProjectStatus：只写 status，`failed to update project status: %w`。
- UpdateProjectProfiles：先 ValidateProjectProfiles（错误不包前缀），再序列化
  与写入，失败包 `failed to update project profiles: %w`。校验顺序：名称
  Trim/ToLower 后为空 → `profile name cannot be empty`；重复 →
  `duplicate profile name: <原始 name>`；matchMode 非 ""/any/all/none →
  `profile '<name>' channelTagsMatchMode is invalid`；最后 ActiveProfile
  非空时须与某个 profile.Name 大小写敏感相等 →
  `active profile '<name>' does not exist in the profiles list`。
- profiles JSON：字段顺序 activeProfile、profiles；profile 内 name、
  channelIDs、channelTags、channelTagsMatchMode，nil 与空切片均省略、
  空 mode 省略；HTML 转义 `<`/`>`/`&`/U+2028/U+2029、控制字符小写
  `\u00xx`，均按 Go encoding/json 输出。
- DeleteProject：CanDeleteProject（仅系统 owner）失败包
  `permission denied: %w`；事务内取项目 → `failed to get project: %w`；
  硬删 user_projects → `failed to delete project users: %w`；按 project_id
  软删角色（不清理 user_roles）→ `failed to delete project roles: %w`；
  软删 api_keys → `failed to delete project API keys: %w`；软删项目 →
  `failed to delete project: %w`；缺失项目返回 false，resolver 报
  `failed to get project: ent: project not found`；成功返回 true。
- projects 查询：CREATED_AT 改写为 ent.DefaultProjectOrder 的 id 并保留
  direction，UPDATED_AT 走 updated_at；Project.profiles 在所有 Project
  出口（projectUsers.node、myProjects、变更、connection）接线，jsonb NULL
  序列化为 null。
- 项目缓存失效在端口为 no-op（新增 C174）；ChannelTagsMatchMode 以
  字符串标量实现（新增 C175）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 服务语义：创建顺序（项目 → 三角色 → owner）、重名 DUPLICATE_NAME、缺用户、角色失败保留项目；更新（名称 / 边替换 / 重复加边回滚 / 缺失）、状态、profiles 序列化与异常、删除权限与软删；校验器 5 例（含 3 个 theory 行）；JSON 编解码 2 例 | `tests/AxonHub.Api.Tests/ProjectManagementServiceTests.cs` | 19（新增） |
| 存储语义：NameExists 忽略软删、过滤 / 排序 / keyset / last、部分列更新与 updated_at、边替换与 join 默认值、重复加边整体回滚、profiles / status 与缺失、AssignOwner 与重复前缀、删除级联（user_projects 硬删、角色与 API key 软删、user_roles 保留） | `tests/AxonHub.Persistence.Tests/ProjectStoreTests.cs` | 8（新增） |
| 端点线形：projects 连接形状（含 profiles 与空 mode）、nameContainsFold or 过滤与 status、keyset 分页、分页参数校验、createProject（角色 / owner / 默认 profiles / 重名扩展）、updateProject（status 忽略、边、重复加边前缀）、updateProjectStatus、updateProjectProfiles（Go JSON 往返、校验文案不包前缀）、deleteProject（成功 / 缺失 / 非 owner） | `tests/AxonHub.Api.Tests/AdminProjectsEndpointTests.cs` | 14（新增） |

`AxonHub.Persistence.Tests` 由 187 增至 195（+8），`AxonHub.Api.Tests` 由
2114 增至 2147（+33）；四个项目合计 5413 增至 5454，0 失败 0 跳过。

差异：C165 更新（Project.profiles 已接线；projects 查询接入，单实体与其余
SDL 成员仍缺）；新增 C174（项目缓存失效为 no-op）；新增 C175
（ChannelTagsMatchMode 为字符串标量，SDL kind 与枚举字面量语法差异）；
C153 沿用（user_projects 唯一冲突的存储文案）。

## 87. API key 管理切片（apiKeys 查询、node 与八个变更，39 例，2026-11-04）

参照 `internal/server/biz/api_key.go` 的 CreateAPIKey、UpdateAPIKey、
UpdateAPIKeyStatus、UpdateAPIKeyProfiles、RotateAPIKey、BulkDisableAPIKeys /
BulkEnableAPIKeys / BulkArchiveAPIKeys 与 QueryAPIKeys，
`internal/server/gql/ent.resolvers.go` 的 apiKeys 查询与 node APIKey 分支，
`internal/server/gql/axonhub.resolvers.go` 的八个变更，以及生成的 APIKey
SDL；端口新增 `IApiKeyStore`（EF 写 + Dapper 读）、`ApiKeyManagementService`、
`ApiKeyProfilesCodec`、GraphQL `APIKeyWhereInput` / `ApiKeyOrder` /
`APIKeyConnection` / `APIKey`（INode）与全部输入类型，接线 apiKeys /
node(APIKey) 与 createAPIKey / updateAPIKey / updateAPIKeyStatus /
updateAPIKeyProfiles / rotateAPIKey / bulkDisableAPIKeys / bulkEnableAPIKeys /
bulkArchiveAPIKeys，对齐前端 api-keys.ts 的实际查询与变更：

- CreateAPIKey：先取当前用户（缺失 `user not found in context`），再拒
  `noauth`（`noauth type API key is reserved`），仅 user 类型要求项目管理员
  （`permission denied: project API keys require project admin permissions`）；
  GenerateAPIKey 为 `<prefix>-` + 32 随机字节的小写 hex；事务内锁项目、过
  privacy 与插入（两处错误均包 `failed to create API key: %w`），随后按
  Name + ProjectID + (TypeNEQ(personal) OR UserIDEQ(当前用户)) 计数，大于 1
  抛 `xerrors.DuplicateNameError("API Key", name)`（`API Key name '<name>'
  already exists` + resource/field/value 扩展）并回滚插入。
  user 默认作用域 `["read_channels","write_requests"]`，service_account 未给
  作用域为 `[]`，profiles 默认 `{"activeProfile":"","profiles":null}`，
  allowedIps 默认 `[]`。
- UpdateAPIKey：先按目标行的类型拒 scope 变更（`user type API key cannot
  update scopes` / `personal type ...`），再拒 noauth（`noauth type API key
  cannot be updated`），再检查 personal 所有权（`personal API key can only be
  modified by its creator or a system owner`）；读取失败包
  `failed to get API key: %w`；改名先查重（`failed to check API key name
  uniqueness: %w`），写入失败包 `failed to update API key: %w`，成功后再次
  查重。ent 的 json Append 覆盖同列的 Set，追加基于更新前的值。
- UpdateAPIKeyStatus：拒绝 noauth（`noauth type API key status cannot be
  updated`）、检查 personal 所有权；读取 / 写入错误包 `failed to update API
  key status: %w`，privacy deny 也包进该前缀。
- UpdateAPIKeyProfiles：拒绝 noauth（`noauth type API key profiles cannot be
  updated`），读取后经 `ApiKeyProfilesCodec` 校验（名称 Trim/ToLower 空、
  重复、matchMode、active profile 存在性）、按模板当前值决定是否脱钩，失败
  `failed to update API key profiles: %w`；JSON 字段顺序为 activeProfile /
  profiles，profile 内 name、templateID、templateName、modelMappings、
  quota（仅非 nil）、loadBalanceStrategy、traceStickyMode、channelIDs、
  channelTags、channelTagsMatchMode、modelIDs，配额 cost 以 JSON 字符串
  输出、period 按 type 只带对应字段；查询侧 templateName 经 gqlgen 的
  optional string marshaller 输出 `""`。
- RotateAPIKey：拒绝 noauth（`noauth type API key cannot be rotated`）、检查
  personal 所有权（`personal API key can only be rotated by its creator or a
  system owner`），生成新 key 写回；错误包 `failed to rotate API key: %w`。
- 批量 disable / enable / archive：先按 ID 集合读取（数量不符
  `expected to find %d API keys, but found %d`），再拒 noauth（`noauth type
  API key cannot be bulk %sd`），再逐个检查 personal 所有权（`personal API
  key "<name>" can only be %sed by its creator or a system owner`）；空 ID
  列表为 no-op；archive 只写 status=archived，不写 deleted_at。写失败包
  `failed to <action> API keys: %w`。
- apiKeys 查询：项目作用域 + privacy 过滤，CREATED_AT 改写为 id 排序并保留
  direction，UPDATED_AT / NAME 走对应列；游标值只在 UPDATED_AT / NAME 携带；
  node(APIKey) 缺失报 `ent: api_key not found`，隐私拒绝为
  `ent/privacy: deny rule`。
- APIKey 输出：`id` / `key` / `name` / `type` / `status` / `scopes` /
  `profiles` / `allowedIps` / `userID` / `projectID` / `user` / `project` /
  `createdAt` / `updatedAt`；`userID` 恒为 `gid://axonhub/User/<UserID>`，
  user_id 为空时即 `gid://axonhub/User/0`；user 边经 privacy 解析，缺失与
  deny 均为 null；project 边缺失或不可见报 `ent: project not found`，
  deny 报 `permission denied`。
- 读取缓存失效在端口为 no-op（C026 / C173 / C174 同型），每个写方法自开
  事务（C142 同型）；`APIKeyWhereInput` 只建模前端使用的谓词（C135 同型）；
  枚举输出对未知存储值报错（C176），旋转已实现。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 服务语义：创建默认值 / 密钥生成 / 项目管理员门槛 / noauth / allowedIps 校验 / 重名回滚；更新 scope 限制与 append；状态 privacy 包裹与 personal 所有权；profiles 模板脱钩与校验顺序；旋转；批量计数 / noauth / 所有权 / 空列表；列表与 node 隐私 | `tests/AxonHub.Api.Tests/ApiKeyManagementServiceTests.cs` | 19（新增） |
| 存储语义：按 ID 与 key 读取、软删可见性、key 跨软删唯一、archived 状态不软删、列表 / 游标 / 总数、重复名与 key 谓词、clear / replace / append 更新、项目谓词、批量状态只影响目标行 | `tests/AxonHub.Persistence.Tests/ApiKeyStoreTests.cs` | 9（重写，净 +5） |
| 端点线形：apiKeys 连接形状（profiles / quota / 空 templateName / 空 mode / User 与 Project 边 / 游标）、过滤与 NAME 排序、无创建者输出 User/0 与 null 边、node 查询与缺失错误、创建默认值与前缀、service_account 的 scopes / allowedIps、重名 DUPLICATE_NAME 扩展、更新改名与 scope 拒绝、状态与旋转、profiles JSON 往返、批量 disable / enable / archive 与计数错误 | `tests/AxonHub.Api.Tests/AdminApiKeysEndpointTests.cs` | 11（新增） |

`AxonHub.Persistence.Tests` 由 195 增至 200（+5），`AxonHub.Api.Tests` 由
2147 增至 2177（+30）；四个项目合计 5454 增至 5489，0 失败 0 跳过。

差异：新增 C176（API key 管理切片的已接受差异与真实缺口）；C165 更新
（node(APIKey) 已接入，其余 ent 类型仍缺）；C142（事务边界）、C135（where
子集）、C026 / C173 / C174（无读取缓存）沿用。

## 88. 模型管理切片（models 查询、node 与八个变更，32 例，2026-11-05）

参照 `internal/server/biz/model.go` 的 CreateModel（356-389）、BulkCreateModels
（392-456）、UpdateModel（459-498）、UpdateModelStatus（501-510）、DeleteModel
（513-519）、BulkArchive / Disable / Enable / DeleteModels（522-570）、ListModels
（610-636）、QueryUnassociatedChannels（813-840）、countAssociatedChannels
（842-868）与 `internal/server/biz/model_association_matcher.go`，
`internal/server/gql/model.resolvers.go` 的 queryModels（129-160）、
`internal/server/gql/ent.resolvers.go` 的 models 查询与 node Model 分支、
`internal/server/gql/axonhub.resolvers.go` 的八个变更，以及生成的 Model SDL；
端口新增 `IModelStore`（EF 写 + Dapper 读）、`ModelManagementService`、
`ModelPrivacy`、`ModelSettingsInheritance`、`ModelAssociationMatcher`、
GraphQL `ModelOrder` / `ModelWhereInput` / `ModelConnection` / `ModelType`
（INode）与全部输入类型，接线 models / queryModels /
queryModelChannelConnections / queryUnassociatedChannels / fetchModels /
node(Model) 与 createModel / bulkCreateModels / updateModel / deleteModel /
updateModelStatus / bulkArchiveModels / bulkDisableModels / bulkEnableModels /
bulkDeleteModels，对齐前端 models.ts 的实际查询与变更：

- CreateModel：先 validateModelSettings（仅当 input.Settings 非 nil，文案与
  `ModelAssociationValidation` 一致），再按 ModelID 查重（错误包
  `failed to check model existence: %w`，命中抛
  `xerrors.DuplicateNameError("model", input.ModelID)` → `model name
  '<modelId>' already exists` + resource/field/value 扩展），最后 SetInput 与
  可选 Remark 写入，失败包 `failed to create model: %w`。ent 默认：type
  chat、status disabled，model_card / settings 的空串由存储层替换为完整默认
  JSON（Go 字段序），created_at / updated_at 同秒。
- BulkCreateModels：逐条校验设置；重复键 `developer:modelID` 报
  `duplicate model in input: developer '%s' and modelId '%s'`；再以 OR-of-AND
  （developer, model_id）查询已存在行（空输入生成恒假 OR），命中报
  `models already exist: %v`（Go slice 的 `[a:b c:d]` 格式）；无命中则单事务
  CreateBulk。
- UpdateModel：Settings 非 nil 时先校验（错误不包前缀）；SetNillable 仅写提供
  的字段，modelCard / settings / remark 非 nil 才写，clearRemark 最后执行并
  覆盖 SetRemark；UpdateOneID 无 soft-delete 谓词，失败包
  `failed to update model: %w`，缺失行即
  `failed to update model: ent: model not found`。
- UpdateModelStatus / DeleteModel / 四个批量方法：前缀分别为
  `failed to update model status: %w`、`failed to delete model: %w`、
  `failed to bulk archive/disable/enable/delete models: %w`。删除写
  deleted_at（Unix 秒），批量状态只写 status 与 updated_at，空 ID 列表为
  no-op，批量删除带 `deleted_at = 0` 谓词。
- ListModels：statusIn 非空按集合过滤，否则默认仅 enabled；输出
  `ModelIdentityWithStatus{ID: modelID, Status}`。
- QueryModels：系统设置 `QueryAllChannelModels` 或输入
  `includeAllChannelModels` 为真时走通道派生模型（request model 去重、状态恒
  enabled），否则走 ListModels；结果按模型 id 稳定排序（C177）。
- QueryModelChannelConnections / QueryUnassociatedChannels：取 enabled/disabled
  通道（前者按 orderingWeight 降序），匹配六类关联规则（channel_model /
  channel_regex / regex / model / channel_tags_model / channel_tags_regex，
  共享或内部去重键），未被任何规则覆盖的通道报出其模型；模型条目按 id 排序
  （C177）。
- CountModelAssociatedChannels：系统设置与模型设置继承后的关联规则按通道去重
  计数；无规则或没有启停通道为 0。
- fetchModels：复用 §77 的 ModelFetcher，要求 write_channels 作用域，失败包
  `failed to fetch models: %w`。
- Model 输出：`id` / `createdAt` / `updatedAt` / `developer` / `modelID` /
  `type` / `name` / `icon` / `group` / `status` / `remark` / `modelCard` /
  `settings` / `associatedChannelCount`；modelCard 与 settings 为 jsonb 解析后
  的 Go 形状（cost 为数字、空 reasoningEfforts 为 null），列为 NULL / JSON
  null 时非空字段以 gqlgen 文案 `the requested element is null which the
  schema does not allow` 失败；枚举以存储字符串输出（C177）。
- models 连接：CREATED_AT 排序改写为 id 并保留方向，UPDATED_AT / NAME 走对应
  列；游标只在 UPDATED_AT / NAME 携带值；无分页参数校验（与参照 entgql
  一致）；node(Model) 缺失报 `ent: model not found`，隐私拒绝为
  `ent/privacy: deny rule`。
- ModelPrivacy：显式评估参照 ent Policy（查询：API key read_channels 作用域
  → owner → read_channels 系统作用域；变更：owner → write_channels 系统
  作用域），查询拒绝为 `ent/privacy: deny rule`，变更拒绝由 GraphQL 过滤器
  折叠为 `permission denied` / FORBIDDEN；测试主体跳过。参照 ModelService
  无读取缓存，端口同样直达数据库。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 服务语义：创建默认值与重名回滚、bulk 重复键与 Go slice 文案、更新 / 状态 / 删除前缀、批量状态、隐私拒绝、queryModels 分支与状态过滤、关联计数与未关联通道 | `tests/AxonHub.Api.Tests/ModelManagementServiceTests.cs` | 18（新增） |
| 存储语义：ent 默认值、全列往返、软删可见性、谓词与空集合、键集分页、SET 顺序与 ClearRemark、批量状态与软删 | `tests/AxonHub.Persistence.Tests/ModelStoreTests.cs` | 7（新增） |
| 端点线形：models 连接形状与 modelCard / settings JSON、where or 与空 or、createModel 默认值、updateModel clearRemark 与缺失文案、状态与批量变更、deleteModel、node(Model) | `tests/AxonHub.Api.Tests/AdminModelsEndpointTests.cs` | 7（新增） |

`AxonHub.Persistence.Tests` 由 200 增至 207（+7），`AxonHub.Api.Tests` 由
2177 增至 2202（+25）；四个项目合计 5489 增至 5521，0 失败 0 跳过。

差异：新增 C177（模型管理切片的已接受差异与真实缺口）；C165 更新
（node(Model) 已接入，其余 ent 类型仍缺）；C088 收窄为公开模型列表的
configured-model 合并 / profile 过滤；C142（事务边界）、C135（where 子集）
沿用。

## 89. 公开模型列表切片（ListEnabledModels 合并 / profile / include / 403，45 例，2026-11-05）

参照 `internal/server/biz/model.go` 的 ListEnabledModels（646-755）与
queryConfiguredModelFacades（744-799）、`model_routability.go` 的
hasCapableEndpointForModel（13-38）、`model_settings_inheritance.go` 的
EffectiveModelAssociations（129-146）、`internal/server/api/openai.go` 的
parseOpenAIModelInclude（620-655）、convertModelFacadeToOpenAIModel /
convertModelToOpenAIExtended（657-744）、ListModels 与 RetrieveModel
（792-...）、`anthropic.go` 的 ListModels（85-138）与 `gemini.go` 的
ListModels（141-...）；端口新增 `ModelService.ListEnabledModelsAsync` /
`QueryConfiguredModelFacadesAsync` / `HasCapableEndpointForModel`、
`ModelsEndpoint` 的 include 解析与全量数据查询、`GeminiEndpoint` 的 403
分支、`RequestIdentity.ApiKey` / `Project` 与 `ApiKeyAuthenticator` 的项目边
加载、`IManagementIdentity.GetCurrentProject`、`ChannelTagsCodec`，以及
`ModelListFilter.ModelIdIn`，对齐三个公开列表端点的可观察行为：

- ListEnabledModels：先判定 read_channels 作用域（拒绝抛
  `failed to list configured models: ent/privacy: deny rule`），再取启用通道；
  API key 身份下依次按 Project 的激活 profile（ChannelIDs → ChannelTags）与
  key 的激活 profile（同序）收窄通道，profile 的 ModelIDs 作为
  allowedModelIDs。配置模型查询为 `status = enabled`（allowedModelIDs 非空时
  加 `model_id IN (...)`）；每行按 ent 行扫描语义解码 settings / model_card
  （畸形 JSON 整体失败，文案 `unmarshal field settings|model_card: ...` 外包
  `failed to list configured models: `），再取设置继承后的关联并匹配通道，
  无连接的行跳过；`HideUnroutableModelsInList` 开启时，端点能力判定不通过的
  行进入 suppressedIDs 并从列表隐藏。
- `QueryAllChannelModels=false` 时只返回配置模型 facade；为 true（默认）时
  配置模型在前，通道派生项追加在后：已在 modelSet 的 ID 跳过（configured
  与 suppressed 都预置），`ModelBlacklistRegex` 只过滤通道派生项并把判定缓存
  进 modelSet；每个通道的模型条目按 request model 字典序稳定输出（C178）。
  最后再按 allowedModelIDs 过滤一次。facade 的 DisplayName 等于 ID、Created
  为通道/模型行的 Unix 秒、OwnedBy 为通道类型字符串或 `configured`。
- 端点能力：`RequestTypeForModelType` → `CapableApiFormats`，空集合视为
  未知类型放行；连接通道的 `ChannelEndpoints.Resolve` 合并类型默认端点与
  用户覆盖，命中任一允许格式即可路由；忽略 When 条件、stream 策略与配额。
- OpenAI GET /v1/models：include 默认值取 `DefaultModelApiIncludeAll`；空串
  返回（nil, 默认），`all` 全量，逗号分隔字段去空白；needFullData 仅当命中
  name / description / context_length / max_output_tokens / modalities /
  capabilities / pricing / icon / type。可见模型为空时直接返回空 data 数组。
  全量分支以 `status = enabled` + `model_id IN (visibleIDs)` 查询并按 model id
  映射（重复 id 后者胜出），缺失行回退 facade；扩展字段的 name / icon /
  type / description 取模型行，modalities / capabilities / context_length /
  max_output_tokens / pricing 取 model_card，空字符串与零值按 omitempty
  省略，pricing 固定 `per_1m_tokens` / `USD`。
- OpenAI GET /v1/models/{model}：include 默认值硬编码 false；先找可见
  facade，缺失报 `model_not_found`；needFullData 时按 `model_id` + enabled
  做 ent Only（0 行回退 facade，多行报 `ent: model not singular`）。
- Anthropic GET /anthropic/v1/models：列表来自同一 ListEnabledModels，
  `first_id` / `last_id` 取首尾 ID（空列表为 `""`），`has_more` 恒 false。
- 403 分支：ListEnabledModels 的错误经 `PrivacyDenyException.IsDeny` 识别，
  OpenAI 返回 403 `permission_denied` / `authentication_error`（无 request_id
  时省略），Anthropic 返回 403 `permission_error` 且 request_id 恒写出，
  Gemini 返回 403 code 403 / `permission_error`；其余错误保持 500。
- 身份：API key 中间件把 key 与其 Project 边写入 RequestIdentity，
  HttpContextManagementIdentity 在管理面身份缺席时才读取它，使 /v1 与
  GraphQL 两条路径都能拿到 contexts.GetAPIKey 等价物（C178）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 服务语义：默认 9 个模型的合并集合与 owned_by、配置优先、profile ModelIDs / ChannelIDs / ChannelTags 过滤（含项目与 key 顺序交集）、黑名单正则 / 空串 / 配置豁免、QueryAll=false 两分支、HideUnroutable 抑制与不复活、能力判定忽略 When、privacy 拒绝与存储 / 畸形行错误包装、空通道 | `tests/AxonHub.Api.Tests/ModelServiceTests.cs` | 38（其中 31 例新增） |
| 端点线形：facade 形状、include all / 单字段 / 系统默认、configured 行回退、全量查询失败、not singular、OpenAI 与 Anthropic 的 403 信封、HTML 转义与请求 id | `tests/AxonHub.Api.Tests/ModelsEndpointTests.cs` | 27（其中 13 例新增） |
| Gemini 列表形状、内部错误与 403 permission_error | `tests/AxonHub.Api.Tests/GeminiEndpointTests.cs` | 7（其中 1 例新增） |
| 存储：ModelIdIn 集合、status + ModelIdIn 组合、空集合恒假 | `tests/AxonHub.Persistence.Tests/ModelStoreTests.cs` | 7（断言增补） |

`AxonHub.Api.Tests` 由 2202 增至 2247（+45），`AxonHub.Persistence.Tests` 保持
207（ModelStoreTests 只增补断言），Llm 3020、Grains 92；四个项目合计由 5521
增至 5566，0 失败 0 跳过。

差异：新增 C178（公开模型列表切片的已接受差异与真实缺口）；C088 标记
resolved；C140（并列顺序）与 C149（实时读取通道）沿用。

## 90. orchestrator 模型关联路由与多模型重试切片（DefaultSelector / 条件 / 聚合 / 重试，59 例，2026-11-05）

参照 `internal/server/orchestrator/candidates.go` 的 DefaultSelector.Select（110-127）、
selectChannelCadidates（130-165）与 selectModelCandidates（167-243）、resolveAssociations
的匹配半程（250-360）、aggregateChannelModelCandidates（551-607）、LoadBalancedSelector 的
sortCandidates（909-986）与 SpecifiedChannelSelector（1042-1078）、`candidates_condition.go`
的 filterResolvedCandidatesForRequest（19-71）与 populateAPIFormat（73-126）、
`model_settings_inheritance.go` 的 EffectiveModelAssociations（129-146）、`transform_options.go`
的 applyModelDefaultMaxTokens（46-70）、`outbound.go` 的 refreshCandidateAPIFormat（468-498）、
CanRetry（766-818）与 NextChannel / PrepareForRetry（823-856）、`select_candidates.go`
的选候选中间件（18-130）与 `tester.go` 的排序接线（240-330）；端口新增
`ModelCandidateSelector` / `CandidateSelection` / `ModelRoutingPolicy`、`ModelAssociationMatcher`
（管理面共用）、`AssociationConditionEvaluator`、`CandidateRequestFeatures`、
`ChannelEndpointSelection` 的显式端点规则、`PersistentOutboundTransformer` 的多模型重试状态，
并把 `IChannelPoolGrain.SelectAsync` 重构为按优先级分组排序的 `SortAsync`，对齐可观察行为：

- DefaultSelector：模型行按 `model_id` + `status = enabled` 查询；查询前先判定
  read_channels 作用域（拒绝保持 `PrivacyDenyException`，文案
  `failed to query AxonHub Model: ent/privacy: deny rule`），其它查询错误包
  `failed to query AxonHub Model: `；0 行抛 not-found。存在模型行但设置继承后关联为 0 时
  返回空列表且**不**回退通道池。not-found 且 `FallbackToChannelsOnModelNotFound` 时走
  `selectChannelCadidates`（按 request.Model 精确匹配通道条目、强制协议覆盖、显式端点跳过、
  priority 0、无 routing policy、DefaultMaxTokens 0），否则抛
  `model not found: "<m>"`（InvalidModel；在 catch 内抛出，不再被外层包装）。
- 行解码：先 model_card 再 settings，畸形 JSON 的文案
  `failed to query AxonHub Model: unmarshal field model_card|settings: ...`；model_card 优先。
- 关联匹配：六种 kind（channel_model / channel_regex / regex / model / channel_tags_model /
  channel_tags_regex）与通道名正则 / 通道 id / 标签排除；停用关联跳过；同一关联内按
  (channel, actualModel) 去重；通道列表读取失败包 `failed to resolve associations: `。
- When 条件：null / !Enabled / Condition=null 视为通过；request_format、reasoning_effort、
  stream、prompt_tokens、has_image/video/document/audio、daily_time、request_header.*；
  条件在聚合前按候选逐个判定；expr 运行时错误（未知运算符、类型不匹配）按 false 处理。
- 聚合：按 (channelId, priority) 合并连接，全局 (channelId, actualModel) 去重，空候选丢弃，
  候选保持首次出现顺序；随后逐模型应用强制 API 格式并选择端点，显式端点类型
  （alpha_search / decisions / systemone）在无匹配格式时丢弃条目、全丢则丢弃候选；
  `ModelApiFormats` 与首个 `ApiFormat` 一并写出。
- 模型级策略：实体设置存在时取 LoadBalancerStrategy / TraceStickyMode（"" / "system_default"
  → "default"），模型卡输出上限作为 DefaultMaxTokens 写入每个候选。
- 多模型重试：CanRetry 先看候选剩余模型（本地队列 / RPM 拒绝、429 与空响应哨兵除外），
  PrepareForRetry 推进模型号并按 ModelApiFormats / 通道端点刷新 ApiFormat、重建变换器并记账，
  NextChannel 把模型号归零并记录新选择，ApplyModelDefaultMaxTokens 只在 Anthropic 出站、
  客户端未设上限且 TransformOptions 未显式设置时浅拷贝请求。
- 负载均衡：sortCandidates 先按关联 priority 分组、组内按策略评分排序、按 requiredCount
  截断后只记账首个候选；单候选短路但仍记账；评分纯函数抽到 `LoadBalancerScoring` 并由
  grain 测试直接锁定（strategy 选择来自模型级 routing policy）。
- tester 路径：ProcessSpecifiedAsync 仍把单候选交给 ChannelPoolGrain.SortAsync
  （trackSelection），与 tester.go 安装同一组负载均衡器、单候选仍记账一致。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 选择器语义：模型行查询与 privacy、无关联不回退、六种关联 kind / 排除规则 / 停用、When 条件、聚合、逐模型 API 格式与显式端点、routing policy 与 DefaultMaxTokens、错误包装 | `tests/AxonHub.Api.Tests/ModelCandidateSelectorTests.cs` | 50 |
| 多模型重试与 DefaultMaxTokens：CanRetry 顺序、429、PrepareForRetry 推进 / 刷新 / 记账、NextChannel 归零、变换器重建、浅拷贝 | `tests/AxonHub.Api.Tests/PersistentOutboundRetryTests.cs` | 9 |
| 负载均衡排序：requiredCount 截断、priority 分组、单候选记账、策略评分纯函数 | `tests/AxonHub.Grains.Tests/ChannelSelectionTests.cs` | 30（其中 2 例新增） |

`AxonHub.Api.Tests` 由 2247 增至 2306（+59），`AxonHub.Grains.Tests` 由 92 增至 94
（+2，SortAsync 重构与评分纯函数），Llm 3020、Persistence 207；四个项目合计由 5566
增至 5627，0 失败 0 跳过。

差异：新增 C179（模型关联路由切片的已接受差异与真实缺口）与 C180（四个未移植的通道
变换选项 / 流策略强制，真实缺口）；C149（实时读取）、C177（连接内模型顺序）与 C012
残余（延迟 / 熔断中性分）沿用。

## 91. trace / thread 通道粘性路由切片（prefer_previous_channel 与 previous-channel 缓存，16 例，2026-11-05）

参照 `internal/server/orchestrator/candidates.go` 的 LoadBalancedSelector 粘性半程（738-905：
resolveStickyChannelID / extractStickyCandidate / sortCandidates 的 track 分支）、
`internal/server/biz/request.go` 的 UpdateRequestChannelID（1353-1366）与
cachePreviousChannelForRequest（1733-1801）、`retry.go` 的 deriveRoutingPolicy 中
TraceStickyMode 的优先级、`system.go` 的归一化与 `system_default.go` 的默认值；
端口新增 `PreviousChannelCache` / `IPreviousChannelProvider`、`RequestIdentity.ThreadId` /
`TraceThreadId`、`ChannelCandidate.TraceSticky`，把 `ChatCompletionOrchestrator` 的候选排序
拆成 `SortCandidatesAsync`，对齐可观察行为：

- 生效策略：TraceStickyMode 按 profile → model routing policy → RetryPolicy 逐级覆盖；
  `default` / `system_default` 与非法值视为未设置，继续沿用下层；RetryPolicy 缺省为
  `prefer_previous_channel`。
- 粘性解析：仅当生效 mode 恰为 `prefer_previous_channel` 时解析；先查 trace 作用域，
  未命中再查 thread 作用域（当前请求 thread（ThreadMiddleware 发布）优先，回退 trace 行的
  `thread_id`）；缓存命中但该通道已不在候选池时忽略，绝不选中不可用通道；`disabled` 时
  完全走普通优先级排序。
- 粘性装配：取该通道中 priority 最小的候选置于首位并标记 `TraceSticky`，该通道的其余候选
  全部从 fallback 移除；剩余候选按正常策略排序且不记账；最后只对粘性候选记账一次
  （ChannelGrain 的 selection 计数），fallback 不记账；跨通道重试关闭时
  requiredCount = 1，粘性候选没有 fallback。
- 写回：每次尝试切换到新通道（attempt.ChannelId != 0 且与上一次不同）时先持久化
  requests.channel_id，再写 previous-channel 缓存；要求 traceId != 0 且 channelId != 0；
  trace 键总是写，thread 键仅在 trace 行的 thread_id != 0 时写；thread 作用域取 trace 行的
  thread_id（request.go:1768-1774），而不是当前请求的 thread 头；TTL 30 分钟；读缓存未命中
  返回 0，不查询历史请求。
- 缓存后端：键形状 `axonhub:routing:previous-channel:v1:trace|thread:<id>` 与参照一致，
  进程内字典实现，多实例不共享（C181）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 粘性选择：trace 覆盖 priority 与 fallback 去重、thread 作用域回退、disabled 顺序、无跨通道重试单候选、trace/thread 写回、trace 行 thread 优先、无 trace 不写、策略优先级 | `tests/AxonHub.Api.Tests/StickyChannelSelectionTests.cs` | 9 |
| previous-channel 缓存：trace/thread 写入、按作用域覆盖、未命中不查历史、零值忽略、null thread 仅 trace、30 分钟边界 | `tests/AxonHub.Api.Tests/PreviousChannelCacheTests.cs` | 6 |
| 跨优先级分组只记账最终首候选 | `tests/AxonHub.Grains.Tests/ChannelSelectionTests.cs` | 1（新增） |

`AxonHub.Api.Tests` 由 2306 增至 2321（+15），`AxonHub.Grains.Tests` 由 94 增至 95（+1），
Llm 3020、Persistence 207；四个项目合计由 5627 增至 5643，0 失败 0 跳过。

差异：新增 C181（previous-channel 缓存为进程内实现，参照经 xcache 可配置 Redis；单实例
行为等价，多实例不共享）；C179 的实时读取与 C012 残余沿用。

## 92. 项目 / API key profile 的候选过滤切片（SelectedChannels / ChannelTags，12 例，2026-11-05）

参照 `internal/server/orchestrator/select_candidates.go` 的选候选中间件前半程（27-51）、
`candidates.go` 的 SelectedChannelsSelector（633-671）与 TagsFilterSelector（988-1024）、
`internal/objects/apikey.go` 的 MatchChannelTags / OrDefault（46-94）与
`ent/extra.go` 的 Project.GetActiveProfile / APIKey.GetActiveProfile（16-42）；
端口新增 `ProfileFilters`（激活 profile 解析 + 候选过滤）与 `ChannelCandidate.Tags`，
在 `ChatCompletionOrchestrator` 里于基础选择器产出之后、粘性 / 负载均衡排序之前应用，
对齐可观察行为：

- 作用顺序：项目 profile（上界）先过滤，API key 自己的激活 profile 再在其中收窄；
  两者都是交集。项目 profile 只在请求带 API key 时生效（参照从 `APIKey.Edges.Project`
  读项目），没有 key 时即使上下文里有 project 也不过滤。
- 空过滤器：`ChannelIDs` 为 nil / 空、`ChannelTags` 为 nil / 空时该维度是 no-op；
  profile 名与 `activeProfile` 不匹配、`activeProfile` 为空时没有激活 profile。
- 标签匹配：`all` 要求通道包含全部允许标签；`none` 要求通道不含任何允许标签；
  `any`、空串、非法值都按 `any`（有交集即匹配）；无标签通道在 allow list 下永不匹配，
  在 `none` 下通过。
- 适用面：过滤包住所有基础选择器，模型关联候选与无模型行的通道回退都受约束；
  tester 的指定通道候选同样过一遍过滤。
- 过滤后为空：抛出 `TransformerException`，文案 `model not found: <model>`（无引号），
  与参照的 `ErrInvalidModel` 一致；不会向上游发请求。
- 复用：`ModelService` 的公开模型列表改用同一组激活 profile 解析函数，行为不变；
  `ChannelCandidate.Tags` 是新增的 Orleans 字段（Id 23），默认 null。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| key profile 的 ChannelIDs 过滤、空过滤器 no-op、项目与 key 交集、无 key 忽略项目、标签 any / 未知模式按 any / all / none、同 profile 内 IDs 与标签交集、无标签不匹配 allow list、legacy 通道回退、tester 指定候选 | `tests/AxonHub.Api.Tests/ProfileFilterSelectionTests.cs` | 12 |

`AxonHub.Api.Tests` 由 2321 增至 2333（+12），Llm 3020、Grains 95、Persistence 207；
四个项目合计由 5643 增至 5655，0 失败 0 跳过。

差异：不新增编号；C179 的真实缺口 (b) 由本节闭合（该条目已就地更新），畸形 profile
JSON 的失败关闭口径沿用 C178。

## 93. 原生工具候选过滤切片（Google / Anthropic，13 例，2026-11-05）

参照 `internal/server/orchestrator/select_candidates.go` 的原生工具装饰器安装位（53-61）、
`candidates_google.go`（12-57）、`candidates_anthropic.go`（13-64）、
`internal/ent/channel/type.go` 的能力判定（29-44）、`llm/tools.go` 的
ContainsGoogleNativeTools / IsGoogleNativeTool（241-252）与
`llm/transformer/anthropic/tools.go` 的 ContainsAnthropicNativeTools /
IsAnthropicNativeTool / FilterOutAnthropicNativeTools（11-56）；
端口新增 `AnthropicToolFilter`、`ChannelTypeCapabilities` 与 `NativeToolFilters`，
在 profile 过滤之后、粘性 / 负载均衡排序之前应用，对齐可观察行为：

- 安装门：Google 过滤只在入站 API 格式为 `gemini/contents` 时安装，Anthropic 过滤只在
  `anthropic/messages` 时安装；其他格式即使带 `google_*` / `web_search` 工具也不过滤。
- 触发条件：Google 侧存在任一 `google_` 前缀工具（`google_search` /
  `google_url_context` / `google_code_execution` 等任意前缀）时触发；Anthropic 侧工具
  `type` 为 `web_search` 或 `web_search_20250305` 时触发，仅函数名为 `web_search` 的
  `function` 工具不算原生。
- 兼容集合：Google 只保留 `gemini` 与 `gemini_vertex`，排除 `gemini_openai` 与
  `zenmux_gemini`；Anthropic 只保留 `anthropic`、`anthropic_aws`、`anthropic_gcp`、
  `claudecode` 与 `deepseek_anthropic`，其余 `*_anthropic` 变体排除。
- 回退：兼容候选为空时返回原候选列表，交给下游回退逻辑，不产生 invalid-model。
- 适用面：普通请求与 tester 的指定通道候选都经过同一过滤；无原生工具或格式不匹配时
  是 no-op。
- 复用：`AnthropicToolFilter.ToolTypeWebSearch20250305` 成为出站请求常量的唯一来源；
  选候选的 debug / warn 日志沿用既有切片口径不移植。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| Anthropic 原生类型判定、包含性与过滤保留 function 列表 | `tests/AxonHub.Llm.Tests/AnthropicNativeToolFilterTests.cs` | 3 |
| Google 兼容集合与多工具、无工具 no-op、无兼容回退、格式门；Anthropic 兼容集合、function 名不算原生、无兼容回退、格式门；orchestrator 只路由兼容通道 | `tests/AxonHub.Api.Tests/NativeToolSelectionTests.cs` | 10 |

`AxonHub.Llm.Tests` 由 3020 增至 3023（+3），`AxonHub.Api.Tests` 由 2333 增至 2343
（+10），Grains 95、Persistence 207；四个项目合计由 5655 增至 5668，0 失败 0 跳过。

差异：不新增编号；C179 的真实缺口 (c) 由本节闭合（该条目已就地更新）。

## 94. 流策略候选过滤切片（StreamPolicySelector，19 例，2026-11-05）

参照 `internal/server/orchestrator/candidates_stream_policy.go`（12-84）与
`errors.go` 的 StreamPolicyConflictError（17-33），以及 `objects.ChannelPolicies.Stream`
（unlimited / require / forbid）；端口新增 `StreamPolicySelector` 与
`StreamPolicyConflictException`、`ChannelCandidate.Policies` 原始 jsonb 字段与
`ChannelDescriptor.Policies` 投影，在原生工具过滤之后、负载均衡排序之前应用，
对齐可观察行为：

- 流式请求（`stream == true`）：剔除 `forbid` 通道，`require` / `unlimited` / 空策略保留。
- 非流式请求（`stream == false` 或 null）：先保留非 `require` 通道；只要存在一个这样的
  通道就返回它（`forbid` 也算可用），不进入回退。
- 仅剩 `require` 通道时：chat（含空 RequestType/APIFormat、OpenAI chat、Responses、
  Anthropic Messages、Gemini contents、Ollama chat）与 completion（OpenAI completion）
  这些可自动聚合的请求保留全部候选；AI SDK text / datastream、embedding、compact 等
  不可聚合请求返回空列表，最终由 selectCandidates 变成 `model not found: <model>`。
- Decisions：仅剩 `require` 通道时抛出 `StreamPolicyConflictException`，文案
  `no eligible channel for model <model>: matching channels require streaming, but Decisions requests are non-streaming`；
  混合候选时保留非 `require` 通道。
- 策略来源：候选携带通道行 `policies` 原始 jsonb（模型关联、无模型行回退与 tester 指定
  通道三条路径一致）；空 / null / 非法 JSON 按 unlimited 处理（沿用 C157 的宽松加载口径）。
- 空候选列表直接返回，不触发冲突。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 流式 forbid 过滤、非流式 require 优先级与自动聚合回退、AI SDK / embedding / compact 空结果、Decisions 冲突与混合候选、nil stream、空策略、空列表、通道策略流入候选、orchestrator 两条接线 | `tests/AxonHub.Api.Tests/StreamPolicySelectionTests.cs` | 19 |

`AxonHub.Api.Tests` 由 2343 增至 2362（+19），Llm 3023、Grains 95、Persistence 207；
四个项目合计由 5668 增至 5687，0 失败 0 跳过。

差异：不新增编号；C179 的真实缺口 (d) 由本节闭合（该条目已就地更新）。

## 95. 配额路由闸门切片（QuotaRoutingGate / ProviderQuotaRouting / 设置映射，43 例，2026-11-05）

参照 `internal/server/orchestrator/candidates_quota.go`（QuotaRoutingGate，14-166）、
`internal/server/biz/provider_quota/routing.go`（EvaluateQuotaRouting 42-118、routingGroups
120-168）、`types.go` 的 RequestModality / IsBalanceLimit / ElapsedRatio、
`internal/server/biz/quota_routing_migration.go`（quotaRoutingModeFromLegacy 128-142）、
`internal/server/biz/system.go` 的 QuotaRoutingSettingsOrDefault（1819-1867）、
`objects.QuotaRoutingMode`、`orchestrator/errors.go` 的 QuotaExhaustedError，以及
`api/chat.go` 的 wrapQuotaExhaustedAsResponseError（855-875）、FormatStreamError 配额分支
（786-795）与 streamErrorStatus；端口新增 `ProviderQuotaStatus.cs`、`QuotaRoutingGate.cs`、
`SystemQuotaRoutingSettingsService.cs`，在 `SortCandidatesAsync` 中解析 sticky 之后、钉住
之前应用，并接入非流 / 流式 / 二进制三处错误映射，对齐可观察行为：

- 路由决策（`EvaluateQuotaRouting`）：整体状态 `exhausted` 直接返回 exhausted；`limits`
  按 `availabilityGroup` 分组（仅当组名来自本请求 modality 的限制时才纳入其它类型），未分组
  的窗口合并为一组、未分组的余额合并为一组；组可用性按组语义（有组名任一未耗尽即可用，
  无组名的窗口组全部未耗尽才可用）；全部组耗尽 → exhausted（空 limitations 时按整体
  状态 available/warning → open，否则 unknown）；窗口全耗尽且余额可用 →
  sticky_only `window_exhausted_balance_fallback`；任一有活动窗口的未耗尽窗口使用率
  超过时间进度 → sticky_only `window_pressure`；否则 open。
- 两阶段过滤：第一阶段保留 keepPhaseOne；第一阶段为空时改用 keepPhaseTwo；`DroppedCount`
  始终为 `原候选数 - 最终候选数`。backpressure 下第一阶段保留 open / unknown 与命中
  sticky id 的 sticky_only，第二阶段仅保留 sticky_only；remove_on_exhausted 保留所有
  非 exhausted；ignore_quota 全部保留。空候选列表与 nil provider 原样返回。
- 模式解析：通道 `settings.quotaRoutingMode` 非空则覆盖系统默认；未知 / inherit 值
  fail-closed 为 remove_on_exhausted；系统默认来自 `quota_routing_settings.defaultMode`
  （空值 → remove_on_exhausted），新键缺失时即时映射 legacy
  `quota_enforcement_settings`（`!enabled` → ignore_quota；exhaustedOnly 或
  `EXHAUSTED_ONLY`/`exhausted_only` → remove_on_exhausted；dePrioritize 或
  `DE_PRIORITIZE`/`de_prioritize` → backpressure；其余 remove_on_exhausted），任何读取 /
  解码 / 非法模式错误都回退默认设置。
- 请求 modality：`request.image != nil` 使用 `image` 限制，否则 `token`；无配额状态的
  通道在两个阶段都保留。
- 错误面：最终候选为空且 `DroppedCount > 0` → QuotaExhaustedException
  “all channels quota exhausted for model <model>”（否则仍是 `model not found: <model>`）；
  非流映射为 503 ResponseError，body 为
  `{"error":{"code":"quota_exhausted","message":...,"type":"quota_exhausted"}}`；流式
  FormatStreamError 返回同形状 envelope 且不带外层 request_id；二进制 writer 的状态码为
  503。
- 接缝：生产装配暂不注册 `IProviderQuotaStatusProvider`（null），对应参照中通道尚无配额
  状态的 nil 指针分支；tester 路径沿用参照的无配额 provider 选择器，不经过闸门。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 路由决策表 16 条、IsBalanceLimit / ElapsedRatio、两阶段过滤与 dropped count、通道覆盖与 fail-closed、modality、nil provider / 空列表、设置与 legacy 映射及回退、orchestrator 的 QuotaExhausted / sticky 钉住与放弃 / 并发独立 dropped count / invalid-model、非流 503 信封、流式与二进制配额 envelope | `tests/AxonHub.Api.Tests/QuotaRoutingSelectionTests.cs` | 43 |

`AxonHub.Api.Tests` 由 2362 增至 2405（+43），Llm 3023、Grains 95、Persistence 207；
四个项目合计由 5687 增至 5730，0 失败 0 跳过。

差异：供应商配额采集子系统（checkers / ProviderQuotaService / provider_quota_status /
GraphQL 配额面）未移植，登记为 C182（真实缺口）；启动数据迁移（legacy 设置复制 /
allowedChannelIDs 改写 / 迁移标记）未执行，登记为 C183。C179 的真实缺口 (e) 由本节闭合
（该条目已就地更新）。

## 96. 通道变换选项与流策略强制（applyTransformOptions / reasoning effort / custom tool 过滤 / force streaming，43 + 9 例，2026-11-05）

参照 `internal/server/orchestrator/transform_options.go` 的 applyTransformOptions（16-44）、
applyReasoningEffortMapping（76-104）与 replaceDeveloperRoleWithSystem（107-129），
`internal/server/orchestrator/outbound.go` 的 TransformRequest 请求整形（552-583）、
filterResponseCustomToolMessagesForNonResponsesOutbound（597-613，含 isResponsesFormat 与
containsResponseCustomToolMessages）与 shouldForceStreamingForCandidate（434-450），
以及 `llm/transformer/shared/messages.go`
的 FilterOutResponseCustomToolMessages / shouldDropMessageAfterToolFiltering。端口新增
`ChannelTransformOptions.cs`、`AxonHub.Llm/Transformer/Shared/Messages.cs`，在
`PersistentOutboundTransformer.TransformRequest` 的 provider 变换之前按参照顺序应用，
对齐可观察行为：

- 变换选项：三项开关全关时原样返回；ForceArrayInstructions / ForceArrayInputs 写入
  TransformOptions 的独立副本（Go 的内联结构体复制语义），保留既有 DefaultMaxTokens；
  ReplaceDeveloperRoleWithSystem 按 OrdinalIgnoreCase 匹配 developer 并把匹配消息复制后
  改为 system，未命中时返回原列表。
- reasoning effort 映射：空映射 / 空 effort / 未命中时原样返回；首个 From 命中项生效；
  命中时浅拷贝请求，若 TransformerMetadata 含 Anthropic output_config.effort 标记则先复制
  字典再写入映射值，原请求的元数据不变。
- custom tool 过滤：仅当入站为 Responses / ResponsesCompact 且出站不是这两种格式且消息含
  Responses custom tool 调用时，浅拷贝请求并过滤：移除 custom tool 调用、记录其 Id 与
  CallId、移除对应 tool 结果消息；失去全部 tool call 且无内容 / 拒绝 / 推理 / 音频的消息
  整条丢弃。
- Anthropic 流意图归一化：出站为 anthropic/messages 且 Stream 为 nil 时置为 false，使
  provider 体显式携带 `"stream": false`（该步骤位于同一函数，原 C180 描述未单列）。
- 强制流式：通道 stream 策略为 require、客户端未要求流式、且请求类型支持自动聚合时，
  置 Stream=true 与 StreamOptions.IncludeUsage=true；同样的置位同步到 state 请求
  （CurrentLlmRequest），供 pipeline 走聚合路径；OriginalRequestStream 仍保留客户端原始
  意图供 pass-through 比较。
- 通道 settings 按原始 jsonb 读取；非法 JSON 视为未配置（与 C157 的宽松读取一致）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 变换选项（替换 / 数组化 / 保留既有值 / 不修改原请求）、reasoning 映射与元数据同步、Responses custom tool 过滤、Anthropic 显式 `stream:false`、require-streaming 强制与 state 同步、默认配置原样透传 | `tests/AxonHub.Api.Tests/ChannelTransformOptionsTests.cs` | 43 |
| 共享消息过滤：custom tool 调用 / 结果移除、空消息丢弃、内容与推理等载荷保留、CallId 去重 | `tests/AxonHub.Llm.Tests/SharedMessageFilterTests.cs` | 9 |

`AxonHub.Api.Tests` 由 2405 增至 2448（+43），Llm 由 3023 增至 3032（+9），Grains 95、
Persistence 207；四个项目合计由 5730 增至 5782，0 失败 0 跳过。

差异：C180 由本节闭合（该条目已就地更新为 accepted）；无新增差异。

## 97. API key profile 模型映射（applyModelMapping / 响应模型还原，8 例，2026-11-05）

参照 `internal/server/orchestrator/model_mapper.go` 的 applyModelMapping 中间件（18-95）、
ModelMapper（97-171），以及 `orchestrator.go:250-255` 的注册位置（在入站配额 /
reasoning effort / model access 检查之后，候选选择与提示词注入之前）。端口新增
`ModelMapping.cs`（ModelMapper 与 ApiKeyModelMappingMiddleware），在 ProcessAsync /
ProcessSpecifiedAsync 选择候选之前应用，并把同一实例注册进 pipeline 的入站链，使
pipeline 的第二次入站变换走参照的「恢复 OriginalModel」分支：

- 空模型：抛 `model not found: request model is empty`（biz.ErrInvalidModel 包装），
  发生在任何选择或提示词处理之前，且不初始化共享状态。
- 映射：仅在 API key 存在且 profiles 有命中的 active profile 时生效；首个 `from`
  命中的映射生效，匹配沿用 XRegexp（无元字符按精确比较、`*` 全匹配、其余按锚定
  正则、非法正则恒不匹配）；nil key / 无 profiles / 空或未知 active profile /
  无命中均返回原模型。
- 共享状态：第一次入站调用保存客户端模型到 RequestModel、映射后的路由模型到
  OriginalModel；再次调用时恢复 OriginalModel 而不是对已映射模型二次映射（与参照
  「入站不应调用两次，以防万一恢复」的语义一致）。
- 响应还原：非空且与客户端模型不同的响应 model 改回客户端模型；空 model 保持为空；
  LLM 流逐条映射，RequestModel 为空时原样返回流。
- 接线：映射先于流策略 / 原生工具 / profile 过滤与配额闸门；`.RequestModel` 覆盖
  模板渲染路由别名，provider body 仍写通道 actual model；tester 的
  SpecifiedChannelSelector 同样看到映射后的模型。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| MapModel 的 8 种输入、XRegexp 匹配语义（7 子例）、共享状态（8 子例）、空模型拒绝、响应与流还原、pipeline 条件覆盖 parity、tester 前置映射 | `tests/AxonHub.Api.Tests/ModelMappingTests.cs` | 8 |

`AxonHub.Api.Tests` 由 2448 增至 2456（+8），Llm 3032、Grains 95、Persistence 207；
四个项目合计由 5782 增至 5790，0 失败 0 跳过。

差异：新增 C184（API key profile 模型映射，已由本节闭合）与 C185（orchestrator 其余
入站中间件的真实缺口：enforceQuota / applyAutoReasoningEffort /
checkApiKeyModelAccess / Claude Code 计费消息）。

## 98. orchestrator 其余入站中间件（enforceQuota / auto reasoning / model access / 计费消息，2026-11-05）

参照 `internal/server/orchestrator/orchestrator.go:241-312` 的入站注册顺序（全局中间件 →
enforceQuota → applyAutoReasoningEffort → checkApiKeyModelAccess → applyModelMapping →
selectCandidates → injectPrompts → protectPrompts → 响应/流透传 → persistRequest），§97 已闭合
映射，本节闭合其余四项：

- enforceQuota（`biz/quota.go`、`orchestrator/quota.go:14-66`）：active profile 带 quota 时按
  requests / total_tokens / cost 聚合 `usage_logs`。窗口：all_time（Start 为空、End=now、闭区间）；
  past_duration（now−d、闭区间，minute/hour/day，int64 纳秒按假想溢出回绕）；calendar_duration
  （系统时区本地零点、End 开区间，day/month；DST 春季缺口按分钟前移，歧义零点取转换前偏移）。
  聚合 `COALESCE(SUM(...), 0)`，成本以 float 扫描后按 `decimal.NewFromFloat` 的最短往返表示转
  decimal。超限抛 403 `quota_exceeded_error` 信封（code=`quota_exceeded`、message 为
  `requests quota exceeded: n/n` / `total_tokens quota exceeded: n/n` /
  `cost quota exceeded: x/x`、request_id 取身份且为空时省略）。拒绝发生在 persistRequest 之前：
  不产生请求 / 执行 / 用量行，也不调用上游。
- applyAutoReasoningEffort（`auto_reasoning_effort.go`）：设置开启时拆分末尾
  `-max|-xhigh|-high|-medium|-low`（大小写不敏感；最后一个 `-` 必须在 0 之后且不在末尾），
  写入 ReasoningEffort 并覆盖显式值；Qwen max 模型（Trim/ToLower 后以 `-max` 结尾且最后一个
  `/` 之后以 `qwen` 开头）豁免。
- checkApiKeyModelAccess（`model_access.go`）：null key / 无 profiles / 无 active profile /
  空 ModelIDs 放行；否则按 ordinal 精确匹配 active profile 的 ModelIDs，拒绝抛 invalid-model
  （`model not found: <model>`）；空模型先报 `model not found: request model is empty`。
- billingSystemMessageMiddleware（`claude_code.go:30-43`、`ClaudeCodeUtils`）：仅候选
  type == `claudecode` 且凭据为 OAuth 时保留 `x-anthropic-billing-header` 系统消息；其余候选
  删除（含多段 content 中的计费段），作用于出站副本，共享请求不被修改。

接线：`ProcessAsync` 在入站变换之后、候选选择之前依次执行配额 → 读取模型设置 → auto effort →
model access → model mapping；前三项的异常直接冒出（HTTP 层按既有错误信封渲染），映射后的模型
供选择器与覆盖模板使用。`AutoReasoningEffortMiddleware` 同时注册在 pipeline 的 `modelMapping`
之前，pipeline 的第二次入站变换因此幂等。`ProcessSpecifiedAsync`（tester）执行 auto / access /
mapping 而不执行配额：参照 tester 不设置 QuotaService 且身份不带 API key，两者可观察等价。
计费消息判定位于 `PersistentOutboundTransformer.TransformRequest` 的
`ApplyModelDefaultMaxTokens` 之后。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 三类配额窗口、校验文案、聚合与 COALESCE、时区解析与回退、GeneralSettings 解码、GoFloatDecimal | `tests/AxonHub.Api.Tests/ApiKeyQuotaTests.cs` | 28 |
| split 的 8 种输入与补充拼写、Qwen 判定、中间件 6 例、model access 8 例、顺序、pipeline 改写、拒绝不落库、403 信封、分钟配额首放次拒 | `tests/AxonHub.Api.Tests/InboundMiddlewareTests.cs` | 15 |
| 非 claudecode 删除 / OAuth 保留 / apiKey 删除 / 无计费消息原样 / 分片删除 | `tests/AxonHub.Api.Tests/BillingSystemMessageTests.cs` | 5 |
| CountByApiKey 窗口谓词、Aggregate 的 COALESCE 与列裁剪、空结果 | `tests/AxonHub.Persistence.Tests/UsageLogStoreTests.cs` | 4 |

`AxonHub.Api.Tests` 由 2456 增至 2504（+48），`AxonHub.Persistence.Tests` 由 207 增至 211（+4），
Llm 3032、Grains 95；四个项目合计由 5790 增至 5842，0 失败 0 跳过。

差异：C185 由本节闭合（resolved）；C096 第 (4) 项（计费消息中间件未接线）随之关闭，其余残余
缺口不变；C186 登记配额时区按请求读取与参照进程内缓存的差异（accepted）。

## 99. 项目提示词注入（injectPrompts / decisions 提示词，63 + 6 例，2026-11-05）

参照 `internal/server/orchestrator/prompt.go`（injectPrompts 37-87、injectDecisionsPrompts
89-146、promptScopeMatches 148-150）、`internal/server/biz/prompt_matcher.go`、
`internal/server/biz/prompt.go` 的启用列表、`internal/objects/prompt.go` 的设置模型、
`internal/ent/schema/prompt.go` 与 `internal/ent/migrate/schema.go:425-465`（prompts 表、
两个索引、无外键）、`internal/pkg/xregexp/match.go`。端口新增 `PromptRecord`、`prompts` DDL
与 EF 映射、`PromptStore`（EF 写 / Dapper 读）、`PromptModels`（设置 JSON 解码）、
`PromptService` / `IPromptProvider`、`PromptMatcher`、`InjectPromptsMiddleware` /
`DecisionsPrompts`、`DecisionsRawJson` 与 `RawJsonStringSet`，并接入
`ChatCompletionOrchestrator`：

- 门控：上下文无项目（端口用 ProjectId<=0）跳过；provider 读取失败记录并跳过（fail open）；
  启用列表为空或无匹配提示词跳过。tester 路径不提供 provider，与参照的
  `stubPromptProvider{}` 空列表可观察等价。
- 匹配：conditions 为 composite 的 AND，composite 为 condition 的 OR；null/空 conditions
  匹配全部；composite 元素为 null（Go 零值）同样匹配；model_id 需非 nil 且 ordinal 精确
  相等；model_pattern 需非空并经 XRegexp（`*` 全匹配、无元字符精确比较、其余锚定正则、
  非法正则恒不匹配）；api_key 需非 nil 且等于调用方 key id；未知或缺失类型不匹配。
- 应用：按 Order 升序、CreatedAt 稳定次级排序；action.type == `append` 追加到 messages
  末尾，其余（prepend、空、未知）前插；消息 role / content 取自提示词。
- API 格式分派：`openai/decisions` 走原始 body 拼接，其余走统一 messages。
- decisions 字符串 input：不查询角色作用域，逐条前插 / 追加并以 `\n` 连接，经 sjson 语义
  替换顶层 `input` 值（字节保持）。
- decisions 数组 input：`input.#` 取元素数（非数组为 0）；缺省 role 视为 user；遍历
  `input.i.content.#`，首个 `type` 大小写不敏感等于 `input_text` 的分片被改写（promptRole
  为空、等于 inputRole、或为 system 时参与），替换 `input.i.content.j.text` 后立即返回，
  后续分片与消息不变。
- 设置解码：键大小写不敏感、未知键忽略、缺失 / 显式 null 取零值、`conditions: [null]`
  解码为匹配全部的零 composite；设置 JSON 畸形使 provider 读取失败，注入随即 fail open。
- 接线：pipeline 中注入中间件注册在 ProtectPrompts 之前（orchestrator.go:244-245），保护
  阶段因此能看到注入后的提示词；`Program.cs` 注册 `IPromptStore` / `IPromptProvider`；
  EF 写入失败后 detach，避免同一 scope 的后续写入重试失败行（Ent 客户端没有失败实体状态）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 匹配条件 16 例（null / 空、OR / AND、未知类型、空值）、零 composite 与 `[null]`、MatchPrompt、过滤顺序、ApplyPrompts 6 类与应用排序 | `tests/AxonHub.Api.Tests/PromptInjectionTests.cs`（PromptMatcherTests） | 27 |
| 注入中间件：空列表、匹配注入、模型条件 3 例、前插 / 追加、api key、decisions 字符串与数组改写、角色作用域、首个 input_text 短路、大小写与缺省 role、不可改写输入 3 例、空 body、非 decisions、scope 6 例 | 同上（InjectPromptsMiddlewareTests） | 24 |
| 项目门控、失败开放与 provider 入参 | 同上（LoadPromptsTests） | 4 |
| 记录到设置映射、空 / null 设置 3 例、大小写键、`[null]` 条件、非法 JSON、存储失败 | 同上（PromptServiceTests） | 8 |
| prompts 表往返、project / status / 软删过滤、空结果、跨项目同名、软删名复用、jsonb 形状 | `tests/AxonHub.Persistence.Tests/PromptStoreTests.cs` | 6 |
| schema 快照（新增 prompts 表与两个索引）与 EF 模型对齐 | `tests/AxonHub.Persistence.Tests/SchemaTests.cs`、`EfCoreModelTests.cs` | 既有用例扩表（19→20 表、45→47 索引） |

`AxonHub.Api.Tests` 由 2504 增至 2567（+63），`AxonHub.Persistence.Tests` 由 211 增至 217
（+6），Llm 3032、Grains 95；四个项目合计由 5842 增至 5911，0 失败 0 跳过。

差异：C187 登记每请求读取与参照每项目一分钟缓存的差异（accepted）；C188 登记提示词管理面
（GraphQL CRUD / 设置校验 / 缓存失效）未移植（gap），随后由 §100 闭合。

## 100. 项目提示词管理面（prompts / 七个变更 / 设置校验，37 + 7 例，2026-11-05）

参照 `internal/server/gql/prompt.graphql`、`prompt.resolvers.go`、
`ent.resolvers.go:465-478`（Prompts）、`internal/server/gql/gql_pagination.go`
（DefaultPromptOrder / 排序重写）、`internal/server/biz/prompt.go`
（CreatePrompt 135-181、UpdatePrompt 183-260、DeletePrompt 262-288、UpdatePromptStatus
290-321、Bulk* 323-384、ValidatePromptSettings 96-133）、
`internal/ent/schema/prompt.go:93-106`、`internal/scopes/rule_*.go`、
`internal/objects/prompt.go` 与 `internal/ent/prompt_update.go:231-286`。端口新增
`PromptPrivacy`、`PromptSettingsCodec`、`PromptManagementService`、`PromptTypes`，
扩展 `PromptStore` / `IPromptStore`：

- 查询 `prompts`：validatePaginationArgs（first / last 至少一个，1..1000）→
  validateFirstLast（不可同时、非负）→ CREATED_AT 重写为 DefaultPromptOrder 的字段
  `id`；ORDER 按 order 列、UPDATED_AT 按 updated_at，id / CREATED_AT 无排序值；键集
  游标 Value 为 Go int（ORDER）或 time（UPDATED_AT），与 Cursor 标量一致。
- 查询隐私：OwnerRule → UserProjectScopeReadRule(read_prompts)（用户 + 上下文项目 +
  系统或项目 scope → 安装 project_id 谓词，作为 ProjectScope 传入存储）→
  UserReadScopeRule(read_prompts) → default deny；node(Prompt) 走同一策略，缺失报
  `ent: prompt not found`。
- 变更隐私：OwnerRule → UserProjectScopeWriteRule(write_prompts) →
  UserWriteScopeRule(write_prompts) → default deny；create 的 mutation 项目必须等于
  上下文项目才由规则放行，其余操作安装同一 project_id 谓词；服务始终显式带 project
  谓词，隐私只作决策。Prompt 模式没有 API-key 规则，service account 主体得到
  `no user in context`。
- 服务顺序与文案：先要求上下文项目（`project id not found in context`），再校验设置；
  create / update 的重名校验经隐私过滤查询并包裹 `failed to check prompt name
  uniqueness: `；重复名 `prompt name '<name>' already exists` + DUPLICATE_NAME /
  resource=prompt / field=name / value；create / update / delete / status / bulk 分别以
  `failed to create prompt: `、`failed to update prompt: `、`failed to delete prompt: `、
  `failed to update prompt status: `、`failed to bulk delete/enable/disable prompts: `
  包裹隐私与存储错误；affected==0 → `prompt not found or not in project`（不包裹）；
  update / status 成功后按隐私再查询，缺失报 `failed to query updated prompt:
  ent: prompt not found`（只写用户在此处被查询策略拒绝，写入已提交，与参照一致）。
- 设置校验（ValidatePromptSettings）：空 / nil conditions 为 no-op；model_pattern
  非空且经 xregexp.ValidateRegex；model_id 非空；api_key_id 非空且 >0；文案逐字一致
  （`model_pattern is required when type is model_pattern`、`invalid regex pattern in
  model_pattern: invalid regex pattern: <pattern>`、`model_id is required when type is
  model_id`、`api_key_id is required when type is api_key`、`api_key_id must be greater
  than 0`）。update 仅在 input.settings 非空时校验。
- 设置为 jsonb：Go struct 序列化语义（action 恒有、conditions omitempty、指针字段
  omitempty 但指向空串保留、key 顺序），经 `CAST(@ AS jsonb)` 写入；jsonb 归一化键序
  与空白，字节级差异不可观察。
- 存储：读取一律 deleted_at=0；Update / UpdateStatus / BulkUpdateStatus 是 aggregate
  更新，无 deleted_at 谓词（软删行仍被写，随后再查询报 not found）；Delete 转为
  SoftDeleteMixin 的 update（updated_at + deleted_at=unix 秒，谓词 id + project +
  deleted_at=0）；BulkDelete 仅活行；空 id 列表 no-op；SET 顺序为 updated_at、name、
  description、role、content、status、order、settings。
- 接线：`Program.cs` 注册 `PromptPrivacy` / `PromptManagementService`；
  `AdminQuery.prompts` 与 node(Prompt)、`AdminMutation` 七个变更（create / update /
  delete / status / bulk delete / bulk enable / bulk disable）。输入端缺失必填字段在
  gqlgen 为零值继续（端口由 SDL 拒绝），未知枚举输出在 gqlgen 原样透出（端口抛错），
  其余差异见 C188。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 查询形状（gid、时间、projectID、settings JSON、project 边）、where + ORDER 键集分页、CREATED_AT 回退、分页校验、create 默认值与设置 JSON、DUPLICATE_NAME extensions、update / status / bulk、软删、node、service_account 拒绝 | `tests/AxonHub.Api.Tests/AdminPromptsEndpointTests.cs` | 10 |
| 默认值、项目要求、校验顺序与文案、重复名与包裹、只读 / 只写用户、write-only 再查询拒绝与改名先拒、软删状态更新、批量语义与空列表、node 与 API-key 主体、test principal、设置校验 5 例与空列表、序列化 3 例 | `tests/AxonHub.Api.Tests/PromptManagementServiceTests.cs` | 27 |
| GetById 作用域 / 软删、ExistsByName、List 过滤 + 键集、更新时间排序、Update 无软删谓词与 SET、Delete 软删一次、Bulk 状态 / 删除语义 | `tests/AxonHub.Persistence.Tests/PromptStoreTests.cs` | +7 |

`AxonHub.Api.Tests` 由 2567 增至 2604（+37），`AxonHub.Persistence.Tests` 由 217 增至 224
（+7），Llm 3032、Grains 95；四个项目合计由 5911 增至 5955，0 失败 0 跳过。

差异：C188 由 gap 改为 accepted，登记剩余六项差异（where 子集、必填输入严格度、未知
枚举输出、order 列宽、无缓存失效、Project.prompts 反向边归 C165）。

## 101. 追踪与线程管理面（traces / threads / 八个状态变更 / 段与用量解析，23 + 15 例，2026-11-05）

参照 `internal/server/gql/ent.graphql:4477-4520`（Request）、`:5747-5810`（Thread）、
`:5940-6030`（Trace）、`axonhub.graphql:822-843`（UsageMetadata 与 Trace / Thread 的
扩展字段）、`ent.resolvers.go:541-570`（Threads / Traces）、`:856-896`（ID / projectID /
threadID 的 GUID 解析）、`axonhub.resolvers.go:786-810`（四个 Trace 变更）、
`:999-1085`（Thread / Trace 的 firstUserQuery、usageMetadata、archivedTracesCount、
rootSegment、rawRootSegment、firstText）、`gql_edge.go:652-688`（Project / Thread /
requests 边）、`gql_pagination.go`（DefaultTraceOrder / DefaultThreadOrder 与
CREATED_AT 重写）、`internal/server/biz/trace.go:117-256、448-1919`、`thread.go:120-222`、
`usage_metadata.go`、`request.go:1872-1897`、`internal/ent/schema/trace.go:80-107` 与
`thread.go:61-79`。端口新增 `TraceTypes`（Trace / Thread / Request / Segment / Span /
UsageMetadata 等 17 个类型、4 个枚举、三个 where 输入与三个 order 输入）、
`TracePrivacy`、`TraceManagementService`、`ThreadManagementService`、
`TraceSegmentService`、`TraceSegmentJson`、`TraceMapping`、`TraceAdminStore`、
`ThreadAdminStore`、`UsageMetadataStore`，扩展 `RequestSegmentStore`；`AdminQuery`
接线 traces / threads 与 node(Trace / Thread)，`AdminMutation` 接线八个状态变更。

- 查询与排序：traces / threads 的 first / last 经 validatePaginationArgs（至少一个、
  1..1000）与 validateFirstLast（不可同时）校验；根查询把 `orderBy.field == CREATED_AT`
  重写为 ent 默认排序字段 `id` 并保留方向（ent.resolvers.go:543-547、559-563）；
  UPDATED_AT 按 updated_at 排序，id 排序没有时间游标。Thread.traces 与 Trace.requests
  边不重写 CREATED_AT，保持 created_at 排序与 time 游标；Trace.requests 还带隐式
  `TraceIDEQ` 谓词。连接游标由 id + 排序字段时间组成，pageInfo 的 start / end cursor
  取 edges 首尾，与参照的键集分页一致。
- 节点与边：Trace / Thread / Request 的 id、projectID、threadID、apiKeyID、traceID、
  dataStorageID 按参照 resolver 生成 GID（零值语义见 C190）；project 边走 Project
  查询策略，缺失时抛 `ent: project not found`；Trace.thread 的缺失按 MaskNotFound
  返回 null（gql_edge.go:652-668）。rawRootSegment 按 Go encoding/json 语义序列化，
  nil 段输出 null；firstUserQuery / firstText / usageMetadata / archivedTracesCount
  分别调用与参照相同的第一段 / 聚合查询，存储错误以 `failed to ...` 包裹。
- 段构建：GetRootSegment 读取该 trace 的全部 completed 请求（Request 查询策略的项目
  谓词与逐行可见性谓词），参照的 10 路并发正文加载以顺序加载复现；正文按 completed
  状态与 `_ext` 标记规范化为空对象；请求 / 响应 span 由已移植的入站 / 出站变换器提取，
  覆盖 OpenAI Chat / Responses / Compact / Decisions、Anthropic、Gemini、图片、
  moderation、alpha search 的既有格式；首段按同一线程上一 trace 的 span 去重请求
  span，后续段按 tool_call_id 与已产生工具调用挂到父段，parentId / children 顺序与
  参照一致；变换失败跳过受影响 span（C191）。firstUserQuery / firstText 使用
  GetTraceFirstSegment（最早的 completed 请求）。
- 状态变更：archiveTrace / unarchiveTrace / retainTrace / unretainTrace 与四个 thread
  变更分别以 active↔archived、active↔retained 的 from / to 调用 UpdateStatus，谓词为
  id + from 状态 + 隐私项目谓词；affected==0 → `cannot <op> trace|thread: current
  status is not <from> or trace|thread not found`；存储异常以
  `failed to <op> trace|thread: ` 包裹；thread 变更在同一事务内把该 thread 中与 thread
  原状态相同的 trace 级联到新状态（Trace 变更策略的 project 谓词同样生效），级联失败
  回滚并以 `failed to cascade <op> traces: ` 报错。
- 隐私（C189）：Trace / Thread 共用查询策略（API-key read_requests → 用户项目
  read_requests → owner → 用户 read_requests → default deny）与变更策略
  （write_requests 链）；Request 查询策略额外给出逐行可见性谓词。端口在存储调用前
  显式求值并转成 project_id / 可见性 / 级联 project 谓词；API-key、service account
  与 test principal 的允许 / 拒绝文案与参照一致（拒绝经 GraphQL 呈现层映射为
  `permission denied`）。
- 顺带修复：`HttpContextManagementIdentity` 在管理 JWT 主体没有项目上下文时曾回落到
  /v1 的 `RequestIdentity.Default`（项目 1），导致 owner 查询被意外限定到项目 1；
  现已按主体类型分流，管理主体不再继承 /v1 默认项目。`RequestSegmentStore` 的
  `GetFirstCompletedByTraceAsync` 补上 LIMIT 1（消除 QuerySingleOrDefault 多行异常），
  线程级联 SQL 补上 Trace 变更策略的 project 谓词。提示词端点夹具改为像前端一样发送
  `X-Project-ID`，与参照的上下文项目要求一致。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 根查询形状（gid / 时间 / projectID / thread 边 / requests totalCount）、CREATED_AT→id 回退、where 子集、段 / 用量 / rawRootSegment、project / thread 边错误、Thread/0、requests 边排序与时间谓词、threads 查询与 tracesSummary / archivedTracesCount、Thread.traces 的 CREATED_AT 顺序、空 where、node 解析与缺失文案、八个状态变更与文案、级联错误、项目成员作用域、service account 拒绝 | `tests/AxonHub.Api.Tests/AdminTracesEndpointTests.cs` | 23 |
| Trace 列表过滤 / 作用域 / 键集 / 状态更新 / 线程内首条与上一条 / archived 计数；Thread 列表 / 级联事务与回滚；用量聚合；completed 请求读取 / where 时间谓词 / 可见性谓词 | `tests/AxonHub.Persistence.Tests/TraceAdminStoreTests.cs`（4）、`ThreadAdminStoreTests.cs`（5）、`UsageMetadataStoreTests.cs`（3）、`RequestSegmentStoreTests.cs`（3） | +15 |

`AxonHub.Api.Tests` 由 2604 增至 2627（+23），`AxonHub.Persistence.Tests` 由 224 增至 239
（+15），Llm 3032、Grains 95；四个项目合计由 5955 增至 5993，0 失败 0 跳过。

差异：C189 登记显式隐私求值与 where 子集；C190 登记 Go int → GraphQL Int 的 checked
截断与零值 GID；C191 登记无外部对象存储、无日志接缝与正文加载并发差异。

## 102. 请求管理面（requests / node(Request) / 请求详情 / executions 与 usageLogs，8 例，2026-11-05）

参照 `internal/server/gql/ent.graphql:4477-4606`（Request）、`:4641-4711`
（RequestExecution）、`:6643-6718`（UsageLog）、`cost.graphql:1-12`（CostItem /
TierCost）、`internal/server/gql/ent.resolvers.go:496-511`（requests 根查询）、
`:600-935`（请求、执行与用量的边、正文与掩码字段）、`internal/ent/gql_edge.go:471-560`
（Request 的 apiKey / project / trace / channel / executions / usageLogs 边）、
`:553-575`（RequestExecution 的 request / channel 边）、`:693-715`（UsageLog 的
request / project / channel 边）、`gql_pagination.go`（DefaultRequestOrder）、
`internal/server/biz/request.go:124-165、1532-1876`（正文与 chunk 加载、`_ext` 标记、
完成态判定）。

新增 `GraphQL/RequestTypes.cs`（RequestExecution / UsageLog 类型与连接、四个 order
输入、三个 where 输入、TierCost / CostItem、RequestType 的正文与边扩展、
`RequestMapping`）与 `Management/RequestQueryService`（node、两个边查询、四个隐私
边辅助），扩展 `TraceMapping.ToFilter(RequestWhereInput)` 与 `ToRootRequestSortField`，
在 `AdminQuery` 接线 requests 根查询与 node(Request)。

- 根查询与排序：requests 的 first / last 经 validatePaginationArgs（至少一个、
  1..1000）与 validateFirstLast 校验；`orderBy.field == CREATED_AT` 重写为 ent
  DefaultRequestOrder 的 `id` 并保留方向，UPDATED_AT 按 updated_at 排序；项目作用域与
  逐行可见性谓词来自 Request 查询策略（owner / 系统作用域 → 无限制）。
- 节点与边：node(Request) 经 Request 查询策略返回节点，缺失时
  `ent: request not found`；apiKey / trace 边按 MaskNotFound 语义（缺失或拒绝 → null），
  project 边非空字段在可见性拒绝或缺失时抛 `ent: project not found`；channel 边走
  getNilableChannel（缺失 / 软删 / 隐私拒绝 → null，其余错误以
  `failed to load channel: ` 包裹）；channelAPIKeyIndex / Suffix 仅在通道可见且列非空时
  返回。
- 正文与 chunk：requestBody 空列 → `{}`；responseBody 仅 completed 行返回，空列或
  压缩后等于 `{"_ext":1}` → `{}`；responseChunks 仅在 stream 且状态为
  completed / failed / canceled 时解析，单元素 `_ext` 标记折叠为 `[]`；执行使用同一组
  规则（LoadRequestBody / LoadResponseBody 的执行版本）。stream + processing 的实时预览
  未移植（C192）。
- 嵌套连接：Request.executions / usageLogs 的游标为 id + created_at / updated_at，
  默认排序 id（边不重写 CREATED_AT），where 输入映射到既有持久化过滤器（空 In 列表
  丢弃，与 entgql 的 len > 0 一致）；执行项目 ID 在 SDL 为 Int，内部按 long 读写并
  checked 截断（C190）。用量成本按 `BillingJson` 解码 CostItem / TierCost，token 列经
  Go int64 → GraphQL Int 的 checked 映射。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 根查询形状与节点详情（gid / 时间 / 正文 / 请求头 / chunk 规则 / content 列 / apiKey / project / trace 边 / executions 与 usageLogs 连接）、CREATED_AT→id 回退、where 谓词映射、执行状态与掩码字段、成本明细、node 错误文案 | `tests/AxonHub.Api.Tests/AdminRequestsEndpointTests.cs` | 5 |
| 通道边的隐私拒绝 → null、加载失败包裹文案、空 / 零通道 id 短路 | `tests/AxonHub.Api.Tests/AdminRequestsEndpointTests.cs`（`RequestQueryServiceVisibilityTests`） | 3 |

`AxonHub.Api.Tests` 由 2627 增至 2635（+8），Llm 3032、Grains 95、
`AxonHub.Persistence.Tests` 239；四个项目合计由 5993 增至 6001，0 失败 0 跳过。

差异：C192 登记三个 where 输入的 entgql 子集、缺失的 dataStorage 边 / 类型与
stream + processing 的实时 chunk 预览缺口。

## 103. 用量日志管理面（usageLogs 根查询 / node(UsageLog) / 可见性谓词，8 + 1 例，2026-11-05）

参照 `internal/server/gql/ent.resolvers.go:572-586`（根 usageLogs）、`:334-342`
（node 经 guidTypeToNodeType 落到 usage_log 表）、`internal/ent/schema/usage_log.go:122-136`
（查询 / 变更隐私链）、`internal/scopes/rule_user_project_scope_requests.go:20-75`
（UserProjectScopeReadRequestsRule 与 UsageLogFilter 分支）、`rule_owner.go:10`
（OwnerRule）、`rule_user_scope.go:12`（UserReadScopeRule）、
`internal/ent/gql_pagination.go:6772-6784`（DefaultUsageLogOrder = id ASC）。

扩展 `GraphQL/RequestTypes.cs`（UsageLogType 实现 INode、ToRootUsageLogSortField）、
`Management/TracePrivacy.cs`（EvaluateUsageLogQueryAsync）、
`Persistence/UsageLogStore`（GetByIdAsync、ProjectScope / VisibilityUserId 过滤）、
新增 `Management/UsageLogManagementService`，在 `AdminQuery` 接线 usageLogs 根查询与
node(UsageLog)。

- 根查询与排序：first / last 走与 requests 相同的 validatePaginationArgs /
  validateFirstLast；`orderBy.field == CREATED_AT` 重写为 DefaultUsageLogOrder 的 `id`
  并保留方向，UPDATED_AT 按 updated_at 排序，缺省 id ASC。
- 隐私链：先按 current user + project 上下文应用 UserProjectScopeReadRequestsRule
  （owner 视为系统作用域；项目作用域下非 owner 追加逐行可见性），再落到 OwnerRule
  （owner → 全量）与 UserReadScopeRule（无用户 → `default deny: ent/privacy: deny rule`，
  系统作用域 → 全量，否则拒绝）；UsageLogFilter 分支把可见性展开为
  `requests.source = playground AND requests.user_id = 当前用户` 或
  `requests.source <> playground AND (requests.api_key_id IS NULL OR 关联 api_keys.type <> personal OR api_keys.user_id = 当前用户)`。
  端口把该谓词渲染为 SQL EXISTS 子查询（join 的 api_keys 不过滤 deleted_at，与 C189 一致）。
- 节点与边：node(UsageLog) 在同一隐私链下查询，缺失或不可见时
  `ent: usage_log not found`；request / project / channel 边沿用 §102 的
  RequestQueryService 边语义；where 输入为 §102 UsageLogWhereInput 的子集（C192）。
- 反向边：Project.usageLogs / Channel.usageLogs 仍未接线（C165）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 根连接形状与排序（CREATED_AT→id DESC、缺省 id ASC）、成本明细 / 档位、channel 边、where 谓词下推、node 详情与 `ent: usage_log not found` | `tests/AxonHub.Api.Tests/AdminUsageLogsEndpointTests.cs` | 3 |
| 四种上下文（成员 / 项目 owner / 系统 owner / 无作用域成员）的隐私求值与拒绝文案 | `tests/AxonHub.Api.Tests/AdminUsageLogsEndpointTests.cs`（`UsageLogPrivacyTests`） | 5 |
| 项目范围与可见性 SQL 子查询（playground 用户、缺失 / 共享 / 他人 personal key） | `tests/AxonHub.Persistence.Tests/UsageLogStoreTests.cs`（`ListAndGetByIdApplyProjectAndUsageLogVisibility`） | 1 |

`AxonHub.Api.Tests` 由 2635 增至 2643（+8），Llm 3032、Grains 95、
`AxonHub.Persistence.Tests` 由 239 增至 240（+1）；四个项目合计由 6001 增至 6010，
0 失败 0 跳过。

差异：C193 登记显式隐私求值 / SQL 子查询表达与反向边、where 子集缺口（承接 C165 / C192）。

## 104. Analytics 页 GraphQL（四个根字段，10 + 4 例，2026-11-05）

参照 `2851a192`（局部基线跟进，见 00-reference.md 漂移记录与 C194）：
`internal/server/gql/analytics.graphql`（AnalyticsFilter / AnalyticsMetadata /
AnalyticsOverview / AnalyticsDailyStat / AnalyticsDimensionStat 与四个根字段）、
`analytics.resolvers.go`（四个解析器）、`analytics_helpers.go`（buildAnalyticsWhere /
resolveFilterAPIKeyIDs / startOfDay / 维度名称合成）、`internal/authz/scope.go`
（WithScopeDecision / HasScope）、`internal/server/biz/performance.go`
（MinLatencyMs = 10 与生成延迟）、`internal/server/biz/system.go:1499-1546`
（TimeLocation 解析）。

新增 `Persistence/Stores/AnalyticsStore.cs`（Dapper 聚合与维度 / 性能查询）、
`Api/Settings/SystemTimeLocations.cs`（共享时区解析，配额切片 C186 复用同一实现）、
`Api/Management/AnalyticsService.cs`（作用域决策、过滤解析、名称合成、日桶补零）、
`Api/GraphQL/AnalyticsTypes.cs`（输入 / 输出类型与映射），在 `GraphQLSetup` 显式注册
`AnalyticsFilter` 的线缆名（HotChocolate 对解析器参数推断的输入对象按 CLR 类型命名、
忽略 `[GraphQLName]`），在 `AdminQuery` 接线四个根字段。

- 过滤器与边界：startTime / endTime 为 RFC3339 `Time`，下界 `>=`、上界 `<=`
  （毫秒精度）；projectIDs / channelIDs / apiKeyIDs / userIDs 按 GID 解析，
  modelIDs 原样；显式 apiKeyIDs 与 userIDs 同时存在时取交集（保留显式顺序）；
  userIDs 经 `api_keys.user_id` 解析（不过滤 deleted_at），解析失败吞掉并按
  「用户过滤未命中任何 key」处理（SQL FALSE）；无过滤条件时不加谓词。
- 作用域：overview / daily / dimension 在查询前按 `read_dashboard` 求值——
  测试 Principal 放行、API key 按 scopes 判断、无用户 `no user in context`、
  owner / 系统角色 scope 放行、项目级 scope 需显式项目头、其余 `default deny`，
  统一附加 `ent/privacy: deny rule` 并映射 `extensions.code = FORBIDDEN`；
  被允许的调用绕过 UsageLog 隐私链（无项目过滤）。metadata 不做 scope 决策，
  走 `TracePrivacy.EvaluateUsageLogQueryAsync`（项目作用域 + 逐行可见性）。
- 日统计：按配置时区的本地日历日
  `to_char(created_at AT TIME ZONE <name>, 'YYYY-MM-DD')` 分组，服务层用
  startOfDay 补零（缺省今天 - 29 天到今天的本地日）；时区缺失 / 畸形 / 未知
  回退 UTC，`Local` 映射 TimeZoneInfo.Local 且名称为 "Local"；时区名以参数
  绑定（C194）。
- 维度：channel（join channels 且 deleted_at = 0）、model（id = name = model_id）、
  apiKey（api_key_id 非空，名称按未过滤删除的 api_keys 读取，缺失回退
  `API Key #<id>`）、user（join api_keys deleted_at = 0 与 users，名称
  `TrimSpace(first + " " + last)`，空则 email）；均按 total_tokens DESC 排序。
- 性能：request_executions join requests 的 request_id 与 channel_id，取每个
  (request, channel) 最新的 completed 尝试（metrics_latency_ms > 0）；生成延迟 =
  流式且有 TTFT ? latency - ttft : latency，下限 10ms；tokensPerSecond =
  SUM(completion_tokens) * 1000 / SUM(生成延迟)，ttftMs = 流式且 ttft > 0 的均值；
  无样本时两者为 null。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 四个根字段线形、过滤器映射与交集、最早时间与隐私链、空用户过滤 FALSE、时区日桶与缺省 30 天、维度名称合成与性能合并、未知维度文案、read_dashboard 拒绝 FORBIDDEN、存储失败前缀 | `tests/AxonHub.Api.Tests/AdminAnalyticsEndpointTests.cs` | 10 |
| 边界含毫秒与上界闭区间、空聚合零值、维度 join（活跃通道 / 活 key / 用户名称）、apiKey 名称与用户 key 解析、性能最新 completed 尝试与 10ms 下限 | `tests/AxonHub.Persistence.Tests/AnalyticsStoreTests.cs` | 4 |

`AxonHub.Api.Tests` 由 2643 增至 2653（+10），Llm 3032、Grains 95、
`AxonHub.Persistence.Tests` 由 240 增至 244（+4）；四个项目合计由 6010 增至 6024，
0 失败 0 跳过。

差异：C194 登记 analytics 切片的局部基线跟进（2851a192）、直接 Dapper SQL 渲染、
Postgres-only 日期分支、时区名参数绑定与 `::text` / `checked` 截断。

## 105. Dashboard 首页 GraphQL（21 个根字段，13 例，2026-11-05）

参照 `f272549f`（局部基线跟进，见 00-reference.md 漂移记录与 C195）：
`internal/server/gql/dashboard.graphql`（21 个根字段与全部输出类型；与基线逐字节相同，`cmp` 验证）、
`dashboard.resolvers.go`（全部解析器）、`dashboard_cache.go`（`cachedDashboardQuery` /
`computeDashboardCache` / `loadDashboardCache`）、`dashboard_helpers.go`
（`calculateConfidenceAndSort` / `normalizeChannelSuccessRatesParams` /
`normalizeFastestChannelsInput` / `parseTimeWindow` / `safeIntFromInt64`）、
`dashboard_usage_aggregates.go`（维度、窗口键、稳定排序与 top 10）、`xtime.GetCalendarPeriods`。

新增 `Persistence/Stores/DashboardStore.cs`（17 个读取方法、聚合行与 PostgreSQL
`DISTINCT ON` 查询）、`Api/Management/DashboardService.cs`（21 个字段的语义）、
`Api/Management/DashboardCache.cs`（作用域先行的进程内单飞缓存）、
`Api/Management/DashboardTokenStatsCache.cs`（all-time 的软 / 硬 TTL）、
`Api/Management/CalendarPeriods.cs`、`Api/GraphQL/DashboardTypes.cs`（输出 / 输入类型与映射），
在 `AdminQuery` 接线 21 个根字段。

- 缓存：`read_dashboard` 作用域在读写缓存之前求值；命中条件 `now <= expiresAt`；
  同一 key 单飞，等待者按自身请求取消退出而共享计算继续；计算体在独立 DI 作用域内以
  2 分钟超时执行并二次查缓存；TTL 为 1m（usage 聚合 / dailyRequestStats /
  channelSuccessRates）与 5m（fastestChannels / fastestModels / 模型与通道性能）。
- 容错：DashboardOverview / RequestStats 在任何失败时返回零值；TokenStats 的 all-time
  在 hard TTL（24h）内返回快照，soft TTL（1h）后参照启动异步刷新（`context.Background()`
  无用户上下文，恒被隐私拒绝），端口省略；同步失败返回非终止错误
  `Failed to aggregate all-time token statistics` +
  `extensions.code = TOKEN_STATS_ALL_TIME_AGG_FAILED`，三项归零且 `lastUpdated: null`，
  失败不缓存；空表语义由 `RowCount == 0` 复现。
- 归一化：channelSuccessRates 的 null / 空串 → day、仅精确 day/week/month 保留、
  其余 allTime、limit ≤ 0 不限量、详情含软删通道（`SkipSoftDelete`）、
  `ChannelDisabled = status != "enabled"`；fastest* 的未知值 → day、缺省 5、上限 100、
  `sqlLimit = max(limit*4, 20)`；置信度按请求数中位数分级（高 ≥ 1.5× 且 ≥ 500、
  中 ≥ 0.5×），分值降序再按吞吐降序；usage 聚合窗口 null → allTime、
  键 `dashboard-usage:<dim>:<window>:<location>`、score 降序 → ID 升序 → Name 升序、
  top 10、totalTokens = Input + Output。
- 日历：今天的本地午夜、周一为一周起点、月首 / 月末，以 UTC 存储；
  dailyRequestStats 补零最近 30 个本地日。
- 性能：模型统计 30 秒内部超时、30 天本地窗口、按请求量排名、TTFT 仅 > 0、空吞吐为 null；
  通道统计优先 `channel_probes`（端口缺席，见 C195），否则 executes 回退、
  通道 id 升序、名称回退 `channel-<id>`、零吞吐 / TTFT 转 null。
- 其余字段：APIKeyTokenUsageStats 的三条校验文案与可访问 key 过滤（每 key 前 3 模型）、
  UsageStatsByUser 的 owner / 项目校验与 custom RFC3339 窗口解析、
  TopRequestsProjects 的缺失项目跳过与 privacy 拒绝映射（`permission denied` / FORBIDDEN）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 21 个根字段线形、缓存前 read_dashboard 拒绝、overview 计数与容错零值、all-time 失败错误与硬 TTL 快照、channelSuccessRates 归一化与软删详情、fastest 置信度与过量采样、usage 聚合窗口规范化与共享缓存、daily 本地日补零、模型 / 通道性能排名与名称回退、usageStatsByUser owner 与 custom 窗口、apiKeyTokenUsageStats 校验与访问过滤、topRequestsProjects 跳过缺失与 FORBIDDEN | `tests/AxonHub.Api.Tests/AdminDashboardEndpointTests.cs` | 13 |

`AxonHub.Api.Tests` 由 2653 增至 2666（+13），Llm 3032、Grains 95、
`AxonHub.Persistence.Tests` 244；四个项目合计由 6024 增至 6037，0 失败 0 跳过。

差异：C195 登记 dashboard 切片的实时基线跟进（f272549f）、缓存 key 省略 ent client 指针、
省略恒失败的异步 soft 刷新、`channel_probes` 分支缺席、Postgres-only `DISTINCT ON`、
`checked` 32 位截断与 7 位小数时间精度。

## 106. 数据存储管理面 GraphQL（dataStorages / node(DataStorage) / 两个变更，14 + 8 例，2026-11-06）

参照 `f272549f`（局部基线跟进，见 00-reference.md 漂移记录与 C196）：
`internal/ent/schema/data_storage.go`（TimeMixin / SoftDeleteMixin、字段与枚举、唯一索引
`data_sources_by_name`、policy 88-104）、`migrate/schema.go:262-288`（列顺序与 PostgreSQL 类型）、
`internal/server/biz/data_storage.go:200-229`（CreateDataStorage）与 `:232-289`（UpdateDataStorage）、
`:793-887`（mergeSettings 与三个 is*Provided）、`internal/objects/data_stograge.go`（settings JSON）、
`internal/ent/gql_where_input.go`（DataStorageWhereInput）、`internal/ent/gql_pagination.go:2635-2645`
（DefaultDataStorageOrder = id）、`internal/server/gql/ent.resolvers.go:410-423`（dataStorages 仅
validatePaginationArgs + CREATED_AT 改写）、`internal/server/gql/ent.graphql` 与 `axonhub.graphql`
（DataStorage / 输入 / 枚举 / settings 输出类型；DataStorage 相关章节与基线相同，
`git diff` 仅见通道相关增删）、`internal/server/gql/axonhub.resolvers.go:668-676`（两个变更）。

新增 `Persistence/Stores/DataStorageStore.cs`（EF 写 + Dapper 读、软删谓词、唯一 name 索引、
键集分页、部分列 `UPDATE ... RETURNING`）、`Persistence/Records.cs` 的 `DataStorageRecord` 与
`AxonHubDbContext` 映射、`DatabaseSchema.Initial` 幂等 DDL、`Api/Management/DataStorageSettings.cs`
（Go 兼容 JSON 与 mergeSettings）、`Api/Management/DataStoragePrivacy.cs`（查询 / 变更策略）、
`Api/Management/DataStorageManagementService.cs`、`Api/GraphQL/DataStorageTypes.cs`（类型与映射），
在 `AdminQuery` 接线 `dataStorages` 与 `node(DataStorage)`，在 `AdminMutation` 接线
`createDataStorage` / `updateDataStorage`。

- 表：`id / created_at / updated_at / deleted_at / name / description / primary / type / settings / status`；
  `data_sources_by_name` 唯一索引只覆盖 `name`（软删行仍占用名称，与 prompts 的
  `(project_id, name, deleted_at)` 不同）；查询追加 `deleted_at = 0`，
  生成的 `UpdateOneID` 没有软删谓词（只有先行的 Get 读活行）。
- 查询：`dataStorages(first, last, before, after, orderBy, where)` 只做 validatePaginationArgs
  （不做 first + last 互斥）；`CREATED_AT` 改写为 `DefaultDataStorageOrder.Field`（id）并保留方向，
  缺省 id ASC；`UPDATED_AT` 按 `updated_at, id` 排序且游标携带时间值，id 排序的游标不含值；
  totalCount 为 where 过滤后的总数。
- 输出：gid `DataStorage/<id>`、GoTime 时间、`settings { dsn, directory, s3 { bucketName, endpoint,
  region, pathStyle }, gcs { bucketName }, webdav { url, username, password, insecure_skip_tls, path } }`；
  S3 的 accessKey / secretKey 与 GCS credential 不在输出类型中；settings 列为 NOT NULL，
  输出恒有值。
- 创建：先经隐私查询做重名检查（错误前缀 `failed to check data storage name uniqueness: `），
  重名抛 DUPLICATE_NAME（message `data storage name '<name>' already exists`，
  extensions resource=`data storage` / field=`name` / value=<name>）；input.status 被忽略，
  行固定 `primary=false`、`status=active`；settings 按 Go 结构体全字段序列化（无 omitempty，
  指针 nil 写 JSON null）。
- 更新：Get 经隐私查询（缺失报 `failed to get data storage: ent: data storage not found`）；
  仅当 name 提供且与现有值不同才做重名检查（NameEQ + IDNEQ，排除自身）；settings 提供时按
  mergeSettings 合并——Directory / DSN 以输入覆盖，S3 / GCS / WebDAV 仅在检测字段非空时视为提供
  （PathStyle / InsecureSkipTLS 不参与检测），凭据 / 密码为空串时保留旧值，其余字段按输入覆盖；
  SET 列表只含提供的列并总是刷新 updated_at，`UPDATE ... RETURNING` 返回存储后的行。
- 隐私：查询 `APIKeyScopeQueryRule(read_data_storages)` → `UserReadScopeRule(read_data_storages)`
  → `OwnerRule()` → default deny；变更 `UserWriteScopeRule(write_data_storages)` → `OwnerRule()`
  → default deny；无用户时 deny 文案为 `no user in context`，GraphQL 呈现为
  `permission denied` / FORBIDDEN（`ent/privacy: deny rule` 后缀）。f272549f 把 API-key
  查询规则的作用域由基线的 `write_requests` 改为 `read_data_storages`（e5c904ef），本切片随之（C196）。
- where 子集：id / idNEQ / idIn / idNotIn、name / nameNEQ / nameIn / nameNotIn / nameContainsFold、
  primary / primaryNEQ（显式 false 也安装谓词）、type / typeNEQ / typeIn / typeNotIn、
  status / statusNEQ / statusIn / statusNotIn 与 not / and / or；空 `*In` 列表不安装谓词；
  嵌套空 not / and / or 报 `ent: empty predicate DataStorageWhereInput: field '<not|and|or>'`；
  顶层空输入被 entgql 吞掉。
- 未移植：fs / S3 / GCS / WebDAV 存储后端与缓存（含 f272549f 的 S3 checksum 选项）、
  GetDataStorageByID / GetPrimaryDataStorage / GetDefaultDataStorage 的进程内缓存、
  requests / executions 边、WhereInput 的时间 / 描述 / 字符串比较 / 边谓词、datamigrate v0.4.0
  的主存储种子行。见 C196。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 表结构与索引快照、全列往返、软删隐藏与唯一名保留、名称检查排除自身、containsFold / typeIn / statusIn 过滤、id 键集分页、updated_at 排序与游标值、嵌套空谓词文案、部分列更新与 RETURNING | `tests/AxonHub.Persistence.Tests/DataStorageStoreTests.cs`（+ `SchemaTests` / `EfCoreModelTests` 表数与索引数更新） | 8 |
| dataStorages 线形与 settings 输出、webdav / gcs 形状、where + UPDATED_AT 键集分页、CREATED_AT 回退 id、显式 primary=false、创建默认值与 JSON、DUPLICATE_NAME 扩展、更新合并凭据、重名 / 自身重命名、缺失行文案、node、service_account 作用域与 default deny | `tests/AxonHub.Api.Tests/AdminDataStoragesEndpointTests.cs` | 14 |

`AxonHub.Api.Tests` 由 2666 增至 2680（+14），`AxonHub.Persistence.Tests` 由 244 增至 252（+8），
Llm 3032、Grains 95；四个项目合计由 6037 增至 6059，0 失败 0 跳过。

差异：C196 登记 data storage 切片的实时基线跟进（f272549f 的 API-key 作用域）、显式隐私求值、
EF 写 + Dapper 读、settings JSON 的恶化解码（C141 同型）、未移植的存储后端 / 缓存 / 边与 where 子集，
以及 Go mergeSettings 在 settings 指针为 nil 时的空指针边界（端口降级而非崩溃）。

## 107. 备份 / 恢复与自动备份管理面 GraphQL（backup / restore / autoBackupSettings / updateAutoBackupSettings / triggerAutoBackup，57 + 24 例，2026-11-06）

参照 `b8bb3421`（冻结基线；与实时上游 `f272549f` 相比，`internal/server/backup/`、`backup.graphql`
与 `backup.resolvers.go` 零差异，`biz/system.go` 的差异只有 safeguards 接线，不触及自动备份设置）：
`internal/server/backup/types.go:11-244`（BackupData 与九个实体、usage request / log 的自定义
MarshalJSON、版本常量、BackupOptions / RestoreOptions、冲突策略）、
`internal/server/backup/backup_ops.go:26-132`（Backup / BackupWithoutAuth 的 owner 门禁、
doBackupToWriter 的根成员顺序与 17 个可移植系统配置 key）、`:124-440`（八个 stream* 的 500 行 ID 游标、
SoftDeleteMixin 谓词、eager 边与 omitempty）、
`internal/server/backup/restore.go:29-146`（owner 门禁、encoding/json 解码、版本检查先于事务、单事务提交、
恢复顺序）、`:148-259`（系统配置 upsert、旧配额路由迁移、channel id map）、`:482-535`（projects）、
`:536-699`（channel_model_prices 的校验 / 归档 / active 版本）、`:700-790`（channels）、`:791-870`（models）、
`:871-960`（API keys）、`:961-1555`（用量请求与用量日志的去重、请求壳、批量 500 行）、
`internal/server/backup/autobackup.go:22-222`（Reschedule、triggerAutoBackup、shouldRunBackup、
performBackup、cleanupOldBackups、RunBackupNow）、
`internal/server/biz/system.go:188-235` 与 `:1616-1676`、`:1975-1986`（AutoBackupSettings 的三个指针字段
回退、SetAutoBackupSettings、UpdateAutoBackupLastRun 的 UTC 时间）、
`internal/server/biz/system_default.go:118-129`（默认设置）、
`internal/server/gql/backup.graphql:1-97` 与 `internal/server/gql/backup.resolvers.go:20-146`（SDL 与五个
解析器）、`internal/ent/channel_update.go:1149-1155`（SetNillableBaseURL 仅在指针非 nil 时写列）。

新增 `Api/Management/BackupModels.cs`（选项、版本、自动备份设置编解码与备份文档 DTO）、
`Api/Management/BackupJson.cs`（与 Go 同序的流式文档）、`Api/Management/BackupService.cs`（备份、恢复、
自动备份）、`Persistence/BackupSystemConfigKeys.cs`（17 个 key）、`Persistence/Stores/BackupStore.cs`
（备份读侧）、`Persistence/Stores/BackupRestoreStore.cs`（恢复读写与事务）、`Api/GraphQL/BackupTypes.cs`
（SDL 类型与映射），在 `AdminQuery` 接线 `autoBackupSettings`、在 `AdminMutation` 接线 `backup` /
`restore` / `updateAutoBackupSettings` / `triggerAutoBackup`，`GraphQLSetup` 注册 Upload 标量，`Program`
注册存储与服务；`SystemSettingsAdminService` 增加自动备份设置的读 / 写 / LastRun，
`ChannelWriteSerialization` 暴露 `NormalizeSettings` / `NormalizeCredentials`（恢复解码后的再编码），
`ChannelCredentialsDraft` 补 `azure` 凭据，`ApiKeyProfilesCodec` 的 `Cost` 增加 Go 十进制转换器
（恢复路径经 System.Text.Json 解码）。

- 备份文档：根成员固定顺序 `version, timestamp, system_configs, projects, channels, models,
  channel_model_prices, api_keys, usage_requests, usage_logs`；timestamp 为带本地偏移的 RFC3339Nano；
  `system_configs / projects / channel_model_prices / api_keys / usage_requests / usage_logs` 关闭或为空时
  省略，`channels / models` 关闭时写 null（Go 指针）；channel `edges` 恒为 `{}`、credentials 恒输出、
  `base_url` 与 `default_test_model` 空串省略；project / API key 的 `edges` 只含活边；price 的 channel
  边缺失（通道不存在或软删）时跳过该元素；usage request 的 `api_key_key` 仅 `IncludeAPIKeys` 时输出；
  usage log 的 api key 名字还需 `IncludeUsageStats` 且映射非空；八个 stream 都以 500 行
  `id > lastID ORDER BY id` 取页，NextId 为最后一行 id，空页为 0；空凭据通道、软删行与白名单外的
  系统配置都不进入文档。
- 权限与错误：`Backup` / `Restore` 无用户时 `user not found in context`，非 owner 分别
  `only owners can perform backup operations` / `only owners can perform restore operations`；
  `BackupWithoutAuth` 跳过门禁（自动备份路径）；GraphQL 的四个变更统一以
  `permission denied: owner access required` 拒绝非 owner。
- 恢复：先解码（Go encoding/json 语义：大小写不敏感、null 文档降级为空、字段缺失取零值），再版本校验
  （接受 1.4 / 1.3 / 1.2 / 1.1 / 1.0，否则 `backup version mismatch: expected 1.4, got {version}`），
  之后才开事务；顺序为系统配置 → 通道 → channel id map → 旧配额路由 → 价格 → 模型 → 项目 → API key →
  用量；任一步失败回滚，提交后整体生效。系统配置只 upsert 17 个白名单 key
  （`failed to restore system configuration "<key>": `）；通道先按名字查活行再判断空凭据
  （APIKey 与 APIKeys 与 OAuth 都空则跳过）；冲突策略 skip / error / overwrite 的文案逐字复刻；
  overwrite 保留备份为空的 BaseUrl（ent 的 SetNillableBaseURL 对 nil 不写列），Remark 为 null 时清空，
  AutoSyncModelPattern / DefaultTestModel 以空串落库；create 不触碰 DisabledApiKeys / Policies /
  Endpoints / Settings / SupportedModels / ManualModels / Tags（ent schema 默认值）。
  模型 / 项目 / API key 的查重与 overwrite / create 字段集与参照一致，API key 缺项目时跳过、项目名空串
  回退 `Default`、UserId 取当前用户；价格先 `Price.Validate()`（`invalid channel model price:
  channel={name} model_id={id}: {msg}`）、reference ID 为空报错、引用通道缺失或软删则跳过，overwrite
  归档 active 版本（archived + EffectiveEndAt=now）后更新价格并新建 active 版本，同值同 reference 时
  无操作；通道 updated_at 由批量末尾的 SetChannelUpdatedAt 刷新。用量请求按 id 与指纹（含 / 不含
  API key 两种形态）去重，未映射的请求 id 在日志阶段跳过；日志缺请求时创建请求壳
  （`request_body "{}"`、status `completed`、stream false、client_ip `""`、ApiKeyId / ChannelId 为 0 时
  落 null），项目缺失的请求连同日志跳过，CostItems 为 null 时写 `"null"`，CostPriceReferenceId 空串
  写 null，批量 500 行 `failed to restore usage logs: `。
- 自动备份：`ShouldRunBackup` 对 daily 恒真、weekly 仅周日、monthly 仅 1 号、未知频率恒真（本地时间）；
  `TriggerAutoBackupAsync` 吞设置读取失败、禁用或频率不匹配时直接返回，执行后把错误文本写入
  `last_backup_error`（UTC 时间戳，记录失败也吞）；`RunBackupNowAsync` 把设置读取失败包装为
  `failed to get auto backup settings: `、`DataStorageId == 0` 报 `data storage not configured for backup`，
  执行后记录 last run 并以 `ExceptionDispatchInfo` 重抛原异常；`PerformBackupAsync` 的 data storage
  缺失文案为 `failed to get data storage: failed to get data storage by ID {id}: ent: data storage not found`，
  database 类型完整生成文档并跳过参照中为纯 nil 的 SaveData。
- 自动备份设置：`system_auto_backup_settings` 的 JSON 形状与 Go 结构体一致（last_backup_at 与
  last_backup_error 为空时省略）；行缺失时返回默认值（channels / models / modelPrices true，其余 false，
  retention 30）；解码错误 `failed to unmarshal auto backup settings: `、读取失败
  `failed to get auto backup settings: failed to get system value: `、写入失败
  `failed to set auto backup settings: `；三个指针字段（include_system_configs / include_usage_stats /
  include_request_logs）缺失时回退默认值，其余缺失字段取 Go 零值；UpdateAutoBackupLastRun 先读后写并保留
  其余字段。
- GraphQL SDL：`BackupOptionsInput` / `RestoreOptionsInput`（含 SDL 默认值 includeModelPrices=true、
  includeUsageStats=true）、三个输入枚举、`BackupPayload` / `RestorePayload` / `TriggerBackupPayload`、
  `AutoBackupSettings`、`UpdateAutoBackupSettingsInput`、`scalar Upload`、`autoBackupSettings` 查询与四个
  变更；`IncludeProjects` 与 `ProjectConflictStrategy` 不是 SDL 字段，管理面恒为 false / 空串（与 gqlgen
  生成映射的零值一致），因此经 GraphQL 的备份文档不含 projects 段、恢复也不写项目段；未知存储
  frequency 的输出与输入枚举校验见 C198。
- fs / S3 / GCS / WebDAV 存储后端、cron 调度与保留清理由 §108 接入（C197 已解决）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| owner 门禁与版本校验先行、备份文档根顺序 / 白名单 / 软删 / 边与 omitempty、系统配置恢复、通道四种策略与默认值、模型 / 项目 / API key 策略、价格校验 / 归档 / 版本、用量请求双指纹去重、用量日志请求壳与跳过规则、ShouldRunBackup 频率表、RunBackupNow / TriggerAutoBackup 的记录与重抛 | `tests/AxonHub.Api.Tests/BackupServiceTests.cs` | 43 |
| autoBackupSettings 默认值与存储文档、未知 / 非法 frequency、update 合并与 owner 门禁、backup 金文档与关闭段的 null、restore 的 multipart 上传 / 提交 / 版本拒绝 / owner 门禁、triggerAutoBackup 的返回与门禁 | `tests/AxonHub.Api.Tests/AdminBackupEndpointTests.cs` | 14 |
| 系统配置白名单与软删、项目 / 通道 / 模型分页、ID 游标、价格与 API key 的活边、用量请求 / 日志的边与 includeApiKeys 门、API key 映射 | `tests/AxonHub.Persistence.Tests/BackupStoreTests.cs` | 8 |
| 事务提交 / 回滚 / 环境事务、通道查询与全列更新、ent not found 文案、设置与 updated_at、项目 / 模型 / API key / 价格 CRUD、active 版本归档、用量请求查询与 500 分批、日志批量插入 | `tests/AxonHub.Persistence.Tests/BackupRestoreStoreTests.cs` | 16 |

`AxonHub.Api.Tests` 由 2680 增至 2737（+57），`AxonHub.Persistence.Tests` 由 252 增至 276（+24），
Llm 3032、Grains 95；四个项目合计由 6059 增至 6140，0 失败 0 跳过。

差异：C197 登记未移植的 fs / S3 / GCS / WebDAV 存储后端、自动备份 cron 调度与保留清理
（非 database 存储的自动备份在端口确定性失败）；C198 登记 GraphQL 接入层差异（HotChocolate Upload
标量、未知 frequency 的输出与输入枚举校验、triggerAutoBackup 的新作用域替代 context.WithoutCancel、
32 位 Int 与解码错误文本）。

## 108. 数据存储后端与自动备份调度（fs / S3 / GCS / WebDAV、保留清理、每日 02:00，35 例，2026-11-06）

参照 `b8bb3421`：`internal/server/biz/data_storage.go:83-197`（RegisterScheduledTasks、refreshFileSystems、
buildFileSystem 的六种错误文本）、`:388-524`（InvalidateFsCache、createS3Fs / createGcsFs / createWebDAVFs
的客户端构造与 `failed to create s3/gcs/webdav filesystem: %w` 包装、GetFileSystem）、`:531-584`
（SaveData：database 纯 nil、S3 原生 PutObject、其余 afero.WriteFile）、`:589-647`（SaveDataFromReader：
database 拒绝流式写、fs 的 `filepath.FromSlash` + MkdirAll + OpenFile `O_WRONLY|O_CREATE|O_TRUNC`、
webdav 的 `TrimPrefix("/")` + mkdirAll、GCS 不裁剪）、`:651-691`（DeleteData：fs 的 `os.ErrNotExist`
视作成功、S3 幂等、webdav / gcs 透出 404）、`:696-733`（LoadData：database 返回键字节、S3 缺失 →
`os.ErrNotExist: <key>`、其余 `failed to read file: %w`）、`:870-903`（mkdirAll 的逐级 Mkdir 与
ErrExist / DirExists 回退）、`internal/server/biz/objectstore.go:59-85`（objectStoreFor 仅 S3；缺 settings
裸返、客户端构造包装 `failed to create s3 object store: %w`）、`internal/server/biz/objectstore_s3.go:26-160`
（16 MiB 分片、Put/Get/Delete 的四个错误模板、isS3NotFound 的 NoSuchKey / NotFound / 404 判定、自适应
重试 max 3）、`internal/server/backup/autobackup.go:22-32`（cron `0 2 * * *` + 系统时区、Reschedule）、
`:88-153`（performBackup 的 database / 临时文件两分支与错误前缀）、`:155-205`（cleanupOldBackups 的前缀 /
后缀过滤、按 ModTime 升序、早于 now-retention 删除、单文件失败只告警）、
`internal/server/scheduler/task.go:22-31`（TaskSpec 的 CronExpr / Timezone）。
实时上游 `f272549f` 的 `data_storage.go` / `objectstore_s3.go` 与冻结基线相比只多了 S3 的
`RequestChecksumCalculation` / `ResponseChecksumValidation` 配置与 HTTPClient 测试注入接缝（以及对应的
`newS3ClientWithHTTPClient` / `newS3ObjectStoreWithHTTPClient`），`autobackup.go`、`objectstore.go`、
`scheduler/` 零差异；端口以冻结基线为准。

新增 `Api/Storage/DataStorageBackend.cs`（`IDataStorageBackend` / `DataStorageEntry` /
`IDataStorageBackendFactory`）、`Api/Storage/DataStorageBackendFactory.cs`、`Api/Storage/FsDataStorageBackend.cs`、
`Api/Storage/S3DataStorageBackend.cs`（AWSSDK.S3 4.0.20.4）、`Api/Storage/GcsDataStorageBackend.cs`、
`Api/Storage/WebDavDataStorageBackend.cs`、`Api/Storage/CountingStream.cs`；
`Api/Management/AutoBackupSchedule.cs`（`0 2 * * *` 的下一触发时刻）与 `Api/Management/AutoBackupScheduler.cs`
（`BackgroundService`）；`BackupService.PerformBackupAsync` 的非 database 分支、`CleanupOldBackupsAsync`
与 `CreateBackend` 的错误分层；`Program` 注册工厂与托管服务，`Directory.Packages.props` / csproj 增加
AWSSDK.S3。

- 后端工厂：database → `database storage does not support file system operations`；fs 缺目录 →
  `directory not configured for fs storage`；s3 / gcs / webdav 缺设置块分别 → `s3 settings not configured` /
  `gcs settings not configured` / `webdav settings not configured`；未知类型 → `unsupported storage type: <type>`
  （裸文本）；客户端构造失败在工厂内包装为 `failed to create s3/gcs/webdav filesystem: %w`。
- 错误分层：`SaveData` / `SaveDataFromReader` / `LoadData` / `DeleteData` 对 S3 先走 `objectStoreFor`，
  因此 S3 只出现 `failed to create s3 object store: %w`、永不出现 `failed to get file system:`；fs / gcs / webdav
  在调用侧得到 `failed to get file system: <buildFileSystem 文本>`；database 的流式写 → 
  `database storage does not support streaming writes`；写入 / 删除的尾部统一 `, key: %s`；
  `failed to create directory:` / `failed to create file:` / `failed to write file:` / `failed to remove file:` /
  `failed to read file:`。
- fs：BasePathFs 等价为固定根目录 + 键的单前导分隔符剥离（Go `filepath.Join` 语义）+ 越界拒绝
  `invalid path: <key>`；写入时逐级建目录并以截断模式打开；删除缺失键视为成功；列表只返回直接子项，
  修改时间取文件系统时间。
- S3：对象键只剥离一个前导 `/`；写入为一次 PutObject（不切分片）；读取缺失 →
  `FileNotFoundException("file does not exist: <key>")`（对应 `fmt.Errorf("%w: %s", os.ErrNotExist, key)`）；
  删除幂等；列表用 Delimiter `/` + 分页，名字取 basename，修改时间缺失时用 `DateTime.MinValue`。
- GCS：凭据在构造期解析（service_account 的 RSA PEM 立即校验、authorized_user 的 refresh_token），
  构造期失败为 `failed to create gcs filesystem: failed to parse GCP credentials: …`；访问令牌惰性获取并
  缓存（60 秒提前量），service_account 以 RS256 JWT-bearer、authorized_user 以 refresh_token 交换；
  上传为 media upload、读取 / 删除 / 列表走 JSON API，404 → `storage: object doesn't exist`，401 重试一次；
  列表分页并解析 `updated`。
- WebDAV：URL = 配置 URL + Path-or-Directory（单前导 `/` 裁剪，兼容 Synology/NAS），Basic 认证仅在用户名或
  密码非空时发送，超时 10 分钟；写入先逐级 MKCOL（2xx / 405 / 409 / 301 / 302 视为已存在）再 PUT；
  读取非 2xx → `failed to read file: <status>`；删除 404 抛错（gowebdav 语义）；列表为 PROPFIND Depth 1，
  跳过集合自身并解析 `getlastmodified`，只返回直接子项。
- 自动备份：database 存储生成完整文档后跳过保存（参照的 SaveData 为纯 nil）；其余存储写临时文件
  `axonhub-backup-*.json`，依次 `failed to create temp backup file:` / `failed to create backup:` /
  `failed to seek backup file:` / `failed to write backup file:`，结束后删除临时文件；成功后可记录
  `uploaded` 日志（端口新增，参照无此日志）。
- 保留清理：`RetentionDays > 0` 时在备份成功后执行；`axonhub-backup-` 前缀 + `.json` 后缀、按修改时间升序、
  删除早于 `UtcNow - RetentionDays` 的条目；`failed to get data storage filesystem:` / `failed to list backups:`
  与单文件删除失败都只记日志、不向上抛出（database 存储下同样执行并吞掉文件系统错误）。
- 调度：以 `BackgroundService` 轮询替代 robfig/cron，最多每分钟重读一次系统时区，按系统墙钟计算严格晚于
  “现在”的下一个 02:00；设置变更无需显式 Reschedule（下一轮自会按当前设置执行），错过的 02:00 不补跑；
  DST 缺口按 Go `time.Date` 语义向前归一（纽约 2026-03-08 的 02:00 → 03:00 -04），歧义取首个偏移
  （柏林 2026-10-25 的 02:00 → +02:00）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| fs 往返与父目录创建、流式写入字节数、列表名字与修改时间、删除幂等、越界键拒绝；工厂六种错误文本、GCS 凭据包装、S3 键规范化 | `tests/AxonHub.Api.Tests/StorageBackendTests.cs` | 12 |
| WebDAV 基本认证与 10 分钟超时、根集合与多级 MKCOL + PUT、读取状态文案、PROPFIND 解析；GCS authorized_user 与 service_account 令牌、媒体上传、令牌缓存、404 映射、分页与时间解析 | `tests/AxonHub.Api.Tests/StorageProtocolTests.cs` | 10 |
| 02:00 的当日 / 次日边界、上海墙钟、纽约春季缺口、柏林秋季歧义 | `tests/AxonHub.Api.Tests/AutoBackupScheduleTests.cs` | 5 |
| 非 database 存储的流式备份、文件系统构造 / 未知类型 / 流式写失败的包装、保留清理的过滤与失败容忍（含 database 存储仍执行、单文件删除失败保留） | `tests/AxonHub.Api.Tests/BackupServiceTests.cs`（新增 8 例） | 8 |

`AxonHub.Api.Tests` 由 2737 增至 2772（+35），Persistence 276、Llm 3032、Grains 95，四个项目合计由
6140 增至 6175。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `InlineData` 的静态
计数；下一次全量运行应以 2772 / 6175 复核。**

差异：C197 由真实缺口改为已解决（四种后端、保留清理与每日 02:00 调度均已接入）；C199 登记实现差异
（S3 单次 PutObject 取代 16 MiB 分片、GCS / WebDAV 为手写客户端而非 gcsfs / gowebdav、按操作创建客户端、
第三方库错误文案为语义近似、调度轮询最多 1 分钟延迟）。

## 109. API key profile template 管理面（api_key_profile_templates / 四个变更 / linkedProfilesCount，21 例，2026-11-06）

参照 `b8bb3421`：`internal/ent/schema/api_key_profile_template.go:20-87`（TimeMixin + SoftDeleteMixin、
`api_key_profile_templates_by_project_name` 唯一索引 (project_id, name, deleted_at)、`profile` 为
objects.APIKeyProfile 的 JSON 列、project 边 Unique / Immutable / Required、QueryField + RelayConnection +
MutationCreate / MutationUpdate、Query 策略 UserProjectScopeReadRule(read_api_keys) →
APIKeyProjectScopeReadRule(read_api_keys) → OwnerRule()、Mutation 策略 UserProjectScopeWriteRule(write_api_keys)
→ OwnerRule()，无 API key 变更规则）、`internal/server/biz/api_key_profile_template.go:29-419`
（CreateTemplate 的 profile 身份覆写与 `normalizeAndValidateProfileRoutingPolicy` 两条错误文案、GetTemplate /
GetForRead 的 `exactly one of template id or name must be provided`、UpdateTemplate 的事务内发布与
syncLinkedProfiles / 仅改名时 Clone 重发布、DeleteTemplate 的 detachLinkedProfiles 与预删行返回、
LoadTemplate 的模板应用与名称冲突 `"%s (%d)"`、personal 守卫两条文案、CountLinkedProfiles 的模板 ID 或
legacy 结构匹配、detachLinkedProfiles 只认显式 TemplateID）、`internal/server/biz/api_key_profile_template_test.go:35-765`
（10 个参照测试）。实时上游 `f272549f` 的 schema、biz 文件和模板相关 SDL / resolver 与冻结基线逐字节一致；
端口以冻结基线为准。

新增 `Persistence/Stores/ApiKeyProfileTemplateStore.cs`（EF Core 写 + Dapper 读、jsonb、软删、唯一冲突类）、
`Api/Management/ApiKeyProfileTemplateService.cs`、`Api/Management/ApiKeyProfileTemplatePrivacy.cs`、
`Api/GraphQL/ApiKeyProfileTemplateTypes.cs`；`Records.cs` / `DatabaseSchema.cs` / `AxonHubDbContext.cs` 增加
`api_key_profile_templates` 表与索引；`ApiKeyProfilesCodec` 增加 `SerializeProfile` / `DeserializeProfile` /
`NormalizeAndValidateRoutingPolicy`；`AdminQuery` / `AdminMutation` 接入根连接、node 与四个变更；`Program`
注册服务。

- 创建：profile 必填（GraphQL `profile!`），嵌入 profile 的 Name 被 input.Name 覆写、TemplateID / TemplateName
  清空，随后路由策略归一化并校验（`profile '<name>' loadBalanceStrategy is invalid` /
  `profile '<name>' traceStickyMode is invalid`）；描述缺省空串；项目来自 gid；唯一冲突 →
  `xerrors.DuplicateNameError("Template", input.Name)`，其余存储失败 `failed to create template: %w`。
- 读取：GetTemplate 包装 `failed to get template: %w`；GetForRead 要求 id / name 恰有一个；
  node / 分页根字段走查询隐私（项目谓词），CREATED_AT 排序重写为 id。
- 更新：事务内先读（`failed to get template: %w`），profile 缺省且改名时以既有 profile 的 Clone 重新发布；
  清空嵌入模板字段后归一化（在 Name 覆写之前），写库后 syncLinkedProfiles 把新 profile 发布到显式
  TemplateID 匹配与 legacy 结构匹配的 key（保留每个 key 的本地 profile Name 别名）；唯一冲突 →
  DuplicateName(input.Name)，其余 `failed to update template: %w`。
- 删除：事务内读取（`failed to get template for deletion: %w`）、只脱钩显式 TemplateID 的 key、软删并向
  GraphQL 返回预删行。
- 加载：模板 / key 分别包装 `failed to get template: %w` / `failed to get API key: %w`；项目不一致
  `template and API key must belong to the same project`；personal key 需创建者或系统 owner；profile 为空
  `template has no profile`；名称冲突从 1 起编号 `"%s (%d)"`；追加后写回并包装
  `failed to update API key profiles: %w`。
- linkedProfilesCount：按模板项目读取可见 API key（包装
  `failed to list API keys linked to template: %w`），统计 TemplateID 命中或 legacy 结构匹配的数量。
- GraphQL：gid ID、GoTime / Cursor、`profile` JSON 形状、`project` 边经 Project 隐私仿真（过滤为
  `ent: project not found`）、`APIKeyProfileTemplateOrderField { CREATED_AT, UPDATED_AT }`、where 输入覆盖
  entgql 生成的比较谓词与 hasProject / hasProjectWith。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 创建的身份覆写与路由策略、SQL NULL profile、DUPLICATE_NAME 扩展与项目作用域、Get / GetForRead 错误包装、更新发布到显式与 legacy 关联 key（保留本地别名）、仅改名重发布、加载追加与名称冲突、未命名 profile 回退、缺失 / 跨项目 / 空 profile、personal 守卫、列表的项目边与查询作用域、删除返回预删行 | `tests/AxonHub.Api.Tests/ApiKeyProfileTemplateServiceTests.cs` | 14 |
| 根连接与 node 线形（gid / GoTime / Cursor / profile JSON / linkedProfilesCount / project 边）、create / update / delete 往返与 DUPLICATE_NAME 扩展、load 变更追加 profile | `tests/AxonHub.Api.Tests/AdminApiKeyProfileTemplateEndpointTests.cs` | 3 |
| jsonb 与 SQL NULL、按项目重名与软删名复用、列表 id 顺序 / 项目边 / 访问作用域、部分列更新与写谓词 | `tests/AxonHub.Persistence.Tests/ApiKeyProfileTemplateStoreTests.cs` | 4 |

`AxonHub.Api.Tests` 由 2772 增至 2789（+17），`AxonHub.Persistence.Tests` 由 276 增至 280（+4），Llm 3032、
Grains 95，四个项目合计由 6175 增至 6196。**本切片按项目规则未运行 lint / build / test，上述数字为
`[Fact]` 的静态计数；下一次全量运行应以 2789 / 280 / 6196 复核。**

差异：C200 登记顺序确定化（ListForProject 按 id 升序）与已接受差异（事务边界按方法收窄、逐 key 更新的语句
形状）；C176(6) 的 apiKeyProfileTemplates 延迟项闭合。

## 110. 邀请与注册 REST 面（invitations / 创建 / 查询 / 注册 / 角色删除撤销，59 + 14 例，2026-11-06）

参照 `b8bb3421`：`internal/ent/schema/invitation.go:20-58`（TimeMixin + SoftDeleteMixin、`token_hash` 唯一
索引与 `project_id` 索引、project 边 Required、`entgql.Skip(entgql.SkipAll)` 故 SDL 无邀请成员）、
`internal/server/biz/invitation.go:66-335`（CreateInvitation 的 canInvite → 参数校验 → 项目 / 角色 /
CanGrantRole → 落库，GetInvitation 的过期与用量判定，RegisterInvitation 的单事务、条件扣减与后置重载，
invitationExpiry / invitationInfo / invitationIsExpired / generateInvitationToken / hashInvitationToken）、
`internal/server/biz/role.go:181-268`（DeleteRole / BulkDeleteRoles 的事务内 revokeRoleInvitations → 角色
软删，`failed to revoke role invitations: %w`）、`internal/server/api/invitation.go:70-138`（三个 handler 与
InvitationResponse / 注册响应）、`internal/server/routes.go:87-88,112`（public 对与 adminGroup
`POST /invitations`）、`internal/server/biz/invitation_test.go`（7 个参照测试）。实时上游 `f272549f` 的
schema / biz / api / role 文件与冻结基线逐字节一致。

新增 `Persistence/Stores/InvitationStore.cs`（EF Core 写 + Dapper 读、软删、token_hash 查找与 WithProject
名称、RegisterAsync 单事务、DeleteByRoleIdsAsync 加入环境事务）、`Api/Management/InvitationService.cs`、
`Api/Auth/InvitationHandlers.cs`（MapInvitationRoutes）；`Records.cs` / `DatabaseSchema.cs` /
`AxonHubDbContext.cs` 增加 `invitations` 表与两个索引；`RoleManagementService.DeleteAsync` /
`BulkDeleteAsync` 复用 `IRoleStore.RunInTransactionAsync` 调用撤销；`Program` 注册服务与路由，
`/admin/invitations` 走 AdminJwtAuthMiddleware + ProjectIdMiddleware。

- 创建：canInvite 先于一切校验（上下文用户缺失 `user not found in context`；系统 `write_users`、项目
  owner、成员 `user_projects.is_owner` 或成员 scopes / 项目角色 scopes 含 `write_users` 放行；否则
  `permission denied: project user management access required`，且项目角色只在匹配 membership 内被咨询）
  → roleID 0 `project role is required` → maxUses 仅 0 / 1 `max uses must be 1 or 0` → expiry（nil 7 天、
  <0 `expiration must not be negative`、0 永不过期、>100000 `expiration hours exceed maximum limit`）→
  6 字节 base64url token → 项目读取（包装 `failed to get project: %w`，缺失 `ent: project not found`）→
  角色按 id 且必须属于该项目（任何查找错误折叠为 `project role not found`）→ CanGrantRole
  （`permission denied: %w`）→ 落库（`failed to create invitation: %w`）。响应为 InvitationResponse：token
  仅创建返回，maxUses 0 时 remainingUses 恒 0。
- 查询：按 SHA-256 小写 hex 查 token；未知 / 软删 / 存储错误 → `invitation not found`；过期或用量耗尽 →
  `invitation is no longer valid`；项目名取 WithProject 边，项目被软删时参照 panic 而端口返回空串（C201）。
- 注册：email TrimSpace，空 email / password → `email and password are required`；password 按 UTF-8 字节数
  < 7 → `password must be at least 7 characters`；单事务内重读邀请（`invitation not found`）、过期 / 用量
  （`invitation is no longer valid`）、role_id 空（`invitation role is required`）、角色 live 且同项目
  （`invitation role is no longer available`）、邮箱占用（`a user with this email already exists`；查询失败
  `failed to check existing user: %w`）、条件更新 `used_count = used_count + 1`（带 expires_at / max_uses
  谓词，失败 `failed to use invitation: %w`，影响 0 行视为失效）、建用户（`failed to create user: %w`）、
  建成员（`failed to add user to project: %w`）、绑角色（`failed to assign invitation role: %w`）；提交后
  重载用户及其 roles / projectUsers / oidcIdentities 生成 UserInfo。
- 角色删除：DeleteRole / BulkDeleteRoles 在同一事务内先 `DeleteByRoleIdsAsync`（只软删 live 且 role_id
  命中的邀请），失败输出 `failed to revoke role invitations: %w` 并整体回滚；默认 Developer 角色仍先被拒绝。
- REST：`POST /admin/invitations` 绑定失败 400 `invalid invitation request`、服务错误 403、项目头缺失 400
  `X-Project-ID header is required`；`GET /auth/invitations/:token` 错误 404；
  `POST /auth/invitations/:token/register` 绑定失败 400 `invalid registration request`、服务错误 400、
  签发会话失败 500 `failed to create session`（错误信封为 gin 的 `{"error":{"type":…,"message":…}}`）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 单次 / 无限邀请、未迁移的 legacy 邀请、不可授予的角色、已删除角色、过期、token 形状（8 字符）、canInvite 六条路径、创建参数校验与错误包装、查询 / 注册全部分支、注册后重载 | `tests/AxonHub.Api.Tests/InvitationServiceTests.cs` | 33 |
| JWT 门与 Bearer 文案、项目头、绑定失败（roleID 0 / null / 缺失 / 坏 JSON）、响应形状与 null expiresAt、错误分类、gid 校验、查询剥离 token、注册建用户 + 会话、密码按 rune 计、缺系统密钥 500 | `tests/AxonHub.Api.Tests/AdminInvitationEndpointTests.cs` | 22 |
| 角色删除 / 批量删除的撤销、撤销失败回滚、默认角色提前拒绝 | `tests/AxonHub.Api.Tests/ManagementServiceTests.cs` | 4 |
| 列往返与 project 边、默认时间戳、软删过滤、软删项目空名、注册的用户 / 成员 / 角色 / 用量、二次使用拒绝、无限次、过期 / legacy / 软删角色 / 跨项目角色 / 已占用邮箱、撤销只命中 live、加入环境事务与回滚 | `tests/AxonHub.Persistence.Tests/InvitationStoreTests.cs` | 14 |

`AxonHub.Api.Tests` 由 2789 增至 2848（+59），`AxonHub.Persistence.Tests` 由 280 增至 294（+14），Llm 3032、
Grains 95，四个项目合计由 6196 增至 6269。**本切片按项目规则未运行 lint / build / test，上述数字为
`[Fact]` / `[InlineData]` 的静态计数；下一次全量运行应以 2848 / 294 / 6269 复核。**

差异：C201 登记邀请 REST 片段的边界行为（软删项目边、时间精度、JSON 解码、`%w` 内层文本、密码哈希时机、
提交后重载）；C136 由 no-op 转为 resolved（角色删除同事务撤销）。

## 111. Antigravity OAuth 管理面（start / exchange / 项目发现，35 + 26 例，2026-11-06）

参照 `b8bb3421`：`internal/server/api/antigravity.go`（AntigravityHandlers 的 StartOAuth / Exchange、
parseAntigravityCallbackURL、resolveProjectID、onboardUser、generateAntigravityState / CodeVerifier /
CodeChallenge）、`internal/server/api/error.go:12-20`（JSONError 信封）、`internal/server/routes.go:106,124-125`
（adminGroup 的 JWT + WithProjectID 与两条 POST）、`llm/httpclient/proxy.go` 与 `client.go:71-215`
（ProxyConfig / NewHttpClientWithProxy / getProxyFunc）。参照没有该文件的测试。实时上游 `f272549f` 的
`antigravity.go` / `routes.go` 与冻结基线逐字节一致。

新增 `Api/Management/AntigravityOAuthService.cs`（PKCE、回调校验、令牌交换、项目发现）、
`Api/Auth/AntigravityOAuthHandlers.cs`（HTTP 绑定、gin 信封、MapAntigravityOAuthRoutes）、
`Api/Auth/AntigravityOAuthCache.cs`（go-cache 到期语义的进程内状态缓存）、`Llm/Http/ProxyConfig.cs` 与
`Llm/Http/ProxyExecutorFactory.cs`（proxy 参数的 url 分支与传输选项）；`Program` 注册 DI、两条路由与
`/admin/antigravity/oauth` 的 AdminJwtAuthMiddleware + ProjectIdMiddleware。

- start：绑定失败（空 body / 坏 JSON / 类型不符）400 `invalid request format`，JSON `null` 视为零请求；
  32 字节 state、64 字节 verifier、SHA-256 base64url challenge；缓存键 `antigravity:oauth:<state>`，值为
  `{code_verifier, project_id, created_at}`，TTL 10 分钟（到期判定 `now > expiresAt`，恰在截止时刻仍有效）；
  Set 失败 500 `failed to save oauth state: %w`；授权 URL 为 authorize 端点 + url.Values.Encode（键序
  access_type、client_id、code_challenge、code_challenge_method、prompt、redirect_uri、response_type、
  scope、state）；响应 `{session_id, auth_url}`。
- exchange：绑定失败或空 session_id / callback_url 400 `invalid request format`；缓存未命中 400
  `invalid or expired oauth session`；回调解析按 TrimSpace → http(s) 前缀（`callback_url must be a full URL`）
  → URL 解析（`invalid callback_url: %w`）→ code（`code parameter not found in callback_url`）→ state
  （`state parameter not found in callback_url`）的顺序报 400；state 不一致 400 `oauth state mismatch` 且不消费
  会话；校验成功后删除会话（删除失败只告警）；proxy 仅在 type=url 且 url 非空时生效；令牌交换失败 502
  `token exchange failed: %w`（provider 文本已含一层同名前缀，故常见为双层）；project_id 为空时按
  Prod / Daily / Autopush 顺序 POST loadCodeAssist，项目可来自 `cloudaicompanionProject` 的字符串或 `{id}`
  对象，缺失时按 allowedTiers 首项 / isDefault 选 tier 调 onboardUser（每端点 3 次、失败 1s、未完成 2s），
  全部失败 502 `failed to resolve project id and none provided: %w`；成功输出 `refreshToken|projectID`。
- 线路形状：令牌请求为 form（client_id、client_secret、code、code_verifier、grant_type、redirect_uri），
  Accept `application/json` 与 Antigravity UA；loadCodeAssist / onboardUser 请求带 Bearer、Content-Type、
  User-Agent、X-Goog-Api-Client、Client-Metadata，body 键序与参照逐字节一致。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| PKCE 形状与逐调用随机、授权 URL 键序、项目存储、TTL 截止边界、回调完整解析、令牌请求逐字节、provider / 传输错误包装、项目 string/object/大小写、tier 选择、端点回退与状态、onboard 3 次重试顺序、代理生效与忽略、空 refresh token | `tests/AxonHub.Api.Tests/AntigravityOAuthServiceTests.cs` | 35 |
| JWT 门与 Bearer 文案、绑定失败（空 / 坏 JSON / null / 缺字段）、gin 信封、项目头校验、缓存失败 500、未知 / 过期会话、非 URL 回调、state 不一致、502 分类、会话单次消费、代理接线 | `tests/AxonHub.Api.Tests/AdminAntigravityOAuthEndpointTests.cs` | 26 |

`AxonHub.Api.Tests` 由 2848 增至 2909（+61），Llm 3032、Grains 95、`AxonHub.Persistence.Tests` 294，四个项目
合计由 6269 增至 6330。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]`
的静态计数；下一次全量运行应以 2909 / 294 / 6330 复核。**

差异：C121 由 gap 转为 resolved；C202 登记代理、传输与 JSON 解码的残余细节（错误文案、连接池、
ProxyFromEnvironment、ShouldBindJSON 的尾随内容、xcache 多后端）。

## 112. 配额路由启动数据迁移（claim / legacy 映射 / allowedChannelIDs 改写 / 标记，19 + 7 例，2026-11-06）

参照 `b8bb3421`：`internal/server/biz/quota_routing_migration.go`（132 行，`Migrate`:28-33、
`migrateInTransaction`:35-110、`quotaRoutingModeFromLegacy`:112-123、`QuotaRoutingModeFromLegacy`:125-131）、
`internal/server/biz/fx_module.go:117-128`（ProviderQuotaService 的 OnStart 在 goroutine 里执行迁移
`(&quotaRoutingMigrator{system: systemSvc, channels: channelSvc}).Migrate(context.Background())`，失败只
`log.Error("quota routing migration failed")`，不阻塞也不使启动失败）与 `internal/server/biz/abstract.go`
的 `RunInTransaction`（已有外层事务时复用）。参照测试 `quota_routing_migration_test.go`（1 个 Test、9 个
`t.Run`）与 `quota_routing_migration_postgres_test.go`（`AXONHUB_TEST_PG_DSN` 门控，3 个 `t.Run`：legacy
缺失只写标记、legacy + allowed 通道只迁移一次、4 路并发启动幂等）。实时上游 `f272549f` 的该文件与冻结
基线逐字节一致。

新增 `Api/Management/QuotaRoutingMigrationService.cs`（claim、legacy 解码与映射、通道改写、标记与全部
错误包装）、`Api/Management/QuotaRoutingMigrationHostedService.cs`（宿主启动后在独立 scope 里执行一次，
`CancellationToken.None`，失败只记录错误）；`Program` 注册两者。持久化侧补齐 `ISystemStore.CreateIfAbsentAsync`
（`ON CONFLICT ("key") DO NOTHING`，软删行同样冲突）与 `ISystemStore.RunInTransactionAsync`（复用
`db.Database.CurrentTransaction`，否则新开并在本调用内提交 / 回滚）；`ChannelWriteStore.UpdateAsync` 改为
加入 ambient 事务而不是每次新开，使通道更新与 systems 写入落在同一事务。

- 事务与 claim：`uuid.NewString()` 等价的 `Guid.NewGuid().ToString()` 写入 `quota_routing_migration_done`；
  读回时应用软删谓词，缺失按 `ent: system not found` 报错；`marker.Value != claim` 直接返回，因此并发启动
  只有一个实例执行迁移。
- legacy 与新键：`quota_enforcement_settings` 缺失时只把标记写为 `"true"`（此路径不套 outer 包装）；解码按
  `{"enabled","exhaustedOnly","dePrioritize","allowedChannelIDs","mode"}`；`quota_routing_mode_from_legacy`
  的五条映射沿用 §95；`quota_routing_settings` 仅在缺失时写入 `{"defaultMode":<mode>}`（GoJson、模式校验）。
- allowedChannelIDs：逐项按 ent `Channel.Query().Where(IDEQ).Only` 读取（缺失只 Warn 并跳过），把
  `settings.quotaRoutingMode` 置为 `ignore_quota` 后经 `ChannelLifecycleService.UpdateAsync` 写回
  （`ChannelService.UpdateChannel` 的校验顺序、Go JSON 序列化与 `channel was updated concurrently` 守卫）。
- 错误文案：claim、标记读取、legacy 读取（`failed to get system value` 双层）、解码、新键检查、新键写入
  （`failed to create system setting` 双层）、通道加载、通道写入（`failed to migrate channel %d`）与最终
  标记写入（双层）逐条与参照一致；任一失败回滚整条事务，下次启动重试。
- 启动时序：hosted service 的 `Task.Yield()` + 独立 scope 复刻 goroutine 的非阻塞、失败只打日志语义。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 全新迁移、重跑与 4 路并发幂等、缺失通道跳过、nil settings、既有新键、空 allowed 列表、legacy 缺失、失败回滚后重试、10 个错误前缀 | `tests/AxonHub.Api.Tests/QuotaRoutingMigrationServiceTests.cs` | 19 |
| claim 插入 / 软删冲突、事务提交 / 回滚 / 复用 ambient、4 路并发抢占恰有一个 owner | `tests/AxonHub.Persistence.Tests/SystemStoreTests.cs` | +6 |
| 通道更新加入 ambient 事务，channels 与 systems 写入随外部回滚一起消失 | `tests/AxonHub.Persistence.Tests/ChannelWriteStoreTests.cs` | +1 |

`AxonHub.Api.Tests` 由 2909 增至 2928（+19），`AxonHub.Persistence.Tests` 由 294 增至 301（+7），四个项目
合计由 6330 增至 6356。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]`
的静态计数；下一次全量运行应以 2928 / 301 / 6356 复核。**

差异：C183 由 accepted 转为 resolved；日志字段形态（`channel_ids=1,2` 对照 `log.Any([]int)`）与
System.Text.Json 的解码文案沿用 C095 / C120 与既有 JSON 文案差异的口径，不另立条目。

## 113. GC 清理与存储策略（worker / cron / GraphQL，54 + 9 例，2026-11-06）

参照 `f272549f`：`internal/server/gc/gc.go`（795 行，`RegisterScheduledTasks`:77-84、`runCleanup`:114-243、
`cleanupRequests`:246-274、`cleanupOldRequestExecutions`:276-328、`cleanupOldRequestsRecords`:330-373、
`cleanupUsageLogs`:489-527、`cleanupThreads`:529-566、`cleanupTraces`:568-604、`cleanupChannelProbes`:607-640、
`runVacuum`:643-690、`RunCleanupNow`:698-717、`PreviewCleanup`:720-795）、`gc_bodies.go`（484 行，
`cleanupBodyPayloads`:46-82、`previewBodyCleanup`:71-109、`stripRequestPayloads`:111-180、
`stripExecutionPayloads`:182-365、`primaryStorageID`:367-382）、`gc_internal.go`（12 行）、
`internal/server/biz/system.go:960-1044`（`StoragePolicy` / `SetStoragePolicy`）与
`system_default.go:11-77`（默认清理项、合并、默认策略）、`internal/server/scheduler`
（`TaskSpec` / `Register` / `CRONRule{Timezone:"UTC"}`）、`internal/server/gql/system.graphql`
（`StoragePolicy` / `CleanupOption` / `UpdateStoragePolicyInput` / `TriggerGcCleanupInput` /
`GcCleanupPreviewItem`、`storagePolicy`、`updateStoragePolicy`、`triggerGcCleanup`、`previewGcCleanup`）与
`system.resolvers.go`（85-92、301-321、500-515、591-593）。实时上游的 GC 文件与冻结基线一致。

新增 `src/AxonHub.Api/Management/GcWorker.cs`（清理编排、分批删除、外部存储对象删除、body 清除、预览）、
`GcCronSchedule.cs`（五字段解析与下一次触发）、`GcScheduler.cs`（宿主托管，UTC 调度）、
`RequestDataKeys.cs`（`/{project}/requests/...` 键生成）、`src/AxonHub.Api/Settings/StoragePolicy.cs` /
`StoragePolicyCodec.cs` / `SystemSettingsAdminService.cs`（策略默认值、合并、Go 形状 JSON、读写与错误包装）、
`src/AxonHub.Persistence/Stores/GcStore.cs`（Dapper SQL）与 `DataStorageStore.cs`（含软删的主存储 / 按 id 查询）、
GraphQL 的 `AdminQuery` / `AdminMutation` / `SystemSettingsTypes` 与 `AuthorizationScope` 的系统 scope 判定；
`Program` 在启动时解析 cron 并注册 worker 与 hosted service。

- 配置与调度：`gc.cron` 默认 `0 2 * * *`、`vacuum_enabled` 默认 true、`vacuum_full` 默认 false；读取
  `Gc:Cron` / `Gc:VacuumEnabled` / `Gc:VacuumFull`，缺省回退 `AXONHUB_GC_CRON` /
  `AXONHUB_GC_VACUUM_ENABLED` / `AXONHUB_GC_VACUUM_FULL`。启动时急切解析 cron，失败抛
  `register task "gc": ...`；任务时区固定 UTC，进程停机期间错过的触发不回补，单次运行失败只记录日志。
- 资源过滤：读取 storage policy 失败只记录并返回；`option.Enabled || manual` 才执行，manual 过滤掉
  `manualDays` 中不存在的资源类型并覆盖天数（`> 0` 才进入映射）；requests 分支按
  executions → requests → threads → traces 顺序执行，三者错误互相隔离；未知资源类型只 Warn；
  channel probes 每次以 3 天窗口运行（端口无该表，no-op，C195）；`vacuum_enabled` 时最后执行 VACUUM。
- 分批删除：`DefaultBatchSize = 500`，按 id 升序，排除 `traces.status='retained'` 的请求及其
  executions / usage_logs（`NOT EXISTS` 子查询）；每批先删外部对象再删行；错误包装为
  `failed to query old ...: %w`、`failed to delete ... batch: %w`，外层为
  `failed to cleanup request executions: %w` / `failed to cleanup requests: %w` /
  `failed to delete old usage logs|threads|traces: %w`。
- 外部存储：`data_storage_id` 为 null/0 跳过；按 id 读取含软删行并在本次清理内缓存；空行按
  `ent: data storage not found` 处理（请求删除路径 Warn 后继续，body 清除路径跳过该行）；primary 行跳过。
  请求键序为 body / response body / chunks，fs / webdav 追加 executions 目录与请求目录；execution 键序为
  body / response body / chunks，fs / webdav 追加 execution 目录；单个键删除失败只 Warn。
- body 清除：`days <= 0` 直接返回；主存储查询缺失时抛
  `get primary data storage for body cleanup: failed to get primary data storage: ent: data storage not found`；
  请求与 execution 两条清除总是都执行，错误以 `\n` 合并返回。占位符谓词用
  `CAST(col AS TEXT) NOT IN (...)`（body/headers 为 `{}`、`null`、空串，chunks 为 `[]`、`{}`、`null`、空串），
  外部存储的 NULL 子句仅在 primaryID > 0 时追加（`data_storage_id <> primary`），request_bodies 的额外子句
  还要求 `request_headers IS NOT NULL`；外部删除失败的 id 进入下一轮 skipIds。清除字面量为
  `request_body` / `response_body` 写 `CAST('{}' AS jsonb)` 且 headers 置 NULL、`response_chunks` 写
  `CAST('[]' AS jsonb)`，同时刷新 `updated_at`。
- 预览：捕获一次 now；顺序固定为 requests、usage_logs、request_bodies、response_bodies、response_chunks；
  非正天数跳过；body 窗口按天数缓存并复用同一条目（只改写 ResourceType）；计数为窗口内 requests +
  executions，保留 trace 排除；错误包装为 `failed to count requests for preview: %w`、
  `failed to count usage logs for preview: %w`、`failed to count %s on requests: %w`、
  `failed to count %s on executions: %w`。
- 存储策略：缺失行返回默认策略（五个清理项按 requests / usage_logs / request_bodies / response_bodies /
  response_chunks，天数 3 / 30 / 7 / 7 / 3，全部 disabled）；解码时缺失的 `store_request_body` /
  `store_response_body` 键按 true 处理；`MergeCleanupOptions` 首见保序、重复项后者胜、未知类型保留、
  缺失默认项追加；序列化字段顺序为 store_chunks、live_preview、store_request_body、store_response_body、
  cleanup_options；`SetStoragePolicy` 拒绝非正天数（
  `cleanup_days for %q must be positive; set enabled=false to keep data forever`），写入失败包装
  `failed to create system setting: %w`。
- GraphQL：`storagePolicy`（无 scope 检查，读取失败直接透出）、`updateStoragePolicy`（无 scope 检查，
  包装 `failed to update storage policy: %w`；gqlgen 直接解码进 `biz.StoragePolicy`，输入缺省的布尔字段
  为 false、缺省的 cleanupOptions 为 nil）、`previewGcCleanup`（`read:settings`，错误原样透出，`Time` 标量）
  与 `triggerGcCleanup`（`write:settings`，立即返回 true，后台 detached 运行）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 默认策略 / 合并顺序 / 缺键 body 标志 / 显式 false / Go 字段序 / 回环 / 读写的错误包装 / 非正天数校验 | `tests/AxonHub.Api.Tests/StoragePolicyTests.cs` | 13 |
| 默认表达式、UTC 转换、步长与范围、月份 / 星期名、7=周日、Vixie OR 语义、10 组非法文案、永不匹配、配置默认与 env 回退 | `tests/AxonHub.Api.Tests/GcCronScheduleTests.cs` | 14 + 10 |
| 自动 / 手动资源过滤与天数覆盖、零天数、策略失败中止、主存储缺失、外部删除失败 skip、外部对象键序、VACUUM、预览窗口缓存与错误包装 | `tests/AxonHub.Api.Tests/GcWorkerTests.cs` | 10 |
| 策略查询、更新输入零值、更新包装、预览线形与 Time、两个 scope 门禁与后台触发 | `tests/AxonHub.Api.Tests/AdminGcEndpointTests.cs` | 7 |
| retained 排除与 id 升序、usage / execution 排除、thread / trace status、payload 占位符与外部 NULL 子句、清除字面量、按 id 删除、主存储含软删、VACUUM 事务外 | `tests/AxonHub.Persistence.Tests/GcStoreTests.cs` | 9 |

`AxonHub.Api.Tests` 由 2928 增至 2982（+54），`AxonHub.Persistence.Tests` 由 301 增至 310（+9），四个项目
合计由 6356 增至 6419。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]`
的静态计数；下一次全量运行应以 2982 / 310 / 6419 复核。**

差异：C095 的 trace GC 缺口由本节闭合并转为 resolved；GC / 存储策略的调度器结构、cron 语法、
32 位预览计数、数据存储缓存与 SQL 形态登记为 C203。

## 114. 供应商配额采集核心（ProviderQuotaService / provider_quota_status / GraphQL，111 例，2026-11-07）

参照 `f272549f`：`internal/server/biz/provider_quota.go`（`loadQuotaCache`:369、`registerProviderQuotaSupport`:374-480、
`RegisterScheduledTasks`:396-404、`getWarningCheckInterval`:518-525、`getCheckInterval`:534-540、
`GetQuotaStatus`:561-584、`InvalidateChannelQuota`:595-603、`getProviderType`:1012-1049、
`hasCredentialsForProvider`:1051-1095、`extractLimitsFromQuotaData`:1141-1219、`maxConcurrentQuotaChecks`:26、
`providerQuotaChannelTypes`:54-83）、`internal/server/biz/provider_quota/`（`types.go`、归一化与聚合、
`provider_quota_account.go:25-64` 的 `quotaAccountKey` / `groupChannelsByQuotaAccount`、`url_detection.go`、
`ListResets` / `ResetChannelQuotaNow` 与 checker / resetter 接口——19 个 checker 本体见 C182）、
`internal/server/biz/provider_quota_settings.go:12-174`、`provider_quota_cost.go`、
`channel_provider_quota_hook.go`、`internal/server/db/removed_provider_quotas.go`、
`internal/ent/schema/provider_quota_status.go`、`internal/server/gql/ent.graphql:3729-3758` 与
`ent.resolvers.go:98-142`，以及 `axonhub.graphql` 的 `checkProviderQuotas` / `resetChannelQuotaNow` /
`providerQuotaCollectionSettings` / `updateProviderQuotaCollectionSettings` 与对应 resolver。

新增 `src/AxonHub.Api/Orchestration/ProviderQuotaService.cs`（采集编排、缓存、错误退避、周期成本与重置分发）、
`ProviderQuotaStatus.cs`（`QuotaLimitStatus` / `QuotaChannelStatus` / 状态常量）、`ProviderQuotaData.cs`
（归一化、limit 过滤、克隆与 URL provider 检测）、`ProviderQuotaCheckerRegistry.cs`（`IQuotaChecker` /
`IQuotaResetter` 注册表，当前为空）、`ProviderQuotaCollectionSettingsService.cs` 与
`ProviderQuotaSettingsCache.cs`（`provider_quota_collection_settings` 的读取、归一化、写入与同步快照）、
`src/AxonHub.Api/Management/ProviderQuotaInitializationHostedService.cs` / `ProviderQuotaScheduler.cs`
（启动缓存加载与 `*/5 * * * *` UTC 调度）、`src/AxonHub.Persistence/Stores/ProviderQuotaStatusStore.cs`
（Dapper SQL）、`DatabaseSchema` 的 `provider_quota_status` 表与启动清除，GraphQL 的 `AdminQuery` /
`AdminMutation` / `ChannelTypes` / `ChannelMapping` / `SystemSettingsTypes`，`ChannelLifecycleService`
的失效钩子与 `Program` 接线。

- 受支持集合与 provider 键：`ProviderQuotaChannelTypes` 28 项按参照顺序；`GetProviderType` 把通道类型折叠到
  19 个 checker 键（`moonshot_coding` → `kimi_code`，五类 `zenmux_*` → `zenmux`，`openai` / `openai_responses`
  按 base URL 主机映射到 `synthetic` / `neuralwatt` / `apertis` / `charm_hyper`）。
- 采集设置：`provider_quota_collection_settings` 缺失时 `enabled=true`、19 个 provider 全部启用；缺失的
  provider 视为禁用；写入拒绝未知 / 重复 provider（`unsupported|duplicate provider quota type`）并按键排序
  序列化（复刻 Go `json.Marshal`）；运行时读取失败回退默认值并刷新同步快照。
- 采集调度：`Initialize` 把全部存活行装入进程内缓存；采集只选启用、类型可折叠且设置启用的通道，按 account
  key 分组（仅 zenmux 管理键参与 SHA256 分组，其余通道单成员成组），组间并行度 8；`force` 跳过
  `next_check_at` 到期判定；设置读取失败只告警并继续。
- 检查与状态：无凭据的通道硬删其配额行并清缓存；无 checker 时记录 “No checker for provider” 并保留旧行；
  checker 结果经 `Normalize`（ratio 截断、过期时间清除、分组就绪、状态推导）后 upsert；`warning` 用 4 倍
  间隔（20 分钟），其余 5 分钟；`_resets` 由 resetter 的 `ListResets` 填入（失败写 `error`）。
- 错误与重置：普通检查失败写 `{error_code,error_count}` 并保留原状态 / 重置时间；provider 变更或
  `missing_credentials` 走 reset 路径（provider / account / status=unknown / not ready / null reset /
  退避后下一检查）；错误退避为 1/2/4/8 倍间隔，计数封顶。
- 失效钩子：通道更新时 provider 身份（类型、base URL、端点、Command Code 设置）变化或配额 cookie 被清空，
  调用 `InvalidateChannelQuota`（硬删行 + 清缓存）；钩子失败只记录，不阻断更新。
- 持久化：`provider_quota_status`（jsonb `quota_data`、唯一 `channel_id`、`next_check_at` / `account_key`
  索引）；upsert 冲突只改插入列（`created_at` / `updated_at` / `deleted_at` 不动），create 依赖唯一约束
  （含软删行），错误更新与 reset 分别写字段，硬删同时清除软删行；启动脚本删除退役 provider（`wafer`）的行。
- GraphQL：`Channel.providerQuotaStatus` 缺行 / 退役 provider / 设置禁用返回 null，其余映射真实行
  （`quotaData` 为 Map 标量原样 JSON）；`providerQuotaCollectionSettings` 按受支持顺序输出；更新与
  `checkProviderQuotas`（互斥锁内同步执行）、`resetChannelQuotaNow`（`write:channels` 用户 scope）的错误
  按参照包裹。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 初始化缓存与失败保留、设置门禁、缓存副本、失效硬删、采集遍历与跳过、28 项通道类型、状态持久化与缓存、间隔与退避、失败 / 重置 / 周期成本 / zenmux 分组 / 无 checker / 无凭据 / 设置读失败 / 调度到期 | `tests/AxonHub.Api.Tests/ProviderQuotaServiceTests.cs` | 31 |
| 归一化边界、状态推导、model 过滤、退避计数、cron 表达式、limit 合并与提取、legacy 错误键、provider 键映射、URL 检测、zenmux account key、RFC3339 | `tests/AxonHub.Api.Tests/ProviderQuotaDomainTests.cs` | 21 + 27 |
| 设置默认 / 归一化 / 严格读取 / 回退 / 写入校验与错误包装 / 快照 | `tests/AxonHub.Api.Tests/ProviderQuotaCollectionSettingsServiceTests.cs` | 11 |
| `providerQuotaStatus` 缺行与枚举错误、真实行形状（Map / gid / 时间）、退役 / 禁用 / 空类型隐藏 | `tests/AxonHub.Api.Tests/AdminChannelsEndpointTests.cs` | 2 |
| 配额采集设置查询 / 更新 / 错误包装 / 路由设置写入 / 强制采集 / 立即重置 | `tests/AxonHub.Api.Tests/AdminSystemSettingsEndpointTests.cs` | 6 |
| 通道更新触发失效、身份不变跳过、cookie 清空、钩子失败不阻断 | `tests/AxonHub.Api.Tests/AdminChannelUpdateEndpointTests.cs` | 4 |
| upsert 冲突列、软删读谓词、create 唯一约束、reset / error 更新、硬删 | `tests/AxonHub.Persistence.Tests/ProviderQuotaStatusStoreTests.cs` | 6 |
| 表 / 列 / 类型 / 索引与退役行清除 | `tests/AxonHub.Persistence.Tests/SchemaTests.cs` | 1 |
| `ListEnabledByTypesAsync` 的存活 / 启用 / 类型过滤 | `tests/AxonHub.Persistence.Tests/ChannelStoreTests.cs` | 1 |
| `SumCostByChannelAsync` 的半开窗口与 NULL 合并 | `tests/AxonHub.Persistence.Tests/UsageLogStoreTests.cs` | 1 |

`AxonHub.Api.Tests` 由 2982 增至 3084（+102），`AxonHub.Persistence.Tests` 由 310 增至 319（+9），四个项目
合计由 6419 增至 6530。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]`
的静态计数；下一次全量运行应以 3084 / 319 / 6530 复核。**

差异：C182 由「子系统未移植」收窄为「19 个 checker 与供应商专属重置未移植」；C123 仅剩 antigravity checker；
C139 的 `providerQuotaStatus` 缺口闭合并转为 resolved；`QuotaLimitStatus.Type` 的默认值补偿登记为 C204，
`Map` 标量的 jsonb 键序 / 转义差异登记为 C205。

## 115. Antigravity 供应商配额检查器（quota summary / legacy 回退，61 例，2026-11-07）

参照 `f272549f`：`internal/server/biz/provider_quota/antigravity_checker.go:36-267`（`CheckQuota`:44、
`SupportsChannel`:98、`credentials`:102、摘要 404/501 回退:52-93、legacy 解析与 pool 折叠:148-267）、
`antigravity_summary.go:1-169`（envelope 优先级、`antigravityModelPool`、窗口别名、剩余额度回退链、
顺序 limits 与 RawData）、`provider_quota.go:422-424` 的 `registerAntigravitySupport`，以及
`antigravity_checker_test.go`、`antigravity_summary_test.go`。

新增 `src/AxonHub.Api/Orchestration/AntigravityQuotaChecker.cs`（`IQuotaChecker` 实现：凭据解析与 OAuth
刷新、摘要优先 / 旧式回退两段式请求、两份解析器、epoch / RFC3339 重置时间解析），
`ProviderQuotaData.cs` 新增公开的 `AntigravityPoolAvailabilityGroup`，`Program.cs` 注册
`AntigravityQuotaChecker` 与 `ProviderQuotaCheckerRegistration("antigravity", …)`，
`ProviderQuotaCheckerRegistry` 的文档同步为「仅 Antigravity 已注册」。

- 采集流程：先 POST `…/v1internal:retrieveUserQuotaSummary`（仅 legacy 凭据带 projectID 时 body 含
  `project`），只把 404/501（抛出的 `HttpClientError` 或返回的状态码）与「200 但响应体无法解析」视作
  摘要不可用并静默回退；其余摘要错误按 `fetch Antigravity quota summary: %w` 包装，非 2xx 报
  `fetch Antigravity quota summary: status %d`。回退请求 `…/v1internal:fetchAvailableModels`，复用同一
  executor（含通道代理）与同一组 `AntigravityVersion` 客户端头，错误包装为
  `fetch legacy Antigravity quota: %w` / `… status %d`。
- 凭据：OAuth 对象优先于 legacy API key；legacy 按 `SplitN(TrimSpace, "|", 2)` 取
  `refreshToken|projectID`。access token 非空且（未过期或两个 refresh token 都为空）时直接使用；
  否则克隆并补齐 RefreshToken / ClientId / Scopes，经 `AntigravityTokenProviderFactory`（client_secret
  form grant）刷新，失败包装为 `refresh Antigravity OAuth token: %w`；无 refresh token 报
  `channel has no Antigravity OAuth credentials`。刷新与配额请求共用同一（可能被代理的）executor。
- Legacy 解析：按模型 ID 排序；跳过 internal / 缺 quotaInfo / 缺 remainingFraction / NaN / Inf / 负数；
  remaining 截断到 0..1，≥1 为 exhausted、≥0.8 为 warning；记录最早 resetTime；RawData.models 为
  `{displayName, remainingPercentage, status, resetAt}`；同一 pool 的 effort 变体折叠为用量最高的 5h
  窗口并挂 `availabilityGroup=antigravity_pool:<pool>`；错误文案 `decode Antigravity quota response: %w`、
  `Antigravity quota response has no models`、`… has no quota data`、`… has no recognized model pools`。
- Summary 解析：envelope 的 `response` 优先于 `summary`、再退回顶层 `groups`；pool 由
  `id + " " + group.DisplayName + " " + group.Name` 归类，窗口由 `window` / `id` / `displayName` 的别名
  token 判定（`7d|week|weekly`、`5h|session`、`5|five` + hour、`7|seven` + day，`bi` / `semi` 后不认
  week）；remaining 依次取 `remainingFraction`、`remaining.remainingFraction`、
  `remaining.case=="remainingFraction"` 的 `value`；跳过 disabled / 未知 pool / 未知窗口 / 缺失或越界
  remaining；同一 pool+window 取最大用量；limits 按 `[gemini, claude_gpt] × [5h, 7d]` 顺序输出；无可用
  窗口报 `Antigravity quota summary has no measured pool windows`；RawData 为整个响应体的 Go 形状 map。
- 重置时间：数字按 epoch 秒 / 毫秒，字符串按 `strconv.ParseInt`（base 10）或 RFC3339；端口按 Go
  `time.Parse` 的宽度与范围规则实现，`DateTimeOffset` 无法表示的极端值返回无重置（见 C123）。
- 注册与接线：`antigravity` 通道不再落 “No checker for provider” 分支；采集循环、错误退避、状态归一化
  与持久化沿用 §114 核心，GraphQL 面无需改动。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 线路形状（两条 URL、Authorization、X-Client-*、User-Agent、Content-Type/Accept、`{project}` body）、摘要优先与 404/501 / 解析失败回退、脚本化非 2xx 文案、OAuth 刷新优先级与失败包装、legacy 凭据、空凭据、通道代理与畸形设置、摘要 / 旧式解析、pool 折叠与可用性分组、窗口别名、resetTime 接受 / 拒绝、状态阈值 | `tests/AxonHub.Api.Tests/AntigravityQuotaCheckerTests.cs` | 17 + 44 |

`AxonHub.Api.Tests` 由 3084 增至 3145（+61），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由 6530
增至 6591。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态计数；
下一次全量运行应以 3145 / 319 / 6591 复核。**

差异：C123 由真实缺口转为 resolved（子差异：UTC 渲染、不可表示的年份 / 偏移 / 极端 epoch、超 100ns 小数、
超范围 JSON 数字的容错）；C182 由 19 个 checker 收窄为剩余 18 个。

## 116. Claude Code 供应商配额检查器（unified ratelimit headers，80 例，2026-11-07）

参照 `f272549f`：`internal/server/biz/provider_quota/claudecode_checker.go:26-260`（`CheckQuota`:26-91、
`parseResponse`:93-187、`SupportsChannel`:189-191、`buildTokenLimit`:193-236、`getEndpointURL`:238-250、
`parseUnixTimestamp` / `parseFloat`:252-260）、`llm/transformer/anthropic/claudecode/constants.go` 的配额常量，
`provider_quota.go:406-407` 的 `registerClaudeCodeSupport`，以及 `period_window_test.go` 与
`provider_contract_fixtures_test.go` 中的 claudecode 用例。

新增 `src/AxonHub.Api/Orchestration/ClaudeCodeQuotaChecker.cs`（`IQuotaChecker` 实现：凭据优先级、
one-token Haiku 请求、unified ratelimit 头解析、窗口与状态归一化），`GoCast` 新增
`ParseFloatIgnoringError`（Go `ParseFloat` 错误被忽略时的上溢 / 下溢语义），`Program.cs` 注册
`ClaudeCodeQuotaChecker` 与 `ProviderQuotaCheckerRegistration("claudecode", …)`，
`ProviderQuotaCheckerRegistry` 的文档同步为「Claude Code 与 Antigravity 已注册，其余 17 个待切片」。

- 请求：POST `getEndpointURL(BaseUrl)`（空则 `https://api.anthropic.com/v1/messages`；去掉一个尾部 `/`，
  以 `/v1` 结尾补 `/messages`，否则补 `/v1/messages`），Bearer access token，头 `anthropic-beta`
  = `ClaudeCodeQuotaCheckHeader`、`anthropic-version`、`anthropic-dangerous-direct-browser-access`、
  `x-app: cli`、`content-type: application/json`，body 为
  `{"model":"claude-haiku-4-5","messages":[{"role":"user","content":"limit"}],"max_tokens":1}`
  （Go map 键序，序列化后按键排序）。
- 凭据：OAuth 对象存在时优先（即使 access token 为空也不回退 legacy apiKey）；否则把 legacy apiKey 当
  OAuth JSON 解析；两者都没有 / 仅空白报 `channel has no credentials`，OAuth 对象的 token 为空报
  `channel credentials missing access token`，legacy 解析失败包装为
  `failed to parse OAuth credentials: %w`。
- 传输：通道 `settings.proxy` 存在时经 `IProxyExecutorFactory` 走代理，否则共享 executor；执行错误包装为
  `HTTP request failed: %w`（含 `HttpClientError` 的 `METHOD - URL with status STATUS`）；非 200（低于
  400）报 `HTTP %d: %s`。
- 解析：`Anthropic-Ratelimit-Unified-Status` 为空报 `missing quota headers`；`allowed` → available，
  `throttled` / `rejected` → exhausted，其余 → unknown；overall available 且 5h 或 7d utilization ≥ 0.8
  升级为 warning（overage 不触发）；每个有任一头部（status / reset / utilization）的 5h / 7d 窗口生成
  token limit（utilization ≥ 1 → exhausted，≥ 0.8 → warning），带 5h / 7d period；代表窗口由
  `representative_claim` 映射（`five_hour`→5h、`seven_day`→7d），未知值按原字符串查 windows；
  RawData 保留 unified_status / windows（5h、7d、overage 三组 status/reset/utilization）/
  representative_claim / fallback / fallback_percentage / reset；最后交给 §114 的归一化（最早重置、
  状态提升 / 合并、period 计算）。
- 数值解析：`parseUnixTimestamp` = `strconv.ParseInt` 忽略错误（语法错 0，范围错饱和到 int64 极值），
  `parseFloat` = `strconv.ParseFloat` 忽略错误（语法错 0，上溢 ±Inf，下溢 ±0，接受十六进制浮点与下划线）；
  `DateTimeOffset` 无法表示的年份范围的 reset 置空（C123 子差异）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 线路形状（URL、Bearer、四个 Claude Code 头、Content-Type/Accept/User-Agent、body 字节）、base URL 变体、OAuth 对象 / legacy apiKey / 空凭据 / 缺 token / 解析错误包装、HTTP 错误包装与 201 文案、通道代理与畸形设置、unified 状态映射与 0.8 阈值、窗口存在性、代表窗口重置与归一化最早重置、RawData 形状、period 携带、ParseInt / ParseFloat / getEndpointURL 边界 | `tests/AxonHub.Api.Tests/ClaudeCodeQuotaCheckerTests.cs` | 13 + 67 |

`AxonHub.Api.Tests` 由 3145 增至 3225（+80），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由 6591
增至 6671。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态计数；
下一次全量运行应以 3225 / 319 / 6671 复核。**

差异：C182 由 18 个 checker 收窄为剩余 17 个；C123 仍为 resolved（claudecode 复用同一时间边界处理）。

## 117. Codex 供应商配额检查器（wham usage / reset credits，83 例，2026-11-07）

参照 `f272549f`：`internal/server/biz/provider_quota/codex_checker.go`（`Reset`:75-87、`ListResets`:89-115、
`listResetCredits`:117-147、`parseCodexResetTime`:149-160、`consumeResetCredit`:162-196、
`extractCodexCredentials`:198-224、`CheckQuota`:226-251、`parseResponse`:253-304、
`codexRateLimitExhausted`:306-309、`buildCodexQuotaLimit`:311-346、`codexWindowDuration`:348-359、
`codexWindowResetAt`:361-373、`codexAggregateStatus`:375-391、`codexEarliestFutureReset`:406-419、
`SupportsChannel`:421-423、`convertRateLimitToMap`:425-445、`convertWindowToMap`:447-466）、
`provider_quota.go:418-420` 的注册顺序，以及 `codex_checker_test.go`、`codex_quota_regression_test.go`、
`period_window_test.go` 与 `provider_contract_fixtures_test.go` 中的 codex 用例。

新增 `src/AxonHub.Api/Orchestration/CodexQuotaChecker.cs`（`IQuotaChecker` + `IQuotaResetter`：usage 采集、
窗口归一化、重置清单与 consume），`Program.cs` 注册 `CodexQuotaChecker` 与
`ProviderQuotaCheckerRegistration("codex", …)`（位于 claudecode 与 antigravity 之间，与参照注册顺序一致），
`ProviderQuotaCheckerRegistry` 的文档同步为「Claude Code、Codex 与 Antigravity 已注册，其余 16 个待切片」。

- 凭据：OAuth 对象存在时优先（即使 access token 为空也不回退 legacy apiKey）；否则把 legacy apiKey 当
  OAuth JSON 解析；两者都没有 / 仅空白报 `channel has no credentials`，OAuth 对象的 token 为空报
  `OAuth missing access_token`（与 claudecode 的文案不同），legacy 解析失败包装为
  `failed to parse OAuth credentials: %w`；access token 必须携带 `https://api.openai.com/auth` 下的
  `chatgpt_account_id` 字符串，否则报 `failed to extract ChatGPT account id from access token`（usage
  请求不使用该 id，但校验仍发生）。
- 采集：GET `https://chatgpt.com/backend-api/wham/usage`，Bearer access token，头
  `Content-Type: application/json`；无 body，因此线上不带 Content-Type（C206）。通道 `settings.proxy`
  存在时经 `IProxyExecutorFactory` 走代理，否则共享 executor；执行错误包装为
  `quota request failed: %w`；响应体解析失败包装为 `failed to parse codex usage response: %w`；非 2xx 由
  executor 抛 `HttpClientError`（`METHOD - URL with status STATUS`）。
- 解析：rawData 恒含 `plan_type`（缺省空串），`rate_limit` / `code_review_rate_limit` 仅在非 null 时以 Go
  map 形状加入（`allowed`、`limit_reached`、`primary_window`、`secondary_window` 与窗口四字段，均省略
  缺省值）；limits 只来自 `rate_limit` 的 primary / secondary 窗口，`code_review_rate_limit` 只进 rawData。
  窗口：`limit_window_seconds` 缺省为 0、≤0 或 >9223372036（int64 秒上限）视为无效并丢弃；
  `used_percent` 为 NaN / ±Inf / 负值时丢弃；ratio = min(percent/100, 1)，≥1 exhausted、≥0.8 warning；
  账户级 `limit_reached` / `allowed=false` 只在本窗口没有百分比时推断 100% exhausted。重置时间优先
  `reset_at > 0`（Go `time.Unix`），否则 `reset_after_seconds > 0` 按 now+delta；窗口标签由时长归一化为
  5h / 7d / daily / 30d，否则回退 "primary" / "secondary"。
- 状态：起始 unknown；`allowed=true` → available；`limit_reached` 或 `allowed=false` → exhausted；再取
  limits 的最大 rank（exhausted 3 / warning 2 / available 1）。`NextResetAt` 取严格大于 now 的最早窗口重置；
  最后交给 §114 的归一化（过期重置置空、period 计算、limit 合并）。
- 重置清单：GET `https://chatgpt.com/backend-api/wham/rate-limit-reset-credits`，Bearer + 
  `ChatGPT-Account-Id` + `Content-Type`；执行错误包装为 `list codex reset credits failed: %w`，解析错误包装为
  `failed to parse codex reset credits response: %w`；只保留 `status == "available"` 的 credit，时间字段按
  Go `time.Parse(RFC3339)`（失败 / 空 → null），返回 `supported=true` 且 `resets` 非 null。
- 重置：`Reset` 先取清单，无可用 credit 报 `no available codex reset credit`；否则 POST
  `…/rate-limit-reset-credits/consume`，body 为 `{"credit_id":…,"redeem_request_id":<v4 UUID>}`（Go map
  键序），头同清单；执行 / 解析错误分别包装为 `consume codex reset credit failed: %w` /
  `failed to parse codex reset consume response: %w`。
- 时间与序列化：新增 `GoTimeParsing.TryParseRfc3339`（从 Antigravity 的私有解析器提取，供两处共用）与
  `GoTime.Rfc3339Nano(DateTimeOffset)` / `GoNullableRfc3339NanoConverter`，用于配额重置清单的
  `grantedAt` / `expiresAt` 按 Go RFC3339Nano 输出（`ProviderQuotaDomainTests` 的 merge round-trip 同步
  扩展为断言 `_resets` 的完整形状）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 线路形状（usage / reset-credits 两条 GET 的 URL、Bearer、User-Agent、ChatGPT-Account-Id、无 Originator、无线上 Content-Type 的 C206 锁定）、请求数据保留 Content-Type、通道代理与畸形设置、OAuth 对象 / legacy apiKey / 空凭据 / 缺 token / JWT account id、HTTP 错误与解析错误包装、JSON null 视为零结构体、rawData 形状、拒绝请求的四组窗口状态、period / 双窗口 / 未知窗口标签 / 过期重置 / 无效窗口 / 时长边界 / reset_after_seconds 回退、重置清单过滤与空列表、consume 的键序与 v4 UUID、无 credit 与 consume 错误包装、`parseCodexResetTime` 的 Go 时间边界 | `tests/AxonHub.Api.Tests/CodexQuotaCheckerTests.cs` | 27 + 56 |

`AxonHub.Api.Tests` 由 3225 增至 3308（+83），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由 6671
增至 6754。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态计数；
下一次全量运行应以 3308 / 319 / 6754 复核。**

差异：C182 由 17 个 checker 收窄为剩余 16 个；新增 C206（无 body 的 GET 在线上丢掉 Content-Type 头，
已接受并有锁定测试）。

## 118. xAI Subscription 供应商配额检查器（weekly / monthly billing，46 例，2026-11-07）

参照 `f272549f`：`internal/server/biz/provider_quota/xai_subscription_checker.go`（`CheckQuota`:24-58、
`SupportsChannel`:57-59、`fetchBilling`:61-85、`xaiBillingQuotaData`:87-133、`xaiBillingWindowStatus`:135-143、
`parseXAIReset`:145-151、`earliestReset`:153-161）、`provider_quota.go:426-428` 的注册，以及
`xai_subscription_checker_test.go` 与 `provider_contract_fixtures_test.go` 中的 xai 用例。

新增 `src/AxonHub.Api/Orchestration/XaiSubscriptionQuotaChecker.cs`，`Program.cs` 在 antigravity 之后注册
`XaiSubscriptionQuotaChecker` 与 `ProviderQuotaCheckerRegistration("xai_subscription", …)`（与参照
`registerXAISubscriptionSupport` 的顺序一致），`ProviderQuotaCheckerRegistry` 的文档同步为「Claude Code、
Codex、Antigravity 与 xAI Subscription 已注册，其余 15 个待切片」。凭据解析提取为
`OAuthCredentialsJson.ResolveChannelCredentials`（`internal/objects/channel.go:631-639` 的端口），
`ModelFetcher` 与 `ChannelOutboundFactory` 的私有副本改为委托，避免三处漂移。

- 凭据：typed `oauth` 对象的 access token 经 `strings.TrimSpace` 后非空则优先；否则把 legacy `apiKey`
  当作 OAuth JSON 解析（空输入报 `empty credentials`、缺 token 报 `access_token is empty`）。解析失败包装为
  `parse xAI subscription credentials: %w`；trim 后的 token 为空报
  `xAI subscription channel has no OAuth access token`。
- 采集：同一 executor 先 GET `https://cli-chat-proxy.grok.com/v1/billing?format=credits`（weekly），再 GET
  `https://cli-chat-proxy.grok.com/v1/billing`（monthly）；Bearer token，头 `Accept: application/json`、
  `Content-Type: application/json`、`x-xai-token-auth: xai-grok-cli`、
  `x-grok-client-version: 0.2.114`、`User-Agent: grok-pager/0.2.114 grok-shell/0.2.114 (macos; aarch64)`；
  无 body，因此线上不带 Content-Type（C206）。`settings.proxy` 存在时**一次**解析代理 executor 供两个请求
  共用，畸形 settings 视为无代理。执行错误分别包装为 `fetch xAI weekly billing: %w` /
  `fetch xAI monthly billing: %w`；非 [200,300) 状态（executor 只对 ≥400 抛错，故此处覆盖 1xx/3xx）报
  `HTTP %d` 并同受上述包装。报文合并失败包装为 `parse xAI billing responses: %w`（无 `config` 报
  `xAI billing responses contain no config`）。
- 窗口：weekly（存在时）→ 状态 `xaiBillingWindowStatus(usagePercent)`、ratio = percent/100、
  `WithWindow("weekly", 7d)`（period start = reset − 7d）；monthly（存在时）→ 同状态函数、
  `Window="monthly"`、`PeriodStartFromMonthlyReset(reset)`。重置时间按 `time.Parse(RFC3339)` 解析
  （TrimSpace 后，失败 → null）。整体 `usagePercent` 取 weekly 与 monthly 的最大值（起始 −1，NaN 传播），
  ≥0 才覆盖 unknown；`NextResetAt` 取两个重置中的最早值（不过滤未来，随后由归一化清空过期值）。
  `plan_type` 恒进 rawData（缺省空串），`billing` 恒为对象（两个窗口都缺失时为空对象），仅非 null 的窗口
  以 Go `BillingWindow` 形状加入；`PlanFromAccessToken` 的非空结果覆盖 `summary.Plan`。
- 状态映射：≥100 exhausted、≥80（`WarningThresholdRatio*100`）warning、其余 available；
  ratio > 1 由归一化截断为 1，负 ratio 的 limit 被丢弃（整体状态仍按原始百分比计算）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 线路形状（weekly → monthly 顺序、两条 URL、Bearer、Accept、`x-xai-token-auth`、`x-grok-client-version`、CLI User-Agent、无线上 Content-Type 的 C206 锁定）、请求数据保留头与顺序、通道代理只解析一次、畸形设置、OAuth 对象 / legacy apiKey / 空白 token 回退 / 空凭据 / 空白 token 拒绝、传输错误与 1xx/3xx 状态、解析错误包装、JWT tier 覆盖、SupportsChannel、两个窗口的合并与 period、monthly-only / weekly-only / 空 summary / 负值 / 超 1 截断 / 边界状态、重置 TrimSpace 与偏移 / 过期清空 / 最早值、rawData 的 Go `BillingWindow` 字段形状与 plan | `tests/AxonHub.Api.Tests/XaiSubscriptionQuotaCheckerTests.cs` | 25 + 21 |

`AxonHub.Api.Tests` 由 3308 增至 3354（+46），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由 6754
增至 6800。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态计数；
下一次全量运行应以 3354 / 319 / 6800 复核。**

差异：C182 由剩余 16 个 checker 收窄为 15 个；无新增差异（无 body GET 的 C206 与 RFC3339 解析的 C123
沿用既有登记）。

## 119. GitHub Copilot 供应商配额检查器（copilot_internal/user，75 例，2026-11-07）

参照 `f272549f`：`internal/server/biz/provider_quota/github_copilot_checker.go`（`CheckQuota`:40-71、
`buildLimits`:73-117、`copilotLimit`:119-128、`isFiniteCopilotNumber`:130-132、`getAccessToken`:134-155、
`fetchUserPayload`:157-180、`mapPlanType`:182-199、`prepareRawData`:201-230、`getNumber`:232-243、
`calculateStatus`:245-291、`parseResetDate`:293-310、`SupportsChannel`:312-314）、`provider_quota.go:430-432`
的注册、`llm/transformer/openai/copilot/outbound.go:160-168` 的 `SetCopilotHeaders`，以及
`provider_contract_fixtures_test.go` 的 github_copilot 用例与 `period_window_test.go:389-420` 的
`TestGithubCopilotQuotaChecker_PreservesMonthlyResetWithoutPeriod`。

新增 `src/AxonHub.Api/Orchestration/GithubCopilotQuotaChecker.cs`（`IQuotaChecker` 实现与
`copilotUserPayload` 的 Go 语义解码），`Program.cs` 在 xai_subscription 之后注册
`GithubCopilotQuotaChecker` 与 `ProviderQuotaCheckerRegistration("github_copilot", …)`（与参照
`registerGithubCopilotSupport` 的顺序一致），`CopilotOutboundTransformer.SetCopilotHeaders` 由 internal
提升为 public（AxonHub.Api 不能访问 Llm 模块的 internal，参照侧该函数本就是导出函数），
`ProviderQuotaCheckerRegistry` 的文档同步为「Claude Code、Codex、Antigravity、xAI Subscription 与
GitHub Copilot 已注册，其余 14 个待切片」。

- 凭据：typed `oauth` 对象存在时直接返回其 access token（**不 trim、不校验空值**，空 token 会发出
  `Authorization: token `）；否则把 legacy `apiKey` 当 OAuth JSON 解析，`err == nil && AccessToken != ""`
  才采用；最后回退到 `strings.TrimSpace(apiKey)`。既无 oauth 且 trim 后 apiKey 为空报
  `channel has no credentials`；回退 token 为空报 `GitHub access token is missing`。
- 采集：GET `https://api.github.com/copilot_internal/user`，头 `Authorization: token <accessToken>`、
  `Accept: application/json` 与 `SetCopilotHeaders` 的 7 个头（`Editor-Version: vscode/1.95.0`、
  `Editor-Plugin-Version: copilot-chat/0.26.7`、`User-Agent: GitHubCopilotChat/0.26.7`、
  `Copilot-Integration-Id: vscode-chat`、`Openai-Intent: conversation-edits`、
  `X-Github-Api-Version: 2025-04-01`、`X-Vscode-User-Agent-Library-Version: electron-fetch`）；无 body，
  因此不带 Content-Type。`settings.proxy` 存在时**一次**解析代理 executor；畸形 settings 视为无代理（C013）。
  执行错误包装为 `failed to fetch copilot user info: %w`；状态不在 [200,300)（executor 只对 ≥400 抛错，
  故覆盖 1xx/3xx）报 `failed to fetch copilot user info, status: %d`；解码失败包装为
  `failed to parse copilot user response: %w`。
- 解码：`json.Unmarshal` 进入 `copilotUserPayload` 的 Go 语义——根 `null` 对 struct 是 no-op；重复对象键
  对 map 字段做合并（Go 复用非 nil map），对标量字段 last-wins；键匹配先精确后 ASCII 大小写折叠；数字
  统一解码为 `float64`（溢出报 `cannot unmarshal number <literal> into Go value of type float64`）；
  字段类型不匹配按 Go 文案报错（与 .NET 解码器文案的差异沿用 C093）。`quota_reset_date_utc` 用 RFC3339
  （不 trim）解析，两个日期字段用 `2006-01-02`（不 trim），空串或解析失败继续回退（C123 的年份 0 限制）。
- 窗口：先按排序后的 `limited_user_quotas` 键生成 limit（`monthly_quotas` 的嵌套值作为 total；缺失 /
  非数字 / 非有限 / 负数回退为 remaining；`total == 0 && remaining != 0` 跳过；ratio =
  (total−remaining)/total，total 为 0 时 0）；再按排序后的 `quota_snapshots` 键生成（非对象跳过；
  `unlimited == true` 仅对 bool 成立，非 bool 计数；`percent_remaining` 必须为 0..100 的有限数字；
  ratio = 1 − remaining/100）。两者状态：ratio ≥ 1 exhausted、≥ `WarningThresholdRatio`（0.8）warning、
  其余 available；`WithWindow(window, 0)` 只设标签，`PeriodStart` 恒为 nil。
- 整体状态：取所有窗口的最低百分比（limited 用 `remaining/total*100`，monthly total > 0 才覆盖；
  snapshots 用 `percent_remaining`，`unlimited == true` 跳过且**不校验** 0..100），起始 100；≤0 exhausted、
  <20 warning、其余 available。`NextResetAt` 与每个 limit 的 `NextResetAt` 同取解析出的月度重置
  （不过滤未来，归一会清空过期值）。
- rawData：`copilot_plan` 恒存在（缺省为 JSON null）；`access_type_sku` 与映射后的 `plan_type`（未知 SKU
  不写 `plan_type`）；`quota_reset_date_utc` / `limited_user_reset_date` / `quota_reset_date` 按非 null 写入；
  `limited_user_quotas`、`total_quotas`（即 `monthly_quotas`）与 `quota_snapshots` 按非 null 写入；序列化
  沿用 Go 的键排序与 null 输出（`GoJson`）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 线路形状（URL、`Authorization: token`、Accept、7 个 Copilot 头、无 body / Content-Type）、请求数据保留头与 Auth 为空、通道代理只解析一次、畸形设置、typed oauth / legacy apiKey OAuth JSON / 空 token 回退 / 不可解析回退 / 空 oauth token 透传 / 三种空凭据拒绝、传输错误包装与 1xx/3xx 状态、解码错误包装与非对象根 / 重复键合并 / last-wins、两族窗口的排序与跳过规则、百分比→ratio 与状态边界、整体状态八组、重置优先级与偏移 / 一位小时 / 小数秒 / 空值与非法日期、月度重置无 period（period_window_test）、rawData 的 Go 键序与 null、SKU→plan 八组、SupportsChannel | `tests/AxonHub.Api.Tests/GithubCopilotQuotaCheckerTests.cs` | 28 + 47 |

`AxonHub.Api.Tests` 由 3354 增至 3429（+75），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由 6800
增至 6875。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态计数；
下一次全量运行应以 3429 / 319 / 6875 复核。**

差异：C182 由剩余 15 个 checker 收窄为 14 个；无新增差异（解码文案沿用 C093、RFC3339 / 日期解析沿用
C123、畸形 settings 沿用 C013）。

## 120. NanoGPT 供应商配额检查器（订阅用量端点，105 例，2026-11-07）

参照 `f272549f`：`internal/server/biz/provider_quota/nanogpt_checker.go`（`CheckQuota`:61-94、
`parseResponse`:96-202、`SupportsChannel`:204-206、`buildNanoGPTQuotaURL`:211-228、
`buildNanoGPTLimitStatuses`:230-281、`findEarliestResetAt`:285-302、三个 convert 函数:304-352）、
`provider_quota.go:434-436` 的 `registerNanoGPTSupport`，以及 `provider_contract_fixtures_test.go` 的
nanogpt 用例与行 `{"nanogpt", channel.TypeNanogpt, "", "nanogpt_windows", 3}` /
`{"nanogpt", channel.TypeNanogptResponses, "", "nanogpt_windows", 3}`。

新增 `src/AxonHub.Api/Orchestration/NanogptQuotaChecker.cs`（`IQuotaChecker` 实现与
`NanoGPTUsageResponse` 的 Go 语义解码），`Program.cs` 在 github_copilot 之后注册
`NanogptQuotaChecker` 与 `ProviderQuotaCheckerRegistration("nanogpt", …)`（与参照
`registerNanoGPTSupport` 的顺序一致），`ProviderQuotaCheckerRegistry` 的文档同步为「Claude Code、
Codex、Antigravity、xAI Subscription、GitHub Copilot 与 NanoGPT 已注册，其余 13 个待切片」。
两个共享原语：`ChannelCredentials.GetApiKeys`（原样读取 `apiKeys` 数组，非数组 / 非字符串元素按
C013 宽松处理）与 `GoUrl.TryParse`（复用 Codex 的 `net/url.Parse` 端口，暴露 scheme / host / path）。

- 凭据：`strings.TrimSpace(Credentials.APIKey)`；trim 后为空且 `APIKeys` 非空时取 `APIKeys[0]`
  （**不 trim、不按禁用 key 过滤、不检查 OAuth**）；仍为空报 `channel has no API key`。
- URL：`buildNanoGPTQuotaURL` 先 trim；空串回退 `https://nano-gpt.com/api/subscription/v1/usage`；
  `url.Parse` 报错或 scheme 与 host 同时为空同样回退；scheme `http` 升级为 `https`；输出
  `<scheme>://<host>/api/subscription/v1/usage`（保留 host 大小写与端口、丢弃 userinfo / path / query）。
  `url.Parse` 语义复用既有 Codex 端口（畸形 URL 的 fragment / userinfo / IPv6 字面量校验未逐字复刻，
  落入 C182 的已接受口径）。
- 采集：GET 上述 URL，`WithBearerToken(apiKey)` 写入 `Auth`（执行器落成 `Authorization: Bearer`），
  头仅 `Content-Type: application/json`；无 body。`settings.proxy` 存在时**一次**解析代理 executor；畸形
  settings 视为无代理（C013）。执行错误包装为 `quota request failed: %w`；**不检查状态码**，任何能返回
  body 的状态（executor 只对 ≥400 抛错，故覆盖 1xx/3xx）都按 JSON 解析；解码失败包装为
  `failed to parse nanogpt usage response: %w`。
- 解码：根 `null` 对 struct 是 no-op；重复对象键对嵌套对象做合并（Go 复用非 nil 指针），对标量字段
  last-wins、对 `null` 重置指针；键匹配按 ASCII 大小写折叠；`*int64` 字段用 Go `ParseInt` 语义（拒绝
  `1.5` / `1e3` / 溢出，错误值带原始字面量），`*float64` 用 Go `ParseFloat`（1e400 报错），字段类型
  不匹配按 Go 文案 `json: cannot unmarshal <value> into Go struct field <Struct>.<FieldStack> of type <T>`
  报错（文案差异沿用 C003 / C093）。
- 窗口：固定顺序 image（daily，24h）、daily tokens（daily，24h）、weekly tokens（weekly，7d）；窗口
  对象缺失则跳过。ratio 先取 `PercentUsed`（默认 0）；`Remaining != nil && <= 0` → exhausted 且
  ratio = 1；否则 ratio ≥ 1 → exhausted、≥ 0.8 → warning、其余 available。`ResetAt > 0` 时用
  `time.UnixMilli` 设 `NextResetAt`，`WithWindow` 由 `reset − 窗口长度` 得 `PeriodStart`；越界毫秒
  值返回 nil（C123）。归一化会丢弃负 ratio（整个 limit 消失）并清空过期 reset / 无效 PeriodStart。
- 整体状态：state `active` → available、`grace` → warning、`inactive` → exhausted、其它（含 null）→
  unknown；仅当基状态为 available / warning 时按 `quotaStatusRank` 升到窗口最差状态
  （available < warning < exhausted），inactive 与 unknown 不被覆盖（unknown 最终由归一化按窗口重算）。
  `NextResetAt` 取三窗口最早的正 resetAt（不过滤过去，归一会清空）；无窗口 reset 且 `graceUntil` 非空
  时用 RFC3339 解析结果（不 trim，空串 / 非法日期 / 缺时间继续为 null，年份 0 等限制见 C123）。
- rawData：`active` / `provider` / `providerStatus` / `state` / `allowOverage` 按非 null 标量写入；
  `limits` 与 `period` 在对象存在时恒写入（内部只写非 null 字段，空对象即 `{}`）；`windows` 仅在至少
  一个窗口存在时写入，键固定 `dailyImages` / `dailyInputTokens` / `weeklyInputTokens`，值为只含非 null
  字段的对象；`graceUntil` 按非 null 写入。序列化沿用 Go 键排序与数字格式（`GoJson`；double 格式的
  已接受差异见 C067）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 线路形状（默认 URL、Bearer 经 Auth、Content-Type / Accept、无 body）、21 组 base URL 解析、传输错误与 HTTP 状态包装、<400 状态仍解析、凭据 trim / apiKeys 回退原样 / 优先级 / 空值矩阵（7 组）、共享契约 fixture、percentUsed→状态与 ratio 边界（7 组）、remaining ≤ 0 覆盖（5 组）、状态映射（5 组）与升级（6 组）、窗口顺序与负 ratio 丢弃、最早 reset / 非正 reset / 过期 reset、graceUntil 接受（4 组）与拒绝（4 组）、窗口 reset 优先、Go 解码错误（16 组）与畸形 JSON、根 null / 重复键合并 / null 重置 / 大小写折叠、rawData 的 Go 键序与空对象、代理路由 / 畸形 settings / 无代理、SupportsChannel 两类型 | `tests/AxonHub.Api.Tests/NanogptQuotaCheckerTests.cs` | 26 + 79 |

`AxonHub.Api.Tests` 由 3429 增至 3534（+105），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
6875 增至 6980。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]`
的静态计数；下一次全量运行应以 3534 / 319 / 6980 复核。**

差异：C182 由剩余 14 个 checker 收窄为 13 个；无新增差异（解码文案沿用 C003 / C093、Unix 毫秒与
RFC3339 解析沿用 C123、畸形 credentials / settings 沿用 C013、rawData 的 double 序列化沿用 C067）。

## 121. ZenMux 供应商配额检查器（管理订阅端点，152 例，2026-11-07）

参照 `f272549f`：`internal/server/biz/provider_quota/zenmux_checker.go`（`CheckQuota`:62-89、
`SupportsChannel`:91-102、`parseZenmuxQuotaResponse`:104-160、`parseZenmuxResetAt`:162-172、
`zenmuxWindowStatus`:174-182、`zenmuxAccountStatus`:184-195、`worstZenmuxStatus`:197-205、
`zenmuxStatusRank`:207-218、报文类型:16-52）、`provider_quota.go:438-440` 的 `registerZenmuxSupport`，
以及 `provider_contract_fixtures_test.go` 的 zenmux 返回体与 `provider_contract_test.go` 的五行
`{"zenmux", <type>, "", "zenmux_windows", 2}`（zenmux / zenmux_responses / zenmux_anthropic /
zenmux_gemini / zenmux_video）与 `TestQuotaCheckers_KeepZenmuxMonthlyQuotaInRawData`。

新增 `src/AxonHub.Api/Orchestration/ZenmuxQuotaChecker.cs`（`IQuotaChecker` 实现、
`zenmuxSubscriptionResponse` 系列类型与 Go 语义解码、`time.Parse(time.RFC3339)` 的分阶段解析器），
`ChannelCredentials` 新增 `GetManagementApiKey`（大小写折叠键匹配、重复键 last-wins、非字符串按
C013 视为缺失），`Program.cs` 在 nanogpt 之后注册 `ZenmuxQuotaChecker` 与
`ProviderQuotaCheckerRegistration("zenmux", …)`（与参照 `registerZenmuxSupport` 的顺序一致），
`ProviderQuotaCheckerRegistry` 的文档同步为「Claude Code、Codex、Antigravity、xAI Subscription、
GitHub Copilot、NanoGPT 与 ZenMux 已注册，其余 12 个待切片」。

- 凭据：`strings.TrimSpace(Credentials.ManagementAPIKey)`；为空（含无凭据、非字符串、空白）报
  `channel has no management API key`，不考虑 legacy `apiKey` / `apiKeys`。
- 采集：GET `https://zenmux.ai/api/v1/management/subscription/detail`，`WithBearerToken` 写入
  `Auth`（执行器落成 `Authorization: Bearer`），无 body；`settings.proxy` 存在时**一次**解析代理
  executor；畸形 settings 视为无代理（C013）。执行错误包装为 `zenmux quota request failed: %w`；
  状态不在 [200,300)（executor 只对 ≥400 抛错，故覆盖 1xx/3xx）报
  `zenmux quota request returned HTTP %d`；解码失败包装为 `failed to parse zenmux quota response: %w`。
- 解码：根 `null` 对 struct 是 no-op；重复对象键合并（Go 复用非 nil 结构体 / 指针），标量
  last-wins，`data` 的 `null` 重置指针、`plan` / 两个窗口 / `quota_monthly` 的 `null` 是 no-op；
  键匹配按 ASCII 大小写折叠；非对象根与字段类型不匹配按 Go 文案
  `json: cannot unmarshal <value> into Go struct field <Struct>.<FieldStack> of type <T>` 报错
  （文案差异沿用 C003 / C093），`float64` 溢出（如 `1e400`）同样按该文案报错。`success=false` 时
  `message` 非空报 `zenmux API error: %s`、为空报 `zenmux API returned success=false`；`data` 缺失
  （含 `null`）报 `zenmux quota response contains no data`。
- 状态：窗口 ratio ≥ 1 → exhausted、≥ `WarningThresholdRatio`（0.8）→ warning、其余 available；
  账户状态 `strings.ToLower(strings.TrimSpace(...))` 后 healthy / monitored → available、
  abusive → warning、suspended / banned → exhausted、其余 unknown；整体取三者最差，排名
  available 0 < warning 1 < exhausted 2 < unknown 3（unknown 比 exhausted 更差）。
- 限额：固定两个 token limit，顺序 5h（`QuotaWindow5h`，5 小时）与 7d（`QuotaWindow7d`，7 天），
  `WithWindow` 由 `reset − 窗口长度` 得 `PeriodStart`；`NextResetAt` 取两个 reset 的最早者（nil 感知，
  不过滤过去）。归一化沿用共享 `ProviderQuotaData.Normalize`：ratio 截断到 1、≥1 强制 exhausted、
  丢弃负数 / 非有限 ratio 的 limit、清空过期重置、按 ready 优先级推导总状态并重算总重置；因此
  未知账户状态通常被 ready 窗口掩盖，仅在两个窗口都被丢弃（ratio 为负）时可见。
- reset：`time.Parse(time.RFC3339, *resets_at)`（不 trim）；nil → nil；失败报
  `invalid reset timestamp %q: %w`。端口的分阶段解析器逐字复现 Go 的 `time.Parse` 文案
  （month / hour / minute / second / 时区偏移范围、`extra text`、`cannot parse ... as ...`），并接受
  Go 允许的小数秒（`.` / `,`）、一位或两位小时与 ±24:60 偏移；Go 接受但 `DateTimeOffset` 无法
  表示的值（年份 0000、超出 ±14:00 的偏移、UTC 时刻越界）报端口的范围限制（C123 / C207）。
- rawData：`plan`、`quota_monthly`、`account_status` 三个键（按 Go map 排序输出），嵌套对象按
  Go 字段序与 null 输出（`GoJson`；double 格式的已接受差异见 C067）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 线路形状（固定 URL、Bearer 经 Auth、Accept、无 Content-Type / body）、两条窗口与 PeriodStart / 最早 reset、空 data 的零值窗口、ratio 截断、success=false 四组、缺 data 三组、reset 错误按窗口顺序包装（2 组）、管理 key 缺失（8 组）/ trim / 优先级 / 大小写 / 重复键、传输错误包装与 1xx/3xx 状态（4 组）/ 2xx 接受（3 组）、Go 解码错误 28 组与畸形 JSON、根 null / 重复键合并 / null 重置 / 大小写折叠 / null 后重新赋值、窗口状态 8 组、账户状态 10 组、最差状态排序、unknown 被掩盖 / 被保留、reset 解析 5 组接受 / 29 组 Go 错误文案 / 7 组 DateTimeOffset 范围、契约 fixture 与 rawData 键序、代理路由 / 畸形 settings / 无代理、SupportsChannel 五类型接受与四类型拒绝 | `tests/AxonHub.Api.Tests/ZenmuxQuotaCheckerTests.cs` | 32 + 120 |

`AxonHub.Api.Tests` 由 3534 增至 3686（+152），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
6980 增至 7132。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]`
的静态计数；下一次全量运行应以 3686 / 319 / 7132 复核。**

差异：C182 由剩余 13 个 checker 收窄为 12 个；新增 C207（ZenMux checker 未移植
`WithRejectHTTPSDowngrade`，transport 错误内层文本来自 .NET 栈；reset 的年份 0000 / ±24:60 偏移与
端口 `timestamp is outside the DateTimeOffset range` 回退沿用 C123）。

## 122. Cline 供应商配额检查器（ClinePass 用量限额端点 + 账本回退，100 例，2026-11-07）

参照 `f272549f`：`internal/server/biz/provider_quota/cline_checker.go`（`CheckQuota`:248-320、
`getJSON`:322-355、`clineNativeHTTPClient`:357-362、`clineHTTPError`:364-377、`clineAPIKey`:379-392、
`buildClineQuotaURL`:394-423、`classifyClineModelScope`:425-457、`selectClinePassThreshold`:459-482、
`fetchUsageLimits`:484-503、`fetchUsageItems`:505-553、`clineLedgerErrorCode`:555-565、
`oldestClineUsageTime`:567-581、`parseClineTime`:583-595、`parseClineUsageLimits`:597-673、
`clineUsageLimitWindowKey`:675-686、`buildClineQuotaData`:688-747、`buildClinePassUnavailableQuota`:749-779、
`buildClineDirectOnlyQuota`:781-795、`buildClineWindow`:797-919、`alignClineWindowStart`:921-945、
`clineWindowStatus`:947-960、`worstClineStatus`:962-968、`worseQuotaStatus`:970-977、`clineWindowLabel`:979-990、
`clineLimitStatuses`:992-1018、`earliestClineWindowReset`:1020-1034、`clineWindowsRawData`:1036-1075、
`clineUsageLimitsFetchRawData`:1077-1085、`clineBalanceRawData`:1087 起、报文类型:64-235）、
`provider_quota.go:442-444` 的 `registerClineSupport`，以及 `cline_checker_test.go` 的 30 个测试函数。

新增 `src/AxonHub.Api/Orchestration/ClineQuotaChecker.cs`（`IQuotaChecker` 实现、`clineEnvelope<T>`
系列报文类型与 Go 语义解码、`time.Parse(time.RFC3339)` 复用共享 `GoTimeParsing` 解析器），
`Program.cs` 在 zenmux 之后注册 `ClineQuotaChecker` 与 `ProviderQuotaCheckerRegistration("cline", …)`
（与参照 `registerClineSupport` 的顺序一致），`ProviderQuotaCheckerRegistry` 的文档同步为
「Claude Code、Codex、Antigravity、xAI Subscription、GitHub Copilot、NanoGPT、ZenMux 与 Cline 已注册，
其余 11 个待切片」。

- 凭据：`strings.TrimSpace(Credentials.APIKey)`；为空时遍历 `credentials.apiKeys` 取首个 trim 非空项，
  都为空（含无凭据、非字符串、空白）报 `channel has no API key`（C013）。
- 采集顺序：GET `/api/v1/users/me` → `/api/v1/plans` → `/api/v1/users/{id}/balance`（id 经
  `url.PathEscape`）；direct_only 在此提前返回；其余范围在无 active ClinePass threshold 时报
  `Cline plans response does not include an active ClinePass threshold`；随后 GET
  `/api/v1/users/me/plan/usage-limits`，404 走 pass-unavailable 分支，其余错误使检查失败；再 GET
  `/api/v1/users/{id}/usages?limit=200`（cursor 非空才带 `cursor`）。四类请求均为 GET，头为
  `Accept: application/json`、`Authorization: Bearer <key>`、`User-Agent: axonhub/1.0`，无 body；
  `settings.proxy` 存在时**一次**解析代理 executor，畸形 settings 视为无代理（C013）。
- URL：base URL trim 后为空回退 `https://api.cline.bot`；`url.Parse` 失败或无 host 同样回退；scheme
  为空或 `http` 升级为 `https`；只保留 scheme + host（base 的 path / query 丢弃）；path 以 Go
  `url.URL{Path}` 的转义规则写出（保留字符转义、`%` 二次转义），query 经 `url.Values.Encode`
  按键排序。
- getJSON：创建请求失败报 `failed to create Cline quota request`；transport / nil 响应报
  `Cline quota request failed during transport`；状态不在 [200,300) 时丢弃 body 并报
  `HTTP <code> <StatusText>`（无文案时 `HTTP <code>`，body 从不进入错误文本）；body 读取失败报
  `failed to read Cline quota response`；解码失败报 `failed to parse Cline quota response`（Go 解码
  文案沿用 C003 / C093）。identity / plans / balance 的错误分别包装为
  `failed to read Cline user identity|plans|balance: %w`；`me.Data.ID` trim 后为空报
  `Cline user identity response missing id`。
- 范围：`supportedModels` + `manualModels` 逐个 trim；`cline-pass/` 前缀（大小写敏感）计入 pass 池，
  其余计入 direct 池；两者都有 → mixed、只有 pass → cline_pass_only、只有 direct → direct_only、
  全空 → unknown。
- threshold：遍历 plans，跳过 `type` 非 `cline_pass`、disabled、threshold 为 nil 或 `active=false` 的
  条目；选首个符合条件的 threshold；plan summaries 为每个符合条件 plan 的 `{type, interval}`。
- usage limits：`parseClineUsageLimits` 只认 `type` 为三窗口（last5h / last7d / last30d）的条目，
  每窗口取首个有效 ratio（`percentUsed / 100` 截断到 [0,1]）；reset 状态机 missing → unavailable、
  null / 空白 → inactive、非字符串 → invalid，且仅当前状态为空 / unavailable / invalid 时被后值覆盖；
  inactive 且 ratio 为 0 / 缺失保持 inactive，inactive 但 ratio > 0 降为 unavailable；active 的
  reset 用 `parseClineTime`（RFC3339，失败标 invalid）；只有 ratio 非 null 或状态非 unavailable 的
  条目才进入结果。字段计数给出 complete（3 窗口 6 字段）/ partial / unusable，无可用数据报
  `failed to read Cline usage limits: response contains no usable window data`。
- 账本：`fetchUsageItems` 的 cutoff 为 now − 30 天，每页 `limit=200`，最多 100 页；空 cursor、空页或
  最旧条目早于 cutoff 时停止，打满 100 页置 `truncated`；按 trim 后的 `aiModelTypeName` 计数
  cline-pass / 空（unclassified）/ 其他（direct），无效 `created_at` 另行计数。账本失败非致命：
  `ledger_unavailable=true` 并记 `ledger_error_code`（`rate_limited` / `http_<code>` / `transport`），
  cost 整体标记不可用，官方窗口仍生效。
- 窗口：`buildClineWindow` 以官方 ratio / reset 为准；active reset 必须满足
  `reset > now && reset <= now + duration + 2s`；inactive 且 ratio > 0 为 unavailable，否则 inactive；
  cost 仅在未 truncated / 账本可用时计算，起点为官方窗口起点对齐到 ±2s 内最近的 cline-pass 条目；
  只累计起点之后、reset 之前且模型为 `cline-pass` 的条目（空模型清零并停止，其他模型忽略）；
  `costRatio` 仅在 limit > 0 时给出，官方 usage ratio 缺省时用 costRatio 补。
- 组装：三个 token limit（5h / 7d / 30d），`PeriodStart = reset − duration`，整体取三窗口最差；
  pass_only 允许 exhausted，mixed / direct 范围的 pass 池耗尽降为 warning 且 basis 为
  `mixed_pool_pass_exhausted`；随后经 `ProviderQuotaData.Normalize` 复核 ratio、过期 reset 与总状态。
  pass-unavailable 分支**不归一化**：pass_only → exhausted、mixed → warning +
  `cline_pass_unavailable_mixed_pool`、其余 → warning + `cline_pass_unavailable`，rawData 无
  `cost_scale` / `windows` / `usage_fetch`，另加 `pass_state=unavailable`。direct-only 为 available，
  rawData 只含 `model_scope` / `status_basis` / `pool` / `pool_note` / `balance` / `plans`。
- rawData：`model_scope`、`status_basis`、`pool`、`pool_note`、`cost_scale`（100000000）、`balance`、
  `plans`、`windows`、`usage_fetch`、`usage_limits_fetch`，按 Go map 排序输出（`GoJson`；double 格式
  沿用 C067）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 五请求 happy path（URL / 头 / 三条 limit / PeriodStart / rawData / usage_fetch meta）、usage-limits 404 三 scope、identity 404、usage-limits 非 404 四状态、503 安全错误、partial fallback、畸形字段、direct-only、凭据矩阵、缺 threshold、空 limits、transport / 429 账本失败、getJSON 分类（403 / transport / read / decode / 1xx / 1 MiB）、解析器 meta / reset 状态机 / 重复键 / inactive / 信封合并 / organizations / plans nil、窗口构建七组、窗口状态与最差状态、mixed 降级、URL 与 scope / threshold 选择、`parseClineTime` 8 组、账本错误码、分页（cursor + 100 页上限）、代理路由、`SupportsChannel` | `tests/AxonHub.Api.Tests/ClineQuotaCheckerTests.cs` | 44 + 56 |

`AxonHub.Api.Tests` 由 3686 增至 3786（+100），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
7132 增至 7232。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]`
的静态计数；下一次全量运行应以 3786 / 319 / 7232 复核。**

差异：C182 由剩余 12 个 checker 收窄为 11 个；新增 C208（Cline checker 经共享执行器发请求而非
`*http.Client`、读取错误 / 取消的错误分类、1 MiB 响应截断为近似、URL host 解析为 `GoUrl` 子集、
reset 的亚 100ns 截断与 DateTimeOffset 范围沿用 C123、`organizations` 数字元素不做 float64 范围校验）。

## 123. Synthetic 供应商配额检查器（固定 /v2/quotas 端点、rolling five-hour / weekly token / search 计数，209 例，2026-11-07）

参照 `f272549f`：`internal/server/biz/provider_quota/synthetic_checker.go`（`CheckQuota`:84-118、
`parseResponse`:120-193、`weeklyLimit`:200-220、`SupportsChannel`:222-228、`buildSyntheticQuotaURL`:230-247、
`buildSyntheticLimitStatuses`:249-290、`searchHourlyLimitStatus`:302-331、`weeklyTokenLimitStatus`:333-388、
`parseSyntheticCredits`:392-408、`findEarliestSyntheticResetAt`:410-444、七个 map 转换器:446-574、
报文类型:19-72）、`provider_quota.go:446-448` 的 `registerSyntheticSupport`（在 `registerClineSupport` 之后、
`registerNeuralWattSupport` 之前调用，见 `provider_quota.go:383`），以及 `synthetic_checker_test.go` 的
22 个测试函数。

新增 `src/AxonHub.Api/Orchestration/SyntheticQuotaChecker.cs`（`IQuotaChecker` 实现、`SyntheticUsageResponse`
系列报文类型与 Go 语义 JSON 解码、`time.Parse(time.RFC3339)` 复用共享 `GoTimeParsing` 解析器），
`Program.cs` 在 cline 之后注册 `SyntheticQuotaChecker` 与 `ProviderQuotaCheckerRegistration("synthetic", …)`
（与参照 `registerSyntheticSupport` 的顺序一致），`ProviderQuotaCheckerRegistry` 的文档同步为
「Claude Code、Codex、Antigravity、xAI Subscription、GitHub Copilot、NanoGPT、ZenMux、Cline 与 Synthetic
已注册，其余 10 个待切片」。

- 凭据：`strings.TrimSpace(Credentials.APIKey)`；为空且 `apiKeys` 非空时直接取 `APIKeys[0]`（**不 trim**），
  都为空（含无凭据、非字符串）报 `channel has no API key`（C013）。
- 采集：GET `buildSyntheticQuotaURL(BaseURL)`，头为 `Content-Type: application/json` 与
  `Authorization: Bearer <key>`，无 body；`settings.proxy` 存在时**一次**解析代理 executor，畸形 settings
  视为无代理（C013）。
- URL：trim 后为空、`url.Parse` 失败或无 scheme + host 时回退 `https://api.synthetic.new/v2/quotas`；
  `http` 升级 https；只保留 `scheme://host`，固定 path 为 `/v2/quotas`（base 的 path / query 丢弃）。
- 响应：`Do` 错误包装为 `quota request failed: %w`；`StatusCode != 200` 报 `HTTP <code>: <body>`；
  解码失败报 `failed to parse synthetic usage response: %w`（Go 解码文案沿用 C003 / C093）。
- 限额：先 five-hour（`limited=true` → exhausted / ratio 1.0；否则 `tickPercent` → ratio，>= 0.8 为 warning；
  reset 取 `nextTickAt`），再 weekly（ratio = 1 − `percentRemaining` / 100，>= 1 exhausted、>= 0.8 warning，
  reset 取 `nextRegenAt`；credits 可解析且 max > 0 时给出 `PeriodCost = max(used, 0)` 与
  `PeriodQuota = max`）；search hourly 只追加展示（`type=search`、window `search_hourly`），不参与状态升级与
  reset 聚合。
- weekly 合并：`weeklyLimit()` 在两种形状都存在时复制 `weeklyTokenLimit`，plan 的非空 `nextRegenAt` /
  `percentRemaining` 逐字段覆盖，credits 始终来自 `weeklyTokenLimit`。
- 总状态：无任何 limit 时为 unknown；否则为 available，按 exhausted（break）/ warning 升级；
  `nextResetAt` 取 subscription `renewsAt`、合并后 weekly `nextRegenAt`、five-hour `nextTickAt` 的最早有效值
  （search 排除），解析失败忽略；随后 `NormalizeQuotaData` 归一化（ratio 截断、负数 limit 丢弃、过期 reset
  清除），显式 available 在全部 limit 被丢弃后仍保留（与参照归一化一致）。
- rawData：仅在指针非空时输出 `subscription` / `search` / `freeToolCalls` / `weeklyTokenLimit` /
  `planWeeklyLimit` / `rollingFiveHourLimit`，值保留零值与 `limited:false`，按 Go map 排序输出（`GoJson`；
  double 格式沿用 C067）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 线路形状（固定 URL、Bearer 经 Auth、线上无 Content-Type、默认 Accept、无 body）、三条 limit 与 PeriodQuota / PeriodCost、search 只展示不阻断 / 阈值 / 不可用计数器、raw counter 零值与 false、credits 10 组与省略 20 组、`parseSyntheticCredits` 22 组、plan weekly 三种形状、mixed shapes 3 组、`weeklyLimit` 合并 3 组、warning / exhausted、five-hour 7 组、weekly 7 组、tick=1 归一化、负 ratio 丢弃、percent>100、空 builder、reset 聚合 4 组 / 非法 1 组、逗号小数与 100ns 截断、DateTimeOffset 范围、<= now 清除、凭据 4 组 + 拒绝矩阵 7 组、畸形 JSON、执行器状态 4 组 / 传输 1 组 / 1xx-3xx-2xx 4 组、自定义 base、`buildSyntheticQuotaURL` 19 组、`SupportsChannel` 11 组、Go 解码根 null / 非对象 5 组、类型不匹配 29 组、重复键合并、null 重置、未知字段、空对象、代理路由 3 组 | `tests/AxonHub.Api.Tests/SyntheticQuotaCheckerTests.cs` | 40 + 169 |

`AxonHub.Api.Tests` 由 3786 增至 3995（+209），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
7232 增至 7441。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]`
的静态计数；下一次全量运行应以 3995 / 319 / 7441 复核。**

差异：C182 由剩余 11 个 checker 收窄为 10 个；新增 C209（Synthetic checker 经共享执行器发请求而非
按通道 client、>=400 被包装为 `quota request failed`、非法 UTF-8 错误 body 变为 U+FFFD、共享执行器补默认
`Accept`、bodyless GET 的 Content-Type 沿用 C206、URL host 为 `GoUrl` 子集、reset 的亚 100ns 截断与
DateTimeOffset 范围沿用 C123）。

## 124. NeuralWatt 供应商配额检查器（固定 /v1/quota 端点、月度 kWh 订阅窗口，127 例，2026-11-07）

参照 `f272549f`：`internal/server/biz/provider_quota/neuralwatt_checker.go`（`CheckQuota`:50-84、
`parseResponse`:86-150、`SupportsChannel`:152-158、`isNeuralWattLowRemaining`:160-166、
`buildNeuralWattQuotaURL`:168-185、两个 map 转换器:187-237、报文类型:19-38、`warningRemainingFraction`:17）、
`provider_quota.go:450-452` 的 `registerNeuralWattSupport`（在 `registerSyntheticSupport` 之后、
`registerApertisSupport` 之前调用，见 `provider_quota.go:384`），以及 `neuralwatt_checker_test.go` 的
12 个测试函数。

新增 `src/AxonHub.Api/Orchestration/NeuralWattQuotaChecker.cs`（`IQuotaChecker` 实现、
`NeuralWattUsageResponse` 系列报文类型与 Go 语义 JSON 解码、`time.Parse(time.RFC3339)` 复用共享
`GoTimeParsing` 解析器），`Program.cs` 在 synthetic 之后注册 `NeuralWattQuotaChecker` 与
`ProviderQuotaCheckerRegistration("neuralwatt", …)`（与参照 `registerNeuralWattSupport` 的顺序一致），
`ProviderQuotaCheckerRegistry` 的文档同步为「Claude Code、Codex、Antigravity、xAI Subscription、GitHub
Copilot、NanoGPT、ZenMux、Cline、Synthetic 与 NeuralWatt 已注册，其余 9 个待切片」。同时修正共享
`ProviderQuotaData.PeriodStartFromMonthlyReset`：改用构造后 `.Add(reset.TimeOfDay)`，保留亚毫秒 tick
（此前用 `reset.Millisecond` 会截断；Go 的 `time.Date(..., Nanosecond, ...)` 保留完整纳秒）。

- 凭据：`strings.TrimSpace(Credentials.APIKey)`；为空且 `apiKeys` 非空时直接取 `APIKeys[0]`（**不 trim**），
  都为空（含无凭据、非字符串）报 `channel has no API key`（C013）。
- 采集：GET `buildNeuralWattQuotaURL(BaseURL)`，头为 `Content-Type: application/json` 与
  `Authorization: Bearer <key>`，无 body；`settings.proxy` 存在时**一次**解析代理 executor，畸形 settings
  视为无代理（C013）。
- URL：trim 后为空、`url.Parse` 失败或无 scheme + host 时回退 `https://api.neuralwatt.com/v1/quota`；
  `http` 升级 https；只保留 `scheme://host`，固定 path 为 `/v1/quota`（base 的 path / query 丢弃）。
- 响应：`Do` 错误包装为 `quota request failed: %w`；`StatusCode != 200` 报 `HTTP <code>: <body>`；
  解码失败报 `failed to parse neuralwatt usage response: %w`（Go 解码文案沿用 C003 / C093）。
- 状态：无 subscription 或关键字段不足时为 unknown；`in_overage == true` → exhausted（优先于正 remaining）；
  `kwh_remaining <= 0` → exhausted；`kwh_remaining < kwh_included × 0.2`（严格小于、`included > 0`）→ warning；
  其余 `kwh_remaining` 非空 → available。
- 用量比例：仅当 `kwh_included > 0`；`kwh_used` 非空时 `used / included`（优先），否则 `kwh_remaining` 非空时
  `1 - remaining / included`，否则 0。
- 限额与重置：恒为**一个** token limit，window 为 `kwh`；`kwh_reset_date` 经 RFC3339 解析（失败忽略），
  `NextResetAt` 与 `PeriodStartFromMonthlyReset`（上月同日的最后一天夹紧，保留 offset 与亚毫秒 tick），
  随后 `NormalizeQuotaData` 归一化（ratio 截断、负数 limit 丢弃、过期 reset 清除、显式 available 在 limit
  被丢弃或 exhausted 后仍保留）。
- rawData：仅在指针非空时输出 `balance` / `subscription`，字段按 snake_case 键名逐项省略 nil，保留零值、
  `false` 与空字符串，按 Go map 排序输出（`GoJson`；double 格式沿用 C067）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 线路形状（固定 URL、Bearer 经 Auth、线上无 Content-Type、默认 Accept、无 body / 请求数据形状）、available / warning / missing reset / overage exhausted / zero remaining / unknown 六种基线、状态优先级 11 组、usageRatio 优先级 7 组、ratio>1 截断与显式状态胜出、负 ratio 丢弃、月度 PeriodStart 夹紧 7 组（含 2096 闰年、2100 非闰年与 +05:30）、100ns tick 与 offset 保留、非法 reset 5 组、过期 reset 2 组、rawData 全文 / 空 balance / 空 subscription / 零值与 false / 重复 balance 合并 / 显式 null、凭据 4 组 + 拒绝矩阵 7 组、畸形 JSON、执行器状态 4 组 / 传输 1 组 / 1xx-3xx-2xx 4 组、自定义 base、`buildNeuralWattQuotaURL` 19 组、`SupportsChannel` 12 组、Go 解码根 null / 非对象 4 组、类型不匹配 14 组、重复键合并、null 重置、未知字段、代理路由 3 组 | `tests/AxonHub.Api.Tests/NeuralWattQuotaCheckerTests.cs` | 31 + 96 |

`AxonHub.Api.Tests` 由 3995 增至 4122（+127），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
7441 增至 7568。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]`
的静态计数；下一次全量运行应以 4122 / 319 / 7568 复核。**

差异：C182 由剩余 10 个 checker 收窄为 9 个；新增 C210（NeuralWatt checker 经共享执行器发请求而非
按通道 client、>=400 被包装为 `quota request failed`、非法 UTF-8 错误 body 变为 U+FFFD、共享执行器补默认
`Accept`、bodyless GET 的 Content-Type 沿用 C206、URL host 为 `GoUrl` 子集、reset 的亚 100ns 截断与
DateTimeOffset 范围沿用 C123）。

## 125. Apertis 供应商配额检查器（固定 /v1/dashboard/billing/credits 端点、订阅周期 + PAYG + credits 三族额度，187 例，2026-11-07）

参照 `f272549f`：`internal/server/biz/provider_quota/apertis_checker.go`（`CheckQuota`:77-121、
`parseResponse`:123-158、`SupportsChannel`:160-162、`buildApertisQuotaURL`:165-189、
`determineApertisStatus`:192-229、`determineSubscriptionStatus`:231-250、`determinePaygStatus`:252-281、
`betterStatus`:283-288、`buildApertisLimits`:290-387、`parseApertisCycleStart`:389-401、
`convertApertisResponseToMap`:403-450、`toFloat64`:452-477、报文类型:27-61、`betterStatusRank`:18-25、
`apertisAvailabilityGroup`:67），`provider_quota.go:454-456` 的 `registerApertisSupport`（在
`registerNeuralWattSupport` 之后、`registerOpenCodeGoSupport` 之前调用，见 `provider_quota.go:385`），
以及 `apertis_checker_test.go` 的 24 个测试函数。

新增 `src/AxonHub.Api/Orchestration/ApertisQuotaChecker.cs`（`IQuotaChecker` 实现、
`ApertisBillingCreditsResponse` / `ApertisPayg` / `ApertisSubscription` 报文类型与 Go 语义 JSON 解码、
`fmt.Sscanf("%f")` 的 token 扫描近似、`time.Parse(time.RFC3339)` 复用共享 `GoTimeParsing` 解析器），
`Program.cs` 在 neuralwatt 之后注册 `ApertisQuotaChecker` 与 `ProviderQuotaCheckerRegistration("apertis", …)`
（与参照 `registerApertisSupport` 的顺序一致），`ProviderQuotaCheckerRegistry` 的文档同步为「Claude Code、
Codex、Antigravity、xAI Subscription、GitHub Copilot、NanoGPT、ZenMux、Cline、Synthetic、NeuralWatt 与
Apertis 已注册，其余 8 个待切片」。

- 凭据：`strings.TrimSpace(Credentials.APIKey)`；为空且 `apiKeys` 非空时直接取 `APIKeys[0]`（**不 trim**），
  都为空（含无凭据、非字符串）时返回 unknown 行（RawData `{"error":"missing API key"}`、Ready=false、
  NextResetAt=null）且**不返回错误**、不经过 Normalize（C211）。
- 采集：GET `buildApertisQuotaURL(BaseURL)`，头为 `Content-Type: application/json` 与
  `Authorization: Bearer <key>`，无 body；不处理 proxy（C211）。
- URL：trim 后为空或 `url.Parse` 失败回退 `https://api.apertis.ai`；空 scheme 补 `https`；只保留
  `scheme://host`，固定 path 为 `/v1/dashboard/billing/credits`（base 的 path / query 丢弃）；不做
  http→https 升级、空 host 不回退（C211）。
- 状态：`betterStatusRank` available 3 / warning 2 / exhausted 1 / unknown 0；订阅状态先算：suspended /
  cancelled → exhausted，`cycle_quota_limit <= 0` → unknown，ratio 为 `used / limit`，remaining <= 0 →
  exhausted，ratio >= 0.8 → warning，否则 available；PAYG 状态：`token_is_unlimited` → available，
  `account_credits > 0` 时 total 有效且 > 0 则 ratio >= 1 → available、>= 0.8 → warning、其余 available，
  credits <= 0 时 total 无效或 <= 0 → unknown、否则 exhausted；`shouldSkipPAYG` 仅当
  `is_subscriber && subscription != nil && !payg_fallback_enabled && status == "active" &&
  cycle_quota_remaining > 0`（**区分大小写的 "active"**）；`shouldCheckPayg` 在非订阅、订阅回退启用、
  suspended / cancelled 或周期额度耗尽时为 true；最终状态取订阅与 PAYG 中排名更高者。
- 限额与重置：PAYG token limit（除非被 shouldSkipPAYG 跳过）、`account_credits > 0` 时的 credits limit、
  订阅周期 limit（subscriber 且 subscription 非空）三族，group 均为 `apertis_capacity`，window 分别为
  `pay_as_you_go` / `credits` / `cycle`；订阅 limit 的 Type 为 `subscription_cycle`，PeriodStart 取自
  `cycle_start`（RFC3339）；`cycle_end` 解析成功时作为 NextResetAt；随后 `Normalize` 归一化（ratio 截断、
  负 ratio limit 丢弃、过期 reset 清除、显式状态在 limit 被丢弃或 exhausted 后仍保留）。
- rawData：`is_subscriber`；`payg` 恒有 account_credits / token_used / token_total / token_remaining /
  token_is_unlimited 与可选的 token_monthly_limit_usd / token_monthly_used_usd / monthly_reset_day；
  `subscription` 恒有 plan_type / status / cycle_quota_limit / cycle_quota_used / cycle_quota_remaining /
  cycle_start / cycle_end / payg_fallback_enabled 与可选的 payg_spent_usd / payg_limit_usd；按 Go map
  排序输出（`GoJson`；double 格式沿用 C067）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 线路形状（固定 URL、Bearer 经 Auth、Content-Type、默认 Accept、无 body）、PAYG-only available / warning / exhausted / 空源 unknown、订阅五种状态、suspended / cancelled 回退、unlimited PAYG、非订阅 subscription 对象、大小写 "Active" 的跳过分支、状态函数 19 + 11 + 21 组、归一化与 ratio 截断、周期时间 6 组（含亚 tick 与 +05:30）、rawData 全文与重复键 / null 重置、凭据 4 组、畸形 JSON、类型不匹配全文、执行器 >=400 4 组 / 1xx-3xx-2xx 4 组 / 传输 1 组、自定义 base、`buildApertisQuotaURL` 20 组、`SupportsChannel`、Go 解码 10 组、`TryToFloat64` / Sscanf 边界 5 组 | `tests/AxonHub.Api.Tests/ApertisQuotaCheckerTests.cs` | 54 + 82 + 51 |

`AxonHub.Api.Tests` 由 4122 增至 4309（+187），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
7568 增至 7755。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` /
`MemberData` 的静态计数；下一次全量运行应以 4309 / 319 / 7755 复核。**

差异：C182 由剩余 9 个 checker 收窄为 8 个；新增 C211（Apertis checker 经共享执行器发请求而非直接
httpClient、>=400 被还原为 `apertis API returned status`、错误路径数据不可观察、缺失 key 返回未归一化的
unknown 数据、共享执行器保留 Content-Type 并补默认 Accept、URL host 为 `GoUrl` 子集、Go 解码语义与报文
文案、`fmt.Sscanf` 近似、嵌套 any 的 1e400 与 100ns 截断）。

## 126. OpenCode Go 供应商配额检查器（固定 /zen/go/v1/usage 端点、rolling / weekly / monthly 百分比窗口，122 例，2026-11-07）

参照 `f272549f`：`internal/server/biz/provider_quota/opencode_go_checker.go`（`CheckQuota`:82-106、
`SupportsChannel`:109-111、`parseResponse`:113-210、`parseOpenCodeGoResetsAt`:215-246、
`opencodeGoWindowLabel`:251-262、`opencodeGoPeriodStart`:268-279、`normalizeOpenCodeGoWindowStatus`:281-289、
`quotaStatusRank`:291-302、`opencodeGoAPIKey`:70-78、报文类型:23-50），
`provider_quota.go:458-460` 的 `registerOpenCodeGoSupport`（在 `registerApertisSupport` 之后、Kimi 之前调用，
见 `provider_quota.go:386`），以及 `opencode_go_checker_test.go` 的 16 个测试函数。

新增 `src/AxonHub.Api/Orchestration/OpenCodeGoQuotaChecker.cs`（`IQuotaChecker` 实现、
`OpenCodeGoUsageResponse` / `OpenCodeGoUsageWindows` / `OpenCodeGoUsageWindow` 报文类型与 Go 语义 JSON 解码、
`resetsAt` 的 RFC3339 / Unix 秒 / epoch 毫秒三态解析、`GoTimeParsing` 复用与 5h / 7d / 日历月的周期起点），
`Program.cs` 在 apertis 之后注册 `OpenCodeGoQuotaChecker` 与 `ProviderQuotaCheckerRegistration("opencode_go", …)`
（与参照 `registerOpenCodeGoSupport` 的顺序一致），`ProviderQuotaCheckerRegistry` 的文档同步为「Claude Code、
Codex、Antigravity、xAI Subscription、GitHub Copilot、NanoGPT、ZenMux、Cline、Synthetic、NeuralWatt、Apertis 与
OpenCode Go 已注册，其余 7 个待切片」。

- 凭据：遍历 `ChannelCredentials.GetAllApiKeys`，逐个 `strings.TrimSpace`，取第一个非空项；`apiKey` 为
  OAuth JSON 时被排除，`apiKeys` 依次回退；全部为空（含无凭据、空白、畸形 JSON）时抛出
  `channel has no API key`，不发起请求。
- 采集：GET 固定 `https://opencode.ai/zen/go/v1/usage`（不读 BaseURL），头为
  `Authorization: Bearer <key>` 与 `Accept: application/json`，无 body；`Settings.Proxy` 非空时经代理工厂
  派生执行器，否则用共享执行器（C212）。
- 响应：参照不做显式状态码检查（Do 已对 >=400 报错），端口同样把 >=400 的 `HttpClientError` 包装为
  `opencode go usage request failed: <inner>`，1xx / 3xx / 非 200 2xx 照常解析。
- 窗口：rolling / weekly / monthly 三个指针窗口；`percent` 缺省为 0，`resetsAt` 解析失败（空、null、
  负值、超出 1e15、无法识别的字符串或类型）的窗口跳过；全部跳过时报
  `could not parse OpenCode Go usage windows`；`resetInSec` 为 reset 与 now 之差并夹到 >= 0。
- 状态：`usageRatio = percent / 100`，`>= 1` exhausted、`>= 0.8` warning、其余 available；总体状态取三个
  窗口排名最高者（exhausted 2 / warning 1 / available 0），Ready 跟随总体状态；`NextResetAt` 取最早窗口 reset。
- 限额与 rawData：每个窗口一个 token limit，window 为 `5h` / `weekly` / `monthly`，`PeriodStart` 分别为
  reset-5h / reset-7d / 上一日历月同日（月末夹紧）；rawData 为 `plan_type:"go"` 与按窗口名索引的
  `usage_percent` / `reset_in_seconds` / `reset_time`（秒级 RFC3339）/ `status` / `percent_remaining`
  （`100 - percent`）与可选的 `api_status`（原样透传，不参与状态判定）；随后 `Normalize` 归一化
  （ratio 截断、负 ratio limit 丢弃、过期 reset 清除、rawData 保留原值）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 线路形状（固定 URL、Bearer 经 Auth、显式 Accept、无 body）、三窗口完整映射与 rawData 全文、百分比阈值 6 组、负 percent 丢 limit 保 raw、>100 截断、`NormalizeWindowStatus` 6 组、`QuotaStatusRank` 6 组、`ParseResetsAt` 合法 9 组 / 非法 11 组（含范围兜底）、毫秒小数秒与偏移、Unix 秒 / 毫秒等价 2 组、epoch 毫秒参考值、部分窗口、全部不可解析、无 usage、缺失 resetsAt、`api_status`、过期 reset、最早 reset、月度周期起点 4 组、窗口标签 5 组、`FormatRfc3339` 4 组、凭据 8 组、畸形 / 空 body、类型不匹配 4 组、执行器 >=400 4 组 / 传输 1 组 / 2xx 3 组、Go 解码 null 根 / 非对象根 4 组 / 类型不匹配 6 组 / 空窗口 / 合并 / null 重置 / 未知键 / 显式 null resetsAt / 非法 UTF-8、代理路由 3 组、`SupportsChannel` 6 组 | `tests/AxonHub.Api.Tests/OpenCodeGoQuotaCheckerTests.cs` | 34 + 88 |

`AxonHub.Api.Tests` 由 4309 增至 4431（+122），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
7755 增至 7877。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 4431 / 319 / 7877 复核。**

差异：C182 由剩余 8 个 checker 收窄为 7 个；新增 C212（共享执行器接缝、>=400 包装、错误路径数据不可观察、
数字 resetsAt 按 UTC 呈现而非 Go 本地时区、超出 DateTimeOffset 年份范围的 resetsAt 跳过、Go 解码语义与
UTF-8 修复、GetAllApiKeys 的 C149 冻结边界、注入时钟的读取时机）。

## 127. Kimi Code 供应商配额检查器（BaseURL 派生 /v1/usages、usage 行 + limits 行 + boosterWallet，218 例，2026-11-08）

参照 `f272549f`：`internal/server/biz/provider_quota/kimi_code_checker.go`（`CheckQuota`:50-77、
`SupportsChannel`:79-81、`buildKimiCodeUsageURL`:83-102、`parseKimiCodeUsageResponse`:104-180、
`parseKimiCodeUsageRow`:182-216、`kimiCodeWindowLength`:218-238、`kimiCodeLimitLabel`:240-267、
`kimiCodeResetAt`:269-276、`kimiCodeResetAfterSeconds`:278-285、`parseKimiCodeBoosterWallet`:287-315、
`kimiCodeMoney`:317-325、`kimiCodeFixedPointToCents`:327-332、`kimiCodeInt`:334-344、
`firstKimiCodeInt`:346-353、`firstKimiCodeString`:355-362、`kimiCodeStatusForUsageRatio`:364-372、
`worseKimiCodeStatus`:374-381、报文类型:19-40），`provider_quota.go:387` 的 `registerKimiCodeSupport`
（在 `registerOpenCodeGoSupport` 之后、`registerMinimaxSupport` 之前调用，定义见 `provider_quota.go:462-464`），
以及 `kimi_code_checker_test.go` 的 3 个测试函数。

新增 `src/AxonHub.Api/Orchestration/KimiCodeQuotaChecker.cs`（`IQuotaChecker` 实现、
`KimiCodeUsageResponse` / `KimiCodeUsageRow` / `KimiCodeBoosterWallet` 报文类型与 Go 语义 JSON 解码、
`KimiCodeInt` 的 ParseFloat 与 amd64 饱和转换、`buildKimiCodeUsageURL` 的 URL 重建与 ForceQuery），
`Program.cs` 在 opencode_go 之后注册 `KimiCodeQuotaChecker` 与 `ProviderQuotaCheckerRegistration("kimi_code", …)`
（与参照 `registerKimiCodeSupport` 的顺序一致），`ProviderQuotaCheckerRegistry` 的文档同步为「Claude Code、
Codex、Antigravity、xAI Subscription、GitHub Copilot、NanoGPT、ZenMux、Cline、Synthetic、NeuralWatt、
Apertis、OpenCode Go 与 Kimi Code 已注册，其余 6 个待切片」。

- 凭据：取 legacy `APIKey` 并 `TrimSpace`；为空且 `APIKeys` 非空时取首个 `APIKeys[0]`（不跳过空白项）；
  仍为空（含无凭据、空白、`apiKeys` 首项空白、OAuth-only、畸形 JSON）时抛出 `channel has no API key`，
  不发起请求。
- 采集：GET `buildKimiCodeUsageURL(ch.BaseURL)`，缺省 `https://api.kimi.com/coding/v1/usages`；URL 解析失败、
  scheme 或 host 为空时回退缺省端点；去掉 query 与 fragment、路径去尾斜杠、缺 `/v1` 时补上、再追加 `/usages`，
  空查询保留 `?`；头为 `Authorization: Bearer <key>` 与 `Accept: application/json`，无 body；`Settings.Proxy`
  非空时经代理工厂派生执行器，否则用共享执行器（C213 / C214）。
- 响应：不做显式状态码检查（Do 已对 >=400 报错），端口把 >=400 的 `HttpClientError` 包装为
  `kimi code usage request failed: <inner>`；解码失败包装为 `failed to parse kimi code usage response: <inner>`；
  1xx / 3xx / 非 200 2xx 照常解析。
- 行解析：usage 行（label 缺省 `Weekly limit`、窗口 7d）先加入；limits 每项的 `detail` 为对象时作为行源、
  否则用项本身；limit 与 used 至少一个为数字（字符串按 ParseFloat），仅有 limit 时用 `remaining` 推导 used
  （不做非负检查）；label 取 name / title / scope（键优先，item 先于 detail；行标签不含 scope）；
  ResetAt 键 reset_at / resetAt / reset_time / resetTime，ResetAfterSeconds 键 reset_in / resetIn / ttl / window
  （>0）；无数字的行跳过，全部跳过时报 `kimi code usage response contains no quota limits`。
- 窗口与标签：window.duration / item.duration / detail.duration 取首个正数，单位取 window.timeUnit /
  item.timeUnit / detail.timeUnit，MINUTE / HOUR / DAY（大小写敏感）换算，其余按秒；标签先按
  name / title / scope 取非空字符串，缺失时按窗口时长生成 `5h limit` / `90m limit` / `2d limit` / `45s limit`，
  仍无则 `Limit #<n>`；`NormalizeQuotaWindowLabel` 命中 5h / daily / 7d / 30d 时用归一化标签，否则用行标签。
- 状态与重置：ratio = used / limit（limit<=0 时为 0），>=1 exhausted、>=0.8 warning、其余 available；总体取本地
  排名（available 0 / warning 1 / exhausted 2）最高者，Ready 跟随总体状态；`NextResetAt` 取最早的重置时刻；
  resetAt 字符串按 RFC3339（含亚 tick 小数秒与偏移）解析，存在但不可解析时抑制 reset_in；reset_in 按注入时钟
  回推（C214）。
- 钱包与 rawData：`balance.type == "BOOSTER"` 且 amount > 0 时换算 `balanceCents` / `totalCents`
  （amountLeft 缺省 0；`kimiCodeFixedPointToCents`：`0 < value < 1_000_000` → 1，否则
  `(value + 500_000) / 1_000_000`，溢出按 int64 回绕），`monthlyChargeLimitEnabled` 仅接受 JSON true，
  `monthlyChargeLimitCents` / `monthlyUsedCents` 与币种（charge limit → used → `USD`）；rawData 为
  `{"rows": [...], "boosterWallet": {...}}`（钱包仅在解析成功时出现），随后 `Normalize` 归一化
  （负 ratio limit 丢弃、ratio>1 截断、过期 reset 清除、rawData 保留原值）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 线路形状（缺省端点与 BaseURL 派生、Bearer 经 Auth、显式 Accept、无 body）、参照报文完整映射与 rawData 全文、URL 重建 26 组（ForceQuery / encodePath / scheme 大小写 / 尾斜杠 / `/v1` 补全 / userinfo 原始拼写）、缺省端点回退 3 组、凭据 6 组、usage 行数字与 remaining 推导、行标签 name / title（scope 不参与）、reset_at 键 4 组与优先级、reset_in 键 4 组与别名、窗口单位 8 组与数字字符串、时长标签 7 组、`Limit #n` 3 组、detail 继承与 item / detail 标签分歧、无数字行跳过、空限额报错 3 组、used-only / 负 used / >1 ratio、reset_at 抑制 reset_in、注入时钟锚点、最早 reset、亚 tick 小数秒、偏移保留、过期 reset、状态阈值 6 组、`WorseStatus` 5 组（含 unknown）、本地排名 5 组、钱包（BOOSTER 判定 7 组 / 固定点换算 9 组 / 币种回退 5 组 / 字符串金额 / true 门）、Go 解码 null 根、非对象根 4 组、类型不匹配 6 组、重复键合并、嵌套替换、limits 按索引合并截断、显式 null 重置、null 元素、未知键、非法 UTF-8、`KimiCodeInt` 9 组、`GoFloatToInt64` 7 组、畸形 / 空 body、执行器 >=400 4 组 / 传输 1 组 / 2xx 3 组、代理路由 3 组、`SupportsChannel` 6 组 | `tests/AxonHub.Api.Tests/KimiCodeQuotaCheckerTests.cs` | 56 + 162 |

`AxonHub.Api.Tests` 由 4431 增至 4649（+218），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
7877 增至 8095。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 4649 / 319 / 8095 复核。**

差异：C182 由剩余 7 个 checker 收窄为 6 个；新增 C213（Kimi Code 用量 URL 的 authority 保留原始拼写）、
C214（reset_in 推断时刻的锚点时区与读取时机）与 C215（采集 HTTP 接缝、状态码分类与 Go 解码语义）。

## 128. MiniMax 供应商配额检查器（BaseURL 派生 /v1/token_plan/remains、general 模型 5h / weekly 窗口，161 例，2026-11-08）

参照 `f272549f`：`internal/server/biz/provider_quota/minimax_checker.go`（`CheckQuota`:69-96、
`SupportsChannel`:98-100、`buildMinimaxQuotaURL`:102-114、`minimaxPeriodStart`:119-127、
`minimaxTotalPercent`:131-136、未被调用的 `minimaxUtilization`:138-147、`minimaxStatusForRatio`:151-165、
`parseMinimaxResponse`:167-302、局部 `worseStatus`:304-310、报文类型:16-59），`provider_quota.go:387`
的 `registerMinimaxSupport`（在 `registerKimiCodeSupport` 之后、`registerZhipuSupport` 之前调用，
定义见 `provider_quota.go:466-468`），以及 `minimax_checker_test.go` 的 15 个测试函数。

新增 `src/AxonHub.Api/Orchestration/MinimaxQuotaChecker.cs`（`IQuotaChecker` 实现、
`MinimaxResponse` / `MinimaxBaseResp` / `MinimaxModelRemain` 报文类型与 Go 语义 JSON 解码、
`MinimaxModelRow` 的 rawData 形状），`Program.cs` 在 kimi_code 之后注册 `MinimaxQuotaChecker` 与
`ProviderQuotaCheckerRegistration("minimax", …)`（与参照 `registerMinimaxSupport` 的顺序一致），
`ProviderQuotaCheckerRegistry` 的文档同步为「Claude Code、Codex、Antigravity、xAI Subscription、
GitHub Copilot、NanoGPT、ZenMux、Cline、Synthetic、NeuralWatt、Apertis、OpenCode Go、Kimi Code 与
MiniMax 已注册，其余 5 个待切片」。

- 凭据：取 legacy `APIKey` 并 `TrimSpace`；为空且 `APIKeys` 非空时取首个 `APIKeys[0]` 并 `TrimSpace`
  （不跳过空白项）；仍为空（含无凭据、空白、`apiKeys` 首项空白、OAuth-only、畸形 JSON）时抛出
  `channel has no API key`，不发起请求。
- 采集：GET `buildMinimaxQuotaURL(ch.BaseURL)`，缺省 `https://www.minimaxi.com/v1/token_plan/remains`；
  `TrimSpace` 后为空、解析失败、scheme 或 host 为空时回退缺省端点；否则重建为
  `scheme://parsed.Host + /v1/token_plan/remains`（userinfo、path、query、fragment 全部丢弃；scheme
  小写、host 大小写保留、端口与 IPv6 保留）；头为 `Authorization: Bearer <key>` 与
  `Content-Type: application/json`（参照不设 Accept），无 body；`Settings.Proxy` 非空时经代理工厂派生
  执行器，否则用共享执行器（C216）。
- 响应：不做显式状态码检查（Do 已对 >=400 报错），端口把 >=400 的 `HttpClientError` 包装为
  `minimax quota request failed: <inner>`；解码失败包装为 `failed to parse minimax quota response: <inner>`；
  `base_resp.status_code != 0` 时报 `minimax API error: <status_msg> (code <status_code>)`（先于模型列表
  检查）；模型列表为空报 `minimax quota response contains no model remains`；找不到 `model_name == "general"`
  报 `minimax quota response contains no general model`；1xx / 3xx / 非 200 2xx 照常解析。
- 窗口：5h 窗口恒存在；`intervalTotalPercent = boost>0 ? boost/10.0 : 100.0`，
  `intervalUsedPercent = 100 - remaining`（不做非负夹紧），`intervalPercent = used/total*100`，
  ratio = intervalPercent/100；weekly 窗口仅在 `current_weekly_status == 1` 时加入（0 / 2 / 3 均跳过），
  口径同上（weekly_boost_permille）；窗口标签 `5h` / `weekly`，类型 `Token`。
- 状态与重置：`minimaxStatusForRatio(apiStatus, ratio)`：apiStatus==3 → exhausted；ratio>=1 → exhausted；
  ratio>=0.8 → warning；apiStatus==1 → available；其余 unknown。总体状态由局部 `worseStatus`
  （available 0 / warning 1 / exhausted 2，非表内值按 0）从 available 起取最差；`Ready` 跟随总体状态。
  `NextResetAt` 取 `end_time` / `weekly_end_time`（毫秒 > 0）中最早者（严格 `Before`，同刻保留先者）；
  `PeriodStart` 取 `start_time` / `weekly_start_time`（毫秒 > 0，否则 nil）。
- rawData：`{"rows": [...]}`，行字段与顺序 `modelName, intervalUsedPercent, intervalTotalPercent,
  intervalPercent, intervalStatus, intervalResetAt(omitempty), weeklyUsedPercent, weeklyTotalPercent,
  weeklyPercent, weeklyStatus, weeklyResetAt(omitempty), weeklyBoostPermille(omitempty)`；重置时刻按
  RFC3339 字符串；随后 `Normalize` 归一化（负 ratio limit 丢弃、ratio>1 截断、过期 reset 清除、
  rawData 保留原值）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 线路形状（缺省端点与 BaseURL 派生、Bearer 经 Auth、仅 Content-Type、无 body）、参照报文完整映射与 rawData 全文、URL 重建 28 组（userinfo / path / query / fragment 丢弃、scheme 小写与 host 大小写、端口 / IPv6、非法输入回退）、缺省端点回退 3 组、转义宿主逐字节还原、凭据 11 组、weekly boost 与 interval boost、weekly 状态门 3 组、warning / exhausted / apiStatus 3 / boost 后 available、前置于模型列表的 API 错误、最早 reset、首个 general、阈值 11 组、总百分比 6 组、`PeriodStart` 3 组、本地排名 6 组、`WorseStatus` 7 组、负 ratio 丢弃、ratio 截断、unknown 状态、无起点、过期 reset、不可表示 / 非正 end_time 3 组、空列表 2 组、精确 general 4 组、Go 解码 null 根 / 非对象根 6 组 / 类型不匹配 18 组 / null no-op / 显式 null 重置 / 重复切片合并 / 重复字段与大小写折叠 / 未知键 / int64 边界 / 非法 UTF-8 / null 元素 / 解析前缀、畸形 4 组 / 空 body、执行器 >=400 4 组 / 传输 1 组 / 2xx 3 组、代理路由 3 组、`SupportsChannel` 6 组 | `tests/AxonHub.Api.Tests/MinimaxQuotaCheckerTests.cs` | 37 + 124 |

`AxonHub.Api.Tests` 由 4649 增至 4810（+161），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
8095 增至 8256。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 4810 / 319 / 8256 复核。**

差异：C182 由剩余 6 个 checker 收窄为 5 个；新增 C216（MiniMax 采集 HTTP 接缝、状态码分类、
Go 解码语义与转义宿主还原）。

## 129. ZhiPu 供应商配额检查器（固定 open.bigmodel.cn 用量端点、TOKENS_LIMIT / CREDIT_LIMIT 窗口与多 key 可用性组，144 例，2026-11-08）

参照 `f272549f`：`internal/server/biz/provider_quota/zhipu_checker.go`（`CheckQuota`:130-132、
`SupportsChannel`:134-137、`collectZhipuFamilyQuota`:142-240、`fetchZhipuAccountQuota`:242-262、
`buildZhipuQuotaData`:269-290、`zhipuAggregateStatus`:292-322、`zhipuChannelLimits`:324-332、
`zhipuWindowLimits`:334-353、`zhipuBindingLimits`:355-378、`zhipuBindingRow`:380-403、
`zhipuWorstRows`:405-430、`zhipuAggregateLevel`:432-440、`zhipuEarliestReset`:442-459、
`zhipuRowResetAt`:461-473、`parseZhipuQuotaResponse`:475-481、`parseZhipuFamilyQuotaResponse`:483-495、
`parseZhipuAccountSnapshot`:497-537、`zhipuResponseWindow.toRow`:544-568、`zhipuResponseWindows`:570-583、
`zhipuBuildResponseWindows`:585-605、`zhipuCreditWindowSpec`:607-624、`zhipuWindowSpecForLength`:626-635、
`zhipuWindowSpecByName`:637-646、`zhipuPositionalSpec`:648-659、`zhipuChannelAPIKeys`:661-682、
`zhipuDisabledKeySet`:684-698、`zhipuKeyRef`:700-706、`zhipuKeySuffix`:708-715、
`buildZhipuQuotaURL`:717-723、`zhipuQuotaBaseFromChannelURL`:725-727、`zhipuStatusForRatio`:729-739、
`worseZhipuStatus`:741-752、`orderZhipuBuckets`:754 起），`internal/server/biz/provider_quota.go:389`
的 `registerZhipuSupport`（定义见 `provider_quota.go:470-472`，在 `registerMinimaxSupport` 之后、
`registerZaiSupport` 之前调用），以及 `zhipu_checker_test.go` 的测试函数。

新增 `src/AxonHub.Api/Orchestration/ZhipuQuotaChecker.cs`（`IQuotaChecker` 实现、`ZhipuQuotaResponse` /
`ZhipuQuotaData` / `ZhipuLimitEntry` 报文类型与 Go 语义 JSON 解码、`ZhipuWindowRow` / `ZhipuAccountQuota`
的 rawData 形状、供 zai / charm_hyper 复用的 `CollectFamilyQuota` / `ParseFamilyQuotaResponse`），
`Program.cs` 在 minimax 之后注册 `ZhipuQuotaChecker` 与
`ProviderQuotaCheckerRegistration("zhipu", …)`（与参照 `registerZhipuSupport` 的顺序一致），
`ProviderQuotaCheckerRegistry` 的文档同步为「Claude Code、Codex、Antigravity、xAI Subscription、
GitHub Copilot、NanoGPT、ZenMux、Cline、Synthetic、NeuralWatt、Apertis、OpenCode Go、Kimi Code、
MiniMax 与 ZhiPu 已注册，其余 4 个待切片」。

- 凭据：`ChannelCredentials.GetAllApiKeys` 展开 legacy `APIKey` 与 `APIKeys` 全部条目，逐项
  `TrimSpace`、去重、跳过空白（C149）；`DisabledApiKeys` 按 `ParseDisabledApiKeys` 宽松解析
  （畸形 JSON 视为空集，C013 同族），条目 key 经 Go 语义 `TrimSpace` 后标记禁用，过期条目按注入时钟
  视为可用；无 key 时抛出 `channel has no API key`，不发起请求。
- 采集：GET 固定 `https://open.bigmodel.cn/api/monitor/usage/quota/limit`（参照
  `buildZhipuQuotaURL` → `zhipuQuotaBaseFromChannelURL` 恒返回默认 host，忽略通道 BaseURL）；头为
  原样 key 的 `Authorization`（无 Bearer 前缀）、`Content-Type: application/json` 与
  `Accept-Language: en-US,en`，无 body；`Settings.Proxy` 非空时经代理工厂派生执行器，否则用共享
  执行器（C217）。启用 key 全量在前、禁用 key 在后串行采集；禁用池超过 32 个时截断并打印
  `zhipu quota check truncates disabled keys: channel_id=… disabled_keys=… checked=32` 的 warning。
- 响应：>=400 的 `HttpClientError` 包装为 `zhipu quota request failed: <inner>`；解码失败包装为
  `failed to parse zhipu quota response: <inner>`；`success == false` 报
  `zhipu API error: <msg>`（msg 为空时 `zhipu API error: API error code <code>`）；`data` 为空报
  `zhipu quota response contains no data`；既无 `CREDIT_LIMIT` 也无 `TOKENS_LIMIT` 条目报
  `zhipu quota response contains no TOKENS_LIMIT or CREDIT_LIMIT entries`；1xx / 3xx / 非 200 2xx
  照常解析。全部 key 失败时报 `zhipu quota check failed for all <len(keys)> keys: <首个错误>`；
  启用 key 全失败（禁用 key 有成功也不掩盖）时报 `… all <len(enabledKeys)> enabled keys: …`。
- 窗口：`CREDIT_LIMIT` 条目优先（`EqualFold` 大小写不敏感），按 unit/number 以 int64 纳秒乘法
  （unchecked 回绕）识别 5h / 7d 窗口，无法识别时按索引位置回退（0 → 5h、1 → weekly）；否则读取
  `TOKENS_LIMIT` 条目并先按 `nextResetTime` 缺失 / 非正者优先排序，再按索引映射 5h / weekly。
  行字段 `window` / `usedPercent`（percentage/100）/ `status` / `resetAt` / `usage` / `used`
  （currentValue）/ `remaining`；状态阈值 >=1 exhausted、>=0.8 warning、否则 available。
- 状态与重置：单个可用账户（启用、无错误、有行）时按窗口给出限额；多账户时汇总为
  `zhipu_accounts` availability group 的绑定限额（各账户最紧的已知窗口，严格 `>` 保留先者）；
  总体状态取可用账户最差、全部不可用时 exhausted 且 not ready；`NextResetAt` 取最早非空重置
  （严格 `<`）；rawData 为 `{"level": …, "rows": [...], "accounts": [...]}`，账户含 ref（SHA-256
  前 8 位十六进制）、suffix（末尾 4 个 rune）、disabled / status / ready / level / error / rows。
  行与派生重置按 UTC `yyyy-MM-dd'T'HH:mm:ss'Z'` 写文本（亚秒丢弃，C123 / C217），随后 `Normalize`
  归一化（负 ratio limit 丢弃、ratio>1 截断、过期 reset 清除、rawData 保留原值）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 线路形状（固定 URL、raw Authorization、Content-Type、Accept-Language、无 body）、参照报文完整映射与 rawData 全文、CREDIT_LIMIT unit/number 窗口 11 组、禁用 32 上限与 warning、全失败 / 启用失败计数、凭据 10 组、suffix、Go 解码 null 根 / 非对象根 / 类型不匹配 21 组 / 重复字段合并 / null 重置 / 大小写折叠 / int64 边界 2 组 / 浮点 3 组 / 缺失 success 3 组、状态阈值 8 组 + NaN、`WorseStatus` 6 组、排名 6 组、聚合与绑定限额、重置文本 / 越界 / 非正 2 组 / 不可解析 3 组、执行器 >=400 4 组 / 传输 1 组 / 2xx 3 组、代理路由 3 组、`BuildQuotaUrl` 6 组、`SupportsChannel` 6 组 | `tests/AxonHub.Api.Tests/ZhipuQuotaCheckerTests.cs` | 40 + 104 |

`AxonHub.Api.Tests` 由 4810 增至 4954（+144），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
8256 增至 8400。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 4954 / 319 / 8400 复核。**

差异：C182 由剩余 5 个 checker 收窄为 4 个；新增 C217（ZhiPu 采集 HTTP 接缝、raw Authorization、
Go 解码语义与 UTC 重置文本）。

## 130. Zai 供应商配额检查器（固定 api.z.ai 用量端点、复用 ZhiPu 族采集器，28 例，2026-11-08）

参照 `f272549f`：`internal/server/biz/provider_quota/zai_checker.go`（`zaiDefaultQuotaBaseURL`:11、
`ZaiQuotaChecker` / `NewZaiQuotaChecker`:13-19、`CheckQuota`:21-23、`SupportsChannel`:25-28、
`parseZaiQuotaResponse`:30-33），`internal/server/biz/provider_quota.go:390` 的 `registerZaiSupport`
（定义见 `provider_quota.go:474-476`，在 `registerZhipuSupport` 之后、`registerCharmHyperSupport` 之前调用），
以及 `zai_checker_test.go` 的 5 个测试函数。

新增 `src/AxonHub.Api/Orchestration/ZaiQuotaChecker.cs`：`IQuotaChecker` 薄包装，组合
`ZhipuQuotaChecker` 的共享 `CollectFamilyQuota`；固定 `https://api.z.ai/api/monitor/usage/quota/limit`，
providerType `zai`，`SupportsChannel` 只匹配 `zai` / `zai_anthropic`；`ParseZaiQuotaResponse`
委托 `ParseFamilyQuotaResponse(body, "zai", now)`。`Program.cs` 在 zhipu 之后注册 `ZaiQuotaChecker` 与
`ProviderQuotaCheckerRegistration("zai", …)`（与参照 `registerZaiSupport` 的顺序一致），
`ProviderQuotaCheckerRegistry` 的文档同步为「… MiniMax、ZhiPu 与 Zai 已注册，其余 3 个待切片」。

- 采集：与 zhipu 相同（raw Authorization、Content-Type、Accept-Language、无 body、代理路由、逐 key
  顺序、禁用 32 上限、Go 解码语义），仅 URL 与 providerType 不同（C218）；通道 BaseURL 忽略。
- 文案：共享采集器硬编码 `zhipu` 前缀（`zhipu quota check failed for all …`、`… enabled keys: …`、
  `failed to parse zhipu quota response: …`、`zhipu API error: …`、禁用截断 warning），zai 通道照常出现。
- 聚合：单账户按窗口限额、多账户为 `zhipu_accounts` availability group 的绑定限额；rawData 与
  `NextResetAt` 规则同 §129。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 固定 api.z.ai 端点与 URL 忽略 BaseURL 6 组、raw Authorization / Content-Type / Accept-Language / 无 body、参照报文映射与 rawData、zai / zai_anthropic 通道类型门 5 组、API 错误与缺失 key、全失败族前缀、多 key 的 zhipu_accounts 分组与 exhausted 跳过、40 个禁用 key 截断 warning、ParseZaiQuotaResponse 委托与解码前缀、执行器 >=400 4 组 / 2xx 3 组、代理路由 | `tests/AxonHub.Api.Tests/ZaiQuotaCheckerTests.cs` | 10 + 18 |

`AxonHub.Api.Tests` 由 4954 增至 4982（+28），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
8400 增至 8428。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 4982 / 319 / 8428 复核。**

差异：C182 由剩余 4 个 checker 收窄为 3 个；新增 C218（Zai 复用族采集器的固定主机、provider 标识、
共享错误文案与 availability group）。

## 131. Charm Hyper 供应商配额检查器（BaseURL 派生 /v1/credits、100 credits 基线与严格 200 判定，67 例，2026-11-08）

参照 `f272549f`：`internal/server/biz/provider_quota/charm_hyper_checker.go`（`charmHyperBaseline`:17、
`charmHyperWarningThreshold`:20、`CharmHyperCreditsResponse`:23-25、`CharmHyperQuotaChecker`:28-30、
`NewCharmHyperQuotaChecker`:32-34、`CheckQuota`:37-71、`parseResponse`:73-94、`computeStatus`:96-109、
`SupportsChannel`:111-116、`extractAPIKey`:118-127、`buildCharmHyperQuotaURL`:129-140），
`internal/server/biz/provider_quota.go:391` 的 `registerCharmHyperSupport`（定义见
`provider_quota.go:478-480`，在 `registerZaiSupport` 之后、`registerCommandCodeSupport` 之前调用），
以及 `charm_hyper_checker_test.go` 的 14 个测试函数。

新增 `src/AxonHub.Api/Orchestration/CharmHyperQuotaChecker.cs`：独立 `IQuotaChecker`（不复用 ZhiPu
族采集器）。`Program.cs` 在 zai 之后注册 `CharmHyperQuotaChecker` 与
`ProviderQuotaCheckerRegistration("charm_hyper", …)`（与参照 `registerCharmHyperSupport` 的顺序一致），
`ProviderQuotaCheckerRegistry` 的文档同步为「… Zai 与 Charm Hyper 已注册，其余 2 个待切片」。

- 采集：`BaseURL == ""` 时先取默认 `https://hyper.charm.land`，再按 Go `url.Parse` 重建
  `/v1/credits`（路径替换、查询 / userinfo / fragment 清空，C219）；GET、`Bearer <key>`、
  `Accept: application/json`，无 Content-Type、无 body；`Settings.Proxy` 非空时经代理工厂派生执行器；
  缺失 key（legacy `apiKey` 与 `apiKeys[0]` 都 TrimSpace 后为空）报
  `missing API key for Charm Hyper channel` 且不发起请求；`apiKeys` 只取第一个元素，不再扫描后续 key。
- 判定：transport / >=400 包装为 `executing Charm Hyper quota request: <inner>`（Go httpclient 对
  >=400 返回 `<method> - <url> with status <status>`）；1xx / 3xx / 非 200 2xx 报
  `Charm Hyper quota API returned status <code>: <body>`；解码失败包装为
  `parsing Charm Hyper quota response: <inner>`；缺失 / null 余额报
  `Charm Hyper quota response missing balance field`。
- 状态：usageRatio = 1 − balance/100（负值夹 0）；balance == 0 → exhausted / ratio 1 / not ready；
  balance <= 20 → warning；否则 available；随后 `Normalize` 把 ratio > 1 夹到 1 并令该 limit
  exhausted，但显式整体 warning 保留（负余额 → 整体 warning、limit exhausted）。单一 `credits` 窗口的
  token limit、无 reset / period start；rawData `{"balance": <float64>}`。
- 通道门：`openai` / `openai_responses` 且 `DetectProviderFromURL(BaseURL) == "charm_hyper"`
  （host 等于 `hyper.charm.land` 或以 `.hyper.charm.land` 结尾）；复用 `ProviderQuotaUrlDetection`。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 缺省 / 自定义主机的 GET 线路（仅 Accept + Bearer、无 body）、Go net/url 重建 13 组（ForceQuery、opaque、相对、userinfo / fragment / 查询清理）、严格 200（>=400 执行器文案 vs <400 的供应商文案 3 组）、Go 解码语义 7 组 + 重复键 / null 2 组 + 缺失余额 3 组、100 credits 基线阈值 6 组与负余额 / 下溢、缺 key 3 组与 key 优先级 3 组、代理路由、通道门 8 组 | `tests/AxonHub.Api.Tests/CharmHyperQuotaCheckerTests.cs` | 14 Fact + 53 InlineData = 67 |

`AxonHub.Api.Tests` 由 4982 增至 5049（+67），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
8428 增至 8495。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 5049 / 319 / 8495 复核。**

差异：C182 由剩余 3 个 checker 收窄为 2 个；新增 C219（Charm Hyper 独立 credits 采集、严格 200 判定、
Go 解码与 net/url 重建边界）。

## 132. Command Code 供应商配额检查器（alpha key / Studio cookie 双计费面、plan allowance 表与 Go map 解码，116 例，2026-11-08）

参照 `f272549f`：`internal/server/biz/provider_quota/commandcode_checker.go`（`commandCodeProviderType`:19、
双计费面端点常量:24-30、`commandCodeQuotaUserAgent`:31、`commandCodeMaxResetEpochMillis`:32、
`commandCodeCookieNames`:38-45、`commandCodePlanAllowance`:49-53、`commandCodePlanAllowances`:61-70、
`commandCodeTeamProAllowance`:74、`commandCodeAPIKey`:80-90、`commandCodeCookie`:93-104、
`HasCommandCodeQuotaCredentials`:110-121、`SupportsChannel`:135-141、`CheckQuota`:143-175、
`commandCodeFetchQuota`:177-192、`commandCodeGet`:196-224、`commandCodeStatusError`:229-240、
`NormalizeCommandCodeCookie`:252-321（已由 C144 移植）、`parseCommandCodeCredits`:326-478、
`commandCodeWindow` 与 `parseCommandCodeWindow`:480-511、`usageRatio`:521-529、`raw`:531-541、
`earliestCommandCodeReset`:543-556、`parseCommandCodeReset`:558-582、`commandCodeResetFromEpoch`:584-594、
`commandCodeMatchPlan`:600-620、`parseJSONObject`:622-631 与折叠辅助件:633-703），
`internal/server/biz/provider_quota.go:392` 的 `registerCommandCodeSupport`（定义见
`provider_quota.go:410-412`，在 `registerCharmHyperSupport` 之后、`registerOllamaSupport` 之前调用），
以及 `commandcode_checker_test.go` 的测试函数。

新增 `src/AxonHub.Api/Orchestration/CommandCodeQuotaChecker.cs`。`Program.cs` 在 charm_hyper 之后注册
`CommandCodeQuotaChecker` 与 `ProviderQuotaCheckerRegistration("commandcode", …)`（与参照
`registerCommandCodeSupport` 的顺序一致），`ProviderQuotaCheckerRegistry` 的文档同步为「… Zai、Charm
Hyper 与 Command Code 已注册，其余 1 个待切片」。`ProviderQuotaService.HasCredentialsForProvider` 的
Command Code 分支改为调用 checker 的 `HasQuotaCredentials`，删除服务内重复的 key / cookie 解析；
`InvalidProviderQuotaCredentialsException` 改为显式双构造函数以携带 detail 文案。

- 采集：账号 API key（`GetAllApiKeys` 按 legacy 顺序取第一个 TrimSpace 非空者，C149）走
  `/alpha/billing/credits` 与 `/alpha/billing/subscriptions`（Bearer）；无 key 时用
  `Settings.ProviderQuota.CommandCode.AuthCookie`（TrimSpace + `NormalizeCommandCodeCookie`，C144）走
  `/internal/billing/*`（仅 `Cookie` 头）。key 面 401/403 且 cookie 非空时静默回退到 cookie 面；
  cookie 为空或错误非凭据类时照常抛出。请求头：`Accept: application/json, text/plain, */*`、
  `Accept-Language: en-US,en;q=0.9`、Chrome 126 UA、`Origin` / `Referer: https://commandcode.ai/`；
  `Settings.Proxy` 非空时经代理工厂派生执行器。credits 失败即失败；subscriptions 失败只丢 plan 信息。
- 判定：401/403 报 `Command Code billing API returned <code> (invalid API key?)` /
  `(expired session cookie?)` 并包装为 invalid-credentials；其余非 2xx（含 3xx）报
  `Command Code billing API returned <code>`；transport 错误包装为
  `Command Code billing request failed: <inner>`；2xx 的任意状态码仍解析 body；缺凭据报
  `channel has no Command Code API key or quota cookie`，损坏 cookie 报
  `invalid Command Code auth cookie: <inner>`。
- 解码：复刻 `map[string]interface{}` 语义（root 对象、float64 数字、ASCII 折叠、重复键最后者胜、
  折叠重复键按文档顺序取首个、root null 报 `empty JSON object`、类型错误报 Go unmarshal 文案）；
  credits 对象优先取嵌套 `credits`，窗口在 root 或 credits 内（root 优先）；subscriptions 取
  `data` / `subscription` 包裹，失败 fail-open。
- 状态：five_hour（别名 5h / fiveHour）与 weekly 两窗的 usage_percent（否则 used/cap）按
  0.8 / 1.0 阈值生成 subscription-cycle 限额，整体取最差；两窗都存在且 plan 行 5h/weekly cap 匹配、
  `monthlyRemaining <= allowance` 时追加可信 monthly 窗口（`monthly_limit_usd` 线上优先、否则表值）；
  无窗口的按量计划按余额 / 已购 > 0 → available、否则 exhausted，单一 monthly 限额；nextResetAt 取
  两窗最早 reset。重置解析接受 Unix 秒 / 毫秒（>= 1e12）与 RFC3339，`<= 0` 或 `> 1e15` 丢弃；
  rawData 的 reset_time 按 UTC `yyyy-MM-dd'T'HH:mm:ss'Z'` 渲染（C217 同族）。
- 通道门：`commandcode` / `commandcode_anthropic` 且 `HasQuotaCredentials`（有 key，或已配置 cookie，
  即使损坏）；null 通道 false。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| key / cookie 双路径与 401/403 回退（cookie 空、错误非凭据时不回退）、请求头与 Bearer / Cookie、代理路由、状态分类（401/403 凭据文案、429 / 5xx 通用文案、3xx、2xx 非 200、transport 文案）、Go map 解码语义 7 组与重复键 / 折叠键、plan 表 11 组、阈值 5 + 3 组、按量分支 5 组、重置 14 组 + 非标量、月度分母可信条件（线上 / 表值 / 超额 / 负余额）、rawData 字段省略、nextResetAt、通道门 13 + 9 组 | `tests/AxonHub.Api.Tests/CommandCodeQuotaCheckerTests.cs` | 41 Fact + 75 InlineData = 116 |

`AxonHub.Api.Tests` 由 5049 增至 5165（+116），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
8495 增至 8611。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 5165 / 319 / 8611 复核。**

差异：C182 由剩余 2 个 checker 收窄为 1 个；新增 C220（Command Code 双计费面回退、plan allowance 表、
Go map 解码语义与 UTC 重置文本）。

## 133. Ollama 供应商配额检查器（API key 余额 / 会话 cookie 计量页双面采集、HTML 抓取与 Go 匿名结构体解码，123 例，2026-11-08）

参照 `f272549f`：`internal/server/biz/provider_quota/ollama_checker.go`（常量 `ollamaProviderType`:22、
`ollamaBalanceURL`:25、`ollamaSettingsURL`:28、`ollamaQuotaUA`:29、`ollamaCookieName`:30、
`ollamaCookiePrefix`:31、`ollamaMinCookieLen`:35；`OllamaQuotaChecker`:43-46、`NewOllamaQuotaChecker`:49-54、
`ollamaAPIKey`:56-66、`ollamaRawCookie`:68-74、`HasOllamaQuotaCredentials`:80-86、`SupportsChannel`:90-96、
`CheckQuota`:101-132、`fetchBalance`:134-154、`ollamaBalanceStatusError`:156-162、`fetchSettings`:164-191、
`NormalizeOllamaCookie`:201-241、三个正则:243-247、`ollamaUsageWindow`:249-254、
`ollamaBalanceResponse`:259-273、`ollamaBalanceWindow`:275-278、`parseBalance`:280-340、
`clampOllamaPercent`:342-344、`parseResponse`:346-405、`buildOllamaQuotaData`:407-465、
`ollamaWindowDuration`:469-478、`normalizeOllamaWindowStatus`:480-488、`ollamaLooksLikeLoginPage`:498-514），
`internal/server/biz/provider_quota.go:393` 的 `registerOllamaSupport`（定义见
`provider_quota.go:414-416`，在 `registerCommandCodeSupport` 之后调用），以及 `ollama_checker_test.go`
的 24 个测试函数（其中 `TestOllama_CheckQuota_RejectsHTTPSDowngrade`:325 与
`TestOllama_CheckQuota_BalanceRejectsHTTPSDowngrade`:608 未移植，见 C221）。

新增 `src/AxonHub.Api/Orchestration/OllamaQuotaChecker.cs`。`Program.cs` 在 commandcode 之后注册
`OllamaQuotaChecker` 与 `ProviderQuotaCheckerRegistration("ollama", …)`（与参照 `registerOllamaSupport`
的顺序一致），`ProviderQuotaCheckerRegistry` 的文档同步为「全部 19 个 provider checker 已注册」；
`ProviderQuotaService.HasCredentialsForProvider` 的 ollama / ollama_anthropic 分支改为调用 checker 的
`HasQuotaCredentials`。

- 采集：账号 API key（`GetAllApiKeys` 按 legacy 顺序取第一个 TrimSpace 非空且未禁用者，C149）走固定
  `https://ollama.com/api/balance`（`Authorization: Bearer <key>` 字面头、`Accept: application/json`，
  无 UA、无 body）；key 面 401/403 且已配置 `Settings.ProviderQuota.Ollama.AuthCookie` 时静默回退到
  cookie 面；cookie 经 `NormalizeOllamaCookie` 只保留 `__Secure-session`（UTF-8 字节长度 >= 30），
  GET 固定 `https://ollama.com/settings`（`Cookie`、`Accept: text/html,application/xhtml+xml`、
  Chrome 126 UA，无 body）；`Settings.Proxy` 非空时经代理工厂派生执行器。双面都缺报
  `channel has no Ollama API key or quota cookie`；损坏 cookie 报
  `invalid Ollama auth cookie: <inner>`。
- 判定：balance 401/403 报 `Ollama balance API returned <code>`（凭据类），其余非 2xx（含 1xx / 3xx）
  报同名通用错误，transport 包装为 `Ollama balance request failed: <inner>`；settings 401/403 报
  `Ollama settings page returned <code> (expired session cookie?)`，其余非 2xx 报
  `Ollama settings page returned <code>`，transport 包装为
  `Ollama settings request failed: <inner>`；无窗口时命中登录页标记（`<title>sign in`、`sign-in`、
  `signin`、`wos-session`、`log in to your account`、`/signin`）报
  `ollama.com/settings returned the sign-in page (expired session cookie?)`，否则报
  `no Ollama usage windows found in settings page (expired cookie or markup change)`。
- 解析：balance 报文按 Go 匿名结构体解码（root null 空操作、类型错误逐字复刻 reflect 类型文本、
  `time.Time` 错误文案、ASCII 折叠、重复键合并、null 指针 / 值字段、下溢归零），月额度 →
  monthly 百分比窗口、legacy session / weekly → 5h / weekly 窗口、purchased > 0 → `credits` 限额与
  rawData；HTML 页按 UTF-8 字节定位首个关键字并在 4000 字节窗口内抓 `aria-label` / 百分比 /
  `data-time`（Go `\s` 为 ASCII 集合）；rawData 的 reset_time 保留输入偏移的 RFC3339 文本（C221）。
- 通道门：`ollama` / `ollama_anthropic` 且 `HasQuotaCredentials`（有 key 或已配置 cookie，即使损坏）；
  null 通道 false。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| settings 页双窗口线路与 rawData、警告 / 耗尽阈值、缺凭据 3 组、401/403 与登录页 200 凭据分类、登录页标记、仅 session 窗口 / 无窗口 / 空 body、settings 状态分类 4 组、cookie 归一化 6 + 10 组与 UTF-8 字节长度、balance 月度 / credits / 免费额度耗尽 / legacy 窗口 / 无配额、401/403 2 组、key→cookie 回退与 5xx 不回退、禁用 key 6 组、legacy 耗尽 + credits 2 组、transport 包装与执行器映射、Go 解码文案 29 组与 root null 2 组 / 下溢 / 重复键 / null 字段 / ASCII 折叠 4 组 / 未知字段、时间拼写 4 组 / 亚秒截断 / 不可表示时间 2 组 / 偏移保留 3 组 / 过去 reset、百分比夹紧 2 组、4000 字节窗口 2 组、ASCII 分隔符 2 组、首个关键字、非法 reset 2 组、非法百分比、非法 UTF-8、通道门 8 组与 settings 为 null | `tests/AxonHub.Api.Tests/OllamaQuotaCheckerTests.cs` | 30 Fact + 93 InlineData = 123 |

`AxonHub.Api.Tests` 由 5165 增至 5288（+123），`AxonHub.Persistence.Tests` 保持 319，四个项目合计由
8611 增至 8734。**本切片按项目规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态
计数；下一次全量运行应以 5288 / 319 / 8734 复核。**

差异：C182 由剩余 1 个 checker 收窄为 0（C182 resolved，全部 19 个 provider checker 已落地）；
新增 C221（Ollama 双面凭据回退、HTML 计量抓取、Go 匿名结构体解码与 HTTPS 降级拒绝缺口）。

## 134. HTTP 重定向循环复刻与 HTTPS 降级拒绝策略（24 例，2026-11-08）

参照 `f272549f`：Go `net/http/client.go` 的重定向循环（`do`:597-755、`redirectBehavior`:512、
`checkRedirect`:502、`uerr`:622-638、`defaultCheckRedirect`:833、`shouldCopyHeaderOnRedirect`:1005、
`stripPassword`:1047、`refererForURL`:147），`llm/httpclient/client.go` 的策略客户端
（`WithRejectHTTPSDowngrade`:110-130、`WithRejectRedirects`:132-143、`rejectHTTPSDowngrade`:146-151，
`NewHttpClientWithProxy`:103-108 在派生代理客户端时保留降级策略），以及四个调用点：
`internal/server/biz/channel_llm.go:757-763`（命令码通道）、
`internal/server/biz/model_fetcher.go:542-548`（命令码 / codex / 第三方 codex 目录；`:562` 的
`WithRejectRedirects` 随冻结基线未移植，C158）、`ollama_checker.go:108-114`、
`zenmux_checker.go:74-78`。参照测试：`llm/httpclient/client_test.go:424-430`、
`ollama_checker_test.go:325` 与 `:608`（后两者此前登记为未移植，见 C221）。

重写 `src/AxonHub.Llm/Http/HttpExecutor.cs`：重定向不再交给 `SocketsHttpHandler`，而是在执行器内复刻
Go 的循环——301/302/303 除 GET/HEAD 外改写为 GET 并丢弃 body 与 Content-Encoding / Content-Language /
Content-Location / Content-Type；307/308 保留方法与 body（Go 在初始请求的 `GetBody == nil` 且有 body 时
放弃跟随，端口以字节重放，等价于 `GetBody` 恒存在）；目的地离开初始域名后
Authorization / Www-Authenticate / Cookie / Cookie2 / Proxy-Authorization / Proxy-Authenticate 粘性剥离，
子域（IDNA 比较，含 `:` / `%` 不复制）保留；Referer 在 https→http 时抑制、显式 Referer 优先、
否则取上一跳 URL 去掉 userinfo；无 Location 的 3xx 原样返回；Location 解析失败按 `uerr` 的
`<op> "<url>": failed to parse Location header "<loc>": ...` 形状报错并丢弃响应；CheckRedirect 拒绝时
把 3xx 响应头交给 `OnResponseHeaders` 再包装为 `<prefix><op> "<loc>": <reason>`；默认客户端 10 跳上限
（`stopped after 10 redirects`），安装任一策略后无上限（Go 的策略钩子替换默认钩子）。新增
`HttpRedirectPolicy`（Follow / RejectHttpsDowngrade / RejectAll）、`HttpRedirectException` 与
`HttpRedirectPolicies`（`WithRejectHttpsDowngrade` / `WithRejectRedirects` 扩展方法，脚本执行器原样返回）。

接线：`PersistentOutboundTransformer.CustomizeExecutor` 对 commandcode / commandcode_anthropic 候选安装降级
拒绝（C124 resolved）；`ModelFetcher` 对命令码与 codex 通道安装（C158）；Ollama / Zenmux 检查器在
`ResolveExecutor` 选择代理之后安装（C221 / C207）。`Program.cs` 的共享 `SocketsHttpHandler`、代理工厂、
Vertex 与 Antigravity 默认 handler 全部改为 `AllowAutoRedirect = false`（重定向由执行器负责），调用方
自备的 transport 不强制。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 相对跳转与 GET 保持、302 后 POST→GET 且丢 body 与 body 头、307 保留方法与 body、跨域剥离敏感头、子域保留 Authorization、Referer 取上一跳、10 跳上限与文案、https→http 拒绝与精确文案、https→https 放行、策略客户端无跳数上限、RejectAll 文案、无 Location 的 3xx 原样返回、拒绝时观察 3xx 响应头、DoStream 的 stream 前缀、不可解析 Location 的报错形状、脚本执行器不受策略影响 | `tests/AxonHub.Llm.Tests/HttpRedirectTests.cs` | 16 Fact |
| 命令码两类候选经 CustomizeExecutor 拒绝降级且只发一次请求、普通候选不受策略影响 | `tests/AxonHub.Api.Tests/PersistentOutboundRetryTests.cs` | 1 Fact + 2 InlineData = 3 |
| 命令码模型目录拒绝降级且密钥不落入明文、普通 OpenAI 通道照常跟随 | `tests/AxonHub.Api.Tests/ModelFetcherTests.cs` | 2 Fact |
| Ollama balance（Bearer）与 settings（cookie）两面拒绝降级 | `tests/AxonHub.Api.Tests/OllamaQuotaCheckerTests.cs` | 2 Fact |
| Zenmux 配额请求拒绝降级 | `tests/AxonHub.Api.Tests/ZenmuxQuotaCheckerTests.cs` | 1 Fact |

`AxonHub.Api.Tests` 由 5288 增至 5296（+8），`AxonHub.Llm.Tests` 新增 16 例，`AxonHub.Persistence.Tests`
保持 319，四个项目合计由 8734 增至 8750。**本切片按项目规则未运行 lint / build / test，上述数字为
`[Fact]` / `[InlineData]` 的静态计数；下一次全量运行应以 5296 / 319 / 8750 复核。**

差异：C124 由剩余 1 项（降级拒绝）收窄为 0（resolved）；C158 的第 ③ 项收窄为「按通道代理」，降级拒绝已移植；
C207 / C221 的降级缺口关闭（accepted，Ollama 125 例）；新增 C222（Go 重定向循环的端口残差：URI 解析、
错误文案、Referer 渲染与 Host 保留）。

## 135. 存储策略与响应分块持久化（C020 闭合，66 + 11 例，2026-11-08）

参照冻结基线 `b8bb3421`（实时上游 `f272549f` 在本切片范围内一致）：`internal/server/biz/system.go:237-243`
的 `StoragePolicy`（`store_chunks` / `live_preview` / `store_request_body` / `store_response_body` /
`cleanup_options`）、`system_default.go:71-77` 的默认策略（false / false / true / true）与
`:11-69` 的 `defaultCleanupOptions` / `mergeCleanupOptions`、`system.go:981-1038` 的读取与写入
（缺行返回默认策略、旧文档缺两个 body 键时按旧行为回填 true、`cleanup_days <= 0` 拒绝），
`DataStorageService.GetDefaultDataStorage`（data_storage.go:337-361）与 `getDataStorage` /
`shouldUseExternalStorage`（request_internal.go:10-22、request.go:64-72）、写路径的策略门禁
（request.go:198 / 334 / 521 / 539 / 638 / 736 / 830，失败时 body 默认 true、chunks 默认 false）、
`request.go:1011-1258` 的 `isBinaryStreamChunk` / `shouldSkipStoredStreamChunk` /
`marshalStreamEventForStorage` / `SaveRequestChunks` / `SaveRequestExecutionChunks`、
`request.go:1369-1683` 的六个正文 / chunk loader 与 `_ext` 标记折叠、`request.go:725-814` 的
快照写入，以及 `internal/ent/migrate/datamigrate/v0.4.0.go` 的主存储种子迁移。

实现：新增 `StoragePolicy` / `StoragePolicyCodec`（`AxonHub.Api.Settings`）——键 `storage_policy`，
缺行返回默认、Go JSON 解码（字段名大小写不敏感、缺省布尔为 false、旧文档按字符串包含回填两个 body
开关、cleanup 列表按首见顺序合并默认项）、`GoJson.MarshalToString` 序列化；写路径的读取失败被
`RequestPayloadStorage.TryGetStoragePolicyAsync` 吞掉并按参照默认值继续（取消原样抛出）。新增
`RequestPayloadStorage`（`AxonHub.Api.Management`）复刻外部存储半边：默认存储 id 读取（缺失视为 0、
解析失败文案 `failed to parse default data storage ID: expected integer`、外层再包
`failed to get default data storage ID: `）、`GetDataStorage` / `GetDataStorageByID` /
`GetPrimaryDataStorage` 的三条缺失文案、`ShouldUseExternalStorage`（非 primary 即外部，type 不参与）、
`SaveData` / `LoadData` / `DeleteData` 的 database 特例（database 类型下 key 即数据、写入与删除均为
no-op）、`WriteResponseBodyAsync` 与快照写入的 `_ext` 标记 / 外部写入失败不动列 / 数据库写失败回滚
外部对象、六个 loader 的状态门（response body 仅 completed、chunks 仅 stream + 终态）、外部 JSON
合法性校验、`_ext` 标记折叠与 `{}` 空正文回退。新增 `RequestChunkStorage`（`AxonHub.Api.Persistence`）
复刻分块编组：二进制判定（`audio/` 前缀或 `application/octet-stream`，Trim + 小写）、跳过 nil /
非二进制 `[DONE]` / `BinaryStreamDoneEventType`、二进制摘要 `{"object":"binary.stream_chunk",
"content_type":<Trim 后类型>,"bytes":<len(Data) 或 Size>}`、`last_event_id` → `event` → `data` 的
手写包装（`last_event_id` 空时省略、data 无效 JSON 时整块跳过、空 data 输出 `null`、
`GoJson.Marshal` 的 HTML 转义与 `MarshalRaw` 的压缩），空结果返回 `[]` 且不写库。`RequestChainRecorder`
接入 `SaveRequestChunks` / `SaveExecutionChunks`：策略读取 → 编组 → 行读取 → 外部写入 + 标记或
数据库列写入，错误前缀逐字复刻（`failed to get request:` / `failed to save chunks to external
storage:` / `failed to mark request chunks as external:` / `failed to mark execution chunks as
external:` / `failed to save response chunks:`），标记写失败回滚外部对象。新增
`DataStorageBootstrap.EnsurePrimaryDataStorageAsync`（`AxonHub.Persistence`）在
`DatabaseMigrator.EnsureCreatedAsync` 之后启动执行：查询 `primary = TRUE AND deleted_at = 0`，
已存在则跳过；否则单事务插入 Name `Primary` / Description `Primary database storage` /
Type `database` / Settings `{}` / Status `active` 的行并写 `systems.default_data_storage_id`。
中间件按参照顺序接线：execution 成功先 `UpdateExecutionResult` 再存 chunk、失败分支先存 chunk 再
`PersistExecutionFailure`、聚合异常先 `UpdateExecutionStatus` 再存 chunk；request 失败先存 chunk 再
`UpdateRequestStatus`、成功先 `UpdateRequestResult` 再存 chunk。GraphQL 的 Request / RequestExecution
正文与 chunk 字段改经 `RequestPayloadStorage` 的 loader，trace 段构建同样改经 `LoadRequestBody` /
`LoadResponseBody`（外部存储读取随之接入，后端实现差异仍见 C199）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 二进制判定（大小写 / 空白 / octet-stream / 非二进制）、nil 与 `[DONE]` / `binary.done` 的跳过条件、包装字段顺序与 HTML 转义、空 data 与 `last_event_id`、无效 raw JSON 跳过、二进制摘要与 Size 回退、`MarshalChunks` 全过滤与空列表、`_ext` 分块标记 | `tests/AxonHub.Api.Tests/RequestChunkStorageTests.cs` | 13 Fact + 8 InlineData = 21 |
| 存储策略缺行 / 旧文档 / 畸形 JSON / cleanup 校验与序列化、写路径失败默认值、默认存储 id 与双包裹、按 id / primary 查找的三条缺失文案、database 与外部存储的 Save / Load / Delete、body 写入与 `_ext` 标记、快照写入的门禁 / 外部写失败 / 数据库写失败回滚（含回滚失败）、六个 loader 的状态门 / 标记折叠 / JSON 校验 / 存储查找回退 | `tests/AxonHub.Api.Tests/RequestPayloadStorageTests.cs` | 44 Fact |
| 成功流的两侧 chunk 归档（含 `[DONE]`）与失败流的 chunk 先于状态写入、无结果 / 无用量写入 | `tests/AxonHub.Api.Tests/RequestChainPersistenceTests.cs` | 新增 1 Fact（既有成功用例追加断言） |
| 空库种子行与 `default_data_storage_id`、重复执行为 no-op、已有 primary 时不动 `systems`、非 primary 行不抑制种子 | `tests/AxonHub.Persistence.Tests/DataStorageBootstrapTests.cs` | 4 Fact |
| primary 查询过滤软删行并保持 id 顺序、`setResponseBody=false` 不动正文、chunks 单独写入、`GetById` 缺失返回 null | `tests/AxonHub.Persistence.Tests/DataStorageStoreTests.cs`、`RequestStoreTests.cs`、`RequestExecutionStoreTests.cs` | 1 + 3 + 3 Fact |

`AxonHub.Api.Tests` 由 5296 增至 5362（+66），`AxonHub.Persistence.Tests` 由 319 增至 330（+11），
`AxonHub.Llm.Tests` 保持 1455，四个项目合计由 8750 增至 8827。**本切片按项目规则未运行
lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态计数；下一次全量运行应以
5362 / 330 / 8827 复核。**

差异：C020 resolved（存储策略、分块归档、外部存储读写与 v0.4.0 种子全部接入）；C191 的
「外部正文 / chunk 读取分支缺席」关闭，剩余顺序加载 / 无日志 / 多错误选择顺序（accepted）；
C196 的第 (8) 项（主存储种子未移植）关闭；C192 的「DataStorage 子系统尚未移植」原因描述同步更新。

## 136. 通道价格读取边与查询隐私（C048 读取面闭合、`node(Channel)` / `channelModelPrices` / `versions`，9 + 1 例，2026-11-08）

参照把 `channel_model_prices` 的读取全部交给 ent / gqlgen：`Noder` 的 Channel 分支是
`Channel.Query().Where(channel.ID(id)).Only(ctx)`（gql_node.go:247-255），生成的边访问器是
`Channel.ChannelModelPrices` → `QueryChannelModelPrices().All(ctx)`（gql_edge.go:131-141）、
`ChannelModelPrice.Channel` → `QueryChannel().Only(ctx)`（143-149）、
`ChannelModelPrice.Versions` → `QueryVersions().All(ctx)`（159-169）、
`ChannelModelPriceVersion.ChannelModelPrice` → `QueryChannelModelPrice().Only(ctx)`（171-177）。
SDL 形状为 `Channel.channelModelPrices: [ChannelModelPrice!]`、`ChannelModelPrice.channel: Channel!` /
`versions: [ChannelModelPriceVersion!]`、`ChannelModelPriceVersion.channelID: Int!` /
`channelModelPriceID: ID!` / `status: ChannelModelPriceVersionStatus!` / `effectiveStartAt` /
`effectiveEndAt` / `channelModelPrice: ChannelModelPrice!`（ent.graphql:574-590、641-666）。

三个 ent schema 的查询隐私链相同：`APIKeyScopeQueryRule(read_channels)` → `OwnerRule()` →
`UserReadScopeRule(read_channels)` → `default deny`；拒绝文案是 ent 的
`<rule 文案>: ent/privacy: deny rule`。端口把求值做成显式前置检查
`ChannelPrivacy.EvaluateQueryAsync`：测试 principal 直接放行；API key 无 `read_channels` 立即拒绝；
owner 放行；无用户拒绝 `no user in context`；用户经直接 scopes 或系统角色 scopes 命中
`read_channels` 放行；其余落 `default deny`。`PrivacyDenyException` 由 GraphQL 错误过滤器映射为
`permission denied` / FORBIDDEN。求值点覆盖 `node(Channel)` 与四条读取边；`ChannelModelPrice` /
`ChannelModelPriceVersion` 在参照的 `guidTypeToNodeType` 里没有条目（graphql.go:206-226），
因此 `node()` 对两者仍报 `unknown node type: ...`（端口一致，C165）。

读取语义：`ChannelAdminStore.GetByIdAsync` 与 `ChannelModelPriceStore.GetByIdAsync` 都是
`id = @Id AND deleted_at = 0` 的 Dapper 单行查询；缺失行分别报 `ent: channel not found` /
`ent: channel_model_price not found`。价格 JSON 按 Go / shopspring 的字符串小数解码，GraphQL
`Decimal` 输出为 JSON 数字，`channelID`（价格）为 Channel gid、`channelID`（版本）为 Int、
`channelModelPriceID` 为价格 gid；未知版本状态按 gqlgen 的枚举套用文案失败。参照的四条边没有
ORDER BY；端口存储固定 `ORDER BY id` 并过滤软删行（可重复的确定性判定，参照的行序本身不可依赖）。
根查询 `channels` / `queryChannels` / `allChannelSummarys` / `allChannelTags` /
`countChannelsByType` 的隐私与 scopes 门禁、以及 Channel 变更策略仍属 C014 裁剪（§65 / §66
按前端可用性接入，不做隐私求值）。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| `node(Channel)` 成功与 `ent: channel not found`、前端 `GetChannelModelPrices` 文档原文（软删行跳过、按 id 排序、定价树 / 变体 / 调度全字段）、版本边与两条反向边（gid / status / effective 时间）、未知状态枚举文案、并发软删下两条反向边的 NotFound 文案 | `tests/AxonHub.Api.Tests/AdminChannelsEndpointTests.cs` | 5 Fact |
| 服务账号无 / 有 `read_channels`、非 owner 用户无 / 有 `read_channels` 的 node(Channel) 与嵌套价格边隐私分支（拒绝为 `permission denied`） | `tests/AxonHub.Api.Tests/AdminChannelsEndpointTests.cs` | 4 Fact |
| `ChannelModelPrice` / `ChannelModelPriceVersion` 字段与 Node 接口、`ChannelModelPriceVersionStatus` 枚举值（扩展既有 introspection 用例） | `tests/AxonHub.Api.Tests/AdminChannelModelPriceEndpointTests.cs` | 既有用例追加断言 |
| back-edge 的 `GetByIdAsync` live 行谓词（软删与缺失返回 null） | `tests/AxonHub.Persistence.Tests/ChannelModelPriceStoreTests.cs` | 1 Fact |

§136 落地后的静态计数为 `AxonHub.Api.Tests` 5371、`AxonHub.Persistence.Tests` 331、
`AxonHub.Llm.Tests` 1455、`AxonHub.Grains.Tests` 1680，四项目合计 8837。**本切片按项目规则未运行
lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态计数；下一次全量运行应以
5371 / 331 / 8837 复核。**

差异：C048 resolved（读取边接入；进程内价格缓存刷新不可观察）；C165 的 Channel `node()` 分支关闭
（剩余 7 个 ent 类型）；C014 的通道 node / 价格读取边隐私复核项同步更新，根查询隐私与写入门禁仍为裁剪。

## 137. 所有 double 字段的 Go 数序格式化（C067 闭合，13 例，2026-11-08）

参照的每个 wire 结构都由 Go `encoding/json` 序列化，float64 统一使用同一套格式化：
`1e-6 ≤ |v| < 1e21` 用定点记法，之外用科学计数，负指数去掉前导零（`1e-7`、`1e+21`）；
NaN / Infinity 在写出阶段失败。端口此前只在 `cost` 等标注 `GoOptionalNumberConverter` 的字段与
手写 writer 上复刻该规则，其余 `double`（temperature / top_p / penalty / logprob / embedding
向量 / moderation 分数）仍按 System.Text.Json 的默认格式，`5e-6` 会写成 `5E-06`、`1e21` 写成
`1E+21`（C067）。

端口在共享的 `LlmJson.Options` 注册 `GoDoubleJsonConverter`：`Write` 经 `GoJsonFloat.Format` 的
`WriteRawValue` 输出，`Read` 沿用 `Utf8JsonReader.GetDouble()`（与 STJ 内置 converter 相同的数字
语义，整数与 E/e 指数写法都接受）；System.Text.Json 自动为 `double?` 套用可空包装，字典值与数组
元素经 `GetConverter` 解析到同一 converter。`Embedding.MarshalJSON` 的向量元素与
`ModerationClassification` 的 `category_scores` 手写 writer 同步改为 `GoJsonFloat`；属性级
`GoOptionalNumberConverter`（cost）优先于 options 级 converter，行为不变。非有限值仍由
`GoJsonFloat.Format` 拒绝，与 Go 的写出失败一致。

| 守护对象 | 测试位置 | 用例数 |
|---|---|---|
| 请求标量的 6 种量级（0.000005 / 1e-7 / 1.5e-7 / 1e20 / 1e21 / 0.7）覆盖四个 double 字段 | `tests/AxonHub.Llm.Tests/GoDoubleConverterTests.cs` | 6（Theory） |
| logprob 与 top_logprobs 的正负小数 | 同上 | 3（Theory） |
| 可空字段省略、embedding 向量、category_scores 字典、Go / STJ 指数写法的读取往返 | 同上 | 4 Fact |

§137 落地后的静态计数为 `AxonHub.Api.Tests` 5371、`AxonHub.Persistence.Tests` 331、
`AxonHub.Llm.Tests` 1468（+13）、`AxonHub.Grains.Tests` 1680，四项目合计 8850。**本切片按项目
规则未运行 lint / build / test，上述数字为 `[Fact]` / `[InlineData]` 的静态计数；下一次全量运行
应以 5371 / 331 / 1468 / 8850 复核。**

差异：C067 resolved（所有 double 统一为 Go 规则；cost 等属性级 converter 不变）。

## 138. 通道限流可观测性指标（C044 闭合，5 例，2026-11-08）

参照 `internal/server/orchestrator/channel_limiter_metrics.go` 在进程级 meter `axonhub`
（`internal/metrics/provider.go:27`）上注册五个 instrument，由 orchestrator 构造时创建
（`orchestrator.go:41-46`；创建失败时告警并继续以 nil 指标运行）：

| instrument | 类型 | 单位 | 描述 |
|---|---|---|---|
| `axonhub_channel_inflight` | Int64ObservableGauge | requests | In-flight requests currently holding a channel-limiter slot |
| `axonhub_channel_queue_waiting` | Int64ObservableGauge | requests | Requests currently waiting in the channel-limiter FIFO queue |
| `axonhub_channel_queue_full_total` | Int64Counter | requests | Total requests rejected because the channel queue was full |
| `axonhub_channel_queue_timeout_total` | Int64Counter | requests | Total requests that exited the channel queue via per-channel wait timeout |
| `axonhub_channel_queue_wait_seconds` | Float64Histogram | s | Wait time from channel-limiter Acquire entry to slot grant, on successful acquisitions |

所有 instrument 带 `channel_id`（int）与 `channel_name`（string）标签；manager 非 nil 时用
一个 `RegisterCallback` 遍历 `ChannelLimiterManager.Snapshot()`，同时 Observe 两个 gauge。
`connection_tracking.go:77-100` 在 `lim.Acquire(ctx)` 之前取 `acquireStart`；遇到
`ChannelQueueError` 时按 reason（full / timeout）递增对应 counter 后返回类型化错误；成功
获准后 `ObserveQueueWait(time.Since(acquireStart))`。nil meter 返回空结构体，三个记录方法
对 nil receiver / nil counter / nil channel 均为 no-op。

端口实现：

- `src/AxonHub.Grains.Abstractions/ChannelLimiterMetrics.cs`：`System.Diagnostics.Metrics`
  的 `Meter("axonhub")` 上注册同名、同单位、同描述的五个 instrument；两个
  `CreateObservableGauge<long>` 回调分别从 `Snapshot()` 返回 `Measurement<long>` 列表（参照
  用单个回调同时 Observe 两个 gauge，逐次抓取的观测值一致）；两个 `Counter<long>` 与一个
  `Histogram<double>` 使用 `channel_id` / `channel_name` 标签；null meter 时所有记录方法为
  no-op；`CreateDefault(manager)` 供宿主注册进程级单例。
- `ChannelLimiterMiddleware`（`src/AxonHub.Api/Orchestration/RateLimitMiddlewares.cs`）：
  新增可选 `ChannelLimiterMetrics` 与 `TimeProvider` 参数；`Acquire` 前取
  `GetTimestamp()`，按 `ChannelQueueException.Reason` 递增 full / timeout counter，
  成功获准后以 `GetElapsedTime` 的 `TotalSeconds` 记录等待直方图。
- `ChatCompletionOrchestrator`：新增可选 `ChannelLimiterMetrics` 构造参数并传给中间件；
  `Program.cs` 以单例注册 `ChannelLimiterMetrics.CreateDefault(ChannelLimiterManager)`。
  OTel .NET 按 Meter 名 `axonhub` 收集，与参照的导出面一致。

| 参照测试 | 端口测试 | 例数 |
|---|---|---|
| `TestChannelLimiterMetrics_NilMeterIsNoop` | `NilMeterIsNoop` | 1 |
| `TestChannelLimiterMetrics_GaugeCallbackEmitsLimiterStats` | `GaugeCallbackEmitsLimiterStats` | 1 |
| `TestChannelLimiterMetrics_CountersAndHistogram` | `CountersAndHistogram` | 1 |
| 无（接线守卫） | `QueueFullRejectionIncrementsTheCounter` | 1 |
| 无（接线守卫） | `SuccessfulAcquireRecordsTheWait` | 1 |

静态计数：`AxonHub.Api.Tests` 由 5371 增至 5376（+5），四项目合计由 8850 增至 8855
（`AxonHub.Llm.Tests` 1468、`AxonHub.Persistence.Tests` 331、`AxonHub.Grains.Tests` 1680
不变）。**按项目规则未运行 lint / build / test。**

C044 由 gap 改为 resolved。另登记 C223（gap）：参照的通用 metrics 子系统
（`internal/metrics` provider/config 与 `WithMetrics()` HTTP 中间件）尚未移植，属于本轮
发现、待决策范围的独立缺口；C223 已由 §139 闭合。

## 139. 通用 HTTP 指标与 OTel metrics provider/config（C223 闭合，26 例，2026-11-08）

参照：`internal/metrics/{config,metrics,provider}.go`、`internal/server/middleware/metrics.go`、
`internal/server/routes.go:64`、`conf/conf.go`（metrics 默认值）、`cmd/axonhub/main.go`。

参照行为：

| 项 | 参照值 |
|---|---|
| `metrics.enabled` | 默认 false；关闭时构造 `sdk.NewMeterProvider()`（无 reader） |
| `metrics.exporter.type` | `stdout` / `otlpgrpc` / `otlphttp`；仅在启用时校验，未知值启动失败，文案 `unsupported metrics exporter type: "x"` |
| `metrics.exporter.endpoint` | Go 风格 `host:port`；gRPC 缺省 `localhost:4317`、HTTP 缺省 `localhost:4318` |
| `metrics.exporter.insecure` | true 用明文 HTTP，false 用 TLS |
| 导出周期 | `sdk.NewPeriodicReader(exporter, sdk.WithInterval(5*time.Second))` |
| Meter 名 | `SetupMetrics(provider, server.Config.Name)`；`server.name` 默认 `AxonHub`；通道限流另用全局 `axonhub` Meter（C044），参照的全局 provider 同时收集两者 |

九个 instrument（名称 / 类型 / 单位 / 描述）：

| instrument | 参照类型 | 单位 | 描述 |
|---|---|---|---|
| `http_request_count` | Int64Counter | requests | Number of HTTP requests |
| `http_request_duration_seconds` | Float64Histogram | seconds | HTTP request duration in seconds |
| `graphql_request_count` | Int64Counter | requests | Number of GraphQL requests |
| `graphql_request_duration_seconds` | Float64Histogram | seconds | GraphQL request duration in seconds |
| `chat_request_count` | Int64Counter | requests | Number of chat requests |
| `chat_request_duration_seconds` | Float64Histogram | seconds | Chat request duration in seconds |
| `chat_token_count` | Int64Counter | tokens | Number of tokens in chat requests |
| `chat_success_count` | Int64Counter | requests | Number of successful chat requests |
| `chat_failure_count` | Int64Counter | requests | Number of failed chat requests |

标签：HTTP 指标为 `method` / `path` / `status_code`（整数）；GraphQL 为 `operation`；chat 为
`channel_id` / `channel_name`（均为字符串）。

接线：`WithMetrics()` 位于 `routes.go` 的路由中间件链（第 64 行）。中间件在 `c.Next()`
之前取 `time.Now()`，返回后以 `method`、`c.FullPath()`、`c.Writer.Status()` 与
`time.Since(start).Seconds()` 记录两个 HTTP instrument。`c.FullPath()` 给出 gin 模板
（`/v1/videos/:id`、`/v1/models/*model`），未匹配路由为空串。Recovery 位于 `WithMetrics`
外层，因此 panic 请求在记录前展开，不产生指标。GraphQL / chat 记录函数
（`RecordGraphQLRequest` / `RecordChatRequest`）在参照内没有调用点。

端口实现：

- `src/AxonHub.Api/Metrics/MetricsOptions.cs`：`AXONHUB_METRICS_*` 优先、`Metrics:*` 次之
  （Viper AutomaticEnv 语义）；布尔值按 spf13/cast 接受 `1/t/T/TRUE/true/True`，其余为 false；
  `Validate()` 仅在启用时检查 exporter 类型，未知值抛出与参照一致的文案。
- `src/AxonHub.Api/Metrics/ServerMetrics.cs`：在 `server.name` Meter 上注册九个同名、
  同类型（`Counter<long>` / `Histogram<double>`）、同单位、同描述的 instrument；三个记录方法
  使用与参照相同的标签。`server.name` 缺省 `AxonHub`，环境变量 `AXONHUB_SERVER_NAME` 优先。
- `src/AxonHub.Api/Metrics/MetricsProvider.cs`：用 OpenTelemetry .NET 1.20.1 的
  `Sdk.CreateMeterProviderBuilder()` 同时 `AddMeter(server.name)` 与 `AddMeter("axonhub")`；
  启用时挂 5 秒 `PeriodicExportingMetricReader`（导出超时 30 秒）与 stdout / OTLP gRPC /
  OTLP HTTP 导出器；关闭时构造无 reader provider。
- `src/AxonHub.Api/Http/HttpMetricsMiddleware.cs`：挂在 `RequestTracingMiddleware` 之后；
  ASP.NET 模板按 gin 语法转换（`{p}` / `{p:约束}` → `:p`，`{*p}` / `{**p}` → `*p`），
  `StaticFallbackMetadata` 的 fallback 记空路径；无 try/finally，panic 不记录。
- `Program.cs`：解析配置与 `server.name`，单例注册 `ServerMetrics` 与 `MeterProvider`，并在
  `Build()` 后解析 provider 以启动周期导出，宿主关闭时由 provider 的 dispose 刷新。
- Gemini 两组路由的参数名改为 `{gemini-api-version}`，使 metrics 的 `path` 标签与参照的
  gin 模板逐字一致（handler 不读取该参数，无行为影响）。

已接受子差异（C223 decision）：stdout 导出器输出格式不同（Go 输出 OTLP JSON 行，.NET 为
可读文本）；OTLP endpoint 对空值与已带 scheme 的输入比 Go `WithEndpoint` 更宽松；关闭时的
no-op provider 由不同实现承担，但两者都不导出任何数据；`routes.go` 的中间件顺序在端口由
ASP.NET 管道复现，panic 不记录这一可观察行为一致。

测试（26 例；参照 `internal/metrics` 无测试文件，全部为端口接线/行为守卫）：

| 测试文件 | 例数 | 锁定内容 |
|---|---|---|
| `tests/AxonHub.Api.Tests/ServerMetricsTests.cs` | 3 | 九个 instrument 的名称/类型/单位/描述；HTTP 标签；GraphQL/chat 标签与成功/失败分支 |
| `tests/AxonHub.Api.Tests/MetricsOptionsTests.cs` | 14 | 默认值；`AXONHUB_METRICS_*` 读取；环境变量优先；spf13/cast 布尔语义；三种导出器；未知类型文案；endpoint/insecure/默认端口/已带 scheme 的映射 |
| `tests/AxonHub.Api.Tests/HttpMetricsMiddlewareTests.cs` | 9 | gin path 转换（6 种模板）；fallback 空 path；panic 不记录；状态码与耗时标签 |

静态计数：`AxonHub.Api.Tests` 由 5376 增至 5402（+26），四项目合计由 8855 增至 8881
（`AxonHub.Llm.Tests` 1468、`AxonHub.Persistence.Tests` 331、`AxonHub.Grains.Tests` 1680
不变）。**按项目规则未运行 lint / build / test。**

C223 由 gap 改为 resolved。未移植残余：无。

## 140. index 宽度与 Go 值结构 null 语义（C022 闭合，13 例，2026-11-08）

参照：`llm/model` 的统一模型、`llm/transformer/*` 的 wire 模型与响应转换、
`internal/objects/channel.go` 的 `ParseOverrideOperations`。

参照行为：

| 项 | 参照值 |
|---|---|
| 统一模型 index | Go `int`（64 位）：`Choice.Index`、`ToolCall.Index`、`CompletionChoice.Index`、`EmbeddingData.Index`、`RerankResult.Index` |
| 线上模型 index | 同上：openai choice / tool_call / completion choice / embedding、Jina、Cline、OpenRouter、Nanogpt、Gemini / Ollama 的 index |
| 入站 index 解码 | `{"index":3000000000}` 被接受，`ToolCall.Index` 为 3000000000 |
| 值结构切片 null 元素 | `encoding/json` 解码为结构零值：choices、content parts、logprobs、image data、Jina results/data、Cline choice/reasoning_details、Anthropic citations、Responses output/sources |
| 指针切片 null 元素 | `[]*T` 保持 nil（不补零值） |
| Api override index | `OverrideOperation.Index` 是 `*int`（64 位），`ParseOverrideOperations` 接受 3000000000 |

端口实现：

- 统一模型与线上模型的 index 全部改为 `long`；流聚合字典（OpenAI / Anthropic / Responses /
  Gemini / Bailian）的键同步加宽。
- 响应转换对 Go 值结构切片做 null→零值替换；指针切片（`Candidate`、`Part`、`Embedding`、
  `RerankDocument`、流式 `TextCitation`）不做替换。
- `AxonHub.Api/Management/ChannelOverrideOperations.ReadOperation` 改用 `TryGetInt64`，
  `OverrideOperationDraft.Index` 为 `long?`；GraphQL 输出按 C190 做 checked 截断。

测试（13 例）：

| 测试文件 | 例数 | 锁定内容 |
|---|---|---|
| `tests/AxonHub.Llm.Tests/OpenAiRequestTypeCheckTests.cs` | 1 | `tool_calls[].index=3000000000` 解码为 3000000000 |
| `tests/AxonHub.Llm.Tests/OpenAiResponseNullSemanticsTests.cs` | 5 | choices / content parts / logprobs / embedding data / 流式分块中的 null 元素 |
| `tests/AxonHub.Llm.Tests/OpenAiImageOutboundTests.cs` | 1 | image data null → 零值 `ImageData` |
| `tests/AxonHub.Llm.Tests/JinaRerankTransformerTests.cs` | 2 | rerank results null → 零值；embedding data null → 零值 |
| `tests/AxonHub.Llm.Tests/ClineOutboundTransformerTests.cs` | 1 | choice null → 零值；reasoning_details null → 空串 |
| `tests/AxonHub.Llm.Tests/AnthropicOutboundTransformerTests.cs` | 1 | citation null → 零值类型，不产生注解 |
| `tests/AxonHub.Llm.Tests/ResponsesOutboundTransformerTests.cs` | 1 | output null → 零值 `Item` |
| `tests/AxonHub.Api.Tests/ChannelOverrideOperationsTests.cs` | 1 | override index 3000000000 保留为 long |

静态计数：`AxonHub.Llm.Tests` 由 1468 增至 1480（+12），`AxonHub.Api.Tests` 由 5402 增至
5403（+1），四项目合计由 8881 增至 8894（`AxonHub.Persistence.Tests` 331、
`AxonHub.Grains.Tests` 1680 不变）。**按项目规则未运行 lint / build / test。**

C022 由 gap 改为 resolved。残余：C224（普通 `List<string>` 的 null 元素保留 null）。

## 141. Responses WebSocket 生产接线：trace 会话注入与按尝试 Stop（C072 收窄，12 例，2026-11-08）

参照：`internal/server/biz/channel.go` 的 `stopChannelOutbounds` / `cleanupSwappedChannels`、
`internal/server/biz/channel_llm.go` 的 Responses 构造分支、
`llm/transformer/openai/responses/outbound.go` 的 `Stop`、
`llm/transformer/openai/codex/outbound.go`、`llm/transformer/opencode/headers.go`、
`internal/server/orchestrator/outbound.go` 的重试 / 换通道。

参照行为：

| 项 | 参照值 |
|---|---|
| Stop 调用点 | 启用通道缓存换入 / 换出时，`stopChannelOutbounds` 对旧通道的 outbounds 逐一调用 `Stop`（`biz/channel.go:326-349`）；`cleanupSwappedChannels` 在候选构建后清理被换下的通道 |
| 可停对象 | 仅 responses、codex 与 opencode 的 `sessionHeaderCustomizedOutbound` 实现 `interface{ Stop() }`；其余出站跳过 |
| responses Stop | 关闭并清空 WebSocket 执行器池；各执行器的 `Close` 关闭空闲连接，进行中的连接保留至请求结束 |
| opencode Stop | 转发给被包裹的出站（仅当内层实现 `Stop`） |
| 会话来源 | responses builder 从 ctx 读取 `shared.GetSessionID` / `GetSessionScope` 并写入出站配置 |
| websocket 传输 | 仅 `openai/responses` 格式允许；显式 `endpoint.Transport` 优先，否则由端点 / 通道 base URL 的 ws / wss 推断 |

端口实现：

- `AxonHub.Llm.Transformer.IStoppableOutbound` 复刻上述结构接口；`ResponsesOutboundTransformer`、
  `CodexOutboundTransformer` 与 `OpenCodeSessionHeaderCustomizedOutbound`（仅当内层实现时转发）
  实现它；`Stop` 语义沿用执行器 `Close`（关闭全部池化执行器、清空池、幂等）。
- `ChannelOutboundFactory.CreateResponsesOutbound` 统一构造 8 个 Responses 分支（openai
  responses、openai_responses、xai、xai_responses、nanogpt_responses、bailian_responses、
  zenmux、zenmux_responses），注入 `SessionIdProvider = () => request.RawRequest?.SessionId`
  与 `SessionScopeProvider = () => request.RawRequest?.SessionScope`；opencode_go、
  github_copilot 与 xai_subscription 包装器把两个 provider 透传给内层 Responses 配置。
- `PersistentOutboundTransformer` 在 `TransformRequest`（首次或替换 `_current`）、
  `TryPrepareForRetry`（换模型）与 `TryNextChannel`（换通道）前调用 `StopTransformer`，
  新增公开 `Stop()`；非 `IStoppableOutbound` 的 transformer 跳过，与参照的结构判断一致。
- `ChatCompletionOrchestrator.RunPipelineAsync` 在非流式成功返回、pipeline 抛错与流被 Dispose
  时停止出站；流以 `DisposeCallbackStream` 包装，保证流消费方 Dispose（成功、错误、取消）时
  触发且只触发一次，源 Dispose 抛错也先执行回调。
- 端点记录仍未建模（C094 item 1 / C082）：显式 `endpoint.Transport` 未进入通道配置，生产
  websocket 选择仅由 base URL scheme 推断（`InferTransport`；参照也只允许 `openai/responses`
  格式使用 websocket）；每次尝试构建 transformer 意味着连接池不跨请求复用，参照的通道级池
  （空闲 50m、寿命 50m、上限 128）在端口等价于每请求新池。

测试（12 例）：

| 测试文件 | 例数 | 锁定内容 |
|---|---|---|
| `tests/AxonHub.Api.Tests/ResponsesSessionWiringTests.cs` | 6 | openai / xai / opencode_go / github_copilot / xai_subscription 五条生产链路把 trace 会话写入 `prompt_cache_key` 前缀与 `SessionId` / `SessionScope`；无 trace 时不写 `prompt_cache_key` |
| `tests/AxonHub.Api.Tests/PersistentOutboundRetryTests.cs` | 4 | 换模型停旧 transformer；换通道停旧与中间 transformer；`Stop` 停当前；非可停 transformer 跳过 |
| `tests/AxonHub.Llm.Tests/DisposeCallbackStreamTests.cs` | 2 | 透传并在 Dispose 后回调一次（重复 Dispose 不再触发）；源 Dispose 抛错仍回调 |

静态计数：`AxonHub.Llm.Tests` 由 1480 增至 1482（+2），`AxonHub.Api.Tests` 由 5403 增至
5413（+10），四项目合计由 8894 增至 8906（`AxonHub.Persistence.Tests` 331、
`AxonHub.Grains.Tests` 1680 不变）。**按项目规则未运行 lint / build / test。**

C072 保持 gap 但收窄：会话注入与 Stop 生命周期完成，残余为端点记录传输与通道级连接复用；
C062 的残余接线说明、C094 item (12) 与 C096 item (6) 随之关闭。

## 142. Codex 响应头转发（C094 item 7 关闭，8 例，2026-11-08）

参照：`internal/server/api/chat.go` 的 `writeForwardResponseHeaders` /
`copyCodexTurnStateHeader` / `primeCodexTurnStateHeader`（含 `primedStream`，chat.go:146-259）、
`internal/server/orchestrator/orchestrator.go:360-375`、`llm/httpclient/response_headers.go`。

参照行为：

| 项 | 参照值 |
|---|---|
| 转发门 | `result.CodexResponseHeadersSupported` 为真、请求路径以 `/responses` 结尾、`codex.GetSessionIDFromHeaders(request.Headers)` 非空，三者同时成立才转发 |
| 转发集合 | `MergeForwardResponseHeaders` 只复制 `X-Reasoning-Included`，且源值全部为 `true`（Trim + 大小写不敏感）；不合格时删除目标既有值 |
| 非流头来源 | `result.ChatCompletion.Headers`（pipeline 已把非流式响应头合并为可转发集合） |
| 流头来源 | `httpclient.GetResponseHeaders(result.ChatCompletionStream)`（pipeline 末尾 `WithResponseHeaders` 附加的元数据） |
| 非流 turn state | `copyCodexTurnStateHeader`：`resp.Headers.Get(X-Codex-Turn-State)` 非空则写入响应头 |
| 流 turn state | 仅 `/responses`：`primeCodexTurnStateHeader` 先取第一个事件，把其 `Headers` 中的 `X-Codex-Turn-State` 写入响应头，再以 `primedStream` 回放该事件（SSE 头必须在首 flush 前发送） |
| 支持方 | 只有 codex 出站实现 `SupportsCodexResponseHeaders() == true`；其余出站恒 false |

端口实现：

- `ChatCompletionResult.CodexResponseHeadersSupported` 由 `PersistentOutboundTransformer.SupportsCodexResponseHeaders()`（`_current is ISupportsCodexResponseHeaders`）在 pipeline 成功后填充；非流与流两个返回点都带该标志。
- `ChatCompletionEndpoint.WriteForwardResponseHeaders` / `CopyCodexTurnStateHeader` / `PrimeCodexTurnStateHeader`（嵌套 `PrimedStream`）按参照逐行移植；`MergeForwardResponseHeaders(null, headers)` 只读转发集合，端点没有预先存在的 `X-Reasoning-Included` 可删除。
- 流式响应先经 `DisposeCallbackStream`（§141 的按尝试 Stop 包装），该包装会遮蔽 `IResponseHeadersProvider`；`RunPipelineAsync` 用 `ResponseHeaders.WithResponseHeaders` 把 pipeline 附着的元数据重新挂到包装外层，端点读到的转发集合与参照一致。

测试（8 例）：

| 测试文件 | 例数 | 锁定内容 |
|---|---|---|
| `tests/AxonHub.Api.Tests/CodexResponseHeaderForwardingTests.cs` | 7 | 首事件 `X-Codex-Turn-State` 复制并完整回放两事件；三条路径门（chat / responses 无会话 / codex responses）；不支持出站的拒绝；流式 provider 读取；缺失或空 turn state 不写入 |
| `tests/AxonHub.Api.Tests/ChatCompletionOrchestratorTests.cs` | 1 | pipeline 的 `X-Reasoning-Included` 元数据穿过 `DisposeCallbackStream` 仍可被端点读取 |

静态计数：`AxonHub.Api.Tests` 由 5413 增至 5421（+8），`AxonHub.Llm.Tests` 1482、
`AxonHub.Persistence.Tests` 331、`AxonHub.Grains.Tests` 1680 不变；四项目合计由 8906 增至
8914。**按项目规则未运行 lint / build / test。**

C094 item (7) 关闭；C094 保持 gap，残余为端点表、OAuth 凭据持久化与自动刷新等。

## 143. Responses 端点运行时解析与传输保真（28 例，2026-11-08）

参照：live 树（`f272549f`，端点语义来自基线后的 `94cd05b1`，见 `porting/00-reference.md`）
`internal/server/biz/channel_endpoint_responses.go`（`isResponsesEndpoint` /
`NormalizeEndpoints` / `Channel.DefaultEndpoints` / `legacyResponsesFormat` /
`normalizeModelProtocols`）、`internal/server/biz/channel_endpoint.go:455-487`
（`ResolveEndpoints` = 合并默认端点与归一化用户端点）、`internal/server/biz/channel_llm.go:217-329,431-620`
（`buildChannelWithOutbounds` / `buildNonDefaultEndpointOutbound` 的端点级 BaseURL/Path/Transport）、
`internal/server/orchestrator/tester.go:130-221`（`channelTestAPIFormat` /
`usesResponsesWebSocket`）、`internal/server/gql/axonhub.resolvers.go:34-41`（`defaultEndpoints`
解析器走 `Channel.DefaultEndpoints()`）、`llm/transformer/openai/responses/outbound.go:168-173`、
`llm/transformer/openai/codex/outbound.go:154-159`、`llm/transformer/orchestrator/outbound.go:838-840`。

参照行为：

| 项 | 参照值 |
|---|---|
| `endpointTransport(ep)` | `api_format == openai/responses-ws` → websocket；否则非空 `transport`；否则 BaseURL（TrimSpace + 小写）以 `ws://` / `wss://` 开头 → websocket；否则空 |
| `primaryEndpointTransport(c, h)` | 第一条带该格式的原始用户端点（BaseURL 缺省为通道基址）的 transport；对 h，任一原始 w 端点也使其为 websocket；否则 `{BaseURL: 通道基址}` 的 transport |
| `NormalizeEndpoints` | 只处理 h/w；resolvedBase = 端点 BaseURL 或通道基址；h 的旧式副本带 resolvedBase：transport 为 websocket → APIFormat 改为 w（BaseURL 不变）；否则显式 `transport == http` 时把 wss→https、ws→http（TrimSpace 后按前缀重写）；最后清空 transport；幂等，不修改原始文档 |
| `Channel.DefaultEndpoints` | 非 codex：默认表中每条 h 的 APIFormat 替换为 `legacyResponsesFormat`；codex：允许集来自归一化后的用户 responses 端点，为空时允许 w 恒真、h 仅在通道基址的 transport 不是 websocket 时允许；删除不被允许的 responses 家族默认项（alpha 与两个图片默认项保留） |
| `legacyResponsesFormat` | 第一条原始 h 端点的归一化格式；否则任一原始 w → h；否则以通道基址归一化 h |
| `ResolveEndpoints` | 合并默认端点与归一化用户端点：用户端点覆盖内置位置，其余追加；原始用户文档不被改写 |
| `normalizeModelProtocols` | 旧式意图为 w 时，把 `settings.modelProtocols` 中启用的 h 改写为 w 供选择/校验（存储不改写） |
| 出站分派 | 默认端点走 primary；用户端点走 `buildNonDefaultEndpointOutbound`，按归一化格式覆盖（后者胜出）；codex/fenno 的 h/w 走 codex 出站并带端点 BaseURL/Path/Transport，alpha 搜索把端点 Path 作为 alpha 路径、codex 图片走 codex 图片出站；其余格式按家族构造并接收 `ep.Path`（chat 在 Path 非空或基址带家族版本时走家族 chat 出站）；原始格式别名（原始 h 归一化为 w）仅在键缺失时登记 |
| transformer 标识 | responses 与 codex 出站的 `APIFormat()` 在 transport 为 websocket 时返回 `openai/responses-ws`，否则返回 `openai/responses` |
| `isResponsesFormat` | h、w 与 compact 都算 Responses 家族 |
| tester | `channelTestAPIFormat`：强制过滤（收窄到首个匹配）→ decisions → chat → systemone → 回退 chat；`usesResponsesWebSocket`：首个 w 直接为真，否则 h/compact 看显式 transport，再否则看端点 BaseURL 或通道基址的 scheme |
| `defaultEndpoints`（GraphQL） | `channelResolver.DefaultEndpoints` 返回 `Channel.DefaultEndpoints()`，即已应用存储的 Responses 家族意图的类型默认表；不改写存储 |

端口实现：

- `ChannelEndpoints.Resolve(channelType, endpointsJson, baseUrl)` 按参照合并 `DefaultEndpoints` 与
  `NormalizeEndpoints`；新增 `IsResponsesEndpoint`、`EndpointTransport`、`NormalizeEndpoints`、
  `DefaultEndpoints`、`LegacyResponsesFormat`、`PrimaryEndpointTransport`、`FindUserEndpoint`
  （每个格式最后一次归一化匹配）。解析只读，原始 JSON 不被改写。
- 所有调用点传入通道基址：`ModelService`、`ModelCandidateSelector`（两处）、
  `PersistentOutboundTransformer.RefreshCandidateApiFormat`、`ChannelTestService`（三处）与
  `ChannelEndpointDetectService` / `ChannelTestService.BuildCandidateAsync` 的候选补带
  `Endpoints` / `Settings`（此前 tester 与检测路径看不到已存端点）。
- `ChannelOutboundFactory` 每次尝试按入站格式选取用户端点，把端点 BaseURL / Path / Transport
  交给 codex/fenno 的 responses 与 alpha（alpha 路径只给 codex）、openai 的 h/w/compact、
  decisions、completion、chat、anthropic、xai responses、nanogpt_responses、openai_responses
  （无用户端点时用 `PrimaryEndpointTransport`）、bailian_responses 与 zenmux/zenmux_responses；
  `SelectUserEndpoint` 复刻参照的原始格式别名（原始 h 归一化为 w 且没有原始 w 时，h 查不到则
  回退到归一化 w）。`CreateCodexOutbound` 把端点 Path 按格式分给 alpha 或 responses。
- `ResponsesOutboundTransformer.ApiFormat` 与 `CodexOutboundTransformer.ApiFormat` 在
  websocket transport 下返回 w；`CodexOutboundParams.EndpointPath` 传入内层 Responses 配置。
- `ApiCapabilities.CapableApiFormats(RequestTypes.Chat)` 接纳 w；`ChannelTransformOptions.IsResponsesFormat`
  接纳 w；`ChannelEndpointSelection.ForcedApiFormats` 按 `normalizeModelProtocols` 把启用的 h 改写为 w
  （settings 原文不变）。
- `ChannelTestService.ChannelTestApiFormat` 顺序改为 decisions → chat → systemone → chat 回退；
  `UsesResponsesWebSocket` 先看 w，再对 h/compact 看 transport/scheme，全部经 BaseUrl 解析。
- `ChannelValidation.EndpointTransport`（草稿）把 w 格式视为 websocket，先于显式 transport。
- GraphQL `Channel.defaultEndpoints` 改走 `ChannelMapping.DefaultEndpoints(type, endpointsJson, baseUrl)`，
  与 live 解析器一致；校验路径仍使用类型默认表（写入面冻结语义，C144/C148）。
- 未改动：非 Responses 家族仍按通道类型与通道基址分派（zai/doubao 家族 chat 的端点 Path、
  minimax/gemini/jina/typesafe 的端点路径、xai_subscription 覆盖、fenno 图片门、codex/fenno 的
  compact 端点存在性），通道级连接复用仍缺（C094 item 1 / C072）；写入面端点规范化仍冻结
  （C144/C148）。

测试（28 例）：

| 测试文件 | 例数 | 锁定内容 |
|---|---|---|
| `tests/AxonHub.Llm.Tests/ChannelEndpointsTests.cs` | 19（7 方法） | 13 行 codex 默认端点矩阵（HTTPS/HTTP/wss/ws 基址、显式 h/http/websocket、端点基址、w 格式、混合清单、无关覆盖；原始文档不被改写）；显式 http 的 wss→https 规范化与幂等；w 格式优先于 legacy http 字段；primary transport 的首个原始端点 / 原始 w / 通道 scheme 三种回退；`Resolve` 的默认与覆盖合并位置；chat 能力包含 w；`FindUserEndpoint` 只取每个格式最后一个归一化匹配 |
| `tests/AxonHub.Api.Tests/ResponsesEndpointDispatchTests.cs` | 9 | legacy h+websocket+path 进入 codex 出站（ApiFormat w、端点基址与路径）；alpha 端点路径；ws 基址的 openai_responses 传输；chat 端点基址+路径；forced protocols 的 h→w 改写且 settings 不变；无 ws 意图时保持 h；tester 混合表面优先 chat；`UsesResponsesWebSocket` 读取归一化端点；GraphQL `defaultEndpoints` 跟随 codex 存储意图 |

静态计数：`AxonHub.Llm.Tests` 由 1482 增至 1501（+19），`AxonHub.Api.Tests` 由 5421 增至
5430（+9），四项目合计由 8914 增至 8942（`AxonHub.Persistence.Tests` 331、
`AxonHub.Grains.Tests` 1680 不变）。**按项目规则未运行 lint / build / test。**

C094 item (1) 与 C072 残余 (a) 收窄：运行时端点解析、传输与 Responses 家族的端点级
BaseURL/Path 已按 live 语义接线；剩余为非 Responses 端点/供应商家族与通道级连接复用。
C144/C148 的写入面冻结语义不变。

## 144. 限流状态迁入 ChannelGrain（A1，14 例，2026-11-08）

参照：`internal/server/orchestrator/channel_request_tracker.go`（自然分钟桶、RPM 准入、TPM
记账、extend-only 冷却）、`rate_limit_admission.go`、`rate_limit_tracking.go`、
`lb_strategy_rate_limit.go`（RPM/TPM 子分与并发子分取 min，耗尽 -10000）。

参照行为：

| 项 | 参照值 |
|---|---|
| 桶 | `time.Now().Truncate(time.Minute)` 自然分钟；桶过期按读取时刻判断，旧桶读作 0 |
| RPM 准入 | `limit <= 0` 旁路且不扣槽；否则 `count >= limit` 拒绝，成功才 `count++` |
| TPM 记账 | 非正 token 忽略；否则当前桶累加 |
| 冷却 | 只由带 Retry-After 的上游 429 设置；`SetCooldown` 只延长，过期条目在读取时惰性清理且不覆盖更新的值 |
| 评分 | 冷却/任一硬上限耗尽 → -10000；否则 `min(RPM/TPM 子分, 并发子分)` |

端口实现：

- 状态机仍是 `ChannelRequestTracker`，新增显式 `now` 重载与 `Clear`，供 grain 在一个回合内用
  同一时刻完成读写；单参重载保留给直接构造与内存 recorder。
- `IChannelGrain` 新增 `TryAcquireRequestAsync` / `AddTokensAsync` / `SetCooldownAsync` /
  `ResetRateLimitAsync`；`ChannelGrain` 组合一个按通道分片的 tracker，`GetMetricsAsync` 额外返回
  `RateLimitRequests` / `RateLimitTokens` / `CooldownUntil`（`Id(7..9)`）。
- `RateLimitAwareScorer` 增加快照重载（`ChannelRateLimitState`）；`LoadBalancerScoring.StrategyScore`
  从 metrics 快照取限流切片，`ChannelPoolGrain` 不再依赖 tracker。
- API 侧新增 `IChannelRateLimitRecorder`：生产注册 `GrainChannelRateLimitRecorder`（同步桥接，
  模式与 `GrainChannelMetricsRecorder` 一致），`RateLimitAdmissionMiddleware` /
  `RateLimitTrackingMiddleware` 改为经 recorder 读写；直接构造的测试仍走
  `InMemoryChannelRateLimitRecorder`，由 tracker 自己的时钟裁决。
- 未改动：并发 limiter（`ChannelLimiter` / `ChannelLimiterManager`）仍为每 silo 单例（C042）；
  `ChannelMetricsSnapshot` 的 10 分钟滑窗字段不变。

测试（14 例）：

| 测试文件 | 例数 | 锁定内容 |
|---|---|---|
| `tests/AxonHub.Grains.Tests/ChannelGrainRateLimitTests.cs` | 8 | RPM 准入与超限、非正上限旁路、TPM 累加与非法值、自然分钟重置、冷却只延长与过期、Reset、200 并发只放行 100、pool 从 grain 快照把 9/10 的忙通道排在 1/10 之后 |
| `tests/AxonHub.Grains.Tests/RateLimitAwareScorerTests.cs` | 4（追加） | 快照 RPM/TPM 取 max ratio、快照冷却耗尽、快照 RPM 耗尽、无 tracker 的 scorer 只能走快照重载 |
| `tests/AxonHub.Api.Tests/ChannelRateLimitRecorderTests.cs` | 2 | grain recorder 写穿后 metrics 可见 2 请求/300 token/冷却；内存 recorder 忽略传入时刻并保持 tracker 时钟 |

静态计数：`AxonHub.Grains.Tests` 由 1680 增至 1692（+12），`AxonHub.Api.Tests` 由 5430 增至
5432（+2），四项目合计由 8942 增至 8956（`AxonHub.Llm.Tests` 1501、`AxonHub.Persistence.Tests` 331
不变）。**按项目规则未运行 lint / build / test。**

多 silo 语义登记为 C225：单 silo 与参照逐项等价；多 silo 时同一通道的预算变成集群全局，
而非参照的每进程分片。

## 145. 启用通道缓存迁入 EnabledChannelsGrain（A2，12 + 2 + 1 例，2026-11-08）

参照：`internal/server/biz/channel.go`（NewChannelService 的 `live.NewCache` 构造与初始
`Load(force)`、`reloadEnabledChannels` 的指纹查询 / 启用通道查询 / `buildChannelWithOutbounds`
跳过、`onEnabledChannelsSwap` 的 cacheVersion 递增与资源清理）、`internal/pkg/xcache/live/cache.go`
（worker、500ms 防抖、30s 刷新超时、singleflight / reloadMu、GetData / GetLastUpdate）、
`channel_apikey.go:107-141`（全部 key 禁用时的 10s 同步 `Load(force)` 与随后的 asyncReloadChannels）。

参照行为：

| 项 | 参照值 |
|---|---|
| 指纹 | 最新 `updated_at`（`Order(ent.Desc(FieldUpdatedAt)).First(SkipSoftDelete)`），空表为零值 |
| 跳过规则 | `!force` 且指纹未前进（`!latest.After(lastUpdate)`）→ 不交换、不递增版本；空表 + 零 lastUpdate + 空 current 同样跳过 |
| 强制交换 | `force` → 无论 changed 与否都交换，OnSwap 递增 cacheVersion |
| 刷新失败 | RefreshFunc 返回错误时保留旧数据，doRefresh 记日志 |
| 触发 | 变更后 reloadChannelsAfterCommit / asyncReloadChannels；500ms 防抖合并；API key 不可用同步 Load |

端口实现：

- 新增 `IEnabledChannelSource`（AxonHub.Abstractions）：`GetLatestUpdatedAtAsync` +
  `ListEnabledAsync`；生产实现 `PostgresEnabledChannelSource` 走 `ChannelStore`
  （新增 `GetLatestUpdatedAtAsync` 的 Dapper 查询，无软删谓词，软删行同样推进指纹）。
- 新增 `EnabledChannelsGrain`（string key `enabled-channels`）：持有 `EnabledChannelSnapshot`
  列表、lastUpdate 与 cacheVersion；`GetSnapshotAsync` 首次以 force 懒加载，`RefreshAsync(force)`
  复用同一 reload 逻辑，`RequestRefreshAsync` 以 500ms 防抖注册一次性 grain timer；周期 timer
  每分钟做一次非强制刷新；刷新失败只记日志并保留旧快照；单次刷新 30s 上界。
- 新增 `EnabledChannelSnapshot` / `EnabledChannelsSnapshot`（Grains.Abstractions）：
  `ChannelDescriptor` 的 Orleans 序列化孪生，逐字段双向映射；快照按调用复制。
- API 侧：`GrainEnabledChannelProvider` 替换 `PostgresEnabledChannelProvider`；
  `IEnabledChannelsCacheRefresher`（`RequestRefresh` / `ReloadAsync`）由
  `GrainEnabledChannelsCacheRefresher` 实现；`ChannelLifecycleService` 的全部变更入口在成功后
  请求防抖刷新，API key 不可用路径先同步强制刷新（`WaitAsync(10s)`，失败记 warning）；
  `ChannelModelSyncService` / `ChannelOverrideTemplateService` 的 reload hook 改为
  `GrainChannelModelSyncReloadHook`。
- 测试基建：Grain 测试的 cluster fixture 改为 collection fixture（静态注入的假 provider 不能
  并发建 cluster），两个既有测试类标注 `[Collection]`；假 provider 同时实现
  `IEnabledChannelProvider` 与 `IEnabledChannelSource`。
- 未改动：`ModelCandidateSelector` 的关联解析与 `ModelService` 仍实时读库（C149/C178/C179）；
  `CacheVersion` 尚未被任何消费者使用。

测试（12 + 2 + 1 例）：

| 测试文件 | 例数 | 锁定内容 |
|---|---|---|
| `tests/AxonHub.Grains.Tests/EnabledChannelsGrainTests.cs` | 12（`EnabledChannelsGrainTests` 11 + 同文件 `EnabledChannelSnapshotTests` 1） | 冷加载与版本 1（含读取不重复查询源）、指纹未变跳过、指纹前进重载、仅删除也重载、空库不重载、强制绕过指纹、空库强制仍递增版本、失败保留旧快照、冷加载失败传播且下次重试、防抖合并为一次强制重载、指纹 UTC 归一、快照往返全字段 |
| `tests/AxonHub.Api.Tests/EnabledChannelsGrainProviderTests.cs` | 2 | provider 经 Orleans 映射 grain 快照全字段；`ReloadAsync` 在指纹不变时仍强制交换 |
| `tests/AxonHub.Persistence.Tests/ChannelStoreTests.cs` | 1（追加） | 软删除推进 `GetLatestUpdatedAtAsync` 指纹 |

调用点断言（不新增用例，扩展现有测试）：`AdminChannelApiKeyEndpointTests` 的
`DisableOfTheLastKeyDisablesTheChannel` 断言一次同步重载 + 一次防抖刷新，
`DisableAppendsTheRecordAndKeepsTheChannelEnabled` 断言普通禁用只请求防抖刷新；
`AdminChannelLifecycleEndpointTests` 的 `UpdateChannelStatusClearsTheMarkersAndReturnsTheChannel`
断言变更后请求刷新。

静态计数：`AxonHub.Grains.Tests` 由 1692 增至 1704（+12），`AxonHub.Api.Tests` 由 5432 增至
5434（+2），`AxonHub.Persistence.Tests` 由 331 增至 332（+1），四项目合计由 8956 增至 8971
（`AxonHub.Llm.Tests` 1501 不变）。**按项目规则未运行 lint / build / test。**

差异登记为 C226：集群单激活、懒加载、读者排队、无 OnSwap 资源清理、watcher / 自动禁用事件未移植。

## 146. 按通道 OAuth 令牌刷新迁入 ChannelTokenGrain（A3，5 + 8 + 6 + 4 例，2026-11-09）

参照：`llm/oauth/token_provider.go`（`Get` / `EnsureFresh` / `singleflight` / `OnRefreshed` /
`StartAutoRefresh` / `runAutoRefresh` / `StopAutoRefresh`）、`internal/server/biz/channel_llm.go:1638-1741`
（`refreshOAuthTokenWithClient` 的乐观写与冲突分支、`channelCredentialsEqual` 及 OAuth / Azure / GCP
比较）、`internal/server/biz/channel_internal.go:66-76`（`onTokenRefreshed`）、
`channel_llm.go:331-425 / 924-989 / 867-884`（codex / claudecode / xai_subscription 的 provider
构造与 `setupAutoRefresh`）。

参照行为（按上游 `f272549f`；冻结基线 `b8bb3421` 只有无条件写回，见 00-reference 漂移记录）：

| 项 | 参照值 |
|---|---|
| `Get` | 缓存凭据有效（3 分钟提前过期）→ 直接返回；过期 → singleflight 刷新 |
| `EnsureFresh` | refresh token 为空 → 不刷新；默认提前 5 分钟，窗口内刷新 |
| 单飞 | `singleflight.Do("refresh")`，同一 provider 的并发刷新合并为一次 |
| 写回 | `UpdateOneID(id).Where(UpdatedAtEQ(快照时间)).SetCredentials(...)`；NotFound 时重载当前行 |
| 重试 | 当前行类型相同且凭据相等（不比较 `vertexApiKeys`）→ 用新 `updated_at` 重试同一 payload |
| 冲突 | 胜利者同类型且 OAuth → `CredentialConflictError{winner}`；否则空冲突（provider 退休） |
| 自动刷新 | `delay = ExpiresAt - refreshBefore`；失败后 1 分钟重试；`StopAutoRefresh` 取消 |

端口实现：

- 新增 `ChannelOAuthToken` / `ChannelTokenSeed` / `ChannelTokenRefreshRequest` /
  `ChannelTokenRefreshResult` / `IChannelTokenSource` / `IChannelTokenGrain`
  （Grains.Abstractions）：Orleans 序列化孪生；seed 携带凭据、原始 credentials 文档与行 `updated_at`。
- 新增 `ChannelTokenGrain`（integer key = channel id）：`GetAccessTokenAsync` 采纳较新 seed 后复刻
  `Get`；`EnsureFreshAsync` 复刻 `EnsureFresh(0)`；`OnActivateAsync` 注册 1 分钟周期 timer，tick 内做
  窗口检查并在失败时记 warning（保留快照）；grain turn 串行化替代 singleflight；非 OAuth 胜利者令
  grain 退休（后续读取抛 `credentials is nil`）。
- 新增 `ChannelTokenSource`（Api）：按通道类型构造一次性 `TokenProvider`（codex / claudecode /
  xai_subscription），`OnRefreshed` 复刻 `refreshOAuthTokenWithClient` 的乐观重试循环；
  `ChannelCredentialsComparison` 复刻 `channelCredentialsEqual`（`ExpiresAt` 按时刻比较，scopes
  nil==empty，跳过 `vertexApiKeys`）；非冲突持久化失败记 warning 并保留刷新后的内存凭据。
- 新增 `ChannelTokenStore`（Persistence）：Dapper 读 + EF `ExecuteUpdateAsync` 条件写；0 行重读当前行；
  软删行不可见 / 不可写（`ent: channel not found`）。
- 接线：`ChannelOutboundFactory.Create` 新增可选 `IGrainFactory`，`ChatCompletionOrchestrator` 传入；
  codex（fenno 除外）/ claudecode / xai_subscription 的 OAuth 通道用 `GrainTokenGetter`（同步
  `GetAwaiter().GetResult()`），API key 通道不变；直接调用者（端点探测 / 通道测试）不传 grain 工厂，
  保留本地 provider。
- 未迁移：fenno（令牌配方不同）、github_copilot、antigravity（`refreshToken|projectID`）、
  ModelFetcher 目录刷新、端点探测 / 通道测试（C094 / C158）。

测试（5 + 8 + 6 + 4 例）：

| 测试文件 | 例数 | 锁定内容 |
|---|---|---|
| `tests/AxonHub.Llm.Tests/OAuthTokenProviderConflictTests.cs` | 5 | 冲突胜利者采纳、过期胜利者二次刷新、空胜利者退休、非冲突失败保留凭据、无 refresh token 的胜利者不重试 |
| `tests/AxonHub.Grains.Tests/ChannelTokenGrainTests.cs` | 8 | 冷种子不刷新、过期刷新并采纳、较新快照覆盖 / 旧快照忽略、退休后抛错直到更新快照、无 refresh token 不刷新、窗口外不刷新、窗口内刷新、失败保留快照 |
| `tests/AxonHub.Api.Tests/ChannelTokenSourceTests.cs` | 6 | 轮换凭据写回并保留无关字段、仅元数据变更重试、并发凭据变更返回胜利者、非 OAuth 行退休、持久化失败保留内存凭据、无 refresh token 不刷新 |
| `tests/AxonHub.Persistence.Tests/ChannelTokenStoreTests.cs` | 4 | 读取、时间戳匹配写入、陈旧时间戳重读、软删不可见 / 不可写 |

静态计数：`AxonHub.Llm.Tests` 由 1501 增至 1506（+5），`AxonHub.Grains.Tests` 由 1704 增至 1712（+8），
`AxonHub.Api.Tests` 由 5434 增至 5440（+6），`AxonHub.Persistence.Tests` 由 332 增至 336（+4），
四项目合计由 8971 增至 8994。**按项目规则未运行 lint / build / test。**

差异登记为 C227：懒激活、1 分钟 timer 粒度、不可取消的 grain 刷新、EF 条件写与日志文案；
C094 的 (2)(3)(4) 对 codex / claudecode / xai_subscription 关闭，C127 / C158 / C226 补充说明；
写回语义局部跟进上游 `f272549f`（00-reference 漂移记录）。

## 覆盖缺口

| 未覆盖部分 | 原因 | 风险 | 计划 |
|---|---|---|---|
| pipeline 重试、空响应检测、SSE 事件上限 | 已由 24 个端口测试锁定 | — | 完成（M2） |
| TypeSafe SystemOne（`/v1/systemone`、`/typesafe/v1/systemone`、`typesafe` 通道） | 已由第 24 节的 65 例锁定（统一模型与 Answer Extra、双向变换、错误信封、两条路由、空响应探测）；通道能力表与字段级解码文案为 C087 登记的裁剪，pass-through body 的请求与提示词保护半程已由第 26、28 节闭合 | 低 | 完成（C087、C091、C092） |
| pass-through 提示词保护 | 请求半程由第 26 节的 95 例锁定，响应/流半程（原始响应替换、双通道扇出、1024 事件/8 MiB 回放缓冲、取消后排干、上游错误传播）由第 27 节的 26 例锁定，提示词保护的统一应用、回放校验与原始 body 补丁由第 28 节的 71 例锁定；规则管理面/预览未移植（C092），codex 出站的 `AllowPassThroughBody` 策略由第 29 节的 61 例 codex 用例锁定（C091 缺口关闭，见 C094） | 中 | 管理面随 C013/C014 |
| Codex 出站与 OAuth 通道生命周期 | 出站变换、凭据解析、刷新单飞与通道接线由第 29 节的 61 例 codex + 50 例 OAuth + 20 例接线 + 4 例 `default_test_model` 锁定；端点运行时解析已由 §143 按 live 语义接线（`NormalizeEndpoints` / `DefaultEndpoints` / `PrimaryEndpointTransport` / `FindUserEndpoint`；codex/fenno 的 responses 与 alpha 取端点 BaseURL/Path/Transport，openai 的 h/w/compact 等家族同样取端点值；C094 item 1 收窄），残余为非 Responses 端点的家族路由与供应商路径（fenno 图片门、codex/fenno 的 compact 端点存在性、zai/doubao 家族 chat 的 Path、minimax/gemini/jina/typesafe 的路径、xai_subscription 覆盖）、OAuth `TokenProvider` 已由 §146 的 `ChannelTokenGrain` 按通道持有（刷新写回、后台预刷新、冲突接管；C094 item 2/3/4 对 codex / claudecode / xai_subscription 关闭，fenno / github_copilot / antigravity / ModelFetcher / 端点探测仍每次新建）、无按通道代理 client、codex 响应头转发已由 §142 接线（C094 item 7 关闭）、`DecodeAuthJson` 的消费方（模型拉取管理面）未移植（C094） | 中 | 随端点记录/管理面切片（C013/C014、C072） |
| 首包超时、非流式超时 | 实现已对等（`FirstEventTimeoutGuard` / `NonStreamTimeoutScope`），参照侧无独立单测可移植；端口在 orchestrator 层补了 2 例集成测试（超时 → 跳过同通道重试 → 切通道，取消不计失败、首包超时计失败） | — | 完成（M3） |
| 数据库接线的端到端启动 | 已用真实宿主冒烟与应用镜像冒烟（第 9 节）：Kestrel + Orleans silo + `postgres:17-alpine` 启动、幂等 DDL 建表、`/health` 200、422/502 失败链路与请求/执行落库均通过；多阶段 Dockerfile 构建的镜像在同一容器网络内复现同一结果 | 低 | 完成（M4） |
| 非 chat 响应类型的空响应探测（video） | 已由第 16.1、17、18、19、20 节先后闭合图片、embedding / moderation、decisions / alpha-search、speech / transcription 与 rerank 分支；video Llm 分支（统一模型与变换器）由第 21 节闭合（57 例，C081）；video 端点接线（`POST /v1/videos`、`GET/DELETE /v1/videos/:id`、`VideoService` 与持久化接缝）由第 22 节闭合（36 例，C082–C085），非 chat 响应类型已全部闭合 | — | 完成 |
| Responses API 非流式以外部分 | Anthropic Messages 双向链路已实现：入站（非流式与流式）由第 10 节的 155 + 7 例锁定，出站（含 SSE 与 cache_control 优化器）由第 11 节的 67 例锁定；Responses 非流式双向变换（含 compact）由第 12 节的 158 例锁定，图片请求/响应路径与图片流式聚合由第 14 节的 26 例锁定（C059 闭合），流式状态机与聚合由第 13 节的 86 例锁定（C063 闭合），WebSocket 传输与 HTTP 包装由第 15 节的 45 例锁定（C061 闭合；生产接线见 C072），端点运行时解析与传输保真由 §143 的 28 例锁定（C094 item 1 / C072 残余收窄），客户端 OpenAI Images 入站/出站变换器由第 16 节的 71 例锁定（C069 闭合），`/v1/images/*` 路由接线由第 16.1 节的 8 例锁定（C075 闭合，参照本身停用 variations 路由），OpenAI Embeddings / Moderations 客户端变换器与两条路由由第 17 节的 71 例锁定（C076 闭合），OpenAI Alpha Search / Decisions 客户端变换器与两条路由由第 18 节的 137 例锁定（C077 闭合），OpenAI Audio 客户端变换器与三条 `/v1/audio/*` 路由由第 19 节的 102 例锁定（C079 登记，C010 的 speech / transcription 分支闭合），Jina Rerank / Embedding 客户端变换器与 `/v1/rerank`、`/jina/v1/rerank`、`/jina/v1/embeddings` 三条路由由第 20 节的 88 例锁定（C080 登记，C010 的 rerank 分支闭合；非 chat 响应类型仅剩 video），OpenAI Video 客户端变换器 Llm 层由第 21 节的 57 例锁定（C081 登记，C010 的 video Llm 分支闭合），OpenAI Video 端点接线（三条路由、biz VideoService 的任务查询/删除流程与持久化接缝）由第 22 节的 36 例锁定（C082–C085 登记，C010 的 video 端点接线闭合；doubao 视频变换器由第 43 节闭合、ZenMux 视频变换器与通道接线由第 44 节闭合），OpenAI 旧版 Completions 客户端变换器与 `POST /v1/completions` 路由由第 23 节的 69 例锁定（C086 登记） | 中 | 后续阶段 |
| GraphQL / 前端 | 前端已原样复用（§47：829 文件、逐字节一致、`frontend/src/gql/` 即服务端契约）；服务端 GraphQL 由 HotChocolate 16 接入 `/admin/graphql`（§48），`allScopes`、system-settings 的 4 查询 / 3 变更（§63）与 users / roles 的 2 查询 + 7 变更（§64：gid / Cursor / Time 标量、entgql 分页、嵌套角色、密码哈希）已闭合；channels 的读取（§65：channels / queryChannels / 四个聚合查询）、生命周期写入（§66：状态 / 删除 / 批量状态 / 批量删除 / 批量标签共 10 个变更）与 createChannel（§67：写入校验、ent 默认值与凭据序列化）与 updateChannel（§68：部分列更新、乐观守卫与 live 校验器）、事务内自动补价（§74：create / update 均按 supportedModels 补齐 channel_model_prices）、批量排序 / 批量自动禁用（§69）与端点保存（§70：类型与协议校验、updated_at 守卫）、API key 记账（§71：禁用 / 启用 / 启用全部 / 启用选定 / 删除禁用 key 五个变更）、端点自动检测（§72：detectChannelEndpoints）与定价写入（§73：saveChannelModelPrices）已接入，重复 / 批量创建 / 批量导入由 §75 接入，通道测试（testChannel / testChannelAPIKeys / testChannelAPIKey）由 §76 接入，模型同步（syncChannelModels / ModelFetcher）由 §77 接入，通道覆盖模板（channelOverrideTemplates 查询与五个变更）由 §78 接入，自助身份（me / myProjects / updateMe / updateMyPassword / unlinkOIDCIdentity）由 §79 接入，管理面登录端点（`POST /admin/auth/signin`、`POST /admin/auth/refresh`）与 JWT 会话由 §80 接入，管理员 GraphQL 鉴权（WithAdminGraphqlAuth / WithProjectID / service_account 只读）由 §81 接入（27 例），但请求超时与其余 SDL 成员（user / role 单实体查询、pricing、backups、dashboard、系统设置类查询等）未移植（traces / threads 已由 §101 闭合，requests 根查询与 Request 详情 / executions / usageLogs 边由 §102 接入，usageLogs 根查询与 node(UsageLog) 由 §103 接入，analytics 的四个根字段由 §104 接入，dashboard 的 21 个根字段由 §105 接入，数据存储管理面（dataStorages / node(DataStorage) / createDataStorage / updateDataStorage）由 §106 接入（14 例），备份与自动备份管理面（autoBackupSettings / backup / restore / updateAutoBackupSettings / triggerAutoBackup）由 §107 接入（57 + 24 例）），API key profile template 管理面（apiKeyProfileTemplates 根字段、四个变更与 linkedProfilesCount）由 §109 接入（21 例），邀请的 REST 面（`POST /admin/invitations`、`GET /auth/invitations/:token`、`POST /auth/invitations/:token/register`，含角色删除同事务撤销）由 §110 接入（59 + 14 例；邀请 schema 标注 entgql.SkipAll，SDL 无邀请成员，故无 GraphQL 面）；前端实际调用的 215 个根字段中 145 个已接入（约 67%）；前端 project-users 用到的 `node` 查询（Project / User 的 projectUsers 边）与 `addUserToProject` / `removeUserFromProject` / `updateProjectUser` 三个变更由 §85 接入（32 例；node() 其余 7 个 ent 类型（ChannelProbe / ChannelOverrideTemplate / RequestExecution / Role / System / UserProject / UserRole）见 C165，node(Channel) 与价格读取边由 §136 接入）；静态托管与 SPA fallback 已接入（§62：NoRoute 三分支、JSON 404 信封、资产 404 与 SPA no-cache 头，C116 已解决，磁盘根差异登记为 C129）；OIDC 提供方配置、启动发现、图标内联与 `GET /oauth/oidc/providers` 由 §82 接入（53 例），登录流程（authorize / callback / exchange / link）由 §83 接入（143 例，C014 的 OIDC 部分闭合），权限校验器与 users / roles 写入校验由 §84 接入（25 例），项目成员查询与写入由 §85 接入（32 例），模型管理（models / queryModels / queryModelChannelConnections / queryUnassociatedChannels / fetchModels 与八个变更）由 §88 接入（32 例） | 中 | 以现有前端的查询/变更为契约，按实体切片 |
| 通道管理面：写入校验与覆盖模板 CRUD | 参照的 `ValidateBodyOverrideOperations` / `ValidateOverrideHeaders` / `ValidateRateLimit` 在写入时拒绝非法操作与限流参数，`channel_override_template_test.go` 的 6 例锁定模板 CRUD；端口已有读取/执行路径、§66 的生命周期写入与 §67 的 createChannel 写入校验（body / header 操作、限流与其余设置项）；updateChannel 由 §68 接入（非法配置在写入时拒绝），通道测试由 §76 接入，模型同步由 §77 接入，覆盖模板的 `channel_override_templates` 表、`channelOverrideTemplates` 查询与 create/update/delete/apply/clear 五个变更由 §78 接入（隐私作用域 C161、where 子集 C162、通道顺序 C163），其余通道写操作（C037）未移植 | 中 | 随通道管理面落地（C014） |
| 负载均衡：adaptive 策略链、WeightRoundRobin、rate-limit、error-aware / additive scoring | 已由 §7 的 24 例与 §7.1 的 18 例锁定（C012 已解决；RateLimitAware 已接入真实限流状态，C035 已解决）；残余 C034（无延迟样本）与 C036（无模型级熔断状态） | 中 | 随延迟指标与模型级熔断落地 |
| 持久化（channels / projects / users / api_keys / channel_model_prices / models / channel_override_templates / oidc_identities / requests / request_executions / usage_logs / systems / traces / threads / data_storages / api_key_profile_templates） | 已实现 EF Core 写 + Dapper 读 + PostgreSQL 层并由 56 例锁定，覆盖模板存储另由 §78 的 7 例锁定，`models` 管理存储由 §88 的 7 例锁定，`data_storages` 管理存储由 §106 的 8 例锁定，`api_key_profile_templates` 存储由 §109 的 4 例锁定，`invitations` 存储与注册事务由 §110 的 14 例锁定，`oidc_identities` 与自助存储由 §79 的 6 例锁定；生产宿主经 `EnabledChannelsGrain`（`PostgresEnabledChannelSource`）读取启用通道快照，请求/执行/用量/成本经 `RequestChainPersistenceMiddleware` 落库并由 5 例锁定；`traces` 表与 `GetOrCreateTrace` 由第 30 节的 6 例锁定，`threads` 表与 `GetOrCreateThread` 由第 31 节的 5 例锁定。流式归档（external id / body）、终态分类与响应头回填已按参照补齐（C019）；`response_chunks` 的请求链路归档与存储策略的运行时消费未接入（C020）；存储策略的读写、body 清理与 GC 由 §113 接入，响应头捕获点差异已登记（C021） | 低 | 容器化验收；C020 随请求链路存储策略落地 |
| usage 成本管理面：`channel_model_prices` 自动定价与读取边 | 写入面已由 §73 接入（`saveChannelModelPrices`、create/update/skip/delete 计划、reference id 轮换、`channel_model_price_versions` 版本行与 GraphQL 定价类型，68 例）；自动定价由 §74 接入（`models` 表、模型卡换算、createChannel / updateChannel 事务内补价，20 例）；ent 的读取边由 §136 接入（`node(Channel)`、`channelModelPrices` / `versions` / `channel` / `channelModelPrice` 四条边与 read_channels 查询隐私，Node 接口与版本状态枚举，10 例），进程内价格缓存刷新不可观察（C048 resolved） | — | 完成（C048） |
| API key 鉴权（/v1 中间件、header 提取、ClientIP/allowlist、no-auth 懒建）与 IP 黑名单 | 已按参照实现并由 73 例鉴权 + 29 例黑名单/系统设置锁定（另有 6 例 netip 前缀边界计入鉴权的 73 例）；管理面（GraphQL API key 创建）未接入，角色 / 权限校验由 §84 接入（PermissionValidator 与 users / roles 写入接线，25 例），OIDC 登录流程由 §83 接入（提供方配置与 providers 列表由 §82 接入），JWT 登录 / 刷新由 §80 接入，管理员 GraphQL 的 bearer / service_account 门与只读规则由 §81 接入 | 中 | 管理面随 GraphQL/前端里程碑 |
| 其余 Ent 表 | 请求链路四表、鉴权三表（users / api_keys / projects）、`roles` / `user_roles` / `user_projects`（§64）、`systems`、`traces`（第 30 节）、`threads`（第 31 节）、`models`（§74 供自动定价读取，§88 管理面 CRUD）、`channel_override_templates`（§78）、`oidc_identities`（§79）、`data_storages`（§106）、`prompts`（§99）与 `invitations`（§110）已落；其余表未移植 | 中 | 随对应功能落地 |
| trace 子系统：查询 / GC、GraphQL | trace id 解析、响应别名、`traces` 幂等落库、唯一冲突重读、非消息端点与 fail-open 由第 30 节的 38 + 6 例锁定，thread 中间件与 `traces.thread_id` 由第 31 节的 7 + 5 例锁定，trace 粘性 API key 由第 32 节的 7 + 1 例锁定；claudecode / DeepSeek / OpenCode 出站生成由第 33 节的 154 例锁定（C096）；查询与 GraphQL（列表 / node / 段 / 用量 / 八个状态变更）由 §101 闭合（23 + 15 例），GC / 保留策略由 §113 闭合（C095 resolved）；仅剩日志接缝与 gjson 归一化等已接受差异（C095 / C203） | — | 完成（C095 / C203） |
| 其余供应商通道类型（Gemini 原生 / Vertex、ZenMux 系列、Antigravity、Commandcode、OpenaiResponses 等） | 参照 `channel_llm.go` 的类型分支为上述类型装配 provider 专用出站；端口已移植 openai、anthropic、codex、fenno、claudecode、opencode_go(_anthropic)、jina、typesafe、deepseek、fireworks、moonshot、longcat、cline、xai、ollama(_anthropic)、openrouter、cerebras、nanogpt(_responses)、bailian 四类、modelscope、doubao/volcengine 与 doubao_anthropic/volcengine_anthropic、11 个 OpenAI 兼容别名（atlascloud、vercel、deepinfra、qiniu、ppio、siliconflow、aihubmix、burncloud、github、evolink、groq）、7 个 anthropic 变体（qiniu/aihubmix/evolink/longcat/deepseek/moonshot _anthropic 与 anthropic_aws）与 xai_responses；`ChannelOutboundFactory` 遇到其余类型会报 unsupported channel type。Ollama 两种类型由第 34 节闭合（C097），DeepSeek 通道接线由第 35 节闭合，Fireworks 与 Moonshot 由第 36 节闭合（C098），Longcat 由第 37 节闭合（C099），xAI 与 Cline 由第 38 节闭合（C100），OpenRouter、Cerebras 与 nanoGPT（含 `nanogpt_responses`）由第 39 节闭合（C101），Zai / Zhipu / Zai-Anthropic / Zhipu-Anthropic / Xiaomi / Xiaomi-Anthropic / MiniMax / MiniMax-Anthropic（含两家的图片出站）由第 40 节闭合（C102），Bailian 四类（`bailian` / `bailian_responses` / `bailian_anthropic` / `moonshot_coding`）由第 41 节闭合（C103），Modelscope（chat 与异步图片任务）由第 42 节闭合（C104），Doubao / Volcengine / Doubao-Anthropic / Volcengine-Anthropic（chat、图片、多模态 embedding、Seedance 任务与 `/doubao/v3` 客户端面）由第 43 节闭合（C105），ZenMux 系列（`zenmux` / `zenmux_responses` / `zenmux_anthropic` / `zenmux_video` 的原生视频任务、chat 委托与本地取消）由第 44 节闭合（C106），GitHub Copilot（chat / Responses 双路径、令牌交换与 OAuth 通道接线）由第 45 节闭合（C107），`gemini_openai`（OpenAI 兼容端点、extra_body thinking 配置、Google 原生工具过滤与 thought signature 透传）由第 46 节闭合（C108），Gemini 原生基础（全模型、共享转换、MIME 推断、thought signature、usage 双向与配置清理）由第 49 节先行锁定（C110），Gemini 原生出站转换（outbound_convert.go 全量）由第 50 节锁定（C111），出站变换器与 embedding/image（outbound.go/embedding.go/image.go/vertex/constants.go）由第 51 节锁定（C112），出站流式与聚合（outbound_stream.go/aggregator.go 及其测试）与 gemini/zenmux_gemini/gemini_vertex 通道接线由第 52 节锁定（C113），Gemini 原生入站转换（inbound_convert.go 全量）由第 53 节锁定（C114），入站变换器与流式（inbound.go/inbound_stream.go）及 `/v1beta`、`/gemini/:version` 原生端点接线由第 54 节锁定（C115–C117），Antigravity Llm 层（router/executor/health/version/sanitizer/envelope/transformer 全包与 129 例测试）由第 55 节锁定（C118–C120），Antigravity 通道接线由第 56 节锁定（C121–C123），其 OAuth 管理面（start / exchange / 项目发现）由第 111 节闭合（C121 resolved，残余见 C202），OpenaiResponses 通道接线由第 57 节闭合，Commandcode 通道接线由第 58 节闭合（C124），别名通道类型（11 个 OpenAI 兼容别名、8 个 anthropic 变体、xai_responses）由第 59 节闭合（C125/C126 登记 anthropic_gcp 与两个 fake 通道），xai_subscription 由第 60 节闭合（C127） | 中 | 按 provider 分组后续切片（剩余：Antigravity 模型拉取（配额采集已由 §115 闭合）、xai_subscription 的自动刷新/持久化回写与管理面宿主（C127 残余）；anthropic_gcp 与两个 fake 通道随 C056/C126 排期） |
| 模型列表的管理面来源 | 已由 §89 闭合：ListEnabledModels 的 configured-model 合并、QueryAllChannelModels 两分支、项目 / API key profile（ChannelIDs / ChannelTags / ModelIDs）过滤、ModelBlacklistRegex、HideUnroutableModelsInList 抑制、include 扩展字段与 OpenAI / Anthropic / Gemini 三种 403 信封（45 例）；残余运行时结构差异登记为 C178 | — | 完成（C088 resolved / C178） |
| 模型关联路由与多模型重试 | 已由 §90 闭合：DefaultSelector 的模型行查询 / privacy / 关联匹配（六种 kind 与排除规则）/ When 条件 / 聚合 / 逐模型 API 格式、模型级 routing policy、模型卡 DefaultMaxTokens、无模型行回退通道、多模型同通道重试与 tester 路径排序（59 例）；残余差异与真实缺口登记为 C179，通道变换选项与流策略强制由 §96 闭合（C180） | 中 | 完成（C179 / C180） |
| trace / thread 通道粘性路由 | 已由 §91 闭合：prefer_previous_channel 的生效策略优先级、trace → thread 作用域解析、命中仅限候选池、priority 最小候选置首与同通道 fallback 去重、只记账粘性候选、写回条件与 trace 行 thread 优先、30 分钟 TTL（15 + 1 例）；进程内缓存的多实例限制登记为 C181 | — | 完成（C181） |
| 项目 / API key profile 的候选过滤 | 已由 §92 闭合：项目 profile（仅带 key 时）与 key 激活 profile 的 ChannelIDs / ChannelTags（any / all / none）交集过滤、空过滤器 no-op、模型关联与通道回退两条来源、tester 指定候选、过滤为空时的 invalid-model 文案（12 例）；C179 的真实缺口 (b) 随之关闭 | — | 完成（C179） |
| Google / Anthropic 原生工具候选过滤 | 已由 §93 闭合：格式门（Gemini contents / Anthropic messages）、原生工具触发条件、兼容通道集合、无兼容时回退全量、普通与 tester 两条路径（3 + 10 例）；C179 的真实缺口 (c) 随之关闭 | — | 完成（C179） |
| 流策略候选过滤 | 已由 §94 闭合：流式 forbid 过滤、非流式 require 优先级、自动聚合回退、AI SDK / embedding / compact 空结果、Decisions 冲突文案、通道策略流入候选（19 例）；C179 的真实缺口 (d) 随之关闭 | — | 完成（C179） |
| 配额路由与供应商配额采集 | 已由 §95 闭合候选选择侧的配额闸门：EvaluateQuotaRouting 决策表、两阶段过滤与 dropped count、通道级 quotaRoutingMode 覆盖与 fail-closed、请求 modality、系统设置与 legacy 映射、QuotaExhausted 的非流 503 / 流式 envelope / 二进制 503（43 例）；启动数据迁移由 §112 闭合（claim / legacy 映射 / allowedChannelIDs 改写 / 标记，19 + 7 例，C183 resolved）；采集核心（ProviderQuotaService / provider_quota_status / 设置 / 调度 / GraphQL 配额面）由 §114 落地（111 例），Antigravity checker 由 §115 落地（61 例），Claude Code checker 由 §116 落地（80 例），Codex checker 由 §117 落地（83 例，含 IQuotaResetter），xAI Subscription checker 由 §118 落地（46 例），GitHub Copilot checker 由 §119 落地（75 例），NanoGPT checker 由 §120 落地（105 例），ZenMux checker 由 §121 落地（152 例），Cline checker 由 §122 落地（100 例），Synthetic checker 由 §123 落地（209 例），NeuralWatt checker 由 §124 落地（127 例），Apertis checker 由 §125 落地（187 例），OpenCode Go checker 由 §126 落地（122 例），Kimi Code checker 由 §127 落地（218 例），MiniMax checker 由 §128 落地（161 例），ZhiPu checker 由 §129 落地（144 例），Zai checker 由 §130 落地（28 例），Charm Hyper checker 由 §131 落地（67 例），Command Code checker 由 §132 落地（116 例），Ollama checker 由 §133 落地（123 例；全部 19 个 provider checker 已注册，C182 resolved）；未注册 provider 的通道保留既有行、antigravity / claudecode / codex / xai_subscription / github_copilot / nanogpt / zenmux / cline / synthetic / neuralwatt / apertis / opencode_go / kimi_code / minimax / zhipu / zai / charm_hyper / commandcode / ollama 通道发起采集 | 中 | 闸门、迁移与全部 checker 完成（C182） |
| 通道变换选项与流策略强制 | 已由 §96 闭合：ForceArrayInstructions / ForceArrayInputs / ReplaceDeveloperRoleWithSystem、ReasoningEffortMapping 与 Anthropic output_config.effort 同步、Responses custom tool 消息过滤、Anthropic 非流式显式 `stream:false`、require-streaming 候选对可聚合请求的强制流式与 state 同步（43 + 9 例）；C180 随之闭合 | — | 完成（C180） |
| API key profile 模型映射与响应模型还原 | 已由 §97 闭合：active profile ModelMappings 的首个匹配与 XRegexp 语义、RequestModel / OriginalModel 共享状态与二次调用恢复、空模型拒绝、响应与流模型还原、选择前的 pipeline 接线与 tester 路径（8 例）；C184 随之闭合；其余入站中间件（enforceQuota / applyAutoReasoningEffort / checkApiKeyModelAccess / Claude Code 计费消息）登记为 C185 | 中 | 映射完成；其余中间件随 C185 切片 |
| orchestrator 其余入站中间件（配额 / auto reasoning / model access / 计费消息） | 已由 §98 闭合：三类配额窗口与 usage_logs 聚合、403 `quota_exceeded_error` 信封、模型后缀拆分与 Qwen 豁免、active profile ModelIDs 精确匹配、Claude Code 计费消息按 claudecode + OAuth 保留（15 + 28 + 5 例，另 Persistence +4）；C185 随之闭合，时区读取差异登记为 C186 | — | 完成（C185 / C186） |
| 项目提示词注入与 decisions 提示词拼接 | 已由 §99 闭合运行时链路：`prompts` 表（EF 写 / Dapper 读、软删与唯一索引）、设置 JSON 解码、条件匹配与 XRegexp 语义、稳定排序与前插 / 追加、ProtectPrompts 之前的 pipeline 接线、decisions 字符串与数组 input 的逐字节改写、项目门控与 fail-open（63 + 6 例）；每请求读取与参照每分钟缓存的差异登记为 C187（accepted）；管理面（GraphQL CRUD / 设置校验）由 §100 闭合，残余差异登记为 C188（accepted） | 中 | 完成（C187 / C188） |
| 项目提示词管理面（GraphQL CRUD / 设置校验） | 已由 §100 闭合：Prompt 节点 / prompts 连接（过滤、排序、键集分页）、七个变更、ValidatePromptSettings、DUPLICATE_NAME、隐私策略（含无 API-key 规则）、软删与 aggregate 更新语义（37 + 7 例）；残余差异（where 子集、必填输入严格度、未知枚举输出、order 列宽、无缓存失效、Project.prompts 反向边）登记为 C188（accepted） | — | 完成（C188） |
| 追踪与线程管理面（GraphQL） | 已由 §101 闭合：traces / threads 根查询、`node()` 的 Trace / Thread 分支、Trace / Thread 完整类型与边、八个 archive / unarchive / retain / unretain 变更、TracePrivacy 查询与变更策略、段构建（OpenAI / Anthropic / Gemini / 图片 / moderation / alpha search）、用量聚合与 thread 级联状态变更（23 + 15 例）；残余差异（隐私显式求值、Int 截断与零值 GID、顺序加载 / 无对象存储）登记为 C189–C191（accepted），GC / 保留策略仍见 C095 | — | 完成（C189–C191） |
| 请求管理面（GraphQL） | 已由 §102 闭合：requests 根查询、`node()` 的 Request 分支、RequestExecution / UsageLog 完整类型与嵌套连接、正文与 chunk 加载规则、四个 order 输入、三个 where 输入、成本明细与隐私边（5 + 3 例）；残余差异（where 输入子集、缺失 dataStorage 边、stream + processing 实时预览）登记为 C192（accepted） | — | 完成（C192） |
| 用量日志管理面（GraphQL） | 已由 §103 闭合：usageLogs 根查询、`node()` 的 UsageLog 分支、项目作用域与逐行可见性谓词、CREATED_AT→id 排序重写（8 + 1 例）；残余差异（显式隐私求值 / SQL 表达式、反向边、where 子集）登记为 C193（accepted，承接 C165 / C192） | — | 完成（C193） |
| Analytics 页（GraphQL） | 已由 §104 闭合：四个根字段、过滤器解析与显式 / 用户 key 交集、read_dashboard 作用域与 FORBIDDEN 扩展、metadata 的 UsageLog 隐私链、本地日桶补零（缺省 30 天）、四个维度的 join 与名称合成、tokensPerSecond / ttftMs 与 10ms 下限（10 + 4 例）；残余差异（仅 analytics 切片局部跟进 2851a192、直接 Dapper SQL、Postgres-only 日期分支、时区名参数绑定、`::text` / `checked` 截断）登记为 C194（accepted） | — | 完成（C194） |
