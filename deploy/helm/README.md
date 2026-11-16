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

## 多副本一致性

集群模式不需要 Redis，但所有副本必须连接同一业务 PostgreSQL：

- API Key、系统配置、供应商配额及存储配置按需读库，避免其他 Pod 长期持有旧值；
  渠道快照在读取时校验数据库版本，版本随渠道/模型写事务一起提交。
- `maxConcurrent` 和 FIFO 等待队列是整个集群的总量，不再按副本数相乘；
  负载打分和并发指标读取同一份共享占位。缩小上限不取消已经获准的请求。
- OIDC state、PKCE、绑定状态和 exchange code 使用 PostgreSQL TTL 存储并原子消费，
  OIDC 登录不需要 sticky session；消费后失败需重新开始登录。
- 数据库不可用时拒绝新的协调操作，不回退到本地缓存或本地准入。

**安全优先**：失联请求的已获准占位不自动到期，也不因 Pod 重启、删除渠道或
修改并发上限而清除。尚未获准的等待票据每次轮询续期，30 秒无续期后可回收。
这可能导致故障后可用容量减少，是避免不确定旧请求与新请求重叠的有意取舍。
网关关闭连接不等于上游计算已停止；不承诺上游 exactly-once。

用相同数据库配置在运行容器中检查占位；只有确认对应上游操作已停止后才释放：

```bash
dotnet AxonHub.Api.dll admission list
dotnet AxonHub.Api.dll admission release <permit-id> --confirm-upstream-stopped
```

不要直接清空占位表。首次升级至此方案须先停止入口流量并排空旧版本请求，再整体
切换所有副本；旧版使用本地并发限制，混合版本滚动期间不能保证全局上限。
切回单 silo 前同样须排空请求并处理遗留占位。

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
