// Ambient context for `docs/platform/metric-registry.md`.
//
// The read block is an excerpt from a request handler, so it reads the
// `HttpContext` a handler is already holding rather than manufacturing
// one. `open Giraffe` is what puts the `ctx.GetService<'T>()` extension
// in scope — a handler file has it already, so the markdown a reader
// copies does not need to grow the ceremony.
open Giraffe
open Microsoft.AspNetCore.Http

[<AutoOpen>]
module PageAmbient =

    /// The per-request context a Giraffe handler receives.
    let ctx: HttpContext = failwith "ambient"