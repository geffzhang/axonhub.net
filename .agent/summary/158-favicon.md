# §158 GET /favicon（品牌图标路由，14 例）

日期：2026-11-09
参照：`internal/server/api/system.go:187-251`（GetFavicon）、`internal/server/biz/system.go:899-914`
（BrandLogo）、`internal/server/assets/favicon.go`（//go:embed favicon.ico）、
`internal/server/routes.go:83-84`（公开路由）。

## 背景

§157 登记了 C242（gap）：前端 `frontend/index.html` 的
`<link rel="icon" href="/favicon">` 落到静态 index 回退，标签页拿到 HTML 而不是图标。
本切片把默认图标、品牌 data URL 与两类 400 一并接上。

## 交付

- `src/AxonHub.Api/Assets/favicon.ico`：从参照 `internal/server/assets/favicon.ico` 逐字节
  复制（28,882 字节，SHA-256
  `faadeaf0626d71ef774fdb899e54c82bd2f7717581c337e4d095ea681b2eefed`）。
- `src/AxonHub.Api/AxonHub.Api.csproj`：`EmbeddedResource` +
  `LogicalName=AxonHub.Api.Assets.favicon.ico`。
- `src/AxonHub.Api/Assets/FaviconProvider.cs`：`IFaviconProvider` /
  `EmbeddedFaviconProvider`，缺失资源返回 null（对应 `assets.Favicon.ReadFile` 的错误）。
- `src/AxonHub.Api/Endpoints/SystemHandlers.cs`：`GetFaviconAsync` 与
  `/favicon` 路由；`WriteBrandLogoErrorAsync` / `WriteFaviconAsync` / `IsGoBase64`。
- `src/AxonHub.Api/Program.cs`：注册 `IFaviconProvider`。

## 行为要点

- 无品牌标识（或读取失败）：内嵌 ico、`image/x-icon`、
  `Cache-Control: public, max-age=3600`；不显式设置 Content-Length（参照的 28,882 字节
  超过 net/http 2048 字节缓冲，走 chunked）。
- 读取失败：只经 ILogger 记录 `Failed to get brand logo`，按空值回退默认图标。
- 默认资源缺失：500 `{"error":{"type":"Internal Server Error","message":"Failed to read default favicon"}}`。
- 格式错误（非 `data:` 前缀、逗号不是恰好两段、头部缺 `:` / `;`）：400
  `Invalid brand logo format`。
- base64 失败：400 `Failed to decode brand logo`。
- 成功：`Content-Type: <mime>`、同一缓存头、显式 `Content-Length`、原始字节。

## base64 语义对齐

按 Go 1.26 `encoding/base64` 的 `decodeQuantum` 核对：StdEncoding 只忽略 CR/LF、要求完整
填充（末尾 2/3 个数据字符必须补齐 `=`）、非 Strict 模式忽略非零尾随位。
`Convert.FromBase64String` 额外忽略空格 / 制表符且同样要求长度补齐；`IsGoBase64` 预检把
字符集收窄回参照，接受 / 拒绝集合由此一致。

## 测试

`tests/AxonHub.Api.Tests/FaviconEndpointTests.cs`（14 = 7 Fact + 7 InlineData）：默认图标 /
缓存头 / 字节、读取失败回退、资源缺失 500、data URL 成功与 Content-Length、空载荷、
CR/LF 忽略、四种格式 400、三种 base64 400、内嵌资源长度与 SHA-256。

静态计数：`AxonHub.Api.Tests` 5713 → 5727（+14），`AxonHub.Persistence.Tests` 359、
`AxonHub.Grains.Tests` 1746、`AxonHub.Llm.Tests` 1506 不变，四项目合计 9324 → 9338。
**按项目规则未运行 lint / build / test。**

## 台账

C242 由 gap 改为 resolved；`02-differences.md` 重新生成（242 条：176 accepted /
56 resolved / 10 gaps），lint 通过。残余：日志通道 / 类别名不同（ILogger 类别
`AxonHub.Api.Endpoints.SystemHandlers` vs narada 结构化日志），仅进程内可见。
