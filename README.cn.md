[English Version](./README.md) | 中文版

# 🌳 Banyan

> 给 AI agent 用的记忆节点 — 跑 [NPS](https://github.com/labacacia/NPS-Release) 协议、
> SQLite 存储、完全可离线运行。

[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](./LICENSE)
[![Status](https://img.shields.io/badge/status-alpha-orange)]()

Banyan 是一个事件驱动的记忆存储 — agent 通过 `Remember()` / `Search()` /
`Update()` / `Forget()` 与之交互。Wire 协议是
[NPS-3](https://github.com/labacacia/NPS-Release)：Memory Node 走
[`NPS.NWP`](https://www.nuget.org/packages/LabAcacia.NPS.NWP) 中间件，agent / node 身份是
[NIP CA](https://github.com/labacacia/nip-ca-server) 颁发的 Ed25519 NID 证书；
人机身份走 [OLS](https://www.nuget.org/packages/OLS.Root.Core) 实现的 OIDC。

---

## 特性

- **混合检索** — BM25 (FTS5) + ONNX 向量 + RRF 融合。向量索引在
  [sqlite-vec](https://github.com/asg017/sqlite-vec) 加载时走 ANN，否则线扫 cosine。
- **真语义 embedding** — `IEmbedder` 可插拔；默认 `bge-small-zh-v1.5`（多语言、
  22 MB INT8 ONNX、384 维）；离线降级是 hashing n-gram。
- **双轨身份模型**
  - **Agent / Memory Node**：Ed25519 NID 证书，本地 `NipCaService` 或远程 NPS-CA
    都能签。Banyan 提供 NPS-3 §8 标准的 HTTP 路由 — 因为 `NPS.NIP` 这个 NuGet
    目前还没把 routing 实装好，由我们补齐。
  - **Operator / 管理员**：OLS 提供 OIDC + JWT，所有 Identity / OAuth store
    我们都用 SQLite 自实装。
- **Lite 自带真 NID 鉴权** — `Authorization: NID <base64(IdentFrame)>` 中间件，
  三档可选（`anonymous-allowed` / `writes-required` / `all-required`）。
  服务端校验过的 NID 会直接覆盖请求体里的 `agentNid`；内嵌 CA 的吊销立刻生效。
- **事件驱动记忆** — 每次 `Write/Update/Forget` 都追加到不可变日志；最新快照在
  `memories_current`；即使 forget 之后，trace 仍然可审计。
- **标准合规的 Memory Node** — `banyan serve` 挂载
  `app.UseMemoryNode<TProvider>`，暴露 `/.nwm`（`NeuralWebManifest`）、
  `/.schema`、`POST /api/memory/query`（带 `anchor_ref`、`token_est` 的 NWP 帧）。
- **Demo Web UI** — 霓虹玻璃 + 粒子网格背景，三 tab SPA（Memory · Agents · About）；
  搜索框直接切换 BM25 / Vector / Hybrid，中英双语语义命中。
- **单二进制 CLI** — `dotnet tool install -g Banyan.Cli` 一键拿全功能
  （`keygen` / `init` / `login` / `ca init` / `agent issue/verify/revoke` /
  `embedder download` / `web` / `serve`）。

## 快速开始

```bash
# 0. 安装
dotnet tool install -g Banyan.Cli

# 1. 拉 embedder 模型 + sqlite-vec 扩展（约 24 MB）
banyan embedder download

# 2. 初始化人机身份库（交互式建 admin）
banyan keygen
banyan init

# 3. 初始化 NID CA
export BANYAN_NIP_CA_PASSPHRASE='your-passphrase'
banyan ca init

# 4. 本地签发一个 agent NID
banyan agent issue --id summarizer-01 --cap memory.read,memory.write \
  --key-out ~/.banyan/agents/summarizer-01.key

# 5a. 试 Web UI demo（Memory / Agents / About 三个面板）
export BANYAN_EMBEDDER=onnx
banyan web
# 浏览器打开 http://localhost:5180
# → 点击右上角「Sign in」，用步骤 2 创建的管理员账号登录
#   登录后界面会显示完整管理功能（agent 管理、CA 操作）。
#   跳过步骤 2 的话，UI 仍可用于匿名记忆读写（zero-config demo 模式）。

# 5b. 或起一个真 Memory Node（NWP wire）
banyan serve --allow-anon
# POST /api/memory/query 带 QueryFrame body
# GET  /.nwm 看 NeuralWebManifest

# 5c. 打开 NID 鉴权（writes-required 是常见生产档）
banyan web   --nid-auth writes-required
banyan serve --nid-auth writes-required
# POST/PUT/DELETE/PATCH 都要带 Authorization: NID <base64(IdentFrame)>，读保持公开

# 6. 在另一台主机：通过远端 CA issue / verify / revoke
export BANYAN_CA_URL=https://your-ca-host:5180
banyan agent issue --id offsite-agent --cap memory.read --remote $BANYAN_CA_URL
banyan agent verify urn:nps:agent:.../offsite-agent --remote $BANYAN_CA_URL
```

## 当 agent 持久记忆用

如果你是 agent 作者（Claude / GPT / 自研助手），把 Banyan 接进去：

```python
import requests

def recall(query: str, user_id: str, threshold: float = 0.50) -> list[str]:
    r = requests.get("http://banyan-host:5180/api/memory/search",
                     params={"q": query, "mode": "hybrid", "k": 5,
                             "namespace": f"user-{user_id}"}, timeout=2)
    return [h["content"] for h in r.json()["hits"] if h["score"] > threshold]

def remember(fact: str, user_id: str, agent_nid: str | None = None):
    requests.post("http://banyan-host:5180/api/memory", json={
        "content": fact, "namespace": f"user-{user_id}", "agentNid": agent_nid,
    }, timeout=2)
```

每轮对话开始前 recall 一次，写入只在显式信号（用户说"记住 X"、纠正你、定下决策）。
完整接入指南在 [`docs/recipes/agent-memory.cn.md`](./docs/recipes/agent-memory.cn.md) —
覆盖 namespace 设计、阈值经验、写入触发、NID 鉴权模式、失败恢复、反模式。

## 项目结构

```
src/
├── Banyan.Core         # IMemoryStore、IEmbedder 接口、请求/响应记录
├── Banyan.Lite         # SqliteMemoryStore（BM25 + 余弦 + RRF + sqlite-vec ANN）
├── Banyan.Embedders    # HashingEmbedder、OnnxEmbedder、EmbedderFactory
├── Banyan.Auth         # NID CA：EmbeddedNipCa / SqliteNipCaStore / RemoteNipCaClient
├── Banyan.Identity     # OLS 驱动的人机身份（OIDC、JWT、RBAC），存 SQLite
├── Banyan.Web          # ASP.NET Core demo UI + agents/memory/identity REST，
│                         以及 NPS-3 §8 NIP CA HTTP 路由（.NET 侧补缺）
├── Banyan.Node         # banyan serve — 挂 NPS.NWP MemoryNodeMiddleware
└── Banyan.Cli          # banyan dotnet tool

tests/
├── Banyan.Core.Tests       (5)
├── Banyan.Lite.Tests       (42，含 6 个 ONNX + 5 个 sqlite-vec)
├── Banyan.Auth.Tests       (37，含 7 个 RemoteNipCaClient + 10 个 NID 中间件)
├── Banyan.Identity.Tests   (43)
└── Banyan.Node.Tests       (8)
```

## 文档

| 文档 | 内容 |
|---|---|
| [`docs/recipes/agent-memory.cn.md`](./docs/recipes/agent-memory.cn.md) | **Recipe**：把 agent（Claude / GPT / 自研）接到 Banyan 当持久记忆 |
| [`docs/architecture/editions.cn.md`](./docs/architecture/editions.cn.md) | Lite · Pro · Ent 三层范围矩阵 — NPS 合规 + 拓扑、本仓库范围 |
| [`docs/architecture/storage-tiers.cn.md`](./docs/architecture/storage-tiers.cn.md) | 记忆 / 身份 / CA 的 SQLite 表结构、事件日志、FTS5、向量布局 |
| [`docs/architecture/nps-mapping.cn.md`](./docs/architecture/nps-mapping.cn.md) | Banyan 与 NPS-3（NCP / NWP / NIP）映射 — 我们消费什么、补齐什么 |
| [`docs/architecture/identity.cn.md`](./docs/architecture/identity.cn.md) | 双轨身份模型：机器走 NID，人走 OLS / OIDC |
| [`docs/architecture/ols-surface-reference.cn.md`](./docs/architecture/ols-surface-reference.cn.md) | 反射出来的 `OLS.Root.*` API 表面（参考用） |

## 站在巨人肩膀上

- [LabAcacia.NPS.{Core,NIP,NWP}](https://github.com/labacacia/nps) — Neural Web Protocol 栈
- [labacacia/nip-ca-server](https://github.com/labacacia/nip-ca-server) — NIP CA HTTP API 参考实装
- [OLS.Root.{Core,Authentication,Authorisation,Oidc}](https://github.com/orilynn/ols-root) — 人机身份栈
- [Microsoft.ML.OnnxRuntime](https://onnxruntime.ai/) + [Microsoft.ML.Tokenizers](https://www.nuget.org/packages/Microsoft.ML.Tokenizers) — ONNX 推理 + BERT WordPiece
- [Xenova/bge-small-zh-v1.5](https://huggingface.co/Xenova/bge-small-zh-v1.5) — 多语言句子向量
- [asg017/sqlite-vec](https://github.com/asg017/sqlite-vec) — SQLite ANN 向量索引

## 许可证

Apache-2.0。Copyright © 2026 INNO LOTUS PTY LTD。
