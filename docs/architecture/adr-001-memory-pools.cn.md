[English Version](./adr-001-memory-pools.md) | 中文版

# ADR-001：共享记忆池（Pro 层）

- **状态**：Proposed（提议）
- **日期**：2026-05-02
- **层级**：Pro（不在 Lite 中）
- **取代**：—
- **被取代**：—

## 上下文

当前 Banyan（Lite）用 `namespace` 来组织记忆 — 一个自由字符串，比如
`user-alice` 或 `project-acme`。Namespace 是**标签**，不是**围栏**：任何能
访问 HTTP 的人都能读写任何 namespace；`agentNid` 要么是自由字符串要么是
（`feat/nid-auth-lite` 之后）服务端校验过的身份戳。除了客户端自觉遵守的
命名约定之外，没有"这一组记忆属于这一组 agent"的概念。

两个场景把这个边界推爆：

1. **多 agent 项目协同。** 一个 agent 扫了代码库后往 `project-acme` 写了
   200 条事实。第二个加入项目的 agent 今天还是得重新扫一遍 — 没有任何
   信号告诉它 `project-acme` 是共享的、可信的。我们希望它能直接读到第
   一个 agent 的成果而不是重做工作，同时仍然能审计这些事实是谁写的。
2. **系统初识。** 一个新接入 Banyan 实例的 agent 完全不知道有哪些
   namespace、host 用什么 schema、operator 想要什么约定。今天这是 README
   级别的工作，活在协议外面 — agent 没法从 wire 上自启蒙。

Lite 故意保持简单 — namespace + 校验过的 `agentNid` 对单组织场景够用。但
一旦你有多个不同 NID 下的 agent 在协同，就需要一等公民的、带读写 ACL 的
组共享存储。

## 决策

Pro 引入**记忆池（memory pools）**— 命名的、有 owner 的、按 NID 做 ACL
的容器，与 namespace **正交**。

- **池**有 `id`、`name`、owner NID、可选描述、和一组授权
  （`{principal_nid, permission}` 行）。
- 权限分 `read | write | admin`。`admin` 包含读写 + 授权 / 撤权。
- 一条记忆**只属于一个池**。`namespace` 保留为**池内标签**（所以
  `pool=team-acme, namespace=session-42` 是完全合理的地址）。
- 池成员**只挂 NID**。人类 operator 通过自己拥有的 agent 代为操作，与双
  轨身份模型一致 — 机器走 NID、人走 OLS / OIDC — 同时避免引入
  OLS↔NID 映射表。
- **池 0 是系统池**，每个 Banyan 实例都有。
  - 读 ACL：`('*', 'read')` — 每个鉴权过的 NID 都能读。
  - 写 ACL：operator 配置里显式列出的 NID
    （`BanyanNodeOptions.system_pool_writers`）。其他人都不能写，
    池"owner"也不行 — 池 0 的写权由 operator 钉死，不通过 `admin` 授权。
  - 初始内容（operator 可选种入）："如何往这个 Banyan 实例写记忆"、
    schema 说明、namespace 约定、其他可发现池列表、保留策略。

### 数据库 schema

`memory.db` 加两张表，三张已有表加 `pool_id` 列：

```sql
memory_pools(
  pool_id      INTEGER PK,
  name         TEXT NOT NULL UNIQUE,
  owner_nid    TEXT NOT NULL,         -- 仅池 0（系统）为 NULL
  description  TEXT,
  created_at   TIMESTAMP NOT NULL
);

pool_acl(
  pool_id      INTEGER NOT NULL FK→memory_pools ON DELETE CASCADE,
  principal    TEXT NOT NULL,         -- NID，或 '*'（仅系统池通配符）
  permission   TEXT NOT NULL CHECK (permission IN ('read','write','admin')),
  granted_by   TEXT,                  -- 授权者 NID（系统种入为 NULL）
  granted_at   TIMESTAMP NOT NULL,
  PRIMARY KEY (pool_id, principal, permission)
);

memories_current.pool_id  INTEGER NOT NULL DEFAULT 0 FK→memory_pools
memory_events.pool_id     INTEGER NOT NULL DEFAULT 0 FK→memory_pools
embeddings.pool_id        INTEGER NOT NULL DEFAULT 0  -- 反范式以加速过滤
```

迁移时建池 0：`name='system'`、`owner_nid=NULL`。`'*'` 通配符**只**在
读路径硬编码 — 通配符永远不匹配 write / admin。

### API

读端点接受池列表，merge 结果：

```
GET /api/memory/search?q=...&pools=0,42,team-acme&mode=hybrid&k=10
GET /api/memory/{id}                  ← 服务端按记忆所属池检查读 ACL
```

写端点接受目标池。中间件验证调用方在该池有 `write` 权限。服务端校验过的
NID（来自 `Authorization: NID`）就是 principal — 任何池都不允许匿名写。

```
POST   /api/memory                    body: {pool, namespace, content, ...}
PUT    /api/memory/{id}               body: {content}             ← 读+写检查
DELETE /api/memory/{id}                                           ← 读+写检查
```

池管理是独立端点组，CLI 镜像：

```
POST   /api/pools                     body: {name, description}      → 创建（owner = 调用方 NID）
GET    /api/pools                                                     → 列出调用方能读的池
GET    /api/pools/{id}                                                → 详情 + 授权（admin 才看到完整授权列表）
PATCH  /api/pools/{id}                body: {description}             → owner / admin
POST   /api/pools/{id}/grants         body: {principal_nid, permission} → 仅 admin
DELETE /api/pools/{id}/grants/{nid}/{perm}                            → 仅 admin
DELETE /api/pools/{id}                                                → 仅 owner；存在非墓碑记忆时拒绝
```

CLI：
```
banyan pool create  <name> [--desc]
banyan pool list
banyan pool show    <name|id>
banyan pool grant   <name|id> <principal-nid> <read|write|admin>
banyan pool revoke  <name|id> <principal-nid> [--perm read|write|admin]
banyan pool delete  <name|id>
```

### 多池搜索 merge

请求列出多个池时，服务端对调用方有读权的每个池跑相同的
hybrid/lexical/vector 查询，过滤掉调用方不能读那个池的命中（纵深防御），
按 `score` 降序合并，稳定 tiebreak 用 `(pool_id, memory_id)`。响应里
每个 hit 都带 `pool_id` 让客户端展示来源。

### 池 0 维护

operator 配置暴露系统池写者集合：

```yaml
# banyan.config.yml (Pro)
system_pool:
  writers:
    - urn:nps:agent:banyan:operator
    - urn:nps:agent:banyan:doc-bot
  seed_path: /etc/banyan/system-pool-seed.md   # 可选；首次启动加载
```

启动任务读 `seed_path`（带 `---` frontmatter 分块的 markdown），幂等地把
新条目写入池 0，归属一个合成的 `urn:nps:agent:banyan:system` NID（由本地
CA 签发）。后续编辑通过普通写 API 由配置的写者 NID 操作。

## 理由

- **为啥不直接给 namespace 加 ACL？** Namespace 是用户层的**标签**，我们
  希望它能随手起 — `namespace=user-X` 是约定，不是强制。给 namespace 加
  ACL 等于追溯地把每个现存 namespace 都变成安全边界，强迫调用方在写之
  前先声明命名约定。池引入了一条独立的、显式管理的轴；namespace 仍是标签。
- **为啥一条记忆只属一个池？** 多池归属在 update / forget 时语义混乱。
  跨池共享发生在**读时**（多池搜索），不在**存时**。如果一条记忆真的
  要同时进两个池，就写两份 — 它们成本低，事件归属也对得上各自作者。
- **为啥只挂 NID？** Banyan 身份模型本来就双轨 — 机器走 NID、人走 OLS。
  池 ACL 在 agent 层；人类 operator 通过签发 NID 给自己控制的 agent 来
  授权。避免引入 OLS↔NID 映射表（跨实例就更糟），让 ACL 检查保持本地。
- **为啥通配符只用在读？** 池 0 需要"每个接入 agent 都能读"。通配符写
  与匿名写不可区分，而我们已经在中间件层显式拒绝匿名写。
- **池 0 为啥要特殊处理？** 它在每个实例都存在、没有 owner（operator
  托管）、要通配符读权、写者由配置钉死而非 API 授权。把这些做成运行时
  可改会让 operator 不小心把自己锁出自己的 onboarding 池。

## 后果

### 正面

- 多 agent 项目协同变成一等公民 — 共享池、跨池搜索、每条事实归属于
  写者 NID。
- 系统池让每个 Banyan 实例都自带一个自我说明的入口；新 agent 可以
  `search?pool=0&q="how do I write memories"` 自启蒙。
- ACL 在服务端的同一个 NID 鉴权中间件位置上 enforce（一个接缝，不四散）。
- Lite 不变。池是 Pro 卖点；Lite 用户后续迁移时已有记忆默认全部归到
  池 0（或一个 per-tenant 默认池），客户端不用改。

### 负面 / 成本

- `memory.db` 真要做迁移 — 三张表加 `pool_id` 列、两张新表、加上现存
  记忆 backfill（迁移时全部默认 → 池 0，operator 后续重分池）。
- 每条 search/write 路径多一次 ACL 查找。`pool_acl(principal, pool_id)`
  上索引查找成本低（每租户表都不大），但毕竟是新增热路径查询。
- 新增 CLI（六个子命令）+ web UI 面板（池列表 + 授权）。整体含测试 +
  文档估 2-3 天工作量。
- 跨池搜索把结果合并交给服务端；跨池 RRF 归一化要做小校准测试（否则
  一个池 10 个密集命中可能淹没另一个池里 2 个强命中）。

### 不在范围（属于 Ent）

- 池 ACL 变更的密码学审计链（Ent：签名审计日志）。
- 跨实例池联邦 — 多 Banyan 节点间的池复制不在本 ADR 解决。
- 高风险池操作的 K-of-N 审批（delete、批量 revoke）。

## 待定

- ✅ **池成员身份**：NID-only — 人类通过自己的 agent 访问。_2026-05-02 决定。_
- ⏳ 池 0 种子内容是否还要包含**机器可读的** schema 文档（让 agent 自省
  端点而非解析散文）？大概率要；具体到实现时再定。
- ⏳ 每池配额（最大记忆数、最大字节数）？大概率 Ent 级 — Pro 暂时让
  operator 监控 + 手动清。

## 参考

- [`docs/architecture/editions.cn.md`](./editions.cn.md) — Lite/Pro/Ent 矩阵
- [`docs/architecture/storage-tiers.cn.md`](./storage-tiers.cn.md) — 本 ADR
  扩展的现有 `memory.db` schema
- [`docs/architecture/identity.cn.md`](./identity.cn.md) — 双轨身份模型
  （为啥只挂 NID）
- `feat/nid-auth-lite`（commit `c83c9ee`）— 本 ADR 依赖的服务端校验过的
  NID 中间件
