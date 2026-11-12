# §193 anthropic_fake / openai_fake 测试通道（C126 闭合，C280 新增，8 例）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`llm/transformer/anthropic/fake.go`、
`llm/transformer/openai/fake.go`、`internal/server/biz/channel_llm.go:1089-1095`、
`:675`、`internal/server/biz/channel_endpoint.go:282-283`，以及四个 testdata 固件
（`anthropic-stop.response.json` / `anthropic-stop.stream.jsonl` /
`openai-stop.response.json` / `openai-stop.stream.jsonl`）。

## 背景

C126 把两个测试通道登记为真实缺口：参照的 fake transformer 只服务 Go 侧集成测试
（channel_probe_test、usage_cost_test 等以 fake 通道作为通用夹具），端口当时由自有
xUnit 假件覆盖。但通道类型枚举、端点表与模型清单早已存在，只缺 transformer 与两路
分派；补齐后，照着参照写的接线 / 探针 / 用量测试可以在端口直接复现。

## 交付

- `src/AxonHub.Llm/Transformer/Anthropic/AnthropicFakeTransformer.cs`：组合真实
  Anthropic outbound（`https://fake.anthropic.com` + key `fake`），
  `CustomizeExecutor` 返回固定固件执行器；`Do` 读取嵌入响应并返回 200 +
  `application/json`，`DoStream` 先 `Trim()` 整体再按 `\n` 切分，逐行反序列化
  `LastEventID` / `Type` / `Data` 信封并转成 `StreamEvent`（null 行按零值处理）。
  委托 TransformRequest / TransformResponse / TransformStream / TransformError /
  AggregateStreamChunks，`IApiKeyProvenance` 返回 null（参照性能记录在 fake 通道上
  没有 key provider context）。
- `src/AxonHub.Llm/Transformer/OpenAI/OpenAiFakeTransformer.cs`：同一配方，
  `https://fake.openai.com`；流扫描逐行 Trim 并跳过空行（对应 bufio.Scanner 语义）。
- `src/AxonHub.Llm/Transformer/FakeTestData.cs`：按资源名读取嵌入固件。
- `src/AxonHub.Llm/AxonHub.Llm.csproj`：四个 fixture 以显式 `LogicalName` 的
  `EmbeddedResource` 嵌入。
- `src/AxonHub.Api/Orchestration/ChannelOutboundFactory.cs`：新增
  `AnthropicFakeChannelType` / `OpenaiFakeChannelType` 常量，fail-fast 免凭据分支改用
  常量，switch 增加两路 dispatch（用户端点仍先行）。
- 测试 fixture 副本：`tests/AxonHub.Llm.Tests/testdata/openai/` 两份（anthropic 已有）。

## 测试

- `tests/AxonHub.Llm.Tests/FakeTransformerTests.cs` 6 例：Anthropic 请求 URL /
  X-API-Key `fake` / 审计键 null、响应体与参照固件逐字节相等、10 个流事件逐项相等
  （message_start … message_stop）；OpenAI 请求 URL / Bearer `fake` / 审计键 null、
  响应体逐字节相等、68 个流事件逐项相等且 Type 为空、末事件 Data 为 `[DONE]`。
  两个 `Do` / `DoStream` 都通过一个会抛异常的网络执行器证明 CustomizeExecutor 完全
  接管传输。
- `tests/AxonHub.Api.Tests/FakeChannelWiringTests.cs` 2 例：空 credentials 的
  anthropic_fake / openai_fake 候选经工厂得到对应 fake transformer，请求 URL 正确，
  自定义执行器返回固件响应（不触网）。
- 实测：Llm 6 通过 / 0 失败；Api 2 通过 / 0 失败（2026-11-13）；
  `AxonHub.Api.Tests` 未跑全量。

## 台账

C126 → resolved（resolved 累计 +1，gap 5 → 4）；新增 C280（accepted：EmbeddedResource
与 go:embed、bufio.Scanner 单行上限、JSON 解码错误文案、null 行、审计键为 null、
异常与 error 模型、每实例内层 outbound）。`diff_register.py lint` 通过；
`02-differences.md` 已重新生成；280 条（205 accepted / 4 gap / 71 resolved）。
