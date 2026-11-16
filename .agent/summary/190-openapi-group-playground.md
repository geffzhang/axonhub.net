# §190 playground 与 openapi 组（C272 收窄，C274–C277 新增，29 例）

日期：2026-11-12
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/routes.go:157,164-176`、
`internal/server/gql/openapi/graphql.go`、`openapi.graphql`、`openapi.resolvers.go`、
`internal/server/api/system.go:103-130`（webhook echo）、`internal/server/middleware/auth.go:125-177`
（WithOpenAPIAuth）、gqlgen v0.17.86 `playground/page.tmpl`。

## 背景

C272 记录 5 条未注册的管理面辅助路由。本切片接通其中 4 条，C272 收窄为单独的
`POST /admin/playground/chat`（依赖 aisdk transformer 与管理面 chat 编排入口，约
1,500 行，留作独立切片）：

- `GET /admin/playground` — admin group（JWT + WithProjectID）
- `POST /openapi/v1/graphql` — openapi group（IP 黑名单 → OpenAPI 鉴权）
- `GET /openapi/v1/playground` — openapi group
- `POST /openapi/webhook/echo` — openapi group

## 交付

- `src/AxonHub.Api/Auth/OpenApiAuthMiddleware.cs`：WithOpenAPIAuth 的完整分支。
  Authorization 首值；空 → 401 `API key is required`；非 `Bearer ` 前缀 → 401
  `invalid token: Authorization header must start with 'Bearer '`；TrimSpace 后空 →
  401 `invalid token: API key is required`；查找/校验失败 → 401 `Invalid API key`；
  其他失败 → 500 `Failed to validate API key`；`Type != service_account` → 401；
  AllowedIps 不匹配 → 403；随后发布 `RequestIdentity`（apiKey + project id）。
- `src/AxonHub.Api/GraphQL/OpenApiGraphQLSetup.cs` / `OpenApiRoots.cs` /
  `OpenApiTypes.cs`：独立具名 HotChocolate schema `openapi`，POST-only；
  `AddQueryType` / `AddMutationType` 显式命名根类型 `Query` / `Mutation`（HC16
  默认按 CLR 类名）；错误过滤器把异常消息恢复为 message 并清空 code/extensions
  （对齐 gqlgen 默认 presenter）；内省拦截器逐请求 `AllowIntrospection()`；
  标量 `Time` / `Decimal` / `DecimalInput` 与枚举 `ChannelTagsMatchMode`。
  解析器复刻 `apiKey`、`apiKeyQuotaUsages`、`createLLMAPIKey`、
  `updateAPIKeyProfiles`、`loadApiKeyProfileTemplate`（含 profile 的 nil
  modelMappings → `[]` 归一、template 先于 key 解析、quota 错误不包装）。
- `src/AxonHub.Api/Management/ApiKeyManagementService.cs`：补
  `CreateLlmApiKeyAsync`（TrimSpace、owner 的 UserId/ProjectId、事务内 lock +
  privacy + 重名检查与回滚、`failed to create api key: ` 包装）与
  `GetForReadAsync`（id/key/name 三选一、隐私过滤、name 歧义与 not singular 文案）。
- `src/AxonHub.Api/Management/WebhookEchoEndpoint.cs`：1 MiB 上限读取、
  `json.Valid` 门（非法 JSON → 200 空 body）、method/path/query/headers/body
  固定顺序、`application/json`、encoder 尾随换行、`json.RawMessage` 的
  compact + HTML 转义、`ParseGoQuery` / `QueryUnescape`（跳过 `;` 段与非法转义、
  `+` 变空格、重复键保序）、`CanonicalMIMEHeaderKey`、Host/Transfer-Encoding
  过滤、UTF-8 字节序排序。
- `src/AxonHub.Api/Static/PlaygroundPages.cs` + `Static/Playground/admin.html` /
  `openapi.html`：gqlgen v0.17.86 模板对两个 endpoint 的逐字节渲染结果，作为
  EmbeddedResource 返回，Content-Type `text/html; charset=UTF-8`。
- `src/AxonHub.Api/Auth/AbortResponse.cs`：改用
  `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` + `GoJson.Escape`，单引号保持
  原样，`<` / `>` / `&` 仍按 Go 转义。
- `Program.cs`：挂载 4 条路由；非 POST 的 `/openapi/v1/graphql` 与 HEAD 的两个
  playground 短路到 `StaticHandler` 的 404 信封；openapi 组 UseWhen 先
  `IpBlocklistMiddleware` 后 `OpenApiAuthMiddleware`；admin playground 用
  `AdminJwtAuthMiddleware` + `ProjectIdMiddleware`。

## 测试

`tests/AxonHub.Api.Tests/OpenApiEndpointTests.cs` 29 例：

- 鉴权矩阵 4：缺头、非 Bearer / 尾随空白、未知/停用/user 类型、AllowedIps 403。
- `apiKey` 6：id/key/name 三路投影、三选一、GID 类型与 Atoi 文案、只读 key 的
  privacy deny（无 extensions）、跨项目与缺失、重名歧义。
- `createLLMAPIKey` 4：trim + owner 继承、空名、重名回滚（错误无 extensions）、
  只写 scope 被 privacy 拒绝（`failed to create api key: ent/privacy: deny rule`）。
- `updateAPIKeyProfiles` 2：nil modelMappings → `[]`、跨项目 404。
- `loadApiKeyProfileTemplate` 2：追加 profile 与 template 链持久化、template/key
  各自三选一（template 先解析）。
- `apiKeyQuotaUsages` 2：行投影、quota 服务错误直出。
- 内省 1：`APIKeyProfile` / `APIKey` / `APIKeyProfileInput` / `Query` / `Mutation`
  字段清单。
- playground 2：openapi 页面字节 + Content-Type；admin 页面 JWT 门与字节。
- 路由回退 1：非 POST graphql 与 HEAD playground 的 static 404。
- webhook 4 + 解析器 1：Go 编码形状、非法 JSON 空 body、1 MiB 截断、
  `ParseGoQuery` 的非法转义 / `;` / `+` / 重复键 / UTF-8 键序。

## 本轮实测

- `dotnet test --filter FullyQualifiedName~OpenApiEndpointTests`：29 通过 /
  0 失败 / 0 跳过。
- 鉴权回归：`ApiKeyAuthMiddlewareTests`、`GeminiKeyAuthMiddlewareTests`、
  `AdminGraphqlAuthTests`、`AdminAuthEndpointTests` 合计 54 通过 / 0 失败。
- `AxonHub.Api.Tests` 未跑全量。
- 用冻结的 Go echo 参照服务器确认 `Authorization: Bearer    ` 在 Go net/http
  侧同样被裁成 `Bearer`，故该输入两侧都走 `must start with 'Bearer '` 分支；
  空 token 分支在真实 HTTP 上不可达。
- 内省诊断确认 HC16 默认把根类型暴露为 `OpenApiQuery` / `OpenApiMutation`，
  已显式改名对齐参照。

## 台账

- `differences.jsonl`：C272 收窄为仅 `POST /admin/playground/chat`（gap）；
  新增 C274（OpenAPI GraphQL 引擎替换）、C275（OpenAPI 鉴权与路由回退）、
  C276（webhook echo 运行时差异）、C277（playground 页面与 GET-only 路由）。
- lint 通过，`02-differences.md` 已重新生成；277 条
  （202 accepted / 6 gap / 69 resolved）。
- `01-baseline.md` 与 `03-acceptance.md` 已追加 §190。

## 后续

- `POST /admin/playground/chat`：aisdk transformer + orchestrator inbound。
- C192 实时 SSE 预览仍待 `LiveStreamRegistry` 接缝。
