# §259 通道覆盖全局单通道重试次数

## 背景

live 提交 `090e542e`（#2660）：通道 `settings.maxSingleChannelRetries` 可覆盖
全局 `retry_policy` 的同通道重试次数。端口此前已移植 GraphQL 字段、创建/更新
校验与 0..65535 边界，但 Pipeline 决策仍只看全局值，通道设置没有效果。

## 实现

- `AxonHub.Llm.Pipeline.IChannelSameChannelRetryLimit`：
  `(int Limit, bool HasOverride) SameChannelRetryLimit()`；无覆盖时回退全局。
- `PipelineOptions.RetriesDisabled`：全局 retry policy 关闭标记。
- `Pipeline.MaxSameChannelRetriesFor()`：
  - `RetriesDisabled` 时恒为 0；
  - outbound 实现接口且有覆盖时用覆盖值；
  - 否则用 `MaxSameChannelRetries`。
- `Pipeline.Process` 的同通道重试条件改用该值；
  `PipelineStream.HasStreamRetryBudget` 同样改用有效值，使正覆盖值启用首事件
  前的流式重试预算。
- `PersistentOutboundTransformer` 实现接口，从当前候选的 `ChannelSettingsDraft`
  读取 `MaxSingleChannelRetries`。
- `ChatCompletionOrchestrator` 在 `retryPolicy.Enabled == false` 时设置
  `RetriesDisabled`，覆盖值不能重新开启重试。

## 测试

- `PipelineSameChannelRetryLimitTests` 5 例：无覆盖回退、覆盖替换、全局关闭、
  0 覆盖阻止重试（执行器只调用 1 次）、覆盖提高次数（全局 1、覆盖 3，执行 4 次）。
- `ResponseTimeoutProviderTests.SameChannelRetryLimitReadsTheChannelOverride`
  4 例：null / 空设置 / 5 / 0。
- Llm 全量 **3363/3363**；ResponseTimeout/ChatCompletionOrchestrator 36/36。
  未跑 Api 全量。

## 台账

- 新增 C318（resolved）。
- 318 条（190 accepted / 128 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 覆盖值只影响同通道重试计数；跨通道重试预算仍由全局 `maxChannelRetries` 控制。
- 读取覆盖值时畸形 settings 按端口既有宽松策略视为未设置（C157 家族）。
