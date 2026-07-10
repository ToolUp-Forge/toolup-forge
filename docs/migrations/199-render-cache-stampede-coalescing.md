# Migration — Phase 199: Render-cache request coalescing (stampede protection)

**Status:** additive, opt-in, on-by-default *once a cacheable default policy is set*. A deployment that never composes a render cache — or composes one but leaves the SDK-default `NoCache` default policy — is **byte-for-byte identical** to the pre-199 pipeline: no coalescer resolved, no allocation, no behaviour change (GP 11 / GP 13).

## What changes

`ToolUp.PublicRendering`'s render cache (Phase 84) memoises the rendered HTML for a `RenderKey = (Slug, ScopeId, ContentVersion)` with a TTL and optional stale-while-revalidate (SWR). SWR closes the *warm-key* refresh window — one request refreshes an already-populated entry in the background while the rest are served stale. It does **not** cover the *cold* key: on a cache miss (a key never rendered, or the first hit after a publish-invalidation), the page handler re-runs the expensive `IContentSource` resolution + render. If **M** requests for the same key arrive in that miss window, all M execute the projection — the classic **cache-stampede / dog-pile**.

Phase 199 adds a per-`RenderKey` **single-flight** primitive so a cold-key traffic spike collapses to **one** render:

- **`IRenderCoalescer`** (`Server/IRenderCache.fs`) — the seam. `Coalesce key produce` runs `produce` at most once per in-flight key; concurrent callers for the same key await that single computation and observe its result. Distinct keys never block each other. Once the round completes the key is released, so a later miss (e.g. after expiry) starts a fresh round.
- **`InProcessRenderCoalescer`** (`Server/RenderCacheImpl.fs`) — the default, process-local impl (a `ConcurrentDictionary<RenderKey, Lazy<Task<obj>>>` single-flight). Registered automatically as a DI singleton whenever a render cache is composed.
- The page handler routes the cold-key **produce-and-store** step through the coalescer.

New public surface (all in `ToolUp.PublicRendering`):

| Symbol | Where | Purpose |
|---|---|---|
| `IRenderCoalescer` | `Server/IRenderCache.fs` | Per-`RenderKey` single-flight seam (six-rule clean) |
| `InProcessRenderCoalescer` / `InProcessRenderCoalescer.create` | `Server/RenderCacheImpl.fs` | Default process-local coalescer |
| `PublicRenderingServerApp.withRenderCoalescer` | `Server/PublicRenderingCompose.fs` | Supply a custom (e.g. distributed) coalescer |

## When coalescing engages

The coalescer is consulted on the **cached serve path only** (a deployment with no render cache never resolves one). Within that path, the miss engages the coalescer when the **compose-level default policy is cacheable** — i.e. the deployment called `withRenderCacheDefaultPolicy (Cache …)`. That is deliberately the pre-resolve signal: the effective policy for a page is only known *after* the very resolution we mean to collapse, so keying the engage-decision off the deployment default lets cold content-source pages (the stampede target — they carry no `cache:` frontmatter and take the default) coalesce without a chicken-and-egg resolve.

Consequences, stated honestly:

- **SDK-default `NoCache` default policy** → the miss path is byte-for-byte the pre-199 per-request path; the coalescer is never touched. A page made cacheable *solely* by its own `cache: 300` frontmatter (under a `NoCache` default) is still cached but **not** stampede-coalesced. If you want stampede protection for content-source pages, set a cacheable default (`withRenderCacheDefaultPolicy`), which is the recommended configuration for a data-bound SSR site anyway.
- **Cacheable default policy** → cold-key misses coalesce. A page with `cache: off` frontmatter overriding the cacheable default still routes through the coalescer (its render is shared under concurrency but never stored); this matches the render cache's existing invariant that a render is a pure function of its key within a scope.
- **Audience gating is per-caller.** The coalesced produce renders **gate-free** and the Phase 86 audience gate runs per caller *after* coalescing (mirroring the cache-hit path), so two same-scope principals with different roles still gate independently against the produced page's stored `Audience`. One consequence: a forbidden caller that happens to win the single-flight warms the cache for the role-holders in its scope, then receives its own 401/403 — the render cost is paid once for the scope, not per request.

## Multi-instance honesty (GP 12)

The default `InProcessRenderCoalescer` is **process-local** — it coalesces within one replica, exactly as `InMemoryRenderCache` caches within one replica. On a fleet of N replicas that already collapses each replica's own stampede to one render (the dominant win: N renders instead of M×N). A `BlobRenderCache` deployment that wants **cross-replica** single-flight — a cold key rendered once for the whole fleet — MAY supply a distributed coalescer (e.g. a lock keyed by `RenderKey` in Redis) via `withRenderCoalescer`. The seam allows it; the SDK does not mandate it.

## Adopting it

Nothing to do for the common case: compose a render cache with a cacheable default policy and coalescing is on.

```fsharp
open ToolUp.PublicRendering

PublicRenderingServerApp.create ()
|> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
|> PublicRenderingServerApp.withRenderCache (InMemoryRenderCache.create ())
|> PublicRenderingServerApp.withRenderCacheDefaultPolicy (CachePolicy.Cache(300, true))
|> PublicRenderingServerApp.run
// content-source pages now coalesce cold-key misses automatically
```

Cross-replica single-flight for a multi-instance deployment:

```fsharp
|> PublicRenderingServerApp.withRenderCache (BlobRenderCache.create storage)
|> PublicRenderingServerApp.withRenderCacheDefaultPolicy (CachePolicy.Cache(300, true))
|> PublicRenderingServerApp.withRenderCoalescer myDistributedCoalescer   // implements IRenderCoalescer
```

## Breaking change

None. `IRenderCoalescer` is registered only when a render cache is composed; the page handler resolves it only on the cached path and engages it only under a cacheable default policy. No signature on an existing public function changed (`serveCached` is private; the coalescer flows through DI).

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — the `RenderCoalescing (Phase 199)` suite covers the `IRenderCoalescer` contract (M concurrent callers → producer runs once and all observe the same result; distinct keys never block each other; a key is released after its round; a producer exception reaches every awaiter and frees the key) and the handler integration (M concurrent cold-key misses → resolved exactly once, all get the identical page; a later request is a plain cache hit; the cache-off path resolves per request; the SWR hit path is unaffected).

## Rollback

Set the default policy back to `NoCache` (or drop the render cache): the miss path reverts to the pre-199 per-request behaviour and the coalescer is never resolved. The `IRenderCoalescer` / `InProcessRenderCoalescer` symbols can remain in place harmlessly (no runtime cost when unused).

## See also

- [`docs/migrations/84-ssr-render-cache.md`](84-ssr-render-cache.md) — the render cache, `RenderKey`, and the SWR freshness model this builds on.
- [`docs/platform/dynamic-ssr.md`](../platform/dynamic-ssr.md) — the `IContentSource` resolution being coalesced.
