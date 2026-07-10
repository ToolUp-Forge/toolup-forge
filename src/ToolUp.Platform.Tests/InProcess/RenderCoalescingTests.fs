module ToolUp.Platform.Tests.InProcess.RenderCoalescingTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Giraffe.ViewEngine
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.PublicRendering

// ─── Phase 199 — render-cache request coalescing (stampede protection) ─
//
// Two layers:
//   1. `IRenderCoalescer` contract, bound to the shipped default
//      `InProcessRenderCoalescer`: M concurrent callers for one key run
//      the producer exactly once and all observe the same result; distinct
//      keys never block each other; a key is released after its round so a
//      later call re-runs; a producer exception reaches every awaiter.
//   2. `PublicPageHandler` integration: with a cacheable default policy, M
//      concurrent cold-key misses collapse to ONE `IContentSource`
//      resolution and every request gets the identical rendered body; the
//      SWR hit path is unaffected; the cache-off path is unchanged.

let private key slug : RenderKey = {
    Slug = slug
    ScopeId = "public"
    ContentVersion = ""
}

// ─── 1. IRenderCoalescer contract (default in-process impl) ─────────

let private coalescerTests =
    testList "IRenderCoalescer — InProcessRenderCoalescer" [

        testCase "M concurrent callers for one key → producer runs exactly once, all get the same result"
        <| fun _ ->
            let coalescer = InProcessRenderCoalescer.create ()
            let runs = ref 0
            let gate = TaskCompletionSource<unit>()

            let produce () : Async<string> = async {
                Interlocked.Increment(&runs.contents) |> ignore
                // Block so every concurrent caller piles onto the single
                // in-flight entry before it completes.
                do! Async.AwaitTask gate.Task
                return "RESULT"
            }

            // Call Coalesce synchronously M times first (each GetOrAdd shares
            // the one published in-flight entry), then start + release.
            let tasks = [ for _ in 1..8 -> coalescer.Coalesce (key "k") produce |> Async.StartAsTask ]

            gate.SetResult()
            let results = Task.WhenAll(tasks).GetAwaiter().GetResult()

            Expect.equal runs.Value 1 "producer ran exactly once across 8 concurrent callers"
            Expect.all results (fun r -> r = "RESULT") "every caller observed the single shared result"

        testCase "distinct keys never block each other (run in parallel)"
        <| fun _ ->
            let coalescer = InProcessRenderCoalescer.create ()
            let startedA = TaskCompletionSource<unit>()
            let startedB = TaskCompletionSource<unit>()
            let releaseA = TaskCompletionSource<unit>()
            let releaseB = TaskCompletionSource<unit>()

            let produce (started: TaskCompletionSource<unit>) (release: TaskCompletionSource<unit>) () : Async<string> = async {
                started.SetResult()
                do! Async.AwaitTask release.Task
                return "ok"
            }

            let tA =
                coalescer.Coalesce (key "a") (produce startedA releaseA) |> Async.StartAsTask

            let tB =
                coalescer.Coalesce (key "b") (produce startedB releaseB) |> Async.StartAsTask

            // Both producers must start without either being released — a
            // key-a render in flight must not block a key-b render.
            let bothStarted =
                Task.WhenAll(startedA.Task, startedB.Task).Wait(TimeSpan.FromSeconds 5.0)

            Expect.isTrue bothStarted "both distinct-key producers started concurrently (no cross-key blocking)"

            releaseA.SetResult()
            releaseB.SetResult()
            Task.WhenAll(tA, tB).GetAwaiter().GetResult() |> ignore

        testCase "a key is released after its round — a later call re-runs the producer"
        <| fun _ ->
            let coalescer = InProcessRenderCoalescer.create ()
            let runs = ref 0

            let produce () : Async<int> = async {
                let n = Interlocked.Increment(&runs.contents)
                return n
            }

            let first = coalescer.Coalesce (key "k") produce |> Async.RunSynchronously
            let second = coalescer.Coalesce (key "k") produce |> Async.RunSynchronously

            Expect.equal first 1 "first round runs the producer"
            Expect.equal second 2 "the key was released, so the second call runs a fresh round"

        testCase "a producer exception reaches every awaiter and frees the key"
        <| fun _ ->
            let coalescer = InProcessRenderCoalescer.create ()
            let gate = TaskCompletionSource<unit>()
            let runs = ref 0

            let boom () : Async<string> = async {
                Interlocked.Increment(&runs.contents) |> ignore
                do! Async.AwaitTask gate.Task
                return failwith "boom"
            }

            let tasks = [ for _ in 1..4 -> coalescer.Coalesce (key "k") boom |> Async.StartAsTask ]

            gate.SetResult()

            for t in tasks do
                Expect.throws
                    (fun () -> t.GetAwaiter().GetResult() |> ignore)
                    "each awaiter observes the producer failure"

            Expect.equal runs.Value 1 "the failing producer still ran only once for the round"

            // The key is free again: a fresh, succeeding call runs a new round.
            let recovered =
                coalescer.Coalesce (key "k") (fun () -> async { return "recovered" })
                |> Async.RunSynchronously

            Expect.equal recovered "recovered" "the key was released after the failed round"
    ]

// ─── 2. Handler integration — concurrent cold-key misses ────────────

let private layouts: Map<LayoutName, PublicPage -> XmlNode> =
    Map[(LayoutName "page", (fun (p: PublicPage) -> html [] [ body [] [ str p.Title ] ]))]

let private mkPage (slug: string) : PublicPage = {
    Slug = Slug slug
    Title = $"Title-{slug}"
    Description = ""
    Body = Html $"body-{slug}"
    Layout = LayoutName "page"
    Frontmatter = Map.empty
    PublishedAt = None
    Collection = None
    Status = Published
    Audience = PageAudience.Public
}

/// Content-API stub counting `GetPageInContext` calls and blocking each on
/// `gate` so concurrent requests pile onto the single coalesced produce
/// before it stores.
let private mkGatedApi (page: PublicPage) (resolveCount: int ref) (gate: Task) : IPublicContentApi =
    { new IPublicContentApi with
        member _.GetPage slug = async { return Some page }
        member _.ListPages _ = async { return [ page ] }
        member _.GetCollection _ = async { return [] }

        member _.GetPageInContext(_slug, _ctx) = async {
            Interlocked.Increment(&resolveCount.contents) |> ignore
            do! Async.AwaitTask gate
            return Some page
        }
    }

type private HResult = { Status: int; Body: string }

let private cacheableSettings = {
    RenderCacheSettings.defaults with
        DefaultPolicy = CachePolicy.Cache(300, true)
}

let private oneRequest (provider: IServiceProvider) (api: IPublicContentApi) (path: string) : Task<HResult> =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx.Request.Path <- PathString(path)
    let respBody = new MemoryStream()
    ctx.Response.Body <- respBody
    let finalFunc: HttpFunc = fun c -> Task.FromResult(Some c)
    let h = PublicPageHandler.handler api layouts

    task {
        let! _ = h finalFunc ctx
        respBody.Position <- 0L
        let text = (new StreamReader(respBody)).ReadToEnd()

        return {
            Status = ctx.Response.StatusCode
            Body = text
        }
    }

let private buildProvider (cache: IRenderCache) (settings: RenderCacheSettings) : IServiceProvider =
    let services = ServiceCollection()
    services.AddSingleton<IMetricsSink>(NoOpMetricsSink() :> IMetricsSink) |> ignore
    services.AddSingleton<IRenderCache>(cache) |> ignore
    services.AddSingleton<RenderCacheSettings>(settings) |> ignore

    services.AddSingleton<IRenderCoalescer>(InProcessRenderCoalescer.create ())
    |> ignore

    services.BuildServiceProvider() :> IServiceProvider

let private handlerTests =
    testList "PublicPageHandler — Phase 199 concurrent-miss coalescing" [

        testCase "M concurrent cold-key misses → resolved exactly once, all get the identical page"
        <| fun _ ->
            let count = ref 0
            let gate = TaskCompletionSource<unit>()
            let api = mkGatedApi (mkPage "about") count gate.Task
            let cache = InMemoryRenderCache.create ()
            let provider = buildProvider cache cacheableSettings

            // Dispatch 8 concurrent requests; they pile onto the one gated
            // produce. Release once they are all in flight.
            let tasks = [ for _ in 1..8 -> oneRequest provider api "/about" ]
            Thread.Sleep 150
            gate.SetResult()
            let results = Task.WhenAll(tasks).GetAwaiter().GetResult()

            Expect.equal
                count.Value
                1
                "the expensive IContentSource resolution ran exactly once for 8 concurrent misses"

            Expect.all results (fun r -> r.Status = 200) "every request served 200"
            Expect.all results (fun r -> r.Body.Contains "Title-about") "every request got the rendered page"
            let bodies = results |> Array.map (fun r -> r.Body) |> Array.distinct
            Expect.equal bodies.Length 1 "all M requests observed the identical RenderedPage body"

        testCase "after the coalesced miss stores, a later request is a plain cache hit (no re-resolve)"
        <| fun _ ->
            let count = ref 0
            let api = mkGatedApi (mkPage "about") count Task.CompletedTask
            let cache = InMemoryRenderCache.create ()
            let provider = buildProvider cache cacheableSettings

            oneRequest provider api "/about"
            |> fun t -> t.GetAwaiter().GetResult() |> ignore

            oneRequest provider api "/about"
            |> fun t -> t.GetAwaiter().GetResult() |> ignore

            Expect.equal count.Value 1 "second request served from the stored entry (coalescer not consulted on a hit)"

        testCase "cache-off deployment (no cache composed) is unchanged — every request resolves"
        <| fun _ ->
            let count = ref 0
            let api = mkGatedApi (mkPage "about") count Task.CompletedTask

            // No IRenderCache / RenderCacheSettings / IRenderCoalescer in DI:
            // the pre-84 uncached path runs per request, byte-for-byte.
            let services = ServiceCollection()
            services.AddSingleton<IMetricsSink>(NoOpMetricsSink() :> IMetricsSink) |> ignore
            let provider = services.BuildServiceProvider() :> IServiceProvider

            let tasks = [ for _ in 1..4 -> oneRequest provider api "/about" ]
            let results = Task.WhenAll(tasks).GetAwaiter().GetResult()

            Expect.equal count.Value 4 "with no cache composed, coalescing never engages — each request resolves"
            Expect.all results (fun r -> r.Status = 200) "every request served 200"

        testCase "SWR hit path still serves a stale entry under a cacheable default (coalescing does not disturb it)"
        <| fun _ ->
            let count = ref 0
            let api = mkGatedApi (mkPage "about") count Task.CompletedTask
            let cache = InMemoryRenderCache.create ()
            let provider = buildProvider cache cacheableSettings

            // Seed an already-expired stale-while-revalidate entry.
            let k = key "about"

            let stale =
                RenderedPage.forStore "<stale-marker/>" (DateTimeOffset.UtcNow.AddSeconds(-30.0))

            cache.Set k stale (CachePolicy.Cache(1, true)) |> Async.RunSynchronously

            let r = oneRequest provider api "/about" |> fun t -> t.GetAwaiter().GetResult()

            Expect.equal r.Status 200 "the stale entry is served (SWR hit path unaffected by coalescing)"
            Expect.stringContains r.Body "stale-marker" "the stale body is served immediately"
    ]

let tests = testList "RenderCoalescing (Phase 199)" [ coalescerTests; handlerTests ]