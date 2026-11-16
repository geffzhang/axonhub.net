# §261 无 index 的流式 tool_calls 自动编号

## 背景

live 提交 `19862baf`（#2707）：Gemini 的 OpenAI 兼容端点把每个并行 tool
call 整体放在独立 chunk，且不带 `tool_calls[].index`。所有调用都解码为
index 0，流式和聚合结果会把多个调用合并成一个，参数字符串被直接拼接。

## 实现

- 新增 `ToolCallIndexer`：
  - `next[choice]`：下一个新调用的 index；
  - `last[choice]`：最近一次 delta 所属调用的 index；
  - `ids[(choice,index)]`：每个调用的 ID。
  - raw chunk 中 `choices[pos].delta.tool_calls[j].index` 存在时保留显式
    index，并把 `next` 推到 `index + 1`；
  - 否则：有新 ID 或新 name 时分配 `next` 并递增；空片段或重复最近调用的
    ID 时续接 `last`；
  - 记录 `last` 与该 index 的 ID。
- `OpenAiOutboundStream.TransformStream`：对每个非 DONE 的统一响应应用。
- `OpenAiAggregator.AggregateStreamChunks`：对每个解码后的 wire chunk 应用，
  再按 index 聚合。
- 两组循环都跳过 null choice 元素（C022 语义）。

## 测试

- 两个无 index 的完整调用得到 index 0 / 1。
- 空片段续接同一调用（index 0）。
- 显式 index 5 后，无 index 新调用得到 6。
- 聚合两个无 index 调用得到两个 tool call，参数分别为 Paris / Tokyo，不再
  拼接。
- 定向 100/100；Llm 全量 **3371/3371**。未跑 Api 全量。

## 台账

- 新增 C320（resolved）。
- 320 条（190 accepted / 130 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 只对缺少 index 的 delta 生效；有显式 index 的 chunk 原样保留。
- 与参照相同的 raw JSON 路径假设：响应 choices/tool_calls 与 raw chunk 顺序
  一致。
