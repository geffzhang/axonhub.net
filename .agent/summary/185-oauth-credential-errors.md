# §185 OAuth 凭据错误文案对齐（C096 (8) 关闭，C096 resolved，3 例）

日期：2026-11-12
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/biz/channel_llm.go`
的 claudecode 分支（:924-949）与 `buildCodexOutbound`（:334-356），以及
`llm/oauth/credentials.go` 的 `ParseCredentialsJSON` / `ToJSON`。

## 背景

参照分两阶段构造 OAuth 凭据：先把 typed `oauth` 对象按字段拷贝（不复制
`id_token`）重新 `ToJSON`，失败报
`failed to encode {channelType} oauth credentials: %w`；再
`ParseCredentialsJSON`，失败报 `failed to parse {channelType} oauth credentials: %w`。
端口的 `ParseOAuthCredentials` 只包装了 parse 阶段，encode 阶段的失败以原始
`JsonException` 逃逸；重序列化还会把 `id_token` 带回凭据文档。

## 交付

- `ChannelOutboundFactory.ParseOAuthCredentials` 恢复参照的两阶段结构：
  - typed `oauth` 对象存在时，按参照字段拷贝（AccessToken / RefreshToken /
    ClientId / ExpiresAt / TokenType / Scopes，丢弃 `id_token`）后 `ToJson`；
    decode 或 marshal 失败统一报
    `failed to encode {channelType} oauth credentials: <inner>`。
  - 随后 `ParseCredentialsJson`，失败报
    `failed to parse {channelType} oauth credentials: <inner>`。
- 两个消费方（claudecode、codex）的 channelType 前缀与参照一致；github_copilot
  维持其既有独立文案（`github_copilot channel ... has invalid credentials`），未受影响。
- 端口宽松载入（C013）把参照在 Ent 解码阶段就拒绝的畸形 `oauth` 对象带到工厂，
  其错误现归入 encode 文案，与参照的可观察文案对齐。

## 测试

- `tests/AxonHub.Api.Tests/ClaudeCodeAndOpenCodeChannelWiringTests.cs` 新增 2 例：
  - `ClaudeCodeOAuthCredentialsKeepTheReferenceParseErrorCopy`：legacy apiKey 中的
    OAuth JSON 空 access_token → `failed to parse claudecode oauth credentials:
    access_token is empty`。
  - `ClaudeCodeOAuthCredentialsKeepTheReferenceEncodeErrorCopy`：畸形 typed oauth
    （`expires_at` 为数字）→ `failed to encode claudecode oauth credentials: ` 前缀。
- `tests/AxonHub.Api.Tests/CodexChannelWiringTests.cs` 新增 1 例：
  `CodexOAuthCredentialsKeepTheReferenceParseErrorCopy` → `failed to parse codex
  oauth credentials: access_token is empty`。

## 本轮实测

- 新增 3 例通过；定向 39 例（两个接线测试类）通过。
- 定向 580 例（OAuth / ChannelToken / ClaudeCode / Codex / GithubCopilot /
  Antigravity）通过。
- `AxonHub.Api.Tests` 未跑全量（改动限于凭据解析辅助方法），静态计数
  6208 → 6211；四项目合计 9883 → 9886（Llm 3122 / Persistence 376 / Grains 177
  未重跑）。

## 台账

- `differences.jsonl`：C096 的 `what` / `decision` / `test` 补记 (8) 关闭与 3 例，
  `status` 由 `gap` 改为 `resolved`（各条目均已关闭）。
- `01-baseline.md`：§33 追加 2026-11-12 更新段。
- `03-acceptance.md`：新增 §185 段。
- 269 条（196 accepted / 5 gap / 68 resolved），`diff_register.py lint` 通过，
  `02-differences.md` 已重新生成。
