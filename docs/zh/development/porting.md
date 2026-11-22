# 移植台账与验收

AxonHub.NET 的验收标准是**可观察行为等价**，不是逐行翻译，也不是接口能通
即可。所有与参照不一致的地方都必须有编号、原因、决策和测试。

## 参照物

| 项 | 值 |
| --- | --- |
| 上游 | [looplj/axonhub](https://github.com/looplj/axonhub) |
| 锁定提交 | `b8bb3421e1fe946cfc4f0d86e8412098738f2793`（v1.0.0-beta11，2026-10-08） |
| 冻结副本 | `E:\GitHub\axonhub-ref`（git worktree） |
| 目标 | .NET 10 / C# 14 / ASP.NET Core / Orleans 10.x |
| 持久化 | PostgreSQL 17；EF Core 写、Dapper 读 |

部分切片在实施时跟进到 live `f272549f` 的局部变更，逐条记录在
[`porting/00-reference.md`](../../../porting/00-reference.md)。升级基线时先
重新 lock，再为新旧版本各录一份语料，差异进入台账。

## 三本账

| 账本 | 文件 | 内容 |
| --- | --- | --- |
| 行为基线 | [`porting/01-baseline.md`](../../../porting/01-baseline.md) | 参照测试/语料与端口测试的逐项对照 |
| 差异台账 | [`porting/differences.jsonl`](../../../porting/differences.jsonl) / [`02-differences.md`](../../../porting/02-differences.md) | `C###` 编号的现象、原因、决策、测试 |
| 验收账 | [`porting/03-acceptance.md`](../../../porting/03-acceptance.md) | 测试数字、公开 API、E2E、容器与风险 |

当前台账：**338 条（192 accepted / 146 resolved / 0 gap）**。`accepted`
表示已解释并接受的差异，也可能包含明确裁剪的能力，不应全部计为待开发功能；
`0 gap` 不代表与上游所有功能完全相同。历史切片描述以该条目的最新决策和当前
状态为准，例如 C176/C177/C179 的主要业务缺口已经闭合，不能仅按旧标题判断。

## 差异类别

- `C`：核心语义、协议、错误、并发、配置等行为差异；
- `T`：终端/UI/渲染；
- `E`：环境、进程、远程执行。

每条差异至少包含：

1. **现象**：和参照哪里不同；
2. **原因**：语言/SDK/平台限制；
3. **决策**：为什么接受或如何解决；
4. **测试**：锁定该行为的测试或语料。

## 运行守护

```bash
python -I porting/scripts/diff_register.py lint
python -I porting/scripts/diff_register.py render
python -I porting/scripts/http_route_audit.py --port src/AxonHub.Api
python -I porting/scripts/reference_lock.py check \
  --repo ../axonhub-ref --dest porting/reference-lock.json
```

`02-differences.md` 必须由 `render` 重新生成且与 JSONL 一致；CI 的
`porting-guard` 会检查 lint、render diff、HTTP 路由、四类差分语料和参照
锁定。

`tests.yml` 的普通 push 入口仅针对 `unstable`，`main` 上的完整后端验收需要
nightly 或手动触发；PR 不执行 Api 全量 job。验收时记录目标 SHA、实际执行数和
跳过原因，不能用镜像发布成功代替测试通过。收尾任务见
[移植收尾清单](../../superpowers/plans/2026-11-18-porting-closeout-todo.md)。

## 已知边界

| 边界 | 说明 |
| --- | --- |
| 数据库 | 仅 PostgreSQL；SQLite/MySQL/读副本未移植（C011） |
| GraphQL HTTP | `/admin/graphql` 使用 GraphQL over HTTP September 2026 Edition；请求错误为 422、带 `data` 的执行错误为 294；媒体类型遵循规范协商（C109） |
| GCP OAuth | 交互式 web/installed OAuth 不支持（C128） |
| WebSocket | 端口自写 TLS/SSE 不声明 ALPN，WebSocket 使用 HTTP/1.1 |
| 日志 | 使用 ASP.NET Core 日志；参照文件轮转日志未逐项移植 |
| 缓存 | Orleans/进程内缓存；系统设置读取缓存及写入失效已实现（C029），不连接 Redis |
| Safeguards | 设置面与运行时 reviewer 已移植（C243 已关闭）；`server.safeguards` YAML 默认值尚未接线，运行时使用内置默认值与持久化系统设置 |
| 供应商配额 | 核心采集链路及全部 19 个 provider checker 已移植（C182 已关闭）；`provider_quota.*` 配置尚未接入，定时检查使用五分钟间隔 |
| 配置 | `config.yml` 只覆盖实际读取的键；未移植节见配置文档 |
| 前端 | 来自上游，Docker 构建时编译进镜像 |

## 新增行为的流程

1. 先跑 `reference_lock.py check`；
2. 找到对应参照实现和测试；
3. 在端口中实现，优先复用现有 transformer/seam；
4. 增加契约测试、线路形状测试或语料；
5. 更新 `01-baseline.md` 与 `03-acceptance.md`；
6. 如有差异，登记 `C###` 并运行 `diff_register.py lint/render`；
7. 运行相关项目测试；Api 全量按需或 nightly 运行。

验收和差异数字以账本为准，不以本文档为准。
