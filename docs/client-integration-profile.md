> **See also:** [Pro client profile](../../pro/docs/client-integration-profile.md) · [Ent client profile](../../ent/docs/client-integration-profile.md)

# Banyan Client Integration Profile

This profile keeps customer integrations portable across Banyan Lite, Pro, and Ent. Application code should depend on these settings and capability names, not on edition-specific startup commands or admin APIs.

## Portable client settings

| Setting | Lite value | Portability rule |
| --- | --- | --- |
| `BANYAN_EDITION` | `lite` | Selects the edition adapter. Keep this as the only edition switch in application code. |
| `BANYAN_BASE_URL` | `http://localhost:5180` for `banyan web`, or the `banyan serve` URL | Base URL for the memory plane. |
| `BANYAN_MEMORY_PLANE` | `http-rest`, `nps-memory-node`, or `mcp-http` | Prefer `nps-memory-node` for clients that must move to Pro or Ent later. |
| `BANYAN_AUTH_MODE` | `anonymous`, `nid-writes-required`, or `nid-all-required` | Production clients should use NID even when Lite allows anonymous reads. |
| `BANYAN_TENANT_ID` | unset | Lite is single-node. Preserve this setting in client config but do not send it as a security boundary. |
| `BANYAN_WORKSPACE_ID` | namespace such as `user-alice`, `project-foo`, or `shared` | Treat the namespace as the workspace-level portability field. |

## Portable memory operations

| Operation | Client intent | Lite endpoint |
| --- | --- | --- |
| `memory.remember` | Store durable user, project, or agent memory. | `POST /api/memory` |
| `memory.search` | Recall relevant memory for a turn. | `GET /api/memory/search` or NWP `POST /api/memory/query` |
| `memory.update` | Correct an existing memory. | `PUT /api/memory/{id}` |
| `memory.forget` | Tombstone a memory while keeping audit trace. | `DELETE /api/memory/{id}` |
| `memory.schema` | Discover the NPS memory-node shape. | `GET /.schema` |
| `memory.manifest` | Discover node capabilities. | `GET /.nwm` |

## Lite edition profile

Lite is the standalone, offline-first profile. It can be run as a web UI, a pure NWP Memory Node, stdio MCP, or Streamable HTTP MCP.

```bash
export BANYAN_EDITION=lite
export BANYAN_BASE_URL=http://localhost:5180
export BANYAN_MEMORY_PLANE=nps-memory-node
export BANYAN_AUTH_MODE=nid-writes-required
export BANYAN_WORKSPACE_ID=user-alice
```

Recommended startup:

```bash
banyan web --nid-auth writes-required
```

Use `banyan serve --nid-auth writes-required` when the client only needs the NPS Memory Node surface.

## Switching guidance

- Lite to Pro: keep NID authentication and the NPS Memory Node request shape. Move the workspace boundary from a client-supplied namespace to the NID-derived organization/workspace scope enforced by Pro.
- Lite to Ent: keep the same memory operation names and capability expectations, but route trusted agent traffic through the Ent NPS Gateway. Treat Ent HTTP tenant routes as admin and compatibility APIs, not the portable agent memory plane.
- Do not hardcode `localhost`, `/api/memory/search`, or anonymous access in client libraries. Bind those through the profile settings above.

## Machine-readable profile

The repository root contains `banyan.integration.json` with the same edition profile for tools, installers, and smoke tests.
