# §277 S3 PutObject 关闭默认尾部校验和（live #2637 / #981）

## 背景

live 提交 `18cc192e`（#2637，修复 #981）：aws-sdk-go-v2 默认
`RequestChecksumCalculation=WhenSupported`，PutObject 在 HTTPS 下会计算 CRC32
并把请求体包装成 aws-chunked（`Content-Encoding: aws-chunked`、
`x-amz-content-sha256: STREAMING-UNSIGNED-PAYLOAD-TRAILER`、
`x-amz-trailer`）。阿里云 OSS 的 S3 兼容接口返回 HTTP 200，却存储 0 字节；备份
仍按客户端 `countingReader` 报告成功。参照改为 `WhenRequired` 关闭默认校验和与
响应校验，调用方显式指定 `ChecksumAlgorithm` 时仍可用。

## 端口差异

`AWSSDK.S3` 4.0.20.4 同样默认 `WHEN_SUPPORTED`，且 `PutObjectRequest.UseChunkEncoding`
默认为 true，会发送 `STREAMING-AWS4-HMAC-SHA256-PAYLOAD` 的 aws-chunked 请求体。
只设置 `WhenRequired` 后，请求体仍是分块签名，与参照的普通签名请求体不一致。

## 修复

- `S3DataStorageBackend.CreateConfig` 设置
  `RequestChecksumCalculation.WHEN_REQUIRED` 与
  `ResponseChecksumValidation.WHEN_REQUIRED`。
- 两条 PutObject 路径（`WriteAsync`、`WriteFromStreamAsync`）设置
  `UseChunkEncoding = false`，确保 S3 兼容存储收到普通签名请求体。
- 新增内部 `CreateClient(settings, HttpClientFactory)` 与接收 `IAmazonS3` 的构造
  缝隙，对应参照的 `newS3ClientWithHTTPClient` / `newS3ObjectStoreWithHTTPClient`。
- 顺带修正 `WriteFromStreamAsync`：原实现在 finally 释放缓冲流后才读取
  `buffered.Length`；SDK 本身也会释放 InputStream。改为上传前捕获长度，避免
  `ObjectDisposedException`。

## 测试

- 新增 `tests/AxonHub.Api.Tests/S3DataStorageChecksumTests.cs`：
  `PutObjectSendsAPlainSignedPayloadOverHttps` 通过 HTTPS 端点捕获字节 Save 和
  非 seekable 流式备份两条路径，断言无 `STREAMING-` 哈希、无 `aws-chunked`、
  无 `x-amz-trailer`、请求体与原文完全一致，并断言客户端配置为 `WHEN_REQUIRED`。
- 定向：S3 checksum **1/1**；Api 存储相关 **189/189**。
- Llm 未改动，仍为 **3437/3437**；Api 全量待本轮改动后重跑。

## 台账

- 新增 C333（resolved）。
- 333 条（190 accepted / 143 resolved / 0 gap）；lint 与
  `02-differences.md` 重生成通过。

## 边界

- `UseChunkEncoding=false` 依赖上传流可 seek；两条路径都使用 `MemoryStream`
  或已缓冲流，生产调用不会传未知长度的直连流。
- 调用方在请求上显式设置 `ChecksumAlgorithm` 时，`WHEN_REQUIRED` 仍会计算校验和。
