namespace ToolUp.PublicRendering

open ToolUp.Platform.EntityTypes

// ─── Phase 38 follow-up — runtime-edited content overlay ────────────
//
// The file-backed `MarkdownContentLoader` is the source of truth for
// publishable content baked at deploy time. Deployments that also need
// runtime-authored or AI-emitted pages (CMS-style editing, generated
// landing pages, A/B variants) layer those entries into an
// `IEntityStore` keyed off the slug; the API impl falls through from
// file → entity store on `GetPage` when the slug doesn't appear in the
// file set. `ListPages` / `GetCollection` stay file-only until a clear
// use case demands an index — adding them later is additive.
//
// `PublicPage` itself is intentionally free of `Id` / `Type` /
// `Version` fields so the markdown loader doesn't have to invent
// entity-store metadata for every file. `PublicPageEntity` is the
// thin envelope that carries the three IEntity-shape fields plus the
// embedded `Page`. The entity-store overlay reads the envelope and
// projects `.Page` back out before handing the page to a layout.

/// Envelope record stored in `IEntityStore` to satisfy the IEntity
/// `Id` / `Type` / `Version` shape without polluting `PublicPage`.
/// `Id` always equals `Slug.value Page.Slug` — one envelope per slug.
type PublicPageEntity = {
    Id: EntityId
    Type: string
    Version: int
    Page: PublicPage
}

module PublicPageEntity =

    /// Reserved scope for public-rendering content. Public pages are
    /// not tenant-scoped; the entity store partitions by `scopeId`, so
    /// a fixed string keeps every deployment's overlay in one well-
    /// known container. Mirrors the `_smoke` / `_platform` reserved-
    /// scope conventions elsewhere in the SDK.
    [<Literal>]
    let PublicScope = "_public"

    /// Entity-type discriminator written into the `Type` field. Must
    /// match the value passed to `entityStore.Get` / `Save`.
    [<Literal>]
    let EntityTypeName = "PublicPage"

    /// Construct an envelope around a freshly-built `PublicPage`. The
    /// store overwrites `Version` on save (Phase 19 contract), so the
    /// `0` here is a placeholder that round-trips correctly.
    let fromPage (page: PublicPage) : PublicPageEntity = {
        Id = Slug.value page.Slug
        Type = EntityTypeName
        Version = 0
        Page = page
    }

    /// Registration passed to `ServerApp.withEntity` so the underlying
    /// `IEntityStore` recognises the type. No indexes declared — v1
    /// only resolves by slug (the `EntityId`), which is a primary-key
    /// lookup that bypasses indexes. Indexes can be added later when
    /// `ListPages` / `GetCollection` route through the store.
    let registration: EntityRegistration<PublicPageEntity> =
        EntityRegistration.create<PublicPageEntity> EntityTypeName