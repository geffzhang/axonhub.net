# 参照物锁定

- 项目：**axonhub.net**
- 方向：`Go 1.26` → `C# 14 / .NET 10 + Orleans 10.x`
- 建立日期：2026-10-30

## 参照实现

| 项 | 值 |
|---|---|
| 上游仓库 | https://github.com/looplj/axonhub （本地副本 `e:\GitHub\axonhub`） |
| 锁定版本 | `b8bb3421e1fe946cfc4f0d86e8412098738f2793` = tag **v1.0.0-beta11**（2026-10-08） |
| 冻结副本 | git worktree：`E:\GitHub\axonhub-ref`（detached，干净） |
| 锁定方式 | `python -I .../reference_lock.py lock --repo E:\GitHub\axonhub-ref --dest porting/reference-lock.json` |
| 指纹 | tree `20b085a4985c456bf89e2bd86e23184c3f2cf839`，2705 个受版本控制文件 |
| 上游规模 | 1657 个 `.go` 文件 / 约 63.7 万行 / 740 个 `_test.go` |

> 为什么用 worktree：`reference_lock.py` 锁的是仓库 HEAD，而基线是 `unstable` 分支的祖先提交。
> 冻结副本既作为锁定对象，也作为差分语料录制时的可运行参照实现。

## 同步策略

- [x] 长期跟随上游，每个切片收口时核对一次漂移
- [ ] 一次性移植，完成后不再同步上游

每个切片验收前执行：

```powershell
python -I C:\Users\geffz\.claude\skills\cross-lang-porting\scripts\reference_lock.py check `
  --repo E:\GitHub\axonhub-ref --dest E:\GitHub\axonhub.net\porting\reference-lock.json
```

## 纪律

> **每次动工前先跑 `reference_lock.py check`。漂移了先决策，再动手。**

漂移后必须完成三件事：
1. 明确本次跟进到哪个 commit
2. 列出受影响的语义范围
3. 逐项回审受影响的实现，统一混用之处

升级基线时：先把 `E:\GitHub\axonhub-ref` 切到新 commit 并重新 `lock`，
再为新旧版本各录一份差分语料，语料差异逐条进入差异台账。

## 目标框架

| 项 | 值 |
|---|---|
| 运行时 | .NET 10（本机 SDK 10.0.401） |
| Actor 框架 | Orleans 10.x，**NuGet 发布包**（不引用 `E:\GitHub\orleans` 源码） |
| 本地框架参考 | `E:\GitHub\orleans`（main，v10.4.0-101），仅作 API/设计阅读，不参与构建 |
| 持久化（CQRS） | 写侧/事务：EF Core 10.0.14 + Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3；读侧/热路径：Dapper 2.1.89；两者共用 Npgsql 10.0.3（PostgreSQL，本机验证版本 17）。全部为 **NuGet 发布包**，不引用源码 |
| 本地持久化参考 | `E:\GitHub\Dapper`（HEAD `eb47546`，tag `2.1.89`），仅作 API/语义阅读，不参与构建 |
| 测试基础设施 | Testcontainers.PostgreSql 4.16.0，测试时启动 `postgres:17-alpine` 容器，每个测试使用独立 schema |

## 漂移记录

| 日期 | 从 | 到 | 影响范围 | 处置 |
|---|---|---|---|---|
| 2026-10-30 | — | `b8bb3421`（v1.0.0-beta11） | 初始基线 | 锁定 |
| 2026-11-05 | `b8bb3421` | `2851a192`（仅 analytics 切片） | analytics SDL 与解析器 / 助手：`startTime` / `endTime` 由日期字符串改为 `Time`（RFC3339），`AnalyticsMetadata.earliestDate` 改为 `earliestTime`，日统计改用 `startOfDay` 语义（ba84cab8 的 tok/s 与 TTFT 指标同属该切片） | 局部跟进：端口前端已随上游 HEAD 拷贝，按 2851a192 实现；其余子系统仍以 `b8bb3421` 为基线，差异登记为 C194 |
| 2026-11-05 | `b8bb3421` | `f272549f`（仅 dashboard 切片） | 新增 `internal/server/gql/dashboard_cache.go`（进程内缓存、按 key 单飞、2 分钟 detached 计算）；`dashboard.resolvers.go` / `dashboard_helpers.go` / `dashboard_usage_aggregates.go` 的解析器语义随 532a8032（perf(dashboard): reduce aggregate load，#2755）与 dc68318c（#2780）改变：聚合结果缓存、fastest 排名的 PostgreSQL DISTINCT ON、`channel_probes` 分支。`dashboard.graphql` 与基线逐字节相同（`cmp` 验证） | 局部跟进：§105 的 21 个根字段按 f272549f 实现；其余子系统仍以 `b8bb3421` 为基线，差异登记为 C195 |
| 2026-11-06 | `b8bb3421` | `f272549f`（仅 data storage 切片） | API-key 查询规则的作用域由 `write_requests` 改为 `read_data_storages`（e5c904ef，fix(api-keys): enforce scope grants for service account keys）；`newS3ClientWithHTTPClient` 的 checksum 选项（存储后端，未移植）。DataStorage 的 SDL、`objects.DataStorageSettings` 与服务语义（Create / Update / mergeSettings）与基线相同 | 局部跟进：§106 按 f272549f 实现；其余子系统仍以 `b8bb3421` 为基线，差异登记为 C196 |
| 2026-11-08 | `b8bb3421` | `f272549f`（仅端点 / Responses 协议切片） | 基线后的 `94cd05b1`（#2656 feat(responses): add HTTP and WebSocket protocol endpoints）新增 `openai/responses-ws` 与 `internal/server/biz/channel_endpoint_responses.go`（`NormalizeEndpoints` / `Channel.DefaultEndpoints` / `legacyResponsesFormat` / `normalizeModelProtocols` / `isResponsesEndpoint`），并改动 `channel_endpoint.go:455-487`（`ResolveEndpoints` 接收 BaseURL 后合并归一化端点）、`channel_llm.go:217-620`（默认端点表与端点级 outbound：BaseURL/Path/Transport、codex alpha 路径）、`internal/server/orchestrator/tester.go:130-221`（`channelTestAPIFormat` 顺序与 `usesResponsesWebSocket`）、`llm/transformer/openai/responses/outbound.go:168-173`、`llm/transformer/openai/codex/outbound.go:154-159`（transport 为 websocket 时 `APIFormat()` 返回 w）、`llm/transformer/orchestrator/outbound.go:838-840`（`isResponsesFormat` 含 h/w/compact）。GraphQL 的 `defaultEndpoints` 仍由 `channelResolver.DefaultEndpoints` → `Channel.DefaultEndpoints()`（axonhub.resolvers.go:34-41）提供 | 局部跟进：§143 的运行时读取侧、模型选择、tester、端点检测、出站工厂与 `defaultEndpoints` 按 f272549f 实现；写入面（createChannel / updateChannel / saveChannelEndpoints 的校验顺序与端点规范化回写）仍以 `b8bb3421` 为基线（C144/C148）；其余端点/供应商家族与通道级连接复用仍为 C094/C072 的残余 |
| 2026-11-09 | `b8bb3421` | `f272549f`（仅 OAuth 令牌刷新切片） | 基线后的 `channel_llm.go:1638-1741` 新增 `refreshOAuthTokenWithClient` 与 `channelCredentialsEqual`：刷新写回由无条件 `UpdateOneID(...).SetCredentials(...).Save` 改为 `Where(channel.UpdatedAt(...))` 的乐观条件写，0 行时重载并按类型 + 凭据相等决定「重试 / 冲突胜利者 / 非 OAuth 退休」；`llm/oauth/token_provider.go` 相应新增 `CredentialConflictError`、`refreshResult{credentials, retryExpired}` 与胜利者再刷新循环（仍过期且带 refresh token 时二次刷新） | 局部跟进：§146 的 `ChannelTokenGrain` / `ChannelTokenSource` / `ChannelTokenStore` 按 f272549f 实现；其余子系统仍以 `b8bb3421` 为基线；基线版的无条件 `refreshOAuthToken` 语义不再保留，差异登记为 C227 |
| 2026-11-09 | `b8bb3421` | `f272549f`（仅 Claude Code safeguards 设置切片） | 基线后新增整套 Claude Code safeguards：`internal/server/safeguards`（evaluator / response，评审 dangerous_tool_use）、`internal/server/biz/system_claude_code.go`（`claude_code_safeguards` 系统设置）、`conf/conf.go:257-264` 的 `server.safeguards` 默认值、`internal/server/gql/system.graphql:542-573` 的 `ClaudeCodeSafeguardsConfig` / `UpdateClaudeCodeSafeguardsInput` / `ClaudeCodeSafeguardsSettings` 与三个根字段、`system.resolvers.go:445-455,804-807`，以及 `internal/server/api/anthropic.go` 的 reviewer 接线（基线版无这些文件与 schema） | 局部跟进：§159 按 f272549f 实现设置面（读取 / 校验 / 持久化 / GraphQL）；评审运行时未移植，登记为 C243；其余子系统仍以 `b8bb3421` 为基线 |
