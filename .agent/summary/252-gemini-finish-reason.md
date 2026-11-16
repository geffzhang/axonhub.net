# §252 Gemini 流式 tool_calls 完成原因跨 chunk 保持

## 背景

live 提交 `d6e638ad`（#2713）：Gemini 流式响应可能把 `functionCall` part 放在
一个 chunk，把 `finishReason` 放在后续不带 parts 的 chunk。参照原实现逐 chunk
映射完成原因，于是后续 `STOP` 变成 `stop`（Anthropic 客户端变成 `end_turn`），
而同一批 chunk 的非流式聚合却得到 `tool_calls`。

## 实现

- `GeminiOutboundStream.StreamState` 增加 `ToolCallChoices`（按 choice.Index
  记录 candidate 是否已流式发送 function call）。
- chunk 经 `ConvertGeminiToLlmResponseWithState` 转换后，若 `Delta.ToolCalls`
  非空则记入集合；集合命中时用 `hasToolCall: true` 重新映射该 candidate 的
  `finishReason`（STOP→tool_calls、MAX_TOKENS→length）。
- 未发送过 function call 的 candidate 保持原映射，多 candidate 各自独立。
- `TransformStreamChunk` 的单 chunk 路径仍使用新的空状态，行为不变。

## 测试

- `TransformStream_KeepsToolCallsFinishReasonWhenStopArrivesLater`：函数调用
  chunk + 仅 STOP 的后续 chunk → `tool_calls`。
- `TransformStream_TracksToolCallFinishReasonPerCandidate`：candidate 0 发函数
  调用、candidate 1 发文本，后续 STOP → 分别为 `tool_calls` / `stop`。
- `TransformStream_MaxTokensAfterAToolCallStaysLength`：后续 MAX_TOKENS →
  `length`。
- Gemini 流式定向 29/29；Llm 全量 **3327/3327**。未跑 Api 全量，新增 Llm 3 例。

## 台账

- 新增 C314（resolved）。
- 314 条（190 accepted / 124 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 状态按 `choice.Index` 记录，与参照的 candidate index 键一致；未知/重复索引
  沿用列表顺序。
- 非流式聚合路径不经过 `StreamState`，行为不变。
