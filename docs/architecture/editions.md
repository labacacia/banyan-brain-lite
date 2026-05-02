English | [中文版](./editions.cn.md)

# Editions: Lite · Pro · Ent

Banyan ships in three tiers, distinguished by NPS compliance level and the
deployment topology each one supports. Each tier is a separate distribution
(separate Git repo + NuGet stream), but the source tree under
`innolotus-banyan-main/` is the upstream from which all three are cut.

| Tier | NPS Compliance | Role |
|------|----------------|------|
| **Lite** | NWP Memory Node (minimum set) + **embedded self-signed NIP Mini-CA** | Single process, single namespace, L0 anonymous |
| **Pro** | NWP Memory Node + **external `nip-ca-server`** | Multi-tenant (NID scope isolation), L1 attested |
| **Ent** | **AaaS Level 3**: Anchor Node ingress + many Memory Nodes (with Vector Proxy) + Bridge Node (legacy adapter) + NOP orchestration + audit log + K-of-N quorum | L2 verified |

The current public repository — [`labacacia/banyan-brain-lite`](https://github.com/labacacia/banyan-brain-lite) —
implements the **Lite** tier. Pro and Ent live in
[`innolotus/banyan-brain-pro`](https://github.com/innolotus/banyan-brain-pro) and
[`innolotus/banyan-brain-ent`](https://github.com/innolotus/banyan-brain-ent).

---

## What Lite gives you

- **One `banyan` binary** speaks NWP Memory Node + hosts an in-process NIP Mini-CA
- SQLite storage for everything (memory, identity, NID ledger)
- Single namespace (the `default` namespace; the column exists for forward
  compatibility with Pro multi-tenancy but Lite doesn't enforce isolation)
- L0 anonymous access via `banyan serve --allow-anon`
- L1 attested (the default — `RequireAuth=true` on the NWP middleware) is
  available too, but the Mini-CA is the only trust root, so it's
  effectively single-tenant attestation
- Demo web UI at `banyan web`

NPS-protocol features Lite **does** ship:

- ✅ `NPS.NWP.MemoryNode.IMemoryNodeProvider` — `BanyanMemoryProvider`
- ✅ NWP `QueryFrame` / `VectorSearchOptions` / `MemoryNodeSchema`
- ✅ `NeuralWebManifest` at `/.nwm`
- ✅ `NPS.Core.Frames.Ncp.AnchorFrame` digest in query responses (`anchor_ref: sha256:...`)
- ✅ `NptMeter` token estimation (`token_est` header + body field)
- ✅ NIP Mini-CA: `EmbeddedNipCa` + `SqliteNipCaStore` + NPS-3 §8 conformant
      HTTP routes (`/v1/agents/...`, `/v1/ca/cert`, `/.well-known/nps-ca`)
- ✅ `RemoteNipCaClient` — usable as a client even in Lite (so a Lite agent
      *can* be pointed at a remote `nip-ca-server` if you want to share trust
      roots, even though Lite doesn't run one itself)

NPS-protocol features Lite **doesn't** ship (these are Pro / Ent territory):

- ❌ External `nip-ca-server` deployment (Pro)
- ❌ Multi-tenant NID-scope isolation per request (Pro)
- ❌ L2 verified — Anchor Node ingress, NOP orchestration, K-of-N quorum (Ent)
- ❌ Bridge Node legacy protocol adapters (Ent)
- ❌ `NPS.NWP.ActionNode` / `ComplexNode` (we only host MemoryNode)
- ❌ NPS-RFC-0002 v2 X.509 dual-trust register (`/v2/agents/register`)

## How Lite differs from Pro

| Concern | Lite | Pro |
|---------|------|-----|
| NIP CA | `EmbeddedNipCa` in-process | External `nip-ca-server` (Docker, Postgres-backed) |
| Trust root | One self-signed Ed25519 keypair | One or more remote CAs in `TrustedIssuers` |
| Tenancy | Single `default` namespace | Per-tenant namespace + scope check on every IdentFrame |
| Assurance | L0 anon (opt-in) or L1 attested (default) | L1 attested mandatory; scope check enforced |
| Memory Node count | 1 | N (each holds a tenant's slice) |
| Postgres | not used | NIP CA storage + optional shared memory store |

## How Lite differs from Ent

Ent is **Agentic-as-a-Service Level 3**. On top of everything Pro has:

- **Anchor Node** as the ingress edge; clients submit AnchorFrames there and
  the Anchor Node fans out to the right Memory Nodes
- **Vector Proxy** in front of each Memory Node — query splitting, caching,
  re-ranking
- **Bridge Node** — protocol adapters that let non-NPS clients (legacy REST,
  gRPC, etc.) round-trip through NPS
- **NOP orchestration** — Neural Orchestration Protocol, scheduling
  multi-step agent workflows across Action / Complex / Memory nodes
- **Audit log** with cryptographic chain (each entry signed by the node
  that produced it; the chain is verifiable)
- **K-of-N quorum** for high-stakes ops (issuance / revocation requires
  K signatures from N CA instances)
- **L2 verified** — every IdentFrame is also re-verified by the Anchor Node
  using a fresh OCSP-style probe before forwarding

## Where the current source tree sits relative to Lite

The `innolotus-banyan-main/` working copy implements **everything in Lite**
plus a small forward-compatible surface that Pro will need:

- `RemoteNipCaClient` — already a Pro consumer dependency, harmless in Lite
  (the CLI just won't have anyone to point it at by default)
- `Banyan.Web.NipCaEndpoints` — exposes the embedded Mini-CA's service over
  the standard NPS-3 §8 HTTP routes. In Lite this is "your single embedded
  CA also has the public HTTP shape"; in Pro this same code becomes the
  template for `nip-ca-server` (Postgres-backed) once we cut that repo

So when we cut Pro, Lite stays exactly as-is and Pro layers on:

1. Replace `EmbeddedNipCa` with a `RemoteNipCaClient` connecting to a
   standalone `nip-ca-server` Docker
2. Add a `TenantContext` middleware that reads the NID issuer + scope from
   `IdentFrame` and applies it to `MemoryNodeOptions.Schema` queries (filter
   on `namespace`)
3. Add `NPS.NWP.ActionNode` middleware for tenant onboarding actions
4. Switch the demo / Web UI from "single-org" to a tenant chooser
5. Add **shared memory pools** — see [ADR-001](./adr-001-memory-pools.md)
   for the design (NID-ACL'd containers, system pool 0, cross-pool merge
   search)

Full Pro phase plan with cost estimates and dependency graph:
[`pro-roadmap.md`](./pro-roadmap.md).

And Ent layers on top of Pro:

5. Anchor Node ingress (probably a separate `Banyan.Anchor` project)
6. Vector Proxy (`Banyan.Embedders` already isolates the embedder
   contract — proxy just intercepts at this seam)
7. Bridge Node legacy adapters
8. NOP scheduler + audit log + K-of-N CA quorum

## Repo strategy

- This source tree (`innolotus-banyan-main/`) is the **upstream**
- `labacacia/banyan-brain-lite` is the **public Apache-2.0 release** —
  cuts at the Lite boundary above
- `innolotus/banyan-brain-pro` is the **commercial Pro tier** — adds
  multi-tenancy, external CA, scope enforcement
- `innolotus/banyan-brain-ent` is the **commercial Ent tier** — adds
  AaaS L3 components

The same Git work tree feeds all three by managing tier-specific files via
distinct branches and remotes; the per-tier branch retains only the projects
in scope. (We're not split-yet; this doc describes the target.)
