# §274 Codex 原生 Images 端点

## 背景

live `3cb7e333`（#2651）：Codex 通道的 Images 请求不再转换成 Responses
`image_generation` 工具，而是走原生 OpenAI Images 端点。参照同时为图片编辑
增加 JSON 引用形式（data URL / HTTP URL / `file_id`）与 Codex 专用身份头。

## 实现

- `CodexImageOutboundTransformer`（新）：
  - 包装 `OpenAiOutboundTransformer`，固定 `ImageEditJson=true`；
  - 使用 Codex OAuth token provider，设置 Bearer、Originator、Session-Id
    与 Chatgpt-Account-Id；
  - 透传 Originator/User-Agent/Codex passthrough 头；
  - `AllowPassThroughBody` 对 image_edit 返回 false。
- `CodexOutboundTransformer` / `CodexExecutor`：image 请求直接
  `invalid_request`，删除 Responses image 桥与 `ImageMainModel`。
- `ResponsesOutboundTransformer`：image 请求改为
  `native Images endpoint is required for image requests`。
- `ImageRequest` 新增 `ImageReferences` / `MaskReference` 与
  `ImageReference`；JSON image edit 解析支持 data URL、HTTP URL、`file_id`、
  mask 引用与未知字段校验。
- OpenAI image outbound：新增 `BuildImageEditJsonRequest` 与 `ImageEditJson`
  配置，按 references 数量/显式开关选择 JSON 或 multipart。
- 通道工厂：codex/fenno 的 `image_generation` / `image_edit` 端点路由到
  `CodexImageOutboundTransformer`。

## 测试

- Codex：拒绝无原生端点的 image 请求、原生 generation 请求、JSON edit
  file/HTTP 引用。
- ResponsesImage：拒绝 image 请求。
- OpenAI Image inbound：HTTP URL 引用与非法引用文案。
- Api wiring：codex/fenno image 端点使用原生 image outbound。

结果：Codex **116/116**，Image **246/246**，Api Codex/image **69/69**，
Llm 全量 **3437/3437**。未跑 Api 全量。

## 台账

- 新增 C330（resolved）。
- 330 条（189 accepted / 141 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 图片引用仅支持 data URL、HTTP(S) URL 与 file_id；其他对象字段按 invalid
  reference 拒绝。
- 原生 image 响应的既有 usage 投影保持不变。
