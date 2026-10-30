# 验收账（第三本账）

验收是体检报告，不是"我感觉挺好"。

测量时间：2026-10-30（M2 Llm 切片 + M4 持久化层与请求链路落库 + M3 通道选择与 HTTP 切片，含超时路径）。

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
| `AxonHub.Llm.Tests` | 34 | 34 | 0 | 0 | pipeline 重试 15 + 空响应 9 + 聚合器 1 + HTTP 执行器/SSE 增量解码 9 |
| `AxonHub.Persistence.Tests` | 32 | 32 | 0 | 0 | schema 快照 5 + EF 模型一致性 2 + EF/Dapper 共享事务 2 + 连接语义 2 + 四表仓储 21 |
| `AxonHub.Grains.Tests` | 14 | 14 | 0 | 0 | 通道选择：打分/窗口 5 + 健康与 topK 4 + 别名解析 3 + 同分顺序/不计数 2 |
| `AxonHub.Api.Tests` | 28 | 28 | 0 | 0 | HTTP 切片：orchestrator/重试/超时 10 + 请求链路落库 4 + SSE 写帧 6 + 请求读取 4 + 端点胶水 4 |
| **合计** | **108** | **108** | **0** | **0** | |

命令：`dotnet test AxonHub.slnx`（2026-10-30，Windows 11 / .NET SDK 10.0.401）。

持久化测试通过 Testcontainers 启动 `postgres:17-alpine`（需要本机 Docker），每个测试使用独立 schema；
M2 时期的 SQLite 测试已全部替换为 PostgreSQL 对应项。
写侧 create/update 由 EF Core 10.0.14 执行，读侧 get/list 由 Dapper 2.1.89 执行；
仓储注入 scoped `AxonHubDbContext`，写入加入调用方事务，读侧经同一连接/事务执行（`EfDapperInteropTests` 锁定）。
EF 模型（而非 EF 迁移）由 `EfCoreModelTests` 与幂等 DDL 建出的库结构逐列/逐索引对齐，运行时不创建版本表。

Grain 与 API 测试用 Orleans `TestCluster` 在进程内起一个 silo，`IEnabledChannelProvider` 由假实现注入，
不依赖 Docker；grain 键按请求模型/通道 ID 隔离，用例之间无共享状态。API 测试的传输层用脚本化
`IExecutor` 替代，不发起真实上游请求；SSE 写帧与请求读取用 `DefaultHttpContext` + `MemoryStream` 逐字节断言。

跳过项说明：无。

## 公开 API 审计

- 参照实现导出数：未统计（M5 收口时执行）
- 本实现公开表面：`AxonHub.Llm`（统一模型、OpenAI 双向变换、SSE 解码、HTTP 执行器、pipeline）、
  `AxonHub.Persistence`（EF Core 写侧 DbContext 与模型、Dapper 读侧仓储、幂等迁移与连接工厂）、
  `AxonHub.Grains` / `AxonHub.Grains.Abstractions`（通道池与通道指标 grain）、
  `AxonHub.Api`（HTTP 宿主、`POST /v1/chat/completions`、请求读取、SSE 写出、orchestrator 适配）
- 签名差异 / 运行时差异 / 平台限制：见 `02-differences.md`（C001–C019）

## 协议锁定

- [x] 关键协议字段名与顺序由测试锁定（`corpus/upstream-request`、`corpus/unified-request`、
  `corpus/client-response`、`corpus/client-stream`；见 `01-baseline.md` 第 2、3 节）
- [x] SSE 写出帧与客户端错误事件由 `SseStreamWriterTests` 逐字节锁定（gin-contrib/sse 兼容）
- [ ] 跨语言数字范围已校验（±2^53−1）：token 计数以 int64 落库并由
  `UsageLogStoreTests.TokenCountsRoundTripAsInt64` 锁定；LLM JSON 数字路径尚未专项审计
- [ ] 编码与行尾归一化策略已确定：`diff_corpus` 自动归一化行尾/BOM；非法 UTF-8 行为仍是缺口（C008）

## 端到端

- [x] 进程内 HTTP 切片：`POST /v1/chat/completions` 的请求读取、候选选择、重试、SSE 写出由 28 例覆盖，
  其中 2 例锁定首包超时与非流式超时，4 例锁定请求/执行/用量落库的字段与时机（不启动真实 Kestrel 与 PostgreSQL）
- [ ] 真实宿主启动（Kestrel + PostgreSQL + Orleans silo） — 未测试
- [ ] 容器内 — 未开始
- [ ] 跨平台（Windows / Linux / macOS） — 目前仅在 Windows 11 验证
- [ ] 跨传输（stdio / HTTP / 进程内） — 仅进程内 HTTP 切片

## 未闭合风险

| 风险 | 影响 | 处置 |
|---|---|---|
| HTTP 端点与 Orleans 调用链已接通，但真实宿主启动（Kestrel + PostgreSQL 迁移 + silo）未测试 | 数据库或配置错误可能到部署时才暴露 | 容器化验收时补启动冒烟 |
| C012：默认 `adaptive` 负载均衡策略链未移植，仅 round-robin 排序 | 默认配置下通道顺序可能与参照不同 | 补齐策略链后按参照用例扩展 |
| C013：多 API key 通道只用首个 key，无 trace/thread 粘性 | 多 key 通道流量集中，限流/配额分布与参照不同 | 接入 trace/thread 后补 TraceStickyKeyProvider |
| C014：鉴权、限流、成本与通道级请求改写未接入 | 端点对任意客户端开放，usage 已落库但成本为空、通道级改写不生效 | 后续里程碑按参照逐项接入并补测 |
| C015：zstd 请求体未解压，压缩错误文案不同 | zstd 客户端请求失败；压缩错误文本与参照不一致 | 引入 zstd 依赖并映射错误文案 |
| C016：通道级 `retryableStatusCodes` / `retryableErrorPatterns` 未生效 | 配置了额外重试规则的通道少重试 | 接入通道 settings 时补齐 |
| C017：通道性能记录仅成功/失败计数，取消与无终态流语义不同 | 取消不影响健康分；省略终态且靠聚合判定的流不计成功 | 需要性能明细/自动禁用时补齐 |
| C018：同步 recorder 每次调用一个短生命周期工作单元 | 一次请求链路最多 5 次短事务，request 与 execution 不同事务提交 | 对当前数据路径等价；引入批量/事务需求时重估 |
| C019：流式响应的 external id / body / chunks 与流式 request 响应头未回填 | 流式请求在控制台可见状态与用量，但归档字段为空 | 接入 raw-stream 分块聚合后补齐 |
| C008：非法 UTF-8 请求体 | 与 Go 的 U+FFFD 替换行为不同 | HTTP 切片已落地，M3 收尾前决策 |
| C010：非 chat 响应类型的空响应探测 | 14 个参照用例顺延 | 随对应端点落地 |
| C003：字段级 JSON 类型错误文案 | 错误文案与参照不同 | M3 收尾前评估 |
| 持久化测试依赖 Docker（Testcontainers 启动 `postgres:17-alpine`） | 无 Docker 的环境无法运行持久化测试 | CI 必须提供 Docker；随 M3 补 CI 配置 |
| 其余 Ent 表未移植（projects / api_keys / data_storages / traces / users 等） | 依赖这些表的功能不可用 | 随功能落地 |
