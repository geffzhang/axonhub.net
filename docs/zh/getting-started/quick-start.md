# 快速开始

本指南用 Docker Compose 启动一个包含 PostgreSQL、API 和管理界面的
AxonHub.NET 本地实例，然后创建渠道、API Key 并发出第一个请求。

## 前置条件

- Docker Desktop / Docker Engine + Compose v2
- 一个可用的上游 AI 提供商 API Key（OpenAI、Anthropic、Gemini 等）
- 若要本地开发：.NET SDK 10.0.4xx、Node.js 24 + pnpm 10

## 1. 启动服务

在仓库根目录执行：

```bash
docker compose up --build
```

Compose 会：

- 启动 `postgres:17-alpine` 并等待健康检查；
- 从本仓库 Dockerfile 构建 API 镜像（镜像内已包含管理界面）；
- 把 `config.example.yml` 挂载为容器内的 `/app/config.yml`；
- 在 `127.0.0.1:8090` 暴露服务。

启动完成后打开 <http://127.0.0.1:8090/>。首次启动需要按页面向导创建
管理员、项目和第一个 API Key。

默认数据库密码仅用于本机回环地址。需要暴露到局域网或修改密码时：

```bash
DB_PASSWORD='change-me' AXONHUB_BIND_ADDRESS=0.0.0.0 docker compose up --build
```

停止服务：

```bash
docker compose down       # 保留 PostgreSQL 数据卷
docker compose down -v    # 同时删除数据卷
```

## 2. 初始化、渠道与 API Key

1. 打开管理界面，完成初始化向导；
2. 进入 **Channels（渠道）**，选择提供商，填写上游 Base URL 与 API Key；
3. 配置支持的模型和默认测试模型，先 **Test** 再启用渠道；
4. 进入 **API Keys（API 密钥）** 创建客户端 Key，复制 `ah-...`。

渠道、模型映射、覆盖参数等细节见[渠道管理](../guides/channel-management.md)
和[模型管理](../guides/model-management.md)。

## 3. 发出第一个请求

AxonHub.NET 接受 OpenAI、Anthropic、Gemini 兼容协议。使用官方 SDK 时只需
修改 Base URL 和 API Key。

### OpenAI SDK

```python
from openai import OpenAI

client = OpenAI(
    base_url="http://localhost:8090/v1",
    api_key="ah-...",  # 在管理面创建的 AxonHub Key
)

reply = client.chat.completions.create(
    model="gpt-4o",
    messages=[{"role": "user", "content": "Hello, AxonHub!"}],
)
print(reply.choices[0].message.content)
```

### Anthropic SDK

```python
from anthropic import Anthropic

client = Anthropic(
    base_url="http://localhost:8090",
    api_key="ah-...",
)

message = client.messages.create(
    model="claude-sonnet-4-5",
    max_tokens=256,
    messages=[{"role": "user", "content": "Hello, Claude!"}],
)
print(message.content[0].text)
```

### curl

```bash
curl http://localhost:8090/health

curl http://localhost:8090/v1/chat/completions \
  -H "Authorization: Bearer ah-..." \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-4o",
    "messages": [{"role": "user", "content": "Hello"}]
  }'
```

### 跨协议调用

同一个 API Key 可以用 OpenAI 协议调用 Anthropic/Gemini 渠道，也可以用
Anthropic 协议调用 OpenAI 渠道；转换由入站/出站 transformer 完成。详细链路
见[请求处理流程](request-processing.md)。

## 4. 配置

配置来源优先级（高到低）：

1. 命令行参数
2. 环境变量（如 `ConnectionStrings__Default`、`AXONHUB_SERVER_*`）
3. `appsettings.json` / `appsettings.{Environment}.json`
4. `config.yml`

Compose 已通过环境变量设置 PostgreSQL 连接和 `8090` 端口；其余服务器、CORS、
trace、GC 和 metrics 设置来自挂载的 `config.example.yml`。

复制示例以建立本地独立配置：

```bash
cp config.example.yml config.yml
# 修改 docker-compose.yml 的 volume 为 ./config.yml:/app/config.yml:ro
docker compose up -d
```

Linux 上向容器发送 `SIGHUP` 可重新读取 `config.yml`；本地运行时会从
Content Root 或当前工作目录（通常是仓库根目录）查找该文件。完整键说明见
[配置](configuration.md)。

## 5. 本地开发（不使用 Compose）

```bash
docker run -d --name axonhub-postgres \
  -e POSTGRES_USER=axonhub \
  -e POSTGRES_PASSWORD=axonhub \
  -e POSTGRES_DB=axonhub \
  -p 5432:5432 postgres:17-alpine

export ConnectionStrings__Default="Host=localhost;Port=5432;Database=axonhub;Username=axonhub;Password=axonhub"
export ASPNETCORE_HTTP_PORTS=8090
dotnet run --project src/AxonHub.Api
```

前端开发：

```bash
cd frontend
corepack enable
pnpm install --frozen-lockfile
VITE_API_URL=http://localhost:8090 pnpm dev
```

开发、测试和调试的完整说明见[开发指南](../development/development.md)。

## 下一步

- [配置](configuration.md)：数据库、端口、CORS、trace、GC
- [渠道管理](../guides/channel-management.md)：渠道类型、模型、测试
- [负载均衡](../guides/load-balance.md)：多渠道路由和故障转移
- [成本追踪](../guides/cost-tracking.md)：价格与 usage
- [API 参考](../api-reference/openai-api.md)：协议细节
