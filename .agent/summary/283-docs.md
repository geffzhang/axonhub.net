# §283 文档中心

## 结构

以 `docs/README.md` 为入口，中文文档放在 `docs/zh/`：

```text
docs/
├── README.md
├── screenshots/                  # 从上游复制的管理面截图
└── zh/
    ├── index.md
    ├── getting-started/
    │   ├── quick-start.md        # Compose/本地启动、初始化、首个请求
    │   ├── configuration.md      # config.yml/环境变量/DB/端口/已支持配置
    │   └── request-processing.md # 产品级请求链路（上游文档）
    ├── guides/                   # 14 篇渠道/模型/负载/成本/追踪/OIDC/工具集成
    ├── api-reference/            # 7 篇 OpenAI/Anthropic/Gemini/Images/Embedding/Rerank/SystemOne
    ├── development/
    │   ├── architecture.md       # .NET 项目、Orleans、EF/Dapper、Pipeline
    │   ├── development.md        # 构建、前端、调试、新增渠道
    │   ├── testing.md            # 四项目测试、E2E、CI、语料
    │   ├── porting.md            # 三本账、差异、边界与新增行为流程
    │   ├── transformation-flow.md
    │   └── erd.md
    └── faq/faq.md
```

## 来源与适配

- 指南与 API 参考改编自上游 `docs/zh/`（Apache-2.0），产品行为与端口一致；
- `quick-start.md`、`configuration.md`、`development/` 与 FAQ 针对 .NET 实现
  重写；
- `oidc.md` 的 `conf/config.yml` 改为本仓库的 `config.yml`；
- 追踪指南删除指向 Go integration_test 的引用，改为本仓库 Api 测试；
- `erd.md` 的事实来源从 Ent schema 改为 `Records.cs` / `AxonHubDbContext.cs` /
  `DatabaseSchema.cs`；
- 文档索引说明 API 文档中的语言示例可能以 Go 为主，部署/配置以 .NET 文档为准。

## 验证

- 相对链接检查：除一个 Go 代码片段被正则误判外，无缺失本地文件；
- 34 篇 Markdown 均非空；
- Compose 文档与 `docker-compose.yml`、`config.example.yml` 一致
  （8090、回环绑定、镜像内置 UI、配置优先级）。

## FAQ 补充

`docs/zh/faq/faq.md` 增加“为什么选用 Orleans？有哪些优势？”：单写者语义替代
进程内锁/缓存、grain 寻址与放置、定时任务生命周期、TestingHost 可测试性、
与 .NET 生态一致；同时明确热点 grain 串行、序列化成本、当前单 silo/内存
reminder 的边界，避免把架构能力说成已部署的横向扩展。

## 待办

- 全量 Api 复跑完成后更新测试数字；
- 英文文档暂未复制，索引保留上游链接。
