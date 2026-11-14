# §232 create/update 写入面的 live 校验顺序与归一化

## 背景

C144 在 C148 关闭 saveChannelEndpoints 后只剩 createChannel / updateChannel：
端口仍按冻结基线在 settings 之后校验端点，且不做端点/协议归一化。

## 实现

- `ChannelLifecycleService.PrepareCreate`
  - 在 settings 块之前用 `command.BaseUrl` 校验端点
    （`ValidateEndpointsForChannelType(..., baseUrl)`）。
  - `NormalizeModelProtocols` + `NormalizeEndpoints` 应用于输入
    settings/endpoints，再执行 settings 校验（含 `ValidateModelProtocols`）。
  - Vertex 校验移到 settings 块之后，对应 live `channel.go:627`。
- `ChannelLifecycleService.UpdateAsync`
  - settings 校验后加载 effective type/baseURL/endpoints，先校验端点；
  - 归一化 settings.modelProtocols（输入 settings 用 effective
    endpoints/baseURL，缺省时用 stored settings 的 source）与 endpoints；
  - 按归一化 settings 校验 model protocols；仅当输入 settings 非空或
    normalization 实际改写时回写 `command.Settings`。
- `NormalizeModelProtocols` 改为接收 `endpointsJson` / `baseUrl` 并返回
  `changed`，供 create/update/save 复用。

## 测试

- Api `AdminChannelCreateEndpointTests|AdminChannelUpdateEndpointTests`：40/40。
  - 校验顺序改为 live：端点错误先于 settings 错误
    （`EndpointValidationRunsBeforeTheSettingsBlock`）。
  - 新增 create/update 归一化用例：ws 基址下 responses→responses-ws、
    modelProtocols 同步改写。
- Api `AdminChannelEndpointsEndpointTests|AdminChannelLifecycleEndpointTests|
  AdminChannelEndpointDetectEndpointTests`：62/62。
- Api `AdminChannelSettingsEndpointTests|AdminChannelBulkEndpointTests|
  AdminChannelDuplicateBulkEndpointTests`：47/47。
- 未跑 Api 全量；新增 Api 2 例待下次全量计入。

## 台账

- C144 → resolved；表示层剩余差异拆到新条目 C306（accepted）：
  代理枚举大小写、retryable .NET Regex、Command Code .NET Uri。
- 306 条（199 accepted / 107 resolved / 0 gap）；`diff_register.py lint`
  通过；`02-differences.md` 已重新生成。

## 边界

- C306 的表示层差异不影响合法请求的持久化结果，测试以共享成功/失败划分
  与错误前缀锁定。
- C148 的 saveChannelEndpoints 行为不变，复用同一
  `NormalizeModelProtocols` 签名（`out _`）。
