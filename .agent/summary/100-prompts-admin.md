# §100 项目提示词管理面（prompts / 七个变更 / 设置校验）

日期：2026-11-05
参照：`internal/server/biz/prompt.go`、`internal/ent/schema/prompt.go`、
`internal/server/gql/prompt.graphql` / `prompt.resolvers.go`、`internal/ent/migrate/schema.go:425-465`

## 交付

- `src/AxonHub.Api/GraphQL/PromptTypes.cs`：`PromptWhereInput`、`PromptOrder`、
  `PromptSettingsInput` / `PromptSettingsType`、`CreatePromptInput` / `UpdatePromptInput`、
  `PromptType`（`INode`，gid / GoTime / `projectID` / settings / `project` 边）、
  `PromptEdgeType` / `PromptConnectionType` 与 `PromptMapping`（filter / sort / cursor）。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：`prompts` 连接（分页校验 → 排序 →
  过滤 → `PromptManagementService.ListAsync`）与 `node(id)` 的 `Prompt` 分支。
- `src/AxonHub.Api/GraphQL/AdminMutation.cs`：`createPrompt` / `updatePrompt` /
  `deletePrompt` / `updatePromptStatus` / `bulkDeletePrompts` / `bulkEnablePrompts` /
  `bulkDisablePrompts`。
- `Program.cs`：注册 `PromptPrivacy` 与 `PromptManagementService`。
- `tests/AxonHub.Api.Tests/FakePromptStore.cs`：内存 store（含失败钩子与 ent 过滤镜像）。

## 行为要点

- 项目必须存在（`project id not found in context`）；设置校验先于重名检查；
  重名抛 `DUPLICATE_NAME`（extensions resource=prompt / field=name / value）。
- 隐私：OwnerRule → read/write_prompts 项目 scope → 默认拒绝；无 API-key 规则，
  service-account 主体在 GraphQL 呈 `permission denied`。
- 更新先写（aggregate update 无 `deleted_at` 谓词），再经查询策略回读；
  只写用户在改名时先撞重名检查、非改名更新在回读时被拒（写已提交）。
- 删除为 SoftDeleteMixin：仅命中活行并写 `updated_at` + Unix 秒 `deleted_at`；
  批量删除仅活行，批量状态含软删行，空 ids 仍过隐私判定。
- 分页：`first` / `last` 必填且不可同传；`CREATED_AT` 重写为默认 id 排序；
  `ORDER` / `UPDATED_AT` 键集游标，id 排序游标无值。
- 设置 JSON：`action` 恒有且 `{"type":...}`（nil 为空串）、conditions omitempty、
  指针 omitempty 但指针指向空串保留、`api_key_id` 数字、键序与 Go 一致。

## 测试

- `tests/AxonHub.Api.Tests/AdminPromptsEndpointTests.cs`：10 例（查询形状 / 键集分页 /
  分页校验 / 创建默认值与 JSON / 重名 extensions / update+status+bulk / 软删 /
  node / service-account 拒绝）。
- `tests/AxonHub.Api.Tests/PromptManagementServiceTests.cs`：27 例（含 5 例 Theory）。
- `tests/AxonHub.Persistence.Tests/PromptStoreTests.cs`：新增 7 例。
- 总计：Llm 3032、Grains 95、Persistence 224、Api 2604，合计 5955，0 失败 0 跳过。

## 差异

- C188（accepted）：where 子集、必填输入 SDL 拒绝、未知枚举抛错、`order` 32 位、
  无缓存失效、`Project.prompts` 反向边归 C165。
- C187（accepted）：每请求读库替代参照的每项目一分钟缓存。
