// Ambient context for `docs/platform/gated-ssr.md`.
//
// Both compose / content-source blocks read from a deployment the page
// never shows in full: its `config` (auth + scope resolver already
// configured), its first-registered layout, and the per-principal
// analytics projection a gated source calls.
open Giraffe.ViewEngine
open ToolUp.PublicRendering

[<AutoOpen>]
module PageAmbient =

    let config: ServerConfig = failwith "ambient"

    let pageLayout: PublicPage -> XmlNode = failwith "ambient"

    /// Builds THIS principal's analytics narrative — the body of a
    /// `ClientGated` portal page.
    let buildClientNarrative (ctx: AccessContext) : Async<ToolUp.Platform.Narrative.NarrativeDocument> =
        failwith "ambient"