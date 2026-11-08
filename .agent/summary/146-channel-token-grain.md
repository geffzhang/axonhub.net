# §146 按通道 OAuth 令牌刷新迁入 ChannelTokenGrain（A3）

日期：2026-11-09
参照：`llm/oauth/token_provider.go`、`internal/server/biz/channel_llm.go:1638-1741`、
`internal/server/biz/channel_internal.go:66-76`、`channel_llm.go:331-425 / 924-989 / 867-884`。
写回语义按上游 `f272549f`（局部跟进，`00-reference.md` 已记录漂移）；冻结基线 `b8bb3421`
的 `refreshOAuthToken` 只有无条件 `Save`，没有乐观条件写与冲突分支。

## 背景

A2 之后通道对象仍按每次尝试重建：每次尝试都新建一个 OAuth `TokenProvider`，刷新缓存、
singleflight、后台预刷新与冲突重试全部随请求丢弃；参照则为每个启用通道保留一个 provider。
A3 把这份按通道状态收进以 channel id 为键的 `ChannelTokenGrain`，刷新写回与后台预刷新
都变成集群内单写入口。

## 交付

- 新增 `ChannelOAuthToken` / `ChannelTokenSeed` / `ChannelTokenRefreshRequest` /
  `ChannelTokenRefreshResult` / `IChannelTokenSource` / `IChannelTokenGrain`
  （AxonHub.Grains.Abstractions）：Orleans 序列化孪生。seed 携带凭据、原始 credentials 文档
  与行 `updated_at`；grains 模块不引用 Llm / Persistence，配方与持久化经接缝注入。
- 新增 `ChannelTokenGrain`（integer key = channel id）：
  - `GetAccessTokenAsync(seed)` 仅在 seed 的 `updated_at` 更新时采纳快照，之后复刻 `Get`
    （3 分钟提前过期；过期才刷新）；
  - `EnsureFreshAsync()` 复刻 `EnsureFresh(0)`（refresh token 为空跳过；5 分钟窗口）；
  - `OnActivateAsync` 注册 1 分钟周期 timer，窗口内执行 `EnsureFresh`，失败记 warning 并
    保留快照；grain turn 串行化替代参照的 singleflight；
  - 冲突胜利行非 OAuth 时退休（后续读取抛 `credentials is nil`），更新的 seed 可复活。
- 新增 `ChannelTokenSource`（Api）：按 codex / claudecode / xai_subscription 构造一次性
  provider；`OnRefreshed` 复刻 `refreshOAuthTokenWithClient` 的乐观重试循环
  （`updated_at` 条件写 → 0 行重读 → 仅元数据变更重试同一 payload → 冲突胜利者 /
  非 OAuth 退休）；`ChannelCredentialsComparison` 复刻 `channelCredentialsEqual`
  （`ExpiresAt` 按时刻比较、scopes nil==empty、刻意跳过 `vertexApiKeys`）。
- 新增 `ChannelTokenStore`（Persistence）：Dapper 读取 + EF `ExecuteUpdateAsync` 条件写
  （`id + deleted_at=0 + updated_at`），0 行重读当前行；软删行不可见 / 不可写。
- 新增 `GrainTokenGetter`（Api）：把转换器的同步 `ITokenGetter.Get` 桥到 grain
  （`GetAwaiter().GetResult()`）。
- 接线：`ChannelOutboundFactory.Create` 新增可选 `IGrainFactory`，`ChatCompletionOrchestrator`
  传入；codex（fenno 除外）/ claudecode / xai_subscription 的 OAuth 通道走 grain，
  API key 通道仍走 `ApiKeyTokenProvider`；端点探测 / 通道测试等直接调用者不传工厂，保留
  本地 provider。`Program.cs` 注册 `IChannelTokenStore` 与 `IChannelTokenSource`。
- 测试基建：两个 `GrainClusterFixture` 注入假 `IChannelTokenSource` 并注册
  `TimeProvider.System`。

## 行为要点

- 主链等价：同一通道至多一次并发刷新、3 分钟斜移、5 分钟窗口、轮换凭据写回（apiKey 为
  刷新后的 OAuth JSON、oauth 为类型化对象，其余字段原样保留）、仅元数据变更不丢弃轮换令牌、
  冲突胜利者采纳 / 非 OAuth 退休、持久化失败保留内存凭据并沿用旧快照重试。
- 延迟差异：懒激活（首个请求建立快照）；1 分钟 timer 粒度（参照精确睡到
  `ExpiresAt - 5min`，最多晚约 1 分钟）；grain 接口无 CancellationToken，请求取消不能中止
  已排队 / 执行中的刷新；重读失败的日志文案与参照不同（仅日志可见）。
- 未迁移：fenno（令牌配方不同）、github_copilot、antigravity（`refreshToken|projectID`）、
  ModelFetcher 目录刷新、端点探测 / 通道测试（C094 / C158 保留缺口）。
- 差异登记为 C227（accepted）；C094 的 (2)(3)(4) 对三类通道关闭；C127 / C158 / C226 补充说明。

## 测试

- `tests/AxonHub.Llm.Tests/OAuthTokenProviderConflictTests.cs`（5）：冲突胜利者采纳、过期
  胜利者二次刷新、空胜利者退休、非冲突失败保留凭据、无 refresh token 的胜利者不重试。
- `tests/AxonHub.Grains.Tests/ChannelTokenGrainTests.cs`（8）：冷种子不刷新、过期刷新并采纳、
  较新快照覆盖 / 旧快照忽略（含 Unspecified 归一）、退休后抛错直到更新快照、无 refresh token
  不刷新、窗口外不刷新、窗口内刷新、失败保留快照。
- `tests/AxonHub.Api.Tests/ChannelTokenSourceTests.cs`（6）：轮换凭据写回并保留无关字段、
  仅元数据变更重试、并发凭据变更返回胜利者、非 OAuth 行退休、持久化失败保留内存凭据、
  无 refresh token 不刷新。
- `tests/AxonHub.Persistence.Tests/ChannelTokenStoreTests.cs`（4）：读取、时间戳匹配写入、
  陈旧时间戳重读、软删不可见 / 不可写。
- `tests/AxonHub.Grains.Tests/EnabledChannelsGrainTests.cs`：快照往返用例补
  `UpdatedAt`（不新增用例）。

静态计数：`AxonHub.Llm.Tests` 1501 → 1506（+5），`AxonHub.Grains.Tests` 1704 → 1712（+8），
`AxonHub.Api.Tests` 5434 → 5440（+6），`AxonHub.Persistence.Tests` 332 → 336（+4），
四项目合计 8971 → 8994。**按项目规则未运行 lint / build / test。**

台账：基线 §146、验收 §146 已写入；差异台账新增 C227（accepted），并更新 C094 / C127 /
C158 / C226；`00-reference.md` 增加「仅 OAuth 令牌刷新切片」的漂移行；总数 227 条
（164 accepted / 54 resolved / 9 gap），`diff_register.py lint` 通过、`02-differences.md`
已重新生成。
