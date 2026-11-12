# §181 全量移植 modelname 响应 / 流式上游模型名（C097 (6) 收口，29 例）

日期：2026-11-12
参照（冻结基线 `b8bb3421` / v1.0.0-beta11）：`llm/modelname/model.go`（`FromResponse` /
`FromEvent` / `reportedModel` / `jsonModel` / `validModel`，约 130 行）、
`internal/server/orchestrator/outbound.go:96-100`（流式事件取模型）、
`internal/server/orchestrator/request_execution.go:220,269`（非流式成功与失败落库）。

## 背景

C097 (6) 记录：端口此前只在三种格式（OpenAI chat、legacy completion、Ollama chat）的流式
事件上取 `upstream_model_id`；非流式落库不按 format 分派、不检查状态码与 media type。
参照的 `FromResponse` / `FromEvent` 覆盖全部 provider 协议的元数据路径，因此 Anthropic、
Responses、Gemini、Transcription 等通道的 `request_executions.upstream_model_id` 与参照
不一致，且非流式错误响应可能误取 body 里的 `model`。

## 交付

- `src/AxonHub.Api/Orchestration/RequestChainPersistenceMiddleware.cs`：
  - `ResponseModel(HttpResponseData?, apiFormat)` 移植 `FromResponse`：空响应与
    `StatusCode >= 400` 直接为空；`response.Request.ApiFormat` 非空时覆盖执行格式；
    transcription / translation 以 JSON media type（`application/json` 或 `+json` 后缀）
    设闸；否则交给 `ReportedModel(..., stream: false)`。
  - `EventModel(StreamEvent?, apiFormat)` 移植 `FromEvent`：空事件与二进制音频块为空，
    其余交给 `ReportedModel(..., stream: true)`；保留调用点“首事件非空即停”的语义
    （`attempt.UpstreamModelId.Length == 0` 守卫，对应参照 `ts.upstreamModelID == ""`）。
  - `ReportedModel` 逐分支移植 `reportedModel`：Anthropic 非流式取 `model`、流式仅
    `message_start` 取 `message.model`；Responses / ResponseCompact / ImageGeneration /
    ImageEdit 非流式取 `model`、流式七个生命周期事件取 `response.model`；Gemini
    Contents / Embedding 取 `modelVersion` 再 `response.modelVersion`；chat / completion /
    Ollama chat 在 `success` 为布尔真时取 `data.model`，否则取 `model`；Decisions 与
    Embedding / Moderation / ImageVariation / TypeSafe / Transcription / Translation /
    Video / JinaEmbedding / JinaRerank / Seedance / ZenmuxVideo 仅非流式取 `model`；
    其余格式为空。
  - `FirstJsonModel` 支持点路径（`message.model` / `response.model`）并在值无效时回退
    下一路径；`JsonString` 对应 `gjson.GetBytes(body, "type").String()`；
    `IsJsonMediaType` 对应 Go 的 `mime.ParseMediaType`（媒体类型大小写不敏感）。
  - 非流式成功落库与 `OnOutboundRawError` 的 `UpstreamModelId` 改走 `ResponseModel`
    （对应参照两处 `modelname.FromResponse(m.rawResponse, ...)`）；错误状态码因此不再取
    body 中的模型名。
  - 删除重构后不再使用的 `ValidModel(JsonElement)` 私有重载。
- `tests/AxonHub.Api.Tests/RequestChainModelNameTests.cs`：由 4 例重写为 29 例
  （19 Fact + 10 Theory），见下。

## 行为要点

- `validModel` 的 512 字节、`TrimSpace` 非空、控制字符规则原样移植；`jsonModel` 只接受
  JSON 字符串（数字、null、对象均丢弃），点路径首值无效时继续尝试下一路径。
- 非对象根、空 body、非法 JSON 一律为空；Cline 信封仅在 `success` 为布尔真时解包。
- JSON media-type 闸门只作用于 transcription / translation（其 body 是可能恰为 JSON 的
  生成内容）；chat 类协议不设 media type 闸门（参照注释的明确决定）。
- 残余（已记 C097 (6)）：非法 UTF-8 的模型值经共享 `GoJsonUtf8` 修复为 U+FFFD 后接受；
  参照 gjson 原样返回后被 `validModel` 的 `utf8.ValidString` 检查拒绝。极窄路径，沿用
  C008 的修复策略，无测试。
- `response.Request.ApiFormat` 覆盖执行格式：`HttpExecutor` 把线上请求挂在
  `HttpResponseData.Request` 上，与参照 `httpclient.Response.Request.APIFormat` 同义。

## 测试

- 流式：OllamaChat / LegacyCompletion 顶层 `model`；Anthropic `message_start` 嵌套
  `message.model`、其它事件为空、事件类型优先于 body `type`；Responses 七个生命周期
  事件（Theory）与内容事件为空；codex 图片格式流；Gemini `modelVersion` 与
  `response.modelVersion`；清单外格式（AiSDK / Speech）为空；二进制音频块。
- 非流式：Responses / Anthropic 顶层 `model`；清单外格式为空；transcription 的
  `text/plain` / `application/json; charset=utf-8` / `application/vnd.openai+json` 三态；
  400 错误状态不取模型；`Request.ApiFormat` 覆盖执行格式；Cline 信封的 `success` 闸门。
- 校验：数字、空白、控制字符、513 字节四类无效值丢弃。
- 定向 35 例（模型名 29 + `RequestChainPersistenceTests` 6）通过；删除死代码后复跑通过。

## 本轮实测

- `AxonHub.Api.Tests`：6182 通过 / 0 失败 / 0 跳过（21m33s，全量重建运行），与 §180
  静态计数 6157 + 25 完全一致。
- `AxonHub.Llm.Tests` 3115、`AxonHub.Persistence.Tests` 376、`AxonHub.Grains.Tests` 177
  未重跑（本刀未触碰），四项目合计 9850。

## 台账

- `differences.jsonl`：C097 (6) 标记关闭并补记 §181 的全部分支、测试计数 29 与 UTF-8 残余。
- `01-baseline.md`：§34 的模型名分派补充为 `FromEvent` 全格式 + `FromResponse` 全格式
  （§34 + §181，4 + 25 例），并追加 2026-11-12 更新段。
- `03-acceptance.md`：新增 §181 段（实现、测试、本轮实测）。
- 269 条（196 accepted / 6 gap / 67 resolved），`diff_register.py lint` 通过，
  `02-differences.md` 已重新生成。
