# §180 claudecode / codex 多 key 执行审计（C096 (7) 收口，11 例）

日期：2026-11-12
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：
`internal/server/biz/channel_llm.go:169-184`（`getAPIKeyProvider` 的
`ChannelAPIKeyContextProvider` 包装）、`:951-989`（claudecode 两条凭据路径）、
`internal/server/biz/channel_llm.go:331-425`（`buildCodexOutbound` 的凭据路径）。

## 背景

C096 (7) 记录：参照把实际发送的 key 经 key provider 发布到 ctx，
`CreateRequestExecution` 据此写 `request_executions.channel_api_key_suffix` /
`channel_api_key_index`；端口从出站的 `IApiKeyProvenance` 读取，但 claudecode 与 codex
的 api-key 出站没有实现该接缝，多 key 通道的审计列为 null。C013 的 provider 选择与
OpenAI 家族审计此前已闭合，缺口只在 claudecode / codex 两个出站。

## 交付

- `src/AxonHub.Llm/Transformer/Anthropic/ClaudeCode/ClaudeCodeOutboundTransformer.cs`：
  `ClaudeCodeParams` 增加端口专用的 `AuditApiKey`（bool）与 `ChannelApiKeyIndex`
  （int?）；出站实现 `IApiKeyProvenance`，`TransformRequest` 在取到
  `credentials.AccessToken` 后按 `AuditApiKey` 记录 `_selectedApiKey`。
- `src/AxonHub.Llm/Transformer/OpenAI/Codex/CodexOutboundTransformer.cs` 与
  `CodexAlphaSearch.cs`：`CodexOutboundParams` 增加同样的两个字段；出站实现
  `IApiKeyProvenance`，普通 Responses 路径与 alpha-search 路径都在取到 token 后记录。
- `src/AxonHub.Api/Orchestration/ChannelOutboundFactory.cs`：
  - `CreateClaudeCodeOutbound` 的非 OAuth 分支设置 `AuditApiKey = true`、
    `ChannelApiKeyIndex = apiKeyProvider.KeyIndex`；OAuth 分支保持默认（不审计）。
  - `CreateCodexOutbound` 以 `!ChannelCredentials.IsOAuth(...)` 决定审计开关，
    索引同样来自 `apiKeyProvider.KeyIndex`；codex 与 fenno 的非 OAuth 分支共用该路径。

## 行为要点

- 记录的是 `ApiKeyTokenProvider` 实际返回并写入 `Auth.ApiKey` 的 token，而不是再次读取
  provider；与参照“发布 ctx 的 key”在同一时机、同一值。
- `SelectedApiKeyIndex` 来自 `ChannelApiKeyProvider.KeyIndex`：全量 key 列表的 1-based
  位置，单 key 或 override key 不在列表时为 null，与参照的索引规则一致。
- OAuth 分支不审计：参照的 OAuth 路径不使用 `getAPIKeyProvider`，访问令牌不是通道 key；
  claudecode / codex 的 OAuth 测试都断言审计列为 null。
- codex 的 alpha-search 请求同样携带 Bearer key，因此也记录；图片分支走普通
  `TransformRequest` 的 token 记录点。

## 测试

- Llm 新增 5 例：`ClaudeCodeOutboundTransformerTests`（审计记录、OAuth 不审计）、
  `CodexOutboundExecutorTests`（审计记录、OAuth 不审计）、
  `CodexAlphaSearchTests`（alpha-search 审计）。
- Api 新增 6 例：`ClaudeCodeAndOpenCodeChannelWiringTests`（key 通道审计、OAuth 不审计）、
  `CodexChannelWiringTests`（key 通道审计、OAuth 不审计）、
  `ChannelApiKeyAuditTests`（claudecode / codex 多 key 端到端，断言后缀与 1-based 索引）。

## 本轮实测

- `AxonHub.Llm.Tests`：3115 通过 / 0 失败 / 0 跳过（§179 的 3110 + 5）。
- `AxonHub.Api.Tests`：定向 45 例通过（上述三个改动类）；全量未跑，按分层验收策略
  顺延。静态计数 6157（6151 + 6），四项目合计 9825，等待下一次全量复核。
- Persistence / Grains 未改动，沿用实测 376 / 177。

## 分层验收策略（自 §180 起）

每次切片跑受影响项目的定向测试，纯 Llm 切片同时跑 `AxonHub.Llm.Tests` 全量（秒级）。
`AxonHub.Api.Tests` 全量只在以下情形触发：改动编排中间件 / 请求管线 / 持久化落库 /
DI 与宿主启动 / 共享序列化等横切面，跨多个通道家族的共享代码，连续 3–5 个切片未跑，
或发布前收口。全量失败时先单类重跑，以区分 §103 的 testhost 随机崩溃与真实回归。

## 台账

- `differences.jsonl`：C096 (7) 标记关闭并补记 §180；C013 的审计用例数由 7 增至 9。
- `01-baseline.md`：§8.6 审计链改为 OpenAI 家族 / claudecode / codex，用例数 30 → 32；
  §33 的残余描述与文件计数更新。
- 269 条（196 accepted / 6 gap / 67 resolved），`diff_register.py lint` 通过，
  `02-differences.md` 已重新生成。
