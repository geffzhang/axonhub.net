# §285 部署资产（deploy 移植）

## 目标

把上游 `E:\GitHub\axonhub\deploy` 的部署形态搬到 axonhub.net，并按本项目
架构改写，而不是照抄 Go 二进制发布脚本：

- 端口只支持 PostgreSQL 17；
- 没有 GitHub release 单二进制，安装从源码 `dotnet publish`；
- 管理面前端需要随发布产物安装；
- 多副本依赖 Orleans AdoNet membership + durable reminder；
- 容器镜像仍由仓库根 `Dockerfile` 构建。

## 本地脚本

新增 `deploy/install.sh` 与 `deploy/install.ps1`：

- 用 `dotnet publish -c Release -r <rid> --self-contained true
  -p:PublishSingleFile=true` 生成自包含单文件；
- 安装到 `/usr/local/bin/axonhub`（Unix）或
  `%LOCALAPPDATA%\AxonHub\axonhub.exe`（Windows）；
- 复制 `frontend/dist` 到配置目录的 `frontend/dist`；
- 首次安装生成 `config.yml`，已存在则保留；
- 配置包含 PostgreSQL DSN、PID 文件、`orleans_cluster` 关闭态；
- 中文/英文提示明确“SQLite/MySQL 不支持”。

`start/stop/restart` 保留 PID、日志、端口检测与优雅停止；Unix 下 root
调用时以 `SUDO_USER` 身份发布和运行，避免仓库 `obj/bin` 被 root 持有。
`upgrade` 默认要求工作树干净后 `git pull --ff-only`，再重发布并重启；
`--no-pull` 允许直接重建当前工作树。Windows 版本对应 PowerShell。

`setup.sh` 生成 systemd user service（必要时回退 system），macOS 生成
launchd agent；`setup.ps1` 使用计划任务，失败回退启动文件夹。新增
`axonhub.service` 作为 `/opt/axonhub` 布局的系统服务示例。

## Nginx

`deploy/nginx.conf` 针对 .NET 路由与传输改写：

- `/v1/` 关闭 `proxy_buffering` / `proxy_request_buffering`，保留 600s
  读写超时；
- 使用 `$connection_upgrade` 支持 `/v1/responses` WebSocket；
- `/admin/graphql`（HotChocolate）替代上游 `/query`；
- `/health` 代理真实后端，不再静态返回 200；
- `client_max_body_size 100m`，覆盖音频/图片上传；
- 静态资源缓存、私有网段 `/metrics`、敏感文件拒绝保持。

## Helm chart

`deploy/helm`：

- `axonhub` Deployment + Service，镜像默认 `axonhub.net:latest`；
- 自动 Orleans 开关：`replicaCount > 1` 或 `autoscaling.enabled=true` 时
  注入 `ORLEANS_CLUSTER_ENABLED/ID/SERVICE_ID/SILO_NAME/ADVERTISED_IP/
  SILO_PORT/GATEWAY_PORT`；
  - SILO_NAME 来自 `metadata.name`；
  - ADVERTISED_IP 来自 `status.podIP`；
  - gateway 默认 0；
- PostgreSQL 17 内置 StatefulSet（单 primary，replicaCount 必须为 1）或
  外置 DSN/Secret；`database.existingSecret` 映射
  `ConnectionStrings__Default`；
- 可选 `axonhub.config.content` 生成 ConfigMap，挂载
  `/app/config.yml`；
- 只读根文件系统、drop ALL、非 root、`/tmp` emptyDir、
  startup/liveness/readiness probe；
- 多副本默认软主机反亲和，PDB、HPA、Ingress 模板；
- `values-production.yaml` 使用外置数据库 Secret、Ingress、HPA、PDB；
- `NOTES.txt` 显示访问方式、Orleans 状态和数据库提示；
- `install.sh`/`install.bat` 改为 `helm upgrade --install` 并支持额外
  `--set/-f` 参数。

## CLI 修正

部署脚本需要从 `config.yml` 读取 `server.port`。原
`CliConfiguration.Load()` 只读 appsettings/环境变量，与宿主不一致。
现在 CLI 与宿主使用同一套 YAML 配置源，顺序为
`config.yml → appsettings → 环境变量`；新增
`CliLoadsConfigYmlForDeploymentScripts` 测试。

## 文档

- 新增 `deploy/README.md`：本地安装、配置、systemd、Windows、Nginx、
  Helm、多副本和故障排查；
- 新增 `deploy/helm/README.md`：values、多副本、探针与测试；
- README、docs 索引、快速开始、Kubernetes 文档增加 deploy/Helm 入口。

## 验证

- `bash -n`：6 个 deploy shell + 1 个 Helm install，全部通过；
- PowerShell parser：6 个 `.ps1` 全部通过，`--help` 冒烟通过；
- `helm lint deploy/helm` 通过；
- `helm template` 默认 values、双副本、生产 values 均通过；
  双副本渲染包含 Orleans 环境变量、11111 端口和软反亲和；
- `CliCommandTests` 41/41（含新增部署配置测试）；
- 未执行真实 `dotnet publish`、本地安装、Kubernetes apply 或镜像重建；
  这些需要在目标主机/集群上验收。

## 边界

- 本地脚本只从源码发布，不实现 GitHub release 下载（项目尚无 release
  资产流水线）；
- 内置 PostgreSQL 不支持多副本/流复制；
- Helm 的 `/health` readiness 不代表 Orleans membership 健康，生产需
  观察 membership 表或另加自定义探针；
- Nginx 配置默认 TLS 证书路径，实际部署需提供证书；
- 没有改动 Dockerfile / Compose；二者仍是本地容器路径的权威。
