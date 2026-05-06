English | [中文版](./adr-001-memory-pools.cn.md)

# ADR-001: Shared Memory Pools (Pro tier)

- **Status**: Proposed
- **Date**: 2026-05-02
- **Tier**: Pro (not in Lite)
- **Supersedes**: —
- **Superseded by**: —

## Context

Banyan today (Lite) organises memory by `namespace` — a free-form string
like `user-alice` or `project-acme`. Namespace is a *tag*, not a fence:
anyone with HTTP access can read or write any namespace, and `agentNid` is
either a free-form claim or (after `feat/nid-auth-lite`) a server-verified
identity stamp. There is no concept of "this set of memories belongs to
that group of agents" beyond a naming convention the client honours.

Two scenarios push past this:

1. **Multi-agent collaboration on a project.** One agent scans the
   codebase and writes 200 facts into `project-acme`. A second agent
   joining the project would today have to re-scan, because nothing tells
   it `project-acme` is shared and trusted. We want it to read from the
   first agent's findings without re-doing the work, while still being
   sure the source is auditable.
2. **System onboarding.** A fresh agent that connects to a Banyan
   instance has no idea what namespaces exist, what schema the host uses,
   or what conventions the operator wants. Today this is README work that
   lives outside the protocol — agents can't bootstrap themselves from
   the wire.

Lite intentionally stays simple — namespace + verified `agentNid` is
enough for single-org use. But the moment you have multiple
collaborating agents under different NIDs, you need first-class
group-shared storage with read/write ACLs.

## Decision

Pro introduces **memory pools** — named, owned, NID-ACL'd containers
that sit *orthogonal* to namespaces.

- A **pool** has an `id`, a `name`, an owner NID, an optional description,
  and a list of grants (`{principal_nid, permission}` rows).
- Permissions are `read | write | admin`. `admin` includes write + read +
  ability to grant/revoke.
- A memory belongs to **exactly one pool**. `namespace` survives as a
  *within-pool tag* (so `pool=team-acme, namespace=session-42` is a
  perfectly fine address).
- Pool membership is **NID-only**. Human operators read pool contents by
  having an agent they own act on their behalf; this matches the
  dual-track identity model — NIDs for machines, OLS/OIDC for humans —
  and avoids needing an OLS↔NID mapping table.
- **Pool 0 is the system pool**, present in every Banyan instance.
  - Read ACL: `('*', 'read')` — every authenticated NID can read it.
  - Write ACL: explicit list of NIDs from operator config
    (`system_pool_writers` in `BanyanNodeOptions`). No one else can write,
    not even the pool owner — pool 0's writers are pinned by the operator,
    not by `admin` grants.
  - Initial content (operator-seeded, optional): "How to write memories
    to this Banyan instance", schema doc, namespace conventions, list of
    other discoverable pools, retention policy.

### Schema

Two new tables in `memory.db`, plus one column on `memories_current` and
`memory_events`:

```sql
memory_pools(
  pool_id      INTEGER PK,
  name         TEXT NOT NULL UNIQUE,
  owner_nid    TEXT NOT NULL,         -- NULL only for pool 0 (system)
  description  TEXT,
  created_at   TIMESTAMP NOT NULL
);

pool_acl(
  pool_id      INTEGER NOT NULL FK→memory_pools ON DELETE CASCADE,
  principal    TEXT NOT NULL,         -- NID, or '*' for the system-pool wildcard
  permission   TEXT NOT NULL CHECK (permission IN ('read','write','admin')),
  granted_by   TEXT,                  -- NID of granter (NULL for system-seeded grants)
  granted_at   TIMESTAMP NOT NULL,
  PRIMARY KEY (pool_id, principal, permission)
);

memories_current.pool_id  INTEGER NOT NULL DEFAULT 0 FK→memory_pools
memory_events.pool_id     INTEGER NOT NULL DEFAULT 0 FK→memory_pools
embeddings.pool_id        INTEGER NOT NULL DEFAULT 0  -- denormalised for filter speed
```

Pool 0 is created by the migration with `name='system'` and `owner_nid=NULL`.
The `'*'` wildcard is hard-coded only in the read path — wildcards never
match write or admin permissions.

### API

Read endpoints accept a list of pool IDs and merge results:

```
GET /api/memory/search?q=...&pools=0,42,team-acme&mode=hybrid&k=10
GET /api/memory/{id}                  ← server checks read ACL on memory's pool
```

Write endpoints take a pool target. The middleware verifies the caller
has `write` on that pool. The server-verified NID (from
`Authorization: NID`) is the principal — there is no anonymous write to
any pool.

```
POST   /api/memory                    body: {pool, namespace, content, ...}
PUT    /api/memory/{id}               body: {content}             ← read+write check
DELETE /api/memory/{id}                                           ← read+write check
```

Pool management is its own endpoint group, mirrored on the CLI:

```
POST   /api/pools                     body: {name, description}      → create (owner = caller NID)
GET    /api/pools                                                     → list pools the caller can read
GET    /api/pools/{id}                                                → details + grants (admin only sees full grant list)
PATCH  /api/pools/{id}                body: {description}             → owner / admin
POST   /api/pools/{id}/grants         body: {principal_nid, permission} → admin only
DELETE /api/pools/{id}/grants/{nid}/{perm}                            → admin only
DELETE /api/pools/{id}                                                → owner only; refuses if non-tombstone memories exist
```

CLI surface:
```
banyan pool create  <name> [--desc]
banyan pool list
banyan pool show    <name|id>
banyan pool grant   <name|id> <principal-nid> <read|write|admin>
banyan pool revoke  <name|id> <principal-nid> [--perm read|write|admin]
banyan pool delete  <name|id>
```

### Search merging

When a request lists multiple pools, the server runs the same
hybrid/lexical/vector query against each pool the caller has read on,
filters out hits whose pool the caller can't read (defence in depth),
and merges by `score` desc with stable tiebreak on `(pool_id, memory_id)`.
The response includes `pool_id` on every hit so clients can render
provenance.

### Pool 0 maintenance

Operator config exposes the system-pool writer set:

```yaml
# banyan.config.yml (Pro)
system_pool:
  writers:
    - urn:nps:agent:banyan:operator
    - urn:nps:agent:banyan:doc-bot
  seed_path: /etc/banyan/system-pool-seed.md   # optional; loaded on first boot
```

A startup task reads `seed_path` (a markdown file with `---` frontmatter
chunks per memory) and idempotently writes any new entries into pool 0,
attributed to a synthetic `urn:nps:agent:banyan:system` NID issued by the
local CA. Subsequent edits go through the normal write API from a
configured writer NID.

## Rationale

- **Why pools instead of "namespace ACLs"?** Namespaces are user-facing
  *tags* and we want to keep them cheap to invent — `namespace=user-X` is
  conventional, not enforced. Layering ACLs on namespaces would
  retroactively turn every existing namespace into a security boundary
  and force callers to declare their conventions before storing anything.
  Pools introduce a separate, explicitly-managed dimension; namespace
  stays a tag.
- **Why one pool per memory?** Multi-pool ownership invites confusing
  semantics on update / forget. Cross-pool sharing is read-time
  (search across pools), not storage-time. If a memory genuinely belongs
  in two pools, write it twice — they're cheap and the events are
  attributable to the right authors.
- **Why NID-only membership?** Banyan's identity model is two-track —
  NIDs for machines, OLS for humans. Pool ACLs operate on the agent
  layer; a human operator delegates by issuing a NID to an agent they
  control. This avoids an OLS↔NID mapping table (which would have to
  span instances) and keeps pool ACL enforcement local.
- **Why a wildcard only on read?** Pool 0 needs "every connected agent
  can read this." Wildcard write would be indistinguishable from
  anonymous write, which we already explicitly rejected at the
  middleware layer.
- **Why is pool 0 special-cased?** It exists in every instance, has no
  owner (operator-managed), needs a wildcard read grant, and its
  writers are pinned by config rather than by API grants. Modelling
  these as runtime-mutable would invite operators to accidentally lock
  themselves out of their own onboarding pool.

## Consequences

### Positive

- Multi-agent project workflow becomes first-class — agents share a pool,
  search across pools, and credit each fact to the writer NID.
- The system pool gives every Banyan instance a self-documenting entry
  point; new agents can `search?pool=0&q="how do I write memories"` to
  bootstrap.
- ACL is enforced server-side at the same point that NID auth lives
  today (one middleware seam, not scattered).
- Lite stays unchanged. Pools are a Pro upsell, and Lite users who later
  migrate keep all their existing memories under pool 0 (or a default
  per-tenant pool) without rewriting clients.

### Negative / cost

- Real schema migration on `memory.db` — `pool_id` columns on three
  tables, two new tables, plus a backfill step for existing memories
  (everything → pool 0 by default at migration time, then operator-driven
  re-pooling).
- Every search and write path now does an ACL lookup. Indexed lookup on
  `pool_acl(principal, pool_id)` is cheap (small table per tenant), but
  it is a new hot-path query.
- New CLI surface (six subcommands) and new web-UI panel (pool list +
  grants). Estimated 2-3 days of work end-to-end including tests + docs.
- Cross-pool search makes result merging the server's responsibility;
  RRF normalisation across pools needs a small calibration test
  (otherwise a pool with 10 dense matches can drown a pool with 2 strong
  ones).

### Out of scope (Ent territory)

- Cryptographic audit chain over pool ACL changes (Ent: signed audit log).
- Cross-instance pool federation — pool replication between Banyan
  nodes is not addressed here.
- K-of-N approval for high-stakes pool ops (delete, mass revoke).

## Open questions

- ✅ **Pool membership identity**: NID-only — humans access via agents
  they own. _Decided 2026-05-02._
- ⏳ Should pool 0's seed content also include a *machine-readable*
  schema doc (so agents can introspect endpoints rather than parsing
  prose)? Probably yes; defer to implementation.
- ⏳ Quota knobs per pool (max memories, max bytes)? Likely Ent-level —
  Pro can punt to "operator monitors and prunes".

## References

- [`docs/architecture/editions.md`](./editions.md) — Lite/Pro/Ent matrix
- [`docs/architecture/storage-tiers.md`](./storage-tiers.md) — current
  `memory.db` schema this ADR extends
- [`docs/architecture/identity.md`](./identity.md) — dual-track identity
  rationale (why NID-only membership)
- `feat/nid-auth-lite` (commit `c83c9ee`) — server-verified NID
  middleware that this ADR builds on
