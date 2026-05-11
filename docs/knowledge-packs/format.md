# Banyan Knowledge Pack Format

`.banyanpack` is a ZIP archive with a required `manifest.json` entry at the archive root.

Initial layout:

```text
manifest.json
sources/
memories/
entities/
relations/
indexes/
citations/
signatures/
```

Only `manifest.json` is required by the v1 core schema validator. Archive validation also verifies any `checksums` entries declared by the manifest. Checksums use `sha256:<hex>` and the manifest key is the archive entry path, for example `memories/records.jsonl`.

The manifest schema lives at `docs/knowledge-packs/manifest.schema.json`.

Commercial context, sponsored recommendation, and publisher-specific metadata must use `extensions` rather than changing the v1 core schema.

## Provenance

Pack-derived recall results must preserve their pack boundary. Metadata returned from mounted pack recall includes:

- `source = knowledge_pack`
- `pack_id`
- `pack_version`
- `pack_name`
- `pack_type`
- `record_id`
- `source_id`
- `source_path`
- `source_title`
- `source_checksum`
- `source_section_id`
- `confidence`
- `mounted_at`

Source records are stored in `sources/sources.jsonl`. Memory records are stored in `memories/records.jsonl` and reference a `source_id`.

## Version Lifecycle

Versions compare by numeric or lexical segments split on `.`, `-`, and `_`.

- A candidate with a greater segment is newer, for example `2026.06` replaces `2026.05`.
- Missing numeric segments are treated as `0`, so `1.2.0` and `1.2` are equivalent.
- A candidate with a lower segment is older and should not silently replace a mounted newer pack.

Mount records are keyed by namespace, pack id, and pack version. Mounting the same pack id and version updates the existing mount record path and checksum while preserving the original mount timestamp. Mounting a new version creates a separate record so callers can decide whether to unmount the old version, run them side-by-side, or replace after validation.

Unmount removes matching mount records. Once unmounted, pack records no longer participate in recall for that namespace.
