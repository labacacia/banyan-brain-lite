English | [中文版](./storage-tiers.cn.md)

# Storage Tiers

Banyan keeps every persisted bit in SQLite, but in three independent files
with different access patterns and threat models:

| File | Owner | Threat model | Backup cadence |
|------|-------|--------------|----------------|
| `memory.db`   | `Banyan.Lite`     | Operator-trusted; large; must survive process crash | hot-snapshot before retention runs |
| `identity.db` | `Banyan.Identity` | Confidential (password hashes, refresh tokens); small | encrypted offsite, lower frequency |
| `nipca.db`    | `Banyan.Auth`     | Auditable (cert ledger); append-mostly              | append-only mirror to a write-once medium |

Splitting them lets backups, retention, and access-control policies move at
different speeds — and lets you run a Banyan node that **only** participates as
a Memory Node (no CA, no human identity) by simply not opening the other two.

---

## `memory.db` — `Banyan.Lite`

The memory store is **event-sourced**: every `Write/Update/Forget` appends to
an immutable log, and a denormalised "current snapshot" view is maintained
alongside it. Searches always read the snapshot; the trace endpoint always
reads the log.

### Tables

```
schema_migrations(version PK, applied_at)
namespaces      (namespace PK, created_at)

memory_events   (event_id PK, memory_id, type, content?, metadata?, agent_nid?, namespace, occurred_at)
                 # type: 0=Write 1=Update 2=Tombstone   — never deleted

memories_current(memory_id PK, event_id, namespace, content, metadata?, agent_nid?, created_at, updated_at)
                 # the latest non-tombstone snapshot — searchable surface

memories_fts    (FTS5 virtual table over memories_current.content; tokenizer=unicode61)

embeddings      (memory_id PK FK→memories_current ON DELETE CASCADE,
                 namespace, model_id, dim, vector BLOB, updated_at)
                 # raw little-endian float32, source-of-truth for vector data

embeddings_vec  (vec0 virtual table — only when sqlite-vec is loadable;
                 a sidecar ANN index, repopulated from `embeddings` on open)
```

### Lifecycle

| Op | Events | memories_current | FTS5 | embeddings | embeddings_vec |
|----|--------|------------------|------|------------|----------------|
| `WriteAsync`   | INSERT (Write)     | INSERT     | INSERT      | INSERT (if embedder)        | INSERT (if vec)             |
| `UpdateAsync`  | INSERT (Update)    | UPDATE     | DELETE+INSERT | UPSERT                    | DELETE+INSERT (no UPSERT in vec0) |
| `ForgetAsync`  | INSERT (Tombstone) | DELETE     | DELETE      | DELETE                      | DELETE                      |

The audit invariant: **`memory_events` is append-only**. `Forget` removes a
memory from the searchable surface but the trace remains intact for audit.

### Retrieval

Three modes, gated by `SearchQuery.Mode`:

- **Lexical (BM25)** — pure FTS5 over `memories_fts` with the unicode61
  tokenizer; supports prefix matching for plurals (`deadline*` matches
  `deadlines`).
- **Vector (cosine)** — the embedder's `EmbedQueryAsync` produces a 384-d
  vector; we run vec0's KNN when the extension is loaded, otherwise linear
  scan + dot-product over `embeddings`. Vectors are L2-normalised at write
  time so cosine ≡ dot product. vec0's default distance is L2², which we map
  back to cosine via `cos = 1 - distance/2`.
- **Hybrid (RRF)** — fetch a deeper pool from each ranker (`max(K*4, 50)`),
  merge with reciprocal rank fusion (`k=60`, the Cormack 2009 default),
  return top-K. The wire shape exposes both `lex_rank` and `vec_rank` per hit
  so callers can debug the fusion.

---

## `identity.db` — `Banyan.Identity`

Backs the OLS-shaped human identity track. Eleven tables map to OLS's
`IUserStore` / `IUserPasswordStore` / `IUserEmailStore` / `IUserLockoutStore` /
`IUserRoleStore` / `IUserTwoFactorStore` / `IRoleStore` /
`IRefreshTokenStore` / `IClientStore` / `IAuthorizationCodeStore` /
`IDeviceCodeStore` / `IReferenceTokenStore`.

### Tables

```
schema_migrations
ols_users               (id PK, user_name, normalized_user_name UNIQUE, email, …,
                         password_hash, security_stamp, concurrency_stamp,
                         lockout_end?, lockout_enabled, access_failed_count, two_factor_enabled, …)
ols_roles               (id PK, name, normalized_name UNIQUE, concurrency_stamp)
ols_user_roles          (user_id, role_id) PK(user_id, role_id), CASCADE
ols_refresh_tokens      (id PK, user_id, token_hash UNIQUE, created_at, expires_at,
                         is_revoked, is_active, replaced_by_token_id?)
ols_oidc_clients        (client_id PK, …)
ols_oidc_client_secrets (client_id, hashed_secret) PK
ols_oidc_client_strings (client_id, kind, value) PK     # redirect / post_logout / cors / scope / grant
ols_authorization_codes (code PK, …, scopes_csv, expires_at)
ols_device_codes        (code PK, user_code UNIQUE, …)
ols_reference_tokens    (token_hash PK, …)
```

Concurrency is enforced via `concurrency_stamp` on every UPDATE — the
`SqliteUserStore` / `SqliteRoleStore` rotate the stamp on each write and
return `IdentityErrors.ConcurrencyFailure()` when it goes stale.

### Why we don't use `InnoLotus.Root.EntityFramework`

The official store implementation requires PostgreSQL via Npgsql. Banyan is
SQLite-first so it can ship as a single binary, so every store interface is
re-implemented in `SqliteUserStore` etc. with raw `Microsoft.Data.Sqlite`
parameterised SQL. **No EF dependency.** Migrations are hand-rolled DDL in
`IdentityMigrations`.

---

## `nipca.db` — `Banyan.Auth`

Holds the NID Certificate Authority's view of the world: every certificate
ever issued, plus a monotonic serial counter.

### Tables

```
schema_migrations
nip_certs   (nid PK, serial UNIQUE, entity_type, pub_key, capabilities (JSON array),
             scope_json?, metadata_json?, issued_by, issued_at, expires_at,
             revoked_at?, revoke_reason?)
nip_serial  (id PK, next INTEGER)        # single row, advanced by RETURNING
```

`SqliteNipCaStore` implements `NPS.NIP.Ca.INipCaStore` — feeding into the
upstream `NipCaService` from `LabAcacia.NPS.NIP`. Issuance is monotonic
zero-padded 16-char hex (`0000000000000001`, …) so cursors are
order-preserving.

### What `NPS.NIP.Storage.PostgreSqlNipCaStore` does and why we replaced it

The upstream package ships only a Postgres-backed store and a Postgres-shaped
`ConnectionString` field on `NipCaOptions`. To keep Banyan SQLite-only,
`EmbeddedNipCa.OpenAsync` constructs `NipCaService` itself, injecting our
SqliteNipCaStore + NipKeyManager directly — bypassing the Postgres path.

---

## Threat & ops considerations

- **All three DBs run with `PRAGMA journal_mode=WAL` + `foreign_keys=ON`** —
  set in each `Migrations.ApplyAsync` before any DDL.
- **The CA private key never lives in the database.** It's an
  AES-256-GCM-encrypted PEM file at `BanyanNipCaOptions.KeyFilePath`,
  loaded by `NipKeyManager` at startup.
- **JWT signing key is similarly file-based** —
  `Banyan.Identity.Crypto.PemSigningKeyLoader` reads a PKCS#8 PEM RSA key
  from `BanyanIdentityOptions.SigningKeyPath`.
- **Embeddings are not encrypted.** A 384-d vector leaks roughly the same
  amount of information as the source text, so we treat `memory.db` as one
  trust unit. Per-namespace encryption is a future direction.
- **Forget is GDPR-shaped, not military-shaped.** The Tombstone event keeps
  the original `memory_id` and the reason, but discards the content blob.
  Operators wanting strict erasure should run a periodic
  `VACUUM` plus a destructive backup-rotation cycle.
