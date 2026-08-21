// Ambient context for `src/ToolUp.Platform/technical-guide/12-hosting-models.md`.
//
// The three worked examples share one composition root, shown in the
// page's first block as `module MyApp.Composition`. Every later host-wiring
// block reads `MyApp.Composition.serverHost` from it, and the Cloud
// Functions example names the `Startup` type its own block declares.
// Both are the reader's program, not SDK surface, so they stand in here.

[<AutoOpen>]
module PageAmbient =

    module MyApp =

        module Composition =

            let serverHost: IServerHost = failwith "ambient"

        /// Stands in for the `Startup` the page's `Startup.fs` block declares
        /// (there it inherits `FunctionsStartup`; here only its identity is
        /// needed, for `typeof<MyApp.Startup>`).
        type Startup() = class end