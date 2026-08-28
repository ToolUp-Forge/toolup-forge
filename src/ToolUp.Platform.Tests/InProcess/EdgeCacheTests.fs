// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.EdgeCacheTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.Secrets
open ToolUp.MediaLibrary
open ToolUp.PublicRendering
open ToolUp.Hosts.EdgeCache
open ToolUp.Platform.Tests.Contracts

// ─── Phase 472 — the edge-cache seam ──────────────────────────────────
//
// Five layers, in the order the phase builds them:
//
//   1. the pure seam surface — the no-op's allocation-free claim, the
//      `Cache-Control` renderer, the retry policy;
//   2. the detached-purge helper, which is where GP 7 (never block the
//      request path) and GP 13 (an unconfigured deployment pays nothing)
//      are actually implemented;
//   3. the render-cache fan-out — purge order and the slug→path map;
//   4. the media declarations — which response class gets which header,
//      and the two postures the config validator refuses;
//   5. the `ToolUp.Hosts.EdgeCache` sub-companion, proving both seams
//      from OUTSIDE the SDK (GP 12).
//
// The media wiring proper — an upload and a delete fanning out through a
// composed `IEdgeCache`, and a composed delegated signer replacing the
// origin-minted URL — lives in `MediaLibraryTests`, beside the library
// fixtures it needs.

// ─── 1. The pure seam surface ─────────────────────────────────────────

let private noopTests =
    testList "NoopEdgeCache" [
        test "every verb returns the SAME Async value — the allocation-free claim, structurally" {
            // The acceptance criterion says the noop default is
            // allocation-free on the request path. Asserted by reference
            // identity rather than by a benchmark: a shared, pre-built
            // `Async` cannot allocate per call, and reference equality
            // is a fact a test can hold rather than a timing it has to
            // hope for.
            let edge = NoopEdgeCache.create ()
            let a = edge.PurgePaths [ "/one" ]
            let b = edge.PurgePaths [ "/two"; "/three" ]
            let c = edge.PurgePrefix "/p/"
            let d = edge.PurgeTags [ "t" ]

            Expect.isTrue (obj.ReferenceEquals(a, b)) "two path purges share one Async value"
            Expect.isTrue (obj.ReferenceEquals(a, c)) "so does a prefix purge"
            Expect.isTrue (obj.ReferenceEquals(a, d)) "and a tag purge"
        }

        test "the module exposes ONE shared instance" {
            Expect.isTrue
                (obj.ReferenceEquals(NoopEdgeCache.create (), NoopEdgeCache.instance))
                "create () hands back the shared instance rather than minting a second"
        }

        test "it is recognised as a no-op by EdgeCache.isNoop, and a real edge is not" {
            Expect.isTrue (EdgeCache.isNoop NoopEdgeCache.instance) "the declared no-op"
            Expect.isFalse (EdgeCache.isNoop (IEdgeCacheContract.RecordingEdgeCache() :> IEdgeCache)) "a real edge"
        }

        testCaseAsync "and it succeeds"
        <| async {
            let! r = NoopEdgeCache.instance.PurgePaths [ "/x" ]
            Expect.equal r (Ok()) "purging when there is no edge is a success, not a failure"
        }
    ]

let private cacheHeaderTests =
    testList "EdgeCacheHeader.render" [
        test "EdgeCacheUnset emits NO header — the pre-472 behaviour (GP 11)" {
            // The distinction that matters most in this file. `None` is
            // "write no header"; it is not `private, max-age=0`, and it
            // is not an empty string.
            Expect.isNone (EdgeCacheHeader.render EdgeCacheUnset) "no header at all"
        }

        test "the three declared postures render as expected" {
            Expect.equal (EdgeCacheHeader.render EdgeNoStore) (Some "no-store, no-cache, must-revalidate") "no-store"

            Expect.equal (EdgeCacheHeader.render (EdgePrivate 60)) (Some "private, max-age=60") "private"

            Expect.equal
                (EdgeCacheHeader.render (EdgePublic(60, 3600)))
                (Some "public, max-age=60, s-maxage=3600")
                "public + shared"
        }

        test "a negative age clamps to zero rather than failing a serving path" {
            Expect.equal (EdgeCacheHeader.render (EdgePrivate -5)) (Some "private, max-age=0") "clamped"

            Expect.equal
                (EdgeCacheHeader.render (EdgePublic(-1, -2)))
                (Some "public, max-age=0, s-maxage=0")
                "both clamped"
        }
    ]

let private retryTests =
    testList "EdgePurgeRetry / purgeWithRetry" [
        test "a non-positive attempt count reads as ONE attempt, never as zero" {
            Expect.equal
                (EdgePurgeRetry.effectiveAttempts {
                    Attempts = 0
                    InitialBackoff = TimeSpan.Zero
                })
                1
                "zero"

            Expect.equal
                (EdgePurgeRetry.effectiveAttempts {
                    Attempts = -3
                    InitialBackoff = TimeSpan.Zero
                })
                1
                "negative"
        }

        testCaseAsync "a transient failure is retried up to the attempt count"
        <| async {
            let mutable calls = 0

            let purge () = async {
                calls <- calls + 1

                if calls < 3 then
                    return Error(PurgeTransportFailure "flaky")
                else
                    return Ok()
            }

            let! result =
                EdgeCache.purgeWithRetry
                    {
                        Attempts = 3
                        InitialBackoff = TimeSpan.Zero
                    }
                    purge

            Expect.equal result (Ok()) "the third attempt succeeded"
            Expect.equal calls 3 "and it took three"
        }

        testCaseAsync "attempts are bounded — a permanently failing purge stops"
        <| async {
            let mutable calls = 0

            let purge () = async {
                calls <- calls + 1
                return Error(PurgeTransportFailure "down")
            }

            let! result =
                EdgeCache.purgeWithRetry
                    {
                        Attempts = 2
                        InitialBackoff = TimeSpan.Zero
                    }
                    purge

            Expect.equal result (Error(PurgeTransportFailure "down")) "the terminal outcome is reported"
            Expect.equal calls 2 "exactly the declared attempts"
        }

        testCaseAsync "PurgeNotSupported is terminal on the FIRST attempt"
        <| async {
            // A capability does not appear on a retry. Burning the
            // backoff window on it delays nothing but the audit line.
            let mutable calls = 0

            let purge () = async {
                calls <- calls + 1
                return Error(PurgeNotSupported "PurgeTags")
            }

            let! result =
                EdgeCache.purgeWithRetry
                    {
                        Attempts = 5
                        InitialBackoff = TimeSpan.FromSeconds 30.0
                    }
                    purge

            Expect.equal result (Error(PurgeNotSupported "PurgeTags")) "reported as-is"
            Expect.equal calls 1 "and never retried"
        }

        testCaseAsync "a THROWING purge becomes a transport failure, not an escaped exception"
        <| async {
            let! result =
                EdgeCache.purgeWithRetry
                    {
                        Attempts = 1
                        InitialBackoff = TimeSpan.Zero
                    }
                    (fun () -> async { return failwith "socket exploded" })

            match result with
            | Error(PurgeTransportFailure d) -> Expect.stringContains d "socket exploded" "the message survives"
            | other -> failtestf "expected a transport failure, got %A" other
        }
    ]

// ─── 2. The detached purge — GP 7 + GP 13 ─────────────────────────────

let private detachedTests =
    testList "EdgeCache.purgeDetached" [
        // Both short-circuit claims are asserted through the general
        // `purgeDetached`, whose last parameter is the thunk that would
        // reach the edge. Observing that the thunk NEVER RUNS is the
        // honest form of "nothing was scheduled": it is a fact about the
        // helper rather than about a recording double, and it holds for
        // any `IEdgeCache`, not only one the test happens to control.

        test "no composed edge never invokes the purge thunk (GP 13)" {
            let mutable invoked = false

            EdgeCache.purgeDetached None None "probe" (fun _ ->
                invoked <- true
                async.Return(Ok()))

            Thread.Sleep 150
            Expect.isFalse invoked "an unconfigured deployment schedules no work at all"
        }

        test "the DECLARED no-op never invokes it either" {
            // A deployment that composed `NoopEdgeCache` rather than
            // nothing must pay exactly what the absent one pays — else
            // "declare your absence explicitly" would carry a cost, and
            // nobody would.
            let mutable invoked = false

            EdgeCache.purgeDetached None (Some NoopEdgeCache.instance) "probe" (fun _ ->
                invoked <- true
                async.Return(Ok()))

            Thread.Sleep 150
            Expect.isFalse invoked "the declared no-op short-circuits before the thunk runs"
        }

        test "…and a REAL edge does invoke it — the control for the two above" {
            // Without this, both claims above would pass just as happily
            // if `purgeDetached` never invoked anything for anyone.
            let mutable invoked = false
            let recording = IEdgeCacheContract.RecordingEdgeCache()

            EdgeCache.purgeDetached None (Some(recording :> IEdgeCache)) "probe" (fun e ->
                invoked <- true
                e.PurgePaths [ "/a" ])

            Expect.isTrue (recording.WaitFor 1) "the purge reached the edge"
            Expect.isTrue invoked "and the thunk ran"
        }

        test "an EMPTY path list schedules nothing" {
            let recording = IEdgeCacheContract.RecordingEdgeCache()
            EdgeCache.purgePathsDetached None (Some(recording :> IEdgeCache)) []
            Expect.isTrue (recording.StaysSilentFor(TimeSpan.FromMilliseconds 150.0)) "nothing to purge, nothing sent"
        }

        test "a whitespace prefix schedules nothing" {
            let recording = IEdgeCacheContract.RecordingEdgeCache()
            EdgeCache.purgePrefixDetached None (Some(recording :> IEdgeCache)) "   "
            Expect.isTrue (recording.StaysSilentFor(TimeSpan.FromMilliseconds 150.0)) "no purge issued"
        }

        test "a composed edge DOES receive the purge, off the calling thread" {
            let recording = IEdgeCacheContract.RecordingEdgeCache()
            EdgeCache.purgePathsDetached None (Some(recording :> IEdgeCache)) [ "/a"; "/b" ]
            Expect.isTrue (recording.WaitFor 1) "the purge arrived"
            Expect.equal recording.AllPaths [ "/a"; "/b" ] "with the paths it was given"
        }

        test "the caller RETURNS IMMEDIATELY even when the edge is slow (GP 7)" {
            // The claim the phase is really making: a CDN outage must
            // not extend the publish that triggered the purge. A five-
            // second edge and a call that returns in milliseconds is the
            // whole of it.
            use gate = new ManualResetEventSlim(false)

            let slow =
                { new IEdgeCache with
                    member _.Name = "slow"
                    member _.Propagation = PurgeEventualUnbounded

                    member _.PurgePaths(_) = async {
                        gate.Wait(TimeSpan.FromSeconds 5.0) |> ignore
                        return Ok()
                    }

                    member _.PurgePrefix(_) = async { return Ok() }
                    member _.PurgeTags(_) = async { return Ok() }
                }

            let sw = Diagnostics.Stopwatch.StartNew()
            EdgeCache.purgePathsDetached None (Some slow) [ "/a" ]
            sw.Stop()

            Expect.isLessThan sw.ElapsedMilliseconds 1000L "the publish path did not wait on the edge"

            gate.Set()
        }

        test "a FAILING purge does not surface to the caller and does not crash the process" {
            let failing =
                { new IEdgeCache with
                    member _.Name = "broken"
                    member _.Propagation = PurgeEventualUnbounded
                    member _.PurgePaths(_) = async { return failwith "the CDN is down" }
                    member _.PurgePrefix(_) = async { return Error(PurgeRejected "nope") }
                    member _.PurgeTags(_) = async { return Ok() }
                }

            EdgeCache.purgePathsDetached None (Some failing) [ "/a" ]
            EdgeCache.purgePrefixDetached None (Some failing) "/a/"
            Thread.Sleep 300
        }
    ]

// ─── 3. The render-cache fan-out ──────────────────────────────────────

let private slugPathTests =
    testList "RenderCacheEdgePaths.forSlug" [
        test "a slug yields both the bare path and its trailing-slash twin" {
            // A CDN keys on the URI as received, so `/hello` and
            // `/hello/` are two edge objects. Purging only one leaves
            // the other stale — invisibly, because whoever checks types
            // one of the two.
            Expect.equal (RenderCacheEdgePaths.forSlug "hello") [ "/hello"; "/hello/" ] "bare slug"
        }

        test "a leading or trailing slash is normalised rather than doubled" {
            Expect.equal (RenderCacheEdgePaths.forSlug "/hello") [ "/hello"; "/hello/" ] "leading"
            Expect.equal (RenderCacheEdgePaths.forSlug "hello/") [ "/hello"; "/hello/" ] "trailing"
            Expect.equal (RenderCacheEdgePaths.forSlug "  hello  ") [ "/hello"; "/hello/" ] "surrounding space"
        }

        test "a nested slug keeps its interior slashes" {
            Expect.equal
                (RenderCacheEdgePaths.forSlug "blog/2026/post")
                [ "/blog/2026/post"; "/blog/2026/post/" ]
                "nested"
        }

        test "an empty slug is the site root" {
            Expect.equal (RenderCacheEdgePaths.forSlug "") [ "/" ] "root"
            Expect.equal (RenderCacheEdgePaths.forSlug "/") [ "/" ] "root, spelled with a slash"
        }
    ]

let private renderFanOutTests =
    testList "EdgeAwareRenderCacheInvalidation" [
        testCaseAsync "purges the ORIGIN cache first, then fans out to the edge"
        <| async {
            // The order is the reason this is a decorator. Purging the
            // edge first would let an edge node re-fetch while the
            // origin cache still held the stale render, re-populating
            // the edge with exactly the bytes the purge was removing.
            let order = System.Collections.Concurrent.ConcurrentQueue<string>()

            let inner =
                { new IRenderCacheInvalidation with
                    member _.PurgeSlug(_) = async { order.Enqueue "origin" }
                }

            let edge =
                { new IEdgeCache with
                    member _.Name = "ordering"
                    member _.Propagation = PurgeImmediate

                    member _.PurgePaths(_) = async {
                        order.Enqueue "edge"
                        return Ok()
                    }

                    member _.PurgePrefix(_) = async { return Ok() }
                    member _.PurgeTags(_) = async { return Ok() }
                }

            let decorated = EdgeAwareRenderCacheInvalidation.create None edge inner
            do! decorated.PurgeSlug "hello"

            // The edge half is detached, so wait for it rather than
            // racing it.
            let sw = Diagnostics.Stopwatch.StartNew()

            while order.Count < 2 && sw.Elapsed < TimeSpan.FromSeconds 5.0 do
                Thread.Sleep 10

            Expect.equal (order |> Seq.toList) [ "origin"; "edge" ] "origin cache first, edge second"
        }

        testCaseAsync "fans the default slug→path map out to the edge"
        <| async {
            let recording = IEdgeCacheContract.RecordingEdgeCache()

            let inner =
                { new IRenderCacheInvalidation with
                    member _.PurgeSlug(_) = async { return () }
                }

            let decorated =
                EdgeAwareRenderCacheInvalidation.create None (recording :> IEdgeCache) inner

            do! decorated.PurgeSlug "blog/hello"
            Expect.isTrue (recording.WaitFor 1) "the edge purge arrived"
            Expect.equal recording.AllPaths [ "/blog/hello"; "/blog/hello/" ] "both URI variants"
        }

        testCaseAsync "honours an explicit slug→path map for a deployment whose URLs are not /slug"
        <| async {
            let recording = IEdgeCacheContract.RecordingEdgeCache()

            let inner =
                { new IRenderCacheInvalidation with
                    member _.PurgeSlug(_) = async { return () }
                }

            let decorated =
                EdgeAwareRenderCacheInvalidation.createWith
                    (fun slug -> [ "/docs/" + slug + ".html" ])
                    None
                    (recording :> IEdgeCache)
                    inner

            do! decorated.PurgeSlug "intro"
            Expect.isTrue (recording.WaitFor 1) "the edge purge arrived"
            Expect.equal recording.AllPaths [ "/docs/intro.html" ] "the deployment's own mapping"
        }

        testCaseAsync "the in-memory render cache still purges its own entries through the decorator"
        <| async {
            // The decorator must not swallow the Phase 84 behaviour it
            // wraps — a regression here would be invisible until a
            // republished page served stale from the ORIGIN.
            let cache = InMemoryRenderCache.create ()
            let recording = IEdgeCacheContract.RecordingEdgeCache()

            let key: RenderKey = {
                Slug = "doc"
                ScopeId = "s1"
                ContentVersion = "v1"
            }

            let page = RenderedPage.forStore "<p>hi</p>" DateTimeOffset.UtcNow

            do! cache.Set key page (Cache(3600, false))
            let! stored = cache.TryGet key
            Expect.isSome stored "precondition: the render is cached"

            let inner = box cache :?> IRenderCacheInvalidation

            let decorated =
                EdgeAwareRenderCacheInvalidation.create None (recording :> IEdgeCache) inner

            do! decorated.PurgeSlug "doc"

            let! afterPurge = cache.TryGet key
            Expect.isNone afterPurge "the origin entry is gone"
            Expect.isTrue (recording.WaitFor 1) "and the edge was told"
        }
    ]

// ─── 4. The media declarations ────────────────────────────────────────

let private mediaDeclarationTests =
    testList "MediaEdgeCacheOptions" [
        test "the DEFAULT declares nothing, on every class (GP 11)" {
            let d = MediaEdgeCacheOptions.defaults
            Expect.equal d.Segment EdgeCacheUnset "segment"
            Expect.equal d.Manifest EdgeCacheUnset "manifest"
            Expect.equal d.Poster EdgeCacheUnset "poster"
            Expect.equal d.Original EdgeCacheUnset "original"

            Expect.equal
                MediaLibraryOptions.defaults.EdgeCache
                MediaEdgeCacheOptions.defaults
                "and the shipped library options carry it"
        }

        test "edgeCacheabilityForDerived routes each extension to its class" {
            let options = {
                MediaLibraryOptions.defaults with
                    EdgeCache = {
                        Segment = EdgePublic(1, 2)
                        Manifest = EdgePrivate 3
                        Poster = EdgeNoStore
                        Original = EdgeCacheUnset
                    }
            }

            let forFile = MediaLibraryOptions.edgeCacheabilityForDerived options
            Expect.equal (forFile "index.m3u8") (EdgePrivate 3) "manifest"
            Expect.equal (forFile "INDEX.M3U8") (EdgePrivate 3) "manifest, case-insensitively"
            Expect.equal (forFile "seg0.ts") (EdgePublic(1, 2)) "segment"
            Expect.equal (forFile "seg0.m4s") (EdgePublic(1, 2)) "fmp4 segment"
            Expect.equal (forFile "poster.jpg") EdgeNoStore "poster"
            Expect.equal (forFile "something.bin") EdgeNoStore "an unrecognised derived artefact takes the poster class"
        }

        test "the worked postures differ in exactly one place — the manifest" {
            // The encrypted posture is the unencrypted one with the
            // manifest withdrawn, because a rewritten manifest may carry
            // the requesting viewer's token. Asserted so a later edit
            // that "tidies" them together fails here rather than in
            // someone's CDN.
            Expect.equal
                MediaEdgeCacheOptions.cdnEncrypted.Segment
                MediaEdgeCacheOptions.cdnUnencrypted.Segment
                "segments cache identically — they are ciphertext, never rewritten"

            Expect.equal MediaEdgeCacheOptions.cdnEncrypted.Manifest EdgeCacheUnset "the encrypted manifest is withheld"

            Expect.notEqual
                MediaEdgeCacheOptions.cdnUnencrypted.Manifest
                EdgeCacheUnset
                "and the unencrypted one is not"
        }

        test "NEITHER worked posture makes the original shared-cacheable" {
            let isPublic =
                function
                | EdgePublic _ -> true
                | _ -> false

            Expect.isFalse (isPublic MediaEdgeCacheOptions.cdnUnencrypted.Original) "unencrypted"
            Expect.isFalse (isPublic MediaEdgeCacheOptions.cdnEncrypted.Original) "encrypted"
        }
    ]

let private mediaRefusalTests =
    testList "MediaConfigValidator edge refusals" [
        test "the shipped defaults are accepted" {
            Expect.isNone
                (MediaConfigValidator.edgeCacheabilityRefusal MediaLibraryOptions.defaults)
                "declaring nothing is always safe"
        }

        test "a PUBLIC original is refused — both routes serving it are gated" {
            let options = {
                MediaLibraryOptions.defaults with
                    EdgeCache = {
                        MediaEdgeCacheOptions.defaults with
                            Original = EdgePublic(60, 60)
                    }
            }

            match MediaConfigValidator.edgeCacheabilityRefusal options with
            | Some message -> Expect.stringContains message "EdgeCache.Original" "the message names the field"
            | None -> failtest "a shared cache holding a scope-gated response must be refused"
        }

        test "a PRIVATE original is accepted" {
            let options = {
                MediaLibraryOptions.defaults with
                    EdgeCache = {
                        MediaEdgeCacheOptions.defaults with
                            Original = EdgePrivate 30
                    }
            }

            Expect.isNone
                (MediaConfigValidator.edgeCacheabilityRefusal options)
                "a browser may hold it; a shared cache may not"
        }

        test "a PUBLIC manifest is refused ONLY when encryption is on" {
            let publicManifest = {
                MediaEdgeCacheOptions.defaults with
                    Manifest = EdgePublic(60, 60)
            }

            let unencrypted = {
                MediaLibraryOptions.defaults with
                    EdgeCache = publicManifest
                    EncryptHlsByDefault = false
            }

            let encrypted = {
                unencrypted with
                    EncryptHlsByDefault = true
            }

            Expect.isNone
                (MediaConfigValidator.edgeCacheabilityRefusal unencrypted)
                "an unencrypted manifest is returned byte-for-byte and is safe to cache"

            match MediaConfigValidator.edgeCacheabilityRefusal encrypted with
            | Some message ->
                Expect.stringContains message "EdgeCache.Manifest" "names the field"
                Expect.stringContains message "token" "and says why — the rewritten key URI can carry a viewer's token"
            | None -> failtest "an encrypted manifest is rewritten per request and must not be shared-cached"
        }

        test "the worked CDN postures both pass their own validator" {
            let unencrypted = {
                MediaLibraryOptions.defaults with
                    EdgeCache = MediaEdgeCacheOptions.cdnUnencrypted
                    EncryptHlsByDefault = false
            }

            let encrypted = {
                MediaLibraryOptions.defaults with
                    EdgeCache = MediaEdgeCacheOptions.cdnEncrypted
                    EncryptHlsByDefault = true
            }

            Expect.isNone (MediaConfigValidator.edgeCacheabilityRefusal unencrypted) "cdnUnencrypted"
            Expect.isNone (MediaConfigValidator.edgeCacheabilityRefusal encrypted) "cdnEncrypted"
        }
    ]

let private mediaEdgePathTests =
    testList "MediaEdgePaths" [
        test "the derived prefix covers every rendition file of one item" {
            Expect.equal (MediaEdgePaths.derivedPrefix (MediaId "abc")) "/api/media/hls/abc/" "prefix"
        }

        test "the original paths are exactly the two routes that serve it" {
            Expect.equal
                (MediaEdgePaths.originalPaths (MediaId "abc"))
                [ "/api/media/stream/abc"; "/media/signed/abc" ]
                "both routes"
        }

        test "the derived prefix is a prefix OF the hls route, and does not collide with the key route" {
            // `/api/media/hls-key/` must never be swept by a purge of
            // `/api/media/hls/` — a purge is harmless there, but a
            // collision in the other direction (a key route that looked
            // like a rendition path) would not be.
            let prefix = MediaEdgePaths.derivedPrefix (MediaId "abc")

            Expect.isFalse
                (HlsKeyDelivery.RoutePrefix.StartsWith prefix)
                "the key route is not under the derived prefix"
        }
    ]

// ─── 5. The sub-companion — the seams proved from outside ─────────────

/// A stub `HttpMessageHandler` recording the request and returning a
/// fixed status. The purge adapter's whole job is "build the right
/// request, classify the response", so this is what a test needs to see.
type private StubHandler(status: HttpStatusCode) =
    inherit HttpMessageHandler()

    member val LastRequest: HttpRequestMessage option = None with get, set
    member val LastBody: string = "" with get, set

    override this.SendAsync(request, _: CancellationToken) : Task<HttpResponseMessage> = task {
        this.LastRequest <- Some request

        match request.Content with
        | null -> this.LastBody <- ""
        | content ->
            let! body = content.ReadAsStringAsync()
            this.LastBody <- body

        return new HttpResponseMessage(status)
    }

type private StubSecretStore(value: string option) =
    interface ISecretStore with
        member _.GetSecret(_, _) = async { return value }
        member _.SetSecret(_, _, _) = async { return Ok() }
        member _.DeleteSecret(_, _) = async { return Ok() }
        member _.ListKeys(_) = async { return [] }

let private renderBody (request: EdgePurgeRequest) =
    match request with
    | PurgeRequestPaths paths -> sprintf "{\"paths\":[%s]}" (paths |> List.map (sprintf "\"%s\"") |> String.concat ",")
    | PurgeRequestPrefix prefix -> sprintf "{\"prefix\":\"%s\"}" prefix
    | PurgeRequestTags tags -> sprintf "{\"tags\":[%s]}" (tags |> List.map (sprintf "\"%s\"") |> String.concat ",")

let private baseConfig () =
    HttpEdgeCacheConfig.create "test-edge" (Uri "https://cdn.test/v1/purge") renderBody

let private httpEdgeCacheTests =
    testList "HttpEdgeCache (ToolUp.Hosts.EdgeCache)" [
        testCaseAsync "a path purge POSTs the deployment's rendered body to the configured endpoint"
        <| async {
            let handler = new StubHandler(HttpStatusCode.OK)
            use client = new HttpClient(handler)
            let edge = HttpEdgeCache.create client None (baseConfig ())

            let! result = edge.PurgePaths [ "/a"; "/b" ]
            Expect.equal result (Ok()) "accepted"

            let request = Expect.wantSome handler.LastRequest "a request was sent"
            Expect.equal request.Method HttpMethod.Post "method"
            Expect.equal (request.RequestUri.ToString()) "https://cdn.test/v1/purge" "endpoint"
            Expect.equal handler.LastBody "{\"paths\":[\"/a\",\"/b\"]}" "the deployment's own body shape"
        }

        testCaseAsync "an empty path purge sends NOTHING and succeeds"
        <| async {
            let handler = new StubHandler(HttpStatusCode.OK)
            use client = new HttpClient(handler)
            let edge = HttpEdgeCache.create client None (baseConfig ())

            let! result = edge.PurgePaths []
            Expect.equal result (Ok()) "purging nothing succeeds"
            Expect.isNone handler.LastRequest "and costs no call"
        }

        testCaseAsync "an UNDECLARED verb answers PurgeNotSupported rather than sending a request"
        <| async {
            // The default config declares neither prefix nor tag
            // support, because most purge APIs offer neither and a
            // silent success would read as "the tagged objects are
            // gone".
            let handler = new StubHandler(HttpStatusCode.OK)
            use client = new HttpClient(handler)
            let edge = HttpEdgeCache.create client None (baseConfig ())

            let! prefix = edge.PurgePrefix "/a/"
            let! tags = edge.PurgeTags [ "t" ]

            Expect.equal prefix (Error(PurgeNotSupported "PurgePrefix")) "prefix"
            Expect.equal tags (Error(PurgeNotSupported "PurgeTags")) "tags"
            Expect.isNone handler.LastRequest "no request was sent for either"
        }

        testCaseAsync "declaring support makes the verb reach the endpoint"
        <| async {
            let handler = new StubHandler(HttpStatusCode.OK)
            use client = new HttpClient(handler)

            let edge =
                baseConfig ()
                |> HttpEdgeCacheConfig.withPrefixSupport
                |> HttpEdgeCacheConfig.withTagSupport
                |> HttpEdgeCache.create client None

            let! prefix = edge.PurgePrefix "/api/media/hls/abc/"
            Expect.equal prefix (Ok()) "prefix accepted"
            Expect.equal handler.LastBody "{\"prefix\":\"/api/media/hls/abc/\"}" "prefix body"

            let! tags = edge.PurgeTags [ "media-abc" ]
            Expect.equal tags (Ok()) "tags accepted"
            Expect.equal handler.LastBody "{\"tags\":[\"media-abc\"]}" "tag body"
        }

        testCaseAsync "the credential is read from ISecretStore on EVERY call and sent as a bearer token"
        <| async {
            let handler = new StubHandler(HttpStatusCode.OK)
            use client = new HttpClient(handler)

            let edge =
                baseConfig ()
                |> HttpEdgeCacheConfig.withCredential {
                    SecretContainer = "_platform"
                    SecretName = "cdn_purge_token"
                    Scheme = EdgeBearerToken
                }
                |> HttpEdgeCache.create client (Some(StubSecretStore(Some "s3cret") :> ISecretStore))

            let! result = edge.PurgePaths [ "/a" ]
            Expect.equal result (Ok()) "accepted"

            let request = Expect.wantSome handler.LastRequest "a request was sent"

            let auth =
                match request.Headers.TryGetValues "Authorization" with
                | true, values -> values |> Seq.head
                | _ -> ""

            Expect.equal auth "Bearer s3cret" "the rotated secret rides every call"
        }

        testCaseAsync "an API-key scheme uses the configured header name"
        <| async {
            let handler = new StubHandler(HttpStatusCode.OK)
            use client = new HttpClient(handler)

            let edge =
                baseConfig ()
                |> HttpEdgeCacheConfig.withCredential {
                    SecretContainer = "_platform"
                    SecretName = "cdn_purge_token"
                    Scheme = EdgeApiKeyHeader "X-Purge-Key"
                }
                |> HttpEdgeCache.create client (Some(StubSecretStore(Some "abc123") :> ISecretStore))

            let! _ = edge.PurgePaths [ "/a" ]
            let request = Expect.wantSome handler.LastRequest "a request was sent"

            let key =
                match request.Headers.TryGetValues "X-Purge-Key" with
                | true, values -> values |> Seq.head
                | _ -> ""

            Expect.equal key "abc123" "the API-key header"
        }

        testCaseAsync "an ABSENT credential is refused before the call, not sent unauthenticated"
        <| async {
            let handler = new StubHandler(HttpStatusCode.OK)
            use client = new HttpClient(handler)

            let edge =
                baseConfig ()
                |> HttpEdgeCacheConfig.withCredential {
                    SecretContainer = "_platform"
                    SecretName = "cdn_purge_token"
                    Scheme = EdgeBearerToken
                }
                |> HttpEdgeCache.create client (Some(StubSecretStore None :> ISecretStore))

            match! edge.PurgePaths [ "/a" ] with
            | Error(PurgeRejected detail) -> Expect.stringContains detail "cdn_purge_token" "names the missing secret"
            | other -> failtestf "expected a rejection, got %A" other

            Expect.isNone handler.LastRequest "and nothing left the process"
        }

        testCaseAsync "4xx is a REJECTION and 5xx is a TRANSPORT failure — the retry policy depends on the difference"
        <| async {
            let rejecting = new StubHandler(HttpStatusCode.Forbidden)
            use rejectingClient = new HttpClient(rejecting)
            let rejectingEdge = HttpEdgeCache.create rejectingClient None (baseConfig ())

            match! rejectingEdge.PurgePaths [ "/a" ] with
            | Error(PurgeRejected detail) -> Expect.stringContains detail "403" "the status survives into the error"
            | other -> failtestf "expected a rejection, got %A" other

            let failing = new StubHandler(HttpStatusCode.BadGateway)
            use failingClient = new HttpClient(failing)
            let failingEdge = HttpEdgeCache.create failingClient None (baseConfig ())

            match! failingEdge.PurgePaths [ "/a" ] with
            | Error(PurgeTransportFailure detail) -> Expect.stringContains detail "502" "the status survives"
            | other -> failtestf "expected a transport failure, got %A" other
        }

        testCase "it declares the propagation contract it was configured with"
        <| fun () ->
            use client = new HttpClient(new StubHandler(HttpStatusCode.OK))

            let edge =
                baseConfig ()
                |> HttpEdgeCacheConfig.withPropagation (PurgeEventualWithin(TimeSpan.FromMinutes 5.0))
                |> HttpEdgeCache.create client None

            Expect.equal edge.Propagation (PurgeEventualWithin(TimeSpan.FromMinutes 5.0)) "declared, not assumed"

            Expect.equal
                (HttpEdgeCache.create client None (baseConfig ())).Propagation
                PurgeEventualUnbounded
                "and the default promises nothing"
    ]

let private callbackSignerTests =
    let scope: StorageScope = {
        ScopeId = "u1"
        Container = "team-a"
        Persist = true
    }

    let frozen = DateTimeOffset(2026, 8, 28, 12, 0, 0, 500, TimeSpan.Zero)

    testList "CallbackUrlSigner (ToolUp.Hosts.EdgeCache)" [
        testCaseAsync "hands the callback the item, the scope, the resource path and the absolute unsigned URL"
        <| async {
            let mutable seen: DelegatedSignRequest option = None

            let signer =
                CallbackUrlSignerConfig.create "cb" "https://media.example.test/" (fun request -> async {
                    seen <- Some request
                    return Ok(request.UnsignedUrl + "?sig=abc")
                })
                |> CallbackUrlSigner.createWith (fun () -> frozen)

            let! result = signer.SignUrl(MediaId "vid1", scope, TimeSpan.FromHours 1.0)
            Expect.equal result (Ok "https://media.example.test/api/media/stream/vid1?sig=abc") "the signed URL"

            let request = Expect.wantSome seen "the callback ran"
            Expect.equal request.MediaId "vid1" "item"
            Expect.equal request.ScopeId "u1" "scope id — a signer can bind the tenant"
            Expect.equal request.Container "team-a" "container"
            Expect.equal request.ResourcePath "/api/media/stream/vid1" "resource path"

            Expect.equal
                request.UnsignedUrl
                "https://media.example.test/api/media/stream/vid1"
                "the base URL's trailing slash is not doubled"
        }

        testCaseAsync "the expiry is rounded DOWN to the declared precision, never up"
        <| async {
            // Rounding up would silently extend the grant past what the
            // caller asked for. `frozen` carries 500ms so the rounding
            // is observable at all.
            let mutable seen: DelegatedSignRequest option = None

            let capture (request: DelegatedSignRequest) = async {
                seen <- Some request
                return Ok "https://signed"
            }

            let second =
                CallbackUrlSignerConfig.create "cb" "https://media.example.test" capture
                |> CallbackUrlSigner.createWith (fun () -> frozen)

            let! _ = second.SignUrl(MediaId "vid1", scope, TimeSpan.FromSeconds 30.0)
            let atSecond = (Expect.wantSome seen "callback ran").ExpiresAt

            Expect.equal
                atSecond
                (DateTimeOffset(2026, 8, 28, 12, 0, 30, TimeSpan.Zero))
                "the 500ms is dropped, not carried up"

            let minute =
                CallbackUrlSignerConfig.create "cb" "https://media.example.test" capture
                |> CallbackUrlSignerConfig.withTtlPrecision SignedUrl.TtlMinute
                |> CallbackUrlSigner.createWith (fun () -> frozen)

            let! _ = minute.SignUrl(MediaId "vid1", scope, TimeSpan.FromSeconds 90.0)
            let atMinute = (Expect.wantSome seen "callback ran").ExpiresAt

            Expect.equal
                atMinute
                (DateTimeOffset(2026, 8, 28, 12, 1, 0, TimeSpan.Zero))
                "12:01:30.5 floors to 12:01, which is EARLIER than asked — never later"

            Expect.isTrue (atMinute <= frozen + TimeSpan.FromSeconds 90.0) "the grant never outlives the request"
        }

        testCaseAsync "a signer can sign a different resource — the HLS master manifest"
        <| async {
            let mutable seen: DelegatedSignRequest option = None

            let signer =
                CallbackUrlSignerConfig.create "cb" "https://media.example.test" (fun request -> async {
                    seen <- Some request
                    return Ok request.UnsignedUrl
                })
                |> CallbackUrlSignerConfig.withResourcePath (fun id ->
                    sprintf "/api/media/hls/%s/index.m3u8" (MediaId.value id))
                |> CallbackUrlSigner.create

            let! _ = signer.SignUrl(MediaId "vid1", scope, TimeSpan.FromHours 1.0)

            Expect.equal
                (Expect.wantSome seen "callback ran").ResourcePath
                "/api/media/hls/vid1/index.m3u8"
                "the deployment chooses what is signed"
        }

        testCaseAsync "a FAILING callback is an error — never a silent fall-through"
        <| async {
            let signer =
                CallbackUrlSignerConfig.create "cb" "https://media.example.test" (fun _ -> async {
                    return Error "the signing key is unavailable"
                })
                |> CallbackUrlSigner.create

            match! signer.SignUrl(MediaId "vid1", scope, TimeSpan.FromHours 1.0) with
            | Error(KeyResolutionFailed detail) ->
                Expect.stringContains detail "cb" "the failing signer names itself"
                Expect.stringContains detail "signing key is unavailable" "and carries the reason"
            | other -> failtestf "expected a failure, got %A" other
        }

        testCaseAsync "a THROWING callback becomes the same typed error"
        <| async {
            let signer =
                CallbackUrlSignerConfig.create "cb" "https://media.example.test" (fun _ -> async {
                    return failwith "boom"
                })
                |> CallbackUrlSigner.create

            match! signer.SignUrl(MediaId "vid1", scope, TimeSpan.FromHours 1.0) with
            | Error(KeyResolutionFailed detail) -> Expect.stringContains detail "boom" "the exception message survives"
            | other -> failtestf "expected a failure, got %A" other
        }

        testCase "it declares its TTL precision (GP 12 rule 6)"
        <| fun () ->
            let signer =
                CallbackUrlSignerConfig.create "cb" "https://x" (fun _ -> async { return Ok "u" })
                |> CallbackUrlSigner.create

            Expect.equal signer.TtlPrecision SignedUrl.TtlSecond "the default is second precision"
            Expect.equal signer.Name "cb" "and it names itself"
    ]

// ─── 6. Phase 740 — purge outcome telemetry ───────────────────────────
//
// 472 left a terminal purge failure as one `Warn` line, which is enough
// to diagnose a purge you already suspect and nothing at all to notice
// one you do not. These cases pin the three counters, the failure-class
// mapping the log line and the metric now share, and — the part worth
// the most care — that a deployment which composed no metrics endpoint
// pays nothing for any of it.

/// `IMetricsSink` recording every `Increment`, with a bounded wait so a
/// detached purge can be observed without a sleep long enough to be a
/// flake either way.
type private RecordingPurgeMetrics() =
    let increments = ConcurrentBag<string * Map<string, string>>()

    member _.Increments = increments |> List.ofSeq

    member this.CountOf(name: string) =
        this.Increments |> List.filter (fun (n, _) -> n = name) |> List.length

    member this.TagsOf(name: string) =
        this.Increments |> List.filter (fun (n, _) -> n = name) |> List.map snd

    /// Wait until `name` has been incremented at least `n` times.
    member this.WaitFor (name: string) (n: int) =
        let deadline = DateTime.UtcNow.AddSeconds 5.0
        let mutable satisfied = false

        while not satisfied && DateTime.UtcNow < deadline do
            if this.CountOf name >= n then
                satisfied <- true
            else
                Thread.Sleep 10

        satisfied

    /// Nothing arrived within `window`. The negative claim's honest
    /// shape — a bounded wait, not an instant read.
    member this.StaysEmptyFor(window: TimeSpan) =
        Thread.Sleep window
        this.Increments |> List.isEmpty

    interface IMetricsSink with
        member _.Record(_name, _value, _tags) = ()
        member _.Increment(name, tags) = increments.Add(name, tags)
        member _.SetGauge(_name, _value, _tags) = ()

/// A sink whose every emission throws. A metrics backend having a bad
/// day must not take the purge — or the thread-pool work item running
/// it — down with it.
type private ThrowingMetrics() =
    interface IMetricsSink with
        member _.Record(_name, _value, _tags) = failwith "the exporter is down"
        member _.Increment(_name, _tags) = failwith "the exporter is down"
        member _.SetGauge(_name, _value, _tags) = failwith "the exporter is down"

type private PurgeWarnLogger() =
    let warnings = ResizeArray<string>()

    member _.Warnings = lock warnings (fun () -> warnings |> List.ofSeq)

    member this.WaitForWarning() =
        let deadline = DateTime.UtcNow.AddSeconds 5.0
        let mutable satisfied = false

        while not satisfied && DateTime.UtcNow < deadline do
            if not (List.isEmpty this.Warnings) then
                satisfied <- true
            else
                Thread.Sleep 10

        satisfied

    interface ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()

        member _.Warn(message: string) =
            lock warnings (fun () -> warnings.Add message)

        member _.Error(_: string, _: exn option) = ()

/// An edge answering every verb with one fixed outcome.
let private edgeAnswering (name: string) (outcome: Result<unit, EdgePurgeError>) : IEdgeCache =
    { new IEdgeCache with
        member _.Name = name
        member _.Propagation = PurgeEventualUnbounded
        member _.PurgePaths(_) = async.Return outcome
        member _.PurgePrefix(_) = async.Return outcome
        member _.PurgeTags(_) = async.Return outcome
    }

/// The shape the in-tree HTTP adapter writes a rejection detail in.
let private rejectionAt (status: int) (reason: string) =
    PurgeRejected(sprintf "https://edge.example/purge returned %d %s" status reason)

let private purgeClassificationTests =
    testList "EdgePurgeMetrics.classify" [
        test "the typed cases that need no refinement" {
            Expect.equal
                (EdgePurgeMetrics.classify (PurgeTransportFailure "timeout"))
                EdgePurgeMetrics.ClassTransport
                "a transport failure"

            Expect.equal
                (EdgePurgeMetrics.classify (PurgeNotSupported "PurgePrefix"))
                EdgePurgeMetrics.ClassUnsupported
                "a capability this edge does not have is not a network failure and not a credential one"
        }

        test "auth and rate-limit are separated out of PurgeRejected by status" {
            // Both arrive as `PurgeRejected` because both are 4xx, so
            // the typed case alone cannot tell them apart — which is
            // the whole reason the detail is read at all. The remedies
            // are completely different: rotate a credential vs. purge
            // less.
            for status in [ 401; 403; 407 ] do
                Expect.equal
                    (EdgePurgeMetrics.classify (rejectionAt status "Forbidden"))
                    EdgePurgeMetrics.ClassAuth
                    (sprintf "%d is a credential problem" status)

            Expect.equal
                (EdgePurgeMetrics.classify (rejectionAt 429 "Too Many Requests"))
                EdgePurgeMetrics.ClassRateLimit
                "429 is a quota problem"

            Expect.equal
                (EdgePurgeMetrics.classify (rejectionAt 404 "Not Found"))
                EdgePurgeMetrics.ClassOther
                "a 4xx that is neither reads as other, not as a guess at one of them"
        }

        test "a detail the convention cannot be read out of degrades to `other`, never to a wrong class" {
            // The refinement is a stated convention, not a dependency —
            // `Platform.Server` references no edge sub-companion. A
            // third-party adapter that writes its rejection some other
            // way must still be classified, and the only honest answer
            // is the unrefined one.
            Expect.equal
                (EdgePurgeMetrics.classify (PurgeRejected "the distribution is not configured for purging"))
                EdgePurgeMetrics.ClassOther
                "no marker at all"

            Expect.equal
                (EdgePurgeMetrics.classify (PurgeRejected "quota exceeded: 429 requests this minute"))
                EdgePurgeMetrics.ClassOther
                "a number that is not a status, in a detail with no marker"

            Expect.equal
                (EdgePurgeMetrics.classify (PurgeRejected "endpoint returned 4291 objects"))
                EdgePurgeMetrics.ClassOther
                "four digits after the marker is not a three-digit status"

            Expect.equal
                (EdgePurgeMetrics.classify (PurgeRejected "endpoint returned nothing"))
                EdgePurgeMetrics.ClassOther
                "non-digits after the marker"
        }

        test "every class the classifier can produce is enumerated in `classes`" {
            // The enumeration is what the registered tag allowlist and
            // any dashboard are written against; a class the classifier
            // emits but nobody enumerated would be a series nobody
            // queries.
            let produced = [
                EdgePurgeMetrics.classify (PurgeTransportFailure "x")
                EdgePurgeMetrics.classify (PurgeNotSupported "PurgeTags")
                EdgePurgeMetrics.classify (rejectionAt 401 "Unauthorized")
                EdgePurgeMetrics.classify (rejectionAt 429 "Too Many Requests")
                EdgePurgeMetrics.classify (PurgeRejected "opaque")
            ]

            for cls in produced do
                Expect.isTrue (List.contains cls EdgePurgeMetrics.classes) (sprintf "%s is enumerated" cls)

            Expect.equal
                (produced |> List.distinct |> List.length)
                (List.length EdgePurgeMetrics.classes)
                "and the enumeration carries no class the classifier cannot produce"
        }
    ]

let private purgeMetricRegistrationTests =
    testList "Phase 740 — metric declarations" [
        test "all three counters are declared in the SDK standard registrations" {
            // An unregistered series is silently dropped by the sink, so
            // a counter that is emitted but not declared is a counter
            // that does not exist.
            let names =
                StandardMetrics.registrations
                |> List.map (fun r -> r.Definition.Name)
                |> Set.ofList

            for name in
                [
                    EdgePurgeMetrics.Attempted
                    EdgePurgeMetrics.Succeeded
                    EdgePurgeMetrics.Failed
                ] do
                Expect.isTrue (names.Contains name) (sprintf "%s is registered" name)
        }

        test "they are counters under the reserved SDK prefix" {
            let declared =
                EdgePurgeMetrics.registrations
                |> List.map (fun r -> r.Definition.Name, r.Definition.Kind, r.Module)

            Expect.equal (List.length declared) 3 "three series, no more"

            for name, kind, modul in declared do
                Expect.isTrue
                    (name.StartsWith MetricDefinition.ReservedPrefix)
                    (sprintf "%s carries the SDK-owned prefix" name)

                Expect.equal kind Counter (sprintf "%s is a counter" name)
                Expect.isNone modul (sprintf "%s is SDK-owned, not module-scoped" name)
        }

        test "the failure counter allows the class tag and the other two do not" {
            let tagsFor name =
                EdgePurgeMetrics.registrations
                |> List.find (fun r -> r.Definition.Name = name)
                |> fun r -> r.Definition.Tags

            Expect.equal (tagsFor EdgePurgeMetrics.Attempted) [ EdgePurgeMetrics.EdgeTagKey ] "attempted"
            Expect.equal (tagsFor EdgePurgeMetrics.Succeeded) [ EdgePurgeMetrics.EdgeTagKey ] "succeeded"

            Expect.equal
                (tagsFor EdgePurgeMetrics.Failed)
                [ EdgePurgeMetrics.EdgeTagKey; EdgePurgeMetrics.ClassTagKey ]
                "failed — there is no class for a success, and a constant tag would be an allowlist entry for nobody"
        }
    ]

let private purgeTelemetryGateTests =
    testList "Phase 740 — the zero-cost gate" [
        test "an unwrapped edge carries no telemetry, and a wrapped one does — the gate discriminates" {
            // Asserting only the OFF half would prove nothing: a gate
            // that is always off passes it. The control is the point.
            let plain = edgeAnswering "plain" (Ok())
            let sink = RecordingPurgeMetrics()
            let wrapped = EdgeCache.withMetrics (sink :> IMetricsSink) plain

            Expect.equal (EdgePurgeTelemetry.forEdge plain) EdgePurgeUnmetered "an ordinary edge is unmetered"

            match EdgePurgeTelemetry.forEdge wrapped with
            | EdgePurgeMetered s -> Expect.isTrue (obj.ReferenceEquals(s, sink)) "the wrap carries THAT sink"
            | EdgePurgeUnmetered -> failtest "the wrapped edge should be metered"
        }

        test "the OFF value is a shared singleton — the allocation-free claim, structurally" {
            // Same form as the no-op's claim above: `EdgePurgeUnmetered`
            // is nullary, so F# caches one instance and resolving the
            // gate on an unmetered deployment cannot allocate. A fact a
            // test can hold, where a timing would be a hope.
            let a = EdgePurgeTelemetry.forEdge (edgeAnswering "one" (Ok()))
            let b = EdgePurgeTelemetry.forEdge (edgeAnswering "two" (Ok()))

            Expect.isTrue (obj.ReferenceEquals(box a, box b)) "two resolutions share one value"
        }

        test "a NO-OP sink leaves the composition byte-for-byte unwrapped (GP 11 / GP 13)" {
            // `NoOpMetricsSink` is what the SDK registers when a
            // deployment composed no metrics endpoint. Reading it as a
            // live sink would put a wrapper object and two type tests on
            // the purge path of every deployment that measures nothing.
            let plain = edgeAnswering "plain" (Ok())
            let wrapped = EdgeCache.withMetrics (NoOpMetricsSink() :> IMetricsSink) plain

            Expect.isTrue (obj.ReferenceEquals(wrapped, plain)) "the same instance comes back"

            // Control — a live sink DOES produce a different object, so
            // the assertion above is about the no-op and not about
            // `withMetrics` never wrapping anything.
            let live = EdgeCache.withMetrics (RecordingPurgeMetrics() :> IMetricsSink) plain

            Expect.isFalse (obj.ReferenceEquals(live, plain)) "a live sink wraps"
        }

        test "the declared no-op edge is never wrapped, so `isNoop` still recognises it" {
            // A wrapper would defeat `EdgeCache.isNoop` and start
            // scheduling purges for a deployment that declared it wanted
            // none — turning 472's "declaring your absence is free" into
            // a cost.
            let sink = RecordingPurgeMetrics()

            let wrapped = EdgeCache.withMetrics (sink :> IMetricsSink) NoopEdgeCache.instance

            Expect.isTrue (obj.ReferenceEquals(wrapped, NoopEdgeCache.instance)) "the shared no-op comes back"
            Expect.isTrue (EdgeCache.isNoop wrapped) "and it is still recognised as the no-op"
        }

        test "wrapping twice hands back the first wrap rather than nesting" {
            let sink = RecordingPurgeMetrics() :> IMetricsSink
            let once = EdgeCache.withMetrics sink (edgeAnswering "plain" (Ok()))
            let twice = EdgeCache.withMetrics sink once

            Expect.isTrue (obj.ReferenceEquals(once, twice)) "no second wrapper, so `Inner` never lies"
        }

        test "the wrap forwards every verb and its identity unchanged" {
            let recording = IEdgeCacheContract.RecordingEdgeCache()

            let wrapped =
                EdgeCache.withMetrics (RecordingPurgeMetrics() :> IMetricsSink) (recording :> IEdgeCache)

            Expect.equal wrapped.Name (recording :> IEdgeCache).Name "Name"
            Expect.equal wrapped.Propagation (recording :> IEdgeCache).Propagation "Propagation"

            Expect.equal (wrapped.PurgePaths [ "/a" ] |> Async.RunSynchronously) (Ok()) "PurgePaths"
            Expect.equal (wrapped.PurgePrefix "/p/" |> Async.RunSynchronously) (Ok()) "PurgePrefix"
            Expect.equal (wrapped.PurgeTags [ "t" ] |> Async.RunSynchronously) (Ok()) "PurgeTags"
        }
    ]

let private purgeCounterTests =
    testList "Phase 740 — purge outcome counters" [
        test "a SUCCESSFUL purge counts attempted + succeeded, tagged by the edge's own name" {
            let sink = RecordingPurgeMetrics()

            let edge =
                EdgeCache.withMetrics (sink :> IMetricsSink) (edgeAnswering "cdn-a" (Ok()))

            EdgeCache.purgePathsDetached None (Some edge) [ "/a" ]

            Expect.isTrue (sink.WaitFor EdgePurgeMetrics.Succeeded 1) "the success was counted"
            Expect.equal (sink.CountOf EdgePurgeMetrics.Attempted) 1 "one attempt"
            Expect.equal (sink.CountOf EdgePurgeMetrics.Failed) 0 "and nothing failed"

            Expect.equal
                (sink.TagsOf EdgePurgeMetrics.Succeeded)
                [ Map.ofList [ EdgePurgeMetrics.EdgeTagKey, "cdn-a" ] ]
                "tagged with the edge the Warn line would have named"
        }

        test "a FAILING purge counts attempted + failed with the class, and never succeeded" {
            let sink = RecordingPurgeMetrics()

            let edge =
                EdgeCache.withMetrics
                    (sink :> IMetricsSink)
                    (edgeAnswering "cdn-b" (Error(rejectionAt 429 "Too Many Requests")))

            EdgeCache.purgePathsDetached None (Some edge) [ "/a" ]

            Expect.isTrue (sink.WaitFor EdgePurgeMetrics.Failed 1) "the failure was counted"
            Expect.equal (sink.CountOf EdgePurgeMetrics.Succeeded) 0 "nothing succeeded"

            Expect.equal
                (sink.TagsOf EdgePurgeMetrics.Failed)
                [
                    Map.ofList [
                        EdgePurgeMetrics.EdgeTagKey, "cdn-b"
                        EdgePurgeMetrics.ClassTagKey, EdgePurgeMetrics.ClassRateLimit
                    ]
                ]
                "the class an operator acts on, on the series they alert from"
        }

        test "`attempted` counts PURGES, not retry attempts" {
            // The retry policy runs two attempts against a failing edge.
            // If `attempted` moved per attempt it would stop being the
            // denominator of the other two, and a flapping edge would
            // read as a healthier one.
            let sink = RecordingPurgeMetrics()

            let edge =
                EdgeCache.withMetrics
                    (sink :> IMetricsSink)
                    (edgeAnswering "cdn-c" (Error(PurgeTransportFailure "unreachable")))

            EdgeCache.purgePathsDetached None (Some edge) [ "/a" ]

            Expect.isTrue (sink.WaitFor EdgePurgeMetrics.Failed 1) "the failure was counted"
            Expect.equal (sink.CountOf EdgePurgeMetrics.Attempted) 1 "one purge, whatever the retry policy did"
            Expect.equal (sink.CountOf EdgePurgeMetrics.Failed) 1 "and one terminal failure"

            Expect.equal
                (sink.TagsOf EdgePurgeMetrics.Failed
                 |> List.map (Map.find EdgePurgeMetrics.ClassTagKey))
                [ EdgePurgeMetrics.ClassTransport ]
                "classified transport"
        }

        test "an unsupported verb is a counted failure, in its own class" {
            let sink = RecordingPurgeMetrics()

            let edge =
                EdgeCache.withMetrics
                    (sink :> IMetricsSink)
                    (edgeAnswering "cdn-d" (Error(PurgeNotSupported "PurgePrefix")))

            EdgeCache.purgePrefixDetached None (Some edge) "/p/"

            Expect.isTrue (sink.WaitFor EdgePurgeMetrics.Failed 1) "counted"

            Expect.equal
                (sink.TagsOf EdgePurgeMetrics.Failed
                 |> List.map (Map.find EdgePurgeMetrics.ClassTagKey))
                [ EdgePurgeMetrics.ClassUnsupported ]
                "an absent capability is not a transport problem and not a credential one"
        }

        test "every tag key emitted is in the metric's registered allowlist" {
            // A tag whose key is not registered is silently DROPPED by
            // the sink, so an emitter and its declaration drifting apart
            // is invisible in production. Asserted against real
            // emissions rather than against a comment.
            let sink = RecordingPurgeMetrics()

            let ok = EdgeCache.withMetrics (sink :> IMetricsSink) (edgeAnswering "cdn-e" (Ok()))

            let bad =
                EdgeCache.withMetrics
                    (sink :> IMetricsSink)
                    (edgeAnswering "cdn-e" (Error(rejectionAt 403 "Forbidden")))

            EdgeCache.purgePathsDetached None (Some ok) [ "/a" ]
            EdgeCache.purgePathsDetached None (Some bad) [ "/b" ]

            Expect.isTrue (sink.WaitFor EdgePurgeMetrics.Succeeded 1) "the success landed"
            Expect.isTrue (sink.WaitFor EdgePurgeMetrics.Failed 1) "and the failure landed"

            let allowed =
                EdgePurgeMetrics.registrations
                |> List.map (fun r -> r.Definition.Name, Set.ofList r.Definition.Tags)
                |> Map.ofList

            for name, tags in sink.Increments do
                match Map.tryFind name allowed with
                | None -> failtestf "%s was emitted but is not declared here" name
                | Some allowedKeys ->
                    for key in tags |> Map.toList |> List.map fst do
                        Expect.isTrue
                            (allowedKeys.Contains key)
                            (sprintf "%s emits tag `%s`, which its declaration allows" name key)
        }

        test "an unmetered edge emits nothing at all" {
            // The other half of the gate: the purge still happens, it
            // just is not counted. Without the recording edge this
            // would pass for a purge that never ran.
            let sink = RecordingPurgeMetrics()
            let recording = IEdgeCacheContract.RecordingEdgeCache()

            EdgeCache.purgePathsDetached None (Some(recording :> IEdgeCache)) [ "/a" ]

            Expect.isTrue (recording.WaitFor 1) "the purge reached the edge"

            Expect.isTrue
                (sink.StaysEmptyFor(TimeSpan.FromMilliseconds 200.0))
                "and nothing was counted, because no sink was composed"
        }

        test "the declared no-op emits nothing even with a live sink in hand" {
            // 472's short-circuit runs BEFORE any telemetry resolution,
            // so a no-op deployment reaches none of this code.
            let sink = RecordingPurgeMetrics()

            let wrapped = EdgeCache.withMetrics (sink :> IMetricsSink) NoopEdgeCache.instance

            EdgeCache.purgePathsDetached None (Some wrapped) [ "/a" ]

            Expect.isTrue (sink.StaysEmptyFor(TimeSpan.FromMilliseconds 200.0)) "no counters for a declared no-op"
        }

        test "a sink that THROWS does not take the purge down with it" {
            let recording = IEdgeCacheContract.RecordingEdgeCache()

            let edge =
                EdgeCache.withMetrics (ThrowingMetrics() :> IMetricsSink) (recording :> IEdgeCache)

            EdgeCache.purgePathsDetached None (Some edge) [ "/a" ]

            Expect.isTrue (recording.WaitFor 1) "the purge still reached the edge"
            Thread.Sleep 150
        }

        test "the Warn line gains the class, so the log and the metric agree" {
            // 740.B — one derivation feeds both, so a dashboard and the
            // line an operator greps for cannot disagree about the same
            // failure.
            let sink = RecordingPurgeMetrics()
            let logger = PurgeWarnLogger()

            let edge =
                EdgeCache.withMetrics
                    (sink :> IMetricsSink)
                    (edgeAnswering "cdn-f" (Error(rejectionAt 401 "Unauthorized")))

            EdgeCache.purgePathsDetached (Some(logger :> ILogger)) (Some edge) [ "/a" ]

            Expect.isTrue (logger.WaitForWarning()) "the failure was logged"
            Expect.isTrue (sink.WaitFor EdgePurgeMetrics.Failed 1) "and counted"

            let line = logger.Warnings |> List.head

            Expect.stringContains line (sprintf "class=%s" EdgePurgeMetrics.ClassAuth) "the line names the class"
            Expect.stringContains line "cdn-f" "and the edge"

            Expect.equal
                (sink.TagsOf EdgePurgeMetrics.Failed
                 |> List.map (Map.find EdgePurgeMetrics.ClassTagKey))
                [ EdgePurgeMetrics.ClassAuth ]
                "the metric carries the same class the line printed"
        }

        test "the purge is still detached — counting did not put the edge on the calling thread (GP 7)" {
            use gate = new ManualResetEventSlim(false)

            let slow =
                { new IEdgeCache with
                    member _.Name = "slow-metered"
                    member _.Propagation = PurgeEventualUnbounded

                    member _.PurgePaths(_) = async {
                        gate.Wait(TimeSpan.FromSeconds 5.0) |> ignore
                        return Ok()
                    }

                    member _.PurgePrefix(_) = async { return Ok() }
                    member _.PurgeTags(_) = async { return Ok() }
                }

            let edge = EdgeCache.withMetrics (RecordingPurgeMetrics() :> IMetricsSink) slow

            let sw = Diagnostics.Stopwatch.StartNew()
            EdgeCache.purgePathsDetached None (Some edge) [ "/a" ]
            sw.Stop()

            Expect.isLessThan sw.ElapsedMilliseconds 1000L "the publish path did not wait on the edge or on the sink"

            gate.Set()
        }
    ]

// ─── Bindings ─────────────────────────────────────────────────────────

let private stubbedHttpEdge () : IEdgeCache =
    // A 200-answering stub, so the contract pack measures the ADAPTER's
    // behaviour rather than a network. Prefix and tag support declared,
    // so the pack sees the supported shape of each verb; the unsupported
    // shape is asserted in the adapter's own list above.
    let client = new HttpClient(new StubHandler(HttpStatusCode.OK))

    baseConfig ()
    |> HttpEdgeCacheConfig.withPrefixSupport
    |> HttpEdgeCacheConfig.withTagSupport
    |> HttpEdgeCache.create client None

[<Tests>]
let tests =
    testList "EdgeCache (Phase 472)" [
        noopTests
        cacheHeaderTests
        retryTests
        detachedTests
        slugPathTests
        renderFanOutTests
        mediaDeclarationTests
        mediaRefusalTests
        mediaEdgePathTests
        httpEdgeCacheTests
        callbackSignerTests
        // Phase 740 — purge outcome telemetry at the 472 choke point.
        purgeClassificationTests
        purgeMetricRegistrationTests
        purgeTelemetryGateTests
        purgeCounterTests
        // The conformance bar, over all three implementations — the
        // in-tree no-op, the recording fake, and the sub-companion that
        // proves the seam from outside the SDK (GP 12).
        IEdgeCacheContract.tests "NoopEdgeCache" NoopEdgeCache.create
        IEdgeCacheContract.tests "RecordingEdgeCache" (fun () -> IEdgeCacheContract.RecordingEdgeCache() :> IEdgeCache)
        IEdgeCacheContract.tests "HttpEdgeCache" stubbedHttpEdge
    ]