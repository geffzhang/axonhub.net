# §213 公开模型列表读取启用通道快照，缓存构建失败过滤落地（C178 / C287）

## 背景

参照 `ChannelService` 的启用通道缓存是模型列表、负载均衡与诊断导出的唯一通道来源：
`ModelService.ListEnabledModels` 读 `channelService.GetEnabledChannels()` 而不是实时查表
（model.go:646-755）。`reloadEnabledChannels`（channel.go:251-279）在把通道换入缓存前，对每条
`status=enabled` 行调用 `buildChannelWithOutbounds`（channel_llm.go:208-258）；构建失败
`log.Warn(ctx, "failed to build channel", channel, type, cause)` 并 `continue`，通道从缓存中消失，
直到行的 `updated_at` 指纹推进触发下一次刷新；刷新读库失败时保留旧快照。

端口此前两处偏离：`ModelService` 每次按 `status=enabled` 实时读 `IChannelStore`；grain 快照只做
descriptor 投影（纯函数），outbound 构建失败的通道仍留在快照里。C178 的 decision 把这一点登记为
“真实缺口”。

## 参照行为

- `buildChannelWithOutbounds` 先建 primary（`buildChannelWithTransformer`：按类型 fail-fast 校验凭据、
  Command Code 要求 HTTPS base URL、按类型构造 outbound；未知类型 `unknown channel type`），失败即整通道
  失败。
- 默认端点表里非 primary 的条目只有 xai 的 responses 会再调用 `buildNonDefaultEndpointOutbound`；
  其余类型复用 primary。每个用户端点都走 `buildNonDefaultEndpointOutbound`（endpoint base URL 优先、
  websocket 仅限 responses、按 api_format 分派，未知格式报错），任一失败即整通道失败。
- 成功后 `Outbounds` 按 api_format 建索引（重复格式后写覆盖先写），`preloadModelPrices` 与
  `OnSwap` 的 token provider 启停不属于本切片。

## 端口实现

- `ModelService` 改依赖 `IEnabledChannelProvider`（`GrainEnabledChannelProvider` → `EnabledChannelsGrain`
  快照），新增 `ToChannelRecord` 把 descriptor 映射回关联匹配与端点表读取所需的 `ChannelRecord` 形状
  （status 固定 enabled，快照只含启用行）。`read_channels` 显式 scope 检查保持不变。
- 新增 `BuildFilteringEnabledChannelSource`（`src/AxonHub.Api/Orchestration/EnabledChannelBuildFilter.cs`）
  装饰 `IEnabledChannelSource`：`GetLatestUpdatedAtAsync` 直接委托，`ListEnabledAsync` 对每条描述符
  调用 `ChannelBuildValidation.Validate`，失败记 warning 并从结果剔除。装饰器放在 Api 层，grain 与两个
  测试集群的 silo 注册不变；校验发生在数据库读取之后、grain 换入之前，与参照的阶段一致。
- `ChannelBuildValidation` 用 `ChannelOutboundFactory.Create` 演练构建：primary 取
  `PrimaryApiFormat(type)`（`opencode_go_anthropic` 无默认端点，回退 `openai/chat_completions` 后仍落
  Anthropic 构造）；xai 的非 primary 默认端点（responses）逐个构建；`ChannelEndpoints.ParseUser`
  的每个非空格式端点逐个构建。构建时 `grainFactory` / `channelExecutor` / `apiKeySelection` 均为 null，
  走各转换器的本地回退（OAuth 解析、copilot exchanger 构造期无网络 I/O，不触碰共享 key 轮转状态），
  与端点检测路径的既有用法一致。
- `Program.cs` 注册 `PostgresEnabledChannelSource` 具体类型，并让 `IEnabledChannelSource` 解析为装饰器。
- 更新 `EnabledChannelsGrain.RefreshCoreAsync` 中“descriptor projection is pure”的过时注释。

## 差异

新增 C287（resolved）：构建校验的落点（校验期构建产物丢弃，参照缓存复用）与重复端点残余——用户端点
按 api_format 去重，后写覆盖先写，被覆盖的重复端点不参与校验；参照会逐个构建并因任一失败剔除通道，
端口在该极端存储数据下可能保留参照会剔除的通道。C178 的真实缺口标记已改为闭合说明；C226 的
“ModelService 仍实时读库”过时表述已更新。台账 287 条（203 accepted / 0 gap / 84 resolved）。

## 测试

- 新增 `tests/AxonHub.Api.Tests/EnabledChannelBuildFilterTests.cs`（10 例）：`GetLatestUpdatedAtAsync`
  委托且不读列表、构建成功保留顺序、缺 key / 全禁用 key 剔除、OAuth-only 类型（xai_subscription）、
  codex 的 key 或 OAuth、未知类型、Command Code 非 HTTPS、用户端点 websocket 非法格式、次级端点失败
  剔除整通道、warning 携带 channel / type / exception。
- `ModelServiceTests` 新增 `ListReadsTheEnabledChannelSnapshotNotTheStore`：快照独有的通道可见、
  `FakeChannelStore.ListEnabledCalls` 为 0；`ServiceWithStores` 增加可选 provider 参数，其余 43 处
  `Service(...)` 调用保持不变（默认 provider 由 fixture 的 store 投影）。
- 定向运行：EnabledChannelBuildFilterTests + ModelServiceTests 49 通过；
  ModelsEndpointTests + GeminiEndpointTests + CodexEndpointTests 51 通过；未跑 Api 全量回归。
