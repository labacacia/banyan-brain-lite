English | [中文版](./nps-mapping.cn.md)

# NPS-3 Mapping

How Banyan plugs into the [NPS-3](https://github.com/labacacia/NPS-Release)
stack — what we consume off the shelf, what we adapt, and where we filled
gaps.

NPS-3 is split into three protocol layers:

| Layer | Purpose | NuGet | What Banyan does |
|-------|---------|-------|------------------|
| **NCP** (Neural Communication Protocol) | Frame format, codecs, registry | `LabAcacia.NPS.Core` | consume — we don't write our own framer |
| **NWP** (Neural Web Protocol) | Memory / Action / Complex node middleware, NWM manifest | `LabAcacia.NPS.NWP` | consume — `Banyan.Node` plugs into it |
| **NIP** (Neural Identity Protocol) | NID certificate authority, IdentFrame verification | `LabAcacia.NPS.NIP` | partial — service layer works, HTTP routing was empty so we filled it |

---

## Memory Node — `IMemoryNodeProvider`

NPS.NWP exposes a generic Memory Node middleware:

```
services.AddMemoryNode<TProvider>(o => { /* schema, limits, auth flag */ });
app.UseMemoryNode<TProvider>(o => { /* same */ });
```

We supply `BanyanMemoryProvider : IMemoryNodeProvider`. It does **two
things**:

1. **Translates `QueryFrame` → `SearchQuery`.**

   ```
   QueryFrame.Filter        → SearchQuery.Text / Namespace
   QueryFrame.VectorSearch  → SearchMode.Vector  (when filter has text too → Hybrid)
   QueryFrame.Limit         → SearchQuery.K
   QueryFrame.Fields        → row projection
   ```

   The NPS-supplied `NwpFilterTranslator` / `SqlQueryBuilder` only know
   PostgreSQL and SQL Server dialects, so we don't use them — the filter is
   a small JSON DSL we parse directly. Patterns we accept today:

   - `{"text": "..."}` → BM25 lexical
   - `{"text": "...", "namespace": "..."}` → namespace-scoped lexical
   - Vector search supplied via `frame.VectorSearch.Vector` (the agent embeds)
   - Hybrid when both text and vector are set

2. **Shapes rows to `MemoryNodeSchema`.**

   The schema we advertise (also at `/.schema`):

   ```
   memory_id  TEXT  PK
   namespace  TEXT
   content    TEXT
   agent_nid  TEXT?
   created_at TEXT  ISO 8601 UTC
   updated_at TEXT
   score      REAL?    BM25 / cosine / RRF
   lex_rank   INT?     1-based rank within the lexical pool
   vec_rank   INT?     1-based rank within the vector pool
   ```

NWP middleware handles the rest — frame parsing, anchor SHA-256, NPT token
metering (via `NptMeter`), `next_cursor` pagination scaffolding,
`X-NWP-*` headers, IdentFrame validation gating.

### What `banyan serve` exposes

| Path | Method | Source | Purpose |
|------|--------|--------|---------|
| `/api/memory/query`  | POST | NWP middleware              | run a `QueryFrame` against `BanyanMemoryProvider.QueryAsync` |
| `/api/memory/stream` | POST | NWP middleware              | streaming pagination via `StreamAsync` |
| `/.nwm`              | GET  | Banyan (`MemoryNodeApp`)    | published `NeuralWebManifest` |
| `/.schema`           | GET  | Banyan (`MemoryNodeApp`)    | `MemoryNodeSchema` JSON (NWP middleware claims `/api/memory/*`) |
| `/api/health`        | GET  | Banyan                      | liveness |
| `/api/agents/*`      |      | `Banyan.Web.AgentEndpoints` | demo-shape agent management (only when CA is loaded) |
| `/api/ca`            | GET  | `Banyan.Web.CaEndpoints`    | CA info (only when CA is loaded) |
| `/v1/agents/*`<br/>`/v1/nodes/register`<br/>`/v1/ca/cert`<br/>`/v1/crl`<br/>`/.well-known/nps-ca`<br/>`/health` | various | `Banyan.Web.NipCaEndpoints` | **NPS-3 §8 conformant CA HTTP API** (only when CA is loaded) |

---

## NID identity — `EmbeddedNipCa` / `RemoteNipCaClient`

NPS.NIP supplies the **service** layer:

```
NipCaService(NipCaOptions, INipCaStore, NipKeyManager)
  .RegisterAsync(entityType, identifier, pubKey, capabilities, scopeJson, metadataJson)
  .RenewAsync(nid)
  .RevokeAsync(nid, reason)
  .VerifyAsync(nid)
  .GetCrlAsync()
  .GetCaPublicKey()
  .BuildNid(entityType, identifier)
```

…and the verifier:

```
NipIdentVerifier(NipVerifierOptions, IHttpClientFactory, ILogger)
  .VerifyAsync(IdentFrame frame, NipVerifyContext)
```

What it **doesn't** ship is HTTP routing. `app.MapNipCa()` registers
zero endpoints in alpha.4 and alpha.5 (we [confirmed via spike](#spikes)
on both versions). The Go reference implementation under
[`labacacia/nip-ca-server/example/go`](https://github.com/labacacia/nip-ca-server)
defines the full path layout per NPS-3 §8.

### What Banyan provides

`Banyan.Web/Endpoints/NipCaEndpoints.cs` mounts the missing routes,
calling into the upstream `NipCaService` for issuance / verification:

| Path | Method | Maps to | Notes |
|------|--------|---------|-------|
| `POST /v1/agents/register` | `NipCaService.RegisterAsync("agent", …)` | issues an Ed25519 IdentFrame |
| `POST /v1/nodes/register`  | `NipCaService.RegisterAsync("node", …)`  | issues a node IdentFrame |
| `POST /v1/agents/{nid}/renew`  | `NipCaService.RenewAsync(nid)` | requires the cert to be inside `RenewalWindowDays` |
| `POST /v1/agents/{nid}/revoke` | `NipCaService.RevokeAsync(nid, reason)` | emits a `RevokeFrame` |
| `GET  /v1/agents/{nid}/verify` | `NipCaService.VerifyAsync(nid)` | also covers OCSP-shaped checks |
| `GET  /v1/ca/cert`             | `NipCaService.GetCaPublicKey()` | discovery: NID + display name + pubkey |
| `GET  /v1/crl`                 | `EmbeddedNipCa.ListAsync(revokedOnly:true)` | revoked list |
| `GET  /.well-known/nps-ca`     | discovery payload | algorithms, endpoints, max validity |
| `GET  /health`                 | liveness |

Endpoints mount on **both** `banyan web` (`Banyan.Web/WebApp`) and
`banyan serve` (`Banyan.Node/MemoryNodeApp`) when an `EmbeddedNipCa` is
loaded — so a Memory Node can **also** be a CA, or you can split the roles
across hosts.

### Client side

`RemoteNipCaClient` (in `Banyan.Auth`) is a thin `HttpClient` wrapper that
mirrors the NPS spec verbatim. Same surface as `EmbeddedNipCa` so calling
code is uniform; CLI selects between them via `--remote URL` (or env
`BANYAN_CA_URL`).

When NPS.NIP eventually ships routing in `app.MapNipCa()`, our endpoint
class becomes redundant and the CLI can switch over without touching
`RemoteNipCaClient`.

---

## Wire formats

Frames Banyan currently produces or consumes:

| Frame                             | Producer                              | Consumer                                   |
|-----------------------------------|---------------------------------------|--------------------------------------------|
| `NCP.HelloFrame`                  | NWP middleware                        | NWP client                                 |
| `NWP.QueryFrame`                  | NWP client                            | `BanyanMemoryProvider`                     |
| `NWP.VectorSearchOptions`         | NWP client                            | `BanyanMemoryProvider`                     |
| `NIP.IdentFrame`                  | `NipCaService.RegisterAsync`          | `NipIdentVerifier` on the Memory Node      |
| `NIP.RevokeFrame`                 | `NipCaService.RevokeAsync`            | clients distributing CRLs                  |
| `NIP.TrustFrame` (cross-CA trust) | not produced yet                      | n/a                                        |

Encoding tiers (from `NPS.Core.Frames.EncodingTier`): we currently honour
**Tier1 (JSON)** end-to-end — manifests, query bodies, IdentFrame, CRL.
Tier2 (MsgPack) and AnchorFrame are unused so far; they're available via
`NPS.Core.Codecs.Tier2MsgPackCodec` when wire size becomes a concern.

---

## Spikes

Two reproducible probes that informed the design above:

1. **alpha.4 routing spike.** Reference: `nip-ca-server` ships at
   `LabAcacia.NPS.NIP` 1.0.0-alpha.4. We ran `services.AddNipCa(...)` +
   `app.MapNipCa()` against the same package and dumped
   `EndpointDataSource.Endpoints` — count was **0**. (`NipKeyManager` did
   load the key successfully, so the DI bind half works.)
2. **alpha.5 routing spike.** Re-ran with `LabAcacia.NPS.NIP` 1.0.0-alpha.5
   and the same DI path. Same result: **0 endpoints**.

These spikes are why Banyan ships `Banyan.Web/Endpoints/NipCaEndpoints.cs`
rather than waiting on the upstream package. The endpoint shapes are taken
verbatim from the working Go reference at
`labacacia/nip-ca-server/example/go/api/api.go`, so cross-language clients
(Go, Java, Rust, Python — all listed in the reference repo) can talk to a
Banyan-hosted CA out of the box.

## What's not mapped yet

- **NCP `AnchorFrame`** for memory anchoring — the NWP query response
  already carries `anchor_ref: sha256:...`, but we don't produce or store
  AnchorFrames separately.
- **`ActionNode` and `ComplexNode`** middlewares — NPS.NWP supplies them
  (we saw the types in reflection), but Banyan only plays the Memory Node
  role today.
- **MsgPack Tier2 codec.** Everything is JSON / Tier1 right now; switching
  to Tier2 is a per-route content-type negotiation problem we'll tackle
  when payload sizes start hurting.
- **NPS-RFC-0002 dual-trust X.509 register** — the v2 endpoints
  (`POST /v2/{agents,nodes}/register`) emit both an Ed25519 frame and a
  2-cert X.509 chain. NPS.NIP alpha.5 shipped the `NPS.NIP.X509` types but
  Banyan doesn't expose v2 routes yet.
