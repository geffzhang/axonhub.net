# §244 管理面 GraphQL 根字段审计

## 背景

M5 公开 API 审计的第一个可量化项：参照 `internal/server/gql/*.graphql`
暴露的 admin GraphQL 根字段是否都在端口 `/admin/graphql` 的 schema 中。

## 审计方法

- 参照：解析 `type Query` / `type Mutation`（含 extend）块，按块内最小非空
  缩进取字段行。
- 端口：对 HotChocolate schema 执行内省
  `{ __schema { queryType { fields { name } } mutationType { fields { name } } } }`。
- 差集逐项核对并回看台账条目。

## 结果

| 面 | 参照 | 端口 | 差异 |
|---|---:|---:|---|
| Query | 87 条解析结果 / 实际 86 | 85 | `nodes`（C269 有意不暴露） |
| Mutation | 134 | 134 | 无 |
| 合计 | 220 | 219 | 219/220（99.5%），未解释缺口 0 |

- 参照的 `hasTag` 是输入类型字段被缩进扫描误计，不是根字段。
- 端口 `allScopes` 由 `AdminQuery.AllScopes` 的方法名推断生成，内省可见。
- 参照的 `nodes` resolver 自身 panic，前端未调用；C269 已记录为有意裁剪，
  不计为未解释缺口。

## 测试

- 新增 `AdminChannelEndpointsEndpointTests.AdminRootFieldSurfaceMatchesTheReferenceAudit`：
  一次内省查询断言 Query 85、Mutation 134、`allScopes` 与 `node` 存在、`nodes` 缺席。
- 定向 1/1 通过；未跑 Api 全量，新增 Api 1 例待下次全量计入。

## 台账

- 无新增/变更差异条目（`nodes` 归 C269）。
- 309 条（192 accepted / 117 resolved / 0 gap）。

## 边界

- 本次只覆盖 admin GraphQL 的根字段数量与集合，不含每个字段的输入/输出签名、
  各模块的导出数、HTTP 路由表与运行时/平台限制；这些仍属 M5 后续项。
