[English Version](./README.md) | 中文版

# 🌳 Banyan Brain Lite

> 版本 1.0.0 — 给 AI agent 用的离线优先记忆节点，基于 NPS wire protocol，SQLite 存储。

[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](./LICENSE)
[![Version](https://img.shields.io/badge/version-1.0.0-green)]()
[![Status](https://img.shields.io/badge/status-stable-green)]()

Banyan Brain Lite 是一个事件驱动的记忆存储。Agent 可以通过 `Remember()`、`Search()`、`Update()`、`Forget()` 与它交互。它通过 [`NPS.NWP`](https://www.nuget.org/packages/LabAcacia.NPS.NWP) 暴露 [NPS-3](https://github.com/labacacia/NPS-Release) Memory Node 表面，机器身份使用本地或远程 [NIP CA](https://github.com/labacacia/nip-ca-server) 签发的 Ed25519 NID，人类管理员身份走 OLS/OIDC。

1.0.0 是 Lite 的第一个稳定版：单进程、单 SQLite 记忆库、内置 Mini-CA、Web UI、CLI、MCP Server、混合检索和 NID 鉴权都已经进入可发布状态。

---

## 1.0.0 包含什么

- **混合检索** — BM25 / FTS5 + ONNX 向量 + RRF 融合；加载 `sqlite-vec` 时走 ANN，否则降级到内存 cosine。
- **离线语义 embedding** — 可插拔 `IEmbedder`，支持 `bge-small-zh-v1.5` ONNX，也保留 hashing fallback。
- **事件驱动记忆** — 不可变 write/update/forget 日志，加 current snapshot 表用于快速读取。
- **NPS Memory Node 兼容** — `banyan serve` 通过 NWP Memory Node middleware 暴露 `/.nwm`、`/.schema`、`POST /api/memory/query`。
- **NID 鉴权** — `Authorization: NID <base64(IdentFrame)>`，支持 `anonymous-allowed`、`writes-required`、`all-required` 三种模式。
- **内置 NIP Mini-CA** — 本地签发、验证、撤销，以及 NPS-3 §8 兼容 HTTP 路由。
- **远程 CA 支持** — Lite 节点可通过 `--trusted-issuer` 和 `--ocsp-url` 验证远程 `nip-ca-server` 签发的身份。
- **管理员身份** — OLS/OIDC admin setup、login、JWT、SQLite-backed identity stores。
- **Web UI** — 记忆搜索/写入、agent 与 CA 管理、首次 admin setup、登录保护。
- **MCP Server** — `banyan mcp` 提供 stdio MCP；`banyan web` 在 `/mcp` 提供 Streamable HTTP MCP。
- **单二进制 CLI** — .NET tool 形式安装，覆盖 memory、CA、agent、embedder、web、MCP、NWP 命令。

## 快速开始

```bash
# 0. 安装 Banyan Brain Lite 1.0.0
dotnet tool install -g Banyan.Cli --version 1.0.0

# 1. 拉 embedder 模型和 sqlite-vec 扩展（约 24 MB）
banyan embedder download

# 2. 初始化内置 NID CA
export BANYAN_NIP_CA_PASSPHRASE='your-passphrase'
banyan ca init

# 3. 通过 CLI 创建 admin；也可以稍后走浏览器首次 setup
banyan init --admin-username admin --admin-password 'change-me-now'

# 4. 签发 agent 证书
banyan agent issue --id summarizer-01 --cap memory.read,memory.write \
  --key-out ~/.banyan/agents/summarizer-01.key

# 5. 启动 Web UI
export BANYAN_EMBEDDER=onnx
banyan web
# 打开 http://localhost:5180
```

作为纯 NWP Memory Node 运行，不启动 Web UI：

```bash
banyan serve --allow-anon
# GET  /.nwm
# GET  /.schema
# POST /api/memory/query
```

要求写操作必须带 NID 鉴权：

```bash
banyan web   --nid-auth writes-required
banyan serve --nid-auth writes-required
```

验证远程 CA 签发的证书，而不是使用内置 CA：

```bash
banyan web --no-ca \
  --trusted-issuer "urn:nps:ca:<ca-nid>=ed25519:<ca-pubkey>" \
  --ocsp-url http://your-ca-host:17435/ocsp
```

把 Codex 接到 Banyan 原生 Web MCP endpoint：

```bash
codex mcp add banyan-lite --url http://localhost:5180/mcp
```

## 当 agent 持久记忆用

```python
import requests

def recall(query: str, user_id: str, threshold: float = 0.50) -> list[str]:
    r = requests.get(
        "http://banyan-host:5180/api/memory/search",
        params={"q": query, "mode": "hybrid", "k": 5, "namespace": f"user-{user_id}"},
        timeout=2,
    )
    return [hit["content"] for hit in r.json()["hits"] if hit["score"] > threshold]

def remember(fact: str, user_id: str, agent_nid: str | None = None) -> None:
    requests.post(
        "http://banyan-host:5180/api/memory",
        json={"content": fact, "namespace": f"user-{user_id}", "agentNid": agent_nid},
        timeout=2,
    )
```

推荐模式：每轮 agent 对话前 recall；只有在用户明确要求“记住这个”、纠正信息、给出长期偏好或确定决策时才写入。完整指南见 [`docs/recipes/agent-memory.cn.md`](./docs/recipes/agent-memory.cn.md)，覆盖 namespace 设计、阈值、写入触发、NID attest 模式、失败恢复和反模式。

## 项目结构

```text
src/
├── Banyan.Core         # IMemoryStore、IEmbedder、请求/响应 records
├── Banyan.Lite         # SQLite memory store、BM25、vector search、RRF
├── Banyan.Embedders    # HashingEmbedder、OnnxEmbedder、EmbedderFactory
├── Banyan.Auth         # 内置 NIP CA、SQLite CA store、RemoteNipCaClient
├── Banyan.Identity     # 基于 SQLite 的 OLS/OIDC 人类身份
├── Banyan.Web          # ASP.NET Core Web UI + memory/agent/identity/CA REST APIs
├── Banyan.Mcp          # MCP tools 与 transport integration
├── Banyan.Node         # NWP Memory Node host
└── Banyan.Cli          # banyan .NET tool

tests/
├── Banyan.Core.Tests
├── Banyan.Lite.Tests
├── Banyan.Auth.Tests
├── Banyan.Identity.Tests
└── Banyan.Node.Tests
```

## 文档

| 文档 | 内容 |
|---|---|
| [`docs/release/1.0.0.cn.md`](./docs/release/1.0.0.cn.md) | Banyan Brain Lite 1.0.0 发布说明和运维检查清单 |
| [`docs/recipes/mcp-server.cn.md`](./docs/recipes/mcp-server.cn.md) | Claude Desktop / Claude Code MCP 接入 |
| [`docs/recipes/agent-memory.cn.md`](./docs/recipes/agent-memory.cn.md) | 通过 HTTP 把 agent 接到 Banyan |
| [`docs/architecture/editions.cn.md`](./docs/architecture/editions.cn.md) | Lite · Pro · Ent 版本矩阵和仓库范围 |
| [`docs/architecture/storage-tiers.cn.md`](./docs/architecture/storage-tiers.cn.md) | SQLite memory、identity、CA 存储结构 |
| [`docs/architecture/nps-mapping.cn.md`](./docs/architecture/nps-mapping.cn.md) | Banyan 如何映射到 NPS-3 NCP / NWP / NIP |
| [`docs/architecture/identity.cn.md`](./docs/architecture/identity.cn.md) | 双轨身份：机器走 NID，人走 OLS/OIDC |
| [`docs/architecture/pro-roadmap.cn.md`](./docs/architecture/pro-roadmap.cn.md) | Pro 功能范围和依赖计划 |
| [`docs/architecture/adr-001-memory-pools.cn.md`](./docs/architecture/adr-001-memory-pools.cn.md) | ADR-001 共享记忆池设计 |

## 版本边界

本仓库是 **Lite** 发行版。Lite 采用 Apache-2.0，单节点、SQLite-backed，适合本地 agent memory、小型部署、demo、嵌入式和离线工作负载。

商业版本单独维护：

- `innolotus/banyan-brain-pro` — Pro 多租户版本，包含外置 CA、tenant scope enforcement、共享记忆池。
- `innolotus/banyan-brain-ent` — 企业 AaaS L3 版本，包含 Anchor Node ingress、Vector Proxy、Bridge Node adapters、编排、审计和 quorum 能力。

## 站在巨人肩膀上

- [LabAcacia.NPS.{Core,NIP,NWP}](https://github.com/labacacia/NPS-Release) — Neural Protocol Suite 协议栈
- [labacacia/nip-ca-server](https://github.com/labacacia/nip-ca-server) — 远程 NIP CA server
- [OLS.Root.{Core,Authentication,Authorisation,Oidc}](https://github.com/orilynn-studio/ols-root) — 人类身份栈
- [Microsoft.ML.OnnxRuntime](https://onnxruntime.ai/) + Microsoft.ML.Tokenizers — ONNX 推理和 WordPiece tokenization
- [Xenova/bge-small-zh-v1.5](https://huggingface.co/Xenova/bge-small-zh-v1.5) — 多语言句向量
- [asg017/sqlite-vec](https://github.com/asg017/sqlite-vec) — SQLite vector index

## 许可证

Apache-2.0。Copyright © 2026 INNO LOTUS PTY LTD。
