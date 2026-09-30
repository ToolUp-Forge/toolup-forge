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

// ─── 4. Phase 870 — frames are written one at a time, in order ──────

/// A response body that behaves like Kestrel's on the one point this pin
/// needs: a write or flush issued while another is still in flight
/// faults, as a faulted `Task` (the way an async method faults), rather
/// than being serialised for the caller. `MemoryStream` completes every
/// write synchronously, so it can never show the race.
type private SerialOnlyBody() =
    inherit Stream()

    let buffer = new MemoryStream()
    let busy = ref 0

    static member val ConcurrentWriteMessage = "Phase 870 fake: concurrent writes are not supported."

    member _.Text = lock buffer (fun () -> Text.Encoding.UTF8.GetString(buffer.ToArray()))

    member private _.Guarded(append: unit -> unit) : Task = task {
        if Interlocked.CompareExchange(&busy.contents, 1, 0) <> 0 then
            raise (InvalidOperationException SerialOnlyBody.ConcurrentWriteMessage)

        try
            do! Task.Yield()
            lock buffer append
        finally
            Volatile.Write(&busy.contents, 0)
    }

    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.Read(_: byte[], _: int, _: int) : int = raise (NotSupportedException())
    override _.Seek(_: int64, _: SeekOrigin) : int64 = raise (NotSupportedException())
    override _.SetLength(_: int64) = raise (NotSupportedException())

    override _.Write(bytes: byte[], offset: int, count: int) =
        lock buffer (fun () -> buffer.Write(bytes, offset, count))

    override this.WriteAsync(bytes: byte[], offset: int, count: int, _: CancellationToken) : Task =
        let copy = Array.sub bytes offset count
        this.Guarded(fun () -> buffer.Write(copy, 0, copy.Length))

    override this.WriteAsync(bytes: ReadOnlyMemory<byte>, _: CancellationToken) : ValueTask =
        let copy = bytes.ToArray()
        ValueTask(this.Guarded(fun () -> buffer.Write(copy, 0, copy.Length)))

    override this.FlushAsync(_: CancellationToken) : Task = this.Guarded ignore

let private waitUntil (timeoutMs: int) (condition: unit -> bool) =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

    while not (condition ()) && DateTime.UtcNow < deadline do
        Thread.Sleep 5

    condition ()

let private seqsOf (text: string) =
    Text.RegularExpressions.Regex.Matches(text, "\"seq\":(\\d+)")
    |> Seq.map (fun m -> int m.Groups[1].Value)
    |> Seq.toList

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
                        |> Seq.exists (fun e -> e.Message = SerialOnlyBody.ConcurrentWriteMessage)

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

                            let handler = LiveSessionHandler.handler LiveSessionOptions.defaults host
                            use cts = new CancellationTokenSource()

                            let ctx, _ =
                                mkContext (ServiceCollection()) $"/api/live-sessions/%s{d.SessionId}" cts.Token

                            let body = new SerialOnlyBody()
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
    testList "LiveSessionHost (Phase 112)" [ contractTests; capTests; endpointTests; burstTests ]