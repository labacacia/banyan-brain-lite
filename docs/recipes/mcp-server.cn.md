[English Version](./mcp-server.md) | 中文版

# Recipe：Banyan 作为 MCP 记忆服务器

本 Recipe 说明如何把 Banyan 跑成一个 **Model Context Protocol（MCP）服务器**，
让 Claude Desktop、Claude Code 和任何 MCP 兼容宿主直接获得持久化 agent 记忆能力，
无需在 system prompt 里堆 API 文档。

---

## 你能得到什么

四个工具自动暴露给 LLM 宿主：

| 工具 | 描述 |
|------|------|
| `recall` | 语义 + 关键词混合搜索已存储的记忆，生成前调用以注入相关上下文 |
| `remember` | 持久化一条新记忆供后续会话使用 |
| `update` | 通过 ID 替换已有记忆的内容 |
| `forget` | 通过 ID 删除记忆（内容移除，审计 trace 保留） |

---

## 快速开始

### 1. 安装 Banyan CLI

```bash
dotnet tool install -g Banyan.Cli
```

### 2. 下载 embedding 模型（可选，启用语义搜索）

```bash
banyan embedder download
```

跳过此步骤时，`recall` 自动降级到纯 BM25 关键词搜索。

### 3. 接入 Claude Desktop

添加到 `~/Library/Application Support/Claude/claude_desktop_config.json`（macOS）
或 `%APPDATA%\Claude\claude_desktop_config.json`（Windows）：

```jsonc
{
  "mcpServers": {
    "banyan": {
      "command": "banyan",
      "args": ["mcp"]
    }
  }
}
```

重启 Claude Desktop，记忆工具自动出现。

### 4. 接入 Claude Code

```bash
claude mcp add banyan -- banyan mcp
```

或在项目 `.mcp.json` 里添加：

```jsonc
{
  "mcpServers": {
    "banyan": {
      "command": "banyan",
      "args": ["mcp"]
    }
  }
}
```

---

## CLI 选项

```
banyan mcp [options]

  --db PATH          memory.db 路径          (默认: ~/.banyan/memory.db)
  --namespace NS     默认写入 namespace       (默认: default)
  --sqlite-vec PATH  sqlite-vec 扩展路径      (省略则自动发现)
```

### Namespace 隔离

用 `--namespace` 为不同用户或项目设置独立的记忆分区：

```bash
# 按用户隔离
banyan mcp --namespace user-alice

# 按项目隔离
banyan mcp --namespace project-banyan
```

工具调用时带上 `namespace` 参数，则只搜索该分区；省略则跨所有分区搜索。

---

## 工具参考

### `recall`

按语义和关键词搜索已存储的记忆。在生成回复前调用，注入相关上下文。

| 参数 | 类型 | 默认 | 描述 |
|------|------|------|------|
| `query` | string | 必填 | 自然语言搜索查询 |
| `namespace` | string? | null（全部） | 限定到某个 namespace |
| `k` | int | 5 | 最多返回条数 |
| `mode` | string | `hybrid` | `hybrid` · `lexical` · `vector` |

**返回**按相关度排序的记忆列表，带分数和 ID：

```
[1] score=0.923  id=a1b2c3d4-...  ns=default
用户偏好简洁的列表式回答。

[2] score=0.871  id=e5f6g7h8-...  ns=default
项目截止日期是 2026 年 6 月 15 日。
```

### `remember`

存储一条新记忆，返回记忆 ID 供后续引用。

| 参数 | 类型 | 默认 | 描述 |
|------|------|------|------|
| `content` | string | 必填 | 要持久化的文本内容 |
| `namespace` | string? | CLI 默认值 | 该记忆的分区 |

**返回：** `Stored. id=<uuid>`

### `update`

在不改变 ID 和 namespace 的情况下替换记忆内容。

| 参数 | 类型 | 默认 | 描述 |
|------|------|------|------|
| `memoryId` | string | 必填 | `remember` 返回的或 `recall` 显示的 ID |
| `content` | string | 必填 | 新内容 |

### `forget`

删除一条记忆。原始内容从搜索索引移除；事件日志（审计 trace）保留。

| 参数 | 类型 | 默认 | 描述 |
|------|------|------|------|
| `memoryId` | string | 必填 | 要删除的 ID |
| `reason` | string? | null | 可选原因（记录到审计日志） |

---

## 推荐 System Prompt

在 Claude Desktop 或 Claude Code 的 system prompt 里加入以下内容：

```
你可以通过 Banyan MCP 工具访问持久化记忆存储。

每次生成回复前：
1. 用用户消息作为查询调用 `recall`。
2. 分数 > 0.5 的命中，在上下文中以「相关记忆：」为前缀注入。

在以下情况写入记忆：
- 用户明确说「记住 X」
- 用户纠正了你之前的回答
- 用户确立了某个硬性偏好或决策

不要写入原始对话记录——只写提炼后的事实和偏好。
```

---

## 进阶：多 namespace

同时运行多个 Banyan MCP 实例，各自使用不同数据库或 namespace：

```jsonc
{
  "mcpServers": {
    "banyan-personal": {
      "command": "banyan",
      "args": ["mcp", "--namespace", "personal", "--db", "~/.banyan/personal.db"]
    },
    "banyan-work": {
      "command": "banyan",
      "args": ["mcp", "--namespace", "work", "--db", "~/.banyan/work.db"]
    }
  }
}
```

---

## 搜索模式

| 模式 | 适用场景 |
|------|----------|
| `hybrid` | 默认，最佳质量 — RRF 融合 BM25 关键词排名和向量余弦排名 |
| `lexical` | 精确关键词匹配。适合 ID、名称、代码片段、序列号 |
| `vector` | 纯语义相似度。适合概念和同义词，不适合精确词语 |

向量搜索需要 ONNX 模型（`banyan embedder download`）；
模型缺失时自动降级到纯关键词搜索。

---

## 架构

```
Claude Desktop / Claude Code
        │  MCP stdio（JSON-RPC 2.0）
        ▼
   banyan mcp
        │  IMemoryStore（进程内）
        ▼
  SqliteMemoryStore
  ├── BM25 FTS5（关键词）
  ├── ONNX embedder（向量）
  └── sqlite-vec ANN 索引
```

`banyan mcp` 是自包含进程，无需另起服务。
直接打开 `memory.db`，通过 stdin/stdout 提供 MCP 协议服务。
