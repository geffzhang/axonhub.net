# §233 safeguards 评审器日志

## 背景

C264：live 评审器在 denial escalation 与 reviewer fallback 两处输出 warning，
端口没有日志接缝。本轮补日志，并把 classifier operation name 拆到 C307。

## 实现

- `SafeguardsEndpointOptions.Logger`：可空 `ILogger<SafeguardsEvaluator>`；
  Anthropic Messages 处理器从 `ILoggerFactory` 注入。
- `SafeguardsRequest.Prepare` / `SafeguardsEvaluator.Create` 接收可选 logger。
- `SafeguardsEvaluator` 用 `LoggerMessage.Define`（满足 CA1848）：
  - `Safeguards denial escalation assessed`：Model / ToolId /
    PreviousOutcome / Outcome；
  - `Safeguards reviewer fallback`：Model / NextModel / Reason /
    PendingTools。
  - 条件与 live `evaluator.go:287,301` 一致：前一条在既有 evaluated verdict
    被后续评审覆盖时；后一条在仍有 pending、还有下一个模型且外层 deadline
    未过期时。
- 未注入 logger 时保持静默（库调用与既有测试）。

## 测试

- `SafeguardsFallbackTests.FallbackWarningsCarryTheReferenceFields`：断言两条
  warning 的级别与全部结构化字段。
- Safeguards 定向：100/100。
- 未跑 Api 全量；新增 Api 1 例待下次全量计入。

## 台账

- C264 → resolved；classifier operation name 拆到 C307（accepted）。
- 307 条（199 accepted / 108 resolved / 0 gap）；`diff_register.py lint`
  通过；`02-differences.md` 已重新生成。

## 边界

- C307：operation name `safeguards-classifier` 仍未设置；该标签不参与路由、
  配额、计费或响应。
- 日志不改变评审行为与响应字节。
