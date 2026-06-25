English | [中文版](./README.cn.md)

# 🌳 Banyan Brain Lite

> Version 1.1.0 — an offline-first memory node for AI agents, built on the NPS wire protocol and backed by SQLite.

[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](./LICENSE)
[![Version](https://img.shields.io/badge/version-1.1.0-green)]()
[![Status](https://img.shields.io/badge/status-stable-green)]()

Banyan Brain Lite is an event-sourced memory store that agents can `Remember()`, `Search()`, `Update()`, and `Forget()` against. It speaks the [NPS-3](https://github.com/labacacia/NPS-Release) Memory Node surface through [`NPS.NWP`](https://www.nuget.org/packages/LabAcacia.NPS.NWP), uses Ed25519 NIDs issued by a local or remote [NIP CA](https://github.com/labacacia/nip-ca-server), and provides an OLS/OIDC operator identity track for human administrators.

The 1.1.0 release is the post-Wave GA Lite cut: one process, one SQLite-backed memory store, embedded Mini-CA, web UI, CLI, MCP server, hybrid retrieval, NID authentication, observability, and signed knowledge packs.

---

## What ships in 1.1.0

- **Hybrid retrieval** — BM25 / FTS5 + ONNX vector search + RRF fusion. When `sqlite-vec` is available, vector search uses ANN; otherwise it falls back to in-memory cosine.
- **Offline semantic embeddings** — pluggable `IEmbedder`, `bge-small-zh-v1.5` ONNX support, and a hashing fallback for fully offline operation.
- **Event-sourced memory** — immutable write/update/forget log plus a current snapshot table for fast reads.
- **NPS Memory Node compatibility** — `banyan serve` exposes `/.nwm`, `/.schema`, and `POST /api/memory/query` through the NWP Memory Node middleware.
- **NID authentication** — `Authorization: NID <base64(IdentFrame)>`, with `anonymous-allowed`, `writes-required`, and `all-required` modes.
- **Embedded NIP Mini-CA** — local certificate issuance, verification, revocation, and NPS-3 §8 compatible HTTP routes.
- **Remote CA support** — a Lite node can verify identities issued by a remote `nip-ca-server` using `--trusted-issuer` and `--ocsp-url`.
- **Operator identity** — OLS/OIDC-backed admin setup, login, JWT, and SQLite-backed identity stores.
- **Web UI** — memory search/write UI, agent and CA operations, first-run admin setup, and login enforcement.
- **MCP server** — stdio MCP via `banyan mcp` and Streamable HTTP MCP at `/mcp` when running `banyan web`.
- **Knowledge packs** — `.banyanpack` v2 signing, mount trust through the NID/CA chain, in-pack vector recall, and pack version pin / upgrade / rollback.
- **Observability and audit** — Lite OpenTelemetry wiring, memory-operation metrics, and tamper-evident local audit records.
- **Single-binary CLI** — installable as a .NET tool with memory, CA, agent, embedder, web, MCP, and NWP commands.

## Quick start

```bash
# 0. Install Banyan Brain Lite 1.1.0
dotnet tool install -g Banyan.Cli --version 1.1.0

# 1. Pull the embedder model and sqlite-vec extension (~24 MB)
banyan embedder download

# 2. Bootstrap the embedded NID CA
export BANYAN_NIP_CA_PASSPHRASE='your-passphrase'
banyan ca init

# 3. Create an admin account from CLI, or use the browser setup flow later
banyan init --admin-username admin --admin-password 'change-me-now'

# 4. Issue an agent certificate
banyan agent issue --id summarizer-01 --cap memory.read,memory.write \
  --key-out ~/.banyan/agents/summarizer-01.key

# 5. Start the Web UI
export BANYAN_EMBEDDER=onnx
banyan web
# Open http://localhost:5180
```

To run as a pure NWP Memory Node without the web UI:

```bash
banyan serve --allow-anon
# GET  /.nwm
# GET  /.schema
# POST /api/memory/query
```

To require NID authentication for writes:

```bash
banyan web   --nid-auth writes-required
banyan serve --nid-auth writes-required
```

To verify certificates from a remote CA instead of using the embedded CA:

```bash
banyan web --no-ca \
  --trusted-issuer "urn:nps:ca:<ca-nid>=ed25519:<ca-pubkey>" \
  --ocsp-url http://your-ca-host:17435/ocsp
```

To connect Codex to Banyan's native Web MCP endpoint:

```bash
codex mcp add banyan-lite --url http://localhost:5180/mcp
```

## Use as agent memory

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

Recommended pattern: recall before each agent turn, and write only on explicit signals such as "remember this", user corrections, durable preferences, or decisions. See [`docs/recipes/agent-memory.md`](./docs/recipes/agent-memory.md) for namespace design, thresholds, write triggers, NID-attested mode, failure recovery, and anti-patterns.

## Project structure

```text
src/
├── Banyan.Core         # IMemoryStore, IEmbedder, request/response records
├── Banyan.Lite         # SQLite memory store, BM25, vector search, RRF
├── Banyan.Embedders    # HashingEmbedder, OnnxEmbedder, EmbedderFactory
├── Banyan.Auth         # Embedded NIP CA, SQLite CA store, RemoteNipCaClient
├── Banyan.Identity     # OLS/OIDC human identity on SQLite
├── Banyan.Web          # ASP.NET Core Web UI + memory/agent/identity/CA REST APIs
├── Banyan.Mcp          # MCP tools and transport integration
├── Banyan.Node         # NWP Memory Node host
└── Banyan.Cli          # banyan .NET tool

tests/
├── Banyan.Core.Tests
├── Banyan.Lite.Tests
├── Banyan.Auth.Tests
├── Banyan.Identity.Tests
└── Banyan.Node.Tests
```

## Documentation

| Document | Description |
|---|---|
| [`docs/release/1.1.0.md`](./docs/release/1.1.0.md) | Release notes and operational checklist for Banyan Brain Lite 1.1.0 |
| [`docs/release/1.0.0.md`](./docs/release/1.0.0.md) | Historical release notes for Banyan Brain Lite 1.0.0 |
| [`docs/client-integration-profile.md`](./docs/client-integration-profile.md) | Portable client profile for switching between Lite, Pro, and Ent |
| [`docs/recipes/mcp-server.md`](./docs/recipes/mcp-server.md) | Claude Desktop / Claude Code MCP integration |
| [`docs/recipes/agent-memory.md`](./docs/recipes/agent-memory.md) | Connecting an agent to Banyan through HTTP |
| [`docs/architecture/editions.md`](./docs/architecture/editions.md) | Public Lite edition boundary |
| [`docs/architecture/shared-memory-pools.md`](./docs/architecture/shared-memory-pools.md) | Planned local shared memory pool levels for Lite |
| [`docs/architecture/storage-tiers.md`](./docs/architecture/storage-tiers.md) | SQLite memory, identity, and CA storage layout |
| [`docs/architecture/nps-mapping.md`](./docs/architecture/nps-mapping.md) | How Banyan maps to NPS-3 NCP / NWP / NIP |
| [`docs/architecture/identity.md`](./docs/architecture/identity.md) | Dual-track identity: NID for machines, OLS/OIDC for humans |

## Edition boundary

This repository is the **Lite** distribution. Lite is Apache-2.0, single-node, SQLite-backed, and suitable for local agent memory, small deployments, demos, and embedded/offline workloads.

Commercial editions and enterprise deployment options are maintained separately. For commercial licensing or enterprise deployment, contact INNO LOTUS PTY LTD.

## Built on

- [LabAcacia.NPS.{Core,NIP,NWP}](https://github.com/labacacia/NPS-Release) — Neural Protocol Suite stack
- [labacacia/nip-ca-server](https://github.com/labacacia/nip-ca-server) — remote NIP CA server
- [OLS.Root.{Core,Authentication,Authorisation,Oidc}](https://github.com/orilynn-studio/ols-root) — human-side identity stack
- [Microsoft.ML.OnnxRuntime](https://onnxruntime.ai/) + Microsoft.ML.Tokenizers — ONNX inference and WordPiece tokenization
- [Xenova/bge-small-zh-v1.5](https://huggingface.co/Xenova/bge-small-zh-v1.5) — multilingual sentence embeddings
- [asg017/sqlite-vec](https://github.com/asg017/sqlite-vec) — SQLite vector index

## License

Apache-2.0. Copyright © 2026 INNO LOTUS PTY LTD.
