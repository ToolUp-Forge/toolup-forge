// Ambient context for `docs/platform/multi-site-public-rendering.md`.
//
// The registration block reads the three content-root paths and the
// deployment's own layout module from a composition root the page never
// shows in full.
open Giraffe.ViewEngine
open ToolUp.PublicRendering

[<AutoOpen>]
module PageAmbient =

    /// Absolute paths to each site's `content/` directory.
    let mainContent: string = failwith "ambient"

    let docsContent: string = failwith "ambient"

    let blogContent: string = failwith "ambient"

    /// The deployment's own layout module.
    module Layouts =

        let page: PublicPage -> XmlNode = failwith "ambient"

        let blogPage: PublicPage -> XmlNode = failwith "ambient"