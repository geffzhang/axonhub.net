# §111 Antigravity OAuth 管理面（start / exchange / 项目发现）

日期：2026-11-06
参照：`b8bb3421:internal/server/api/antigravity.go`（AntigravityHandlers、parseAntigravityCallbackURL、
resolveProjectID、onboardUser、state / verifier / challenge 生成）、
`internal/server/api/error.go:12-20`（JSONError）、`internal/server/routes.go:106` / `:124-125`、
`llm/httpclient/proxy.go` 与 `client.go:71-215`（ProxyConfig / NewHttpClientWithProxy / getProxyFunc）。
参照没有该文件的测试。

实时上游 `f272549f` 的 `antigravity.go` / `routes.go` 与冻结基线逐字节一致。

## 交付

- `src/AxonHub.Llm/Http/ProxyConfig.cs`：`ProxyType`（disabled / environment / url）与 `ProxyConfig`
  （`disableConnectionReuse` 沿用 Go 的 JSON tag）。
- `src/AxonHub.Llm/Http/ProxyExecutorFactory.cs`：`IProxyExecutorFactory` + `HttpProxyExecutorFactory`，
  复刻 `NewHttpClientWithProxy` 的传输选项（ConnectTimeout 30s、Idle 90s、ExpectContinue 1s、无总超时），
  url 分支用 `WebProxy` 并在请求时抛出空 URL / 不可解析 URL 的错误，environment / 未知类型回退
  `HttpClient.DefaultProxy`。
- `src/AxonHub.Api/Auth/AntigravityOAuthCache.cs`：`AntigravityOAuthState(CodeVerifier, ProjectId, CreatedAt)`
  与 go-cache 语义（`now > expiresAt` 才过期）的进程内缓存；接口便于测试注入失败。
- `src/AxonHub.Api/Management/AntigravityOAuthService.cs`：PKCE 生成、授权 URL、回调解析、令牌交换、
  loadCodeAssist / onboardUser 项目发现；`AntigravityOAuthException(StatusCode, Message)` 承载 JSONError 分类。
- `src/AxonHub.Api/Auth/AntigravityOAuthHandlers.cs`：`MapAntigravityOAuthRoutes` 映射
  `POST /admin/antigravity/oauth/start` 与 `/exchange`，ShouldBindJSON 语义（类型不符 / 空 body 400、
  字面 null 视为零值）与 gin 信封。
- `src/AxonHub.Api/Program.cs`：DI（代理工厂、状态缓存、业务服务）、路由挂载与
  `/admin/antigravity/oauth` 的 AdminJwtAuthMiddleware + ProjectIdMiddleware 分支。

## 行为要点

- start：绑定失败 400 `invalid request format`，JSON `null` 接受为零请求；state 32 字节、verifier
  64 字节、challenge 为 SHA-256 base64url；缓存键 `antigravity:oauth:<state>`，值含 `code_verifier` /
  `project_id` / `created_at`，TTL 10 分钟且恰在截止时刻仍有效；Set 失败 500
  `failed to save oauth state: …`；授权 URL 的查询键按 url.Values.Encode 排序，响应
  `{session_id, auth_url}`。
- exchange：绑定失败或空 `session_id` / `callback_url` 400 `invalid request format`；未命中缓存 400
  `invalid or expired oauth session`；回调按 TrimSpace → http(s) 前缀 → URL 解析 → code → state 顺序
  报 400（文案分别为 `callback_url must be a full URL`、`invalid callback_url: …`、
  `code parameter not found in callback_url`、`state parameter not found in callback_url`）；state
  不一致 400 `oauth state mismatch` 且保留会话；校验成功后消费会话（删除失败只告警）；proxy 仅在
  type=url 且 url 非空时经工厂生效；令牌交换失败 502 `token exchange failed: …`（provider 文本已含同名前缀）；
  project_id 为空时按 Prod / Daily / Autopush 探测 loadCodeAssist（项目可为字符串或 `{id}` 对象），
  再按 allowedTiers 首项 / isDefault 选择 tier 调 onboardUser（每端点 3 次、失败 1s、未完成 2s），
  失败 502 `failed to resolve project id and none provided: …`；成功输出 `refreshToken|projectID`。
- 线路形状：令牌请求表单字段与顺序、Accept、UA；loadCodeAssist / onboardUser 的五个头与 body 键序
  与参照逐字节一致。

## 测试

- `tests/AxonHub.Api.Tests/AntigravityOAuthServiceTests.cs`：35 例——PKCE 形状与逐调用随机、授权 URL
  键序、TTL 截止边界、回调五类错误、令牌请求逐字节、provider / transport 双层包装、项目 string / object /
  大小写、tier 选择、端点回退与状态、onboard 3 次重试顺序、代理生效与忽略、空 refresh token。
- `tests/AxonHub.Api.Tests/AdminAntigravityOAuthEndpointTests.cs`：26 例——JWT 门与 Bearer 文案、绑定
  失败（空 / 坏 JSON / null / 缺字段）、gin 信封、项目头校验、缓存失败 500、未知 / 过期会话、非 URL
  回调、state 不一致、502 分类、会话单次消费、代理接线。
- 静态计数：`AxonHub.Api.Tests` 2848 → 2909（+61），`AxonHub.Persistence.Tests` 294、Llm 3032、
  Grains 95，合计 6269 → 6330。**本轮按项目规则未运行 lint / build / test**，下一次全量运行应以该数字复核。

## 差异

- C121 由 gap 转为 resolved：两条 REST 路由、缓存、令牌交换与项目发现均已落地。
- C202（accepted）：代理错误文案与 connect 时机、`disableConnectionReuse` 用
  `PooledConnectionIdleTimeout=0` 表达、environment 用 `HttpClient.DefaultProxy`、请求体的
  ShouldBindJSON 尾随内容语义、System.Text.Json 解码文案、xcache 多后端仍见 C171、crypto/rand
  失败的不可测分支。

## 残余风险

- 本轮未运行构建与测试，新增代码的编译 / 分析器正确性仅由人工检查保证。
- `HttpProxyExecutorFactory` 的 url 分支由 API 用例经接缝验证配置传递，具体 `SocketsHttpHandler`
  参数与错误时机以代码审查为准（C202）。
- 项目发现的 1s / 2s 重试延迟用注入的 `TimeProvider` 折叠为零耗时；生产行为仍按参照等待。
