[English Version](./storage-tiers.md) | 中文版

# 存储分层

Banyan 全部数据落 SQLite，但分成三个独立文件，访问模式和威胁模型都不同：

| 文件 | 主人 | 威胁模型 | 备份节奏 |
|------|------|----------|----------|
| `memory.db`   | `Banyan.Lite`     | Operator 信任；体量大；进程崩溃必须能恢复 | 留存策略前热快照 |
| `identity.db` | `Banyan.Identity` | 机密（密码哈希、refresh token）；体量小   | 加密异地、低频备份 |
| `nipca.db`    | `Banyan.Auth`     | 可审计（证书账本）；几乎只追加          | 追加同步到一次写介质 |

分库的好处：备份、留存、访问控制各走各的节奏。需要纯 Memory Node（不发证书、
不管人机身份）时，只开 `memory.db` 即可，其它两个不打开。

---

## `memory.db` — `Banyan.Lite`

Memory store 是**事件驱动**：每次 `Write/Update/Forget` 追加到不可变日志，
旁路维护一份去范式化的"当前快照"。搜索读快照，trace 端点读日志。

### 表

```
schema_migrations(version PK, applied_at)
namespaces      (namespace PK, created_at)

memory_events   (event_id PK, memory_id, type, content?, metadata?, agent_nid?, namespace, occurred_at)
                 # type: 0=Write 1=Update 2=Tombstone   — 永不删除

memories_current(memory_id PK, event_id, namespace, content, metadata?, agent_nid?, created_at, updated_at)
                 # 最新非 tombstone 快照 — 可搜索面

memories_fts    (FTS5 虚拟表 over memories_current.content；tokenizer=unicode61)

embeddings      (memory_id PK FK→memories_current ON DELETE CASCADE,
                 namespace, model_id, dim, vector BLOB, updated_at)
                 # 原始 little-endian float32，向量数据的 source-of-truth

embeddings_vec  (vec0 虚拟表 — 仅当 sqlite-vec 加载成功时；
                 ANN sidecar 索引，open 时从 `embeddings` 重新填)
```

### 生命周期

| 操作 | events | memories_current | FTS5 | embeddings | embeddings_vec |
|------|--------|------------------|------|------------|----------------|
| `WriteAsync`   | INSERT (Write)     | INSERT     | INSERT      | INSERT (有 embedder)        | INSERT (有 vec)             |
| `UpdateAsync`  | INSERT (Update)    | UPDATE     | DELETE+INSERT | UPSERT                    | DELETE+INSERT (vec0 无 UPSERT) |
| `ForgetAsync`  | INSERT (Tombstone) | DELETE     | DELETE      | DELETE                      | DELETE                      |

审计不变量：**`memory_events` 只追加、不删除**。`Forget` 把 memory 从可搜索面
移除，但 trace 完整保留。

### 检索

三种模式，由 `SearchQuery.Mode` 决定：

- **Lexical (BM25)** — `memories_fts` 上的纯 FTS5，unicode61 分词；支持
  prefix 匹配（`deadline*` 命中 `deadlines`）。
- **Vector (cosine)** — embedder 的 `EmbedQueryAsync` 产生 384 维向量；扩展
  加载时走 vec0 KNN，否则线扫 + 点积过 `embeddings`。向量写入时已经 L2 归一，
  cosine ≡ dot product。vec0 的默认距离是 L2²，我们映射回 cosine：
  `cos = 1 - distance/2`。
- **Hybrid (RRF)** — 每个 ranker 各取深一点的池子（`max(K*4, 50)`）→ RRF 融合
  （`k=60`, Cormack 2009 默认值）→ top-K。wire 上每条 hit 同时带 `lex_rank` /
  `vec_rank`，调用方能调试融合策略。

---

## `identity.db` — `Banyan.Identity`

驻留 OLS 形态的人机身份。十一张表映射到 OLS 的
`IUserStore` / `IUserPasswordStore` / `IUserEmailStore` / `IUserLockoutStore` /
`IUserRoleStore` / `IUserTwoFactorStore` / `IRoleStore` /
`IRefreshTokenStore` / `IClientStore` / `IAuthorizationCodeStore` /
`IDeviceCodeStore` / `IReferenceTokenStore`。

### 表

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

并发用每行的 `concurrency_stamp` 控制 — `SqliteUserStore` / `SqliteRoleStore`
每次 UPDATE 旋转 stamp，stamp 过期时返回 `IdentityErrors.ConcurrencyFailure()`。

### 为啥不用 `OLS.Root.EntityFramework`

官方 store 实装强依赖 Postgres（Npgsql）。Banyan 走 SQLite-only 就为了能单二进制
ship，所以全套 store 接口都用原生 `Microsoft.Data.Sqlite` 参数化 SQL 重写到
`SqliteUserStore` 等。**零 EF 依赖**。Migrations 是 `IdentityMigrations` 里手写的 DDL。

---

## `nipca.db` — `Banyan.Auth`

NID 证书签发机构的世界：所有签发过的证书 + 单调序列号计数器。

### 表

```
schema_migrations
nip_certs   (nid PK, serial UNIQUE, entity_type, pub_key, capabilities (JSON array),
             scope_json?, metadata_json?, issued_by, issued_at, expires_at,
             revoked_at?, revoke_reason?)
nip_serial  (id PK, next INTEGER)        # 单行，用 RETURNING 推进
```

`SqliteNipCaStore` 实装 `NPS.NIP.Ca.INipCaStore` — 喂给上游 `LabAcacia.NPS.NIP`
的 `NipCaService`。Serial 是单调零填充 16 字符 hex（`0000000000000001`、…），
游标天然有序。

### 为啥替换 `NPS.NIP.Storage.PostgreSqlNipCaStore`

上游包只 ship 一个 Postgres 后端 store + Postgres 形状的 `ConnectionString`
字段。Banyan 要 SQLite-only，所以 `EmbeddedNipCa.OpenAsync` 自己构造
`NipCaService`，直接注入我们的 SqliteNipCaStore + NipKeyManager — 绕开 PG 路径。

---

## 安全与运维考量

- **三个 DB 都跑 `PRAGMA journal_mode=WAL` + `foreign_keys=ON`** — 各
  `Migrations.ApplyAsync` 在跑 DDL 之前就设。
- **CA 私钥永远不在数据库里。** 它在 `BanyanNipCaOptions.KeyFilePath` 路径
  的 AES-256-GCM 加密 PEM 文件，启动时由 `NipKeyManager` 加载。
- **JWT 签名密钥同样落文件** —
  `Banyan.Identity.Crypto.PemSigningKeyLoader` 从
  `BanyanIdentityOptions.SigningKeyPath` 读 PKCS#8 PEM RSA。
- **Embedding 不加密。** 384 维向量泄露的信息量大致等于源文本，所以把
  `memory.db` 当一个信任单元。"按 namespace 加密" 留给以后。
- **Forget 是 GDPR 形态而非军规形态。** Tombstone 事件保留 `memory_id` 和
  reason，但内容 blob 丢弃。需要严格擦除的运维方应配合周期 `VACUUM` 加备份
  破坏性轮换。
