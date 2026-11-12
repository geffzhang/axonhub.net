# §186 codex AllowPassThroughBody 的 gjson 宽松探测（C094 (10) 关闭，39 例）

日期：2026-11-12
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`llm/transformer/openai/codex/outbound.go`
的 `responsesBlockedPassThroughFields`（:73-77）与 `AllowPassThroughBody`（:180-192），
依赖 `github.com/tidwall/gjson v1.18.0`。

## 背景

参照用 `gjson.GetBytes(llmReq.RawRequest.Body, field).Exists()` 探测三个
token-limit 字段。gjson 不做 JSON 校验：它扫描首个 `{` 或 `[`，宽松地扫 key 与值，
截断或畸形的 body 仍可能命中顶层键（例如 `{"max_tokens":12000` 命中，
`{"max_tokens":"abc` 不命中）。端口此前用严格 `JsonDocument.Parse`，解析失败一律
放行，与参照的可观察行为不同。

## 交付

- 新增 `src/AxonHub.Llm/Json/GjsonRootKeyProbe.cs`，逐字移植 gjson v1.18.0 中
  `Get` 走到的代码路径：`Get`（首个 `{`/`[` 定位；数组根对简单 key 不命中）、
  `parseObject`（key 扫描、值扫描跳过 `:`/`,` 等任意非值字节、匹配 key 无值时越过
  `}` 继续、字符串值必须终止否则整体不命中）、`parse_key_string` 与 `unescape`
  （反斜杠奇偶规则、`\uXXXX` 与代理对、未知转义截断）、`parseString`、
  `parseSquash`（嵌套对象/数组/括号与字符串跳过）、`parseNumber`、`parseLiteral`。
- `CodexOutboundTransformer.AllowPassThroughBody` 改用该探测；字段表改为
  `ReadOnlyMemory<byte>[]`（`u8` 字面量），删去严格解析与 catch 放行。
- null 请求、非 `OpenAIResponse`、缺 RawRequest、空 body 的 true 分支保持不变。

## 测试

- `tests/AxonHub.Llm.Tests/CodexOutboundExecutorTests.cs` 新增 39 例：
  - 38 个差分语料行：用 Go + gjson v1.18.0 对 40 个 body 实跑
    `GetBytes(body, field).Exists()`，覆盖截断对象/数组/数字、未终止字符串、
    未加引号 key、缺逗号/冒号、`{"max_tokens"} 5`、`_` 转义 key、
    leading garbage、字符串根内的对象文本、null/true/false/NaN、数组根、
    嵌套 key、空 body、BOM 之外的各种畸形输入。
  - 1 个 UTF-8 BOM 前置用例（`Encoding.UTF8.GetPreamble()` + body）。
- 开发期另用 3026 个随机/变异 body 做了临时差分核对（3 字段共 9078 次比较，
  与 gjson v1.18.0 零差异），该临时测试与语料未入库。

## 本轮实测

- 新增 39 例通过（类内合计 80 例）。
- `AxonHub.Llm.Tests` 全量 3161 例通过。
- Api 定向 96 例（`PassThroughTests` / `CodexChannelWiringTests` 等）通过。

## 台账

- `differences.jsonl`：C094 的 `what` / `decision` / `test` 补记 §186，(10) 关闭；
  `status` 仍为 `gap`（保留 (11) DecodeAuthJson 消费方与 §146 的 fenno /
  github_copilot / antigravity provider 复用残余）。
- `01-baseline.md`：§29 末尾追加 2026-11-12 更新（§186）段。
- `03-acceptance.md`：新增 §186 段。
- 269 条（196 accepted / 5 gap / 68 resolved），`diff_register.py lint` 通过，
  `02-differences.md` 已重新生成。
