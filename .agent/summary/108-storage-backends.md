# §108 数据存储后端与自动备份调度（fs / S3 / GCS / WebDAV、保留清理、每日 02:00）

日期：2026-11-06
参照：`b8bb3421:internal/server/biz/data_storage.go:83-197` / `:388-524` / `:531-584` /
`:589-647` / `:651-691` / `:696-733` / `:870-903`、
`internal/server/biz/objectstore.go:59-85`、`internal/server/biz/objectstore_s3.go:26-160`、
`internal/server/backup/autobackup.go:22-32` / `:88-153` / `:155-205`、
`internal/server/scheduler/task.go:22-31`

实时上游 `f272549f` 的 `data_storage.go` / `objectstore_s3.go` 相比冻结基线只增加了 S3 的
`RequestChecksumCalculation` / `ResponseChecksumValidation` 配置与 HTTPClient 测试注入接缝；
`autobackup.go`、`objectstore.go`、`scheduler/` 零差异。端口以冻结基线 `b8bb3421` 为准。

## 交付

- `src/AxonHub.Api/Storage/DataStorageBackend.cs`：`IDataStorageBackend`（Write / WriteFromStream /
  Read / Delete / List / Dispose）、`DataStorageEntry`、`IDataStorageBackendFactory`。
- `src/AxonHub.Api/Storage/DataStorageBackendFactory.cs`：`buildFileSystem` 的六种错误文本、
  s3 / gcs / webdav 客户端构造包装 `failed to create … filesystem: `。
- `src/AxonHub.Api/Storage/FsDataStorageBackend.cs`：BasePathFs 等价（固定根 + 单前导分隔符剥离 +
  越界 `invalid path: <key>`）、截断写入、删除缺失键成功、列表只返回直接子项。
- `src/AxonHub.Api/Storage/S3DataStorageBackend.cs`：AWSSDK.S3 4.0.20.4（Basic 凭据、
  ForcePathStyle、Region / ServiceURL、自适应重试 3 次）、单次 PutObject、缺失读取
  `file does not exist: <key>`、删除幂等、Delimiter `/` 分页与 basename。
- `src/AxonHub.Api/Storage/GcsDataStorageBackend.cs`：service_account（RS256 JWT-bearer）与
  authorized_user（refresh_token）令牌链、令牌缓存、media upload、JSON API 读取 / 删除 / 分页列表、
  404 → `storage: object doesn't exist`、401 重试一次。
- `src/AxonHub.Api/Storage/WebDavDataStorageBackend.cs`：Basic 认证、10 分钟超时、逐级 MKCOL
  （2xx / 405 / 409 / 301 / 302）、PUT、PROPFIND Depth 1 + multistatus 解析、删除 404 抛错。
- `src/AxonHub.Api/Storage/CountingStream.cs`：流式写入的字节计数。
- `src/AxonHub.Api/Management/AutoBackupSchedule.cs`：`0 2 * * *` 的下一次触发（Go `time.Date`
  的 DST 语义：缺口向前归一、歧义取首个偏移）。
- `src/AxonHub.Api/Management/AutoBackupScheduler.cs`：`BackgroundService` 轮询（≤1 分钟一次），
  重读系统时区与设置，02:00 触发 `TriggerAutoBackupAsync`，不补跑错过的运行。
- `src/AxonHub.Api/Management/BackupService.cs`：非 database 存储改为临时文件
  `axonhub-backup-*.json` + `WriteFromStreamAsync`；`CleanupOldBackupsAsync`；`CreateBackend`
  按 S3 / 其余类型分层包装错误；四条 `LoggerMessage` 日志。
- `src/AxonHub.Api/Program.cs`：`IDataStorageBackendFactory` 单例与 `AutoBackupScheduler`
  托管服务；`Directory.Packages.props` / `AxonHub.Api.csproj`：AWSSDK.S3。

## 行为要点

- 工厂错误：`database storage does not support file system operations`、
  `directory not configured for fs storage`、`s3 / gcs / webdav settings not configured`、
  `unsupported storage type: <type>`（裸文本）。
- 包装顺序：S3 先走 `objectStoreFor`，只出现 `failed to create s3 object store: `，永不出现
  `failed to get file system:`；fs / gcs / webdav 得到 `failed to get file system: <buildFileSystem 文本>`；
  database 流式写 → `database storage does not support streaming writes`；尾部 `, key: %s`。
- fs 键：`TrimStart(分隔符)` 后 `Path.Combine`（复刻 Go `filepath.Join`），组合后校验仍在根内。
- S3 键：只剥离一个前导 `/`。
- 自动备份文件名 `axonhub-backup-yyyy-MM-dd_HH-mm-ss.json`（本地时间）；临时文件错误前缀依次
  `failed to create temp backup file:` / `failed to create backup:` / `failed to seek backup file:` /
  `failed to write backup file:`；临时文件在 finally 删除。
- 保留清理：`axonhub-backup-` 前缀 + `.json` 后缀、按修改时间升序、早于 `UtcNow - RetentionDays`
  删除；构造 / 列表失败与单文件删除失败都只记日志；database 存储下同样执行并吞掉文件系统错误。
- GraphQL 的 `Reschedule` 不再需要显式调用：轮询在下一轮读取最新设置与时区。

## 测试

- `tests/AxonHub.Api.Tests/StorageBackendTests.cs`：12 例——fs 往返 / 父目录、流式字节数、列表、
  删除幂等、越界键；工厂六种错误文本、GCS 凭据包装、S3 键规范化。
- `tests/AxonHub.Api.Tests/StorageProtocolTests.cs`：10 例——WebDAV 认证 / 超时、MKCOL 链 + PUT、
  读取状态文案、PROPFIND 解析；GCS 两种凭据的令牌链、媒体上传、令牌缓存、404 映射、分页。
- `tests/AxonHub.Api.Tests/AutoBackupScheduleTests.cs`：5 例——当日 / 次日、上海墙钟、
  纽约春季缺口、柏林秋季歧义。
- `tests/AxonHub.Api.Tests/BackupServiceTests.cs`：新增 8 例——非 database 流式备份、
  文件系统构造 / 未知类型 / 流式写失败的包装、保留清理的过滤与失败容忍。
- `tests/AxonHub.Api.Tests/FakeDataStorageBackends.cs`：内存后端与工厂替身。
- 静态计数：`AxonHub.Api.Tests` 2737 → 2772（+35），Persistence 276、Llm 3032、Grains 95，
  合计 6140 → 6175。**本轮按项目规则未运行 lint / build / test**，下一次全量运行应以该数字复核。

## 差异

- C197（resolved）：四种存储后端、非 database 自动备份、保留清理与每日 02:00 调度全部接入；
  database 存储下的行为与 §107 一致。
- C199（accepted）：S3 单次 PutObject 取代 16 MiB 分片；GCS / WebDAV 为手写客户端而非
  gcsfs / gowebdav，库错误文本为语义近似；后端按操作创建并 Dispose（无跨请求缓存）；
  调度为轮询（02:00 最多晚 1 分钟）；S3 未采用实时上游新增的 checksum 关闭配置。

## 残余风险

- S3 无协议 / 线路形状测试：AWSSDK.S3 客户端不暴露可替换的 HttpMessageHandler 接缝，单次
  PutObject 的请求形态只能靠 SDK 契约保证；GCS / WebDAV 已由脚本化 `HttpMessageHandler` 锁定。
- 本轮未运行构建与测试，新增代码的编译 / 分析器正确性仅由人工检查保证。
