# §241 测试矩阵 CI

## 背景

phase 7 的守护工作流（§230）只覆盖台账 / 语料 / 参照物；完整测试矩阵仍未
接入 CI，`03-acceptance.md` 的未闭合风险把「CI 必须提供 Docker」列为待办。

## 实现

- 新增 `.github/workflows/tests.yml`：
  - PR / push 到 `unstable`：并行运行 `AxonHub.Llm.Tests`、
    `AxonHub.Grains.Tests`、`AxonHub.Persistence.Tests`；
  - ubuntu-latest 提供 Docker，Persistence 的 Testcontainers 可启动
    `postgres:17-alpine`；
  - `AxonHub.Api.Tests` 全量（约 42 分钟，含时序敏感的 safeguards 用例）
    仅在 nightly（18:00 UTC）与 `workflow_dispatch` 运行；
  - 路径过滤 `src/**`、`tests/**`、`Directory.Build.props`、
    `Directory.Packages.props`、`global.json` 与工作流自身。
- 风险表「持久化测试依赖 Docker」的处置更新为上述工作流。

## 验证

- 本地定向：Llm 32/32、Grains 34/34、Persistence 30/30、Api 109/109；
  完整 Api 最近一次为 §227 的 7028 例。
- 工作流尚未在 GitHub 上实际触发（未 push）；首次远端运行结果待记录。

## 台账

- 台账不变（308 条，193 accepted / 115 resolved / 0 gap）。
- `diff_register.py lint` 不受影响；`02-differences.md` 未变。

## 边界

- 工作流不运行 E2E 的 LlmTestServer 用例（无外部进程时 3 例跳过）。
- Api 全量为 nightly/手动，不阻塞每个 PR；PR 的快速矩阵由三个项目覆盖。
