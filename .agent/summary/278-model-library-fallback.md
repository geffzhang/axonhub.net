# §278 渠道无价格时回退模型库价格（live #2655）

## 背景

live 提交 `cc1c7e5d`（#2655）：渠道价格是创建时的一次性快照，模型库后来新增的
价格不会回填；于是渠道没有该模型的显式价格时，usage cost 一直为空。参照在
`computeUsageCost` 中增加模型库回退：

- 先查精确 `model_id`，再去掉 `-high` / `-medium` / `-low` 后缀查基础模型；
- 只接受 `enabled` / `disabled` 且未软删的模型库条目；
- 渠道价格仍然优先；
- 命中后 usage log 的 `cost_price_reference_id` 写成
  `model_library:<请求的 model_id>`（保留后缀，便于追溯）；
- 请求上下文安装 memo，成本注入与 usage log 创建共享一次查询（含 miss）；
  查询失败不写 memo，后续健康查询可重试。

GraphQL 的 `costPriceReferenceID` 字段与前端提示已在基线前端中存在，缺口只在
服务端计费。

## 实现

- `IChannelPricingSource` 新增：
  - `IsEnabledChannel`：区分「禁用渠道」与「启用但无价格」，只有后者允许回退；
  - `FindModelLibraryModelCard`：返回命中 / miss / failed，避免把数据库或解码
    失败缓存成 miss。
- `PostgresChannelPricingSource`：渠道启用状态走 EF 查询；模型库查询限定
  `status IN (enabled, disabled)` 且 `deleted_at = 0`，异常映射为 failed。
- `UsageCostService`：
  - 渠道价格表解析失败仍整通道返回 null（对应 preload 失败），不回退；
  - 精确模型未命中后调用 `ResolveModelLibraryPrice`；
  - 实例级 memo 缓存命中与 miss，错误不缓存；
  - 回退结果使用 synthetic reference id。
- `ModelLibraryPrice`：synthetic 前缀与候选模型 id（精确优先、后缀剥离、去重）。
- `Program.cs`：`IUsageCostService` 由 singleton 改为 scoped。参照的 memo 挂在
  请求 context 上；scoped 实例是同等的请求边界，编排器与持久化中间件共享同一
  实例。
- `ModelCardPricing.ToPriceFromJson`：供回退路径复用模型卡转换。

## 测试

- `UsageCostServiceTests`：无渠道价回退、渠道价优先、不可定价条目、后缀剥离、
  精确优先、候选顺序、命中 / miss memo、失败不缓存、禁用渠道不回退、解析失败
  不回退；与既有用例合计定向 **32/32**。
- `UsageCostInjectionTests.RealCostServiceFallsBackToTheModelLibrary`：真实
  cost service 经编排器把库价写入响应 usage，并在 usage log 记录
  `model_library:` reference id。
- `ChannelModelPriceStoreTests.SyncSeamReadsPricesAndSystemValues`：enabled /
  disabled 模型可查，archived / 软删不可查；`IsEnabledChannel` 命中与未知 id。
  定向 **1/1**。
- Api 全量：**7153 例（7150 通过 + 3 跳过，0 失败，28m51s）**。
- Persistence 全量：**430/430，3m12s**。

## 台账

- 新增 C334（resolved）。
- 334 条（190 accepted / 144 resolved / 0 gap）；lint 与
  `02-differences.md` 重生成通过。

## 边界

- memo 从参照的 context 改为 scoped 服务实例；同一 HTTP 请求内的多次计算共享，
  跨请求不共享。直接 `new UsageCostService` 的测试实例即一次请求。
- 模型库 `First` 未指定排序，与参照一致；同 model_id 多状态行并存时结果由数据库
  返回顺序决定。
