English | [中文版](./README.cn.md)

# 🌳 Banyan

> A memory node for AI agents — speaks the [NPS](https://github.com/labacacia/NPS-Release)
> wire protocol, stores everything in SQLite, runs entirely offline.

[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](./LICENSE)
[![Status](https://img.shields.io/badge/status-alpha-orange)]()

Banyan is an event-sourced memory store that agents can `Remember()`,
`Search()`, `Update()` and `Forget()` against. The wire protocol is
[NPS-3](https://github.com/labacacia/NPS-Release) — Neural Web Protocol
Memory-Node middleware via [`NPS.NWP`](https://www.nuget.org/packages/LabAcacia.NPS.NWP),
identity via Ed25519 NIDs from the [NIP CA](https://github.com/labacacia/nip-ca-server),
and a parallel OIDC track for human operators on top of
[OLS](https://www.nuget.org/packages/OLS.Root.Core).

---

## Features

- **Hybrid retrieval** — BM25 (FTS5) + ONNX vector search + RRF fusion. Vector index
  uses [sqlite-vec](https://github.com/asg017/sqlite-vec) ANN when available, falls
  back to in-memory cosine.
- **Real semantic embeddings** — pluggable `IEmbedder`; ships with
  `bge-small-zh-v1.5` (multilingual, 22 MB INT8 ONNX, 384-dim) plus an offline
  hashing fallback.
- **Dual-track identity**
  - **Agents / Memory Nodes**: Ed25519 NID certificates issued by an embedded
    `NipCaService` or a remote NPS-CA. Banyan ships the NPS-3 §8 conformant
    HTTP routes that the `NPS.NIP` NuGet hasn't shipped yet.
  - **Operators / Admins**: OIDC + JWT via OLS, with a SQLite-backed implementation
    of every Identity / OAuth store interface.
- **Event-sourced memory** — every `Write/Update/Forget` appends to an immutable
  log; the latest snapshot lives in `memories_current`; trace stays auditable
  even after forget.
- **Standards-compliant Memory Node** — `banyan serve` mounts
  `app.UseMemoryNode<TProvider>`, exposes `/.nwm` (`NeuralWebManifest`),
  `/.schema`, `POST /api/memory/query` (NWP frames with `anchor_ref`,
  `token_est`, etc.).
- **Demo Web UI** — neon-glass, particle-network background, three-tab SPA
  (Memory · Agents · About) with live BM25 / Vector / Hybrid mode toggle and
  semantic search across English + 中文 corpora.
- **Single-binary CLI** — `dotnet tool install -g Banyan.Cli` ships the entire
  surface (`keygen`, `init`, `login`, `ca init`, `agent issue/verify/revoke`,
  `embedder download`, `web`, `serve`).

## Quick Start

```bash
# 0. Install
dotnet tool install -g Banyan.Cli

# 1. Pull the embedder model + sqlite-vec extension (~24 MB)
banyan embedder download

# 2. Bootstrap the human-side identity DB (interactive admin user)
banyan keygen
banyan init

# 3. Bootstrap the agent-side NID CA
export BANYAN_NIP_CA_PASSPHRASE='your-passphrase'
banyan ca init

# 4. Issue an agent NID locally
banyan agent issue --id summarizer-01 --cap memory.read,memory.write \
  --key-out ~/.banyan/agents/summarizer-01.key

# 5a. Try the demo web UI (memory + identity + CA panels)
export BANYAN_EMBEDDER=onnx
banyan web
# → open http://localhost:5180

# 5b. Or run a real Memory Node listening on NWP
banyan serve --allow-anon
# → POST /api/memory/query with QueryFrame body
# → GET  /.nwm for the NeuralWebManifest

# 6. From another host: issue / verify / revoke against a remote CA
export BANYAN_CA_URL=https://your-ca-host:5180
banyan agent issue --id offsite-agent --cap memory.read --remote $BANYAN_CA_URL
banyan agent verify urn:nps:agent:.../offsite-agent --remote $BANYAN_CA_URL
```

## Use as agent memory

If you're an agent author plugging Banyan into Claude / GPT / your own assistant:

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

Recall before every turn, write only on explicit signals (the user says
*"remember X"*, corrects you, or pins a decision). Full integration guide in
[`docs/recipes/agent-memory.md`](./docs/recipes/agent-memory.md) — covers
namespace design, threshold heuristics, write triggers, NID-attested mode,
failure recovery, anti-patterns.

## Project Structure

```
src/
├── Banyan.Core         # IMemoryStore, IEmbedder, request/response records
├── Banyan.Lite         # SqliteMemoryStore (BM25 + cosine + RRF + sqlite-vec ANN)
├── Banyan.Embedders    # HashingEmbedder, OnnxEmbedder, EmbedderFactory
├── Banyan.Auth         # NID CA: EmbeddedNipCa, SqliteNipCaStore, RemoteNipCaClient
├── Banyan.Identity     # OLS-backed human identity (OIDC, JWT, RBAC) on SQLite
├── Banyan.Web          # ASP.NET Core demo UI + agents/memory/identity REST,
│                         + NPS-3 §8 NIP CA HTTP routes (the .NET-side gap-fill)
├── Banyan.Node         # banyan serve — NPS.NWP MemoryNodeMiddleware host
└── Banyan.Cli          # `banyan` dotnet tool

tests/
├── Banyan.Core.Tests       (5)
├── Banyan.Lite.Tests       (42, incl. 6 ONNX + 5 sqlite-vec)
├── Banyan.Auth.Tests       (27, incl. 7 RemoteNipCaClient round-trips)
├── Banyan.Identity.Tests   (43)
└── Banyan.Node.Tests       (8)
```

## Documentation

| Document | Description |
|---|---|
| [`docs/recipes/agent-memory.md`](./docs/recipes/agent-memory.md) | **Recipe**: connecting an agent (Claude / GPT / custom) to Banyan as persistent memory |
| [`docs/architecture/editions.md`](./docs/architecture/editions.md) | Lite · Pro · Ent tier matrix — NPS compliance + topology, scope of this repo |
| [`docs/architecture/storage-tiers.md`](./docs/architecture/storage-tiers.md) | Memory / identity / CA SQLite schemas, event log, FTS5, vector layout |
| [`docs/architecture/nps-mapping.md`](./docs/architecture/nps-mapping.md) | How Banyan maps to NPS-3 (NCP / NWP / NIP) — what we consume, what we fill in |
| [`docs/architecture/identity.md`](./docs/architecture/identity.md) | Dual-track identity model: NID for machines, OLS / OIDC for humans |
| [`docs/architecture/ols-surface-reference.md`](./docs/architecture/ols-surface-reference.md) | Reflected `OLS.Root.*` API surface (informational) |

## Built On

- [LabAcacia.NPS.{Core,NIP,NWP}](https://github.com/labacacia/nps) — Neural Web Protocol stack
- [labacacia/nip-ca-server](https://github.com/labacacia/nip-ca-server) — reference NIP CA HTTP API spec
- [OLS.Root.{Core,Authentication,Authorisation,Oidc}](https://github.com/orilynn/ols-root) — human-side identity stack
- [Microsoft.ML.OnnxRuntime](https://onnxruntime.ai/) + [Microsoft.ML.Tokenizers](https://www.nuget.org/packages/Microsoft.ML.Tokenizers) — ONNX inference + BERT WordPiece
- [Xenova/bge-small-zh-v1.5](https://huggingface.co/Xenova/bge-small-zh-v1.5) — multilingual sentence embeddings
- [asg017/sqlite-vec](https://github.com/asg017/sqlite-vec) — SQLite ANN vector index

## License

Apache-2.0. Copyright © 2026 INNO LOTUS PTY LTD.
