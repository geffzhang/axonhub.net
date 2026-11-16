# §106 数据存储管理面 GraphQL（dataStorages / node / 两个变更）

日期：2026-11-06
参照：`f272549f:internal/ent/schema/data_storage.go`、`internal/ent/migrate/schema.go:262-288`、
`internal/server/biz/data_storage.go:200-289`、`:793-887`（mergeSettings 与三个 is*Provided）、
`internal/objects/data_stograge.go`、`internal/server/gql/ent.resolvers.go:410-423`、
`internal/server/gql/ent.graphql` / `axonhub.graphql`、`internal/server/gql/axonhub.resolvers.go:668-676`
（仅 data storage 切片局部跟进 f272549f：API-key 查询规则作用域 `write_requests` →
`read_data_storages`，e5c904ef；其余子系统仍以 `b8bb3421` 为基线，见 00-reference.md 与 C196）

## 交付

- `src/AxonHub.Persistence/Stores/DataStorageStore.cs`：`IDataStorageStore` 的 EF 写 +
  Dapper 读——`CreateAsync` / `GetByIdAsync`（追加 `deleted_at = 0`）/
  `ExistsByNameAsync`（活行 + 可选 excludeId）/ `ListAsync`（ent 谓词渲染、id 或
  `updated_at, id` 键集分页、totalCount）/ `UpdateAsync`（部分列
  `UPDATE ... RETURNING`，总是刷新 `updated_at`，无软删谓词）。
- `src/AxonHub.Persistence/Records.cs` 的 `DataStorageRecord`、
  `src/AxonHub.Persistence/AxonHubDbContext.cs` 的 `ConfigureDataStorage`、
  `src/AxonHub.Persistence/DatabaseSchema.cs` 的幂等 DDL（十列 +
  `data_sources_by_name` 唯一索引，只覆盖 `name`）。
- `src/AxonHub.Api/Management/DataStorageSettings.cs`：Go 兼容的 settings 域模型与
  `DataStorageSettingsCodec.Serialize / Deserialize / Merge`（无 omitempty、nil 写
  JSON null；mergeSettings 的 provided 判定与凭据保留规则）。
- `src/AxonHub.Api/Management/DataStoragePrivacy.cs`：查询规则
  `APIKeyScopeQueryRule(read_data_storages)` → `UserReadScopeRule(read_data_storages)` →
  `OwnerRule()` → default deny；变更规则 `UserWriteScopeRule(write_data_storages)` →
  `OwnerRule()` → default deny。
- `src/AxonHub.Api/Management/DataStorageManagementService.cs`：Create / Update /
  List / GetNode 与错误前缀、DUPLICATE_NAME 扩展、`ent: data storage not found`。
- `src/AxonHub.Api/GraphQL/DataStorageTypes.cs`：枚举 / 输入 / 输出 / 连接类型与映射
  （`CREATED_AT` 改写为 id、游标值仅 `UPDATED_AT` 携带）。
- `src/AxonHub.Api/GraphQL/AdminQuery.cs`：`dataStorages` 根字段（仅
  `ValidatePaginationArgs`）与 `node(DataStorage)`；`AdminMutation.cs`：
  `createDataStorage` / `updateDataStorage`；`Program.cs`：三个服务的 DI 注册。

## 行为要点

- 表：`id / created_at / updated_at / deleted_at / name / description / primary /
  type / settings / status`；`data_sources_by_name` 只覆盖 `name`，软删行仍占用名称
  （与 prompts 的 `(project_id, name, deleted_at)` 不同）；查询过滤软删，更新不。
- 查询：只做 `validatePaginationArgs`（无 first / last 互斥）；`CREATED_AT` →
  `DefaultDataStorageOrder.Field`（id）并保留方向，缺省 id ASC；`UPDATED_AT` 按
  `updated_at, id` 排序，游标携带时间值。
- 输出：gid `DataStorage/<id>`、GoTime；settings 输出不含 S3 凭据与 GCS credential；
  WebDAV 的四个可选字段在 Go 侧是普通 string / bool，恒有值（`""` / `false`）。
- 创建：隐私查询查重（前缀 `failed to check data storage name uniqueness: `），
  重名抛 DUPLICATE_NAME（`data storage name '<name>' already exists`，
  extensions: resource `data storage` / field `name` / value）；`input.status` 被忽略，
  行固定 `primary=false`、`status=active`；settings 全字段序列化。
- 更新：先 Get（缺失报 `failed to get data storage: ent: data storage not found`）；
  仅当 name 提供且与现有值不同才查重（NameEQ + IDNEQ）；settings 提供时合并——
  Directory / DSN 覆盖，S3 / GCS / WebDAV 以检测字段非空判定是否提供
  （PathStyle / InsecureSkipTLS 不参与），空凭据 / 密码保留旧值；SET 只含提供列。
- 隐私：无用户 deny 文案 `no user in context`，无作用域用户 `default deny`，
  GraphQL 呈现 `permission denied` / FORBIDDEN（`ent/privacy: deny rule` 后缀）。
- where 子集：id / name / primary / type / status 的 eq、neq、in、notin，
  nameContainsFold 与 not / and / or；空 `*In` 不安装谓词；嵌套空谓词报
  `ent: empty predicate DataStorageWhereInput: field '<not|and|or>'`。
- 更新变更忽略 gid 的类型、只用 `id.ID`（参照行为，端口忠实保留）。

## 测试

- `tests/AxonHub.Persistence.Tests/DataStorageStoreTests.cs`：8 例——全列往返、
  软删隐藏与唯一名保留（UniqueViolation + detach）、名称检查排除自身、
  containsFold / typeIn / statusIn 与空 `*In`、id 键集分页、`updated_at` 排序与
  游标值、嵌套空谓词文案、部分列更新与 RETURNING。
- `tests/AxonHub.Api.Tests/AdminDataStoragesEndpointTests.cs`：14 例——线形与
  settings 输出、webdav / gcs 形状、where + `UPDATED_AT` 键集分页、`CREATED_AT`
  回退 id、显式 `primary=false`、创建默认值与完整 JSON、DUPLICATE_NAME 扩展、
  更新合并与凭据保留、重名拒绝与自身重命名、缺失行 wrapped get 文案、node、
  service_account 无作用域 FORBIDDEN、有 `read_data_storages` 可读、普通用户
  default deny。
- `SchemaTests` / `EfCoreModelTests`：data_storages 的快照与 EF 模型一致性；
  表数 21、索引数 48。
- 整解决方案复跑：Llm 3032、Grains 95、Persistence 252、Api 2680，合计 6059，
  0 失败 0 跳过。

## 差异

- C196（accepted / 真实缺口若干）：data storage 切片局部跟进 f272549f；
  ent privacy 钩子改为显式求值；EF 写 + Dapper 读；fs / S3 / GCS / WebDAV 存储
  后端与缓存（含 S3 checksum 选项）未移植；`requests` / `executions` 边未接线；
  WhereInput 的时间 / description / 字符串比较 / 边谓词未移植；settings 畸形 JSON
  退化（C141 同型）与 mergeSettings 的 nil 边界；主存储种子行（datamigrate
  v0.4.0）未移植。
