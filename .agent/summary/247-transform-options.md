# §247 通道变换选项与 between_tools 对账

## 背景

C180 原记录四项通道变换与流策略强制“仅解析、出站未应用”。对账发现端口已在
`PersistentOutboundTransformer` 中实际应用：

- `ChannelTransformOptions.Apply`（ForceArrayInstructions / ForceArrayInputs /
  ReplaceDeveloperRoleWithSystem）；
- `ApplyReasoningEffortMapping`（含 `output_config.effort` 同步）；
- `FilterResponseCustomToolMessagesForNonResponsesOutbound`；
- `ShouldForceStreamingForCandidate`（require 通道强制流式并补 `include_usage`）。

唯一遗漏是 Anthropic 的 `thinking.type=between_tools`：端口入站校验拒绝该值，
入站/出站转换与通道 reasoning effort 映射也没有对应分支。

## 实现

- `AnthropicInboundTransformer`：`adaptive` 与 `between_tools` 共用 output_config
  校验；错误文案扩展为
  `thinking.type must be one of: enabled, disabled, adaptive, between_tools`。
- `AnthropicConvertToLlm.ConvertThinking`：`between_tools` 写入
  `thinking_type` metadata，统一 `ReasoningEffort` 保持 `none`；
  `output_config.effort` 只写 metadata，不再覆盖统一 effort。
- `AnthropicOutboundRequest`：新增公开的 `TransformerMetadataKeyThinkingType`；
  thinking 重建增加 `between_tools` 分支。
- `ChannelTransformOptions.ApplyReasoningEffortMapping`：当统一 effort 为 `none`
  且 metadata 为 `thinking_type=between_tools` + 非空 native effort 时，映射
  native effort 并置 `nativeOnly`，保留统一 `none`；metadata 字典仍先克隆再写。

## 测试

- `AnthropicInboundTransformerTests.MapsDisabledAndAdaptiveThinking`：扩展
  between_tools 的接受、`ReasoningEffort=none`、metadata 保留 `max`。
- `AnthropicOutboundTransformerTests.TransformRequest_BetweenToolsRoundTripsTheNativeThinkingType`：
  出站回写 `thinking.type=between_tools` 与 `output_config.effort=max`。
- `ChannelTransformOptionsTests.BetweenToolsMapsTheNativeEffortAndKeepsTheUnifiedNone`：
  native effort 映射为 `high`、统一 effort 仍为 `none`、原 metadata 不被改动。
- 定向：Anthropic 相关 Llm 244/244；Api 端点/编排 90/90；
  `ChannelTransformOptionsTests` 44/44。未跑 Api 全量，新增 Llm 1 例、Api 1 例。

## 台账

- C180 → resolved。
- 309 条（190 accepted / 119 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- `between_tools` 的 native effort 仅影响 Anthropic outbound；其他协议仍看到
  `none`，与参照一致。
- `ChannelTransformOptions` 对畸形 channel settings 仍按端口既有策略视为缺失
  （C157 家族），参照会在通道加载期失败。
