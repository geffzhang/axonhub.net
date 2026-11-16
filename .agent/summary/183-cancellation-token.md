# §183 取消令牌贯穿同步 outbound transform（C094 (6) 关闭，9 例）

日期：2026-11-12
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`llm/pipeline/pipeline.go`（`ctx` 传入
`TransformRequest`）、`llm/transformer/openai/codex/outbound.go` 与
`llm/transformer/anthropic/claudecode/outbound.go`（`TokenProvider.Get(ctx)`）、
`llm/transformer/xai/subscription/outbound.go`、
`llm/transformer/antigravity/transformer.go`、
`internal/server/biz/channel_endpoint_detect.go:155`（`probeCtx` 传给 `TransformRequest`
与 `Do`）。

## 背景

参照的 `TransformRequest(ctx, ...)` 把 `ctx` 传给 OAuth `TokenProvider.Get`，客户端断开
会中止令牌刷新。端口的 transformer 接口是同步的（无 context 参数），Codex / Codex alpha
search / ClaudeCode / xai_subscription / antigravity 五处都以 `CancellationToken.None`
取令牌，刷新不随请求取消。

## 交付

- `LlmRequest.RequestCancellationToken`（port-only 接缝）：镜像 C104 已有的
  `HttpRequestData.RequestCancellationToken`；`[JsonIgnore]` 不序列化，`ShallowClone`
  （MemberwiseClone）自动携带。
- `Pipeline.ProcessRequest` 在 `_outbound.TransformRequest` 前写入 `Process` 收到的
  `CancellationToken`。
- `PersistentOutboundTransformer.TransformRequest` 在 channel transform options 可能
  克隆请求后再次透传到 per-attempt 请求。
- 五个 token provider 调用点改读 `request.RequestCancellationToken`：
  `CodexOutboundTransformer`、`CodexAlphaSearch`、`ClaudeCodeOutboundTransformer`、
  `SubscriptionOutbound`（null 请求回退 None）、`AntigravityTransformer`。
- `ChannelEndpointDetectService.Probe` 按参照把 `probeCts.Token` 设到探测请求上（此前
  只在 `Do` 使用）；探测自身超时在 transform 内抛出的 `OperationCanceledException`
  归类为 invalid（参照把任何 transform 错误都记为 invalid），外层取消仍向上传播。

## 测试

- `tests/AxonHub.Llm.Tests/TransformRequestCancellationTests.cs` 7 例：
  - `Pipeline.Process` 的令牌到达 outbound `TransformRequest`（`CanBeCanceled`）。
  - Codex / Codex alpha search / ClaudeCode / xai_subscription / antigravity 的
    `ITokenGetter.Get` 收到请求令牌（记录式 getter / 记录式刷新 executor）。
  - 令牌属性不进入 JSON 序列化。
- `tests/AxonHub.Api.Tests/PersistentOutboundCancellationTests.cs` 2 例：
  - 无 transform options 时同一请求透传令牌。
  - `forceArrayInputs` 触发克隆路径后仍保留令牌（`Assert.NotSame` 证明克隆发生）。

## 本轮实测

- 新增 9 例通过（Llm 7 / Api 2）。
- 定向 377 例通过（Llm：Codex / ClaudeCode / XaiSubscription / Antigravity /
  ChannelEndpoints / PipelineRetry / OAuthTokenProvider）。
- 定向 143 例通过（Api：ChannelTransformOptions / Codex 与 ClaudeCode / OpenCode /
  XaiSubscription 接线 / ResponsesEndpointDispatch / 端点探测 / PersistentOutbound）。
- `AxonHub.Api.Tests` 未跑全量（本刀不触碰编排中间件与持久化落库），静态计数
  6193 → 6195；四项目合计 9861 → 9870（Llm 3115 → 3122 / Persistence 376 /
  Grains 177 未重跑）。

## 台账

- `differences.jsonl`：C094 的 `what` 补记 (6) 关闭与实现要点，`test` 补记 9 例；
  C094 保持 gap（端点表、fenno / github_copilot / antigravity 的 provider 复用、
  模型拉取管理等残余未变）。
- `01-baseline.md`：§29 追加 2026-11-12 更新段。
- `03-acceptance.md`：新增 §183 段。
- 269 条（196 accepted / 6 gap / 67 resolved），`diff_register.py lint` 通过，
  `02-differences.md` 已重新生成。
