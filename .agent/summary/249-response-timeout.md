# §249 按通道的响应超时覆盖

## 背景

live 提交 `9142eb6a`（#2630）允许通道 `settings.responseTimeout` 选择
INHERIT（零值，沿用全局 retry policy）或 CUSTOM；CUSTOM 可按字段覆盖
`streamFirstEventTimeoutSeconds` / `nonStreamResponseTimeoutSeconds`，nil 字段
继续继承，显式 0 关闭该超时。超时必须在每次尝试 `TransformRequest` 之后解析，
这样重试切换通道时才会使用新通道的覆盖。

端口此前已移植 GraphQL 类型、写入校验、`NormalizeResponseTimeout`（INHERIT
时丢弃覆盖值），但运行时 Pipeline 始终使用全局值；通道覆盖保存后没有效果。

## 实现

- `AxonHub.Llm.Pipeline.IResponseTimeoutProvider`
  - `ResponseTimeouts()` 返回两个可空 `TimeSpan`；null 表示继承，0 表示关闭。
- `Pipeline`
  - `EffectiveResponseTimeouts()` 在 `TransformRequest` 之后读取 provider 覆盖，
    并合并到 `PipelineOptions` 的两个默认值。
  - stream 路径与 auto-aggregate / 普通 non-stream 路径分别使用解析后的值。
- `PersistentOutboundTransformer`
  - 实现 `IResponseTimeoutProvider`；读取当前候选的 `ChannelSettingsDraft.
    ResponseTimeout`，仅 `mode == "custom"` 生效，两个字段分别映射为秒。
  - 畸形 / 缺失 settings、inherit 模式返回 `(null, null)`。
- 既有写入侧不变：INHERIT 覆盖值仍在保存时清空，校验边界 0..600。

## 测试

- `PipelineResponseTimeoutTests` 8 例：无 provider、provider 无覆盖、单/双字段
  覆盖、0 关闭；Process 级验证 30ms 通道覆盖先于 150ms 执行器延迟触发
  `NonStreamResponseTimeout`，以及通道 0 覆盖压过全局 10ms 超时。
- `ResponseTimeoutProviderTests` 11 例：null / 空 / inherit（含带旧值）/ 畸形
  settings 继承；custom 单/双字段与 0 值；无候选返回 null。
- Llm 全量 **3310/3310**；Api 定向 76/76（ResponseTimeoutProvider、
  ChannelTransformOptions、ChatCompletionOrchestrator）。未跑 Api 全量，新增
  Llm 8 例、Api 11 例待下次全量计入。

## 台账

- 新增 C311（resolved）。
- 311 条（190 accepted / 121 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 覆盖按当前候选解析；同一通道内的模型重试仍使用该通道的值。
- 全局超时仍由 `retry_policy.enabled` 门控：禁用时 PipelineOptions 传入 0，
  通道 custom 覆盖按 live 语义仍可设置非零值并生效（与参照的 per-attempt
  resolver 一致）。
