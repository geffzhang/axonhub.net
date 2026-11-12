# §189 请求正文下载与预览路由（C272 收窄，C273 新增，39 例）

日期：2026-11-12
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/api/request_content.go`、
`request_live.go:97-163`、`internal/server/routes.go:145-152`、gin v1.12.0
`context.go:1295-1310` 与 net/http `fs.go`，参照测试 `request_content_test.go`、
`request_live_test.go`。

## 背景

C272 记录 7 条未注册的管理面辅助路由。本切片接通其中两条请求详情路由：

- `GET /admin/requests/:request_id/content` — `RequestContentHandlers.DownloadRequestContent`
- `GET /admin/requests/:request_id/preview` — `RequestPreviewHandlers.PreviewRequest`

两条都挂在 admin group（`WithJWTAuth` + `WithProjectID`），端口此前没有任何
`/admin/requests` 的 REST 表面。

## 交付

- 新增 `src/AxonHub.Api/Management/AdminRequestEndpointSupport.cs`：
  - `RequireProjectIdAsync`：项目缺失/≤0 → 400 `Project ID not found in context`。
  - `ResolveRequestAsync`：gin `ShouldBindUri` 的 `RequestID int` 语义（空→0，
    `strconv.ParseInt` 的 `invalid syntax` / `value out of range` 文案）→ 未加
    项目过滤的 `Request.Get`（非 NotFound 错误 → 500 `Failed to load request`）
    → 缺失或项目不符统一 404 `Request not found`。
  - `FilenameFromKey`：`filepath.Base`，`.` / `/` / 空 → `request-<id>-content`。
  - `TryGetSafeRelativePath`：`FromSlash` → `TrimPrefix("/")` → `Clean`，拒绝
    `.`、空、`..`、`..<sep>`；`JoinStoragePath` 复刻 `filepath.Join`（rooted
    第二元素不丢弃目录）；自实现 Go `filepath.Clean` / `FromSlash`。
  - `FileAttachmentDisposition`：gin `FileAttachment` 的头部规则——ASCII 名用
    `attachment; filename="<escapeQuotes>"`，非 ASCII 名用
    `attachment; filename*=UTF-8''<url.QueryEscape>`。
- 新增 `src/AxonHub.Api/Management/RequestContentHandlers.cs`：完整复刻
  content 的校验次序与文案（未保存 / 空 key → 404；key 必须
  `/<project>/requests/<id>/` 前缀；存储缺失 404、加载异常 500；Primary 或
  database → 400 `Content storage is not file-based`）。fs+Directory 走
  `Results.File`，缺失文件复刻 `http.ServeFile` 的失败响应（404、text/plain、
  nosniff、`404 page not found\n`）；其余经 `IDataStorageBackendFactory`
  （fs 用 `FromSlash`、s3 path-style 去一个前导 `/`），读取失败一律 404
  `Content not found`，成功写 Content-Length、`filename=%q`、
  application/octet-stream、`Cache-Control: private, max-age=0, no-cache`。
  `MapRequestDetailRoutes()` 注册两条路由。
- 新增 `src/AxonHub.Api/Management/RequestPreviewHandlers.cs`：端口没有
  `LiveStreamRegistry`，processing+stream 与其余状态统一走参照的静态回退；
  持久化 chunk 先按 loader 规则 sanitize（`_ext` 标记折叠），为空再
  `LoadResponseChunksAsync`；输出逐个按 `GoJson.Compact`（escapeHTML=true）
  压缩，形状为 `{"mode":"static-fetch","responseChunks":[...]}`；
  `application/json; charset=utf-8`。
- `tests/AxonHub.Api.Tests/VideoTaskTestDoubles.cs`：`FakeRequestStore` 增加
  `GetByIdFailure`，覆盖 500 `Failed to load request`。
- `Program.cs`：`/admin/requests` 的 `UseWhen` 分支挂
  `AdminJwtAuthMiddleware` + `ProjectIdMiddleware`，调用
  `app.MapRequestDetailRoutes()`。

## 测试

`tests/AxonHub.Api.Tests/AdminRequestContentEndpointTests.cs` 39 例（19 个
`[Fact]` + 9 个 `[Theory]` / 20 行）：

- 共享门：JWT 401 信封、缺项目头 400、非法项目头 400、`abc` 的
  invalid syntax、超长数字的 value out of range、未知请求 404、跨项目 404、
  行读取失败 500。
- content：未保存矩阵（false / id null / key null / key 空白）、key 前缀不符、
  存储缺失、存储加载失败、Primary 与 database 拒绝、fs 直读（精确
  Content-Disposition、非 ASCII 的 filename*、目录穿越 404 + ServeFile 文本）、
  fs 无 Directory / s3 path-style / gcs 的 key 形状与响应头、后端缺失对象与
  读取失败 404、构造失败 500、文件名引号转义。
- preview：持久化 chunk 精确 JSON、chunk 压缩、外部存储 chunk、processing +
  stream 的空列表、非流式空列表。

## 本轮实测

- `dotnet test` 定向过滤 `AdminRequestContentEndpointTests`：39 通过 / 0 失败
  / 0 跳过，约 1 s；连同既有 `AdminRequestsEndpointTests` 过滤 44 通过，约 15 s。
- `AxonHub.Api.Tests` 未跑全量。

## 台账

- `differences.jsonl`：C272 收窄为 5 条剩余路由（playground 2 条、openapi 组
  3 条，仍为 gap）；新增 C273（accepted：Results.File 的 MIME 表与条件请求
  细节、ServeFile 缺失/目录行为、整块读取的 Content-Length、预览对 `_ext`
  标记的折叠、无 LiveStreamRegistry 的静态回退与 C192 呼应）。
- `01-baseline.md`：§29 末尾追加 2026-11-12 更新（§189）段。
- `03-acceptance.md`：新增 §189 段。
- 273 条（198 accepted / 6 gap / 69 resolved），`diff_register.py lint` 通过，
  `02-differences.md` 已重新生成。
