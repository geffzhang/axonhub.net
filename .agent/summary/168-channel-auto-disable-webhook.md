# §168 自动禁用运行时与 Webhook 通知器（C236，59 例）

日期：2026-11-10
参照（`f272549f`）：`internal/server/biz/channel_auto_disable.go`、
`internal/server/biz/webhook_notifier.go`、`internal/server/biz/channel_metrics.go`、
`internal/server/biz/performance.go`、`internal/server/orchestrator/orchestrator.go`、
`internal/server/biz/channel_apikey.go`。

## 背景

C236 此前登记为真实缺口：参照在请求失败后按 `channel.policies.apiKeyAutoDisableRules` /
`retry_policy.apiKeyAutoDisableRules` 评估连续失败，达到阈值后禁用 API key 或整个通道，
并在通道被自动禁用时经 `webhook_notifier.go` 发送 HTTP 通知。端口此前只有配置读写
（`webhookNotifierConfig` / `updateWebhookNotifierConfig`），没有运行时。本切片闭合该缺口，
同时修正 C149 中「冻结版本没有自动禁用事件总线」的错误描述。

## 交付

- `src/AxonHub.Api/Management/ChannelAutoDisableService.cs`：
  - `AutoDisableCounters`：进程级单例，`identity:scope:rule:<index>:<statusCodes>:<keywordPatterns>:<times>:<action>:<duration>` 键
    的计数与 `actionInFlight`，`Increment` / `CompleteAction`（阈值认领 + 扣减 + 失败回补）/
    `ClearOnSuccess`（只清 identity 与其前缀键）。
  - `ChannelAutoDisableService.EvaluateAsync`：取消 / 未完成记录短路；成功清零、失败按模式
    （off / custom / inherit / 缺省 custom）与两层规则评估；首个匹配规则生效；动作
    temporary / until_cron / permanent / permanent_delete；OAuth 哨兵 `__oauth__` 与
    keyless 渠道的 `permanent_delete` 降级；`ONE_KEY_PRESERVED` 仍先禁用。
  - `MarkChannelUnavailableAsync`：条件更新（`status = enabled`）；未命中只强制重载；命中后
    读取更新后的通道快照（失败只跳过事件）、发布 `ChannelAutoDisabledEvent`、同步
    `Load(ctx, true)`、最后 `asyncReloadChannels()`。
  - `ChannelAutoDisableEvaluationQueue`：`BackgroundService`，Unbounded Channel 单读者，
    每条记录新建 DI 作用域，异常记 "panic in record performance"。
- `src/AxonHub.Api/Management/WebhookNotifier.cs`：
  - `WebhookNotifier`：`channel.auto_disabled` / `warning`；首个事件精确匹配的订阅；按
    targetNames 顺序取目标、跳过未启用与空 URL（重名重复发送）；每个目标先 body 后 headers，
    渲染失败只跳过该目标；默认 `application/json`、3000ms、支持代理；配置读取失败回退空配置；
    发送失败只记日志；`OccurredAt` 按系统时区格式化为 RFC3339（零偏移 `Z`）。
  - `IWebhookNotificationSink` / `BackgroundWebhookNotificationSink`：独立作用域异步投递。
- 接线：`Program.cs` 注册服务、计数器单例、队列、sink 与后台 sink；
  `ChannelMetricsMiddleware` 的失败路径经队列评估；`ChatCompletionOrchestrator` 在
  `HasRequestExecution` 时先落库失败再评估（detach 10s，评估异常不替换原请求错误）。
- `ChannelLifecycleService` 的 channelDisabled 分支沿用更新前快照发布事件（既有代码核对）。

## 行为要点

- 阈值语义：`threshold = max(times, 1)`；达到即认领并扣减，动作失败回补，成功动作从零开始。
- 关键字匹配：statusCodes 为空或包含；keywordPatterns 为空恒匹配；message 为空不匹配；
  正则 `(?i)` 缓存、非法 pattern 退化为大小写不敏感字面量（与参照一致）。
- until_cron：默认 UTC；时区解析失败 / 表达式非法 / 永不触发均不执行动作，reason 为
  `Disabled until <RFC3339> by auto-disable rule after N consecutive errors`。
- 后台通知：不阻塞禁用路径；目标级失败隔离；空 header key 跳过。
- 错误匹配文本：`ExtractErrorMessage` + 首个 8 KiB 响应体（超过截断，C262 的 UTF-8 差异）。

## 测试

- `tests/AxonHub.Api.Tests/ChannelAutoDisableTests.cs`（48）：模式 / 规则回退、连续计数与
  成功清零、取消与未完成记录、四个动作与两类降级、`ONE_KEY_PRESERVED`、until_cron 时区与
  非法输入、mark-unavailable 的未命中 / 成功 / 快照缺失 / 存储失败 / 读取失败 / 重载失败、
  计数器认领协议、匹配与键格式、队列投递、错误码与 8 KiB 截断（含 UTF-8 截断 U+FFFD）。
- `tests/AxonHub.Api.Tests/WebhookNotifierTests.cs`（11）：完整 body / headers 渲染与系统时区、
  无模板原样、body / header 坏模板跳过目标、目标选择与重复名、配置读失败、代理、默认
  Content-Type、发送失败、RFC3339、后台 sink 独立作用域。
- `tests/AxonHub.Api.Tests/ChannelAutoDisableTestDoubles.cs`：凭证变更、webhook sink、
  读取失败的启用通道提供者。
- `EnabledChannelsCacheTestDoubles.cs`：`RecordingEnabledChannelsRefresher.ReloadFailure`。

实测：`dotnet build tests/AxonHub.Api.Tests` 0 警告 0 错误；定向
`ChannelAutoDisableTests|WebhookNotifierTests` 59/59；全量 `dotnet test --no-build`
5878 通过 / 122 失败 / 6000 总计（31m11s），失败均为既有环境 / DB 依赖基线（上次 123），
本切片无新增失败。

静态计数：`AxonHub.Api.Tests` 5944 → 6003（+59），`AxonHub.Grains.Tests` 1754、
`AxonHub.Llm.Tests` 1517、`AxonHub.Persistence.Tests` 365 不变，四项目合计 9639。

## 台账

C236 由 gap 更新为 resolved；C149 修订（冻结版本确有 `ChannelAutoDisabledEvent` 与
`webhook_notifier.go`，残余仅 live 的 Vertex key 分支）；C017 残余更新（自动禁用与异步评估
worker 已移植）。新增 C258–C263（accepted）：RE2 vs .NET Regex、进程级计数器与后台队列、
5 字段 cron、时区解析、8 KiB 错误体 UTF-8 截断、评估异常不影响原错误。
`porting/02-differences.md` 重新生成（263 条：193 accepted / 9 gap / 61 resolved），lint 通过。
