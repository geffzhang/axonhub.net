# 验收账（第三本账）

验收是体检报告，不是"我感觉挺好"。

测量时间：2026-11-01（M2 Llm 切片 + M4 持久化层与请求链路落库 + M3 通道选择与 HTTP 切片，含超时、非法 UTF-8 与字段级类型错误路径；C014 鉴权子切片新增 API key 中间件、ClientIP/allowlist 与 `projects` / `users` / `api_keys` 三表；C028 新增 `systems` 表、系统安全设置与 `/v1` IP 黑名单；C030 新增 `retry_policy` 读取、归一化与每请求 topK；C031 新增 `upstream_error_policy` 的应用与流式错误改写；C012 新增四条负载均衡策略链，残余 C034/C036；通道级请求改写新增 body/header 覆盖、Go 模板子集与设置解析，残余 C037–C041；C035 新增通道限流与并发准入：RPM/TPM 自然分钟窗口、并发硬信号量与 FIFO 队列、429 Retry-After 冷却、RateLimitAware 打分接线，残余 C042–C045；C046–C049 新增 usage 成本计算与注入：flat_fee / usage_per_unit / usage_tiered / usage_volume 四种定价模式与调度匹配、`channel_model_prices` 表与读取接缝、`system_inject_usage_cost` 门控、`usage_logs` 成本列与响应 `usage.cost`；C013 新增无 trace 的多 key 随机分配与执行 key 审计：`ChannelApiKeyProvider`、rendezvous/FNV-1a 选择器、`channel_api_key_suffix` 与 `channel_api_key_index`；Anthropic Messages 入站（非流式）新增请求/响应转换、Go 解码文案、错误信封与 `POST /v1/messages` + `POST /anthropic/v1/messages` 两条路由（127 例 Llm + 7 例 API）；C050 新增 Anthropic 入站流式状态机与聚合器：SSE 事件序列、Read 工具参数缓冲、citations/thinking 事件、usage 合并与 4 组 xtest fixture（Llm 由 272 增至 300，C050 闭合，残余 C052–C055；C051 仍为已接受差异）；C056–C058 新增 Anthropic Messages 出站：请求/响应转换与校验、工具/tool_choice、SSE 出站流式（含 server_tool_use 与 web_search 内联结果）、cache_control 断点优化器（Llm 由 300 增至 367；残余 C056 Vertex 执行器缺口、C057 缺失 delta.type 防御、C058 gjson 宽松解析差异））。本次追加 C059–C067：Responses API 非流式双向变换（入站/出站/压缩、usage 与 created_at 兼容、`GoJsonFloat` 的 Go 数字格式、`POST /v1/responses` 与 `POST /v1/responses/compact` 路由），Llm 由 367 增至 529；C038（Compact.PromptCacheKey 回退）随之闭合，跳过归零。本次追加 C068：Responses API 流式状态机与聚合（`TransformStream` 入站/出站、两侧 `AggregateStreamChunks`、16 个 fixture 回环、错误分类与终止事件语义），移除 2 例旧的「流式未移植」断言并新增 86 例，Llm 由 529 增至 613，C063 闭合。本次追加 C059：Responses 图片请求/响应路径（统一 ImageRequest/ImageResponse/ImageData、image_generation 工具请求构造、线路 RequestType 回写与图片响应还原、codex 图片流聚合），移除 2 例旧的「图片未移植」断言并新增 26 例，Llm 由 613 增至 637，C059 闭合；C061（WebSocket 传输）仍为缺口，客户端 OpenAI Images 变换器登记为 C069。本次追加 C061：Responses WebSocket 传输（执行器与连接池、续传对账、手写 RFC 6455 握手、Do 聚合、HTTP 传输包装与编排接缝），Llm 由 637 增至 677（37 + 3），Api 由 495 增至 500（接缝 5），C061 闭合；拨号、JSON 往返、URL 归一化与生产接线分别登记为 C070–C073。本次追加 C069：OpenAI Images 客户端变换器（客户端 `/v1/images/generations|edits|variations` 的 JSON 与 multipart 入站解码、出站请求构造与响应还原、共享 GoMultipart/DetectContentType/MimeTypes 基础设施），参照 69 个测试函数逐条移植为 71 例，Llm 由 677 增至 748，C069 闭合；入站解码边界登记为 C074。本次追加 C075：OpenAI Images 端点接线（`POST /v1/images/generations` 与 `/v1/images/edits` 两条路由、multipart 透传、错误信封与空响应检测的 `resp.Image` 分支），Api 由 500 增至 508，C075 闭合；variations 路由按参照保持停用。本次追加 C076：OpenAI Embeddings / Moderations 客户端变换器与 `POST /v1/embeddings`、`POST /v1/moderations` 两条路由（统一模型与 Go JSON 编解码、入站/出站变换、`hasResponseContent` 的 moderation/embedding 分支），参照 16 个测试函数逐条移植为 59 例 + 空响应探测 5 例 + 端点接线 7 例，Llm 由 748 增至 812，Api 由 508 增至 515，C076 闭合；C010 的 embedding/moderation 分支随之闭合。本次追加 C077：OpenAI Alpha Search / Decisions 客户端变换器与 `POST /v1/alpha/search`、`POST /v1/decisions` 两条路由（原始 body 透传、sjson 语义的顶层 model 补丁、严格信封校验、`hasResponseContent` 的 alpha-search/decisions 分支），参照 alpha_search 测试与 decisions 的 5 个测试文件逐条移植为 19 + 110 例、空响应探测 2 例与端点接线 6 例，Llm 由 812 增至 943，Api 由 515 增至 521，C077 闭合；C010 的 decisions/alpha-search 分支随之闭合，model 补丁的 HTML 转义字节差异登记为 C078。本次追加 C079：OpenAI Audio 客户端变换器与三条 `/v1/audio/*` 路由（speech TTS 的三种传输形态——非流式二进制、SSE `audio_bytes` 流、二进制块流，STT 的 multipart/JSON 与 SRT/verbose_json 透传，`GoCast.TryParseFloat` 的 strconv.ParseFloat 保真，`BinaryStreamWriter` 原始字节写出），参照 `audio_inbound_test.go` / `audio_outbound_test.go` 的 15 个测试函数逐条移植为 39 例、空响应探测 5 例与端点接线 58 例，Llm 由 943 增至 987，Api 由 521 增至 579，C079 登记，C010 的 speech / transcription 分支随之闭合，仅剩 rerank / video。本次追加 C080：Jina Rerank / Embedding 客户端变换器与 `/v1/rerank`、`/jina/v1/rerank`、`/jina/v1/embeddings` 三条路由（rerank 入站校验链与 500 `api_error` 信封、embedding 入站的 Content-Type 校验与 Cache-Control: no-cache、出站 rerank/embedding 线路形状与 usage 映射、`jina` 通道类型分派），参照 `rerank_inbound_test.go` / `embedding_test.go` 等测试逐条移植为 79 例、空响应探测 2 例与端点接线 7 例，Llm 由 987 增至 1068，Api 由 579 增至 586，C080 登记，C010 的 rerank 分支随之闭合，非 chat 响应类型仅剩 video。本次追加 C081：OpenAI Video 客户端变换器 Llm 层（`POST /v1/videos` 的 JSON 与 multipart 入站解码、extra_body 扫入与合并、统一 VideoRequest/VideoResponse 与出站 create/get/delete 转换、`hasResponseContent` 的 video 分支），参照 `video_inbound_test.go` / `video_outbound_test.go` 的 11 个测试函数逐条移植为 57 例（video 变换器 56、空响应探测 1），Llm 由 1068 增至 1125，C081 登记；C010 的 video Llm 分支随之闭合，仅剩 video 端点接线（routes + VideoService + 持久化）。本次追加 C082–C085：OpenAI Video 端点接线（`POST /v1/videos` 复用 chat handler，`GET/DELETE /v1/videos/:id` 走 biz VideoService 的任务查询/删除流程，`VideoEndpoint` 与 `RequestIdentity.FromHttpContext`，请求/执行两表的查询与轮询快照持久化接缝），参照 `video_test.go` 的 7 个测试函数与 api/openai.go 的三个 handler 移植为 33 例 Api + 3 例持久化，Api 由 586 增至 619，Persistence 由 56 增至 59，C010 的 video 端点接线随之闭合（doubao / zenmux 视频变换器仍为既定裁剪）。本次追加 C086：OpenAI 旧版 Completions 客户端变换器与 `POST /v1/completions` 路由（入站 `CompletionRequest` 解码与校验链、`Completion != nil` 的响应/错误信封、`[DONE]` 与 usage-only 流块过滤、聚合器按 Go 字段序还原 `llm.Response`、出站 `##` RawURL/版本归一与 Bearer、`openai` 通道按入站 ApiFormat 选中 completions 出站），参照 `completion_inbound.go` / `completion_outbound.go` / `completion_aggregator.go` 与 inbound/outbound 测试逐条移植为 65 例 Llm + 4 例端点接线，Llm 由 1125 增至 1190，Api 由 619 增至 623，C086 登记（通道能力表未建模，出站按通道类型 + 入站 ApiFormat 分派）。本次追加 C087：TypeSafe SystemOne 客户端变换器与 `POST /v1/systemone`、`POST /typesafe/v1/systemone` 两条路由（统一模型与 `SystemOneAnswer` 的自定义 JSON / Extra 保真、Go `any` 的键序与数字字面量、入站 Decoder 语义与校验序、出站线路形状与错误信封、`hasResponseContent` 的 systemone / compact / completion 分支、`typesafe` 通道分派），参照 `typesafe/{model,inbound,outbound}.go`、`llm/systemone.go` 与 orchestrator 的 4 个测试函数逐条移植为 56 例 Llm + 9 例端点接线，Llm 由 1190 增至 1246，Api 由 623 增至 632，C087 登记（pass-through body 未移植、通道能力表未建模、`JsonElement` 承载 `any` 与字段级解码文案）；`/jina/v1` 与 `/typesafe/v1` 一并进入鉴权 / IP 黑名单分支。本次追加 C088–C090：模型清单与检索端点（`GET /v1/models`、`GET /v1/models/*model` 与 `GET /anthropic/v1/models` 三条路由、`XRegexp` 的黑名单匹配语义、系统模型设置的 Go 语义解码、`ModelService.ListEnabledModels` 的通道派生模型、请求 ID 中间件及其在错误体中的回显、OpenAI / Anthropic 两种列表与错误信封的逐字节线路形状），新增 55 例（`GoTime` 7 + `XRegexp` 8 + 系统模型设置 15 + `ModelService` 7 + 端点 14 + 请求 ID 中间件 4），Llm 由 1246 增至 1253，Api 由 632 增至 680，C088（`models` 表与 API Key profile 裁剪）、C089（trace ID 链路未移植）、C090（路由边界与顺序确定化）登记。本次追加 C091：pass-through 通道重放的请求半程（`isPassThroughEnabled` 的通道/全局开关继承与流对齐、body 重放与 sjson 语义的 model 补丁、Content-Type 同步、Codex 响应头白名单、User-Agent 策略、`pass_through_applied` 落库，以及 `IPassThroughBodyPolicy` 的 decisions 恒真 / typesafe 仅 SystemOne），新增 95 例（`PassThroughTests` 72、通道 settings 解码 11、系统设置 provider 8、编排端到端 4），Api 由 680 增至 775，Llm 保持 1253，C091 登记（取代 C087 中 pass-through body 缺口的描述并修正 C014 的开关表述）。本次追加 C091 的响应/流半程：`captureRawProviderResponse/Stream` 与 `applyPassThroughResponse/Stream` 四个中间件、双通道扇出（容量 64）与 attach 前 backlog（1024 事件 / 8 MiB，含 Type 与 LastEventID）、per-attempt 错误引用、取消后排干缓冲再关闭上游、`NextChannel` / `PrepareForRetry` 复位扇出，新增 26 例（`PassThroughStreamTests`：非流响应 7、原始流捕获 6、原始流应用 6、通道流取消语义 2、backlog 预算 2、真实 pipeline 集成 3），Api 由 775 增至 801，Llm 保持 1253，C091 更新为请求与响应/流半程均已接入、codex 策略仍为缺口。本次追加 C092：pass-through 提示词保护（`protectPrompts` 入站中间件、Messages / Decisions / Compact 三路规则应用与拒绝文案、`IPromptProtecter` 与 `IPromptProtectionResultProvider` 双接缝、legacy protector 的提示词快照回放校验、五种 APIFormat 的原始 body 文本补丁与 Gemini `functionResponse.response` 的 UseNumber 递归掩码、`PassThroughRequestBodyMiddleware` 的掩码合并与校验回退、`prompt_protection_rules` 读取接缝与失败开放），并修正 `ApiFormats.OpenAIResponseCompact` 的字面量为参照的 `openai/responses_compact`，新增 71 例（规则应用 18、中间件 11、规则服务 9、原始补丁 13、真实编解码载荷 13、回放校验 4、编排端到端 3），Api 由 801 增至 872，Llm 保持 1253，Grains 保持 88，合计 2213；C092 登记（每请求读库替代 30 秒 live 缓存、管理面/GraphQL/预览未移植、.NET Regex 与 regexp2 语义差异、Gemini/Ollama 载荷级对照延后），C091 中的提示词保护缺口关闭，codex `AllowPassThroughBody` 仍为缺口。本次追加 C093–C094：OAuth 凭据与令牌链路（凭据/令牌响应解析校验顺序、单飞刷新、EnsureFresh 与自动刷新、FormEncoded/JSON 交换策略、PKCE、设备码）与 codex 出站及通道接线（`buildCodexOutbound` 的 OAuth/API key 两种 token provider、旧版 apiKey 内嵌 OAuth、`default_test_model` 经 `ChooseModel` 解析、ws/wss transport 推断、pass-through body 策略与 `SupportsCodexResponseHeaders` 接缝），新增 111 例 Llm（codex 61 + OAuth 50）、20 例 API 接线与 4 例 Grains，Llm 由 1253 增至 1364，Api 由 872 增至 892，Grains 由 88 增至 92，合计 2407；C091 的 codex `AllowPassThroughBody` 缺口关闭，C094 登记端点表、token provider 复用/持久化/自动刷新、代理感知 client 与 API 层 codex 响应头转发的真实缺口。本次追加 C089 的 trace 子系统基础：`TracingOptions`（三条默认头、extra headers/body 字段、三个提取器开关）与 `TraceContext`/`ThreadContext`，`traces` 表与 `TraceStore.GetOrCreateTrace` 的 `(trace_id, project_id)` 语义与唯一约束重试，`TraceMiddleware` 的请求头 → extra headers → OpenCode → Claude Code `metadata.user_id` → Codex 头/turn metadata → body 字段提取链、非消息端点跳过与 400 错误信封，trace id 经 `RequestIdentity` 落到 `requests.trace_id` 并以 session id 传给通道出站，响应的 `AH-Trace-Id` 头与 `AH-Session-Id` 上下文；新增 60 例（Llm 12：ClaudeCode 用户 id 解析 5 + OpenCode 会话头 7；Api 42：trace 中间件 38 + 请求 ID 新增 3 + codex 会话接线 1；Persistence 6：traces 表），Llm 由 1364 增至 1376，Api 由 892 增至 934，Persistence 由 59 增至 65，合计 2467；C089 闭合，C013/C062 的 trace 相关成因更新，残余差距登记为 C095（thread 中间件、trace 查询/GC/GraphQL、trace 粘性 key、日志与 gjson 语义）。本次追加 C095 的 thread 半程：`ThreadMiddleware`（配置头首值、按项目 get-or-create、无头 / 无身份 / 持久化失败 fail-open、`ThreadContext` 发布线程行 id）、`ThreadStore.GetOrCreateThread` 的 `(thread_id, project_id)` 语义与唯一约束重试、`threads` 表（6 列、3 个索引）与 EF 模型，`Program` 在 `/v1` 族分支按参照顺序挂载 `ThreadMiddleware` → `TraceMiddleware`；新增 12 例（Api 7 + Persistence 5），Api 由 934 增至 941，Persistence 由 65 增至 70，合计 2479；C095 的 thread 缺口关闭，残余为 trace 查询/GC/GraphQL、trace 粘性 key、claudecode/opencode 出站与日志接缝。本次追加 C013 的 trace 粘性 API key：`ChannelApiKeyProvider` 读取 `WithTrace` 发布的 trace id（线上请求 `SessionId` 接缝），多 key 通道按 `traceID|key` 做 rendezvous 选择，同一 trace 在重试与跨请求间固定同一 key；新增 8 例（Api 7 + 编排端到端 1），Api 由 941 增至 949，合计 2487；C013 闭合（残余 apiKeyOverride 与 trace_sticky_mode 通道粘性），C095 的 trace 粘性 key 项关闭。本次追加 C096：claudecode / DeepSeek / OpenCode 出站与通道接线（`ClaudeCodeOutboundTransformer` 的 UA 捕获与清理、`proxy_` 前缀、系统消息与伪造 user id 注入、13 个头与 `beta=true`、响应条件剥离与流式逐行去前缀、`AggregateStreamChunks` 的 `tool_use` 过滤、`RemoveBillingSystemMessages`，`ClaudeCodeUserID` 的 legacy / v2 解析与 SHA-256 设备 id / UUID v5 生成，`DeepSeekOutboundTransformer` 的 URL 归一、`/beta/completions` 分派、`json_schema` 降级与 `thinking` / `reasoning_content`，`OpenCodeOutboundTransformer` 的模型路由与会话头三级回退，`WithSessionHeader` 的两种包裹形态，以及 `ChannelOutboundFactory` 的 claudecode / opencode 三种通道分支），新增 154 例（Llm 143：ClaudeCode 工具与前缀 55 + 出站 23 + 用户 id 生成 7 + DeepSeek 20 + OpenCode 38；Api 11：通道接线），Llm 由 1376 增至 1519，Api 由 949 增至 960，合计 2641；C095 第 (7) 项关闭，C096 登记计费消息中间件、opencode 用户端点、`Stop` 转发、执行 key 审计与凭据文案的残余差异。本次追加 C097：Ollama 原生出站与 NDJSON 流（`OllamaOutboundTransformer` 的 `/api/chat` 线格式、`stream` 恒显式、`options`、`images` 的 data-URL 剥离、工具与 `tool_calls` 的 Go map 排序往返、`thinking` → `reasoning_content`、finish reason 优先级、`ollama_error` 与聚合；`HttpNdjsonEventStream` 与 `HttpExecutor.DoStream` 的 NDJSON 路由；`ChannelOutboundFactory` 的 `ollama` / `ollama_anthropic` 分支与无启用 key 时不发送认证；`modelname.FromEvent` 补齐 OpenAICompletion / OllamaChat 分派），新增 63 例（Llm 52：Ollama 出站 42 + NDJSON 10；Api 11：通道接线 7 + 流式模型名 4），Llm 由 1519 增至 1571，Api 由 960 增至 971，合计 2704；C097 登记取消语义、数字字面量、arguments 宽容度、STJ 文案、能力/端点表、apiKeyOverride 与其余 provider 通道的残余差异。本次追加第 35 节的 DeepSeek 通道接线：`ChannelOutboundFactory` 的 `deepseek` 分支复用第 33 节的出站（URL 归一、`/beta/completions` 与 `thinking` 保持不变），新增 4 例 Api 接线，Api 由 971 增至 975，合计 2708；无新增差异条目。本次追加第 36 节的 Fireworks / Moonshot：`FireworksOutboundTransformer`（默认 `https://api.fireworks.ai/inference/v1`、单尾部斜杠裁剪、`ReasoningFieldContent`、返回普通 OpenAI 出站）与 `MoonshotOutboundTransformer`（内嵌 OpenAI 出站、chat-only 请求路径、messages 必填、`json_schema` 降级为 `json_object` 并清空 schema、`NormalizeBaseURL(raw, "v1")`、Bearer，其余方法委托），以及 `ChannelOutboundFactory` 的 `fireworks` / `moonshot` 分支；新增 25 例（Llm 19：Fireworks 7 + Moonshot 12；Api 6：两种通道接线各 3），Llm 由 1571 增至 1590，Api 由 975 增至 981，合计 2733；C098 登记 nil 配置异常化、嵌套 `ArgumentException` 参数名后缀、能力/端点表缺口与注释冲突的取舍。本次追加第 37 节的 Longcat：`LongcatOutboundTransformer`（内嵌 OpenAI 出站、调用方消息内容补空、请求体按 `openai.Request` 反序列化后以 `longcat.Request` 语义重排——根 `messages` 与每条消息的 `content` 末位、字符串/空内容包成单元素 text 数组、多分片原样保留）与 `ChannelOutboundFactory` 的 `longcat` 分支；新增 10 例（Llm 8 + Api 2），Llm 由 1590 增至 1598，Api 由 981 增至 983，合计 2743；C099 登记 nil 配置异常化、嵌套 `ArgumentException` 参数名后缀、不可达的缺 content 防御分支与端点表缺口。本次追加第 38 节的 xAI / Cline：`XaiOutboundTransformer`（`https://api.x.ai/v1` 默认与 config 就地变更、内嵌 OpenAI 出站的 `reasoning_content`、Grok 三类模型的 penalties/stop 与 `reasoning_effort` 裁剪、`IsValidResponse` 空 delta 过滤与 `FilterStream`）与 `ClineOutboundTransformer` / `ClineModels` / `ClineErrors`（`reasoning` 字段、`X-Client-Type: cline-cli`、user/system/developer 空内容补丁与参照的统一索引错位、`{success,data,error,errors}` 信封与 502 错误映射、流内错误事件、`clineChunkTransform` 聚合），以及 `ChannelOutboundFactory` 的 `xai`（responses / compact 分派到 Responses 出站）与 `cline` 分支；新增 59 例（Llm 51：xAI 27 + Cline 24；Api 8：两种通道接线），Llm 由 1598 增至 1649，Api 由 983 增至 991，合计 2802；C100 登记 nil 配置异常化、`ArgumentException` 参数名后缀、JsonNode 与 sjson 的字节差异与索引补齐、STJ 与 Go 的解码文案 / `any` 语义、`ReasoningEffort` 空串与 null 的不可观察差异、`IsValidResponse` 的公开测试面与端点表缺口。本次追加第 39 节的 OpenRouter / Cerebras / nanoGPT：`OpenRouterOutboundTransformer`（内嵌 OpenAI 出站的 `ReasoningFieldReasoning`、构造包装、单尾部斜杠裁剪的独立 BaseURL、chat 与 audio 委托及 compact 拒绝、`/images` 图片请求的 16 条引用上限 / mask 拒绝 / seed 透传 / 魔数嗅探 data URL、图片响应的 `img-<created>` 与 `chat.completion` 与 model 元数据与首个可用 OutputFormat、`metadata.raw` > message > `http.StatusText` 的错误信封与恒字符串 code）、`CerebrasOutboundTransformer`（默认 `https://api.cerebras.ai/v1`、nil 配置与缺 provider 文案、空 URL 回退、包装、`store` 浅拷贝剥离、其余委托 OpenRouter）、`NanogptOutboundTransformer` / `NanogptModels` / `NanogptXmlToolCalls`（状态诊断 `{"error": string}` > 原始 body > 裸状态码、reasoning → reasoning_content、XML 工具调用的归一化 / 正则抽取 / 稳定排序 / Go 键序与数字格式 / `nanogpt_` 确定性 ID、全消费零 MessageContent、`[DONE]` 过滤与 DoneResponse、流内错误与聚合），以及 `ChannelOutboundFactory` 的 `openrouter` / `cerebras` / `nanogpt` / `nanogpt_responses` 分支；新增 106 例（Llm 96：OpenRouter 39 + nanoGPT 16 + XML 33 + Cerebras 8；Api 10：四类通道接线），Llm 由 1649 增至 1745，Api 由 991 增至 1001，合计 2908；C101 登记 nil 配置异常化与 `(Parameter 'config')` 后缀、能力 / 端点表缺口、STJ 解码文案、XML 正则等价与贪婪回溯、零 MessageContent、浅拷贝语义、图片字段缺省、reasoning 方向与 key 溯源。本次追加第 40 节的 Zai / Zhipu / Xiaomi / MiniMax：`ZaiOutboundTransformer`（内嵌 OpenAI 出站的 `ReasoningFieldContent`、nil 构造与包装、`##` 全 URL 与 Version `v4`/`v1` 的 URL 三分支、GLM 版本正则与 ≥5.2 原生 reasoning_effort、glm-5.3 恒思考与 effort 归并、`none`/`minimal` 映射、`user_id` 6-128 rune 截断与 `request_id` 会话回退、`json_schema` 降级、`tool_choice` 强制 auto、Metadata 置空、Go 字段序的 `user_id`/`request_id`/`thinking` 追加）、`ZaiImage`（排序的 `map[string]any` 请求体、quality 映射、图片响应 `zai-img-<uuid>` 与 `chat.completion`、`Request.Metadata` 的 model 回退、data URL 下载）、`MinimaxOutboundTransformer`（构造校验、`/image_generation` 请求的 model 缺省与尺寸规则、`response_format` 映射、业务错误信封、url→base64 顺序与 created/id 回退、chat 全部委托与 SelectedApiKey 透传），以及 `ChannelOutboundFactory` 的 `zai` / `zhipu` / `xiaomi` / `zai_anthropic` / `zhipu_anthropic` / `xiaomi_anthropic` / `minimax` / `minimax_anthropic` 分支；新增 122 例（Llm 101：Zai 59 + Zai 图片 11 + MiniMax 31；Api 21：八类通道接线），Llm 由 1745 增至 1846，Api 由 1001 增至 1022，合计 3030；C102 登记 nil 配置异常化与 `(Parameter 'config')` 后缀、端点默认集合折叠、STJ 解码文案、Go map 排序与 `%q`、共享 HttpClient、UUID 与 rune 语义、以及参照的 TransformerMetadata/Metadata 字段错位。第 41 节新增 Bailian 出站（连续 assistant tool_calls 的合并与浅拷贝语义、`reasoning_effort=none` 清空 effort 后以 `RawJsonPatch` 写 `enable_thinking=false`、其余方法委托内嵌 OpenAI 出站）、`BailianStreamFilter`（tool call 后缓存文本并在 finish 前释放、重复 `{}` 参数抑制、Done 引用透传）与 `bailian` / `bailian_responses` / `bailian_anthropic` / `moonshot_coding` 四类通道接线；新增 52 例（Llm 43：Bailian 34 + 流过滤 9；Api 9），Llm 由 1846 增至 1889，Api 由 1022 增至 1031，合计 3082；C103 登记 nil 配置异常化与 `(Parameter 'config')` 后缀、`RawJsonPatch` 替代 sjson、Go 值拷贝与 `ReferenceEquals` 的 Done 语义、Go White_Space 表及端点默认集合折叠。第 42 节新增 ModelScope 异步图片任务出站（chat 委托内嵌 OpenAI 出站并清空 Metadata；图片请求的 ApiFormat / prompt / 输入图 data URL / mask 校验、排序请求体与异步头、任务轮询的 2s/15m 预算与调用方取消、三种结果分支、output_images 两步解码、URL 下载转 base64 与响应缺省）与 `ChannelOutboundFactory` 的 `modelscope` 分支，另新增 `GoUrl.PathEscape`、`GoStrings.TrimSpace` 与 `HttpRequestData.RequestCancellationToken` 接缝；新增 48 例（Llm 44 + Api 4），Llm 由 1889 增至 1933，Api 由 1031 增至 1035，合计 3130；C104 登记 nil 配置异常化与 `(Parameter 'config')` 后缀、无 context 的调用方 token 接缝、注入执行器与静态回退的状态处理、Go PathEscape 与 TrimSpace 复刻、输出图片解码语义、无按通道代理 client 与端点默认集合折叠。本次追加第 43 节的 Doubao / Volcengine：`DoubaoOutboundTransformer`（v3 URL 归一与 `##` 原始 URL、`user_id` 6–128 rune 截断与 `request_id` 会话回退、`doubao.Request` 的字段追加序、图片生成的排序 map 请求体与 `guidance_scale`、多模态嵌入、Seedance 任务 create/get/delete 与状态归一）、`DoubaoVideoInboundTransformer`（创建请求解码与 Go 解码文案、client 侧 `{id}` 与任务响应字段序）与 `doubao` / `volcengine` / `doubao_anthropic` / `volcengine_anthropic` 四类通道接线，以及 `/doubao/v3` 的任务查询/删除端点；新增 92 例 Llm + 11 例通道接线 + 5 例端点，Llm 由 1933 增至 2025，Api 由 1035 增至 1051，合计 3238；C105 登记（按通道代理 client 与端点表缺口沿用 C013/C014），C082 的 doubao 项闭合、仅剩 zenmux，C010 标记 resolved。本次追加第 44 节的 ZenMux：`ZenmuxOutboundTransformer`（配置与构造校验、`EndpointPath` 缺省 `/videos` 与前导斜杠、`##` 原始 URL 只剥离不归一、内嵌 OpenAI chat 出站与 `SelectedApiKey` 回退、原生 create/poll/delete、`extra_body` 仅在 TrimSpace 非空时合并并按排序键重编码且原生字段覆盖、`service_tier` / `execution_expires_after` 拒绝、size 推断表、字段声明序与指针 bool 的线路形状）与 `ZenmuxVideoResponse`（create/poll 的两步 content 解码、缺 id 与未知状态用原始未 trim 字符串的文案、model 经请求 metadata 回退与统一 VideoResponse），四类 ZenMux 的默认 base URL（openai / responses / video → `https://zenmux.ai/api/v1`，anthropic → `https://zenmux.ai/api/anthropic`）与 `zenmux/video` 在四类的原生分派、`zenmux_video` 主出站，以及 `VideoService` 的 zenmux/video 执行格式解析与 delete-unsupported 的本地取消；新增 62 例 Llm + 18 例通道接线 + 7 例 VideoService，Llm 由 2025 增至 2087，Api 由 1051 增至 1076，合计 3325；C106 登记（nil 配置异常化、默认集合折叠的端点表、Go 排序 map 与 `%q` 复刻、共享 HttpClient、key 溯源双源回退与 zenmux/video 端点门控残余），C082 的 zenmux 项闭合（`zenmux_gemini` 随 Gemini/Vertex 切片），C010 维持 resolved。本次追加第 45 节的 GitHub Copilot：`CopilotOutboundTransformer`（chat / Responses 双路径、七个 Copilot 头与视觉头、initiator 推断与覆盖、`{"response": ...}` 解包、四类流事件 call-id 修复与聚合分派）、`CopilotTokenProvider` / `CopilotTokenExchanger`（GitHub 设备流常量、SHA-256 缓存键、单飞与 5 分钟提前过期缓冲）与 `github_copilot` 通道接线（OAuth 必经准入、typed OAuth 省略 IdToken 重编码、legacy apiKey 非空要求与四条错误文案）；新增 110 + 9 + 3 例 Llm 与 9 例 Api，Llm 由 2087 增至 2209，Api 由 1076 增至 1085，合计 3325 增至 3456；C107 登记（`%p` 单飞键近似、缺执行器文案、gjson/STJ 解析差异、自动刷新与端点表残余），C010 的 github_copilot 分支闭合。本次追加第 46 节的 Gemini OpenAI：`GeminiOpenAiOutboundTransformer`（OpenAI 兼容端点与 `/v1beta/openai` 归一、extra_body 的 google.thinking_config 与 reasoning_effort 双向映射、Google 原生工具过滤与 tool_choice 复位、thought signature 按 ID/下标回填与消息级回退）、`GeminiOpenAiModels`（ThinkingBudget 双态与 Go 结构解码语义的 ParseExtraBody）、`GoogleToolFilter` 与 `gemini_openai` 通道接线；新增 89 + 19 例 Llm 与 6 例 Api，Llm 由 2209 增至 2317，Api 由 1085 增至 1091，合计 3456 增至 3570；C108 登记（JsonElement 承载 extra_body、两行 slog 与 nil 配置防御、raw body 的 UTF-8 替换、STJ 的路径后缀）。本次追加第 48 节的管理面 GraphQL 基础设施：HotChocolate.AspNetCore 16.8.0-p.9、`/admin/graphql`、`allScopes` 与内省放行，新增 6 例 Api，合计 3570 增至 3576；C109 登记（引擎替换、错误信封与鉴权缺口）。本次追加第 49 节的 Gemini 原生基础切片：`GeminiModels`（GenerateContent 全模型与 Tool 的 camel/snake 双键解码、Cost 三态解析）、`GeminiConvert`（内容/角色/完成原因/media part/工具选择/thinking/usage 双向与 TransformerMetadata 提取）、`GeminiMime`（Go 1.26 内建表冻结的 MIME 推断）、thought signature 与 `GeminiOutboundConfig` 的清理语义；新增 69 例 Llm，合计 3576 增至 3645；C110 登记（MIME 表冻结、ErrorDetail 重命名、最小 url.Parse/path.Ext 复刻、类型断言 → 反序列化 + JsonException 捕获）。本次追加第 50 节的 Gemini 原生出站转换切片：`GeminiOutboundConvert`（请求的 maxOutputTokens/thinking 预算/response_format/safetySettings/ImageConfig、tools 门控、消息与工具结果转换、响应与 finish reason/grounding/thought signature 回填、UUID 生成与 JSON schema 剥壳），并修正 ImageConfig 的非空 string 模型与 VideoUrl/VideoURL 类型统一；新增 150 例 Llm，合计 3645 增至 3795；C111 登记（UUID 随机性、grounding 的 JsonElement 存储、response testdata 3 例随入站切片）。本次追加第 51 节的 Gemini 原生出站变换器切片：`GeminiOutboundTransformer`（TransformRequest 的 embedding/image/chat 分派与 compact 拒绝、`BuildFullRequestUrl` 的端点覆盖/流式动作/版本回退/Vertex 三分支、`TransformResponse` 的嵌入/图片/错误状态/空体/解码分派、`TransformError` 信封与 SetAPIKey/SetBaseURL）、embedding 单/批请求与响应还原、image 请求构造/响应还原/错误/尺寸映射与 MIME 嗅探、vertex 常量；新增 71 例 Llm，合计 3795 增至 3866；C112 登记（RouteValues 接缝与死分支 `gemini-api-version`、embedding 恒设 auth、JSON null 与空串映射、nullable provider 防御、流式占位随后续切片）。本次追加第 52 节的 Gemini 原生出站流式与聚合切片：`GeminiOutboundStream` / `GeminiAggregator`（`TransformStream` 的 DONE 哨兵与稳定 `chatcmpl-` UUID、`TransformStreamChunk` 的流式转换与 toolCallIndex 累积、聚合器按 candidate.Index 合成与 buildGeminiResponse 部件序、四组 gemini-*.stream.jsonl 夹具回放）与 gemini / zenmux_gemini / gemini_vertex 通道接线（ZenMux 默认 base、顶点 URL 形状、无 key 拒绝）；新增 64 例 Llm + 6 例 Api，Llm 由 2607 增至 2671，Api 由 1097 增至 1103，合计 3866 增至 3936；C113 登记（`chatcmpl-` UUID 形态、MapStream null 跳过、候选空槽收缩、JsonElement args、死字段未迁移、Vertex 清 ID null、STJ chunk 错误文案）。本次追加 C114–C117：Gemini 原生入站转换与流式切片（`GeminiInboundConvert` 的 LLM→Gemini 请求 / Gemini→LLM 响应双向与请求模式分派、thinking/grounding/thought signature 映射与 TransformerMetadata 提取，`GeminiInboundTransformer` / `GeminiInboundStream` 的 TransformRequest/TransformError 与按选择索引的 tool-call 累积、reasoning-signature 缓冲、六夹具回放、双向集成，`GeminiEndpoint` 的 `/v1beta` 与 `/gemini/:version` 两条原生路由与 ListModels，以及 Gemini key 鉴权中间件），新增 147 例 Llm（入站转换 102 + 入站流式与聚合 25 + 双向集成 19 + parametersJsonSchema 1）与 12 例 Api（key 鉴权 6 + 端点 6），Llm 由 2671 增至 2818，Api 由 1103 增至 1115，合计 3936 增至 4095；C114（JSON Schema 归一化差异）、C115（超时/来源/响应头中间件省略）、C116（未匹配路由的框架默认 404 体）、C117（入站解码错误文案）登记。本次追加 C118–C120：Antigravity Llm 层切片（`AntigravityRouter` 的配额偏好与模型归一、`AntigravityExecutor` 的 Daily→Autopush→Prod 端点回退与 per-model 冷却、`AntigravityHealthTracker`、`AntigravityVersion` 三级回退、`AntigravityTokenProvider`、`AntigravityEnvelope`、`AntigravitySanitizer` 四阶段消毒与 UPPERCASE 类型、`AntigravityTransformer` 的 OAuth-only 出站与流式解包），参照 antigravity 包 6 个测试文件逐条移植为 129 例（router 47 + version 21 + health 12 + executor 22 + sanitizer 19 + transformer 8），Llm 由 2818 增至 2947，合计 4095 增至 4224；C118（响应信封反序列化的错误文案）、C119（默认传输 100s 超时 vs Go 无总超时）、C120（Antigravity 切片 slog 日志省略）登记。本次追加 C121–C123：Antigravity 通道接线（ChannelOutboundFactory 的 antigravity 分支：legacy APIKey 直接消费、enabled key 列表不满足、OAuth-only 信封变换器与错误包装文案链），新增 5 例 Api 接线，Api 由 1115 增至 1120，合计 4224 增至 4229；C121（OAuth 管理面端点与状态缓存）、C122（模型拉取分支）、C123（provider quota checker）登记为真实缺口，C094 补记 antigravity 刷新写回分支。本次追加 OpenaiResponses 通道接线（ChannelOutboundFactory 的 openai_responses 分支：单端点 Responses 出站与 ws/wss 传输推断），新增 4 例 Api 接线，Api 由 1120 增至 1124，合计 4229 增至 4233；无新增差异条目（端点记录与 ValidateEndpoints 的 websocket/compact 拒绝沿用 C082/C094，配额采集沿用 C123，模型拉取沿用 C088）。本次追加 Commandcode 通道接线（ChannelOutboundFactory 的 commandcode / commandcode_anthropic 分支：普通 OpenAI chat 出站与 Anthropic PlatformCommandCode 出站、HTTPS 基址校验），新增 12 例 Api 接线，Api 由 1124 增至 1136，合计 4233 增至 4245；C124（WithRejectHTTPSDowngrade 未移植与 URL 解析严格性）登记为真实缺口，C014 补记通道 key 测试流。本次追加别名通道类型接线（ChannelOutboundFactory 的 19 个通道类型分支：11 个 OpenAI 兼容别名共用普通 OpenAI 出站、7 个 anthropic 变体按平台出站（Direct 三变体 X-API-Key、LongCat Bearer、DeepSeek/Moonshot X-API-Key、anthropic_aws Bedrock URL 信封）、xai_responses 单端点 Responses 出站无传输推断），新增 40 例 Api 接线，Api 由 1136 增至 1176，合计 4245 增至 4285；C125–C127（anthropic_gcp、anthropic_fake / openai_fake、xai_subscription 未移植）登记为真实缺口。本次追加 xai_subscription 通道与订阅 Llm 子包（§60：constants / token / sso / sso_device / plan / billing / outbound 七个源文件，ChannelOutboundFactory 的 OAuth-only 校验、凭据解析与 Grok CLI 端点出站），参照 20 个用例逐条移植并补 22 例 Llm 与 9 例 Api 接线，Llm 由 2947 增至 2989，Api 由 1176 增至 1185，合计 4285 增至 4336；C127 由「真实缺口」改写为「已解决（残余：自动刷新与 OnRefreshed 持久化回写 C094 同族、管理面宿主 C014、provider quota 采集 C123 同族）」。本次追加 anthropic_gcp 通道与 Vertex 执行器（§61：`VertexExecutor` 的请求改写与 `GcpAuthHandler`、`GcpCredentials` / `GcpTokenSource` 的 service_account JWT-bearer 与 authorized_user refresh 两条令牌链及 reuse 缓存、`VertexTransformer` 与 `ChannelOutboundFactory` 的 `anthropic_gcp` 分支），参照 vertex 执行器、anthropic vertex 变换器与 oauth2/google 包逐条移植为 11 + 7 + 13 例 Llm 与 4 例 Api 接线，Llm 由 2989 增至 3020，Api 由 1185 增至 1189，合计 4336 增至 4371；C125 闭合（残余差异登记为 C128），C056 收窄为 Bedrock AWS EventStream 流式解码缺口。本次追加静态托管与 SPA fallback（§62：`StaticHandler` 的三分支分派、API 前缀 JSON 404 信封、资产文件/纯文本 404、SPA index 与 no-cache 头、`MapFallback("{**path}")` 接线与 `Server:Static:Root`、回退端点的 `StaticFallbackMetadata`），参照 `embed.go` / `embed_test.go` 移植为 26 例 Api，Api 由 1189 增至 1215，合计 4371 增至 4397；C116 由「已接受」改写为「已解决」（原记录误判参照 NoRoute 为 gin 默认纯文本 404），C129 登记 go:embed → 磁盘根、缺失 index 的目录列表与条件请求/嗅探差异。本次追加第 63 节的管理面 system-settings 切片（SystemSettingsTypes / AdminMutation、四个查询与三个变更、严格读取与自动禁用规则校验、ISystemStore upsert、HotChocolate 类型与 Any 标量），新增 68 例 Api 与 3 例 Persistence，Api 由 1215 增至 1283，Persistence 由 70 增至 73，合计 4397 增至 4468；C109 更新为覆盖 system-settings 命名，C130–C133 登记。本次追加第 64 节的管理面 users / roles 切片（users / roles 查询与 7 个变更、gid / Cursor / Time 标量、entgql 分页与嵌套角色、hex(bcrypt cost 10) 密码哈希、roles / user_roles / user_projects 三表与键集分页存储），新增 31 例 Api 与 17 例 Persistence，Api 由 1283 增至 1314，Persistence 由 73 增至 90，合计 4468 增至 4516；C014 补记权限校验器与管理员 GraphQL 鉴权仍缺，C134–C138 登记。本次追加第 65 节的管理面 channels 读取切片（channels / queryChannels / allChannelSummarys / allChannelTags / countChannelsByType、entgql 键集分页与 string / int 游标、Channel 字段解析器、settings / credentials / policies 解码与前端实时契约扩展、Dapper 读取与四条聚合 SQL），新增 18 例 Api 与 4 例 Persistence，Api 由 1314 增至 1332，Persistence 由 90 增至 94，合计 4516 增至 4538；C014 补记 channels 读取面已接入，C134（游标 ValueKind）与 C135（ChannelWhereInput）更新，C139–C141 登记。本次追加第 66 节的管理面 channels 生命周期写入切片（updateChannelStatus / deleteChannel / bulkArchiveChannels / bulkDisableChannels / bulkEnableChannels / bulkRecoverChannels / bulkDeleteChannels / bulkAddChannelTags / bulkRemoveChannelTags / bulkManageChannelTags 十个变更、ChannelWriteStore 的 EF 写侧与软删除 / 批量状态 / 批量标签事务、ChannelLifecycleService 的参照错误前缀与限流器失效），新增 14 例 Api 与 9 例 Persistence，Api 由 1332 增至 1346，Persistence 由 94 增至 103，合计 4538 增至 4561；C014 补记通道生命周期写入已接入、createChannel / updateChannel 与管理员鉴权仍缺，C142 登记（entgql Transactioner 改为存储层单方法事务）。本次追加第 67 节的管理面 channels createChannel 写入切片（写入顺序：命令码 cookie 规范化 → xai_subscription 基址覆盖 → 设置与端点校验 → ent 默认值；`CreateChannelCommand` / `ChannelWriteSerialization` / `ChannelValidation`、`ChannelWriteStore.CreateAsync` 与 `ChannelLifecycleService.CreateAsync`），新增 14 例 Api 与 4 例 Persistence，Api 由 1346 增至 1360，Persistence 由 103 增至 107，合计 4561 增至 4579；C014 补记 createChannel 已接入、updateChannel 与管理员鉴权仍缺，C139 补记写侧消费 live 字段，C143–C145 登记。本次追加第 68 节的管理面 channels updateChannel 写入切片（部分列更新：身份快照与 guard、命令码 / xai 归一化、活行同名检查、supportedModels 协议裁剪、设置 / 协议 / 端点校验、孤立 quota 清理、zenmux 凭据保留与顶点校验、单条 ExecuteUpdate 与 UpdatedAtEQ 守卫；`ChannelUpdateRecord`、`ChannelWriteStore` 的三个新方法、`ChannelLifecycleService.UpdateAsync` 与 `updateChannel` 变更），新增 16 例 Api 与 8 例 Persistence，Api 由 1360 增至 1376，Persistence 由 107 增至 115，合计 4579 增至 4603；C014 补记 updateChannel 已接入、其余通道写操作与管理员鉴权仍缺，C139 补记写侧 updateChannel 同步消费 live 字段，C143 补记 updateChannel 同样未调用 ensureChannelModelPrices，C144 补记 updateChannel 的 live 校验器相对位置，C146 登记（单次快照读取与投影返回）。本次追加 §69：管理面 channels 批量写入（bulkUpdateChannelOrdering / bulkUpdateChannelAutoDisable，排序事务、自动禁用动作校验与归一化、计数前置），新增 13 例 Api 与 7 例 Persistence，Api 由 1376 增至 1389，Persistence 由 115 增至 122，合计 4603 增至 4623；C014 补记批量排序与批量自动禁用已接入、其余通道写操作与管理员鉴权仍缺，C147 登记（自动禁用写入直接编辑 policies JSON 对象的保留边界）。本次追加 §70：管理面 channels saveChannelEndpoints（读取活行、按类型与已存 modelProtocols 校验、xai_subscription 拒绝、只写 endpoints 列并以 updated_at 守卫），新增 10 例 Api 与 4 例 Persistence，Api 由 1389 增至 1399，Persistence 由 122 增至 126，合计 4623 增至 4637；C014 补记端点保存已接入，C144 / C146 补记冻结校验与单快照守卫写入，C148 登记（live 端点规范化与 settings 重写延后）。本次追加 §73：管理面 channels 定价写入（`saveChannelModelPrices` 的重复 / 校验、既有价格读取、create/update/skip/delete 计划、reference id 轮换、`channel_model_price_versions` 的 active/archived 版本行、通道 `updated_at` 触碰与单事务回滚，定价输出 / 输入类型与 `PriceMapping`，`ChannelModelPriceService` 与 `ChannelModelPriceStore.ApplyAsync`），新增 16 例端点、45 例校验 / 相等性与 7 例持久层，Api 由 1452 增至 1513，Persistence 由 130 增至 137，合计 4694 增至 4762；C048 写入面闭合（剩自动定价与读取边），C143 补记写入接缝已具备，C151 登记（缓存刷新、Decimal 标量文本、DateOnly 年 0 与软删实现）。本次追加 §74：通道自动定价（`models` 表与 ModelRecord、`EnsureAutoPricesAsync` 的去空去重 / 500 分批 / 模型卡换算 / `ON CONFLICT` 与候选 reference id 胜负判定 / active 版本、`ChannelWriteStore` 的 create/update 事务内回调、createChannel / updateChannel 接线与 `ModelCardPricing` 的 Go 浮点线格式），新增 5 例持久层 ensure、4 例持久层事务回调、7 例模型卡换算与 4 例端点接线，Api 由 1513 增至 1524，Persistence 由 137 增至 146，合计 4762 增至 4782；C143 闭合（create / update 事务内补价并整体回滚），C048 收窄为读取边与缓存，C088 补记 `models` 表已建，C152 登记（模型卡解码的错误文案、非有限值与 decimal 精度边界）。本次追加 §80：管理面登录（`POST /admin/auth/signin` 与 `POST /admin/auth/refresh` 的 JWT 会话：30/90/7 天窗口、手写 HS256/384/512 编解码、凭据认证与 OIDC-only 守卫、登录 UserInfo 线形与刷新三态；内部文案与邮箱正则近似登记为 C166），新增 65 例 Api 与 1 例持久化，Api 由 1778 增至 1843，Persistence 由 177 增至 178，合计 5067 增至 5133；C014 更新为登录 / 刷新已接入，OIDC 提供方配置、管理员 GraphQL 鉴权与权限校验仍缺。本次追加 §81：管理面 GraphQL 鉴权（WithAdminGraphqlAuth / WithProjectID / service_account 只读：bearer 提取、JWT 优先与 API key 回退的 401/403/500 分支、类型先于 allowed_ips、X-Project-ID 三条规则、变更拒绝与查询放行、POST-only 路由；只读判定在解析后、校验前的近似登记为 C167），新增 27 例 Api，Api 由 1843 增至 1870，Persistence 保持 178，合计 5133 增至 5160；C014 收窄为 OIDC 提供方配置与权限校验仍缺，C109 的第 (5) 项收窄为请求超时，C161 改写为 HttpContext 身份接缝。本次追加 §87：API key 管理切片（apiKeys 查询、node(APIKey) 与八个变更 createAPIKey / updateAPIKey / updateAPIKeyStatus / updateAPIKeyProfiles / rotateAPIKey / bulkDisableAPIKeys / bulkEnableAPIKeys / bulkArchiveAPIKeys；`IApiKeyStore` 的 EF 写 + Dapper 读、`ApiKeyManagementService` 的参照排序与逐字文案、`ApiKeyProfilesCodec` 的 Go JSON 与模板脱钩、APIKey 输出类型与 `Decimal` 标量），新增 30 例 Api（服务 19 + 端点 11）并重写持久化存储用例 9 例（净 +5），Api 由 2147 增至 2177，Persistence 由 195 增至 200，合计 5454 增至 5489；C176 登记（事务边界、Decimal 精度、缓存 no-op、枚举未知值、where 子集、延迟读边与边语义），C165 更新（node(APIKey) 已接入）。本次追加 §88：模型管理切片（models / queryModels / queryModelChannelConnections / queryUnassociatedChannels / fetchModels、node(Model) 与八个变更 createModel / bulkCreateModels / updateModel / deleteModel / updateModelStatus / bulkArchiveModels / bulkDisableModels / bulkEnableModels / bulkDeleteModels；`IModelStore` 的 EF 写 + Dapper 读、`ModelManagementService` 的参照排序与逐字文案、`ModelPrivacy` 与关联规则匹配、Model 输出类型与 jsonb 默认值），新增 25 例 Api（服务 18 + 端点 7）与 7 例持久化存储用例，Api 由 2177 增至 2202，Persistence 由 200 增至 207，合计 5489 增至 5521；C177 登记（事务边界、where 子集、空集合谓词、顺序确定化、枚举未知值、显式隐私与公开列表合并缺口），C165 更新（node(Model) 已接入），C088 收窄为公开模型列表的 configured-model 合并 / profile 过滤。本次追加 §89：公开模型列表切片——ListEnabledModels 的 configured-model 合并（enabled Model 行 + 设置继承 + 关联匹配）、项目/API key profile 的 ChannelIDs / ChannelTags / ModelIDs 过滤、HideUnroutableModelsInList 的端点能力抑制与 suppressed ID 不被通道派生项复活、ModelBlacklistRegex（仅通道派生项）、QueryAllChannelModels=false 的配置模型直返；OpenAI /v1/models 与 /v1/models/{model} 的 include 解析（默认值分别取系统设置与硬编码 false）、扩展字段转换与 configured 行回退、ModelIDIn 全量查询；OpenAI / Anthropic / Gemini 三个列表端点的 403 信封；45 例 Api（38 + 27 + 7 中新增），Api 由 2202 增至 2247，Persistence 保持 207（ModelStoreTests 增补 ModelIdIn 断言），Llm 3020、Grains 92，合计 5521 增至 5566，0 失败 0 跳过；C178 登记（实时读通道、无 outbound 构建跳过、通道派生稳定排序、畸形 profile JSON 失败关闭），C088 标记 resolved。本次追加 §90：orchestrator 模型关联路由与多模型重试切片——DefaultSelector 的模型行查询（enabled + read_channels privacy）、设置继承后的六种关联匹配与排除、When 条件、按 (channel, priority) 聚合与 (channel, actualModel) 去重、逐模型强制 API 格式与显式端点规则、模型级 LoadBalancerStrategy / TraceStickyMode（归一化）与模型卡 DefaultMaxTokens、无模型行时按 FallbackToChannelsOnModelNotFound 回退通道；PersistentOutboundTransformer 的模型优先 CanRetry、PrepareForRetry 推进模型并刷新 ApiFormat / 重建变换器、NextChannel 归零与 ApplyModelDefaultMaxTokens 的浅拷贝；IChannelPoolGrain 重构为按优先级分组的 SortAsync（requiredCount 截断、单候选记账、评分纯函数）；59 例 Api（50 + 9），Api 由 2247 增至 2306，Grains 由 92 增至 94（+2），Llm 3020、Persistence 207，合计 5566 增至 5627，0 失败 0 跳过；C179 登记（实时读取、稳定顺序、privacy 判定时序与五个未接入选择器），C180 登记四个未移植的通道变换选项与流策略强制。本次追加 §91：trace / thread 通道粘性路由切片——TraceStickyMode 的 profile → model policy → RetryPolicy 逐级覆盖（default / system_default / 非法值视为未设置）、prefer_previous_channel 的 trace → thread 作用域解析（当前 thread 优先、回退 trace 行 thread、命中仅限候选池）、priority 最小候选置首并移除同通道 fallback、仅对粘性候选记账一次、fallback 不记账；每次切换到新通道（traceId != 0 且 channelId != 0）时写回 previous-channel 缓存，trace 键总是写、thread 键取 trace 行 thread_id，TTL 30 分钟，未命中不查历史；进程内缓存的多实例限制登记为 C181。新增 9 例 StickyChannelSelectionTests、6 例 PreviousChannelCacheTests 与 1 例 Grains 跨优先级记账用例，Api 由 2306 增至 2321，Grains 由 94 增至 95，Llm 3020、Persistence 207，合计 5627 增至 5643，0 失败 0 跳过。本次追加 §92：项目 / API key profile 的候选过滤（ProjectFilters 等价物 ProfileFilters，项目 profile 仅在带 API key 时生效、key 激活 profile 再收窄，ChannelIDs 与 ChannelTags 的 any / all / none 匹配、空过滤器 no-op、模型关联与通道回退两条候选来源、tester 指定候选、过滤为空时 `model not found: <model>`；12 例 ProfileFilterSelectionTests；C179 真实缺口 (b) 关闭）。Api 由 2321 增至 2333，Llm 3020、Grains 95、Persistence 207，合计 5643 增至 5655，0 失败 0 跳过。本次追加 §93：Google / Anthropic 原生工具候选过滤（仅 Gemini contents 安装 Google 过滤、仅 Anthropic messages 安装 Anthropic 过滤；google_* 前缀与 web_search / web_search_20250305 触发；兼容集合只含 gemini / gemini_vertex 与 anthropic / anthropic_aws / anthropic_gcp / claudecode / deepseek_anthropic；无兼容回退全量；3 例 AnthropicNativeToolFilterTests + 10 例 NativeToolSelectionTests；C179 真实缺口 (c) 关闭）。Llm 由 3020 增至 3023，Api 由 2333 增至 2343，Grains 95、Persistence 207，合计 5655 增至 5668，0 失败 0 跳过。本次追加 §94：流策略候选过滤（流式剔除 forbid；非流式优先非 require；chat / completion 自动聚合保留 require-only；AI SDK / embedding / compact 返回空；Decisions 抛 StreamPolicyConflictException 精确文案；候选携带通道 policies 原始 jsonb；19 例 StreamPolicySelectionTests；C179 真实缺口 (d) 关闭）。Api 由 2343 增至 2362，Llm 3023、Grains 95、Persistence 207，合计 5668 增至 5687，0 失败 0 跳过。

## 构建

- 命令：`dotnet build AxonHub.slnx`
- 结果：**0 警告 / 0 错误**
- 目标框架：`net10.0`
- 附带验证：`TreatWarningsAsErrors=true`、`AnalysisLevel=latest-recommended`、`EnforceCodeStyleInBuild=true`。
  全局 `NoWarn` 仅豁免与参照实现命名/签名映射直接冲突的规则（CA1068 等），理由逐条写在
  `Directory.Build.props` 注释与对应源文件的 `[SuppressMessage]` 中。
- 管理面前端：`frontend/` 为 `E:\GitHub\axonhub\frontend` 的原样副本（HEAD
  `f272549f1342`，829 文件，`diff -r` 逐字节一致），沿用其 pnpm / Vite /
  Playwright 工具链，不纳入本次 `dotnet build` / `dotnet test` 门禁，见 §47。

## 测试

| 项目 | 总数 | 通过 | 失败 | 跳过 | 说明 |
|---|---|---|---|---|---|
| `AxonHub.Llm.Tests` | 3032 | 3032 | 0 | 0 | pipeline 重试 15 + 空响应 24（chat/图片/嵌入/审核/决策/alpha-search/音频/rerank/video 探测与 pipeline 语义）+ OpenAI 聚合器 1 + HTTP 执行器/SSE 增量解码 9 + Go JSON UTF-8 修复与语法文案 10 + 字段级类型错误文案 101 + Anthropic 入站 155（请求/响应转换与校验、Go 解码语义、错误信封、流式状态机 17、Anthropic 聚合器与 usage_test 表 12）+ Anthropic 出站 67（transformer 请求/响应/工具/校验 28、出站 SSE 与 fixture 14、cache_control 优化器 25）+ Responses 非流式 158（入站 38、出站 54、压缩 16、usage/model 50）+ Responses 图片 26（请求构造 9、响应还原 11、线路与错误 4、codex 图片流 1、item action 1）+ Responses 流式 86（入站 24、出站 34、聚合 28）+ Responses WebSocket 40（执行器/握手/URL 4、流生命周期与取消 6、错误事件与聚合 9、连接池与续传对账 18、HTTP 传输包装 3）+ OpenAI Images 71（入站 JSON/multipart 与响应 36、出站请求/响应与 extractFile 33、编辑线路形状 2）+ OpenAI Embeddings/Moderations 59（嵌入变换器 43、审核变换器 16）+ OpenAI Alpha Search/Decisions 129（alpha 变换器 19、decisions 变换器 110）+ OpenAI Audio 44（speech/transcription/translation 变换器 39、空响应探测 5）+ Jina Rerank/Embedding 79（rerank 入站 23、出站 29、embedding 出站 7、embedding 入站 20）+ OpenAI Video 57（video 变换器 56、空响应探测 1）+ OpenAI 旧版 Completions 65（入站请求 13、入站响应 6、入站流式 5、入站错误映射 6、出站请求 17、出站响应 9、出站流式 4、聚合 5）+ TypeSafe SystemOne 56（统一模型 8、入站请求 15、入站响应与错误 8、出站请求 9、出站响应 8、出站错误与流 6、空响应探测 2）+ Go 时间线路形态 7（RFC3339Nano 整秒 / 毫秒 / 微秒有效位、零值、Unspecified 视为 UTC、Unix 秒截断与零值 `-62135596800`）+ Codex 61（出站变换与执行器 39、模拟器身份头 10、additional tools 作用域 6、alpha search session 优先级 3、`DecodeAuthJson` 1、token provider 2）+ OAuth 50（凭据文档与 token 响应 8、token provider 4、自动刷新 2、设备码 31、PKCE 5）+ ClaudeCode 用户 id 解析 5（legacy 前缀、v2 JSON、空 session、非法输入、首尾空白）+ OpenCode 会话头 7（当前头 / `X-Session-Id` / 亲和头、优先级、缺失与空白回退、trim 归一）+ ClaudeCode 工具与前缀 55（工具名前缀剥离、流式去前缀、`tool_use` 聚合过滤、计费系统消息移除）+ ClaudeCode 出站 23（api-key / OAuth 两路的头与 URL、UA 透传、`proxy_` 前缀、系统消息与 user id 注入、`beta` 与 `strip_tool_prefix`、响应条件剥离与流式逐行去前缀、错误信封）+ ClaudeCode 用户 id 生成 7（设备 id 与 UUID v5 的确定性、会话回退、空白处理）+ DeepSeek 出站 20（URL 归一、`response_format` 降级、`thinking` 开关、`reasoning_content` 填充、completion 分派、错误与构造校验）+ OpenCode 出站 38（模型路由 13、按模型路由 6、会话头优先级与三级回退、`WithSessionHeader` 两种形态、响应 / 流分派、错误与构造校验）+ Ollama 出站 42（`/api/chat` 线格式、`stream` 恒显式、`options` 与 `images`、工具与 `tool_calls` 的 Go map 排序往返、URL 与可选 Bearer、响应 / 流 / 聚合 / `ollama_error`、校验与构造）+ NDJSON 解码与传输选择 10（逐行 / 空行 / CRLF / EOF 残余 / 读错误 / `application/x-ndjson` 及其 charset 变体的路由）+ Fireworks 7（空 URL 默认端点、尾部斜杠裁剪与版本归一、`reasoning` 回填 `reasoning_content`、Bearer/API 格式、构造校验）+ Moonshot 12（`json_schema`→`json_object` 与 schema 清除、URL 归一、请求形状、reasoning 回填、messages / 类型 / compact 拒绝、错误信封委托、构造校验）+ Longcat 8（字符串 / 空串 / null 内容强制数组、多分片保持、`messages` 与 `content` 末位键序、调用方内容变更、nil 请求、构造校验）+ xAI 27（默认 URL 与 config 就地变更、自定义端点、Bearer、Grok 字段裁剪、`IsValidResponse` 13 组、空 delta 过滤与 `[DONE]`、nil / model / messages 校验、构造校验、key 溯源）+ Cline 24（`EndpointPath` 与 `cline-cli`、空内容归一的 8 条消息表、统一索引补丁的错位与 null 填充、信封解包与裸 OpenAI body、502 错误与 errors 回退、HTTP 状态映射、空 / nil 响应、TransformError 三态、流内错误与 `[DONE]`、流转换、聚合的 reasoning 与错误块跳过、构造校验）+ OpenRouter 39（构造校验与包装前缀、单斜杠 BaseURL、chat 形状与 reasoning、audio 委托、compact 拒绝、图片请求的引用上限 / mask / seed / data URL、图片响应的 `img-<created>` 与 OutputFormat、错误信封三态与恒字符串 code、字段遮蔽、流与聚合）+ nanoGPT 16（状态诊断三态、chat 解码与 reasoning、非聊天委托、`[DONE]` 过滤与 DoneResponse、流内错误、聚合、零 MessageContent、非 nil choices）+ nanoGPT XML 工具调用 33（maybe 判定、10 种标签形态、名称覆盖表、确定性 ID、Go 数字与键序、未识别标签、未闭合贪婪回溯）+ Cerebras 8（默认 URL、nil 配置与缺 provider、`store` 浅拷贝剥离、OpenRouter chat 语义、key 索引溯源、响应与错误委托）+ Zai 59（URL 归一 5、请求/类型校验 7、metadata 提取与会话回退、thinking 阶梯、response_format 与 URL 覆盖、user_id 6-128 rune 规则、版本/恒思考/归并/resolve 四张表、响应委托与错误、构造校验）+ Zai 图片 11（排序请求体逐字节、quality 五态、Image/APIFormat/输入图/mask/prompt 拒绝、data URL 响应、model 字段错位回退）+ MiniMax 31（构造三条文案、model 缺省四态、response_format 三态、尺寸解析与六种非法尺寸、5 种路径、九键排序请求体、业务错误 1008、url→base64、created/id 回退、错误与 chat 委托）+ Bailian 34（构造两条文案、reasoning_effort 12 态映射与 `enable_thinking`、OpenAI 入站 none、连续工具调用合并与 14 类阻止合并且各段独立合并、chat 线路形状、响应 / 错误委托、流过滤端到端）+ Bailian 流过滤 9（tool call 后文本缓存与 finish 前释放、重复 `{}` 参数、无 tool call 直通、多 choice 文本与非文本 part、最后 choice 索引、按 (choice, call) 累计、Done 引用透传、nil 跳过、未触碰响应引用透传）+ ModelScope 44（图片请求构造与全部拒绝、chat / 响应委托、内联 data 与 output_images 解析、任务轮询 / 状态 / 失败文案、下载转 base64 / data URL、超时取消与构造校验）+ Doubao 出站 30（构造校验、`user_id`/`request_id` 与截断、URL 三分支、图片请求体与编辑图 data URL、chat 委托与错误）+ Doubao 嵌入与 Seedance 出站 42（多模态嵌入请求/响应、任务 create/get/delete 线路、时长取整与尺寸推断、状态缺省与响应映射）+ Doubao Seedance 入站 20（创建请求解码、Go 解码文案与根类型不匹配、client 侧 `{id}` 与任务响应字段序、OpenAI 错误委托）+ ZenMux 62（原生 create 线路形状与字段序逐字节、base URL 三种归一、size 推断与显式字段优先、校验序与逐字文案、extra_body 排序合并与拒绝、create 响应映射与 model 回退、poll 状态归一与错误文案、delete 哨兵、chat 委托与 key 溯源、构造校验）+ GitHub Copilot 122（出站 110：构造与单斜杠裁剪、模型路由 19、chat 线路形状与七头、Responses 路由 3、视觉头 5 与视觉内容 15、initiator 转发 6 与推断 12、data URL 12、响应解包与 HTTP 错误文案、错误信封、流事件 call-id 修复、Responses 流判定与聚合；令牌交换 9：缓存、5 分钟缓冲、错误文案；令牌提供者 3：GitHub 设备流交换与凭据替换）+ Gemini OpenAI 出站 89（构造 9、请求基础与校验 9、ThinkingBudget 11、thinking 映射 18、extra_body 15、ParseExtraBody 9、thought signature 4、Google 工具过滤 3、错误信封 11）+ Google 原生工具过滤 19（`google_` 前缀表 10、包含判定 5、过滤顺序与空结果、len==0 原样返回）+ Gemini 基础切片 69（共享 thought signature 9、MIME 推断 11、文档/音频 Part 与 MIME 判定 22、thinkingLevel 预算选择 13、usage 双向一致 6、TransformerMetadata 提取 8）+ Gemini 出站转换 150（请求转换 58：basic/video/remote audio/response format/tools/multiple tool messages/edge cases/testdata/safety settings/image config；消息与工具结果 30：message to content/tool message/VertexAI/extract parts/image url to part/thought signature；响应转换 34：basic/thought signature/finish reasons/edge cases/citation/grounding/tool call thought signature；预算与角色 28：budget/effort→budget/with config/角色双向）+ Gemini 出站变换器 71（embedding 请求/响应/URL 三分支/任务映射、image 请求/响应/错误/尺寸映射与 MIME 嗅探、TransformRequest 分派与 URL 三分支、TransformError 信封、chat 接线 smoke、端口补充边界）+ Gemini 出站流式与聚合 26（TransformStreamChunk 7：nil/空/[DONE]/文本/思考/工具调用/非法 JSON；TransformStream 4：稳定 ID、toolCallIndex 累积、单事件多工具调用；聚合 6：空/文本/思考/工具/跳过 [DONE]/跳过非法 JSON；finish reason 5 与四夹具回放 4）+ Gemini 出站核心 27（配置清理 8 组、构建 URL 7、构造器 4+3、extra_body thinking 5、reasoning_effort 回退、多工具调用响应 5、Vertex 清 ID）+ Gemini strict 工具选择 8（6 选择 × 3 模型、非严格 4×2、名称往返 3×2）+ Gemini developer 消息 3 + Gemini 入站转换 102（请求基础 12、音频/视频文件 2、响应格式 6、工具 9、内容消息 10、多函数响应 1、thought signature 入站 4、candidate thought signature 8、LLM→Gemini 响应 6、finish reasons 5、testdata 请求 4、testdata 响应 3、四向往返 12、citation 回退 1、grounding 优先 1、grounding 5、安全设置 4、图像配置 4、grounding 附加 5）+ Gemini 入站流式与聚合 25（TransformStreamChunk 6 / TransformStream 3 / AggregateStreamChunks 5 / 六夹具回放 6 / finish reason 5）+ Gemini 双向集成 19（五形状主线含 system instruction、三请求夹具、grounding 往返、图像响应 5、modalities 与 snake_case 工具）+ parametersJsonSchema 保真 1 + Antigravity 129（router 47 / version 21 / health 12 / executor 22 / sanitizer 19 / transformer 8）+ xai_subscription 42（billing 10：weekly/monthly 合并、SuperGrok Heavy、缺配置、缺窗口、null config、`{"val": null}`、raw number 与空白字符串、大小写不敏感键、无 val 包装、非对象文档；plan 14：参照 4 行 tier 表、不透明令牌与其余 tier 编码；sso 6：cookie 提取、控制字符清洗、超长、空 cookie 回退、UTF-8 字节上限；sso_device 6：设备流全流程、验证页状态码、缺令牌、不可信 URL、authorization_pending、slow_down；outbound 6：CLI 身份头、APIFormat、非 chat 2 例、缺 provider、逐请求令牌）+ Vertex 执行器 11（构造校验与 FromJson 文案、URL 改写与 sjson 字节语义、count_tokens 保留 model、令牌注入与复用缓存、令牌端点错误）+ VertexTransformer 与工厂 7（CreateWithConfig 顺序、透传、错误包装、provenance、端到端双斜杠线路形状）+ GCP 令牌链 13（JWT 断言字节与 RS256 签名、aud 默认与覆盖、reuse 缓存边界、refresh 表单与 text/plain 解析、refresh_token 轮换、精确错误文案、C128 拒绝） + 共享消息过滤 9（Responses custom tool 调用与结果消息移除、CallId 去重、空消息丢弃、内容 / 推理 / 拒绝 / 音频保留、非 tool 角色不受影响） |
| `AxonHub.Persistence.Tests` | 276 | 276 | 0 | 0 | backup 存储读写 24（§107：BackupStore 8 例 + BackupRestoreStore 16 例：软删谓词、ID 游标、活边与 includeApiKeys 门、事务提交 / 回滚 / 环境事务、ent not found 文案、全列更新、active 版本归档、500 分批、日志批量插入） + data_storages store 8（§106：全列往返、软删隐藏与唯一名保留、名称检查排除自身、过滤与空 *In、id 键集分页、updated_at 排序与游标值、嵌套空谓词文案、部分列更新与 RETURNING）+ schema 快照 5 + EF 模型一致性 2 + EF/Dapper 共享事务 2 + 连接语义 2 + 九表仓储 48（projects / users / api_keys 各 4 例，systems 4 例，channel_model_prices 6 例，requests / request_executions 的 video 任务查询与轮询快照接缝 3 例）+ `traces` 表 6（`GetOrCreateTrace` 默认值与固定时钟、thread_id 往返、已存在行原样返回、跨项目 trace_id 唯一与约束重试、失败后 detach 可复用、并发创建单行）+ `threads` 表 5（`GetOrCreateThread` 默认值与固定时钟、已存在行原样返回、跨项目 thread_id 唯一与约束重试、失败后 detach 可复用、并发创建单行） + systems upsert 3（插入、冲突原地更新保留 id/created_at、软删除复活） + users / roles 存储 17（UserAdminStoreTests 7：带角色创建与往返、过滤与键集分页、字段 / scope / 角色边更新与清空、停用联动禁用 personal key、删除归档 key 与关系清理、禁用 / 归档失败前缀与事务回滚；RoleStoreTests 10：全列往返、null project_id、按项目名称唯一与软删、仅更新提供字段、过滤 / 排序 / 键集分页、last 尾页、用户角色 join、删除清理边与软删、批量软删） + `roles` / `user_roles` / `user_projects` 三表的 schema 快照与 EF 模型断言（既有用例扩列 / 索引） + channels 存储 4（statusIn / nameContainsFold / errorMessageIsNil / hasTag 过滤与键集分页、ListAll 仅在给定 orderBy 时排序、摘要过滤与权重排序、tag 行与类型计数） + channels 写入存储 20（状态变更清空自动禁用标记、软删除 Unix 秒并隐藏行、批量状态数量校验且无部分写、recover 清 error、批量删除只命中活行、标签增删与未变更行跳过、缺失行回滚、null tags 视为空数组；create 插入全列往返、同名活行返回 null、软删名可复用、插入失败前缀；update 快照仅返回活行、同名检查排除自身与软删行、部分列写入并刷新 updated_at、清空标志写 NULL 与 jsonb null、版本守卫命中 / 失配、无守卫更新忽略旧版本、缺失 / 软删行返回 null、唯一索引冲突前缀 8 例；批量排序全量写入与 updated_at 刷新、缺失 / 软删行回滚整批、自动禁用 write_rules 归一化并保留 stream、inherit 清规则、off 保留存储规则、null policies、ID 计数先于写入 7 例） + channels 端点存储 4（GetAsync 仅返回活行、端点 JSON 写入并刷新 updated_at 且不动 settings、版本守卫 / 缺失与软删行、空数组）+ channels API key 状态 4（disabled_api_keys 与自动禁用列写入、未指定列不动、恢复标记清空、缺失与软删行返回 false） + channel_model_prices 写入语义 7（create 写当前行与 active 版本并触碰通道、update 归档旧版本并新建新 reference id、skip 不写、delete 软删并归档、混合计划按序写入并原序返回、缺失通道回滚全计划、缺失行的 update / delete 保留参照前缀） + 自动定价 ensure 5（去空去重与原始顺序补齐、既有价格与版本历史保留、空集合短路、加入调用方事务并在失败时回滚、模型卡工厂失败按模型库前缀包装） + create/update 事务回调 4（回调在事务内可见、create 回调失败回滚通道、update 在保存后运行、update 回调失败回滚行） + `channel_override_templates` 存储 7（创建 JSON 精确值与读取回环、更新 set / clear / append 优先级、按用户唯一冲突与软删、软删隐藏、列表作用域 / 过滤 / 键集分页、通道查询忽略缺失与删除行并按 id 升序、批量设置写入提交与回滚） + `oidc_identities` 与自助存储 6（角色 join 的软删过滤与角色 id 顺序、user_projects / OIDC 身份按 id 顺序、myProjects 的成员 / 状态 / 软删过滤、UpdateOwnProfile 仅写提供字段、OIDC 取行 / 计数 / 软删、`(issuer, subject, deleted_at)` 唯一性） + 登录查询 1（`email + status = activated` 精确匹配活行，排除停用、软删与大小写不同的行） + user_projects 读写 9（AddProjectUser 关系创建与角色去重、isOwner 缺省与 scopes null、重复关系前缀、缺用户回滚整事务、Remove 只清本项目角色 / 无关系不动角色、Update 先加后移 / 缺失关系前缀、列表无排序） + projects 写入 / 读取 8（项目名唯一性排除软删、键集分页与名称折叠 / 状态过滤 / updated_at 排序、部分列更新与 updated_at 刷新、用户边清空替换与 join 表默认值、重复加边回滚整次更新、profiles / status 写入与缺失行 null、owner 行与重复冲突、级联软删角色 / API key 且保留 user_roles） + api_keys 管理存储 9（重写：按 ID / key 读取、软删可见性、key 跨软删唯一、archived 不软删、列表 / 游标 / 总数、重复名与 key 谓词、clear / replace / append、项目谓词、批量状态） + `models` 管理存储 7（ent 默认值、全列往返、软删可见性、谓词与空集合、键集分页、SET 顺序与 ClearRemark、批量状态与软删） + usage_logs 窗口聚合 4（CountByApiKey 键与窗口、Aggregate 的 COALESCE / 列裁剪 / 空结果） + prompts 存储 13（全列往返与软删谓词、project / status / 软删过滤、空结果、跨项目同名与唯一冲突、软删名复用、jsonb 形状；GetById 作用域与软删、ExistsByName 排除自身 / 项目 / 软删、列表过滤与 ORDER / UPDATED_AT 键集分页、Update 无软删谓词且跨项目失配、软删一次、批量状态含软删行与批量删除仅活行） + trace / thread 存储 15（TraceAdminStoreTests 4：列表过滤 / 作用域 / 键集 / CREATED_AT 游标、GetById 作用域、状态更新的 from 与 scope 谓词、线程内首条 / 上一条 / archived 计数；ThreadAdminStoreTests 5：列表、GetById、同状态级联、级联 trace 的 project 谓词、级联失败回滚；UsageMetadataStoreTests 3：trace / thread 聚合与空行；RequestSegmentStoreTests 3：completed 读取 / where 时间谓词与键集 / 可见性谓词） + usage_logs 可见性 1（List / GetById 的项目范围与逐行可见性 SQL 子查询） + analytics 存储 4（区间闭界与毫秒精度、空聚合零值、维度 join / 名称 / key 解析、性能最新 completed 尝试与 10ms 下限） |
| `AxonHub.Grains.Tests` | 95 | 95 | 0 | 0 | 通道选择：打分/窗口 5 + 健康与 topK 6（含 topK 截断与归一化 2）+ 别名解析 3 + 同分顺序/不计数 2 + 负载均衡策略 8（adaptive 中性总分/线性衰减 2、冷却边界 1、权重 tie-break 与未知回退 2、failover 1、circuit-breaker 1、策略切换 1）+ `default_test_model` 解析 4（Trim、`ChooseModel` 映射、`lowercaseModelId`、未知模型保留 Trim 值）；限流与并发准入 64：ChannelLimiter 10 + ChannelLimiterManager 12 + ChannelRequestTracker 24 + RateLimitAwareScorer 18 |
| `AxonHub.Api.Tests` | 2737 | 2737 | 0 | 0 | 备份 / 恢复与自动备份管理面 57（§107：BackupServiceTests 43 例 + AdminBackupEndpointTests 14 例：owner 门禁、版本校验先行、金文档与 omitempty、九个实体的恢复策略、双指纹去重、请求壳、自动备份频率与记录、五个根字段线形、multipart restore、未知枚举） + data storage 管理面 14（§106：dataStorages 线形与 settings 输出、webdav / gcs 形状、where + UPDATED_AT 键集分页、CREATED_AT 回退 id、显式 primary=false、创建默认值与完整 JSON、DUPLICATE_NAME 扩展、更新合并与凭据保留、重名拒绝与自身重命名、缺失行文案、node、service_account 作用域与 default deny）；Responses WebSocket 编排接缝 5（CustomizeExecutor、finalizeTransportRequest 与中间件委托/透传）；HTTP 切片 87：orchestrator/重试/超时 18（含通道级重试配置 3、流式性能记录语义 2、策略 topK 1、负载均衡策略接线 1、trace 粘性 key 端到端 1）+ 请求链路落库 5（含鉴权身份传播 1、trace id 传播断言）+ SSE 写帧 9（含上游错误策略 2）+ 请求读取 12（含 zstd/压缩错误文案）+ 端点胶水 18（OpenAI 11 含上游错误策略 3，Anthropic Messages 两条路由 7）+ 期望流选择 2 + 通道重试谓词与 settings 解析 16 + 通道性能记录 7；API key 鉴权与 ClientIP 73：header 提取 14 + 鉴权器流程 13 + ClientIP/allowlist 与中间件文案 46；系统设置与 IP 黑名单 29：SecuritySettings 回退/归一化 15 + 黑名单放行/命中/受信代理 14；重试策略系统设置 49：回退/零值/类型错误 16 + 解析与重复键 7 + 归一化 26；上游错误策略 applier 29：非流改写/本地错误/quota 11 + 响应体字段 8 + 流式分类与策略读取次数 10 + 通道级请求改写 134：body/header 改写与设置解析 133（ChannelOverrideTests 87 + ChannelOverrideSettingsTests 47）+ 端到端落库 1；限流中间件 29：RPM 准入 5 + TPM/冷却追踪 13 + 队列错误 4 + 连接准入与让位 7；usage 成本 43：计算器 26 + 价格读取 12 + 注入与落库 5；多 key 选择与执行 key 审计 30：provider 23（含 trace 粘性 7）+ 审计 7 + OpenAI Images 端点接线 8（生成/编辑路由、multipart 透传与错误信封、空响应检测图片分支）+ OpenAI Embeddings/Moderations 端点接线 7（两条路由、嵌入/审核透传、缺省模型回退与错误信封、空响应检测）+ OpenAI Alpha Search/Decisions 端点接线 6（两条路由、原样透传与上游错误信封、空响应检测）+ OpenAI Audio 端点接线 58（speech 路由探测 15：nil/空 body/非 JSON/stream_format 判定表/解码错误文案；strconv.ParseFloat 保真表 36；二进制流写入器 7：首块 CT 与头、done/空块吞掉、headers 前分类错误信封、headers 后只记日志）+ Jina Rerank/Embedding 端点接线 7（`/v1/rerank`、`/jina/v1/rerank`、`/jina/v1/embeddings` 三条路由、校验/解码错误 → 500 api_error 信封、rerank/embedding 转发与空响应检测）+ OpenAI Video 端点接线 33（biz/video_test.go 对照的服务层 23、GET/DELETE 胶水 6、POST /v1/videos 4）+ OpenAI 旧版 Completions 端点接线 4（`POST /v1/completions` 转发、completions 线路形状与 Bearer、缺 prompt 400、空 body 400、非 JSON CT 400）+ TypeSafe SystemOne 端点接线 9（`/v1/systemone` 与 `/typesafe/v1/systemone` 共用 handler、上游 `/v1/systemone` 线路形状与 Bearer、空响应探测保留 answers、空 answers 切换通道、429 换用下一通道 key、401 不重试、缺 model / state 的 500 `api_error`、空 body 400）+ 模型清单与检索 51（xregexp 8：`*`、精确匹配、双向锚定、`(?i)` 修饰符、非法正则恒 false 与缓存、空模式；系统模型设置 15：缺行 / 读失败 / 软删 / 畸形 JSON 回退、缺成员与字面 null 的零值、大小写不敏感键、`developer_settings` 归一化；`ListEnabledModels` 7：facade 字段、前缀 / 映射别名、跨通道去重、黑名单正则与精确匹配、`QueryAllChannelModels=false`、null 支持模型；端点 14：OpenAI 列表 / 空列表 / HTML 转义、尾斜杠 404、斜杠模型 ID、两种 not_found 文案、request_id 回显、500 信封、Anthropic 分页列表 / 空列表 / 500 的 request_id 空与回显；请求 ID 中间件 7（`ar-<uuid>` 生成、响应头与错误体回显、缺失头生成））+ pass-through 通道重放 95（开关与流对齐 17、body 重放与 model 补丁 31、格式支持与策略 12、Codex 头 4、User-Agent 策略 8、通道 settings 解码 11、系统设置 provider 8、编排端到端 4）+ pass-through 响应/流重放 26（非流捕获与应用 7、原始流捕获 6、原始流应用 6、通道流取消语义 2、backlog 预算 2、真实 pipeline 集成 3）+ 提示词保护 71（规则应用 18、中间件 11、规则服务 9、原始补丁 13、真实编解码载荷 13、回放校验 4、编排端到端 3）+ codex/fenno 通道接线 21（7 组 ApiFormat 分派、fenno 拒绝 alpha search、fenno 的 `/v1/responses` 与 bearer、API key/OAuth/旧版 apiKey 三种凭据、oauth 对象优先、两种缺凭据文案、`SupportsCodexResponseHeaders`、ws/wss 与 HTTP 的 transport 推断、会话 id 的 `Session-Id`/`Thread-Id` 透传）+ trace 中间件 38（trace 头 trim 与优先级、extra headers、OpenCode 三头与开关、Claude Code `metadata.user_id` 新旧格式与两条 `messages` 路径、Codex 头 / turn metadata 与三头优先级、body 字段点路径与首命中、无 id 不落库、非消息端点跳过、`GetOrCreateTrace` 幂等与失败开放、`AH-Trace-Id` 响应头、`AH-Session-Id` 上下文、身份 trace id 与 session 传播、body 读取失败 400 信封与回绕）+ thread 中间件 7（默认头建行并发布 id、配置头名、缺失 / 空头、无身份、持久化失败 fail-open、多值取首、thread → trace 链路写入 `thread_id`）+ claudecode / opencode 通道接线 11（claudecode 的 api-key 与 OAuth 两路凭据、Bearer 与 `proxy_` 前缀、伪造 user id、`strip_tool_prefix`、缺凭据文案；opencode 三种通道类型按模型路由与会话头、缺 api key 文案）+ Ollama 通道接线 7（`ollama` 与 `ollama_anthropic` 的类型分派、无 key 建站、禁用 key 不发送、Bearer、`/v1/messages` 归一与 `num_predict` 映射）+ 流式模型名分派 4（OllamaChat / OpenAICompletion 的顶层 `model`、其它格式为空、Cline 信封仅在 `success` 为真时解包）+ Fireworks / Moonshot 通道接线 6（Fireworks 默认端点与 `reasoning_content`、Moonshot URL 归一与 Bearer 及 schema 降级、两种类型缺 key 拒绝）+ Longcat 通道接线 2（`longcat` 分支、数组内容证明未落到普通 OpenAI 分支、缺 key 拒绝）+ xAI / Cline 通道接线 8（`xai` 的 chat 出站与 Bearer、缺省 base URL、responses / compact 分派；`cline` 的 `/chat/completions` 与 `X-Client-Type`、信封解包；两种类型缺 key 拒绝）+ OpenRouter / Cerebras / nanoGPT 通道接线 10（openrouter 的 chat 出站与 Bearer、speech 由出站委托到 `/audio/speech`；cerebras 的缺省 URL 与自定义 URL、`store` 剥离；nanogpt 的 chat URL；`nanogpt_responses` 的 Responses 出站与 `/responses` URL；四类缺 key 拒绝）+ Zai / Zhipu / Xiaomi / MiniMax 通道接线 21（zai 家族 v4 chat 4、xiaomi v1 chat 2、四种 anthropic 变体的 messages 4、session→request_id、minimax chat 与图片双路由、minimax_anthropic chat、八类缺 key 拒绝）+ Bailian 四类通道接线 9（bailian chat 出站与 none→`enable_thinking=false`、bailian_responses 的 `/responses`、bailian_anthropic / moonshot_coding 的 `/messages`、四类缺 key 拒绝）+ ModelScope 通道接线 4（chat 与图片双路径、图片保持原始 BaseURL、异步头与 ApiFormat、缺 key 拒绝）+ Doubao / Volcengine / Doubao-Anthropic / Volcengine-Anthropic 通道接线 11（v3 chat 与 Seedance 任务 URL、Bearer 与 key 溯源、doubao_anthropic 的 `PlatformDoubao` 与 volcengine_anthropic 的 `Direct`、两族 anthropic 变体的 Doubao 回退、缺 key 拒绝）+ Doubao Seedance 任务端点 5（`GET`/`DELETE /doubao/v3/contents/generations/tasks/:id` 的结构序渲染、gin `c.Data` 的 Content-Type-only 头语义、非法 id 400、未知任务 500、DELETE 204 与上游线路）+ ZenMux 通道接线 18（四类默认 base URL、plain zenmux 的 OpenAI 主出站与共享 openai/video、Responses 分派、zenmux_responses、zenmux_anthropic 的 Direct 与 `/v1/messages`、zenmux_video 原生主出站与 chat 委托、`zenmux/video` 在四类的分派、anthropic 型的默认视频 URL、自定义 base URL、缺 key 文案）+ VideoService zenmux 任务解析与本地取消 7（最新执行格式、无外部 id 跳过、持久化 openai/video、非视频格式拒绝、legacy 回退、本地取消零上游调用、取消写入失败包装）+ GitHub Copilot 通道接线 9（typed OAuth + 占位 apiKey、OAuth 内嵌 apiKey、Responses 路由、自定义 base URL、无 OAuth 早退、仅 typed OAuth 的 `has no credentials`、畸形 legacy OAuth、畸形 typed `expires_at`、无执行器的 `http client is nil`）+ Gemini OpenAI 通道接线 6（base URL 归一与 key 溯源、已归一 URL 两种写法、空 base URL 的包装文案、缺 key 早退、Metadata 与 extra_body 经工厂透传）+ 管理面 GraphQL 6（`POST /admin/graphql` 的 query/variables/operationName 契约、19 条 scope 的顺序/描述/层级、system 与 project 过滤、未知层级空列表、null 层级、ScopeInfo 内省）+ Gemini 通道接线 6（gemini 原生变换器与 `x-goog-api-key`、自定义 base URL 保持、zenmux_gemini 的 ZenMux 默认 base 与存储 base、gemini_vertex 顶点 URL 形状、无 key 拒绝早于变换器构建）+ Gemini 原生端点接线 6（非流式 / 流式 / alt=sse 三形状、ListModels 成败两分支、未知动作 404 信封）+ Gemini key 鉴权中间件 6 + Antigravity 通道接线 5（信封变换器与 OAuth-only 鉴权、自定义 base URL、缺凭据文案、key 列表语义）+ OpenaiResponses 通道接线 4（单端点 Responses 出站、自定义 base URL、缺凭据文案、ws/wss 传输推断）+ Commandcode 通道接线 12（chat 出站与 Bearer、anthropic 出站与 Bearer 且 X-API-Key 恒缺省、缺 key 文案、HTTPS 基址校验与校验顺序）+ 别名通道类型接线 40（OpenAI 兼容别名 11 共用 chat 出站、Direct 三变体 3、LongCat 1、DeepSeek/Moonshot 2、anthropic_aws Bedrock 1、xai_responses 单端点 1 与无传输推断 2、19 类型缺 key 拒绝）+ xai_subscription 通道接线 9（OAuth-only 拒绝、非法凭据包装、oauth 对象优先、legacy apiKey 回退、空白 access_token 回退、忽略存储 BaseURL、非 chat 2 例、空凭据拒绝）+ anthropic_gcp 通道接线 4（keyless 构建 Vertex 变换器与 Vertex URL / 版本头、缺 gcp 两条文案、空 jsonData 的双层包装）+ 静态托管与 SPA fallback 26（API 前缀 JSON 信封逐字节与前缀边界 /v10、/administering；SPA、资产与纯文本 404 三分支；no-cache 原始头；favicon 与后缀大小写；映射路由优先；POST 回退；HEAD 无体；缺失 index 不回退目录列表） + 管理面 GraphQL system-settings 68（RetryPolicyCodec 26、ModelSettingsCodec 11、SystemSettingsAdminService 22、端点 9） + 管理面 users / roles 31（端点 14：查询形状 / 排序 / gid / 嵌套角色 / Cursor / PageInfo / where / 7 变更 / DUPLICATE_NAME / owner 与 Developer 保护 / 内省；密码哈希 6；业务服务 11：not found 文案、重哈希、系统角色校验、批量删除、重命名唯一性、密钥失败不加前缀） + 管理面 channels 18（channels 连接形状与 CREATED_AT 重映射、string / int 键集游标、first / last 校验、queryChannels 的 where 与 hasTag、模型分支忽略分页、四个聚合查询、Policies / settings / credentials / disabledAPIKeys / limiter 统计 / defaultEndpoints / allModelEntries、providerQuotaStatus 与未知枚举、损坏 JSON 列降级、错误前缀与内省） + 管理面 channels 生命周期 14（状态变更清空标记且保留 error、gid 类型段被忽略、缺失行文案、删除软删并清限流器、批量四动作与 recover 清 error、数量不符、批量删除容忍缺失 id、标签 Trim / 去重 / no-op / 双列表委派、存储失败前缀、空列表、内省） + 管理面 channels create 14（最小输入 ent 默认值、settings / credentials Go 字段序与 omitempty、空列表与省略的存储差异、xai 基址覆盖、命令码 cookie 规范化与其它类型丢弃、命令码错误包装、设置校验顺序与前缀、responseTimeout 继承丢弃、端点校验晚于设置块、顶点密钥校验、同名 DUPLICATE_NAME、软删名复用、存储失败前缀、内省） + 管理面 channels update 16（仅写提供的列并忽略 status / clearSettings / errorMessage、三个清空标志、同名 DUPLICATE_NAME 与 extensions、软删名复用、xai 类型与存储类型强制、命令码 cookie 规范化与空 cookie 清空、孤立 quota 清理、zenmux managementApiKey 保留、协议裁剪、live 重试上限与顶点校验、并发守卫、身份读取失败、内省） + 管理面 channels 批量写入 13（排序逐项写入与返回顺序、缺失行 resolver 前缀、空列表仍调用一次存储、write_rules 归一化、permanent_disable_delete、inherit 清规则并保留 stream、off 忽略输入并保留存储规则、空 ID / 缺规则 / 非法规则的写入前校验、服务级未知动作分支、计数不匹配无前缀、内省） + 管理面 channels 端点保存 10（写入端点并只改 endpoints 列、空列表、重复 api_format、完整 URL path、xai_subscription、已存 modelProtocols 校验、缺失通道、并发守卫、存储失败透传、内省）+ 管理面 channels API key 记账 25（禁用追加记录、自动禁用文案与标记、OAuth 哨兵、过期清理、启用恢复、删除保留最后一个 key 与 `ONE_KEY_PRESERVED`、OAuth 删除拒绝、错误前缀叠加、内省） + 管理面 channels 端点自动检测 28（三格式固定顺序与 URL / 鉴权形状、9 组状态码分类、传输失败 unreachable/0、请求值 / `default_test_model` / 首个 `supported_models` 的模型优先级、空 model 的 chat/anthropic `invalid_request` 与 responses 空串、缺失通道与存储失败前缀、主出站构建失败包装、`ollama_anthropic` Bearer 复用、`opencode_go_anthropic` 全 generic、Command Code generic messages 走 Bearer、typesafe 全 generic、xai 主 chat 与 generic responses、`zai_anthropic` v4 家族 chat、乱序完成固定顺序、零 key 不 panic、内省） + 管理面 channels 定价写入 61（saveChannelModelPrices 的计划顺序与结果顺序 / 空输入删除全部 / 重复 model_id / 校验前缀 / 调度树校验 / 查询失败与不可解码价格 / apply 失败透传 / 缺失通道 / 小数按值相等 / 结构差异换 reference id / Go 形状落库 JSON / 返回价格树 / 畸形 GID / 内省 16；ModelPrice Validate 与 Equals 45） + 通道自动定价 11（模型卡换算与 Go 线格式 7、createChannel 接线 2、updateChannel 接线 2） + 通道测试 75（ChannelTestService 49：五种格式请求形状、流式累加与空流 / 终止错误、批量成功 / 部分失败 / 取消 / 禁用标记、单 key 所有权、提示词 mask / reject、byte 级掩码；AdminChannelTestEndpointTests 9：三个变更、404 映射、批量 gid / 计数与 SDL；SystemChannelSettingsServiceTests 17：system_channel_settings 的缺行 / 回退 / 大小写 / 重复键 / 空白与非法 JSON） + 模型同步 68（ModelFetcher 25：默认表、46 行端点表、Gemini 分页、Anthropic 重试、header override、Command Code、Cline 推荐 / 静态回退、provider-conf 缓存、凭据分支与传输错误前缀；ModelFetcherCodex 10；ModelFetcherAntigravity 14；ChannelModelSync 14：合并 / 补价 / reload / 回退 / 顺序稳定 / 协议裁剪 / 周期批 / 并发 manual / 合并表 / 调度窗口；AdminChannelModelSyncEndpoint 5：载荷形状、gid 回显、模式覆盖、null 归一、错误透传） + 通道覆盖模板 58（ChannelOverrideTemplateService 42：创建 / 更新 / 删除 / Get / 列表 / Apply / Clear 的前缀与时序、操作校验顺序、append 语义、唯一冲突、缺行、隐私作用域、JSON 列解析失败、Merge / Replace 写入形状、空批次与 reload；AdminChannelOverrideTemplateEndpoint 16：查询字段与 gid / Time / Cursor 形状、排序 / 过滤 / 分页、二选一校验、隐私拒绝、SDL 输入子集与五个变更、创建 / 更新 / 删除 / Apply / Clear 线上形状与错误扩展） + 自助身份 35（UserProfileService 26：UserInfo 投影、系统角色判定、scope 并集与项目 effectiveScopes、avatar 与 hasPassword、五个入口的缺用户 / 前缀 / 缺行、updateMe 字段直传、updateMyPassword 的 OIDC-only / 旧密码缺失 / 校验失败折叠 / 哈希失败不包装、unlink 守卫链；AdminMeEndpoint 9：me 完整线形与 gid / Time、空 avatar 为空串、myProjects 的 Project 标量与状态枚举、updateMe 的 User 载荷、updateMyPassword、unlinkOIDCIdentity 的 gid 解析与错误文案） + 管理面登录 65（JwtTokenCodec 14、ManagementAuthService 34、AdminAuthEndpoint 17：header / payload 字节、HMAC 方法接受、篡改 / 过期 / nbf、30/90/7 天窗口与两级密钥前缀、认证与 OIDC-only 分支、登录线形与 GUID / 空 avatar、binding 400、401/204/200/500 分类） + 管理面 GraphQL 鉴权 27（AdminGraphqlAuth 27：三类 header 文案与修剪、JWT 与 service_account 成功、类型 / IP 顺序、未知 / 停用 / 非 active 项目 / noauth 的 401、存储失败 500、X-Project-ID 五例、只读变更拒绝与 operationName 选择、GET 404）；OIDC 提供方配置 / 发现 / providers 列表 53（OidcProviderConfig 18、OidcProviderRegistry 20、OidcProvidersEndpoint 9、OidcHttpClient 6）+ OIDC 登录流程 143（OidcLoginService 62、OidcLoginEndpoint 30、OidcLoginOptions 26、OidcTokenClient 9、OidcUserInfoClient 9、OidcIdTokenVerifier 7：state / PKCE / link / exchange 缓存与消费、authorize / callback 重定向、令牌交换样式探测、UserInfo 两路径、resolveUser / JIT / 角色映射五策略、gin 绑定仿真与 ent.User JSON、RS256 与 iss / aud / exp 校验） + 权限校验 25（PermissionValidatorTests 20：grant / edit / role / 项目级 / 集成 / 守卫与顺序；ManagementServiceTests 5：users / roles 写入接线与 `permission denied:` 包装） + 项目成员 23（ProjectUserServiceTests 15：Add / Remove / Update 的参照语义与逐字文案、权限拒绝；AdminUsersRolesEndpointTests 8：node() 的 Project / User projectUsers 查询形状、未知类型与缺失节点文案、三个变更线形、跨项目角色拒绝、移除幂等、更新加移角色、缺失关系） + 管理面 projects 14（Projects 查询形状与 CREATED_AT→id 排序、名称折叠包含 / 状态过滤、first / last 校验、create 写入预设角色与 owner 行并忽略 status、同名 DUPLICATE_NAME、update 替换边、重复加边整滚、status / profiles 变更、profiles Go JSON 回环与校验文案、删除软删并返回 true、缺失文案、系统 owner 校验） + 管理面 API key 30（服务语义 19：创建默认值 / 密钥生成 / 项目管理员门槛 / noauth / allowedIps / 重名回滚、更新 scope 与 append、状态 privacy 与 personal 所有权、profiles 脱钩与校验顺序、旋转、批量计数 / 所有权 / 空列表、列表与 node 隐私；端点线形 11：连接形状与 profiles / quota / 空 templateName / 空 mode、过滤与 NAME 排序游标、User/0 与空边、node 查询、创建 / 更新 / 状态 / 旋转 / profiles / 批量与错误扩展） + 管理面模型 25（服务语义 18：创建默认值与重名回滚、bulk 重复键与 Go slice 文案、更新 / 状态 / 删除前缀、批量状态、隐私拒绝、queryModels 分支与状态过滤、关联计数与未关联通道；端点线形 7：models 连接形状与 modelCard / settings JSON、where or 与空 or、createModel 默认值、updateModel clearRemark 与缺失文案、状态与批量变更、deleteModel、node(Model)） + 配额路由 43（routing 决策表 16 条与 IsBalanceLimit / ElapsedRatio、两阶段过滤与 dropped count、通道覆盖与 fail-closed、modality、nil provider / 空列表、设置与 legacy 映射及回退、orchestrator 的 QuotaExhausted / sticky 钉住与放弃 / 并发独立 dropped count / invalid-model、非流 503 信封、流式配额 envelope 与 BinaryStreamWriter 状态码） + 通道变换选项 43（ForceArrayInstructions / ForceArrayInputs / ReplaceDeveloperRoleWithSystem 与不可变性、ReasoningEffortMapping 与 Anthropic output_config.effort 同步、Responses custom tool 过滤、Anthropic 非流式 stream:false、require-streaming 强制流式与 state 同步、默认配置原样透传、非法 settings 宽松处理） + API key profile 模型映射 8（MapModel 8 种输入、XRegexp 7 子例、共享状态 8 子例、空模型拒绝、响应与流模型还原、pipeline 条件覆盖 parity、tester 前置映射） + orchestrator 入站中间件 48（配额窗口 / 聚合 / 时区 28、auto reasoning / model access / 顺序 / 403 信封 / 分钟配额 15、计费消息 5） + 项目提示词注入 63（匹配条件 16 与 ApplyPrompts 排序 / 前插 / 追加、注入中间件 24、decisions 字符串与数组逐字节改写、项目门控与失败开放 4、设置解码 8） + 管理面 trace / thread 23（根查询形状与 CREATED_AT→id 回退、where 子集、段 / 用量 / rawRootSegment、project / thread 边错误与 Thread/0、requests 边排序与时间谓词、threads 查询与 tracesSummary / archivedTracesCount、Thread.traces 的 CREATED_AT 顺序、空 where、node 解析与缺失文案、八个状态变更与文案、级联错误、项目成员作用域、service account 拒绝） + 管理面 requests 8（根查询形状与节点详情、正文 / chunk 规则、executions / usageLogs 连接、CREATED_AT→id 回退与 where 谓词、成本明细、node 错误文案；通道边隐私拒绝 / 加载失败包裹 / 零值短路） + 管理面 usageLogs 8（根连接形状与 CREATED_AT→id 排序重写、成本明细 / 档位、channel 边、where 谓词下推、node 详情与 ent 错误文案；四种上下文的隐私求值与拒绝文案） + 管理面 analytics 10（四个根字段线形、过滤器映射与交集、最早时间与隐私链、空用户过滤 FALSE、时区日桶与缺省 30 天、维度名称合成与性能合并、未知维度文案、read_dashboard 拒绝 FORBIDDEN、存储失败前缀） + 管理面 dashboard 13（21 个根字段线形、缓存前 read_dashboard 拒绝、overview 计数与容错零值、all-time 失败错误与 hard TTL 快照、channelSuccessRates 归一化与软删详情、fastest 置信度与过量采样、usage 聚合窗口规范化与共享缓存、daily 本地日补零、模型 / 通道性能排名与名称回退、usageStatsByUser owner 与 custom 窗口、apiKeyTokenUsageStats 校验与访问过滤、topRequestsProjects 跳过缺失与 FORBIDDEN） |
| **合计** | **6140** | **6140** | **0** | **0** | 四个测试项目全部通过（含 PostgreSQL Testcontainers） |

命令：`dotnet test AxonHub.slnx`（2026-11-06，Windows 11 / .NET SDK 10.0.401；
Antigravity Llm 层切片（§55）与通道接线（§56）、OpenaiResponses 通道接线（§57）、Commandcode 通道接线（§58）、别名通道类型接线（§59）、xai_subscription 通道与订阅 Llm 子包（§60）、anthropic_gcp 通道与 Vertex 执行器（§61）、静态托管与 SPA fallback（§62）、管理面 system-settings（§63）、管理面 users / roles（§64）、管理面 channels 读取（§65）、管理面 channels 生命周期写入（§66）、管理面 channels createChannel（§67）与 updateChannel（§68）、批量写入（§69）、端点保存（§70）、API key 记账（§71）、端点自动检测（§72）、定价写入（§73）、通道自动定价（§74）、通道重复 / 批量创建 / 批量导入（§75）、通道测试（§76）、模型同步（§77）、通道覆盖模板（§78）、自助身份（§79）、管理面登录（§80）、管理面 GraphQL 鉴权（§81）、OIDC 提供方配置 / providers 列表（§82）、OIDC 登录流程（§83）、权限校验（§84）与项目成员（§85）与项目管理（§86）、API key 管理（§87）、模型管理（§88）、公开模型列表（§89）、模型关联路由（§90）、trace/thread 通道粘性路由（§91）、配额路由闸门（§95）、通道变换选项（§96）、API key profile 模型映射（§97）、orchestrator 入站中间件（§98）、项目提示词注入（§99）、项目提示词管理面（§100）、追踪 / 线程管理面（§101）、请求管理面（§102）、用量日志管理面（§103）、analytics 页（§104）与 dashboard 首页（§105）、数据存储管理面（§106）、备份 / 恢复与自动备份管理面（§107）落地后复跑，全绿，表中数字取自该次运行；§108 的数据存储后端与自动备份调度随后落地，按项目规则未复跑，未计入该次运行）。

本轮验收运行时本机 Docker（29.2.0）可用，`AxonHub.Persistence.Tests` 的 276 例
全部通过；整解决方案并行运行一次通过（本次无 testhost 崩溃），表中数字取自该次运行。
持久化测试通过 Testcontainers 启动 `postgres:17-alpine`
（需要本机 Docker），每个测试使用独立 schema；M2 时期的 SQLite 测试已全部替换为
PostgreSQL 对应项。
写侧 create/update 由 EF Core 10.0.14 执行，读侧 get/list 由 Dapper 2.1.89 执行；
仓储注入 scoped `AxonHubDbContext`，写入加入调用方事务，读侧经同一连接/事务执行（`EfDapperInteropTests` 锁定）。
EF 模型（而非 EF 迁移）由 `EfCoreModelTests` 与幂等 DDL 建出的库结构逐列/逐索引对齐，运行时不创建版本表。

§108 追加 35 个用例（`StorageBackendTests` 12、`StorageProtocolTests` 10、`AutoBackupScheduleTests` 5、
`BackupServiceTests` 新增 8），按项目规则未运行 lint / build / test；上表仍是 §107 的实测值。§108 落地后的
静态计数为 `AxonHub.Api.Tests` 2772、四项目合计 6175，下一次全量运行应以该数字复核。

§109 追加 21 个用例（`ApiKeyProfileTemplateServiceTests` 14、`AdminApiKeyProfileTemplateEndpointTests` 3、
`ApiKeyProfileTemplateStoreTests` 4），按项目规则未运行 lint / build / test；上表仍是 §107 的实测值。§109 落地后的
静态计数为 `AxonHub.Api.Tests` 2789、`AxonHub.Persistence.Tests` 280、四项目合计 6196，下一次全量运行应以该数字复核。

§110 追加 73 个用例（`InvitationServiceTests` 33、`AdminInvitationEndpointTests` 22、`ManagementServiceTests`
新增 4、`InvitationStoreTests` 14），按项目规则未运行 lint / build / test；上表仍是 §107 的实测值。§110 落地后的
静态计数为 `AxonHub.Api.Tests` 2848、`AxonHub.Persistence.Tests` 294、四项目合计 6269，下一次全量运行应以该数字复核。

§111 追加 61 个用例（`AntigravityOAuthServiceTests` 35、`AdminAntigravityOAuthEndpointTests` 26），按项目规则
未运行 lint / build / test；上表仍是 §107 的实测值。§111 落地后的静态计数为 `AxonHub.Api.Tests` 2909、
`AxonHub.Persistence.Tests` 294、四项目合计 6330，下一次全量运行应以该数字复核。§111 同时落地
`Llm/Http/ProxyConfig.cs` / `ProxyExecutorFactory.cs`，其 URL 分支由上述 API 用例经 `IProxyExecutorFactory`
接缝验证，具体 `SocketsHttpHandler` 参数与残余差异见 C202。

§112 追加 26 个用例（`QuotaRoutingMigrationServiceTests` 19、`SystemStoreTests` 新增 6、
`ChannelWriteStoreTests` 新增 1），按项目规则未运行 lint / build / test；上表仍是 §107 的实测值。
§112 落地后的静态计数为 `AxonHub.Api.Tests` 2928、`AxonHub.Persistence.Tests` 301、四项目合计 6356，
下一次全量运行应以该数字复核。§112 同时把 `ChannelWriteStore.UpdateAsync` 改为加入 ambient 事务、
为 `ISystemStore` 增加 `CreateIfAbsentAsync` / `RunInTransactionAsync`，共享事务语义由
`SystemStoreTests` 与 `ChannelWriteStoreTests` 的真实 PostgreSQL 用例锁定。

§113 追加 63 个用例（`StoragePolicyTests` 13、`GcCronScheduleTests` 14 + 10 个 InlineData、`GcWorkerTests` 10、
`AdminGcEndpointTests` 7、`GcStoreTests` 9），按项目规则未运行 lint / build / test；上表仍是 §107 的实测值。
§113 落地后的静态计数为 `AxonHub.Api.Tests` 2982、`AxonHub.Persistence.Tests` 310、四项目合计 6419，
下一次全量运行应以该数字复核。§113 同时把 `DataStorageStore.GetPrimaryAsync` 的软删语义与 GC 的
`SkipSoftDelete` 上下文对齐，并新增 `GetByIdIncludingDeletedAsync`；两者由 `GcStoreTests` 的真实
PostgreSQL 用例锁定。

§114 追加 111 个用例（`ProviderQuotaServiceTests` 31、`ProviderQuotaDomainTests` 21 + 27 个 InlineData、
`ProviderQuotaCollectionSettingsServiceTests` 11、`AdminChannelsEndpointTests` +2、
`AdminSystemSettingsEndpointTests` +6、`AdminChannelUpdateEndpointTests` +4、`ProviderQuotaStatusStoreTests` 6、
`SchemaTests` +1、`ChannelStoreTests` +1、`UsageLogStoreTests` +1），按项目规则未运行 lint / build / test；
上表仍是 §107 的实测值。§114 落地后的静态计数为 `AxonHub.Api.Tests` 3084、
`AxonHub.Persistence.Tests` 319、四项目合计 6530，下一次全量运行应以该数字复核。§114 新增
`provider_quota_status` 表、`ProviderQuotaStatusStore`（Dapper）、`ChannelStore.ListEnabledByTypesAsync` 与
`UsageLogStore.SumCostByChannelAsync`，并把已退役 provider 行的启动清除并入迁移脚本；这些 SQL 由真实
PostgreSQL 用例锁定。

§115 追加 61 个用例（`AntigravityQuotaCheckerTests` 17 + 44 个 InlineData），同样按项目规则未运行
lint / build / test；§115 落地后的静态计数为 `AxonHub.Api.Tests` 3145、`AxonHub.Persistence.Tests` 319、
四项目合计 6591，下一次全量运行应以该数字复核。该切片不新增表或 SQL。

§116 追加 80 个用例（`ClaudeCodeQuotaCheckerTests` 13 + 67 个 InlineData），同样按项目规则未运行
lint / build / test；§116 落地后的静态计数为 `AxonHub.Api.Tests` 3225、`AxonHub.Persistence.Tests` 319、
四项目合计 6671，下一次全量运行应以该数字复核。该切片不新增表或 SQL。

§117 追加 83 个用例（`CodexQuotaCheckerTests` 27 + 56 个 InlineData），同样按项目规则未运行
lint / build / test；§117 落地后的静态计数为 `AxonHub.Api.Tests` 3308、`AxonHub.Persistence.Tests` 319、
四项目合计 6754，下一次全量运行应以该数字复核。该切片不新增表或 SQL；`ProviderQuotaDomainTests` 的
既有 merge round-trip 断言扩展为锁定重置清单的 Go RFC3339Nano 序列化（用例数不变）。

§118 追加 46 个用例（`XaiSubscriptionQuotaCheckerTests` 25 + 21 个 InlineData），同样按项目规则未运行
lint / build / test；§118 落地后的静态计数为 `AxonHub.Api.Tests` 3354、`AxonHub.Persistence.Tests` 319、
四项目合计 6800，下一次全量运行应以该数字复核。该切片不新增表或 SQL；`OAuthCredentialsJson` 新增
`ResolveChannelCredentials`（`ModelFetcher` 与 `ChannelOutboundFactory` 改为委托），既有用例数不变。

§119 追加 75 个用例（`GithubCopilotQuotaCheckerTests` 28 + 47 个 InlineData），同样按项目规则未运行
lint / build / test；§119 落地后的静态计数为 `AxonHub.Api.Tests` 3429、`AxonHub.Persistence.Tests` 319、
四项目合计 6875，下一次全量运行应以该数字复核。该切片不新增表或 SQL；
`CopilotOutboundTransformer.SetCopilotHeaders` 由 internal 改为 public（供配额检查器复用，参照侧本就是
导出函数），既有用例数不变。

§120 追加 105 个用例（`NanogptQuotaCheckerTests` 26 + 79 个 InlineData），同样按项目规则未运行
lint / build / test；§120 落地后的静态计数为 `AxonHub.Api.Tests` 3534、`AxonHub.Persistence.Tests` 319、
四项目合计 6980，下一次全量运行应以该数字复核。该切片不新增表或 SQL；`ChannelCredentials` 新增
`GetApiKeys`（原样读取 `apiKeys` 数组），`AxonHub.Llm` 的 `GoUrl` 新增 `TryParse`（复用 Codex 的
`net/url.Parse` 端口），既有用例数不变。

§121 追加 152 个用例（`ZenmuxQuotaCheckerTests` 32 + 120 个 InlineData），同样按项目规则未运行
lint / build / test；§121 落地后的静态计数为 `AxonHub.Api.Tests` 3686、`AxonHub.Persistence.Tests` 319、
四项目合计 7132，下一次全量运行应以该数字复核。该切片不新增表或 SQL；`ChannelCredentials` 新增
`GetManagementApiKey`（大小写折叠读取 `managementApiKey`，非字符串按 C013 视为缺失），既有用例数不变。

§122 追加 100 个用例（`ClineQuotaCheckerTests` 44 + 56 个 InlineData），同样按项目规则未运行
lint / build / test；§122 落地后的静态计数为 `AxonHub.Api.Tests` 3786、`AxonHub.Persistence.Tests` 319、
四项目合计 7232，下一次全量运行应以该数字复核。该切片不新增表或 SQL；只新增检查器、`Program.cs`
的 cline 注册与注册表文档，既有用例数不变。

§123 追加 209 个用例（`SyntheticQuotaCheckerTests` 40 + 169 个 InlineData），同样按项目规则未运行
lint / build / test；§123 落地后的静态计数为 `AxonHub.Api.Tests` 3995、`AxonHub.Persistence.Tests` 319、
四项目合计 7441，下一次全量运行应以该数字复核。该切片不新增表或 SQL；只新增检查器、`Program.cs`
的 synthetic 注册与注册表文档，既有用例数不变。

§124 追加 127 个用例（`NeuralWattQuotaCheckerTests` 31 + 96 个 InlineData），同样按项目规则未运行
lint / build / test；§124 落地后的静态计数为 `AxonHub.Api.Tests` 4122、`AxonHub.Persistence.Tests` 319、
四项目合计 7568，下一次全量运行应以该数字复核。该切片不新增表或 SQL；只新增检查器、`Program.cs`
的 neuralwatt 注册与注册表文档，既有用例数不变。

§125 追加 187 个用例（`ApertisQuotaCheckerTests` 54 + 82 个 InlineData + 51 个 MemberData），同样按项目
规则未运行 lint / build / test；§125 落地后的静态计数为 `AxonHub.Api.Tests` 4309、`AxonHub.Persistence.Tests`
319、四项目合计 7755，下一次全量运行应以该数字复核。该切片不新增表或 SQL；只新增检查器、`Program.cs`
的 apertis 注册与注册表文档，既有用例数不变。

§126 追加 122 个用例（`OpenCodeGoQuotaCheckerTests` 34 + 88 个 InlineData），同样按项目规则未运行
lint / build / test；§126 落地后的静态计数为 `AxonHub.Api.Tests` 4431、`AxonHub.Persistence.Tests` 319、
四项目合计 7877，下一次全量运行应以该数字复核。该切片不新增表或 SQL；只新增检查器、`Program.cs`
的 opencode_go 注册与注册表文档，既有用例数不变。

§127 追加 218 个用例（`KimiCodeQuotaCheckerTests` 56 + 162 个 InlineData），同样按项目规则未运行
lint / build / test；§127 落地后的静态计数为 `AxonHub.Api.Tests` 4649、`AxonHub.Persistence.Tests` 319、
四项目合计 8095，下一次全量运行应以该数字复核。该切片不新增表或 SQL；只新增检查器、`Program.cs`
的 kimi_code 注册与注册表文档，既有用例数不变。

§128 追加 161 个用例（`MinimaxQuotaCheckerTests` 37 + 124 个 InlineData），同样按项目规则未运行
lint / build / test；§128 落地后的静态计数为 `AxonHub.Api.Tests` 4810、`AxonHub.Persistence.Tests` 319、
四项目合计 8256，下一次全量运行应以该数字复核。该切片不新增表或 SQL；只新增检查器、`Program.cs`
的 minimax 注册与注册表文档，既有用例数不变。

§129 追加 144 个用例（`ZhipuQuotaCheckerTests` 40 + 104 个 InlineData），同样按项目规则未运行
lint / build / test；§129 落地后的静态计数为 `AxonHub.Api.Tests` 4954、`AxonHub.Persistence.Tests` 319、
四项目合计 8400，下一次全量运行应以该数字复核。该切片不新增表或 SQL；只新增检查器、`Program.cs`
的 zhipu 注册与注册表文档，既有用例数不变。

§130 追加 28 个用例（`ZaiQuotaCheckerTests` 10 + 18 个 InlineData），同样按项目规则未运行
lint / build / test；§130 落地后的静态计数为 `AxonHub.Api.Tests` 4982、`AxonHub.Persistence.Tests` 319、
四项目合计 8428，下一次全量运行应以该数字复核。该切片不新增表或 SQL；只新增检查器、`Program.cs`
的 zai 注册与注册表文档，既有用例数不变。

§131 追加 67 个用例（`CharmHyperQuotaCheckerTests` 14 + 53 个 InlineData），同样按项目规则未运行
lint / build / test；§131 落地后的静态计数为 `AxonHub.Api.Tests` 5049、`AxonHub.Persistence.Tests` 319、
四项目合计 8495，下一次全量运行应以该数字复核。该切片不新增表或 SQL；只新增检查器、`Program.cs`
的 charm_hyper 注册与注册表文档，既有用例数不变。

§132 追加 116 个用例（`CommandCodeQuotaCheckerTests` 41 + 75 个 InlineData），同样按项目规则未运行
lint / build / test；§132 落地后的静态计数为 `AxonHub.Api.Tests` 5165、`AxonHub.Persistence.Tests` 319、
四项目合计 8611，下一次全量运行应以该数字复核。该切片不新增表或 SQL；只新增检查器、`Program.cs`
的 commandcode 注册与注册表文档，并收拢 `ProviderQuotaService` 内重复的 Command Code 凭据解析，
既有用例数不变。

§133 追加 123 个用例（`OllamaQuotaCheckerTests` 30 + 93 个 InlineData），同样按项目规则未运行
lint / build / test；§133 落地后的静态计数为 `AxonHub.Api.Tests` 5288、`AxonHub.Persistence.Tests` 319、
四项目合计 8734，下一次全量运行应以该数字复核。该切片不新增表或 SQL；只新增检查器、`Program.cs`
的 ollama 注册、注册表文档，并把 `ProviderQuotaService` 的 ollama / ollama_anthropic 凭据判定收拢到
checker 的 `HasQuotaCredentials`，既有用例数不变。

§134 追加 24 个用例（`AxonHub.Llm.Tests` 的 `HttpRedirectTests` 16 个 Fact，加上 Api.Tests 的 8 个：
Ollama 2、Zenmux 1、ModelFetcher 2、PersistentOutboundRetry 的 1 Fact + 2 InlineData），同样按项目规则
未运行 lint / build / test；§134 落地后的静态计数为 `AxonHub.Api.Tests` 5296、`AxonHub.Persistence.Tests` 319、
四项目合计 8750，下一次全量运行应以该数字复核。该切片不新增表或 SQL；重写 `HttpExecutor` 的重定向循环
（`AllowAutoRedirect = false` 由宿主 handler 承担），新增策略类型并把降级拒绝接到命令码 / codex / Ollama /
Zenmux 四条路径，既有用例数不变。

§135 追加 77 个用例（`AxonHub.Api.Tests` 66：`RequestChunkStorageTests` 13 Fact + 8 InlineData、
`RequestPayloadStorageTests` 44 Fact、`RequestChainPersistenceTests` 1 Fact；`AxonHub.Persistence.Tests` 11：
`DataStorageBootstrapTests` 4、`RequestStoreTests` 3、`RequestExecutionStoreTests` 3、`DataStorageStoreTests` 1），
同样按项目规则未运行 lint / build / test；§135 落地后的静态计数为 `AxonHub.Api.Tests` 5362、
`AxonHub.Persistence.Tests` 330、四项目合计 8827，下一次全量运行应以该数字复核。该切片不新增表或 SQL；
新增 `StoragePolicy` / `StoragePolicyCodec`、`RequestPayloadStorage`、`RequestChunkStorage` 与启动期
`DataStorageBootstrap`（v0.4.0 主存储种子），把 `RequestChainRecorder` 的分块归档、GraphQL / trace 的
正文加载与存储策略门禁接到请求链路，C020 关闭，既有用例数不变。

§136 追加 10 个用例（`AxonHub.Api.Tests` 9：`AdminChannelsEndpointTests` 的 node(Channel)、
前端 `GetChannelModelPrices` 文档、版本 / 反向边、枚举失败、反向边 NotFound 与 4 个隐私分支；
`AxonHub.Persistence.Tests` 1：`ChannelModelPriceStoreTests.GetByIdReturnsOnlyLiveRows`），同样按项目
规则未运行 lint / build / test；§136 落地后的静态计数为 `AxonHub.Api.Tests` 5371、
`AxonHub.Persistence.Tests` 331、四项目合计 8837，下一次全量运行应以该数字复核。该切片不新增表或 SQL；
新增 `ChannelPrivacy`、`ChannelQueryService.GetNodeAsync`、`ChannelAdminStore.GetByIdAsync` 与
`ChannelModelPriceStore.GetByIdAsync`，把 `node(Channel)`、`Channel.channelModelPrices`、
`ChannelModelPrice.channel` / `versions`、`ChannelModelPriceVersion.channelModelPrice` 接到 ent 形状与
read_channels 查询隐私，C048 关闭（读取面），C165 的 Channel 分支关闭，既有用例数不变。

§137 追加 13 个用例（`AxonHub.Llm.Tests`：`GoDoubleConverterTests` 的请求标量 6 个 InlineData、
logprob 3 个 InlineData、可空省略 / embedding 向量 / category_scores 字典 / Go 与 STJ 指数往返
4 个 Fact），同样按项目规则未运行 lint / build / test；§137 落地后的静态计数为
`AxonHub.Llm.Tests` 1468、四项目合计 8850（`AxonHub.Api.Tests` 5371、`AxonHub.Persistence.Tests`
331、`AxonHub.Grains.Tests` 1680 不变），下一次全量运行应以该数字复核。该切片不新增表或 SQL；
`LlmJson.Options` 注册 `GoDoubleJsonConverter`（`Embedding` 向量与 `ModerationClassification`
的 `category_scores` 手写 writer 同步改用 `GoJsonFloat`），所有 LLM wire 载荷的 double 在任意
量级都与 Go `encoding/json` 一致，C067 关闭。

§138 追加 5 个用例（`AxonHub.Api.Tests`：`ChannelLimiterMetricsTests` 的 3 个参照 metrics
用例 `NilMeterIsNoop` / `GaugeCallbackEmitsLimiterStats` / `CountersAndHistogram`，加 2 个接线
守卫 `QueueFullRejectionIncrementsTheCounter` / `SuccessfulAcquireRecordsTheWait`），同样按项目规则
未运行 lint / build / test；§138 落地后的静态计数为 `AxonHub.Api.Tests` 5376、四项目合计 8855
（`AxonHub.Llm.Tests` 1468、`AxonHub.Persistence.Tests` 331、`AxonHub.Grains.Tests` 1680 不变），
下一次全量运行应以该数字复核。该切片不新增表或 SQL；`ChannelLimiterMetrics` 在
`System.Diagnostics.Metrics` 的 `axonhub` Meter 上注册五个同名 instrument（gauge 来自
`ChannelLimiterManager.Snapshot()`，counter / histogram 由 `ChannelLimiterMiddleware` 在拒绝与
成功获准路径上报，等待时长经 `TimeProvider` 计时），C044 关闭；同时把参照通用 metrics 子系统
（provider / config + `WithMetrics()` HTTP 中间件）登记为 C223 真实缺口。

§139 追加 26 个用例（`ServerMetricsTests` 3、`MetricsOptionsTests` 14、
`HttpMetricsMiddlewareTests` 9）；参照 `internal/metrics` 无测试文件，这些全部是端口接线/
行为守卫。同样按项目规则未运行 lint / build / test，§139 落地后的静态计数为
`AxonHub.Api.Tests` 5402、四项目合计 8881（`AxonHub.Llm.Tests` 1468、
`AxonHub.Persistence.Tests` 331、`AxonHub.Grains.Tests` 1680 不变），下一次全量运行应以
该数字复核。该切片不新增表或 SQL；通用 metrics 子系统（`metrics.enabled` / exporter 配置、
九个 instrument、5 秒周期 stdout/OTLP 导出、`WithMetrics()` 等价 HTTP 中间件、gin FullPath
转换、panic 不记录）已按参照移植，C223 关闭。

§140 追加 13 个用例（`AxonHub.Llm.Tests` 12：`OpenAiRequestTypeCheckTests` 1、
`OpenAiResponseNullSemanticsTests` 5、`OpenAiImageOutboundTests` 1、`JinaRerankTransformerTests` 2、
`ClineOutboundTransformerTests` 1、`AnthropicOutboundTransformerTests` 1、
`ResponsesOutboundTransformerTests` 1；`AxonHub.Api.Tests` 1：`ChannelOverrideOperationsTests`），
同样按项目规则未运行 lint / build / test；§140 落地后的静态计数为 `AxonHub.Llm.Tests` 1480、
`AxonHub.Api.Tests` 5403、四项目合计 8894（`AxonHub.Persistence.Tests` 331、
`AxonHub.Grains.Tests` 1680 不变），下一次全量运行应以该数字复核。该切片不新增表或 SQL；
统一模型与线上模型的 index 加宽为 long（流聚合字典键同步加宽），响应转换对 Go 值结构切片做
null→零值替换，`objects.ParseOverrideOperations` 的 index 放宽到 int64，C022 关闭；残余的普通
`List<string>` null 元素登记为 C224 已接受差异。

§141 追加 12 个用例（`AxonHub.Api.Tests` 10：`ResponsesSessionWiringTests` 6、
`PersistentOutboundRetryTests` 新增 4；`AxonHub.Llm.Tests` 2：`DisposeCallbackStreamTests`），
同样按项目规则未运行 lint / build / test；§141 落地后的静态计数为 `AxonHub.Llm.Tests` 1482、
`AxonHub.Api.Tests` 5413、四项目合计 8906（`AxonHub.Persistence.Tests` 331、
`AxonHub.Grains.Tests` 1680 不变），下一次全量运行应以该数字复核。该切片不新增表或 SQL；
Responses 生产链路按 `RawRequest.SessionId` / `SessionScope` 注入 trace 会话，`IStoppableOutbound`
在换模型 / 换通道 / TransformRequest 替换 / 编排收尾与流 Dispose 时按尝试停止旧 transformer，
C072 收窄为端点记录传输与通道级连接复用两项残余。

§142 追加 8 个用例（`AxonHub.Api.Tests` 8：`CodexResponseHeaderForwardingTests` 5 个方法 / 7 例、
`ChatCompletionOrchestratorTests` 新增 1），同样按项目规则未运行 lint / build / test；§142 落地后的
静态计数为 `AxonHub.Api.Tests` 5421、`AxonHub.Llm.Tests` 1482、四项目合计 8914
（`AxonHub.Persistence.Tests` 331、`AxonHub.Grains.Tests` 1680 不变），下一次全量运行应以该数字复核。
该切片不新增表或 SQL；API 层按 `api/chat.go:146-221` 移植 `writeForwardResponseHeaders` /
`copyCodexTurnStateHeader` / `primeCodexTurnStateHeader` 与 `primedStream`，
`ChatCompletionResult` 携带 `CodexResponseHeadersSupported`，流式响应在按尝试 Stop 包装后重新附加
response headers 元数据（`X-Reasoning-Included` 与 `/responses` 的 `X-Codex-Turn-State`），
C094 item (7) 关闭。

§143 追加 28 个用例（`AxonHub.Llm.Tests` 19：`ChannelEndpointsTests` 7 个方法 / 19 例；
`AxonHub.Api.Tests` 9：`ResponsesEndpointDispatchTests`），同样按项目规则未运行 lint / build / test；
§143 落地后的静态计数为 `AxonHub.Llm.Tests` 1501、`AxonHub.Api.Tests` 5430、四项目合计 8942
（`AxonHub.Persistence.Tests` 331、`AxonHub.Grains.Tests` 1680 不变），下一次全量运行应以该数字复核。
该切片不新增表或 SQL；端点运行时解析按 live 树的 `channel_endpoint_responses.go` /
`channel_llm.go:217-620` / `tester.go:130-221` 移植（`NormalizeEndpoints`、`DefaultEndpoints`、
`legacyResponsesFormat`、`PrimaryEndpointTransport`、`FindUserEndpoint` 与 `normalizeModelProtocols`
的模型协议改写），Responses 家族的出站 base URL / path / transport 由最后一次归一化匹配的端点提供，
transformer 的 `APIFormat()` 在 websocket 传输下返回 `openai/responses-ws`，GraphQL 的
`defaultEndpoints` 也跟随存储的 codex 意图；C094 item (1) 与 C072 残余 (a) 收窄为
「非 Responses 端点与部分供应商家族仍按通道类型分派」及通道级连接复用；写入面的端点规范化仍按
冻结基线（C144/C148）。

§144 追加 14 个用例（`AxonHub.Grains.Tests` 12：`ChannelGrainRateLimitTests` 8、
`RateLimitAwareScorerTests` 新增 4；`AxonHub.Api.Tests` 2：`ChannelRateLimitRecorderTests`），
同样按项目规则未运行 lint / build / test；§144 落地后的静态计数为 `AxonHub.Grains.Tests` 1692、
`AxonHub.Api.Tests` 5432、`AxonHub.Llm.Tests` 1501、四项目合计 8956
（`AxonHub.Persistence.Tests` 331 不变），下一次全量运行应以该数字复核。该切片不新增表或 SQL；
限流状态按通道驻留 `IChannelGrain`（RPM/TPM 自然分钟桶、extend-only 冷却、Reset），
API 中间件经 `IChannelRateLimitRecorder` 写穿、`ChannelPoolGrain` 从 `ChannelMetricsSnapshot`
评分，阻塞并发 limiter 仍为每 silo 单例（C042）；多 silo 的预算全局化登记为 C225。

§145 追加 15 个用例（`AxonHub.Grains.Tests` 12：`EnabledChannelsGrainTests` 11、
`EnabledChannelSnapshotTests` 1；`AxonHub.Api.Tests` 2：`EnabledChannelsGrainProviderTests`；
`AxonHub.Persistence.Tests` 1：`ChannelStoreTests.GetLatestUpdatedAtSeesSoftDeletedChannels`），
同样按项目规则未运行 lint / build / test；§145 落地后的静态计数为 `AxonHub.Grains.Tests` 1704、
`AxonHub.Api.Tests` 5434、`AxonHub.Persistence.Tests` 332、`AxonHub.Llm.Tests` 1501、四项目合计 8971，
下一次全量运行应以该数字复核。该切片不新增表或 SQL；启用通道缓存以 `EnabledChannelsGrain` 单激活
持有（1 分钟周期 + `updated_at` 指纹跳过 + 变更后 500ms 防抖强制刷新 + API key 不可用 10s 同步重载），
选择回退路径经 `GrainEnabledChannelProvider` 读取快照，关联解析与模型列表仍实时读库；
集群单激活、懒加载、读者排队与资源清理缺口登记为 C226。Grain 测试的 cluster fixture 改为
collection fixture，避免静态注入的假 provider 在并发建 cluster 时互相覆盖。

§146 追加 23 个用例（`AxonHub.Llm.Tests` 5：`OAuthTokenProviderConflictTests`；
`AxonHub.Grains.Tests` 8：`ChannelTokenGrainTests`；`AxonHub.Api.Tests` 6：`ChannelTokenSourceTests`；
`AxonHub.Persistence.Tests` 4：`ChannelTokenStoreTests`），同样按项目规则未运行 lint / build / test；
§146 落地后的静态计数为 `AxonHub.Llm.Tests` 1506、`AxonHub.Grains.Tests` 1712、
`AxonHub.Api.Tests` 5440、`AxonHub.Persistence.Tests` 336，四项目合计 8994，
下一次全量运行应以该数字复核。该切片不新增表或 SQL；OAuth 令牌状态按通道驻留
`ChannelTokenGrain`（懒种子、grain 串行替代 singleflight、5 分钟窗口、1 分钟周期器、
冲突胜利者采纳 / 非 OAuth 退休），刷新写回经 `IChannelTokenStore` 的 `updated_at` 乐观条件写，
codex（fenno 除外）/ claudecode / xai_subscription 的出站经 `GrainTokenGetter` 读取；
fenno / github_copilot / antigravity / ModelFetcher / 端点探测仍用本地 provider；
写回语义局部跟进上游 `f272549f`（冻结基线只有无条件写回），漂移记录见 00-reference，
差异登记为 C227（accepted），C094 item (2)(3)(4) 对三类通道关闭。

§147 追加 9 个用例（`AxonHub.Grains.Tests` 4：`PreviousChannelGrainTests`；
`AxonHub.Api.Tests` 5：`GrainPreviousChannelProviderTests`），同样按项目规则未运行
lint / build / test；§147 落地后的静态计数为 `AxonHub.Grains.Tests` 1716、
`AxonHub.Api.Tests` 5445、`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 336，
四项目合计 9003，下一次全量运行应以该数字复核。该切片不新增表或 SQL；previous-channel
粘性缓存按缓存键驻留 `PreviousChannelGrain`（30 分钟 TTL、读取惰性过期、
`DelayDeactivation` 防止激活在 TTL 前被回收），生产 `IPreviousChannelProvider` 为
`GrainPreviousChannelProvider`（同步桥接、读取失败返回 0、写入失败丢弃），Orleans 集群内
共享同一份 trace/thread 亲和；`PreviousChannelCache` 保留给直接构造与测试。
C181 由 accepted 转为 resolved：多实例不共享的缺口关闭；残余为 Redis / two-level
xcache 后端不支持（与 C171 同源）与 silo 重启丢失内存条目（参照 memory 模式相同）。

§148 追加 9 个用例（`AxonHub.Grains.Tests` 5：`ChannelModelSyncGrainTests`；
`AxonHub.Api.Tests` 4：`GrainChannelModelSyncGuardTests` 1 与 `ChannelModelSyncTests` 3），
同样按项目规则未运行 lint / build / test；§148 落地后的静态计数为
`AxonHub.Grains.Tests` 1721、`AxonHub.Api.Tests` 5449、`AxonHub.Llm.Tests` 1506、
`AxonHub.Persistence.Tests` 336，四项目合计 9012，下一次全量运行应以该数字复核。
该切片不新增表或 SQL；参照的 cron `11 * * * *`（UTC）由 `ChannelModelSyncScheduler` 复刻
（注册时不运行、错过不补跑），执行窗口驻留单例 `ChannelModelSyncGrain`
（`now.Truncate(interval)` 对齐、每窗口一次、grain turn 替代 `modelSyncMu`、
`DelayDeactivation` 跨 tick 保活），生产 `IChannelModelSyncGuard` 为
`GrainChannelModelSyncGuard`（频率映射 60/360/1440 分钟、分派失败跳过本 tick），
`ChannelModelSyncService` 在无守卫的直接构造下回退进程内窗口。C158 的第 ① 项
（后台周期注册缺失）关闭，仅剩按通道代理与 live-only 分支；新增 C228（accepted）：
执行窗口由每进程一份收窄为集群单激活，单 silo 等价，多 silo 下参照可能每个进程各跑一遍，
端口整个集群只跑一遍。

§149 追加 15 个用例（`AxonHub.Grains.Tests` 10：`AutoBackupGrainTests` 6、`GcGrainTests` 4；
`AxonHub.Api.Tests` 5：`GrainScheduledTaskTests` 3、`GrainSchedulerBridgeTests` 1、
`BackupServiceTests.RescheduleSignalsTheScheduledTask` 1），同样按项目规则未运行
lint / build / test；§149 落地后的静态计数为 `AxonHub.Grains.Tests` 1731、
`AxonHub.Api.Tests` 5454、`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 336，
四项目合计 9027，下一次全量运行应以该数字复核。该切片不新增表或 SQL；参照的备份 cron
`0 2 * * *`（系统时区）与 GC cron `gc.cron`（UTC）改由单例 `AutoBackupGrain` / `GcGrain`
的 Orleans reminder 承载（启动激活器只装填不运行、触发前先装填下一次、过期 tick 去重、
任务失败不丢调度、设置变更后从调用时刻重装、错过不补跑），`AutoBackupScheduler` /
`GcScheduler` 轮询宿主已删除，silo 启用 `UseInMemoryReminderService` 并新增
`Microsoft.Orleans.Reminders` 包（`IRemindable` 与 `RegisterOrUpdateReminder` 不在 Server 包内）。
C197 的「永不自动备份」、C199 的轮询延迟与 C203(3) 的调度器描述随之更新；新增 C229
（accepted）：每进程 cron 收窄为集群单激活 reminder，多 silo 下参照各跑一遍、端口整集群
只跑一遍，内存 reminder 表随 silo 停止丢失并由启动激活器重装（不补跑），冷启动 reminder
表刷新带来数十秒首次延迟，reminder 最小周期使极端早到 tick 最坏晚 1 分钟。

§150 追加 28 个用例（`AxonHub.Grains.Tests` 15：`ProviderQuotaGrainTests` 5、
`ProviderQuotaCoordinatorGrainTests` 10；`AxonHub.Api.Tests` 13：
`ProviderQuotaGrainBridgeTests` 8、`ProviderQuotaServiceTests` 5），同样按项目规则未运行
lint / build / test；§150 落地后的静态计数为 `AxonHub.Grains.Tests` 1746、
`AxonHub.Api.Tests` 5467、`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 336，
四项目合计 9055，下一次全量运行应以该数字复核。该切片不新增表或 SQL；参照的 `svc.mu`
改由单例 `ProviderQuotaCoordinatorGrain` 的 grain turn 承担，固定 5 分钟的
`provider-quota-check` cron 改由同名 Orleans reminder 承载（启动激活器只装填不运行、
触发前先装填下一次、过期 tick 去重、失败不丢调度），每个 due 组派给组长通道的
`ProviderQuotaGrain` 执行并把成员缓存条目镜像到各通道 grain；`ProviderQuotaScheduler`
轮询宿主已删除，`ProviderQuotaService` 保留进程内 `_quotaCache` 作为同步路由读模型与
唯一写入口，直接构造时沿用 `_mutex` 路径。新增 C230（accepted）：单 silo 下互斥、调度、
并发与失败语义等价；多 silo 下参照各进程各采集各更新缓存、端口整个集群只采集一轮，
非承载 silo 的读缓存不会更新（有意的收窄，项目只支持单 silo）；通道 grain 的镜像启动为
空且从不参与决策；runners 的意外异常被组长 grain 捕获记录，参照会 panic 进程。

§151 追加 32 个用例（`AxonHub.Api.Tests` 18：`AdminPromptProtectionRulesEndpointTests`；
`AxonHub.Persistence.Tests` 14：`PromptProtectionRuleStoreTests`），同样按项目规则未运行
lint / build / test；§151 落地后的静态计数为 `AxonHub.Grains.Tests` 1746、
`AxonHub.Api.Tests` 5485、`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 350，
四项目合计 9087，下一次全量运行应以该数字复核。该切片接入 `promptProtectionRules` 连接、
`createPromptProtectionRule` / `updatePromptProtectionRule` /
`deletePromptProtectionRule` / `updatePromptProtectionRuleStatus` /
`bulkDelete|Enable|DisablePromptProtectionRules` / `previewPromptProtectionRule` 八个变更
（根 `node` 查询不含规则：参照的 `guidTypeToNodeType` 未登记该类型，端口同样报 `unknown node type`）：
`PromptProtectionRuleManagementService` 逐项复刻参照的校验顺序（先编译正则，再 settings 必填、
action 合法、mask 必须有 replacement、scope 合法）、先读后校验再写的更新路径、`DUPLICATE_NAME`
扩展与四个 `failed to ... prompt protection rule` 错误前缀；`PromptProtectionRuleStore` 以 EF
创建、Dapper 读取/更新，软删谓词只加在查询上，`(name, deleted_at)` 唯一索引与批量聚合语义由
14 例锁定；settings 的写入按 Go `json.Marshal` 的 `omitempty` 形状、输出按 gqlgen 的
`replacement=""` / `scopes=[]`。新增 C231（accepted）：where 只暴露前端实际使用的子集
（未移植的时间/description/pattern 谓词在输入校验阶段被拒绝）、HotChocolate 输入枚举严格拒绝
（参照 gqlgen 透传字符串，未知 action 由服务层报 `invalid action: <x>`）、未知存储枚举值的输出
抛错，规则 scopes 策略仍归 C014。C092 由 gap 改为 accepted：请求侧 71 例与管理面 32 例锁定，
残余收窄为无 30 秒 live 缓存、写入不触发缓存重载、.NET Regex 与 regexp2 尚未逐特性对照、
Gemini/Ollama 载荷级对照延后。

§152 追加 25 个用例（`AxonHub.Api.Tests` 22：`AdminSystemSettingsEndpointTests`；
`AxonHub.Persistence.Tests` 3：`SystemStoreTests`），同样按项目规则未运行 lint / build / test；
§152 落地后的静态计数为 `AxonHub.Grains.Tests` 1746、`AxonHub.Api.Tests` 5507、
`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 353，四项目合计 9112，下一次全量运行
应以该数字复核。该切片接入 system-settings 第二批 12 个根字段：`brandSettings` /
`systemChannelSettings` / `systemGeneralSettings` / `userAgentPassThroughSettings` /
`passThroughSettings` / `usageCostInjectionSettings` 六个查询与对应的六个变更。
`SystemSettingsCodec` 复刻 Go 的序列化形状（字段序、HTML 转义、AutoSyncFrequency 永不失败的
JSON 解码）与 normalize / validate（空白提示词恢复默认且非空白不裁剪、未知 auto-sync → 1h、
系统提示词先校验、4096 rune 上限）；`SystemStore.UpdateValueIfUnchangedAsync` 以
(id, updated_at, value, deleted_at = 0) 谓词实现 UpdateChannelSetting 的五次 compare-and-set，
`TryCreateAsync` 把唯一键冲突映射为 `ent.IsConstraintError`（继续重试）；general 写入成功后经
`IAutoBackupRescheduleSignal` 重排自动备份（无 Orleans 的宿主为空操作，生产实现与参照一样只
记录调度失败，不影响变更结果）。新增 C232（accepted）：非法枚举输入的拒绝文案来自
HotChocolate 校验器，而非参照 UnmarshalGQL 的 `invalid ProbeFrequency: <x>` /
`invalid AutoSyncFrequency: <x>`，两侧都拒绝且不产生写入；合法输入的映射、写入顺序与逐层
错误前缀（含 brand 读取与 general 写入的双前缀）逐字一致，字段级 JSON 类型错误沿用 C064，
`timeLocation` 缓存失效因端口每次读取时区而不可观察（C186）。

§153 追加 18 个用例（`AxonHub.Api.Tests` 18：`AdminSystemSettingsEndpointTests`），同样按
项目规则未运行 lint / build / test；§153 落地后的静态计数为 `AxonHub.Grains.Tests` 1746、
`AxonHub.Api.Tests` 5525、`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 353，四项目
合计 9130，下一次全量运行应以该数字复核。该切片接入 system-settings 第三批 9 个根字段：
`videoStorageSettings` / `proxyPresets` / `onboardingInfo` 三个查询与
`updateVideoStorageSettings` / `saveProxyPreset` / `deleteProxyPreset` 与三个
`complete*Onboarding` 六个变更。视频设置复刻默认 false/0/1/50、`<=0` 扫描值归一、Enabled 时
对 data storage 的四种拒绝（缺 ID / 不存在 / Primary / database），写入成功后调用可选
`IVideoStorageRescheduleSignal`；代理预设复刻缺失 / `null` → `[]`、按 URL 就地替换与追加、
omitempty 字段、删除过滤与逐层错误前缀；onboarding 复刻共享 `time.Now()`、仅自动创建缺失的
通道模块、模块完成只覆盖自身与逐层错误前缀。新增 C233（accepted）：onboarding 时间戳在
GraphQL 输出为 UTC（参照保留进程本地偏移，同一瞬时），非法 `completed_at` 的解析文案不同；
新增 C234（gap）：视频存储扫描 worker 未移植，设置面等价但后台扫描不存在。

§154 追加 70 个用例（`AxonHub.Api.Tests` 70：`AdminSystemSettingsEndpointTests` 3、
`AdminSystemVersionEndpointTests` 67），同样按项目规则未运行 lint / build / test；§154
落地后的静态计数为 `AxonHub.Grains.Tests` 1746、`AxonHub.Api.Tests` 5595、
`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 353，四项目合计 9200，下一次全量
运行应以该数字复核。该切片接入 2 个查询与 1 个变更：`webhookNotifierConfig`、
`systemVersion`、`checkForUpdate` 与 `updateWebhookNotifierConfig`。webhook 配置复刻
Go 文档编解码（字段序、proxy omitempty、headers / target_names nil 写 null、缺行归一、
`input:{}` → `{targets:[],subscriptions:[]}`）与三层错误前缀；nil 列表在输出侧由
gqlgen 列表 marshaler 渲染为 `[]`，端口同样合并为空列表。版本面复刻出站请求（URL 键序、
Accept、User-Agent、GET、10 秒超时）、30 分钟冷却、draft / 服务前缀 / 非 beta 预发布
过滤、beta10 数值比较与三层错误链；uptime 按 Go `time.Duration.String()` 布局。新增
C235（accepted）：systemVersion 的构建元数据随 .NET 运行时变化（版本固定冻结参照值，
commit / buildTime 为空，goVersion / platform / uptime 为环境相关），checkForUpdate 的
错误链内层文本来自 .NET 异常与 STJ；新增 C236（gap）：webhook 通知器运行时（通道
自动禁用时按订阅发送）未移植，管理面配置读写完整但配置当前不被消费。

§155 追加 64 个用例（`AxonHub.Api.Tests` 64：`CatalogFilterTests` 36、
`AdminCatalogEndpointTests` 19、`CatalogServiceTests` 9），同样按项目规则未运行
lint / build / test；§155 落地后的静态计数为 `AxonHub.Grains.Tests` 1746、
`AxonHub.Api.Tests` 5659、`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 353，
四项目合计 9264，下一次全量运行应以该数字复核。该切片接入 2 个查询与 2 个变更：
`catalogSettings` / `providersCatalog` 与 `updateCatalogSettings` /
`refreshProvidersCatalog`。设置面复刻 `catalog_settings` 文档（字段序、TrimSpace、
<=0→3600、<60→60、>604800→604800）、缺行默认、双层读取前缀与 `updateCatalogSettings`
的逐字段 patch + Invalidate/Reschedule 副作用；上游 URL 校验逐字符复刻 Go 1.26.3
`net/url.Parse`（28 例 oracle 的接受 / 拒绝与错误文本，含 8 个接受例）。目录面复刻
进程级单例缓存、singleflight 并发去重、鲜度窗口、20 秒抓取超时、失败回落内嵌 /
last-good、filtered 默认 true 与允许列表 + meta/llama、ibm、bytedance、tencent、
xiaomi、kwaipilot、nvidia、thinkingmachines 后处理、extra models 合并与
release_date 降序 + id 升序排序；`refreshProvidersCatalog` 强制抓取且失败不回落。
新增 C237（accepted）：URL 校验不读取 `GODEBUG=urlstrictcolons=0`，`%q` 引号对不可打印
非 ASCII / 非法 UTF-8 的转义与 `strconv.Quote` 不同（仅在解析失败时可见）；新增
C238（accepted）：调度器与作用域实现细节（BackgroundService + TCS 重排、scoped
settings 读取、无 tick 补发、串行刷新不重叠），可观察行为由测试锁定。

§156 追加 26 个用例（`AxonHub.Api.Tests` 26：`CacheDiagnosticsTests` 16、
`GoJsonIndentTests` 4、`AdminDiagnosticsEndpointTests` 6），同样按项目规则未运行
lint / build / test；§156 落地后的静态计数为 `AxonHub.Grains.Tests` 1746、
`AxonHub.Api.Tests` 5685、`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 353，
四项目合计 9290，下一次全量运行应以该数字复核。该切片接入 1 个查询与 1 个变更：
`getCacheDiagnostics` 与 `clearCache`。导出面复刻参照的字段序、omitempty 与
nil / 空数组边界（`lo.Map(nil)` → `[]`、`append(nil, ...)` → `null`）、通道
（ordering_weight 降序 + id 升序）与模型（id 升序）查询顺序、两次时钟读取
（exportedAt / capturedAtUtc 与文件名）、启用 / 禁用 API key 计数、settings /
modelEntries / 候选预览，以及 `json.MarshalIndent` 的缩进遍历与 Go 条件值编码
（整数定点、浮点 Go 格式、map 键排序）；清缓存复刻 owner 门、targets 去重排序与
逐目标重载错误链。新增 C239（accepted）：候选预览复用 owner-bypass 的模型选择器，
`modelAssociationCache` 恒为 `[]`（关联解析缓存随 C179 未移植）；新增 C240
（accepted）：两次时钟读取、导出时重算启用 key 计数、clearCache 重载不接收取消令牌。

§157 追加 34 个用例（`AxonHub.Api.Tests` 28：`SystemInitializationServiceTests` 9、
`AdminSystemEndpointTests` 19；`AxonHub.Persistence.Tests` 6：`DataStorageBootstrapTests`
的启动闸用例），同样按项目规则未运行 lint / build / test；§157 落地后的静态计数为
`AxonHub.Grains.Tests` 1746、`AxonHub.Api.Tests` 5713、`AxonHub.Llm.Tests` 1506、
`AxonHub.Persistence.Tests` 359，四项目合计 9324，下一次全量运行应以该数字复核。该切片
接入 2 个 admin 端点与 `/health` 对齐：`GET /admin/system/status`、`POST
/admin/system/initialize`，并把启动时的 v0.4.0 Primary 播种改为仅在 system_initialized
为 true 时运行。状态面复刻大小写不敏感的 initialized 判定与 500 信封；初始化面复刻 gin
绑定（email、min=6 按 rune、三个必填名称）、400 / 500 分类、已初始化文案与成功响应逐字节，
并在一个事务内按参照顺序创建 owner（scopes `["*"]`）、默认项目（Default / Default
project，Admin / Developer / Viewer 三角色 + owner 关联）、JWT secret（32 字节小写十六
进制）、品牌名、Primary 存储、default_data_storage_id、initialized 标志与 system_version，
逐一保留参照错误前缀（含 HashPassword 双层与 failed to create system setting 内层）；
`/health` 复刻 build.Info 的字段序、commit / build_time 的 omitempty 与 timestamp 的
RFC3339Nano。新增 C241（accepted）：Primary settings 写 `"{}"`（C196）、build 元数据环境
差异（C235）、JSON 边界（C201）、email / rune 校验近似（C166）、时间戳来源与无迁移记账表
（C020）；新增 C242（gap）：GET /favicon 尚未接线，前端图标回落到静态 index。

§158 追加 14 个用例（`AxonHub.Api.Tests` 14：`FaviconEndpointTests`），同样按项目规则未
运行 lint / build / test；§158 落地后的静态计数为 `AxonHub.Grains.Tests` 1746、
`AxonHub.Api.Tests` 5727、`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 359，
四项目合计 9338，下一次全量运行应以该数字复核。该切片接入 `GET /favicon`：内嵌
favicon.ico 与参照逐字节一致（28,882 字节，SHA-256 `faadeaf0…eefed`），无品牌标识时返回
`image/x-icon` 与 `Cache-Control: public, max-age=3600` 且不显式设置 Content-Length，
品牌读取失败只经 ILogger 记录并回退默认图标；data URL 分支复刻 `data:` 前缀、按逗号恰好
两段、首个 `:` 与 `;` 之间的 MIME 提取、四种 `Invalid brand logo format` 与 base64 失败
的 `Failed to decode brand logo`，成功时返回该 MIME、同一缓存头与显式 Content-Length。
base64 的接受 / 拒绝集合按 Go 1.26 `decodeQuantum` 对齐（只忽略 CR/LF、要求完整填充、
忽略非零尾随位），`IsGoBase64` 预检把 `Convert.FromBase64String` 对空格 / 制表符的额外
容忍收窄回参照字符集。C242 由 gap 改为 resolved，唯一残余为日志通道 / 类别名不同。

§159 追加 24 个用例（`AxonHub.Api.Tests` 24：`ClaudeCodeSafeguardsSettingsServiceTests` 19、
`AdminClaudeCodeSafeguardsEndpointTests` 5），同样按项目规则未运行 lint / build / test；
§159 落地后的静态计数为 `AxonHub.Grains.Tests` 1746、`AxonHub.Api.Tests` 5751、
`AxonHub.Llm.Tests` 1506、`AxonHub.Persistence.Tests` 359，四项目合计 9362，下一次全量
运行应以该数字复核。该切片为 post-baseline 漂移（冻结基线 `b8bb3421` 无 safeguards 包与
claudeCodeSafeguards schema），按 `f272549f` 接入 Claude Code safeguards 设置面：
`claudeCodeSafeguardsSettings` 查询、`updateClaudeCodeSafeguardsSettings` /
`resetClaudeCodeSafeguardsSettings` 变更与 `ClaudeCodeSafeguardsConfig` /
`ClaudeCodeSafeguardsSettings` 输出类型；默认值（timeout 30s / maxTokens 4096 /
transcriptCharBudget 24000 / DefaultPolicy）、零值修补、`"null"` 重置、直接读、Go 字段序
JSON（空 fallback 为 `[]`）、`time.Duration` 纳秒截断与 15 条校验文案均按参照锁定，
读取 / 写入分别要求 `read_settings` / `write_settings`。评审运行时随后在 §169 移植；默认
`classifierModel` 为空时评审关闭，默认部署的 /v1 行为不受影响。

§160 追加 43 个用例（`AxonHub.Api.Tests` 37：`CodexCompatibilitySettingsServiceTests` 22、
`AdminCodexCompatibilityEndpointTests` 15；`AxonHub.Llm.Tests` 6：`HttpRedirectTests` 的
SameOrigin 用例），同样按项目规则未运行 lint / build / test；§160 落地后的静态计数为
`AxonHub.Grains.Tests` 1746、`AxonHub.Api.Tests` 5788、`AxonHub.Llm.Tests` 1512、
`AxonHub.Persistence.Tests` 359，四项目合计 9405，下一次全量运行应以该数字复核。该切片为
post-baseline 漂移（冻结基线 `b8bb3421` 无 codex 兼容性文件与 schema），按 `f272549f` 接入
Codex 兼容性管理面与目录服务：`codexCompatibilitySettings` / `codexCompatibilityEnabled` /
`codexCatalogChannels` 查询与 `updateCodexCompatibilitySettings` / `testCodexCatalog` 变更。
设置面复刻 `codex_compatibility` 的 Go 形状（`channelID` 无 omitempty）、缺行与 `"null"` 的
零值、Go 的键大小写 / 重复键 / null / 64 位 int 解码容忍与逐字错误文案、`read_settings` 与
`write_settings` 的读写分离、`RunWithSystemBypass` 的免权限启用判定与三类校验文案；目录面
复刻 singleflight（键 `%d:%s`、30s 分离预算、调用方取消 502）、启用快照优先与快照代理
executor、实时行类型检查、凭据构建、wss/ws 改写、`client_version` 查询串回写、same-origin
重定向钩子、上游 HTTP / 传输错误折叠、严格目录解析与 `testCodexCatalog` 的结果折叠
（message 覆盖与仅非零 upstreamStatus）。`/codex/*` 运行时路由随后在 §165 移植，C244 由 gap
更新为 resolved；
same-origin 比较的 URI 规范化差异登记为 C245（accepted）；零启用 key 的参照 panic 经
singleflight 崩溃进程，端口折叠为有界 502 凭据错误，登记为 C246（accepted）；多 key
`apiKeyStrategy` 分派未移植，登记为 C247（gap），端口保持基线 sticky / random。

§161 追加 15 个用例（`AxonHub.Api.Tests` 15：`ApiKeyQuotaTests` 8、
`AdminApiKeyQuotaUsageEndpointTests` 7），同样按项目规则未运行 lint / build / test；
§161 落地后的静态计数为 `AxonHub.Grains.Tests` 1746、`AxonHub.Api.Tests` 5803、
`AxonHub.Llm.Tests` 1512、`AxonHub.Persistence.Tests` 359，四项目合计 9420，下一次全量
运行应以该数字复核。该切片为冻结基线行为（`internal/server/gql/axonhub.graphql:1116`、
`axonhub.resolvers.go:952-981`、`internal/server/biz/quota.go:129-155,157-191`）：
`apiKeyQuotaUsages` 查询经 APIKey 查询隐私加载密钥（缺失或被过滤 → `failed to get api key: %w`），
再让配额服务为每个携带 quota 的 profile 输出一项（`failed to get api key quota usage: %w`），
窗口与用量逐 profile 用 `quotaWindow(UTCNow, period, TimeLocation)` 与系统旁路的
`requestCount` / `usageAgg(needTokens=true, needCost=true)` 计算；空或 null profiles 输出空列表，
int64 计数按 C190 截断，隐私拒绝由 presenter 折叠为 `permission denied` / FORBIDDEN。
profiles 损坏列的 decode 位置差异（ent 扫描 vs 配额服务）登记为 C248（accepted）；
C176 item (6) 的 apiKeyQuotaUsages 缺口随之关闭。

§162 追加 20 个用例（`AxonHub.Api.Tests` 17：`ChannelProbeServiceTests` 10、
`AdminChannelProbeDataEndpointTests` 7；`AxonHub.Persistence.Tests` 3：
`ChannelProbeStoreTests`），同样按项目规则未运行 lint / build / test；§162 落地后的静态
计数为 `AxonHub.Grains.Tests` 1746、`AxonHub.Api.Tests` 5820、`AxonHub.Llm.Tests` 1512、
`AxonHub.Persistence.Tests` 362，四项目合计 9440，下一次全量运行应以该数字复核。该切片为
冻结基线行为（`internal/server/gql/channel_probe.graphql:1-19`、
`channel_probe.resolvers.go:15-26`、`internal/server/biz/channel_probe.go:345-465`）：
`channelProbeData` 按 `read_channels` 的 scope decision 选择存储的 `system_channel_settings`
或默认探针设置（deny / 读取失败 / 解码失败一律回退 defaultChannelSetting），把 UTC now
按 interval 截断得到窗口，查询 `channel_probes` 的闭区间行并按 `(channel_id, timestamp)`
映射到对齐时间点（缺失为零计数与 null 均值），输出按输入 id 顺序、重复 id 重复输出、空输入
在读取设置前返回空列表，ent 查询错误原样冒泡；输入 GUID 只取 id 不校验 type，输出
`channelID` 恒为 `gid://axonhub/Channel/<id>`，GraphQL Int 按 C190 截断。新增
`channel_probes` 的 DDL、EF 模型与 Dapper 读取 seam（表基线更新为 24 表 / 52 索引）。
探针 writer / 每分钟调度器 / GC `cleanupChannelProbes` / dashboard probes 分支当时未移植，
登记为 C249（§170 已 resolved）；scope 近似与系统设置缓存缺席（C029）的影响同条目登记。

§163 追加 25 个用例（`AxonHub.Api.Tests` 24：`ChannelSettingsMergeTests` 8、
`AdminChannelSettingsEndpointTests` 16；`AxonHub.Persistence.Tests` 1：
`ChannelWriteStoreTests`），同样按项目规则未运行 lint / build / test；§163 落地后的静态
计数为 `AxonHub.Grains.Tests` 1746、`AxonHub.Api.Tests` 5844、`AxonHub.Llm.Tests` 1512、
`AxonHub.Persistence.Tests` 363，四项目合计 9465，下一次全量运行应以该数字复核。该切片为
post-baseline 漂移（冻结基线 `b8bb3421` 无 `channel_settings` 包与对应 schema 成员），按
`f272549f` 接入 `updateChannelSettings`：读取 `settings` / `updated_at` 后在进程内把 patch
合并到存储设置（nil 切片保留、显式空切片清空、嵌套对象整体替换、`quotaRoutingMode` 的
INHERIT 映为 `""`），合并值经完整 `UpdateChannel` 的校验、Command Code cookie 归一化与
`updated_at` 乐观锁写入；守卫使用合并读取时的版本（`UpdateChannelCommand.ExpectedUpdatedAt`
覆盖第二次读取），冲突重试至多 8 次并返回 `channel was updated concurrently; retry the
operation`，其它错误立即返回。序列化沿用 Go tags：非 omitempty 空值恒出现、omitempty
空值在写入后消失，因此立即响应等于存储 JSON 的读回。缺失 / 软删通道与损坏 settings 的
读取失败保持 `failed to load channel settings: ` 前缀。live 的 `channelExpectedVersionKey`
已在本路径采用（C144 更新）；`apiKeyStrategy` 的 present-empty 指针折叠为缺失登记为
C250（accepted）。

§164 追加 57 个用例（`AxonHub.Api.Tests` 57：`ChannelApiKeyStrategiesTests` 50 Fact +
2 Theory / 7 InlineData），同样按项目规则未运行 lint / build / test；§164 落地后的静态
计数为 `AxonHub.Grains.Tests` 1746、`AxonHub.Api.Tests` 5901、`AxonHub.Llm.Tests` 1512、
`AxonHub.Persistence.Tests` 363，四项目合计 9522，下一次全量运行应以该数字复核。该切片为
post-baseline 漂移（冻结基线 `b8bb3421` 只有 trace-sticky 规则），按 `f272549f` 接入
`apiKeyStrategy` 的六种策略：sticky（默认 / 空 / 未知）、random、round_robin
（`apiKeyRoundRobinSwitchAfter`，缺省 1、<1 归一）、round_robin_success（Get 不计数、成功
回报按 per 推进游标）、priority（恒首）、fixed（游标失效才沿完整数组前进并跳过禁用项）。
共享的每通道选择状态（游标 / rr 计数器 / 1024 项 trace 粘性 LRU）由
`ChannelApiKeySelectionRegistry` 单例持有，跨通道快照重建存活；启用通道读取时为每个通道
发布完整 key 数组与禁用集，`ChannelMetricsMiddleware` 成功分支回报 key，删除 / 批量删除后
遗忘状态。多 key 分派条件与参照一致（启用 > 1 或配置 > 1）；单 key 与 override 保持静态
provider，`VideoService` 按参照「状态查询不读 key」不接 registry。`isServing` 用代际内容
等价替代参照的指针相等，登记为 C251（accepted）；C247 由 gap 更新为 resolved。

§165 追加 19 个用例（`AxonHub.Api.Tests` 19：`ResponsesWebSocketEndpointTests` 11、
`CodexEndpointTests` 8）。本轮按用户要求运行了构建与过滤测试：`dotnet build
tests/AxonHub.Api.Tests` 为 0 警告 0 错误，`dotnet test --filter
"FullyQualifiedName~ResponsesWebSocketEndpointTests|FullyQualifiedName~CodexEndpointTests"`
为 19/19 通过；§165 落地后的静态计数为 `AxonHub.Grains.Tests` 1746、`AxonHub.Api.Tests`
5920、`AxonHub.Llm.Tests` 1512、`AxonHub.Persistence.Tests` 363，四项目合计 9541，
下一次全量运行应以该数字复核。该切片为 post-baseline 漂移（冻结基线 `b8bb3421` 没有
`codex_routes.go` / `codex_compatibility.go` / `responses_websocket.go`），按 `f272549f`
接入 `/codex` 运行时：中间件链 IP 黑名单 → Enabled 门（读取失败 500、禁用 404，
均带 no-store）→ strict API key（无 key / 哨兵 / 无效 → 401、IP 禁止 → 403、验证失败 →
500）→ thread / trace；whoami 与 accounts-check 的固定身份（`axonhub-key-%d`、promax、
NO_CONSTRAINT、personal、Go map / 结构体字段序、`Cache-Control: no-store`）；`/codex/models`
的可见模型读取（privacy deny → 403、其余 500）、系统旁路的目录源选择（读设置失败 → 500、
ChannelID 为空 → 503）、`client_version` 透传与 `CodexCatalog.Intersect` 输出（`application/json`；
C190 的 Int 截断沿用）；images 与 responses（HTTP / WS）复用既有处理器。
Responses WebSocket 端点按 f272549f 复刻：128 MiB 帧上限、每个 `stream_id` 一条 lane 串行
（无 `stream_id` 共用空 lane）、最多 32 条命名 lane 与无 stream_id 回显的校验错误、16 个并发
活动事件、64 个 pending 与 128 MiB 队列预算（429 `websocket_queue_limit_reached`）、64 MiB
retained/warmup 预算（400 `websocket_context_limit_reached`）、`generate=false` 的
response.created / in_progress / completed 三事件与本地 continuation 合并、`{error,status,type}`
错误信封与 stream_id 回显、协议 / 处理器错误不关闭连接、非升级 GET 400
`websocket upgrade required`。参照的 2.5 分钟协议 ping / 5 分钟 read deadline 由 ASP.NET
keep-alive 代替（C252），per-event `LLMRequestTimeout` 缺席（C253，C115），
`shared.ResponsesLane` continuation lease 未移植（C254），`writeStreamDelivery` 交付边界未移植
（C255），均登记为 accepted。超大帧（128 MiB + 1）的关闭行为未在测试中构造（分配代价高），
实现按 gorilla `SetReadLimit` 语义以 CloseMessageTooBig 关闭。

Grain 与 API 测试用 Orleans `TestCluster` 在进程内起一个 silo，`IEnabledChannelProvider` 由假实现注入，
不依赖 Docker；grain 键按请求模型/通道 ID 隔离，用例之间无共享状态。API 测试的传输层用脚本化
`IExecutor` 替代，不发起真实上游请求；SSE 写帧与请求读取用 `DefaultHttpContext` + `MemoryStream` 逐字节断言。

跳过项说明：无。C038 已闭合——`LlmRequest.Compact` 随 Responses 切片移植后，
`PromptCacheKeyFallsBackToTheCompactRequest` 取消 `Skip`，按参照的「顶层键优先、否则回退
`Compact.PromptCacheKey`」分支断言并通过。

§166 追加 5 个用例（`AxonHub.Llm.Tests` 5：`ResponsesWebSocketExecutorTests` 5），收口
C072 的跨请求连接复用。参照的池活在按通道缓存的 transformer 里，而端口每次尝试构建
transformer 且在尝试结束调用 `Stop`，因此连接从不跨请求存活。本轮新增进程级
`WebSocketExecutorCache`（键为 inner `IExecutor` + `ChannelID`），Responses / Codex 出站经
缓存取共享执行器，pool key 增加 `ChannelID` 保持按通道分离，两个 `Stop()` 改为 no-op；
连接由空闲 TTL 50m、最大寿命 50m、池上限 128 与后台清理退役。该生命周期差异登记为
C256（accepted）。同时补齐参照的 retention 语义：`MaxRetainedInput` 16 MiB（原 1 MiB）、
每执行器共享 64 MiB retained 预算、`rememberTurn` 的五条分支（空 id、closed、先写
`lastResponseID`、超限或预算失败时释放预算并清空 input 但保留 id）、lease 的 native /
空 input / 拼接三路、`response.completed` 的 native / 超限清空 `nextFullInput` 分支，以及
prepare payload 仅在 owner 为空或未超限时缓存 `nextFullInput`。本轮按用户要求运行了构建与
测试：`dotnet build AxonHub.slnx` 为 0 警告 0 错误；`dotnet test tests/AxonHub.Llm.Tests`
为 3110/3110 通过；`dotnet test tests/AxonHub.Api.Tests --filter` 的七个 Responses / Codex
相关测试类为 59/59 通过（期间修复了 `ResponsesEndpointDispatchTests` 夹具把端点格式写成
`openai/alpha/search` 的笔误——参照常量为 `openai/alpha_search`，该用例此前一直失败）。
§166 落地后的静态计数为 `AxonHub.Grains.Tests` 1746、`AxonHub.Api.Tests` 5920、
`AxonHub.Llm.Tests` 1517、`AxonHub.Persistence.Tests` 363，四项目合计 9546，下一次全量
运行应以该数字复核。

§167 追加 34 个用例（`AxonHub.Grains.Tests` 8：`VideoStorageGrainTests`；`AxonHub.Api.Tests`
24：`VideoStorageWorkerTests` 21、`GrainScheduledTaskTests` 3；`AxonHub.Persistence.Tests` 2：
`RequestStoreTests`），收口 C234 的视频存储扫描 worker。参照的
`video_storage.Worker.RegisterScheduledTasks/Reschedule/scanAndSave/processOne`
（internal/server/video_storage/worker.go，f272549f）由单例 `IVideoStorageGrain` 的 Orleans
reminder 承载：注册只装填不运行，fire 先按当前间隔重排下一次再运行，过期 tick 只重排不重复
扫描，Reschedule 从调用时刻重排，扫描失败只记录 "Video storage worker failed" 且不丢调度；
间隔读取 `system_video_storage_settings`，非正数下限 1 分钟。`VideoStorageWorker` 复刻全部
可观察行为：启用闸门、`data_storage_id` 必填、存储存在性与「非 database / 非 primary」校验、
行查询（processing/completed、openai/video 与 seedance/video、content_saved=false、id ASC、
limit 非正数回落 50）、ResponseBody 的 `video_url` 优先与视频任务 `succeeded` 回退、5 分钟
下载超时、仅 http/https、非 200 失败、512 MiB 静默截断（`io.LimitReader` 语义）、
Content-Disposition 文件名与 URL 去 query 的 basename 回退、时间戳文件名、
`/{project_id}/requests/{request_id}/video/{basename}` key、内容列与 updated_at 写入、
行查询失败前缀 "failed to query video requests: "、逐行失败记录后继续。请求存储新增 Dapper 读
`ListUnsavedVideoRequestsAsync` 与 EF 写
`MarkContentSavedAsync`；生产接线为 `GrainVideoStorageScheduleSource` /
`GrainVideoStorageScanRunner` / `GrainVideoStorageRescheduleSignal`，启动激活器装填
`video-storage` 单例。调度机制差异登记为 C257（accepted）。本轮按用户要求运行了构建与测试：
`dotnet build AxonHub.slnx` 为 0 警告 0 错误；`dotnet test tests/AxonHub.Grains.Tests` 为
169/169 通过；`dotnet test tests/AxonHub.Api.Tests --filter
"VideoStorageWorkerTests|GrainSchedulerBridgeTests"` 为 22/22、
`--filter "GrainScheduledTaskTests"` 为 6/6（本切片 Api 定向共 28/28）；
`dotnet test tests/AxonHub.Persistence.Tests --filter "RequestStoreTests"` 为 14/14。宽过滤
运行另命中 3 个与本切片无关的既有失败（`updateQuotaRoutingSettings` 等报
`AuthorizationScope` 未注册），未在本轮处理。§167 落地后的静态计数为
`AxonHub.Grains.Tests` 1754、`AxonHub.Api.Tests` 5944、`AxonHub.Llm.Tests` 1517、
`AxonHub.Persistence.Tests` 365，四项目合计 9580，下一次全量运行应以该数字复核。

§168 追加 59 个用例（`AxonHub.Api.Tests` 59：`ChannelAutoDisableTests` 48、`WebhookNotifierTests` 11），
收口 C236 的自动禁用运行时与 Webhook 通知器（§154）。参照 `f272549f` 的
`internal/server/biz/channel_auto_disable.go`、`webhook_notifier.go`、`channel_metrics.go`、
`performance.go`、`internal/server/orchestrator/orchestrator.go`：

- `ChannelAutoDisableService` 复刻计数器协议：键为 `identity:scope:rule:<index>:<statusCodes>:<keywordPatterns>:<times>:<action>:<duration>`，
  达到阈值时认领并扣减、`actionInFlight` 防并发、动作失败回补连续失败、成功清零；
  identity 对 keyless 记录使用 `channel:<id>`；取消与未完成记录不计入。
- 策略模式：`off` 跳过两层规则、`custom` 只读渠道规则、`inherit` 只读全局规则、缺 mode 视为
  `custom`；全局策略禁用与读取失败回退默认策略均与参照一致。
- 动作：`temporary_disable`（到期时刻 + 分钟文案）、`until_cron`（时区解析、严格晚于、
  RFC3339 文案）、`permanent_disable`、`permanent_delete`（先禁用再删除，`ONE_KEY_PRESERVED`
  时仍先禁用）；OAuth 与 keyless 渠道的 `permanent_delete` 按参照降级为 permanent。
- `markChannelUnavailable`：条件更新只命中 enabled；未命中时只强制重载；命中后读取更新后的
  通道快照（快照缺失只跳过事件但仍重载）、发布 `ChannelAutoDisabledEvent`（更新前字段 +
  `OccurredAt` = 自动禁用时刻）、同步强制重载、最后请求防抖刷新；存储失败只记日志。
- `WebhookNotifier`：首个事件精确匹配的订阅、按 targetNames 顺序、跳过未启用 / 空 URL、
  重名重复发送；每个目标先 body 再 headers，模板失败只跳过该目标（body / header 任一失败）；
  headers 空 key 跳过、默认 `Content-Type: application/json`、默认 3000ms 超时、支持代理；
  配置读取失败回退空配置（error 日志）；发送失败只记日志。`OccurredAt` 按系统时区
  格式化为 RFC3339（零偏移写 `Z`）。`BackgroundWebhookNotificationSink` 在独立 DI 作用域
  投递，与参照的异步通知解耦一致。
- 生产接线：`ChannelMetricsMiddleware` 的失败路径构造 `ChannelPerformanceRecord` 后经
  `ChannelAutoDisableEvaluationQueue`（BackgroundService，单读者，每条记录新建作用域）串行
  评估；`ChatCompletionOrchestrator` 在请求失败且存在执行记录时先落库失败再评估，评估异常
  不替换原请求错误（C263）。
- 管理面已有的 `webhookNotifierConfig` / `updateWebhookNotifierConfig` 读写与校验（3 例）
  复用，行为不变。

本轮按用户要求运行了构建与测试：`dotnet build tests/AxonHub.Api.Tests` 为 0 警告 0 错误；
`dotnet test --filter "FullyQualifiedName~ChannelAutoDisableTests|FullyQualifiedName~WebhookNotifierTests"`
为 59/59 通过；随后 `dotnet test --no-build` 全量运行：5878 通过 / 122 失败 / 6000 总计，
31m11s。失败均为既有的环境 / DB 依赖基线（上一次基线 123 例），本切片无新增失败。
§168 落地后的静态计数为 `AxonHub.Grains.Tests` 1754、`AxonHub.Api.Tests` 6003、
`AxonHub.Llm.Tests` 1517、`AxonHub.Persistence.Tests` 365，四项目合计 9639，下一次全量
运行应以该数字复核。

台账：C236 由 gap 更新为 resolved；C149 修订（删除「冻结版本没有自动禁用事件总线」的错误
描述，残余仅剩 live 的 Vertex key 分支）；C017 的残余说明更新（自动禁用与异步评估 worker 已
由 C236 移植）。新增 C258–C263（accepted）：RE2 与 .NET Regex 的引擎差异、进程级计数器与
后台队列、5 字段 cron 语法、时区解析依赖、8 KiB 错误体的 UTF-8 截断解码、评估异常不影响
原请求错误。`porting/02-differences.md` 重新生成（263 条：193 accepted / 9 gap / 61 resolved），
lint 通过。

§169 追加 81 个用例（`AxonHub.Api.Tests` 81：`SafeguardsEvaluatorTests` 30、
`SafeguardsFallbackTests` 20、`SafeguardsEndpointTests` 16、`SafeguardsClassifierTests` 9、
`AnthropicTerminalStreamTests` 6），收口 C243 的 Claude Code safeguards 评审运行时（§159
只含设置面）。参照 `f272549f` 的 `internal/server/safeguards/{evaluator,classifier,stream,
response}.go`、`internal/server/api/{safeguards,anthropic,chat}.go` 与
`internal/server/orchestrator/anthropic_terminal_stream.go`：

- 请求准备：`HasDangerousToolUse` 复刻 gjson 的
  `safeguards.#(type=="dangerous_tool_use")` 探针（严格解析的宽容度差异登记为 C265），命中
  时才重读运行时设置；`Prepare` 只剥离本地处理的 `dangerous_tool_use`、保留其他 safeguard，
  删除过期的 `Content-Length`，其余字段按 Go 的 `map[string]json.RawMessage` 语义重编码；
  解码失败映射 400（文案差异登记为 C266）。
- 评审器：`DefaultPolicy` 与空/空白 policy 回退、context 键子集与 `git_state` 裁剪、
  transcript 的默认 24000 字节预算与 UTF-8 安全裁剪、截断/无效工具永不提交、模型名 trim +
  去重、MaxTokens 默认 4096、context 16000 / tool 64000 / verdict 64000 上限；逐模型尝试与
  整体/单次预算，预算到期归因为 timeout、其他失败归因为 error；verdict 拒绝重复键、每工具
  只接受首个有效判定、解释裁剪 2000 字节；`EscalateOnDenial` 只对首个模型的拒绝做二次意见，
  回退只重审未决工具，耗尽时保留已评估的 verdict。
- 分类器：Fork 语义（新 request id、保留项目/追踪/API key/来源、
  `/v1/chat/completions`、非流式单 choice、stop 与非拒答校验），身份与 session 经
  `ClassifierFactory` 接缝传入（C062）。
- 流式评审：终端 `message_delta` 先 drain 上游（最多 256 事件 / 1 MiB，只允许 `ping`，且必须
  以 `message_stop` 结束），再评审并回放；reviewer 错误优先于源错误；annotation 生成
  `safeguard_results` 并更新 `Size`；1s `abortUpstream` 上限。
- 传输收口：`ChatCompletionOrchestrator` 在 raw-stream 中间件末尾注册
  `AnthropicTerminalStreamMiddleware`，仅包装 `anthropic/messages` 的 provider 流，转发一次
  `message_stop`（深拷贝事件与 Data）、随后关闭 provider 传输且 `Err` 恒为 null，
  `message_stop` 之前的错误原样保留；这是 passthrough 评审看到干净 EOF、不会因传输未关闭而
  误判取消的前提。
- 接线：`/v1/chat/completions`、`/v1/messages` 与 `/anthropic/v1/messages` 共用
  `ChatCompletionEndpoint`；设置读取失败返回 500
  `Unable to load Claude Code safeguards settings`，`classifierModel` 为空时评审关闭。

本轮按用户要求运行了构建与测试：`dotnet test --filter
"FullyQualifiedName~Safeguards|FullyQualifiedName~AnthropicTerminalStream"`（含重编译）为
105/105 通过；`SafeguardsFallbackTests` 连续 4 轮 20/20。评审测试对需要成功的尝试改用宽裕
预算（仅显式 timeout 场景保留 10ms），避免 Windows 线程调度抖动把 10ms 尝试预算误判为超时。
§169 落地后的静态计数为 `AxonHub.Grains.Tests` 1754、`AxonHub.Api.Tests` 6084、
`AxonHub.Llm.Tests` 1517、`AxonHub.Persistence.Tests` 365，四项目合计 9720，下一次全量
运行应以该数字复核。

台账：C243 由 gap 更新为 resolved；新增 C264–C267（accepted）：评审日志与 operation name、
gjson 探针宽容度、解码错误文案、Timer 精度与上限。`porting/02-differences.md` 重新生成
（267 条：197 accepted / 8 gap / 62 resolved），lint 通过。

§170 追加 30 个用例（`AxonHub.Grains.Tests` 6：`ChannelProbeGrainTests`；`AxonHub.Api.Tests`
17：`ChannelProbeServiceTests` +12、`GcWorkerTests` +3、`AdminDashboardEndpointTests` +2；
`AxonHub.Persistence.Tests` 7：`ChannelProbeStoreTests` +4、`DashboardProbeStatsStoreTests` 2、
`GcStoreTests` +1），收口 C249 的探针 writer / 分钟调度 / GC 清理（§162 只含读取面）。参照
`f272549f` 的 `internal/server/biz/channel_probe.go`、`channel_probe_internal.go`、
`internal/server/gql/qb/throughput.go`、`internal/server/gc/gc.go`、
`internal/server/gql/channel_performance_helpers.go` 与 `dashboard.resolvers.go`：

- writer：`RunProbeAsync` 先读 `system_channel_settings` 的 probe 设置（任何读取 / 解码错误
  回退 `defaultChannelSetting`，默认 `enabled=true`、`frequency=5m`），禁用则跳过；频率映射
  1m / 5m / 30m / 1h、未知 1m；`aligned = UTC now` 截断到 interval，**在 DB 工作前**占用
  去重槽（同区间重入 / 并发跳过，失败也占用）；`StatusEQ(enabled)` 取通道 id；窗口
  `[aligned-interval, aligned)` 执行 `qb.BuildProbeStatsQuery` 的 PostgreSQL 形态：attempt 级
  `completed` / `failed`，`usage_logs` 子查询按 `created_at >= start` 聚合完成 tokens，
  streaming 有效延迟扣 TTFT 且 `TTFT >= latency` 归零，`request_count = COUNT(DISTINCT
  request_id)`，`streaming_request_count` 仅 completed + stream + 有 TTFT；TPS =
  `total_tokens / (effective_latency_ms / 1000)`、TTFT =
  `total_first_token_latency / streaming_request_count`（仅当分母 > 0）；`CREATE_BULK`；
  `effective_latency_ms` 为 NULL 时中止整次 pass（等价 `database/sql` 把 NULL 扫进 int64
  失败）；所有错误只记日志、从不抛出。
- 调度：`ChannelProbeGrain` 以 Orleans reminder（60s tick）承载参照的 `* * * * *` cron；
  `EnsureScheduledAsync` 只在没有 future pending 时 arm，`FireScheduledAsync` 先 arm 下一
  分钟边界再运行，pending 晚于 now 的 tick 视为 stale；启动由 `GrainSchedulerActivator`
  touch。
- GC：`cleanupChannelProbes` 在每次策略循环后固定执行（manual 参数不改变窗口）：
  `timestamp < now-3d` 的 Unix 秒、id 升序、500 分批删除；失败只记日志并继续 VACUUM。
- dashboard：`channelPerformanceStats` 先查 `channel_probes`（本地日期 + channel 分组、
  `timestamp >= start.Unix()`、`avg_tokens_per_second <= 2000`，按 `total_request_count`
  加权吞吐 / TTFT）；probes 查询错误包裹后**不回退**，只有空结果才回退 executions 聚合。

附带修正（既有缺陷，非新差异）：`GcStore` 的两处 payload 查询把 `AND` 与 `NOT EXISTS`
用不带空格的 StringBuilder 拼接，生成 `ANDNOT`（PostgreSQL 语法错误）；`IN @ids` 改为
`= ANY(@ids)`——Dapper 在 Npgsql 上把数组参数按原生数组传递，不会展开 `IN` 列表。另按参照
的 `schematype.SkipSoftDelete` 上下文拆分 `GetPrimaryAsync`（live）与
`GetPrimaryIncludingDeletedAsync`（GC 使用），消除同一条测试互相矛盾的两种期望。

本轮按用户要求运行了构建与测试：`dotnet build AxonHub.slnx` 为 0 警告 0 错误；定向
`dotnet test` 为 Grains 6/6、Api 50/50、Persistence 28/28（含既有的
`DataStorageStoreTests` 软删语义）。§170 落地后的静态计数为 `AxonHub.Grains.Tests`
1760、`AxonHub.Api.Tests` 6101、`AxonHub.Llm.Tests` 1517、`AxonHub.Persistence.Tests`
372，四项目合计 9750，下一次全量运行应以该数字复核。（既有 3 个 `AuthorizationScope`
未注册失败见 §167，本切片未触碰。）

台账：C249 由 gap 更新为 resolved；C195 与 C203 中「probes 分支 / 清理缺席」的措辞按
§170 更新。`porting/02-differences.md` 重新生成（268 条：196 accepted / 7 gap / 65
resolved），lint 通过。

§171（2026-11-11）按用户要求对 `AxonHub.Api.Tests` 做了全量实测（不带过滤器，构建后
`--no-build` 运行，TRX `E:\tmp\trx2\api-full-3.trx`）：总计 6102，通过 6102，失败 0，
跳过 0，用时 19m11s（`AxonHub.Api.Tests.dll (net10.0)`）。实测比 §170 记录的静态计数
6101 多 1 条，以 6102 为准；其余三项目本轮未重跑，沿用 §170 的静态计数
`AxonHub.Grains.Tests` 1760、`AxonHub.Llm.Tests` 1517、`AxonHub.Persistence.Tests`
372，四项目合计 9751。

本轮起点是同一次全量运行的 6078 通过 / 24 失败。其中 23 条是陈旧二进制造成的假失败：
测试程序集构建于当日 7:54，晚于它的若干修复（API key profile 模板、channel limiter
metrics、system settings 等）未编入；重新构建后按 16 个失败测试类过滤复跑为 1139
通过 / 1 失败。最后一条 `ResetChannelQuotaNowResolvesTheGidAndSurfacesTheServiceError`
是测试期望写错：Go resolver 在 `system.resolvers.go:260` 用
`fmt.Errorf("failed to reset channel quota: %w", err)` 包了一层，线上消息保留前缀（内层
为 `provider quota reset is not supported for provider "claudecode"`，来自
`provider_quota.go:683/688` 与 `provider_quota/types.go:25`）；端口
`AdminMutation.cs:961` 本就输出前缀，故只改测试期望。修正后全量复跑清零。

台账：本轮无新增差异条目；C206（无 body GET 的 Content-Type）与 C268（ChannelModelPrice
内省字段顺序，决策为接受）均为 resolved，相关测试在本轮全量运行中通过。

§172（2026-11-11）按用户要求重跑四个项目的全量测试，用实测校正 §170 / §171 沿用的静态
计数。结果：`AxonHub.Grains.Tests` 175 通过 / 0 失败 / 0 跳过（5s）；`AxonHub.Llm.Tests`
3110 / 0 / 0（1s）；`AxonHub.Persistence.Tests` 372 / 0 / 0（2m46s）；`AxonHub.Api.Tests`
6102 / 0 / 0（46m11s，`--no-build`，日志 `E:\tmp\api-full-4.log`）。四项目合计 9759，
而非 §170 / §171 的 9751；§170 记录的 Grains 1760 与 Llm 1517 均与实测不符（分别为
175 与 3110），以本节为准。Api 运行前先重建测试程序集，避免 §171 的陈旧二进制假失败；
本轮耗时 46m11s 明显长于 §171 的 19m11s，期间 testhost 持续占用 CPU 并正常完成，未见挂起。

本轮起点是全量运行的 1 个 Llm 失败与 16 个 Persistence 失败，分诊后归类为产品缺陷与
过期测试期望两类：

- 产品修复：`PromptProtectionRuleStore.BulkUpdateStatusAsync` 的批量状态 SQL 在
  `"id" IN (...)` 后漏掉右括号（PostgreSQL 42601，near ";"）；
  `ChannelStore.ListEnabledByTypesAsync` 的 `IN @Types` 改为 `= ANY(@Types)` 并传
  `ToArray()`（Npgsql 把数组参数当原生数组，`IN $1` 是语法错误，同 §170 的 GcStore 修正）；
  `ProviderQuotaStatusRecord.AccountKey` 删除 `= string.Empty` 初始值（Dapper 不覆盖
  引用类型默认值，NULL 会读成 ""，而参照 ent 的 nillable 字段返回 nil）。
- 测试期望修正：5 处 jsonb 文本比较改用 `RecordAssert.Json`（PostgreSQL jsonb 会重排
  key 并规范化空白）；`SchemaTests` / `EfCoreModelTests` 补上 §170 新增的 `channel_probes`
  表与其索引，计数更新为 25 表 / 55 索引；邀请注册的 `user_projects.is_owner` 期望改为
  false（`user_project.go:40-41` 默认 false，`invitation.go:238` 不设置）、被拒的第二次
  注册不再创建用户、第二个角色改用不同名称；302 重定向后的 body 断言为空串（C206 的
  零长度 ByteArrayContent）；批量软删后存活行数为 0。

管理面 GraphQL 根字段盘点（静态对照 `internal/server/gql/*.graphql` 与 `AdminQuery.cs` /
`AdminMutation.cs`）：参照 211 个根字段，端口 217 个；除 post-baseline 漂移的 9 个新增
字段外，仅 `nodes` / `oidcIdentities` / `systems` 未实现，已登记 C269（真实缺口；`nodes`
在参照中为 panic 占位，三者均未被 `frontend/src/gql` 使用）。`defaultDataStorageID` 的
大小写由 `AdminQuery.cs:110` 的显式 `[GraphQLName]` 对齐，此前盘点误报为缺失，已复核为
假阳性。

台账：新增 C269（gap），现有 269 条（196 accepted / 8 gap / 65 resolved）；lint 通过，
`porting/02-differences.md` 已重新生成。

§173（2026-11-11）按上一节的盘点补齐 C269 中两个可用的根查询 `systems` 与
`oidcIdentities`，`nodes` 保持不暴露（参照解析器是 `panic("not implemented")` 占位）。
新增 `SystemAdminTypes.cs`（System / OIDCIdentity 的 order / where 子集 / connection /
edge / node 类型与映射）、`SystemPrivacy.cs`（两套 ent 查询策略的显式求值）、
`OidcIdentityAdminStore.cs`（Dapper 键集分页读取），`SystemStore` 增加 entgql 连接读取，
`AdminQuery` 增加两个解析器，`Program.cs` 注册存储与隐私服务。

保真要点：两个连接与参照一样先做 `validatePaginationArgs`，再由 ent 生成的 `Paginate`
做 `validateFirstLast`（`internal/ent/gql_pagination.go:60`，端口在 resolver 中按同样
顺序调用，故 `first` / `last` 同时出现与负数输入的错误文案一致）；`CREATED_AT` /
`UPDATED_AT` 是真实排序字段，不做 users/roles 式的 id 改写；`first` 多取一行探测、
`last` 反向扫描后还原顺序、totalCount 先于游标；System 查询策略
Owner → read_settings → default deny，OIDCIdentity 查询策略
Owner → read_users → user_id 本人谓词（作为额外 and 过滤），无 user 时
`no user in context` 经错误过滤器表现为 permission denied / FORBIDDEN；
OIDCIdentity.user 边按 user_id 加载（生成代码 `obj.User(ctx)`），不是身份行 id；
where 输入只建模前端使用的子集（C269）。`nodes` 与参照一致不暴露。

测试：`tests/AxonHub.Api.Tests/AdminSystemsOidcIdentitiesEndpointTests.cs` 11 例（连接
形状与真实 CREATED_AT 排序、UPDATED_AT 键集游标、非 owner 无 read_settings 拒绝与有
scope 放行、node(System) 策略、node(OIDCIdentity) unknown、OIDC 本人 / read_users
全量 / where 与 user_id 求 AND、user 边按 user_id、API key 拒绝）；
`tests/AxonHub.Persistence.Tests/SystemStoreTests.cs` 新增 2 例、
`OidcIdentityAdminStoreTests.cs` 2 例（id 序分页与软删过滤、UPDATED_AT 排序 /
标量过滤 / user_id AND）。

本轮实测：`AxonHub.Api.Tests` 6113 通过 / 0 失败 / 0 跳过（26m43s，先重建测试程序集，
`--no-build` 运行）；`AxonHub.Persistence.Tests` 376 通过 / 0 失败（较 §172 的 372
增加 4 例）；`AxonHub.Grains.Tests` 175 与 `AxonHub.Llm.Tests` 3110 本轮未改动，沿用
§172 的实测，四项目合计 9774。§172 的 Api 6102 + 新增 11 = 6113、Persistence
372 + 4 = 376，与实测一致。

台账：C269 由 gap 更新为 resolved；现有 269 条（196 accepted / 7 gap / 66 resolved），
lint 通过，`porting/02-differences.md` 已重新生成。

附加发现（下一小节的输入）：端口 `models` 与 `dataStorages` 两个根查询没有调用 entgql
生成的 `Paginate` 内部的 `validateFirstLast`，参照会拒绝 `first` + `last` 同时出现；
C196 中「dataStorages 不要求 first / last 互斥」的措辞是对照 resolver 时的误读，需要
修正并补测试。

§174（2026-11-11）闭合 §173 的附加发现：`models` 与 `dataStorages` 缺少 entgql 生成层的
`validateFirstLast`。参照的约束分两层——resolver 显式调用 `validatePaginationArgs`，
随后 `Query.Paginate` 在开头调用 `validateFirstLast`（`internal/ent/gql_pagination.go:60`）。
`DataStorages` resolver 先做前者（`internal/server/gql/ent.resolvers.go:400-412`）再进入
`DataStorageQuery.Paginate`（`gql_pagination.go:2503-2510`）；`Models` resolver 不做前者，
只进入 `ModelQuery.Paginate`（`ent.resolvers.go:416-424`、`gql_pagination.go:2817-2824`）。
端口此前分别缺这一层，故 `first` + `last` 同时出现、或 `models(first: -1)` 不会被拒绝。
修复：`AdminQuery.cs` 的 `DataStoragesAsync` 在 `ValidatePaginationArgs` 之后补
`ValidateFirstLast`；`ModelsAsync` 只在方法开头补 `ValidateFirstLast`，保持两层校验的
顺序与各自文案。

测试：`AdminModelsEndpointTests.ModelsValidateFirstAndLast`（互斥文案与
`` `first` on a connection cannot be less than zero. ``）、
`AdminDataStoragesEndpointTests.DataStoragesValidateFirstAndLast`（互斥文案与 resolver
先行的 `first must be greater than 0`），两文件现为 8 例与 15 例。

本轮实测：`AxonHub.Api.Tests` 6115 通过 / 0 失败 / 0 跳过（24m28s，先重建测试程序集，
`--no-build` 运行）；Persistence 376、Grains 175、Llm 3110 本轮未改动，沿用 §173 / §172
的实测，四项目合计 9776。

台账：C196 中「dataStorages 只做 validatePaginationArgs（不要求 first / last 互斥）」
更正为两层校验的描述，C177 / C196 的测试计数同步更新；269 条不变（196 accepted / 7 gap /
66 resolved），lint 通过，`02-differences.md` 已重新生成。

§175（2026-11-11）推进 C014 的通道根查询隐私与写入门禁。查询侧：
`ChannelQueryService` 的 `channels` / `queryChannels` 在进入 store 前显式求值 Channel
查询策略（`APIKeyScopeQueryRule(read_channels)` → `OwnerRule` →
`UserReadScopeRule(read_channels)` → default deny）。`allChannelSummarys` 按参照的两条
分支实现：无 project 上下文时先 `authz.HasScope(read_channels)`，不足则返回未包裹的
RequireScope 错误；有 project 上下文时允许 read_channels 全状态查询，否则要求
write_requests / write_api_keys 并走 `RunWithSystemBypass("project-available-channels")`
的 enabled-only 查询；随后恒以 system bypass 读取 Project 并应用 active profile
（channelIDs 与 channelTags，tag 模式按 any/all/none）。`allChannelTags` 先按查询策略
读取非 archived 行，project 分支的 `Project.Get` 走普通上下文，因此受 Project 查询策略
（`UserProjectScopeReadRule(read_projects)` → `OwnerRule` →
`UserReadScopeRule(read_projects)`）约束；tags 按行序 flatten + 首现去重。
`countChannelsByType` 同样先求值查询策略。错误包装保持参照的
`failed to query channels: ` / `failed to query project channels: ` /
`failed to get project: ` / `failed to query channel type counts: ` 前缀；privacy deny
经错误过滤器折叠为 permission denied / FORBIDDEN。字段侧：credentials、
disabledAPIKeys、settings.providerQuota 在 `scopes.UserHasScope(write_channels)` 为
false 时返回 null（只看 management user，API key 永不满足）。写入侧：26 个通道写
resolver（通道生命周期、批量、端点保存、API key 记账、create / update / settings、
定价、模型同步、duplicate / bulk create / bulk import）在入口求值 mutation 策略
（`OwnerRule` → `UserWriteScopeRule(write_channels)` → default deny）；
testChannel* 与 detectChannelEndpoints 保持原样。

关键实测结论：`/admin/graphql` 的 service-account API key 在进入解析器前先被
apiKeyReadOnly 中间件拒绝（`service account API keys are read-only`），因此 mutation
策略的 no-user deny 分支在该面不可达；API key 的查询面则按自身 scopes 走
APIKeyScopeQueryRule，但 Project.Get 的普通查询策略没有 API key 规则，项目分支下的
allChannelTags 对 API key 表现为 permission denied（与参照一致）。

残余（已写入 C014 decision）：写服务内部的读未重新求值查询策略——create 名称查重、
bulkCreate 名称查询、saveChannelEndpoints 与禁用 / 启用 API key 的 `Channel.Get`、bulk
状态计数、批量标签 / 批量自动禁用查询、模型同步的 `Channel.Get` 等在参照走普通 ctx
（需要 read_channels），端口放行仅持 write_channels 的用户；update / updateSettings /
定价写入的内部读在参照以 `RunWithScopeDecision(write_channels)` 绕过查询策略，端口无差异；
updateChannelStatus / deleteChannel 没有内部读，同样无差异。

测试：新增 13 例。`AdminChannelsEndpointTests` 10 例（无 read_channels 拒绝五个根查询 /
有 read_channels 放行、项目分支的 enabled-only 系统绕过与 write_requests 要求、项目
profile 过滤、allChannelTags 的 read_projects 项目策略与 API key no-user 拒绝、
credentials / disabledAPIKeys / providerQuota 的 write_channels 字段门禁）；
`AdminChannelLifecycleEndpointTests` 3 例（无 write_channels 的 8 个写变更拒绝且零副作用、
有 write_channels 放行、API key 在 mutation 前的只读拒绝）。另以 `AdminChannel` 前缀的
257 例做通道面回归，全部通过。

本轮实测：`AxonHub.Api.Tests` 6128 通过 / 0 失败 / 0 跳过（29m44s，先重建测试程序集，
`--no-build` 运行，日志 `.agent/summary/_api_full.log`，6128 = §174 的 6115 + 新增 13）；
`AxonHub.Persistence.Tests` 定向 `ChannelAdminStoreTests` 4 通过 / 0 失败；Persistence
其余与 Grains / Llm 本轮未改动，沿用 §174 / §173 / §172 的实测 376 / 175 / 3110，
四项目合计 9789。

台账：C014 保持 gap（what / decision / test 三段改写为 §175 的实现与残余），269 条不变
（196 accepted / 7 gap / 66 resolved），lint 通过，`02-differences.md` 已重新生成。

§176（2026-11-11）闭合 §175 记录的 C014 残余：写服务内部读的查询策略求值。
`ChannelLifecycleService` 在参照以普通 ctx 触发 ent 查询的读点前调用
`EnsureChannelQueryAllowedAsync`（`ChannelPrivacy.EvaluateQueryAsync`）：
createChannel 名称查重（channel.go:665-676）、duplicateChannel 源读取
（channel_duplicate.go:23-26）、bulkCreateChannels 的 ListLiveNames
（channel_bulk.go:82-86）、updateChannel 的 xai Exist / 名称查重 / clearStaleQuota
settings 重读 / credentials 重读（channel.go:958-967、979-993、1135-1141、1151-1157）、
bulkUpdateChannelStatus 计数（channel_bulk.go:170-176）、bulkManageChannelTags 与
bulkUpdateChannelAutoDisable 查询（channel_bulk.go:272-284、379-386）、
saveChannelEndpoints 的 Channel.Get（channel.go:1288-1292）、五个 API key 操作的
Channel.Get（channel_apikey.go:39/152/200/237/296）、syncChannelModels 的
Channel.Get（channel_model_sync.go:203-208）与 saveChannelModelPrices 的现有价格查询
（channel_price.go:562-567）。

参照以 `authz.RunWithScopeDecision(write_channels)` 包裹的读（updateChannel 的
provider identity / settings for model protocol sync / endpoint validation、
updateChannelSettings 的 merge 读、定价内部读）由更新入口的单次快照服务，不再求值——
与 Allow 决策跳过全部 ent 规则的语义一致。后台自动禁用的 API key 记账经
`SystemChannelCredentialMutations` 适配器传递系统绕过（channel_internal.go:16-21 的
`authz.WithSystemBypass`），手动 resolver 路径不绕过；`SyncAllAsync` 与
`runSyncChannelModelsPeriodically` 同样不设门禁。`QuotaRoutingMigrationService` 只提交
settings，不触发任何新增门禁。

测试：新增 3 例。`AdminChannelLifecycleEndpointTests` 的
WriteOnlyMembersAreDeniedByTheInternalChannelReads：仅持 write_channels 的成员在
createChannel、updateChannel（name / baseURL / credentials）、bulkArchiveChannels、
bulkAddChannelTags、bulkUpdateChannelAutoDisable、saveChannelEndpoints、
disableChannelAPIKey 上得到 permission denied，且 store 无任何写入；remark 与
supportedModels 更新照常成功（后者证明 RunWithScopeDecision 绕过读无差异）。
`AdminChannelModelPriceEndpointTests` 的
SaveChannelModelPricesIsDeniedByThePriceReadForWriteOnlyMembers 与
`AdminChannelModelSyncEndpointTests` 的
SyncChannelModelsIsDeniedByTheChannelReadForWriteOnlyMembers 各锁定 write-only 拒绝且
无价格计划 / 同步调用。

本轮实测：`AxonHub.Api.Tests` 6131 通过 / 0 失败 / 0 跳过（32m21s，全量重建运行，
日志 `.agent/summary/_api_full.log`，6131 = §175 的 6128 + 新增 3）；定向
`AdminChannelLifecycleEndpointTests`、`AdminChannelModelPriceEndpointTests` 与
`AdminChannelModelSyncEndpointTests` 41 例通过。Persistence / Grains / Llm 本轮未改动，
沿用 §175 / §174 / §173 / §172 的实测 376 / 175 / 3110，四项目合计 9792。

台账：C014 保持 gap（条目仍覆盖请求链路 settings 剩余生效面与通道 key 测试流程等
未拆分残余；本条登记的内部读差异已闭合，what / decision / test 三段追加 §176），
269 条不变（196 accepted / 7 gap / 66 resolved），lint 通过，`02-differences.md`
已重新生成。

§177（2026-11-11）把通道级 `settings.proxy` 接入请求链路，并接通
`testChannel(TestChannelInput.proxy)` 的 per-request override（C154 → resolved）。

实现：`ChannelProxySettings` 是 `ProxyConfigDraft`（ent 侧 objects.ProxyConfig）到
传输层 `ProxyConfig` 的唯一转换接缝，`Fingerprint` 用序列化值作缓存身份并保留 Go
omitempty 的 null-vs-false；`ChannelProxyExecutors`（单例）复刻 getHttpClient
（channel_llm.go:113-130）——无 proxy 用共享 IExecutor，有 proxy 经
IProxyExecutorFactory 建 client 并按 channelId + 指纹缓存（proxy 清空时移除），
type=disabled 也建独立 client 以抑制环境代理；`PersistentOutboundTransformer.CustomizeExecutor`
复刻 outbound.go:1164-1196——override（tester 的 state.Proxy）每次新建且优先于通道
settings.proxy，随后应用 Command Code 的 HTTPS-downgrade（等价于 WithProxy 保留
rejectHTTPSDowngrade），最后交给 provider transformer 的 CustomizeExecutor。
`ChatCompletionOrchestrator.RunPipelineAsync` 让 ChannelOutboundFactory 的
tokenRefreshExecutor 收到按通道 executor（本地 OAuth TokenProvider 路径；grain 刷新
仍见 C094）；`ProcessSpecifiedAsync` 接受可选 proxy；`ChannelTestService.TestChannelAsync`
与 GraphQL `testChannel(input.proxy)` 接线，`testChannelAPIKeys` / `testChannelAPIKey`
按参照显式不带 override。

残余（已在台账注明）：grain 令牌刷新（C094）、ModelScope transformer 内部异步轮询 /
图片下载（C104）、视频任务 API 的 per-channel client（C105）、模型抓取（C158）、
端点探测仍走共享 executor；`server.disable_ssl_verify` 未建模，补记在 C027。

测试：新增 10 例。`ChannelProxyExecutorsTests` 8 例（无 proxy 回退共享、proxy 建
client 的字段映射、同值复用、变更替换、disabled 仍建 client、override 每次新建、
transformer 用通道 settings proxy、override 优先）；`AdminChannelTestEndpointTests`
新增 TestChannelUsesTheChannelSettingsProxy 与 TestChannelProxyOverrideWinsOverTheChannelSetting
（GraphQL → tester → pipeline 全链路，断言请求落在对应 executor）。

本轮实测：`AxonHub.Api.Tests` 6141 通过 / 0 失败 / 0 跳过（27m10s，仓库
global.json 固定的 SDK 10.0.401，日志 `.agent/summary/_api_full_177.log`，
6141 = §176 的 6131 + 新增 10）；定向 `ChannelProxyExecutorsTests`、
`AdminChannelTestEndpointTests` 与 `ChannelTestServiceTests` 68 例通过。前两次全量
尝试分别在 6038 / 4832 例因 §103 记录的 Api testhost 随机崩溃中止（均为 0 断言
失败），第三次通过。Persistence / Grains / Llm 本轮未改动，沿用 §176 引用的实测
376 / 175 / 3110，四项目合计 9802。

台账：C154 改为 resolved；C014 / C094 / C104 / C105 的残余与 decision 更新；C027
补记未建模的 disable_ssl_verify。269 条不变（195 accepted / 7 gap / 67 resolved），
lint 通过，`02-differences.md` 已重新生成。

§178（2026-11-11）关闭 C094 (5)：grain 令牌刷新跟随通道级 `settings.proxy`，与推理
共用同一个按通道 HTTP client（参照 channel_llm.go:113-130、707 把 `getHttpClient(c.Settings)`
同时交给 codex / xai_subscription / claudecode 的 TokenProvider）。

实现：`ChannelTokenSeed` 与 `ChannelTokenRefreshRequest` 各自追加
`[Id(5)] / [Id(6)] Settings`（原始 `channels.settings` jsonb 文本，追加字段保持 Orleans
序列化兼容）；`CreateTokenSeed`（ChannelOutboundFactory）与 codex 目录的
`CreateTokenProvider`（缓存与 live 两个分支）都把候选行的 settings 填入 seed。
`ChannelTokenGrain` 以 `updated_at` 为版本采纳快照：更旧 seed 完全忽略；**相等版本只更新
settings、不回滚已采纳凭据**——grain 自身刷新写回会推进 updated_at，缓存重建读到的同
版本 seed 仍须交付并发修改过的 proxy（channel.go:291-306）。`ChannelTokenSource` 改为
注入 `ChannelProxyExecutors`，按 `request.Settings` 经新增的
`Resolve(long channelId, string? settingsJson)` 取 executor；它与候选路径共用
`ResolveChannel` 的 channelId + 指纹缓存，无 proxy 时移除缓存并回退共享 executor，
`type: disabled` 仍建独立 client 以抑制环境代理。畸形 settings 沿用
`ChannelTransformOptions.SettingsOfRaw` 的宽容解析（C013 / C157）。

残余（台账已注明）：C094 的端点表与 fenno / copilot / antigravity 生命周期等其余项
不变；C104（ModelScope 异步轮询 / 图片下载）、C105（视频任务 API）、C158（ModelFetcher）
仍按各自条目为残余。

测试：新增 6 例，Api 4 + Grains 2。`ChannelTokenSourceTests` 8 例（原 6 + 按通道 proxy
刷新、无 proxy 回退共享）；`ChannelProxyExecutorsTests` 10 例（原 8 + 原始 settings 与
候选路径共享缓存 client、无 proxy 回退共享）；`ChannelTokenGrainTests` 10 例（原 8 +
seed settings 透传到刷新请求、同版本 seed 更新 settings 而不回滚凭据）。

本轮实测：`AxonHub.Api.Tests` 6145 通过 / 0 失败 / 0 跳过（30m4s，仓库 global.json
固定的 SDK 10.0.401，日志 `.agent/summary/_api_full_178.log`，6145 = §177 的 6141 +
新增 4）；定向 `ChannelTokenSourceTests`、`ChannelProxyExecutorsTests` 与 codex 目录
相关测试 23 例通过；`AxonHub.Grains.Tests` 全量 177 通过 / 0 失败 / 0 跳过（175 + 新增
2）。Persistence / Llm 本轮未改动，沿用 §176 引用的实测 376 / 3110，四项目合计 9808。
前两次 Api 全量尝试分别在一次 SafeguardsEndpointTests 的 10ms attempt-timeout 用例
失败（单跑该类 21 例全通过，判定为全量并行负载下的时序抖动）与一次纯 testhost 随机
崩溃（4123 例、0 断言失败，§103 记录的同类抖动）后中止，日志
`.agent/summary/_api_full_178a-crash.log` / `_api_full_178b-crash.log`。

台账：C094 (5) 关闭；C227 记录 seed settings 与三个测试文件的计数；C154 的
ChannelProxyExecutorsTests 计数 8→10；台账卫生把已闭合的 C091 改为 accepted 并订正
C014 / C158 的过期残余描述。269 条（196 accepted / 6 gap / 67 resolved），lint 通过，
`02-differences.md` 已重新生成。

§179（2026-11-12）关闭 C104 / C105 / C158 的按通道 HTTP client 残余：ModelScope 异步
图片任务的轮询与下载、视频任务的查询 / 删除、模型抓取的常规 / Cline / Antigravity
三条分支都与推理共用通道的代理感知 client（参照 channel_llm.go:1096-1108 的
`Config.HTTPClient`、biz/video.go:37,136 的 `ch.HTTPClient`、model_fetcher.go:374-387 /
524-530 与 model_fetcher_antigravity.go:20-36 的 `settings.proxy` 派生 client）。

实现：`ChannelOutboundFactory.Create` 的 executor 参数由 `tokenRefreshExecutor` 更名为
`channelExecutor`（同一引用，供 OAuth 刷新与 ModelScope transformer 内部 HTTP），
Modelscope 分支把它填入 `ModelscopeOutboundConfig.HttpClient`。`VideoService` 注入
`ChannelProxyExecutors`，`GetTaskAsync` / `DeleteTaskAsync` 经新增
`LoadTaskWithExecutorAsync` 按 `channel.Id + settings` 解析 client（无 proxy 回退共享
executor；`LoadTaskAsync` 保留 2-tuple 包装给既有测试）。`ModelFetcher` 注入同一注册表，
新增 `ExecutorFor(channel)`：已保存通道按行 settings 解析，Cline 推荐目录、常规模型发现
（先取按通道 client 再应用 `WithRejectHttpsDowngrade`）与 Antigravity 目录 / 令牌刷新都
使用它；provider-conf 仍走共享 `_executor`，与参照一致。`PersistentOutboundTransformer`
的默认工厂保持不解析 proxy，避免覆盖 testChannel 的 per-request 语义（C154）。

残余（台账已注明）：端点探测仍走共享 executor（C158 残余）；C082/C094 的端点记录表与
live-only 分支不变；C104/C105 的语言级差异不变。

测试：新增 6 例。`ModelscopeChannelWiringTests` 5 例（原 4 + 按通道 executor 轮询与
图片下载）；`VideoServiceTests` 新增 2 例（GET / DELETE 均走通道 proxy、无 proxy 回退
共享），`ModelFetcherTests` 新增 2 例（常规发现与 Cline 发现走已保存通道的 proxy），
`ModelFetcherAntigravityTests` 新增 1 例（Antigravity 目录与令牌刷新走通道 proxy）。

本轮实测：`AxonHub.Api.Tests` 6151 通过 / 0 失败 / 0 跳过（24m18s，SDK 10.0.401，
日志 `.agent/summary/_api_full_179g.log`，6151 = §178 的 6145 + 新增 6）；定向
`ModelscopeChannelWiringTests`、`VideoServiceTests`、`ModelFetcherTests`、
`ModelFetcherAntigravityTests` 等 91 例通过。此前五次全量尝试：三次 §103 记录的
testhost 随机崩溃（5863 / 6004 / 5238 例，均 0 断言失败），一次 SDK 10.0.401 从
Program Files 消失导致未启动（改用 dotnetup 安装的 dotnet.exe 解决），一次完整跑完但
`SafeguardsEndpointTests.CompletedUpstreamStaysOpen` 因并行负载下 500ms 分类超时失败
（单跑该类 21 例 11s 全通过，与 §178 记录的同类时序抖动一致）；一次按用户要求中止。
日志 `_api_full_179.log` / `_api_full_179b.log` / `_api_full_179c.log` /
`_api_full_179d.log` / `_api_full_179e.log` / `_api_safeguards_179.log`。Persistence /
Grains / Llm 本轮未改动，沿用实测 376 / 177 / 3110，四项目合计 9814。

台账：C104 / C105 / C158 的残余与 decision 更新（C158 ②关闭），C014 补记 §179 接线；
269 条（196 accepted / 6 gap / 67 resolved），lint 通过，`02-differences.md` 已重新生成。

§180（2026-11-12）关闭 C096 (7)：claudecode 与 codex 的 api-key 出站补上执行 key 审计。
参照由 `getAPIKeyProvider`（channel_llm.go:169-184）把实际发送的 key 包进
`ChannelAPIKeyContextProvider` 发布到 ctx，`CreateRequestExecution` 据此写
`request_executions.channel_api_key_suffix` / `channel_api_key_index`；端口从出站的
`IApiKeyProvenance` 读取，而 claudecode 与 codex 此前没有实现该接缝（codex 的
alpha-search 请求同样发送 Bearer key）。

实现：`ClaudeCodeParams` / `CodexOutboundParams` 增加端口专用的 `AuditApiKey`（bool）与
`ChannelApiKeyIndex`（int?）；`ClaudeCodeOutboundTransformer` 与
`CodexOutboundTransformer` 实现 `IApiKeyProvenance`，在取到 `AccessToken` 后按
`AuditApiKey` 记录实际写入 `Auth.ApiKey` 的 token（codex 的普通 Responses 路径与
`CodexAlphaSearch` 各一处）。`ChannelOutboundFactory.CreateClaudeCodeOutbound` 的非 OAuth
分支设置 `AuditApiKey = true`、`ChannelApiKeyIndex = apiKeyProvider.KeyIndex`；
`CreateCodexOutbound` 以 `!ChannelCredentials.IsOAuth(...)` 决定同样的两项（codex 与
fenno 的非 OAuth 分支共用）。OAuth 分支保持不审计：参照的 OAuth 路径不经过
`getAPIKeyProvider`，访问令牌不是通道 key；索引沿用 `ChannelApiKeyProvider.KeyIndex`
（全量 key 列表的 1-based 位置，单 key 或不在列表时为 null）。

测试：新增 11 例。Llm 5 例（`ClaudeCodeOutboundTransformerTests` 2：审计记录 / OAuth
不审计；`CodexOutboundExecutorTests` 2：同上；`CodexAlphaSearchTests` 1：alpha-search
审计）。Api 6 例（`ClaudeCodeAndOpenCodeChannelWiringTests` 2、
`CodexChannelWiringTests` 2、`ChannelApiKeyAuditTests` 2 例端到端断言后缀与 1-based
索引，覆盖 claudecode 与 codex 多 key 通道）。

本轮实测：`AxonHub.Llm.Tests` 3115 通过 / 0 失败 / 0 跳过（§179 的 3110 + 5）；
`AxonHub.Api.Tests` 定向 45 例通过（上述三个改动类），全量未跑——应要求停掉本轮
全量（约 24 分钟），并按本节起采用的分层验收策略顺延。Persistence / Grains 未改动，
沿用 376 / 177；`AxonHub.Api.Tests` 静态计数 6157（6151 + 6），四项目合计 9825，
等待下一次全量复核。

分层验收策略（自 §180 起）：每次切片跑受影响项目的定向测试，Llm 类切片同时跑
`AxonHub.Llm.Tests` 全量（秒级）；`AxonHub.Api.Tests` 全量只在以下情形触发：改动
编排中间件 / 请求管线 / 持久化落库 / DI 与宿主启动 / 共享序列化等横切面，跨多个通道
家族的共享代码，连续 3–5 个切片未跑，或发布前收口。定向失败或全量失败时先单类重跑，
以区分 §103 记录的 testhost 随机崩溃与真实回归。

台账：C096 (7) 关闭、C013 的审计用例数更新，lint 通过，`02-differences.md` 已重新生成；
269 条（196 accepted / 6 gap / 67 resolved）。

§181（2026-11-12）收口 C097 (6)：全量移植 `llm/modelname/model.go` 的上游模型名提取。
参照的 `FromResponse` / `FromEvent` 按请求的 API format 分派：非流式响应取协议元数据
（Anthropic、Responses 与 Responses Compact / 图片生成的顶层 `model`；Gemini 与 Gemini
Embedding 的 `modelVersion`，缺失时再取 `response.modelVersion`；OpenAI chat / legacy
completion / Ollama chat 的 `model`；Cline 信封在 `success` 为布尔真时取 `data.model`；
Decisions / Embedding / Moderation / ImageVariation / TypeSafe / Transcription /
Translation / Video / Jina Embedding / Jina Rerank / Seedance / Zenmux Video 的仅非流式
`model`），流式事件只从生命周期事件读取（Anthropic `message_start` 的 `message.model`、
Responses 七个生命周期事件 `response.created` / `in_progress` / `completed` / `incomplete` /
`failed` / `cancelled` / `canceled` 的 `response.model`、Gemini 的 `modelVersion`），
其余格式与事件一律为空。响应上记录的 `Request.APIFormat` 非空时覆盖执行格式；状态码
>= 400 不取模型；transcription / translation 以 JSON media type 设闸（`application/json`
或 `+json` 后缀）；`validModel` 的 512 字节、TrimSpace 非空与控制字符规则原样移植。

实现：`RequestChainPersistenceMiddleware` 的 `EventModel` 保留“首事件取到非空值即停”的
参照语义；`ResponseModel` 接入非流式成功与 `OnOutboundRawError` 两处落库；`ReportedModel`
按上表逐分支移植，`FirstJsonModel` 支持 `message.model` / `response.model` 点路径并在值
无效时回退下一路径，`IsJsonMediaType` 对应 Go 的 `mime.ParseMediaType`；无效 JSON、
非对象根与空响应体返回空。删除重构后不再使用的 `ValidModel(JsonElement)` 重载。

测试：`RequestChainModelNameTests` 由 4 例增至 29 例（19 Fact + 10 Theory）。新增 Anthropic
`message_start` 嵌套模型与事件类型优先级、Responses 七个生命周期事件（Theory）与内容
事件为空、codex 图片格式流、Gemini 两级 `modelVersion`、Responses 与 Anthropic 非流式、
清单外格式（AiSDK / Speech）为空、transcription 的 text/plain 与 application/json /
`+json` 三态、400 错误状态、请求格式覆盖、Cline 信封的 success 闸门、二进制音频块、
数字 / 空白 / 控制字符 / 513 字节四类无效值。

本轮实测：`AxonHub.Api.Tests` 6182 通过 / 0 失败 / 0 跳过（21m33s，全量重建运行），
与 §180 静态计数 6157 + 25 一致；`AxonHub.Llm.Tests` 3115、`AxonHub.Persistence.Tests`
376、`AxonHub.Grains.Tests` 177 未重跑，四项目合计 9850。定向 35 例（模型名 29 +
`RequestChainPersistenceTests` 6）在删除死代码后复跑通过。

台账：C097 (6) 关闭；`what` 记录非法 UTF-8 模型值的极窄残余（共享修复器把非法字节
变为 U+FFFD 后接受，参照 gjson 原样返回后被 `validModel` 的 UTF-8 检查拒绝，沿用 C008
的修复策略、无测试）；lint 通过，`02-differences.md` 已重新生成；269 条（196 accepted /
6 gap / 67 resolved）。

§182（2026-11-12）纠正 apiKeyOverride 的台账并补齐通道 key 测试流的凭据语义。
§76 已把 `ChannelCandidate.ApiKeyOverride` 接到 `ChannelApiKeyProvider.ForOverride`，
`ChannelOutboundFactory` 在构建出站前按参照先 `TrimSpace` 再应用；但 C013 / C014 /
C097 (7) 仍写着“apiKeyOverride 未移植”，且参照的强制 key 分支没有直接端口测试。
参照路线：`channel_llm.go:655-658`（TrimSpace 与仅非空才设置）、`:677-693`（按通道
类型的凭据检查）、`:716`（override 写入通道）、`:1392-1428`（ollama /
ollama_anthropic 的 override 感知 provider 守卫）、`request.go:414-421`（1-based
审计索引仅在全量 key 列表命中时写入）。

实现：工厂与 provider 无需改动（§76 / §164 已按参照实现），只纠正
`ChannelOutboundFactory` 中“端口没有 key-test flow”的过时注释。新增
`tests/AxonHub.Api.Tests/ChannelApiKeyOverrideTests.cs` 11 例：ollama /
ollama_anthropic 无存储 key 时 override 安装 provider 并发送 Bearer（参照
`TestOllamaAnthropicChannel_APIKeyOverrideWithoutStoredKeys` 的直接移植，另含原生
ollama 的对称面）；空白 override 被 TrimSpace 成空、保持无认证；commandcode /
commandcode_anthropic 的存储 key 全部禁用时 override 通过检查并按 TrimSpace 后的值
发送 Bearer；空白 override 不满足命令码检查（missing api key）；default 类型
（OpenAI）的 override 不能替代缺失的存储 key（`channel_llm.go:691-693`）；空白
override 回退存储 key；override 命中存储列表时审计索引为 1-based 位置，不在列表时
为 null。

本轮实测：定向 136 例通过（新增 11 + `OllamaChannelWiringTests` 7 +
`CommandcodeChannelWiringTests` 12 + `ChannelApiKeyStrategiesTests` 57 +
`ChannelTestServiceTests` 49）；`AxonHub.Api.Tests` 未跑全量，静态计数 6182 → 6193，
四项目合计 9850 → 9861（Llm 3115 / Persistence 376 / Grains 177 未重跑）。

台账：C013 / C014 的 apiKeyOverride 描述纠正为“已由 §76 接线、§182 锁定”，C014
补记 11 例；C097 (7) 关闭。lint 通过，`02-differences.md` 已重新生成；269 条
（196 accepted / 6 gap / 67 resolved）。

§183（2026-11-12）关闭 C094 (6)：取消令牌贯穿同步 outbound transform，客户端断开可中止
OAuth 刷新。参照的 `TransformRequest(ctx, ...)` 把 `ctx` 传给 `TokenProvider.Get`，
端口此前的 transformer 接口没有上下文参数，Codex / Codex alpha search / ClaudeCode /
xai_subscription / antigravity 都以 `CancellationToken.None` 取令牌，刷新不会随请求取消。

实现：`LlmRequest.RequestCancellationToken`（port-only 接缝，镜像 C104 的
`HttpRequestData.RequestCancellationToken`；`[JsonIgnore]`，`ShallowClone` 携带）
承载 transform 阶段的调用方令牌；`Pipeline.ProcessRequest` 在 outbound transform 前
写入，`PersistentOutboundTransformer` 在 channel transform options 可能克隆请求后
再次透传；上述五个 transformer 改读该令牌。端点探测按
`channel_endpoint_detect.go:155` 把 `probeCtx` 传给 `TransformRequest`（此前只在
`Do` 使用），探测自身超时在 transform 内抛出的 `OperationCanceledException` 归类为
invalid，外层取消仍向上传播。

测试：新增 `tests/AxonHub.Llm.Tests/TransformRequestCancellationTests.cs` 7 例
（管线把 `Process` 的令牌交给 outbound、Codex / alpha search / ClaudeCode /
xai_subscription / antigravity 的刷新收到请求令牌、令牌不进入 JSON）与
`tests/AxonHub.Api.Tests/PersistentOutboundCancellationTests.cs` 2 例（无选项时透传
同一请求、`forceArrayInputs` 克隆后仍保留令牌）。

本轮实测：新增 9 例通过；定向 377 例（Llm：Codex / ClaudeCode / XaiSubscription /
Antigravity / ChannelEndpoints / PipelineRetry / OAuthTokenProvider）与 143 例（Api：
ChannelTransformOptions / Codex 与 ClaudeCode / OpenCode / XaiSubscription 接线 /
ResponsesEndpointDispatch / 端点探测 / PersistentOutbound）通过。`AxonHub.Api.Tests`
未跑全量，静态计数 6193 → 6195；四项目合计 9861 → 9870（Llm 3115 → 3122 /
Persistence 376 / Grains 177 未重跑）。

台账：C094 (6) 关闭并补记 9 例；C094 保持 gap（端点表、fenno / github_copilot /
antigravity 的 provider 复用、模型拉取管理等残余未变）。lint 通过，
`02-differences.md` 已重新生成；269 条（196 accepted / 6 gap / 67 resolved）。

§184（2026-11-12）关闭 C094 (1)：用户端点按 API format 分派出站。参照
`buildChannelWithOutbounds` 先建类型默认出站，再对每个用户端点调用
`buildNonDefaultEndpointOutbound` 并以 format 为键覆盖默认表；端口此前只在
Responses 家族的若干分支消费端点的 BaseURL/Path/Transport，其余家族仍按通道类型与
通道基址分派。

实现：`ChannelOutboundFactory.Create` 在选中用户端点（格式非空）时改走新的
`CreateUserEndpointOutbound`——`buildNonDefaultEndpointOutbound` 的完整移植。端点
BaseURL 优先、空则继承通道 BaseURL（ZenMux 空基址先套用类型默认值），transport 由
继承后的基址/显式 transport 推断；websocket 仅允许 Responses 家族，Command Code
端点基址仍校验 HTTPS。chat 分派保持参照的家族规则：Cline 保信封；zai/zhipu/xiaomi/
doubao 家族仅在显式 path 或 `providerChatEndpointUsesFamilyVersion` 时保留家族
transformer，否则回退通用 OpenAI。responses/compact 的 codex/fenno 分支、alpha
search 的 codex 分支、anthropic/messages 的 Command Code 与 Direct 平台、MiniMax
图片、ZenMux video、Gemini/Jina/TypeSafe/completion/decisions/embedding/音频/图片/
视频均带端点 path；opencode_go 与 opencode_go_anthropic 的用户端点再包
`WithSessionHeader`。端点探测的 `CreateNonDefaultEndpointOutbound` 改为调用同一分派
（合成端点仅带 format、继承通道基址、path 为空），并抛参照文案的
`unsupported api_format "..."`。

测试：新增 `tests/AxonHub.Api.Tests/ChannelEndpointDispatchTests.cs` 13 例（zai 家族
chat 的显式 path / 空 path 通用回退 / v4 基址家族保留、doubao path、minimax 图片
path、zai_anthropic 的 Direct 端点与 X-API-Key、Command Code Bearer、gemini/jina/
typesafe path、xai_subscription responses 端点覆盖、opencode 会话头包裹、chat +
websocket 拒绝）。

本轮实测：新增 13 例通过；定向 378 例（Api：ResponsesEndpointDispatch / 24 个
*WiringTests / ChannelTestService / PersistentOutboundRetry / ChannelTransformOptions）
与 89 例（dispatch + zai/zenmux/detect）通过。`AxonHub.Api.Tests` 未跑全量，静态计数
6195 → 6208；四项目合计 9870 → 9883（Llm 3122 / Persistence 376 / Grains 177 未重跑）。

台账：C094 (1) 关闭并补记 13 例；C094 仍保留 (10) AllowPassThroughBody 严格 JSON
探测、(11) DecodeAuthJson 消费方与 §146 已记录的 fenno / github_copilot /
antigravity provider 复用。§183 的 Api 静态计数由 6202 更正为 6195。lint 通过，
`02-differences.md` 已重新生成；269 条（196 accepted / 6 gap / 67 resolved）。

§185（2026-11-12）关闭 C096 (8)：claudecode / codex 的 OAuth 凭据错误文案。参照
`buildChannelWithTransformer` 的 claudecode 分支（channel_llm.go:924-949）与
`buildCodexOutbound`（:334-356）分两阶段处理凭据：先把 typed `oauth` 对象按字段
拷贝（不复制 `id_token`）重新序列化，失败报
`failed to encode {channelType} oauth credentials: %w`；再解析凭据文档，失败报
`failed to parse {channelType} oauth credentials: %w`。端口此前只包装了 parse 阶段，
encode 阶段（`ParseOAuthObject(...).ToJson()`）的失败以原始 `JsonException` 逃逸，
且重序列化会带上 `id_token`。

实现：`ChannelOutboundFactory.ParseOAuthCredentials` 现保留参照的两阶段结构。typed
`oauth` 对象存在时按参照字段拷贝（AccessToken / RefreshToken / ClientId / ExpiresAt /
TokenType / Scopes，丢弃 `id_token`）重新序列化，decode 或 marshal 失败统一归入
encode 阶段并保留参照文案；随后 `ParseCredentialsJson` 的失败保留 parse 文案。
端口由宽松载入（C013）把参照在 Ent 解码阶段就会拒绝的畸形 oauth 对象带到此处，
其错误现归入 encode 文案，与参照的可观察文案对齐。

测试：`ClaudeCodeAndOpenCodeChannelWiringTests` 新增
`ClaudeCodeOAuthCredentialsKeepTheReferenceParseErrorCopy`（legacy apiKey 中的 OAuth
JSON 空 access_token → `failed to parse claudecode oauth credentials: access_token is
empty`）与 `ClaudeCodeOAuthCredentialsKeepTheReferenceEncodeErrorCopy`（畸形 typed
oauth → `failed to encode claudecode oauth credentials: ` 前缀，2 例）；
`CodexChannelWiringTests` 新增 `CodexOAuthCredentialsKeepTheReferenceParseErrorCopy`
（1 例）。

本轮实测：新增 3 例通过；定向 39 例（两个接线测试类）与 580 例（OAuth / ChannelToken /
ClaudeCode / Codex / GithubCopilot / Antigravity）通过。`AxonHub.Api.Tests` 未跑全量，
静态计数 6208 → 6211；四项目合计 9883 → 9886（Llm 3122 / Persistence 376 / Grains 177
未重跑）。

台账：C096 (8) 关闭，C096 全部条目关闭（resolved）；lint 通过，`02-differences.md`
已重新生成；269 条（196 accepted / 5 gap / 68 resolved）。

§186（2026-11-12）关闭 C094 (10)：codex `AllowPassThroughBody` 的 gjson 宽松探测。
参照 `codex.OutboundTransformer.AllowPassThroughBody`（outbound.go:180-192）对
`max_output_tokens` / `max_completion_tokens` / `max_tokens` 逐一调用
`gjson.GetBytes(llmReq.RawRequest.Body, field).Exists()`；gjson 不做 JSON 校验，
截断或畸形的 body 仍可能命中顶层键。端口此前用严格 `JsonDocument.Parse`，解析失败
即放行。

实现：新增 `src/AxonHub.Llm/Json/GjsonRootKeyProbe.cs`，逐字移植 gjson v1.18.0
（MIT）的 `Get` / `parseObject` / key 扫描 / `unescape` / `parseString` /
`parseSquash` / `parseNumber` / `parseLiteral`：扫描首个 `{` 或 `[`（数组根不命中）、
key 的引号终止与反斜杠奇数/偶数规则、转义 key 经 `unescape` 后比较、值扫描跳过
`:` 与 `,` 等任意非值字节（匹配 key 无值时越过 `}` 继续）、字符串值必须终止、
对象/数组用 squash、`n`（非 `nu`）/`t`/`f` 与数字/`+`/`-`/`i`/`I`/`N` 字面量。
`AllowPassThroughBody` 的三字段探测改用该实现，删去严格解析与 catch 放行。

测试：`CodexOutboundExecutorTests` 新增 39 例：38 个差分语料行，语料由 Go +
gjson v1.18.0 对 40 个 body 实跑 `gjson.GetBytes(...).Exists()` 录制（截断的对象/
数组/数字、未终止字符串、未加引号 key、缺逗号/冒号、`{"max_tokens"} 5`、
`max\u005ftokens` 转义 key、leading garbage、字符串根内的对象文本、null/true/false/
NaN、数组根、嵌套 key、空 body 等），另 1 个 UTF-8 BOM 前置用例。

本轮实测：新增 39 例通过；`AxonHub.Llm.Tests` 全量 3161 例通过；Api 定向 96 例
（PassThroughTests / CodexChannelWiringTests 等）通过。

台账：C094 (10) 关闭，C094 仍为 gap（保留 (11) DecodeAuthJson 消费方与 §146 的
fenno / github_copilot / antigravity provider 复用残余）；lint 通过，
`02-differences.md` 已重新生成；269 条（196 accepted / 5 gap / 68 resolved）。

§187（2026-11-12）关闭 C094 (11)：codex 管理面 OAuth 端点。

参照 routes.go:114-116 在 admin group 上挂载 POST /admin/codex/oauth/start、
/oauth/exchange 与 /auth/decode（internal/server/api/codex.go）。端口此前只有 llm
模块的令牌提供者与 `CodexToken.DecodeAuthJson`，没有管理面 HTTP 表面；本切片接入
其消费方。

实现：新增 `src/AxonHub.Api/Management/CodexOAuthService.cs`（PKCE 会话、回调解析、
令牌交换、auth.json 规范化与错误映射）、`src/AxonHub.Api/Auth/CodexOAuthHandlers.cs`
（ShouldBindJSON 等价绑定、gin JSONError 信封、三条路由）与
`src/AxonHub.Api/Auth/CodexOAuthCache.cs`（go-cache 到期语义的进程内 typed 缓存，
redis / two-level 见 C171）；Program.cs 注册 DI、`MapCodexOAuthRoutes()` 与
`/admin/codex` 的 admin JWT + WithProjectID 门。start 用 `GenerateState(32)` 与
`GenerateCodeVerifier(64)` 生成 S256 PKCE，按 `codex:oauth:<state>` 写入 10 分钟 TTL，
授权 URL 按 `url.Values.Encode` 排序并含 `id_token_add_organizations=true`、
`codex_cli_simplified_flow=true`、不含 `originator`。exchange 复刻参照次序：命中
会话后立即删除（回调畸形或 state 不匹配也会消耗会话），再解析 `callback_url`、校验
state；`proxy.type=url` 经 `HttpProxyExecutorFactory`，令牌交换失败 502、凭据序列化
失败 500。decode 走 `CodexToken.DecodeAuthJson` + `ToJson`，失败 400。

测试：`tests/AxonHub.Api.Tests/CodexOAuthServiceTests.cs`（32 例：PKCE 形状、授权 URL
排序与 originator 缺席、TTL 边界、回调错误文案与先删会话次序、令牌请求形状、双层
错误包装、代理分支、auth.json 规范化的精确 JSON / 零值 expiry / 一小时回退 / 空白
access_token / 畸形 JSON）与 `tests/AxonHub.Api.Tests/AdminCodexOAuthEndpointTests.cs`
（37 例：三条路由的 JWT 门、绑定失败、gin 信封、400/401/500/502、会话单次消费、
代理路由）。

本轮实测：新增 69 例通过（连同既有 CodexChannelWiringTests 的 4 个 CodexOAuth 用例，
过滤跑 73 例通过）。`AxonHub.Api.Tests` 未跑全量，静态计数 6211 → 6280；四项目合计
9925 → 9994（Llm 3161 / Persistence 376 / Grains 177 未重跑）。

台账：C094 (11) 关闭，C094 仍为 gap（保留 §146 的 fenno / github_copilot /
antigravity provider 复用）；新增 C270（gap：xai / claudecode / copilot 管理面 OAuth
端点未移植）；lint 通过，`02-differences.md` 已重新生成；270 条（196 accepted /
6 gap / 68 resolved）。

§188（2026-11-12）关闭 C270：xai / claudecode / copilot 管理面 OAuth 端点。

参照 routes.go:117-122、127-128 在 admin group 上挂载 POST /admin/xai/oauth/start、
/oauth/exchange、/oauth/sso（internal/server/api/xai.go），POST
/admin/claudecode/oauth/start、/oauth/exchange（api/claudecode.go）与 POST
/admin/copilot/oauth/start、/oauth/poll（api/copilot.go）。端口此前只有这三类
provider 的 llm 层构件（PKCE、SubscriptionToken、ClaudeCodeTokenProvider、
SubscriptionSsoDevice、OAuthCredentials.ToJson），没有管理面 HTTP 表面；本切片
接通七个端点。

实现：新增 `src/AxonHub.Api/Management/XaiOAuthService.cs`、
`ClaudeCodeOAuthService.cs`、`CopilotOAuthService.cs`（会话缓存、回调解析、令牌
交换、设备码轮询与错误映射）、`src/AxonHub.Api/Auth/XaiOAuthHandlers.cs`、
`ClaudeCodeOAuthHandlers.cs`、`CopilotOAuthHandlers.cs`（ShouldBindJSON 等价绑定、
gin JSONError 信封、七条路由）与 `XaiOAuthStateCache.cs`、`ClaudeCodeOAuthCache.cs`、
`CopilotDeviceFlowCache.cs`（go-cache 到期语义的进程内 typed 缓存；copilot 复刻
0→默认 5 分钟、负值→永不过期，redis / two-level 见 C171）。Program.cs 注册 DI、
`MapXaiOAuthRoutes()` / `MapClaudeCodeOAuthRoutes()` / `MapCopilotOAuthRoutes()` 与
`/admin/xai`、`/admin/claudecode`、`/admin/copilot` 的 admin JWT + WithProjectID 门。
xai start 不读 body，`GenerateState(32)` / `GenerateCodeVerifier(32)` / nonce 与
S256 PKCE 按 `xai:oauth:<state>` 写入 30 分钟 TTL 并绑定 UserId；exchange 按 Get
（错误按 miss）→ 未登录 401 → UserId 不符 400 且保留会话 → 解析回调（full URL /
code 与 state）→ state 不符 400 且保留会话 → Delete（失败仅 warn）→ 仅 url 非空
代理经 `HttpProxyExecutorFactory` → 令牌交换失败 502 → 凭据序列化失败 500；sso 走
`SubscriptionSsoDevice.ConvertSsoToBuild`，失败 502。claudecode start 生成 64 字节
verifier 并写入 10 分钟 TTL，绑定空结构体（空 body / 畸形 JSON 400，`{}` / `null`
200）；exchange 先 Delete 再解析（回调畸形或 state 不匹配也消耗会话），state 优先
取 fragment、再回退 query，令牌请求为带 state 的 JSON，失败 502。copilot start 仅
容忍空/纯空白 body（Go EOF 语义），设备码缓存 TTL=min(expires_in,15 分钟)；poll
用严格 `>` 过期判断，JSON/form 双解析，pending / slow_down 保留会话，
expired_token / access_denied 删除并回 400，成功删除并回 complete，未知错误 502，
空响应 500。`ProxyExecutorFactory` 抽出 `HttpProxyExecutorFactory.CreateTransport`
供 xAI SSO 的非代理分支构造 transport（测试经 port-only `ssoTransportFactory`
接缝注入脚本化 handler）。

测试：`tests/AxonHub.Api.Tests/XaiOAuthServiceTests.cs`（30 例）、
`ClaudeCodeOAuthServiceTests.cs`（29 例）、`CopilotOAuthServiceTests.cs`（32 例）、
`AdminXaiOAuthEndpointTests.cs`（29 例）、`AdminClaudeCodeOAuthEndpointTests.cs`
（29 例）、`AdminCopilotOAuthEndpointTests.cs`（29 例），合计 178 例（服务 91、
端点 87）：PKCE 形状与授权 URL 键序、TTL 边界、会话保留 / 消耗矩阵、回调错误文案
与解析次序、令牌请求的精确表单 / JSON 形状、双层错误包装、gin 信封与绑定矩阵、
三条 poll 状态机、代理工厂 url 分支。

本轮实测：新增 178 例通过（定向 `dotnet test`，耗时约 1 s）；`AxonHub.Api.Tests`
未跑全量，静态计数 6280 → 6458；四项目合计 9994 → 10172（Llm 3161 / Persistence
376 / Grains 177 未重跑）。

台账：C270 关闭（resolved）；新增 C271（accepted：System.Uri 回调解析接受边界与
本地化文案、claudecode fragment 解码、System.Text.Json 解码文案、SSO 传输构造、
进程内缓存与 C171）；lint 通过，`02-differences.md` 已重新生成；271 条（197
accepted / 5 gap / 69 resolved）。

## 公开 API 审计

- 参照实现导出数：未统计（M5 收口时执行）
- 本实现公开表面：`AxonHub.Llm`（统一模型、OpenAI 双向变换、SSE 解码、HTTP 执行器、pipeline）、
  `AxonHub.Persistence`（EF Core 写侧 DbContext 与模型、Dapper 读侧仓储、幂等迁移与连接工厂）、
  `AxonHub.Grains` / `AxonHub.Grains.Abstractions`（通道池与通道指标 grain、ChannelLimiter/ChannelLimiterManager/ChannelRequestTracker/RateLimitAwareScorer）、
  `AxonHub.Api`（HTTP 宿主、`POST /v1/chat/completions`、`POST /v1/messages` 与 `POST /anthropic/v1/messages`（Anthropic Messages，含流式）、`POST /v1/responses` 与 `POST /v1/responses/compact`（Responses API，含流式）、`POST /v1/embeddings` 与 `POST /v1/moderations`（OpenAI Embeddings/Moderations）、`POST /v1/alpha/search` 与 `POST /v1/decisions`（OpenAI Alpha Search/Decisions）、`POST /v1/audio/speech`、`POST /v1/audio/transcriptions` 与 `POST /v1/audio/translations`（OpenAI Audio，含 SSE 与二进制流写出）、`POST /v1/rerank`、`POST /jina/v1/rerank` 与 `POST /jina/v1/embeddings`（Jina Rerank/Embedding）、`POST /v1/videos`、`GET /v1/videos/:id` 与 `DELETE /v1/videos/:id`（OpenAI Video 创建、`VideoService` 的任务查询/删除与轮询快照落库、`VideoEndpoint` 胶水）、`GET /doubao/v3/contents/generations/tasks/:id` 与 `DELETE /doubao/v3/contents/generations/tasks/:id`（Seedance 任务查询/删除，`DoubaoVideoEndpoint` 胶水与 gin `c.Data` 的 Content-Type-only 头语义）、`GET /v1/models` 与 `GET /v1/models/:model`（`ModelService` 的通道派生清单、`XRegexp` 黑名单语义、`SystemModelSettingsService` 设置读取）与 `GET /anthropic/v1/models`（Anthropic 分页线路形状）、`RequestTracingMiddleware`（`ar-<uuid>` 请求 ID 与响应头 / 上下文回显）、`TraceMiddleware`（trace 头 / extra headers / OpenCode / Claude Code / Codex / body 字段提取、`traces` 落库与 `AH-Trace-Id` 响应头）、`TracingOptions`、`TraceContext` 与 `ThreadContext`、`TraceStore`（`GetOrCreateTrace`）、`ThreadMiddleware`（thread 头 get-or-create 与 `ThreadContext` 发布）、`ThreadStore`（`GetOrCreateThread`）、请求读取、SSE 写出、orchestrator 适配与 `WithInbound` 入站替换接缝、管理员 GraphQL 门（`AdminGraphqlAuthMiddleware` / `ProjectIdMiddleware` / `ApiKeyReadOnlyMiddleware` 与 `HttpContextManagementIdentity`）、
  API key 鉴权中间件与 header 提取、gin 兼容的 ClientIP 与 allowlist 匹配、`projects` / `users` / `api_keys` / `systems` / `traces` / `threads` 仓储、
  系统安全设置读取与 `/v1` IP 黑名单中间件、Billing（成本计算器、价格读取/注入接缝与 CostItem JSON 形状）、
  通道 API key 选择（`ChannelApiKeyProvider` 与 `IApiKeyProvenance` 审计接缝））
- `AxonHub.Api` 的管理面 channels 读取面：`channels` / `queryChannels` /
  `allChannelSummarys` / `allChannelTags` / `countChannelsByType`、`node(Channel)`、Channel 输出类型与
  字段解析器（policies / settings / credentials / disabledAPIKeys / liveLimiterStats /
  defaultEndpoints / allModelEntries / providerQuotaStatus / channelModelPrices）、
  `ChannelModelPrice` / `ChannelModelPriceVersion` 输出类型与读取边（channel / versions /
  channelModelPrice，含 `ChannelModelPriceVersionStatus` 枚举）、`ChannelPrivacy` 的
  read_channels 查询策略、`ChannelAdminStore`
  （Dapper 键集分页与四条聚合 SQL）、`ChannelQueryService`、`CursorCodec` 的
  time / string / int 值类型与 `Map` 标量
- `AxonHub.Api` 的管理面 channels 生命周期写入面：`ChannelLifecycleService`（状态 / 删除 / 批量状态 / 批量删除 / 批量标签的 biz 错误前缀与 `ChannelLimiterManager.Forget`）、`IChannelWriteStore` 的 EF 实现（软删除 Unix 秒、批量数量的 `ExpectedRowsMismatchException`、标签管理的 `updated_at` 乐观守卫）以及 10 个 GraphQL 变更（updateChannelStatus / deleteChannel / bulkArchiveChannels / bulkDisableChannels / bulkEnableChannels / bulkRecoverChannels / bulkDeleteChannels / bulkAddChannelTags / bulkRemoveChannelTags / bulkManageChannelTags）
- `AxonHub.Api` 的管理面 channels updateChannel 写入面：`ChannelUpdateRecord`、`IChannelWriteStore.GetForUpdateAsync` / `NameExistsAsync` / `UpdateAsync`（单条 `ExecuteUpdateAsync` 的部分列写入、清空标志写 NULL 与 `updated_at` 乐观守卫）、`ChannelLifecycleService.UpdateAsync`（身份快照与 guard、命令码 / xai 归一化、活行同名检查、supportedModels 协议裁剪、设置 / 协议 / 端点校验、孤立 quota 清理、zenmux 凭据保留、顶点校验与投影返回）以及 `updateChannel` GraphQL 变更
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
- `AxonHub.Llm` 的 ModelScope 面：`ModelscopeOutboundTransformer` /
  `ModelscopeOutboundConfig`（`IOutboundTransformer` + `IApiKeyProvenance`，
  chat 委托内嵌 OpenAI 出站，图片走异步任务提交 / 轮询 / 下载转 base64）、
  `ApiFormats.ModelScopeImage`、`GoUrl.PathEscape` 与 `GoStrings.TrimSpace`
  （Go 语义复刻）、`HttpRequestData.RequestCancellationToken`（调用方 token 接缝）
- `AxonHub.Llm` 的 Doubao / Volcengine 面：`DoubaoOutboundTransformer` / `DoubaoOutboundConfig`
  （`IOutboundTransformer` + `IVideoTaskOutbound` + `IApiKeyProvenance`；partial 文件承载
  chat、图片、多模态 embedding 与 Seedance 任务四路，v3 归一、`##` 原始 URL、
  `user_id` / `request_id` 追加序、图片生成的排序 map 请求体与 data URL 下载）、
  `DoubaoVideoInboundTransformer`（`IInboundTransformer`，Seedance 创建请求解码与 client 侧
  `{id}` / 任务响应）、`ApiFormats.SeedanceVideo`
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
- 签名差异 / 运行时差异 / 平台限制：见 `02-differences.md`（C001–C198）

## 协议锁定

- [x] 关键协议字段名与顺序由测试锁定（`corpus/upstream-request`、`corpus/unified-request`、
  `corpus/client-response`、`corpus/client-stream`；见 `01-baseline.md` 第 2、3 节）
- [x] SSE 写出帧与客户端错误事件由 `SseStreamWriterTests` 逐字节锁定（gin-contrib/sse 兼容）
- [x] HTTP 重定向由 16 例锁定：301/302/303 的 POST→GET 与 body/body 头丢弃、307/308 保留方法
  与 body、跨域敏感头粘性剥离与子域保留、Referer 生成、10 跳上限、无 Location 的 3xx 原样返回、
  https→http 拒绝与 `Get "<url>": refusing HTTPS to HTTP redirect` 文案、RejectAll、拒绝时
  观察 3xx 响应头、DoStream 前缀与不可解析 Location 的 uerr 形状；命令码 / codex / Ollama /
  Zenmux 四条上游接线由 8 例锁定（C124 resolved、C158、C207、C221；残差 C222）
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
| C035：RateLimitAware 恒满分 | 已解决：`ChannelRequestTracker`（自然分钟窗口、只延长的冷却、惰性清理）、`ChannelLimiter`/`ChannelLimiterManager`（硬信号量、FIFO、配置重建）、`RateLimitAwareScorer`（RPM/TPM 与并发取最小值、-10000）接入四条策略链；RPM 准入、TPM 追踪与 429 冷却中间件挂在请求链路上；93 个用例锁定（64 grain + 29 API）。残余：C042（阻塞线程）、C043（单 InnerException）、C044（已由 §138 闭合）、C045（nil tracker 的 noop 不可达） | 已关闭；残余已登记 |
| C013：多 API key 的 trace 粘性 | 已解决：无 trace 时的随机分配、rendezvous/FNV-1a 选择器与 `channel_api_key_suffix`/`channel_api_key_index` 审计列已按参照实现（23 例）；`ChannelApiKeyProvider` 读取 `WithTrace` 发布的 trace id（线上请求 `SessionId` 接缝），多 key 通道按 `traceID|key` 做 rendezvous 固定选择，同一 trace 在重试与跨请求间同一 key，另有 1 例编排端到端锁定；参照的 1024 项 LRU 不单独保留（它只记忆固定启用集合内的 rendezvous 结果，通道重建会新建 provider 与空缓存） | 已关闭；残余 apiKeyOverride（通道 key 测试流程）与 `trace_sticky_mode` 通道粘性未接入 |
| C042–C045：限流的实现结构差异 | C042 等待中的请求占用线程而非 goroutine；C043 队列错误用 `Cause` 属性承载类型化原因而非多错误 Unwrap；C044 五个 OTel 限流指标已由 §138 按 System.Diagnostics.Metrics 移植，部署方订阅 axonhub Meter 即可获得 inflight/waiting/拒绝计数；C045 nil tracker 的 noop 路径不可达；C223 通用 HTTP metrics 亦已由 §139 按同名 instrument 与 provider 配置移植（默认关闭，开启后导出面一致） | 准入与打分行为等价，差异在资源模型与可观测性；已按同名 instrument 与 Snapshot 回调接入（§138、§139） |
| C014：成本计费、用户鉴权（JWT/OIDC/角色）与通道管理面未接入 | API key 鉴权与身份传播已接入（C023），系统安全设置与 IP 黑名单已接入（C028）：`/v1` 先过黑名单再过鉴权，request/execution/usage 的 project/api_key 已回填；通道级请求改写已接入（body/header 覆盖、模板渲染、sjson 路径语义，134 例锁定，残余 C037–C041）；通道限流与并发准入已接入（C035，93 例锁定，残余 C042–C045）；usage 成本计费与注入已接入（C046–C049，43 例锁定）；管理面登录（§80）与管理员 GraphQL 鉴权（§81）已接入，OIDC 提供方配置、启动发现与 providers 列表（§82）已接入，登录流程与角色权限仍未接入；通道写入校验已覆盖 createChannel（§67）、updateChannel（§68：部分列更新、乐观守卫与同一套 body/header、限流、设置、端点与凭据校验）与批量排序 / 批量自动禁用（§69）、端点保存（§70：类型与已存 modelProtocols 校验、updated_at 守卫）与 API key 记账（§71：禁用 / 启用 / 启用全部 / 启用选定 / 删除禁用 key，按冻结语义；live 的 Vertex key 删除与自动禁用事件 / 缓存刷新延后，见 C149）；覆盖模板 CRUD 与 `channel_model_prices` 管理面未接入（C037/C048/C143） | 后续里程碑按参照逐项接入并补测 |
| C046–C049：成本计费的精度、读取与未命中语义 | C046 已接受：金额用 `System.Decimal`（约 28–29 位有效数字），参照为 shopspring 任意精度，极端乘积可能有末位差异；C047 已接受：价格按请求实时读库（注入开启时最多两次查询），参照为进程内缓存，外部改价立即生效；C048 真实缺口：`channel_model_prices` 管理面（写入、reference-id 轮换、版本表、自动定价）未移植，§67 的 createChannel 与 §68 的 updateChannel 均尚未调用 ensureChannelModelPrices（C143）；C049 推断：未命中价格时 `cost_items` 写 JSON `null`（Ent `SetCostItems(nil)` 语义，Ent 源码不可得） | 读取路径与可观察金额一致；管理面随通道管理面落地；C049 若日后用真实 Ent 数据库核对为 `[]`，只需改中间件字面量并更新条目与测试 |
| C015：zstd 请求体与压缩错误文案 | 已解决：gzip/x-gzip/deflate/zstd 全部解码；gzip 头错误与 zlib/raw DEFLATE 分支逐字/逐分支对齐。残余差异仅损坏/截断压缩体的错误细节（缺 flate 偏移、gzip 尾部校验、zstd 内层文案） | 已关闭（ZstdSharp.Port；残余差异见 C015） |
| C016：通道级 `retryableStatusCodes` / `retryableErrorPatterns` 未生效 | 已解决：候选携带从 settings 解析的重试规则，`CanRetry` 按参照逐条判断（传输错误、429/5xx、额外状态码、子串/正则文本） | 已关闭（16 个谓词/解析用例 + 3 个端到端用例锁定） |
| C017：通道性能记录仅成功/失败计数，取消与无终态流语义不同 | 已解决：按 attempt 共享 `ChannelPerformanceRecord`，非流成功与 raw error 由指标中间件标记，流终态分类与聚合完成由持久化副本补记；取消不计故障，无终态且未聚合完整的流不计数。`retry_policy.auto_disable_channel` 的规则已解析并归一化（legacy statuses 迁移），但自动禁用动作未执行；残余还有性能明细字段与异步工作 | 已关闭（7 个记录语义用例 + 2 个端到端用例锁定） |
| C018：同步 recorder 每次调用一个短生命周期工作单元 | 一次请求链路最多 5 次短事务，request 与 execution 不同事务提交 | 对当前数据路径等价；引入批量/事务需求时重估 |
| C019：流式响应的 external id / body 与流式 request 响应头未回填 | 已解决：Close 时聚合分块回填两侧归档字段，终态分类与失败路径按参照实现 | 已关闭（测试锁定） |
| C020：`response_chunks` 与 `store_chunks` / `store_response_body` 存储策略未接入请求链路 | 已解决：§135 接入 `StoragePolicy` / `StoragePolicyCodec`（缺行默认、旧文档回填、cleanup 校验）、六个正文 / chunk loader（外部存储、`_ext` 标记折叠、状态门禁）、`SaveRequestChunks` / `SaveExecutionChunks` 与 v0.4.0 主存储启动种子；默认配置与参照逐字节一致，开启 store_chunks 后写数据库列或 `[{\"_ext\":1}]` 标记，关闭 store_response_body 后两侧都不写正文。残余：loader 的顺序加载与无日志差异（C191）、后端客户端缓存与分片差异（C199） | 已关闭（§135，66 + 11 例） |
| C021：下游响应头捕获点与服务器自动头集合不同 | Kestrel 与 gin 注入的自动头不同，落库 JSON 键集合有差异；业务头与脱敏一致 | 已接受（测试锁定业务头） |
| C008：非法 UTF-8 请求体 | 已解决：字符串字面量内的非法字节与孤立代理项转义按 Go 规则修复（每字节一个 U+FFFD，合法代理对保留），结构错误仍用原始字节报告。残余差异仅 JsonElement 承载的原始片段与请求体归档路径 | 已关闭（C008 残余差异见登记） |
| C010：非 chat 响应类型的空响应探测与视频端点 | 已解决：embedding 与 moderation 分支由第 17 节闭合（5 例，C076），图片分支由第 16.1 节闭合，decisions / alpha-search 分支由第 18 节闭合（2 例，C077），speech / transcription 分支由第 19 节闭合（5 例，C079），rerank 分支由第 20 节闭合（2 例，C080），video Llm 分支由第 21 节闭合（57 例，C081），video 端点接线（三条路由、biz VideoService 与持久化接缝）由第 22 节闭合（36 例，C082–C085）；doubao 视频变换器（Seedance 出站、client 入站、通道接线与 `/doubao/v3` 任务端点）由第 43 节闭合（C105），zenmux 视频变换器（原生 create/poll/delete、四类通道接线与 delete-unsupported 本地取消）由第 44 节闭合（87 例，C106；`zenmux_gemini` 随 Gemini/Vertex 切片） | 已关闭（doubao 视频 outbound 见第 43 节 / C105；zenmux 见第 44 节 / C106；`zenmux_gemini` 为 Gemini/Vertex 切片的既定范围） |
| C003：字段级 JSON 类型错误文案 | 已解决：入站请求按参照的字段/类型表逐 token 复刻 Go 解码语义，错误文案逐字节一致 | 已关闭（101 例锁定；残余见 C022） |
| C022：int32 index 上限与响应侧 null 元素 | 已解决（§140）：index 全部加宽为 long，`tool_calls[].index=3000000000` 被接受；响应转换对 Go 值结构切片的 null 元素补零值（choices / content parts / logprobs / image data / Jina / Cline / Anthropic citations / Responses output/sources），指针切片保持 null；`ParseOverrideOperations` 的 index 放宽到 int64 | 已关闭（§140，13 例）；残余 C224（普通 `List<string>` 的 null 元素保留 null） |
| 持久化测试依赖 Docker（Testcontainers 启动 `postgres:17-alpine`） | 无 Docker 的环境无法运行持久化测试 | CI 必须提供 Docker；随 M3 补 CI 配置 |
| C050–C058：Anthropic 双向流式、聚合器与出站 | C050 已解决：流式状态机与聚合器按 `inbound_stream.go`/`aggregator.go` 移植，4 组 xtest fixture 逐事件对比，`stream:true` 返回 200 `text/event-stream`；C051 已接受（raw JSON 载体，见登记）；C052 已接受：多条非法工具调用按 index 升序报告（参照 map 迭代随机）；C053 已接受：聚合器跳过负 index（参照 panic）；C054 已接受：无效工具输入在聚合早期抛 `InvalidOperationException`（参照在 `json.Marshal` 时失败，死 jsonrepair 省略）；C055 已接受：省略从未赋值的 `pendingReasoningItemID` 死字段；C056 真实缺口：Vertex AI 出站执行器（OAuth/JWT/区域 URL）未移植，构造期显式拒绝，Bedrock 仅保留直连报文形状；C057 已接受：缺失 `delta.type` 时跳过而非 panic；C058 已接受：流错误事件用严格 JSON 解析，畸形输入的兜底文案退化为 `stream_error` | C050 已关闭（测试锁定）；C056 为后续里程碑的显式缺口（不会发出错误请求）；C052–C055、C057、C058 仅影响畸形输入或内部错误文案 |
| C076：OpenAI Embeddings / Moderations | 已解决：按 `embedding.go`、`moderation.go`、`embedding_inbound.go`、`moderation_inbound.go` 移植统一模型、自定义 JSON 编解码、双向变换与两条路由，59 例变换器测试 + 5 例空响应探测 + 7 例端点接线锁定；校验顺序与文案、URL 构造（EndpointPath/raw URL）与默认审核模型逐字对齐 | 已关闭（测试锁定） |
| C077：OpenAI Alpha Search / Decisions | 已解决：按 `alpha_search_inbound.go`、`alpha_search_outbound.go` 与 `decisions/{inbound,outbound}.go` 移植双向变换与两条路由；原始 body 只改写顶层 model、其余字节原样保留；严格信封校验与 Go 解码文案逐字对齐；嵌套上限 10000 层。19 + 110 例变换器测试、2 例空响应探测与 6 例端点接线锁定 | 已关闭（测试锁定）；model 补丁的 HTML 转义字节差异见 C078 |
| C078：model 补丁的 HTML 字符转义 | 已接受：sjson 的快速路径对纯可打印 ASCII 字符串原样写入，端口用 Go `json.Marshal` 会把 <、>、& 写成 Unicode 转义序列；JSON 语义相同，仅字节表示不同，混合非 ASCII 时两侧一致（Go 探针确认） | 已接受（1 例锁定差异面；逐字节对齐需实现 sjson 的 mustMarshalString 判定） |
| C079：OpenAI Audio 切片的已接受差异 | 已接受（7 项，登记于台账）：(1) `GoCast.TryParseFloat` 完整复刻 strconv.ParseFloat 64（hex 负偏置、零尾数短路、下划线、范围错误），36 例保真表锁定；(2) Go omitempty 丢空字符串，端口以可空字符串 + 边界置 null 复刻线上形状；(3) multipart 急切读取：截断 body 文案与 Go `mime/multipart` 措辞不同，参照的逐 part 读取错误路径不可达，出站字段按插入序而非 Go map 随机序（参照本身不确定，逐字节对齐本就不可能）；(4) 流事件字段级类型错误用 System.Text.Json 文案（前缀保持 Go 风格）；(5) transcription 流事件 logprobs 在解码期压缩（线上不可观察）；(6) walker 的 FitsFloat64 改用 `GoCast.TryParseFloat`，`1e400` 等 ErrRange 字面量报类型错误；(7) .NET `Stream.Read` 无 Go 的 (0, nil) 分支，解码器的 0 读重试循环删除 | 已接受（102 例锁定音频切片；仅(2)为结构可见差异，其余为内部措辞或不可达路径） |
| C080：Jina Rerank/Embedding 切片的已接受差异 | 已接受（4 项，登记于台账）：(1) 出站 TransformResponse 对 nil Request 空安全回退 rerank 解码（Go 会 panic，参照 HttpExecutor 总会附带 Request，实际不可达）；(2) wire 模型缺省字符串（task / encoding_format / user）在统一模型中归一化为 null（Go 保留空串），线上输出一致（空 task 默认 text-matching、空 encoding_format / user 省略）；(3) 字段级解码错误用 System.Text.Json 文案（前缀保持 Go 风格，同 C064）；relevance_score / embedding 的 double 沿用 C067 的数字格式缺口；(4) 参照的 capability 表（Rerank→{jina/rerank}、Embedding→{…jina/embedding…}）在端口中未建模，按通道类型 + 入站 ApiFormat 分派（既有架构，video 切片同样继承） | 已接受（88 例锁定 jina 切片；(1)(2)为不可观察/语义等价差异，(3)(4)为既有登记项延续） |
| C081：OpenAI Video Llm 切片的已接受差异 | 已接受（2 项，登记于台账）：(1) 参照的「failed to read multipart field」/「failed to read multipart file」逐 part 读取错误折叠为 NextPart 级「failed to read multipart」（端口的 `GoMultipartReader` 急切缓冲整段 part，截断在 NextPart 暴露，语义等价）；(2) ParseGetVideoTaskResponse 将缺省的 model / prompt / size / video_url 在统一模型上归一化为 null（Go 内存中保留空串），序列化输出一致（Go omitempty 丢弃空串，延续 C079/C080 的可空字符串约定） | 已接受（57 例锁定 video Llm 切片；两项均为不可观察或语义等价差异） |
| C082–C085：OpenAI Video 端点接线的已接受差异 | 已接受（4 项，登记于台账）：(1) C082 参照的 `ch.Outbounds[videoKey]` 映射由 `ChannelOutboundFactory` 按通道类型重建：openai 通道承载 openai/video，doubao / volcengine 通道承载 Doubao 家族的 Seedance 出站（第 43 节，C105），四类 zenmux 通道承载 ZenMux 原生出站（第 44 节，C106），持久化的 seedance / zenmux 格式与对应通道回退一致；其余无视频变换器的通道仍报「channel does not support video task operations」，zenmux 的 delete-unsupported 本地取消分支已可达（7 例锁定），剩余差异只是「通道端点配置是否声明 zenmux/video」的端点表未建模——端口按请求 ApiFormat 直接分派（C080 item 4 同族）；(2) C083 `videoTaskProjectID` 的缺省 project 报错在端口不可达，无鉴权直调回退 `RequestIdentity.Default`（project 1）；(3) C084 同 project 下多条 external_id 命中时 ent Only 的 NotSingularError 与 Dapper QuerySingleOrDefault 的异常文案不同，均为 500 信封；(4) C085 `video.Video` 为 nil 时端口抛 `InvalidOperationException`（500 信封）而非 Go 的 nil 解引用 panic | 已接受（36 例锁定 video 端点切片；(1) 的 doubao 项由第 43 节的 11 + 5 例闭合，zenmux 项由第 44 节的 18 + 7 例闭合，残余为端点配置门控（C106）；其余为不可达或数据完整性边缘路径） |
| C087：TypeSafe SystemOne 切片的已接受差异 | 已接受（4 项，登记于台账）：(1) 参照的 `AllowPassThroughBody` 对 SystemOne 恒为 true，开启 pass-through_body 时重放客户端原始 body（只写回映射后的 model）；端口已按 C091 接入请求与响应/流半程（额外顶层字段保留，原始响应/流按开关替换，26 例锁定），提示词保护补丁由 C092 接入（71 例锁定）；(2) 参照按通道端点/能力表选择出站并拒绝不支持的请求类型，端口按通道类型分派（与 C080 item 4、C086 同源）；(3) `state` / `instructions` / `criteria` / `legend` / `probabilities` 以 `JsonElement` 承载，`GoAnyJson` 复刻键排序、重复键与数字字面量，仅内部构造时需提供 `JsonElement`；(4) `SystemOneAnswer` 的字段级解码错误为端口自述的 Go 风格文案（缺 `Alias.<field>` 路径，同 C064/C079 的 STJ 差异） | 已接受（56 例 Llm + 9 例端点锁定正常往返；pass-through 请求半程由 C091 的 95 例覆盖，通道可用性判定与非 JSON 输入文案仍为既定差异） |
| C091：pass-through 通道重放 | 请求与响应/流半程已解决：`isPassThroughEnabled` 的通道/全局开关继承、`passThroughStreamAligned` 的 Go 真值相等、body 重放与 sjson 语义的 model 补丁（空白 body 视作 `{}`）、格式支持（multipart / image_variation / transcription / translation 拒绝、JSON image_edit 允许）、Content-Type 同步、Codex Responses 头白名单、User-Agent 策略、`PassThroughApplied` 复位与 `request_executions.pass_through_applied` 落库（95 例）；响应/流半程按 `captureRawProviderResponse/Stream` 与 `applyPassThroughResponse/Stream` 移植，含双通道扇出（容量 64）、attach 前 backlog（1024 事件 / 8 MiB，含 Type/LastEventID 计入）、per-attempt 错误引用、取消后排干缓冲再关闭上游、`NextChannel`/`PrepareForRetry` 复位，并按参照顺序注册（26 例）；`pass_through_prompt_protection.go` 的补丁/校验由 C092 接入（71 例），codex 出站的 `AllowPassThroughBody` 由第 29 节的 codex 用例锁定（C094 item 10 登记 gjson 探测差异）；另有三项已接受差异：每请求设置快照替代参照的逐中间件惰性读取、RawRequest 缺失时保守跳过替代参照 panic、通道 settings 畸形 JSON 降级为继承（C037 家族） | 重放与参照逐分支一致（121 例锁定：请求 95 + 响应/流 26）；关闭开关时行为不变，开启时下游拿到原始响应/流；codex 策略已由 C094 锁定 |
| C092：pass-through 提示词保护 | 请求侧与管理面均已解决：`protectPrompts` 入站中间件（Messages / Decisions / Compact 三路规则应用、拒绝固定文案、legacy protector 的提示词快照回放校验）、五种 APIFormat 的原始 body 补丁（含 Gemini `functionResponse.response` 的 UseNumber 递归掩码）与 `PassThroughRequestBodyMiddleware` 的合并/校验回退按参照移植（71 例）；规则管理面（`promptProtectionRules` 连接、八个变更与预览；根 `node` 不含规则，参照 `guidTypeToNodeType` 未登记该类型）由 §151 的 18 + 14 例锁定（C231 登记 where 子集与枚举严格度）。残余为已接受差异：无 30 秒 live 缓存（每请求读 `prompt_protection_rules`，失败开放）、写入不触发缓存重载、.NET Regex 与 regexp2 尚未逐特性对照、Gemini/Ollama 无变换器导致载荷级对照延后 | 请求侧 71 例、管理面 32 例锁定；规则读取即时且失败开放不改变可观察行为，管理面已完成（规则 scopes 策略仍归 C014），Gemini/Ollama 变换器落地时补载荷对照 |
| C093–C094：OAuth 凭据与 Codex 通道 | C093 已接受：`expires_at` / `last_refresh` 用 `DateTimeOffset.TryParse` 宽松解析（参照为严格 RFC3339/RFC3339Nano）、JSON 解码错误文案来自 System.Text.Json、OnRefreshed 失败无日志接缝、单飞用锁 + TaskCompletionSource 替代 `x/sync/singleflight`、`ParseOAuthObject` 延迟解码（C013 延续）；C094 已接受 + 真实缺口：codex/fenno 分派、三种凭据形态、镜像主模型、transport 推断、pass-through 策略与传输收尾已锁定，真实缺口为端点表未建模（图片/alpha-search 按通道类型与入站格式分派）、OAuth `TokenProvider` 每次尝试新建（刷新单飞不跨请求共享）、刷新不写回数据库、无后台自动刷新、刷新复用共享 executor 而非按通道代理 client、`DecodeAuthJson` 的消费方未移植（API 层 codex 响应头转发已由 §142 接线：`X-Reasoning-Included` 与 `/responses` 的 `X-Codex-Turn-State`） | 已接受差异只影响畸形输入/日志/资源结构；真实缺口在管理面与通道生命周期切片补齐，期间 OAuth 凭据仍按需刷新，API key 通道不受影响 |
| C088：模型列表的 `models` 表与 API Key profile | 已解决：§89 接入 ListEnabledModels 的 configured-model 合并、QueryAllChannelModels 两分支、项目 / API Key profile（ChannelIDs / ChannelTags / ModelIDs）过滤、ModelBlacklistRegex、HideUnroutableModelsInList 抑制、OpenAI include 扩展字段（列表取系统默认、检索硬编码 false）以及 OpenAI / Anthropic / Gemini 的 403 信封；45 例 Api 锁定。残余运行时结构差异登记为 C178（实时读通道、无 outbound 构建跳过、通道派生稳定排序、畸形 profile JSON 失败关闭） | 已关闭（C178 残余） |
| C089：请求 ID 与 trace ID 中间件 | 已解决：`RequestTracingMiddleware` 的 `ar-<uuid>` 请求 ID、`AH-Request-Id` 响应头与错误体 `request_id` 回显（7 例）；`TraceMiddleware` 按参照实现请求头 → extra headers → OpenCode → Claude Code `metadata.user_id` → Codex 头 / turn metadata → body 字段的提取链、非消息端点跳过、`GetOrCreateTrace` 的 `(trace_id, project_id)` 语义与唯一约束重试、响应的 `AH-Trace-Id` 头与 `AH-Session-Id` 上下文；trace id 经 `RequestIdentity` 落到 `requests.trace_id`，并以 session id 传给通道出站（42 例 Api + 6 例 Persistence + 12 例 Llm） | 已关闭；残余差异见 C095 |
| C095：trace 子系统的残余差异 | 已解决：thread 中间件与 `threads` 表已按参照移植（`AH-Thread-Id` 首值、按项目 get-or-create、fail-open、`traces.thread_id` 落库，7 + 5 例），trace 粘性 API key 已接入（C013，7 + 1 例），claudecode / opencode 出站与 user id / 会话头生成由第 33 节关闭（C096），trace 查询 / 归档 / GraphQL 由 §101 闭合（23 + 15 例），GC / 保留策略由 §113 闭合（54 + 9 例）；已接受：无 slog 日志与 operation name、gjson 数字归一化（`1.50` → `1.5`、`1e3` → `1000`）与路径语法差异、约束错误按 SQLSTATE 23 分类（参照按 Ent 约束名）、`EnableBuffering` 回绕与参照 `io.ReadAll` + 新 reader 的资源差异 | 已关闭（C203 登记 GC 侧的调度 / cron / 计数实现差异） |
| C203：GC / 存储策略切片的实现差异 | 调度器结构（§149 起由 `GcGrain` 的 reminder 承载 `gc.cron`，替代共享 scheduler）、cron 为五字段 Vixie 子集（不支持库扩展）、channel_probes 清理已接线（§170：固定 3 天窗口、`timestamp < cutoff` 的 Unix 秒、id 升序、500 分批、失败隔离）、预览计数 32 位、数据存储进程缓存缺席、手写 Dapper SQL；清理结果、键序、错误包装与 GraphQL 线形由 67 例锁定 | 已接受；channel_probes 清理已由 §170 补齐，其余不改变清理结果 |
| C096：ClaudeCode / DeepSeek / OpenCode 出站与通道接线 | 已接受 + 真实缺口：claudecode 的 Bearer 认证、默认 base URL 与 `beta` 查询、13 个头与入站透传覆盖、系统消息注入、伪造 user_id、官方工具前缀与响应 / 流 / 聚合去前缀、思考开关，DeepSeek 的 URL 归一、completion 分派、`json_schema` 降级、`thinking` 与 `reasoning_content`，OpenCode 的模型路由、会话头优先级、响应 / 流分派与 `WithSessionHeader` 的能力保留，两条通道的 OAuth / API key 凭据构造与 `IsOfficial` 差异均已锁定（Llm 143 例、Api 11 例）；真实缺口：opencode 用户端点未包裹会话头（端点表，C094）、claudecode 多 key 通道的执行 key 审计列为空、OAuth provider 每次尝试新建（C094）（计费系统消息移除已由 §98 接线、`sessionHeaderCustomizedOutbound.Stop` 转发与编排调用已由 §141 接线）；已接受：会话值走 `RawRequest.SessionId`（C062）、流式行去前缀经 `JsonNode` 重序列化（键序保留、空白可能不同）、OAuth 凭据文案统一为 `failed to parse claudecode oauth credentials` | 主链路 154 例锁定；剩余缺口在端点表切片补齐；计费消息移除与 Stop 转发已接线 |
| C097：Ollama 原生出站与 NDJSON 流 | 已接受 + 真实缺口：OllamaChat 的 `/api/chat` 线格式、`stream` 恒显式、`options`、`images` 的 data-URL 剥离、工具与 `tool_calls` 的 Go map 排序往返、`thinking` → `reasoning_content`、finish reason 优先级、`ollama_error` 与聚合、NDJSON 解码与 Content-Type 路由、`ollama` / `ollama_anthropic` 接线（无启用 key 时不发送认证）、流式模型名分派均已锁定（Llm 52 例、Api 11 例）；真实缺口：apiKeyOverride（C013 残留，参照 5 个接线用例中的 1 例未移植）、能力/端点表未建模（C080/C086 同族）、modelname 的其余 format 分支（Anthropic `message_start` / Responses 生命周期 / Gemini `modelVersion`）、其余 provider 通道类型未接线；已接受：NDJSON 取消以 `OperationCanceledException` 进入 `Err`（参照返回 `ctx.Err`）、工具参数数字字面量保真（参照经 float64 重编码）、非对象 `arguments` 的宽容度与 STJ 解码文案、`TransformError(null)` 以 `null!` 承载非空接口 | 主链路由 63 例锁定；ollama_anthropic 的 Bearer 与 `/v1/messages` 归一、禁用 key 不发送与流式 `upstream_model_id` 均与参照一致；缺口在端点记录、通道 key 测试流程与后续 provider 切片补齐 |
| C098：Fireworks / Moonshot OpenAI 兼容出站与通道接线 | 已接受：Fireworks 的默认 `https://api.fireworks.ai/inference/v1`、`strings.TrimSuffix` 单斜杠裁剪、`ReasoningFieldContent` 与普通 OpenAI 出站，Moonshot 的内嵌 OpenAI 出站、chat-only 请求路径、messages 必填、`json_schema` → `json_object` 与 schema 清除、`NormalizeBaseURL(raw, "v1")` + `/chat/completions`、Bearer，以及 `fireworks` / `moonshot` 通道分支均由 19 例 Llm + 6 例 Api 锁定；已接受差异：Moonshot nil 配置由 Go nil 指针 panic 改为 `InvalidOperationException`、嵌套构造错误保留 .NET `ArgumentException` 的 `(Parameter 'config')` 后缀（测试只钉 Go 前缀）、`openai.ReasoningFieldNone` 注释与 Fireworks 实际使用的 `Content` 冲突时以代码为准；真实缺口：能力/端点表未建模（C080/C086 同族），但两个类型各只有一个 OpenAI chat 端点，按入站 ApiFormat 分派与参照一致 | 主链路由 25 例锁定；异常文案参数后缀与端点表缺口随全局错误文案策略与端点记录切片补齐 |
| C099：Longcat 强制数组内容出站与通道接线 | 已接受：内嵌 OpenAI 出站（`ReasoningFieldContent`）、调用方零值内容补空、委托后的二次编解码（字符串 / 空内容 → 单元素 text 数组、多分片原样、根 `messages` 与消息 `content` 末位键序）以及 `longcat` 通道分支均由 8 例 Llm + 2 例 Api 锁定；已接受差异：nil 配置由 Go nil 指针 panic 改为 `InvalidOperationException`、嵌套构造错误保留 .NET `ArgumentException` 的 `(Parameter 'config')` 后缀（测试只钉 Go 前缀）、请求体缺 `content` 的防御分支不可达（前置补空保证 OpenAI 出站总会写出 content）；真实缺口：能力/端点表未建模（C080/C086 同族），longcat 仅有 chat 端点，按入站 ApiFormat 分派与参照一致 | 主链路由 10 例锁定；异常文案参数后缀与端点表缺口随全局错误文案策略与端点记录切片补齐 |
| C100：xAI / Cline OpenAI 兼容出站与通道接线 | 已接受：xAI 的默认 `https://api.x.ai/v1`（空 URL 就地变更 `config.BaseURL`）、两层构造前缀、`ReasoningFieldContent`、Grok 字段裁剪与 `IsValidResponse` / `FilterStream`，Cline 的 `ReasoningFieldReasoning`、`X-Client-Type: cline-cli`、空内容归一与统一索引补丁、`{success,data,error,errors}` 信封与 502 错误映射、流内错误事件、`clineChunkTransform` 聚合，以及 `xai`（responses / compact 分派到 Responses 出站）与 `cline` 通道分支均由 27 + 24 例 Llm 与 8 例 Api 锁定；已接受差异：nil 配置由 Go nil 指针 panic 改为 `InvalidOperationException`、嵌套构造错误保留 .NET `ArgumentException` 的 `(Parameter 'config')` 后缀（测试只钉 Go 前缀）、空内容补丁由 JsonNode 重写 body（键序保留，空白与转义可能与 sjson 不同；参照的 `mergeSystemMessages` 索引错位与 sjson null 填充刻意复刻）、信封 / 错误解码用 System.Text.Json（Type / 重复键 / `any` 数字语义手工复刻，JsonException 文案不同）、`grok-4` 的 `ReasoningEffort` 用 null 代替空串（均被序列化省略）、`IsValidResponse` 公开为静态方法以便测试；真实缺口：能力 / 端点表未建模（C080/C086 同族），xAI 的相邻类型（xai_subscription / githubcopilot）与 Cline 默认模型表不在本切片 | 主链路由 59 例锁定；异常文案参数后缀与端点表缺口随全局错误文案策略与端点记录切片补齐 |
| C059–C073：Responses API 切片 | C059 已解决：图片请求/响应路径与图片流式聚合按 `image_request.go` / `image_response.go` 移植，26 例锁定，出站遇 `RequestType=image` 改走 `image_generation` 工具请求并在响应侧还原统一 `ImageResponse`；C069 已解决：客户端 OpenAI Images 入站/出站变换器按 `image_inbound.go` / `image_outbound.go` 移植，71 例锁定（JSON 与 multipart 入站、上限与错误文案、三端点出站构造、multipart 线路形状、响应还原与模型回显）；C074 已接受：base64 空白容忍、非 UTF-8 multipart 文本、重复 part 头与畸形 multipart 错误文案与 Go 不同；C075 已解决：`/v1/images/generations|edits` 按 routes.go:215-216 接线（共享 chat 处理器、multipart 透传、错误信封），并补齐 `hasResponseContent` 的图片分支，8 例端点测试锁定（参照停用 variations 路由）；C060 已接受：`stream_id` 用严格 `JsonDocument` 探测，畸形 body 的结果与 gjson 不同；C061 已解决：WebSocket 传输按 `websocket_executor.go` / `http_executor.go` 移植（执行器、连接池、续传对账、手写 RFC 6455 握手、Do 聚合、HTTP 包装），45 例锁定；C062 已接受：会话 ID 与作用域经 `SessionIdProvider` / `SessionScopeProvider` 接缝注入（trace 子系统见第 30/32 节与 C089）；C063 已解决：入站/出站流状态机与聚合器按参照实现，86 例锁定（fixture 回环、终止事件、错误分类）；C064 已接受：字段级解码文案为 System.Text.Json 文本；C065 已接受：strict `required` 补齐为文档序（参照 map 随机序）；C066 已接受：工具 schema 保留数字词法（参照经 float64 重编码）；C067 真实缺口：cost 之外的 double 仍按 STJ 数字格式输出；C068 已接受：并行工具收尾顺序由 Go map 随机序改为插入序，响应元数据经 JSON 字符串/对象往返；C070 已接受：手写握手不继承代理环境变量与 HTTP 执行器 TLS，拨号错误文案为近似；C071 已接受：WebSocket payload/事件以 `JsonElement` 保真往返，键序与数字词法与 Go 的 map+float64 重编码不同但语义一致；C072 已收窄：会话/作用域注入与 `Stop` 调用方由 §141 接线（生产 websocket 选择经 ws/wss base URL 推断、按尝试关闭连接池），端点记录传输与跨请求连接复用仍为缺口；C073 已接受：`toWebSocketURL` 用 System.Uri 归一化，丢弃 userinfo/fragment | 非流式 158 例 + 图片 26 例 + 流式 86 例 + WebSocket 45 例 + OpenAI Images 71 例锁定文本/工具/压缩/图片/流事件/WebSocket/Images 行为；OpenAI Images 端点已接线（C075 闭合），WebSocket 端点记录传输与跨请求连接复用仍缺（C072）；极端量级浮点与 WebSocket JSON 字节序不同但语义等价 | 后续里程碑补端点记录传输与通道级连接复用（C072）；C067 随全局 double 序列化工厂闭合 |
| C024–C027：鉴权子切片的已接受差异 | noauth key 指向已删除项目时参照触发唯一键冲突返回 500，本实现返回 401 且不改动该行（C024）；owner 选择在参照中无 `ORDER BY`，多 owner 时顺序不定（C025）；无 API key/项目缓存，每次请求读库（C026）；配置载体为 ASP.NET Core 配置而非 YAML 结构体（C027） | 已接受，测试锁定 401 行为（C024/C025），C026/C027 为性能与载体差异 |
| C028–C029：系统安全设置与 IP 黑名单 | C028 已解决：`systems` 表、`SecuritySettingsOrDefault` 的回退/归一化与 `/v1` 黑名单中间件按参照实现，403 信封逐字节一致；C029 已接受：系统设置无进程内缓存，每个 `/v1` 请求（有 ClientIP 时）读一次库，外部改库立即可见 | 已关闭；缓存随配置/缓存里程碑（C029） |
| C030–C033：重试策略与上游错误策略 | C030 已解决：`retry_policy` 的读取/归一化与每请求 topK 已接入（49 例 + 3 例），禁用时 1 个候选，策略计数大于默认值时能打满候选；C031 已解决：`upstream_error_policy` 的 hidden/custom 按参照应用到非流与流式错误（含"非流传输错误不被改写"的真实不对称），34 例锁定；C032 已接受：重复键后跟 `null` 时 .NET 与 Go 解码语义不同；C033 已接受：错误体解析用 System.Text.Json 而非 gjson，截断报文与重复键的结果不同 | C030/C031 已关闭；C032/C033 仅手改库或畸形上游报文可触发 |
| 其余 Ent 表未移植 | `data_storages` 由 §106、`invitations` 由 §110 落地；其余表依赖的功能不可用 | 随功能落地 |
| C179：orchestrator 模型关联路由切片的已接受差异与真实缺口 | 已接受 + 真实缺口：关联解析缓存省略（实时读通道，C149）；连接内模型按 request model 排序（C177），多模型重试顺序确定；read_channels 在查询前显式判定，类型与文案一致但不产生查询副作用；通道先后沿用 C140。真实缺口：trace 粘性（TraceStickyMode / PreviousChannelProvider）、项目与 API key 激活 profile 的 SelectedChannelsSelector / ChannelTagsFilterSelector、Google / Anthropic 原生工具选择器、StreamPolicySelector 与 QuotaRoutingSelector / 配额门未接入；59 例锁定 | 已接受 + 缺口（C179） |
| C180：四个未移植的通道变换选项与流策略强制 | 真实缺口：applyTransformOptions（ForceArrayInstructions / ForceArrayInputs / ReplaceDeveloperRoleWithSystem）、applyReasoningEffortMapping（含 output_config.effort 同步）、filterResponseCustomToolMessagesForNonResponsesOutbound、shouldForceStreamingForCandidate（stream=require 时强制流式与 include_usage）仅解析 / 暴露在通道设置与 GraphQL 中，出站变换未应用；默认配置下一致 | 真实缺口（C180） |
| C186：API key 配额时区按请求读取 | 已接受：参照 `SystemService.TimeLocation` 进程内缓存，端口每次配额检查读取 `system_general_settings`；行缺失 / 读取失败 / 畸形 JSON / 空值 / 未知时区回退 UTC 与 `Local` 映射一致，calendar 零点转换（含 DST 缺口与歧义）由测试锁定；差异仅在设置变更的生效时机与每次检查多一次主键查询 | 已接受（写入即时可见，无陈旧缓存） |
| C187：项目提示词按请求读取数据库 | 已接受：参照 `biz.PromptService` 把每个项目的启用提示词缓存为进程内 map（初始化、每分钟刷新、进程内 CRUD 后失效），端口 `IPromptProvider` / `PromptService` 每次请求经 Dapper 读取 `prompts` 表；同一时刻的行集合与注入行为一致，差异仅在写入后的生效时机（端口立即生效，参照最多延迟一分钟）与每请求多一次查询；多实例部署下端口可见性更强 | 已接受（§99 注入 63 例锁定；若后续引入缓存需保持单一数据源并复核快照时机） |
| C188：项目提示词管理面的 GraphQL 契约与已接受差异 | 已接受：§100 接入 `prompts` 连接（分页 / 排序 / where）与七个变更，写入校验 model_id / model_pattern / api_key 条件、重名 `DUPLICATE_NAME`、软删与 aggregate 更新语义均与参照一致；残余差异六项：`PromptWhereInput` 仅支持前端使用的谓词子集、缺失必填输入的 SDL 拒绝（gqlgen 零值继续）、未知枚举输出抛错（参照原样透出）、`order` 为 32 位 Int（参照 64 位）、写入不刷新缓存（端口无缓存，C187）、`Project.prompts` 反向边归 C165；37 例 Api（端点 10 + 服务 27）与 7 例 Persistence 锁定 | 已接受（六项均不影响前端实际查询 / 变更契约与运行时注入；超出子集的 where 需扩展 `PromptWhereInput` 与 `PromptStore.BuildFilter`） |
| C121 / C202：Antigravity OAuth 管理面 | 已解决：§111 接入 `POST /admin/antigravity/oauth/start` 与 `/exchange`（admin JWT + WithProjectID）、PKCE 状态缓存、令牌交换与 loadCodeAssist / onboardUser 项目发现，35 + 26 例锁定；残余差异登记为 C202（代理错误文案、连接池表达、ProxyFromEnvironment、ShouldBindJSON 尾随内容、System.Text.Json 解码文案、xcache 多后端见 C171） | 已关闭（C121 resolved / C202 accepted） |
| C197：备份存储后端、自动备份调度与保留清理 | 已解决（§108）：fs / S3 / GCS / WebDAV 四种后端按 `SaveData` / `SaveDataFromReader` / `LoadData` / `DeleteData` / `afero.ReadDir` 的可观察语义移植（键规范化、错误分层与包装顺序、缺失对象映射、列表只返回直接子项）；非 database 存储的自动备份改为临时文件 + 流式写入；`RetentionDays > 0` 的保留清理按前缀 / 后缀 / 修改时间执行且失败只告警（database 存储下同样执行并吞掉文件系统错误）；§149 起 `AutoBackupGrain` 以系统时区每日 02:00 触发且不补跑错过的运行，`updateAutoBackupSettings` 后经 `IAutoBackupRescheduleSignal` 重装调度。保留清理与调度由 8 + 5 例锁定，四种后端的键 / 错误 / 协议形状由 12 + 10 例锁定；残余实现差异登记为 C199 | 已关闭（database 存储下的行为与 §107 一致；非 database 存储由 §107 的确定性失败改为可用） |
| C199：存储后端与调度的实现差异 | 已接受：S3 用 AWSSDK.S3 单次 PutObject 而非参照的 16 MiB multipart（对象存储写入本身原子，字节与键一致）；GCS / WebDAV 为手写 HTTP 客户端（OAuth JWT-bearer / refresh_token、media upload、PROPFIND Depth 1），第三方库的错误文本为语义近似（如 googleapi 的 `Error <code>: <message>, <reason>`、gowebdav 的状态处理）；后端按操作创建并 Dispose 而非进程内缓存（配置即时生效，代价是每次操作一次 TLS 握手）；§149 起调度由 Orleans reminder 在到期时刻投递（冷启动 reminder 表刷新可带来数十秒首次延迟，参照也不保证毫秒级），DST 缺口 / 歧义按 Go `time.Date` 语义；S3 客户端未采用实时上游 `f272549f` 新增的 checksum 关闭配置，以冻结基线 `b8bb3421` 的默认行为为准 | 已接受（协议形状由 10 例脚本化 HttpMessageHandler 用例锁定；误差仅在库内部实现、错误措辞与首次投递的数十秒延迟，不改变对象内容、键、列表、保留与触发结果） |
| C198：备份 GraphQL 接入层 | 已接受：HotChocolate `AddUploadType` / `IFile` 对接 graphql-multipart-request-spec（gqlgen `graphql.Upload` + `io.ReadAll`）；存储的未声明 frequency 输出时报字段错误 `Enum "BackupFrequency" cannot represent value: "hourly"`（gqlgen 原样透出字符串）；`updateAutoBackupSettings` 的输入枚举在 HotChocolate 校验阶段被拒绝（gqlgen 接受任意字符串并落库）；`triggerAutoBackup` 以 `Task.Run` + 新建 DI scope + `CancellationToken.None` 实现（参照 goroutine + `context.WithoutCancel`）；GraphQL Int 32 位（C190）、restore 解码文案（C002 / C141）、取消语义（C155）沿用既有登记；14 例 Api 锁定 | 已接受（合法频率下查询 / 变更契约与参照一致；输入枚举更严格只拒绝参照本就不该接受的值） |
| C201：邀请与注册 REST 切片的边界差异 | 已接受：(1) 项目被软删时参照对 WithProject 的 nil 边解引用 panic，端口返回空项目名；(2) 时间以 RFC3339Nano 写出，端口为 100ns tick、Postgres 为微秒，仅在纳秒级有效位不同；(3) gin 忽略 JSON 尾随字节而 STJ 拒绝、roleID 显式 null 触发绑定失败（Go 零值语义）、maxUses 缺省为 0；(4) `%w` 包裹的内层文本来自 Npgsql 而非 ent 驱动，外层措辞与顺序一致；(5) 密码哈希在进入注册事务前完成（参照在事务内），回滚时无可观察差异；(6) 提交后的用户边重载经 `IUserProfileStore` 完成（C161/C164）。服务 33 + 端点 22 + 角色撤销 4 + 持久化 14 例锁定 | 已接受（合法输入下响应、状态码、错误文案与事务语义一致；差异仅发生在参照本身会 panic 的软删项目或畸形报文上） |
| C182：供应商配额采集核心与 Antigravity / Claude Code / Codex / xAI Subscription / GitHub Copilot / NanoGPT / ZenMux / Cline / Synthetic / NeuralWatt / Apertis / OpenCode Go / Kimi Code / MiniMax / ZhiPu / Zai / Charm Hyper / Command Code / Ollama checker 已接入（§114–§133），全部 19 个 provider checker 已注册 | 采集核心（`ProviderQuotaService`、`ProviderQuotaCheckerRegistry`、`provider_quota_status` 表、`ProviderQuotaInitializationHostedService` / `ProviderQuotaScheduler`、channel 失效钩子与 GraphQL 配额面）已落地并由 111 例锁定；Antigravity checker 由 §115 注册（`ProviderQuotaCheckerRegistration("antigravity", …)`），摘要优先 / 旧式回退、OAuth 刷新与两份解析器由 61 例锁定；Claude Code checker 由 §116 注册（`ProviderQuotaCheckerRegistration("claudecode", …)`），one-token Haiku 请求、unified ratelimit 头解析、状态 / 窗口归一化与 Go 数值解析语义由 80 例锁定；Codex 由 §117 注册（`ProviderQuotaCheckerRegistration("codex", …)`），wham/usage 两窗口与 rate-limit-reset-credits 重置清单由 83 例锁定；xAI Subscription 由 §118 注册（`ProviderQuotaCheckerRegistration("xai_subscription", …)`），weekly / monthly 两个 billing 报文合并由 46 例锁定；GitHub Copilot 由 §119 注册（`ProviderQuotaCheckerRegistration("github_copilot", …)`），copilot_internal/user 的 limited_user_quotas 与 quota_snapshots 两族窗口由 75 例锁定；NanoGPT 由 §120 注册（`ProviderQuotaCheckerRegistration("nanogpt", …)`），订阅用量端点三窗口与 state / graceUntil 由 105 例锁定；ZenMux 由 §121 注册（`ProviderQuotaCheckerRegistration("zenmux", …)`），管理订阅端点、5h / 7d 两窗口与账户状态升级（unknown 最差）由 153 例锁定（§134 补 HTTPS 降级拒绝）；Cline 由 §122 注册（`ProviderQuotaCheckerRegistration("cline", …)`），users/me、plans、balance 与 usage-limits / usages 账本采集、5h / 7d / 30d 三窗口、pass-unavailable 与 direct-only 分支由 100 例锁定；Synthetic 由 §123 注册（`ProviderQuotaCheckerRegistration("synthetic", …)`），GET 固定 /v2/quotas 端点读取 rolling five-hour 与 weekly token 两个实际限额（另有只展示、不参与闸门的小时级 search 计数），weekly 的 credits 与 plan 两种报文形状按字段合并由 209 例锁定；NeuralWatt 由 §124 注册（`ProviderQuotaCheckerRegistration("neuralwatt", …)`），GET 固定 /v1/quota 端点读取月度 kWh 订阅窗口（overage 或 remaining<=0 → exhausted、剩余低于 20% → warning、used/included 用于 usageRatio），`kwh_reset_date` 的月度周期起点按上月最后一天夹紧由 127 例锁定；Apertis 由 §125 注册（`ProviderQuotaCheckerRegistration("apertis", …)`），订阅周期 + PAYG + credits 三族额度由 187 例锁定；OpenCode Go 由 §126 注册（`ProviderQuotaCheckerRegistration("opencode_go", …)`），/zen/go/v1/usage 的 rolling / weekly / monthly 百分比窗口由 122 例锁定；Kimi Code 由 §127 注册（`ProviderQuotaCheckerRegistration("kimi_code", …)`），usage / limits / boosterWallet 采集由 218 例锁定；MiniMax 由 §128 注册（`ProviderQuotaCheckerRegistration("minimax", …)`），general 模型 5h / weekly 窗口由 161 例锁定；ZhiPu 由 §129 注册（`ProviderQuotaCheckerRegistration("zhipu", …)`），per-key TOKENS_LIMIT / CREDIT_LIMIT 与 zhipu_accounts 可用性组由 144 例锁定；Zai 由 §130 注册（`ProviderQuotaCheckerRegistration("zai", …)`），复用同一族采集器由 28 例锁定；Charm Hyper 由 §131 注册（`ProviderQuotaCheckerRegistration("charm_hyper", …)`），独立 /v1/credits 采集由 67 例锁定；Command Code 由 §132 注册（`ProviderQuotaCheckerRegistration("commandcode", …)`），alpha key / Studio cookie 双计费面、plan allowance 表与 Go map 解码由 116 例锁定；Ollama 由 §133 注册（`ProviderQuotaCheckerRegistration("ollama", …)`），API key 余额面与 Studio 会话 cookie 计量页的双面采集、HTML 抓取与 Go 匿名结构体解码由 125 例锁定（§134 补 HTTPS 降级拒绝）。未注册的通道类型仍走参照的 “No checker for provider” 分支并保留既有配额行（路由闸门照常消费缓存行） | 已关闭：全部 19 个 checker 已注册；仅已注册 checker 覆盖的通道会向供应商发出配额请求。残余差异见各 checker 条目（C219 / C220 / C221 等） |