# §229 Vertex key 记账与按 key 的 Vertex 路由

## 背景

C149 是 C139/C144 live Vertex 契约的剩余记账部分：端口已接受并保留
`vertexApiKeys`，但 `GetAllAPIKeys`、credential refs、禁用 / 启用 / 删除
不把 Vertex key 当作凭证，`gemini_vertex` 出站也没有按选中 key 的
project/region 构造 URL。

## 实现

- `src/AxonHub.Abstractions/ChannelCredentials.cs`
  - 新增 `VertexApiKey` 与 `GetVertexApiKeys`。
  - `GetAllApiKeys` 按 live `objects/channel.go:510-534` 在 legacy `apiKey`、
    `apiKeys` 之后追加 `vertexApiKeys` 的非空 `apiKey`；`GetAllCredentialRefs`
    与 `GetEnabledCredentialRefs` 自动继承。
- `src/AxonHub.Api/Management/ChannelLifecycleService.cs`
  - `DeleteDisabledApiKeys`：先从 `credentials.VertexApiKeys` 移除匹配的三元组；
    若移除后无任何凭证且确有被移除的三元组，则在通用保留规则之前整体恢复
    第一个三元组并返回 `ONE_KEY_PRESERVED`；否则才恢复扁平 `apiKeys`。
  - 禁用 / 启用 / 自动恢复路径经 `GetEnabledCredentialRefs` 现在包含 Vertex key。
- `src/AxonHub.Llm/Transformer/Gemini/`
  - `GeminiOutboundConfig.BaseUrlProvider`（live `BaseURLProvider`），
    `Cleanup` 透传。
  - `GeminiOutboundTransformer.ResolveBaseUrl` 在 chat / embedding / image /
    endpoint-path 四条 URL 构造路径上使用 provider；空值回退 BaseUrl。
- `src/AxonHub.Api/Orchestration/ChannelOutboundFactory.cs`
  - 主 `gemini_vertex` 出站与用户端点都设置 provider：按 `ApiKeyProvider.Get()`
    选中 key 查找三元组，构造
    `{vertexServiceBaseURL(base)}/v1/projects/{project}/locations/{region}`。
  - `VertexServiceBaseUrl` 复刻 live：TrimSpace、去尾部 `/`、裁掉 `/projects/...`、
    去尾部 `/v1`。

## 测试

- Api `AdminChannelApiKeyEndpointTests|GeminiChannelWiringTests`：37/37。
  - 删除匹配三元组并保留其余；删除全部时恢复第一个三元组（含 project/region）；
    扁平 key 保留时不恢复三元组；最后一个 Vertex key 禁用使通道 disabled；
    Vertex key 启用恢复通道；`gemini_vertex` 选中 key 的 project/region 进入 URL。
- Api Vertex 相关回归 `ChannelTokenSourceTests|ChannelApiKeyStrategiesTests|
  GeminiChannelWiringTests`：76/76。
- Llm `GeminiVertexBaseUrlTests`：2/2；Llm Gemini 全量过滤：594/594。
- 未跑 Api 全量；新增 Api 6 例、Llm 2 例待下次全量计入。

## 台账

- C149 → resolved。
- 305 条（200 accepted / 105 resolved / 0 gap）；`diff_register.py lint` 通过；
  `02-differences.md` 已重新生成。

## 边界

- OAuth 渠道删除仍直接拒绝；Vertex 三元的写入校验仍由 C144 的
  `ValidateVertexApiKeys` 负责。
- `vertexEndpointBaseURLProvider` 的等价逻辑已随用户端点分支接入；若端点 base URL
  为空，仍按通道 Base URL 解析，与 live 的继承顺序一致。
