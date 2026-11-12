# §194 fenno / github_copilot / antigravity 的 token provider 复用（C094 闭合，10 例）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/biz/channel_llm.go:1208`、
`:1218-1276`、`:1375-1391`、`:1451-1481`，`llm/transformer/antigravity/transformer.go:94-122`、
`llm/oauth/device_flow_provider.go`、`llm/transformer/openai/copilot/token_exchanger.go`。

## 背景

C094 的剩余缺口是 §146 登记的 fenno / github_copilot / antigravity 三处 provider
复用：端口只在 codex / claudecode / xai_subscription 上把 token provider 换成
`ChannelTokenGrain`，其余三类的刷新状态仍活在每次构造的一次性 provider 里。
调研纠正了 §146 的一处错误注释：参照 `channel_llm.go:1208` 让 codex 与 fenno 共用
`buildCodexOutbound`（同一 `codex.NewTokenProvider` 配方），fenno 配方并无不同。

## 交付

- **fenno**：`CreateCodexTokenProvider` 的 grain 条件改为
  `CodexChannelType or FennoChannelType`；`ChannelTokenSource.CreateProvider` 增加
  fenno 分支复用 `CodexToken` 配方。注意参照的 fail-fast 未列 `TypeFenno`，OAuth fenno
  通道只有在 `apiKeys` 非空时可达（`IsOAuth` 会隐藏 legacy apiKey）。
- **antigravity**：`AntigravityTransformerOptions` 新增 port-only `ITokenGetter?
  TokenProvider`；Auth 改用 `_tokenGetter`，本地 provider 仍由 `initTokenProvider`
  构建（`GetTokenProvider()` 非空语义不变），仅当 refreshToken 非空时注入。
  工厂在 `grainFactory != null` 且 `ParseCredentials(apiKey).RefreshToken` 非空时注入
  `GrainTokenGetter`，seed 为 `{ RefreshToken, ClientId = AntigravityConstants.ClientID,
  Scopes = AntigravityConstants.Scopes }`（与 `initTokenProvider` 相同，无 access token，
  首次 Get 即刷新）。`ChannelTokenSource` 增加 `AntigravityTokenProviderFactory` 配方；
  `PersistRefreshedCredentials` 增加 antigravity 分支：从写基 apiKey 取
  `Split('|', SplitOptions.None)[1]`，写回 `refreshed.RefreshToken + "|" + parts[1]`，
  oauth 同步写回；缺 project ID 时返回参照的
  `failed to extract project ID from antigravity credentials: api key does not contain
  project ID (expected format: "<refreshToken>|<projectID>")` 且不写库。
- **github_copilot（不接 grain）**：参照在配置 exchanger 后 `DeviceFlowProvider.GetToken`
  直接走 exchange，从不检查/刷新 OAuth 凭据（`getAccessTokenWithRefresh` 不可达），
  接 grain 会在合成的一小时过期后触发 GitHub refresh grant。改为新增进程级
  `ChannelCopilotTokenCache`（`(channelId, updated_at)` → `CopilotTokenExchanger`），
  通道行更新换新 exchanger、对应参照 enabled-cache 失效重建 provider；
  `CopilotTokenProviderParams.TokenExchanger` 为 port-only 注入接缝。
  `ChatCompletionOrchestrator` 增构造参数并在 `WithInbound` / `WithChannelSelector`
  克隆中转发；`Program.cs` 注册单例。
- 管理面（`ChannelEndpointDetectService`、`ChannelTestService`、`VideoService`）不传
  grain/cache，保持每次构造 provider，登记为已接受残余。

## 测试

- `ChannelTokenSourceTests` +4：fenno 走 codex 配方（`https://auth.openai.com/oauth/token`）；
  antigravity 写回 `refresh-refreshed|project-123`、保留 managementApiKey、请求带
  `grant_type=refresh_token` / `client_secret`；`a|b|c` 取第二段原文；缺 project ID
  不写库且内存保留刷新结果。
- `GithubCopilotChannelWiringTests` +3：共享 cache 两次构建只交换一次；无 cache 两次构建
  各交换一次；行 `updated_at` 变化换新 exchanger 后再交换。
- 新增 `TokenGrainOutboundWiringTests` 3 例（Orleans 内存集群 + `FakeChannelTokenSource`）：
  fenno OAuth 通道的 `TokenProvider` 为 `GrainTokenGetter` 且刷新经 grain；
  antigravity 经 grain（空 bearer 证明未走本地 Google 交换）且 envelope 项目号仍来自旧 key；
  `"|project-123"`（无 refresh token）保持本地 provider 并报
  `no OAuth token provider configured`，不产生 grain 刷新。
- 实测（2026-11-13）：Api 定向 56 通过 / 0 失败；Llm 定向（AntigravityTransformer /
  Copilot / OAuth）188 通过 / 0 失败。`AxonHub.Api.Tests` 未跑全量。

## 台账

C094 → resolved（280 条：205 accepted / 3 gap / 72 resolved）。§146 的
"Fenno … not the codex token recipe" 注释已删除并改为参照行号说明。
