namespace ToolUp.PublicRendering

open System
open ToolUp.Platform

/// Server-side substrate for retrieving publishable content by slug
/// or collection. The default implementation (`PublicContentApiImpl`)
/// composes a file-backed `MarkdownContentLoader` with an optional
/// `IEntityStore<PublicPage>` overlay for runtime-edited entries —
/// the file-as-source-of-truth entry wins on collision.
///
/// **Six portability rules** (per `CLAUDE.md` — every
/// substrate interface that could plausibly be implemented by a
/// distributed framework must satisfy all six):
///
///   1. **Identity by value.** Every input / output is `string`,
///      `Slug`, or `PublicPage` (a record of primitives + Map). No
///      `IActorRef` / `IGrainReference` / live handles. ✓
///   2. **Async at every boundary.** Every method returns
///      `Async<_>`. ✓
///   3. **Retry + supervision as data.** N/A — `IPublicContentApi`
///      is a read-only query surface with no retry policy to
///      express. (Distributed impls that need retry on transient
///      backing-store failures wrap their own retry policy as
///      record-shaped configuration; not part of this interface.)
///   4. **Stateless between invocations.** Implementations may cache
///      loaded content for process lifetime (the default impl does;
///      that's why hot-reload exists). They must not depend on
///      per-request state held between calls — every method takes
///      its inputs by parameter only.
///   5. **No cross-shard ordering promises.** `ListPages` returns
///      alphabetical-by-slug order — a deterministic shape derived
///      from the input set, not a per-shard arrival order.
///   6. **Precision at the lower bound.** N/A — no scheduling /
///      timing primitives. (The hot-reload debounce window lives in
///      `MarkdownContentLoader`, not on this interface.)
type IPublicContentApi =
    /// Resolve a slug to a single page. `None` means the page does
    /// not exist; `PublicPageHandler` then consults `RedirectMap`
    /// before returning 404.
    abstract GetPage: slug: string -> Async<PublicPage option>

    /// List every page whose slug starts with `prefix`. Empty
    /// `prefix` returns every page in the system. Order:
    /// alphabetical-by-slug.
    ///
    /// **This is the UNGATED enumeration** — it returns `Draft` /
    /// `Archived` / not-yet-`Scheduled` pages and non-`Public` (gated)
    /// pages alike. It exists for admin / authoring / preview surfaces
    /// that legitimately need the whole set. **A public, anonymous
    /// surface must call `ListPagesPublic` instead** (Phase 632); see
    /// its documentation for why the distinction is structural rather
    /// than advisory.
    abstract ListPages: prefix: string -> Async<PublicPage list>

    /// Phase 632 — the **gated** enumeration: `ListPages prefix` with the
    /// Phase 38 egress gate (`PublicPage.isPubliclyDiscoverable`) already
    /// applied, so what comes back is exactly what an anonymous visitor —
    /// and therefore a crawler — may be shown to exist.
    ///
    /// **Why this is on the interface rather than left to each caller.**
    /// Phase 38 found that every enumeration surface (sitemap + shards,
    /// the JSON search index, the Atom feed, static export, the IndexNow
    /// push channel) filtered on audience alone, an axis orthogonal to
    /// publish status, so an unpublished page carrying the default
    /// `Audience = Public` leaked its slug — and via the feed its title
    /// and full rendered body. 38 fixed the five call sites it found, but
    /// the guarantee then rested on five callers each remembering to
    /// conjoin both axes; a sixth surface got it wrong by default, and
    /// one duly did (`TaxonomyHandler`'s `/tag/{slug}` index, added by
    /// Phase 100 and found by this phase). This member makes the safe set
    /// the default one and raw `ListPages` the deliberate, unusual choice.
    ///
    /// `now` is an explicit parameter rather than a read of the wall
    /// clock so a caller can pin ONE instant across a multi-step pass —
    /// a static export must not have a `Scheduled` page land in the
    /// sitemap but miss the page write because the clock crossed its
    /// publish instant mid-run — and so tests can assert the boundary
    /// exactly. `PublicContentApi.listPagesPublicNow` is the wall-clock
    /// convenience.
    ///
    /// **Implementing it (external implementers).** The whole body can be
    /// `PublicContentApi.defaultListPagesPublic this now prefix` — filter
    /// your own `ListPages` through the shipped gate. Override it only if
    /// your backing store can push the predicate down into its query.
    /// Whatever you do, the returned set must be a subset of
    /// `ListPages prefix` and must contain no page for which
    /// `PublicPage.isPubliclyDiscoverable now` is false.
    abstract ListPagesPublic: now: DateTimeOffset * prefix: string -> Async<PublicPage list>

    /// List every page whose `Collection = Some collectionId`. The
    /// canonical use case is news / events / team listings. Order:
    /// `PublishedAt` descending (newest first), `None` last.
    abstract GetCollection: collectionId: string -> Async<PublicPage list>

    /// Phase 83 — context-aware resolution. Runs the same file +
    /// entity-overlay tiers as `GetPage`, then consults any registered
    /// `IContentSource` resolvers with the caller's `AccessContext`
    /// (registration order; first `Some` wins). The `AccessContext`
    /// lets a resolver scope its backing query to the requesting
    /// principal (GP 4 — tenant isolation rides the context, not a
    /// "remember to filter" convention).
    ///
    /// **Backward compatibility (GP 11).** When no content sources are
    /// registered, this returns *exactly* what `GetPage slug` returns —
    /// the default impl delegates to its own `GetPage` and then walks an
    /// empty source list. Context-free callers (sitemap generation,
    /// static export) keep calling `GetPage`; only the per-request page
    /// handler — which has an `AccessContext` in hand — calls this.
    ///
    /// `AccessContext` is identity-by-value (a record of `string` /
    /// `Guid` / DU primitives), so this method preserves the six
    /// portability rules documented above.
    abstract GetPageInContext: slug: string * ctx: AccessContext -> Async<PublicPage option>

/// Phase 632 — helpers around `IPublicContentApi`'s gated enumeration.
/// Kept as free functions rather than interface members so the seam stays
/// one member wider, not four (every added member is a break for an
/// external implementer).
[<RequireQualifiedAccess>]
module PublicContentApi =

    /// The public-egress gate applied to an already-enumerated list — the
    /// Phase 38 predicate, in the one place `ListPagesPublic` implementers
    /// and caller-supplied-list surfaces can both reach it.
    let gateAt (now: DateTimeOffset) (pages: PublicPage list) : PublicPage list =
        pages |> List.filter (PublicPage.isPubliclyDiscoverable now)

    /// The canonical default body for `IPublicContentApi.ListPagesPublic`:
    /// enumerate ungated, then gate. An implementation whose backing store
    /// cannot push the predicate into its query satisfies the member with
    /// this one line.
    let defaultListPagesPublic
        (api: IPublicContentApi)
        (now: DateTimeOffset)
        (prefix: string)
        : Async<PublicPage list> =
        async {
            let! pages = api.ListPages prefix
            return gateAt now pages
        }

    /// `ListPagesPublic` at the current wall clock — for a single-step
    /// caller with no need to pin an instant. A multi-step pass (static
    /// export, a sitemap + shard pair) should pin `now` itself and call
    /// `ListPagesPublic` directly.
    let listPagesPublicNow (api: IPublicContentApi) (prefix: string) : Async<PublicPage list> =
        api.ListPagesPublic(DateTimeOffset.UtcNow, prefix)