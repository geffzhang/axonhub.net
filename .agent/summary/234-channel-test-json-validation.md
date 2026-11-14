# §234 通道测试的加载期 JSON 校验

## 背景

C157：参照 ent 在加载通道时解码 policies 与 disabled_api_keys，非法 JSON 会
使整次加载失败；端口读取原始列，只在读取 stream 策略/禁用 key 时宽松解码并
视为缺省。

## 实现

- `ChannelTestService.LoadChannelAsync` 在共享加载之后调用
  `ValidateStoredColumns`：
  - `policies` 非空且非 `null` 时用 `LlmJson.Deserialize<ChannelPoliciesDraft>`
    解码；
  - `disabled_api_keys` 非空且非 `null` 时用
    `ChannelWriteSerialization.ParseDisabledApiKeys` 解码；
  - `JsonException` 包装为 `failed to get channel for test: ...`。
- 三个入口（TestChannel / TestSingleApiKey / TestChannelApiKeys）共用该加载。
- 合法数据的 `StreamRequired` / `DisabledKeySet` 行为不变。

## 测试

- `ChannelTestServiceTests.TestChannelAPIKeysRejectsMalformedPolicyAndDisabledColumns`
  （Theory：policies 非法、disabled_api_keys 非法各一例）断言前缀与零执行器调用。
- ChannelTest 定向：63/63。
- 未跑 Api 全量。

## 台账

- C157 → resolved。
- 307 条（198 accepted / 109 resolved / 0 gap）；`diff_register.py lint`
  通过；`02-differences.md` 已重新生成。

## 边界

- 错误内层文案来自 System.Text.Json，归入既有 JSON 文案差异（C002/C064）；
  可观察失败模式与前缀与参照一致。
- 其他服务（禁用记账、自动禁用等）的宽松读取策略不变。
