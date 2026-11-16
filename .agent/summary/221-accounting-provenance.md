# §221 usage 记账 provenance 门（C294）

## 参照

- live f272549f `llm/model.go:829-864`：`AccountingEvidence`（unavailable / explicit-final /
  clean-end-candidate）、`ResponseMeta.AccountingFinal(sourceEnd)`（explicit-final 恒真；
  clean-end-candidate 仅 SourceEndCleanEOF / SourceEndSemantic 为真）、`json:"-"` 标记。
- `llm/transformer/anthropic/aggregator.go`：message_start 的 usage 为基线、message_delta 合并、
  message_stop 且无 stream error 置 explicit-final、error 在非 explicit-final 时降级。
- `llm/transformer/openai/aggregator.go`：seen/finished choices、zero-choice usage 计数与
  explicit-final / clean-end-candidate / unavailable 分类。
- `llm/transformer/openai/responses/aggregator.go`：terminal 事件记录 usage 并覆盖 meta。
- `internal/server/orchestrator/outbound.go:495-612`：`persistAggregatedResponse` 的写入门、
  "Authoritative usage unavailable" 警告、`accountingMayBeFinal` / `accountingUnavailableReason`。
- `internal/server/orchestrator/task5_accounting_test.go`：口径来源。

## 端口实现

- `src/AxonHub.Llm/Model/LlmResponse.cs`：新增 `AccountingEvidence` 枚举（Unavailable=0 /
  ExplicitFinal / CleanEndCandidate，Go 零值 `""` 对应 Unavailable）与 `ResponseMeta.AccountingEvidence`
  （`[JsonIgnore]`）及 `AccountingFinal(SourceEndReason)`。
- `src/AxonHub.Llm/Transformer/Anthropic/AnthropicAggregator.cs`：`messageDeltaUsage` /
  `streamError` / `usageEvidence`；message_stop 置 ExplicitFinal；error 在当前不是 ExplicitFinal
  时降级 Unavailable（message_stop 之后的 error 不撤回终值）。
- `src/AxonHub.Llm/Transformer/OpenAI/OpenAiAggregator.cs`：`usageEvidence` / `seenChoices` /
  `finishedChoices` / `zeroChoiceUsages`；choices 为空的 final usage → ExplicitFinal，
  finish_reason 后带 usage → CleanEndCandidate，结束时仍为 CleanEndCandidate 且重复 zero-choice
  usage → Unavailable。
- `src/AxonHub.Llm/Transformer/OpenAI/Responses/ResponsesAggregator.cs`：`TerminalSeen` /
  `TerminalUsage`；completed / failed / cancelled / incomplete 先记录 terminal usage，再
  `ApplyResponseSnapshot`，非空时覆盖 meta.Usage 并置 ExplicitFinal，否则 Unavailable。
- `src/AxonHub.Api/Orchestration/RequestChainPersistenceMiddleware.cs`：
  - `PersistAggregatedExecution` 先写 `attempt.AccountingSourceEnd` / `attempt.AccountingFinal`
    （与 live 一致只写不读），仅当 usage 非空且 `AccountingMayBeFinal` 时 `LogUsage`。
  - `AccountingMayBeFinal`：`meta.AccountingFinal(sourceEnd)` 优先；openai/chat、openai/responses、
    openai/responses-ws、openai/responses_compact、anthropic/messages 只认证据；其余格式回退
    `meta.Usage is not null`。
  - `AccountingUnavailableReason`：budget / provider-error / truncated（Canceled 或
    CleanEndCandidate）/ absent。
  - 不写 usage log 时以静态 `LoggerMessage` 委托记 "Authoritative usage unavailable"
    （execution_id + reason）。
- `src/AxonHub.Api/Orchestration/ChatCompletionOrchestrator.cs`：`WithInbound` 与
  `WithChannelSelector` 透传 `loggerFactory`，使两条构造路径的警告都可用。
- 非流式路径保持与 live 一致：直接用响应 usage 写 usage log，无 provenance 门。

## 差异

- C294（resolved）：本切片主登记项。
- C255（仍 accepted）：host shutdown / pass-through detach / request chunk 来源，与本切片无关。
- 设计差异：Go 的零值字符串 `""` 映射为枚举 0（Unavailable），JSON 序列化不输出该属性
  （`[JsonIgnore]`）；警告用源生成日志委托（项目 analyzer 规则），文案与字段与 live 一致。

## 测试

- `tests/AxonHub.Llm.Tests/ResponseMetaAccountingTests.cs`（新）：镜像
  `TestResponseMetaAccountingContract`（finalZero、partialNonzero、cleanCandidate）。
- `tests/AxonHub.Llm.Tests/AnthropicAggregatorTests.cs`：`MessageStopFinalizesOrderedUsage`、
  `MessageDeltaUsageWithoutStartUsageIsExplicitFinal`（含 message_stop 后 error 仍终值、error 先于
  message_stop 降级）、`MissingMessageStopLeavesUsageUnavailable`。
- `tests/AxonHub.Llm.Tests/OpenAiAggregatorTests.cs`：`AccountingProvenanceCases`
  （choices-less final zero → ExplicitFinal；finish usage → CleanEndCandidate；重复 zero →
  Unavailable）与 `CleanEOFUsageWithoutFinishReason`。
- `tests/AxonHub.Llm.Tests/ResponsesAggregatorTests.cs`：`TerminalUsageCases`（completed / failed /
  incomplete / cancelled × 非零 / 零）、`TerminalUsageIsExplicitFinal`、
  `MissingTerminalUsageIsUnavailable`。
- `tests/AxonHub.Api.Tests/AccountingProvenanceTests.cs`（新，23 例）：门控 Theory 9、budget 1、
  reason Theory 4、chat 5、Responses failed terminal 2、Anthropic message_stop 2；使用
  `ChatCompletionOrchestrator` + `ScriptedExecutor` + `RecordingRequestChainRecorder`，
  跨格式通道用 `FixedSelector` 注入 candidate。
- 定向运行：Llm 全量 3294/3294 通过；Api `AccountingProvenanceTests` 23/23、相关四类
  （RequestChainPersistence|ResponsesSessionOrchestrator|AnthropicMessagesEndpoint|
  AccountingProvenance）40/40；未跑 Api 全量回归。

## 台账

- C294 由 accepted 改为 resolved；C255 收窄不变；294 条（199 accepted / 0 gap / 95 resolved）；
  `diff_register.py lint` 通过，`02-differences.md` 已重新生成。

## 后续

- C255：host shutdown 接线（生命周期管理器）、pass-through detach 与 request chunk 来源。
- Api 全量回归仍待择机执行。
