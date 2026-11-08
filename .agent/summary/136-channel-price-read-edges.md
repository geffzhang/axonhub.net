# §136 通道价格读取边与查询隐私（C048 读取面闭合）

日期：2026-11-08
参照：`b8bb3421`（上游 `f272549f` 在本切片范围内一致）：`internal/server/gql/gql_node.go:247-255`（Channel 节点查找）、
`internal/ent/gql_edge.go:131-177`（Channel.ChannelModelPrices / ChannelModelPrice.Channel / Versions /
ChannelModelPriceVersion.ChannelModelPrice）、`internal/ent/graphql.go`（ent.graphql:574-590、641-666 的 SDL 形状）、
`internal/server/gql/graphql.go:206-226`（guidTypeToNodeType，19 条）、
`internal/ent/schema/channel.go:233-243` 与 `internal/ent/schema/channel_model_price.go:62-72`（查询隐私链）。

## 交付

- `src/AxonHub.Api/Management/ChannelPrivacy.cs`：显式查询隐私求值 `EvaluateQueryAsync`——
  测试 principal 放行；API key 无 `read_channels` 拒绝；owner 放行；无用户 `no user in context`；
  用户经直接 scopes 或系统角色 scopes 命中 `read_channels` 放行；其余 `default deny`。
  拒绝文案包装 ent 哨兵 `: ent/privacy: deny rule`，由 GraphQL 错误过滤器映射为
  `permission denied` / FORBIDDEN。
- `src/AxonHub.Api/Management/ChannelQueryService.cs`：`GetNodeAsync` 先求隐私，再
  `channels.GetByIdAsync`，缺失报 `ent: channel not found`。
- `src/AxonHub.Api/GraphQL/PriceTypes.cs`：`ChannelModelPriceType` / `ChannelModelPriceVersionType`
  （均实现 `Node`）、`ChannelModelPriceVersionStatusValue`（active / archived）、
  `GetChannelAsync` / `GetVersionsAsync` / `GetChannelModelPriceAsync` 三条读取边的隐私与 NotFound 语义、
  未知状态文案 `Enum "ChannelModelPriceVersionStatus" cannot represent value: "<v>"`。
- `src/AxonHub.Api/GraphQL/ChannelTypes.cs`：`Channel.channelModelPrices` 边（隐私 + 存储读取）。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：`node()` 的 `Channel` 分支。
- `src/AxonHub.Persistence/Stores/ChannelAdminStore.cs` / `ChannelModelPriceStore.cs`：
  `GetByIdAsync` 均为 `id = @Id AND deleted_at = 0` 的 Dapper 单行查询；版本列表按 id 排序。
- `src/AxonHub.Api/Program.cs`：`IChannelAdminStore`、`ChannelPrivacy`、`ChannelQueryService` 均 Scoped 注册。

## 行为要点

- `ChannelModelPrice` / `ChannelModelPriceVersion` 虽然实现 `Node` 接口，但参照的
  `guidTypeToNodeType` 没有条目，`node()` 对两者仍报 `unknown node type: ...`；端口一致（C165）。
- 四条边在参照中没有 ORDER BY；端口存储固定 `ORDER BY id` 并过滤软删行——行序本身不可依赖，
  此处是可重复的确定性判定（C048 记为已解决，不另立差异）。
- 价格 JSON 按 Go / shopspring 的字符串小数解码，GraphQL `Decimal` 输出为 JSON 数字；
  价格 `channelID` 为 Channel gid、版本 `channelID` 为 Int、`channelModelPriceID` 为价格 gid。
- 根查询 `channels` / `queryChannels` / 四个聚合查询的隐私与 scopes 门禁、Channel 写入门禁仍属
  C014 裁剪（§65 / §66 按前端可用性接入，不做隐私求值）；本切片只覆盖 `node(Channel)` 与价格读取边。
- 进程内价格缓存刷新在端口不可观察（每次读取直接命中数据库）。

## 测试

- `tests/AxonHub.Api.Tests/AdminChannelsEndpointTests.cs`：9 Fact（节点查找与文案、前端
  `GetChannelModelPrices` 文档原文与价格树全字段、版本边与两条反向边、未知状态枚举文案、
  并发软删下反向边 NotFound、服务账号 / 用户四条隐私分支）。
- `tests/AxonHub.Api.Tests/AdminChannelModelPriceEndpointTests.cs`：既有 introspection 用例扩展
  （9 字段的 `ChannelModelPrice`、12 字段的 `ChannelModelPriceVersion`、Node 接口、状态枚举值）。
- `tests/AxonHub.Api.Tests/AdminUsersRolesEndpointTests.cs`：未知类型用例改用 `ChannelProbe`
  （Channel 已可解析）。
- `tests/AxonHub.Persistence.Tests/ChannelModelPriceStoreTests.cs`：1 Fact（`GetById` 只返回 live 行）。
- Fake 存储补齐：`FakeChannelAdminStore.VanishAfterFirstLookup`（模拟两次查询之间的并发软删）、
  `FakeChannelPriceReadStore.HideFromGetById`（反向边首查返回 null）。

`AxonHub.Api.Tests` 由 5362 增至 5371（+9），`AxonHub.Persistence.Tests` 由 330 增至 331（+1），
四个项目合计由 8827 增至 8837。**按项目规则未运行 lint / build / test，上述为 `[Fact]` /
`[InlineData]` 静态计数；下一次全量运行应以 5371 / 331 / 8837 复核。**

## 台账

- `porting/differences.jsonl`：C048 → resolved（读取边接入；缓存刷新不可观察）；C014 的
  `what` / `decision` / `test` 追加 §136 隐私复核项（根查询隐私与写入门禁仍为裁剪）；
  C165 更新（Channel node 分支关闭，剩余 7 个 ent 类型：ChannelProbe / ChannelOverrideTemplate /
  RequestExecution / Role / System / UserProject / UserRole）；guidTypeToNodeType 计数由 20 修正为 19；
  `lint` 通过，`render` 重新生成 `02-differences.md`（222 条：160 accepted / 50 resolved / 12 gap）。
- `porting/01-baseline.md`：新增 §136；覆盖缺口两行更新（价格读取边完成、node() 其余 7 个类型）。
- `porting/03-acceptance.md`：追加 §136 静态计数段落；channels 读取面审计项扩充。

## 下一步

- 剩余真实缺口：C014、C022、C044、C067、C072、C091、C092、C094、C096、C097、C126、C135。
