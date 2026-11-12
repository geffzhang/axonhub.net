# §187 codex 管理面 OAuth 端点（C094 (11) 关闭，69 例）

日期：2026-11-12
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/api/codex.go`、
`routes.go:114-116`、`llm/transformer/openai/codex/token.go`、`llm/oauth/pkce.go`，
参照测试 `internal/server/api/codex_test.go`。

## 背景

C094 (11) 的记录是“`DecodeAuthJson` 的消费方未移植”。`CodexToken.DecodeAuthJson`
本身已随 llm 模块落地，缺口是管理面的三条 HTTP 端点：
`POST /admin/codex/oauth/start`、`POST /admin/codex/oauth/exchange` 与
`POST /admin/codex/auth/decode`。三者挂在 admin group（`WithJWTAuth` +
`WithProjectID`）上，参照 handler 为 `CodexHandler.StartOAuth` / `Exchange` /
`DecodeAuthJSON`。

## 交付

- 新增 `src/AxonHub.Api/Auth/CodexOAuthCache.cs`：
  `CodexOAuthState(CodeVerifier, CreatedAt)`、`ICodexOAuthStateCache` 与进程内 TTL
  实现。到期判断复刻 go-cache：`now > ExpiresAt` 才算过期，恰好到期仍可见
  （redis / two-level 后端见 C171）。
- 新增 `src/AxonHub.Api/Management/CodexOAuthService.cs`：
  - `StartAsync`：`Pkce.GenerateState(32)`、`Pkce.GenerateCodeVerifier(64)` 与 S256
    challenge；缓存键 `codex:oauth:<sessionId>`、TTL 10 分钟；分别映射 500
    `failed to generate oauth state` / `failed to generate code verifier` /
    `failed to save oauth state`。授权 URL 按 `url.Values.Encode` 的键序与转义
    构造，参数含 `id_token_add_organizations=true`、
    `codex_cli_simplified_flow=true`，不含 `originator`。
  - `ExchangeAsync`：取会话（读失败视为 miss）→ 400
    `invalid or expired oauth session`；随后立即删除会话（失败只忽略），再解析
    `callback_url` 与校验 state——参照测试明确畸形回调与 state 不匹配都会消耗
    会话。回调校验文案：`callback_url must be a full URL`、`invalid
    callback_url: …`、`code parameter not found in callback_url`、`state
    parameter not found in callback_url`、`oauth state mismatch`。仅
    `proxy.type=url` 且 URL 非空时经 `IProxyExecutorFactory`。令牌交换复用
    `CodexToken.CreateTokenProvider(...).Exchange(...)`，失败 502
    `token exchange failed: …`；`OAuthCredentialsJson.ToJson` 失败 500
    `failed to encode credentials: …`。
  - `DecodeAuthJson`：失败 400 `failed to decode auth json: …`，编码失败 500。
  - `ParseCallbackUrl` 复刻 `TrimSpace`、`http://`/`https://` 前缀检查、
    `url.Parse` 与 query 首值语义。
- 新增 `src/AxonHub.Api/Auth/CodexOAuthHandlers.cs`：`ShouldBindJSON` 等价绑定
  （case-insensitive；空 body 与非法 JSON → 400 `invalid request format`；
  literal `null` 绑定为零值结构体并放行）、gin `JSONError` 信封、三条路由的
  `MapCodexOAuthRoutes`。
- `Program.cs`：注册 `ICodexOAuthStateCache`（singleton）与
  `CodexOAuthService`（scoped）；调用 `MapCodexOAuthRoutes()`；为
  `/admin/codex` 挂 `AdminJwtAuthMiddleware` + `ProjectIdMiddleware`。

## 测试

- `tests/AxonHub.Api.Tests/CodexOAuthServiceTests.cs`（32 例）：
  - start：state 32 字节 / verifier 64 字节、CreatedAt 秒精度、授权 URL 的排序
    键集合、无 `originator` / `access_type` / `prompt`、随机性、缓存写入失败
    500。
  - exchange：未知/过期会话、恰好 10 分钟仍有效、非全 URL（含前导空白的
    Trim 分支）、畸形 URL、缺 code/state、state 不匹配后会话已删、非 URL 回调
    先消费会话、成功后再次交换 400、令牌请求精确 body（含
    `grant_type=authorization_code` 与编码后的 redirect_uri、无 User-Agent）、
    双层 `token exchange failed: token exchange failed: …`、传输错误
    `POST - … with status 500 Internal Server Error`、凭据精确 JSON（含
    `expires_at":"0001-01-01T00:00:00Z"`）、空 refresh_token、url 代理、三种
    非 url 代理被忽略。
  - decode：精确规范化 JSON（`last_refresh` + 1 小时 → `expires_at`）、
    无时间戳的 refreshable → now+1h、不可解析 `last_refresh` 被忽略、空文档、
    空白 `access_token`、畸形 JSON 的错误前缀。
- `tests/AxonHub.Api.Tests/AdminCodexOAuthEndpointTests.cs`（37 例）：
  三条路由的 JWT 门（401 `API key is required`）、start 的绑定失败矩阵与
  `null` 放行、`X-Project-ID` 校验、缓存失败 500；exchange 的 9 种非法 body、
  未知会话、非 URL 回调消费会话、state 不匹配后重试仍 400、令牌失败 502 且
  会话已删、成功返回精确 credentials 且单次消费、url 代理路由、disabled 代理
  忽略；decode 的 6 种非法 body、成功规范化与 `{"tokens":{}}` 的 400。

## 本轮实测

- 新增 69 例通过；`--filter FullyQualifiedName~CodexOAuth` 共 73 例通过
  （另 4 例为既有 `CodexChannelWiringTests.CodexOAuth*`）。
- `AxonHub.Api.Tests` 未跑全量，静态计数 6211 → 6280；四项目合计
  9925 → 9994（Llm 3161 / Persistence 376 / Grains 177 未重跑）。

## 台账

- `differences.jsonl`：C094 的 `what` / `decision` / `test` 补记 §187，
  (11) 关闭；`status` 仍为 `gap`（剩余仅为 §146 的 fenno /
  github_copilot / antigravity provider 复用）。
- 新增 C270（gap）：xai / claudecode / copilot 的管理面 OAuth 端点
  （`routes.go:117-122`、`127-128`）未移植。
- `01-baseline.md`：§29 末尾追加 2026-11-12 更新（§187）段。
- `03-acceptance.md`：新增 §187 段。
- 270 条（196 accepted / 6 gap / 68 resolved），`diff_register.py lint` 通过，
  `02-differences.md` 已重新生成。
