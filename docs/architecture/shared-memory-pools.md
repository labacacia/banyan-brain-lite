> **See also (Canonical reference):** [ADR-001: Shared Memory Pools](../../../../docs/architecture/adr-001-memory-pools.md)

# Lite Shared Memory Pools

Lite tracking issue: labacacia/banyan-brain-lite#16

## Goal

Lite shared memory pools give a local Banyan node a small, understandable
sharing model without turning Lite into an enterprise access-control product.

The feature is for local and self-hosted workflows:

- one person separating private, project, and agent-session memory;
- a small local deployment sharing project context between trusted agents;
- a workflow that wants pool-shaped recall without Pro/Ent tenant management.

## Product Boundary

Lite supports local sharing only. It does not provide:

- SaaS tenant management;
- organization-wide administration;
- enterprise role/group ACL management;
- cross-customer or cross-tenant sharing;
- managed identity-provider integration as a required dependency.

NID authentication remains useful for attribution, but the Lite pool model must
stay operable for single-user local deployments.

## Share Levels

| Level | Meaning | Intended use |
| --- | --- | --- |
| `personal` | Default private memory scope for one local user/profile. | Preferences, durable user facts, private agent context. |
| `local_workspace` | Local project/workspace pool on the same deployment or device. | Codebase notes, project decisions, runbooks, shared local context. |
| `agent_session` | Bounded pool for one local agent/session workflow. | Temporary task memory, multi-step local automation, disposable context. |

Lite must not infer enterprise structure from these names. They are local
coordination levels, not tenant or organization boundaries.

## Resource Model

The Lite model is intentionally smaller than Pro/Ent:

```text
MemoryPool
  pool_id
  level                  # personal | local_workspace | agent_session
  name
  description
  state                  # active | archived
  policy                 # owner-only | read-only | read-write
  created_at
  updated_at

Memory
  memory_id
  namespace              # existing Lite tag
  pool_id?               # null means private/default behavior
  source_type            # private | pool | knowledge_pack
```

`namespace` remains a query tag. `pool_id` is the sharing boundary. A memory
belongs to private scope or to one local pool.

## Storage Design

The SQLite implementation should add pool metadata without breaking existing
namespace-only stores:

```sql
memory_pools(
  pool_id       TEXT PRIMARY KEY,
  level         TEXT NOT NULL CHECK (level IN ('personal','local_workspace','agent_session')),
  name          TEXT NOT NULL,
  description   TEXT,
  state         TEXT NOT NULL DEFAULT 'active',
  policy        TEXT NOT NULL DEFAULT 'owner-only',
  created_at    TEXT NOT NULL,
  updated_at    TEXT NOT NULL
);

memories_current.pool_id TEXT NULL REFERENCES memory_pools(pool_id)
memory_events.pool_id    TEXT NULL REFERENCES memory_pools(pool_id)
embeddings.pool_id       TEXT NULL
```

Migration rule: existing memories remain private (`pool_id IS NULL`). Lite must
not automatically move old namespace data into shared pools.

## API Design

The HTTP API should keep the current memory endpoints and add a small pool
surface:

```http
POST   /api/pools
GET    /api/pools
GET    /api/pools/{pool_id}
PATCH  /api/pools/{pool_id}
DELETE /api/pools/{pool_id}

POST   /api/memory           body: { content, namespace?, poolId?, agentNid? }
GET    /api/memory/search    query: q, mode, k, namespace?, pools?
```

Search behavior:

- no `pools` parameter keeps current private/namespace behavior;
- `pools=<id>` searches the selected local pool;
- `pools=<id1>,<id2>` searches multiple local pools and merges results;
- responses include `sourceType` and `poolId` when a hit comes from a pool.

## CLI Design

The CLI should mirror the local API:

```bash
banyan pool create <name> --level personal|local_workspace|agent_session [--desc TEXT]
banyan pool list
banyan pool show <pool-id-or-name>
banyan pool archive <pool-id-or-name>

banyan remember "text" --pool <pool-id-or-name> [--namespace NS]
banyan recall "query" --pool <pool-id-or-name> [--namespace NS]
```

Lite does not need grant/revoke commands in the first version. A future local
multi-user profile design can add them without changing the core pool identity.

## Knowledge Pack Boundary

Mounted Knowledge Packs are source material, not native shared-pool memory.
Recall may combine:

- private memories;
- local shared-pool memories;
- mounted Knowledge Pack records.

Results must preserve source labels so users and agents can distinguish native
memory from pack-derived context.

## Test Plan

Implementation should include tests for:

- existing namespace-only memories still work after migration;
- private recall does not return pool memories unless requested;
- `local_workspace` recall returns only selected pool records;
- `agent_session` pools remain isolated from each other;
- multi-pool recall includes `sourceType=pool` and `poolId`;
- Knowledge Pack recall remains labeled separately from native pool memory.

## Non-Goals

- Enterprise ACLs.
- Tenant isolation.
- Cross-device federation.
- Cross-customer sharing.
- Legal hold, managed retention, or cryptographic audit chain.
