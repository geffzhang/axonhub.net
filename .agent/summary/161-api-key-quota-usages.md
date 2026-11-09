# §161 apiKeyQuotaUsages（API 密钥配额用量查询根，15 例）

日期：2026-11-10
参照（冻结基线 `b8bb3421`）：`internal/server/gql/axonhub.graphql:1116`（根字段）、`562-639`
（`APIKeyQuotaUsage` / `APIKeyQuotaWindow` / `APIKeyProfileQuotaUsage` 类型）、
`internal/server/gql/axonhub.resolvers.go:952-981`（resolver）、
`internal/server/biz/quota.go:129-155`（`ProfileQuotaUsages`）、`157-191`（`GetQuota`）、
`193-256`（`quotaWindow`）、`internal/server/gql/graphql.go:181-190`（隐私拒绝呈现）。

## 背景

前端已调用 `apiKeyQuotaUsages`，而 C176 item (6) 把它登记为未移植的延迟面。该根字段在冻结
基线中已存在，所需的窗口计算、用量聚合与 API key 隐私在端口均已就绪，因此本切片只补读取
链路：配额服务的 profile 循环 + GraphQL 根字段 + 三个输出类型。

## 交付

- `src/AxonHub.Api/Orchestration/ApiKeyQuotaService.cs`：新增 `QuotaResult` /
  `ProfileQuotaUsage` / `IApiKeyProfileQuotaReader` 与 `GetQuotaAsync`（nil quota 返回零值）、
  `ProfileQuotaUsagesAsync`（`Profiles == nil || len == 0` → 空、跳过无 quota、按 profile
  顺序逐项计算窗口与用量）。
- `src/AxonHub.Api/GraphQL/ApiKeyTypes.cs`：`APIKeyQuotaUsage` / `APIKeyQuotaWindow` /
  `APIKeyProfileQuotaUsage` 三个类型与 `ApiKeyMapping.ToGraphQL(ProfileQuotaUsage)`
  （C190 checked 截断、Decimal 与 Time 标量）。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：`apiKeyQuotaUsages(apiKeyId: ID!)` 根字段；
  `GidValue.Parse` 先于取键，`failed to get api key: %w` 与
  `failed to get api key quota usage: %w` 两段前缀。
- `src/AxonHub.Api/Program.cs`：`ApiKeyQuotaService` 同时转发为 `IApiKeyQuotaChecker` 与
  `IApiKeyProfileQuotaReader`。

## 行为要点

- 取键走 APIKey 查询隐私（项目 scope 用户 / 带 `read_api_keys` 的 API key / owner，全部
  Skip 时默认拒绝）；缺失或被过滤为 `ent: api_key not found`；隐私拒绝由 presenter 折叠为
  `permission denied` + `code: FORBIDDEN`。
- 空串、`{}`、`{"profiles":null}` 与 `{"profiles":[]}` 都输出空列表（非 null）。
- 每个携带 quota 的 profile 独立计算窗口：all_time（start nil、end now、含端点）、
  past_duration（start now−Δ、end now、含端点）、calendar_duration（系统时区的本地日 / 月
  边界，end 不含）；未知 `period.type` → `unknown period.type: %s`。
- 每个 profile 分别执行 `CountByApiKeyAsync` 与
  `AggregateByApiKeyAsync(needTokens: true, needCost: true)`；`TotalCost` 按
  `decimal.NewFromFloat` 的最短往返转换。
- `RequestCount` / `TotalTokens` 在 GraphQL 边界按 C190 `checked((int)…)` 截断；`TotalCost`
  走 Decimal 标量（GoDecimal 格式，JSON 数字）。

## 测试

`tests/AxonHub.Api.Tests/ApiKeyQuotaTests.cs`（+8：3 Fact + 1 Theory / 5 InlineData）：
profile 顺序与逐窗口聚合（all_time 与 1 分钟 past_duration）、空 / null profiles 的四种
形状、窗口错误传播、nil quota 的零值结果。

`tests/AxonHub.Api.Tests/AdminApiKeyQuotaUsageEndpointTests.cs`（7 Fact）：前端形状
（quota / window / usage 与 Decimal 原样）、past_duration 独立窗口、空 profiles 的 `[]`、
缺失键前缀、非法 GID 先于取键、隐私拒绝 `permission denied`、窗口错误的配额前缀。

静态计数：`AxonHub.Api.Tests` 5788 → 5803（+15），`AxonHub.Persistence.Tests` 359、
`AxonHub.Grains.Tests` 1746、`AxonHub.Llm.Tests` 1512 不变，四项目合计 9405 → 9420。
**按项目规则未运行 lint / build / test。**

## 台账

新增 C248（accepted）：profiles 损坏列的解码位置（参照在 ent 扫描阶段 → 第一段前缀；
端口在配额服务 → 第二段前缀 + System.Text.Json 文案）；合法写入路径不会产生损坏列。
C176 item (6) 的 `apiKeyQuotaUsages` 缺口关闭。`02-differences.md` 重新生成
（248 条：179 resolved / 56 accepted / 13 gaps），lint 通过。
