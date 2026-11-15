# §275 M5 公开 API 表面审计

## 目标

收口 M5 的公开 API 表面项：HTTP 路由、GraphQL 根字段、模块导出清单与平台
限制。M5 不改变行为，只给出可量化证据与明确口径。

## 结果

- HTTP 路由：参照 76 条 method+path，端口 **76/76**（§246，脚本
  `porting/scripts/http_route_audit.py`）。
- Admin GraphQL 根字段：参照实际 Query 86 + Mutation 134 = 220；端口
  Query 85 + Mutation 134 = 219，**219/220 = 99.5%**。唯一缺口 `nodes` 是
  参照自身 resolver panic 且前端不调用的字段，按 C269 有意不暴露。
- 字段级签名：不做自动 SDL diff（C331）。gqlgen 与 HotChocolate 在可空性、
  列表包装、输入类型命名与 resolver 方法名上存在系统性表示差异；形状由各
  端点/字段行为测试锁定。

## 导出清单（非一一映射）

| 面 | 文件 | 导出 type | 导出 func | 导出 method |
|---|---:|---:|---:|---:|
| 参照 `llm/` | 262 | 524 | 318 | 622 |
| 参照 `internal/server` | 305 | 606 | 279 | 1241 |

| 端口程序集 | 文件 | public 类型 | public 方法 |
|---|---:|---:|---:|
| `AxonHub.Llm` | 279 | 723 | 1259 |
| `AxonHub.Persistence` | 60 | 252 | 444 |
| `AxonHub.Grains` | 21 | 17 | 75 |
| `AxonHub.Grains.Abstractions` | 31 | 63 | 38 |
| `AxonHub.Api` | 405 | 1394 | 2582 |

计数是清单，不是等价率：Go 的包导出标识符与 C# 的 public 类型/方法没有
稳定的一一对应键，且端口包含 port-only 接缝与 DTO。

## 平台限制

- 仅 PostgreSQL（参照默认 SQLite + PostgreSQL，见 C011）。
- 前端 React 不在后端移植范围。
- GCP 交互式 web/installed OAuth 客户端不支持（C128）。
- 自写 TLS/SSE 不声明 ALPN，因此 WebSocket 天然停留在 HTTP/1.1（#2664）。

## 台账

- 新增 C331（accepted）：公开表面审计口径为根字段集合 + 路由 + 导出清单 +
  行为测试，而非逐签名 SDL diff。
- 331 条（190 accepted / 141 resolved / 0 gap）；lint 与
  `02-differences.md` 重生成通过。

## 边界

- 根字段计数锁定的是集合与数量，不是每个字段的签名语义。
- 导出计数可重跑但未固化为脚本；后续若需要可加入 porting-guard。
