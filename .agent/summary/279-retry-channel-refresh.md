# §279 同通道重试前刷新渠道凭据（live #2545）

## 背景

live 提交 `8262e95a`（#2545）：请求固定候选选择时的渠道快照。凭据自动禁用由
性能 worker 异步提交，快照与按快照构建的 key provider 仍指向刚被禁用的凭据，
剩余同通道重试预算会全部浪费在该凭据上。参照在 `PrepareForRetry` 开头重读存储
渠道（`retry_channel_refresh.go`）：

- 渠道已删除 / 禁用 / 归档：返回 `errChannelUnavailableForRetry`，pipeline
  切换下一候选或结束请求；
- 本次尝试使用的凭据有生效禁用记录且仍有其他可用凭据：用存储渠道重建候选与
  outbound；
- 没有可用凭据、刷新快照读不到、或重建不出 transformer：同样放弃候选；
- 瞬时读取失败、禁用尚未提交、无凭据渠道（本地 Ollama）：保持原候选；
- 凭据比较使用 `GetEnabledCredentialRefs`（OAuth 折叠为单一引用），临时禁用
  过期视为可用。

## 实现

- 新增 `IChannelRefreshSource` / `PostgresChannelRefreshSource`：
  `GetEnabledChannel(id)` 同步读取 live + enabled 行，返回
  `Found` / `Unavailable` / `Failure`。禁用、软删和未知 id 都是 confirmed miss，
  与参照先取行再判状态的结果一致；异常保持为瞬时失败。
- `PostgresEnabledChannelSource` 抽出 `ToDescriptor`，供列表与单行读取共用。
- `ChannelCandidate.Clone()`：深拷贝模型条目与协议表，刷新在副本上完成，重建
  失败时原候选保持不变。
- `PersistentOutboundTransformer` 新增可选 `IChannelRefreshSource`：
  - `TryPrepareForRetry` 最前面调用 `RefreshChannelBeforeRetry`；
  - 尝试凭据取 `CurrentApiKey`，OAuth 渠道回落到固定 `__oauth__` 引用；
  - 存储渠道字段应用到副本：名称、权重、更新时间、类型、BaseURL、凭据、
    禁用列表、四组 settings 解析结果、DefaultTestModel、Endpoints、Tags、
    Policies；模型列表、评分、位置与 key override 保留；
  - 重建成功后才替换候选并停止旧 transformer；失败返回
    `ChannelUnavailableForRetry`，pipeline 走换通道分支；
  - 畸形 `disabled_api_keys` 文档按降级读取处理，保持原候选。
- `ChatCompletionOrchestrator` 注入该接缝并传给 transformer；`Program.cs`
  注册单例。

## 测试

- `RetryChannelRefreshTests` 14 例：提交禁用后实际认证凭据切换到 sk-b、禁用未
  提交、OAuth 不误判、OAuth 引用被禁用、无凭据渠道、空尝试凭据、瞬时失败、
  四种离开服务状态、无可用凭据、重建失败保留原候选、畸形禁用文档、旧
  transformer 停止。
- `ChannelRefreshSourceTests` 1 例：live+enabled 读取；disabled / 软删 / 未知
  均为 miss。
- 定向 78/78（含 `PersistentOutboundRetryTests`、`PersistentOutboundCancellationTests`、
  `ChannelTransformOptionsTests`）。
- Api 全量：**7167 例（7164 通过 + 3 跳过，0 失败，40m53s）**。
- Persistence 全量：**431/431，3m25s**。

## 台账

- 新增 C335（resolved）。
- 335 条（190 accepted / 145 resolved / 0 gap）；lint 与
  `02-differences.md` 重生成通过。

## 边界

- 端口候选均来自 enabled 缓存，候选上没有独立的 Status 字段；因此不实现参照
  「候选初始状态非 enabled 则跳过」的分支。生产路径等价；直接构造的候选按已
  启用处理。
- 刷新只重读渠道行，不重算候选的模型关联、评分或模型卡输出上限；参照同样保留
  `candidate.Models`，仅替换 `candidate.Channel`。
