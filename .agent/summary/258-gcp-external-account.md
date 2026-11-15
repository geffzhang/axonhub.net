# §258 GCP external_account 的 file/URL 凭据源

## 背景

C128 前两轮已实现 workforce 用户凭据与 service-account impersonation，但
workload identity federation 的 `external_account` 仍显式失败。本轮补齐其
file/URL subject token 源与 STS token exchange；AWS 与 executable 源仍为显式
缺口。

## 实现

- `GcpCredentialSource` / `GcpCredentialFormat` / `GcpExecutableConfig`
  解码 `credential_source`：
  - `file` / `url` / `headers` / `format.type` /
    `format.subject_token_field_name` / `environment_id` / `executable`。
- `ExternalAccountFileGcpTokenSource`：
  - 读取最多 1 MiB，TrimSpace；
  - `text` / 空类型直接返回，`json` 按 `subject_token_field_name` 取字符串；
  - 缺失字段、非字符串、未知格式使用参照文案。
- `ExternalAccountUrlGcpTokenSource`：
  - GET URL，附加配置的 headers，检查 2xx，同样解析格式。
- `ExternalAccountGcpTokenSource`：
  - RFC 8693 表单：audience / grant_type / requested_token_type /
    subject_token_type / subject_token / scope；
  - client_id + client_secret 时使用 Basic auth；workforce pool 且无 client id
    时附加 `options={"userProject":"..."}`；
  - STS 默认 URL 支持 universe domain；解析 access_token / token_type /
    expires_in / refresh_token；expires_in <= 0 报参照错误。
- 可选 `service_account_impersonation_url`：
  - STS 源 scope 固定 `cloud-platform`，impersonation 请求 scope 为空；
  - 复用 §256 的 `ImpersonatedGcpTokenSource` 与 lifetime。
- 配置校验：audience、subject_token_type、workforce audience 形状、单一 source；
  AWS / executable 抛 `NotSupportedException`（C128）。

## 测试

- `ExternalAccount_exchanges_a_json_file_subject_token`：文件 JSON 源与 STS 表单。
- `ExternalAccount_reads_a_url_subject_token_with_headers`：URL 源与 header。
- `ExternalAccount_validates_the_configuration`：缺 audience / 类型 / source，
  AWS 源 NotSupported。
- `ExternalAccount_impersonation_uses_the_sts_source`：STS scope 与二次 IAM 交换。
- GCP 定向 22/22；Llm 全量 **3358/3358**。未跑 Api 全量。

## 台账

- C128 保持 accepted，真实缺口进一步收窄并更新 what/decision/test。
- 317 条（190 accepted / 127 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- AWS（SigV4/IMDS）与 executable subject token 源未移植。
- `x-goog-api-client` 指标头未发送；底层 JSON/传输错误仍是近似文案。
- 交互式 web/installed 凭据仍不可用。
