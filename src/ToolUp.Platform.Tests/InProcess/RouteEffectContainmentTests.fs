// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 773 — declared-route effect containment.
///
/// The claim the phase makes is narrow, and each half is probed where it
/// can fail:
///
///   • a route that declared `Pure` and invokes a host capability through
///     the shipped registry is refused by the Phase 300 gate — the
///     capability never runs, and the refusal reaches the audit path;
///   • the SAME route undeclared behaves exactly as before — same handler
///     object, the capability runs, nothing is audited;
///   • a declared egress grant permits exactly its origins, and a `Pure`
///     envelope permits none;
///   • the declaration is visible on the module's surface.
///
/// The egress probes touch the process-wide enforcement state, so they run
/// under `testSequenced` and restore the permit-all default in `finally`.
module ToolUp.Platform.Tests.InProcess.RouteEffectContainmentTests

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform

// ── fixtures ──────────────────────────────────────────────────────────

type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    let gate = obj ()
    member _.Events = lock gate (fun () -> List.ofSeq recorded)

    /// The default observer writes fire-and-forget, so a probe asserting
    /// on rows waits for them; capped, and loud on timeout.
    member _.WaitFor(count: int) =
        let cap = TimeSpan.FromSeconds 30.0
        let sw = Diagnostics.Stopwatch.StartNew()

        lock gate (fun () ->
            while recorded.Count < count && sw.Elapsed < cap do
                Threading.Monitor.Wait(gate, cap - sw.Elapsed) |> ignore

            if recorded.Count < count then
                failtestf "audit wait: %d of %d expected row(s) arrived" recorded.Count count)

    interface IAuditLog with
        member _.Record(scope, audit) = async {
            lock gate (fun () ->
                recorded.Add((scope, audit))
                Threading.Monitor.PulseAll gate)
        }

        member _.GetAuditTrail(_, _, _) = async { return lock gate (fun () -> recorded |> Seq.map snd |> List.ofSeq) }

/// A primary handler that never opens a socket and records what reached it.
type private RecordingHandler() =
    inherit HttpMessageHandler()
    let seen = ResizeArray<Uri>()
    member _.Seen = lock seen (fun () -> List.ofSeq seen)

    override _.SendAsync(request, _cancellationToken) =
        lock seen (fun () -> seen.Add request.RequestUri)
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))

let private reporter = ComponentId.ofModule "reporter"

let private Route = "/api/reporter/run"

let private ctxOf: AccessContext = {
    UserId = "alice"
    TeamId = Some "team-1"
    Subject = TeamMember("alice", "team-1")
    ModulePermissions = Map.empty
    TokenRoles = []
    Admission = ToolUp.Platform.Auth.PrincipalAdmission.Member
    ModuleExposure = Map.empty
    PlatformRole = None
}

let private effectingEnvelope =
    CompanionCapability.identity |> CompanionCapability.withEffect Effecting

/// A registry with the clipboard built-in registered, counting how often
/// the capability actually ran.
let private registryWithCounter () =
    let ran = ref 0
    let registry = HostCapabilityRegistry.create ActionAuthorizer.allowAll

    registry
    |> HostCapability.registerClipboardWrite (fun _ -> async { ran.Value <- ran.Value + 1 })

    registry, ran

/// A module whose one handler invokes the clipboard capability and records
/// the outcome on the request.
let private invokingModule (registry: IHostCapabilityRegistry) : ServerModule =
    let handler: Giraffe.Core.HttpHandler =
        fun next ctx -> task {
            let! outcome =
                registry.Invoke HostCapability.ClipboardWrite (Map [ HostCapability.ClipboardTextArg, "x" ]) ctxOf
                |> Async.StartAsTask

            ctx.Items["outcome"] <- box outcome
            ctx.Items["routeInForce"] <- box (RouteEffectContext.current () |> Option.map _.Route)
            return! next ctx
        }

    ServerModule.create "reporter" |> ServerModule.withHandlers [ handler ]

let private requestTo (services: IServiceProvider) (httpMethod: string) (path: string) : DefaultHttpContext =
    let ctx = DefaultHttpContext()
    ctx.Request.Method <- httpMethod
    ctx.Request.Path <- PathString path
    ctx.RequestServices <- services
    ctx

/// The services the composed app would serve with: `configure` first (what
/// a composition root registers), then the app's own `ServiceConfig`.
let private servicesFor (app: ServerApp) (configure: IServiceCollection -> IServiceCollection) : IServiceProvider =
    let configured = configure (ServiceCollection())

    let composed =
        match app.Extensions.ServiceConfig with
        | Some register -> register configured
        | None -> configured

    composed.BuildServiceProvider() :> IServiceProvider

let private run (handler: Giraffe.Core.HttpHandler) (ctx: HttpContext) =
    handler (fun c -> Task.FromResult(Some c)) ctx
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> ignore

let private outcomeOf (ctx: HttpContext) =
    ctx.Items["outcome"] :?> HostCapabilityOutcome

let private refusedRows (audit: RecordingAuditLog) =
    audit.Events
    |> List.choose (fun (_, event) ->
        match event with
        | AuditEvent.CompositionCapabilityRefused p -> Some p
        | _ -> None)

// ── the effect axis ───────────────────────────────────────────────────

let private effectAxis =
    testList "effect axis" [

        testCase "a Pure route invoking an effecting capability is refused by the Phase 300 gate, with an audit row"
        <| fun _ ->
            let registry, ran = registryWithCounter ()
            let audit = RecordingAuditLog()

            let app =
                ServerApp.empty
                |> ServerApp.addModule (
                    invokingModule registry
                    |> ServerModule.withRouteEffect
                        "GET"
                        Route
                        (RouteEffectDeclaration.envelope CompanionCapability.pure')
                )

            let ctx =
                requestTo (servicesFor app (fun s -> s.AddSingleton<IAuditLog>(audit))) "GET" Route

            run (List.exactlyOne app.Handlers) ctx

            match outcomeOf ctx with
            | HostCapabilityOutcome.Denied reason ->
                Expect.stringContains reason "route GET /api/reporter/run" "the refusal names the route"
                Expect.stringContains reason "composition capability sandbox" "the refusal is the Phase 300 denial"
            | other -> failtestf "a Pure route must be refused the invocation, got %A" other

            Expect.equal ran.Value 0 "the capability must never run"
            audit.WaitFor 1

            match refusedRows audit with
            | [ row ] ->
                Expect.equal row.Component (ComponentId.value reporter) "the row names the owning module"
                Expect.stringStarts row.Required "effecting" "an invocation is recorded as an effecting requirement"
                Expect.stringStarts row.Declared "pure" "the row carries the route's declared envelope"
            | other -> failtestf "expected exactly one CompositionCapabilityRefused row, got %A" other

        testCase "the same route undeclared behaves as today"
        <| fun _ ->
            let registry, ran = registryWithCounter ()
            let audit = RecordingAuditLog()
            let m = invokingModule registry
            let app = ServerApp.empty |> ServerApp.addModule m

            let ctx =
                requestTo (servicesFor app (fun s -> s.AddSingleton<IAuditLog>(audit))) "GET" Route

            run (List.exactlyOne app.Handlers) ctx

            match outcomeOf ctx with
            | HostCapabilityOutcome.Completed _ -> ()
            | other -> failtestf "an undeclared route must invoke as before, got %A" other

            Expect.equal ran.Value 1 "the capability ran"
            Expect.isEmpty audit.Events "nothing is audited for an undeclared route"
            Expect.equal (ctx.Items["routeInForce"]) (box (None: string option)) "no route context is in force"

        testCase "a module declaring no route effect has its handler list returned unchanged"
        <| fun _ ->
            // Built through a call, not a literal: the optimiser may inline a
            // let-bound list literal at each use, which would make two lists.
            let handlers: Giraffe.Core.HttpHandler list =
                List.init 1 (fun _ -> fun next ctx -> next ctx)

            Expect.isTrue
                (obj.ReferenceEquals(RouteEffectContainment.containHandlers reporter [] handlers, handlers))
                "no declaration must mean no wrapper at all"

            let neither = {
                EffectEnvelope = None
                EgressGrant = None
            }

            Expect.isTrue
                (obj.ReferenceEquals(
                    RouteEffectContainment.containHandlers reporter [ ("GET", Route), neither ] handlers,
                    handlers
                ))
                "a declaration of neither half is not a declaration"

        testCase "an undeclared route of a declared module runs untouched"
        <| fun _ ->
            let registry, ran = registryWithCounter ()

            let app =
                ServerApp.empty
                |> ServerApp.addModule (
                    invokingModule registry
                    |> ServerModule.withRouteEffect
                        "GET"
                        Route
                        (RouteEffectDeclaration.envelope CompanionCapability.pure')
                )

            let ctx = requestTo (servicesFor app id) "POST" Route
            run (List.exactlyOne app.Handlers) ctx

            match outcomeOf ctx with
            | HostCapabilityOutcome.Completed _ -> ()
            | other -> failtestf "another method on the same path is not the declared route, got %A" other

            Expect.equal ran.Value 1 "the capability ran"

        testCase "an effecting envelope admits the invocation, matched case-insensitively"
        <| fun _ ->
            let registry, ran = registryWithCounter ()

            let app =
                ServerApp.empty
                |> ServerApp.addModule (
                    invokingModule registry
                    |> ServerModule.withRouteEffect
                        "get"
                        "/API/Reporter/Run"
                        (RouteEffectDeclaration.envelope effectingEnvelope)
                )

            let ctx = requestTo (servicesFor app id) "GET" Route
            run (List.exactlyOne app.Handlers) ctx

            match outcomeOf ctx with
            | HostCapabilityOutcome.Completed _ -> ()
            | other -> failtestf "an effecting envelope permits an invocation, got %A" other

            Expect.equal ran.Value 1 "the capability ran"

            Expect.equal
                (ctx.Items["routeInForce"])
                (box (Some "GET /api/reporter/run"))
                "the route context is in force while the handler runs"

        testCase "a registered RouteEffectObserver receives the refusal instead of the default audit"
        <| fun _ ->
            let registry, _ = registryWithCounter ()
            let audit = RecordingAuditLog()
            let observed = ResizeArray<CapabilityDenial>()

            let app =
                ServerApp.empty
                |> ServerApp.addModule (
                    invokingModule registry
                    |> ServerModule.withRouteEffect
                        "GET"
                        Route
                        (RouteEffectDeclaration.envelope CompanionCapability.pure')
                )

            let services =
                servicesFor app (fun s ->
                    s
                        .AddSingleton<IAuditLog>(audit)
                        .AddSingleton<RouteEffectObserver>(RouteEffectObserver(observed.Add)))

            run (List.exactlyOne app.Handlers) (requestTo services "GET" Route)

            Expect.equal observed.Count 1 "the composition's observer saw the refusal"
            Expect.equal observed[0].Component reporter "the denial names the owning module"
            Expect.isEmpty audit.Events "the default observer is not also invoked"

        testCase "the route context ends with the handler"
        <| fun _ ->
            let registry, ran = registryWithCounter ()

            let app =
                ServerApp.empty
                |> ServerApp.addModule (
                    invokingModule registry
                    |> ServerModule.withRouteEffect
                        "GET"
                        Route
                        (RouteEffectDeclaration.envelope CompanionCapability.pure')
                )

            run (List.exactlyOne app.Handlers) (requestTo (servicesFor app id) "GET" Route)

            Expect.isNone (RouteEffectContext.current ()) "no route context outlives the request"
            Expect.isNone (EgressComponentContext.tryCurrent ()) "the owner's egress claim is released"
            Expect.isNone (EgressRouteContext.tryCurrent ()) "the route's egress grant is released"

            match
                registry.Invoke HostCapability.ClipboardWrite Map.empty ctxOf
                |> Async.RunSynchronously
            with
            | HostCapabilityOutcome.Completed _ -> Expect.equal ran.Value 1 "outside a route the registry is unbounded"
            | other -> failtestf "outside any route an invocation is not contained, got %A" other
    ]

// ── the egress axis ───────────────────────────────────────────────────

/// A module whose one handler sends to each of `urls` through a platform
/// client and records, per URL, whether the inner handler was reached.
let private sendingModule (client: HttpClient) (urls: string list) : ServerModule =
    let handler: Giraffe.Core.HttpHandler =
        fun next ctx -> task {
            for url in urls do
                let! sent = task {
                    try
                        let! _ = client.GetAsync(url: string)
                        return "sent"
                    with
                    | :? EgressDeniedException as denied -> return "denied: " + denied.Reason
                    | :? AggregateException as a when (a.InnerException :? EgressDeniedException) ->
                        return "denied: " + a.InnerException.Message
                }

                ctx.Items[url] <- box sent

            return! next ctx
        }

    ServerModule.create "reporter" |> ServerModule.withHandlers [ handler ]

let private isolated (body: unit -> unit) =
    EgressEnforcement.reset ()

    try
        body ()
    finally
        EgressEnforcement.reset ()

let private allowed = "https://api.example.com/v1/data"
let private other = "https://other.example.com/v1/data"

let private egressAxis =
    testSequenced
    <| testList "egress axis" [

        testCase "a declared egress grant permits exactly its origins"
        <| fun _ ->
            isolated (fun () ->
                let audit = RecordingAuditLog()
                EgressEnforcement.bindAudit audit
                let inner = new RecordingHandler()
                use client = PlatformHttpClient.createWith EgressSurface.ModuleHandler inner

                let app =
                    ServerApp.empty
                    |> ServerApp.addModule (
                        sendingModule client [ allowed; other ]
                        |> ServerModule.withRouteEffect
                            "POST"
                            Route
                            (RouteEffectDeclaration.egress (EgressGrant.ofOrigins [ "https://api.example.com" ]))
                    )

                let ctx = requestTo (servicesFor app id) "POST" Route
                run (List.exactlyOne app.Handlers) ctx

                Expect.equal (ctx.Items[allowed]) (box "sent") "the declared origin is reached"

                let refusal = string ctx.Items[other]
                Expect.stringStarts refusal "denied: " "an undeclared origin is refused"
                Expect.stringContains refusal "route POST /api/reporter/run" "the refusal names the route"
                Expect.stringContains refusal "component module:reporter" "the call is attributed to the owner"

                Expect.equal
                    (inner.Seen |> List.map _.Host)
                    [ "api.example.com" ]
                    "only the declared origin ever reached the socket side"

                Expect.equal (EgressEnforcement.denialCount ()) 1 "the refusal is ledgered"

                match
                    audit.Events
                    |> List.choose (fun (_, e) ->
                        match e with
                        | EgressDenied p -> Some p
                        | _ -> None)
                with
                | [ row ] ->
                    Expect.equal row.Origin "https://other.example.com" "the audit row carries the origin only"
                | rows -> failtestf "expected one EgressDenied row, got %A" rows)

        testCase "a Pure envelope permits no outbound call"
        <| fun _ ->
            isolated (fun () ->
                let inner = new RecordingHandler()
                use client = PlatformHttpClient.createWith EgressSurface.ModuleHandler inner

                let app =
                    ServerApp.empty
                    |> ServerApp.addModule (
                        sendingModule client [ allowed ]
                        |> ServerModule.withRouteEffect "POST" Route {
                            EffectEnvelope = Some CompanionCapability.pure'
                            EgressGrant = Some(EgressGrant.ofOrigins [ "https://api.example.com" ])
                        }
                    )

                let ctx = requestTo (servicesFor app id) "POST" Route
                run (List.exactlyOne app.Handlers) ctx

                Expect.stringStarts (string ctx.Items[allowed]) "denied: " "Pure is the narrower declaration"
                Expect.isEmpty inner.Seen "nothing reached the socket side")

        testCase "the same route undeclared reaches any origin, as today"
        <| fun _ ->
            isolated (fun () ->
                let inner = new RecordingHandler()
                use client = PlatformHttpClient.createWith EgressSurface.ModuleHandler inner

                let app =
                    ServerApp.empty |> ServerApp.addModule (sendingModule client [ allowed; other ])

                let ctx = requestTo (servicesFor app id) "POST" Route
                run (List.exactlyOne app.Handlers) ctx

                Expect.equal (ctx.Items[allowed]) (box "sent") "permit-all is unchanged"
                Expect.equal (ctx.Items[other]) (box "sent") "permit-all is unchanged"
                Expect.equal (EgressEnforcement.denialCount ()) 0 "nothing was refused")
    ]

// ── the module surface ────────────────────────────────────────────────

let private surface =
    testList "module surface" [

        testCase "a declared route is reported as a route-effect entry, and the handler note says so"
        <| fun _ ->
            let described =
                ServerModule.create "reporter"
                |> ServerModule.withRouteEffect "get" Route (RouteEffectDeclaration.envelope CompanionCapability.pure')
                |> ServerModule.withRouteEffect
                    "POST"
                    "/api/reporter/send"
                    (RouteEffectDeclaration.egress (EgressGrant.ofOrigins [ "https://api.example.com" ]))
                |> ModuleSurface.describe

            let entries =
                described.Provides
                |> List.filter (fun e -> e.Kind = "route-effect")
                |> List.map (fun e -> e.Key, e.Label)

            Expect.equal
                entries
                [
                    "GET /api/reporter/run", "envelope=pure/deterministic/distributed-ready; egress=declared{}"
                    "POST /api/reporter/send", "envelope=undeclared; egress=declared{https://api.example.com}"
                ]
                "each declared route is an entry carrying what it is held to"

            let handlerNote = described.Opaque |> List.find (fun o -> o.Kind = "http-handler")

            Expect.stringContains handlerNote.Reason "2 route(s) declare their effects" "the opaque note counts them"

        testCase "a module declaring nothing reports no route-effect entry and the note it always had"
        <| fun _ ->
            let described = ServerModule.create "reporter" |> ModuleSurface.describe

            Expect.isFalse
                (described.Provides |> List.exists (fun e -> e.Kind = "route-effect"))
                "no declaration, no entry"

            let handlerNote = described.Opaque |> List.find (fun o -> o.Kind = "http-handler")

            Expect.isFalse (handlerNote.Reason.Contains "declare their effects") "the note is unchanged"
    ]

[<Tests>]
let tests =
    testList "Phase 773 — declared-route effect containment" [ effectAxis; egressAxis; surface ]