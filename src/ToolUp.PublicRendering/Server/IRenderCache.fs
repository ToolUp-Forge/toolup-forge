// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open System
open System.Security.Cryptography
open System.Text

// ─── Phase 84 — SSR render cache + incremental regeneration ──────────
//
// `IRenderCache` is the opt-in ISR (incremental-static-regeneration)
// tier for `ToolUp.PublicRendering`. A data-bound SSR page (Phase 83 —
// `IContentSource`) re-runs its backing query on every crawler / client
// hit; without a cache, every Googlebot pass re-executes an analytics
// projection or a retrieval query. The cache stores the rendered HTML
// keyed by `(slug, scope, content-version)` with a TTL and optional
// stale-while-revalidate, so the expensive resolution chain runs once
// per TTL window instead of once per request.
//
// **Off by default (GP 11 / GP 13).** A deployment that never composes
// `withRenderCache` is byte-for-byte identical to pre-84 — no DI
// registration, no lookup, no HTTP cache headers, no allocation. The
// cache only activates when explicitly composed, and even then a route
// whose policy is `NoCache` (the per-page default) skips the store
// path entirely.

/// Cache key. `(Slug, ScopeId, ContentVersion)` per the Phase 84 design.
///
/// - `Slug` — an **opaque cache discriminator**. For the content-file
///   page handler it is the page slug (path with leading `/` stripped;
///   `"index"` for the root). For a programmatic-SSR consumer
///   ([Phase 155]) it is any composite cache key the consumer chooses
///   (e.g. `"report/{tenant}/{quarter}"`) — the cache treats it as an
///   opaque string and never parses it. Use `RenderKey.forKey` to build
///   one.
/// - `ScopeId` — the requesting principal's storage scope
///   (`"public"` for anonymous; the team/user container otherwise).
///   Structural tenant isolation (GP 4): a request derives its
///   `ScopeId` from its resolved `AccessContext`, never from caller
///   input, so team A's lookup can never return team B's cached HTML.
/// - `ContentVersion` — a discriminator letting multiple content
///   versions of the same `(slug, scope)` coexist. The page handler
///   uses `""` today (single current version); the field is part of the
///   key so a future content-versioned authoring flow can shard the
///   cache without an interface change. A programmatic consumer
///   ([Phase 155]) flows its own single content-version stamp (e.g. a
///   package-version + deploy signature) through here so it drives the
///   cache sharding alongside the consumer's `Last-Modified` validator.
type RenderKey = {
    Slug: string
    ScopeId: string
    ContentVersion: string
}

module RenderKey =
    /// Phase 155 — smart constructor for a programmatic cache key. `slug`
    /// is an opaque cache discriminator (a programmatic consumer keys on
    /// its own composite string); `scopeId` isolates tenants (`"public"`
    /// for an anonymous / un-scoped resource); `contentVersion` is the
    /// caller-supplied content-version stamp (`""` = the single current
    /// version).
    let forKey (slug: string) (scopeId: string) (contentVersion: string) : RenderKey = {
        Slug = slug
        ScopeId = scopeId
        ContentVersion = contentVersion
    }

    /// Phase 155 — the common programmatic-consumer shape: a `"public"`-
    /// scope, single-current-version key over an opaque composite cache
    /// key.
    let forPublic (slug: string) : RenderKey = forKey slug "public" ""

/// Per-route cache policy, parsed from a page's `cache:` frontmatter key
/// (or a content source's metadata; or the compose-level default). The
/// default is `NoCache` — a page that declares nothing is served pure
/// per-request exactly as pre-84.
type CachePolicy =
    /// Pure per-request — never stored. The default.
    | NoCache
    /// Cache the rendered HTML for `ttlSeconds`. When
    /// `staleWhileRevalidate` is `true`, a request arriving after the
    /// TTL expires is served the stale HTML immediately while a
    /// background render refreshes the entry; when `false`, an expired
    /// entry is dropped and the request re-renders synchronously.
    | Cache of ttlSeconds: int * staleWhileRevalidate: bool

module CachePolicy =
    /// Parse a `cache:` frontmatter value into a policy. Recognised
    /// forms (case-insensitive, whitespace-trimmed):
    ///
    ///   - absent / `""` / `"off"` / `"none"` / `"0"` → `NoCache`
    ///   - `"300"`            → `Cache(300, staleWhileRevalidate = true)`
    ///   - `"300;swr"`        → `Cache(300, true)` (explicit)
    ///   - `"300;no-swr"`     → `Cache(300, false)`
    ///
    /// An unparseable value degrades to `NoCache` (fail-safe: a typo'd
    /// `cache:` key never silently serves stale content).
    let parse (raw: string option) : CachePolicy =
        match raw with
        | None -> NoCache
        | Some value ->
            let trimmed = value.Trim()

            match trimmed.ToLowerInvariant() with
            | ""
            | "off"
            | "none"
            | "0" -> NoCache
            | _ ->
                let parts = trimmed.Split(';') |> Array.map (fun p -> p.Trim())

                match Int32.TryParse parts[0] with
                | true, ttl when ttl > 0 ->
                    let swr =
                        if parts.Length > 1 then
                            not (
                                parts[1].Equals("no-swr", StringComparison.OrdinalIgnoreCase)
                                || parts[1].Equals("nostale", StringComparison.OrdinalIgnoreCase)
                            )
                        else
                            true

                    Cache(ttl, swr)
                | _ -> NoCache

/// A rendered, cacheable page. The body plus the metadata the page
/// handler needs to emit HTTP cache headers and decide freshness on a
/// later `TryGet`.
///
/// `ExpiresAt` and `StaleWhileRevalidate` are authoritatively (re)derived
/// by `IRenderCache.Set` from the supplied `CachePolicy` — a caller
/// constructing a `RenderedPage` to store passes `RenderedPage.forStore`
/// (which seeds `ExpiresAt = RenderedAt` as a placeholder) and lets
/// `Set` overwrite them. On `TryGet` the returned value carries the
/// derived `ExpiresAt` / `StaleWhileRevalidate` so the handler can
/// distinguish a fresh hit from a stale-but-servable one.
type RenderedPage = {
    /// The fully-rendered HTML document.
    Html: string
    /// Strong-ETag content hash over `Html` (no surrounding quotes; the
    /// handler wraps it for the wire).
    ContentHash: string
    /// When the HTML was rendered. Drives the `Last-Modified` header.
    RenderedAt: DateTimeOffset
    /// When the entry stops being fresh. Derived by `Set` from
    /// `RenderedAt + policy.ttlSeconds`.
    ExpiresAt: DateTimeOffset
    /// Whether an expired entry may be served stale while a background
    /// render refreshes it. Derived by `Set` from the policy.
    StaleWhileRevalidate: bool
    /// Phase 86 — the resolved page's audience, stored so the handler can
    /// re-run the authorization gate on a cache *hit* without re-resolving
    /// the page. Entries are keyed by scope, so a gated page is cached
    /// per-scope; storing the audience lets the handler still enforce
    /// role differentiation *within* a scope (e.g. two team members where
    /// only one holds the gating role) on every hit. Defaults to `Public`.
    Audience: PageAudience
}

module RenderedPage =
    /// Strong content hash (ETag value) over the rendered HTML.
    /// SHA-256, lowercase hex. Server-side only (this companion has no
    /// Fable surface), so the BCL crypto API is fine.
    let hash (html: string) : string =
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes html)
        Convert.ToHexStringLower bytes

    /// Construct a `RenderedPage` ready to hand to `IRenderCache.Set`.
    /// `ExpiresAt` is seeded to `renderedAt` as a placeholder — `Set`
    /// overwrites it from the policy — and `StaleWhileRevalidate` to
    /// `false` for the same reason.
    let forStore (html: string) (renderedAt: DateTimeOffset) : RenderedPage = {
        Html = html
        ContentHash = hash html
        RenderedAt = renderedAt
        ExpiresAt = renderedAt
        StaleWhileRevalidate = false
        Audience = PageAudience.Public
    }

/// Opt-in render cache. Six portability rules (GP 12 — every substrate
/// interface a distributed framework could implement must satisfy all
/// six):
///
///   1. **Identity by value.** `RenderKey` is three `string`s;
///      `RenderedPage` is `string` / `DateTimeOffset` / `bool`. No live
///      handle. ✓
///   2. **Async at every boundary.** Every method returns `Async<_>`. ✓
///   3. **Retry as data.** N/A — a cache get/set/invalidate surfaces no
///      retry policy; a distributed impl wraps its own (e.g. a Redis
///      retry record) without changing this contract. ✓
///   4. **Stateless between invocations.** Each call carries the full
///      `RenderKey` (and, for `Set`, the full page + policy). The dict
///      / blob index inside an impl is implementation detail — a
///      distributed impl (Redis, a shared blob container) works with no
///      in-memory continuity. ✓
///   5. **No cross-shard ordering.** Each op is a point operation keyed
///      by `RenderKey`; no ordering contract across keys. ✓
///   6. **Precision at the lower bound.** TTL is declared in whole
///      seconds (`CachePolicy.ttlSeconds : int`). No sub-second promise
///      an impl couldn't honour. ✓
type IRenderCache =
    /// Look up a cached render. Returns `Some` when an entry exists and
    /// is either fresh (`now < ExpiresAt`) or stale-but-servable
    /// (`StaleWhileRevalidate`); returns `None` on a miss or on an
    /// expired entry with no stale-while-revalidate (which the impl
    /// drops). The caller inspects `ExpiresAt` to distinguish fresh from
    /// stale and trigger a background refresh for the latter.
    abstract TryGet: key: RenderKey -> Async<RenderedPage option>

    /// Store a render under `key`. The impl derives `ExpiresAt` /
    /// `StaleWhileRevalidate` from `policy` (ignoring whatever placeholder
    /// values the passed `page` carries). A `NoCache` policy is a no-op —
    /// nothing is stored.
    abstract Set: key: RenderKey -> page: RenderedPage -> policy: CachePolicy -> Async<unit>

    /// Purge the entry at exactly `key`. Idempotent — purging an absent
    /// key is a no-op.
    abstract Invalidate: key: RenderKey -> Async<unit>

/// Slug-level purge surface for the publish / CMS invalidation hook.
/// `IRenderCache.Invalidate` purges one exact `(slug, scope, version)`
/// key; `PurgeSlug` purges *every* cached scope/version of a slug — the
/// shape the CMS authoring layer (Phase 89) and `publish_narrative` need
/// when a page is republished and the editor doesn't know which scopes
/// have cached it. The default cache impls implement both interfaces.
type IRenderCacheInvalidation =
    /// Purge every cached entry for `slug`, across all scopes and
    /// content versions. Idempotent.
    abstract PurgeSlug: slug: string -> Async<unit>

/// Compose-time render-cache settings. Carries the default policy
/// applied to a resolved page that declares no explicit `cache:`
/// frontmatter key — the lever a deployment uses to cache its
/// (frontmatter-less) dynamic content-source pages without annotating
/// each one. Defaults to `NoCache` so file/overlay pages without a
/// `cache:` key stay pure per-request unless the deployment opts in.
type RenderCacheSettings = { DefaultPolicy: CachePolicy }

module RenderCacheSettings =
    let defaults: RenderCacheSettings = { DefaultPolicy = NoCache }

/// Phase 155 — handler-agnostic render memoisation over `IRenderCache`.
/// Lifted out of `PublicPageHandler` so any handler — not just the
/// content-file page handler — can memoise an expensive deterministic
/// render keyed on an arbitrary composite `RenderKey`, with the same
/// lookup + store + stale-while-revalidate semantics the page handler
/// uses. The caller supplies its own `render` thunk and key.
module RenderCache =
    /// Look up `key`; on a fresh hit return it; on a stale-but-servable
    /// hit return it immediately and refresh the entry on a detached
    /// background task (stale-while-revalidate); on a miss run `render`,
    /// store it under `policy` (a `NoCache` policy stores nothing), and
    /// return the freshly-rendered page.
    ///
    /// `render` must be deterministic for the key — the cache serves a
    /// memoised copy within the TTL window — and is captured by value, so
    /// the background refresh is safe to run after the originating request
    /// has completed. Page-pipeline concerns (audience re-gating, head
    /// injection) live in `PublicPageHandler`, not here; this helper is
    /// the pure cache-orchestration core.
    let getOrRender
        (cache: IRenderCache)
        (key: RenderKey)
        (policy: CachePolicy)
        (render: unit -> Async<string>)
        : Async<RenderedPage> =
        async {
            let! cached = cache.TryGet key

            match cached with
            | Some entry ->
                let stale = DateTimeOffset.UtcNow >= entry.ExpiresAt

                if stale && entry.StaleWhileRevalidate then
                    Async.Start(
                        async {
                            try
                                let! html = render ()
                                let refreshed = RenderedPage.forStore html DateTimeOffset.UtcNow
                                do! cache.Set key refreshed policy
                            with _ ->
                                ()
                        }
                    )

                return entry
            | None ->
                let! html = render ()
                let rendered = RenderedPage.forStore html DateTimeOffset.UtcNow

                match policy with
                | NoCache -> ()
                | Cache _ -> do! cache.Set key rendered policy

                return rendered
        }