# AxonHub.NET 移植收尾 TODO

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成当前行为等价移植的发布验收，明确补齐真实兼容缺口，并把可选扩展与发布阻塞项分开。

**Architecture:** 保持 ASP.NET Core + Orleans、EF Core 写侧 + Dapper 读侧和现有 provider transformer 架构。先完成现有范围的验收，再独立补齐配置、调度及可观测性；数据库扩展、Redis、交互式 OAuth 不默认纳入本次发布。

**Tech Stack:** .NET 10、C#、ASP.NET Core、Orleans、PostgreSQL 17、React、TypeScript、pnpm、Playwright、Docker、GitHub Actions。

## Global Constraints

- 本文是可执行的收尾检查清单，不是所有子系统的完整代码实现方案；涉及新能力的任务先确定参照行为和独立实现方案，再修改代码。
- 代码核查基点为 `3d4fbc11c9f7576de96aff115079e400bdf91901`；执行时记录实际 HEAD，不将该提交以后的修改默认为已验收。
- 上游冻结参照为 `b8bb3421e1fe946cfc4f0d86e8412098738f2793`，部分切片已按台账跟进 `f272549f`。不得未经决策升级锁定版本或混用两版语义。
- 以可观察行为等价为准。`accepted` 不等于未实现，`resolved` 不等于已在最新提交全量复验，`0 gap` 不代表与上游所有功能完全相同。
- 构建要求 0 警告、0 错误；记录实际执行测试数、通过、失败和跳过原因，禁止把过滤后 0 项执行视为通过。
- Windows/Linux 的平台差异必须有明确断言；不得通过任意延时、增加重试次数、删除关键断言来消除测试失败。
- 生产重试契约保持不变：未收到正文时按预算回退；收到正文后不得拼接其他渠道，不得补成功终止标记。
- 新增或修复行为采用失败测试先行；复用现有测试和接缝，不为收尾引入新的测试框架。
- 配置默认值、环境变量优先级、错误响应、并发与事务语义不能因补齐能力而静默改变。
- 仅使用隔离测试数据和测试凭据；备份恢复、密钥撤销、数据库重启和故障注入不得操作共享或生产环境。
- 初始清单创建时未执行任务；后续按用户授权推进并逐项记录证据，不自动提交、推送或部署。

### 执行记录：2026-11-18

- 用户授权在当前 checkout 切换新分支，执行分支为 `chore/porting-closeout`。
- 本轮代码基点为 `3d4fbc11c9f7576de96aff115079e400bdf91901`；文档修改尚未提交，不将远端旧 SHA 的验收归给后续代码变更。
- 参照锁检查通过，仍为 `b8bb3421e1fe946cfc4f0d86e8412098738f2793`；局部跟进范围继续按参照记录维护，不升级基线。
- 重新统计差异源账为 338 条：192 accepted、146 resolved、0 gap。已核实 C029/C182/C243 的 resolved 状态，以及 C176/C177/C179 的后续闭合决策。
- Safeguards 运行时与 YAML 默认值接线是两件事：前者已实现，后者仍未接入；文档保留真实配置缺口。
- CLOSE-03 本地及远端均通过：[porting-guard 44201899794](https://github.com/geffzhang/axonhub.net/actions/runs/44201899794) 的四个 job 成功；本地路由 76/76，四类语料 7/7、7/7、9/9、12/12。
- CLOSE-02 的 [tests 44201894365](https://github.com/geffzhang/axonhub.net/actions/runs/44201894365) 已结束，目标 SHA 与代码基点一致；Llm 3438/3438、Grains 191/191、Persistence 447/447、独立 E2E 44/44。API 全量为 7265 项（7221 通过、1 失败、43 个 opt-in 跳过），工作流失败。本地 Release 解决方案构建为 0 警告、0 错误。
- CLOSE-04 基线阻塞：前端尚未修改，单测为 376 项（372 通过、3 失败、1 跳过）；lint 为 159 errors / 93 warnings；E2E 入口引用不存在的 shell 脚本。先确认前端修复范围，再继续实现，不将上述基线失败归因于本轮文档改动。
- 用户随后批准先修 E2E 入口与三项单测，lint 存量单独列项。该限定范围已修复并验证：前端单测 393 通过、0 失败、1 个 TZ 条件跳过；共享 AI SDK 语料由后端 Release 定向 21/21 锁定；生产构建成功（存在既有 chunk 提示）；修改的三个前端测试文件定向 ESLint 通过。完整 lint 门禁仍未通过。
- E2E 普通/headed 入口各发现 114 项、21 文件，setup 为 1 项，UI/debug CLI 正常；尚未宣称真实浏览器业务通过。限定修复证据见验收账 §282，当前改动仍未推送。
- 补充设置 `TZ=America/New_York` 执行 date-range 测试 9/9 通过、0 跳过，覆盖默认环境下跳过的 DST 回拨用例；不重复加总。最终 Release 解决方案构建再次为 0 警告、0 错误。
- 后端预算用例已完成本地修复（验收账 §283）：Go 参照检查 deadline 相等，端口原墙钟断言无法区分逻辑预算与调度延迟。改用可控时钟严格验证 39ms/40ms 边界及主模型已耗时 20ms 后的剩余预算，不放宽阈值；生产默认系统计时、父子取消和超时归因不变。
- 最终 Safeguards 在 Windows/Linux 单 CPU 各 102/102 通过；三个确定性预算用例额外重复 10 轮、每轮 3/3。断开回退父预算的突变使两个边界用例都失败，已恢复并复验。Release 构建 0 警告、0 错误。
- 用户随后授权提交、推送并执行完整 CI。提交 `8bcc8e8cfd4577d77b5ab1a37fc35a734bc82974` 已推送到 `chore/porting-closeout`；远端 tests [44210353098](https://github.com/geffzhang/axonhub.net/actions/runs/44210353098) 与 porting-guard [44210357805](https://github.com/geffzhang/axonhub.net/actions/runs/44210357805) 均针对该 SHA 成功。后端五个 job 全通过；API 7267 项（7224 通过、0 失败、43 个 opt-in 跳过），独立 E2E 44/44、0 跳过。CLOSE-02、CLOSE-03 的远端门禁完成。

---

## 一、范围与当前证据

### 优先级

| 类别 | 含义 | 是否阻塞当前范围发布 |
| --- | --- | --- |
| P0 / CLOSE | 已有能力的验收、文档纠偏和测试入口收尾 | 是 |
| P1 / PORT | 已知兼容缺口，建议逐项补齐 | 只有被选入本轮发布范围时才阻塞 |
| P2 / OPTIONAL | 按部署需求选择的扩展移植 | 否，不默认承诺实现 |

### 已有证据，不重复当作待开发功能

- OpenAI / Responses / Anthropic / Gemini 主流程、主要管理面、渠道选路和配额采集已有实现。
- Safeguards reviewer 运行时已实现，C243 已关闭；现有文档的“运行时未移植”说明需要纠正。
- 系统设置进程内缓存已实现，C029 已关闭；缺少 Redis 后端不等于完全没有缓存。
- 配额核心和全部 19 个 provider checker 已落地，C182 已关闭；后续主要是配置兼容，不是重做采集器。
- 本轮 Windows Release 请求内容、进程清理及真实多实例定向测试 46/46 通过；Linux 内容端点和进程清理 45/45 通过。
- Windows/Linux 的完整 LlmTestServer 矩阵加端点测试各 57/57 通过；Linux abort 额外重复 10/10，pipeline 边界契约 2/2 通过。
- 上述测试集有交集，不能相加为总测试数；本地通过不能替代最新提交的完整远端验收。

## 二、执行依赖

```text
CLOSE-01 基线与文档纠偏
  ├─ CLOSE-02 完整后端 CI
  ├─ CLOSE-03 移植守护
  ├─ CLOSE-04 前端测试入口 → CLOSE-05 浏览器业务验收
  └─ CLOSE-06 部署与多实例验收
       ↓
CLOSE-07 验收账与发布结论（依赖 CLOSE-01 至 CLOSE-06）

PORT-01 配置差距清单 → PORT-02 配额配置兼容
                   └→ PORT-05 配置预览一致性
PORT-03 Cron 特殊语法、PORT-04 评审 operation name 可独立推进

每项被批准的 PORT / OPTIONAL 完成后，重新打开 CLOSE-07，
只复验受影响的门禁，必要时补全量回归。
```

## 三、P0：发布前必须完成

### CLOSE-01：冻结验收基点，纠正文档与台账矛盾

**依赖：** 无。

**涉及：** [README](../../../README.md)、[配置示例](../../../config.example.yml)、[移植说明](../../zh/development/porting.md)、[差异源账](../../../porting/differences.jsonl)、[参照记录](../../../porting/00-reference.md)。

- [x] 记录本次验收的分支、完整提交 SHA、上游锁定 SHA，以及局部跟进 live 版本的范围。
- [x] 执行参照物检查；发生漂移时先决定继续旧基线还是单独升级，不直接重写锁文件。
- [x] 以差异台账和当前接线代码核实 C243、C029、C182，修正文档中“未移植”“没有缓存”等过时表述。
- [x] 对齐 README、开发文档和差异台账的条目数；当前记录为 338 条、192 accepted、146 resolved、0 gap，更新时重新统计。
- [x] 不将 C176/C177/C179 的历史缺口描述误列为新任务；这些条目的后续复核已关闭原有主要缺口。
- [x] 将构建/发布成功与测试成功分开描述，删除无法对应到具体运行的“全部 CI 全绿”结论。

**验收：** 每条已知限制都能指向仍有效的差异或代码证据；文档没有将已实现能力标为未实现。

### CLOSE-02：对最新代码完成后端远端验收

**依赖：** CLOSE-01。

**涉及：** [测试工作流](../../../.github/workflows/tests.yml)、[解决方案](../../../AxonHub.slnx)、[验收账](../../../porting/03-acceptance.md)。

- [x] 记录最新目标 SHA，确认待运行代码包含目录越界、进程清理和 abort 测试修复。
- [x] 手动触发 `tests.yml`。当前 `main` 普通 push 不会自动触发该工作流，不能只等待镜像发布。
- [x] 核实运行的 `headSha`，记录 run ID 和结果；若执行期间分支前进，不把旧提交结果归给新提交。
- [x] 核实 Llm、Grains、Persistence、API 全量 nightly job、LlmTestServer E2E 五个 job 都实际执行且通过。
- [x] 确认 E2E job 的外部服务环境变量已设置，真实用例没有因 opt-in 条件被跳过。
- [x] API 全量中的 opt-in 跳过与独立 E2E job 的执行结果分别记录；其余跳过逐项说明。
- [x] 核实同一代码版本的 Release 解决方案构建为 0 警告、0 错误；失败时保留日志并修根因。

```powershell
gh workflow run tests.yml --ref main
gh run list --workflow tests.yml --branch main --limit 5 --json databaseId,headSha,status,conclusion,url
dotnet build AxonHub.slnx -c Release
```

从列表定位本次 dispatch，再通过对应运行页面核对各 job，不使用历史成功运行替代。

**验收：** 最新验收基点对应的五个测试 job 全通过，执行数量非零，跳过有解释，构建没有警告或错误。

**完成证据（2026-11-18）：** 提交 `8bcc8e8cfd4577d77b5ab1a37fc35a734bc82974` 已推送到 `chore/porting-closeout`。同 SHA 的 [tests run 44210353098](https://github.com/geffzhang/axonhub.net/actions/runs/44210353098) 五个 job 全成功：Llm 3438/3438、Grains 191/191、Persistence 447/447、API 7267 项（7224 通过、0 失败、43 个 opt-in 跳过）、独立 LlmTestServer E2E 44/44。43 个 API 跳过是未配置外部服务 URL 的条件测试；独立 E2E job 已配置服务并实际执行，0 跳过。同 SHA 的 [porting-guard run 44210357805](https://github.com/geffzhang/axonhub.net/actions/runs/44210357805) 四个 job 全成功。API 总项数与先前运行不同，照录本次 runner 输出，不推断额外原因。

### CLOSE-03：完成行为等价守护验收

**依赖：** CLOSE-01；可与 CLOSE-02 并行。

**涉及：** [守护工作流](../../../.github/workflows/porting-guard.yml)、[基线账](../../../porting/01-baseline.md)、[语料目录](../../../corpus)、[语料运行器](../../../tools/AxonHub.CorpusRunner)。

- [x] 运行差异源账 lint，确保每条记录包含现象、原因、决策、对应测试和有效状态。
- [x] 重新生成差异 Markdown，确认生成结果与提交内容一致；禁止手工改生成表掩盖源账问题。
- [x] 运行 HTTP 路由审计，对每个缺少、额外或方法不同的路由给出差异编号。
- [x] 回放 upstream-request、unified-request、client-response、client-stream 四类差分语料。
- [x] 运行参照锁检查，确认匹配冻结 worktree。
- [x] 手动运行远端 `porting-guard.yml`，记录 ledger/routes/corpus/reference 四个 job 的结果和目标 SHA。

```powershell
python -I porting\scripts\diff_register.py lint --dir porting
python -I porting\scripts\diff_register.py render --dir porting
git diff --exit-code -- porting\02-differences.md
python -I porting\scripts\http_route_audit.py --port src\AxonHub.Api
python -I porting\scripts\reference_lock.py check --repo E:\GitHub\axonhub-ref --dest porting\reference-lock.json
dotnet build tools\AxonHub.CorpusRunner\AxonHub.CorpusRunner.csproj -c Release
foreach ($mode in @('upstream-request', 'unified-request', 'client-response', 'client-stream')) {
    python -I porting\scripts\diff_corpus.py verify --cmd "dotnet tools\AxonHub.CorpusRunner\bin\Release\net10.0\AxonHub.CorpusRunner.dll --mode $mode" --corpus "corpus\$mode"
    if ($LASTEXITCODE -ne 0) { throw "Corpus verification failed: $mode" }
}
gh workflow run porting-guard.yml --ref main
```

**验收：** 四类语料全部通过；生成账没有未提交差异；路由与参照锁检查无未解释失败。

### CLOSE-04：修通前端测试入口并验证构建

**依赖：** CLOSE-01。

**涉及：** [前端包脚本](../../../frontend/package.json)、[Playwright 配置](../../../frontend/playwright.config.ts)、[前端测试](../../../frontend/tests)、[测试说明](../../zh/development/testing.md)。

- [x] 修复 `test:e2e*` 指向仓库中不存在的 `scripts/e2e/e2e-test.sh` 的入口问题；按现有 Playwright 工具链完成，不引入第二套 E2E runner。
- [x] 明确基础 E2E、setup、headed、UI、debug 各入口的职责和启动方式，Windows/Linux 均能使用。
- [x] 核对真实 .NET API、Vite 代理和 Playwright 地址的配合；当前配置使用 API 8099、页面 9527，不能误连共享实例。
- [x] 在隔离环境运行 `pnpm test:unit`、`pnpm build`、`pnpm lint`，记录退出状态和测试执行数。
- [x] 用 `pnpm exec playwright test --list` 确认实际发现 setup 和业务用例；缺少依赖或浏览器时再按失败信息安装所需项。
- [x] 文档写明测试账号与数据生命周期，不复制生产账号、令牌或备份。

**验收：** 文档中的前端测试命令有实际入口，生产构建和 lint 通过，Playwright 能发现并启动预期测试。

**当前结论：** 入口、单测、构建和 discovery 子项已验证；lint 执行结果为失败，不是该门禁通过。用户已将以下存量工作独立排期，CLOSE-04 整体仍未关闭；浏览器真实业务由 CLOSE-05 验收。

#### LINT-01：前端 lint 存量问题（用户明确独立列项）

- [ ] 按现有规则整理全量 lint 基线的 159 errors / 93 warnings，区分业务代码、导入和 React Hooks 问题。
- [ ] 在独立批准的修复批次处理；保留业务行为，不通过关闭规则、全局忽略或 broad auto-fix 隐藏问题。
- [ ] 重跑完整 `pnpm lint` 并记录剩余数；涉及行为变化的文件补对应测试。
- [ ] 在全量 lint 通过后关闭 CLOSE-04 对应门禁，再纳入 CLOSE-07 发布结论。

### CLOSE-05：对真实 .NET 后端验收浏览器业务闭环

**依赖：** CLOSE-04。

**涉及：** [前端业务测试](../../../frontend/tests)、[GraphQL 客户端](../../../frontend/src/gql/graphql.ts)、[备份恢复调用](../../../frontend/src/features/system/data/system.ts)。

- [ ] 初始化系统 → 管理员登录 → 页面刷新后会话保持 → 退出登录；验证无效/过期认证的提示与跳转。
- [ ] 创建项目和 API key → 添加渠道与模型 → 发起测试请求 → 查看请求和 usage；UI 与实际后端数据一致。
- [ ] 验证请求详情及其 channel 字段、追踪详情和成本展示，排除仅 schema 能查询而页面仍报错的情况。
- [ ] 验证用户、角色、项目权限和跨项目不可见性；不只使用 owner 身份跑通页面。
- [ ] 在独立测试数据库验证备份导出及恢复，确认 multipart boundary、认证和 GraphQL 媒体类型协商正确。
- [ ] 验证 GraphQL 294 执行错误、422 请求错误与 legacy JSON 的 UI 错误路径，不出现“非 JSON 响应”误报。
- [ ] 复用已有 channels/models/api-keys/projects/users/roles/system/data-storages/usage-logs 测试；只给缺少的行为增加测试。
- [ ] 保存失败时的 trace/截图及复现步骤；不得依靠重复点击或 Playwright 自动重试掩盖确定性故障。

**验收：** Chromium 上上述关键闭环全部通过；业务请求指向真实 .NET API，关键权限与错误路径有证据。

### CLOSE-06：完成 Linux 部署、反向代理和多实例验收

**依赖：** CLOSE-01；最终镜像验收还依赖 CLOSE-02 和 CLOSE-04。

**涉及：** [Compose](../../../docker-compose.yml)、[部署说明](../../../deploy/README.md)、[Nginx 配置](../../../deploy/nginx.conf)、[Helm](../../../deploy/helm)、[多实例测试](../../../tests/AxonHub.Api.Tests/MultiReplicaBusinessTests.cs)。

- [x] 使用独立 Compose project 和数据卷，从空数据库启动当前验收 checkout `a86de18` 对应的镜像。
- [x] 验证健康检查、首页、初始化、管理员登录，以及 PostgreSQL 和 API 容器重启后的初始化状态持久性。
- [ ] 验证非流式请求和 SSE 端到端行为。流式请求经代理到达 API，但因未配置 provider/API key 返回 HTTP 401；此项不能据此判定通过。
- [ ] 在独立测试部署验证升级与回滚流程，确认配置文件和业务数据不被安装/升级脚本覆盖。
- [x] 验证 Nginx 配置语法、HTTP 到 HTTPS 重定向、HTTP/HTTPS 健康检查、SPA 首页以及 HSTS/X-Frame-Options 响应头。
- [ ] 通过 Nginx 验证 SSE 实时到达、不被响应缓冲；验证 Responses WebSocket 握手、消息和关闭。
- [ ] 在 Linux 上验证双 API 进程的共享数据库、API key 撤销/轮换、OIDC 单次消费、全局并发/FIFO 与故障恢复。
- [ ] 将测试进程启动失败的原始诊断保留下来；清理未启动/已退出进程不得再次抛异常掩盖根因。
- [x] 对 Helm 执行 lint/template 检查，默认值和双副本模板渲染通过。
- [ ] 在独立 namespace 验证 membership、durable reminder 和跨副本访问。当前 kubectl 没有选中的 context；已有 kind 容器未触碰。
- [ ] 保留已登记的故障许可占位行为；不要为让测试通过而擅自释放可能仍被旧请求占用的许可。
- [x] 仅清理本轮创建的容器、网络、数据卷和临时文件；不删除共享数据卷，不修改宿主现有系统服务。

**本轮证据：** 隔离 Compose project `axonhub-closeout-a86de18` 使用镜像 `axonhub.net:closeout-a86de18`、本地端口 `18090` 和独立 PostgreSQL volume。Nginx 使用隔离本地端口 `18080/18443`；HTTP/HTTPS `/health` 均返回 200，HTTP 首页返回 301，HTTPS 首页返回 200，签入路由的无效 JSON 返回 400。PostgreSQL 与 API 容器重启后 `/admin/system/status` 仍报告已初始化。Helm 真实部署、多副本、升级/回滚和 provider-backed SSE/WebSocket 未验收。

**阻塞与结论：** 本机没有当前 Kubernetes context（`kubectl` 默认访问 `localhost:8080` 失败）；未使用身份不明的现有 kind 容器。provider/API key 未配置，因此没有将 401 的流式请求记为 SSE 通过。CLOSE-06 尚未完成，不能据本轮单实例与代理检查宣称部署/多实例验收通过。

### CLOSE-07：统一验收账并给出发布结论

**依赖：** CLOSE-01 至 CLOSE-06，以及本轮明确选入范围的 PORT/OPTIONAL 项。

**涉及：** [验收账](../../../porting/03-acceptance.md)、[基线账](../../../porting/01-baseline.md)、[README](../../../README.md)、[测试说明](../../zh/development/testing.md)。

- [ ] 为每个项目记录目标 SHA、平台、命令、实际执行/通过/失败/跳过数、运行链接。
- [ ] 将定向测试、API 全量、独立 E2E、浏览器测试和差分语料分别统计，禁止重复加总。
- [ ] 核对 HTTP 路由与 GraphQL 根字段审计结果；有意不暴露的表面关联既有差异，不伪装成全量一一相同。
- [ ] 每项剩余限制明确列为“已接受差异”“下一轮补齐”或“本轮阻塞”，并关联编号。
- [ ] 更新 README 与开发文档，移除过时的测试数字、状态和“进行中”说明。
- [ ] 给出可复核的发布结论：本轮范围通过/不通过、阻塞项列表、镜像标识、回滚方法。

**验收：** 读者无需历史聊天即可从仓库和 CI 链接确认本轮范围、通过证据及未覆盖边界。

## 四、P1：建议补齐的兼容性移植

这些项目不是本轮测试修复的延续。选入范围后分别实施、分别验收，不将其捆绑为一次大改动。

### PORT-01：建立逐项配置兼容矩阵

**依赖：** CLOSE-01。

**涉及：** [YAML 配置](../../../src/AxonHub.Api/Configuration/YamlConfiguration.cs)、[配置测试](../../../tests/AxonHub.Api.Tests/ConfigurationSurfaceTests.cs)、[YAML 测试](../../../tests/AxonHub.Api.Tests/YamlConfigurationTests.cs)、[配置示例](../../../config.example.yml)；关联 C027。

- [ ] 对锁定上游配置键逐项记录 .NET 对应键、默认值、读取位置、环境变量名称、是否热更新和对应测试。
- [ ] 区分“YAML 能解析”为配置字典与“运行时真的读取并生效”，不能以前者替代后者。
- [ ] 核查 `provider_quota.*`、`cache.*`、`log.*`、TLS 相关键及 safeguards 配置说明，保留真实缺口，移除已过时限制。
- [ ] 明确未支持键的处理契约和迁移说明；若改变现有告警/拒绝行为，先批准该行为变化。
- [ ] 验证 YAML、appsettings、环境变量和命令行的既有优先级，以及空值、非法 duration、重载的行为。
- [ ] 配置矩阵中每一项给出“实现、兼容映射、显式不支持”结论，不把不安全选项默认打开。

**验收：** 不存在“示例声称支持但运行时不读取”的键；后续配置任务有明确、冻结的输入清单。

### PORT-02：补齐 provider_quota 配置到运行时调度的链路

**依赖：** PORT-01。

**涉及：** [配额协调接缝](../../../src/AxonHub.Api/Management/GrainProviderQuota.cs)、[配额服务](../../../src/AxonHub.Api/Orchestration/ProviderQuotaService.cs)、[调度 grain](../../../src/AxonHub.Grains/ProviderQuotaCoordinatorGrain.cs)、[配置测试](../../../tests/AxonHub.Api.Tests/ConfigurationSurfaceTests.cs)。

- [ ] 从锁定参照确定实际支持的 `provider_quota` 字段、默认值、边界及注册时机，不自行扩展配置协议。
- [ ] 针对“配置非默认检查间隔仍按固定五分钟运行”先增加失败测试，再接通配置读取与 schedule source。
- [ ] 默认未配置时保留现有五分钟行为；验证非法值、最小边界和禁用语义（若参照提供）。
- [ ] 保持定时、手动检查和 reset 后刷新之间的互斥；不丢失下一次调度，不重复追补错过的触发。
- [ ] 明确配置修改是仅启动生效还是动态生效，按参照契约测试，不能承诺未实现的热更新。
- [ ] 回归既有 19 个 checker 注册、手动检查和 coordinator 行为，更新配置示例及差异说明。

**验收：** 配置值会改变真实调度结果，而不只是改变配置预览；默认行为和并发契约不退化。

### PORT-03：补齐 Cron 的 L / W / # 特殊日期语法

**依赖：** CLOSE-01；与 PORT-02 独立。

**涉及：** [Cron 解析器](../../../src/AxonHub.Api/Management/GcCronSchedule.cs)、[Cron 测试](../../../tests/AxonHub.Api.Tests/GcCronScheduleTests.cs)；关联 C309/C260。

- [ ] 运行锁定参照，收集其实际支持的特殊语法、合法位置、组合限制与下一次触发结果。
- [ ] 语料覆盖月末、闰年、最近工作日、当月第 n 个星期几、无匹配日期，以及月份/年份边界。
- [ ] 对每种新增语法先增加失败测试；实现时保留既有 5/6/7 字段、描述符和日期字段组合语义。
- [ ] 验证无效表达式的拒绝方式及无下一次触发的行为，避免无限搜索或返回伪造日期。
- [ ] 回归 GC、配额调度、until_cron 的既有调用路径，并纠正解析器中已过时的“五字段限定”注释。
- [ ] 完成后更新 C309 和对应基线/验收账，不改变 C260 已关闭的标准语法结论。

**验收：** 新增语法与参照语料逐项一致，普通表达式与时区行为全部保持兼容。

### PORT-04：补齐 safeguards classifier 的 operation-name 标签

**依赖：** CLOSE-01。

**涉及：** [Safeguards classifier](../../../src/AxonHub.Api/Safeguards/SafeguardsClassifier.cs)、[请求入口](../../../src/AxonHub.Api/Endpoints/ChatCompletionEndpoint.cs)；关联 C307。

- [ ] 确定参照 operation name 的实际取值、传播范围及与父请求的关联方式。
- [ ] 增加可观测性断言，验证 classifier 子调用带标签，且标签不泄漏到普通业务调用。
- [ ] 在现有 trace/log 上下文中传递标签，不为该字段建立新的追踪系统。
- [ ] 回归 request ID、计费归属、超时/回退、verdict 和响应字节，确保评审逻辑无变化。
- [ ] 更新 C307 及观测文档，避免把此任务描述为“实现 Safeguards 运行时”。

**验收：** 可以从父请求定位评审子调用；业务和协议结果与补标签前一致。

### PORT-05：让配置预览与真实支持面保持一致

**依赖：** PORT-01；若本轮实施 PORT-02，还依赖其完成。

**涉及：** [配置面测试](../../../tests/AxonHub.Api.Tests/ConfigurationSurfaceTests.cs)、[配置示例](../../../config.example.yml)及现有 config preview 入口；关联 C304。

- [ ] 依据配置矩阵核对预览字段、默认值、顺序、duration 和 memory size 的表示。
- [ ] 补齐本轮新增且真正生效的配置项；不把尚未实现的 Redis、数据库后端显示为已支持。
- [ ] 对可能含连接串、令牌或凭据的字段验证现有脱敏约定，不把敏感值写入日志或验收附件。
- [ ] 比对参照预览的可观察差异；ANSI 高亮保留为可接受差异，除非明确要求终端显示等价。
- [ ] 更新快照/行为测试与 C304，保证配置示例、预览和运行时三者一致。

**验收：** 用户能从预览判断真正生效的配置，不再存在“显示已配置、实际被忽略”的误导。

## 五、P2：按需立项，不默认计入当前发布

每项均先确认目标使用场景和是否取消已有 accepted 裁剪；未批准时保持不支持声明，不影响 P0 收尾。

### OPTIONAL-01：GCP web/installed 交互式 OAuth

**涉及：** [GCP 凭据](../../../src/AxonHub.Llm/Vertex/GcpCredentials.cs)、[令牌源](../../../src/AxonHub.Llm/Vertex/GcpTokenSource.cs)、[令牌测试](../../../tests/AxonHub.Llm.Tests/GcpTokenSourceTests.cs)；关联 C128。

- [ ] 确认需要 web、installed 还是二者；明确服务器无浏览器环境下由谁完成授权。
- [ ] 为授权码、state、适用的 PKCE、回调、取消和超时定义完整流程，并用隔离模拟授权端点测试。
- [ ] 验证 refresh token 保存、刷新及失效恢复，不在控制台/日志输出真实凭据。
- [ ] 回归已支持的 service account、外部账户和 impersonation 路径。

**验收：** 选择的客户端类型能完整授权、刷新与撤销；不再在首次取令牌时落入未支持异常。

### OPTIONAL-02：上游 log 配置与文件轮转兼容

**涉及：** [宿主](../../../src/AxonHub.Api/Program.cs)、[配置映射](../../../src/AxonHub.Api/Configuration/YamlConfiguration.cs)、[部署说明](../../../deploy/README.md)。

- [ ] 明确需要兼容的日志级别、格式、路径、文件大小/保留策略，区分应用日志与部署脚本 stdout 重定向。
- [ ] 优先映射到现有 ASP.NET Core 日志；新增 sink/依赖前确认维护成本与必要性。
- [ ] 测试并发写、轮转边界、目录无权限、磁盘写失败和重启，不得无声丢弃错误。
- [ ] 验证容器 stdout 工作方式不被改变，并更新配置支持矩阵。

**验收：** 选定的上游日志配置确实生效，轮转和失败路径有可复核测试。

### OPTIONAL-03：SQLite 后端

**涉及：** [持久化层](../../../src/AxonHub.Persistence)、[持久化测试](../../../tests/AxonHub.Persistence.Tests)、[配置示例](../../../config.example.yml)。

- [ ] 明确仅单实例轻量部署；SQLite 支持不自动意味着 Orleans AdoNet 多实例方案也支持 SQLite。
- [ ] 逐项盘点 PostgreSQL 专有 DDL、JSON、时间、大小写、upsert、RETURNING、约束和事务行为。
- [ ] 在批准的独立方案中拆分方言与连接，复用同一组仓储契约测试，不机械替换 SQL 字符串。
- [ ] 验证初始化、分页、软删唯一性、备份恢复及并发写；文档明确与 PostgreSQL 的能力边界。

**验收：** 选定范围的仓储/业务测试在 SQLite 通过，单实例运行路径和限制均明确。

### OPTIONAL-04：MySQL 后端

**依赖：** 不依赖 SQLite 完成，但复用已批准的存储方言设计。

**涉及：** [持久化层](../../../src/AxonHub.Persistence)、[持久化测试](../../../tests/AxonHub.Persistence.Tests)。

- [ ] 确定支持的 MySQL 版本和大小写/排序规则、JSON、时间精度、identity/自增、upsert 语义。
- [ ] 实现数据库初始化与读写方言，保持 EF/Dapper 共用事务和约束错误分类。
- [ ] 在真实 MySQL 测试容器中执行同一组仓储契约、管理 API 和备份恢复测试。
- [ ] 独立验证或明确排除多实例 membership/reminder，不以业务表可读写推断集群已兼容。

**验收：** 明确范围内的迁移、事务、分页和 API 行为与参照一致，跨库差异有编号。

### OPTIONAL-05：读副本

**依赖：** 先选定数据库部署与一致性要求；不要求 SQLite/MySQL 已实现。

**涉及：** [持久化层](../../../src/AxonHub.Persistence)、[鉴权层](../../../src/AxonHub.Api/Auth)、[多实例验收](../../../tests/AxonHub.Api.Tests/MultiReplicaBusinessTests.cs)。

- [ ] 定义允许最终一致的查询；密钥撤销、权限、OIDC 单次消费、并发准入等关键路径不得被副本延迟放宽。
- [ ] 明确读后写的一致性、事务内读取和副本不可用时的错误/回退策略。
- [ ] 测试复制延迟、断连、主库故障及恢复，验证不会读取过期权限或重复消费状态。
- [ ] 更新部署、监控和故障处置文档，并区分普通 Dapper 查询与安全敏感查询。

**验收：** 读写分离确实生效，且所有关键一致性和安全业务契约保持成立。

### OPTIONAL-06：Redis 缓存后端

**涉及：** [Grains](../../../src/AxonHub.Grains)、[API 编排层](../../../src/AxonHub.Api/Orchestration)、[配置示例](../../../config.example.yml)。

- [ ] 先确定 Orleans/现有缓存不能满足的具体需求；如果只是要求多实例，不因此默认新增 Redis。
- [ ] 明确哪些缓存迁移，逐项定义 key、TTL、失效、序列化和多实例一致性。
- [ ] 不为同一业务状态建立两个权威写入口；缓存失效不得让已撤销密钥或旧权限继续生效。
- [ ] 验证 Redis 中断、超时、恢复、过期与跨实例更新；错误处理遵循既有显式诊断约定。

**验收：** 新增后端满足明确场景，并保持故障、安全和一致性契约；否则保留现有架构裁剪。

## 六、不应默认加入本轮范围

- 不把 Ent → EF/Dapper、Go 调度 → Orleans、gqlgen → HotChocolate 的实现替换当作待移植功能。
- 不重复开发已关闭的 Safeguards、系统设置缓存、模型关联路由、配额 checker、API key 旋转和公开模型列表能力。
- HTTP/1.1 WebSocket 的 ALPN/传输限制先按现有差异说明维护；没有实际兼容需求时，不新增传输栈重写任务。
- 不把 NativeAOT 当作当前行为等价移植的发布门槛。
- 上游版本升级单独立项：先列新旧提交差异、重新评审范围，再更新参照锁；不夹带进收尾修复。
- 本清单中的 P2 不用于虚增“未完成工作量”，也不直接换算成模块完成百分比。

## 七、最终交付检查

- [ ] CLOSE-01 至 CLOSE-07 全部有验收结论和证据链接。
- [ ] 本轮选入的 P1/P2 有独立完成记录，未选入项仍明确保留为可选。
- [ ] 所有新增可观察差异进入差异源账，生成账、基线账和验收账一致。
- [ ] 最新代码的测试、镜像、部署证据对应同一验收版本。
- [ ] 工作区无遗留临时服务或测试资源；提交范围只包含本轮批准工作。
- [ ] 经过明确的用户授权后再提交、推送或发布；不因勾选清单而自动操作远端。
