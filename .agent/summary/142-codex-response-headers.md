# §142 Codex 响应头转发（C094 item 7 关闭）

日期：2026-11-08
参照：`internal/server/api/chat.go`（`writeForwardResponseHeaders` /
`copyCodexTurnStateHeader` / `primeCodexTurnStateHeader` / `primedStream`）、
`internal/server/orchestrator/orchestrator.go:360-375`、
`llm/httpclient/response_headers.go`。

## 交付

- `ChatCompletionResult` 新增 `CodexResponseHeadersSupported`；`RunPipelineAsync` 在 pipeline
  成功后由 `PersistentOutboundTransformer.SupportsCodexResponseHeaders()` 填充（codex 出站恒真，
  其余出站恒假），非流与流两个返回点都携带。
- `ChatCompletionEndpoint` 新增三个内部辅助函数与嵌套 `PrimedStream`：
  - `WriteForwardResponseHeaders`：门为「支持 codex 响应头 + 路径以 `/responses` 结尾 + 请求携带
    codex 会话」；非流读 `result.Response.Headers`，流式读 `ResponseHeaders.GetResponseHeaders`，
    经 `MergeForwardResponseHeaders` 只复制 `X-Reasoning-Included`（值必须全为 `true`）。
  - `CopyCodexTurnStateHeader`：非流式响应把 `X-Codex-Turn-State` 写入下游响应头。
  - `PrimeCodexTurnStateHeader`：仅 `/responses` 流式响应先取首事件、复制其
    `X-Codex-Turn-State`，再以 `PrimedStream` 回放（SSE 头必须在首 flush 前发送）。
- 流式响应在 §141 的 `DisposeCallbackStream`（按尝试 Stop）包装后，用
  `ResponseHeaders.WithResponseHeaders` 重新附加 pipeline 的 response headers 元数据；该包装会
  遮蔽 `IResponseHeadersProvider`，不重新附加则 `WriteForwardResponseHeaders` 读不到任何内容。
- 代码生成/工具链未变动；不新增表或 SQL。

## 行为要点

- codex 通道的 `/v1/responses` 响应现在与参照一致地转发 `X-Reasoning-Included`（仅当上游声明
  usage 已含 reasoning 且值全为 `true`）与 `X-Codex-Turn-State`（非流式取响应头，流式取首事件
  transport 头）；其它出站、非 `/responses` 路径与无 codex 会话的请求不转发。
- 端点对 `MergeForwardResponseHeaders` 的 `dst` 传 null 而非 gin 的 writer 头：ASP.NET 响应头
  在调用点没有可删除的既有 `X-Reasoning-Included`，只读转发集合与参照的可观察结果一致。
- C094 item (7) 关闭；C094 仍为 gap（端点表、OAuth 凭据持久化/自动刷新等残余）。

## 测试

- `tests/AxonHub.Api.Tests/CodexResponseHeaderForwardingTests.cs`（5 方法 / 7 例）：首事件
  `X-Codex-Turn-State` 复制并完整回放；三条路径门（chat / responses 无会话 / codex responses）；
  不支持出站的拒绝；流式 provider 读取；缺失或空 turn state 不写入。
- `tests/AxonHub.Api.Tests/ChatCompletionOrchestratorTests.StreamKeepsForwardableResponseHeadersForTheEndpoint`
  （1）：pipeline 附着的 `X-Reasoning-Included` 元数据穿过 `DisposeCallbackStream` 仍可被端点读取。

静态计数：`AxonHub.Api.Tests` 5403 → 5413（§141）→ 5421（§142，+8）；`AxonHub.Llm.Tests` 1482、
`AxonHub.Persistence.Tests` 331、`AxonHub.Grains.Tests` 1680 不变；四项目合计 8906 → 8914。
**按项目规则未运行 lint / build / test。**

台账：C094 item (7) 关闭并更新 decision/cause/test；基线 §142、验收 §142、覆盖缺口行同步更新；
总数 224 条（161 accepted / 54 resolved / 9 gap，C094 仍为 gap）。
