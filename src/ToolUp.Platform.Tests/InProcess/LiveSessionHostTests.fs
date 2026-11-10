module ToolUp.Platform.Tests.InProcess.LiveSessionHostTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Infra
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose
open ToolUp.Platform.Config

// ─── Phase 112 — live-session host + endpoint tests ──────────────────
//
// Three layers:
//   1. `ILiveSessionHostContract` bound to the in-process default.
//   2. In-memory-specific behaviour: the per-scope concurrent-session
//      cap refusal (as data, not an exception).
//   3. `LiveSessionHandler` endpoint integration over a
//      `DefaultHttpContext`: path-gate fall-through (the not-composed /
//      wrong-path GP 13 shape), 404 on an unknown / cross-scope
//      session id, per-scope subscribe budget via `IRateLimitStore`
//      (429 + Retry-After), and an end-to-end frame delivery through
//      the SSE response body.

let private run a = a |> Async.RunSynchronously

let private scope (id: string) : StorageScope = {
    ScopeId = id
    Container = $"test-%s{id}"
    Persist = false
}

// ─── 1. Contract binding ─────────────────────────────────────────────

let private contractTests =
    ILiveSessionHostContract.tests "InMemoryLiveSessionHost" (fun () -> LiveSessionHost.createInMemory None)

// ─── 2. Cap refusal ──────────────────────────────────────────────────

let private capTests =
    testList "InMemoryLiveSessionHost — per-scope cap" [
        testCase "OpenSession refuses (as data) at the configured cap, per scope"
        <| fun _ ->
            let host = LiveSessionHost.createInMemory (Some 1)

            match host.OpenSession(scope "a") |> run with
            | Ok _ -> ()
            | Error r -> failtestf "first open must succeed; got refusal %A" r

            match host.OpenSession(scope "a") |> run with
            | Error refusal ->
                Expect.equal refusal.ScopeId "a" "names the scope"
                Expect.equal refusal.Cap 1 "carries the cap"
                Expect.equal refusal.CurrentCount 1 "carries the current count"
            | Ok _ -> failtest "second open in the same scope must refuse at cap 1"

            match host.OpenSession(scope "b") |> run with
            | Ok _ -> ()
            | Error r -> failtestf "the cap is per-scope; scope b must open; got %A" r
    ]

// ─── 3. Endpoint integration ─────────────────────────────────────────

let private mkContext (services: IServiceCollection) (path: string) (aborted: CancellationToken) =
    let provider = services.BuildServiceProvider()
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx.Request.Method <- "GET"
    ctx.Request.Path <- PathString(path)
    ctx.Features.Set<IHttpRequestLifetimeFeature>(HttpRequestLifetimeFeature(RequestAborted = aborted))
    let body = new MemoryStream()
    ctx.Response.Body <- body
    ctx, body

let private finalFunc: HttpFunc = fun c -> Task.FromResult(Some c)

let private cancelled = CancellationToken(canceled = true)

let private endpointTests =
    testList "LiveSessionHandler — endpoint integration" [

        testCase "non-matching path falls through to the next handler"
        <| fun _ ->
            let host = LiveSessionHost.createInMemory None

            let handler =
                LiveSessionHandler.handler QueryParamFallback LiveSessionOptions.defaults host

            let ctx, _ = mkContext (ServiceCollection()) "/api/other" cancelled

            let result = (handler finalFunc ctx).GetAwaiter().GetResult()

            Expect.isNone result "the handler skips paths it does not own"

        testCase "unknown session id → 404 (cross-scope ids are indistinguishable from unknown)"
        <| fun _ ->
            let host = LiveSessionHost.createInMemory None

            // A session exists — but in another scope than the caller's
            // (anonymous fallback) — so resolution under the caller's
            // partition fails structurally.
            let d =
                match host.OpenSession(scope "team-42") |> run with
                | Ok d -> d
                | Error r -> failtestf "open failed: %A" r

            let handler =
                LiveSessionHandler.handler QueryParamFallback LiveSessionOptions.defaults host

            let ctx, _ =
                mkContext (ServiceCollection()) $"/api/live-sessions/%s{d.SessionId}" cancelled

            (handler finalFunc ctx).GetAwaiter().GetResult() |> ignore

            Expect.equal ctx.Response.StatusCode 404 "cross-scope session id resolves to 404, never to frames"

        testCase "per-scope subscribe budget via IRateLimitStore → 429 + Retry-After when exceeded"
        <| fun _ ->
            let host = LiveSessionHost.createInMemory None

            let options = {
                LiveSessionOptions.defaults with
                    SubscribesPerMinutePerScope = Some 1
            }

            let handler = LiveSessionHandler.handler QueryParamFallback options host
            let services = ServiceCollection()

            services.AddSingleton<IRateLimitStore>(InMemoryRateLimitStore.create ())
            |> ignore

            // First request burns the budget (404s afterwards — no such
            // session — but the rate-limit count is already taken).
            let ctx1, _ = mkContext services "/api/live-sessions/nope" cancelled
            (handler finalFunc ctx1).GetAwaiter().GetResult() |> ignore
            Expect.equal ctx1.Response.StatusCode 404 "first request passes the budget gate"

            let ctx2, _ = mkContext services "/api/live-sessions/nope" cancelled
            (handler finalFunc ctx2).GetAwaiter().GetResult() |> ignore

            Expect.equal ctx2.Response.StatusCode 429 "second request exceeds the per-scope budget"
            Expect.isTrue (ctx2.Response.Headers.ContainsKey "Retry-After") "429 carries Retry-After"

        testCase "a subscribed connection receives pushed frames over the SSE body"
        <| fun _ ->
            let host = LiveSessionHost.createInMemory None

            let d =
                match host.OpenSession(scope "anonymous") |> run with
                | Ok d -> d
                | Error r -> failtestf "open failed: %A" r

            let handler =
                LiveSessionHandler.handler QueryParamFallback LiveSessionOptions.defaults host

            use cts = new CancellationTokenSource()

            let ctx, body =
                mkContext (ServiceCollection()) $"/api/live-sessions/%s{d.SessionId}" cts.Token

            let serving = handler finalFunc ctx

            // Wait until the subscription is registered, then push.
            let mutable waited = 0

            while (host.ListSessions "anonymous" |> run |> List.isEmpty) && waited < 100 do
                Thread.Sleep 10
                waited <- waited + 1

            Thread.Sleep 50

            let channel = (host.TryGetChannel("anonymous", d.SessionId) |> run).Value
            channel.PushFrame """{"op":"patch"}""" |> run

            Thread.Sleep 50
            cts.Cancel()
            serving.GetAwaiter().GetResult() |> ignore

            body.Position <- 0L
            let text = (new StreamReader(body)).ReadToEnd()

            Expect.stringContains text "event: live-frame" "frame rides the named SSE event"
            Expect.stringContains text """{"op":"patch"}""" "payload delivered verbatim"
    ]

// ─── 4. Phase 870 — frames are written one at a time, in order ──────

// The faulting-body harness, `SerialOnlyBody`, lives in
// `SsePerConnectionWriterTests` (it compiles first) since Phase 1064 pins
// the notification stream with it too.

let private waitUntil (timeoutMs: int) (condition: unit -> bool) =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

    while not (condition ()) && DateTime.UtcNow < deadline do
        Thread.Sleep 5

    condition ()

let private seqsOf (text: string) =
    Text.RegularExpressions.Regex.Matches(text, "\"seq\":(\\d+)")
    |> Seq.map (fun m -> int m.Groups[1].Value)
    |> Seq.toList

// ─── Phase 1068 — the subscribe budget's declared store posture ──────

/// A Phase 56 store whose write side always fails.
type private FailingRateLimitStore() =
    interface IRateLimitStore with
        member _.GetCurrent(_, _) = async { return 0 }
        member _.IncrementAndCheck(_, _, _) = async { return Error(StoreUnavailable "simulated outage") }
        member _.GetRecentDecisions(_, _) = async { return [] }

/// An `IAuditLog` that keeps what it is handed.
type private RecordingAuditLog() =
    let rows = System.Collections.Concurrent.ConcurrentQueue<string * AuditEvent>()
    member _.Rows = rows |> Seq.toList

    interface IAuditLog with
        member _.Record(AuditScope.StreamKey scopeId, _actor, audit) = async {
            rows.Enqueue(scopeId, audit)
            return Ok()
        }

        member _.GetAuditTrail(AuditScope.StreamKey _, _, _) = async { return [] }

let private postureTests =
    let subscribeOnce (posture: StorePosture) =
        let host = LiveSessionHost.createInMemory None

        let options = {
            LiveSessionOptions.defaults with
                SubscribesPerMinutePerScope = Some 5
                SubscribeStorePosture = posture
        }

        let audit = RecordingAuditLog()
        let services = ServiceCollection()
        services.AddSingleton<IRateLimitStore>(FailingRateLimitStore()) |> ignore
        services.AddSingleton<IAuditLog>(audit) |> ignore
        let ctx, _ = mkContext services "/api/live-sessions/nope" cancelled

        (LiveSessionHandler.handler QueryParamFallback options host finalFunc ctx).GetAwaiter().GetResult()
        |> ignore

        ctx, audit

    testList "subscribe budget store posture (Phase 1068)" [
        testCase "the default posture is FailOpen"
        <| fun _ ->
            Expect.equal
                LiveSessionOptions.defaults.SubscribeStorePosture
                StorePosture.FailOpen
                "a deployment that declares nothing keeps today's behaviour"

        testCase "FailOpen: a failing store admits the subscribe and writes no audit row"
        <| fun _ ->
            let ctx, audit = subscribeOnce StorePosture.FailOpen
            Expect.equal ctx.Response.StatusCode 404 "the subscribe passes the budget gate (no such session)"
            Expect.isEmpty audit.Rows "an admission is not audited"

        testCase "FailClosed: a failing store refuses the subscribe with 503 and audits it under the scope"
        <| fun _ ->
            let ctx, audit = subscribeOnce StorePosture.FailClosed
            Expect.equal ctx.Response.StatusCode 503 "the subscribe is refused"

            match audit.Rows with
            | [ scopeId, AuditEvent.RateLimitRefused row ] ->
                Expect.equal scopeId "anonymous" "written under the caller's scope"
                Expect.equal row.Provider "live-sessions:subscribe" "names the budget that could not be counted"
                Expect.stringContains row.Reason "simulated outage" "carries the store's reason"
            | other -> failtestf "expected one RateLimitRefused row, got %A" other
    ]

// ─── The live-session route resolves its scope through the one SSE resolver

let private resolverTests =
    testList "LiveSessionHandler — the one SSE scope resolver" [
        testCase "CookieRequired: a connect with no validated principal is refused with 401"
        <| fun _ ->
            let host = LiveSessionHost.createInMemory None

            let d =
                match host.OpenSession(scope "anonymous") |> run with
                | Ok d -> d
                | Error r -> failtestf "open failed: %A" r

            let handler =
                LiveSessionHandler.handler CookieRequired LiveSessionOptions.defaults host

            let ctx, body =
                mkContext (ServiceCollection()) $"/api/live-sessions/%s{d.SessionId}" cancelled

            ctx.Request.QueryString <- QueryString "?userId=anonymous"
            (handler finalFunc ctx).GetAwaiter().GetResult() |> ignore

            Expect.equal ctx.Response.StatusCode 401 "no client-asserted id opens a stream under CookieRequired"
            body.Position <- 0L
            let text = (new StreamReader(body)).ReadToEnd()
            Expect.stringContains text "SseAuthMode = CookieRequired" "the shared refusal body"

        testCase "a mismatching ?userId= on the live-session route is audited, and the principal's partition wins"
        <| fun _ ->
            let host = LiveSessionHost.createInMemory None
            let audit = RecordingAuditLog()
            let services = ServiceCollection()
            services.AddSingleton<IAuditLog>(audit) |> ignore

            let handler =
                LiveSessionHandler.handler QueryParamFallback LiveSessionOptions.defaults host

            let ctx, _ = mkContext services "/api/live-sessions/nope" cancelled
            ctx.Request.QueryString <- QueryString "?userId=mallory"
            AccessContext.stamp ctx.Items (AccessContexts.teamMember "alice" "team-42")

            (handler finalFunc ctx).GetAwaiter().GetResult() |> ignore

            Expect.equal ctx.Response.StatusCode 404 "no such session in the team partition"

            let deadline = DateTime.UtcNow.AddSeconds 5.0

            while List.isEmpty audit.Rows && DateTime.UtcNow < deadline do
                Thread.Sleep 5

            match audit.Rows with
            | [ _, AuditEvent.SurfaceDenied row ] ->
                Expect.equal row.DenialCode SseScopeResolution.mismatchDenialCode "the mismatch denial code"
                Expect.equal row.Hint (Some "asserted=mallory") "names the asserted id"
            | other -> failtestf "expected one SurfaceDenied row, got %A" other

        testCase "a validated team member subscribes under its team partition"
        <| fun _ ->
            let host = LiveSessionHost.createInMemory None

            let d =
                match host.OpenSession(scope "team-42") |> run with
                | Ok d -> d
                | Error r -> failtestf "open failed: %A" r

            let handler =
                LiveSessionHandler.handler CookieRequired LiveSessionOptions.defaults host

            use cts = new CancellationTokenSource()

            let ctx, body =
                mkContext (ServiceCollection()) $"/api/live-sessions/%s{d.SessionId}" cts.Token

            AccessContext.stamp ctx.Items (AccessContexts.teamMember "alice" "team-42")
            let serving = handler finalFunc ctx
            let mutable waited = 0

            while (body.Length = 0L) && waited < 200 do
                Thread.Sleep 10
                waited <- waited + 1

            cts.Cancel()
            serving.GetAwaiter().GetResult() |> ignore

            Expect.equal ctx.Response.StatusCode 200 "the stream opened under the team partition"
    ]

let private burstTests =
    testList "LiveSessionHandler — frame burst (Phase 870)" [

        testCase "a burst of frames arrives complete and in order, with no unobserved task fault"
        <| fun _ ->
            // 200 frames per burst, 20 bursts. Pushed back to back, so a
            // writer that does not await one write before starting the
            // next overlaps them on the first pair.
            let frames = 200
            let bursts = 20
            let unobserved = ref 0

            let onUnobserved =
                EventHandler<UnobservedTaskExceptionEventArgs>(fun _ args ->
                    let ours =
                        args.Exception.Flatten().InnerExceptions
                        |> Seq.exists (fun e ->
                            e.Message = SsePerConnectionWriterTests.SerialOnlyBody.ConcurrentWriteMessage)

                    if ours then
                        Interlocked.Increment(&unobserved.contents) |> ignore)

            TaskScheduler.UnobservedTaskException.AddHandler onUnobserved

            let failures =
                try
                    [
                        for burst in 1..bursts do
                            let host = LiveSessionHost.createInMemory None

                            let d =
                                match host.OpenSession(scope "anonymous") |> run with
                                | Ok d -> d
                                | Error r -> failtestf "open failed: %A" r

                            let handler =
                                LiveSessionHandler.handler QueryParamFallback LiveSessionOptions.defaults host

                            use cts = new CancellationTokenSource()

                            let ctx, _ =
                                mkContext (ServiceCollection()) $"/api/live-sessions/%s{d.SessionId}" cts.Token

                            let body = new SsePerConnectionWriterTests.SerialOnlyBody()
                            ctx.Response.Body <- body
                            let serving = handler finalFunc ctx

                            // The ready comment is written after the
                            // subscription is registered.
                            if not (waitUntil 5000 (fun () -> body.Text.Contains ": ready")) then
                                failtestf "burst %d: the stream never opened" burst

                            let channel = (host.TryGetChannel("anonymous", d.SessionId) |> run).Value

                            for i in 0 .. frames - 1 do
                                channel.PushFrame $"{{\"seq\":%d{i}}}" |> run

                            waitUntil 2000 (fun () -> (seqsOf body.Text).Length >= frames) |> ignore

                            cts.Cancel()
                            serving.GetAwaiter().GetResult() |> ignore

                            let seen = seqsOf body.Text

                            if seen <> [ 0 .. frames - 1 ] then
                                yield burst, seen.Length
                    ]
                finally
                    GC.Collect()
                    GC.WaitForPendingFinalizers()
                    GC.Collect()
                    TaskScheduler.UnobservedTaskException.RemoveHandler onUnobserved

            Expect.isEmpty
                failures
                $"every burst delivers all %d{frames} frames in order (burst, frames seen) — %d{failures.Length} of %d{bursts} bursts failed, %d{unobserved.Value} unobserved write faults"

            Expect.equal unobserved.Value 0 "no frame write faults inside an unobserved task"
    ]

let tests =
    testList "LiveSessionHost (Phase 112)" [
        contractTests
        capTests
        endpointTests
        postureTests
        resolverTests
        burstTests
    ]