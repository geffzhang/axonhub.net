# §228 multipart 内存阈值与 log.* 配置面

## 背景

C302 / C303 是 §225 请求路径配置面的最后两项：`server.max_multipart_memory`
与 `log.*`。§227 已关闭 C298 / C299，本轮处理这两项。

## 实现

- `src/AxonHub.Api/Cli/CliConfiguration.cs`
  - 新增 `GetMaxMultipartMemory`：`Server:MaxMultipartMemory` →
    `AXONHUB_SERVER_MAX_MULTIPART_MEMORY` → 默认 `32M`，复用
    `ParseMemorySize` 的整段原文错误文案。
  - `BuildPreview` 改用该方法，`config preview` 输出不变（33554432 字节）。
- `src/AxonHub.Api/Http/MultipartMemoryOptions.cs`
  - `Configure(IServiceCollection, IConfiguration)` 把配置值写入
    `FormOptions.MemoryBufferThreshold`。
  - 显式 0 / 空值回退到 gin 的默认 32 MiB（`server.go:52` 只在正值时赋值）；
    超过 `int.MaxValue` 的值按 `int.MaxValue` 截断。
- `Program.cs`：在 `WebApplication.CreateBuilder` 之后立即接线，与
  `internal/server/server.go:50-54` 的位置对应。
- HotChocolate 16 的 `MultipartFormReader` 从 `IOptions<FormOptions>` 读取
  `MemoryBufferThreshold` / `MultipartBodyLengthLimit` 等字段（程序集字符串
  与 `FormOptions._formOptions` 可证），因此配置会进入实际 multipart 解析。

## C303 的边界

- 端口读取 `log.name/level/debug/encoding/output` 与 `AXONHUB_LOG_*` 回退，
  但宿主日志仍由 ASP.NET Core Logging（appsettings / 环境变量）决定。
- `file.*`、`includes/excludes`、JSON 字段键仍未映射；C303 保持 accepted，
  由 `ConfigurationSurfaceTests` 锁定键读取面。

## 测试

- `ConfigurationSurfaceTests`：10/10 通过（58ms）。
  - C302：默认 32 MiB、Server 键覆盖、环境键回退、Server 键优先、
    0 回退 gin 默认、非法 `12X` 文案、`FormOptions.MemoryBufferThreshold` 实际值。
  - C303：`log.*` 默认值与覆盖、`AXONHUB_LOG_*` 回退。
- 未跑 Api 全量；全量仍为 §227 的 7028 例，本轮新增 10 例待下次全量计入。

## 台账

- C302 → resolved；C303 更新 what/decision/test，status 保持 accepted。
- 305 条（201 accepted / 104 resolved / 0 gap）；`diff_register.py lint`
  通过；`02-differences.md` 已重新生成。
