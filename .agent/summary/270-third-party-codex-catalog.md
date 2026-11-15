# §270 第三方 Codex 目录与官方目录 gpt-image-2

## 背景

live `de5f9039`（#2675）为使用普通 API key 的 Codex 通道补上第三方模型
目录；`3cb7e333`（#2651）让官方目录补充 `gpt-image-2`。端口此前两者都未
落地，C158 记录了该裁剪。

## 实现

- `CodexModels.ParseThirdPartyModelCatalog`：显式目录语义——不过滤
  `visibility`、不补 `gpt-image-2`；`ParseModelCatalog` 继续只保留
  `visibility=list`，对官方目录补充/去重 `gpt-image-2`。
- `ModelFetcher`：
  - 新增 `thirdPartyCodexCatalog = channelType == "codex" && codexChannel == null
    && !IsOAuthJson(apiKey)`；
  - 该分支与 Command Code/Codex 一样拒绝 HTTPS 降级，并额外拒绝所有重定向；
  - 校验目录 URL 必须为 HTTPS，否则返回
    `failed to prepare Codex models request: Codex catalog URL must use HTTPS`；
  - 用 `ApiKeyTokenProvider` 构造 Codex `/models` 请求，解析走
    `ParseThirdPartyModelCatalog`。
- 前端 Codex 第三方凭据对话框不在后端移植范围。

## 测试

- `ThirdPartyCodexCatalogUsesPlainKeyAndHttps`：普通 key、HTTPS、显式
  visibility、image 去重。
- `ThirdPartyCodexCatalogRequiresHttps`：HTTP 直接报错且不发请求。
- 更新官方目录期望（补充 `gpt-image-2`）与 relay 目录请求形状。

结果：`ModelFetcherCodexTests` **12/12**，全部 ModelFetcher **56/56**，
Llm Codex **119/119**，Llm 全量 **3423/3423**。未跑 Api 全量。

## 台账

- C158 转 **resolved**：模型发现的已知裁剪（调度、按通道代理、Vertex key
  绑定、第三方 Codex 目录）全部关闭。
- 327 条（189 accepted / 138 resolved / 0 gap）；lint 通过，
  `02-differences.md` 已重新生成。

## 边界

- 第三方目录不补 image 模型，也不解析非 `models[].slug` 形状。
- 前端凭据模式校验仍未移植，属于前端范围。
