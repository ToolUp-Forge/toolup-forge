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
open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open Giraffe
open ToolUp.Remoting.Harness.Shared

// ---- Handler implementations -------------------------------------------------

let private handlers = {
    Echo = fun input -> async { return input }
    Heartbeat = fun () -> async { return DateTimeOffset.UtcNow }
    Boom = fun reason -> async { return failwithf "Boom requested: %s" reason }
}

// ---- Forge-shaped middleware: body normalisation for unit methods ------------
//
// Mirrors `RemotingBodyNormalizationMiddleware` at
// toolup-forge/src/ToolUp.Platform.Server/Server/Middleware.fs:459.
// Phase 69b will fold this behaviour into the dispatcher itself; until
// then, the harness reproduces forge's current registration so the
// `unit -> Async<T>` path under test sees the same byte-shape the SDK
// composes today.

let private emptyArrayJsonBytes = Encoding.UTF8.GetBytes "[]"

type RemotingBodyNormalizationMiddleware(next: RequestDelegate) =
    member _.InvokeAsync(ctx: HttpContext) =
        task {
            let isRemotingRequest = ctx.Request.Headers.ContainsKey "x-remoting-proxy"

            if isRemotingRequest then
                ctx.Request.EnableBuffering()
                use reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen = true)
                let! body = reader.ReadToEndAsync()
                ctx.Request.Body.Position <- 0L
                let trimmed = body.Trim()

                if trimmed = "" || trimmed = "\"\"" || trimmed = "null" then
                    ctx.Request.Body <- new MemoryStream(emptyArrayJsonBytes)
                    ctx.Request.ContentLength <- Nullable(int64 emptyArrayJsonBytes.Length)

                    if ctx.Request.Method = "GET" then
                        ctx.Request.Method <- "POST"

            do! next.Invoke ctx
        }
        :> System.Threading.Tasks.Task

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
                    app.UseMiddleware<RemotingBodyNormalizationMiddleware>() |> ignore
                    app.UseGiraffe(buildApi (fun _ -> handlers)))
            |> ignore)
        .Build()