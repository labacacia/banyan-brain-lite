[English Version](./editions.md) | 中文版

# 三层版本：Lite · Pro · Ent

Banyan 分三层 ship，按 NPS 合规级别和部署拓扑划分。每层是独立发布（独立 Git
仓库 + NuGet stream），但 `innolotus-banyan-main/` 这个 source tree 是上游，
三层都从这里裁。

| 层 | NPS 合规 | 角色 |
|----|----------|------|
| **Lite** | NWP Memory Node（最小集） + **嵌入式自签 NIP Mini-CA** | 单进程、单 namespace、L0 anonymous |
| **Pro**  | NWP Memory Node + **外置 `nip-ca-server`**                   | 多租户（NID scope 隔离），L1 attested |
| **Ent**  | **AaaS Level 3**：Anchor Node 入口 + 多 Memory Nodes（带 Vector Proxy） + Bridge Node（接 legacy） + NOP 编排 + audit log + K-of-N 仲裁 | L2 verified |

当前公共仓库 [`labacacia/banyan-brain-lite`](https://github.com/labacacia/banyan-brain-lite)
实装的是 **Lite** 层。Pro 和 Ent 在
[`innolotus/banyan-brain-pro`](https://github.com/innolotus/banyan-brain-pro) 与
[`innolotus/banyan-brain-ent`](https://github.com/innolotus/banyan-brain-ent)。

---

## Lite 提供什么

- **一个 `banyan` 单二进制**，同时跑 NWP Memory Node + 内置 NIP Mini-CA
- 全部数据 SQLite（memory / identity / NID 账本）
- 单 namespace（默认 `default`；列结构存在以兼容 Pro 多租户，但 Lite 不强制隔离）
- L0 anonymous：`banyan serve --allow-anon`
- L1 attested 也支持（默认 — NWP 中间件 `RequireAuth=true`），但 Mini-CA 是
  唯一信任根，attest 是单租户的
- Demo Web UI：`banyan web`

Lite **包含**的 NPS 协议特性：

- ✅ `NPS.NWP.MemoryNode.IMemoryNodeProvider` — `BanyanMemoryProvider`
- ✅ NWP `QueryFrame` / `VectorSearchOptions` / `MemoryNodeSchema`
- ✅ `NeuralWebManifest` 在 `/.nwm`
- ✅ `NPS.Core.Frames.Ncp.AnchorFrame` 摘要在 query 响应里（`anchor_ref: sha256:...`）
- ✅ `NptMeter` token 估算（`token_est` header + body 字段）
- ✅ NIP Mini-CA：`EmbeddedNipCa` + `SqliteNipCaStore` + NPS-3 §8 标准
      HTTP 路由（`/v1/agents/...`、`/v1/ca/cert`、`/.well-known/nps-ca`）
- ✅ `RemoteNipCaClient` — Lite 也能作为 client 用（Lite agent 可以接到远端
      `nip-ca-server` 共享信任根，虽然 Lite 自己不跑一个）

Lite **不包含**的（属于 Pro / Ent）：

- ❌ 外置 `nip-ca-server` 部署（Pro）
- ❌ 每请求按 NID-scope 多租户隔离（Pro）
- ❌ L2 verified — Anchor Node 入口、NOP 编排、K-of-N 仲裁（Ent）
- ❌ Bridge Node legacy 协议适配（Ent）
- ❌ `NPS.NWP.ActionNode` / `ComplexNode`（我们只跑 MemoryNode）
- ❌ NPS-RFC-0002 v2 X.509 双信任 register（`/v2/agents/register`）

## Lite vs Pro

| 关注点 | Lite | Pro |
|--------|------|-----|
| NIP CA | `EmbeddedNipCa` 同进程 | 外置 `nip-ca-server`（Docker，Postgres） |
| 信任根 | 单个自签 Ed25519 keypair | `TrustedIssuers` 里多个远端 CA |
| 租户   | 单 `default` namespace | 每租户独立 namespace + 每个 IdentFrame 都校验 scope |
| Assurance | L0 anon（opt-in）或 L1 attested（默认） | L1 attested 强制；scope check 必校 |
| Memory Node 数量 | 1 | N（每个一个租户切片） |
| Postgres | 不用 | NIP CA 存储 + 可选共享 memory store |

## Lite vs Ent

Ent 是 **Agentic-as-a-Service Level 3**。Pro 的全部 + 还要：

- **Anchor Node** 作为入口边缘；客户端提交 AnchorFrame，Anchor Node 扇出到
  对应 Memory Node
- **Vector Proxy** 摆在 Memory Node 前面 — query 拆分、缓存、重排
- **Bridge Node** — 协议适配器，让非 NPS 客户端（legacy REST / gRPC / 等）
  能 round-trip 到 NPS
- **NOP 编排** — Neural Orchestration Protocol，跨 Action / Complex / Memory
  node 调度多步 agent 工作流
- **审计日志** 带密码学链（每条由产生它的节点签名；链可验证）
- **K-of-N 仲裁** 用于高风险操作（签发 / 撤销 需要 N 个 CA 中 K 个签名）
- **L2 verified** — 每个 IdentFrame 转发前 Anchor Node 还会做一轮新鲜 OCSP

## 当前 source tree 相对 Lite 的位置

`innolotus-banyan-main/` 工作副本实装了 **Lite 完整范围** 加少量 Pro 前向
兼容表面：

- `RemoteNipCaClient` — 已经是 Pro 消费的依赖；在 Lite 里无害（CLI 默认没人指）
- `Banyan.Web.NipCaEndpoints` — 把内置 Mini-CA 的 service 暴露成标准 NPS-3 §8
  HTTP 路由。Lite 看这就是"内置 CA 顺便公开它的 HTTP 形态"；Pro 看这就是
  `nip-ca-server`（Postgres 版）的模板，等到正式 cut Pro 仓库时复用

所以裁 Pro 时，Lite 原地不动，Pro 加层：

1. 把 `EmbeddedNipCa` 换成 `RemoteNipCaClient` 连独立的 `nip-ca-server` Docker
2. 加 `TenantContext` 中间件，从 `IdentFrame` 读 NID issuer + scope，套到
   `MemoryNodeOptions.Schema` 查询（过滤 `namespace` 列）
3. 加 `NPS.NWP.ActionNode` 中间件做租户 onboarding 动作
4. demo / Web UI 从"单组织"切到"租户选择器"
5. 加**共享记忆池** — 设计见 [ADR-001](./adr-001-memory-pools.cn.md)（NID-ACL
   容器、系统池 0、跨池 merge 搜索）

完整 Pro 阶段计划（含成本估算 + 依赖图）：
[`pro-roadmap.cn.md`](./pro-roadmap.cn.md)。

Ent 在 Pro 之上再加：

5. Anchor Node 入口（大概是独立 `Banyan.Anchor` 项目）
6. Vector Proxy（`Banyan.Embedders` 已经把 embedder 契约隔离 — proxy 在这一
   seam 拦截即可）
7. Bridge Node legacy 适配器
8. NOP 调度 + 审计日志 + K-of-N CA 仲裁

## 仓库策略

- 这个 source tree（`innolotus-banyan-main/`）是 **上游**
- `labacacia/banyan-brain-lite` 是 **公共 Apache-2.0 发布** — 在 Lite 边界裁
- `innolotus/banyan-brain-pro` 是 **商业 Pro 层** — 加多租户、外置 CA、scope 强校
- `innolotus/banyan-brain-ent` 是 **商业 Ent 层** — 加 AaaS L3 组件

同一个 Git 工作树通过分支 + 多 remote 喂三个仓库；每层分支只保留范围内的项目。
（目前还没分裂；这文档描述目标态。）
