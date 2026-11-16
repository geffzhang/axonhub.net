# §224 Api 全量回归（测试宿主修复）

## 首轮结果

- 日志 `.agent/summary/_api_full_181.log`：失败 19 / 通过 6510 / 总计 6529，19m57s，测试主机在
  汇总前崩溃，`EXIT=1`。

## 三类确定性失败与修复（仅测试代码）

1. `UsageCostInjectionTests.StreamChunksCarryInjectedCost`（`usageLogs=0`）
   - 根因：§219/§220 的交付确认语义把未 `AcknowledgeDelivery` 的关闭视为下游取消
     （诊断值 `sourceEnd=Canceled`），§221 的 provenance 门因此拒绝写入 usage。
   - 修复：测试在 `Dispose()` 前调用 `result.Stream.AcknowledgeDelivery()`；生产 HTTP 路径
     已由 `ChatCompletionEndpoint` 的 ack 包装覆盖，不是产品缺陷。
2. `GrainSchedulerBridgeTests`
   - 根因：`GrainSchedulerActivator` 启动时激活 `ChannelCredentialRecoveryGrain`，Api.Tests 的
     silo 未注册其依赖 `IChannelCredentialRecoveryRunner`。
   - 修复：`GrainClusterFixture` 增加 `FakeChannelCredentialRecoveryRunner`，在
     `GrainTestSiloConfigurator.Configure` 注册单例，`InitializeAsync` / `DisposeAsync` 设置与清理。
3. `AdminSystemEndpointTests`（17 例）
   - 根因：最小测试宿主只注册 `ProjectManagementService`，但其构造还依赖 `ProjectPrivacy` /
     `RolePrivacy` / `UserProjectPrivacy`。
   - 修复：`InitializeAsync` 补注册三个 scoped 依赖。

定向重跑三类：25/25 通过。

## 全量重跑

- 日志 `.agent/summary/_api_full_182.log`：
  `已通过! - 失败: 0，通过: 6953，已跳过: 0，总计: 6953，持续时间: 42m22s`，`EXIT=0`，
  测试主机未再崩溃。
- 计数差异：上次 6529 是崩溃中断时的部分计数；当前静态计数为 `[Fact]` 4135 + `[InlineData]`
  2660（另 19 个 `[MemberData]`），已超过 6529，与「崩溃截断执行」一致。
- 耗时 42m22s 长于上次的 19m57s，主要因为本轮完整跑完，且机器上同时有多个 dotnet 进程。

## 台账与状态

- `porting/01-baseline.md` 与 `porting/03-acceptance.md` 已追加 §224 记录。
- 生产代码无残留诊断；工作树只有三个测试文件修改，未提交。
