// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Hosting

// ─── IServerHost — Phase 16 host abstraction ─────────────────────────
//
// Surfaced by `SDK.Server.compose`. Both the long-running Kestrel
// default and serverless host adapters (`ToolUp.Hosts.AzureFunctions`,
// future Lambda / GCF) drive the same configured pipeline through this
// seam:
//
//   - `ServerApp.run` (and `AIServerApp.run`, `RAGServerApp.run`,
//     companion `run` helpers) call `RunBlocking()` — today's Kestrel
//     `app.Run()` behaviour, returns the process exit code.
//
//   - Serverless host adapters resolve the `IServerHost` from the
//     composition root, call `Host.StartAsync()` once at cold-start
//     to fire `IHostedService` registrations (no-op when the
//     deployment opts into `ServerConfig.ServerlessHost = ServerlessHost`
//     and `compose` gates background services off), then invoke
//     `Invoke ctx` per cloud invocation to drive the request
//     pipeline against an adapter-built `HttpContext`.
//
// The interface keeps the surface minimal: every adapter only needs
// `App` (escape hatch — the configured `WebApplication`), `Host` (the
// underlying `IHost` — every adapter starts it once at cold-start),
// `RunBlocking` (Kestrel default), and `Invoke` (per-request driver
// for serverless hosts).

/// Host abstraction returned by `SDK.Server.compose`. Both the
/// long-running Kestrel host and serverless host adapters drive the
/// same configured request pipeline through this seam.
type IServerHost =
    /// The configured `WebApplication`. Populated for the
    /// `ProcessProfile = AllInOne | WebOnly | DispatcherOnly` shapes.
    /// `None` for `ProcessProfile = WorkerOnly` — that shape builds a
    /// generic `IHost` via `Host.CreateApplicationBuilder()` with no
    /// Kestrel listener, and `Invoke` raises because worker silos
    /// serve no HTTP traffic.
    abstract App: WebApplication option

    /// The `IHost` backing this server. Always populated.
    /// `WebApplication` extends `IHost`, so the AllInOne / WebOnly /
    /// DispatcherOnly shapes expose the same instance through both
    /// `App` and `Host`; the `WorkerOnly` shape exposes only `Host`.
    abstract Host: IHost

    /// Run the host. Blocks until shutdown (Ctrl+C, SIGTERM, or
    /// `IHostApplicationLifetime.StopApplication`). Returns the
    /// process exit code — today always `0` because process-fatal
    /// exceptions escape earlier and the runtime sets a non-zero
    /// code on its own. Called by `ServerApp.run` / `AIServerApp.run`
    /// / `RAGServerApp.run` and every per-companion wrapper. The
    /// Kestrel default; serverless host adapters do NOT call this.
    abstract RunBlocking: unit -> int

    /// Drive the configured request pipeline against an adapter-built
    /// `HttpContext`. Used by serverless host adapters
    /// (`ToolUp.Hosts.AzureFunctions`, future Lambda / GCF) — the
    /// adapter constructs a `DefaultHttpContext` from the cloud
    /// provider's invocation payload (HttpRequestData / APIGateway
    /// event / Functions Framework HTTP event), calls `Invoke`, then
    /// writes the populated `HttpContext.Response` back to the
    /// provider's response shape. Returns the pipeline's completion
    /// `Task`.
    ///
    /// The adapter is responsible for calling `Host.StartAsync()`
    /// once at cold-start so any non-Kestrel `IHostedService`s
    /// (transactional dispatcher, audit replicator) start. A
    /// `ServerlessHost = ServerlessHost` deployment gates every
    /// background service off so `StartAsync` is effectively free
    /// — the call still happens for the ASP.NET Core internals that
    /// need a started host (logger factory, config root).
    ///
    /// Throws `System.InvalidOperationException` when `App` is
    /// `None` (worker-only silo with no HTTP pipeline).
    abstract Invoke: HttpContext -> Task

// ─── Default Kestrel-shape IServerHost implementation ────────────────
//
// Constructed by `SDK.Server.compose` when the deployment uses the
// default `ServerlessHost = KestrelHost` (or any `ProcessProfile`
// that builds a `WebApplication`). Wraps the built `WebApplication`
// and a deferred-request-delegate thunk that produces the pipeline
// `RequestDelegate` on first call to `Invoke`.

[<AutoOpen>]
module ServerHost =

    /// Build a default `IServerHost` over a configured `WebApplication`.
    /// The `RequestDelegate` is built lazily on first `Invoke` so the
    /// Kestrel-only path (which never calls `Invoke`) pays nothing.
    /// `runBlocking` is the thunk that calls `app.Run()` — held as
    /// a parameter so tests can inject a non-blocking variant.
    let createWebHost (app: WebApplication) (runBlocking: unit -> int) : IServerHost =
        // Cache the built `RequestDelegate` so repeated `Invoke` calls
        // (every serverless invocation) don't rebuild the pipeline.
        // `lazy` is thread-safe by default; the underlying
        // `IApplicationBuilder.Build()` is idempotent in practice (it
        // walks the registered middleware list and produces the
        // composed delegate).
        let pipeline =
            lazy ((app :> Microsoft.AspNetCore.Builder.IApplicationBuilder).Build())

        { new IServerHost with
            member _.App = Some app

            member _.Host = app :> IHost

            member _.RunBlocking() = runBlocking ()

            member _.Invoke(ctx: HttpContext) : Task = pipeline.Value.Invoke ctx
        }

    /// Build a worker-only `IServerHost` over a configured
    /// `IHost` (no `WebApplication`, no HTTP pipeline). Used by
    /// `ProcessProfile.WorkerOnly` — `Invoke` throws,
    /// `App` returns `None`, `RunBlocking` runs the host until
    /// shutdown.
    let createWorkerHost (host: IHost) (runBlocking: unit -> int) : IServerHost =
        { new IServerHost with
            member _.App = None

            member _.Host = host

            member _.RunBlocking() = runBlocking ()

            member _.Invoke(_ctx: HttpContext) : Task =
                raise (
                    System.InvalidOperationException(
                        "IServerHost.Invoke is not supported on a worker-only host (ProcessProfile = WorkerOnly). "
                        + "Worker silos serve no HTTP traffic; route HTTP requests at a sibling WebOnly silo."
                    )
                )
        }

// `BackgroundSubsystem` + `ProcessProfileGate` live in
// `Server/Compose/ProcessProfileGate.fs` (earlier in the compile chain)
// so every Compose helper can use them. This file's contribution is the
// IServerHost interface + `createWebHost` / `createWorkerHost` builders
// — only consumed by `SDK.Server.compose`'s tail.