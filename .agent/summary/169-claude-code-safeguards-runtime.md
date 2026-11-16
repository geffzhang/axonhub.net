# §169 Claude Code safeguards 评审运行时（C243 收口，81 例）

日期：2026-11-10
参照（post-baseline 漂移，按工作仓库 `f272549f`）：
`internal/server/safeguards/{evaluator,classifier,stream,response}.go`、
`internal/server/api/{safeguards,anthropic,chat}.go`、
`internal/server/orchestrator/anthropic_terminal_stream.go`。

## 背景

§159 只移植了 safeguards 设置面，评审运行时登记为 C243 gap。本切片把运行时接回
`/v1/chat/completions`、`/v1/messages` 与 `/anthropic/v1/messages`（三者共用
`ChatCompletionEndpoint`），并补齐参照 orchestrator 的 Anthropic 终端流中间件——没有它，
passthrough 评审会在 provider 传输未关闭时把正常完成误判为取消。

## 交付

- `src/AxonHub.Api/Safeguards/SafeguardsRequest.cs`：`HasDangerousToolUse`（gjson 探针的
  等价实现）与 `Prepare`（只剥离 `dangerous_tool_use`、保留其他 safeguard、删除
  `Content-Length`、浅拷贝请求）。
- `src/AxonHub.Api/Safeguards/SafeguardsEvaluator.cs`：system/context/transcript 构造与预算、
  工具合法性、逐模型与回退尝试、整体/单次 `SafeguardsDeadline`、verdict 解析与去重、
  `EscalateOnDenial` 二次意见、`safeguard_results` 注释。
- `src/AxonHub.Api/Safeguards/SafeguardsClassifier.cs`：Fork 语义与分类器线路形状。
- `src/AxonHub.Api/Safeguards/SafeguardsStream.cs`：终端 tail drain、1s abort、annotation。
- `src/AxonHub.Api/Safeguards/SafeguardsJson.cs` / `SafeguardsTypes.cs`：Go 形状 JSON 辅助与
  运行时类型。
- `src/AxonHub.Api/Orchestration/AnthropicTerminalStreamMiddleware.cs`：`message_stop` 后
  关闭 provider 传输、`Err` 归零；`ChatCompletionOrchestrator` 将其注册为 raw-stream
  中间件末位（逆序应用时最先包裹 provider 流）。
- `src/AxonHub.Api/Endpoints/ChatCompletionEndpoint.cs` 与 `Program.cs`：运行时设置读取、
  classifier factory 与端点接线。

## 行为要点

- 探针命中才重读运行时设置；设置读取失败 500
  `Unable to load Claude Code safeguards settings`；解码失败 400。
- 模型名 trim + 去重，MaxTokens 默认 4096；transcript 默认 24000 字节、context 16000、
  tool 64000、verdict 64000，裁剪按 UTF-8 字节且不拆坏 rune。
- 尝试预算到期归因 timeout，其他失败归因 error；回退共享整体预算；耗尽时保留已评估
  verdict；`EscalateOnDenial` 只对首个模型的拒绝做二次意见，回退只重审未决工具。
- 流式路径在终端 `message_delta` 前 drain 至 `message_stop`（256 事件 / 1 MiB 上限），
  评审后回放；reviewer 错误优先于源错误。
- `AnthropicTerminalStream` 转发一次 `message_stop`（深拷贝 Data），关闭 provider 传输且
  `Err` 恒为 null；`message_stop` 之前的错误原样保留。

## 测试

`tests/AxonHub.Api.Tests` 新增 81 例：`SafeguardsEvaluatorTests` 30、
`SafeguardsFallbackTests` 20、`SafeguardsEndpointTests` 16、`SafeguardsClassifierTests` 9、
`AnthropicTerminalStreamTests` 6。`dotnet test --filter
"FullyQualifiedName~Safeguards|FullyQualifiedName~AnthropicTerminalStream"`（含重编译）
105/105 通过（另含 §159 的 24 例设置面）；`SafeguardsFallbackTests` 连续 4 轮 20/20。

测试时序说明：Go 的 fallback 测试使用 10ms `AttemptTimeout`；在 Windows 上线程调度抖动
（观测到约 16ms）会让同步成功的评审调用被判为过期，因此端口测试只对显式 timeout 场景保留
10ms，其余场景使用宽裕预算。生产语义不变。

## 差异台账

- C243：gap → resolved。
- 新增 C264（评审日志与 operation name 未移植）、C265（gjson 探针宽容度）、C266（解码
  错误文案）、C267（Timer 精度与上限），均为 accepted。
- `SafeguardsRequest.cs` 注释中的 C243 引用改为 C265；`SafeguardsDeadline` 的 Timer 上限
  从 `uint.MaxValue` 修正为 `uint.MaxValue - 1` 毫秒（.NET 合法上界）。
- `porting/02-differences.md` 重新生成（267 条：197 accepted / 8 gap / 62 resolved），
  lint 通过。

## 下一步

C249：channelProbe 的 writer / 每分钟调度 / GC 清理。
