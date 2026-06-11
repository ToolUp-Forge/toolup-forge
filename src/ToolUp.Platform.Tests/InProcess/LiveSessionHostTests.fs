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
            let handler = LiveSessionHandler.handler LiveSessionOptions.defaults host
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

            let handler = LiveSessionHandler.handler LiveSessionOptions.defaults host

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

            let handler = LiveSessionHandler.handler options host
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

            let handler = LiveSessionHandler.handler LiveSessionOptions.defaults host
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

let tests =
    testList "LiveSessionHost (Phase 112)" [ contractTests; capTests; endpointTests ]