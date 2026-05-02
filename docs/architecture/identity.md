English | [中文版](./identity.cn.md)

# Banyan Identity (Human-side OIDC) — Design

> Companion to [`auth.md`](./auth.md) (the NID/mTLS track for Agent ↔ Node). Banyan runs **two identity tracks**; this doc covers humans only. Cross-reference: [OLS API surface](./ols-surface-reference.md).

## Track separation

| Track | Subjects | Carrier | Issued by | Verified by | Project |
|---|---|---|---|---|---|
| NID | Agents, Memory Nodes | X.509 cert in NCP IdentFrame | `INipCaProvider` (embedded mini-CA or external nip-ca-server) | Memory Node middleware | `Banyan.Auth` |
| OLS | Operators, admins | RS256 JWT (Bearer) | OLS.Root.Oidc token endpoint | Banyan admin/CLI surface | `Banyan.Identity` (new) |

The two tracks never overlap on the wire — humans never get NIDs, agents never get JWTs.

## Project layout

```
src/Banyan.Identity/
├── Banyan.Identity.csproj           # net10.0, refs OLS.Root.Core/Authentication/Authorisation/Oidc + Microsoft.Data.Sqlite
├── BanyanIdentityOptions.cs         # DB path, signing-key path, token lifetimes, CLI client_id
├── Stores/
│   ├── IdentityMigrations.cs        # raw-SQL migrations against identity.db
│   ├── SqliteUserStore.cs           # IUserStore + IUserPasswordStore + IUserEmailStore
│   │                                # + IUserLockoutStore + IUserRoleStore + IUserTwoFactorStore
│   ├── SqliteRoleStore.cs           # IRoleStore<IdentityRole>
│   ├── SqliteRefreshTokenStore.cs   # IRefreshTokenStore<IdentityUser>
│   ├── SqliteOidcClientStore.cs     # IClientStore
│   ├── SqliteAuthorizationCodeStore.cs
│   ├── SqliteDeviceCodeStore.cs
│   └── SqliteReferenceTokenStore.cs
├── Crypto/
│   └── PemSigningKeyLoader.cs       # load PEM RSA private key, expose SecurityKey + SigningCredentials
└── Extensions/
    └── BanyanIdentityServiceCollectionExtensions.cs
        // AddBanyanIdentity(this IServiceCollection, Action<BanyanIdentityOptions>)
        // wires AddOlsIdentityCore + AddOlsAuthentication + AddOlsAuthorisation + AddOlsOidc
        // + registers all SQLite stores against the configured identity.db
```

`Banyan.Auth` is **not** modified. `Banyan.Identity` is a peer.

## Locked decisions (2026-04-30)

| # | Decision | Rationale |
|---|---|---|
| 1 | DB: independent `identity.db` (not merged with `banyan.db`) | Decouple backup, migration, and access permissions of identity from memory data |
| 2 | CLI login: Device Code by default, Authorization Code + PKCE when `banyan login --browser` | Device flow is universal; PKCE is nicer when a local browser is available |
| 3 | RSA signing key: `banyan keygen` writes a PEM file at a configurable path (default `~/.banyan/identity-signing.pem`); rotation is manual for now | Filesystem first, KMS later. Avoid storing key in DB |
| 4 | `banyan` CLI is registered as a public PKCE OIDC client (`client_id = banyan-cli`) by `banyan init`, with redirect URI `http://127.0.0.1` (loopback wildcard per RFC 8252) | First-run UX: zero ops needed |

## SQLite schema (`identity.db`)

```sql
-- Migration 001: Identity core
CREATE TABLE schema_migrations (
    version    INTEGER PRIMARY KEY,
    applied_at TEXT    NOT NULL
);

CREATE TABLE ols_users (
    id                       TEXT PRIMARY KEY,
    user_name                TEXT,
    normalized_user_name     TEXT UNIQUE,
    email                    TEXT,
    normalized_email         TEXT,
    email_confirmed          INTEGER NOT NULL DEFAULT 0,
    password_hash            TEXT,
    security_stamp           TEXT,
    concurrency_stamp        TEXT,
    phone_number             TEXT,
    phone_number_confirmed   INTEGER NOT NULL DEFAULT 0,
    two_factor_enabled       INTEGER NOT NULL DEFAULT 0,
    lockout_end              TEXT,
    lockout_enabled          INTEGER NOT NULL DEFAULT 1,
    access_failed_count      INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX ix_ols_users_email ON ols_users(normalized_email);

CREATE TABLE ols_roles (
    id                 TEXT PRIMARY KEY,
    name               TEXT,
    normalized_name    TEXT UNIQUE,
    concurrency_stamp  TEXT
);

CREATE TABLE ols_user_roles (
    user_id  TEXT NOT NULL REFERENCES ols_users(id) ON DELETE CASCADE,
    role_id  TEXT NOT NULL REFERENCES ols_roles(id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, role_id)
);

CREATE TABLE ols_refresh_tokens (
    id                     TEXT PRIMARY KEY,
    user_id                TEXT NOT NULL REFERENCES ols_users(id) ON DELETE CASCADE,
    token_hash             TEXT NOT NULL UNIQUE,
    created_at             TEXT NOT NULL,
    expires_at             TEXT NOT NULL,
    is_revoked             INTEGER NOT NULL DEFAULT 0,
    is_active              INTEGER NOT NULL DEFAULT 1,
    replaced_by_token_id   TEXT
);
CREATE INDEX ix_ols_rt_user ON ols_refresh_tokens(user_id);

CREATE TABLE ols_oidc_clients (
    client_id                          TEXT PRIMARY KEY,
    client_name                        TEXT,
    is_enabled                         INTEGER NOT NULL DEFAULT 1,
    require_client_secret              INTEGER NOT NULL DEFAULT 1,
    require_pkce                       INTEGER NOT NULL DEFAULT 1,
    sliding_refresh_token_expiry       INTEGER NOT NULL DEFAULT 1,
    access_token_lifetime_sec          INTEGER NOT NULL,
    authorization_code_lifetime_sec    INTEGER NOT NULL,
    refresh_token_lifetime_sec         INTEGER NOT NULL
);
CREATE TABLE ols_oidc_client_secrets (
    client_id      TEXT NOT NULL REFERENCES ols_oidc_clients(client_id) ON DELETE CASCADE,
    hashed_secret  TEXT NOT NULL,
    PRIMARY KEY (client_id, hashed_secret)
);
-- One table for every list-typed field on OidcClient — kind discriminates.
CREATE TABLE ols_oidc_client_strings (
    client_id  TEXT NOT NULL REFERENCES ols_oidc_clients(client_id) ON DELETE CASCADE,
    kind       TEXT NOT NULL,           -- 'redirect' | 'post_logout' | 'cors' | 'scope' | 'grant'
    value      TEXT NOT NULL,
    PRIMARY KEY (client_id, kind, value)
);

CREATE TABLE ols_authorization_codes (
    code                    TEXT PRIMARY KEY,
    client_id               TEXT NOT NULL,
    subject_id              TEXT NOT NULL,
    redirect_uri            TEXT,
    code_challenge          TEXT,
    code_challenge_method   TEXT,
    nonce                   TEXT,
    state                   TEXT,
    scopes_csv              TEXT,
    created_at              TEXT NOT NULL,
    expires_at              TEXT NOT NULL
);

CREATE TABLE ols_device_codes (
    code             TEXT PRIMARY KEY,
    user_code        TEXT NOT NULL UNIQUE,
    client_id        TEXT NOT NULL,
    subject_id       TEXT,
    scopes_csv       TEXT,
    is_authorized    INTEGER NOT NULL DEFAULT 0,
    is_denied        INTEGER NOT NULL DEFAULT 0,
    last_polled_at   TEXT,
    interval_sec     INTEGER NOT NULL,
    created_at       TEXT NOT NULL,
    expires_at       TEXT NOT NULL
);

CREATE TABLE ols_reference_tokens (
    token_hash    TEXT PRIMARY KEY,
    subject_id    TEXT,
    client_id     TEXT,
    scopes        TEXT,
    created_at    TEXT NOT NULL,
    expires_at    TEXT NOT NULL,
    is_revoked    INTEGER NOT NULL DEFAULT 0,
    is_active     INTEGER NOT NULL DEFAULT 1
);
```

## Configuration shape

```jsonc
// banyan-identity.json (loaded by Banyan.Cli at startup, mapped into BanyanIdentityOptions)
{
  "DbPath":            "~/.banyan/identity.db",
  "SigningKeyPath":    "~/.banyan/identity-signing.pem",
  "Issuer":            "https://localhost:5001",
  "Audience":          "banyan",
  "AccessTokenExpiry": "00:30:00",
  "RefreshTokenExpiry":"30.00:00:00",
  "CliClientId":       "banyan-cli",
  "CliRedirectUris":   ["http://127.0.0.1"]      // loopback wildcard, RFC 8252
}
```

## CLI commands

| Command | Purpose |
|---|---|
| `banyan keygen` | Generate RSA-2048 PEM at `SigningKeyPath`. Refuses to overwrite. |
| `banyan init` (extended) | Create `identity.db` + run migrations + upsert `banyan-cli` OIDC client + create initial admin user (interactive) |
| `banyan login` | Device Code flow. Prompts user with `user_code` + verification URL, polls `/connect/token`. |
| `banyan login --browser` | Auth Code + PKCE. Spawns localhost listener, opens default browser. |
| `banyan whoami` | Show subject + scopes from cached access token. |
| `banyan logout` | Revoke refresh token at `/connect/revocation`, clear cache. |

## Web UI login flow

`Banyan.Web` wires the full browser-session path on top of OLS:

| Layer | Component | Notes |
|---|---|---|
| Cookie setter | `POST /api/auth/login` (`BrowserAuthEndpoints`) | Validates credentials via `ISignInManager<IdentityUser>`, stores JWT in `banyan_session` HttpOnly cookie |
| Cookie lifter | `SessionCookieMiddleware` | Copies `banyan_session` value onto `Authorization: Bearer` before `UseAuthentication()` sees the request |
| JWT validation | `AddAuthentication().AddJwtBearer(...)` | Validates issuer, audience, lifetime, and RS256 signature. `MapInboundClaims = false` keeps claim names verbatim (`"role"` stays `"role"`) |
| Authorization | `AddAuthorization` — `"admin"` policy | `RequireRole("admin", "ADMIN")` — gates `/api/agents` and `/api/ca` |
| Session status | `GET /api/auth/me` | Returns `{ loggedIn, username, roles, expiresAt }`. Returns `{ loggedIn: false }` (not 401) when identity is not wired, so the front-end can stay quiet on zero-config demo nodes |
| Logout | `POST /api/auth/logout` | Deletes `banyan_session` cookie |

Identity is optional: `WebApp.RunAsync` checks whether `identity.db` and the signing key exist before wiring any of this. Without them, admin routes are still served but completely unauthenticated (suitable for trusted-network demos where CA control is all that's needed).

## Out of scope (explicit non-goals for P1.5)

- Multi-tenant: single tenant only.
- Email/SMS confirmation pipelines: stubbed (`EmailConfirmed = true` on admin-create).
- 2FA: schema present, flow not wired. `IUserTwoFactorStore` returns false until later.
- Key rotation: manual only. No JWKS multi-key serving yet (single `kid`).
- External providers (Google/MS/GitHub) OAuth2: not exposed via CLI. OLS supports it but Banyan has no use case.
