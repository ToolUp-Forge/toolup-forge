namespace ToolUp.PublicRendering

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