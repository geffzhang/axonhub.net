# §236 API key 鉴权的进程内缓存

## 背景

C026：参照通过 live cache 读取 API key（按 key 值缓存），更新时失效；项目每次
请求再查库。端口每次鉴权都用 Dapper 查询 api_keys。

## 实现

- `ApiKeyCache`：单例按 key 值缓存 `ApiKeyRecord`。
- `CachingApiKeyStore`：IApiKeyStore 装饰器。
  - `GetByKeyAsync` 命中缓存直接返回；缺失 key 不缓存。
  - `Create` / `Update` / `BulkUpdateStatus` 成功后清空缓存。
  - `GetById` / `List` / `Count` / `Exists` / 事务与锁方法透传。
- `Program.cs`：`AddSingleton<ApiKeyCache>` + `AddScoped<ApiKeyStore>` +
  装饰后的 `IApiKeyStore`。
- 项目读取未缓存（与参照一致）。

## 测试

- `CachingApiKeyStoreTests`：5/5（命中只读一次、缺失不缓存、Create、
  Update 含 key 轮换、BulkUpdateStatus 失效）。
- Api `ConfigurationSurfaceTests`：10/10（Program DI 编译）。
- 未跑 Api 全量；新增 Persistence 5 例待下次全量计入。

## 台账

- C026 → resolved。
- 307 条（196 accepted / 111 resolved / 0 gap）；`diff_register.py lint`
  通过；`02-differences.md` 已重新生成。

## 边界

- 外部直接改库在写入或重启前不可见，与参照的进程内缓存一致。
- 用户停用/删除 personal key 的级联仍未移植（C176(6)）；该路径不产生写，
  因此没有额外的缓存失效缺口。
