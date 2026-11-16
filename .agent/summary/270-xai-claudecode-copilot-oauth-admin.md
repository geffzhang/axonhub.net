# §188 xai / claudecode / copilot 管理面 OAuth 端点（C270 关闭，178 例）

日期：2026-11-12
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/api/xai.go`、
`claudecode.go`、`copilot.go`、`routes.go:117-122`、`127-128`，参照测试
`xai_test.go`（4 例）、`copilot_test.go`（18 例）。

## 背景

C270 的记录是“xai / claudecode / copilot 三组管理面 OAuth 端点未移植”。三类
provider 的 llm 层构件（`Pkce`、`SubscriptionToken`、`ClaudeCodeTokenProvider`、
`SubscriptionSsoDevice`、`OAuthCredentials.ToJson`、`HttpClientUtils`）此前已落地；
缺口是管理面 HTTP 表面：七条路由挂在 admin group（`WithJWTAuth` +
`WithProjectID`）上，参照 handler 为 `XAIHandlers` / `ClaudeCodeHandlers` /
`CopilotHandlers`。

## 交付

- 新增 `src/AxonHub.Api/Auth/XaiOAuthCache.cs`、`ClaudeCodeOAuthCache.cs`、
  `CopilotDeviceFlowCache.cs`：typed 进程内缓存，到期判断复刻 go-cache
  （`now > ExpiresAt` 才算过期，恰好到期仍可见）；xai 键前缀 `xai:oauth:`、
  TTL 30 分钟、状态为 `(CodeVerifier, UserId)`；claudecode TTL 10 分钟、状态为
  `(CodeVerifier, CreatedAt)`；copilot 键前缀 `copilot:oauth`、TTL
  `min(expires_in, 15 分钟)`，并复刻 go-cache 的 0→默认 5 分钟、负值→永不过期。
  redis / two-level 后端见 C171。
- 新增 `src/AxonHub.Api/Management/XaiOAuthService.cs`：
  - `StartAsync`：不读 body；未登录 401 `authentication required`；
    `Pkce.GenerateState(32)`、`GenerateCodeVerifier(32)`、nonce(16) 与 S256
    challenge，键 `xai:oauth:<state>` 写 30 分钟 TTL 并绑定当前用户；授权 URL
    按 `url.Values.Encode` 的键序（client_id / code_challenge /
    code_challenge_method / nonce / plan / redirect_uri / referrer /
    response_type / scope / state）与转义构造。生成/缓存失败分别 500
    `generate xAI oauth state: …` / `generate xAI code verifier: …` /
    `generate xAI nonce: …` / `save xAI oauth state: …`。
  - `ExchangeAsync`：Get（错误按 miss）→ 400 `invalid or expired oauth
    session`；未登录 401；UserId 不符 400 同文案且保留会话；回调校验
    `callback_url must be a full URL` / `callback_url must contain code and
    state`；state 不符 400 `oauth state mismatch` 且保留会话；仅在校验通过后
    Delete（失败仅 warn）→ 仅 `proxy.type=url` 且 URL 非空时经
    `IProxyExecutorFactory` → `SubscriptionToken.NewTokenProvider(...).Exchange`，
    失败 502 `xAI token exchange failed: …` → `ToJson` 失败 500
    `encode xAI credentials: …`。
  - `DecodeSso`：`SubscriptionSsoDevice.ConvertSsoToBuild`，失败 502
    `xAI SSO conversion failed: …`；非 url 代理分支用
    `HttpProxyExecutorFactory.CreateTransport` 构造 transport（port-only
    `ssoTransportFactory` 接缝供测试注入）。
- 新增 `src/AxonHub.Api/Management/ClaudeCodeOAuthService.cs`：
  - `StartAsync`：`GenerateState(32)` / `GenerateCodeVerifier(64)`，键
    `claudecode:oauth:<state>`、TTL 10 分钟；授权 URL 参数 client_id /
    code_challenge / code_challenge_method / redirect_uri / response_type /
    scope / state；缓存失败 500 `failed to save oauth state: …`。
  - `ExchangeAsync`：Get → 400 `invalid or expired oauth session`；命中后
    **立即 Delete（失败仅 warn），再解析回调**，因此畸形回调、缺 code/state 与
    state 不符都消耗会话；回调文案 `callback_url must be a full URL` /
    `invalid callback_url: …` / `code parameter not found in callback_url` /
    `state parameter not found in callback_url (should be after # or in query)`；
    state 优先取 fragment、再回退 query；令牌请求为带 state 的 JSON，失败 502
    `token exchange failed: …`；仅 url 非空代理换 executor。
- 新增 `src/AxonHub.Api/Management/CopilotOAuthService.cs`：
  - `StartAsync`：只容忍空/纯空白 body（Go EOF 语义），`{}` / `null` 绑定零值；
    设备码请求为精确表单
    `client_id=Iv1.b507a08c87ecfe98&scope=read%3Auser`；缓存 TTL
    `min(expires_in, 15 分钟)`，失败 500 `failed to save device flow state: …`。
  - `PollAsync`：Get → 400 `invalid or expired session`；严格 `>` 过期判断
    （恰好到期仍可轮询）；请求表单含 device_code 与 grant_type；JSON/form
    双解析；`authorization_pending` → pending、`slow_down` → slow_down（保留
    会话）；`expired_token` / `access_denied` → 400 并删会话；成功删会话并回
    complete；未知错误 502 `OAuth error: <code> - <desc>`；空响应 500
    `unexpected response from GitHub`。
- 新增 `src/AxonHub.Api/Auth/XaiOAuthHandlers.cs`、`ClaudeCodeOAuthHandlers.cs`、
  `CopilotOAuthHandlers.cs`：`ShouldBindJSON` 等价绑定、gin `JSONError` 信封与
  `MapXaiOAuthRoutes` / `MapClaudeCodeOAuthRoutes` / `MapCopilotOAuthRoutes`。
- `src/AxonHub.Llm/Http/ProxyExecutorFactory.cs`：抽出
  `HttpProxyExecutorFactory.CreateTransport`，供 xAI SSO 的非代理分支复用。
- `Program.cs`：注册三个缓存（singleton）、三个服务（scoped）与三条路由组，
  为 `/admin/xai`、`/admin/claudecode`、`/admin/copilot` 挂
  `AdminJwtAuthMiddleware` + `ProjectIdMiddleware`。

## 测试

- `tests/AxonHub.Api.Tests/XaiOAuthServiceTests.cs`（30 例）：start 的 401、
  PKCE 字节数、授权 URL 键序与转义、随机性、TTL 边界、缓存失败；exchange 的
  未知会话、未登录 401、跨用户 400 且保留、非全 URL / 缺 code+state /
  TrimSpace、mismatch 保留、成功删除与单次消费、精确令牌表单与头、双层错误、
  精确凭据 JSON、代理矩阵；SSO 的 8 请求断言、Cookie/form、502 包装与 seam。
- `tests/AxonHub.Api.Tests/ClaudeCodeOAuthServiceTests.cs`（29 例）：start 的
  字节数、CreatedAt、URL 键序、TTL 边界、缓存失败；exchange 的先删后解析、
  mismatch 消耗、fragment 优先 / query 回退 / `%2B` 解码 / 空 fragment、
  ParseCallbackUrl 直测（`http://[::1`、缺 code、缺 state）、精确 JSON 令牌
  请求含 state、双层错误、凭据 JSON、代理矩阵。
- `tests/AxonHub.Api.Tests/CopilotOAuthServiceTests.cs`（32 例）：start 的精确
  form body、信封字段、clientId 接缝、15 分钟上限与 0→5 分钟默认、非 2xx
  503、解析失败、缺 device_code、缓存失败、代理矩阵；poll 的恰到期仍可轮询、
  `expires_in=0` +1s → device code expired、pending / slow_down / expired_token /
  access_denied / 未知错误、JSON 与 form 成功、form error、空 `{}`、畸形 form
  转义、503、代理。
- `tests/AxonHub.Api.Tests/AdminXaiOAuthEndpointTests.cs`（29 例）：三条路由的
  JWT 门与项目头、start 不读 body 且存 UserId、exchange 绑定矩阵、未知会话、
  跨用户保留、mismatch 保留、令牌失败 502、成功精确凭据与单次消费、url 代理；
  SSO 绑定矩阵与成功（DI 注入脚本化 SSO transport）。
- `tests/AxonHub.Api.Tests/AdminClaudeCodeOAuthEndpointTests.cs`（29 例）：
  JWT 门、项目头、start 绑定矩阵（空/畸形 400，`{}` / `null` 200）、返回字段、
  缓存失败、exchange 绑定矩阵、未知会话、先删后解析、mismatch 消耗、fragment
  成功、query 回退、502、url 代理。
- `tests/AxonHub.Api.Tests/AdminCopilotOAuthEndpointTests.cs`（29 例）：JWT 门、
  项目头、空/纯空白 body 放行矩阵、非法 body 矩阵、信封字段、缓存失败、
  url 代理、poll 绑定矩阵、未知会话、pending（无 token 键）、slow_down、成功
  删除、access_denied、expired_token + 时钟推进、未知错误 502、url 代理。

## 本轮实测

- 新增 178 例（服务 91、端点 87）通过；定向 `dotnet test`（六个类过滤）
  178 通过 / 0 失败 / 0 跳过，耗时约 1 s，同时证明生产代码编译无误。
- `AxonHub.Api.Tests` 未跑全量，静态计数 6280 → 6458；四项目合计
  9994 → 10172（Llm 3161 / Persistence 376 / Grains 177 未重跑）。

## 台账

- `differences.jsonl`：C270 改为 `resolved`，补记实现与 178 例测试。
- 新增 C271（accepted）：System.Uri 回调解析的接受边界与本地化文案、
  claudecode fragment 的 `UnescapeDataString` 解码、System.Text.Json 解码
  文案、xAI SSO 传输构造（`ssoTransportFactory` 接缝）、进程内缓存（C171）。
- `01-baseline.md`：§29 末尾追加 2026-11-12 更新（§188）段。
- `03-acceptance.md`：新增 §188 段。
- 271 条（197 accepted / 5 gap / 69 resolved），`diff_register.py lint` 通过，
  `02-differences.md` 已重新生成。
