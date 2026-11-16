# §225 请求路径配置面：IP 白名单、CORS、SSE 心跳

## 台账

- 新登记 C297-C303（均 `accepted`）：ip_access_control、SIGHUP/pid_file、
  CLI 子命令、CORS、SSE keep-alive、max_multipart_memory、log.*。
- 本轮实现 C297/C300/C301，已改为 `resolved` 并补测试路径；C298/C299/C302/C303
  保持 `accepted`。
- `differences.jsonl` 仍为紧凑 JSON（`separators=(",", ":")`），共 303 条；
  `lint` 通过，`02-differences.md` 已重新 render。

## 实现

1. `AxonHub.Api.Security.IpAccessControl`
   - `IpAccessControlSettings` 读取 `Server:IpAccessControl:*` 与
     `AXONHUB_SERVER_IP_ACCESS_CONTROL_*`；`allowed_ips` 支持数组与逗号分隔标量。
   - `IpAccessControlConfig` 持有原子快照，`Apply` 先校验后替换，失败保留旧快照
     （为 C298 的 SIGHUP 重载预留）。
   - 校验在宿主构建期执行，非法条目抛
     `invalid allowed IP "..."` / `invalid allowed IP prefix "..."`。
   - `IpAccessControlMiddleware`：`ClientAddress.Resolve`（gin ClientIP +
     trusted_proxies）→ 精确 IP/CIDR 匹配；空或不可解析地址拒绝；
     `redirect_url` 非空返回 302，否则空体 404；在 `Program.cs` 全部路由前注册。
2. `CorsSettings` + `ConfigurePolicy`
   - 默认值与 conf/conf.go:277-285 一致；`server.cors.enabled` 为真时注册
     ASP.NET Core 默认策略，并在 metrics 之后 `app.UseCors()`。
   - `allowed_origins` 含 `*` 时回显来源，避免与 `AllowCredentials` 冲突。
3. SSE keep-alive
   - `SseKeepAliveSettings`（默认关闭、15s）。
   - `SseStreamWriter.WriteWithHeartbeatAsync`：读线程 + 事件/心跳/取消三路选择，
     事件重置间隔；OpenAI `: keep-alive\n\n`，Anthropic
     `event: ping\ndata: {"type":"ping"}\n\n`；心跳写失败按断开返回。
   - `ChatCompletionEndpoint` 仅在启用时换用心跳 writer；Anthropic inbound 选
     Anthropic 载荷；Gemini/playground/request-preview 保持无心跳 writer，与参照
     `WithStreamWriter` 分工一致。

## 测试

- `dotnet test` 定向：32/32 通过（新增 IP/CORS/心跳 + 原有 SSE writer）。
- 聊天/流式相关回归 + 新增：124/124 通过。
- 未跑 Api 全量（上次全量 42m22s，本轮改动集中在请求路径配置面）。

## 未完成

- C298 SIGHUP + pid_file、C299 CLI 子命令、C302 multipart 阈值、C303 log.*
  仍未实现；C298/C299 建议作为宿主/运维切片单独做。
