# 网关韧性 E2E 套件设计

## 目标

以现有 18 条 `LlmTestServer` E2E 用例为基础，扩展出约 40–60 条确定性的网关韧性用例。所有 provider 行为由本地 LlmTestServer 注入，不访问真实模型服务、不使用真实 provider key。

验收重点是可复核的“故障—机制—结果”：客户端最终结果、每个上游通道的实际请求与状态、AxonHub 的最终渠道和 usage/cost，以及重试和流终止边界。

## 当前基础与选定方案

当前基础由 [LlmTestServerE2ETests.cs](../../../tests/AxonHub.Api.Tests/LlmTestServerE2ETests.cs) 的 3 条用例和 [LlmTestServerProviderMatrixTests.cs](../../../tests/AxonHub.Api.Tests/LlmTestServerProviderMatrixTests.cs) 的 15 条用例组成。测试运行真实 `HttpExecutor` 和本地 Orleans fixture，但不启动完整 Kestrel/PostgreSQL 宿主。

新增独立的 `LlmTestServerResilienceE2ETests`，沿用现有 `OrleansCollection` / `GrainClusterFixture`、endpoint、orchestrator 和真实 HTTP 出站链路。必要时抽取少量共享的 LlmTestServer 配置与证据读取 helper；不把故障测试塞入 provider matrix，也不建立第二套完整宿主。

用例继续 opt-in：设置 `AXONHUB_E2E_LLM_TEST_SERVER_URL` 并启动本地 LlmTestServer 才执行；没有 URL 时按现有 `LlmTestServerFact` 规则跳过。使用 `test-key` 假凭据。每个用例用独立模型/场景 ID 完整 configure，以清空队列、请求计数、日志和错误证据；测试串行使用默认 server runtime。

估算新增 20–40 条，连同现有 18 条共约 38–58 条。用例数按 10 类故障分别增加 2–4 个变体控制。

## 故障场景与断言

| # | 场景 | 关键断言 |
|---|---|---|
| 1 | 临时限流型 429 | A 返回 429 后，在配置的尝试上限内转到 B；客户端得到 B 的脚本应答；LlmTestServer 请求证据显示 A 的错误和 B 的匹配；AxonHub usage/cost 记录归属成功的最终渠道。 |
| 2 | 余额/额度耗尽型 429 | 作为终止错误处理，不打 B；保留明确错误语义；调用计数证明没有重试放大。 |
| 3 | 上游 401/403 | 终止，不转 B；客户端错误不是泛化的“所有通道均失败”包装；上游日志只记录 A。 |
| 4 | 连续 5xx 的通道自动禁用 | 用显式自动禁用规则及其阈值注入连续失败；禁用后下一请求不再访问 A 并落到 B；A 的日志计数停在阈值。此规则型自动禁用与模型熔断分开验证。 |
| 5 | 模型熔断与半开探针 | 按当前默认策略验证失败计数到 3 时进入 HalfOpen、到 5 时 Open；可控时钟推进到探针窗口后，只允许一次探测；成功探针关闭熔断，失败探针延后下一窗口。 |
| 6 | 原生工具 fallback 资格 | 对 Gemini / Anthropic 原生工具请求，在存在兼容通道时验证不兼容候选未被调用，且响应及计费归属正确。保持当前移植语义：若没有任何兼容候选，过滤器会回退到原候选集；本套不擅自改成 fail-closed。 |
| 7 | 上游延迟与请求超时 | A 的延迟超过 `LlmRequestTimeout` 后按策略切换；客户端总耗时有界、无挂起，且上游日志可证明延迟注入与尝试顺序。 |
| 8 | 内容过滤错误 | 返回过滤语义，不误判为可重试的临时故障；A 恰一次请求、B 无请求。 |
| 9 | 流式提前结束 | 用 LlmTestServer `EarlyCleanEnd` 在指定内容帧后正常结束但省略协议终止事件；客户端只收到实际前缀，不与另一通道答案拼接；终止标记缺失可被观测，不对已交付内容自动换渠道重试。 |
| 10 | 同 trace 粘性 | 经 trace middleware 连续发送相同 `AH-Trace-Id`；验证通道选择符合当前粘性策略，且各通道请求计数与请求总数一致。 |

成功用例通过请求链 recorder 与确定性价格数据检查最终渠道、usage、cost 和 cost 明细；失败用例检查错误分类、尝试上限、无意外通道调用及失败状态。LlmTestServer 管理 API 的 assertions/request journal 负责验证上游实际收到的通道请求、状态、数量、延迟和流终止证据。

每类只针对该机制添加少数能区分正确与错误行为的变体，不以重复排列参数凑数。每个请求固定 AxonHub retry policy；不把 SDK 自动重试当作网关能力，也不让隐藏重试影响上游计数。

## 时间、隔离与流边界

- 模型熔断探针使用注入的可控 `TimeProvider`，不真实等待默认 5 分钟探针间隔。
- 上游 timeout 用本地脚本延迟和宽裕的总时间上限验证；不对毫秒级耗时做脆弱的精确断言。
- 自动禁用由测试显式注入规则与评估机制，并检查之后的候选/上游记录；它不与 circuit-breaker 状态混为一谈。
- 流式断开后按已观察到的 SSE 前缀和终止证据断言，不要求服务端补造完整答案。
- AxonHub 只转发工具调用，不执行客户端工具动作。因此本套只验证网关不会因流中断自动重复上游尝试或拼接另一份工具调用；外部 action 的 exactly-once 属于实际调用方/agent 套件，不在本设计的验收范围内。

## 验证命令与完成标准

本地 LlmTestServer 启动并设置 `AXONHUB_E2E_LLM_TEST_SERVER_URL` 后，定向运行：

```powershell
dotnet test tests\AxonHub.Api.Tests\AxonHub.Api.Tests.csproj --filter "FullyQualifiedName~LlmTestServerResilienceE2ETests"
```

完成标准：

1. 每条新用例在本地 LlmTestServer 配置下通过，并验证预期客户端结果和上游证据；成功路径另验证 AxonHub 的最终渠道及账单记录。
2. 故障场景的计数、状态序列、错误分类和时间边界均由断言检查，而不是只断言最终成功。
3. 不设置 LlmTestServer URL 时，新用例按既有约定跳过，常规测试不依赖外部进程。
4. 所有凭据均为本地假值，不调用真实 provider。

## 参考

- [Requesty: Best AI Gateways in 2026](https://www.requesty.ai/blog/best-ai-gateways-2026)
- [Margrop: AI Gateway 路由、降级与熔断实战](https://blog.margrop.net/post/ai-gateway-routing-fallback-circuit-breaker/)
