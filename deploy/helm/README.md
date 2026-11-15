# AxonHub.NET Helm chart

部署 ASP.NET Core + Orleans 版 AxonHub。`Chart.yaml` 的 `appVersion` 只是镜像
标签默认值；发布时建议固定 `axonhub.image.tag`。

## 最小安装

```bash
helm upgrade --install axonhub . \
  --namespace axonhub --create-namespace \
  --set postgresql.auth.password='change-me'
```

内置 PostgreSQL 是单 primary StatefulSet，只适合开发或单节点集群。生产请用
外置数据库和 Secret：

```bash
kubectl -n axonhub create secret generic axonhub-db \
  --from-literal=dsn='Host=pg.example;Port=5432;Database=axonhub;Username=axonhub;Password=...'
helm upgrade --install axonhub . -n axonhub \
  --set postgresql.enabled=false \
  --set axonhub.database.existingSecret=axonhub-db
```

## 关键 values

| Value | 说明 |
| --- | --- |
| `axonhub.replicaCount` | 副本数；大于 1 自动启用 Orleans cluster |
| `axonhub.image.repository/tag` | .NET API 镜像 |
| `axonhub.database.dsn` | Npgsql key-value DSN；空则使用内置 PostgreSQL |
| `axonhub.database.existingSecret` | 含 `ConnectionStrings__Default` 的 Secret |
| `axonhub.cluster.enabled` | 单副本也想加入已有集群时显式打开 |
| `axonhub.cluster.clusterId/serviceId` | 必须与所有副本一致 |
| `axonhub.cluster.siloPort` | Pod 间 Orleans 端口，默认 11111 |
| `axonhub.config.content` | 原始 `config.yml`，挂载到 `/app/config.yml` |
| `autoscaling.enabled` | 打开后也会自动启用 Orleans cluster |
| `postgresql.enabled` | 内置 PostgreSQL；replicaCount 必须为 1 |

## 多副本

多副本时 chart 自动设置 `ORLEANS_CLUSTER_*`，包括 Pod 名和 Pod IP：

- `SILO_NAME` = `metadata.name`
- `ADVERTISED_IP` = `status.podIP`
- `GATEWAY_PORT` = 0
- membership/reminder 表与业务表共用 `axonhub.database` 指向的 PostgreSQL

不要用 Service ClusterIP 作为 advertised IP。Pod 之间需允许 11111 端口。

## 探针

`/health` 只表示 HTTP 宿主可用。启动探针给数据库迁移留出时间；readiness
不检查 membership，因此滚动更新时请保留 60 秒 `terminationGracePeriodSeconds`
并观察 `orleansmembershiptable`。

## 测试

```bash
helm lint .
helm template axonhub . > /tmp/axonhub.yaml
helm test axonhub -n axonhub
```
