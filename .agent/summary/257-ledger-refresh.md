# §257 accepted 差异台账复核

## 背景

在 §247/§251/§253 等切片中多次发现 accepted 条目的“真实缺口”段落在后续
切片落地后没有同步更新。本轮对仍带“真实缺口”标注的 accepted 条目逐条核对
源码与测试，闭合五条陈旧描述。

## 复核结果

| 条目 | 旧描述 | 复核结论 |
|---|---|---|
| C176 | `apiKeyTokenUsageStats` / `CreateLLMAPIKey` / `GetForRead` / profile 模板 / 用户 personal key 级联未移植 | 全部已实现并有测试；`UpdateAPIKeyScopes` 在参照 SDL 中只有 input、无 mutation 字段 |
| C177 | `GetModelByModelID` / `ListEnabledModels` 合并仍属 C088 | §89 / §213 已落地，ModelServiceTests / ModelsEndpointTests 覆盖 |
| C178 | 通道来源实时读库 | §213 改为 EnabledChannelsGrain 快照，真实缺口关闭 |
| C193 | Project.usageLogs / Channel.usageLogs 未接线 | 已接线，ProjectUserEdges / ResidualEdges 测试覆盖 |
| C179 | trace 粘性、profile 过滤、原生工具、流策略、配额闸门未接入 | §91–§95 全部关闭；关联解析缓存由 §245 关闭 |

五个条目就地追加 `§257 复核` 说明，状态保持 accepted（事务、Decimal、枚举
输出、where 子集、顺序确定化等实现差异仍在），不新增条目。

## 测试

- 本切片无代码与测试改动；引用的既有测试为准。
- 未跑 Api 全量。

## 台账

- 317 条（190 accepted / 127 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 复核只针对台账文字与现有测试/源码的一致性，不重新执行全量测试；
  引用测试均为此前切片记录且在当前工作树中存在。
