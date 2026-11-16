# §99 项目提示词注入（injectPrompts / decisions 提示词）

日期：2026-11-05
参照：`internal/server/orchestrator/prompt.go`、`internal/server/biz/prompt_matcher.go`、
`internal/server/biz/prompt.go`、`internal/objects/prompt.go`、`internal/ent/schema/prompt.go`、
`internal/ent/migrate/schema.go:425-465`、`internal/pkg/xregexp/match.go`

## 交付

- `src/AxonHub.Api/Prompts/`：`InjectPromptsMiddleware`、`DecisionsPrompts`、
  `DecisionsRawJson`、`PromptMatcher`、`PromptService` / `IPromptProvider`、
  `PromptModels`（`PromptSettings.FromJson` 的 Go 解码语义）。
- `src/AxonHub.Llm/Json/RawJsonStringSet.cs`：sjson 语义的顶层字符串写入。
- `src/AxonHub.Persistence/Stores/PromptStore.cs` + DDL / EF 映射：EF 写、Dapper 读
  （`project_id` + `status = 'enabled'` + `deleted_at = 0`，无 ORDER BY），
  唯一索引 `(project_id, name, deleted_at)`；写入失败 detach。
- `ChatCompletionOrchestrator`：注入中间件注册在 ProtectPrompts 之前，只在启用列表非空时
  注册；`LoadPromptsAsync` 实现无项目跳过与 provider 失败 fail open。
- `Program.cs`：注册 `IPromptStore` / `IPromptProvider`。

## 行为要点

- conditions = composite 的 AND，composite = condition 的 OR；null / 空匹配全部；
  `[null]` 为匹配全部的零 composite；model_id 精确、model_pattern 走 XRegexp、
  api_key 等值；未知类型不匹配。
- ApplyPrompts 稳定排序（Order、CreatedAt），prepend（含默认 / 未知动作）在前、
  append 在后。
- decisions 字符串 input 逐条拼接；数组 input 只改写首个 `type` 大小写不敏感等于
  `input_text` 的分片，role 缺省 user，scope 为 `""` / 相等 / `system`；字节级补丁。
- 设置 JSON 键大小写不敏感、未知键忽略、null 取零值；畸形设置导致读取失败并 fail open。

## 测试

- `tests/AxonHub.Api.Tests/PromptInjectionTests.cs`：63 例。
- `tests/AxonHub.Persistence.Tests/PromptStoreTests.cs`：6 例。
- 总计：Llm 3032、Grains 95、Persistence 217、Api 2567，合计 5911，0 失败 0 跳过。

## 差异

- C187（accepted）：每请求读库替代参照的每项目一分钟缓存。
- C188（gap）：提示词管理面 GraphQL CRUD / 设置校验 / 缓存失效未移植。
