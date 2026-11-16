# §157 系统初始化与首启（/health 对齐 + 2 个 admin 端点）

日期：2026-11-09
参照：`internal/server/biz/system.go:696-710`（IsInitialized）、`721-846`（Initialize 事务）、
`962-978`（setSystemValue）、`internal/server/biz/version.go:32-35`（SetVersion）、
`internal/server/api/system.go:38-66,76-101,132-185`（响应结构、json / binding 标签、
400 / 500 分类）、`internal/server/routes.go:81-99`（公开与 unSecureAdmin 路由）、
`internal/build/info.go:30-54`（build.Info 的 json 标签）、
`internal/ent/migrate/datamigrate/migrator.go`（未初始化时跳过全部数据迁移）。

## 背景

管理面的首启流程依赖 `GET /admin/system/status` 判断是否需要渲染初始化向导，
`POST /admin/system/initialize` 完成 owner、默认项目与 Primary 存储的落库；前端与探针
依赖 `GET /health` 的 build 信息。端口此前只有最小化的 `/health`，且启动时无条件播种
Primary，导致全新库在初始化前就没有 `system_initialized` 却已存在 Primary。

## 交付

- `src/AxonHub.Api/Settings/SystemInitializationService.cs`：`IsInitializedAsync` 透传
  `ISystemSettingsAdminService`；`InitializeAsync` 复刻参照的检查、事务外 secret 生成、
  事务内步骤顺序与每个错误前缀；用回调完成标记区分 `failed to start transaction` 与
  `failed to commit transaction`；`InitializationStepException` 防止二次加前缀。
- `src/AxonHub.Api/Endpoints/SystemHandlers.cs`：`MapSystemRoutes` 接线 `/health`、
  `GET /admin/system/status`、`POST /admin/system/initialize`；health 用
  `SystemBuildInfo` + `GoTime.Rfc3339Nano` + `GoJson.Escape` 复刻字段序、omitempty 与
  HTML 转义；initialize 用 Web JSON 绑定、`ManagementAuthHandlers.EmailRegex()` 与 rune
  计数，400 / 500 响应形状与 gin 一致。
- `src/AxonHub.Api/Management/ProjectManagementService.cs`：拆出
  `CreateForUserAsync(command, currentUserId)`，让初始化事务安装刚创建的 owner；
  `CreateAsync` 从请求身份读取用户，行为不变。
- `src/AxonHub.Persistence/DataStorageBootstrap.cs`：新增
  `IsSystemInitializedAsync`（大小写不敏感、排除软删除行），作为 v0.4.0 播种的启动闸。
- `src/AxonHub.Api/Program.cs`：注册 `ISystemInitializationService`；把 Primary 播种放进
  `IsSystemInitializedAsync` 判定；用 `MapSystemRoutes` 取代原 `/health` 最小端点。
- 测试侧：`FakeUserStore.CreateFailure` 注入 owner 创建失败。

## 行为要点

- status：`{"isInitialized":bool}`；`TRUE` 视为已初始化；读取失败 → 500
  `{"error":{"type":"Internal Server Error","message":"Failed to check system status"}}`。
- initialize：坏 JSON / `null` / 空 body / 缺 email / 非 email / 密码不足 6 个 rune /
  缺三个名称之一 → 400 `Invalid request format`；状态读取失败 → 500
  `Failed to check initialization status`；已初始化 → 400 `System is already initialized`；
  服务失败 → 500 `Failed to initialize system: <err>`；成功 → 200
  `{"success":true,"message":"System initialized successfully"}`。
- 事务：owner（activated、isOwner、`["*"]`、preferLanguage 空→en）→ 默认项目
  （Default / Default project，Admin / Developer / Viewer + owner 关联）→ secret / brand
  → Primary（database / `{}` / active）→ default_data_storage_id → initialized → version。
- health：`status` / `timestamp`（本地时区 RFC3339Nano）/ `version` / `build`
  （version、commit?、build_time?、go_version、platform、uptime）/ `uptime`。
- 启动：未初始化时跳过 v0.4.0 播种；初始化后仍幂等运行。

## 测试

- `tests/AxonHub.Api.Tests/SystemInitializationServiceTests.cs`（9）：首次运行逐字段状态、
  显式 preferLanguage、已初始化 no-op、检查失败前缀、HashPassword 双层前缀、owner 创建
  失败、默认项目失败链、存储失败与系统行回滚、setSystemValue 内层前缀。
- `tests/AxonHub.Api.Tests/AdminSystemEndpointTests.cs`（19）：health 字段序 / omitempty /
  timestamp；status 三例；initialize 的 400 形状（3 + 6 个 Theory 例）、CJK rune 通过、
  已初始化、成功响应逐字节与落库、500 两条链。
- `tests/AxonHub.Persistence.Tests/DataStorageBootstrapTests.cs`（+6）：全新库未初始化、
  大小写不敏感（true / TRUE / false / 1）、软删除标志不计数。

静态计数：`AxonHub.Api.Tests` 5685 → 5713（+28），`AxonHub.Persistence.Tests` 353 → 359
（+6），`AxonHub.Grains.Tests` 1746、`AxonHub.Llm.Tests` 1506 不变，四项目合计 9290 →
9324。**按项目规则未运行 lint / build / test。**

## 台账

基线 §157、验收 §157 已写入；新增 C241（accepted：初始化事务与 /health 的跨实现差异，
含 `"{}"` settings、build 元数据 C235、JSON 边界 C201、email / rune 近似 C166、时间戳
来源与无迁移记账表 C020）与 C242（gap：GET /favicon 未接线）。`02-differences.md` 重新
生成（242 条：176 accepted / 55 resolved / 11 gaps），lint 通过。

## 运维注意

被旧端口版本启动时播种过 Primary、但缺少 `system_initialized` 的本地数据库，初始化会因
`data_sources_by_name` 唯一索引失败（参照不可能出现该状态，迁移被初始化闸挡住）。清理该
`data_storages` 行或补 `system_initialized` 后即可初始化；全新库不受影响。
