# §265 工具输出紧跟其 tool call

## 背景

live 提交 `5659a526`（#2633）：Responses 入站的
`convertInputToMessages` 按数组顺序输出消息。客户端可以在 `function_call` 与
`function_call_output` 之间插入 message，转换后得到
`assistant(tool_calls) → system/user → tool`，Chat Completions 上游会以
400 `tool_calls must be followed by tool messages` 拒绝。reasoning 项合并出
的 tool call 同样会产生这种顺序。

## 实现

`ResponsesInboundConvert.ConvertInputToMessages`：

- 新增与输入项等长的 `emitted` 标记；
- 发出 assistant 消息后调用 `CollectToolCallOutputs`：从当前位置向后扫描
  尚未发出的 `function_call_output` / `custom_tool_call_output`，只收集
  call_id 属于该 assistant 消息 tool_calls 的项，按输入顺序立即追加，并标记
  为已发出；
- reasoning 合并分支以 `i + consumed` 为起点收集；
- 其他调用的输出不提升；本就相邻的顺序与输出保持不变；
- 外层扫描跳过已发出的项，避免重复。

## 测试

移植 live 的
`TestConvertInputToMessages_KeepsToolOutputAdjacentAcrossAnInterveningMessage`
六个子场景：system / user 消息插在调用与输出之间、custom tool 输出、
reasoning 合并调用、多个调用按输入顺序收集输出、后续调用的输出不提升。

结果：Responses 定向 **445/445**，Llm 全量 **3390/3390**。未跑 Api 全量。

## 台账

- 新增 C324（resolved）。
- 324 条（190 accepted / 134 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 仅调整 Responses 入站消息的排列；请求/响应其他字段不变。
- 只移动与当前 assistant 消息 tool_calls 匹配的输出；未知 call_id 保持原位。
