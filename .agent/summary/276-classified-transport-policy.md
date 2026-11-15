# §276 非流式已分类传输错误不再被策略重写

## 背景

Api 全量回归（P4）发现唯一失败：
`ChatCompletionEndpointTests.HiddenPolicyDoesNotRewriteClassifiedTransportErrors`。
该失败在基线提交 `0a98fcd` 就存在，不是本轮切片引入。

## 原因

`TransformErrorAsync` 已把 IOException reset 分类为 502
`upstream_error` / `upstream_stream_interrupted`，但 `ApplyAsync` 仍沿
`ResponseError.CauseException` 链找到 pipeline 的 `UpstreamException`
标记，于是 hidden/custom 策略把分类结果重写成默认文案。

## 修复

`UpstreamErrorPolicyApplier.ApplyAsync`：对
`ResponseError.Detail.Code == upstream_stream_interrupted` 直接返回，
不再进入策略重写。普通 `HttpClientError` / `ResponseError` 仍按
passthrough/hidden/custom 处理；流式路径保持既有“分类后再按 policy”语义。

## 测试

- 定向 `HiddenPolicy*` / `CustomUpstreamErrorPolicy*` /
  `UpstreamTransportError*`：**45/45**。
- Api 全量本轮实测：**7137 例（7133 通过 + 1 该失败 + 3 跳过）**；
  修复后未再跑全量。
- Llm 全量 **3437/3437**；Persistence 276、Grains 95 维持最近记录。
- 验收测试表已刷新：合计 **10945**（10941 通过 + 1 失败 + 3 跳过）；
  该失败已在本次修复，下一次全量应转为通过。

## 台账

- 新增 C332（resolved）。
- 332 条（190 accepted / 142 resolved / 0 gap）；lint 与
  `02-differences.md` 重生成通过。

## 边界

- 只对非流式已分类传输错误短路；未分类或普通上游错误仍可被策略改写。
