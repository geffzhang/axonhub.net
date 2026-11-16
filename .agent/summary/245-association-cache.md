# §245 模型关联解析缓存与诊断导出

## 背景

C239 / C179(1)：参照的 `DefaultSelector.resolveAssociations` 以 model id 为键缓存
关联解析结果 5 分钟，命中还要求关联签名、通道数量、通道最新更新时间、模型更新
时间与通道缓存版本一致；`CandidateSelectorDiagnostics.ReadAssociationCache`
把该缓存导出为 `cache.modelAssociationCache`。端口此前每次请求实时解析，
诊断导出恒输出 `[]`。

## 实现

- `src/AxonHub.Api/Orchestration/ModelAssociationCache.cs`
  - `ModelAssociationCache` 单例：按 model id 存 `AssociationCacheKey`（签名、
    通道数、两个更新时间、通道缓存版本）、已解析候选、关联文档副本、cachedAt；
    命中同时要求键相等与 `now - cachedAt < 5m`。
  - `ReadAssociationCache()` 按 model id 排序返回
    `AssociationCacheEntrySnapshot`，供诊断导出使用。
  - `ModelAssociationSignature`：FNV-1a 64，按参照 `writeAssociationSignature`
    的字段序写入 type / priority / disabled / when / 六类关联体 / exclude，
    字符串与整数以 NUL 分隔。
- `ModelCandidateSelector`
  - 新增可选 `IEnabledChannelCacheSnapshotProvider` 与 `ModelAssociationCache`；
    生产 DI 两者都注入，测试手工构造时不传则保留实时 store 路径。
  - 快照模式一次取得启用通道与 cache version，转回 matcher 使用的
    `ChannelRecord`；请求级 `When` 过滤仍在缓存之后。
  - 新增 `GetLatestChannelUpdateTime`（空列表为 Go 零值）。
- `CacheDiagnosticsCandidateSelector` 与 `CacheDiagnosticsExport`
  - 诊断选择器与 scoped 选择器共用单例缓存，并实现
    `ReadAssociationCache()`。
  - 导出在模型预览之后读取缓存，按参照字段序与 RFC3339 秒精度写出
    `cache.modelAssociationCache`。
- `Program` 注册 `ModelAssociationCache` 单例。

## 测试

- `ModelAssociationCacheTests` 7/7：快照读取与缓存填充（不查
  `IChannelStore`）、命中复用旧通道对象、通道更新时间/版本/签名/模型更新时间
  失效、5 分钟过期、条件关联共享同一解析并按请求过滤。
- `CacheDiagnosticsTests.AssociationCacheEntriesKeepTheReferenceShape`：字段序、
  时间格式、associations 的 null 字段线形。
- `ModelAssociationCacheTests|CacheDiagnosticsTests` 24/24；选择器、编排与诊断
  回归 144/144。未跑 Api 全量，新增 Api 8 例待下次全量计入。

## 台账

- C239 → resolved；C179 的关联缓存项标注关闭，其余缺口不变。
- 309 条（191 accepted / 118 resolved / 0 gap）；`diff_register.py lint`
  通过；`02-differences.md` 已重新生成。

## 边界

- 缓存按 model id 存一份，命中后请求级条件过滤仍在缓存外执行；外部直接改库
  在通道/模型更新时间、数量、版本或 TTL 变化前不可见，与参照一致。
- 诊断缓存条目只在 owner-gated 导出路径中生成，不扩大公开面。
