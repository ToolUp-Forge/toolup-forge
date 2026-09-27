module ToolUp.Platform.Tests.Remoting.RemotingBatchTests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
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

// ─── Phase 855.B / 855.D — the remoting batch route ───────────────────
//
// An envelope's elements are each run through the ordinary per-call
// pipeline, so every seam the dispatcher holds (authorisation, rate limit,
// audit) and every middleware composed after the batch point applies per
// element. These run the real dispatcher on a TestServer behind the real
// `RemotingBatch` middleware — the shape a composed deployment has.

type private BatchedApi = {
    [<AllowAnonymous>]
    [<Audit "PolicyChanged">]
    Echo: string -> Async<string>
    [<RequiresRole "Admin">]
    AdminOnly: unit -> Async<int>
    [<AllowAnonymous>]
    [<RateLimit(2, RateLimitWindow.perMinute)>]
    Capped: unit -> Async<int>
    [<AllowAnonymous>]
    Answer: unit -> Async<int>
    [<AllowAnonymous>]
    Numbers: int -> IAsyncEnumerable<int>
}

let private numbers (upto: int) : IAsyncEnumerable<int> =
    { new IAsyncEnumerable<int> with
        member _.GetAsyncEnumerator _ =
            let mutable current = 0

            { new IAsyncEnumerator<int> with
                member _.Current = current

                member _.MoveNextAsync() =
                    current <- current + 1
                    ValueTask<bool>(current <= upto)

                member _.DisposeAsync() = ValueTask()
            }
    }

let private impl: BatchedApi = {
    Echo = fun text -> async { return "echo:" + text }
    AdminOnly = fun () -> async { return 42 }
    Capped = fun () -> async { return 7 }
    Answer = fun () -> async { return 41 }
    Numbers = numbers
}

/// A non-anonymous caller with no roles: `AdminOnly` refuses it.
let private member': IAuthContext =
    { new IAuthContext with
        member _.HasRole _ = false
        member _.HasClaim(_, _) = false
        member _.HasTenant() = false
        member _.IsAnonymous() = false
        member _.SubjectId = "member"
    }

type private Harness = {
    Client: HttpClient
    Host: IHost
    Audited: ResizeArray<AuditEvent>
    /// Every request the middleware AFTER the batch point saw, in order.
    Downstream: ResizeArray<string * string>
}

let private start (options: RemotingBatchOptions) = async {
    let audited = ResizeArray<AuditEvent>()
    let downstream = ResizeArray<string * string>()

    let emitter =
        { new IAuditEmitter with
            member _.Emit event = async { lock audited (fun () -> audited.Add event) }
        }

    let handler =
        Remoting.createApi ()
        |> Remoting.withAuthContext (fun _ -> async { return member' })
        |> Remoting.withRateLimitStore (InMemoryRateLimitStore())
        |> Remoting.withAudit emitter
        |> Remoting.fromValue impl
        |> Remoting.buildHttpHandler

    let host =
        Host
            .CreateDefaultBuilder()
            .ConfigureWebHostDefaults(fun webHost ->
                webHost
                    .UseTestServer()
                    .ConfigureServices(fun services -> services.AddGiraffe() |> ignore)
                    .Configure(fun (app: IApplicationBuilder) ->
                        RemotingBatch.useBatching options app |> ignore

                        app.Use(
                            Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
                                lock downstream (fun () ->
                                    downstream.Add((ctx.Request.Method, ctx.Request.Path.Value)))

                                next.Invoke ctx)
                        )
                        |> ignore

                        app.UseGiraffe handler)
                |> ignore)
            .Build()

    do! host.StartAsync() |> Async.AwaitTask

    return {
        Client = host.GetTestClient()
        Host = host
        Audited = audited
        Downstream = downstream
    }
}

let private stop (harness: Harness) = async {
    harness.Client.Dispose()
    do! harness.Host.StopAsync() |> Async.AwaitTask
    harness.Host.Dispose()
}

let private element (route: string) (body: string option) =
    sprintf
        "{\"route\":%s,\"body\":%s}"
        (JsonSerializer.Serialize route)
        (match body with
         | Some text -> JsonSerializer.Serialize text
         | None -> "null")

let private post (harness: Harness) (path: string) (body: string) = async {
    use request = new HttpRequestMessage(HttpMethod.Post, path)
    request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
    request.Headers.Add("x-remoting-proxy", "true")
    let! response = harness.Client.SendAsync request |> Async.AwaitTask
    let! text = response.Content.ReadAsStringAsync() |> Async.AwaitTask
    return response.StatusCode, text
}

let private sendEnvelope (harness: Harness) (elements: string list) =
    post harness RemotingBatch.DefaultRoute ("[" + String.concat "," elements + "]")

/// `(status, body)` per element of an envelope answer.
let private answers (text: string) =
    use document = JsonDocument.Parse text

    [
        for item in document.RootElement.EnumerateArray() do
            item.GetProperty("status").GetInt32(), item.GetProperty("body").GetString()
    ]

/// The audit write may complete after the response; wait (bounded) for it.
let private auditedAtLeast (harness: Harness) (count: int) = async {
    let mutable waited = 0

    while lock harness.Audited (fun () -> harness.Audited.Count) < count && waited < 5000 do
        do! Async.Sleep 20
        waited <- waited + 20

    return lock harness.Audited (fun () -> List.ofSeq harness.Audited)
}

let private withHarness (options: RemotingBatchOptions) (body: Harness -> Async<unit>) = async {
    let! harness = start options

    try
        do! body harness
    finally
        stop harness |> Async.RunSynchronously
}

[<Tests>]
let tests =
    testList "Phase 855 — the remoting batch route" [

        testAsync "an envelope of three calls answers three elements in order and audits three entries" {
            do!
                withHarness RemotingBatch.defaults (fun harness -> async {
                    let! status, text =
                        sendEnvelope harness [
                            element "/BatchedApi/Echo" (Some "[\"a\"]")
                            element "/BatchedApi/Echo" (Some "[\"b\"]")
                            element "/BatchedApi/Echo" (Some "[\"c\"]")
                        ]

                    Expect.equal status HttpStatusCode.OK "the envelope is answered"

                    Expect.equal
                        (answers text)
                        [ 200, "\"echo:a\""; 200, "\"echo:b\""; 200, "\"echo:c\"" ]
                        "one element per call, in order, each carrying its own response body"

                    let! events = auditedAtLeast harness 3

                    Expect.equal
                        (events |> List.map _.MethodName)
                        [ "Echo"; "Echo"; "Echo" ]
                        "each element is audited as a call of its own"

                    Expect.equal
                        (lock harness.Downstream (fun () -> List.ofSeq harness.Downstream))
                        [
                            "POST", "/BatchedApi/Echo"
                            "POST", "/BatchedApi/Echo"
                            "POST", "/BatchedApi/Echo"
                        ]
                        "every element passed through the middleware after the batch point, on its own path; the envelope did not"
                })
        }

        testAsync "each element takes its own rate-limit check" {
            do!
                withHarness RemotingBatch.defaults (fun harness -> async {
                    // Budget 2 per minute: three checks, the third denied.
                    let! status, text =
                        sendEnvelope harness [
                            element "/BatchedApi/Capped" None
                            element "/BatchedApi/Capped" None
                            element "/BatchedApi/Capped" None
                        ]

                    Expect.equal status HttpStatusCode.OK "the envelope is answered"

                    Expect.equal
                        (answers text |> List.map fst)
                        [ 200; 200; 429 ]
                        "three elements are three rate-limit checks: the third exceeds the budget of two"
                })
        }

        testAsync "a per-element authorisation refusal leaves the other elements served" {
            do!
                withHarness RemotingBatch.defaults (fun harness -> async {
                    let! status, text =
                        sendEnvelope harness [
                            element "/BatchedApi/Echo" (Some "[\"x\"]")
                            element "/BatchedApi/AdminOnly" None
                            element "/BatchedApi/Answer" None
                        ]

                    Expect.equal status HttpStatusCode.OK "a refused element does not refuse the envelope"

                    // The same call sent alone: the element must be refused
                    // exactly as it is.
                    let! aloneStatus, aloneBody = post harness "/BatchedApi/AdminOnly" ""

                    match answers text with
                    | [ (200, echo); (refused, refusedBody); (200, answer) ] ->
                        Expect.equal echo "\"echo:x\"" "the element before the refusal is served"

                        Expect.isTrue
                            (refused = 401 || refused = 403)
                            "the role-gated element is refused for a caller without the role"

                        Expect.equal refused (int aloneStatus) "refused with the status a lone call gets"
                        Expect.equal refusedBody aloneBody "and the body a lone call gets"
                        Expect.equal answer "41" "the element after the refusal is served"
                    | other -> failtestf "unexpected answers %A" other
                })
        }

        testAsync "an envelope naming a streaming route is refused whole, before any element runs" {
            do!
                withHarness RemotingBatch.defaults (fun harness -> async {
                    let! status, text =
                        sendEnvelope harness [
                            element "/BatchedApi/Echo" (Some "[\"never\"]")
                            element "/BatchedApi/Numbers" (Some "[3]")
                        ]

                    Expect.equal status HttpStatusCode.BadRequest "the envelope is refused"
                    Expect.stringContains text RemotingBatch.RefusalCode "the refusal carries its code"
                    Expect.stringContains text "/BatchedApi/Numbers" "the refusal names the route"
                    Expect.isFalse (RemotingBatch.isBatchable "/BatchedApi/Numbers") "the streaming route is recorded"
                    Expect.isTrue (RemotingBatch.isBatchable "/BatchedApi/Echo") "an ordinary route is batchable"

                    Expect.isEmpty
                        (lock harness.Downstream (fun () -> List.ofSeq harness.Downstream))
                        "nothing ran — not even the element ahead of the streaming one"

                    let! events = auditedAtLeast harness 0
                    Expect.isEmpty events "nothing was audited"
                })
        }

        testAsync "malformed, oversized and non-plain envelopes are refused whole" {
            do!
                withHarness
                    {
                        RemotingBatch.defaults with
                            MaxElements = 2
                    }
                    (fun harness -> async {
                        let refusals = [
                            "not json", "{"
                            "not an array", "{}"
                            "no route", "[{\"body\":null}]"
                            "over the bound",
                            "["
                            + String.concat "," (List.replicate 3 (element "/BatchedApi/Answer" None))
                            + "]"
                            "a query string", "[" + element "/BatchedApi/Answer?x=1" None + "]"
                            "a dot segment", "[" + element "/BatchedApi/../BatchedApi/Answer" None + "]"
                            "a percent escape", "[" + element "/BatchedApi/%41nswer" None + "]"
                            "the batch route itself", "[" + element RemotingBatch.DefaultRoute (Some "[]") + "]"
                        ]

                        for label, body in refusals do
                            let! status, text = post harness RemotingBatch.DefaultRoute body
                            Expect.equal status HttpStatusCode.BadRequest (sprintf "%s: refused" label)
                            Expect.stringContains text RemotingBatch.RefusalCode (sprintf "%s: carries the code" label)

                        Expect.isEmpty
                            (lock harness.Downstream (fun () -> List.ofSeq harness.Downstream))
                            "no refused envelope ran an element"
                    })
        }

        testAsync "the element pipeline wraps every element — a per-element guard refuses only its element" {
            // Stands in for the CSRF middleware `ServerApp.withRemotingBatching`
            // supplies: it refuses a POST element whose route it guards.
            let guard (next: RequestDelegate) : RequestDelegate =
                RequestDelegate(fun ctx ->
                    if ctx.Request.Path.Value = "/BatchedApi/Echo" then
                        ctx.Response.StatusCode <- 403
                        Task.CompletedTask
                    else
                        next.Invoke ctx)

            do!
                withHarness
                    {
                        RemotingBatch.defaults with
                            ElementPipeline = guard
                    }
                    (fun harness -> async {
                        let! status, text =
                            sendEnvelope harness [
                                element "/BatchedApi/Echo" (Some "[\"guarded\"]")
                                element "/BatchedApi/Answer" None
                            ]

                        Expect.equal status HttpStatusCode.OK "the envelope is answered"

                        Expect.equal
                            (answers text)
                            [ 403, ""; 200, "41" ]
                            "the guard ran per element: its refusal is that element's status"
                    })
        }

        testAsync "a request that is not the batch route passes straight through" {
            do!
                withHarness RemotingBatch.defaults (fun harness -> async {
                    let! status, text = post harness "/BatchedApi/Echo" "[\"alone\"]"
                    Expect.equal status HttpStatusCode.OK "an ordinary call is served"
                    Expect.equal text "\"echo:alone\"" "unchanged by the batch middleware"
                })
        }
    ]