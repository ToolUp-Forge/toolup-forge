module ToolUp.Platform.Tests.InProcess.Phase971PublicRenderingTests

open System
open Expecto
open ToolUp.Platform.BlobStorage
open ToolUp.PublicRendering
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.DataObjectStoreTests

// ─── Phase 971 — PublicRendering: a refused invalidation is reported ──
//
// `BlobRenderCache.Invalidate` and `PurgeSlug` are explicit invalidations a
// caller is told happened — a publish purges its slug so the new content is
// served at once. `IBlobStorage.Delete` answers `Ok` on a missing blob, so
// an `Error` is a refusal: the entry is still there and is served until its
// TTL. Both have no failure channel (`Async<unit>`), so they raise.

let private container = "rendercache-971"

let private key slug scope = {
    Slug = slug
    ScopeId = scope
    ContentVersion = ""
}

let private page html =
    RenderedPage.forStore html DateTimeOffset.UtcNow

let private policy = CachePolicy.Cache(300, true)

let private cacheRefusing (refused: string -> bool) =
    BlobRenderCache(DeleteRefusingBlobStorage(InMemoryBlobStorage(), refused), container)

let tests =
    testList "Phase 971 - PublicRendering" [
        testAsync "Invalidate raises when the entry delete is refused, and the entry is still served" {
            let cache = cacheRefusing (fun n -> n = "doc/public/_.json")
            let k = key "doc" "public"
            do! (cache :> IRenderCache).Set k (page "<html>doc</html>") policy

            match! Async.Catch((cache :> IRenderCache).Invalidate k) with
            | Choice1Of2() -> failtest "a refused invalidation must not read as done"
            | Choice2Of2 _ -> ()

            let! still = (cache :> IRenderCache).TryGet k
            Expect.isSome still "the kept entry is still served, so the caller had to be told"
        }

        testAsync "PurgeSlug attempts every entry and raises naming the one it could not delete" {
            let cache = cacheRefusing (fun n -> n = "doc/public/_.json")
            let c = cache :> IRenderCache
            do! c.Set (key "doc" "public") (page "<html>pub</html>") policy
            do! c.Set (key "doc" "team-a") (page "<html>a</html>") policy

            match! Async.Catch((cache :> IRenderCacheInvalidation).PurgeSlug "doc") with
            | Choice1Of2() -> failtest "a purge with a refused delete must not read as done"
            | Choice2Of2 ex -> Expect.stringContains ex.Message "doc/public/_.json" "the failure names the kept entry"

            let! kept = c.TryGet(key "doc" "public")
            let! purged = c.TryGet(key "doc" "team-a")
            Expect.isSome kept "the refused entry is still there"
            Expect.isNone purged "every other entry of the slug was still purged"
        }

        testAsync "PurgeSlug over healthy storage completes" {
            let cache = cacheRefusing (fun _ -> false)
            let c = cache :> IRenderCache
            do! c.Set (key "doc" "public") (page "<html>pub</html>") policy

            do! (cache :> IRenderCacheInvalidation).PurgeSlug "doc"

            let! gone = c.TryGet(key "doc" "public")
            Expect.isNone gone "the purged entry is gone"
        }
    ]