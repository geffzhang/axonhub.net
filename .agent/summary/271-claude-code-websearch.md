# §271 Claude Code WebSearch 经 Responses 后端

## 背景

live `513a7822`（#2750）：Claude Code 的 web_search 经 Responses 后端时，
`web_search_call` 项只是元数据，不是客户端函数调用。端口此前只有元数据检测
与 citation 映射，没有 Anthropic 搜索块状态机。

## 实现

- `ResponsesOutboundConvert.AppendResponseWebSearchCallMetadata` 改为按 call ID
  合并稀疏快照：added / done / terminal 可描述同一次调用；缺失字段不删除既有
  值；来源按 URL 合并、标题非空时替换；terminal 状态保持 completed/failed。
- 新增 `AnthropicResponsesWebSearch`：
  - `Blocks` 生成 `server_tool_use` / `web_search_tool_result`；查询已知即可
    发进度块，结果只在 final 快照发出；失败生成
    `web_search_tool_result_error`（`unavailable`）；不伪造 `encrypted_content`。
  - 单搜索缺 sources 时才回退 answer 的结构化 URL citations；多搜索不归因。
  - 标题匹配忽略 `utm_*` 参数，但结果 URL 保持原样；query 顺序与重复调用
    按首次观察顺序。
  - `AppendCitations` 只接受 `url_citation` / `web_search_result_location`
    的绝对 http(s) URL。
- 非流式 `ResponseFromLlm` 在内容前插入搜索块，并用 message annotations 做
  citation 回退。
- 流式 `AnthropicInboundStream`：逐 chunk 累积 citations、按
  `finish_reason != null` 发 final，流结束时再 flush 一次；发射前关闭打开的
  客户端内容块，server_tool_use 先发 `{}` 再发 input_json_delta。
- `AnthropicContentBlockJsonConverter` 对特殊 `*_tool_result` 内容保留 raw
  JSON（含搜索错误对象）。

## 测试

新增 `AnthropicResponsesWebSearchTests` 20 例：观察式调用追踪、来源去重、
标题回退、utm 归一化、late details、调用顺序、citation 边界、结构化 URL
过滤、非流式响应、citation 回退、流式去重与 late sources、元数据按 ID 合并。

结果：Responses/Anthropic 定向 **743/743**，Llm 全量 **3443/3443**。未跑
Api 全量。

## 台账

- 新增 C328（resolved）。
- 328 条（189 accepted / 139 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 不从生成文本中猜测来源；OpenAI 不提供的 encrypted_content 保持缺失。
- 前端 Claude Code WebSearch 展示不在后端移植范围。
