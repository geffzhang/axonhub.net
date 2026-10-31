# 验收账（第三本账）

验收是体检报告，不是"我感觉挺好"。

测量时间：2026-10-31（M2 Llm 切片 + M4 持久化层与请求链路落库 + M3 通道选择与 HTTP 切片，含超时、非法 UTF-8 与字段级类型错误路径；C014 鉴权子切片新增 API key 中间件、ClientIP/allowlist 与 `projects` / `users` / `api_keys` 三表；C028 新增 `systems` 表、系统安全设置与 `/v1` IP 黑名单；C030 新增 `retry_policy` 读取、归一化与每请求 topK；C031 新增 `upstream_error_policy` 的应用与流式错误改写；C012 新增四条负载均衡策略链，残余 C034/C036；通道级请求改写新增 body/header 覆盖、Go 模板子集与设置解析，残余 C037–C041；C035 新增通道限流与并发准入：RPM/TPM 自然分钟窗口、并发硬信号量与 FIFO 队列、429 Retry-After 冷却、RateLimitAware 打分接线，残余 C042–C045；C046–C049 新增 usage 成本计算与注入：flat_fee / usage_per_unit / usage_tiered / usage_volume 四种定价模式与调度匹配、`channel_model_prices` 表与读取接缝、`system_inject_usage_cost` 门控、`usage_logs` 成本列与响应 `usage.cost`；C013 新增无 trace 的多 key 随机分配与执行 key 审计：`ChannelApiKeyProvider`、rendezvous/FNV-1a 选择器、`channel_api_key_suffix` 与 `channel_api_key_index`；Anthropic Messages 入站（非流式）新增请求/响应转换、Go 解码文案、错误信封与 `POST /v1/messages` + `POST /anthropic/v1/messages` 两条路由（127 例 Llm + 7 例 API）；C050 新增 Anthropic 入站流式状态机与聚合器：SSE 事件序列、Read 工具参数缓冲、citations/thinking 事件、usage 合并与 4 组 xtest fixture（Llm 由 272 增至 300，C050 闭合，残余 C052–C055；C051 仍为已接受差异）；C056–C058 新增 Anthropic Messages 出站：请求/响应转换与校验、工具/tool_choice、SSE 出站流式（含 server_tool_use 与 web_search 内联结果）、cache_control 断点优化器（Llm 由 300 增至 367；残余 C056 Vertex 执行器缺口、C057 缺失 delta.type 防御、C058 gjson 宽松解析差异））。本次追加 C059–C067：Responses API 非流式双向变换（入站/出站/压缩、usage 与 created_at 兼容、`GoJsonFloat` 的 Go 数字格式、`POST /v1/responses` 与 `POST /v1/responses/compact` 路由），Llm 由 367 增至 529；C038（Compact.PromptCacheKey 回退）随之闭合，跳过归零。本次追加 C068：Responses API 流式状态机与聚合（`TransformStream` 入站/出站、两侧 `AggregateStreamChunks`、16 个 fixture 回环、错误分类与终止事件语义），移除 2 例旧的「流式未移植」断言并新增 86 例，Llm 由 529 增至 613，C063 闭合。本次追加 C059：Responses 图片请求/响应路径（统一 ImageRequest/ImageResponse/ImageData、image_generation 工具请求构造、线路 RequestType 回写与图片响应还原、codex 图片流聚合），移除 2 例旧的「图片未移植」断言并新增 26 例，Llm 由 613 增至 637，C059 闭合；C061（WebSocket 传输）仍为缺口，客户端 OpenAI Images 变换器登记为 C069。

## 构建

- 命令：`dotnet build AxonHub.slnx`
- 结果：**0 警告 / 0 错误**
- 目标框架：`net10.0`
- 附带验证：`TreatWarningsAsErrors=true`、`AnalysisLevel=latest-recommended`、`EnforceCodeStyleInBuild=true`。
  全局 `NoWarn` 仅豁免与参照实现命名/签名映射直接冲突的规则（CA1068 等），理由逐条写在
  `Directory.Build.props` 注释与对应源文件的 `[SuppressMessage]` 中。

## 测试

| 项目 | 总数 | 通过 | 失败 | 跳过 | 说明 |
|---|---|---|---|---|---|
| `AxonHub.Llm.Tests` | 637 | 637 | 0 | 0 | pipeline 重试 15 + 空响应 9 + OpenAI 聚合器 1 + HTTP 执行器/SSE 增量解码 9 + Go JSON UTF-8 修复与语法文案 10 + 字段级类型错误文案 101 + Anthropic 入站 155（请求/响应转换与校验、Go 解码语义、错误信封、流式状态机 17、Anthropic 聚合器与 usage_test 表 12）+ Anthropic 出站 67（transformer 请求/响应/工具/校验 28、出站 SSE 与 fixture 14、cache_control 优化器 25）+ Responses 非流式 158（入站 38、出站 54、压缩 16、usage/model 50）+ Responses 图片 26（请求构造 9、响应还原 11、线路与错误 4、codex 图片流 1、item action 1）+ Responses 流式 86（入站 24、出站 34、聚合 28） |
| `AxonHub.Persistence.Tests` | 56 | 56 | 0 | 0 | schema 快照 5 + EF 模型一致性 2 + EF/Dapper 共享事务 2 + 连接语义 2 + 九表仓储 45（projects / users / api_keys 各 4 例，systems 4 例，channel_model_prices 6 例） |
| `AxonHub.Grains.Tests` | 88 | 88 | 0 | 0 | 通道选择：打分/窗口 5 + 健康与 topK 6（含 topK 截断与归一化 2）+ 别名解析 3 + 同分顺序/不计数 2 + 负载均衡策略 8（adaptive 中性总分/线性衰减 2、冷却边界 1、权重 tie-break 与未知回退 2、failover 1、circuit-breaker 1、策略切换 1）；限流与并发准入 64：ChannelLimiter 10 + ChannelLimiterManager 12 + ChannelRequestTracker 24 + RateLimitAwareScorer 18 |
| `AxonHub.Api.Tests` | 495 | 495 | 0 | 0 | HTTP 切片 86：orchestrator/重试/超时 17（含通道级重试配置 3、流式性能记录语义 2、策略 topK 1、负载均衡策略接线 1）+ 请求链路落库 5（含鉴权身份传播 1）+ SSE 写帧 9（含上游错误策略 2）+ 请求读取 12（含 zstd/压缩错误文案）+ 端点胶水 18（OpenAI 11 含上游错误策略 3，Anthropic Messages 两条路由 7）+ 期望流选择 2 + 通道重试谓词与 settings 解析 16 + 通道性能记录 7；API key 鉴权与 ClientIP 73：header 提取 14 + 鉴权器流程 13 + ClientIP/allowlist 与中间件文案 46；系统设置与 IP 黑名单 29：SecuritySettings 回退/归一化 15 + 黑名单放行/命中/受信代理 14；重试策略系统设置 49：回退/零值/类型错误 16 + 解析与重复键 7 + 归一化 26；上游错误策略 applier 29：非流改写/本地错误/quota 11 + 响应体字段 8 + 流式分类与策略读取次数 10 + 通道级请求改写 134：body/header 改写与设置解析 133（ChannelOverrideTests 87 + ChannelOverrideSettingsTests 47）+ 端到端落库 1；限流中间件 29：RPM 准入 5 + TPM/冷却追踪 13 + 队列错误 4 + 连接准入与让位 7；usage 成本 43：计算器 26 + 价格读取 12 + 注入与落库 5；多 key 选择与执行 key 审计 23：provider 16 + 审计 7 |
| **合计** | **1276** | **1276** | **0** | **0** | |

命令：`dotnet test AxonHub.slnx`（2026-10-31，Windows 11 / .NET SDK 10.0.401）。

持久化测试通过 Testcontainers 启动 `postgres:17-alpine`（需要本机 Docker），每个测试使用独立 schema；
M2 时期的 SQLite 测试已全部替换为 PostgreSQL 对应项。
写侧 create/update 由 EF Core 10.0.14 执行，读侧 get/list 由 Dapper 2.1.89 执行；
仓储注入 scoped `AxonHubDbContext`，写入加入调用方事务，读侧经同一连接/事务执行（`EfDapperInteropTests` 锁定）。
EF 模型（而非 EF 迁移）由 `EfCoreModelTests` 与幂等 DDL 建出的库结构逐列/逐索引对齐，运行时不创建版本表。

Grain 与 API 测试用 Orleans `TestCluster` 在进程内起一个 silo，`IEnabledChannelProvider` 由假实现注入，
不依赖 Docker；grain 键按请求模型/通道 ID 隔离，用例之间无共享状态。API 测试的传输层用脚本化
`IExecutor` 替代，不发起真实上游请求；SSE 写帧与请求读取用 `DefaultHttpContext` + `MemoryStream` 逐字节断言。

跳过项说明：无。C038 已闭合——`LlmRequest.Compact` 随 Responses 切片移植后，
`PromptCacheKeyFallsBackToTheCompactRequest` 取消 `Skip`，按参照的「顶层键优先、否则回退
`Compact.PromptCacheKey`」分支断言并通过。

## 公开 API 审计

- 参照实现导出数：未统计（M5 收口时执行）
- 本实现公开表面：`AxonHub.Llm`（统一模型、OpenAI 双向变换、SSE 解码、HTTP 执行器、pipeline）、
  `AxonHub.Persistence`（EF Core 写侧 DbContext 与模型、Dapper 读侧仓储、幂等迁移与连接工厂）、
  `AxonHub.Grains` / `AxonHub.Grains.Abstractions`（通道池与通道指标 grain、ChannelLimiter/ChannelLimiterManager/ChannelRequestTracker/RateLimitAwareScorer）、
  `AxonHub.Api`（HTTP 宿主、`POST /v1/chat/completions`、`POST /v1/messages` 与 `POST /anthropic/v1/messages`（Anthropic Messages，含流式）、`POST /v1/responses` 与 `POST /v1/responses/compact`（Responses API，含流式）、请求读取、SSE 写出、orchestrator 适配与 `WithInbound` 入站替换接缝、
  API key 鉴权中间件与 header 提取、gin 兼容的 ClientIP 与 allowlist 匹配、`projects` / `users` / `api_keys` / `systems` 仓储、
  系统安全设置读取与 `/v1` IP 黑名单中间件、Billing（成本计算器、价格读取/注入接缝与 CostItem JSON 形状）、
  通道 API key 选择（`ChannelApiKeyProvider` 与 `IApiKeyProvenance` 审计接缝））
- `AxonHub.Llm` 的 Anthropic 入站面：`AnthropicInboundTransformer`（`IInboundTransformer`）、
  `AnthropicConvertToLlm` / `AnthropicConvertFromLlm`、`AnthropicRequestTypeCheck`（Go 解码文案复刻）
- `AxonHub.Llm` 的 Anthropic 出站面：`AnthropicOutboundTransformer`（`IOutboundTransformer`、
  `Create` 工厂与平台配置）、`AnthropicOutboundStream`（SSE → 统一 chunk）、
  `AnthropicCacheControlOptimizer`（四断点预算、结构锚点与自适应密度）
- `AxonHub.Llm` 的 Responses 面：`ResponsesInboundTransformer`、`ResponsesCompactInboundTransformer`
  （`IInboundTransformer`）、`ResponsesOutboundTransformer`（`IOutboundTransformer`、`Create` 工厂与
  `ResponsesOutboundConfig`）、`ResponsesRequestExtensions`（raw 字段回放）、
  `TransformStream`（入站/出站流式事件变换）与 `AggregateStreamChunks`（流事件聚合）与 `GoJsonFloat`
  （Go encoding/json 的 float64 数字格式）、`ResponsesImageConvert`（图片工具请求构造与统一图片
  响应还原）与统一图片模型 `ImageRequest` / `ImageResponse` / `ImageData`
- 签名差异 / 运行时差异 / 平台限制：见 `02-differences.md`（C001–C069）

## 协议锁定

- [x] 关键协议字段名与顺序由测试锁定（`corpus/upstream-request`、`corpus/unified-request`、
  `corpus/client-response`、`corpus/client-stream`；见 `01-baseline.md` 第 2、3 节）
- [x] SSE 写出帧与客户端错误事件由 `SseStreamWriterTests` 逐字节锁定（gin-contrib/sse 兼容）
- [x] 通道级请求改写的应用顺序与落库时机由 134 例锁定（body/header 中间件、Go 模板子集、
  数组与头操作、非 JSON 门槛、设置解析；残余差异 C037–C041 已登记）
- [x] 通道限流与并发准入由 93 例锁定：RPM/TPM 自然分钟窗口、硬信号量与 FIFO 队列、
  队列超时/取消、429 Retry-After 冷却、本地拒绝不冷却且不允许同通道重试、
  流关闭先拆上游再让位；RateLimitAware 的四个子分与 -10000 硬耗尽（C035；
  残余 C042–C045 已登记）
- [x] usage 成本由 43 例锁定：四种定价模式与调度匹配（含跨午夜/时区/优先级）、
  写缓存 5m/1h 变体、`system_inject_usage_cost` 门控、CostItem 的 trimmed-decimal
  字符串与省略字段、未命中价格的 `cost_items=null` 语义（C046–C049）
- [x] 通道多 key 选择与执行审计由 23 例锁定：无 trace 随机选择仅限启用 key、
  单 key 静态、全禁用回退、FNV-1a 64 与参照向量一致、rendezvous 的确定性/加删最小重映射、
  `channel_api_key_suffix` 的最后 4 个 rune 与 `channel_api_key_index` 的 1-based 位置
  （含禁用 key 仍计数、共享后缀靠索引区分、每次重试各自审计；trace 粘性仍为 C013 缺口）
- [x] 入站请求的字段级解码错误文案与 Go 一致（C003）：OpenAI 由 101 例锁定，Anthropic 由 80 例锁定
  （根值类型、每层字段类型、`content cannot be null` / `invalid content type` / `invalid system prompt format`
  硬错误优先级、大小写折叠、重复键与显式 null 接受集）；结构名、标签路径、Go 类型、数字字面量逐一比对，
  期望值全部来自对参照模块的 Go 探针输出
- [x] Anthropic Messages 入站协议由 155 + 7 例锁定：请求/响应转换与 `stop_reason`/usage 映射、
  校验顺序与文案、六类错误信封（422 `invalid_model_error`、400 `invalid_request_error`、
  500 `internal_server_error`/`error`、ResponseError 与 `HttpClientError` 分支）、
  两条路由共用的端点胶水；流式 17 例锁定 SSE 事件序列（4 组 xtest fixture 逐事件对比）、
  Read 工具参数缓冲、citations/thinking 与终止事件；聚合器 12 例锁定分块合成与
  `usage_test.go` 的 15 项表（C050 已闭合，残余 C052–C055）；
  `input_schema`/`caller` 的显式 null 与缺失合并登记为 C051
- [x] Anthropic Messages 出站协议由 67 例锁定：请求形状（URL/headers/`api_key`/`max_tokens` 优先级/
  system 与省略字段）、工具与 tool_choice 映射、非法参数修复、响应与错误信封；SSE 出站
  以 stop/tool/think 三组 fixture 逐条对比统一 chunk（含 `[DONE]`、完成块 delta、
  `server_tool_use` 与 `web_search_tool_result` 回放、Zhipu prompt token 合并）；
  cache_control 优化器锁定四断点预算、结构锚点、超限裁剪、自适应密度与 raw 嵌套断点
  （C056–C058 已登记：Vertex 执行器缺口、缺失 `delta.type` 防御、gjson 宽松解析差异）
- [x] Responses API 协议由 270 例锁定：非流式 158 例覆盖入站请求/响应、出站请求/响应与压缩双向映射、
  工具（含 namespace 聚合与 strict schema）、raw 工具/输入项回放、Go 解码文案与 `stream_id` 拒绝、
  usage/cost 的 Go 数字格式与 created_at 兼容表；图片 26 例覆盖 `image_generation` 工具请求构造与
  五类校验、data URL/mask 取值、线路 payload 与 `RequestType` 回写、编辑往返、HTTP ≥400 与解码
  错误文案、codex 图片流聚合；流式 86 例覆盖入站 chunk → `response.*` 事件与错误分类、出站 SSE →
  统一 chunk 与语义终态后的 `[DONE]`、聚合器最终响应（3 组入站 fixture、3 组出站 fixture 与
  2 组聚合 fixture 逐事件/逐字段对比）。WebSocket 传输仍未移植（C061），客户端 OpenAI Images
  变换器登记为 C069 缺口（C060/C062/C064–C068 已登记）
- [ ] 跨语言数字范围已校验（±2^53−1）：token 计数以 int64 落库并由
  `UsageLogStoreTests.TokenCountsRoundTripAsInt64` 锁定；LLM JSON 数字路径尚未专项审计
- [x] 编码与行尾归一化策略已确定：`diff_corpus` 自动归一化行尾/BOM；字符串内非法 UTF-8 与
  孤立代理项转义按 Go 规则修复为 U+FFFD（C008；残余 `json.RawMessage` 片段差异见登记）

## 端到端

- [x] 进程内 HTTP 切片与通道计数语义：`POST /v1/chat/completions` 的请求读取、候选选择、重试、SSE 写出
  与通道性能记录由 259 例覆盖（其中 1 例锁定 `retry_policy.load_balancer_strategy` 的接线：failover 按权重、
  round-robin 忽略权重），2 例锁定首包超时与非流式超时，5 例锁定请求/执行/用量落库的字段与时机
  （含鉴权身份写入 request.api_key_id/project_id、execution.project_id 与 usage 的 api_key_id/project_id，
  以及流式 Close 的聚合回填与响应头边界写入），12 例锁定请求读取与 gzip/deflate/zstd 解码（含参照 Go
  程序产出的 zstd/raw DEFLATE 样本；不启动真实 Kestrel 与 PostgreSQL），1 例锁定消息内容中的非法 UTF-8
  经入站解码与出站变换后变为 U+FFFD，7 例锁定性能记录终态映射，2 例端到端锁定无终态流不计数与聚合完成计成功；
  73 例锁定 API key 鉴权：header 优先级/前缀/首值/Trim、无 header 与无效 key 的 no-auth 回退、懒建 key 的全字段、
  401/403/500 响应体逐字节、allow_no_auth、allowlist 的精确/CIDR/IPv4 映射地址匹配，以及 gin 受信代理规则
  （默认 `trusted_proxies=[]` 时忽略转发头）；29 例锁定系统安全设置回退/归一化与 IP 黑名单的放行、命中、
  受信代理取址和 403 信封（另有 6 例 `netip` 前缀边界并入上述鉴权的 ClientIP 用例）；49 例锁定
  `retry_policy` 的缺行/读取失败/非法文档回退、`null` 与空对象的零值语义、字段归一化与类型错误；
  3 例锁定每请求策略读取后的 topK（6 候选打满 6 次尝试）与池的截断/归一化；29 例锁定
  `upstream_error_policy` 的应用（custom/hidden/passthrough、本地错误不改写、quota 跳过、gjson 字段解析、
  流式先分类再改写与两次策略读取），另有 3 例端点与 2 例 SSE 写出锁定同一策略的端到端响应字节；
  134 例锁定通道级请求改写：body/header 覆盖、Go 模板渲染、数组与头操作、非 JSON 门槛与设置解析，
  其中 1 例端到端断言出站请求与 execution 落库都记录改写后的 body/header；93 例锁定通道限流与
  并发准入（RPM 准入与同通道重试、TPM 追踪、429 冷却、队列拒绝信封、并发让位与流关闭顺序；
  等待方在专用线程上驱动，不依赖真实上游）；43 例锁定 usage 成本：四种定价模式与调度匹配、
  写缓存 5m/1h 变体、价格读取回退与损坏价格整通道失效、`system_inject_usage_cost` 开关、
  非流与流式 chunk 注入、usage 行成本列与响应 `usage.cost`；23 例锁定通道多 key：无 trace
  随机分配仅限启用 key、单 key 静态与全禁用回退、rendezvous/FNV-1a 选择器、多 key Authorization、
  每次尝试（含跨通道重试）的 suffix/index 与实际发送 key 一致；7 例锁定 Anthropic Messages 的
  `POST /v1/messages`（与 `/anthropic/v1/messages` 共用 handler）：OpenAI 上游映射为 Anthropic
  message 信封（content/stop_reason/usage）、校验失败与 stream:true 的 `invalid_request_error`、
  无效模型的 422 `invalid_model_error`、空 body 仍走 OpenAI 风格 `JSONError`、上游 429 经出站解析后
  以 Anthropic 信封返回、ResponseError 的信封
- [x] 真实宿主启动（Kestrel + PostgreSQL + Orleans silo） — 启动冒烟通过（2026-10-30）：
  `dotnet AxonHub.Api.dll` 连接 `postgres:17-alpine`，启动时幂等 DDL 建出当时切片的 4 张表
  （C023 新增 `projects` / `users` / `api_keys`、C028 新增 `systems` 后总数为 8；本次冒烟未重跑，鉴权与黑名单路径由进程内测试覆盖）；`GET /health` 返回
  `200 {"status":"ok"}`；未知模型返回 422 `invalid_model_error`；向 `channels` 插入一条指向拒绝连接端口的
  启用通道后，`POST /v1/chat/completions` 在 3 次尝试后返回 502 `upstream_error`（`upstream_stream_interrupted`）；
  库内写入 1 条 `failed` request（`channel_id` 已回填）、3 条 `failed` execution（每次尝试一行，`response_status_code=502`、
  有 `error_message`、无延迟指标）、0 条 usage log；execution 请求头中的 `Authorization` 为 `******`，请求体以
  jsonb 原样落库。
- [x] 容器内（应用镜像 + PostgreSQL 容器） — 2026-10-30：多阶段 `Dockerfile`（`sdk:10.0` 构建 → `aspnet:10.0` 运行，
  监听 8080）构建出 `axonhub-api:smoke`（253 MB）；镜像与 `postgres:17-alpine` 同网络启动，启动时幂等 DDL 建出
  当时切片的 4 张表（C023 后为 7 张、C028 后为 8 张，镜像未重跑）；
  `GET /health` 返回 `200 {"status":"ok"}`；未知模型 422 `invalid_model_error`；插入指向 `http://127.0.0.1:1` 的启用通道后，
  `POST /v1/chat/completions` 3 次尝试后返回 502 `upstream_error`（`upstream_stream_interrupted`，约 3.1s，容器内拒绝连接更快）；
  库内 1 条 `failed` request（`channel_id` 已回填）、3 条 `failed` execution（`response_status_code=502`、有 `error_message`、
  无延迟指标、`Authorization` 落库为 `******`）、0 条 usage log；请求体以 jsonb 原样落库；容器日志无未处理异常；
  冒烟后删除容器与网络
- [ ] 跨平台（Windows / Linux / macOS） — 目前仅在 Windows 11 验证
- [ ] 跨传输（stdio / HTTP / 进程内） — 仅进程内 HTTP 切片

## 未闭合风险

| 风险 | 影响 | 处置 |
|---|---|---|
| 应用容器镜像与部署形态未验证 | 已解决：多阶段 Dockerfile 构建的镜像与 PostgreSQL 容器同网络启动，健康检查、422/502 链路与落库均通过（见「端到端」） | 已关闭（镜像冒烟） |
| C012：负载均衡策略链 | 已解决：adaptive / failover / circuit-breaker / round-robin 四条链按参照公式、权重 tie-breaker、健康分区与 topK 实现；策略取自 `retry_policy.load_balancer_strategy`，未知值回退 adaptive；24 个 grain 用例 + 1 个 orchestrator 接线用例锁定 | 已关闭；残余 C034（无延迟样本，LatencyAware 恒 40）与 C036（无模型级熔断状态，按关闭态计分） |
| C035：RateLimitAware 恒满分 | 已解决：`ChannelRequestTracker`（自然分钟窗口、只延长的冷却、惰性清理）、`ChannelLimiter`/`ChannelLimiterManager`（硬信号量、FIFO、配置重建）、`RateLimitAwareScorer`（RPM/TPM 与并发取最小值、-10000）接入四条策略链；RPM 准入、TPM 追踪与 429 冷却中间件挂在请求链路上；93 个用例锁定（64 grain + 29 API）。残余：C042（阻塞线程）、C043（单 InnerException）、C044（OTel 指标未移植）、C045（nil tracker 的 noop 不可达） | 已关闭；残余已登记 |
| C013：多 API key 的 trace/thread 粘性未移植 | 无 trace 时的多 key 随机分配、rendezvous/FNV-1a 选择器与 `channel_api_key_suffix`/`channel_api_key_index` 审计列已按参照实现（23 例锁定）；仍缺有 trace 时的同 trace 固定 key、新增 key 的 LRU 稳定性与 `trace_sticky_mode` 的通道粘性，多 key 通道每次尝试可能换 key | 接入 trace 子系统后补 TraceStickyKeyProvider 的 trace 分支与 LRU |
| C042–C045：限流的实现结构差异 | C042 等待中的请求占用线程而非 goroutine；C043 队列错误用 `Cause` 属性承载类型化原因而非多错误 Unwrap；C044 五个 OTel 限流指标未移植，部署方拿不到 inflight/waiting/拒绝计数；C045 nil tracker 的 noop 路径不可达 | 准入与打分行为等价，差异在资源模型与可观测性；接入 OTel 时按同名 instrument 与 Snapshot 回调补 C044 |
| C014：成本计费、用户鉴权（JWT/OIDC/角色）与通道管理面未接入 | API key 鉴权与身份传播已接入（C023），系统安全设置与 IP 黑名单已接入（C028）：`/v1` 先过黑名单再过鉴权，request/execution/usage 的 project/api_key 已回填；通道级请求改写已接入（body/header 覆盖、模板渲染、sjson 路径语义，134 例锁定，残余 C037–C041）；通道限流与并发准入已接入（C035，93 例锁定，残余 C042–C045）；usage 成本计费与注入已接入（C046–C049，43 例锁定）；但仍无用户登录与管理面权限，通道写入校验、覆盖模板 CRUD 与 `channel_model_prices` 管理面未接入（C037/C048） | 后续里程碑按参照逐项接入并补测 |
| C046–C049：成本计费的精度、读取与未命中语义 | C046 已接受：金额用 `System.Decimal`（约 28–29 位有效数字），参照为 shopspring 任意精度，极端乘积可能有末位差异；C047 已接受：价格按请求实时读库（注入开启时最多两次查询），参照为进程内缓存，外部改价立即生效；C048 真实缺口：`channel_model_prices` 管理面（写入、reference-id 轮换、版本表、自动定价）未移植；C049 推断：未命中价格时 `cost_items` 写 JSON `null`（Ent `SetCostItems(nil)` 语义，Ent 源码不可得） | 读取路径与可观察金额一致；管理面随通道管理面落地；C049 若日后用真实 Ent 数据库核对为 `[]`，只需改中间件字面量并更新条目与测试 |
| C015：zstd 请求体与压缩错误文案 | 已解决：gzip/x-gzip/deflate/zstd 全部解码；gzip 头错误与 zlib/raw DEFLATE 分支逐字/逐分支对齐。残余差异仅损坏/截断压缩体的错误细节（缺 flate 偏移、gzip 尾部校验、zstd 内层文案） | 已关闭（ZstdSharp.Port；残余差异见 C015） |
| C016：通道级 `retryableStatusCodes` / `retryableErrorPatterns` 未生效 | 已解决：候选携带从 settings 解析的重试规则，`CanRetry` 按参照逐条判断（传输错误、429/5xx、额外状态码、子串/正则文本） | 已关闭（16 个谓词/解析用例 + 3 个端到端用例锁定） |
| C017：通道性能记录仅成功/失败计数，取消与无终态流语义不同 | 已解决：按 attempt 共享 `ChannelPerformanceRecord`，非流成功与 raw error 由指标中间件标记，流终态分类与聚合完成由持久化副本补记；取消不计故障，无终态且未聚合完整的流不计数。`retry_policy.auto_disable_channel` 的规则已解析并归一化（legacy statuses 迁移），但自动禁用动作未执行；残余还有性能明细字段与异步工作 | 已关闭（7 个记录语义用例 + 2 个端到端用例锁定） |
| C018：同步 recorder 每次调用一个短生命周期工作单元 | 一次请求链路最多 5 次短事务，request 与 execution 不同事务提交 | 对当前数据路径等价；引入批量/事务需求时重估 |
| C019：流式响应的 external id / body 与流式 request 响应头未回填 | 已解决：Close 时聚合分块回填两侧归档字段，终态分类与失败路径按参照实现 | 已关闭（测试锁定） |
| C020：`response_chunks` 与 `store_chunks` / `store_response_body` 存储策略未接入 | 默认配置下与参照一致（chunks 为空、body 写入）；开启存储策略的部署无分块归档 | 随存储策略功能落地 |
| C021：下游响应头捕获点与服务器自动头集合不同 | Kestrel 与 gin 注入的自动头不同，落库 JSON 键集合有差异；业务头与脱敏一致 | 已接受（测试锁定业务头） |
| C008：非法 UTF-8 请求体 | 已解决：字符串字面量内的非法字节与孤立代理项转义按 Go 规则修复（每字节一个 U+FFFD，合法代理对保留），结构错误仍用原始字节报告。残余差异仅 JsonElement 承载的原始片段与请求体归档路径 | 已关闭（C008 残余差异见登记） |
| C010：非 chat 响应类型的空响应探测 | 14 个参照用例顺延 | 随对应端点落地 |
| C003：字段级 JSON 类型错误文案 | 已解决：入站请求按参照的字段/类型表逐 token 复刻 Go 解码语义，错误文案逐字节一致 | 已关闭（101 例锁定；残余见 C022） |
| C022：int32 index 上限与响应侧 null 元素 | `tool_calls[].index` 超过 int32 时 Go 接受而本实现报解码错误；上游响应中的 null 元素当前抛 500 而非零值 | 真实缺口；协议实际取值很小，待遇到再按 Go 语义补零值替换 |
| 持久化测试依赖 Docker（Testcontainers 启动 `postgres:17-alpine`） | 无 Docker 的环境无法运行持久化测试 | CI 必须提供 Docker；随 M3 补 CI 配置 |
| C050–C058：Anthropic 双向流式、聚合器与出站 | C050 已解决：流式状态机与聚合器按 `inbound_stream.go`/`aggregator.go` 移植，4 组 xtest fixture 逐事件对比，`stream:true` 返回 200 `text/event-stream`；C051 已接受（raw JSON 载体，见登记）；C052 已接受：多条非法工具调用按 index 升序报告（参照 map 迭代随机）；C053 已接受：聚合器跳过负 index（参照 panic）；C054 已接受：无效工具输入在聚合早期抛 `InvalidOperationException`（参照在 `json.Marshal` 时失败，死 jsonrepair 省略）；C055 已接受：省略从未赋值的 `pendingReasoningItemID` 死字段；C056 真实缺口：Vertex AI 出站执行器（OAuth/JWT/区域 URL）未移植，构造期显式拒绝，Bedrock 仅保留直连报文形状；C057 已接受：缺失 `delta.type` 时跳过而非 panic；C058 已接受：流错误事件用严格 JSON 解析，畸形输入的兜底文案退化为 `stream_error` | C050 已关闭（测试锁定）；C056 为后续里程碑的显式缺口（不会发出错误请求）；C052–C055、C057、C058 仅影响畸形输入或内部错误文案 |
| C059–C069：Responses API 切片 | C059 已解决：图片请求/响应路径与图片流式聚合按 `image_request.go` / `image_response.go` 移植，26 例锁定，出站遇 `RequestType=image` 改走 `image_generation` 工具请求并在响应侧还原统一 `ImageResponse`；C069 真实缺口：客户端 OpenAI Images 入站/出站变换器（`/v1/images/*`）未移植；C060 已接受：`stream_id` 用严格 `JsonDocument` 探测，畸形 body 的结果与 gjson 不同；C061 真实缺口：WebSocket 传输未移植，配置为 websocket 时仍按 HTTP 发送（`error` 事件在流层已锁定）；C062 已接受：会话 ID 经 `SessionIdProvider` 接缝注入（trace 子系统见 C013）；C063 已解决：入站/出站流状态机与聚合器按参照实现，86 例锁定（fixture 回环、终止事件、错误分类）；C064 已接受：字段级解码文案为 System.Text.Json 文本；C065 已接受：strict `required` 补齐为文档序（参照 map 随机序）；C066 已接受：工具 schema 保留数字词法（参照经 float64 重编码）；C067 真实缺口：cost 之外的 double 仍按 STJ 数字格式输出；C068 已接受：并行工具收尾顺序由 Go map 随机序改为插入序，响应元数据经 JSON 字符串/对象往返 | 非流式 158 例 + 图片 26 例 + 流式 86 例锁定文本/工具/压缩/图片/流事件行为；WebSocket 传输与客户端 Images 端点仍未移植；极端量级浮点的 JSON 表示与 Go 不同但数值等价 | 后续里程碑补 WebSocket 与 Images 端点；C067 随全局 double 序列化工厂闭合 |
| C024–C027：鉴权子切片的已接受差异 | noauth key 指向已删除项目时参照触发唯一键冲突返回 500，本实现返回 401 且不改动该行（C024）；owner 选择在参照中无 `ORDER BY`，多 owner 时顺序不定（C025）；无 API key/项目缓存，每次请求读库（C026）；配置载体为 ASP.NET Core 配置而非 YAML 结构体（C027） | 已接受，测试锁定 401 行为（C024/C025），C026/C027 为性能与载体差异 |
| C028–C029：系统安全设置与 IP 黑名单 | C028 已解决：`systems` 表、`SecuritySettingsOrDefault` 的回退/归一化与 `/v1` 黑名单中间件按参照实现，403 信封逐字节一致；C029 已接受：系统设置无进程内缓存，每个 `/v1` 请求（有 ClientIP 时）读一次库，外部改库立即可见 | 已关闭；缓存随配置/缓存里程碑（C029） |
| C030–C033：重试策略与上游错误策略 | C030 已解决：`retry_policy` 的读取/归一化与每请求 topK 已接入（49 例 + 3 例），禁用时 1 个候选，策略计数大于默认值时能打满候选；C031 已解决：`upstream_error_policy` 的 hidden/custom 按参照应用到非流与流式错误（含"非流传输错误不被改写"的真实不对称），34 例锁定；C032 已接受：重复键后跟 `null` 时 .NET 与 Go 解码语义不同；C033 已接受：错误体解析用 System.Text.Json 而非 gjson，截断报文与重复键的结果不同 | C030/C031 已关闭；C032/C033 仅手改库或畸形上游报文可触发 |
| 其余 Ent 表未移植（data_storages / traces / prompts / roles 等） | 依赖这些表的功能不可用 | 随功能落地 |
