namespace ToolUp.PublicRendering

open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.Platform.IEntityStore

/// Default `IPublicContentApi` impl composing a file-backed
/// `MarkdownContentLoader` with an optional `IEntityStore` overlay for
/// runtime-edited content, plus (Phase 83) an ordered list of
/// request-time `IContentSource` resolvers. The resolution order is:
///
///   1. File-backed markdown (the loader's in-memory map).
///   2. `IEntityStore<PublicPage>` overlay (runtime-edited content).
///   3. Registered `IContentSource` resolvers, in registration order;
///      first `Some` wins (Phase 83 — `GetPageInContext` only).
///
/// File entries win on collision (the loader's in-memory map is
/// consulted first); the entity store is consulted only when the file
/// set has no match; content sources are consulted only when both file
/// and overlay miss.
///
/// **Overlay scope.** v1 falls through to the entity store on
/// `GetPage` only. `ListPages` / `GetCollection` stay file-only until
/// a clear use case demands a server-side index — adding either is
/// additive (declare an index on `PublicPageEntity.registration`,
/// then route the call through `entityStore.FindByIndex`).
///
/// **Content-source scope.** Sources are consulted only by
/// `GetPageInContext` — they are request-time, principal-scoped, and so
/// require an `AccessContext` that the context-free `GetPage` /
/// `ListPages` / `GetCollection` methods do not carry. Sitemap
/// generation and static export (both context-free) therefore never
/// enumerate source-produced pages, which are dynamic by nature.
///
/// **Constructor surface.** `entityStore` is optional so deployments
/// that have no runtime-edited content pay zero overhead. The default
/// SDK always registers `IEntityStore` in DI, so `PublicRenderingCompose`
/// can pass `Some` unconditionally; passing `None` from a custom
/// composition root short-circuits the fallthrough at no runtime cost.
/// `contentSources` defaults to the empty list — a deployment with no
/// `withContentSource` registrations is byte-for-byte identical to the
/// pre-83 file+overlay chain (GP 11).
type PublicContentApiImpl
    (loader: MarkdownContentLoader, entityStore: IEntityStore option, contentSources: IContentSource list) =

    /// Synthesise a `PublicPage` shell around a content-source-produced
    /// `ContentBody`. A source returns only a body; the page's
    /// presentation metadata is derived from it:
    ///
    /// - `Narrative` body — `Title` / `Description` come from the
    ///   document's `Title` / `Subtitle`, and `PublishedAt` from its
    ///   `Provenance.GeneratedAt`. The `<head>` (canonical, OG, JSON-LD)
    ///   is then driven by `NarrativeLayout.headTags` off this same
    ///   `Narrative` body at render time (Phase 83 "page metadata from
    ///   data" — no frontmatter file needed).
    /// - `Markdown` / `Html` body — no embedded metadata, so `Title` /
    ///   `Description` are empty; a source emitting these is expected to
    ///   carry its own `<head>` shape in the fragment or rely on the
    ///   layout's defaults.
    ///
    /// `Layout` is left unset (`LayoutName ""`) so `PublicPageHandler`'s
    /// resolver falls back to the first-registered layout — a source does
    /// not pick a layout (out of scope for Phase 83; layout selection
    /// stays a compose-time concern).
    static member private pageFromBody(slug: string, body: ContentBody) : PublicPage =
        match body with
        | Narrative doc -> {
            Slug = Slug slug
            Title = doc.Title
            Description = doc.Subtitle |> Option.defaultValue ""
            Body = body
            Layout = LayoutName ""
            Frontmatter = Map.empty
            PublishedAt = doc.Provenance |> Option.map _.GeneratedAt
            Collection = None
            Status = Published
          }
        | Markdown _
        | Html _ -> {
            Slug = Slug slug
            Title = ""
            Description = ""
            Body = body
            Layout = LayoutName ""
            Frontmatter = Map.empty
            PublishedAt = None
            Collection = None
            Status = Published
          }

    interface IPublicContentApi with

        member _.GetPage(slug: string) : Async<PublicPage option> = async {
            match loader.GetPage slug with
            | Some page -> return Some page
            | None ->
                match entityStore with
                | None -> return None
                | Some store ->
                    let! result =
                        store.Get<PublicPageEntity>(PublicPageEntity.PublicScope, PublicPageEntity.EntityTypeName, slug)

                    return
                        match result with
                        | Ok envelope -> Some envelope.Page
                        | Error _ -> None
        }

        member _.ListPages(prefix: string) : Async<PublicPage list> = async { return loader.ListPages prefix }

        member _.GetCollection(collectionId: string) : Async<PublicPage list> = async {
            return loader.GetCollection collectionId
        }

        member this.GetPageInContext(slug: string, ctx: AccessContext) : Async<PublicPage option> = async {
            // Tiers 1 + 2 (file + entity overlay) — identical to GetPage.
            let! existing = (this :> IPublicContentApi).GetPage slug

            match existing with
            | Some page -> return Some page
            | None ->
                // Tier 3 — registered content sources, in registration
                // order; first `Some` wins. When `contentSources` is
                // empty this loop returns `None` immediately, so the
                // overall result equals `GetPage slug` byte-for-byte
                // (GP 11).
                let rec tryNext (sources: IContentSource list) = async {
                    match sources with
                    | [] -> return None
                    | source :: rest ->
                        let! body = source.Resolve (Slug slug) ctx

                        match body with
                        | Some b -> return Some(PublicContentApiImpl.pageFromBody (slug, b))
                        | None -> return! tryNext rest
                }

                return! tryNext contentSources
        }

module PublicContentApiImpl =

    /// Construct the default impl over a loader, an optional entity-store
    /// overlay, and an ordered list of request-time content sources. Pass
    /// `[]` for `contentSources` to preserve the pre-83 file+overlay
    /// behaviour exactly.
    let create
        (loader: MarkdownContentLoader)
        (entityStore: IEntityStore option)
        (contentSources: IContentSource list)
        : IPublicContentApi =
        PublicContentApiImpl(loader, entityStore, contentSources) :> IPublicContentApi