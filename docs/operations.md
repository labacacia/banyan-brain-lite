# Operations

## Health Probes

`banyan web` and `banyan serve` expose unauthenticated health endpoints on the main HTTP port:

| Endpoint | Purpose | Success response |
| --- | --- | --- |
| `GET /alive` | Liveness. Returns as long as the process is running; it performs no I/O. | `200 OK` with `{"status":"alive"}` |
| `GET /health` | Readiness. Checks SQLite and the configured embedder. | `200 OK` with `{"status":"ok", ...}` |

When any readiness check fails, `GET /health` returns `503 Service Unavailable` with
`{"status":"degraded","checks":{...}}`.

Example systemd post-start probe:

```ini
ExecStartPost=/usr/bin/curl -fs http://127.0.0.1:5050/alive
```

Example Docker health check:

```dockerfile
HEALTHCHECK --interval=30s --timeout=5s CMD curl -fs http://localhost:5050/health || exit 1
```
