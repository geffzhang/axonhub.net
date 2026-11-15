# §262 Responses done 事件不再重复 tool call 标识

## 背景

live 提交 `c06e896e`（#2686）：Responses 出站的
`response.function_call_arguments.done` chunk 会重复
`response.output_item.added` 已发送的 tool call id、type 与 name。Chat
Completions 客户端（OpenAI SDK）会跨 delta 拼接字符串字段，导致 call_id 与
name 翻倍；37 字符的 call_id 变成 74，下一次请求超出 64 字符上限（#2648）。

## 实现

`ResponsesOutboundStream` 的 done 分支按参照拆分：

- `nameChanged`：done 事件带非空 name 且与已记录 name 不同；
- `namespaceChanged`：done 事件带非空 namespace 且与已记录不同；
- `identityChanged = nameChanged || namespaceChanged`；
- done delta 只设置 `Index` 与缺失参数后缀 `Arguments`；
- `name` 仅在 `nameChanged` 时写入（未变化时保持空串，与 Go 无
  `omitempty` 的零值序列化一致）；
- `namespace` 仅在 `identityChanged` 时写入（空值按 null 省略，对应 Go 的
  `omitempty`）；
- 不再写 `Id` / `Type`。

参数后缀的计算与错误语义保持不变（前缀补齐、等价 JSON 去重、不匹配抛错）。

## 测试

`ResponsesOutboundStreamTests.DoneDoesNotRepeatToolCallIdentity`：移植 live
表驱动 4 例，覆盖参数只在 done、剩余参数在 done、namespace 只在 done、name
只在 done 且保留 namespace。测试按 OpenAI SDK 的方式拼接
id/type/name/namespace/arguments，断言 `call_identity` 只出现一次、name
不被重复、namespace 保留、参数完整。

- Responses 相关定向 **448/448**
- Llm 全量 **3375/3375**
- 未跑 Api 全量

## 台账

- 新增 C321（resolved）。
- 321 条（190 accepted / 131 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 只影响 `function_call_arguments.done` 生成的 delta；`output_item.added`、
  `function_call_arguments.delta` 与 custom tool call 路径不变。
- namespace 的发送条件依赖 identity 变化，与参照注释一致：下游需要它把新
  name 拼成 `namespace__name`。
