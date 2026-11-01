# 验收账（第三本账）

验收是体检报告，不是"我感觉挺好"。

测量时间：2026-11-01（M2 Llm 切片 + M4 持久化层与请求链路落库 + M3 通道选择与 HTTP 切片，含超时、非法 UTF-8 与字段级类型错误路径；C014 鉴权子切片新增 API key 中间件、ClientIP/allowlist 与 `projects` / `users` / `api_keys` 三表；C028 新增 `systems` 表、系统安全设置与 `/v1` IP 黑名单；C030 新增 `retry_policy` 读取、归一化与每请求 topK；C031 新增 `upstream_error_policy` 的应用与流式错误改写；C012 新增四条负载均衡策略链，残余 C034/C036；通道级请求改写新增 body/header 覆盖、Go 模板子集与设置解析，残余 C037–C041；C035 新增通道限流与并发准入：RPM/TPM 自然分钟窗口、并发硬信号量与 FIFO 队列、429 Retry-After 冷却、RateLimitAware 打分接线，残余 C042–C045；C046–C049 新增 usage 成本计算与注入：flat_fee / usage_per_unit / usage_tiered / usage_volume 四种定价模式与调度匹配、`channel_model_prices` 表与读取接缝、`system_inject_usage_cost` 门控、`usage_logs` 成本列与响应 `usage.cost`；C013 新增无 trace 的多 key 随机分配与执行 key 审计：`ChannelApiKeyProvider`、rendezvous/FNV-1a 选择器、`channel_api_key_suffix` 与 `channel_api_key_index`；Anthropic Messages 入站（非流式）新增请求/响应转换、Go 解码文案、错误信封与 `POST /v1/messages` + `POST /anthropic/v1/messages` 两条路由（127 例 Llm + 7 例 API）；C050 新增 Anthropic 入站流式状态机与聚合器：SSE 事件序列、Read 工具参数缓冲、citations/thinking 事件、usage 合并与 4 组 xtest fixture（Llm 由 272 增至 300，C050 闭合，残余 C052–C055；C051 仍为已接受差异）；C056–C058 新增 Anthropic Messages 出站：请求/响应转换与校验、工具/tool_choice、SSE 出站流式（含 server_tool_use 与 web_search 内联结果）、cache_control 断点优化器（Llm 由 300 增至 367；残余 C056 Vertex 执行器缺口、C057 缺失 delta.type 防御、C058 gjson 宽松解析差异））。本次追加 C059–C067：Responses API 非流式双向变换（入站/出站/压缩、usage 与 created_at 兼容、`GoJsonFloat` 的 Go 数字格式、`POST /v1/responses` 与 `POST /v1/responses/compact` 路由），Llm 由 367 增至 529；C038（Compact.PromptCacheKey 回退）随之闭合，跳过归零。本次追加 C068：Responses API 流式状态机与聚合（`TransformStream` 入站/出站、两侧 `AggregateStreamChunks`、16 个 fixture 回环、错误分类与终止事件语义），移除 2 例旧的「流式未移植」断言并新增 86 例，Llm 由 529 增至 613，C063 闭合。本次追加 C059：Responses 图片请求/响应路径（统一 ImageRequest/ImageResponse/ImageData、image_generation 工具请求构造、线路 RequestType 回写与图片响应还原、codex 图片流聚合），移除 2 例旧的「图片未移植」断言并新增 26 例，Llm 由 613 增至 637，C059 闭合；C061（WebSocket 传输）仍为缺口，客户端 OpenAI Images 变换器登记为 C069。本次追加 C061：Responses WebSocket 传输（执行器与连接池、续传对账、手写 RFC 6455 握手、Do 聚合、HTTP 传输包装与编排接缝），Llm 由 637 增至 677（37 + 3），Api 由 495 增至 500（接缝 5），C061 闭合；拨号、JSON 往返、URL 归一化与生产接线分别登记为 C070–C073。本次追加 C069：OpenAI Images 客户端变换器（客户端 `/v1/images/generations|edits|variations` 的 JSON 与 multipart 入站解码、出站请求构造与响应还原、共享 GoMultipart/DetectContentType/MimeTypes 基础设施），参照 69 个测试函数逐条移植为 71 例，Llm 由 677 增至 748，C069 闭合；入站解码边界登记为 C074。本次追加 C075：OpenAI Images 端点接线（`POST /v1/images/generations` 与 `/v1/images/edits` 两条路由、multipart 透传、错误信封与空响应检测的 `resp.Image` 分支），Api 由 500 增至 508，C075 闭合；variations 路由按参照保持停用。本次追加 C076：OpenAI Embeddings / Moderations 客户端变换器与 `POST /v1/embeddings`、`POST /v1/moderations` 两条路由（统一模型与 Go JSON 编解码、入站/出站变换、`hasResponseContent` 的 moderation/embedding 分支），参照 16 个测试函数逐条移植为 59 例 + 空响应探测 5 例 + 端点接线 7 例，Llm 由 748 增至 812，Api 由 508 增至 515，C076 闭合；C010 的 embedding/moderation 分支随之闭合。本次追加 C077：OpenAI Alpha Search / Decisions 客户端变换器与 `POST /v1/alpha/search`、`POST /v1/decisions` 两条路由（原始 body 透传、sjson 语义的顶层 model 补丁、严格信封校验、`hasResponseContent` 的 alpha-search/decisions 分支），参照 alpha_search 测试与 decisions 的 5 个测试文件逐条移植为 19 + 110 例、空响应探测 2 例与端点接线 6 例，Llm 由 812 增至 943，Api 由 515 增至 521，C077 闭合；C010 的 decisions/alpha-search 分支随之闭合，model 补丁的 HTML 转义字节差异登记为 C078。本次追加 C079：OpenAI Audio 客户端变换器与三条 `/v1/audio/*` 路由（speech TTS 的三种传输形态——非流式二进制、SSE `audio_bytes` 流、二进制块流，STT 的 multipart/JSON 与 SRT/verbose_json 透传，`GoCast.TryParseFloat` 的 strconv.ParseFloat 保真，`BinaryStreamWriter` 原始字节写出），参照 `audio_inbound_test.go` / `audio_outbound_test.go` 的 15 个测试函数逐条移植为 39 例、空响应探测 5 例与端点接线 58 例，Llm 由 943 增至 987，Api 由 521 增至 579，C079 登记，C010 的 speech / transcription 分支随之闭合，仅剩 rerank / video。本次追加 C080：Jina Rerank / Embedding 客户端变换器与 `/v1/rerank`、`/jina/v1/rerank`、`/jina/v1/embeddings` 三条路由（rerank 入站校验链与 500 `api_error` 信封、embedding 入站的 Content-Type 校验与 Cache-Control: no-cache、出站 rerank/embedding 线路形状与 usage 映射、`jina` 通道类型分派），参照 `rerank_inbound_test.go` / `embedding_test.go` 等测试逐条移植为 79 例、空响应探测 2 例与端点接线 7 例，Llm 由 987 增至 1068，Api 由 579 增至 586，C080 登记，C010 的 rerank 分支随之闭合，非 chat 响应类型仅剩 video。本次追加 C081：OpenAI Video 客户端变换器 Llm 层（`POST /v1/videos` 的 JSON 与 multipart 入站解码、extra_body 扫入与合并、统一 VideoRequest/VideoResponse 与出站 create/get/delete 转换、`hasResponseContent` 的 video 分支），参照 `video_inbound_test.go` / `video_outbound_test.go` 的 11 个测试函数逐条移植为 57 例（video 变换器 56、空响应探测 1），Llm 由 1068 增至 1125，C081 登记；C010 的 video Llm 分支随之闭合，仅剩 video 端点接线（routes + VideoService + 持久化）。本次追加 C082–C085：OpenAI Video 端点接线（`POST /v1/videos` 复用 chat handler，`GET/DELETE /v1/videos/:id` 走 biz VideoService 的任务查询/删除流程，`VideoEndpoint` 与 `RequestIdentity.FromHttpContext`，请求/执行两表的查询与轮询快照持久化接缝），参照 `video_test.go` 的 7 个测试函数与 api/openai.go 的三个 handler 移植为 33 例 Api + 3 例持久化，Api 由 586 增至 619，Persistence 由 56 增至 59，C010 的 video 端点接线随之闭合（doubao / zenmux 视频变换器仍为既定裁剪）。本次追加 C086：OpenAI 旧版 Completions 客户端变换器与 `POST /v1/completions` 路由（入站 `CompletionRequest` 解码与校验链、`Completion != nil` 的响应/错误信封、`[DONE]` 与 usage-only 流块过滤、聚合器按 Go 字段序还原 `llm.Response`、出站 `##` RawURL/版本归一与 Bearer、`openai` 通道按入站 ApiFormat 选中 completions 出站），参照 `completion_inbound.go` / `completion_outbound.go` / `completion_aggregator.go` 与 inbound/outbound 测试逐条移植为 65 例 Llm + 4 例端点接线，Llm 由 1125 增至 1190，Api 由 619 增至 623，C086 登记（通道能力表未建模，出站按通道类型 + 入站 ApiFormat 分派）。本次追加 C087：TypeSafe SystemOne 客户端变换器与 `POST /v1/systemone`、`POST /typesafe/v1/systemone` 两条路由（统一模型与 `SystemOneAnswer` 的自定义 JSON / Extra 保真、Go `any` 的键序与数字字面量、入站 Decoder 语义与校验序、出站线路形状与错误信封、`hasResponseContent` 的 systemone / compact / completion 分支、`typesafe` 通道分派），参照 `typesafe/{model,inbound,outbound}.go`、`llm/systemone.go` 与 orchestrator 的 4 个测试函数逐条移植为 56 例 Llm + 9 例端点接线，Llm 由 1190 增至 1246，Api 由 623 增至 632，C087 登记（pass-through body 未移植、通道能力表未建模、`JsonElement` 承载 `any` 与字段级解码文案）；`/jina/v1` 与 `/typesafe/v1` 一并进入鉴权 / IP 黑名单分支。本次追加 C088–C090：模型清单与检索端点（`GET /v1/models`、`GET /v1/models/*model` 与 `GET /anthropic/v1/models` 三条路由、`XRegexp` 的黑名单匹配语义、系统模型设置的 Go 语义解码、`ModelService.ListEnabledModels` 的通道派生模型、请求 ID 中间件及其在错误体中的回显、OpenAI / Anthropic 两种列表与错误信封的逐字节线路形状），新增 55 例（`GoTime` 7 + `XRegexp` 8 + 系统模型设置 15 + `ModelService` 7 + 端点 14 + 请求 ID 中间件 4），Llm 由 1246 增至 1253，Api 由 632 增至 680，C088（`models` 表与 API Key profile 裁剪）、C089（trace ID 链路未移植）、C090（路由边界与顺序确定化）登记。本次追加 C091：pass-through 通道重放的请求半程（`isPassThroughEnabled` 的通道/全局开关继承与流对齐、body 重放与 sjson 语义的 model 补丁、Content-Type 同步、Codex 响应头白名单、User-Agent 策略、`pass_through_applied` 落库，以及 `IPassThroughBodyPolicy` 的 decisions 恒真 / typesafe 仅 SystemOne），新增 95 例（`PassThroughTests` 72、通道 settings 解码 11、系统设置 provider 8、编排端到端 4），Api 由 680 增至 775，Llm 保持 1253，C091 登记（取代 C087 中 pass-through body 缺口的描述并修正 C014 的开关表述）。本次追加 C091 的响应/流半程：`captureRawProviderResponse/Stream` 与 `applyPassThroughResponse/Stream` 四个中间件、双通道扇出（容量 64）与 attach 前 backlog（1024 事件 / 8 MiB，含 Type 与 LastEventID）、per-attempt 错误引用、取消后排干缓冲再关闭上游、`NextChannel` / `PrepareForRetry` 复位扇出，新增 26 例（`PassThroughStreamTests`：非流响应 7、原始流捕获 6、原始流应用 6、通道流取消语义 2、backlog 预算 2、真实 pipeline 集成 3），Api 由 775 增至 801，Llm 保持 1253，C091 更新为请求与响应/流半程均已接入、codex 策略仍为缺口。本次追加 C092：pass-through 提示词保护（`protectPrompts` 入站中间件、Messages / Decisions / Compact 三路规则应用与拒绝文案、`IPromptProtecter` 与 `IPromptProtectionResultProvider` 双接缝、legacy protector 的提示词快照回放校验、五种 APIFormat 的原始 body 文本补丁与 Gemini `functionResponse.response` 的 UseNumber 递归掩码、`PassThroughRequestBodyMiddleware` 的掩码合并与校验回退、`prompt_protection_rules` 读取接缝与失败开放），并修正 `ApiFormats.OpenAIResponseCompact` 的字面量为参照的 `openai/responses_compact`，新增 71 例（规则应用 18、中间件 11、规则服务 9、原始补丁 13、真实编解码载荷 13、回放校验 4、编排端到端 3），Api 由 801 增至 872，Llm 保持 1253，Grains 保持 88，合计 2213；C092 登记（每请求读库替代 30 秒 live 缓存、管理面/GraphQL/预览未移植、.NET Regex 与 regexp2 语义差异、Gemini/Ollama 载荷级对照延后），C091 中的提示词保护缺口关闭，codex `AllowPassThroughBody` 仍为缺口。本次追加 C093–C094：OAuth 凭据与令牌链路（凭据/令牌响应解析校验顺序、单飞刷新、EnsureFresh 与自动刷新、FormEncoded/JSON 交换策略、PKCE、设备码）与 codex 出站及通道接线（`buildCodexOutbound` 的 OAuth/API key 两种 token provider、旧版 apiKey 内嵌 OAuth、`default_test_model` 经 `ChooseModel` 解析、ws/wss transport 推断、pass-through body 策略与 `SupportsCodexResponseHeaders` 接缝），新增 111 例 Llm（codex 61 + OAuth 50）、20 例 API 接线与 4 例 Grains，Llm 由 1253 增至 1364，Api 由 872 增至 892，Grains 由 88 增至 92，合计 2407；C091 的 codex `AllowPassThroughBody` 缺口关闭，C094 登记端点表、token provider 复用/持久化/自动刷新、代理感知 client 与 API 层 codex 响应头转发的真实缺口。本次追加 C089 的 trace 子系统基础：`TracingOptions`（三条默认头、extra headers/body 字段、三个提取器开关）与 `TraceContext`/`ThreadContext`，`traces` 表与 `TraceStore.GetOrCreateTrace` 的 `(trace_id, project_id)` 语义与唯一约束重试，`TraceMiddleware` 的请求头 → extra headers → OpenCode → Claude Code `metadata.user_id` → Codex 头/turn metadata → body 字段提取链、非消息端点跳过与 400 错误信封，trace id 经 `RequestIdentity` 落到 `requests.trace_id` 并以 session id 传给通道出站，响应的 `AH-Trace-Id` 头与 `AH-Session-Id` 上下文；新增 60 例（Llm 12：ClaudeCode 用户 id 解析 5 + OpenCode 会话头 7；Api 42：trace 中间件 38 + 请求 ID 新增 3 + codex 会话接线 1；Persistence 6：traces 表），Llm 由 1364 增至 1376，Api 由 892 增至 934，Persistence 由 59 增至 65，合计 2467；C089 闭合，C013/C062 的 trace 相关成因更新，残余差距登记为 C095（thread 中间件、trace 查询/GC/GraphQL、trace 粘性 key、日志与 gjson 语义）。本次追加 C095 的 thread 半程：`ThreadMiddleware`（配置头首值、按项目 get-or-create、无头 / 无身份 / 持久化失败 fail-open、`ThreadContext` 发布线程行 id）、`ThreadStore.GetOrCreateThread` 的 `(thread_id, project_id)` 语义与唯一约束重试、`threads` 表（6 列、3 个索引）与 EF 模型，`Program` 在 `/v1` 族分支按参照顺序挂载 `ThreadMiddleware` → `TraceMiddleware`；新增 12 例（Api 7 + Persistence 5），Api 由 934 增至 941，Persistence 由 65 增至 70，合计 2479；C095 的 thread 缺口关闭，残余为 trace 查询/GC/GraphQL、trace 粘性 key、claudecode/opencode 出站与日志接缝。本次追加 C013 的 trace 粘性 API key：`ChannelApiKeyProvider` 读取 `WithTrace` 发布的 trace id（线上请求 `SessionId` 接缝），多 key 通道按 `traceID|key` 做 rendezvous 选择，同一 trace 在重试与跨请求间固定同一 key；新增 8 例（Api 7 + 编排端到端 1），Api 由 941 增至 949，合计 2487；C013 闭合（残余 apiKeyOverride 与 trace_sticky_mode 通道粘性），C095 的 trace 粘性 key 项关闭。

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
| `AxonHub.Llm.Tests` | 1376 | 1376 | 0 | 0 | pipeline 重试 15 + 空响应 24（chat/图片/嵌入/审核/决策/alpha-search/音频/rerank/video 探测与 pipeline 语义）+ OpenAI 聚合器 1 + HTTP 执行器/SSE 增量解码 9 + Go JSON UTF-8 修复与语法文案 10 + 字段级类型错误文案 101 + Anthropic 入站 155（请求/响应转换与校验、Go 解码语义、错误信封、流式状态机 17、Anthropic 聚合器与 usage_test 表 12）+ Anthropic 出站 67（transformer 请求/响应/工具/校验 28、出站 SSE 与 fixture 14、cache_control 优化器 25）+ Responses 非流式 158（入站 38、出站 54、压缩 16、usage/model 50）+ Responses 图片 26（请求构造 9、响应还原 11、线路与错误 4、codex 图片流 1、item action 1）+ Responses 流式 86（入站 24、出站 34、聚合 28）+ Responses WebSocket 40（执行器/握手/URL 4、流生命周期与取消 6、错误事件与聚合 9、连接池与续传对账 18、HTTP 传输包装 3）+ OpenAI Images 71（入站 JSON/multipart 与响应 36、出站请求/响应与 extractFile 33、编辑线路形状 2）+ OpenAI Embeddings/Moderations 59（嵌入变换器 43、审核变换器 16）+ OpenAI Alpha Search/Decisions 129（alpha 变换器 19、decisions 变换器 110）+ OpenAI Audio 44（speech/transcription/translation 变换器 39、空响应探测 5）+ Jina Rerank/Embedding 79（rerank 入站 23、出站 29、embedding 出站 7、embedding 入站 20）+ OpenAI Video 57（video 变换器 56、空响应探测 1）+ OpenAI 旧版 Completions 65（入站请求 13、入站响应 6、入站流式 5、入站错误映射 6、出站请求 17、出站响应 9、出站流式 4、聚合 5）+ TypeSafe SystemOne 56（统一模型 8、入站请求 15、入站响应与错误 8、出站请求 9、出站响应 8、出站错误与流 6、空响应探测 2）+ Go 时间线路形态 7（RFC3339Nano 整秒 / 毫秒 / 微秒有效位、零值、Unspecified 视为 UTC、Unix 秒截断与零值 `-62135596800`）+ Codex 61（出站变换与执行器 39、模拟器身份头 10、additional tools 作用域 6、alpha search session 优先级 3、`DecodeAuthJson` 1、token provider 2）+ OAuth 50（凭据文档与 token 响应 8、token provider 4、自动刷新 2、设备码 31、PKCE 5）+ ClaudeCode 用户 id 解析 5（legacy 前缀、v2 JSON、空 session、非法输入、首尾空白）+ OpenCode 会话头 7（当前头 / `X-Session-Id` / 亲和头、优先级、缺失与空白回退、trim 归一） |
| `AxonHub.Persistence.Tests` | 70 | 70 | 0 | 0 | schema 快照 5 + EF 模型一致性 2 + EF/Dapper 共享事务 2 + 连接语义 2 + 九表仓储 48（projects / users / api_keys 各 4 例，systems 4 例，channel_model_prices 6 例，requests / request_executions 的 video 任务查询与轮询快照接缝 3 例）+ `traces` 表 6（`GetOrCreateTrace` 默认值与固定时钟、thread_id 往返、已存在行原样返回、跨项目 trace_id 唯一与约束重试、失败后 detach 可复用、并发创建单行）+ `threads` 表 5（`GetOrCreateThread` 默认值与固定时钟、已存在行原样返回、跨项目 thread_id 唯一与约束重试、失败后 detach 可复用、并发创建单行） |
| `AxonHub.Grains.Tests` | 92 | 92 | 0 | 0 | 通道选择：打分/窗口 5 + 健康与 topK 6（含 topK 截断与归一化 2）+ 别名解析 3 + 同分顺序/不计数 2 + 负载均衡策略 8（adaptive 中性总分/线性衰减 2、冷却边界 1、权重 tie-break 与未知回退 2、failover 1、circuit-breaker 1、策略切换 1）+ `default_test_model` 解析 4（Trim、`ChooseModel` 映射、`lowercaseModelId`、未知模型保留 Trim 值）；限流与并发准入 64：ChannelLimiter 10 + ChannelLimiterManager 12 + ChannelRequestTracker 24 + RateLimitAwareScorer 18 |
| `AxonHub.Api.Tests` | 949 | 949 | 0 | 0 | Responses WebSocket 编排接缝 5（CustomizeExecutor、finalizeTransportRequest 与中间件委托/透传）；HTTP 切片 87：orchestrator/重试/超时 18（含通道级重试配置 3、流式性能记录语义 2、策略 topK 1、负载均衡策略接线 1、trace 粘性 key 端到端 1）+ 请求链路落库 5（含鉴权身份传播 1、trace id 传播断言）+ SSE 写帧 9（含上游错误策略 2）+ 请求读取 12（含 zstd/压缩错误文案）+ 端点胶水 18（OpenAI 11 含上游错误策略 3，Anthropic Messages 两条路由 7）+ 期望流选择 2 + 通道重试谓词与 settings 解析 16 + 通道性能记录 7；API key 鉴权与 ClientIP 73：header 提取 14 + 鉴权器流程 13 + ClientIP/allowlist 与中间件文案 46；系统设置与 IP 黑名单 29：SecuritySettings 回退/归一化 15 + 黑名单放行/命中/受信代理 14；重试策略系统设置 49：回退/零值/类型错误 16 + 解析与重复键 7 + 归一化 26；上游错误策略 applier 29：非流改写/本地错误/quota 11 + 响应体字段 8 + 流式分类与策略读取次数 10 + 通道级请求改写 134：body/header 改写与设置解析 133（ChannelOverrideTests 87 + ChannelOverrideSettingsTests 47）+ 端到端落库 1；限流中间件 29：RPM 准入 5 + TPM/冷却追踪 13 + 队列错误 4 + 连接准入与让位 7；usage 成本 43：计算器 26 + 价格读取 12 + 注入与落库 5；多 key 选择与执行 key 审计 30：provider 23（含 trace 粘性 7）+ 审计 7 + OpenAI Images 端点接线 8（生成/编辑路由、multipart 透传与错误信封、空响应检测图片分支）+ OpenAI Embeddings/Moderations 端点接线 7（两条路由、嵌入/审核透传、缺省模型回退与错误信封、空响应检测）+ OpenAI Alpha Search/Decisions 端点接线 6（两条路由、原样透传与上游错误信封、空响应检测）+ OpenAI Audio 端点接线 58（speech 路由探测 15：nil/空 body/非 JSON/stream_format 判定表/解码错误文案；strconv.ParseFloat 保真表 36；二进制流写入器 7：首块 CT 与头、done/空块吞掉、headers 前分类错误信封、headers 后只记日志）+ Jina Rerank/Embedding 端点接线 7（`/v1/rerank`、`/jina/v1/rerank`、`/jina/v1/embeddings` 三条路由、校验/解码错误 → 500 api_error 信封、rerank/embedding 转发与空响应检测）+ OpenAI Video 端点接线 33（biz/video_test.go 对照的服务层 23、GET/DELETE 胶水 6、POST /v1/videos 4）+ OpenAI 旧版 Completions 端点接线 4（`POST /v1/completions` 转发、completions 线路形状与 Bearer、缺 prompt 400、空 body 400、非 JSON CT 400）+ TypeSafe SystemOne 端点接线 9（`/v1/systemone` 与 `/typesafe/v1/systemone` 共用 handler、上游 `/v1/systemone` 线路形状与 Bearer、空响应探测保留 answers、空 answers 切换通道、429 换用下一通道 key、401 不重试、缺 model / state 的 500 `api_error`、空 body 400）+ 模型清单与检索 51（xregexp 8：`*`、精确匹配、双向锚定、`(?i)` 修饰符、非法正则恒 false 与缓存、空模式；系统模型设置 15：缺行 / 读失败 / 软删 / 畸形 JSON 回退、缺成员与字面 null 的零值、大小写不敏感键、`developer_settings` 归一化；`ListEnabledModels` 7：facade 字段、前缀 / 映射别名、跨通道去重、黑名单正则与精确匹配、`QueryAllChannelModels=false`、null 支持模型；端点 14：OpenAI 列表 / 空列表 / HTML 转义、尾斜杠 404、斜杠模型 ID、两种 not_found 文案、request_id 回显、500 信封、Anthropic 分页列表 / 空列表 / 500 的 request_id 空与回显；请求 ID 中间件 7（`ar-<uuid>` 生成、响应头与错误体回显、缺失头生成））+ pass-through 通道重放 95（开关与流对齐 17、body 重放与 model 补丁 31、格式支持与策略 12、Codex 头 4、User-Agent 策略 8、通道 settings 解码 11、系统设置 provider 8、编排端到端 4）+ pass-through 响应/流重放 26（非流捕获与应用 7、原始流捕获 6、原始流应用 6、通道流取消语义 2、backlog 预算 2、真实 pipeline 集成 3）+ 提示词保护 71（规则应用 18、中间件 11、规则服务 9、原始补丁 13、真实编解码载荷 13、回放校验 4、编排端到端 3）+ codex/fenno 通道接线 21（7 组 ApiFormat 分派、fenno 拒绝 alpha search、fenno 的 `/v1/responses` 与 bearer、API key/OAuth/旧版 apiKey 三种凭据、oauth 对象优先、两种缺凭据文案、`SupportsCodexResponseHeaders`、ws/wss 与 HTTP 的 transport 推断、会话 id 的 `Session-Id`/`Thread-Id` 透传）+ trace 中间件 38（trace 头 trim 与优先级、extra headers、OpenCode 三头与开关、Claude Code `metadata.user_id` 新旧格式与两条 `messages` 路径、Codex 头 / turn metadata 与三头优先级、body 字段点路径与首命中、无 id 不落库、非消息端点跳过、`GetOrCreateTrace` 幂等与失败开放、`AH-Trace-Id` 响应头、`AH-Session-Id` 上下文、身份 trace id 与 session 传播、body 读取失败 400 信封与回绕）+ thread 中间件 7（默认头建行并发布 id、配置头名、缺失 / 空头、无身份、持久化失败 fail-open、多值取首、thread → trace 链路写入 `thread_id`） |
| **合计** | **2487** | **2487** | **0** | **0** | 四个测试项目全部通过（含 PostgreSQL Testcontainers） |

命令：`dotnet test AxonHub.slnx`（2026-11-01，Windows 11 / .NET SDK 10.0.401）。

本轮验收运行时本机 Docker（29.2.0）可用，`AxonHub.Persistence.Tests` 的 70 例
全部通过；整解决方案并行运行一次通过（本次无 testhost 崩溃），表中数字取自该次运行。
持久化测试通过 Testcontainers 启动 `postgres:17-alpine`
（需要本机 Docker），每个测试使用独立 schema；M2 时期的 SQLite 测试已全部替换为
PostgreSQL 对应项。
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
  `AxonHub.Api`（HTTP 宿主、`POST /v1/chat/completions`、`POST /v1/messages` 与 `POST /anthropic/v1/messages`（Anthropic Messages，含流式）、`POST /v1/responses` 与 `POST /v1/responses/compact`（Responses API，含流式）、`POST /v1/embeddings` 与 `POST /v1/moderations`（OpenAI Embeddings/Moderations）、`POST /v1/alpha/search` 与 `POST /v1/decisions`（OpenAI Alpha Search/Decisions）、`POST /v1/audio/speech`、`POST /v1/audio/transcriptions` 与 `POST /v1/audio/translations`（OpenAI Audio，含 SSE 与二进制流写出）、`POST /v1/rerank`、`POST /jina/v1/rerank` 与 `POST /jina/v1/embeddings`（Jina Rerank/Embedding）、`POST /v1/videos`、`GET /v1/videos/:id` 与 `DELETE /v1/videos/:id`（OpenAI Video 创建、`VideoService` 的任务查询/删除与轮询快照落库、`VideoEndpoint` 胶水）、`GET /v1/models` 与 `GET /v1/models/:model`（`ModelService` 的通道派生清单、`XRegexp` 黑名单语义、`SystemModelSettingsService` 设置读取）与 `GET /anthropic/v1/models`（Anthropic 分页线路形状）、`RequestTracingMiddleware`（`ar-<uuid>` 请求 ID 与响应头 / 上下文回显）、`TraceMiddleware`（trace 头 / extra headers / OpenCode / Claude Code / Codex / body 字段提取、`traces` 落库与 `AH-Trace-Id` 响应头）、`TracingOptions`、`TraceContext` 与 `ThreadContext`、`TraceStore`（`GetOrCreateTrace`）、`ThreadMiddleware`（thread 头 get-or-create 与 `ThreadContext` 发布）、`ThreadStore`（`GetOrCreateThread`）、请求读取、SSE 写出、orchestrator 适配与 `WithInbound` 入站替换接缝、
  API key 鉴权中间件与 header 提取、gin 兼容的 ClientIP 与 allowlist 匹配、`projects` / `users` / `api_keys` / `systems` / `traces` / `threads` 仓储、
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
  响应还原）与统一图片模型 `ImageRequest` / `ImageResponse` / `ImageData`、
  `WebSocketExecutor`（Responses WebSocket 传输：连接池、续传对账、Do 聚合）、
  `ResponsesWebSocketClient`（手写 RFC 6455 握手）与 `HttpTransportExecutor`（HTTP 传输包装）
- `AxonHub.Llm` 的 Embeddings / Moderations 面：`EmbeddingInput` / `EmbeddingRequest` /
  `EmbeddingResponse` / `EmbeddingData` / `Embedding` 与 `ModerationInput` /
  `ModerationMultiModalPart` / `ModerationRequest` / `ModerationClassification` /
  `ModerationResponse`（自定义 JSON 编解码复刻 Go 的 MarshalJSON/UnmarshalJSON）、
  `EmbeddingInboundTransformer` / `ModerationInboundTransformer`（`IInboundTransformer`）与
  `OpenAiOutboundEmbeddingModeration`（出站请求构造与上游响应解码）
- `AxonHub.Llm` 的 Audio 面：`OpenAiAudioInboundTransformer`（Speech / Transcription /
  Translation 三工厂，`IInboundTransformer`）、`OpenAiOutboundAudio`（出站请求构造、
  响应变换与两侧流聚合）、`AudioModels`（音频事件模型与二进制解码器注册表）、
  `GoMultipartReader` / `GoMultipartWriter`（Go `mime/multipart` 语义）、
  `GoCast.TryParseFloat`（strconv.ParseFloat 64 保真：hex 浮点、下划线、inf / nan、范围错误）
- `AxonHub.Llm` 的 Jina 面：`JinaRerankInboundTransformer` / `JinaEmbeddingInboundTransformer`
  （`IInboundTransformer`，500 `api_error` 信封语义）、`JinaOutboundTransformer`（`IOutboundTransformer` +
  `IApiKeyProvenance`，`/rerank` 与 `/embeddings` URL、v1 规范化、jina 错误信封解析）、
  `JinaModels`（wire 模型，usage cost 走 `GoOptionalNumberConverter`）、
  统一模型 `RerankRequest` / `RerankResponse` / `RerankResult` / `RerankDocument`
- `AxonHub.Llm` 的 Video 面：`OpenAiVideoInboundTransformer`（JSON / multipart 分派，
  `IInboundTransformer`）、`OpenAiVideoInboundMultipart`（multipart 解析与上限校验）、
  `OpenAiVideoExtensions`（Go 解码链、extra_body 扫入与合并）、`IVideoTaskOutbound`
  （video 任务查询/删除扩展接口，由 `OpenAiOutboundTransformer` 实现；出站
  create/get/delete 请求构造与响应还原在 `OpenAiOutboundVideo` partial）、
  统一模型 `VideoRequest` / `VideoResponse` / `VideoContent` / `VideoError`
  （含 `hasResponseContent` 的 video 分支）
- `AxonHub.Llm` 的 TypeSafe 面：`SystemOneInboundTransformer`（`IInboundTransformer`）、
  `TypeSafeOutboundTransformer`（`IOutboundTransformer` + `IApiKeyProvenance`、
  `Create` 工厂与 `TypeSafeOutboundConfig`）、统一模型 `SystemOneRequest` /
  `SystemOneQuestion` / `SystemOneAnswer` / `SystemOneResponse`（自定义 JSON 编解码）
  与 `GoAnyJson`（Go `any` 的键排序 / 重复键 / 数字字面量语义）
- `AxonHub.Llm` 的 Go 兼容面：`GoTime`（`time.Time` 的 RFC3339Nano 与 Unix 秒语义，
  供 Anthropic 列表的 `created` 字段使用）
- `AxonHub.Llm` 的 pass-through 接缝：`IPassThroughBodyPolicy`（`AllowPassThroughBody`；
  decisions 恒真、typesafe 仅 `RequestType=SystemOne`）
- `AxonHub.Grains.Abstractions` 的 pass-through 设置：`ChannelPassThroughSettings`
  （通道 `settings.passThroughUserAgent` / `settings.passThroughBody` 的 Go 语义解码）与
  `ChannelCandidate.PassThroughSettings`
- `AxonHub.Api.Orchestration` 的 pass-through 面：`IPassThroughSettingsProvider` /
  `SystemPassThroughSettingsService`（`system_pass_through` / `system_user_agent_pass_through`）、
  `PassThroughSettings`、请求 body / Codex 头 / User-Agent 三个出站中间件与
  `request_executions.pass_through_applied` 落库、响应捕获与回放中间件
  （`PassThroughStreamState`、`RawStreamBacklog` 的 1024 事件 / 8 MiB 预算、
  `PassThroughChannelStream` 的取消排干语义、`PassThroughErrorRef` 的 per-attempt
  错误隔离）
- `AxonHub.Api.PromptProtection` 面：`IPromptProtectionRuleProvider` /
  `PromptProtectionRuleService`（enabled 规则读取）、`PromptProtectionRuleProtector`
  （`IPromptProtecter` + `IPromptProtectionResultProvider` 双接缝）、`PromptProtectionRule` /
  `PromptProtectionSettings` / `PromptProtectionResult` / `PromptProtectionRejectedException`
  与作用域/动作常量、`IPromptProtectionRuleStore`（`prompt_protection_rules` 读侧与单条写入接缝）
- `AxonHub.Llm` 的 Codex 面：`CodexOutboundTransformer`（`IOutboundTransformer`、`CodexOutboundParams`、
  `SupportsCodexResponseHeaders`、`FinalizeTransportRequest`、`AllowPassThroughBody`）、
  `CodexHeaders`（TurnMetadata 与 Session/Thread/Window 头）、`CodexModels`、`CodexToken`
  （`DecodeAuthJson` 与 JWT 账号解析）、`CodexAlphaSearch`、`CodexUtils`、`CodexConstants`
- `AxonHub.Llm` 的 OAuth 面：`OAuthCredentials` / `TokenResponse` / `TokenError`、
  `OAuthCredentialsJson`（`ParseCredentialsJson` / `ParseOAuthObject` / `ParseTokenResponse` /
  `ToJson`）、`TokenProvider` / `TokenProviderParams` / `ITokenGetter` / `ApiKeyTokenProvider` /
  `StaticTokenProvider`、`ExchangeStrategy`（FormEncoded / JSON）、`Pkce`、`DeviceFlowProvider`、
  `SingleFlight<T>`
- `AxonHub.Api.Orchestration` 的 codex 接线：`ChannelOutboundFactory.CodexChannelType` /
  `FennoChannelType` 与 `CreateCodexOutbound` / `CreateCodexTokenProvider`（按尝试构造
  OAuth/API key token provider，`IExecutor` 作为刷新执行器）、
  `PersistentOutboundTransformer.SupportsCodexResponseHeaders`、`ChannelCandidate.DefaultTestModel`
- 签名差异 / 运行时差异 / 平台限制：见 `02-differences.md`（C001–C095）

## 协议锁定

- [x] 关键协议字段名与顺序由测试锁定（`corpus/upstream-request`、`corpus/unified-request`、
  `corpus/client-response`、`corpus/client-stream`；见 `01-baseline.md` 第 2、3 节）
- [x] SSE 写出帧与客户端错误事件由 `SseStreamWriterTests` 逐字节锁定（gin-contrib/sse 兼容）
- [x] 通道级请求改写的应用顺序与落库时机由 134 例锁定（body/header 中间件、Go 模板子集、
  数组与头操作、非 JSON 门槛、设置解析；残余差异 C037–C041 已登记）
- [x] pass-through 通道重放（请求与响应/流半程）由 121 例锁定：通道 `settings.passThroughBody` /
  `passThroughUserAgent` 覆盖全局 `system_pass_through` / `system_user_agent_pass_through`、
  `passThroughStreamAligned` 的 Go 真值相等、原始 body 重放与 sjson 语义的顶层 model 补丁
  （含空白 body 视作 `{}`）、multipart / 图像变体 / transcription / translation 的格式拒绝、
  JSON image_edit 的 Content-Type 同步、Codex Responses 头白名单的先删后加、
  `axonhub/1.0` 默认 UA 与 provider UA 保留、`request_executions.pass_through_applied`
  落库；按参照顺序注册在 override 之前（C091）。响应/流重放由 26 例锁定：原始响应替换、
  双通道扇出与 attach 前 backlog、取消后排干缓冲再关闭上游、上游错误经 per-attempt
  引用传播；提示词保护由 71 例锁定：Messages / Decisions / Compact 三路规则应用、拒绝固定
  文案不泄露规则、legacy protector 的提示词快照回放校验、五种 APIFormat 的原始 body 补丁与
  Gemini 结构化 functionResponse 的键/数字保真、十组真实编解码载荷（C092）；codex 出站策略
  由第 29 节的 61 例锁定（C091 缺口关闭，C094）
- [x] Codex 出站与 OAuth 凭据协议由 135 例锁定：codex 请求/响应/流变换与 identity 头、session id
  优先级、additional tools 作用域、alpha search、pass-through body 的 token 上限拒绝、图片工具、
  `CustomizeExecutor` 的非流聚合/alpha-search/WebSocket 绕过与 `FinalizeTransportRequest`；
  OAuth 凭据校验顺序、token 响应错误信封、单飞刷新/EnsureFresh/自动刷新/设备码/PKCE；
  codex/fenno 通道分派与 API key/OAuth/旧版 apiKey 三种凭据形态（C093–C094）
- [x] 通道限流与并发准入由 93 例锁定：RPM/TPM 自然分钟窗口、硬信号量与 FIFO 队列、
  队列超时/取消、429 Retry-After 冷却、本地拒绝不冷却且不允许同通道重试、
  流关闭先拆上游再让位；RateLimitAware 的四个子分与 -10000 硬耗尽（C035；
  残余 C042–C045 已登记）
- [x] usage 成本由 43 例锁定：四种定价模式与调度匹配（含跨午夜/时区/优先级）、
  写缓存 5m/1h 变体、`system_inject_usage_cost` 门控、CostItem 的 trimmed-decimal
  字符串与省略字段、未命中价格的 `cost_items=null` 语义（C046–C049）
- [x] 通道多 key 选择与执行审计由 30 例锁定：无 trace 随机选择仅限启用 key、
  单 key 静态、全禁用回退、FNV-1a 64 与参照向量一致、rendezvous 的确定性/加删最小重映射、
  `channel_api_key_suffix` 的最后 4 个 rune 与 `channel_api_key_index` 的 1-based 位置
  （含禁用 key 仍计数、共享后缀靠索引区分、每次重试各自审计）；trace 粘性由第 32 节的 7 + 1 例
  锁定（同 trace 跨请求固定、不同 trace 分散，C013 闭合）
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
- [x] Responses API 协议由 315 例锁定：非流式 158 例覆盖入站请求/响应、出站请求/响应与压缩双向映射、
  工具（含 namespace 聚合与 strict schema）、raw 工具/输入项回放、Go 解码文案与 `stream_id` 拒绝、
  usage/cost 的 Go 数字格式与 created_at 兼容表；图片 26 例覆盖 `image_generation` 工具请求构造与
  五类校验、data URL/mask 取值、线路 payload 与 `RequestType` 回写、编辑往返、HTTP ≥400 与解码
  错误文案、codex 图片流聚合；流式 86 例覆盖入站 chunk → `response.*` 事件与错误分类、出站 SSE →
  统一 chunk 与语义终态后的 `[DONE]`、聚合器最终响应（3 组入站 fixture、3 组出站 fixture 与
  2 组聚合 fixture 逐事件/逐字段对比）；WebSocket 45 例覆盖执行器缓存与 `response.create` 帧、
  握手头观察、终态/取消优先级、顶层 error 与官方状态/body、failed/cancelled/incomplete 聚合、
  同会话复用与不池化、显式 previous_response_id 对账、仅新输入/超限/非追加重连、组织头分池、
  失败/取消/提前关闭逐出、trace 头不进入池键、后台清理与 Close、HTTP 包装剥离 websocket beta
  与编排接缝（C061 已闭合；C070–C073 已登记）；OpenAI Images 客户端变换器 71 例覆盖三端点
  JSON/multipart 入站、出站请求与响应还原、multipart 线路形状（C069 已闭合；C074 入站解码边界、
  C075 路由接线由 `ImageEndpointTests` 8 例闭合；C060/C062/C064–C068 已登记）
- [x] OpenAI Embeddings / Moderations 协议由 71 例锁定：嵌入输入的四种形态与审核输入的三形态
  （含 null 零值、空值输出 `null`）、校验顺序与逐字文案、默认审核模型、线路字段与 URL
  （EndpointPath、`#`/`##` raw URL）、上游响应解码与 usage/cost，以及两条路由的端点胶水（C076）
- [x] OpenAI Alpha Search / Decisions 协议由 137 例锁定：原始 body 逐字节透传、只改写顶层
  model（替换/追加/缺键报错）、严格信封校验与 Go 解码文案（结构体类型、根 null、字段大小写
  折叠、重复键、截断输入、首值语法错误优先、尾随数据二段解码）、响应解码与 usage 视图、
  两条路由的端点胶水与上游错误映射；嵌套上限按 Go 的 10000 层锁定（C077；model 补丁的
  HTML 转义字节差异见 C078）
- [x] OpenAI Audio 协议由 102 例锁定：speech 的 JSON 校验链与三形态传输（stream_format
  trim/lower 比较、SSE `audio_bytes` 聚合、二进制块流 + `binary.done`、Accept 头差异）、
  STT 的 multipart 线路形状（file 名、extra 字段、重复 file 拒绝）与 SRT/verbose_json
  透传、translation URL；`GoCast.TryParseFloat` 的 36 例保真表锁定 strconv.ParseFloat
  语义（hex 浮点负偏置、零尾数短路、下划线、inf/nan、范围错误）；`BinaryStreamWriter`
  逐字节锁定原始块写出、首块 CT 与响应头、headers 前的分类错误信封（C079）
- [x] Jina Rerank / Embedding 协议由 88 例锁定：rerank 请求校验链与 500 `api_error` 信封
  （TransformError 只特判 ResponseError，校验/解码错误全走 Internal server error）、
  top_n / return_documents / usage / cost 的 omitempty 线路形状、`/rerank` 与 `/embeddings`
  URL 与 v1 规范化、embedding 的 task 默认 text-matching 与 Cache-Control: no-cache、
  `{"error":{...}}` 信封解析与非 JSON 回退状态文案（C080）
- [x] TypeSafe SystemOne 协议由 65 例锁定：`/v1/systemone` 与 `/typesafe/v1/systemone`
  共用 handler；入站 Decoder 语义（EOF / unexpected EOF / 尾随数据忽略 / 根类型错误）与
  model → state → questions 校验序、`stream:true` 拒绝；出站
  `https://api.typesafe.ai/v1/systemone` 线路形状（state / questions、无 stream 成员）与
  Bearer；`SystemOneAnswer` 的 Extra 保真（未知字段、大小写变体）与无 Extra 的结构序 /
  有 Extra 的 map 序两种形态、`any` 的键排序与 64 位整数字面量保真、`answers:null` 保持
  null；usage 映射；错误信封与 `errors.As` Cause 链；空响应探测的 systemone / compact /
  completion 分支（C087）
- [x] 模型清单与检索协议由 55 例锁定：OpenAI 列表 `{"data":[…],"object":"list"}` 与检索对象
  （`id/object/created/owned_by`，`created` 为 Unix 秒）、Anthropic 分页列表
  （`data/first_id/has_more/last_id/object`，`created` 为 RFC3339Nano）、`model_not_found`
  与 `internal_server_error` 两种错误信封及 `request_id` 回显、Go 的 HTML 转义、
  `/v1/models/` 尾斜杠的 404 以及 `ar-<uuid>` 请求 ID 头（C088–C090）
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
| C013：多 API key 的 trace 粘性 | 已解决：无 trace 时的随机分配、rendezvous/FNV-1a 选择器与 `channel_api_key_suffix`/`channel_api_key_index` 审计列已按参照实现（23 例）；`ChannelApiKeyProvider` 读取 `WithTrace` 发布的 trace id（线上请求 `SessionId` 接缝），多 key 通道按 `traceID|key` 做 rendezvous 固定选择，同一 trace 在重试与跨请求间同一 key，另有 1 例编排端到端锁定；参照的 1024 项 LRU 不单独保留（它只记忆固定启用集合内的 rendezvous 结果，通道重建会新建 provider 与空缓存） | 已关闭；残余 apiKeyOverride（通道 key 测试流程）与 `trace_sticky_mode` 通道粘性未接入 |
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
| C010：非 chat 响应类型的空响应探测与视频端点 | 已解决：embedding 与 moderation 分支由第 17 节闭合（5 例，C076），图片分支由第 16.1 节闭合，decisions / alpha-search 分支由第 18 节闭合（2 例，C077），speech / transcription 分支由第 19 节闭合（5 例，C079），rerank 分支由第 20 节闭合（2 例，C080），video Llm 分支由第 21 节闭合（57 例，C081），video 端点接线（三条路由、biz VideoService 与持久化接缝）由第 22 节闭合（36 例，C082–C085）；doubao / zenmux 视频变换器仍按 C010 裁剪（C082） | 已关闭（doubao / zenmux 视频 outbound 见 C082） |
| C003：字段级 JSON 类型错误文案 | 已解决：入站请求按参照的字段/类型表逐 token 复刻 Go 解码语义，错误文案逐字节一致 | 已关闭（101 例锁定；残余见 C022） |
| C022：int32 index 上限与响应侧 null 元素 | `tool_calls[].index` 超过 int32 时 Go 接受而本实现报解码错误；上游响应中的 null 元素当前抛 500 而非零值 | 真实缺口；协议实际取值很小，待遇到再按 Go 语义补零值替换 |
| 持久化测试依赖 Docker（Testcontainers 启动 `postgres:17-alpine`） | 无 Docker 的环境无法运行持久化测试 | CI 必须提供 Docker；随 M3 补 CI 配置 |
| C050–C058：Anthropic 双向流式、聚合器与出站 | C050 已解决：流式状态机与聚合器按 `inbound_stream.go`/`aggregator.go` 移植，4 组 xtest fixture 逐事件对比，`stream:true` 返回 200 `text/event-stream`；C051 已接受（raw JSON 载体，见登记）；C052 已接受：多条非法工具调用按 index 升序报告（参照 map 迭代随机）；C053 已接受：聚合器跳过负 index（参照 panic）；C054 已接受：无效工具输入在聚合早期抛 `InvalidOperationException`（参照在 `json.Marshal` 时失败，死 jsonrepair 省略）；C055 已接受：省略从未赋值的 `pendingReasoningItemID` 死字段；C056 真实缺口：Vertex AI 出站执行器（OAuth/JWT/区域 URL）未移植，构造期显式拒绝，Bedrock 仅保留直连报文形状；C057 已接受：缺失 `delta.type` 时跳过而非 panic；C058 已接受：流错误事件用严格 JSON 解析，畸形输入的兜底文案退化为 `stream_error` | C050 已关闭（测试锁定）；C056 为后续里程碑的显式缺口（不会发出错误请求）；C052–C055、C057、C058 仅影响畸形输入或内部错误文案 |
| C076：OpenAI Embeddings / Moderations | 已解决：按 `embedding.go`、`moderation.go`、`embedding_inbound.go`、`moderation_inbound.go` 移植统一模型、自定义 JSON 编解码、双向变换与两条路由，59 例变换器测试 + 5 例空响应探测 + 7 例端点接线锁定；校验顺序与文案、URL 构造（EndpointPath/raw URL）与默认审核模型逐字对齐 | 已关闭（测试锁定） |
| C077：OpenAI Alpha Search / Decisions | 已解决：按 `alpha_search_inbound.go`、`alpha_search_outbound.go` 与 `decisions/{inbound,outbound}.go` 移植双向变换与两条路由；原始 body 只改写顶层 model、其余字节原样保留；严格信封校验与 Go 解码文案逐字对齐；嵌套上限 10000 层。19 + 110 例变换器测试、2 例空响应探测与 6 例端点接线锁定 | 已关闭（测试锁定）；model 补丁的 HTML 转义字节差异见 C078 |
| C078：model 补丁的 HTML 字符转义 | 已接受：sjson 的快速路径对纯可打印 ASCII 字符串原样写入，端口用 Go `json.Marshal` 会把 <、>、& 写成 Unicode 转义序列；JSON 语义相同，仅字节表示不同，混合非 ASCII 时两侧一致（Go 探针确认） | 已接受（1 例锁定差异面；逐字节对齐需实现 sjson 的 mustMarshalString 判定） |
| C079：OpenAI Audio 切片的已接受差异 | 已接受（7 项，登记于台账）：(1) `GoCast.TryParseFloat` 完整复刻 strconv.ParseFloat 64（hex 负偏置、零尾数短路、下划线、范围错误），36 例保真表锁定；(2) Go omitempty 丢空字符串，端口以可空字符串 + 边界置 null 复刻线上形状；(3) multipart 急切读取：截断 body 文案与 Go `mime/multipart` 措辞不同，参照的逐 part 读取错误路径不可达，出站字段按插入序而非 Go map 随机序（参照本身不确定，逐字节对齐本就不可能）；(4) 流事件字段级类型错误用 System.Text.Json 文案（前缀保持 Go 风格）；(5) transcription 流事件 logprobs 在解码期压缩（线上不可观察）；(6) walker 的 FitsFloat64 改用 `GoCast.TryParseFloat`，`1e400` 等 ErrRange 字面量报类型错误；(7) .NET `Stream.Read` 无 Go 的 (0, nil) 分支，解码器的 0 读重试循环删除 | 已接受（102 例锁定音频切片；仅(2)为结构可见差异，其余为内部措辞或不可达路径） |
| C080：Jina Rerank/Embedding 切片的已接受差异 | 已接受（4 项，登记于台账）：(1) 出站 TransformResponse 对 nil Request 空安全回退 rerank 解码（Go 会 panic，参照 HttpExecutor 总会附带 Request，实际不可达）；(2) wire 模型缺省字符串（task / encoding_format / user）在统一模型中归一化为 null（Go 保留空串），线上输出一致（空 task 默认 text-matching、空 encoding_format / user 省略）；(3) 字段级解码错误用 System.Text.Json 文案（前缀保持 Go 风格，同 C064）；relevance_score / embedding 的 double 沿用 C067 的数字格式缺口；(4) 参照的 capability 表（Rerank→{jina/rerank}、Embedding→{…jina/embedding…}）在端口中未建模，按通道类型 + 入站 ApiFormat 分派（既有架构，video 切片同样继承） | 已接受（88 例锁定 jina 切片；(1)(2)为不可观察/语义等价差异，(3)(4)为既有登记项延续） |
| C081：OpenAI Video Llm 切片的已接受差异 | 已接受（2 项，登记于台账）：(1) 参照的「failed to read multipart field」/「failed to read multipart file」逐 part 读取错误折叠为 NextPart 级「failed to read multipart」（端口的 `GoMultipartReader` 急切缓冲整段 part，截断在 NextPart 暴露，语义等价）；(2) ParseGetVideoTaskResponse 将缺省的 model / prompt / size / video_url 在统一模型上归一化为 null（Go 内存中保留空串），序列化输出一致（Go omitempty 丢弃空串，延续 C079/C080 的可空字符串约定） | 已接受（57 例锁定 video Llm 切片；两项均为不可观察或语义等价差异） |
| C082–C085：OpenAI Video 端点接线的已接受差异 | 已接受（4 项，登记于台账）：(1) C082 参照的 `ch.Outbounds[videoKey]` 映射由 `ChannelOutboundFactory` 按通道类型重建，仅 openai 通道承载 openai/video——doubao 回退（seedance/video）与持久化的 seedance/zenmux 格式报「channel does not support video task operations」，zenmux 的 delete-unsupported 本地取消分支不可达（doubao/zenmux 视频变换器为 C010 裁剪）；(2) C083 `videoTaskProjectID` 的缺省 project 报错在端口不可达，无鉴权直调回退 `RequestIdentity.Default`（project 1）；(3) C084 同 project 下多条 external_id 命中时 ent Only 的 NotSingularError 与 Dapper QuerySingleOrDefault 的异常文案不同，均为 500 信封；(4) C085 `video.Video` 为 nil 时端口抛 `InvalidOperationException`（500 信封）而非 Go 的 nil 解引用 panic | 已接受（36 例锁定 video 端点切片；(1) 为 C010 裁剪的直接后果并由 doubao 回退用例显式锁定，其余为不可达或数据完整性边缘路径） |
| C087：TypeSafe SystemOne 切片的已接受差异 | 已接受（4 项，登记于台账）：(1) 参照的 `AllowPassThroughBody` 对 SystemOne 恒为 true，开启 pass-through_body 时重放客户端原始 body（只写回映射后的 model）；端口已按 C091 接入请求与响应/流半程（额外顶层字段保留，原始响应/流按开关替换，26 例锁定），提示词保护补丁由 C092 接入（71 例锁定）；(2) 参照按通道端点/能力表选择出站并拒绝不支持的请求类型，端口按通道类型分派（与 C080 item 4、C086 同源）；(3) `state` / `instructions` / `criteria` / `legend` / `probabilities` 以 `JsonElement` 承载，`GoAnyJson` 复刻键排序、重复键与数字字面量，仅内部构造时需提供 `JsonElement`；(4) `SystemOneAnswer` 的字段级解码错误为端口自述的 Go 风格文案（缺 `Alias.<field>` 路径，同 C064/C079 的 STJ 差异） | 已接受（56 例 Llm + 9 例端点锁定正常往返；pass-through 请求半程由 C091 的 95 例覆盖，通道可用性判定与非 JSON 输入文案仍为既定差异） |
| C091：pass-through 通道重放 | 请求与响应/流半程已解决：`isPassThroughEnabled` 的通道/全局开关继承、`passThroughStreamAligned` 的 Go 真值相等、body 重放与 sjson 语义的 model 补丁（空白 body 视作 `{}`）、格式支持（multipart / image_variation / transcription / translation 拒绝、JSON image_edit 允许）、Content-Type 同步、Codex Responses 头白名单、User-Agent 策略、`PassThroughApplied` 复位与 `request_executions.pass_through_applied` 落库（95 例）；响应/流半程按 `captureRawProviderResponse/Stream` 与 `applyPassThroughResponse/Stream` 移植，含双通道扇出（容量 64）、attach 前 backlog（1024 事件 / 8 MiB，含 Type/LastEventID 计入）、per-attempt 错误引用、取消后排干缓冲再关闭上游、`NextChannel`/`PrepareForRetry` 复位，并按参照顺序注册（26 例）；`pass_through_prompt_protection.go` 的补丁/校验由 C092 接入（71 例），codex 出站的 `AllowPassThroughBody` 由第 29 节的 codex 用例锁定（C094 item 10 登记 gjson 探测差异）；另有三项已接受差异：每请求设置快照替代参照的逐中间件惰性读取、RawRequest 缺失时保守跳过替代参照 panic、通道 settings 畸形 JSON 降级为继承（C037 家族） | 重放与参照逐分支一致（121 例锁定：请求 95 + 响应/流 26）；关闭开关时行为不变，开启时下游拿到原始响应/流；codex 策略已由 C094 锁定 |
| C092：pass-through 提示词保护 | 请求侧已解决：`protectPrompts` 入站中间件（Messages / Decisions / Compact 三路规则应用、拒绝固定文案、legacy protector 的提示词快照回放校验）、五种 APIFormat 的原始 body 补丁（含 Gemini `functionResponse.response` 的 UseNumber 递归掩码）与 `PassThroughRequestBodyMiddleware` 的合并/校验回退按参照移植（71 例）；真实缺口限定在管理面与对照范围：无 30 秒 live 缓存（每请求读 `prompt_protection_rules`，失败开放）、规则 CRUD/GraphQL/预览未移植、.NET Regex 与 regexp2 尚未逐特性对照、Gemini/Ollama 无变换器导致载荷级对照延后 | 请求侧 71 例锁定；规则读取即时且失败开放不改变可观察行为，管理面随 C013/C014 补齐，Gemini/Ollama 变换器落地时补载荷对照 |
| C093–C094：OAuth 凭据与 Codex 通道 | C093 已接受：`expires_at` / `last_refresh` 用 `DateTimeOffset.TryParse` 宽松解析（参照为严格 RFC3339/RFC3339Nano）、JSON 解码错误文案来自 System.Text.Json、OnRefreshed 失败无日志接缝、单飞用锁 + TaskCompletionSource 替代 `x/sync/singleflight`、`ParseOAuthObject` 延迟解码（C013 延续）；C094 已接受 + 真实缺口：codex/fenno 分派、三种凭据形态、镜像主模型、transport 推断、pass-through 策略与传输收尾已锁定，真实缺口为端点表未建模（图片/alpha-search 按通道类型与入站格式分派）、OAuth `TokenProvider` 每次尝试新建（刷新单飞不跨请求共享）、刷新不写回数据库、无后台自动刷新、刷新复用共享 executor 而非按通道代理 client、API 层 codex 响应头未转发、`DecodeAuthJson` 的消费方未移植 | 已接受差异只影响畸形输入/日志/资源结构；真实缺口在管理面与通道生命周期切片补齐，期间 OAuth 凭据仍按需刷新，API key 通道不受影响 |
| C088：模型列表的 `models` 表与 API Key profile | 已接受：参照的 `ListEnabledModels` 先合并配置的 `models` 实体、再按 `QueryAllChannelModels` 并入通道模型，并用 profile（ChannelIDs / ChannelTags / ModelIDs）过滤、privacy 拒绝；端口没有 `models` 表与 profile，列表恒为通道派生模型，`include` 扩展字段永不出现。没有 `models` 行、未配置 profile 时两条实现的响应字节一致（第 25 节 55 例；`QueryAllChannelModels=false` 返回空列表的差异由用例显式锁定） | 已接受；配置模型来源随管理面 / 权限切片补上 |
| C089：请求 ID 与 trace ID 中间件 | 已解决：`RequestTracingMiddleware` 的 `ar-<uuid>` 请求 ID、`AH-Request-Id` 响应头与错误体 `request_id` 回显（7 例）；`TraceMiddleware` 按参照实现请求头 → extra headers → OpenCode → Claude Code `metadata.user_id` → Codex 头 / turn metadata → body 字段的提取链、非消息端点跳过、`GetOrCreateTrace` 的 `(trace_id, project_id)` 语义与唯一约束重试、响应的 `AH-Trace-Id` 头与 `AH-Session-Id` 上下文；trace id 经 `RequestIdentity` 落到 `requests.trace_id`，并以 session id 传给通道出站（42 例 Api + 6 例 Persistence + 12 例 Llm） | 已关闭；残余差异见 C095 |
| C095：trace 子系统的残余差异 | 已解决半程：thread 中间件与 `threads` 表已按参照移植（`AH-Thread-Id` 首值、按项目 get-or-create、fail-open、`traces.thread_id` 落库，7 + 5 例），trace 粘性 API key 已接入（C013，7 + 1 例）；真实缺口：trace 查询 / 归档 / GC / GraphQL 与管理面未移植、claudecode / opencode 出站 user id / 会话头生成未移植（C010）；已接受：无 slog 日志与 operation name、gjson 数字归一化（`1.50` → `1.5`、`1e3` → `1000`）与路径语法差异、约束错误按 SQLSTATE 23 分类（参照按 Ent 约束名）、`EnableBuffering` 回绕与参照 `io.ReadAll` + 新 reader 的资源差异 | 提取 / 落库 / 回显 / thread / 粘性 key 行为由 80 例锁定（Llm 12、Api 57、Persistence 11）；缺口随管理面与通道生命周期切片补齐 |
| C059–C073：Responses API 切片 | C059 已解决：图片请求/响应路径与图片流式聚合按 `image_request.go` / `image_response.go` 移植，26 例锁定，出站遇 `RequestType=image` 改走 `image_generation` 工具请求并在响应侧还原统一 `ImageResponse`；C069 已解决：客户端 OpenAI Images 入站/出站变换器按 `image_inbound.go` / `image_outbound.go` 移植，71 例锁定（JSON 与 multipart 入站、上限与错误文案、三端点出站构造、multipart 线路形状、响应还原与模型回显）；C074 已接受：base64 空白容忍、非 UTF-8 multipart 文本、重复 part 头与畸形 multipart 错误文案与 Go 不同；C075 已解决：`/v1/images/generations|edits` 按 routes.go:215-216 接线（共享 chat 处理器、multipart 透传、错误信封），并补齐 `hasResponseContent` 的图片分支，8 例端点测试锁定（参照停用 variations 路由）；C060 已接受：`stream_id` 用严格 `JsonDocument` 探测，畸形 body 的结果与 gjson 不同；C061 已解决：WebSocket 传输按 `websocket_executor.go` / `http_executor.go` 移植（执行器、连接池、续传对账、手写 RFC 6455 握手、Do 聚合、HTTP 包装），45 例锁定；C062 已接受：会话 ID 与作用域经 `SessionIdProvider` / `SessionScopeProvider` 接缝注入（trace 子系统见第 30/32 节与 C089）；C063 已解决：入站/出站流状态机与聚合器按参照实现，86 例锁定（fixture 回环、终止事件、错误分类）；C064 已接受：字段级解码文案为 System.Text.Json 文本；C065 已接受：strict `required` 补齐为文档序（参照 map 随机序）；C066 已接受：工具 schema 保留数字词法（参照经 float64 重编码）；C067 真实缺口：cost 之外的 double 仍按 STJ 数字格式输出；C068 已接受：并行工具收尾顺序由 Go map 随机序改为插入序，响应元数据经 JSON 字符串/对象往返；C070 已接受：手写握手不继承代理环境变量与 HTTP 执行器 TLS，拨号错误文案为近似；C071 已接受：WebSocket payload/事件以 `JsonElement` 保真往返，键序与数字词法与 Go 的 map+float64 重编码不同但语义一致；C072 真实缺口：通道端点配置尚未选择 websocket，编排层尚未注入会话/作用域，`Stop` 无调用方，生产仍走 HTTP；C073 已接受：`toWebSocketURL` 用 System.Uri 归一化，丢弃 userinfo/fragment | 非流式 158 例 + 图片 26 例 + 流式 86 例 + WebSocket 45 例 + OpenAI Images 71 例锁定文本/工具/压缩/图片/流事件/WebSocket/Images 行为；OpenAI Images 端点已接线（C075 闭合），WebSocket 生产接线仍未完成（C072）；极端量级浮点与 WebSocket JSON 字节序不同但语义等价 | 后续里程碑补 WebSocket 生产接线（C072）；C067 随全局 double 序列化工厂闭合 |
| C024–C027：鉴权子切片的已接受差异 | noauth key 指向已删除项目时参照触发唯一键冲突返回 500，本实现返回 401 且不改动该行（C024）；owner 选择在参照中无 `ORDER BY`，多 owner 时顺序不定（C025）；无 API key/项目缓存，每次请求读库（C026）；配置载体为 ASP.NET Core 配置而非 YAML 结构体（C027） | 已接受，测试锁定 401 行为（C024/C025），C026/C027 为性能与载体差异 |
| C028–C029：系统安全设置与 IP 黑名单 | C028 已解决：`systems` 表、`SecuritySettingsOrDefault` 的回退/归一化与 `/v1` 黑名单中间件按参照实现，403 信封逐字节一致；C029 已接受：系统设置无进程内缓存，每个 `/v1` 请求（有 ClientIP 时）读一次库，外部改库立即可见 | 已关闭；缓存随配置/缓存里程碑（C029） |
| C030–C033：重试策略与上游错误策略 | C030 已解决：`retry_policy` 的读取/归一化与每请求 topK 已接入（49 例 + 3 例），禁用时 1 个候选，策略计数大于默认值时能打满候选；C031 已解决：`upstream_error_policy` 的 hidden/custom 按参照应用到非流与流式错误（含"非流传输错误不被改写"的真实不对称），34 例锁定；C032 已接受：重复键后跟 `null` 时 .NET 与 Go 解码语义不同；C033 已接受：错误体解析用 System.Text.Json 而非 gjson，截断报文与重复键的结果不同 | C030/C031 已关闭；C032/C033 仅手改库或畸形上游报文可触发 |
| 其余 Ent 表未移植（data_storages / prompts / roles 等） | 依赖这些表的功能不可用 | 随功能落地 |
