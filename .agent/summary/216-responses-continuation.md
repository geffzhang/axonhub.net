# §216 Responses continuation lease 全链路（C289 / C254）

## 背景

§215（C288）只落地了基线 b8bb3421 的进程内 `responsesSessionStore` 与数据库 loader：请求在进入
pipeline 前合并历史，响应完成后记录。live f272549f 把同一子系统扩展成完整的 continuation lease：

- 每次尝试用 `shared.ResponsesAttempt` 记录实际发送的 origin（channel / format / model / auth）
  与 generation；
- WebSocket lane 绑定 `shared.ResponsesLane`，以 64 MiB retained 预算保存 warmup 结果与 adapter
  snapshot；
- `responses_attempt.go` 在 dispatch 前按 owner 固定候选，未命中或无法恢复时统一抛
  `ErrResponsesContinuationUnavailable`；
- `responses_capture.go` 只认 raw provider 的 `response.completed`，超过 16 MiB 退化为 metadata；
- `responses_verify.go` 用最终 wire request 复核 continuation 是否仍然合法。

端口此前完全没有这些层：`previous_response_id` 要么被原样转发，要么由 C288 的 store 合并，
无法固定 origin、无法处理 lane warmup、也没有 recovery 错误语义。

## 参照行为

- **lane / attempt**：lane 以 binding id 索引，`Budget` 为 64 MiB retained；`Record()` 返回
  lane 当前响应，`Snapshot` 仅在非 native WS 且未 overflow 时存在；attempt 的 `Origin` 由
  transformer 在 dispatch 前设置，`Generation` 在命中 owner 时继承，连接结束时 `RetireLeases`
  回收。
- **候选固定**（responses_attempt.go:167-202）：owner 无 Snapshot 时，在候选列表内按
  ChannelID / APIFormat / ActualModel 找到产生该响应的通道；没有 `Models` 的候选跳过。
- **prepareAttemptBody**（responses_attempt.go:48-143）：每 attempt 重置 capture 与 retention；
  无 previous → complete；有 previous 无 scope → recovery；lane 命中优先，否则 store
  lookupOrLoad；owner 有 Snapshot → 128 MiB 合并上限、展开 input、删 previous、complete=true；
  owner 无 Snapshot → 必须 `originMatches(owner.Origin, target.origin) && forwardsID`；无 owner →
  `IsResponsesWebSocket || format != openai/responses || !forwardsID` 时 recovery；`known` /
  `knownFailed` → recovery；`firstOrigin` 不匹配 → recovery。随后 `native = webSocket &&
  target.format == openai/responses-ws`，生成 snapshotBody，按 ≤64 MiB retained 预留 lane 预算，
  `capture.metadataOnly = native || !retain`。
- **verify**（responses_attempt.go:304-345）：用最终 wire request 的 APIFormat、URL、CurrentModel、
  `ContinuationAuthIdentity` 检查 previous_response_id、forwardsID、owner origin、firstOrigin；
  complete 且无 previous 且 Responses 格式时 snapshotBody 并重新预留 input；attempt origin 命中
  owner 时继承其 Generation。
- **capture / accept**：只认 raw provider `type=response.completed` 且 `response.status=completed`、
  `id != ""`；累计字节 >16 MiB 置 overflow；accept 校验 JSON + status completed；lane 分支仅
  `origin.Format != openai/responses-ws` 且 `retain && !overflow` 建 Snapshot；store 分支 complete
  用 snapshotBody，overflow 退化为 `{"previous_response_id":"unavailable"}`。
- **store（f272549f）**：`MaxResponse = 16 MiB`；record 仅接受合法 JSON + 顶层
  `status=="completed"`；`complete = 无 previous_response_id && response ≤16 MiB && input ≤16 MiB+2`；
  complete 时严格解析 output；字节压力先清空最旧记录 payload（保留 Origin，Complete=false），
  再按 2048 条硬上限驱逐；`Known = HasCompletedResponsesOriginAsync`（external_id、completed、
  format IN `openai/responses` / `openai/responses-ws`，不按 scope）。
- **certification**（request_responses_certification.go:20-46）：request 对象且 previous 非空拒绝；
  response id == 请求 id、status completed、output 数组；input 为 string 或 object 数组；两侧
  `json.Valid`。

## 端口实现

- `src/AxonHub.Llm/Transformer/Shared/ResponsesLane.cs`（新）：`ResponsesLane`、`ResponsesBudget`、
  `ResponsesAttempt`、`ResponsesSnapshot`、`ResponsesRecovery`（哨兵 + 400 线路错误，复现
  `errors.Join` 的 `Is`/`As` 双重匹配）。
- `src/AxonHub.Llm/Transformer/OpenAI/Responses/ResponsesContinuationAuth.cs`（新）：
  `ResponsesContinuationAuth.Identity`，供 Api 的 Verify 使用。
- `src/AxonHub.Llm/Http/HttpTypes.cs`：`HttpRequestData` 新增 port-only 的 `ResponsesLane` /
  `ResponsesAttempt` / `ResponsesWebSocket`；`src/AxonHub.Llm/Pipeline/Pipeline.cs` 复制到出站 wire
  request，并对 recovery 直接抛出。
- `src/AxonHub.Api/Orchestration/ResponsesContinuation.cs`（新）：`ResponsesCompletion`、
  `ResponsesProviderStream`、`ResponsesAcceptedStream` 与 `ResponsesContinuation`
  （LoadOwner / PrepareAttemptBody / ReleaseRetention / ReleaseCapture / Verify / Accept /
  OriginMatches）。
- `src/AxonHub.Api/Orchestration/ResponsesMiddlewares.cs`（新）：body 覆盖、verify、capture、
  recovery 不计通道失败惩罚四个中间件。
- `src/AxonHub.Api/Orchestration/PersistentOutboundTransformer.cs`：dispatch 前
  `SelectResponsesContinuationCandidate`、`PrepareResponsesAttempt`、`ForwardsResponsesId` 探针与
  `EffectiveResponsesRequestBody`。
- `src/AxonHub.Api/Orchestration/ChatCompletionOrchestrator.cs`：每请求预解析 owner 与 Known（因为
  transformer API 同步）、构造 continuation、非流式 `Accept()`、流式交给
  `ResponsesAcceptedStream`、finally 释放未转移的 lease。
- `src/AxonHub.Api/Orchestration/ResponsesSessionStore.cs`：16 MiB、Complete/Origin、record gating、
  Known 委托、字节压力驱逐语义。
- `src/AxonHub.Api/Orchestration/CompletedResponsesSessionLoader.cs`：`certifiedResponsesSnapshot`
  与 `GoString`。
- `src/AxonHub.Persistence/Stores/RequestStore.cs`：`HasCompletedResponsesOriginAsync`
  （`format = ANY(@Formats)`）。
- `src/AxonHub.Api/Http/ResponsesWebSocketEndpoint.cs`、
  `src/AxonHub.Llm/Transformer/OpenAI/Responses/ResponsesWebSocketExecutor.cs`：lane/budget/lease/
  generation、RetireLeases、recovery 400、共享 retained/queue 预算。
- `src/AxonHub.Api/Orchestration/PassThroughMiddlewares.cs`：改用
  `outbound.EffectiveResponsesRequestBody()`。
- `src/AxonHub.Api/PromptProtection/PromptProtectionBodyCheck.cs`：`ValidateExpanded`，让掩码规则
  作用于展开后的 body。

## 差异

- C289（resolved）：本切片主登记项。Go ctx 取值改为 `HttpRequestData` 显式字段；owner/known 在
  orchestrator 每请求预解析；`errors.Join` 用 inner 链复现；store 语义升级到 f272549f。
- C254（accepted → resolved）：lane lease、warmup 续接、native WS lane 的 snapshot 规则与
  `ErrResponsesContinuationUnavailable` 均已移植。
- C288：store 的单项上限由 1 MiB 提升到 16 MiB，记录携带 Complete/Origin，测试计数更新。
- C255：仍为 accepted；`stream_delivery_capabilities.go` 的 `preserveDeliveryCapabilities` 未移植。
- 残余缺口（登记在 C289 decision）：`pass_through.go` 的 `responsesTransportAdaptationEnabled` /
  `responsesIdentityPreservationEnabled` / `preserveResponsesModel` 与
  `responses.AdaptRequestBody` / `AdaptResponseBody` / `AdaptStreamEvent` 未移植。

## 测试

- `tests/AxonHub.Api.Tests/ResponsesContinuationTests.cs`（6 例）：dispatch 前固定 eligible owner、
  unknown id 前置失败、persisted delta 不是 external unknown、known 缺失不复写、HTTP native id
  固定在 origin、native lane 无 adapter snapshot。
- `tests/AxonHub.Api.Tests/ResponsesCaptureTests.cs`（9 例）：provider 身份提交、16 MiB 边界（2）、
  overflow 不认证前缀、共享预算合并多 lane、聚合上限保留完整快照、error/Close 只释放一次、
  消费事件保留。
- `tests/AxonHub.Llm.Tests/ResponsesLaneTests.cs`（3 例）：recovery 保留 HTTP 错误、预算拒绝聚合
  溢出、provider 错误为 recovery。
- `tests/AxonHub.Llm.Tests/ResponsesWebSocketExecutorTests.cs` 新增 4 例：断连回收 lease、
  provider recovery 用不可重试哨兵、缺历史在 dispatch 前失败、native continuation 不受 retained
  上限影响。
- 既有类补充：`ResponsesSessionStoreTests` 25 例（+BudgetsLargeHistory / SnapshotRejectsUnresolvedAncestor）、
  `CompletedResponsesSessionLoaderTests` 11 例（+certification Theory 4 例）、
  `ResponsesSessionOrchestratorTests` 4 例（+KnownWithoutRestorableSnapshotFailsBeforeDispatch）。
- `tests/AxonHub.Persistence.Tests/RequestStoreTests.HasCompletedResponsesOriginIgnoresTheOwner`：
  真实 Postgres，跨 key/project、两种 Responses 格式、failed/compact/chat 排除。
- 定向运行：Api `FullyQualifiedName~Responses` 142/142、Llm
  `ResponsesLaneTests|ResponsesWebSocketExecutorTests` 49/49、Persistence 1/1；未跑 Api 全量回归。

## 后续

- transport adaptation / identity preservation 与 delivery capability（C255）仍是缺口，需要按
  f272549f 的 `pass_through.go` 与 `responses/protocol_codec.go` 单独评估。
- Api 全量回归仍待择机执行。
