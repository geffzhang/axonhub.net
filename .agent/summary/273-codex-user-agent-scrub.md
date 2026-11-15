# §273 Codex 出站 User-Agent 归属与客户端指纹头清理

## 背景

live `931fafff`（#2635/#2638）：Codex transformer 无条件把入站客户端 UA
复制到出站请求，导致通道级/全局 User-Agent 透传开关失效；同时客户端归因头
会经后续 inbound-header merge 泄漏上游。

## 实现

- `CodexConstants.DefaultUserAgent = "codex_cli_rs/0.159.0"`，与 Version
  默认值同步。
- `CodexHeaders.ScrubClientFingerprintHeaders`：删除 `Http-Referer`、
  `X-Title`、`X-Platform` 精确头与 `X-Zcode-*`、`X-Os-*` 前缀。
- Responses 出站：读取 RawRequest 头前先 scrub；不再读取/复制客户端 UA；
  统一设置 transformer 默认 UA。UA 透传仍由 orchestrator 中间件覆盖。
- alpha search：同样 scrub 入站头并设置默认 UA，不再复制客户端 UA。
- 全局 `blockedHeaders` 不变，保留 OpenRouter 风格通道的 `Http-Referer`。

## 测试

- 新增 alpha search UA/指纹头清理测试。
- 新增 simulator 级客户端指纹头清理测试。
- 更新 Codex simulator/executor 的 UA 期望为 `codex_cli_rs/0.159.0`。

结果：Codex 定向 **121/121**，UA/PassThrough 定向 **222/222**，Llm 全量
**3445/3445**。未跑 Api 全量。

## 台账

- 新增 C329（resolved）。
- 329 条（189 accepted / 140 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 只清理 Codex 路径；其他通道仍可按需使用 `Http-Referer` 等归因头。
- UA 透传开关的启用分支仍由 orchestrator 中间件负责。
