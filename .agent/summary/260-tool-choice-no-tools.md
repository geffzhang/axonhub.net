# §260 无工具时丢弃 tool_choice

## 背景

live 提交 `8bd3e931`（#2706）：请求可以携带 `tool_choice` 却不发送任何
`tools`（compaction 的 `"tools": []`、Responses-Lite 把工具放在 input），部分
OpenAI 兼容上游以 400 拒绝。省略 `tool_choice` 等价于默认 `auto`。

## 实现

- OpenAI chat（`OpenAiConvertFromLlm.RequestFromLlm`）：
  - `tools.Count == 0` 时同时清空 `ParallelToolCalls` 与 `ToolChoice`。
- Responses（`ResponsesRequestExtensions.MarshalRequestPayload`）：
  - 先合并 raw-only tools；
  - 再检查最终 body 是否有非空 `tools`：没有则删除 `tool_choice`，有则按原有
    `RawToolChoiceMatchesCurrentTools` 规则回放 raw `tool_choice`。
  - 这样 raw-only 工具（tool_search、namespace 等）被恢复时不会误删
    tool_choice；payload 自带的 tool_choice 也不会在无工具时漏出。

## 测试

- `OpenAiToolChoiceTests` 2 例：无 tools 丢弃、有 tools 保留。
- `ResponsesOutboundTransformerTests` 2 例：raw tool_choice 无 tools 被删除、
  raw tools 恢复后保留。
- 定向 65/65；Llm 全量 **3367/3367**。未跑 Api 全量。

## 台账

- 新增 C319（resolved）。
- 319 条（190 accepted / 129 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 只在出站线路层丢弃；统一请求上的 `ToolChoice` 保留，重试或调度层不受影响。
- 合并 raw 字段的其他行为不变。
