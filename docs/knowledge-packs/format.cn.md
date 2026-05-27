[English](./format.md) | 中文

# Banyan 知识包格式

`.banyanpack` 是一个 ZIP 压缩包，其压缩包根目录中必须包含 `manifest.json` 条目。

初始目录结构：

```text
manifest.json
sources/
memories/
entities/
relations/
indexes/
citations/
signatures/
```

v1 核心验证器只要求 `manifest.json`。其余目录为构建、挂载、召回、溯源和签名工作预留，将在后续问题中实现。

清单（manifest）的 JSON Schema 定义位于 `docs/knowledge-packs/manifest.schema.json`。

商业上下文、赞助推荐和发布者特定元数据必须使用 `extensions` 字段，而不是修改 v1 核心 schema。
