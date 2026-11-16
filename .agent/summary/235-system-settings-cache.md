# §235 系统设置的进程内缓存

## 背景

C029：参照的 getSystemValue 先查 SystemService.Cache，setSystemValue 后失效；
端口每次请求都通过 Dapper 查询 systems 表。

## 实现

- `SystemValueCache`：单例 `ConcurrentDictionary<string, SystemRecord>`。
- `CachingSystemStore`：ISystemStore 装饰器。
  - `GetByKeyAsync` 命中缓存直接返回；缺失键不缓存。
  - `Create` / `TryCreate` / `Set` / `CreateIfAbsent` /
    `UpdateValueIfUnchanged` 成功后清空整个缓存。
  - 其余方法透传。
- `Program.cs`：`AddSingleton<SystemValueCache>` +
  `AddScoped<SystemStore>` + 装饰后的 `ISystemStore`。
- `PostgresRequestChainRecorder` 与 `PostgresChannelPricingSource` 共用同一
  缓存（可选参数，测试直接构造时保持旧行为）。

## 测试

- `CachingSystemStoreTests`：4/4（命中只读一次、缺失不缓存、写入清空全部键、
  CompareAndSet 失效）。
- `ChannelModelPriceStoreTests` + cache 定向：30/30。
- Api `ConfigurationSurfaceTests`：10/10（Program DI 编译）。
- 未跑 Api 全量；新增 Persistence 4 例待下次全量计入。

## 台账

- C029 → resolved。
- 307 条（197 accepted / 110 resolved / 0 gap）；`diff_register.py lint`
  通过；`02-differences.md` 已重新生成。

## 边界

- 外部直接改库在写入或重启前不可见，与参照的进程内缓存一致。
- C026（API key 缓存）仍未移植，另条跟踪。
