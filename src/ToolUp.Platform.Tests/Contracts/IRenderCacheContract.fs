module ToolUp.Platform.Tests.Contracts.IRenderCacheContract

open System
open Expecto
open ToolUp.PublicRendering

// ─── IRenderCache contract pack (Phase 84) ──────────────────────────
//
// Parametrised tests for any `IRenderCache` implementation. The factory
// yields a fresh, empty cache instance per call so tests don't interfere.
//
// Expiry is exercised deterministically without sleeping: the cache
// derives `ExpiresAt` from `RenderedAt + policy.ttlSeconds`, so a page
// whose `RenderedAt` is in the past is already expired the moment it is
// stored. `RenderedPage.forStore html renderedAt` lets a test choose the
// `RenderedAt`, making "store an already-expired entry" a one-liner.
//
// Every shipped impl (`InMemoryRenderCache`, `BlobRenderCache`) also
// implements `IRenderCacheInvalidation`; the `PurgeSlug` tests cast the
// factory result and require it.

let private mkKey slug scope = {
    Slug = slug
    ScopeId = scope
    ContentVersion = ""
}

let private freshPage html =
    RenderedPage.forStore html DateTimeOffset.UtcNow

let private expiredPage html =
    // RenderedAt 10s in the past — with any short TTL the derived
    // ExpiresAt lands in the past, so the entry is immediately expired.
    RenderedPage.forStore html (DateTimeOffset.UtcNow.AddSeconds(-10.0))

let tests (name: string) (factory: unit -> IRenderCache) =

    testList $"{name} — IRenderCache contract" [

        testCaseAsync "Set then TryGet returns a fresh entry with the stored HTML"
        <| async {
            let cache = factory ()
            let key = mkKey "about" "public"
            do! cache.Set key (freshPage "<html>about</html>") (CachePolicy.Cache(300, true))
            let! got = cache.TryGet key

            match got with
            | Some entry ->
                Expect.equal entry.Html "<html>about</html>" "stored HTML round-trips"
                Expect.isGreaterThan entry.ExpiresAt DateTimeOffset.UtcNow "fresh entry expires in the future"
            | None -> failtest "Expected a fresh cache hit"
        }

        testCaseAsync "TryGet on an unknown key returns None (miss)"
        <| async {
            let cache = factory ()
            let! got = cache.TryGet(mkKey "never-stored" "public")
            Expect.isNone got "unknown key is a miss"
        }

        testCaseAsync "NoCache policy stores nothing"
        <| async {
            let cache = factory ()
            let key = mkKey "ephemeral" "public"
            do! cache.Set key (freshPage "<html>x</html>") CachePolicy.NoCache
            let! got = cache.TryGet key
            Expect.isNone got "a NoCache Set is a no-op — nothing to read back"
        }

        testCaseAsync "an expired entry with no stale-while-revalidate is dropped (miss)"
        <| async {
            let cache = factory ()
            let key = mkKey "expired" "public"
            // TTL 1s but RenderedAt 10s ago → ExpiresAt is in the past.
            do! cache.Set key (expiredPage "<html>old</html>") (CachePolicy.Cache(1, false))
            let! got = cache.TryGet key
            Expect.isNone got "hard-expired, non-SWR entry must not be served"
        }

        testCaseAsync "an expired entry WITH stale-while-revalidate is served stale"
        <| async {
            let cache = factory ()
            let key = mkKey "stale" "public"
            do! cache.Set key (expiredPage "<html>stale</html>") (CachePolicy.Cache(1, true))
            let! got = cache.TryGet key

            match got with
            | Some entry ->
                Expect.equal entry.Html "<html>stale</html>" "stale HTML is still served"

                Expect.isLessThanOrEqual
                    entry.ExpiresAt
                    DateTimeOffset.UtcNow
                    "the served entry is genuinely past its TTL"

                Expect.isTrue entry.StaleWhileRevalidate "the SWR flag is preserved on the entry"
            | None -> failtest "Expected the stale-but-servable entry"
        }

        testCaseAsync "Set derives ExpiresAt from RenderedAt + ttl, overriding the placeholder"
        <| async {
            let cache = factory ()
            let key = mkKey "ttl-derive" "public"
            let renderedAt = DateTimeOffset.UtcNow
            // forStore seeds ExpiresAt = RenderedAt; Set must push it out by ttl.
            do! cache.Set key (RenderedPage.forStore "<html>t</html>" renderedAt) (CachePolicy.Cache(600, false))
            let! got = cache.TryGet key

            match got with
            | Some entry ->
                let ttl = (entry.ExpiresAt - entry.RenderedAt).TotalSeconds

                Expect.isGreaterThan
                    ttl
                    590.0
                    "ExpiresAt is ~600s past RenderedAt (policy-derived, not the placeholder)"
            | None -> failtest "Expected the entry"
        }

        testCaseAsync "Invalidate purges exactly the keyed entry"
        <| async {
            let cache = factory ()
            let key = mkKey "purge-me" "public"
            do! cache.Set key (freshPage "<html>p</html>") (CachePolicy.Cache(300, true))
            do! cache.Invalidate key
            let! got = cache.TryGet key
            Expect.isNone got "invalidated entry is gone"
        }

        testCaseAsync "Invalidate on an absent key is a no-op (idempotent)"
        <| async {
            let cache = factory ()
            // Should not throw.
            do! cache.Invalidate(mkKey "not-there" "public")
            Expect.isTrue true "invalidating an absent key did not throw"
        }

        testCaseAsync "scope isolation — a different ScopeId does not read another scope's entry (GP 4)"
        <| async {
            let cache = factory ()
            do! cache.Set (mkKey "shared-slug" "team-a") (freshPage "<html>A</html>") (CachePolicy.Cache(300, true))
            let! crossScope = cache.TryGet(mkKey "shared-slug" "team-b")
            Expect.isNone crossScope "team-b must not see team-a's cached render for the same slug"

            let! ownScope = cache.TryGet(mkKey "shared-slug" "team-a")
            Expect.isSome ownScope "team-a still reads its own entry"
        }

        testCaseAsync "PurgeSlug removes every scope/version of a slug"
        <| async {
            let cache = factory ()

            match box cache with
            | :? IRenderCacheInvalidation as inv ->
                do! cache.Set (mkKey "doc" "public") (freshPage "<html>pub</html>") (CachePolicy.Cache(300, true))
                do! cache.Set (mkKey "doc" "team-a") (freshPage "<html>a</html>") (CachePolicy.Cache(300, true))
                do! cache.Set (mkKey "other" "public") (freshPage "<html>o</html>") (CachePolicy.Cache(300, true))

                do! inv.PurgeSlug "doc"

                let! pub = cache.TryGet(mkKey "doc" "public")
                let! teamA = cache.TryGet(mkKey "doc" "team-a")
                let! untouched = cache.TryGet(mkKey "other" "public")

                Expect.isNone pub "public scope of the purged slug is gone"
                Expect.isNone teamA "team-a scope of the purged slug is gone"
                Expect.isSome untouched "an unrelated slug is untouched"
            | _ -> failtest "the cache impl under test must also implement IRenderCacheInvalidation"
        }
    ]