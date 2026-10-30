# 验收账（第三本账）

验收是体检报告，不是"我感觉挺好"。

测量时间：2026-10-30（M2 Llm 切片 + M4 持久化层与请求链路落库 + M3 通道选择与 HTTP 切片，含超时、非法 UTF-8 与字段级类型错误路径；C014 鉴权子切片新增 API key 中间件、ClientIP/allowlist 与 `projects` / `users` / `api_keys` 三表；C028 新增 `systems` 表、系统安全设置与 `/v1` IP 黑名单；C030 新增 `retry_policy` 读取、归一化与每请求 topK；C031 新增 `upstream_error_policy` 的应用与流式错误改写；C012 新增四条负载均衡策略链，残余 C034–C036；通道级请求改写新增 body/header 覆盖、Go 模板子集与设置解析，残余 C037–C041）。

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
| `AxonHub.Llm.Tests` | 145 | 145 | 0 | 0 | pipeline 重试 15 + 空响应 9 + 聚合器 1 + HTTP 执行器/SSE 增量解码 9 + Go JSON UTF-8 修复与语法文案 10 + 字段级类型错误文案 101 |
| `AxonHub.Persistence.Tests` | 50 | 50 | 0 | 0 | schema 快照 5 + EF 模型一致性 2 + EF/Dapper 共享事务 2 + 连接语义 2 + 八表仓储 39（projects / users / api_keys 各 4 例，systems 4 例） |
| `AxonHub.Grains.Tests` | 24 | 24 | 0 | 0 | 通道选择：打分/窗口 5 + 健康与 topK 6（含 topK 截断与归一化 2）+ 别名解析 3 + 同分顺序/不计数 2 + 负载均衡策略 8（adaptive 中性总分/线性衰减 2、冷却边界 1、权重 tie-break 与未知回退 2、failover 1、circuit-breaker 1、策略切换 1） |
| `AxonHub.Api.Tests` | 393 | 392 | 0 | 1 | HTTP 切片 79：orchestrator/重试/超时 17（含通道级重试配置 3、流式性能记录语义 2、策略 topK 1、负载均衡策略接线 1）+ 请求链路落库 5（含鉴权身份传播 1）+ SSE 写帧 9（含上游错误策略 2）+ 请求读取 12（含 zstd/压缩错误文案）+ 端点胶水 11（含上游错误策略 3）+ 期望流选择 2 + 通道重试谓词与 settings 解析 16 + 通道性能记录 7；API key 鉴权与 ClientIP 73：header 提取 14 + 鉴权器流程 13 + ClientIP/allowlist 与中间件文案 46；系统设置与 IP 黑名单 29：SecuritySettings 回退/归一化 15 + 黑名单放行/命中/受信代理 14；重试策略系统设置 49：回退/零值/类型错误 16 + 解析与重复键 7 + 归一化 26；上游错误策略 applier 29：非流改写/本地错误/quota 11 + 响应体字段 8 + 流式分类与策略读取次数 10 + 通道级请求改写 134：body/header 改写与设置解析 133（ChannelOverrideTests 86 含 1 Skip + ChannelOverrideSettingsTests 47）+ 端到端落库 1 |
| **合计** | **612** | **611** | **0** | **1** | |

命令：`dotnet test AxonHub.slnx`（2026-10-30，Windows 11 / .NET SDK 10.0.401）。

持久化测试通过 Testcontainers 启动 `postgres:17-alpine`（需要本机 Docker），每个测试使用独立 schema；
M2 时期的 SQLite 测试已全部替换为 PostgreSQL 对应项。
写侧 create/update 由 EF Core 10.0.14 执行，读侧 get/list 由 Dapper 2.1.89 执行；
仓储注入 scoped `AxonHubDbContext`，写入加入调用方事务，读侧经同一连接/事务执行（`EfDapperInteropTests` 锁定）。
EF 模型（而非 EF 迁移）由 `EfCoreModelTests` 与幂等 DDL 建出的库结构逐列/逐索引对齐，运行时不创建版本表。

Grain 与 API 测试用 Orleans `TestCluster` 在进程内起一个 silo，`IEnabledChannelProvider` 由假实现注入，
不依赖 Docker；grain 键按请求模型/通道 ID 隔离，用例之间无共享状态。API 测试的传输层用脚本化
`IExecutor` 替代，不发起真实上游请求；SSE 写帧与请求读取用 `DefaultHttpContext` + `MemoryStream` 逐字节断言。

跳过项说明：1 例。`PromptCacheKeyFallsBackToTheCompactRequest` 记录参照中 `.PromptCacheKey` 回退
`Compact.PromptCacheKey` 的行为，端口 `LlmRequest` 无 Compact 子请求（C038），待移植 CompactRequest 后启用。

## 公开 API 审计

- 参照实现导出数：未统计（M5 收口时执行）
- 本实现公开表面：`AxonHub.Llm`（统一模型、OpenAI 双向变换、SSE 解码、HTTP 执行器、pipeline）、
  `AxonHub.Persistence`（EF Core 写侧 DbContext 与模型、Dapper 读侧仓储、幂等迁移与连接工厂）、
  `AxonHub.Grains` / `AxonHub.Grains.Abstractions`（通道池与通道指标 grain）、
  `AxonHub.Api`（HTTP 宿主、`POST /v1/chat/completions`、请求读取、SSE 写出、orchestrator 适配、
  API key 鉴权中间件与 header 提取、gin 兼容的 ClientIP 与 allowlist 匹配、`projects` / `users` / `api_keys` / `systems` 仓储、
  系统安全设置读取与 `/v1` IP 黑名单中间件）
- 签名差异 / 运行时差异 / 平台限制：见 `02-differences.md`（C001–C041）

## 协议锁定

- [x] 关键协议字段名与顺序由测试锁定（`corpus/upstream-request`、`corpus/unified-request`、
  `corpus/client-response`、`corpus/client-stream`；见 `01-baseline.md` 第 2、3 节）
- [x] SSE 写出帧与客户端错误事件由 `SseStreamWriterTests` 逐字节锁定（gin-contrib/sse 兼容）
- [x] 通道级请求改写的应用顺序与落库时机由 134 例锁定（body/header 中间件、Go 模板子集、
  数组与头操作、非 JSON 门槛、设置解析；残余差异 C037–C041 已登记）
- [x] 入站请求的字段级解码错误文案与 Go 一致（C003）：结构名、标签路径、Go 类型、数字字面量、
  硬错误覆盖软错误、null 零值语义由 101 例锁定；期望值全部来自对参照模块的 Go 探针输出
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
  其中 1 例端到端断言出站请求与 execution 落库都记录改写后的 body/header
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
| C012：负载均衡策略链 | 已解决：adaptive / failover / circuit-breaker / round-robin 四条链按参照公式、权重 tie-breaker、健康分区与 topK 实现；策略取自 `retry_policy.load_balancer_strategy`，未知值回退 adaptive；24 个 grain 用例 + 1 个 orchestrator 接线用例锁定 | 已关闭；残余 C034（无延迟样本，LatencyAware 恒 40）、C035（限流未接入，RateLimitAware 恒 100）、C036（无模型级熔断状态，按关闭态计分） |
| C013：多 API key 通道只用首个 key，无 trace/thread 粘性 | 多 key 通道流量集中，限流/配额分布与参照不同；`trace_sticky_mode` 已解析并归一化，但无 trace/thread 可粘 | 接入 trace/thread 后补 TraceStickyKeyProvider |
| C014：限流/配额、成本计费、用户鉴权（JWT/OIDC/角色）与通道管理面未接入 | API key 鉴权与身份传播已接入（C023），系统安全设置与 IP 黑名单已接入（C028）：`/v1` 先过黑名单再过鉴权，request/execution/usage 的 project/api_key 已回填；通道级请求改写已接入（body/header 覆盖、模板渲染、sjson 路径语义，134 例锁定，残余 C037–C041）；但 usage 成本仍为空、无限流/配额、无用户登录与管理面权限，通道写入校验与覆盖模板 CRUD 未接入（C037） | 后续里程碑按参照逐项接入并补测 |
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
| C024–C027：鉴权子切片的已接受差异 | noauth key 指向已删除项目时参照触发唯一键冲突返回 500，本实现返回 401 且不改动该行（C024）；owner 选择在参照中无 `ORDER BY`，多 owner 时顺序不定（C025）；无 API key/项目缓存，每次请求读库（C026）；配置载体为 ASP.NET Core 配置而非 YAML 结构体（C027） | 已接受，测试锁定 401 行为（C024/C025），C026/C027 为性能与载体差异 |
| C028–C029：系统安全设置与 IP 黑名单 | C028 已解决：`systems` 表、`SecuritySettingsOrDefault` 的回退/归一化与 `/v1` 黑名单中间件按参照实现，403 信封逐字节一致；C029 已接受：系统设置无进程内缓存，每个 `/v1` 请求（有 ClientIP 时）读一次库，外部改库立即可见 | 已关闭；缓存随配置/缓存里程碑（C029） |
| C030–C033：重试策略与上游错误策略 | C030 已解决：`retry_policy` 的读取/归一化与每请求 topK 已接入（49 例 + 3 例），禁用时 1 个候选，策略计数大于默认值时能打满候选；C031 已解决：`upstream_error_policy` 的 hidden/custom 按参照应用到非流与流式错误（含"非流传输错误不被改写"的真实不对称），34 例锁定；C032 已接受：重复键后跟 `null` 时 .NET 与 Go 解码语义不同；C033 已接受：错误体解析用 System.Text.Json 而非 gjson，截断报文与重复键的结果不同 | C030/C031 已关闭；C032/C033 仅手改库或畸形上游报文可触发 |
| 其余 Ent 表未移植（data_storages / traces / prompts / roles 等） | 依赖这些表的功能不可用 | 随功能落地 |
