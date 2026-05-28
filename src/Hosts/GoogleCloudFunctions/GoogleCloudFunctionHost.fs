// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Hosts.GoogleCloudFunctions

open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Google.Cloud.Functions.Framework
open ToolUp.Platform

// ─── ToolUp.Hosts.GoogleCloudFunctions — GCF adapter ─────────────────
//
// Phase 16 host-adapter companion. Bridges Google Cloud Functions
// (Functions Framework for .NET) HTTP invocations through
// `IServerHost.Invoke` so a compose-built ToolUp deployment runs on
// Google Cloud Functions (2nd gen / Cloud Run functions) without
// touching its handler code. Same code-path as Kestrel; only the host
// driver differs.
//
// **Why the bridge is one line.** The Functions Framework for .NET
// already runs an ASP.NET Core host under the hood and invokes the
// consumer's `IHttpFunction.HandleAsync` with a fully populated
// `HttpContext`. Unlike the Azure Functions Worker (`HttpRequestData`
// → translate to `HttpContext`) or AWS Lambda (event payloads →
// translate to `HttpContext`), GCF hands the adapter an `HttpContext`
// directly — `IServerHost.Invoke` consumes the same type. No request
// or response translation is required.
//
// **Usage in a consumer GCF project.** Two shapes are supported.
//
// *Recommended: register the adapter's `ServerHostFunction` as the
// Functions Framework's function target.* The framework instantiates
// it per cold-start and calls `HandleAsync` per invocation.
//
//     // Function.fs
//     module Function
//
//     open Google.Cloud.Functions.Framework
//     open Google.Cloud.Functions.Hosting
//     open Microsoft.Extensions.DependencyInjection
//     open Microsoft.Extensions.Hosting
//     open ToolUp.Hosts.GoogleCloudFunctions
//     open ToolUp.Platform
//
//     // Compose the SDK once at cold-start; reused for every invocation.
//     let serverHost: IServerHost =
//         ServerApp.empty
//         |> ServerApp.withConfig {
//             ServerConfig.defaults with
//                 Mode = Anonymous
//                 ServerlessHost = ServerlessHost
//                 JobScheduler = NoJobScheduler
//                 Webhooks = NoWebhooks
//                 Notifications = NoNotificationsExplicit
//                 AuditLog = NoAuditLog
//                 UsageMetering = NoUsageMetering
//                 HealthStateTracking = false
//         }
//         |> ServerApp.addModule (myModule.register ())
//         |> ServerApp.composeOnly   // hypothetical helper returning IServerHost
//
//     // Start the SDK host once at cold-start (no-op when every
//     // IHostedService is gated off, but ASP.NET Core internals need
//     // a started host to resolve logger factory / config root / options).
//     do serverHost.Host.StartAsync(System.Threading.CancellationToken.None).Wait()
//
//     type Startup() =
//         inherit FunctionsStartup()
//         override _.ConfigureServices(_context, services) =
//             services.AddSingleton<IServerHost>(serverHost) |> ignore
//
//     [<FunctionsStartup(typeof<Startup>)>]
//     type ServerHostFunction(host: IServerHost) =
//         interface IHttpFunction with
//             member _.HandleAsync(ctx: HttpContext) =
//                 GoogleCloudFunctionHost.bridge (host, ctx)
//
// Set `FUNCTION_TARGET=Function.ServerHostFunction` (or pass
// `--target Function.ServerHostFunction`) when running locally /
// deploying.
//
// *Manual shape: write your own `IHttpFunction` and call `bridge`
// directly.* Useful when you need additional per-invocation logic
// (request-scoped logging, custom auth pre-checks) before forwarding
// to the SDK pipeline.
//
//     type Function(host: IServerHost) =
//         interface IHttpFunction with
//             member _.HandleAsync(ctx: HttpContext) : Task =
//                 // ... pre-checks ...
//                 GoogleCloudFunctionHost.bridge (host, ctx)
//
// **Cold-start contract.** `IServerHost.Host.StartAsync()` is called
// once at cold-start by the consumer's startup (above), not on every
// invocation — that would be O(n) work per request and would re-run
// middleware-graph construction. `ServerlessHost = ServerlessHost`
// deployments gate every `IHostedService` off so `StartAsync` is
// effectively free; the call still has to happen for ASP.NET Core's
// internal services (logger factory, config root, options) to
// initialise.
//
// **`HttpContext.RequestServices` ownership.** The Functions
// Framework populates `RequestServices` with the GCF host's own DI
// scope. SDK middleware that resolves services per-request (logger
// lookup, metrics sink, `IConfigStore`) needs the SDK's own service
// provider; `bridge` swaps the field to `IServerHost.App.Services`
// before invoking. The original scope is not restored after the call
// — the `HttpContext` is owned by the framework's per-invocation
// pipeline and disposed after `HandleAsync` returns.

[<AbstractClass; Sealed>]
type GoogleCloudFunctionHost =

    /// Drive the GCF-provided `HttpContext` through the SDK's
    /// configured request pipeline. Called per cloud invocation from
    /// the consumer's `IHttpFunction.HandleAsync` implementation.
    ///
    /// Swaps `HttpContext.RequestServices` to `IServerHost.App.Services`
    /// so SDK middleware that resolves DI services per-request (logger
    /// lookup, metrics sink, `IConfigStore`, etc.) gets the same
    /// singletons DI hands out to long-running Kestrel hosts. The
    /// underlying `HttpRequest` / `HttpResponse` streams are reused —
    /// the framework reads the response back to the cloud after
    /// `HandleAsync` returns.
    ///
    /// Throws `System.InvalidOperationException` when `host.App` is
    /// `None` (worker-only silo).
    static member bridge(host: IServerHost, ctx: HttpContext) : Task =
        match host.App with
        | Some app -> ctx.RequestServices <- app.Services
        | None -> ()

        host.Invoke ctx

/// Default `IHttpFunction` implementation that forwards every GCF
/// invocation to a compose-built `IServerHost`. Register as the
/// Functions Framework target via `FUNCTION_TARGET` /
/// `--target <FullyQualifiedTypeName>`. Consumers wanting custom
/// per-invocation logic can implement `IHttpFunction` themselves
/// and call `GoogleCloudFunctionHost.bridge` directly.
type ServerHostFunction(host: IServerHost) =
    interface IHttpFunction with
        member _.HandleAsync(ctx: HttpContext) : Task =
            GoogleCloudFunctionHost.bridge (host, ctx)