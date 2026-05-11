English | [中文版](./mcp-server.cn.md)

# Recipe: Banyan as an MCP Memory Server

This recipe shows how to run Banyan as a **Model Context Protocol (MCP) server**,
giving Claude Desktop, Claude Code, and any MCP-compatible host first-class access
to persistent agent memory — no system-prompt boilerplate required.

---

## What you get

Four tools become available to the LLM host automatically:

| Tool | Description |
|------|-------------|
| `recall` | Semantic + keyword search over stored memories. Use before generating a response to inject relevant context. |
| `remember` | Persist a new memory for future sessions. |
| `update` | Replace the content of an existing memory by ID. |
| `forget` | Delete a memory by ID (content removed; audit trace kept). |

---

## Quick start

### 1. Install Banyan CLI

```bash
dotnet tool install -g Banyan.Cli
```

### 2. Download the embedding model (optional — enables semantic search)

```bash
banyan embedder download
```

Without this step, `recall` falls back to BM25 keyword search only.

### 3. Wire Banyan into Claude Desktop

Add to `~/Library/Application Support/Claude/claude_desktop_config.json`
(macOS) or `%APPDATA%\Claude\claude_desktop_config.json` (Windows):

```jsonc
{
  "mcpServers": {
    "banyan": {
      "command": "banyan",
      "args": ["mcp"]
    }
  }
}
```

Restart Claude Desktop. The memory tools appear automatically.

### 4. Wire into Claude Code

```bash
claude mcp add banyan -- banyan mcp
```

Or add to your project's `.mcp.json`:

```jsonc
{
  "mcpServers": {
    "banyan": {
      "command": "banyan",
      "args": ["mcp"]
    }
  }
}
```

### 5. Wire into Codex over Web MCP

`banyan web` also exposes the same tools over Streamable HTTP at `/mcp`:

```bash
banyan web --urls http://localhost:5180
codex mcp add banyan-lite --url http://localhost:5180/mcp
```

This path bridges directly to Banyan's native memory store. If the web process is
started with `--nid-auth writes-required` or `--nid-auth all-required`, `/mcp`
participates in the same NID middleware as the native HTTP APIs.

---

## CLI options

```
banyan mcp [options]

  --db PATH          Path to memory.db          (default: ~/.banyan/memory.db)
  --namespace NS     Default write namespace     (default: default)
  --sqlite-vec PATH  sqlite-vec extension path   (auto-discover if omitted)
```

### Namespace isolation

Use `--namespace` to give each user or project a dedicated memory partition:

```bash
# Per-user memory
banyan mcp --namespace user-alice

# Per-project memory
banyan mcp --namespace project-banyan
```

Recall searches the given namespace only when `namespace` is passed in the tool call.
Omitting `namespace` in a `recall` call searches across all namespaces.

---

## Tool reference

### `recall`

Search stored memories by meaning and keywords. Call this before generating a
response to surface relevant context.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `query` | string | required | Natural language search query |
| `namespace` | string? | null (all) | Scope to a specific namespace |
| `k` | int | 5 | Maximum results to return |
| `mode` | string | `hybrid` | `hybrid` · `lexical` · `vector` |

**Returns** a ranked list of memory snippets with score and ID:

```
[1] score=0.923  id=a1b2c3d4-...  ns=default
User prefers concise bullet-point responses.

[2] score=0.871  id=e5f6g7h8-...  ns=default
Project deadline is June 15, 2026.
```

### `remember`

Store a new memory. Returns the memory ID for later reference.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `content` | string | required | Text content to persist |
| `namespace` | string? | CLI default | Partition for this memory |

**Returns:** `Stored. id=<uuid>`

### `update`

Replace the content of a memory without changing its ID or namespace.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `memoryId` | string | required | ID returned by `remember` or shown in `recall` |
| `content` | string | required | New content |

### `forget`

Delete a memory. The original content is removed from the search index; the
audit trace (event log) is retained.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `memoryId` | string | required | ID to delete |
| `reason` | string? | null | Optional reason (recorded in audit log) |

---

## Recommended system prompt

Add this to your Claude Desktop or Claude Code system prompt so the model
knows when and how to use the tools:

```
You have access to a persistent memory store via the Banyan MCP tools.

Before every response:
1. Call `recall` with the user's message as the query.
2. If hits score > 0.5, prepend "Relevant memory:" lines to your context.

Write to memory when:
- The user explicitly says "remember X"
- The user corrects a previous answer
- The user states a hard preference or decision

Never write raw conversation logs — only distilled facts and preferences.
```

---

## Advanced: multiple namespaces

Run multiple Banyan MCP instances with different databases or namespaces:

```jsonc
{
  "mcpServers": {
    "banyan-personal": {
      "command": "banyan",
      "args": ["mcp", "--namespace", "personal", "--db", "~/.banyan/personal.db"]
    },
    "banyan-work": {
      "command": "banyan",
      "args": ["mcp", "--namespace", "work", "--db", "~/.banyan/work.db"]
    }
  }
}
```

---

## Search modes

| Mode | When to use |
|------|-------------|
| `hybrid` | Default. Best quality — fuses BM25 keyword rank and vector cosine rank via RRF. |
| `lexical` | Exact keyword matching. Good for IDs, names, code snippets, serial numbers. |
| `vector` | Semantic similarity only. Good for concepts and paraphrases, not exact terms. |

Vector search requires the ONNX model (`banyan embedder download`); falls back
to lexical-only automatically if the model is absent.

---

## Architecture

```
Claude Desktop / Claude Code
        │  MCP stdio (JSON-RPC 2.0)
        ▼
   banyan mcp
        │  IMemoryStore (in-process)
        ▼
  SqliteMemoryStore
  ├── BM25 FTS5 (lexical)
  ├── ONNX embedder (vector)
  └── sqlite-vec ANN index
```

`banyan mcp` is a self-contained process — no separate server to start.
It opens `memory.db` directly and serves the MCP protocol over stdin/stdout.
