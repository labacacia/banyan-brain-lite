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

Only `manifest.json` is required by the v1 core validator. The remaining directories are reserved for build, mount, recall, provenance, and signature work in later issues.

The manifest schema lives at `docs/knowledge-packs/manifest.schema.json`.

Commercial context, sponsored recommendation, and publisher-specific metadata must use `extensions` rather than changing the v1 core schema.
