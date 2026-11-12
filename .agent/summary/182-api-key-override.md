# §182 通道 key 测试流的 apiKeyOverride 语义（C013 / C014 / C097 (7) 纠偏，11 例）

日期：2026-11-12
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`internal/server/biz/channel_llm.go:655-658`
（`TrimSpace` 与仅非空才设置 `ch.apiKeyOverride`）、`:677-693`（按通道类型的凭据检查）、
`:716`（override 写入通道）、`:1392-1428`（ollama / ollama_anthropic 的 override 感知
provider 守卫）、`internal/server/biz/request.go:414-421`（1-based 执行审计索引）、
`channel_llm_ollama_test.go:159-193`（`TestOllamaAnthropicChannel_APIKeyOverrideWithoutStoredKeys`）。

## 背景

§76 已把 `ChannelCandidate.ApiKeyOverride` 接到 `ChannelApiKeyProvider.ForOverride`，
`ChannelOutboundFactory` 在凭据检查后按参照先 `TrimSpace` 再应用；§164 也依赖同一
provider。但台账 C013 / C014 / C097 (7) 仍写着“apiKeyOverride 未移植”，且参照的强制
key 分支在端口没有直接测试，工厂里还留着“端口没有 key-test flow”的过时注释。

## 交付

- `tests/AxonHub.Api.Tests/ChannelApiKeyOverrideTests.cs`（新增，11 例）：
  - ollama / ollama_anthropic（Theory）：无存储 key + override 时安装 provider、发送
    Bearer，审计索引为 null（参照测试的直接移植 + 原生 ollama 对称面）。
  - ollama / ollama_anthropic（Theory）：纯空白 override 被 `TrimSpace` 成空，保持
    无认证。
  - commandcode / commandcode_anthropic（Theory）：存储 key 全部禁用 + override 时
    凭据检查放行，发送 `TrimSpace` 后的 Bearer key。
  - commandcode / commandcode_anthropic（Theory）：空白 override 不能替代缺失的
    存储 key，抛 `missing api key for channel ...`。
  - default 类型（OpenAI）：override 不能替代缺失的存储 key（`:691-693`）。
  - default 类型：空白 override 回退存储 key。
  - override 命中存储列表时审计索引为 1-based 位置（2），不在列表时为 null。
- `src/AxonHub.Api/Orchestration/ChannelOutboundFactory.cs`：只纠正 commandcode 分支的
  过时注释（工厂与 provider 自 §76 / §164 起已按参照实现，无行为改动）。

## 行为要点

- 强制 key 的生命周期：候选携带原始字符串 → 工厂 `TrimSpace` → 非空才覆盖已启用 key
  的选择与 trace 粘性 → `ForOverride` 决定发送值与审计索引。
- 凭据检查按通道类型分叉：ollama 两型完全豁免；commandcode 两型接受“enabled 存储 key
  或非空 override”；codex / claudecode 只接受 OAuth 或 enabled 存储 key（override 不
  参与）；其余类型要求至少一个 enabled 存储 key，override 只覆盖发送值。
- 审计：`channel_api_key_suffix` 仍取实际发送 key 的后 4 个 rune；`channel_api_key_index`
  只在全量存储 key 多于一个且命中时写 1-based 位置，override 不改变该规则。

## 测试

- 新增 11 例（4 个 Theory 各 2 + 3 个 Fact）。
- 定向 136 例通过：新增 11 + `OllamaChannelWiringTests` 7 +
  `CommandcodeChannelWiringTests` 12 + `ChannelApiKeyStrategiesTests` 57 +
  `ChannelTestServiceTests` 49。

## 本轮实测

- `AxonHub.Api.Tests` 未跑全量（本刀只新增测试与注释，未触碰横切面），静态计数
  6182 → 6193，四项目合计 9850 → 9861（Llm 3115 / Persistence 376 / Grains 177 未重跑）。

## 台账

- `differences.jsonl`：C013 的残余与 C014 的 what / decision / test 改为“apiKeyOverride
  已由 §76 接线、§182 锁定”；C097 (7) 关闭并补记 11 例。
- `01-baseline.md`：§76 追加 2026-11-12 更新段（11 例 + 行为要点）。
- `03-acceptance.md`：新增 §182 段（实现、测试、本轮实测）。
- 269 条（196 accepted / 6 gap / 67 resolved），`diff_register.py lint` 通过，
  `02-differences.md` 已重新生成。
