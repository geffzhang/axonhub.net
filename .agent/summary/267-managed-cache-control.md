# §267 转换请求的 Anthropic 自动缓存与托管平台块级归一化

## 背景

live 三个提交：

- `697b13a5`（#2697）：转换请求（`APIFormat` 非空且不是
  `anthropic/messages`）没有原生 cache 字段，应自动开启 Anthropic 顶层
  `cache_control={type:ephemeral}`；
- `28cab912`（#2702）：Vertex / Bedrock 只接受块级标记，顶层标记需移到最后
  一个可缓存块，并处理显式断点、1h 顺序、容量与 raw 内容；
- `8d3c3865`（#2703）：共享 cache_control 槽位遍历。

## 实现

`AnthropicCacheControlOptimizer`：

- 新增 `MoveTopLevelCacheControlToLastBlock`：
  - 顶层标记先摘下；
  - 末尾已有同 TTL（空串 / `1h`）断点时直接复用；
  - 四断点已满时失败；
  - 1h 标记必须放在已有短 TTL 断点之前，否在成功放置前返回 false；
  - 否则放到最后一个未占用槽位，且不越过已有 1h 断点。
- 新增 `NormalizeAndCollectCacheControlRefs`：归一化字符串 message/system
  内容，收集 tools、system pills 与所有可缓存消息块（含 raw 块）。
- 新增 raw 槽位：移动标记时保留 `vendor_field` 等原始字段，只增删
  `cache_control`。
- `AnthropicOutboundTransformer`：
  - 无顶层标记且无显式断点时，按 `ShouldEnableAutomaticCacheControl` 决定
    是否开启顶层自动缓存；
  - Vertex / Bedrock 调用 `MoveTopLevelCacheControlToLastBlock`，失败返回
    `invalid_request`。

## 测试

- `AnthropicCacheControlOptimizerTests`：移动到最后块、1h 先于短断点、
  不安全时保留、raw 字段保留、同 TTL 复用（空 / 1h）。
- 新增 `AnthropicManagedCacheControlTests`：Vertex / Bedrock 的转换请求、
  原生顶层归一化、显式+顶层共存、满 4 断点集合、Direct 顶层保留、原生无
  元数据不开启。

结果：Anthropic cache/outbound 定向 **74/74**，Llm 全量 **3419/3419**。
未跑 Api 全量（新增 Llm 16 例）。

## 台账

- 新增 C326（resolved）。
- 326 条（190 accepted / 136 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 自动缓存只针对转换请求；原生 Anthropic 请求保持客户端显式行为。
- raw 块移动沿用 Go 的 map 序列化路径；未知字段与插入顺序按现有
  `GoJson` 实现处理。
