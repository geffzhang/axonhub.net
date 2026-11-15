# §284 Orleans 多 silo 集群开关

## 目标

Kubernetes 多副本部署时，若仍使用 `UseLocalhostClustering`，每个 Pod 会成为
独立集群：grain 状态、限流和 reminder 各算各的。增加一个默认关闭的开关，
把 membership 与 reminder 移到共享 PostgreSQL，同时保持单实例行为不变。

## 配置

配置根为 `orleans_cluster`（环境变量 `ORLEANS_CLUSTER_*`）：

```yaml
orleans_cluster:
  enabled: false
  provider: adonet
  connection_string: ""   # 空 = ConnectionStrings:Default / db.dsn
  cluster_id: "axonhub"
  service_id: "axonhub"
  silo_name: ""           # 默认 HOSTNAME
  advertised_ip: ""       # 默认 POD_IP
  silo_port: 11111
  gateway_port: 30000     # 0 关闭外部 gateway
```

- `OrleansClusterOptions` 校验布尔值、端口、provider 与连接串；启用时要求
  连接串，只接受 `adonet`。
- `ServiceId`/`ClusterId`/`SiloName` 支持配置与环境变量，均 Trim；
  advertised address 支持 IP 或主机名解析，Kubernetes 用 `POD_IP`。
- 配置键特意使用 `OrleansCluster` 而不是 `Orleans:Clustering`，避免与
  Orleans 自身的默认配置绑定冲突。

## 宿主接线

`Program.cs`：

- 默认分支保持 `UseLocalhostClustering()` + `UseInMemoryReminderService()`；
- 启用分支配置 `ClusterOptions`、`SiloOptions`、`EndpointOptions`，
  注册 `UseAdoNetClustering` 与 `UseAdoNetReminderService`（`Npgsql`）；
- 在建 host 后、silo hosted service 启动前，调用
  `OrleansSchema.EnsureCreatedAsync`。

`OrleansSchema` 嵌入 Orleans PostgreSQL AdoNet 脚本，做了幂等化：

- `CREATE TABLE IF NOT EXISTS`、`CREATE OR REPLACE FUNCTION`；
- `OrleansQuery` 插入使用 `ON CONFLICT` 更新 QueryText；
- 整个脚本在一个事务内执行，并以
  `SELECT pg_advisory_xact_lock(7364641)` 串行化多副本同时启动的 DDL；
- 资源名 `AxonHub.Persistence.Sql.Orleans-PostgreSQL.sql`，Persistence 项目
  以 `EmbeddedResource` 打入。

## 文档与示例

- `config.example.yml` 增加默认关闭的 `orleans_cluster`；
- `docs/zh/getting-started/configuration.md` 增加单 silo / 多 silo 段落；
- 新增 `docs/zh/getting-started/kubernetes.md`：参考 Deployment、Pod IP、
  NetworkPolicy、滚动更新与故障转移语义；
- FAQ 的 Orleans 优势补充 opt-in 集群，并明确热点 grain、序列化与数据库
  仍是运维面的代价；
- README 快速开始指向 Kubernetes 文档。

## 故障转移语义

- AdoNet membership 记录每个 silo 的心跳；失联 silo 被标记 dead 后不再向
  其放置新激活。
- grain 持久状态主要来自数据库/进程内缓存；激活丢失后在存活 silo 重建。
- AdoNet reminder 持久化，承载 silo 离开后由其他 silo 接管；错过的一次
  occurrence 不回补（与参照 cron 一致）。
- 每个 Pod 都可接收 HTTP，Service/Ingress 无需 sticky session。

## 验证

- `OrleansClusterOptionsTests` 9/9；YAML + Options 定向 13/13（含
  `orleans_cluster` 键扁平化）；
- `OrleansSchemaTests` 1/1（4 张表、17 条 `OrleansQuery`、reminder key、
  幂等创建）；
- 本机 PostgreSQL 17 + 两个 API 进程：
  - HTTP 8091/8092，silo 11111/11112，gateway 30000/30001，
    advertised IP 127.0.0.1；
  - 两实例 `/health` 均 200；
  - `orleansmembershiptable` 两行 status=3；
  - `orleansreminderstable` 6 行；
  - 强杀 silo-a 后 status 变 6（dead），silo-b `/health` 仍 200。
- 按用户要求未跑全量；新增用例总数：Api 14（Yaml 4、ConfigReload 1、
  Options 9）、Persistence 1，均已定向通过。当前套件总数 11160。
- 本轮未重建 Docker 镜像；默认 Compose 栈此前 health/UI 均 200，默认
  `orleans_cluster.enabled: false` 不受影响。

## 台账

- 新增 C336（accepted）：opt-in 多 silo 使用 AdoNet membership 与 durable
  reminder；
- 修订 C228/C229/C230 中“项目只支持单 silo”的旧口径，明确默认单 silo 与
  opt-in 多 silo 的边界；
- 差异总数 336（191 accepted / 145 resolved / 0 gap），`02-differences.md`
  已重新渲染。

## 边界与后续

- 未实现 SQLite/MySQL 的 Orleans 存储；只提供 PostgreSQL AdoNet。
- 无外部 Orleans 客户端时 gateway 可设 0，但本轮只验证了 gateway 开启的
  两个 silo。
- 未引入 Kubernetes 探针检查 membership；`/health` 仍只表示 HTTP 进程健康。
- 容器镜像与远端 CI 尚未在本切片复测。
