# §246 HTTP 路由面审计

## 背景

M5 公开 API 审计的 HTTP 路由部分：参照在 `internal/server/routes.go`、
`codex_routes.go` 与 `api/oidc.go` 注册的每条 method+path，是否都能在端口
`src/AxonHub.Api` 的映射中找到。

## 审计方法

- 参照表：逐条读出 76 条 method+path，含 `/oauth/oidc/*` 五条、`/codex/*`
  七条、OpenAI / Jina / TypeSafe / Anthropic / Doubao 各组，以及
  `/gemini/:version` 与 `/v1beta` 两组 Gemini 路由。
- 端口：静态扫描 `src/AxonHub.Api/**/*.cs` 的
  `MapGet` / `MapPost` / `MapDelete` / `MapGraphQL` 字面路径。
- 归一化只忽略参数名，并把 gin 的 `*model` / `:param` 映射为 ASP.NET 的
  catch-all / 参数占位；不改变任一请求的匹配结果。
- 静态兜底（`StaticHandler` / `MapFallback`）和中间件顺序不在路由表口径内，
  分别由静态托管用例和各端点安全用例覆盖。

## 结果

- 参照 76 条，端口 76 条，**missing 0 / extra 0**。
- 新增 `porting/scripts/http_route_audit.py`（仅标准库），本地运行输出
  `HTTP route surface matches the reference.`。
- 接入 `.github/workflows/porting-guard.yml` 的 `routes` job，并把
  `src/AxonHub.Api/**` 加入 PR/push 路径过滤。

## 测试

- 本切片无新增 xUnit 用例，未跑 Api 全量；脚本本地复核 76/76。
- 台账不变：309 条（191 accepted / 118 resolved / 0 gap）。

## 边界

- 只比较静态 method+path；动态路由、授权/中间件差异仍由既有端点测试与
  C014 / C109 / C167 等条目跟踪。
- 端口新增或删除任何字面路由而未更新参照表时，CI 的 `routes` job 会失败。
