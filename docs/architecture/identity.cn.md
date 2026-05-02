[English Version](./identity.md) | 中文版

# Banyan Identity（人机 OIDC 轨道）— 设计

> 与 [`auth.md`](./auth.cn.md)（NID/mTLS 轨，Agent ↔ Node）配套。Banyan 跑**两条身份轨道**，本文只覆盖人机这一条。配套：[OLS 接口表面](./ols-surface-reference.cn.md)。

## 双轨分离

| 轨道 | 主体 | 载体 | 签发方 | 校验方 | 项目 |
|---|---|---|---|---|---|
| NID | Agent / Memory Node | NCP IdentFrame 内的 X.509 证书 | `INipCaProvider`（embedded mini-CA 或 nip-ca-server） | Memory Node 中间件 | `Banyan.Auth` |
| OLS | Operator / 管理员 | RS256 JWT (Bearer) | OLS.Root.Oidc token endpoint | Banyan admin / CLI | `Banyan.Identity`（新增） |

两轨永不在同一通信链路上混用 — 人不会发 NID，agent 不会发 JWT。

## 项目布局

```
src/Banyan.Identity/
├── Banyan.Identity.csproj           # net10.0; 引 OLS.Root.Core/Authentication/Authorisation/Oidc + Microsoft.Data.Sqlite
├── BanyanIdentityOptions.cs         # DB 路径、签名密钥路径、token 生命周期、CLI client_id
├── Stores/
│   ├── IdentityMigrations.cs        # identity.db 的原生 SQL migrations
│   ├── SqliteUserStore.cs           # IUserStore + IUserPasswordStore + IUserEmailStore
│   │                                # + IUserLockoutStore + IUserRoleStore + IUserTwoFactorStore
│   ├── SqliteRoleStore.cs           # IRoleStore<IdentityRole>
│   ├── SqliteRefreshTokenStore.cs   # IRefreshTokenStore<IdentityUser>
│   ├── SqliteOidcClientStore.cs     # IClientStore
│   ├── SqliteAuthorizationCodeStore.cs
│   ├── SqliteDeviceCodeStore.cs
│   └── SqliteReferenceTokenStore.cs
├── Crypto/
│   └── PemSigningKeyLoader.cs       # 加载 PEM RSA 私钥，输出 SecurityKey + SigningCredentials
└── Extensions/
    └── BanyanIdentityServiceCollectionExtensions.cs
        // AddBanyanIdentity(this IServiceCollection, Action<BanyanIdentityOptions>)
        // 内部调 AddOlsIdentityCore + AddOlsAuthentication + AddOlsAuthorisation + AddOlsOidc
        // 并把所有 SQLite store 注册到指定的 identity.db
```

`Banyan.Auth` **不动**。`Banyan.Identity` 是平级新模块。

## 锁定决策（2026-04-30）

| # | 决策 | 理由 |
|---|---|---|
| 1 | DB：独立 `identity.db`（不合并到 banyan.db） | 身份与记忆解耦，备份、迁移、权限独立 |
| 2 | CLI 登录：默认 Device Code，`banyan login --browser` 走 Auth Code + PKCE | Device flow 通用；有本地浏览器时 PKCE UX 更顺 |
| 3 | RSA 签名密钥：`banyan keygen` 写到可配置路径的 PEM 文件（默认 `~/.banyan/identity-signing.pem`），轮换暂手动 | 先文件后 KMS。不放 DB |
| 4 | `banyan` CLI 注册为 public PKCE OIDC client（`client_id = banyan-cli`），由 `banyan init` 自动 upsert，redirect URI 用 `http://127.0.0.1` loopback wildcard（RFC 8252） | 首次运行零运维 |

## SQLite schema (`identity.db`)

```sql
-- Migration 001: Identity 核心
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
-- OidcClient 上每个 list-typed 字段统一一张表，kind 区分
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

## 配置形态

```jsonc
// banyan-identity.json（Banyan.Cli 启动时加载，映射到 BanyanIdentityOptions）
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

## CLI 命令

| 命令 | 用途 |
|---|---|
| `banyan keygen` | 在 `SigningKeyPath` 生成 RSA-2048 PEM。已存在则拒绝覆盖 |
| `banyan init`（扩展） | 创建 `identity.db` → 跑 migrations → upsert `banyan-cli` OIDC client → 交互式创建初始 admin 用户 |
| `banyan login` | Device Code flow，提示 user_code + 验证 URL，轮询 `/connect/token` |
| `banyan login --browser` | Auth Code + PKCE，启 localhost 监听，拉默认浏览器 |
| `banyan whoami` | 显示当前 access token 的 subject + scopes |
| `banyan logout` | 调 `/connect/revocation` 撤销 refresh token，清缓存 |

## Web UI 登录流

`Banyan.Web` 在 OLS 之上完整对接了浏览器 session 路径：

| 层 | 组件 | 说明 |
|---|---|---|
| Cookie 颁发 | `POST /api/auth/login`（`BrowserAuthEndpoints`） | 通过 `ISignInManager<IdentityUser>` 验密，将 JWT 写入 `banyan_session` HttpOnly cookie |
| Cookie 提升 | `SessionCookieMiddleware` | 在 `UseAuthentication()` 之前把 `banyan_session` 值搬到 `Authorization: Bearer` |
| JWT 验证 | `AddAuthentication().AddJwtBearer(...)` | 验 issuer、audience、有效期、RS256 签名。`MapInboundClaims = false` 保持 claim 名原样（`"role"` 不被映射） |
| 授权策略 | `AddAuthorization` — `"admin"` policy | `RequireRole("admin", "ADMIN")` 守卫 `/api/agents` 和 `/api/ca` |
| Session 状态 | `GET /api/auth/me` | 返回 `{ loggedIn, username, roles, expiresAt }`；未接 identity 时返回 `{ loggedIn: false }`（非 401），前端无感知 |
| 登出 | `POST /api/auth/logout` | 删除 `banyan_session` cookie |

Identity 是可选的：`WebApp.RunAsync` 检测 `identity.db` 和签名密钥是否存在，不存在则跳过全部 identity 中间件。跳过后 admin 路由仍可访问，但无鉴权（适合内网 demo）。

## 显式不做（P1.5 范围外）

- 多租户：单租户。
- 邮件/短信确认流：stub 化（admin 创建用户时直接 `EmailConfirmed = true`）。
- 2FA：schema 留位但流程不接，`IUserTwoFactorStore` 暂返回 false。
- 密钥轮换：手动。JWKS 暂只暴露单 `kid`。
- 外部 provider（Google/MS/GitHub）OAuth2：CLI 不暴露。OLS 支持但 Banyan 暂无场景。
