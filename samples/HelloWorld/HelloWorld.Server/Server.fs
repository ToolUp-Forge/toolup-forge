// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module HelloWorld.Server

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.Server
open ToolUp.Remoting.Server

// Phase 66 Stream F.5 — HelloWorld composition root.
// Authenticated single-user deployment using the canonical
// `Surfaces.individual` shape. `ServerConfigOverrides.referenceApp`
// declares `Some Surfaces.individual` so `fromEnv` pins this app to
// the Individual deployment unless the operator overrides via
// TOOLUP_PLATFORM_SURFACES.
//
// Compare with samples/MinimalApp — that sample uses
// `ServerConfigOverrides.empty`, which falls through to
// `ServerConfig.defaults.Surfaces = Surfaces.anonymous`.
//
// ─── Phase 69b.tail worked example ────────────────────────────────
//
// The `Api.make` wrapper auto-composes the Phase 69b platform seams
// by default; this sample mounts one tiny `IHelloApi` to show the
// per-call output. Two patterns are demonstrated:
//
//   1. A console-printing `IRemotingTelemetry` sink — passed via the
//      `?telemetry` argument to override the IMetricsSink-bridging
//      default. The migration doc references this block as the
//      reproducer for "boot the sample, hit a curl, watch the per-
//      method timing line render on stdout".
//
//   2. A `ForgeAuthContext` resolver that stamps a "request started
//      at" timestamp on the per-request context. The resolver is
//      evaluated PER CALL (not snapshotted at composition), so the
//      `StartedAt` field is fresh on every invocation. Demonstrates
//      the per-request-context shape consumers wire when bespoke
//      claim semantics need to flow through to handler code.
//
// Without the explicit `?telemetry` and `?authContext` overrides the
// wrapper would compose its defaults: `IMetricsSink` bridge for
// telemetry, `HttpContext.Items["ToolUp.Subject"]` lookup for context.
// Either path is correct; the worked example surfaces both seams
// explicitly so the migration doc has something to point at.

/// Minimal Fable.Remoting API record exercised by the Phase 69b.tail
/// worked example. One method, no auth attributes — keeps the wire
/// surface tiny so curl reproduction is one line.
type IHelloApi = { SayHello: string -> Async<string> }

module HelloApi =
    let routeBuilder (typeName: string) (methodName: string) : string =
        sprintf "/api/hello/%s/%s" typeName methodName

    /// Console-printing `IRemotingTelemetry` sink. Emits one line per
    /// method completion with the method name, wall-clock elapsed ms,
    /// outcome (`ok`/`error`), and the per-request correlation id
    /// (Phase 69b.D). Suitable as a worked example — a production
    /// deployment would route through `ServerApp.withMetricsSink` and
    /// let the default `Api.make` bridge emit through there.
    let consoleTelemetry: IRemotingTelemetry =
        { new IRemotingTelemetry with
            member _.OnMethodCompleted t =
                let outcomeText =
                    match t.Outcome with
                    | MethodOutcome.Succeeded -> "ok"
                    | MethodOutcome.Failed ex -> sprintf "error: %s" ex.Message

                let correlationText =
                    match t.CorrelationId with
                    | Some id -> id
                    | None -> "-"

                printfn
                    "[hello/telemetry] method=%s elapsed_ms=%d outcome=%s correlation=%s"
                    t.MethodName
                    t.ElapsedMs
                    outcomeText
                    correlationText
        }

    /// "Request started at" resolver — evaluated per call, not
    /// snapshotted at composition. Demonstrates the build-once / read-
    /// per-call shape Phase 69b.B closed: the timestamp is fresh on
    /// every request, so a closure-captured value couldn't substitute.
    let requestStartedAtResolver (ctx: HttpContext) : Async<ForgeAuthContext> = async {
        let startedAt = DateTimeOffset.UtcNow

        // The per-request context's claim slot carries the stamped
        // timestamp; handler code reads it via `HasClaim ("startedAt",
        // None)` returning `true` AND inspecting the value through
        // whatever side-channel applies. The worked-example shape
        // simply lets the resolver evaluate per call so the timing is
        // observable.
        return
            { new ForgeAuthContext with
                member _.HasRole _ = false

                member _.HasClaim(claim, _) =
                    match claim with
                    | "startedAt" -> true
                    | _ -> false

                member _.HasTenant() = false
                member _.IsAnonymous() = true
                member _.SubjectId = sprintf "anonymous:started-at:%O" startedAt
            }
    }

    /// Server-side implementation of the demo method.
    let helloImpl (_: HttpContext) : IHelloApi = {
        SayHello = fun name -> async { return sprintf "Hello, %s!" name }
    }

    /// Mount the worked-example handler via `Api.make`. Both seams
    /// passed explicitly so the trace lines render on stdout; remove
    /// the explicit args to fall back to the default bridges (forge's
    /// `IMetricsSink` + the `HttpContext.Items["ToolUp.Subject"]`
    /// reader) per the Phase 69b.tail wrapper composition.
    let routes =
        Api.make (
            helloImpl,
            routeBuilder = routeBuilder,
            telemetry = consoleTelemetry,
            authContext = requestStartedAtResolver
        )

/// Console `IMetricsSink` that prints every emission. Composed via
/// `ServerApp.withMetricsSink` so the default `Api.make` bridge in any
/// non-overridden handler emits through here — proves the seam is
/// active end-to-end. Production deployments swap this for the
/// Prometheus / OpenTelemetry companion sink.
type ConsoleMetricsSink() =
    interface IMetricsSink with
        member _.Record(name, value, tags) =
            printfn "[metrics/record] %s=%g %A" name value tags

        member _.Increment(name, tags) = printfn "[metrics/inc] %s %A" name tags

        member _.SetGauge(name, value, tags) =
            printfn "[metrics/gauge] %s=%g %A" name value tags

[<EntryPoint>]
let main _ =
    let logger = ConsoleLogger.fromEnv ()
    let config = ServerConfig.fromEnv logger ServerConfigOverrides.referenceApp

    let helloModule =
        ServerModule.create "Hello" |> ServerModule.withHandlers [ HelloApi.routes ]

    ServerApp.empty
    |> ServerApp.withConfig config
    |> ServerApp.withLogger logger
    |> ServerApp.withMetricsSink (ConsoleMetricsSink())
    |> ServerApp.addModule helloModule
    |> ServerApp.run