# §230 CI 守护接线

## 背景

三本账与差分语料此前只有本地命令，仓库没有 CI 配置；`03-acceptance.md` 的
未闭合风险把 CI 守护列为 0%。本轮把 phase 7 的三项守护接入 GitHub Actions。

## 实现

- `porting/scripts/`：从 cross-lang-porting 技能目录复制
  `diff_register.py` / `diff_corpus.py` / `reference_lock.py`（仅标准库），
  加入 README 说明本地用法。
- `.github/workflows/porting-guard.yml`：
  - `ledger`：`diff_register.py lint --dir porting`；原地 `render` 后
    `git diff --exit-code -- porting/02-differences.md`，防止 JSONL 与渲染稿不同步。
  - `corpus`：`dotnet build tools/AxonHub.CorpusRunner -c Release`，再对
    `upstream-request` / `unified-request` / `client-response` / `client-stream`
    四族运行 `diff_corpus.py verify`。
  - `reference`：`git clone --depth 1 --branch v1.0.0-beta11
    https://github.com/looplj/axonhub` 后运行 `reference_lock.py check`，
    检查 commit / tree / 文件数 / dirty 漂移。
  - 触发：`pull_request`、`push` 到 `unstable`、`workflow_dispatch`；路径过滤
    `porting/**`、`corpus/**`、`tools/AxonHub.CorpusRunner/**`、
    `src/AxonHub.Llm/**`、`src/AxonHub.Abstractions/**` 与工作流自身。
- `porting/00-reference.md` 与 `porting/01-baseline.md` 的命令从技能目录路径改为
  仓库内 `porting/scripts/`。

## 验证

- vendored `diff_register.py lint`：305 条（200 accepted / 105 resolved / 0 gap），通过。
- `reference_lock.py check --repo E:\GitHub\axonhub-ref`：
  `OK 参照物未漂移（b8bb3421…）`，退出 0。
- `diff_corpus.py verify` + Release CorpusRunner：
  - `upstream-request` 7/7
  - `unified-request` 7/7
  - `client-response` 9/9
  - `client-stream` 12/12
- 原地 render 幂等：`git diff --exit-code -- porting/02-differences.md` 退出 0。

## 边界

- 本工作流只覆盖移植守护（台账 / 语料 / 参照物），不运行依赖
  Docker/Testcontainers 的完整测试矩阵；完整测试 CI 仍待 M3。
- 台账条目数不变（305），本轮无代码行为变更。
