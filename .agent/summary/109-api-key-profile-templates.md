# §109 API key profile template 管理面（api_key_profile_templates / 四个变更 / linkedProfilesCount）

日期：2026-11-06
参照：`b8bb3421:internal/ent/schema/api_key_profile_template.go:20-87`、
`internal/server/biz/api_key_profile_template.go:29-419`、
`internal/server/biz/api_key_profile_template_test.go:35-765`、
`internal/server/gql/ent.graphql:116-177` / `:1598-1610` / `:3123-3131`、
`internal/server/gql/axonhub.graphql:551-559` / `:1028-1036`、
`internal/server/gql/axonhub.resolvers.go:30-32` / `:731-749`

实时上游 `f272549f` 的 schema 与 biz 文件、模板相关 SDL 和 resolver 与冻结基线逐字节一致；
端口以冻结基线 `b8bb3421` 为准。

## 交付

- `src/AxonHub.Persistence/Stores/ApiKeyProfileTemplateStore.cs`：`api_key_profile_templates` 的
  EF Core 写 + Dapper 读；`profile` 为 jsonb（显式 nil 写 SQL NULL）、软删 `deleted_at = 0`、
  `(project_id, name, deleted_at)` 唯一冲突映射为 `ApiKeyProfileTemplateNameConflictException`；
  `ListAsync` 支持 entgql 的 id / createdAt / updatedAt / name / description / projectID 比较谓词、
  `hasProject` / `hasProjectWith`（含名称与状态谓词）与键集游标。
- `src/AxonHub.Api/Management/ApiKeyProfileTemplateService.cs`：Create / Get / GetNode / GetForRead /
  ListForProject / List / Update / Delete / Load / CountLinkedProfiles；事务、发布与脱钩、
  legacy 结构匹配、名称冲突编号与 personal 守卫。
- `src/AxonHub.Api/Management/ApiKeyProfileTemplatePrivacy.cs`：Query 策略
  UserProjectScopeReadRule(read_api_keys) → APIKeyProjectScopeReadRule(read_api_keys) → OwnerRule()；
  Mutation 策略 UserProjectScopeWriteRule(write_api_keys) → OwnerRule()（无 API key 变更规则）。
- `src/AxonHub.Api/GraphQL/ApiKeyProfileTemplateTypes.cs`：`APIKeyProfileTemplate` 类型 / 连接 / 边 /
  排序 / where 输入 / Create / Update / Load 输入与映射；`project` 边经
  `ApiKeyPrivacy.GetVisibleProjectAsync`（过滤为 `ent: project not found`）。
- `src/AxonHub.Api/Management/ApiKeyProfilesCodec.cs`：新增 `SerializeProfile` /
  `DeserializeProfile` / `NormalizeAndValidateRoutingPolicy`（Go 形状 JSON 与两条策略错误文案）。
- `src/AxonHub.Persistence/Records.cs` / `DatabaseSchema.cs` / `AxonHubDbContext.cs`：表、索引与记录；
  `src/AxonHub.Api/GraphQL/AdminQuery.cs` / `AdminMutation.cs`：根连接、node 与四个变更；
  `src/AxonHub.Api/Program.cs`：服务注册。

## 行为要点

- 创建：`profile` 为 GraphQL 必填参数；嵌入 profile 的 `name` 被 input.name 覆写，
  `templateID` / `templateName` 清空，再归一化并校验路由策略
  （`profile '<name>' loadBalanceStrategy is invalid` / `profile '<name>' traceStickyMode is invalid`）；
  描述缺省空串；唯一冲突 → `DUPLICATE_NAME`（resource `Template`、field `name`、value 为 input.name），
  其余 `failed to create template: `。
- 读取：`GetTemplate` 包装 `failed to get template: `；`GetForRead` 要求 id / name 恰有一个，
  否则 `exactly one of template id or name must be provided`；node / 分页根字段走查询隐私，
  CREATED_AT 排序重写为 id。
- 更新：事务内先读（`failed to get template: `）；profile 缺省且改名时以既有 profile 的 Clone
  重新发布；清空嵌入模板字段后在 Name 覆写之前归一化；写库后把新 profile 发布到显式 TemplateID
  匹配与 legacy 结构匹配的 key，保留每个 key 的本地 profile Name 别名；唯一冲突 →
  DuplicateName(input.Name)，其余 `failed to update template: `。
- 删除：事务内读取（`failed to get template for deletion: `）、只脱钩显式 TemplateID 的 key、
  软删并返回预删行。
- 加载：`failed to get template: ` / `failed to get API key: `；项目不一致
  `template and API key must belong to the same project`；personal key 仅创建者或系统 owner；
  空 profile `template has no profile`；名称冲突从 1 起 `"%s (%d)"`；写回包装
  `failed to update API key profiles: `。
- linkedProfilesCount：按模板项目读取可见 API key（`failed to list API keys linked to template: `），
  统计 TemplateID 命中或 legacy 结构匹配的数量（保留参照的按项目全量读取 + 内存过滤）。
- 根连接：gid / GoTime / Cursor 线形、`APIKeyProfileTemplateOrderField { CREATED_AT, UPDATED_AT }`、
  where 输入覆盖 entgql 比较谓词与 hasProject / hasProjectWith。

## 测试

- `tests/AxonHub.Api.Tests/ApiKeyProfileTemplateServiceTests.cs`：14 例——创建身份覆写与路由策略、
  SQL NULL profile、DUPLICATE_NAME 扩展、项目作用域、Get / GetForRead 错误包装、更新发布到显式与
  legacy 关联 key（保留本地别名）、仅改名重发布、加载追加与名称冲突、未命名 profile 回退、
  缺失 / 跨项目 / 空 profile、personal 守卫、列表过滤、删除返回预删行。
- `tests/AxonHub.Api.Tests/AdminApiKeyProfileTemplateEndpointTests.cs`：3 例——连接 / node /
  linkedProfilesCount 线形、create / update / delete 往返、load 变更。
- `tests/AxonHub.Persistence.Tests/ApiKeyProfileTemplateStoreTests.cs`：4 例——jsonb 与 SQL NULL、
  按项目重名与软删名复用、列表顺序 / 项目边 / 访问作用域、部分列更新与写谓词。
- 静态计数：`AxonHub.Api.Tests` 2772 → 2789（+17），`AxonHub.Persistence.Tests` 276 → 280（+4），
  Llm 3032、Grains 95，合计 6175 → 6196。**本轮按项目规则未运行 lint / build / test**，
  下一次全量运行应以该数字复核。

## 差异

- C200（accepted）：`ListForProject` 对应参照无 ORDER BY 的查询，端口固定按 id 升序；
  写方法各自开启事务（参照为 GraphQL 请求级事务）；逐 key 更新的语句形状不同但可观察结果一致；
  `project` 边复用 C176(7) 的 Project 隐私仿真。
- C176(6)：移除已闭合的 `apiKeyProfileTemplates` 延迟项。

## 残余风险

- 本轮未运行构建与测试，新增代码的编译 / 分析器正确性仅由人工检查保证。
- `hasProjectWith` 的 PostgreSQL 语义由存储测试覆盖 id / name / nameContainsFold；
  `not` / `or` 组合的项目边谓词未单独加模板用例（与共享谓词构造器一致）。
