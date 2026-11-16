# §137 所有 double 字段的 Go 数序格式化（C067 闭合）

日期：2026-11-08
参照：Go `encoding/json` 的 float64 编码（`1e-6 ≤ |v| < 1e21` 定点、之外科学计数、负指数去前导零；
NaN / Infinity 写出失败）。涉及统一模型（`llm/model.go`）与全部 transformer wire 结构。

## 交付

- `src/AxonHub.Llm/Json/GoDoubleJsonConverter.cs`：options 级 `JsonConverter<double>`，
  `Write` 经 `GoJsonFloat.Format` 的 `WriteRawValue` 输出；`Read` 走 `Utf8JsonReader.GetDouble()`
  （与 System.Text.Json 内置 double converter 相同的 token 检查与数字语义）。
- `src/AxonHub.Llm/Json/LlmJson.cs`：在共享 `LlmJson.Options` 注册该 converter。
- `src/AxonHub.Llm/Model/Embedding.cs`：`EmbeddingJsonConverter` 的向量元素由
  `WriteNumberValue` 改为 `GoJsonFloat.Format`。
- `src/AxonHub.Llm/Model/Moderation.cs`：`category_scores` 手写 writer 同步改为 `GoJsonFloat`。

## 行为要点

- 统一模型与全部 transformer 载荷（`GoJson.Marshal` / `LlmJson.Serialize`）里的 double
  在任意量级与 Go 一致：`0.000005`、`1e-7`、`1e+21`，不再出现 `5E-06` / `1E+21`。
- `double?` 由 System.Text.Json 自动套用可空包装；字典值与数组元素经 `GetConverter` 解析到
  同一 converter；属性级 `GoOptionalNumberConverter`（cost 等）仍然优先。
- 读取仍接受整数与 E/e 指数写法；NaN / Infinity 与 Go 一样在写出阶段失败。
- 不影响 GraphQL（HotChocolate）、持久化（EF Core / Dapper）与自有 options 的管理面 codec
  （`BillingJson` / `ModelSettingsCodec` / `ModelCard` 已各自按 Go 规则格式化）。

## 测试

- `tests/AxonHub.Llm.Tests/GoDoubleConverterTests.cs`：13 例——
  请求标量 6 个 InlineData（0.000005 / 1e-7 / 1.5e-7 / 1e20 / 1e21 / 0.7 覆盖 temperature、
  top_p、frequency_penalty、presence_penalty）、logprob 3 个 InlineData、可空字段省略、
  embedding 向量、category_scores 字典、Go 与 STJ 两种指数写法的读取往返。

`AxonHub.Llm.Tests` 由 1455 增至 1468（+13），四项目合计由 8837 增至 8850（`AxonHub.Api.Tests`
5371、`AxonHub.Persistence.Tests` 331、`AxonHub.Grains.Tests` 1680 不变）。**按项目规则未运行
lint / build / test，上述为 `[Fact]` / `[InlineData]` 静态计数；下一次全量运行应以 5371 / 331 /
1468 / 8850 复核。**

## 台账

- `porting/differences.jsonl`：C067 → resolved（what / cause / decision / test 重写为已闭合状态）；
  `lint` 通过，`render` 重新生成 `02-differences.md`（222 条：160 accepted / 51 resolved / 11 gap）。
- `porting/01-baseline.md`：新增 §137。
- `porting/03-acceptance.md`：追加 §137 静态计数段落。

## 下一步

- 剩余真实缺口：C014、C022、C044、C072、C091、C092、C094、C096、C097、C126、C135。
