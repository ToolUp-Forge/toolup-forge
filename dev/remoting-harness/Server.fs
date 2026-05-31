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

let private handlers = {
    Echo = fun input -> async { return input }
    Heartbeat = fun () -> async { return DateTimeOffset.UtcNow }
    Boom = fun reason -> async { return failwithf "Boom requested: %s" reason }
}

// ---- Error-handler ----------------------------------------------------------
//
// Phase 69b will introduce categorised envelopes (`RemotingError.User`,
// `.System`, etc.). For v0 we use the upstream `ErrorResult.Propagate`
// shape so the harness has a stable baseline to assert against; the
// Phase 69b test additions will swap to the categorised form.

let private errorHandler (ex: exn) (routeInfo: RouteInfo<HttpContext>) : ErrorResult =
    Propagate(box (sprintf "%s: %s" routeInfo.methodName ex.Message))

// ---- Forge-shaped Api.make wrapper ------------------------------------------
//
// Mirrors `Api.make` at
// toolup-forge/src/ToolUp.Platform.Server/Server/Api.fs:26 so the
// harness's composition path matches what forge SDK consumers exercise.

let private buildApi (api: HttpContext -> IHarnessApi) : HttpHandler =
    Remoting.createApi ()
    |> Remoting.withRouteBuilder routeBuilder
    |> Remoting.fromContext api
    |> Remoting.withErrorHandler errorHandler
    |> Remoting.buildHttpHandler

// ---- Host builder ----------------------------------------------------------
//
// Returns a configured IHost ready for TestServer consumption. The
// per-request `api` factory is invoked on every call so per-request
// context resolution (the eventual Phase 69b seam) can be exercised
// here as it lands.

let buildHost () : IHost =
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
                    app.UseGiraffe(buildApi (fun _ -> handlers)))
            |> ignore)
        .Build()