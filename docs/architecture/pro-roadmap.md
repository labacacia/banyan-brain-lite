English | [中文版](./pro-roadmap.cn.md)

# Pro Tier Roadmap

This document scopes what `innolotus/banyan-brain-pro` adds on top of
Lite. It is the planning counterpart to
[`editions.md`](./editions.md) — that doc draws the high-level Lite /
Pro / Ent matrix; this one breaks Pro into shippable phases and pins
each phase to the design docs that govern it.

> **Status**: Pro tier has not been cut from upstream yet. This is a
> design-time roadmap, not a release schedule. Each phase below is
> sized in days-of-work, not calendar dates.

---

## Tier boundary recap

Lite is **single-org, single-process, embedded Mini-CA, namespace-as-tag**.
Pro is **multi-tenant, externalised CA, NID-scoped isolation, shareable
pools**. Ent is **AaaS L3** — Anchor Node + Vector Proxy + Bridge Node +
NOP scheduler + audit chain + K-of-N quorum.

The clean way to think about it: Lite optimises for "one agent, one
operator, one box." Pro adds the primitives a multi-team operator needs
to host *several* organisations on *one* Banyan deployment without them
seeing each other's data. Ent adds the federation, audit, and
quorum-signed governance that regulated environments need on top.

---

## Phase plan

Each phase is independently mergeable behind the next one. Order is
dependency-driven — earlier phases unlock later ones.

### P-Pro.1 — External `nip-ca-server` integration

**Goal**: drop the embedded Mini-CA from the default boot path; trust
roots come from one or more remote `nip-ca-server` deployments.

**In scope**:

- Switch `MemoryNodeApp` / `WebApp` defaults to `RemoteNipCaClient`
  instead of `EmbeddedNipCa`. `EmbeddedNipCa` stays available for dev
  loops + Lite, just no longer the default Pro wiring.
- Configurable `TrustedIssuers` list pulled from the CA's
  `/.well-known/nps-ca` discovery doc.
- Periodic CRL refresh (`/v1/crl`) into the local
  `LocalRevokedSerials` set — so Pro doesn't need to round-trip OCSP
  on every verify.
- Ops doc on running `nip-ca-server` in Docker against Postgres.

**Out of scope**: changes to how `NidAuthenticationMiddleware` consults
revocation. The middleware already supports either an embedded CA or a
local revoked-serials set; Pro just tilts toward the latter.

**Cost**: ~1 day. Mostly wiring + a CRL refresh worker + docs.

**Depends on**: nothing (Lite already has `RemoteNipCaClient` from
`feat/p3.1-remote-ca`).

### P-Pro.2 — Shared memory pools

**Goal**: implement [ADR-001](./adr-001-memory-pools.md) — pools as
NID-ACL'd containers, system pool 0 with operator-pinned writers,
cross-pool merge search.

**In scope**:

- `memory.db` migration: `memory_pools`, `pool_acl`, `pool_id` columns
  on `memory_events` / `memories_current` / `embeddings`.
- `IPoolAuthorizer` service injected into `MemoryEndpoints` for ACL
  checks before every read/write.
- Pool CRUD endpoints + grant/revoke endpoints.
- `&pools=...` parameter on `/api/memory/search`; server-side merge.
- CLI: `banyan pool create/list/show/grant/revoke/delete`.
- System pool 0 seed loader + writer-list config.
- Web UI: a "Pools" tab next to Memory / Agents / About showing pools
  the logged-in identity can see, with a grant editor for owners.

**Out of scope**: cross-instance pool federation (Ent), per-pool quotas
(probably Ent), audit chain on grant changes (Ent).

**Cost**: ~2-3 days. The schema migration + ACL plumbing is the bulk;
search merge + CLI follow.

**Depends on**: P-Pro.1 (so the writer NIDs are issued by the external
CA the Pro instance trusts).

### P-Pro.3 — Tenant-scope middleware

**Goal**: turn the verified NID's `scope.tenant` claim into an enforced
filter. A NID issued under tenant `acme` only ever sees pools and
memories belonging to that tenant.

**In scope**:

- New middleware downstream of `NidAuthenticationMiddleware`: extracts
  `tenant_id` from the verified IdentFrame's scope, stamps it onto
  `HttpContext.Items["banyan.tenant_id"]`, and refuses requests whose
  target pool / memory crosses tenants.
- `memory_pools.tenant_id` column (nullable for the global system pool 0).
- Cross-tenant admin override: a `tenant_admin` capability lets an
  operator NID see across tenants for triage.
- Tenant claims schema added to the IdentFrame issuance flow on the
  CA side (P-Pro.1's `nip-ca-server` deploy doc covers how to set
  `scope.tenant` per agent).

**Out of scope**: per-tenant *resource quotas*. Tenant separation is
about visibility, not capacity; quotas are Ent.

**Cost**: ~1-2 days. Middleware + a column + a handful of guard
clauses; tests are the long pole.

**Depends on**: P-Pro.1 (the CA must issue tenant-scoped frames),
P-Pro.2 (pools are the unit being scoped).

### P-Pro.4 — Postgres backing (optional path)

**Goal**: lift `nipca.db` and (optionally) `memory.db` to Postgres for
deployments that outgrow single-host SQLite.

**In scope**:

- `Banyan.Auth.Stores.PostgresNipCaStore` — same `INipCaStore` interface
  as the SQLite one, talks to the same `nip-ca-server` Postgres schema
  so the on-disk format stays compatible.
- Optional `Banyan.Lite.Postgres` — `IMemoryStore` over Postgres + pgvector.
  This is genuinely optional; many Pro deployments will be happy with
  SQLite per Memory Node.
- Connection-string config in `BanyanNodeOptions`; SQLite remains the
  default if no Postgres URL is set.

**Out of scope**: cross-node memory replication (still single-writer
per Memory Node — that's an Ent concern).

**Cost**: ~3-4 days for `nipca.db` in Postgres alone; double that if we
also do `memory.db` (FTS5 → Postgres FTS or Tantivy, vector → pgvector).
Realistically Pro v1 ships only `nipca.db`-on-Postgres and leaves
`memory.db` on SQLite.

**Depends on**: P-Pro.1.

### P-Pro.5 — NWP ActionNode + admin endpoints

**Goal**: expose tenant lifecycle (`create_tenant`, `add_writer`,
`rotate_key`) as NWP `ActionFrame`s rather than ad-hoc REST. This is
what makes the operator experience NPS-native rather than Banyan-bespoke.

**In scope**:

- Mount `app.UseActionNode<BanyanAdminProvider>` at `/api/admin`.
- `BanyanAdminProvider` exposes a small action set for tenant ops,
  pool ops, and CA ops.
- Frames are signed by an operator NID with the `tenant_admin` capability.
- Admin web UI re-points at the ActionNode (replacing the current
  `IdentityEndpoints` admin surface).

**Out of scope**: NOP — multi-step orchestration is Ent.

**Cost**: ~2 days.

**Depends on**: P-Pro.3 (tenant scope must already exist for actions
to operate on it).

### P-Pro.6 — Tenant-aware web UI

**Goal**: the demo UI today is single-org. Pro's UI lets an operator
NID switch tenants, see per-tenant memory / pool / agent counts, and
drill into a tenant's data without leaving the page.

**In scope**:

- Tenant switcher in the header (visible only to NIDs with
  `tenant_admin`).
- Per-tenant filter on the Memory / Agents / Pools tabs.
- "Onboard tenant" wizard that calls the ActionNode from P-Pro.5.

**Out of scope**: anything that isn't a thin client over the API
surface above.

**Cost**: ~2 days.

**Depends on**: P-Pro.5.

---

## Cross-cutting work (touches every phase)

- **Tests**: the same testing posture Lite uses — real WebApplication
  on ephemeral port, real CA, real frames. Add a `Banyan.Pro.Tests`
  project, mirror the Lite test layout. Target ≥80% line coverage on
  new code, with the auth + ACL paths at 100%.
- **Docs**: every phase ships an EN + CN doc. P-Pro.2 lands ADR-001
  (already drafted); each subsequent phase adds either an ADR or an
  expanded section in `editions.md` / `nps-mapping.md`.
- **Migration safety**: every schema change ships an idempotent
  migration script + a `banyan migrate --dry-run` CLI option.
- **Backwards compatibility**: a Lite `memory.db` opens cleanly in Pro
  (everything lands in pool 0 + the default tenant). The reverse — Pro
  → Lite — is not supported; Pro DBs reference tables Lite doesn't have.

---

## Out of Pro entirely (= Ent)

These features stay reserved for `innolotus/banyan-brain-ent`:

- **Anchor Node ingress** with fan-out to Memory Nodes
- **Vector Proxy** in front of each Memory Node (query splitting,
  caching, rerank)
- **Bridge Node** legacy protocol adapters
- **NOP** orchestration over Action / Complex / Memory nodes
- **Cryptographic audit chain** (signed event log per node, verifiable
  end-to-end)
- **K-of-N CA quorum** for high-stakes ops
- **L2 verified** — every IdentFrame re-verified by the Anchor Node
  with a fresh OCSP probe
- **Cross-instance pool federation**, **per-pool quotas**, **regulated
  retention policies**

The dividing line: Pro is "hosting multiple organisations safely on one
deployment." Ent is "running this in a regulated industry where every
action needs a paper trail and every signing key has K-of-N witnesses."

---

## Suggested merge order

```
P-Pro.1 (external CA)        ──┐
                                ├──→ P-Pro.4 (Postgres CA store, optional)
P-Pro.2 (pools, ADR-001)     ──┤
                                │
                                └──→ P-Pro.3 (tenant scope)
                                          │
                                          └──→ P-Pro.5 (ActionNode admin)
                                                    │
                                                    └──→ P-Pro.6 (tenant-aware UI)
```

P-Pro.1 + P-Pro.2 are independent and can ship in either order; P-Pro.4
is the optional rail beside them. Everything from P-Pro.3 onwards is
strictly serial.

## References

- [`docs/architecture/editions.md`](./editions.md) — Lite / Pro / Ent
  high-level matrix
- [`docs/architecture/adr-001-memory-pools.md`](./adr-001-memory-pools.md)
  — pool design that P-Pro.2 implements
- [`docs/architecture/nps-mapping.md`](./nps-mapping.md) — what NPS
  surface Lite ships and what Pro fills in
- [`labacacia/nip-ca-server`](https://github.com/labacacia/nip-ca-server)
  — the external CA Pro depends on
