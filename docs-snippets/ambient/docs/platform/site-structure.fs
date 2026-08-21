// Ambient context for `docs/platform/site-structure.md`.
//
// The nav / taxonomy blocks are excerpts from a layout module and a
// composition root the page never shows in full: the deployment's
// `config`, its first-registered `pageLayout`, the `siteNav` tree built
// in "Navigation tree", and the content API a tag-index source enumerates
// from. Declared here so the blocks compile as written.
open Giraffe.ViewEngine
open ToolUp.PublicRendering

[<AutoOpen>]
module PageAmbient =

    let config: ServerConfig = failwith "ambient"

    let pageLayout: PublicPage -> XmlNode = failwith "ambient"

    /// The tree built in "Navigation tree". Named `siteNav` rather than
    /// `nav` because a layout module opens `Giraffe.ViewEngine`, whose
    /// `nav` element function would shadow it.
    let siteNav: NavNode list = failwith "ambient"

    let api: IPublicContentApi = failwith "ambient"