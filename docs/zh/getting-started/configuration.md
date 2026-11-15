# 配置

AxonHub.NET 使用 ASP.NET Core 配置系统，同时兼容参照实现的
`config.yml`。配置来源按优先级从高到低：

1. 命令行参数
2. 环境变量
3. `appsettings.json` / `appsettings.{ASPNETCORE_ENVIRONMENT}.json`
4. `config.yml`

## config.yml

Compose 默认把 [`config.example.yml`](../../../config.example.yml) 挂载为
容器内的 `/app/config.yml`。复制为 `config.yml` 后可独立维护：

```bash
cp config.example.yml config.yml
```

> Dockerfile 不复制 `config.yml`；由 Compose 或运行时挂载。
> 本地运行时端口会从 Content Root（通常是 `src/AxonHub.Api`）或当前工作目录
> （通常仓库根目录）查找 `config.yml` / `config.yaml`。

端口支持参照格式的 snake_case 键，并转换为内部读取的 `Section:Key`：

```yaml
server:
  port: 8090
  request_timeout: "30s"
  sse_keep_alive:
    enabled: false
```

等价于：

```text
Server:Port=8090
Server:RequestTimeout=30s
Server:SseKeepAlive:Enabled=false
```

列表既生成索引子键也生成逗号拼接的父键，因此 `GetSection().Get<string[]>()`
和标量读取都能工作：

```yaml
server:
  cors:
    allowed_origins:
      - "http://localhost:5173"
      - "http://localhost:3000"
```

Linux 上发送 `SIGHUP` 会重新读取 `config.yml` 与 appsettings，并重新应用
IP 访问控制；端口、数据库连接等启动期设置不会重新绑定。

## 数据库

仅支持 PostgreSQL。连接串有两种写法：

```yaml
db:
  dialect: postgres
  dsn: "Host=postgres;Port=5432;Database=axonhub;Username=axonhub;Password=..."
```

或环境变量（优先级更高）：

```bash
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=axonhub;Username=axonhub;Password=..."
```

`db.dsn` 使用 Npgsql key-value 连接串，不是 `postgres://` URI。SQLite/MySQL
未移植；`db.dialect` 为其他值会在启动时明确失败。

## Orleans 单 silo 与多 silo

默认 `orleans_cluster.enabled: false`：API 进程内 single silo，localhost
clustering，内存 reminder，适合单实例和本地开发。

多副本（Kubernetes/VM）必须打开：

```yaml
orleans_cluster:
  enabled: true
  provider: adonet
  connection_string: ""       # 空 = ConnectionStrings:Default
  cluster_id: "axonhub"
  service_id: "axonhub"
  silo_name: ""                 # 默认 HOSTNAME
  advertised_ip: ""             # Kubernetes 用 POD_IP
  silo_port: 11111
  gateway_port: 0
```

集群成员表与 reminder 表在启动时自动创建，所有副本共享同一 PostgreSQL。每个 Pod
的 `advertised_ip` 必须是 Pod IP，不是 Service IP。完整部署与故障转移说明见
[Kubernetes 多副本](kubernetes.md)。

## 监听与静态资源

| 配置 | 说明 |
| --- | --- |
| `ASPNETCORE_HTTP_PORTS` / `ASPNETCORE_URLS` | 最高优先级的 Kestrel 监听设置 |
| `server.host` / `server.port` | 未设置 ASP.NET Core URL 时使用；默认 `0.0.0.0:8090`（`config.example.yml`） |
| `Server:Static:Root` | 管理面静态目录，默认 `frontend/dist` |
| `ASPNETCORE_ENVIRONMENT` | `Development` / `Production` |

## 服务器与安全

`config.example.yml` 包含以下实际读取的配置节：

| 配置节 | 用途 |
| --- | --- |
| `server.request_timeout` / `server.llm_request_timeout` | 请求与 LLM 请求超时 |
| `server.max_multipart_memory` | multipart 上传内存上限（`32M` / `1G` 等） |
| `server.trusted_proxies` | 只有这些代理可提供 `X-Forwarded-For` / `X-Real-IP` |
| `server.sse_keep_alive.enabled` / `.interval` | 空闲 SSE 心跳 |
| `server.trace.*` | thread/trace 头名、额外头、Claude Code/Codex/OpenCode 提取开关 |
| `server.cors.*` | CORS 开关、来源、方法、头、凭证 |
| `server.api.auth.allow_no_auth` / `.key_prefix` | 无鉴权开关与 API Key 前缀 |
| `server.ip_access_control.*` | IP 白名单和拒绝跳转 |
| `gc.cron` / `gc.vacuum_enabled` / `gc.vacuum_full` | GC 计划与 VACUUM |
| `metrics.enabled` / `metrics.exporter.*` | OpenTelemetry 指标（stdout / OTLP gRPC / OTLP HTTP） |

环境变量可使用参照的 `AXONHUB_*` 拼写，例如：

```bash
AXONHUB_SERVER_REQUEST_TIMEOUT=60s
AXONHUB_SERVER_CORS_ENABLED=true
AXONHUB_GC_CRON="0 3 * * 0"
```

## 参照中尚未移植的配置

以下配置节会被忽略，或由 .NET 自身机制替代：

| 参照配置 | 当前行为 |
| --- | --- |
| `cache.*` / Redis | Orleans/进程内缓存；不连接 Redis |
| `log.*` 文件轮转 | 使用 ASP.NET Core `Logging:*` |
| `provider_quota.*` | 固定五分钟调度 |
| `server.safeguards` | 运行时 reviewer 尚未移植（C243） |
| `db.read_replica` | 未实现读副本 |
| SQLite/MySQL | 不支持 |

`config.example.yml` 中未列出的键不会报错，但也不会生效。完整差异见
[移植台账](../development/porting.md)。

## Docker Compose 环境变量

| 变量 | 默认值 | 说明 |
| --- | --- | --- |
| `DB_PASSWORD` | `axonhub-local` | PostgreSQL 密码，仅本地回环使用；对外部署必须修改 |
| `AXONHUB_BIND_ADDRESS` | `127.0.0.1` | 宿主机绑定地址 |
| `AXONHUB_HOST_PORT` | `8090` | 宿主机端口 |
| `POSTGRES_IMAGE` | `postgres:17-alpine` | PostgreSQL 镜像 |
