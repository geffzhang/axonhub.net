# §250 TypeSafe SystemOne 响应 envelope 解包

## 背景

live 提交 `c51972c6`（#2631 / #2636）：Cloudflare Workers AI 等网关把
SystemOne 响应包在通用信封 `{"success": true, "result": {...}}` 中。端口此前
把 raw body 直接解码成 `systemOneWireResponse`，信封键与 SystemOne 字段不冲突，
于是静默得到 `{"model":"","answers":null}`；`success:false` 也照样被当成成功。

## 实现

`TypeSafeOutboundTransformer.TransformResponse` 在类型化解码前解析 envelope：

- `success` 字段存在（包括显式 null）即按信封处理；非布尔值返回 502
  `systemone response envelope has an invalid success flag`。
- `success=true`：缺失或 null 的 `result` 返回 502 `... missing result`；
  成功后把 result 的原始 JSON 文本交给既有解码器，保留数字字面量；解码后
  model 为空且 answers 为空/缺失返回 502 `... result is empty`。
- `success=false`：依次从 `error` / `errors` / `messages` 提取字符串、
  `{"message":...}`、`{"detail":...}` 或数组递归拼接（`; `），为空时回退
  `upstream request failed`；统一返回 502 `api_error`。
- 无 `success` 的普通 SystemOne body 走原路径；null body 仍得到零值响应。
- 非对象根改为返回参照的 `typesafe.systemOneWireEnvelope` 类型错误。
- 字段查找按 ASCII 大小写折叠并保留最后一个重复键，与 encoding/json 一致。

## 测试

- `TypeSafeSystemOneTests` 新增 14 例：成功解包、失败错误数组、缺失 result、
  六种错误文案形状（string / message / detail / errors 数组 / messages 数组 /
  无文案回退）、五种畸形信封（null/bool 字符串 success、空 result、无关 result、
  null result）；根数组断言改为 envelope 类型错误。
- TypeSafe 定向 70/70；Llm 全量 **3324/3324**。未跑 Api 全量。

## 台账

- 新增 C312（resolved）。
- 312 条（190 accepted / 122 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 仅 TypeSafe SystemOne 响应路径；其他 provider 的信封语义不变。
- result 经 `GetRawText()` 回填，保留原始数字字面量；整体 JSON 语法已在前面
  校验，畸形 result 会按既有解码错误文案处理。
