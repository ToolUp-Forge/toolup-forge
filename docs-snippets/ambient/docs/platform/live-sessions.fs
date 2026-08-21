// Ambient context for `docs/platform/live-sessions.md`.
//
// The compose block is an excerpt from a composition root, so it reads the
// deployment's own `config` and module list rather than building them —
// what a reader already holds at the point this step is added.

[<AutoOpen>]
module PageAmbient =

    let config: ServerConfig = failwith "ambient"

    let modules: ServerModule list = failwith "ambient"