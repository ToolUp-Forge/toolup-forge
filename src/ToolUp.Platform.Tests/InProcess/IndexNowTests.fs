// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.IndexNowTests

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Giraffe.ViewEngine
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.EntityStore
open ToolUp.Platform.Narrative
open ToolUp.Platform.Server
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.PublicRendering
open ToolUp.PublicRendering.PublicRenderingCompose

// ─── Phase 109 — IndexNow push-indexing tests ───────────────────────
//
// Coverage:
//   * key derivation stability + resolution precedence
//   * host derivation from the public base URL
//   * deploy signature rolls on content change, stable + order-independent
//   * SubmissionState JSON round-trip + corrupt/legacy tolerance
//   * the resumable batched-submission state machine (the postmortem
//     scenario): fresh, partial-failure persistence, restart resumes only
//     failures, new-signature wipe, fully-done sentinel skip, POST
//     exception = failure
//   * file-backed state store survives + tolerates a corrupt file
//   * `/{key}.txt` ownership endpoint: match serves the key, non-key
//     `*.txt` falls through
//   * publish-hook single-URL ping via IndexNowService + the publisher
//   * not composed / disabled = no extra route (GP 11 / 13)

let private nullLogger = ConsoleLogger.ConsoleLogger() :> ILogger
let private noMetrics = NoOpMetricsSink() :> IMetricsSink

// An in-memory IIndexNowStateStore so the submission machine can be
// exercised without touching disk.
type private MemStateStore() =
    let mutable state: IndexNowSubmissionState option = None
    member _.Seed(s: IndexNowSubmissionState) = state <- Some s
    member _.Current: IndexNowSubmissionState option = state

    interface IIndexNowStateStore with
        member _.Read() = async { return state }
        member _.Write(s) = async { state <- Some s }

// A stubbed POST function returning the given statuses in order (200 for
// any overflow call), recording every body it was handed.
let private makePostFn (statuses: int list) : (string -> Async<int>) * (unit -> string list) =
    let bodies = ResizeArray<string>()
    let remaining = ResizeArray<int>(statuses)

    let postFn (body: string) : Async<int> = async {
        bodies.Add body

        if remaining.Count = 0 then
            return 200
        else
            let head = remaining.[0]
            remaining.RemoveAt 0
            return head
    }

    postFn, (fun () -> List.ofSeq bodies)

let private fakeUrls (count: int) : string list = [ for i in 1..count -> sprintf "https://example.test/%d" i ]

let private composeBody = IndexNow.buildBody "example.test" "thekey"
let private testBatchSize = 2

let private run (store: IIndexNowStateStore) (sg: string) (post: string -> Async<int>) (urls: string list) =
    IndexNow.submitBatched store sg testBatchSize composeBody post noMetrics nullLogger urls
    |> Async.RunSynchronously

// ─── Key derivation + resolution ────────────────────────────────────

let private keyTests =
    testList "key derivation + resolution" [

        test "deriveKey is deterministic, 32 lowercase hex chars (within the spec 8–128 / [a-zA-Z0-9-])" {
            let k1 = IndexNow.deriveKey "seed"
            let k2 = IndexNow.deriveKey "seed"
            Expect.equal k1 k2 "same seed → same key"
            Expect.equal k1.Length 32 "32 chars"
            Expect.isTrue (Regex.IsMatch(k1, "^[0-9a-f]{32}$")) "lowercase hex only"
            Expect.notEqual (IndexNow.deriveKey "other") k1 "different seed → different key"
        }

        test "resolveKey: explicit Key wins over seed + fallback" {
            let opts = {
                IndexNowOptions.enabled with
                    Key = Some "EXPLICIT-key"
                    KeySeed = Some "ignored"
            }

            Expect.equal (IndexNow.resolveKey opts "example.com") "EXPLICIT-key" "explicit key used verbatim"
        }

        test "resolveKey: KeySeed used when no explicit Key" {
            let opts = {
                IndexNowOptions.enabled with
                    KeySeed = Some "my-seed"
            }

            Expect.equal (IndexNow.resolveKey opts "example.com") (IndexNow.deriveKey "my-seed") "derived from seed"
        }

        test "resolveKey: stable host-derived fallback (NOT the churny deploy signature)" {
            let opts = IndexNowOptions.enabled
            let k = IndexNow.resolveKey opts "example.com"
            Expect.equal k (IndexNow.resolveKey opts "example.com") "same host → same key (stable, no rotation)"
            Expect.notEqual k (IndexNow.resolveKey opts "other.com") "different host → different key"
        }
    ]

// ─── Host derivation ────────────────────────────────────────────────

let private hostTests =
    testList "host derivation" [

        test "hostFromBaseUrl strips scheme + path" {
            Expect.equal (IndexNow.hostFromBaseUrl "https://example.com/") "example.com" "trailing slash"
            Expect.equal (IndexNow.hostFromBaseUrl "https://example.com/blog/x") "example.com" "with path"
            Expect.equal (IndexNow.hostFromBaseUrl "example.com") "example.com" "already bare host"
        }

        test "resolveHost: explicit Host wins; otherwise derive from base URL" {
            Expect.equal
                (IndexNow.resolveHost
                    {
                        IndexNowOptions.enabled with
                            Host = Some "h.com"
                    }
                    "https://ignored.com/")
                "h.com"
                "explicit host"

            Expect.equal
                (IndexNow.resolveHost IndexNowOptions.enabled "https://derived.com/")
                "derived.com"
                "derived from base url"
        }
    ]

// ─── Deploy signature ───────────────────────────────────────────────

let private signatureTests =
    testList "deploy signature" [

        test "stable + order-independent for the same universe" {
            let a = [ Slug "a", Some "2026-01-01"; Slug "b", None ]
            let b = [ Slug "b", None; Slug "a", Some "2026-01-01" ]

            Expect.equal
                (IndexNow.computeSignature a)
                (IndexNow.computeSignature b)
                "order does not change the signature"
        }

        test "rolls when a page is added" {
            let a = [ Slug "a", None ]
            let b = [ Slug "a", None; Slug "c", None ]
            Expect.notEqual (IndexNow.computeSignature a) (IndexNow.computeSignature b) "added slug rolls the signature"
        }

        test "rolls when a lastmod changes" {
            let a = [ Slug "a", Some "2026-01-01" ]
            let b = [ Slug "a", Some "2026-02-02" ]
            Expect.notEqual (IndexNow.computeSignature a) (IndexNow.computeSignature b) "lastmod change rolls it"
        }

        test "urlsFor builds absolute URLs from the base + slugs" {
            let urls =
                IndexNow.urlsFor "https://example.com/" [ Slug "about", None; Slug "tag/news", None ]

            Expect.equal urls [ "https://example.com/about"; "https://example.com/tag/news" ] "absolute URLs"
        }
    ]

// ─── SubmissionState round-trip + tolerance ─────────────────────────

let private stateTests =
    testList "SubmissionState round-trip" [

        test "populated set round-trips" {
            let original = {
                Signature = "sig-A"
                SuccessfulBatches = Set.ofList [ 0; 2; 5; 11 ]
            }

            let parsed =
                IndexNowSubmissionState.serialize original |> IndexNowSubmissionState.tryParse

            Expect.equal parsed (Some original) "sig + indices preserved"
        }

        test "empty set (fully-done sentinel) round-trips" {
            let original = {
                Signature = "sig-A"
                SuccessfulBatches = Set.empty
            }

            let parsed =
                IndexNowSubmissionState.serialize original |> IndexNowSubmissionState.tryParse

            Expect.equal parsed (Some original) "empty set round-trips"
        }

        test "malformed / legacy content parses to None (treated as fresh)" {
            Expect.equal (IndexNowSubmissionState.tryParse "not json") None "garbage"
            Expect.equal (IndexNowSubmissionState.tryParse "") None "empty"
            Expect.equal (IndexNowSubmissionState.tryParse "sig=foo;deploy=x") None "legacy plain marker"
        }
    ]

// ─── The resumable submission state machine ─────────────────────────

let private submissionTests =
    testList "submitBatched — resumable state machine" [

        test "fresh start — submits every batch and writes the cleared sentinel" {
            let store = MemStateStore()
            let post, bodies = makePostFn [ 200; 200; 200 ]
            run store "sig-fresh" post (fakeUrls 6) // 6 / 2 = 3 batches

            Expect.equal (List.length (bodies ())) 3 "every batch POSTed"

            Expect.equal
                store.Current
                (Some {
                    Signature = "sig-fresh"
                    SuccessfulBatches = Set.empty
                })
                "cleared (fully-done) sentinel written"
        }

        test "partial failure persists only the successful batch indices" {
            let store = MemStateStore()
            let post, bodies = makePostFn [ 200; 200; 403 ]
            run store "sig-partial" post (fakeUrls 6)

            Expect.equal (List.length (bodies ())) 3 "all 3 attempted"

            Expect.equal
                store.Current
                (Some {
                    Signature = "sig-partial"
                    SuccessfulBatches = Set.ofList [ 0; 1 ]
                })
                "0,1 persisted; failed index 2 absent"
        }

        test "restart after partial failure retries ONLY the missing batch" {
            let store = MemStateStore()

            store.Seed {
                Signature = "sig-restart"
                SuccessfulBatches = Set.ofList [ 0; 1 ]
            }

            let post, bodies = makePostFn [ 200 ]
            run store "sig-restart" post (fakeUrls 6)

            let recorded = bodies ()
            Expect.equal (List.length recorded) 1 "only the previously-failed batch retried"
            let body = List.head recorded
            Expect.stringContains body "example.test/5" "retried batch is the right slice"
            Expect.stringContains body "example.test/6" "retried batch is the right slice"
            Expect.isFalse (body.Contains "example.test/1") "does not re-send an already-successful batch"

            Expect.equal
                store.Current
                (Some {
                    Signature = "sig-restart"
                    SuccessfulBatches = Set.empty
                })
                "all done → cleared sentinel"
        }

        test "a new deploy signature discards the prior set and re-submits everything" {
            let store = MemStateStore()

            store.Seed {
                Signature = "sig-OLD"
                SuccessfulBatches = Set.ofList [ 0; 1 ]
            }

            let post, bodies = makePostFn [ 200; 200; 200 ]
            run store "sig-NEW" post (fakeUrls 6)

            Expect.equal (List.length (bodies ())) 3 "new sig discards old skip set, submits everything"

            Expect.equal
                store.Current
                (Some {
                    Signature = "sig-NEW"
                    SuccessfulBatches = Set.empty
                })
                "new sig recorded with cleared sentinel"
        }

        test "matching signature + cleared sentinel → skip the whole run (the postmortem fix)" {
            let store = MemStateStore()

            store.Seed {
                Signature = "sig-done"
                SuccessfulBatches = Set.empty
            }

            let post, bodies = makePostFn []
            run store "sig-done" post (fakeUrls 6)
            Expect.equal (List.length (bodies ())) 0 "no POSTs — fully done for this signature"
        }

        test "POST exception is treated as failure; index not marked successful" {
            let store = MemStateStore()
            let mutable callIx = 0

            let post (_body: string) : Async<int> = async {
                callIx <- callIx + 1

                if callIx = 1 then
                    return 200
                else
                    return raise (System.Net.Http.HttpRequestException "boom")
            }

            run store "sig-throw" post (fakeUrls 4) // 2 batches

            Expect.equal
                store.Current
                (Some {
                    Signature = "sig-throw"
                    SuccessfulBatches = Set.singleton 0
                })
                "only the 2xx batch marked; the thrown one is left for retry"
        }

        test "empty URL list is a no-op (no POST, no state written)" {
            let store = MemStateStore()
            let post, bodies = makePostFn []
            run store "sig-empty" post []
            Expect.equal (List.length (bodies ())) 0 "no POST"
            Expect.isNone store.Current "no state written"
        }
    ]

// ─── File-backed state store ────────────────────────────────────────

let private withTempPath (action: string -> unit) : unit =
    let path =
        Path.Combine(Path.GetTempPath(), "toolup-indexnow-test-" + Guid.NewGuid().ToString("N") + ".json")

    try
        action path
    finally
        try
            if File.Exists path then
                File.Delete path
        with _ ->
            ()

let private fileStoreTests =
    testList "FileIndexNowStateStore" [

        test "write then read round-trips through disk" {
            withTempPath (fun path ->
                let store = FileIndexNowStateStore.createAt path

                let state = {
                    Signature = "sig-file"
                    SuccessfulBatches = Set.ofList [ 1; 3 ]
                }

                (store :> IIndexNowStateStore).Write state |> Async.RunSynchronously
                let back = (store :> IIndexNowStateStore).Read() |> Async.RunSynchronously
                Expect.equal back (Some state) "survives a round-trip through the file")
        }

        test "a corrupt file reads as None (treated as no prior state)" {
            withTempPath (fun path ->
                File.WriteAllText(path, "{ this is not valid json")
                let store = FileIndexNowStateStore.createAt path
                let back = (store :> IIndexNowStateStore).Read() |> Async.RunSynchronously
                Expect.isNone back "corrupt file → None")
        }

        test "an absent file reads as None" {
            withTempPath (fun path ->
                let store = FileIndexNowStateStore.createAt path
                Expect.isNone ((store :> IIndexNowStateStore).Read() |> Async.RunSynchronously) "absent → None")
        }
    ]

// ─── Ownership-key endpoint ─────────────────────────────────────────

type private KeyResult = {
    Status: int
    Body: string
    ContinuationInvoked: bool
    Declined: bool
}

let private runKeyHandler (key: string) (path: string) : KeyResult =
    let ctx = DefaultHttpContext()
    ctx.Request.Method <- "GET"
    ctx.Request.Path <- PathString(path)
    let respBody = new MemoryStream()
    ctx.Response.Body <- respBody

    // The continuation records whether it was invoked. A decline must
    // return `None` WITHOUT invoking it — in the composed chain `next`
    // is the choose's downstream finisher, so `next ctx` would end the
    // whole pipeline as an unwritten 200 instead of advancing to the
    // page handler.
    let finalFunc: HttpFunc =
        fun c ->
            c.Items["continuationInvoked"] <- box true
            Task.FromResult(Some c)

    let h = IndexNowKeyHandler.routes key
    let result = (h finalFunc ctx).GetAwaiter().GetResult()

    respBody.Position <- 0L
    let text = (new StreamReader(respBody)).ReadToEnd()

    {
        Status = ctx.Response.StatusCode
        Body = text
        ContinuationInvoked = ctx.Items.ContainsKey "continuationInvoked"
        Declined = result.IsNone
    }

let private keyEndpointTests =
    testList "ownership-key endpoint" [

        test "GET /{key}.txt serves the key body and does not decline" {
            let r = runKeyHandler "abc123" "/abc123.txt"
            Expect.equal r.Body "abc123" "body equals the key"
            Expect.isFalse r.Declined "the key request is claimed, not declined"
        }

        test "GET /{other}.txt declines (returns None) so the surrounding choose advances" {
            let r = runKeyHandler "abc123" "/something-else.txt"
            Expect.isTrue r.Declined "a non-key .txt request declines with None"

            Expect.isFalse
                r.ContinuationInvoked
                "the continuation must not be invoked — `next ctx` would end the chain as an unwritten 200"

            Expect.equal r.Body "" "nothing written for a non-key request"
        }
    ]

// ─── Publish-hook single-URL ping ───────────────────────────────────

// A recording stub IIndexNowService capturing pinged slugs.
type private RecordingService() =
    let pinged = ResizeArray<string>()
    member _.Pinged = List.ofSeq pinged

    interface IIndexNowService with
        member _.SubmitAll() = async { return () }
        member _.PingSlug(slug) = async { pinged.Add slug }

let private mkPublisher (svc: IIndexNowService option) : INarrativePagePublisher * IEntityStore =
    let blob = InMemoryBlobStorage() :> IBlobStorage
    let dos = DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityRegistry()
    registry.Register<PublicPageEntity>(PublicPageEntity.registration)
    let store = BlobEntityStore(dos, blob, registry, None) :> IEntityStore

    let publisher =
        PublicRenderingNarrativePagePublisher.create
            store
            [ LayoutName "page" ]
            None
            NarrativePublishGuardrails.defaults
            svc

    publisher, store

let private pingTests =
    testList "publish-hook single-URL ping" [

        test "a real IndexNowService.PingSlug POSTs exactly the published URL" {
            let post, bodies = makePostFn [ 200 ]

            let svc =
                IndexNowService(
                    IndexNowOptions.enabled,
                    "example.com",
                    "thekey",
                    "https://example.com",
                    MemStateStore(),
                    post,
                    noMetrics,
                    nullLogger,
                    (fun () -> async { return [] })
                )
                :> IIndexNowService

            svc.PingSlug "blog/launch" |> Async.RunSynchronously
            let recorded = bodies ()
            Expect.equal (List.length recorded) 1 "one URL pushed"
            Expect.stringContains (List.head recorded) "https://example.com/blog/launch" "the published URL"
        }

        test "PingSlug is a no-op when PingOnPublish = false" {
            let post, bodies = makePostFn [ 200 ]

            let svc =
                IndexNowService(
                    {
                        IndexNowOptions.enabled with
                            PingOnPublish = false
                    },
                    "example.com",
                    "thekey",
                    "https://example.com",
                    MemStateStore(),
                    post,
                    noMetrics,
                    nullLogger,
                    (fun () -> async { return [] })
                )
                :> IIndexNowService

            svc.PingSlug "x" |> Async.RunSynchronously
            Expect.equal (List.length (bodies ())) 0 "no push when PingOnPublish off"
        }

        test "the publisher pings the just-published slug on a successful publish" {
            let svc = RecordingService()
            let publisher, _ = mkPublisher (Some(svc :> IIndexNowService))

            let doc = Narrative.create "Hello"

            let result =
                publisher.PublishAsync("hello", None, None, Some "page", OverwriteExisting, doc)
                |> Async.RunSynchronously

            match result with
            | PublishSucceeded slug -> Expect.equal slug "hello" "published"
            | PublishFailed e -> failtestf "expected publish to succeed; got %s" e

            Expect.equal svc.Pinged [ "hello" ] "the published slug was pushed to IndexNow"
        }

        test "the publisher works (no ping) when no IndexNow is composed" {
            let publisher, _ = mkPublisher None
            let doc = Narrative.create "Hi"

            let result =
                publisher.PublishAsync("hi", None, None, Some "page", OverwriteExisting, doc)
                |> Async.RunSynchronously

            match result with
            | PublishSucceeded _ -> ()
            | PublishFailed e -> failtestf "publish must still succeed with no IndexNow; got %s" e
        }
    ]

// ─── Not composed = no extra route (GP 11 / 13) ─────────────────────

let private mkContentRoot () : ContentRoot =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-indexnow-compose-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    ContentRoot dir

let private dummyLayout (_p: PublicPage) : XmlNode = html [] [ body [] [] ]

let private composeGateTests =
    testList "compose gate (GP 11 / 13)" [

        test "withIndexNow flips the option; default is disabled" {
            let app =
                PublicRenderingServerApp.create ()
                |> PublicRenderingServerApp.withIndexNow IndexNowOptions.enabled

            Expect.isTrue app.IndexNow.Enabled "enabled after withIndexNow"
            Expect.isFalse (PublicRenderingServerApp.create ()).IndexNow.Enabled "off by default (GP 11)"
        }

        test "enabled IndexNow adds exactly one handler (the /{key}.txt route) vs not composed" {
            let root = mkContentRoot ()

            let baseConfig = {
                ServerConfig.defaults with
                    PublicRendering = EnabledPublicRendering root
                    PublicBaseUrl = Some "https://example.com"
            }

            let withoutIndexNow =
                ServerApp.empty
                |> ServerApp.withConfig baseConfig
                |> PublicRenderingCompose.withPublicRendering (
                    PublicRenderingServerApp.withLayout (LayoutName "page") dummyLayout
                )

            let withIndexNow =
                ServerApp.empty
                |> ServerApp.withConfig baseConfig
                |> PublicRenderingCompose.withPublicRendering (fun pr ->
                    pr
                    |> PublicRenderingServerApp.withLayout (LayoutName "page") dummyLayout
                    |> PublicRenderingServerApp.withIndexNow IndexNowOptions.enabled)

            Expect.equal
                (List.length withIndexNow.Extensions.Handlers)
                (List.length withoutIndexNow.Extensions.Handlers + 1)
                "IndexNow adds exactly one route handler; a pipeline without it has none (GP 11/13)"
        }

        test "enabled-but-no-host is inactive — adds no route" {
            let root = mkContentRoot ()

            // No PublicBaseUrl and no explicit Host → no resolvable host.
            let baseConfig = {
                ServerConfig.defaults with
                    PublicRendering = EnabledPublicRendering root
                    PublicBaseUrl = None
            }

            let withoutIndexNow =
                ServerApp.empty
                |> ServerApp.withConfig baseConfig
                |> PublicRenderingCompose.withPublicRendering (
                    PublicRenderingServerApp.withLayout (LayoutName "page") dummyLayout
                )

            let withHostlessIndexNow =
                ServerApp.empty
                |> ServerApp.withConfig baseConfig
                |> PublicRenderingCompose.withPublicRendering (fun pr ->
                    pr
                    |> PublicRenderingServerApp.withLayout (LayoutName "page") dummyLayout
                    |> PublicRenderingServerApp.withIndexNow IndexNowOptions.enabled)

            Expect.equal
                (List.length withHostlessIndexNow.Extensions.Handlers)
                (List.length withoutIndexNow.Extensions.Handlers)
                "no host → inactive → no extra route"
        }
    ]

let tests =
    testList "Phase 109 IndexNow" [
        keyTests
        hostTests
        signatureTests
        stateTests
        submissionTests
        fileStoreTests
        keyEndpointTests
        pingTests
        composeGateTests
    ]