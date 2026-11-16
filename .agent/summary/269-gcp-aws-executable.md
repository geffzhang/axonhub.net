# §269 GCP external_account 的 AWS 与 executable 源

## 背景

C128 剩余的真实缺口是 `external_account` 的 AWS 与 executable 凭据源。
参照 `google.CredentialsFromJSON` 通过 `externalaccount` 包支持两者：

- AWS：从环境变量或 IMDSv2/角色/凭据/区域元数据取得 AWS 凭据，使用
  SigV4 签名 STS `GetCallerIdentity` 请求，把签名后的请求 JSON 经
  `url.QueryEscape` 作为 subject token；
- executable：运行配置的命令（或读取 output file），校验
  `version` / `success` / `token_type` / `expiration_time` 后返回
  id_token 或 saml_response。

## 实现

`GcpCredentialSource` 补齐 `region_url`、`regional_cred_verification_url`、
`imdsv2_session_token_url` 与 `executable` 的 command/timeout_millis/
output_file 解码。

新增 `ExternalAccountAwsGcpTokenSource`：

- `environment_id=aws1`，其他版本报参照文案；
- 环境变量优先，缺失时走 IMDSv2 session token、role、credentials、region；
- 手写 SigV4：canonical path/query/headers、HMAC-SHA256 签名链、target
  resource 头、`X-Amz-Date` 等；
- 序列化 `{url, method, headers}` 后 `QueryEscape`。

新增 `ExternalAccountExecutableGcpTokenSource`：

- command 必填，timeout 默认 30s、范围 5..120s；
- output file 优先；非缓存错误（过期、失败响应）回退到执行命令；
- 必须设置 `GOOGLE_EXTERNAL_ACCOUNT_ALLOW_EXECUTABLES=1`；
- 注入 audience/subject token type/interactive/impersonated email/output file
  环境变量，stdout 为空时回退 stderr；
- 校验 version/success/token_type/expiration_time 与 JWT/SAML token。

`ExternalAccountGcpTokenSource` 改用统一的 `IExternalSubjectTokenSource`，
不再只识别 file/URL。

web/installed 交互式客户端仍显式拒绝；参照在 nil AuthorizationHandler 上
会 panic，因此保留为 C128 的 accepted 边界。

## 测试

- AWS 环境变量：签名 URL、method、Host/X-Amz-Date/target resource/
  Authorization（STS 换取的 subject token 解码后断言）。
- AWS metadata：IMDSv2 PUT、role/credentials/region 顺序、区域去掉 AZ
  后缀、session token 头传播。
- executable output file：读取缓存、STS 换取。
- executable 配置与响应：缺 command、timeout 越界、缺 version、失败响应、
  过期 token、SAML token。

结果：GCP 定向 **26/26**，Llm 全量 **3423/3423**。未跑 Api 全量。

## 台账

- C128 保持 accepted 并进一步收窄，明确只剩交互式 web/installed 与少量
  错误文案近似。
- 327 条（190 accepted / 137 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- AWS Azure 源不在 x/oauth2 `externalaccount` 的已支持范围内，未实现。
- executable 命令执行使用 `Process`；output file 路径已覆盖，真实命令路径
  由配置校验与响应解析测试间接锁定。
