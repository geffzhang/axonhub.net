# §256 GCP workforce 与 impersonation 凭据

## 背景

C128 记录了 GCP 凭据覆盖范围的裁剪：端口只支持 service_account 与
authorized_user；`external_account`、`external_account_authorized_user`、
`impersonated_service_account` 以及 web/installed 客户端在构造或取令牌时
显式失败。本轮补齐两种非交互凭据，C128 的真实缺口收窄为 workload federation
与交互式客户端。

## 实现

- `GcpCredentialFile` 补齐字段解码：
  - `token_url`、`revoke_url`、`delegates`；
  - 嵌套 `source_credentials`（递归解码）；
  - `service_account_impersonation.token_lifetime_seconds`。
- `external_account_authorized_user`
  - 构造时要求 `client_id` / `client_secret` / `refresh_token` / `token_url`，
    否则返回参照的“Token should be created with fields...”文案；
  - POST STS，`grant_type=refresh_token` + `refresh_token`，HTTP Basic
    `client_id:client_secret`；
  - 解析 access_token / refresh_token / expires_in；负 expires_in 报
    `got invalid expiry from security token service`；更新 refresh token；
    外层复用 10 秒提前过期窗口。
- `impersonated_service_account`
  - 要求 `service_account_impersonation_url` 与 `source_credentials`；
  - 递归构造 source token source；
  - POST IAM URL，Bearer source token，body 字段序
    `delegates` / `lifetime`（默认 3600s，scope 为空时省略）；
  - 解析 `accessToken` / `expireTime`（RFC3339），外层 reuse；
  - 状态码、读取、解析与 expiry 错误使用参照前缀。
- `external_account` 与交互式 web/installed 行为不变。

## 测试

- `GcpTokenSourceTests` 新增 5 例：
  - Basic auth 刷新与 form 形状；
  - 缺 refresh 字段的构造错误；
  - 负 `expires_in`；
  - 嵌套 source credentials + delegates/lifetime 的 impersonation 请求与
    Bearer 头、expiry；
  - 缺 source/url 的构造错误。
- GCP 定向 18/18；Llm 全量 **3354/3354**。未跑 Api 全量。

## 台账

- C128 保持 accepted，真实缺口收窄并更新 what/decision/test。
- 台账仍 317 条（190 accepted / 127 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- `external_account` 的 STS token exchange 与 AWS/Azure/file/url 源仍未移植；
- web/installed 仍依赖交互式授权处理器，参照在 nil handler 下同样不可用。
- 底层 JSON 解码错误文案仍来自 System.Text.Json（既有 C128 近似项）。
