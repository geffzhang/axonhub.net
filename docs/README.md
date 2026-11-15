# AxonHub.NET 文档

AxonHub.NET 是 [looplj/axonhub](https://github.com/looplj/axonhub) 的
.NET 10 + Orleans 行为等价移植。文档分为两类：

- **产品使用文档**：渠道、模型、API、追踪、成本、权限等；这些行为与上游
  AxonHub 一致，内容改编自上游中文文档（Apache-2.0）。
- **移植与开发文档**：.NET 架构、配置、测试、差异台账与本地开发流程；这些
  内容只适用于本仓库。

## 快速导航

| 文档 | 说明 |
| --- | --- |
| [中文文档索引](zh/index.md) | 全部中文文档导航 |
| [快速开始](zh/getting-started/quick-start.md) | Compose 启动、初始化、第一个请求 |
| [配置](zh/getting-started/configuration.md) | `config.yml`、环境变量、数据库与端口 |
| [请求处理流程](zh/getting-started/request-processing.md) | 从入站协议到上游的完整链路 |
| [架构](zh/development/architecture.md) | .NET 项目、Orleans、EF/Dapper、Pipeline |
| [开发指南](zh/development/development.md) | 构建、前端、运行与代码组织 |
| [测试](zh/development/testing.md) | 单元、持久化、真实 provider E2E、CI |
| [移植台账](zh/development/porting.md) | 参照物、差异编号、验收口径 |
| [FAQ](zh/faq/faq.md) | 数据库、配置、部署与移植边界 |
| [API 参考](zh/api-reference/openai-api.md) | OpenAI / Anthropic / Gemini / Images 等 |

上游文档中的语言示例以 Go 为主；本仓库的部署、配置和开发文档以 .NET 实现
为准。两边的协议行为由差异台账和测试绑定。文档来源与许可见
[NOTICE](NOTICE.md)。英文产品文档暂未复制，可参考
[上游 docs/en](https://github.com/looplj/axonhub/tree/unstable/docs/en)。
