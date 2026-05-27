[English Version](./shared-memory-pools.md) | 中文版

> **参见（规范参考）：**[ADR-001：共享记忆池](../../../../docs/architecture/adr-001-memory-pools.cn.md)

# Lite 共享记忆池

Lite 跟踪 issue：labacacia/banyan-brain-lite#16

## 目标

Lite 共享记忆池给本地 Banyan 节点一个小而清晰的共享模型，但不把 Lite 变成
企业访问控制产品。

这个能力服务于本地和自托管工作流：

- 一个用户区分 private、project、agent-session 记忆；
- 一个小型本地部署在可信 agent 之间共享项目上下文；
- 一个 workflow 需要 pool-shaped recall，但不需要 Pro/Ent 的 tenant 管理。

## 产品边界

Lite 只支持本地共享。它不提供：

- SaaS tenant 管理；
- 组织级管理后台；
- 企业 role/group ACL 管理；
- 跨客户或跨 tenant 共享；
- 强制依赖托管身份提供方。

NID 认证仍然有助于归因，但 Lite pool model 必须对单用户本地部署保持可用。

## Share Level

| Level | 含义 | 适用场景 |
| --- | --- | --- |
| `personal` | 一个本地用户/profile 的默认私有记忆范围。 | 偏好、长期用户事实、私有 agent 上下文。 |
| `local_workspace` | 同一部署或设备上的本地项目/workspace 池。 | 代码库笔记、项目决策、runbook、本地共享上下文。 |
| `agent_session` | 单个本地 agent/session workflow 的有界池。 | 临时任务记忆、多步本地自动化、可丢弃上下文。 |

Lite 不能从这些名字推断企业结构。它们是本地协作层级，不是 tenant 或
organization 边界。

## 资源模型

Lite 模型刻意小于 Pro/Ent：

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
  namespace              # 现有 Lite 标签
  pool_id?               # null 表示 private/default 行为
  source_type            # private | pool | knowledge_pack
```

`namespace` 仍是查询标签。`pool_id` 才是共享边界。一条记忆属于 private scope，
或属于一个本地 pool。

## 存储设计

SQLite 实现应添加 pool metadata，同时不破坏现有 namespace-only store：

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

迁移规则：现有记忆保持 private（`pool_id IS NULL`）。Lite 不能自动把旧
namespace 数据搬进共享池。

## API 设计

HTTP API 保持现有 memory endpoints，并增加一个小型 pool surface：

```http
POST   /api/pools
GET    /api/pools
GET    /api/pools/{pool_id}
PATCH  /api/pools/{pool_id}
DELETE /api/pools/{pool_id}

POST   /api/memory           body: { content, namespace?, poolId?, agentNid? }
GET    /api/memory/search    query: q, mode, k, namespace?, pools?
```

搜索行为：

- 没有 `pools` 参数时保持当前 private/namespace 行为；
- `pools=<id>` 搜索选定本地池；
- `pools=<id1>,<id2>` 搜索多个本地池并合并结果；
- 响应中，来自 pool 的 hit 必须包含 `sourceType` 和 `poolId`。

## CLI 设计

CLI 镜像本地 API：

```bash
banyan pool create <name> --level personal|local_workspace|agent_session [--desc TEXT]
banyan pool list
banyan pool show <pool-id-or-name>
banyan pool archive <pool-id-or-name>

banyan remember "text" --pool <pool-id-or-name> [--namespace NS]
banyan recall "query" --pool <pool-id-or-name> [--namespace NS]
```

Lite 第一版不需要 grant/revoke 命令。未来本地 multi-user profile 设计可以在
不改变核心 pool identity 的前提下补上。

## Knowledge Pack 边界

挂载的 Knowledge Pack 是来源材料，不是原生共享池记忆。Recall 可以组合：

- private memories；
- local shared-pool memories；
- mounted Knowledge Pack records。

结果必须保留 source label，让用户和 agent 能区分原生记忆与 pack-derived
context。

## 测试计划

实现时应覆盖：

- migration 后现有 namespace-only 记忆继续工作；
- private recall 不返回 pool memory，除非显式请求；
- `local_workspace` recall 只返回选定 pool 的记录；
- `agent_session` pool 彼此隔离；
- multi-pool recall 包含 `sourceType=pool` 和 `poolId`；
- Knowledge Pack recall 与原生 pool memory 保持不同标签。

## 非目标

- 企业 ACL。
- Tenant isolation。
- 跨设备 federation。
- 跨客户共享。
- Legal hold、managed retention、密码学 audit chain。
