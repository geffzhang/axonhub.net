# §263 EOF 处未终止的最终 SSE 行

## 背景

live 提交 `cbdf9a6d`（#2692）：synthetic.new 这类上游以没有尾随换行的
`data: [DONE]` 结束 SSE 响应体。go-sse 的字段解析器在 EOF 丢弃未终止的最后
一个字段，终端事件因此丢失；请求在完整回答后被持久化为 canceled，客户端收到
`upstream_stream_interrupted`。

参照用 `newlineTerminatingReader` 包裹读取器：EOF 时若 body 非空且最后一个
字节不是 `\n` 或 `\r`，先合成一个换行。恢复出的 `[DONE]` 由既有的
literal-`[DONE]` 终止分支记为 completed。

## 实现

端口的解码器是自写的，本来就会在 EOF flush 未结束的记录，因此事件本身不会
丢失。缺的是与参照一致的帧/大小语义：

- `HttpSseEventStream.ReadLine`：EOF 返回未终止末行时，报告该行字节数，并
  计一个合成终止符（之前末行字节数为 0）；
- `SseDecoder`：当记录恰好在 body 末尾结束且最后一个字符不是 `\n` / `\r`
  时，事件大小再加 1 字节（合成换行）；
- 已终止行、以孤立 CR 结尾的 body、空 body 均不额外计数；
- 截断的 JSON 仍是截断的 data 事件，绝不改写成 `[DONE]`。

## 测试

新增 `HttpSseEventStreamTests`（10 例）：

- 复制 live 的 synthetic 终止/未终止 fixture，二者都以 `[DONE]` 结束；
- 断言 fixture 对只差 `\n\n` 行终止；
- 裸 `data: [DONE]` 得到唯一 `[DONE]` 事件；
- 截断 JSON 不伪造终端；
- 合成换行计入 `MaxEventSize`（12 字节 body + 换行 > 12）；
- 恰好达到上限的已终止事件被接受；
- 孤立 CR 不合成新行；
- 空 body 不产生事件；
- `SseDecoder` 字符串路径的恢复与大小计数一致。

结果：SSE/Executor 定向 **19/19**，Llm 全量 **3385/3385**。Api 侧持久化状态
断言未单独移植；解码器现在与参照交付相同的 `[DONE]` 事件。未跑 Api 全量。

## 台账

- 新增 C322（resolved）。
- 322 条（190 accepted / 132 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 只对齐 SSE 解码器的帧恢复与事件大小上限；下游终止/持久化逻辑不变。
- 底层读取错误仍按 EOF 处理（既有 accepted 行为，不在本切片范围）。
