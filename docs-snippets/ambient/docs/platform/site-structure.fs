// Ambient context for `docs/platform/site-structure.md`.
//
// The nav / taxonomy blocks are excerpts from a layout module and a
// composition root the page never shows in full: the deployment's
// `config`, its first-registered `pageLayout`, the `siteNav` tree built
// in "Navigation tree", the content API a tag-index source enumerates
// from, the request's `AccessContext`, and the page being rendered.
// Declared here so the blocks compile as written.
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

    /// The requesting principal, as a layout / content source receives it.
    let ctx: AccessContext = failwith "ambient"

    /// The page currently being rendered.
    let page: PublicPage = failwith "ambient"

    /// The candidate set a related-content or facet computation ranks over
    /// — whatever the caller enumerated from `api`.
    let allPages: PublicPage list = failwith "ambient"

    /// The `docs` collection, as returned by `api.GetCollection "docs"`.
    let chapters: PublicPage list = failwith "ambient"

    /// The 1-based page number a paginated menu is rendering.
    let pageNumber: int = failwith "ambient"

    /// The compose pipeline so far — the value each `PublicRenderingServerApp.with*`
    /// excerpt below is threading through.
    let app: PublicRenderingServerApp = failwith "ambient"