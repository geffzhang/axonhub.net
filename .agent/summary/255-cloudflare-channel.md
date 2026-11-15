# §255 Cloudflare Workers AI 通道类型

## 背景

live 提交 `0d82a8e4`（#2674）新增 `cloudflare` 通道：一个账号级 base URL
同时承载 OpenAI 兼容的 chat/embedding 面与原生 `/run/{model}` decision 面。
System One 响应被 Cloudflare v4 envelope 包裹，普通 typesafe transformer 会在
HTTP 200 下解出空 answer。端口此前只有 GraphQL 枚举与默认端点表，没有
transformer、工厂分派、端点校验和通道测试支持。

## 实现

- `AxonHub.Llm.Transformer.Cloudflare`
  - `CloudflareSystemOneOutboundTransformer`：
    - `@cf/{author}/{name}` → `name` selector；
    - base URL 去掉尾部 `/v1`，endpoint path 作为 `/run` 的替代前缀，model 始终
      追加；
    - envelope 解包：`success:false` 或 `errors` 非空时按 502（4xx 保留状态）
      返回 `api_error`；`success:true` 但 result 缺失/非对象、answers 缺失/为空
      均显式报错；
    - `TransformError` 优先使用 envelope 错误的 message 拼接与首个 code，否则
      回退原始 body；
    - 同时实现 `IPassThroughBodyPolicy` / `IPassThroughResponsePolicy`，两者
      都 veto（raw envelope 不是客户端应得的 SystemOne payload）。
- `ChannelOutboundFactory`
  - cloudflare 主出站加入 OpenAI 兼容组，chat/embedding 使用普通 OpenAI 出站；
  - systemone 用户端点按通道类型分派到 Cloudflare transformer。
- `ChannelValidation`
  - `typesafe/systemone` 端点允许 typesafe、openai、cloudflare；其他类型返回
    参照文案。
- `ChannelTestService`
  - modelProtocols pin 的模型直接按其端点格式测试（embedding 不再被当作 chat）；
  - embedding 输入通过 OpenAI embedding inbound 构造并保护角色提示词；
  - embedding 响应返回维度数或 base64 结果。

## 测试

- `CloudflareSystemOneOutboundTransformerTests` 22 例：配置校验、7 种 URL、
  model selector、无效请求、pass-through veto、envelope/flat 响应、失败 envelope、
  无 answers、无效 body、TransformError 形状。
- `CloudflareChannelWiringTests` 7 例：chat 出站、systemone 端点出站、primary
  format、三类型端点白名单与其他类型拒绝。
- `ChannelTestServiceTests` 新增 pinned embedding 与 embedding 结果描述 2 例。
- Llm 全量 **3349/3349**；ChannelTest/Cloudflare/端点回归 60/60；
  create/update/endpoint 回归 67/67。未跑 Api 全量。

## 台账

- 新增 C317（resolved）。
- 317 条（190 accepted / 127 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 前端 Cloudflare 配置与 base URL placeholder 校验来自已同步的 live 前端副本。
- 模型列表端点不存在，模型需手工添加；这与参照一致。
