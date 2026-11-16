# AxonHub.NET 部署资产

本目录是上游 `deploy/` 的 .NET 版本，按 AxonHub.NET 的实际架构重写：

- **只支持 PostgreSQL 17**，没有 SQLite/MySQL；
- 本地安装通过 `dotnet publish` 生成自包含单文件，而不是下载 Go release；
- 管理面 `frontend/dist` 由安装脚本复制到运行目录；
- Kubernetes 多副本使用 Orleans AdoNet membership + durable reminder；
- 集群模式通过 PostgreSQL 提供关键配置一致读取、全局 FIFO 并发准入和 OIDC
  单次状态消费；故障占位不会自动释放，处置步骤见 [Helm 文档](helm/README.md#多副本一致性)；
- Docker Compose 仍以仓库根目录的 `docker-compose.yml` 为准。

## 目录

```text
deploy/
├── install.sh / install.ps1           # 源码发布并安装
├── start.sh / start.ps1               # 直接启动（PID + 日志）
├── stop.sh / stop.ps1
├── restart.sh / restart.ps1
├── upgrade.sh / upgrade.ps1           # 更新源码、重发布、重启
├── setup.sh / setup.ps1               # systemd / launchd / 计划任务自启动
├── axonhub.service                    # /opt/axonhub 布局的系统服务示例
├── nginx.conf                         # SSE / WebSocket 反向代理
└── helm/                              # Kubernetes Helm chart
```

## 前置条件

- Linux 或 macOS：.NET SDK 10.0.4xx；首次安装时如果 `frontend/dist` 不存在，
  还需要 Node.js 24 + pnpm 10；
- Windows：.NET SDK 10.0.4xx；同样可选 Node.js + pnpm；
- PostgreSQL 17（数据库需提前创建，账号需有建表权限）；
- Kubernetes 路线：Docker、kubectl、Helm 3/4。

## Linux / macOS 本地安装

在仓库根目录执行：

```bash
sudo ./deploy/install.sh \
  --db-dsn "Host=127.0.0.1;Port=5432;Database=axonhub;Username=axonhub;Password=change-me"
```

脚本会：

1. 以 `sudo` 调用者身份执行 `dotnet publish`，生成自包含单文件；
2. 安装到 `/usr/local/bin/axonhub`；
3. 复制 `frontend/dist` 到 `~/.config/axonhub/frontend/dist`；
4. 首次安装时生成 `~/.config/axonhub/config.yml`（已存在则保留）。

启动：

```bash
./deploy/start.sh
curl http://127.0.0.1:8090/health
```

停止 / 重启：

```bash
./deploy/stop.sh
./deploy/restart.sh
```

日志在 `~/.config/axonhub/axonhub.log`，PID 在
`~/.config/axonhub/axonhub.pid`。配置里的 `server.port` 会由启动脚本读取；
环境变量（`ASPNETCORE_HTTP_PORTS`、`ConnectionStrings__Default`）优先级更高。

### 配置

```yaml
server:
  host: "0.0.0.0"
  port: 8090
  pid_file: "/home/you/.config/axonhub/axonhub.pid"

db:
  dialect: postgres
  dsn: "Host=127.0.0.1;Port=5432;Database=axonhub;Username=axonhub;Password=change-me"

orleans_cluster:
  enabled: false
```

`db.dsn` 必须是 Npgsql key-value 连接串，不能是 `postgres://` URI。
本机单进程保持 `orleans_cluster.enabled: false`；多实例才打开它。

### 自启动

```bash
sudo ./deploy/setup.sh install-autostart
./deploy/setup.sh status
sudo ./deploy/setup.sh uninstall-autostart
```

Linux 使用 systemd 用户服务，必要时回退到系统服务；macOS 使用 launchd。
服务从 `~/.config/axonhub/config.yml` 读取配置，stdout/stderr 追加到
`axonhub.log`。

### `/opt/axonhub` 系统服务

`axonhub.service` 是另一种布局，适合把发布产物放到 `/opt/axonhub`：

```bash
sudo useradd --system --home /opt/axonhub --shell /usr/sbin/nologin axonhub
sudo install -d -o axonhub -g axonhub /opt/axonhub /etc/axonhub
sudo cp -a <publish-output>/. /opt/axonhub/
sudo install -m 0755 /opt/axonhub/AxonHub.Api /opt/axonhub/axonhub
sudo install -m 0644 deploy/axonhub.service /etc/systemd/system/axonhub.service
sudo systemctl daemon-reload
sudo systemctl enable --now axonhub
```

配置放在 `/opt/axonhub/config.yml`，数据库连接可写在
`/etc/axonhub/axonhub.env`：

```bash
ConnectionStrings__Default=Host=127.0.0.1;Port=5432;Database=axonhub;Username=axonhub;Password=change-me
```

该服务的 `ReadWritePaths`、`ProtectHome` 和 `PrivateTmp` 只按 `/opt/axonhub`
布局配置；不要直接套用到 `install.sh` 的用户目录布局。

## Windows

在仓库根目录的 PowerShell 中执行：

```powershell
.\deploy\install.bat --db-dsn "Host=127.0.0.1;Port=5432;Database=axonhub;Username=axonhub;Password=change-me"
.\deploy\start.bat
```

安装目录是 `%LOCALAPPDATA%\AxonHub`，配置为
`%LOCALAPPDATA%\AxonHub\config.yml`，管理面在
`%LOCALAPPDATA%\AxonHub\frontend\dist`。日志为 `axonhub.log` 和
`axonhub.err.log`。自启动使用计划任务，失败时回退到启动文件夹快捷方式。

Windows 和 Linux 的 `upgrade` 默认要求源码工作树干净，然后
`git pull --ff-only`、重新发布并重启；本地改动可以用 `--no-pull` 保留。

## Nginx

`nginx.conf` 假设 API 可通过 `axonhub:8090` 访问（Compose 服务名），提供：

- TLS 终止和 HTTP 跳转；
- `/v1/` 关闭代理缓冲，支持 SSE；
- `/v1/responses` 的 WebSocket Upgrade；
- `/admin/graphql` 的长超时；
- `/health` 代理到真实后端；
- 100 MiB 请求体上限，覆盖音频/图片上传。

证书默认路径是 `/etc/nginx/ssl/cert.pem` 和 `key.pem`。如果不用 TLS，
删除 443 server 和 HTTP 跳转即可。

## Helm / Kubernetes

### 1. GHCR 镜像

GitHub Actions 在默认分支 `main` 更新时发布 `main` 标签；推送 `v1.2.3`
这类版本标签时发布 `1.2.3`、`1.2` 和 `latest`。PR 只构建验证，不推送。
首次发布的 GHCR 包默认是私有的；如需匿名拉取，请在包的 **Package settings**
中将可见性改为 **Public**。

```bash
docker pull ghcr.io/geffzhang/axonhub.net:latest
```

### 2. 安装

外置 PostgreSQL 推荐用 Secret：

```bash
kubectl create namespace axonhub
kubectl -n axonhub create secret generic axonhub-db \
  --from-literal=dsn='Host=postgres.example;Port=5432;Database=axonhub;Username=axonhub;Password=...'

helm upgrade --install axonhub deploy/helm \
  --namespace axonhub \
  --set axonhub.image.repository=ghcr.io/geffzhang/axonhub.net \
  --set axonhub.image.tag=0.1.0 \
  --set axonhub.database.existingSecret=axonhub-db
```

或者直接使用 chart 内置 PostgreSQL（仅单 primary，适合开发/单节点）：

```bash
helm upgrade --install axonhub deploy/helm \
  --namespace axonhub --create-namespace \
  --set postgresql.auth.password='change-me'
```

`deploy/helm/values-production.yaml` 是生产模板，默认使用外置数据库 Secret、
Ingress、HPA 和 PDB。

### 3. 多副本与 Orleans

应用本身没有本地状态（配置来自 ConfigMap/环境变量，业务数据在 PostgreSQL），
因此 chart 不创建应用数据 PVC；只有内置 PostgreSQL 使用 PVC。

当 `axonhub.replicaCount > 1` 或 `autoscaling.enabled=true` 时，chart 自动注入：

```text
ORLEANS_CLUSTER_ENABLED=true
ORLEANS_CLUSTER_ID=axonhub
ORLEANS_CLUSTER_SERVICE_ID=axonhub
ORLEANS_CLUSTER_SILO_NAME=<pod name>
ORLEANS_CLUSTER_ADVERTISED_IP=<pod IP>
ORLEANS_CLUSTER_SILO_PORT=11111
ORLEANS_CLUSTER_GATEWAY_PORT=0
```

所有副本必须共享同一个 PostgreSQL，并且 `cluster_id`/`service_id` 保持一致。
启动时会幂等创建 `orleansmembershiptable`、`orleansreminderstable` 和
`orleansquery`。Pod 之间需要能直连 11111；Service/Ingress 不需要 sticky
session。

检查集群：

```bash
kubectl -n axonhub exec axonhub-postgresql-0 -- \
  psql -U axonhub -d axonhub -c \
  'select siloname, status from orleansmembershiptable;'
```

不要把 `advertised_ip` 配成 Service ClusterIP；chart 使用 Pod IP。

### 4. Ingress

Nginx Ingress 至少需要以下注解（`values-production.yaml` 已包含）：

```yaml
nginx.ingress.kubernetes.io/proxy-body-size: "100m"
nginx.ingress.kubernetes.io/proxy-read-timeout: "600"
nginx.ingress.kubernetes.io/proxy-send-timeout: "600"
nginx.ingress.kubernetes.io/proxy-buffering: "off"
```

SSE 和 Responses WebSocket 在代理关闭缓冲后工作；Ingress 不支持 WebSocket
时，Responses 仍可使用 HTTP 传输。

### 5. 升级与回滚

```bash
helm upgrade axonhub deploy/helm -n axonhub -f deploy/helm/values-production.yaml
helm rollback axonhub -n axonhub
```

滚动更新使用 `maxUnavailable: 0`，并给旧 Pod 60 秒优雅退出时间。Orleans
membership 会把离开的 silo 标记为 dead，grain 和 reminder 在存活 silo
重新激活。

## 与 Compose 的关系

本目录不重复维护 Compose 编排。本地一体化启动仍使用仓库根目录：

```bash
docker compose up --build
```

Compose 内置 PostgreSQL、前端和只读根文件系统；Helm 面向 Kubernetes，
二者共享同一个 `Dockerfile` 和 `config.example.yml` 配置语义。

## 故障排查

1. 确认 PostgreSQL 可达且账号能建表；
2. `config get server.port` 能否读到 `config.yml`（CLI 与宿主现在使用同一套
   YAML 配置源）；
3. 单实例启动失败先看 `axonhub.log`，不要先启用 Orleans 集群；
4. 多副本时检查 membership 表、Pod IP 路由和 NetworkPolicy；
5. Helm 安装用 `helm template` / `helm lint` 先本地渲染，再 `helm test`。
