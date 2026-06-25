# Changelog

All notable changes to Banyan Brain Lite are documented here.

## 1.1.0 - 2026-06-25

### Added

- `.banyanpack` v2 knowledge-pack support with manifest migration, signing, trust verification, and in-pack vector recall.
- Knowledge-pack mount management, including active-version switching, pin, upgrade, and rollback flows.
- Lite OpenTelemetry foundation and local memory-operation metrics.
- Tamper-evident audit records for memory write, update, and forget operations.
- Split ONNX embedder packaging so the default installer/dev build includes semantic embeddings while the slim .NET tool can stay smaller.
- Windows NSIS installer packaging and Debian `.deb` packaging for the Lite CLI host.

### Changed

- Bumped the Lite package version to `1.1.0`.
- Aligned NPS dependencies to `1.0.0-alpha.12`.
- Updated public README and release notes to describe the post-Wave GA Lite scope.

### Fixed

- Restored ONNX project files that were accidentally hidden by ignore rules.
- Pinned `MessagePack` to `3.1.7` to avoid vulnerable 3.0.x transitive restores.
- Pinned `SQLitePCLRaw.bundle_e_sqlite3` to `3.0.3` to avoid the vulnerable bundled SQLite native library.

## 1.0.0 - 2026-06-18

### Added

- First stable Lite release: single-node SQLite memory store, embedded NIP Mini-CA, Web UI, CLI, MCP server, hybrid retrieval, and NID authentication.
