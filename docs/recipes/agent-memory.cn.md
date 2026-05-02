[English Version](./agent-memory.md) | 中文版

# Recipe：用 Banyan 给 Agent 做持久记忆

这份 recipe 写给 **agent 作者** —— Claude、GPT、自研助手，任何会跟用户对话、
需要跨 session 持久记忆的程序。它把整个接入循环讲完。

> 假设你要连的 Banyan 实例已经搭好。如果要自己起一个，看项目根
> [`README.cn.md`](../../README.cn.md) 的 Quick Start。

---

## TL;DR

整个接入就两个 HTTP 调用：

```
新用户消息
   │
   ▼
GET /api/memory/search?q={msg}&mode=hybrid&k=5&namespace=user-{user_id}
   │
   ▼ 过滤： score > 0.50 才纳入
   ▼
作为 "Relevant memory:" 拼到 system prompt
   │
   ▼
生成回复
   │
   ▼ 用户显式说 "记住 X" / 纠正你 / 表达硬偏好时
   ▼
POST /api/memory  {content, namespace, agentNid?}
```

文档剩下的部分都是怎么选 namespace、怎么选阈值、什么时候触发 write。

---

## 1. 选接入方式

| 方式 | 适用场景 | 代价 |
|------|----------|------|
| **HTTP REST** (`/api/memory/*`) — Banyan-spec, demo 友好 | 大多数 agent，任何语言，脚本。**默认**。 | 每轮一次 HTTP |
| **NWP `QueryFrame`** (`POST /api/memory/query`) — NPS-3 wire | 跨语言 NPS 互操作，要 frame 级特性（anchor_ref、token 预算） | 同上 — 仅 body 不同 |
| **NID 鉴权** (`Authorization: NID <base64(IdentFrame)>`) | 多租户生产、审计追溯、scope 校验（Pro 层） | agent 一次性发 NID + 每请求带 frame |
| **进程内 .NET** (`SqliteMemoryStore` 直接用) | Agent 是 .NET 二进制，跟 Banyan 同进程 | 零（同进程） |

普通 agent，HTTP REST 是合理起点。要 wire 上的 anchor 摘要 / token 预算时换 NWP；
要服务端审计"谁写的"时上 NID。

---

## 2. 召回循环

每次生成回复前，先按用户的 namespace 搜索，过阈值再注入。

### curl 基线
```bash
curl "http://banyan-host:5180/api/memory/search?q=$(jq -nr --arg q "$user_msg" '$q|@uri')&mode=hybrid&k=5&namespace=user-${USER_ID}"
```

### Python
```python
import requests, urllib.parse

BASE = "http://banyan-host:5180"
NS   = f"user-{user_id}"

def recall(query: str, k: int = 5, threshold: float = 0.50) -> list[str]:
    """返回要注入到 system prompt 的相关记忆片段。"""
    r = requests.get(f"{BASE}/api/memory/search", params={
        "q": query, "mode": "hybrid", "k": k, "namespace": NS,
    }, timeout=2)
    r.raise_for_status()
    hits = r.json()["hits"]
    return [h["content"] for h in hits if h["score"] > threshold]

# 在生成循环里：
context_lines = recall(user_message)
if context_lines:
    system_prompt += "\n\n## 相关记忆\n" + "\n".join(f"- {c}" for c in context_lines)
```

### TypeScript
```typescript
async function recall(query: string, k = 5, threshold = 0.5): Promise<string[]> {
  const u = new URL("/api/memory/search", "http://banyan-host:5180");
  u.searchParams.set("q", query);
  u.searchParams.set("mode", "hybrid");
  u.searchParams.set("k", String(k));
  u.searchParams.set("namespace", `user-${userId}`);
  const r = await fetch(u, { signal: AbortSignal.timeout(2000) });
  const { hits } = await r.json();
  return hits.filter((h: any) => h.score > threshold).map((h: any) => h.content);
}
```

### 阈值经验值（`bge-small-zh` embedder）

| Cosine | 含义 | 推荐处理 |
|--------|------|----------|
| `> 0.55` | 强语义匹配。同一话题、同一意图。 | 注入 context |
| `0.50–0.55` | 弱 / fishing。Top-1 可能相关，尾部是噪音。 | 仅当它是 #1 时注入 |
| `< 0.50` | Vector search 兜底返回 — top-K 永远会返回点东西，即使全无关。 | 丢 |

如果你换了别的 embedder（比如把 `Banyan.Embedders.OnnxEmbedder` 换成
all-MiniLM-L6-v2），用一组 ~50 条标注样本重新校准阈值。

### 模式 — 啥时候用哪个

- **`hybrid`** — 默认。BM25 + 向量 + RRF 融合，字面匹配和同义词都能抓到。
- **`vector`** — 用户措辞跟存储记忆字面没交集时（"我怎么部署" → 存储里写的是
  "生产环境上线步骤"）。
- **`lexical`** — 你有确切字符串要精确 / 前缀匹配时（比如召回某个 NID、文件路径、
  错误码）。

---

## 3. 写入策略

**不是每一轮对话都要存。**把每个"嗯"、"好的"都存下来会让真信号淹没在噪音里。
触发写入的信号：

| 触发 | 例子 | 动作 |
|------|------|------|
| 用户显式 "记住 X" | "记住我喜欢深色模式" | `POST /api/memory` |
| 用户纠正 | "实际上是 Postgres 不是 MySQL" | `DELETE /api/memory/{old}` + `POST /api/memory {new}` |
| 用户偏好 / 约束 | "我总希望回答简洁" | 把规则原样 `POST` |
| 决策落定 | "我们用 A 方案" | `POST` 决策本身 + 一行理由 |
| 身份 / 上下文信息 | "我在数据团队，工作时间 EST" | `POST`（长效，下次还有用） |

不存：
- "谢谢" / "好" / 寒暄
- 重复你刚说过的话
- `docs/` / `README` / 代码 / `git log` 已经有的信息

### 元数据建议

```json
{
  "content":   "<事实，完整一句>",
  "namespace": "user-{user_id}",
  "agentNid":  "urn:nps:agent:local.banyan:claude-assistant",
  "metadata":  {
    "source":     "conversation",
    "session_id": "...",
    "captured_at": "2026-05-02T01:23:45Z"
  }
}
```

`agentNid` 标记"这条是哪个 agent 写的"，多 agent 共享 namespace 时关键。
注意 Lite 层 `agentNid` 是自由字符串；**Pro 层** 它会变成 IdentFrame 校验后的
真实身份。

### 更新 / 遗忘

```bash
# 原地修正 — 追加 Update 事件、刷新 lex+vec 索引
curl -X PUT  "http://banyan-host:5180/api/memory/{id}" \
     -H 'content-type: application/json' \
     -d '{"content":"修正后的事实"}'

# 墓碑 — 从搜索面消失，审计 trace 仍在
curl -X DELETE "http://banyan-host:5180/api/memory/{id}?reason=user-correction"
```

---

## 4. Namespace 设计

Namespace 是 Lite 层 Banyan 唯一内建隔离开关。把它当**逻辑作用域**，不是
安全边界（安全边界是 NID-level，Pro 层）。

| 约定 | 寿命 | 例 |
|------|------|----|
| `user-{user_id}` | 永久 | 每个最终用户的持久记忆 |
| `session-{session_id}` | 几小时 | 单次对话的短期上下文 |
| `project-{slug}` | 几月 | 项目级状态，团队共享 |
| `agent-{name}` | 永久 | Agent 自己记的（非用户特定） |
| `shared` | 永久 | 组织级公共（风格指南、公司事实） |

召回时通常**并行查多个 namespace**（user + project + shared），客户端合并去重。
Banyan 的 search API 当前是单 namespace；为每个 namespace 发一个请求然后
按 score 合。

---

## 5. 失败模式

Banyan 永远不应该阻塞你的 agent。

| 失败 | 恢复 |
|------|------|
| HTTP 超时 / 连接拒 | 跳过 recall，无记忆生成。**关键**：不要把错误透给用户。 |
| Search 返回空 | 直接 fresh 生成 — 不要说 "我没存东西"。 |
| 写入失败 | 记日志 + 退避重试一次。**不要重试三次**；以后的写会补缺口。 |
| 阈值过滤后全空 | 等同于"空"处理 — 不是 bug，是设计。 |

推荐的 client 包装：

```python
def safe_recall(query: str, fallback: list[str] = None) -> list[str]:
    try:
        return recall(query, k=5, threshold=0.50)
    except Exception:
        return fallback or []
```

---

## 6. 升级到 NID 鉴权（Pro 层预览）

当你超出 demo namespace、需要真审计时：

```bash
# 1. 给你的 agent 签发一个 NID
banyan agent issue --id claude-assistant --cap memory.read,memory.write \
  --remote http://banyan-host:5180 \
  --key-out ~/.config/claude/agent.key

# 2. 运行时，从签发的 cert 构建 IdentFrame + 用 private key 签
# 3. 每次请求带上：
curl -H "Authorization: NID $(base64 -w0 ident-frame.json)" \
  http://banyan-host:5180/api/memory/...
```

`Banyan.Auth.RemoteNipCaClient` 是 .NET 侧签发 / 校验 NID 的 client。
Frame 签名代码在 `NPS.NIP.Crypto.NipSigner`。

---

## 7. 实例

这就是促成本文的真实 session — Claude 用 Banyan 给 Banyan 项目自己的开发
做记忆：

**Seed（onboarding 后跑一次）：**
```bash
curl -X POST http://192.168.31.50:5180/api/memory \
  -H 'content-type: application/json' \
  -d '{"content":"用户偏好简洁直接的回答，表格+代码块，少散文","namespace":"user-iamzerolin","agentNid":"urn:nps:agent:local.banyan:claude-assistant"}'
```

**Recall + 回答（每轮）：**
```bash
# 用户问：「我之前给的视觉风格是啥来着」
curl 'http://192.168.31.50:5180/api/memory/search?q=...&mode=hybrid&k=5&namespace=user-iamzerolin'
# → 第 1 命中（score 0.55）："UI 视觉偏好：墨蓝 + 亮蓝 + 粉红 配色 + glassmorphism + 粒子背景..."
```

Agent 现在有真实记忆做地基，不会再讲泛泛之论。

---

## 8. 反模式

- **不要把用户整句原话存进去。** 抽出**事实**。"用户偏好 X 不要 Y" 有用；
  "用户说：'嗨能不能用 X 别用 Y 啊'" 是噪音。
- **不要每个 token 都召回。** 每轮一次（每个 `user_message`），不是每生成
  一个 token 一次。
- **不要把 recall 命中塞到用户可见的回复里。** 它们去 *system prompt* /
  *agent context*，不是 assistant message。用户不该看到 "[从记忆里召回：...]"
  这种前缀。
- **不要在没有 NID + scope check 的情况下跨用户共享 namespace。** Lite 层
  namespace 是唯一围栏 — 一个用户的记忆就放在一个用户的 namespace。
- **不要忘了处理 Banyan 挂掉。** Agent 没有 Banyan 也得能跑（差一点但能跑）。
