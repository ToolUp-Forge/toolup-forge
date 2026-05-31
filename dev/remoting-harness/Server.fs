module ToolUp.Remoting.Harness.Server

open System
open System.IO
open System.Text
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe
open Giraffe
open ToolUp.Remoting.Harness.Shared

// ---- Handler implementations -------------------------------------------------

/// Phase 69b.E — distinguished exception types the error handler maps to
/// `ErrorCategory.User` so the wire envelope carries a category field.
exception UserError of string

let private handlers = {
    Echo = fun input -> async { return input }
    Heartbeat = fun () -> async { return DateTimeOffset.UtcNow }
    Boom = fun reason -> async { return failwithf "Boom requested: %s" reason }
    BoomCategorised = fun reason -> async { return raise (UserError(sprintf "User-fault: %s" reason)) }
}

// ---- Error-handler ----------------------------------------------------------
//
// Phase 69b will introduce categorised envelopes (`RemotingError.User`,
// `.System`, etc.). For v0 we use the upstream `ErrorResult.Propagate`
// shape so the harness has a stable baseline to assert against; the
// Phase 69b test additions will swap to the categorised form.

let private errorHandler (ex: exn) (routeInfo: RouteInfo<HttpContext>) : ErrorResult =
    // Phase 69b.E — map domain-distinguished exceptions to categorised
    // envelopes; everything else falls through to the legacy uncategorised
    // path (which forge consumers can adopt incrementally).
    match ex with
    | UserError msg -> PropagateCategorised(ErrorCategory.User, box (sprintf "%s: %s" routeInfo.methodName msg))
    | _ -> Propagate(box (sprintf "%s: %s" routeInfo.methodName ex.Message))

// ---- Phase 69b.C — recording telemetry sink for the harness ----------------
//
// In-memory sink that captures every MethodTelemetry event for assertion.
// One sink is created per test (via `RecordingTelemetry.create`) so tests
// don't leak state through a shared mutable.

type RecordingTelemetry() =
    let events = System.Collections.Concurrent.ConcurrentBag<MethodTelemetry>()
    member _.Events = events |> Seq.toList
    interface IRemotingTelemetry with
        member _.OnMethodCompleted t = events.Add t

module RecordingTelemetry =
    let create () = RecordingTelemetry()

// ---- Forge-shaped Api.make wrapper ------------------------------------------
//
// Mirrors `Api.make` at
// toolup-forge/src/ToolUp.Platform.Server/Server/Api.fs:26 so the
// harness's composition path matches what forge SDK consumers exercise.

let private buildHarnessApi (telemetry: IRemotingTelemetry option) (api: HttpContext -> IHarnessApi) : HttpHandler =
    let withMaybeTelemetry options =
        match telemetry with
        | Some sink -> Remoting.withTelemetry sink options
        | None -> options
    Remoting.createApi ()
    |> Remoting.withRouteBuilder routeBuilder
    |> Remoting.fromContext api
    |> Remoting.withErrorHandler errorHandler
    |> withMaybeTelemetry
    |> Remoting.buildHttpHandler

// ---- Phase 69b.B — per-request async-resolver wrapper -----------------------
//
// Demonstrates `Remoting.fromContextAsync`: the resolver runs per request
// (not snapshotted at composition time), so per-request inputs feed into
// the API impl without each handler having to re-read them.

let private resolveSubject (ctx: HttpContext) : Async<string> =
    async {
        let mutable values : Microsoft.Extensions.Primitives.StringValues =
            Microsoft.Extensions.Primitives.StringValues.Empty
        if ctx.Request.Headers.TryGetValue("X-Subject", &values) then
            return values.ToString()
        else
            return "anonymous"
    }

// ---- Phase 69d — IAuthContext implementation + secure API composition ------

type private HarnessAuthContext(roles: Set<string>, anonymous: bool) =
    interface IAuthContext with
        member _.HasRole(role: string) = roles.Contains role
        member _.HasClaim(_, _) = false
        member _.HasTenant() = false
        member _.IsAnonymous() = anonymous

let private resolveAuthFromHeaders (ctx: HttpContext) : Async<IAuthContext> =
    async {
        let mutable values : Microsoft.Extensions.Primitives.StringValues =
            Microsoft.Extensions.Primitives.StringValues.Empty
        if ctx.Request.Headers.TryGetValue("X-Roles", &values) then
            let raw = values.ToString()
            let roles =
                if System.String.IsNullOrWhiteSpace raw then Set.empty
                else raw.Split(',') |> Array.map _.Trim() |> Set.ofArray
            let anonymous = roles.IsEmpty
            return HarnessAuthContext(roles, anonymous) :> IAuthContext
        else
            return HarnessAuthContext(Set.empty, true) :> IAuthContext
    }

let private secureHandlers = {
    AdminOnly = fun () -> async { return "admin-secret" }
    OpenToAll = fun () -> async { return "everyone-welcome" }
    PublicOnly = fun () -> async { return "public-info" }
}

let private buildSecureApi : HttpHandler =
    Remoting.createApi ()
    |> Remoting.withRouteBuilder routeBuilder
    |> Remoting.fromValue secureHandlers
    |> Remoting.withErrorHandler errorHandler
    |> Remoting.withAuthContext resolveAuthFromHeaders
    |> Remoting.buildHttpHandler

let private buildContextApi : HttpHandler =
    Remoting.createApi ()
    |> Remoting.withRouteBuilder routeBuilder
    |> Remoting.fromContextAsync (fun ctx -> async {
        let! subject = resolveSubject ctx
        return
            { WhoAmI = fun () -> async { return subject }
              WhereAreWe = fun () -> async {
                  // Phase 69b.D — read ambient correlation id without
                  // threading. AsyncLocal flows through Async naturally.
                  return CallContext.correlationId () |> Option.defaultValue "<absent>"
              } }
       })
    |> Remoting.withErrorHandler errorHandler
    |> Remoting.buildHttpHandler

// ---- Host builder ----------------------------------------------------------
//
// Returns a configured IHost ready for TestServer consumption. The
// per-request `api` factory is invoked on every call so per-request
// context resolution (the eventual Phase 69b seam) can be exercised
// here as it lands.

let buildHost (telemetry: IRemotingTelemetry option) : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost
                .UseTestServer()
                .ConfigureServices(fun services -> services.AddGiraffe() |> ignore)
                .Configure(fun (app: IApplicationBuilder) ->
                    // Phase 69b.A — body normalisation is now built into the
                    // ToolUp.Remoting.Giraffe adapter; no separate middleware
                    // registration required.
                    let api : HttpHandler =
                        choose [
                            buildHarnessApi telemetry (fun _ -> handlers)
                            buildContextApi
                            buildSecureApi
                        ]
                    app.UseGiraffe api)
            |> ignore)
        .Build()