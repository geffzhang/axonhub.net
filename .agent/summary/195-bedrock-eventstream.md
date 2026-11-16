# §195 Bedrock AWS EventStream 流式解码（C056 闭合，39 例）

日期：2026-11-13
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`llm/bedrock/register.go`、
`llm/bedrock/stream.go`、`llm/bedrock/stream_test.go`，`llm/httpclient/client.go:408-426`
（解码选择）、`llm/httpclient/decoder.go:218-229`（注册表），以及
`aws-sdk-go-v2/aws/protocol/eventstream@v1.7.10` 的 decode.go / message.go / header.go /
header_value.go。

## 背景

C056 的剩余缺口是 Bedrock 流式响应：参照把 `application/vnd.amazon.eventstream`
注册进 httpclient 解码表（anthropic outbound 以空导入触发），`invoke-with-response-stream`
的响应是 AWS EventStream 二进制帧——prelude（总长 + 头长 + prelude CRC32）、headers、
payload、尾部 message CRC32；chunk 事件的 payload 是 `{"bytes": "<base64>", "p": ...}`，
`bytes` 解出后才是 Anthropic SSE 事件。端口此前只有 SSE 与 NDJSON 解码器，按 SSE 处理
会把二进制帧当文本，无法解析。

## 交付

- 新增 `src/AxonHub.Llm/Http/HttpAwsEventStream.cs`（`IStream<StreamEvent>`）：
  - 帧层：两次独立 uint32 读（4 字节尾巴是干净 EOF、2 字节是 `unexpected EOF`）、
    `Length == 0` 报 `message prelude length invalid, 16/0, <nil>`、prelude 与 message
    两道 CRC32（IEEE，`message checksum mismatch`）、headers 限长读取、payload 增量读取。
  - headers：十种值类型（bool/int8/16/32/64、byte array、string、timestamp、uuid）、
    重名 last-wins、name 长度或 name 本体遇到 EOF 即视为 header 段结束。
  - 分派：缺 `:message-type` / `:event-type`、未知消息类型的文案与参照一致；
    exception 与 error 的 code/message 回退（header → payload `code` → `__type` →
    `UnknownError`）逐项对齐；chunk 的 `bytes` 按 Go StdEncoding 规则解码（忽略
    `\r`/`\n`、长度 4 的倍数、padding 只允许在末组），事件 Type 取 `type` 字段。
  - 生命周期：EOF 与解码错误都关闭读取器（Close 错误丢弃），事件级协议错误只置 Err。
- `src/AxonHub.Llm/Http/HttpExecutor.cs`：按注册表语义选择解码器——精确 Content-Type
  优先，其次 `mime.ParseMediaType` 后的媒体类型；Bedrock 类型走 EventStream，其余维持
  binary/NDJSON/SSE 顺序。
- `src/AxonHub.Llm/Json/GjsonRootKeyProbe.cs`：新增 `GetString`，复刻
  `gjson.GetBytes(decoded, "type").String()`（首个 `{` 起宽松扫描、首个键命中、
  字符串反转义、对象/数组原文、`true`/`false`、整数原文、其余数值
  `FormatFloat('f',-1,64)`、null 与缺失为空）。

## 测试

- 新增 `tests/AxonHub.Llm.Tests/HttpAwsEventStreamTests.cs` 39 例：参照 `stream_test.go`
  的 9 个场景（chunk、exception、error、缺 header、未知类型、非法 chunk JSON、
  base64 失败、多事件、空流）+ header 全类型/未知类型/重名覆盖、prelude 零长与两道
  CRC、4 字节与 2 字节尾巴、header 内截断、Dispose 幂等、非 chunk 事件不清空 Current、
  gjson 语义 11 例（字符串/整数/1e3/1.5/true/false/null/缺失/对象/数组/非法 JSON）、
  DoStream 精确与 `charset` 变体路由。
- 实测（2026-11-13）：Llm 新增 39 通过 / 0 失败；相关回归 223 通过 / 0 失败
  （HttpExecutor / HttpNdjsonEventStream / OpenAiRequestTypeCheck / CodexOutboundExecutor /
  HttpRedirect）。`AxonHub.Api.Tests` 未跑全量。

## 台账

C056 → resolved（原 accepted 的真实缺口闭合）；新增 C281（JSON/base64 错误文案与字节
下标差异）与 C282（gjson 数值/非 UTF-8 极端差异），均 accepted。`01-baseline.md` 新增
§195 段并更新两处旧 C056 表述；`03-acceptance.md` 风险表 C056 行改为已关闭并追加 §195
小节。lint 通过，`02-differences.md` 已重新生成；282 条（206 accepted / 3 gap /
73 resolved）。
