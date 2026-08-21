// Ambient context for `docs/platform/architecture.md`.
//
// The client-composition blocks are excerpts of a consuming app's
// entry point, so they read three things the page never shows in
// full: one illustrative domain module (`SalesAnalysis`), the
// registration list it goes into, and the typed icon component the
// AI assistant's client-side branding takes.
open Feliz
open ToolUp.Platform

[<AutoOpen>]
module PageAmbient =

    /// An illustrative consumer module, in the four-file shape
    /// `modules.md` documents.
    module SalesAnalysis =

        module ClientView =
            let register () : ErasedModule = failwith "ambient"

    /// The registration list the client composition root builds. The
    /// page's own block declares it too and shadows this one.
    let modules: ErasedModule list = []

    /// A `vite-plugin-svgr` icon import, as the app's client entry
    /// point would have it. `AIAssistantClientBranding.Icon` is a
    /// typed `ReactElement`, not a URL string.
    let sparkIcon: ReactElement = failwith "ambient"