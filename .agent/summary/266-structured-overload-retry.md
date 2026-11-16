# §266 结构化 Codex overload 失败可重试

## 背景

live 提交 `36364049`（#2657）：Responses 流的 `response.failed` 若带结构化
`error.code`，且尚未提交任何有意义内容，应作为可重试的上游错误暴露，而不是
`finish_reason=error` 的终态。否则 orchestration 看不到 code，通道配置的
`retryableErrorPatterns`（例如 `server_is_overloaded`）无法匹配，请求直接失败。

## 实现

- `ResponsesOutboundStream`
  - 新增 `_contentCommitted`；`Enqueue` 按 `ResponseHasMeaningfulContent`
    标记内容提交（文本、多段内容、tool calls、refusal、reasoning 三类）。
  - `response.failed`：内容未提交且 `response.error.code` 非空时，若有
    usage 先入队一个空 choices 的 usage chunk；随后抛出 `ResponseError`。
  - `ResponseFailedError`：状态码 400..599 原样，否则 502；Detail 带
    code/message/type/param；`SuppressStatusText=true`；
    `CauseException=LlmErrors.EmptyResponse`。
  - `Next`：队列排空后先检查 `_err`；transform 抛错时先继续循环交付已排队
    chunk，再终止，失败后不再读取源。
  - 无 code 的畸形失败仍走旧的 `finish_reason=error` 终态。
- `LlmErrors.EmptyResponse` 新增；`PipelineErrors.EmptyResponse` 改为别名。
- `ResponseError` 新增 `SuppressStatusText` 并用于 `ToString`。
- `GoErrors` 展开时优先沿 `ResponseError.CauseException`，否则走
  `InnerException`，使 `errors.Is(err, ErrEmptyResponse)` 与实时一致。

## 测试

- `ResponsesOutboundStreamTests`：11 组状态码（null/0/200/399/600/-1/字符串
  → 502；400/429/500/599 原样）；usage 先于错误交付、失败后不再消费源；
  畸形 `response.failed` 回退。
- `ChannelRetryPredicateTests`：结构化 code 匹配、不匹配、无模式 3 例。
- 结果：Responses/EmptyResponse/Retry 定向 **94/94**，
  `ChannelRetryPredicateTests` **19/19**，Llm 全量 **3403/3403**。未跑 Api
  全量（新增 Llm 13 例、Api 3 例）。

## 台账

- 新增 C325（resolved）。
- 325 条（190 accepted / 135 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 仅在“尚无有意义内容”时把结构化失败暴露为可重试错误；已有内容后仍按
  finish_reason 终态处理，避免重放已提交的输出。
- 这是流式 transformer 与重试判定的边界；实际重试次数仍由 pipeline /
  channel 策略决定。
