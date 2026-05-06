[English Version](./pro-roadmap.md) | 中文版

# Pro 层路线图

本文规划 `innolotus/banyan-brain-pro` 在 Lite 之上要加什么。它是
[`editions.cn.md`](./editions.cn.md) 的规划副本 — 那篇画 Lite / Pro / Ent
高层矩阵；这篇把 Pro 拆成可发布的阶段，并把每个阶段钉到管它的设计文档。

> **状态**：Pro 层尚未从上游切出。这是设计期路线图，不是发布日程。每个
> 阶段以"工作日"估算，不是日历日期。

---

## 层级边界回顾

Lite 是**单组织、单进程、内嵌 Mini-CA、namespace 即标签**。Pro 是**多
租户、CA 外置、按 NID scope 隔离、可共享池**。Ent 是 **AaaS L3** —
Anchor Node + Vector Proxy + Bridge Node + NOP 调度 + 审计链 + K-of-N 仲裁。

干净的思考方式：Lite 为"一个 agent、一个 operator、一台机器"优化。Pro
加入了多团队 operator 在**一个 Banyan 部署**上托管**多个组织**而互不可见
所需的原语。Ent 在此之上加联邦、审计、仲裁式签名治理 — 受监管环境用得上。

---

## 阶段计划

每个阶段独立可合并，且层层解锁后续。顺序是依赖驱动 — 前置阶段解锁后续。

### P-Pro.1 — 外部 `nip-ca-server` 集成

**目标**：默认启动路径不再用内嵌 Mini-CA；信任根来自一个或多个远端
`nip-ca-server` 部署。

**范围内**：

- `MemoryNodeApp` / `WebApp` 默认改为 `RemoteNipCaClient` 而非
  `EmbeddedNipCa`。`EmbeddedNipCa` 仍可用于开发循环 + Lite，只是不再是
  Pro 默认接线。
- 可配置 `TrustedIssuers` 列表，从 CA 的 `/.well-known/nps-ca` 发现文档
  拉取。
- 周期性 CRL 刷新（`/v1/crl`）到本地 `LocalRevokedSerials` — Pro 不需
  要每次校验都走 OCSP 往返。
- Docker + Postgres 跑 `nip-ca-server` 的运维文档。

**范围外**：`NidAuthenticationMiddleware` 怎么咨询吊销不变。中间件已经
支持内嵌 CA 或本地吊销集合两种来源；Pro 只是倾向后者。

**成本**：约 1 天。主要是接线 + 一个 CRL 刷新 worker + 文档。

**依赖**：无（Lite 的 `feat/p3.1-remote-ca` 已经有 `RemoteNipCaClient`）。

### P-Pro.2 — 共享记忆池

**目标**：实装 [ADR-001](./adr-001-memory-pools.cn.md) — 池为 NID-ACL
容器、系统池 0 由 operator 钉死写者集合、跨池 merge 搜索。

**范围内**：

- `memory.db` 迁移：`memory_pools`、`pool_acl`、`memory_events` /
  `memories_current` / `embeddings` 三表加 `pool_id` 列。
- `IPoolAuthorizer` 服务注入到 `MemoryEndpoints`，每次读写前 ACL 检查。
- 池 CRUD 端点 + 授权 / 撤权端点。
- `/api/memory/search` 加 `&pools=...` 参数；服务端 merge。
- CLI：`banyan pool create/list/show/grant/revoke/delete`。
- 系统池 0 种子加载器 + 写者列表配置。
- Web UI：Memory / Agents / About 旁边加 "Pools" tab，展示当前身份能看
  到的池，owner 可编辑授权。

**范围外**：跨实例池联邦（Ent）、每池配额（大概率 Ent）、授权变更的
审计链（Ent）。

**成本**：约 2-3 天。schema 迁移 + ACL 接线是大头；搜索 merge + CLI 跟
着来。

**依赖**：P-Pro.1（写者 NID 由 Pro 实例信任的外部 CA 签发）。

### P-Pro.3 — 租户作用域中间件

**目标**：把校验过的 NID 的 `scope.tenant` 声明变成强制过滤。在租户
`acme` 下签发的 NID 只能看到属于该租户的池和记忆。

**范围内**：

- `NidAuthenticationMiddleware` 之后的新中间件：从校验过的 IdentFrame
  scope 提取 `tenant_id`，盖到 `HttpContext.Items["banyan.tenant_id"]`，
  目标池 / 记忆跨租户的请求直接拒。
- `memory_pools.tenant_id` 列（系统池 0 可空）。
- 跨租户管理覆盖：`tenant_admin` 能力让 operator NID 跨租户排错。
- IdentFrame 签发流程加 tenant 声明 schema（CA 侧；P-Pro.1 的
  `nip-ca-server` 部署文档说怎么按 agent 设 `scope.tenant`）。

**范围外**：每租户**资源配额**。租户隔离讲的是可见性，不是容量；配额
属于 Ent。

**成本**：约 1-2 天。中间件 + 一个列 + 几处守卫；测试是长尾。

**依赖**：P-Pro.1（CA 必须签发带租户作用域的帧）、P-Pro.2（池是被作用
域化的单元）。

### P-Pro.4 — Postgres backing（可选路径）

**目标**：当部署超出单机 SQLite 时，把 `nipca.db` 和（可选）`memory.db`
迁到 Postgres。

**范围内**：

- `Banyan.Auth.Stores.PostgresNipCaStore` — 与 SQLite 版同样的
  `INipCaStore` 接口，连同一个 `nip-ca-server` Postgres schema，磁盘
  格式保持兼容。
- 可选 `Banyan.Lite.Postgres` — `IMemoryStore` 走 Postgres + pgvector。
  这是真的可选；很多 Pro 部署单 Memory Node SQLite 就够。
- `BanyanNodeOptions` 加连接串配置；不设 Postgres URL 时 SQLite 仍是默认。

**范围外**：跨节点记忆复制（每 Memory Node 仍是单写者 — 那是 Ent）。

**成本**：仅 `nipca.db` 走 Postgres 约 3-4 天；`memory.db` 也跟上则翻倍
（FTS5 → Postgres FTS 或 Tantivy、向量 → pgvector）。现实里 Pro v1 大概
只把 `nipca.db` 上 Postgres，`memory.db` 留在 SQLite。

**依赖**：P-Pro.1。

### P-Pro.5 — NWP ActionNode + 管理端点

**目标**：把租户生命周期（`create_tenant`、`add_writer`、`rotate_key`）
暴露成 NWP `ActionFrame`，而非临时 REST。这让 operator 体验从 Banyan
私有变成 NPS 原生。

**范围内**：

- `app.UseActionNode<BanyanAdminProvider>` 挂在 `/api/admin`。
- `BanyanAdminProvider` 暴露租户操作、池操作、CA 操作的小动作集。
- 帧由带 `tenant_admin` 能力的 operator NID 签名。
- Admin web UI 改指向 ActionNode（取代当前 `IdentityEndpoints` 的管理面）。

**范围外**：NOP — 多步编排属 Ent。

**成本**：约 2 天。

**依赖**：P-Pro.3（动作要在已有的租户作用域上操作）。

### P-Pro.6 — 租户感知的 Web UI

**目标**：现在的 demo UI 是单组织。Pro 的 UI 让 operator NID 切租户、
看每租户的记忆 / 池 / agent 计数、不离页面就能下钻到某租户数据。

**范围内**：

- header 里加租户切换器（仅 `tenant_admin` NID 可见）。
- Memory / Agents / Pools 各 tab 加每租户过滤器。
- "Onboard tenant" 向导调用 P-Pro.5 的 ActionNode。

**范围外**：超出"上述 API 之上的薄客户端"的任何东西。

**成本**：约 2 天。

**依赖**：P-Pro.5。

---

## 横切工作（贯穿每个阶段）

- **测试**：保持 Lite 的测试姿势 — 真实 WebApplication 临时端口、真
  CA、真帧。新增 `Banyan.Pro.Tests` 项目，镜像 Lite 测试布局。新代码
  目标行覆盖率 ≥80%，鉴权 + ACL 路径 100%。
- **文档**：每阶段交付 EN + CN 文档。P-Pro.2 落地 ADR-001（已起草）；
  后续阶段每个加一份 ADR 或扩 `editions.md` / `nps-mapping.md` 中的章节。
- **迁移安全**：每次 schema 变动随附幂等迁移脚本 + `banyan migrate
  --dry-run` CLI 选项。
- **向后兼容**：Lite 的 `memory.db` 在 Pro 里能干净打开（全部归到池 0
  + 默认租户）。反向（Pro → Lite）不支持；Pro 的库引用 Lite 没有的表。

---

## 完全不在 Pro（= Ent）

这些特性留给 `innolotus/banyan-brain-ent`：

- **Anchor Node 入口**带 fan-out 到 Memory Node
- **Vector Proxy** 在每个 Memory Node 之前（查询切分、缓存、rerank）
- **Bridge Node** 老协议适配器
- **NOP** 在 Action / Complex / Memory 节点上的编排
- **密码学审计链**（每节点签名事件日志，端到端可验证）
- **K-of-N CA 仲裁**用于高风险操作
- **L2 verified** — 每条 IdentFrame 由 Anchor Node 用新鲜 OCSP 探针
  二次校验
- **跨实例池联邦**、**每池配额**、**受监管保留策略**

分界线：Pro 是"在一个部署上安全托管多组织"。Ent 是"在受监管行业里跑，
每个动作要纸面留痕、每把签名密钥要 K-of-N 见证"。

---

## 建议合并顺序

```
P-Pro.1（外部 CA）           ──┐
                                ├──→ P-Pro.4（Postgres CA store，可选）
P-Pro.2（池，ADR-001）       ──┤
                                │
                                └──→ P-Pro.3（租户作用域）
                                          │
                                          └──→ P-Pro.5（ActionNode 管理）
                                                    │
                                                    └──→ P-Pro.6（租户感知 UI）
```

P-Pro.1 + P-Pro.2 互相独立，谁先发都行；P-Pro.4 是平行可选轨。从
P-Pro.3 起严格串行。

## 参考

- [`docs/architecture/editions.cn.md`](./editions.cn.md) — Lite / Pro /
  Ent 高层矩阵
- [`docs/architecture/adr-001-memory-pools.cn.md`](./adr-001-memory-pools.cn.md)
  — P-Pro.2 实装的池设计
- [`docs/architecture/nps-mapping.cn.md`](./nps-mapping.cn.md) — Lite
  覆盖了哪些 NPS 表面、Pro 补什么
- [`labacacia/nip-ca-server`](https://github.com/labacacia/nip-ca-server)
  — Pro 依赖的外部 CA
