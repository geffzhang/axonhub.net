# §243 data storage 行/客户端缓存

## 背景

C196：参照的 DataStorageService 缓存 GetDataStorageByID / GetPrimaryDataStorage
行（30 分钟），并按分钟刷新 fs/object-store 客户端；端口每次操作新建后端，
行读取无缓存。

## 实现

- `DataStorageCache`（单例）：
  - 行缓存 30 分钟（按 id 与 primary），缺失行不缓存；
  - 后端按 storage id 复用 1 分钟；过期后重建，旧客户端在最后一个租约释放时
    Dispose；
  - `Invalidate()` 清空行并 retire 所有后端，未释放的旧后端延迟到租约释放。
- `CachingDataStorageStore`：GetByIdAsync / GetPrimaryAsync 命中缓存；Create /
  Update 清空缓存；GetByIdIncludingDeleted / GetPrimaryIncludingDeleted 透传
  （GC 的 SkipSoftDelete 路径）。
- `CachingDataStorageBackendFactory`：Create 返回非拥有租约，缓存命中复用。
- Program 注册装饰器；`PostgresRequestChainRecorder` 通过可选 `DataStorageCache`
  共享同一缓存（测试直接构造时保持旧行为）。

## 测试

- `DataStorageCacheTests`：5/5（行缓存/写失效/30 分钟过期、缺失不缓存、后端
  复用/写失效/1 分钟过期、租约释放后 Dispose）。
- `AdminDataStoragesEndpointTests|RequestPayloadStorageTests|DataStorageCacheTests`
  定向：67/67。
- 未跑 Api 全量；新增 Api 5 例待下次全量计入。

## 台账

- C196 → resolved。
- 309 条（192 accepted / 117 resolved / 0 gap）；`diff_register.py lint`
  通过；`02-differences.md` 已重新生成。

## 边界

- 外部直接改库的行可见性由 30 分钟 TTL 约束，与参照行缓存一致；后端按 1 分钟
  TTL 近似参照的每分钟 fs 刷新。
- GC 的 SkipSoftDelete 读取保持实时，不经行缓存。
