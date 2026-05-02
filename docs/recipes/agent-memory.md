English | [中文版](./agent-memory.cn.md)

# Recipe: Using Banyan as Agent Memory

This recipe is for **agent authors** — Claude, GPT, an in-house assistant,
anything that has conversations with users and needs persistent memory
across sessions. It walks through the integration loop end to end.

> The Banyan instance you're connecting to has already been set up.
> If you need to bring up your own, see the project [`README.md`](../../README.md)
> Quick Start.

---

## TL;DR

Two HTTP calls is the entire integration:

```
new user message
   │
   ▼
GET /api/memory/search?q={msg}&mode=hybrid&k=5&namespace=user-{user_id}
   │
   ▼ filter:  hits where score > 0.50
   ▼
inject as "Relevant memory:" lines into your system prompt
   │
   ▼
generate the response
   │
   ▼ if user explicitly says "remember X" / corrects you / states a hard preference
   ▼
POST /api/memory  {content, namespace, agentNid?}
```

Everything else in this doc is choosing the right namespace, the right
threshold, the right write trigger.

---

## 1. Pick an integration mode

| Mode | When to use | Cost |
|------|-------------|------|
| **HTTP REST** (`/api/memory/*`) — Banyan-spec, demo-friendly | Most agents, any language, scripts. **Default**. | One HTTP call per turn |
| **NWP `QueryFrame`** (`POST /api/memory/query`) — NPS-3 wire | Cross-language NPS interop, frame-level features (anchor_ref, token budget) | Same — different request body |
| **NID-attested** (`Authorization: NID <base64(IdentFrame)>`) | Multi-tenant production, audit trails, scope enforcement (Pro tier) | One-time NID issuance per agent + per-request frame |
| **In-process .NET** (`SqliteMemoryStore` direct) | Agent ships as a .NET binary alongside Banyan | None (same process) |

For typical agents the HTTP REST path is the right starting point. Switch to
NWP when you need anchor digests / token-budget reporting from the wire, and
to NID when you need server-side audit of who wrote what.

---

## 2. The recall loop

Before generating each response, search the user's namespace and inject hits
that pass the threshold.

### curl baseline
```bash
curl "http://banyan-host:5180/api/memory/search?q=$(jq -nr --arg q "$user_msg" '$q|@uri')&mode=hybrid&k=5&namespace=user-${USER_ID}"
```

### Python
```python
import requests, urllib.parse

BASE = "http://banyan-host:5180"
NS   = f"user-{user_id}"

def recall(query: str, k: int = 5, threshold: float = 0.50) -> list[str]:
    """Return relevant memory snippets to inject into the system prompt."""
    r = requests.get(f"{BASE}/api/memory/search", params={
        "q": query, "mode": "hybrid", "k": k, "namespace": NS,
    }, timeout=2)
    r.raise_for_status()
    hits = r.json()["hits"]
    return [h["content"] for h in hits if h["score"] > threshold]

# In your generation loop:
context_lines = recall(user_message)
if context_lines:
    system_prompt += "\n\n## Relevant memory\n" + "\n".join(f"- {c}" for c in context_lines)
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

### Threshold heuristics (`bge-small-zh` embedder)

| Cosine | Meaning | Recommended action |
|--------|---------|--------------------|
| `> 0.55` | Strong semantic match. Same topic, same intent. | Inject into context |
| `0.50–0.55` | Weak / fishing. Top-1 might be relevant; tail is noise. | Inject only if it's hit #1 |
| `< 0.50` | Vector search padding — top-K returns *something* even if nothing matches. | Drop |

If your embedder isn't bge-small-zh (e.g. you swapped in a real
`Banyan.Embedders.OnnxEmbedder` with all-MiniLM-L6-v2), recalibrate the
thresholds against a 50-example labelled set.

### Modes — when to use which

- **`hybrid`** — default. BM25 + vector + RRF fusion. Catches both literal
  matches and synonyms.
- **`vector`** — when the user's phrasing is unlikely to share words with
  the stored memory ("how do I deploy" → memory titled "production rollout
  steps").
- **`lexical`** — when you have a known string and want exact / prefix
  matches (e.g. recalling a specific NID, file path, or error code).

---

## 3. The write strategy

**Not every turn needs to be remembered.** Storing every "ok, thanks"
drowns the real signal in noise. Trigger writes only on:

| Trigger | Example | Action |
|---------|---------|--------|
| User explicit "remember X" | "remember I prefer dark mode" | `POST /api/memory` |
| User correction | "actually it's Postgres not MySQL" | `DELETE /api/memory/{old}` + `POST /api/memory {new}` |
| User stated preference / constraint | "I always want concise answers" | `POST` with the rule verbatim |
| Decision pinned down | "let's use approach A" | `POST` with the decision + 1-line rationale |
| Identity / context info | "I'm on the data team, working in EST" | `POST` (long-lived, useful next session) |

Skip:
- "thanks" / "ok" / pleasantries
- Restatements of what you just said
- Information already in `docs/`, `README`, the codebase, `git log`

### Metadata you should include

```json
{
  "content":   "<the fact, complete sentence>",
  "namespace": "user-{user_id}",
  "agentNid":  "urn:nps:agent:local.banyan:claude-assistant",
  "metadata":  {
    "source":     "conversation",
    "session_id": "...",
    "captured_at": "2026-05-02T01:23:45Z"
  }
}
```

`agentNid` answers "which agent wrote this", important when multiple
agents share a namespace. Note `agentNid` is currently a free-form string;
in **Pro tier** it becomes a verified identity from the IdentFrame.

### Update / forget

```bash
# correct an existing memory in place — appends an Update event, refreshes lex+vec indexes
curl -X PUT  "http://banyan-host:5180/api/memory/{id}" \
     -H 'content-type: application/json' \
     -d '{"content":"corrected fact"}'

# tombstone — vanishes from search, audit trace remains
curl -X DELETE "http://banyan-host:5180/api/memory/{id}?reason=user-correction"
```

---

## 4. Namespace design

Namespaces are Banyan's only built-in isolation knob in the Lite tier.
Treat them like **logical scopes**, not security boundaries (security is
NID-level, in Pro).

| Convention | Lifetime | Example |
|------------|----------|---------|
| `user-{user_id}` | Forever | Per-end-user persistent memory |
| `session-{session_id}` | Hours | Short-term context for one conversation |
| `project-{slug}` | Months | Per-project state shared across team |
| `agent-{name}` | Forever | Things the agent itself remembered (not user-specific) |
| `shared` | Forever | Org-wide canon (style guide, company facts) |

Recall typically runs against **multiple namespaces** in parallel (user +
project + shared) and you merge / dedupe client-side. Banyan's search API
is single-namespace today; submit one request per namespace and merge by
score.

---

## 5. Failure modes

Banyan should never block your agent.

| Failure | Recovery |
|---------|----------|
| HTTP timeout / connection refused | Skip recall, generate without memory context. **Critical**: don't leak the error to the user. |
| Search returns empty | Generate fresh — no "I don't have anything stored". |
| Write fails | Log + retry with backoff once. Do **not** retry the third time; future writes will catch the gap. |
| Score threshold filters everything out | Same as empty — that's not a bug, it's the design. |

The recommended client wrapper:

```python
def safe_recall(query: str, fallback: list[str] = None) -> list[str]:
    try:
        return recall(query, k=5, threshold=0.50)
    except Exception:
        return fallback or []
```

---

## 6. Going to NID-attested (Pro tier preview)

When you outgrow the demo namespace and need real audit:

```bash
# 1. Issue an NID for your agent
banyan agent issue --id claude-assistant --cap memory.read,memory.write \
  --remote http://banyan-host:5180 \
  --key-out ~/.config/claude/agent.key

# 2. At runtime, build an IdentFrame from the issued cert + sign with the key,
# 3. Attach to every request:
curl -H "Authorization: NID $(base64 -w0 ident-frame.json)" \
  http://banyan-host:5180/api/memory/...
```

`Banyan.Auth.RemoteNipCaClient` is the .NET-side client for issuing /
verifying NIDs. The frame-signing code is in `NPS.NIP.Crypto.NipSigner`.

---

## 7. Worked example

This is the literal session that motivated this doc — Claude using Banyan
as memory across the Banyan project's own development:

**Seed (run once, after onboarding):**
```bash
curl -X POST http://192.168.31.50:5180/api/memory \
  -H 'content-type: application/json' \
  -d '{"content":"User prefers concise direct answers, tables + code blocks over prose","namespace":"user-iamzerolin","agentNid":"urn:nps:agent:local.banyan:claude-assistant"}'
```

**Recall + answer (every turn):**
```bash
# user asks: "我之前给的视觉风格是啥来着"
curl 'http://192.168.31.50:5180/api/memory/search?q=...&mode=hybrid&k=5&namespace=user-iamzerolin'
# → top hit (score 0.55): "UI 视觉偏好：墨蓝 + 亮蓝 + 粉红 配色 + glassmorphism + 粒子背景..."
```

The agent now answers grounded in the actual stored preference rather than
generic platitudes.

---

## 8. Anti-patterns

- **Don't write the user's whole message in.** Summarise to the *fact* it
  contains. "User prefers X over Y" is useful; "the user said: 'hey can you
  please use X instead of Y'" is noise.
- **Don't recall on every keystroke.** Once per turn (per `user_message`),
  not once per token.
- **Don't dump recall hits into the user-facing response.** They go in the
  *system prompt* / *agent context*, not the assistant message. The user
  shouldn't see "[recalled from memory: ...]" prefixes.
- **Don't share namespaces across users without a NID + scope check.**
  In Lite tier, namespace is the only fence — keep one user's memory in
  one user's namespace.
- **Don't forget to handle Banyan being down.** The agent should still
  function (worse, but function) without it.
