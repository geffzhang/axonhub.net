# §141 Responses WebSocket 生产接线：trace 会话注入与按尝试 Stop（C072 收窄）

日期：2026-11-08
参照：`internal/server/biz/channel.go`（`stopChannelOutbounds`）、
`internal/server/biz/channel_llm.go`（Responses 构造）、
`llm/transformer/openai/responses/outbound.go`、`llm/transformer/openai/codex/outbound.go`、
`llm/transformer/opencode/headers.go`、`internal/server/orchestrator/outbound.go`。

## 交付

- 新增 `AxonHub.Llm.Transformer.IStoppableOutbound`，复刻参照的 `interface{ Stop() }`
  结构判断；`ResponsesOutboundTransformer`、`CodexOutboundTransformer` 与
  `OpenCodeSessionHeaderCustomizedOutbound`（仅当内层实现时转发）实现该接口。
- `ChannelOutboundFactory.CreateResponsesOutbound` 统一 8 个 Responses 分支的构造，并注入
  `SessionIdProvider` / `SessionScopeProvider`（读 `request.RawRequest.SessionId` /
  `SessionScope`）；`opencode_go`、`github_copilot`、`xai_subscription` 包装器透传两个 provider。
- `PersistentOutboundTransformer` 在 `TransformRequest`（首次或替换）、`TryPrepareForRetry`
  （换模型）与 `TryNextChannel`（换通道）前停止旧 transformer；新增公开 `Stop()`；非可停
  对象跳过。
- `ChatCompletionOrchestrator.RunPipelineAsync` 在非流式成功、pipeline 异常与流 Dispose 时
  停止出站；流以 `DisposeCallbackStream` 包装（回调一次，`finally` 保证源 Dispose 抛错也执行）。
- 代码生成/工具链未变动；不新增表或 SQL。

## 行为要点

- 生产 Responses 请求现在把 trace 会话写入线路 `SessionId` / `SessionScope`，WebSocket 执行器
  据此池化；`prompt_cache_key` 的回退链（请求级 → 共享会话 + 会话锚点）保持不变。
- 参照在通道缓存换入/换出时停止执行器池；端口每次尝试构建 transformer，尝试结束即关闭，
  因此不会泄漏或悬挂连接，但同一通道后续请求不复用连接池（上游连接新建频率与 WebSocket
  会话连续性弱于参照）。残余登记在 C072。
- 端点记录仍未建模：显式 `endpoint.Transport` 不进入配置，生产 websocket 选择只能由 base URL
  的 ws/wss 推断（`InferTransport`，仅 `openai/responses` 允许 websocket）——C072 的 (a) 项。
- 会话注入与 Stop 生命周期完成：C062 的残余接线说明、C094 item (12)、C096 item (6) 关闭。

## 测试

- `tests/AxonHub.Api.Tests/ResponsesSessionWiringTests.cs`（6）：openai / xai / opencode_go /
  github_copilot / xai_subscription 五条生产链路转发 trace 会话（`prompt_cache_key` 前缀、
  线路 `SessionId` / `SessionScope`）；无 trace 时不写 `prompt_cache_key`。
- `tests/AxonHub.Api.Tests/PersistentOutboundRetryTests.cs`（+4）：换模型停旧 transformer、
  换通道停旧与中间 transformer、`Stop` 停当前、非可停 transformer 跳过。
- `tests/AxonHub.Llm.Tests/DisposeCallbackStreamTests.cs`（2）：回调一次（重复 Dispose 不重复）、
  源 Dispose 抛错仍回调。

静态计数：`AxonHub.Llm.Tests` 1480 → 1482（+2）、`AxonHub.Api.Tests` 5403 → 5413（+10）；
四项目合计 8894 → 8906。**按项目规则未运行 lint / build / test。**

台账：C072 保持 gap（收窄为端点记录传输与跨请求连接复用），C061 / C062 / C094 / C096 同步更新；
总数 224 条（161 accepted / 54 resolved / 9 gap）。
