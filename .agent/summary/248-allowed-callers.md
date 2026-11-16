# §248 Anthropic allowed_callers 工具字段

## 背景

live 提交 `8cdc33a1`（#2504 / #2639）为 Anthropic function/custom 工具在 wire
`Tool` 与统一 `llm.Function` 上增加 `allowed_callers`，并在入站/出站两个
方向复制。端口此前完全没有该字段：客户端传入的 `allowed_callers` 在入站
转换时被丢弃，出站也不会写回。冻结基线 `b8bb3421` 早于该修复，而项目自
§216 起以 live 为规格，因此登记为 C310。

## 实现

- `AxonHub.Llm.Model.Function` 增加 `AllowedCallers`：
  - JSON 名 `allowed_callers`；
  - getter 在列表为空时返回 `null`，复刻 Go `omitempty` 的“缺席与空列表都不
    出现”语义；
  - 反序列化保留原始列表（空列表读入后属性仍为空）。
- `AnthropicTool` 增加同名/同语义字段，位置与参照 `anthropic.Tool` 一致
  （`strict` 之后、`allowed_domains` 之前）。
- `AnthropicConvertToLlm.ConvertToolToLlm`：function/custom 分支复制
  `tool.AllowedCallers`。
- `AnthropicOutboundRequest.ConvertToolsAnthropic`：function 分支复制
  `tool.Function.AllowedCallers`。
- web_search 等 server tool 分支保持原样，不写该字段。

## 测试

- 入站：`ConvertsAllowedCallersOnFunctionTools` 断言 Anthropic 请求的
  `allowed_callers: ["direct"]` 进入统一 `Function`。
- 出站：`TransformRequest_FunctionToolPreservesAllowedCallers` 断言线路 JSON
  含 `"allowed_callers":["direct"]` 且紧凑数组。
- 缺席：`TransformRequest_AbsentAllowedCallersStayOffTheWire` 以 nil / empty
  两例断言线路和统一 Function JSON 都不出现该键，且入站缺失字段保持 null。
- Anthropic 相关 Llm 回归 **248/248**，Llm 全量 **3302/3302**；未跑 Api 全量，
  新增 Llm 4 例待下次全量计入。

## 台账

- 新增 C310（resolved）。
- 310 条（190 accepted / 120 resolved / 0 gap）；`diff_register.py lint`
  通过；`02-differences.md` 已重新生成。

## 边界

- `allowed_callers` 只对 Anthropic function/custom 工具有意义；其他 provider
  的 transformer 继续丢弃该字段，与 live 注释一致。
- 空列表在统一模型中归一为 null，但出站线路与参照同为“不出现”，属不可观察
  的内部表示差异。
