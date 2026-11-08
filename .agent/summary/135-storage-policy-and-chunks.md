# §135 存储策略与响应分块持久化（C020 闭合）

日期：2026-11-08
参照：`b8bb3421`（上游 `f272549f` 在本切片范围内一致）：`internal/server/biz/system.go:237-243`（StoragePolicy）、
`system_default.go:11-77`（默认策略与 cleanup 合并）、`system.go:981-1038`（读取 / 写入）、
`system.go:1590-1613`（default data storage ID），`request.go:64-124`（getDataStorage / shouldUseExternalStorage /
rollbackExternalPayload）、`:198 / 334 / 521 / 539 / 638 / 736 / 830`（策略门禁）、`:725-814`（快照写入）、
`:1011-1258`（分块编组与 SaveRequestChunks / SaveRequestExecutionChunks）、`:1369-1683`（六个 loader），
`data_storage.go:289-361 / 530-733`（GetByID / GetPrimary / GetDefault / SaveData / LoadData / DeleteData），
`internal/ent/migrate/datamigrate/v0.4.0.go`（主存储种子）。

## 交付

- `src/AxonHub.Api/Settings/StoragePolicy.cs`：`StoragePolicy` 记录、五种 cleanup 资源类型、
  `Default`（false / false / true / true + 默认 cleanup 列表）与 `MergeCleanupOptions`。
- `src/AxonHub.Api/Settings/StoragePolicyCodec.cs`：键 `storage_policy` 的 Go JSON 编解码；缺省布尔为
  false，旧文档缺 `store_request_body` / `store_response_body` 键时回填 true，cleanup 列表合并默认项；
  `GoJson.MarshalToString` 序列化。
- `src/AxonHub.Api/Settings/SystemSettingsAdminService.cs`：`GetStoragePolicyAsync`（缺行返回默认、
  畸形 JSON 包装 `failed to unmarshal storage policy: `）、`SetStoragePolicyAsync`（`cleanup_days <= 0`
  拒绝）、`GetDefaultDataStorageIdAsync`（缺失为 0、解析失败 `failed to parse default data storage ID:
  expected integer`）。
- `src/AxonHub.Api/Management/RequestPayloadStorage.cs`：写路径策略读取（失败默认 true / chunks false，
  取消原样抛出）、`GetDefaultDataStorageAsync`（再包一层 `failed to get default data storage ID: `）、
  `GetDataStorageAsync` / `GetDataStorageByIdAsync` / primary 查找的三条缺失文案、
  `ShouldUseExternalStorage`（`storage != null && !storage.Primary`，type 不参与）、
  `SaveDataAsync` / `LoadDataAsync` / `DeleteDataAsync` 的 database 特例（key 即数据，写 / 删 no-op）、
  `WriteResponseBodyAsync`（外部写失败不动列）、快照写入（策略门禁 → 数据存储查找告警降级 → 外部写 →
  数据库写失败回滚外部对象）、六个 loader（completed / stream + 终态门禁、外部 JSON 校验、`_ext` 折叠、
  数据库 / 外部分支、存储查找失败回退）。
- `src/AxonHub.Api/Persistence/RequestChunkStorage.cs`：二进制判定、跳过规则、二进制摘要、
  `last_event_id` / `event` / `data` 手写包装（Go 的 HTML 转义、RawMessage 压缩、空 data → null、
  无效 raw JSON 跳过）、`MarshalChunks` 空结果返回 `[]`。
- `src/AxonHub.Api/Persistence/RequestChainRecorder.cs`：`SaveRequestChunks` / `SaveExecutionChunks`
  （策略 → 编组 → 行读取 → 外部对象 + `[{"_ext":1}]` 标记或数据库列；标记写失败回滚；五条错误前缀逐字复刻）。
  `RequestChainPersistenceMiddleware` 按参照顺序调用：成功先终态后 chunk、失败先 chunk 后状态、聚合异常
  先状态后 chunk。
- `src/AxonHub.Persistence/DataStorageBootstrap.cs` + `Program.cs`：启动期幂等执行 v0.4.0 迁移
  （`primary = TRUE AND deleted_at = 0` 守卫；单事务插入 Primary 行与 `systems.default_data_storage_id`）。
- `src/AxonHub.Persistence/Stores/DataStorageStore.cs`：`GetPrimaryAsync` 补上 ent SoftDeleteMixin 谓词
  （`deleted_at = 0`），`DataStorageBootstrap` 的 EXISTS 守卫与 `FakeDataStorageStore` 同步修正
  （忠实性缺陷，非新差异）。
- GraphQL `RequestTypes` 与 `TraceSegmentService` 的正文 / chunk 字段改经 `RequestPayloadStorage` loader。

## 行为要点

- 默认配置（`store_chunks=false`、两个 body 开关为 true）与参照逐字节一致：`response_chunks` 保持
  null、聚合响应正文照常写入；开启 `store_chunks` 后两侧都写（数据库列或 `_ext` 标记），关闭
  `store_response_body` 后两侧都不写正文。
- 分块只归档已结束的流；nil、非二进制的 `[DONE]` 与 `binary.done` 跳过；二进制块只存摘要
  （`binary.stream_chunk` / Trim 后类型 / `len(Data)` 或 `Size`）；过滤后为空不写库。
- 外部数据存储（非 primary）写对象并在列中落标记；标记写失败删除已写对象；数据库写失败按
  `failed to save response chunks: ` 等原文案抛出。
- 六个 loader：response body 仅 completed；chunks 仅 stream 且 completed / failed / canceled；外部
  数据格式非法回退 `{}` / 空列表；单元素 `_ext` 标记折叠为空列表；数据存储查找失败回退数据库列。
- 启动种子迁移在 DDL 之后执行；任一 live primary 行存在即跳过，重复启动无副作用。

## 测试

- `tests/AxonHub.Api.Tests/RequestChunkStorageTests.cs`：13 Fact + 8 InlineData = 21。
- `tests/AxonHub.Api.Tests/RequestPayloadStorageTests.cs`：44 Fact。
- `tests/AxonHub.Api.Tests/RequestChainPersistenceTests.cs`：新增 `StreamFailurePersistsBufferedChunksBeforeStatus`，
  成功用例追加两侧 chunk 断言（含 `[DONE]` 传入、由存储层过滤）。
- `tests/AxonHub.Persistence.Tests/DataStorageBootstrapTests.cs`：4 Fact。
- `tests/AxonHub.Persistence.Tests/DataStorageStoreTests.cs`：1 Fact（primary 软删过滤与 id 顺序）；
  `RequestStoreTests.cs` / `RequestExecutionStoreTests.cs`：各 3 Fact（`setResponseBody` / chunks / GetById）。

`AxonHub.Api.Tests` 由 5296 增至 5362（+66），`AxonHub.Persistence.Tests` 由 319 增至 330（+11），
四个项目合计由 8750 增至 8827。**按项目规则未运行 lint / build / test，上述为 `[Fact]` / `[InlineData]`
静态计数；下一次全量运行应以 5362 / 330 / 8827 复核。**

## 台账

- `porting/differences.jsonl`：C020 → resolved；C191 更新为「外部存储读取已接入，剩余顺序加载 /
  无日志 / 多错误选择顺序」；C192 的 cause 同步（DataStorage 边仍未移植，正文加载已接入）；
  C196 删除第 (8) 项、修正第 (3) 项（存储后端已由 §108 接入，缓存仍缺席）并追加启动种子测试；
  `lint` 通过，`render` 重新生成 `02-differences.md`（222 条：160 accepted / 49 resolved / 13 gap）。
- `porting/01-baseline.md`：新增 §135。
- `porting/03-acceptance.md`：追加 §135 静态计数段落；C020 风险行改为已关闭。

## 下一步

- 剩余真实缺口：C014、C022、C044、C048、C067、C072、C091、C092、C094、C096、C097、C126、C135。
- 实时 chunk 预览（stream + processing）仍随通道运行时流状态缓存（C192）。
