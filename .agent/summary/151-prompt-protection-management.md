# §151 提示词保护规则管理面（GraphQL 查询 / 8 变更 / 预览）

日期：2026-11-09
参照：`internal/ent/schema/prompt_protection_rule.go:32-77`（五列、`(name, deleted_at)` 唯一索引、
status 默认 `disabled` 且跳过 create input、entgql 注解与 scopes 策略）、
`internal/server/biz/prompt_protection_rule.go:92-370`（ValidateSettings 顺序、CreateRule /
UpdateRule / DeleteRule / UpdateRuleStatus / 三个批量 / ListEnabledRules）、
`internal/server/biz/prompt_protection_preview.go:21-52`（预览）、
`internal/server/gql/prompt_protection_rule.graphql:1-46`（SDL）、
`prompt_protection_rule.resolvers.go:18-91`（八个变更）、
`ent.resolvers.go:471-486`（连接分页 + CREATED_AT 折叠）；`graphql.go:206-226` 的
`guidTypeToNodeType` 未登记 `PromptProtectionRule`（SDL 类型实现 Node，但根 `node` 报
`unknown node type`）。

## 背景

§28 只接入了规则读取与请求侧保护；未改动的管理前端
（`frontend/src/features/prompt-protection-rules`）已有完整的列表 / 新建 / 编辑 / 启停 / 批量 /
预览页面，因此本切片补齐它发送的全部 GraphQL 契约，并把 C092 的管理面缺口关闭。

## 交付

- 新增 `src/AxonHub.Api/Management/PromptProtectionRuleManagementService.cs`：创建（settings
  必填 → ValidateSettings → live 名称查重 → DUPLICATE_NAME → 落库，status 固定默认
  `disabled`）、更新（先 live Get，有效 pattern/settings 取输入或当前值校验，只写提供的列，
  缺失行报 NotFound）、删除（软删，0 行报 NotFound）、状态、三个批量（空列表 no-op）、连接查询、
  预览。错误前缀逐字复刻：`failed to check existing prompt protection rule` /
  `failed to query prompt protection rule` / `failed to create ...` / `failed to update ...` /
  `failed to update ... status` / `failed to delete ...` / `failed to bulk ...`。
- 新增 `src/AxonHub.Api/GraphQL/PromptProtectionRuleTypes.cs`：四个值枚举、order / where 输入、
  settings 输入输出、preview 输入输出、create / update 输入、`PromptProtectionRuleType : INode`、
  edge / connection；映射层做 gid 与线路枚举转换、`CREATED_AT → id`、值游标与连接构造。
- `AdminQuery.cs` 新增 `promptProtectionRules`（分页校验 + first/last 互斥）；根 `node()` 不加
  `PromptProtectionRule` 分支——参照的 `guidTypeToNodeType`（graphql.go:206-226，19 项）没有登记
  该类型，端口同样报 `unknown node type: PromptProtectionRule`，与参照一致；`AdminMutation.cs`
  新增八个变更；`Program.cs` 注册管理面服务。
- `PromptProtectionRuleStore`：EF 创建（唯一冲突 detach 后原样上抛）、Dapper 读取/连接查询/
  部分列更新/状态/软删/批量；软删谓词只加在查询上，更新与状态更新不检查 `deleted_at`。
- `PromptProtectionModels.cs`：新增 `ToJson`（Go `json.Marshal` + `omitempty`：action 恒在、
  空 replacement 省略、nil/空 scopes 省略）；`FromJson` 把 JSON null 的 replacement 归一为 `""`。
- `PromptProtectionRegex.TryGetOrCompile`：暴露缓存中的编译失败，供管理校验与预览复用。

## 行为要点

- 校验顺序：先编译正则，再 settings 必填、action ∈ {mask, reject}、mask 必须有 replacement、
  scope 合法；因此非法 pattern 先于重名检查与缺失 settings 报错。
- 创建 input 不含 status（entgql.SkipMutationCreateInput），默认落 `disabled`；description 缺省 `""`。
- 更新无软删谓词：软删行仍可被 UpdateOneID/状态更新写入；删除只命中 live 行。
- settings 线上形状：写入按 Go 结构体顺序 + omitempty；输出 replacement 恒为字符串（空为 `""`、
  非 null），scopes 为列表（nil → `[]`）。
- where 只暴露前端实际使用的谓词（id/idIn、name/nameContainsFold、status/statusIn/statusNotIn、
  not/and/or），空 `statusNotIn` 恒真、空 `or` 恒假；未移植谓词在输入校验阶段被拒绝（C231）。
- 枚举严格度：HotChocolate 在输入校验阶段拒绝未知枚举；参照 gqlgen 透传字符串（未知 action
  由服务层报 `invalid action: <x>`），未知存储值输出时端口抛 GraphQLException（C231）。
- 无 live 缓存：参照每次变更后 `asyncReloadEnabledRules`，端口每请求读库，变更即为全部效果。
- 规则 scopes 策略（read/write channels）仍归 C014。

## 测试

- `tests/AxonHub.Api.Tests/AdminPromptProtectionRulesEndpointTests.cs`（18）：前端查询形状、
  Go 输出形状、where + NAME 键集分页、CREATED_AT→id、分页文案、创建默认值与精确 settings JSON、
  omitempty、mask 缺 replacement、非法正则先于查重、DUPLICATE_NAME 扩展、更新有效值校验、
  无效有效 pattern 不写、缺失行三处文案、状态/批量/删除流程、软删行状态更新、预览三种结果、
  预览校验、根 `node` 对规则报 `unknown node type`。
- `tests/AxonHub.Persistence.Tests/PromptProtectionRuleStoreTests.cs`（14）：列往返、创建默认、
  ListEnabled 过滤、live 谓词、filter + 键集分页、状态过滤（空 NOT IN / NOT）、UPDATED_AT 值游标、
  id 顺序、部分列更新、无软删谓词、软删时间戳、批量语义、`(name, deleted_at)` 唯一与软删复用。
- 另补齐 `PromptProtectionTests.cs` 中假 store 的接口实现（NotSupportedException），不新增用例。

静态计数：`AxonHub.Api.Tests` 5467 → 5485（+18），`AxonHub.Persistence.Tests` 336 → 350（+14），
`AxonHub.Grains.Tests` 1746、`AxonHub.Llm.Tests` 1506 不变，四项目合计 9055 → 9087。
**按项目规则未运行 lint / build / test。**

台账：基线 §151、验收 §151 已写入；C092 由 gap 改为 accepted（管理面闭合，残余收窄为无 live 缓存、
Regex 方言、Gemini/Ollama 载荷对照与日志接缝）；新增 C231（accepted：where 子集、输入枚举严格度、
未知存储枚举输出、scopes 策略归 C014）。`02-differences.md` 重新生成（231 条：168 accepted /
55 resolved / 8 gaps），lint 通过。
