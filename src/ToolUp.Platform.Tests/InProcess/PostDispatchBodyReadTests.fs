// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.PostDispatchBodyReadTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe

// ─── Phase 461 — the request-body rewind invariant is STRUCTURAL ──
//
// The Giraffe dispatcher calls `ctx.Request.EnableBuffering()` on every
// non-streaming request, so the body is a rewindable
// `FileBufferingReadStream` that ASP.NET Core itself disposes at
// end-of-request. Until Phase 461 the proxy's argument read took
// OWNERSHIP of that stream (`use sr = new StreamReader(props.Input)`)
// and disposed it the moment dispatch finished — so a post-dispatch
// consumer that was the FIRST reader of the body met a dead stream,
// threw `ObjectDisposedException` after the response had started, and
// the connection reset surfaced at the gateway as a 502 over a handler
// that had already succeeded. The rule "seed the body cache BEFORE
// dispatch" lived in a load-bearing comment plus one guard per known
// consumer (audit, idempotency) — correct today, and broken by the next
// stage author who never read the comment.
//
// The proxy now only BORROWS the body (leaveOpen, rewound after the
// read). These tests are the contract: a NEW post-dispatch stage —
// registered by either mechanism a consumer has, an ASP.NET middleware
// around the handler or a Giraffe stage composed after it — reads the
// full body with no guard, no cache seed and no comment to know about.
// The API deliberately arms NOTHING (no `[<Audit>]`, no validation
// attributes, no `[<Idempotent>]`), so no pre-flight stage touches the
// body and the proxy is the first reader — the exact shape that used to
// dispose it.
//
// These run over the real dispatcher on a TestServer, because only the
// genuine EnableBuffering → proxy-read → post-dispatch-read lifecycle
// reproduces the class (same shape as `AuditBodyDisposalTests`).

type private PlainRequest = { Name: string; Count: int }

type private PlainApi = {
    [<AllowAnonymous>]
    Echo: PlainRequest -> Async<string>
}

let private plainImpl: PlainApi = {
    Echo = fun req -> async { return sprintf "%s:%d" req.Name req.Count }
}

let private plainHandler () =
    Remoting.createApi ()
    |> Remoting.fromValue plainImpl
    |> Remoting.buildHttpHandler

let private buildHost (configure: IApplicationBuilder -> unit) : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost.UseTestServer().Configure(fun (app: IApplicationBuilder) -> configure app)
            |> ignore)
        .Build()

let private post (host: IHost) (path: string) (body: string) = async {
    do! host.StartAsync() |> Async.AwaitTask
    use client = host.GetTestClient()
    use req = new HttpRequestMessage(HttpMethod.Post, path)
    req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
    let! resp = client.SendAsync req |> Async.AwaitTask
    let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
    do! host.StopAsync() |> Async.AwaitTask
    return resp.StatusCode, text
}

/// The shape every post-dispatch consumer takes against a buffered body:
/// rewind to 0, read the whole stream. No guard, no cache, no knowledge of
/// what the proxy did with it.
let private readWholeBody (ctx: HttpContext) = task {
    ctx.Request.Body.Position <- 0L
    use ms = new MemoryStream()
    do! ctx.Request.Body.CopyToAsync ms
    return Encoding.UTF8.GetString(ms.ToArray())
}

let private describe (e: exn) =
    sprintf "%s: %s" (e.GetType().Name) e.Message

// ─── Phase 461.D — the failure mode, when it DOES happen ──
//
// The dispatcher no longer disposes the body, but a consumer's own
// middleware can. When a dispatcher body read then finds a disposed
// stream, the raw `ObjectDisposedException` ("Cannot access a disposed
// object. Object name: 'FileBufferingReadStream'") says nothing about
// which method, which stage, or whether the client saw a 500 or a reset.
// The dispatcher translates it into `RequestBodyDisposedException`
// naming the method and whether the response had started, logs it
// through the diagnostics logger, and records it on the telemetry seam
// as a `Failed` outcome — so the "handler succeeded but the client saw a
// 502" shape is diagnosable from a dashboard rather than a packet trace.
// An audited-but-unvalidated method is used because its pre-dispatch
// prefetch is a dispatcher body read that happens BEFORE the first byte,
// so the translated fault is an ordinary 500 rather than a reset.

type private AuditedRequest = {
    [<PiiSafe>]
    Name: string
}

type private AuditedApi = {
    [<AllowAnonymous>]
    [<Audit "PolicyChanged">]
    CreateThing: AuditedRequest -> Async<string>
}

let private auditedImpl: AuditedApi = {
    CreateThing = fun req -> async { return req.Name }
}

[<Tests>]
let tests =
    testList "Phase 461 — post-dispatch request-body read contract" [

        testAsync "a NEW middleware stage reads the full body after next.Invoke — no guard, no ObjectDisposedException" {
            let seen: string option ref = ref None
            let fault: exn option ref = ref None
            let handler = plainHandler ()

            use host =
                buildHost (fun app ->
                    // An audit-logging middleware in the consumer's pipeline:
                    // reads the request body AFTER the remoting handler has
                    // dispatched and written its response. Registered by an
                    // author who has never seen Proxy.fs.
                    app.Use(
                        Func<HttpContext, RequestDelegate, Task>(fun ctx next -> task {
                            do! next.Invoke ctx

                            try
                                let! text = readWholeBody ctx
                                seen.Value <- Some text
                            with e ->
                                fault.Value <- Some e
                        })
                    )
                    |> ignore

                    app.UseGiraffe handler)

            let body = """[{"Name":"CPG","Count":3}]"""
            let! status, text = post host "/PlainApi/Echo" body

            Expect.equal status HttpStatusCode.OK "the handler succeeds and the client sees its real status"
            Expect.stringContains text "CPG:3" "the handler result is returned intact"

            match fault.Value with
            | Some e -> failtestf "the post-dispatch middleware read threw: %s" (describe e)
            | None -> ()

            Expect.equal
                seen.Value
                (Some body)
                "the post-dispatch middleware read the FULL body — the proxy borrowed the stream, it did not dispose it"
        }

        testAsync
            "a NEW Giraffe stage composed after the remoting handler reads the full body — no guard, no ObjectDisposedException" {
            let seen: string option ref = ref None
            let fault: exn option ref = ref None

            let postStage: HttpHandler =
                fun next ctx -> task {
                    try
                        let! text = readWholeBody ctx
                        seen.Value <- Some text
                    with e ->
                        fault.Value <- Some e

                    return! next ctx
                }

            // `>=>` runs the stage from the remoting handler's own `next`
            // continuation, i.e. after dispatch and after the response
            // bytes were written — the other registration mechanism a
            // consumer has.
            let handler = plainHandler () >=> postStage
            use host = buildHost (fun app -> app.UseGiraffe handler)

            let body = """[{"Name":"Core","Count":7}]"""
            let! status, text = post host "/PlainApi/Echo" body

            Expect.equal status HttpStatusCode.OK "the handler succeeds and the client sees its real status"
            Expect.stringContains text "Core:7" "the handler result is returned intact"

            match fault.Value with
            | Some e -> failtestf "the post-dispatch Giraffe stage read threw: %s" (describe e)
            | None -> ()

            Expect.equal
                seen.Value
                (Some body)
                "the post-dispatch Giraffe stage read the FULL body — the proxy left the buffered stream live and rewound"
        }

        testAsync
            "a body disposed by foreign middleware surfaces as a 500 naming the method, on the log and on telemetry — never a post-response reset" {
            let logged = ResizeArray<string>()
            let telemetry = ResizeArray<MethodTelemetry>()

            let sink =
                { new IRemotingTelemetry with
                    member _.OnMethodCompleted evt =
                        lock telemetry (fun () -> telemetry.Add evt)
                }

            let emitter =
                { new IAuditEmitter with
                    member _.Emit _ = async { () }
                }

            let handler =
                Remoting.createApi ()
                |> Remoting.withDiagnosticsLogger (fun line -> lock logged (fun () -> logged.Add line))
                |> Remoting.withTelemetry sink
                |> Remoting.withAudit emitter
                |> Remoting.fromValue auditedImpl
                |> Remoting.buildHttpHandler

            use host =
                buildHost (fun app ->
                    // Translate whatever escapes the handler into a plain 500
                    // carrying the exception's name — the ordinary consumer
                    // error handler. A fault BEFORE the first byte reaches it;
                    // a fault after the first byte cannot (the connection is
                    // reset instead), which is exactly the distinction the
                    // typed exception makes visible.
                    app.UseGiraffeErrorHandler(fun ex _ -> setStatusCode 500 >=> text (describe ex))
                    |> ignore

                    // The foreign stage: buffers, then DISPOSES the body before
                    // the dispatcher ever sees it. `EnableBuffering` is
                    // idempotent on a stream that already reports `CanSeek`,
                    // so the dispatcher's own call does not re-wrap it.
                    app.Use(
                        Func<HttpContext, RequestDelegate, Task>(fun ctx next -> task {
                            ctx.Request.EnableBuffering()
                            ctx.Request.Body.Dispose()
                            do! next.Invoke ctx
                        })
                    )
                    |> ignore

                    app.UseGiraffe handler)

            let! status, text = post host "/AuditedApi/CreateThing" """[{"Name":"CPG"}]"""

            Expect.equal
                status
                HttpStatusCode.InternalServerError
                "a pre-first-byte disposed-body read is an ordinary 500, not a reset"

            Expect.stringContains
                text
                "RequestBodyDisposedException"
                "the fault that escapes is the typed exception, not a bare ObjectDisposedException"

            Expect.stringContains text "CreateThing" "the typed exception names the method being dispatched"

            let loggedLines = lock logged (fun () -> logged |> List.ofSeq)

            Expect.isTrue
                (loggedLines
                 |> List.exists (fun l -> l.Contains "request-body-disposed" && l.Contains "CreateThing"))
                (sprintf "the diagnostics logger carries the fault, naming the method; got %A" loggedLines)

            let events = lock telemetry (fun () -> telemetry |> List.ofSeq)
            Expect.hasLength events 1 "exactly one telemetry event is recorded for the faulted call"

            match events[0].Outcome with
            | MethodOutcome.Failed(:? RequestBodyDisposedException as e) ->
                Expect.equal
                    e.MethodName
                    "CreateThing"
                    "the telemetry outcome carries the typed fault with the method name"

                Expect.isFalse e.ResponseHadStarted "the read failed before the first byte, and the fault says so"
            | other -> failtestf "expected a Failed(RequestBodyDisposedException) outcome, got %A" other
        }
    ]