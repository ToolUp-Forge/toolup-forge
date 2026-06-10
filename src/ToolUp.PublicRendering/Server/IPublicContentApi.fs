namespace ToolUp.PublicRendering

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
    abstract ListPages: prefix: string -> Async<PublicPage list>

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