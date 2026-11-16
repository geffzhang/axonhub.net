# §281 Docker 镜像内置管理界面

## 目标

把管理面前端纳入 Docker 镜像，不再需要运行时挂载 `frontend/dist`。

## Dockerfile

新增 Node 构建阶段：

```dockerfile
FROM node:24-alpine AS frontend
WORKDIR /src/frontend
RUN npm install --global pnpm@10.34.5
COPY frontend/package.json frontend/pnpm-lock.yaml frontend/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY frontend/ ./
RUN pnpm build
```

运行阶段：

```dockerfile
COPY --from=frontend /src/frontend/dist ./frontend/dist
```

`Program.cs` 的静态目录默认是 content root 下的 `frontend/dist`，容器
content root 为 `/app`，因此最终路径为 `/app/frontend/dist`。

`.dockerignore` 增加 `frontend/node_modules/` 与 `frontend/dist/`，避免把
本机依赖和构建产物打进构建上下文；前端在镜像内重新安装与构建。

## 启动路由修复

镜像冒烟时发现 `/v1/chat/completions` 直接注册
`ChatCompletionEndpoint.HandleAsync` 方法组，Minimal API 无法推断其可选参数
`preReadRequest`、`streamWriter`、`nilResultIsInternalError`、`safeguards`，
导致路由匹配器初始化失败（所有请求 500）。改为显式 lambda，只绑定
`HttpContext` 与 `ChatCompletionOrchestrator`，其余参数走方法默认值。

## 验证

- 本地前端：`pnpm install --frozen-lockfile`、`pnpm build` 成功，
  `frontend/dist` 约 24.7 MB。
- `docker build -t axonhub.net:frontend-test .` 成功，镜像约 303 MiB。
- 与 PostgreSQL 17 容器同网络启动：
  - `GET /health` → 200；
  - `GET /` → 200，返回 4811 字节 index.html，包含 `<div id="root">`；
  - 说明 SPA fallback 正确读取镜像内 `/app/frontend/dist`。
- 测试容器与网络已清理。

## 文档

README 的 Docker 段落改为镜像自动构建并包含管理界面；前端限制说明改为
“构建阶段需要访问 npm registry”，不再说镜像只含 API。

## 边界

- 镜像构建需要网络访问 npm registry；运行镜像不依赖 Node。
- 仍可用 `Server:Static:Root` 或挂载目录覆盖内置界面。
- 未新增 CI Docker 构建 job；当前由本地镜像冒烟锁定。
