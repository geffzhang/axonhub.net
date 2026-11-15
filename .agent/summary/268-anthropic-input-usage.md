# §268 Anthropic 初始 input usage 估算与校准

## 背景

live 提交 `847e5919`（#2749）：Claude Code 的进度显示依赖
`message_start` 的 `message.usage.input_tokens`，但许多上游初始报 0。参照在
API 层（持久化之后、safeguards 之前）估算并校准客户端看到的初始值，同时保持
最终 usage、持久化与计费权威。

## 实现

- 依赖：`Microsoft.ML.Tokenizers` 3.0.0 +
  `Microsoft.ML.Tokenizers.Data.O200kBase` 3.0.0（中央包管理），使用
  `o200k_base` 词表。
- `AnthropicInputUsageEstimator`：解析请求体，仅统计文本证据——
  - system 与 messages 中的 text / thinking / tool_use /
    server_tool_use / mcp_tool_use / tool_result 族 / document /
    search_result / web_fetch_result；
  - tools 的 name / description / input_schema；
  - tool_choice；
  - 跳过 metadata、max_tokens 与编码媒体；以 `\n` 拼接后计数。
- `AnthropicInputUsageStream`：只包裹 `/messages`，保留 source 的
  delivery capabilities；仅当初始 input/cache_read/cache_creation 全为 0
  时替换客户端 `message_start` 的 `input_tokens`，不修改原事件对象。
- `AnthropicInputCalibration`：
  - 作用域 = project + API key + route（channel/model/API format）；
  - 指纹 = 规范化后的 system/tools/message 内容 + 累积消息前缀哈希；
  - 只在缺 stop/terminal、上游错误、取消、提前关闭之外的清洁完成后学习
    `actual - raw`；
  - 拒绝 TTL 过期、作用域不符、过期前缀、非严格祖先与溢出。
- `ChatCompletionOrchestrator`：在重试结束后快照最终 route，随
  `ChatCompletionResult` 返回。
- `ChatCompletionEndpoint`：流式路径在持久化之后、safeguards 之前包装。
- Anthropic 入站流初始 usage 从 1/1 改为 0/0；两个 fixture 与一条测试期望同步。

## 测试

- `AnthropicInputUsageTests`：文本证据、忽略 metadata/媒体、嵌套
  tool_result、非法 JSON、16×5 并发稳定。
- `AnthropicInputUsageStreamTests`：四种初始 usage、原事件不变、非
  `/messages` 跳过、六种清洁完成学习场景。
- `AnthropicInputCalibrationTests`：误差传递、作用域/TTL/过期前缀、溢出、
  指纹归一化与工具参数显著性、容量与并发、最终 usage 校验。

结果：Api Anthropic 相关 **90 通过 / 1 跳过**，Llm 全量 **3419/3419**。
`ChatCompletionEndpointTests.HiddenPolicyDoesNotRewriteClassifiedTransportErrors`
在基线 `0a98fcd` 同样失败，与本切片无关。未跑 Api 全量。

## 台账

- 新增 C327（resolved）。
- 327 条（190 accepted / 137 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 估算仅用于客户端显示，不写入最终 usage、响应持久化或成本。
- 校准缓存是进程内的；重启或驱逐只会回到未校准估算。
