# §231 saveChannelEndpoints 的 live 归一化与 settings 同写

## 背景

C148：live 的 `SaveChannelEndpoints` 会在校验与保存前归一化端点（按通道
BaseURL 推断/改写 Responses transport），并把归一化后的 settings 与 endpoints
在同一次更新里写回；端口此前按冻结基线只写 endpoints。

## 实现

- `ChannelValidation`
  - 新增 `NormalizeEndpoints`（draft 版）：`openai/responses` 在 ws/wss 基址上
    改写为 `openai/responses-ws`；显式 `http` transport 在 ws 基址上改写为
    `http(s)` 并清空 transport；非 Responses 端点原样保留。
  - `ValidateEndpoints(endpoints, baseUrl)`：先校验原始 transport，再用归一化
    副本做格式/重复/websocket/path 检查。
  - `ValidateEndpointsForChannelType(..., baseUrl)` 透传；`ValidateModelProtocols`
    新增可选 `baseUrl`，用归一化后的默认端点与 input endpoints 计算可用格式。
- `ChannelLifecycleService`
  - `SaveEndpointsAsync` 传入 `snapshot.BaseUrl`；新增
    `NormalizeModelProtocols`（live `channel_endpoint_responses.go:95-110`），在
    已存通道旧 Responses 意图为 websocket 时把 settings 的 `openai/responses`
    改写为 `-ws`。
  - 归一化端点与 settings 一同写入；`settings` 为 null 时写 NULL。
- `IChannelWriteStore.SaveEndpointsAsync` 增加 `settingsJson` 参数，EF 实现在
  同一 `updated_at` 守卫下 `SetEndpoints` + `SetSettings`。

## 测试

- Api `AdminChannelEndpointsEndpointTests|AdminChannelLifecycleEndpointTests|
  AdminChannelEndpointDetectEndpointTests`：62/62。
  - 新增：ws 基址推断 responses-ws、显式 http 在 ws 基址改写 https、
    settings.modelProtocols 随旧 Responses 意图改写；首例改为断言 settings 同写。
- Api `AdminChannelCreateEndpointTests|AdminChannelUpdateEndpointTests`：38/38。
- Persistence `ChannelWriteStoreTests`：56/56（首例改为断言两列同写）。
- 未跑 Api 全量；新增 Api 3 例待下次全量计入。

## 台账

- C148 → resolved。
- 305 条（199 accepted / 106 resolved / 0 gap）；`diff_register.py lint` 通过；
  `02-differences.md` 已重新生成。

## 边界

- `createChannel` / `updateChannel` 的校验入口仍不传 baseUrl，行为不变（C144 的
  校验顺序差异仍待单独收口）。
- `NormalizeEndpoints` 仅处理 Responses family；其他 apiformat 的 transport
  语义与冻结基线一致。
