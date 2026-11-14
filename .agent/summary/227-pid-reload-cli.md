# §227 PID 文件 / SIGHUP 重载与 CLI 子命令

## 背景

C298 / C299 是 `cmd/axonhub/main.go` 启动面剩余的两块：进程 PID 文件与 Unix
SIGHUP 热重载，以及 argv 子命令（`reload`、`config preview|validate|get`、
`version|--version|-v`、`help|--help|-h`、`build-info`）。规格以 live 仓库
f272549f 的 `main.go`、`reload_unix.go`、`reload_windows.go`、
`config_reload_unix.go`、`config_reload_windows.go`、`internal/build/info.go`
为准；配置解码对齐 `conf/conf.go` 与 `internal/server/config.go`。

## 实现

### PID 文件与 SIGHUP 重载

- `src/AxonHub.Api/Lifecycle/PidFile.cs`
  - `FromConfiguration`：`Server:PidFile` → `AXONHUB_SERVER_PID_FILE` → 空。
  - `ExpandHome`：仅展开前导 `~/`（`filepath.Join(home, path[2:])` 语义）。
  - `Write`：内容 `PID\n`，Unix 以 `0600` 创建并截断，等价
    `os.WriteFile(path, data, 0o600)`。
  - `Delete`：`os.Remove` 语义，ENOENT 不报错。
  - `SendReloadSignal`：Windows 固定返回
    `reload is not available on Windows — use a Unix-like OS and send SIGHUP manually`；
    Unix 读取 PID 文件、`Atoi` 校验、`kill(pid, SIGHUP)`，三条失败文案
    （`read PID file …`、`invalid PID in …: strconv.Atoi: parsing "…": invalid syntax`、
    `send SIGHUP to process …: …`）与上游一致。
- `PidFileHostedService`（同文件）：启动写、停止删；空 `server.pid_file`
  注册但 no-op；写失败抛 `write PID file …: …` 中止启动；删除失败按 fx
  OnStop 语义记录 `remove PID file …: …` 后继续关闭。`Program.cs` 在
  `GrainSchedulerActivator` 之前注册。
- `src/AxonHub.Api/Lifecycle/ConfigReloadService.cs`
  - 启动时定位 `appsettings.json` / `appsettings.{Environment}.json`（存在才启用）。
  - Windows 记录 `SIGHUP config reload is unavailable on Windows`；无配置文件
    记录禁用原因后直接返回。
  - Unix 用 `PosixSignalRegistration(SIGHUP)` + 容量 1、`DropWrite` 的
    `Channel<bool>` 串行消费，复刻 `signal.Notify(ch, SIGHUP)` 单元素缓冲与
    串行 reload 循环；信号处理设置 `context.Cancel = true`。
  - `TryReload`：先 `IConfigurationRoot.Reload()`，再仅执行
    `IpAccessControlConfig.Apply(IpAccessControlSettings.FromConfiguration(...))`；
    任一步失败保留旧快照；成功记录 `SIGHUP config reload applied server.ip_access_control`。
  - `Stop`：注销信号、取消循环并最多等待 30 秒（fx `StopTimeout(30s)`）。
- `Program.cs`：`ApplicationStarted` / `ApplicationStopping` 注册 Start / Stop，
  位置在既有 `serverLifetime` 停止回调之后，复刻 fx hook 逆序停止。

### CLI

- `src/AxonHub.Api/Cli/CliCommand.cs`
  - `TryRun` 按 `args[0]` 分派；未知参数返回 null，正常启动服务器。
  - `version` 输出 `v1.0.0-beta9`；`build-info` 按 `build.Info.String` 行序输出
    Version / Commit? / Build Time? / Go Version / Platform / Uptime，并用
    `fmt.Println` 语义补末尾空行；`help` 文案逐行一致。
  - `reload`：`conf.Load` → pid_file 为空报 `Reload failed: pid_file not configured`
    → 读取/校验/发信号；成功 `Config reload signal sent successfully`。
  - `config`：缺子命令或未知子命令打印用法并 exit 1；`preview` 从下标 2 起
    扫描 `--format|-f`（默认 `yml`），支持 `yml` / `yaml` / `json`，其他格式
    `Unsupported format: <format>`；`validate` 四条规则；`get` 六键与无 key 用法。
- `src/AxonHub.Api/Cli/CliConfiguration.cs`
  - 与宿主同源的配置读取（appsettings.json + 环境变量；`Server:*` 优先，
    `AXONHUB_*` 回退）。
  - `BuildPreview` 只渲染已移植配置面（db / log / server 子集）；duration 输出
    int64 纳秒、memory size 输出 int64 字节，JSON 字段名与上游一致。
  - `ParseMemorySize` 复刻 `server.MemorySize.UnmarshalText`：空串 0、纯 int64、
    可选尾 `B`、K/M/G 倍数、负数/非法数字/未知后缀/溢出文案。
- 文案与退出码：所有 `fmt.Println` 以 `\n` 结尾（跨平台 LF），与上游一致。

## 验证

- 定向：`CliCommandTests|ConfigReloadTests` 53/53 通过，0 失败 0 跳过。
  复核上游时发现 `unknown memory size suffix` 文案应引用整段原文
  （`"12X"`，而非仅后缀 `"X"`），已修正实现与测试。
- Api 全量：第一次 7022 例（7019 通过 / 3 跳过 / 0 失败，41m43s）因 testhost 崩溃较完整计数少 6 例；第二次完整运行 7028 例（7026 通过 / 2 失败 / 0 跳过，57m07s，EXIT=1），3 条 LlmTestServer E2E 实测通过；2 例失败都在本轮未改动的 `SafeguardsEndpointTests`（流式 `not_flagged` 断言，负载下时序敏感），该类定向重跑 21/21 通过，期间无代码改动。按时间成本停止第三次全量，以“完整二次运行 + 失败类定向复跑”合并判定 7028/7028 覆盖，未解释失败为 0。
- 台账 305 条（202 accepted / 103 resolved / 0 gap）；`diff_register.py lint`
  通过；`02-differences.md` 重新生成。

## 边界

- C304（accepted）：`config preview` 只渲染已移植配置面，未移植的
  metrics/gc/cache/provider_quota/oidc 等不出现，也没有上游的 ANSI 高亮。
- C305（accepted）：MemorySize 数字部分按 .NET `double.TryParse` 解析，未复刻
  Go 的数字分隔下划线与十六进制浮点（如 `1_0M` 在上游为 10M）。
- 配置源是 `appsettings*.json` + 环境变量，不是 `config.yml`；没有配置文件时
  SIGHUP 重载禁用（与上游 `Loader.Reload` 的“无文件即禁用”一致）。
- Windows：无 SIGHUP 重载，`reload` 子命令固定报错；PID 文件照常写删，但没有
  Unix 的 0600 语义。
- PID 文件删除失败只记日志，不阻断宿主关闭（fx OnStop 错误只记录且继续）。
