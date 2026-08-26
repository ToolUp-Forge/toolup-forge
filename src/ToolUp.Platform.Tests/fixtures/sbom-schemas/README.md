# Vendored SBOM schemas — test fixtures

These are the **published** JSON Schemas for the two bill-of-materials formats the SBOM projection
emits. They are vendored here, byte-for-byte as fetched, so the suite validates emitted documents
against each format's own schema **offline**: a test that fetched a schema over the network would
be a test that passes when the network is down and fails when a remote file moves, which is the
opposite of what a conformance gate should do.

They are **inputs to a test**, never shipped. Nothing under `ToolUp.Platform.Tests` reaches a
packable assembly, so no ToolUp package acquires a dependency on any of them.

| File | Upstream | Version / tag | Licence | SHA-256 (as committed, LF) |
|---|---|---|---|---|
| `spdx-schema.json` | `https://raw.githubusercontent.com/spdx/spdx-spec/v2.3/schemas/spdx-schema.json` | SPDX 2.3 (tag `v2.3`) | CC-BY-3.0 | `239208b7ac287b3cf5d9a9af23f9d69863971102a5e1587a27a398b43490b89b` |
| `bom-1.6.schema.json` | `https://raw.githubusercontent.com/CycloneDX/specification/1.6/schema/bom-1.6.schema.json` | CycloneDX 1.6 (tag `1.6`) | Apache-2.0 | `3e92dddbc30cf7f6a02b80f0942b1a4cfd4fb1c26f1dfc4310afa9d613cafb93` |
| `spdx.schema.json` | `https://raw.githubusercontent.com/CycloneDX/specification/1.6/schema/spdx.schema.json` | CycloneDX 1.6 (tag `1.6`) | Apache-2.0 | `baa9d3bd1ed57b6751b0887edead6b5063ff53ff7429cf85d476c6c94af0166e` |
| `jsf-0.82.schema.json` | `https://raw.githubusercontent.com/CycloneDX/specification/1.6/schema/jsf-0.82.schema.json` | CycloneDX 1.6 (tag `1.6`) | Apache-2.0 | `8bae002c25e723db7ee1f26afde680ae1a2b1a8f6b4b4b0fd65dc3becb090aae` |

The last two are not validated against directly — `bom-1.6.schema.json` `$ref`s them (for the
`licenses` and `signature` subschemas), and a `$ref` that cannot resolve is a validator error rather
than a document error. They are registered by their `$id` so the reference resolves offline.

**Do not hand-edit any of these files.** A locally adjusted schema would validate documents no real
consumer accepts, which is worse than not validating at all. To move to a newer format version,
re-fetch from the upstream tag above, record the new hash here, and expect the suite to tell you what
changed.
