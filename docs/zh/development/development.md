# 开发指南

## 前置要求

- .NET SDK 10.0.4xx
- PostgreSQL 17（或 Docker/Testcontainers）
- Node.js 24 + pnpm 10（仅前端）
- 可选：Docker（持久化测试、镜像构建）

## 获取代码与构建

```bash
git clone https://github.com/geffzhang/axonhub.net.git
cd axonhub.net
dotnet restore AxonHub.slnx
dotnet build AxonHub.slnx
```

解决方案包含六个源码项目、四个测试项目和一个语料运行器，结构见
[架构](architecture.md)。

## 启动后端

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

也可以直接 `docker compose up --build` 启动完整栈。配置优先级与
`config.yml` 说明见[配置](../getting-started/configuration.md)。

## 前端

```bash
cd frontend
corepack enable
pnpm install --frozen-lockfile
pnpm dev
```

Vite 默认监听 5173，代理目标默认 `http://localhost:8090`；可用
`VITE_API_URL` 覆盖。生产构建：

```bash
pnpm build   # 输出 frontend/dist，由 API 的 SPA fallback 提供
```

前端来自上游 AxonHub（React 19 + TypeScript + TanStack Router/Query +
Zustand + Tailwind），产品行为与后端协议保持一致。

## 代码组织

- `src/AxonHub.Llm/Transformer/<Provider>/`：各 provider 的入站/出站转换；
- `src/AxonHub.Llm/Pipeline/`：重试、超时、流式状态机、空响应检测；
- `src/AxonHub.Api/Orchestration/`：模型选择、渠道选择、请求编排；
- `src/AxonHub.Api/Endpoints/`：HTTP 端点；
- `src/AxonHub.Api/GraphQL/`：HotChocolate 管理面；
- `src/AxonHub.Persistence/Stores/`：Dapper 查询与 EF 写入；
- `src/AxonHub.Grains/`：Orleans grain。

## 新增渠道类型

与 Go 参照的步骤对应到端口：

1. 在 `ChannelOutboundFactory` 增加类型常量与分派；
2. 在 `ChannelEndpoints` / `DefaultEndpoints` 配置默认端点（如适用）；
3. 在 `AxonHub.Llm/Transformer/` 实现或复用 outbound transformer；
4. 在凭据校验、API key provider、默认 base URL 逻辑中补齐分支；
5. 增加通道接线、请求/响应、流式和错误测试；
6. 若前端渠道类型枚举需要展示，更新 `frontend/src/features/channels/data/`
   与 locale；
7. 在差异台账登记无法与参照一一对应的行为。

端口没有 Ent/gqlgen 代码生成步骤：数据库 schema 是
`Persistence/DatabaseSchema.cs` 的幂等 DDL，GraphQL 是 HotChocolate
code-first。

## 测试

```bash
dotnet test tests/AxonHub.Llm.Tests/AxonHub.Llm.Tests.csproj
dotnet test tests/AxonHub.Grains.Tests/AxonHub.Grains.Tests.csproj
dotnet test tests/AxonHub.Persistence.Tests/AxonHub.Persistence.Tests.csproj
dotnet test tests/AxonHub.Api.Tests/AxonHub.Api.Tests.csproj
```

持久化测试需要 Docker（Testcontainers 启动 `postgres:17-alpine`）。Api 全量
约 35–45 分钟，CI 中按 nightly 运行。真实 provider E2E 与 CI 说明见
[测试](testing.md)。

## 移植纪律

- 修改前先运行 `reference_lock.py check`，参照物漂移先决策；
- 行为差异必须登记 `C###`，附现象、原因、决策和测试；
- 不把“编译通过”当作完成，线路形状、错误文案和流式顺序都要测试；
- 修改数据层时同时考虑 EF 模型、DDL 和 Dapper 查询。

详见[移植台账](porting.md)与
[`porting/00-reference.md`](../../../porting/00-reference.md)。
