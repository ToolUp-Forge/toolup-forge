// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Hosts.AzureFunctions

open System
open System.IO
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.Azure.Functions.Worker
open Microsoft.Azure.Functions.Worker.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Primitives
open ToolUp.Platform

// ─── ToolUp.Hosts.AzureFunctions — Azure Functions Worker adapter ────
//
// Phase 16 host-adapter companion. Bridges Azure Functions Worker HTTP
// triggers through `IServerHost.Invoke` so a compose-built ToolUp
// deployment runs on Azure Functions Consumption / Premium plans
// without touching its handler code. Same code-path as Kestrel; only
// the host driver differs.
//
// **Usage in a consumer Functions Worker project.**
//
// 1. Compose the SDK app at worker startup and register `IServerHost`
//    as a DI singleton in the Functions Worker host:
//
//        let serverHost =
//            ServerApp.empty
//            |> ServerApp.withConfig {
//                ServerConfig.defaults with
//                    Mode = Anonymous
//                    ServerlessHost = ServerlessHost
//                    JobScheduler = NoJobScheduler
//                    Webhooks = NoWebhooks
//                    Notifications = NoNotificationsExplicit
//                    AuditLog = NoAuditLog
//                    UsageMetering = NoUsageMetering
//                    HealthStateTracking = false
//            }
//            |> ServerApp.addModule (myModule.register ())
//            |> ServerApp.composeOnly   // returns IServerHost; never calls RunBlocking
//
//        Host
//            .CreateDefaultBuilder()
//            .ConfigureFunctionsWorkerDefaults()
//            .ConfigureServices(fun services ->
//                services.AddSingleton<IServerHost>(serverHost) |> ignore
//                AzureFunctionHost.cacheServerHost serverHost)
//            .Build()
//            .Run()
//
//    (ServerApp.composeOnly is a hypothetical helper that returns
//    `IServerHost` without calling `RunBlocking()`. Until that helper
//    lands as a top-level pattern, consumers can cache the
//    `IServerHost` returned by `compose` directly via the low-level
//    entry point, OR call `AzureFunctionHost.cacheServerHost` from
//    the worker's `ConfigureServices` callback once they obtain the
//    host instance.)
//
// 2. Write one HTTP-trigger function with a `{*path}` catchall route
//    that forwards every request to `AzureFunctionHost.bridge`:
//
//        type CatchallFunction(host: IServerHost) =
//            [<Function("Catchall")>]
//            member _.Run
//                ([<HttpTrigger(AuthorizationLevel.Anonymous,
//                               "get", "post", "put", "delete", "patch", "options",
//                               Route = "{*path}")>]
//                 req: HttpRequestData,
//                 ctx: FunctionContext) =
//                AzureFunctionHost.bridge host req
//
// The bridge translates `HttpRequestData` → `DefaultHttpContext`,
// invokes the compose-registered pipeline via `IServerHost.Invoke`,
// then writes the populated `HttpContext.Response` back to an
// `HttpResponseData` the Functions runtime serialises to the cloud
// invocation response.
//
// **Cold-start contract.** `IServerHost.Host.StartAsync()` is NOT
// called by `bridge` on every invocation (that would be O(n) work per
// request). The Functions Worker startup is responsible for starting
// the host once at cold-start. For `ServerlessHost = ServerlessHost`
// deployments the started host is effectively a no-op (every
// `IHostedService` registration is gated off); the call still has to
// happen so ASP.NET Core's internal services (logger factory, config
// root, options) initialise.

[<AbstractClass; Sealed>]
type AzureFunctionHost =

    /// Translate an `HttpRequestData` into a `DefaultHttpContext`
    /// suitable for driving through `IServerHost.Invoke`. Copies
    /// method / URL / query / headers / body stream verbatim. The
    /// returned `HttpContext` is owned by the caller; its `Response`
    /// is read by `bridge` after `Invoke` returns.
    static member toHttpContext(req: HttpRequestData, host: IServerHost) : HttpContext =
        let ctx = DefaultHttpContext()

        // Wire the Functions Worker's service provider into the
        // HttpContext so middleware that resolves services via
        // `HttpContext.RequestServices` (e.g. the SDK's per-request
        // logger / metrics sink lookup) gets the same instances DI
        // hands out. The composition root registered them on the
        // Functions Worker's IServiceCollection.
        match host.App with
        | Some app -> ctx.RequestServices <- app.Services
        | None -> ()

        let request = ctx.Request

        request.Method <- req.Method

        // `req.Url` is an absolute URI from the Functions Worker
        // (scheme://host[:port]/path?query). Split into the components
        // ASP.NET Core's HttpRequest expects.
        let url = req.Url
        request.Scheme <- url.Scheme
        request.Host <- HostString(url.Host, url.Port)

        request.Path <-
            if String.IsNullOrEmpty url.AbsolutePath then
                PathString "/"
            else
                PathString url.AbsolutePath

        request.QueryString <- QueryString url.Query
        request.PathBase <- PathString ""

        // Headers — Functions Worker's `Headers` is a string-collection
        // dictionary; copy multi-values verbatim. The SDK pipeline
        // reads through `IHeaderDictionary` so values are kept as
        // `StringValues`.
        for header in req.Headers do
            request.Headers[header.Key] <- StringValues(Seq.toArray header.Value)

        // Body — Functions Worker exposes `Body : Stream`; mount
        // directly on `HttpRequest.Body`. The stream is read-once;
        // middleware that re-reads it (Fable.Remoting body
        // normalisation, file-upload handlers) is responsible for
        // its own buffering via `EnableBuffering()`.
        request.Body <- req.Body

        // Response body buffer — `MemoryStream` so post-invoke
        // we can extract the bytes and stream them back to
        // `HttpResponseData`.
        ctx.Response.Body <- new MemoryStream()

        ctx

    /// Write the post-invoke `HttpContext.Response` back to an
    /// `HttpResponseData` constructed against the original
    /// `HttpRequestData`. Status code, headers, and body all
    /// round-trip; cookies are carried in the `Set-Cookie` header
    /// per ASP.NET Core convention.
    static member toHttpResponseData(req: HttpRequestData, ctx: HttpContext) : Task<HttpResponseData> = task {
        let statusCode = ctx.Response.StatusCode |> enum<System.Net.HttpStatusCode>

        let response = req.CreateResponse(statusCode)

        // Copy headers from the populated HttpContext.Response onto
        // the HttpResponseData. Functions Worker handles header
        // serialisation; we just need to forward each pair.
        for header in ctx.Response.Headers do
            response.Headers.Add(header.Key, header.Value :> string seq)

        // Body — flip the MemoryStream back to position 0 and copy
        // into the HttpResponseData body stream. The Functions runtime
        // closes both streams.
        let body = ctx.Response.Body
        body.Position <- 0L
        do! body.CopyToAsync(response.Body)

        return response
    }

    /// Bridge a Functions HTTP trigger through the SDK's configured
    /// request pipeline. Called per cloud invocation from the
    /// consumer's `[<Function>]`-attributed method.
    ///
    /// Translates `HttpRequestData` → `HttpContext`, invokes via
    /// `IServerHost.Invoke`, then writes the response back. Throws
    /// `System.InvalidOperationException` when `host.App` is `None`
    /// (worker-only silo).
    static member bridge(host: IServerHost, req: HttpRequestData) : Task<HttpResponseData> = task {
        let ctx = AzureFunctionHost.toHttpContext (req, host)
        do! host.Invoke ctx
        return! AzureFunctionHost.toHttpResponseData (req, ctx)
    }