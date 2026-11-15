# AxonHub.NET 中文文档索引

AxonHub.NET 是 AxonHub 的 .NET 10 + ASP.NET Core + Orleans 移植版，
保留相同的 OpenAI / Anthropic / Gemini 兼容 API、管理面和管理界面。

## 🚀 快速入门

| 文档 | 描述 |
| --- | --- |
| [快速开始](getting-started/quick-start.md) | 用 Docker Compose 或本地 SDK 启动，并发出第一个请求 |
| [配置](getting-started/configuration.md) | `config.yml`、环境变量、数据库、端口与热重载 |
| [Kubernetes 多副本](getting-started/kubernetes.md) | Orleans AdoNet cluster、reminder 与故障转移 |
| [部署脚本与 Helm](../../deploy/README.md) | 本地安装、systemd/Nginx、Windows、Helm chart |
| [请求处理流程](getting-started/request-processing.md) | 入站协议、模型选择、负载均衡、重试与响应转换 |

## 📖 使用指南

以下内容描述的产品行为与上游 AxonHub 一致，改编自上游中文文档。

| 文档 | 描述 |
| --- | --- |
| [渠道管理](guides/channel-management.md) | 配置和管理 AI 提供商渠道 |
| [模型管理](guides/model-management.md) | 模型映射、关联和优先级 |
| [API Key 配置文件](guides/api-key-profiles.md) | API Key 的模型与渠道配置文件 |
| [权限管理](guides/permissions.md) | RBAC、作用域和项目隔离 |
| [负载均衡](guides/load-balance.md) | 自适应选路与故障转移 |
| [成本追踪](guides/cost-tracking.md) | usage、价格和成本明细 |
| [请求追踪](guides/tracing.md) | 请求、线程和追踪链路 |
| [请求覆盖](guides/request-override.md) | body/header 重写与覆盖参数 |
| [安全功能](guides/security.md) | IP 访问控制、API Key IP 限制 |
| [提示词保护规则](guides/prompt-protection-rules.md) | 敏感信息过滤 |
| [OIDC 集成](guides/oidc.md) | Google/GitHub/Logto 等 OIDC 登录 |
| [Claude Code 集成](guides/claude-code-integration.md) | Claude Code 接入 |
| [Codex 集成](guides/codex-integration.md) | Codex 接入 |
| [OpenCode 集成](guides/opencode-integration.md) | OpenCode 接入 |

## 🔌 API 参考

| 文档 | 描述 |
| --- | --- |
| [OpenAI API](api-reference/openai-api.md) | Chat、Responses、Completions、Embeddings、Images、Audio、Video |
| [Anthropic API](api-reference/anthropic-api.md) | Messages、工具、流式与 cache_control |
| [Gemini API](api-reference/gemini-api.md) | 原生 `generateContent` / 流式 |
| [图像生成 API](api-reference/image-generation.md) | Images 的生成、编辑和引用 |
| [嵌入 API](api-reference/embedding-api.md) | OpenAI Embeddings |
| [重排序 API](api-reference/rerank-api.md) | Jina Rerank |
| [System One API](api-reference/systemone-api.md) | TypeSafe / 决策接口 |

这些 API 参考来自上游文档，示例中的语言可能以 Go 为主；协议、路径和错误
语义与端口一致。部署与 .NET 侧配置请以本仓库的快速开始和配置文档为准。

## 🛠️ 开发与移植

| 文档 | 描述 |
| --- | --- |
| [架构](development/architecture.md) | 六个 .NET 项目、Orleans grain、EF/Dapper、转换管线 |
| [开发指南](development/development.md) | 本地构建、运行、前端、代码组织 |
| [测试](development/testing.md) | Llm/Grains/Persistence/Api、真实 provider E2E、CI |
| [移植台账](development/porting.md) | 参照物锁定、差异编号、验收与限制 |
| [转换流程](development/transformation-flow.md) | 双向转换管线的组件与数据流 |
| [实体关系图](development/erd.md) | 25 张表的数据域与关系 |

## ❓ 常见问题

| 文档 | 描述 |
| --- | --- |
| [FAQ](faq/faq.md) | 数据库、配置、Docker、测试与移植边界 |

## 🔗 相关链接

- [AxonHub.NET README](../../README.md)
- [上游 AxonHub](https://github.com/looplj/axonhub)
- [移植验收账](../../porting/03-acceptance.md)
- [差异台账](../../porting/02-differences.md)
