# §112 配额路由启动数据迁移（claim / legacy 映射 / allowedChannelIDs 改写 / 标记）

日期：2026-11-06
参照：`b8bb3421:internal/server/biz/quota_routing_migration.go`（132 行，`Migrate` / `migrateInTransaction` /
`quotaRoutingModeFromLegacy` / `QuotaRoutingModeFromLegacy`）、`internal/server/biz/fx_module.go:117-128`
（ProviderQuotaService 的 OnStart 在 goroutine 中执行迁移，失败只 `log.Error`）、
`internal/server/biz/abstract.go` 的 `RunInTransaction`（已有外层事务时复用）。
参照测试：`quota_routing_migration_test.go`（1 个 Test / 9 个 `t.Run`）与
`quota_routing_migration_postgres_test.go`（`AXONHUB_TEST_PG_DSN` 门控，3 个 `t.Run`，含 4 路并发启动）。

实时上游 `f272549f` 的 `quota_routing_migration.go` 与冻结基线逐字节一致。

## 交付

- `src/AxonHub.Api/Management/QuotaRoutingMigrationService.cs`：claim 写入与读回、legacy 解码与模式映射、
  `quota_routing_settings` 按需写入、`allowedChannelIDs` 通道改写、标记落库与全部错误包装。
- `src/AxonHub.Api/Management/QuotaRoutingMigrationHostedService.cs`：宿主启动后在独立 scope 执行一次，
  `Task.Yield()` 保证不阻塞启动，`CancellationToken.None`（不随停机取消），失败只记录
  `quota routing migration failed`。
- `src/AxonHub.Api/Program.cs`：注册服务与 hosted service。
- `src/AxonHub.Persistence/Stores/SystemStore.cs`：`ISystemStore.CreateIfAbsentAsync`
  （`ON CONFLICT ("key") DO NOTHING`，软删行同样冲突）与 `ISystemStore.RunInTransactionAsync`
  （复用 `Database.CurrentTransaction`，否则新开并在本调用内提交 / 回滚）。
- `src/AxonHub.Persistence/Stores/ChannelWriteStore.cs`：`UpdateAsync` 改为加入 ambient 事务
  （ent `RunInTransaction` 的复用语义），不再每次新开并提交；`affected == 0` 时仅拥有事务的调用方
  回滚，ambient 拥有者自行决定。

## 行为要点

- claim：`Guid.NewGuid()`（`uuid.NewString()` 等价）写入 `quota_routing_migration_done`；读回使用
  软删谓词，缺失按 `ent: system not found` 报错；`marker.Value != claim` 直接返回，因此并发启动只有
  一个实例执行迁移。
- legacy 缺失：只把标记写为 `"true"`，该路径错误不套 outer 包装（参照直接返回 `setSystemValue`）。
- 模式映射沿用 §95 的五条规则；`quota_routing_settings` 仅在缺失时写
  `{"defaultMode":<mode>}`（GoJson 序列化、`QuotaRoutingModes.IsValid`）。
- 通道改写：逐项按 ent `Channel.Query().Where(IDEQ).Only` 读取；缺失只 Warn 并跳过；存在时把
  `settings.quotaRoutingMode` 置为 `ignore_quota`，经 `ChannelLifecycleService.UpdateAsync` 写回
  （`ChannelService.UpdateChannel` 的校验顺序、Go JSON 序列化与并发守卫）。
- 错误文案：claim、标记读取、legacy 读取（`failed to get system value` 双层）、解码、新键检查、
  新键写入（`failed to create system setting` 双层）、通道加载、通道写入、最终标记写入逐条一致。
- 事务：`systems` 与 `channels` 的写入在同一事务内提交或回滚，失败后下次启动重试。

## 测试

- `tests/AxonHub.Api.Tests/QuotaRoutingMigrationServiceTests.cs`：19 例——全新迁移保留
  `modelMappings` 并写 mode / 通道模式 / 标记、重跑幂等、4 路并发启动幂等、缺失通道跳过、nil settings、
  既有新键不抑制通道迁移、空 allowed 列表、legacy 缺失不动默认值、通道失败回滚全部 systems 写入后重试
  成功；10 个错误前缀（claim、标记读取含软删行、解码、legacy 读取双层、新键检查、新键写入双层、最终
  标记双层、legacy 缺失时标记写入不双层、通道加载）。
- `tests/AxonHub.Persistence.Tests/SystemStoreTests.cs`：+6 例——`CreateIfAbsentAsync` 仅在缺失时插入、
  软删行抑制插入、事务提交 / 回滚 / 复用 ambient、4 路并发抢占恰有一个 owner（真实 PostgreSQL）。
- `tests/AxonHub.Persistence.Tests/ChannelWriteStoreTests.cs`：+1 例——`UpdateAsync` 加入 ambient 事务，
  channels 与 systems 写入随外部回滚一起消失。
- 静态计数：`AxonHub.Api.Tests` 2909 → 2928（+19），`AxonHub.Persistence.Tests` 294 → 301（+7），
  四项目合计 6330 → 6356。**本轮按项目规则未运行 lint / build / test**，下一次全量运行应以该数字复核。

## 差异

- C183 由 accepted 转为 resolved。
- 日志字段形态：端口输出 `channel_ids=1,2 default_mode=...`，参照为 `log.Any([]int)`；按 C095 / C120
  的日志约定同口径，不另立条目。
- JSON 解码失败文案取自 System.Text.Json，与 Go `encoding/json` 的逐字文案不同（既有 JSON 文案差异口径）。

## 残余风险

- 本轮未运行构建与测试，新增代码的编译 / 分析器正确性仅由人工检查保证。
- `QuotaRoutingMigrationHostedService` 本身没有启动宿主级测试；服务逻辑由 19 例直接覆盖，接线仅由
  DI 注册与代码审查保证。
- 通道改写经由 `ChannelLifecycleService`，其 `channel was updated concurrently` 分支只在迁移期间出现
  并发删除 / 修改时触发；该竞态未单独构造测试。
