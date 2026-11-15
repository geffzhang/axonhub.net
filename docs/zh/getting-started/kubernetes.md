# Kubernetes 多副本部署

默认部署是 API 进程内的单 silo：localhost clustering + 内存 reminder。多副本
时必须打开 Orleans 集群开关，否则每个 Pod 都是一个独立集群，`/health` 可能
正常，但 grain 状态、限流和 reminder 会各算各的。

## Helm（推荐）

仓库的 [`deploy/helm`](../../../deploy/helm/README.md) 已封装 PostgreSQL、
Ingress、HPA、PDB 和 Orleans 开关。多副本或 HPA 会自动注入
`ORLEANS_CLUSTER_*`，无需手写 Deployment：

```bash
docker build -t ghcr.io/your-org/axonhub.net:0.1.0 .
docker push ghcr.io/your-org/axonhub.net:0.1.0

kubectl create namespace axonhub
kubectl -n axonhub create secret generic axonhub-db \
  --from-literal=dsn='Host=pg.example;Port=5432;Database=axonhub;Username=axonhub;Password=...'

helm upgrade --install axonhub deploy/helm \
  --namespace axonhub \
  --set axonhub.image.repository=ghcr.io/your-org/axonhub.net \
  --set axonhub.image.tag=0.1.0 \
  --set axonhub.database.existingSecret=axonhub-db
```

只想快速试用内置 PostgreSQL 时：

```bash
helm upgrade --install axonhub deploy/helm \
  --namespace axonhub --create-namespace \
  --set postgresql.auth.password='change-me'
```

内置 PostgreSQL 是单 primary，生产环境应使用外置高可用数据库。下面的手写
Deployment 用于理解注入的字段，或在不使用 Helm 时参考。

## 打开集群开关

集群模式使用 **PostgreSQL AdoNet membership + AdoNet reminder**，所有副本
共享同一个数据库：

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
  gateway_port: 0               # 没有外部 Orleans 客户端时关闭
```

对应环境变量（推荐在 Deployment 中设置）：

| 环境变量 | 说明 |
| --- | --- |
| `ORLEANS_CLUSTER_ENABLED=true` | 打开 AdoNet cluster |
| `ORLEANS_CLUSTER_ID=axonhub` | 所有副本一致；更换它等于新建集群 |
| `ORLEANS_CLUSTER_SERVICE_ID=axonhub` | 所有副本一致 |
| `ORLEANS_CLUSTER_SILO_NAME` | 建议使用 Pod 名（`metadata.name`） |
| `ORLEANS_CLUSTER_ADVERTISED_IP` | 建议使用 `status.podIP`；不要用 Service ClusterIP |
| `ORLEANS_CLUSTER_SILO_PORT` | 默认 11111，Pod 之间必须可路由 |
| `ORLEANS_CLUSTER_GATEWAY_PORT` | 默认 30000；无外部 Orleans 客户端可设 0 |
| `ORLEANS_CLUSTER_CONNECTIONSTRING` | 可选，默认复用 `ConnectionStrings__Default` |

启动时应用会自动创建 `orleansquery`、membership 和 reminder 表；DDL 使用
PostgreSQL advisory lock 串行化，多个 Pod 同时启动是安全的。数据库账号需要
建表/函数权限。

## 参考 Deployment

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: axonhub
spec:
  replicas: 3
  selector:
    matchLabels: { app: axonhub }
  template:
    metadata:
      labels: { app: axonhub }
    spec:
      terminationGracePeriodSeconds: 60
      containers:
        - name: axonhub
          image: ghcr.io/example/axonhub.net:latest
          ports:
            - { name: http, containerPort: 8090 }
            - { name: orleans, containerPort: 11111 }
          env:
            - name: ASPNETCORE_HTTP_PORTS
              value: "8090"
            - name: ConnectionStrings__Default
              valueFrom:
                secretKeyRef: { name: axonhub-db, key: dsn }
            - name: ORLEANS_CLUSTER_ENABLED
              value: "true"
            - name: ORLEANS_CLUSTER_ID
              value: axonhub
            - name: ORLEANS_CLUSTER_SERVICE_ID
              value: axonhub
            - name: ORLEANS_CLUSTER_SILO_NAME
              valueFrom:
                fieldRef: { fieldPath: metadata.name }
            - name: ORLEANS_CLUSTER_ADVERTISED_IP
              valueFrom:
                fieldRef: { fieldPath: status.podIP }
            - name: ORLEANS_CLUSTER_SILO_PORT
              value: "11111"
            - name: ORLEANS_CLUSTER_GATEWAY_PORT
              value: "0"
          readinessProbe:
            httpGet: { path: /health, port: http }
            periodSeconds: 10
          livenessProbe:
            httpGet: { path: /health, port: http }
            initialDelaySeconds: 30
            periodSeconds: 20
          resources:
            requests: { cpu: 250m, memory: 512Mi }
            limits: { cpu: "2", memory: 2Gi }
---
apiVersion: v1
kind: Service
metadata:
  name: axonhub
spec:
  selector: { app: axonhub }
  ports:
    - { name: http, port: 80, targetPort: http }
```

要点：

- `ORLEANS_CLUSTER_ADVERTISED_IP` 必须是 **Pod IP**，不是 Service IP；silo 之间直连
  Pod 网络的 11111 端口。
- 没有外部 Orleans 客户端时把 `gateway_port` 设为 0，减少暴露面。
- `/health` 只表示 HTTP 进程健康，不代替 Orleans membership 检查；需要更强
  的 readiness 时可额外检查 membership 日志或数据库 `orleansmembershiptable`。
- 建议配置 Pod anti-affinity，避免所有 silo 落在同一节点。
- 如果 NetworkPolicy 默认拒绝流量，需要允许 Pod 之间访问 11111。
- 滚动更新时保持相同的 `cluster_id`/`service_id`，新 Pod 会加入现有集群；
  优雅停止时 Orleans 会把离开的 silo 标记为 dead，grain 和 reminder 重新分配。
- 不要在不同副本上使用不同的 `cluster_id`、`service_id` 或数据库。

## 故障转移语义

- **membership**：AdoNet membership 记录每个 silo 的心跳；失联 silo 被标记
  dead 后，placement 不再分配新激活。
- **grain**：这些 grain 的状态主要来自数据库/进程内缓存；激活丢失后会在存活
  silo 重新激活并从数据库重建。Channel limiter 的瞬时窗口状态会丢失，和
  参照实现进程重启的语义一致。
- **reminders**：AdoNet reminder 表持久化；拥有 reminder 的 silo 离开后，
  另一个 silo 接管并继续触发。错过的一次 occurrence 不会回补（与参照 cron
  一致）。
- **HTTP**：每个 Pod 都可以接收请求；Service/Ingress 无需 sticky session，
  grain 调用会在集群内路由。

## 单 silo 回退

保持 `orleans_cluster.enabled: false`（默认）即可回到 localhost clustering
与内存 reminder，和当前单机行为一致；该模式不支持多副本。
