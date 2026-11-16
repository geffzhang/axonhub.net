# §192 实时流预览（C192 收窄，C279 新增，41 例）

日期：2026-11-12
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/pkg/chunkbuffer/chunkbuffer.go`、
`internal/server/biz/stream_preview.go`、`internal/server/orchestrator/live_streaming.go`
（withLivePreview）、`internal/server/api/request_live.go`、
`internal/server/biz/request.go:1495-1560` 与 `:1620-1683`。

## 背景

C192 一直挂着「stream + processing 的请求在参照中会从通道实时读取 response_chunks
预览，端口没有该接缝」这一真实缺口。§189 的预览端点因此只能走静态回退，§191 的
playground 也显式声明不涉及实时预览。本切片补齐 chunkbuffer、LiveStreamRegistry、
live preview 中间件与 SSE 预览读端，把 C192 收窄为 GraphQL 输入子集与缺失边，
残余实现差异另立 C279。

## 交付

- `src/AxonHub.Api/Persistence/ChunkBuffer.cs`：chunkbuffer.Buffer 的逐方法移植——
  `Append` 拒绝 nil / closed / 超 50000，`Slice` 快照，`Read(index)` 返回
  chunk / nextIndex / 读取瞬间的 closed，`Close` 广播，`SubscribeFromCurrent`
  原子返回 replay 截止与幂等退订；广播用 one-slot `SemaphoreSlim` 合并突发。
- `src/AxonHub.Api/Persistence/LiveStreamRegistry.cs`：request / execution 两份
  `ConcurrentDictionary`；`GetRequestChunks` / `GetExecutionChunks` 复用
  `RequestChunkStorage` 的存储过滤与 Go JSON 形状；`SweepStaleEntries` 关闭并
  删除已关闭或闲置超过 10 分钟的 buffer。
- `src/AxonHub.Api/Persistence/LiveStreamRegistrySweeper.cs`：`BackgroundService`
  + `PeriodicTimer(5 分钟, TimeProvider)`，等价参照 fx 生命周期上的 ticker。
- `src/AxonHub.Api/Orchestration/IRequestChainState.cs` 与
  `LivePreviewMiddleware.cs`（`DummyMiddleware`）：persistence 建行之后注册两个
  buffer；`OnOutboundRawError` 先 execution 后 request 关闭并注销；两侧 raw stream
  包装后逐事件镜像（binary 音频先 `SummarizeBinaryChunk`），Dispose 关闭、注销并
  释放内层流。`RequestChainPersistenceMiddleware` 实现 `IRequestChainState`。
- `ChatCompletionOrchestrator`：构造器接收 `LiveStreamRegistry?` 与
  `ISystemSettingsAdminService?`，克隆透传；`RunPipelineAsync` 在管线构建前读取
  一次 `StoragePolicy.LivePreview`（失败回退 false），中间件挂在 persistence 之后、
  并发准入之前；`ProcessSpecifiedAsync` 传 `livePreview: false`，通道测试不注册
  preview buffer（等价参照 tester 的 nil registry）。
- `src/AxonHub.Api/Management/RequestPreviewHandlers.cs`：processing + stream 且
  buffer 仍注册时建 `RequestPreviewStream` 并以 `expectedChoices: 0` 交给
  `SseStreamWriter`：订阅时切 replay / chunk、跳过 `[DONE]`、关闭后发
  `preview.completed`（`{"status":"completed"}`）、等待 notify / 取消 / 3 分钟
  idle；其余请求保持静态回退。completed 后 writer 追加的
  `ErrStreamIncomplete` 错误事件与参照一致。
- `src/AxonHub.Api/Management/RequestPayloadStorage.cs`：两个 chunk loader 在
  processing + stream 时直接返回 registry 的实时 chunks（含空列表兜底）。
- `Program.cs`：注册 `LiveStreamRegistry` 单例与 sweeper 宿主服务。

## 测试

- `tests/AxonHub.Api.Tests/ChunkBufferTests.cs` 10 例：nil / closed / 容量上限、
  Read 的 nextIndex 与 closed、Slice 快照、Close 唤醒全部订阅者、突发合并、
  replay 截止、closed 订阅立即唤醒、退订幂等、LastAppendedAt。
- `tests/AxonHub.Api.Tests/LiveStreamRegistryTests.cs` 10 例：未知 id、注册 /
  替换 / 注销、preview 过滤（`[DONE]`、binary.done）、音频摘要、非法 JSON 与
  null 载荷、last_event_id、closed 清理、10 分钟 idle 清理（边界与继续追加）、
  清理后不再产出 chunks。
- `tests/AxonHub.Api.Tests/LivePreviewMiddlewareTests.cs` 12 例：非流式 / stream
  为 nil / 无 registry / 策略关闭不注册、request + execution 注册、同 id 不替换、
  错误后重建、两侧事件镜像、Dispose 关闭注销、错误关闭两个 buffer、无 buffer 时
  原样返回流。
- `tests/AxonHub.Api.Tests/LivePreviewOrchestratorTests.cs` 4 例：流式注册并在
  Dispose 后释放、策略关闭保持空、读取失败回退默认、非流式不注册。
- 扩展 `AdminRequestContentEndpointTests.cs` 3 例（replay→completed、订阅后增量
  chunk、completed 忽略残留 buffer）与 `RequestPayloadStorageTests.cs` 2 例
  （request / execution loader 优先实时 registry）。
- 实测：组合过滤 124 通过 / 0 失败（含 41 个新例，2026-11-12），
  `LivePreviewOrchestratorTests` 单独 4 通过；`AxonHub.Api.Tests` 未跑全量。

## 台账

C192 收窄为 GraphQL 输入子集与缺失边（accepted）；新增 C279（accepted：阻塞等待
占线程、策略快照预读与静默回退、sweeper 宿主、DI 单例、nil 与空列表序列化、
tester 关闭开关、`IRequestChainState` 接缝、expectedChoices=0 的错误事件）。
`diff_register.py lint` 通过；`02-differences.md` 已重新生成；279 条
（204 accepted / 5 gap / 70 resolved）。
