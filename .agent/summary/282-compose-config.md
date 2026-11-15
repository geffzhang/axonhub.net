# §282 docker-compose 与 config.example.yml

## 目标

为 axonhub.net 提供可直接启动本地栈的 `docker-compose.yml` 和参照格式的
`config.example.yml`，同时让 .NET 宿主真正读取 `config.yml`，而不是只放一份
不会被消费的样例。

## YAML 配置加载

新增 `src/AxonHub.Api/Configuration/YamlConfiguration.cs`：

- `YamlConfigurationSource` / `YamlConfigurationProvider` 读取 `config.yml`，
  `IConfiguration.Reload()` 会重新读文件（SIGHUP 可用）；
- `YamlConfigurationParser` 把 YAML 映射/序列扁平化为 `Section:Key`；
- 每个 snake_case 键段转成 PascalCase，例如
  `server.request_timeout` → `Server:RequestTimeout`，
  `server.sse_keep_alive.enabled` → `Server:SseKeepAlive:Enabled`；
- 标量列表同时生成索引子键（`...:0`、`...:1`）和逗号拼接的父键，兼容
  `GetSection().Get<string[]>()` 与标量读取；空列表保留显式空串。

`Program.cs` 在启动时按 `config.yml` → appsettings → 环境变量 → 命令行顺序
重新叠加，保持“环境变量覆盖文件”的参照语义。`config.yml` 存在时，
`ConfigReloadService` 优先报告它并支持 SIGHUP 重载。

配置驱动的启动行为：

- 未设置 `ASPNETCORE_HTTP_PORTS` / `ASPNETCORE_URLS` 时，`server.port` 决定
  Kestrel 监听端口（`server.host` 可选，默认 `0.0.0.0`）；
- `ConnectionStrings:Default` 缺失时回退到 `db.dsn`；
- `db.dialect` 只接受 PostgreSQL 别名，SQLite/MySQL 会启动失败并给出明确文案。

## config.example.yml

只列出现有端口实际使用的配置节：server（host/port/timeout/trusted_proxies/
SSE/trace/CORS/API auth/IP access control）、db、gc、metrics。文件头说明
`cache.*`、`log.*`、`provider_quota.*`、`server.safeguards` 运行时未移植，
避免把参照的 Redis/日志配置误认为已生效。

## docker-compose.yml

- `postgres:17-alpine`：持久卷、`pg_isready` 健康检查、仅 backend 网络；
- `axonhub`：从本仓库 Dockerfile 构建，内置管理界面；
- `ConnectionStrings__Default` 与 `ASPNETCORE_HTTP_PORTS=8090` 由 compose
  环境变量注入，优先级高于 config；
- 挂载 `./config.example.yml:/app/config.yml:ro`；
- 默认绑定 `127.0.0.1:8090`，可设置 `DB_PASSWORD` /
  `AXONHUB_BIND_ADDRESS` / `AXONHUB_HOST_PORT`；
- app 只加入 backend + egress 网络，`read_only`、`tmpfs /tmp`、drop caps；
- 运行镜像无 curl/wget，因此 app 不使用容器内 healthcheck，由 PostgreSQL
  健康检查控制启动顺序，`/health` 供外部检查。

## 验证

- `docker compose config --quiet` 通过。
- `docker compose up -d --build`：
  - PostgreSQL healthy，app 启动并监听 8090；
  - `GET /health` → 200；
  - `GET /` → 200，index.html 含 `<div id="root">`；
  - `/app/config.yml` 与 `/app/frontend/dist/index.html` 均在镜像/挂载中；
  - 带 `Origin: http://localhost:5173` 的请求返回
    `Access-Control-Allow-Origin` 与 credentials，证明 CORS 读取了 YAML；
- 测试栈已 `docker compose down` 并删除测试卷。

## 测试

- 新增 `YamlConfigurationTests` 3 例：键扁平化/列表/空列表、provider reload；
- `ConfigReloadTests` 新增 `FindConfigFilePrefersTheReferenceConfigYml`；
- 全量 Api 正在后台复跑（YAML 3 例已包含）；ConfigReload 新例待全量结束后
  定向运行，避免与全量 testhost 的文件锁冲突。

## 边界

- YAML 只覆盖端口实际读取的配置节；未移植的 cache/log/provider_quota/
  safeguards 明确标注忽略。
- `db.dsn` 使用 Npgsql key-value 连接串；`postgres://` URI 未做转换。
- 不支持 SQLite/MySQL。
