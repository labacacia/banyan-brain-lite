[English Version](./nps-mapping.md) | 中文版

# NPS-3 映射

Banyan 怎么接到 [NPS-3](https://github.com/labacacia/NPS-Release) 栈 — 哪些
现成消费、哪些适配、哪里补缺。

NPS-3 分三层协议：

| 层 | 用途 | NuGet | Banyan 做什么 |
|----|------|-------|---------------|
| **NCP**（Neural Communication Protocol） | 帧格式、codec、registry | `LabAcacia.NPS.Core` | 直接消费 — 不自己写 framer |
| **NWP**（Neural Web Protocol） | Memory / Action / Complex 节点中间件、NWM manifest | `LabAcacia.NPS.NWP` | 直接消费 — `Banyan.Node` 接进去 |
| **NIP**（Neural Identity Protocol） | NID 证书机构、IdentFrame 校验 | `LabAcacia.NPS.NIP` | 部分消费 — service 层能用，HTTP routing 是空 stub，由我们补齐 |

---

## Memory Node — `IMemoryNodeProvider`

NPS.NWP 暴露通用 Memory Node 中间件：

```
services.AddMemoryNode<TProvider>(o => { /* schema, limits, auth flag */ });
app.UseMemoryNode<TProvider>(o => { /* same */ });
```

我们提供 `BanyanMemoryProvider : IMemoryNodeProvider`。它做**两件事**：

1. **把 `QueryFrame` 翻译成 `SearchQuery`。**

   ```
   QueryFrame.Filter        → SearchQuery.Text / Namespace
   QueryFrame.VectorSearch  → SearchMode.Vector  (filter 也带 text 时 → Hybrid)
   QueryFrame.Limit         → SearchQuery.K
   QueryFrame.Fields        → 行字段投影
   ```

   NPS 自带的 `NwpFilterTranslator` / `SqlQueryBuilder` 只支持 PostgreSQL 和 SQL
   Server dialect，所以我们没用 — filter 是个小 JSON DSL，自己解析。当前接受
   的形式：

   - `{"text": "..."}` → BM25 lexical
   - `{"text": "...", "namespace": "..."}` → 限定 namespace 的 lexical
   - 向量搜索通过 `frame.VectorSearch.Vector` 提供（agent 自己 embed）
   - text 和 vector 同时给 → Hybrid

2. **行 shape 对齐 `MemoryNodeSchema`。**

   我们公布的 schema（同时挂在 `/.schema`）：

   ```
   memory_id  TEXT  PK
   namespace  TEXT
   content    TEXT
   agent_nid  TEXT?
   created_at TEXT  ISO 8601 UTC
   updated_at TEXT
   score      REAL?    BM25 / cosine / RRF
   lex_rank   INT?     在 lexical 池里的 1-based rank
   vec_rank   INT?     在 vector 池里的 1-based rank
   ```

NWP 中间件处理其它一切 — frame 解析、anchor SHA-256、NPT token 计量
（用 `NptMeter`）、`next_cursor` 分页骨架、`X-NWP-*` headers、IdentFrame
校验门控。

### `banyan serve` 暴露什么

| 路径 | 方法 | 来源 | 用途 |
|------|------|------|------|
| `/api/memory/query`  | POST | NWP 中间件                  | 跑 `QueryFrame` → `BanyanMemoryProvider.QueryAsync` |
| `/api/memory/stream` | POST | NWP 中间件                  | 通过 `StreamAsync` 流式分页 |
| `/.nwm`              | GET  | Banyan (`MemoryNodeApp`)    | 公布的 `NeuralWebManifest` |
| `/.schema`           | GET  | Banyan (`MemoryNodeApp`)    | `MemoryNodeSchema` JSON（NWP 中间件占用了 `/api/memory/*`） |
| `/api/health`        | GET  | Banyan                      | liveness |
| `/api/agents/*`      |      | `Banyan.Web.AgentEndpoints` | demo 形态的 agent 管理（仅 CA 加载时） |
| `/api/ca`            | GET  | `Banyan.Web.CaEndpoints`    | CA 信息（仅 CA 加载时） |
| `/v1/agents/*`<br/>`/v1/nodes/register`<br/>`/v1/ca/cert`<br/>`/v1/crl`<br/>`/.well-known/nps-ca`<br/>`/health` | 多种 | `Banyan.Web.NipCaEndpoints` | **NPS-3 §8 标准 CA HTTP API**（仅 CA 加载时） |

---

## NID 身份 — `EmbeddedNipCa` / `RemoteNipCaClient`

NPS.NIP 提供 **service** 层：

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

…和验证器：

```
NipIdentVerifier(NipVerifierOptions, IHttpClientFactory, ILogger)
  .VerifyAsync(IdentFrame frame, NipVerifyContext)
```

它**没有** ship 的：HTTP routing。`app.MapNipCa()` 在 alpha.4 和 alpha.5
都注册 0 个 endpoint（[Spike 验证见下](#spike)）。Go 参考实装在
[`labacacia/nip-ca-server/example/go`](https://github.com/labacacia/nip-ca-server)
按 NPS-3 §8 定义了完整路径布局。

### Banyan 提供什么

`Banyan.Web/Endpoints/NipCaEndpoints.cs` 把缺失的路由挂上，内部调上游
`NipCaService` 处理签发 / 校验：

| 路径 | 方法 | 映射到 | 备注 |
|------|------|--------|------|
| `POST /v1/agents/register` | `NipCaService.RegisterAsync("agent", …)` | 签发 Ed25519 IdentFrame |
| `POST /v1/nodes/register`  | `NipCaService.RegisterAsync("node", …)`  | 签发节点 IdentFrame |
| `POST /v1/agents/{nid}/renew`  | `NipCaService.RenewAsync(nid)` | 要求证书已进入 `RenewalWindowDays` |
| `POST /v1/agents/{nid}/revoke` | `NipCaService.RevokeAsync(nid, reason)` | 发 `RevokeFrame` |
| `GET  /v1/agents/{nid}/verify` | `NipCaService.VerifyAsync(nid)` | 也覆盖 OCSP 形态校验 |
| `GET  /v1/ca/cert`             | `NipCaService.GetCaPublicKey()` | 发现：NID + display name + pubkey |
| `GET  /v1/crl`                 | `EmbeddedNipCa.ListAsync(revokedOnly:true)` | 撤销列表 |
| `GET  /.well-known/nps-ca`     | 发现 payload | 算法、endpoints、max 有效期 |
| `GET  /health`                 | liveness |

加载到 `EmbeddedNipCa` 时，**`banyan web`（`Banyan.Web/WebApp`）和
`banyan serve`（`Banyan.Node/MemoryNodeApp`）都会挂这套路径** — 所以
Memory Node 可以同时充 CA，或者拆到不同 host。

### 客户端

`RemoteNipCaClient`（在 `Banyan.Auth`）是 `HttpClient` 薄包装，路径与 NPS spec
逐字对齐。表面与 `EmbeddedNipCa` 等价，调用代码统一；CLI 通过 `--remote URL`
（或 env `BANYAN_CA_URL`）切换。

将来 NPS.NIP 真补上 `app.MapNipCa()` 路由后，我们的 endpoint 类就冗余了，CLI
不用动 `RemoteNipCaClient`。

---

## Wire 格式

Banyan 当前产生或消费的帧：

| 帧                                | 生产方                                | 消费方                                     |
|-----------------------------------|---------------------------------------|--------------------------------------------|
| `NCP.HelloFrame`                  | NWP 中间件                            | NWP 客户端                                  |
| `NWP.QueryFrame`                  | NWP 客户端                            | `BanyanMemoryProvider`                      |
| `NWP.VectorSearchOptions`         | NWP 客户端                            | `BanyanMemoryProvider`                      |
| `NIP.IdentFrame`                  | `NipCaService.RegisterAsync`          | Memory Node 上的 `NipIdentVerifier`         |
| `NIP.RevokeFrame`                 | `NipCaService.RevokeAsync`            | 分发 CRL 的客户端                            |
| `NIP.TrustFrame`（跨 CA trust）   | 暂未生产                              | n/a                                         |

编码 tier（来自 `NPS.Core.Frames.EncodingTier`）：当前一律走 **Tier1（JSON）** —
manifests、query body、IdentFrame、CRL。Tier2（MsgPack）和 AnchorFrame 都没用，
压力上来再切（`NPS.Core.Codecs.Tier2MsgPackCodec` 是现成的）。

---

## Spike

设计前跑过两个可复现 spike：

1. **alpha.4 routing spike**：参考 `nip-ca-server` 用的就是
   `LabAcacia.NPS.NIP` 1.0.0-alpha.4。我们用同包跑 `services.AddNipCa(...)` +
   `app.MapNipCa()`，dump `EndpointDataSource.Endpoints` — 数量 **0**。
   （`NipKeyManager` 倒是成功加载了 key，DI bind 半工作。）
2. **alpha.5 routing spike**：换 `LabAcacia.NPS.NIP` 1.0.0-alpha.5 重跑同样
   DI 路径。结果一样：**0 endpoint**。

这俩 spike 就是为啥 Banyan ship 自己的
`Banyan.Web/Endpoints/NipCaEndpoints.cs`，而不是等上游补齐。Endpoint shape 直接
对齐 Go 参考 `labacacia/nip-ca-server/example/go/api/api.go`，所以跨语言客户端
（Go / Java / Rust / Python — 参考 repo 里都列了）能直接对接 Banyan host 的
CA。

## 还没映射的

- **NCP `AnchorFrame`** 用于 memory 锚定 — NWP 查询响应已经带
  `anchor_ref: sha256:...`，但我们不单独生产或保存 AnchorFrame。
- **`ActionNode` 和 `ComplexNode`** 中间件 — NPS.NWP 提供（反射看到了），
  Banyan 当前只做 Memory Node 角色。
- **MsgPack Tier2 codec**：现在全部 JSON / Tier1，切 Tier2 是 per-route
  content-type 协商问题，体量上来再做。
- **NPS-RFC-0002 dual-trust X.509 register** — v2 endpoints
  （`POST /v2/{agents,nodes}/register`）同时发 Ed25519 帧和 2-cert X.509 链。
  NPS.NIP alpha.5 ship 了 `NPS.NIP.X509` 类型，但 Banyan 还没暴露 v2 路径。
