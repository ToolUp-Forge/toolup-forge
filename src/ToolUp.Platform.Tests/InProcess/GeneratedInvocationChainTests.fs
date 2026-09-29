// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.GeneratedInvocationChainTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Text
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Platform
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe
open ToolUp.Remoting.Generator

// ─── Phase 906 — a generated method runs INSIDE the pre-flight chain ────
//
// Replaces Phase 856.A's tripwire. The generator now emits a
// `GeneratedInvocationTable` per API record, and the remoting proxy
// composes it where it would otherwise have built reflective endpoints.
// The claim this file pins is that doing so changes NOTHING a caller or an
// operator can observe except the cost: every pre-flight stage the Giraffe
// adapter runs around a reflective method runs around a generated one —
// authorisation, rate limit, idempotency replay, validation, audit — and
// the handler is reached through the generated call only when every stage
// passes.
//
// Two records of identical shape, served on identical routes (the route
// builder drops the record name): `GeneratedChainApi` has a generated
// table registered, `ReflectiveChainApi` does not. Every scenario runs
// against both and compares what the caller sees, so the reflective twin
// IS the specification. The generated table is built with the same public
// builders the generator's dispatch emission writes (the last test checks
// the emission for this record against it), and each entry is wrapped to
// count its calls, which is what proves the generated route — not the
// reflective fallback — served a covered method.

type ChainInput = {
    [<MinLength 3>]
    Name: string
    Count: int
}

type GeneratedChainApi = {
    [<RequiresRole "Admin">]
    AdminOnly: unit -> Async<int>
    [<AllowAnonymous>]
    [<RateLimit(2, RateLimitWindow.perMinute)>]
    Capped: unit -> Async<int>
    [<AllowAnonymous>]
    [<Idempotent>]
    [<Audit "PolicyChanged">]
    Record: ChainInput -> Async<string>
    [<AllowAnonymous>]
    Create: ChainInput -> Async<string>
    /// Deliberately absent from the generated table: served by the
    /// reflective fallback the proxy builds on first use.
    [<AllowAnonymous>]
    Uncovered: int -> Async<int>
}

type ReflectiveChainApi = {
    [<RequiresRole "Admin">]
    AdminOnly: unit -> Async<int>
    [<AllowAnonymous>]
    [<RateLimit(2, RateLimitWindow.perMinute)>]
    Capped: unit -> Async<int>
    [<AllowAnonymous>]
    [<Idempotent>]
    [<Audit "PolicyChanged">]
    Record: ChainInput -> Async<string>
    [<AllowAnonymous>]
    Create: ChainInput -> Async<string>
    [<AllowAnonymous>]
    Uncovered: int -> Async<int>
}

/// What a handler saw, per method, on one host.
type private Handlers() =
    let counts = ConcurrentDictionary<string, int>()

    member _.Hit(name: string) =
        counts.AddOrUpdate(name, 1, (fun _ n -> n + 1)) |> ignore

    member _.Count(name: string) =
        match counts.TryGetValue name with
        | true, n -> n
        | _ -> 0

let private generatedImpl (handlers: Handlers) : GeneratedChainApi = {
    AdminOnly =
        fun () -> async {
            handlers.Hit "AdminOnly"
            return 42
        }
    Capped =
        fun () -> async {
            handlers.Hit "Capped"
            return 7
        }
    Record =
        fun input -> async {
            handlers.Hit "Record"
            return "recorded:" + input.Name
        }
    Create =
        fun input -> async {
            handlers.Hit "Create"
            return sprintf "%s:%d" input.Name input.Count
        }
    Uncovered =
        fun n -> async {
            handlers.Hit "Uncovered"
            return n + 1
        }
}

let private reflectiveImpl (handlers: Handlers) : ReflectiveChainApi = {
    AdminOnly =
        fun () -> async {
            handlers.Hit "AdminOnly"
            return 42
        }
    Capped =
        fun () -> async {
            handlers.Hit "Capped"
            return 7
        }
    Record =
        fun input -> async {
            handlers.Hit "Record"
            return "recorded:" + input.Name
        }
    Create =
        fun input -> async {
            handlers.Hit "Create"
            return sprintf "%s:%d" input.Name input.Count
        }
    Uncovered =
        fun n -> async {
            handlers.Hit "Uncovered"
            return n + 1
        }
}

/// Every call the generated table made, per method — process-wide, so each
/// test reads a delta.
let private generatedCalls = ConcurrentDictionary<string, int>()

let private generatedCallCount (name: string) =
    match generatedCalls.TryGetValue name with
    | true, n -> n
    | _ -> 0

/// The table exactly as the generator's dispatch emission writes it for
/// `GeneratedChainApi` (less `Uncovered`, left to the reflective fallback).
let private emittedShape: GeneratedInvocationTable<GeneratedChainApi> =
    GeneratedInvocation.table [
        GeneratedInvocation.forMethod<GeneratedChainApi>
            "AdminOnly"
            [| typeof<unit>; typeof<Async<int>> |]
            (fun (args: GeneratedArguments) (api: GeneratedChainApi) ->
                let a0 = args.Next<unit>()
                args.Complete(api.AdminOnly a0))
        GeneratedInvocation.forMethod<GeneratedChainApi>
            "Capped"
            [| typeof<unit>; typeof<Async<int>> |]
            (fun (args: GeneratedArguments) (api: GeneratedChainApi) ->
                let a0 = args.Next<unit>()
                args.Complete(api.Capped a0))
        GeneratedInvocation.forMethodWithFirst<GeneratedChainApi, ChainInput>
            "Record"
            [| typeof<ChainInput>; typeof<Async<string>> |]
            (fun (args: GeneratedArguments) (api: GeneratedChainApi) ->
                let a0 = args.Next<ChainInput>()
                args.Complete(api.Record a0))
        GeneratedInvocation.forMethodWithFirst<GeneratedChainApi, ChainInput>
            "Create"
            [| typeof<ChainInput>; typeof<Async<string>> |]
            (fun (args: GeneratedArguments) (api: GeneratedChainApi) ->
                let a0 = args.Next<ChainInput>()
                args.Complete(api.Create a0))
    ]

/// The same table, each call counted (the internal constructor, reached
/// through InternalsVisibleTo — a consumer never builds one by hand).
let private countedTable: GeneratedInvocationTable<GeneratedChainApi> =
    GeneratedInvocation.table (
        emittedShape.Methods
        |> List.map (fun m ->
            GeneratedMethod<GeneratedChainApi>(
                m.Name,
                m.FlattenedTypes,
                m.DecodeFirst,
                fun args api ->
                    generatedCalls.AddOrUpdate(m.Name, 1, (fun _ n -> n + 1)) |> ignore
                    m.Call args api
            ))
    )

/// A caller who is signed in and holds no role: `AdminOnly` refuses it.
let private member': IAuthContext =
    { new IAuthContext with
        member _.HasRole _ = false
        member _.HasClaim(_, _) = false
        member _.HasTenant() = false
        member _.IsAnonymous() = false
        member _.SubjectId = "member"
    }

type private Audited() =
    let events = ResizeArray<AuditEvent>()
    member _.Events = lock events (fun () -> List.ofSeq events)

    interface IAuditEmitter with
        member _.Emit event = async { lock events (fun () -> events.Add event) }

let private compose (audited: Audited) (options: RemotingOptions<_, 'impl>) =
    options
    |> Remoting.withRouteBuilder (fun _ methodName -> "/api/" + methodName)
    |> Remoting.withAuthContext (fun _ -> async { return member' })
    |> Remoting.withRateLimitStore (InMemoryRateLimitStore())
    |> Remoting.withAudit audited
    |> Remoting.withIdempotencyStore (InMemoryIdempotencyStore())
    |> Remoting.buildHttpHandler

type private Harness = {
    Client: HttpClient
    Host: IHost
    Handlers: Handlers
    Audited: Audited
}

let private start (handler: HttpHandler) (handlers: Handlers) (audited: Audited) = async {
    let host =
        Host
            .CreateDefaultBuilder()
            .ConfigureWebHostDefaults(fun webHost ->
                webHost.UseTestServer().Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe handler)
                |> ignore)
            .Build()

    do! host.StartAsync() |> Async.AwaitTask

    return {
        Client = host.GetTestClient()
        Host = host
        Handlers = handlers
        Audited = audited
    }
}

/// The GENERATED twin. The table is registered before the handler is
/// built: the proxy reads the registry at build time.
let private startGenerated () =
    GeneratedInvocation.register countedTable
    let handlers = Handlers()
    let audited = Audited()

    let handler =
        Remoting.createApi ()
        |> Remoting.fromValue (generatedImpl handlers)
        |> compose audited

    start handler handlers audited

let private startReflective () =
    let handlers = Handlers()
    let audited = Audited()

    let handler =
        Remoting.createApi ()
        |> Remoting.fromValue (reflectiveImpl handlers)
        |> compose audited

    start handler handlers audited

let private stop (h: Harness) = async {
    h.Client.Dispose()
    do! h.Host.StopAsync() |> Async.AwaitTask
    h.Host.Dispose()
}

/// What a caller observes of one response.
type private Seen = {
    Status: int
    Body: string
    Replay: bool
}

let private call (h: Harness) (methodName: string) (body: string) (idempotencyKey: string option) = async {
    use req = new HttpRequestMessage(HttpMethod.Post, "/api/" + methodName)
    req.Headers.Add("x-remoting-proxy", "true")

    idempotencyKey
    |> Option.iter (fun key -> req.Headers.Add("X-Idempotency-Key", key))

    req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
    let! resp = h.Client.SendAsync req |> Async.AwaitTask
    let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

    let replay =
        match resp.Headers.TryGetValues "x-idempotency-replay" with
        | true, values -> values |> Seq.contains "true"
        | _ -> false

    return {
        Status = int resp.StatusCode
        Body = text
        Replay = replay
    }
}

/// Run one scenario against both twins; return what each saw.
let private onBoth (scenario: Harness -> Async<'a>) = async {
    let! generated = startGenerated ()
    let! reflective = startReflective ()

    try
        let! g = scenario generated
        let! r = scenario reflective
        return generated, g, reflective, r
    finally
        stop generated |> Async.RunSynchronously
        stop reflective |> Async.RunSynchronously
}

[<Tests>]
let tests =
    testSequencedGroup "Phase 906 — generated invocation (process-wide registry)"
    <| testList "Phase 906 — a generated method runs inside the pre-flight chain" [

        testAsync "a covered method is served by its generated invocation, with the reflective twin's response" {
            let before = generatedCallCount "Create"

            let! g, gSeen, r, rSeen = onBoth (fun h -> call h "Create" """[{"Name":"CPG","Count":3}]""" None)

            Expect.equal gSeen.Status 200 "the generated route succeeds"
            Expect.equal gSeen rSeen "the generated route answers exactly as the reflective one"
            Expect.stringContains gSeen.Body "CPG:3" "the handler got the decoded value"
            Expect.equal (generatedCallCount "Create" - before) 1 "the GENERATED call served it"
            Expect.equal (g.Handlers.Count "Create") 1 "the handler ran once"
            Expect.equal (r.Handlers.Count "Create") 1 "the reflective twin's handler ran once"
        }

        testAsync "auth: a generated method is refused exactly as its reflective twin, before the call" {
            let before = generatedCallCount "AdminOnly"

            let! g, gSeen, _, rSeen = onBoth (fun h -> call h "AdminOnly" "" None)

            Expect.isTrue (gSeen.Status = 401 || gSeen.Status = 403) "a caller without the role is refused"
            Expect.equal gSeen rSeen "status and body identical to the reflective twin's refusal"
            Expect.equal (generatedCallCount "AdminOnly" - before) 0 "the generated call never ran"
            Expect.equal (g.Handlers.Count "AdminOnly") 0 "the handler never ran"
        }

        testAsync "rate limit: the budget is charged and enforced around a generated method" {
            let before = generatedCallCount "Capped"

            let three (h: Harness) = async {
                let! a = call h "Capped" "" None
                let! b = call h "Capped" "" None
                let! c = call h "Capped" "" None
                return [ a.Status; b.Status; c.Status ]
            }

            let! g, gSeen, _, rSeen = onBoth three

            Expect.equal gSeen [ 200; 200; 429 ] "two within budget, the third refused"
            Expect.equal gSeen rSeen "as the reflective twin"
            Expect.equal (generatedCallCount "Capped" - before) 2 "the refused call never reached the generated call"
            Expect.equal (g.Handlers.Count "Capped") 2 "nor the handler"
        }

        testAsync "idempotency replay and audit: the replay skips the generated call and audits once" {
            let before = generatedCallCount "Record"

            let twice (h: Harness) = async {
                let! first = call h "Record" """[{"Name":"CPG","Count":1}]""" (Some "key-906")
                let! second = call h "Record" """[{"Name":"CPG","Count":1}]""" (Some "key-906")
                return first, second
            }

            let! g, (gFirst, gSecond), r, (rFirst, rSecond) = onBoth twice

            Expect.equal (gFirst.Status, gSecond.Status) (200, 200) "both calls succeed"
            Expect.isFalse gFirst.Replay "the first call is fresh"
            Expect.isTrue gSecond.Replay "the second is served from the idempotency store"
            Expect.equal (gFirst, gSecond) (rFirst, rSecond) "as the reflective twin"
            Expect.equal (generatedCallCount "Record" - before) 1 "the replay never reached the generated call"
            Expect.equal (g.Handlers.Count "Record") 1 "the handler ran once"

            let kinds (events: AuditEvent list) =
                events |> List.map (fun e -> e.Kind, e.MethodName) |> List.sort

            Expect.equal
                (kinds g.Audited.Events)
                (List.sort [ AuditKind.PolicyChanged, "Record"; AuditKind.IdempotencyReplay, "Record" ])
                "the generated method's own audit event, then the replay's"

            Expect.equal
                (kinds g.Audited.Events)
                (kinds r.Audited.Events)
                "the method's own audit event once, and one IdempotencyReplay — as the reflective twin"

            Expect.equal
                (g.Audited.Events
                 |> List.filter (fun e -> e.Kind = AuditKind.PolicyChanged)
                 |> List.length)
                1
                "the generated method is audited once; the replay does not double-audit"
        }

        testAsync "validation: an invalid argument is refused before the generated call, as the reflective twin" {
            let before = generatedCallCount "Create"

            let! g, gSeen, _, rSeen = onBoth (fun h -> call h "Create" """[{"Name":"ab","Count":3}]""" None)

            Expect.equal gSeen.Status 400 "MinLength 3 refuses a two-character name"
            Expect.stringContains gSeen.Body "MinLength" "the refusal names the violated rule"
            Expect.equal gSeen rSeen "as the reflective twin"
            Expect.equal (generatedCallCount "Create" - before) 0 "the generated call never ran"
            Expect.equal (g.Handlers.Count "Create") 0 "nor the handler"
        }

        testAsync "a decode refusal is the proxy's own, identical on both routes" {
            let! g, gSeen, _, rSeen = onBoth (fun h -> call h "Create" """[{"Name":"CPG","Count":"three"}]""" None)

            Expect.equal gSeen.Status 400 "an undecodable argument is a caller-side refusal"
            Expect.equal gSeen rSeen "same status, same refusal body, as the reflective twin"
            Expect.equal (g.Handlers.Count "Create") 0 "the handler never ran"
        }

        testAsync "a surplus argument is refused as the reflective twin refuses it" {
            let! _, gSeen, _, rSeen = onBoth (fun h -> call h "Create" """[{"Name":"CPG","Count":3},1]""" None)

            Expect.equal gSeen.Status 400 "arity is a decode refusal"
            Expect.equal gSeen rSeen "as the reflective twin"
        }

        testAsync "a method the table does not cover is served by the reflective fallback" {
            let! g, gSeen, _, rSeen = onBoth (fun h -> call h "Uncovered" "[41]" None)

            Expect.equal gSeen.Status 200 "the uncovered method still dispatches"
            Expect.equal gSeen rSeen "as the reflective twin"
            Expect.equal (generatedCallCount "Uncovered") 0 "nothing generated served it"
            Expect.equal (g.Handlers.Count "Uncovered") 1 "the handler ran once"
        }

        test "the table above is the shape the generator emits for this record" {
            let source =
                Dispatch.compilationUnit
                    "ToolUp.Platform.Tests.Generated"
                    []
                    (Dispatch.tableFor typeof<GeneratedChainApi>)

            let record = typeof<GeneratedChainApi>.FullName.Replace('+', '.')

            for methodName in [ "AdminOnly"; "Capped" ] do
                Expect.stringContains
                    source
                    (sprintf
                        "ToolUp.Remoting.Server.GeneratedInvocation.forMethod<%s>\n                \"%s\"\n                [| typeof<unit>; typeof<Async<int>> |]"
                        record
                        methodName)
                    (sprintf "%s: a unit-first method is emitted with forMethod and its flattened type" methodName)

            for methodName in [ "Record"; "Create" ] do
                Expect.stringContains
                    source
                    (sprintf "ToolUp.Remoting.Server.GeneratedInvocation.forMethodWithFirst<%s, " record)
                    "a method with a JSON first argument is emitted with forMethodWithFirst"

                Expect.stringContains
                    source
                    (sprintf "args.Complete(api.%s a0))" methodName)
                    (sprintf "%s: the call takes its argument through the proxy's step" methodName)

            Expect.stringContains
                source
                "ToolUp.Remoting.Server.GeneratedInvocation.register invocations"
                "the module registers its table"
        }

        test "the builders refuse a table whose first-argument declaration contradicts its field type" {
            Expect.throws
                (fun () ->
                    GeneratedInvocation.forMethod<GeneratedChainApi>
                        "Create"
                        [| typeof<ChainInput>; typeof<Async<string>> |]
                        (fun _ _ -> failwith "never called")
                    |> ignore)
                "a JSON first argument declared as none would skip the validation stage's early decode"

            Expect.throws
                (fun () ->
                    GeneratedInvocation.forMethodWithFirst<GeneratedChainApi, int>
                        "Create"
                        [| typeof<ChainInput>; typeof<Async<string>> |]
                        (fun _ _ -> failwith "never called")
                    |> ignore)
                "a first-argument type that is not the field's first domain is refused"
        }
    ]