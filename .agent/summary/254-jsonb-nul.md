# §254 写入 jsonb 前剥离 NUL 转义

## 背景

live 提交 `f322df79`（#2779）：PostgreSQL jsonb 拒绝 U+0000 转义
（SQLSTATE 22P05）。参照在请求/执行正文与响应正文写入数据库列之前调用
`stripJSONNul`，否则一个含 `\u0000` 的正文会让整次落库失败。外部存储正文
保持原始字节。

## 实现

- `RequestPayloadStorage.StripJsonNul`：
  - 不含 `\u0000` 的文档原样返回；
  - 删除 NUL 转义；
  - 反斜杠转义按对复制，因此 `\\u0000` 不会被误删；
  - 其他转义（如 `\u0001`）保留。
- `PostgresRequestChainRecorder.CreateRequest` / `CreateExecution` 的数据库
  正文分支应用清洗；外部存储分支仍使用原始正文。
- `RequestPayloadStorage.WriteResponseBodyAsync` 的列分支应用清洗，覆盖
  `UpdateRequestFinalized`、音频完成、视频状态/正文快照与执行 finalized
  等响应路径；外部对象内容不变。

## 测试

- `ResponseBodyDropsNulEscapesInTheColumn`：列写入后 NUL 被移除。
- `StripJsonNulMatchesTheReference`：无 NUL、字符串内 NUL、多处 NUL、转义
  反斜杠、转义反斜杠 + NUL、其他转义、空串 7 例。
- `RequestPayloadStorageTests` 定向 89/89（含请求链路相关回归）。
- 未跑 Api 全量，新增 Api 8 例待下次全量计入。

## 台账

- 新增 C316（resolved）。
- 316 条（190 accepted / 126 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 只清洗数据库列；外部存储对象保留原始字节，与参照一致。
- chunk 列不经过该清洗，参照也未处理。
