// Ambient context for `docs/platform/storage.md`.
//
// Two SDK modules the page's interface listings name but do not open in
// the snippets a reader copies: `BlobMetadata` lives in the same module
// as `IBlobStorage`, and `EncryptionKey` / `KeyResolutionError` in
// `EncryptionTypes`. Neither module is `[<AutoOpen>]`, and neither open
// belongs in the markdown — a reader who has `IBlobStorage` in scope at
// all already has both.
//
// Opens only, and deliberately no declarations: everything this page
// names is a REAL SDK type, so nothing here may stand in for one. The
// blocks below still declare the interfaces themselves, which is what the
// doc-declared type parity check holds against `api-baselines/`.

open ToolUp.Platform.BlobStorage
open ToolUp.Platform.EncryptionTypes