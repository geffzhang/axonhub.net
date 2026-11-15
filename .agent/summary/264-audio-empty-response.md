# §264 音频消息计入空响应判定

## 背景

live 提交 `b6d184a8`（#2634）：`hasMessageContent` 检查文本、多段内容、tool
calls、reasoning、reasoning signature 与 refusal，却没有检查 `msg.Audio`。只
返回音频的 Chat Completions 响应因此被判为空，pipeline 抛
`ErrEmptyResponse`、在同通道重试，甚至在上游已成功并计费后以 HTTP 500 结束。

## 实现

`ResponseContent.HasMessageContent` 在 refusal 之后增加音频分支：

- `msg.Audio` 的 `Data` 或 `Transcript` 非空时视为内容；
- 存在但字段都为空的 audio 对象仍视为空，与 `hasResponseContent` 的
  speech / speech-stream 门一致；
- 检查位于 `HasMessageContent`，因此 `Message` 与流式 `Delta` 都被覆盖。

## 测试

移植 live 的 4 个用例到 `EmptyResponseTests`：

- 音频 data；
- 仅 transcript；
- 空 audio 对象（仍为空）；
- 流式 delta 音频。

结果：`EmptyResponseTests` 定向 **28/28**，Llm 全量 **3389/3389**。未跑
Api 全量。

## 台账

- 新增 C323（resolved）。
- 323 条（190 accepted / 133 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 仅影响空响应探测，不改变音频请求/响应的线路形状。
- 其他响应类型（speech、transcription、speech stream）的既有门不变。
