// Ambient context for `docs/operations/degraded-capabilities.md`.
//
// The registration example is an excerpt from a consumer's own
// best-effort wiring site: the service provider, the logger it already
// resolved, and the wiring call whose failure it is reporting all come
// from the surrounding compose code the page does not show.
open System

[<AutoOpen>]
module PageAmbient =

    let services: IServiceProvider = failwith "ambient"

    let logger: ToolUp.Platform.ILogger = failwith "ambient"

    /// The consumer's own best-effort wiring — the thing whose failure
    /// leaves a capability degraded.
    let wireMyBestEffortThing () : unit = failwith "ambient"