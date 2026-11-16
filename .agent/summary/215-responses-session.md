# §215 Responses previous_response_id 会话续接子系统（C288）

## 背景

客户端调用 `/v1/responses` 时可以用 `previous_response_id` 只发送新增的 `input`，由服务端补回
上一轮的输入与输出。参照实现在 `internal/server/orchestrator/responses_session.go`（460 行）里
维护一个进程内的 `responsesSessionStore`，并在 `orchestrator.go:174-179 / 355-375` 三处接线：
请求进入 pipeline 前 `prepare`，非流式响应返回后 `record`，流式响应则用 `wrapStream` 包一层，
在终态事件后聚合记录。端口此前完全没有这套逻辑，`previous_response_id` 会被原样发给上游。

本切片按基线 b8bb3421 完整移植，并顺带补上持久化恢复路径
（`biz/request.go:1442-1489` 的 `LoadCompletedResponsesSession`）。

## 参照行为

- **容量与生命周期**：`responsesSessionTTL = 2h`、`MaxRecords = 2048`、`MaxBytes = 64 MiB`、
  `MaxResponse = 1 MiB`；按 `(scope, response_id)` 索引，写入与查询前都驱逐过期项，
  超限时按 `UpdatedAt` 驱逐最旧项。
- **prepare**：解析 body 为对象（失败/nil 直接返回原 body + 会话 id）；`input` 为数组时删除
  非 `web_search_call` 项的 `status`（有变化才重新序列化）；读取 `previous_response_id`，
  空则只返回归一化后的 body；非空则先查内存、miss 走 loader。命中后若记录带 session id 则沿用，
  合并顺序为「记录的 input + 记录的 output + 本次 input」，删除 `previous_response_id`，
  再按 Go `json.Marshal(map[string]json.RawMessage)` 的键序与转义重新序列化。
- **record**：response body 为空、超过 1 MiB、非对象、`id` 空、`status` 为
  failed/cancelled/canceled、`output` 非数组时跳过；input 用 `responseSessionInputItems`
  （字符串 input 变成 `{"type":"message","role":"user","content":"..."}`）；size 为 input+output
  原始字节和，超过 1 MiB 跳过；scope 空白拒绝（key 用未 trim 的值）。
- **wrapStream**：保留响应头；逐事件透传，同时只拷贝 `Type/LastEventID/Data/Size` 四项并累计
  1 MiB，超限后丢弃缓冲（overflow）；用 `IsTerminalStreamEvent`（即
  `NewStreamTerminalTracker(1).Observe(event)`）判定终态，命中后经
  `responses.AggregateStreamChunks` 聚合并 `record`；`Close` 时补一次。
- **loader**：`TrimSpace` response id；无 API key 返回未命中；按 external_id、api_key_id、
  `format IN (openai/responses, openai/responses-ws)`、`status=completed`、可选 project_id 查询，
  `created_at DESC` 取第一条；两段 body 等于 `{}` 或非合法 JSON 视为未命中。
- **scope**（`middleware/auth.go:326-332`）：`api_key:<id>`，API key 带 project 时追加
  `:project:<pid>`。

## 端口实现

- 新增 `src/AxonHub.Api/Orchestration/ResponsesSessionStore.cs`：
  - `ResponsesSessionLoadFunc` 委托、`ResponsesSessionLoaded`、`ResponsesSessionKey`、
    `ResponsesSessionRecord`、`ResponsesSessionPrepared` 与 `ResponsesSessionStore`。
  - `PrepareAsync` / `Record` / `Lookup` / `WrapStream` 逐段对照参照；Go 的
    `json.Marshal(map[string]RawMessage)` 由 `MarshalRawObject`（按键的 UTF-8 字节排序、
    `LlmGoJson.Compact` 做 compact + HTML 转义）复现，字符串编码由 `EncodeGoString` 复现
    Go `encodeState.string`（引号/反斜杠/控制字符/`<>&`/U+2028/U+2029）。
  - `ResponsesSessionStream` 实现 1 MiB 缓冲、`StreamTerminalTracker(1).Observe` 终态判定、
    Dispose 前补记录；同时保留 inner 的响应头与 Err。
  - loader 异常（含取消）记 `Failed to restore persisted Responses session` 并回退客户端 body，
    与参照的日志和 fail-open 语义一致。
- 新增 `src/AxonHub.Api/Orchestration/CompletedResponsesSessionLoader.cs`：查询并读取两段
  payload，`"{}"` 精确比较与 `JsonDocument` 合法性校验后返回副本。
- `src/AxonHub.Persistence/Stores/RequestStore.cs`：新增
  `FindLatestCompletedResponsesSessionAsync`，Dapper 拼接
  `format = ANY(@Formats)`、`status='completed'`、可选 `project_id`，`ORDER BY created_at DESC LIMIT 1`。
- `src/AxonHub.Api/Auth/RequestIdentity.cs`：新增 `SessionScope`，格式与参照中间件逐字节一致。
- `src/AxonHub.Api/Orchestration/ChatCompletionOrchestrator.cs`：
  `ProcessAsync` 在 inbound 变换前 `PrepareAsync`，写回 `Body` / `SessionId` / `SessionScope`；
  非流式分支在 pipeline 返回后 `Record`；流式分支用 `WrapStream` 包装并透传
  `CodexResponseHeadersSupported`。
- `src/AxonHub.Api/Program.cs`：`ResponsesSessionStore` 注册为 Singleton，loader 工厂每次 miss
  开一个 scope 解析 Scoped 的 `CompletedResponsesSessionLoader`；loader 自身注册为 Scoped。

## 差异

- C288（resolved）：本切片的主登记项。基线 store 是进程内单例，端口保持 Singleton；
  Orleans 下不做 grain 化，跨实例恢复依赖数据库 loader（缓存可重建，grain 化只会引入单点）。
  Dapper 用 `= ANY(@Formats)` 实现 ent 的 `FormatIn`，语义等价；首版写成 `IN @Formats`
  时 Dapper 不做列表展开，真实 PG 测试直接暴露了语法错误，现已修正并由该测试锁定。
- C062：`/v1/responses` 的 HTTP / WS 请求现在由编排器写入服务端生成的 `SessionId` 与
  `SessionScope`，出站 Responses provider 可读取；只剩不经过编排器的路径。
- C254：仍为 accepted。C254 指的是 f272549f 的 `shared.ResponsesLane`（lane 级 retained 预算、
  warmup 结果续接、`ErrResponsesContinuationUnavailable`），本切片没有实现它；
  基线 `responsesSessionStore` 的存在不关闭该缺口。

## 测试

- `tests/AxonHub.Api.Tests/ResponsesSessionStoreTests.cs`（23 例）：跨传输合并、loader 恢复与
  缓存、scope 隔离、缺失 scope、多次切换上游、流式记录、事件原样透传、Close 前记录、
  客户端历史归一化、`web_search_call` 的 status 保留、无 status 输入保持原字节、
  SSE→WS session id 恢复、记录无 session id 时保留上下文、超 1 MiB 停止缓冲、TTL 驱逐、
  2048 条与 64 MiB 上限、响应头保留、loader 失败回退、failed/cancelled 跳过。
- `tests/AxonHub.Api.Tests/CompletedResponsesSessionLoaderTests.cs`（7 例）：API key+project
  作用域、缺 key、空 body、非法 JSON、格式/状态过滤、trim、created_at 最新。
- `tests/AxonHub.Api.Tests/ResponsesSessionOrchestratorTests.cs`（2 例端到端）：两轮合并后上游
  chat messages 为 3 条；流式结束后按 response id 可续接。候选通道需显式
  `ApiFormat = openai/chat_completions`——否则工厂按入站 Responses 格式选择 Responses 原生
  出站，请求体不会转成 chat，流事件也不会经过 chat→Responses 聚合。
- `tests/AxonHub.Persistence.Tests/RequestStoreTests.cs` 新增
  `FindLatestCompletedResponsesSessionScopesTheLookup`：真实 Postgres 下的 API key / project /
  格式 / 状态过滤与 newest-first 排序。
- 定向运行：Store + Loader + Orchestrator 32 通过；Persistence 新增 1 例通过；未跑 Api 全量回归。

## 后续

- C254 的 lane 级 continuation lease 仍是缺口，需按 f272549f 单独评估（涉及 WS lane 的
  retained 预算与 warmup 结果续接）。
- `/v1/responses` 经由 `ChatCompletionOrchestrator` 的路径已覆盖；后续若新增绕过编排器的
  Responses 入口，需要显式传入 `SessionId` / `SessionScope`。
- 全量 Api 回归仍待择机执行。
