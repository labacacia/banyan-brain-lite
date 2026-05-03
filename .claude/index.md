# Banyan Project Memory Index

## LOAD: (always load at session start)
- session.qmd

## Warm triggers (load domain index on keyword match)

| Keyword | File |
|---------|------|
| identity / oidc / jwt / login / auth | memory/warm/identity.qmd |
| nid / ca / nip / certificate / agent cert | memory/warm/nid-ca.qmd |
| memory / search / bm25 / vector / embedding | memory/warm/memory-store.qmd |
| deploy / docker / 50 / 192.168.31.50 | memory/warm/deploy.qmd |

## Cold triggers (load specific ADR when a decision is questioned)

| Keyword | File |
|---------|------|
| websocket ncp / ws ncp / pro transport | memory/cold/ADR-001-pro-websocket-ncp.qmd |
| tcp ncp / raw tcp / ent transport | memory/cold/ADR-002-ent-raw-tcp-ncp.qmd |

See memory/cold/index.qmd for full ADR list.
