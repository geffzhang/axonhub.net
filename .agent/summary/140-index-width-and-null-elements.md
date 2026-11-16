# §140 index 宽度与 Go 值结构 null 语义（C022 闭合）

日期：2026-11-08
参照：`llm/model` 的统一模型、`llm/transformer/*` 的 wire 模型与响应转换、
`internal/objects/channel.go` 的 `ParseOverrideOperations`。

## 交付

- 统一模型 index 加宽：`Choice.Index`、`ToolCall.Index`、`CompletionChoice.Index`、
  `EmbeddingData.Index`、`RerankResult.Index` 由 `int` 改为 `long`（Go `int` 在参照平台为
  64 位）。
- 线上模型 index 同步加宽：OpenAI chat/completion/embedding、Jina、Cline、OpenRouter、
  Nanogpt、Gemini outbound 的 index 字段。
- 流聚合与工具参数键随 index 加宽：`OpenAiAggregator`、`OpenAiInboundStream`、
  `ResponsesInboundStream`、`AnthropicInboundStream`、`GeminiInboundStream`、
  `BailianOutboundTransformer` 的字典键与内部计数改为 `long`。
- Go 值结构切片的 null 元素按 Go 解码为零值：`OpenAiConvertToLlm`（choices、
  multiple_content、logprobs）、`OpenAiAggregator`（choices）、`OpenAiOutboundImage`
  （image data）、`ResponsesImageConvert` / `ResponsesOutboundConvert`（output、sources）、
  `JinaOutboundTransformer`（rerank results、embedding data）、`ClineModels`
  （choice、reasoning_details）、`AnthropicOutboundResponse`（citations）、
  `OpenAiOutboundEmbeddingModeration`（embedding data）。
- Api override 解析放宽：`OverrideOperationDraft.Index` 改为 `long?`，
  `ChannelOverrideOperations.ReadOperation` 改用 `TryGetInt64`；GraphQL 输出按 C190 做
  checked 截断。

## 行为要点

- `tool_calls[].index=3000000000` 这类 Go 接受的值不再被 int32 拒绝，流聚合按同一 64 位键
  合并。
- Go 的 `encoding/json` 把结构体切片的 null 元素解码为零值结构；端口在转换入口替换，
  指针切片（`[]*Candidate`、`[]*Part`、`[]*Embedding`、`*RerankDocument`、流式
  `*TextCitation`）保持 null，与 Go 的 `[]*T` 一致。
- 残余：直接由 STJ 绑定的普通 `List<string>` 字段对 null 元素保留 null（Go 为 `""`），
  登记为 C224 已接受差异；`OpenAiStop` 等手写转换器已按 Go 补空串。

## 测试

- `tests/AxonHub.Llm.Tests/OpenAiRequestTypeCheckTests.ToolCallIndexBeyondInt32DecodesLikeGoInt`：
  64 位 index 解码。
- `tests/AxonHub.Llm.Tests/OpenAiResponseNullSemanticsTests.cs`：5 例——choices、
  content parts、logprobs、embedding data、流式分块中的 null 元素。
- `tests/AxonHub.Llm.Tests/OpenAiImageOutboundTests.cs`：image data null → 零值。
- `tests/AxonHub.Llm.Tests/JinaRerankTransformerTests.cs`：rerank results / embedding data
  的 null 元素 2 例。
- `tests/AxonHub.Llm.Tests/ClineOutboundTransformerTests.cs`：choice 与 reasoning_details
  null 元素。
- `tests/AxonHub.Llm.Tests/AnthropicOutboundTransformerTests.cs`：citation null 元素。
- `tests/AxonHub.Llm.Tests/ResponsesOutboundTransformerTests.cs`：output null 元素。
- `tests/AxonHub.Api.Tests/ChannelOverrideOperationsTests.cs`：override index 3000000000
  保留为 long。

静态计数：`AxonHub.Llm.Tests` 1468 → 1480（+12）、`AxonHub.Api.Tests` 5402 → 5403（+1）；
四项目合计 8881 → 8894。**按项目规则未运行 lint / build / test。**

台账：C022 由 gap 改为 resolved，新增 C224（accepted）（224 条：161 accepted /
54 resolved / 9 gap）。
