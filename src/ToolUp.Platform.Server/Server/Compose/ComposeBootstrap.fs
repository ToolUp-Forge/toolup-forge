module ToolUp.Platform.ComposeBootstrap

open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.ConfigurePipeline

// ─── compose phase: finalisation + Build ─────────────────────────────
//
// Pre-Build aggregator finalisations (config-validator preflight, CSP
// contributor aggregation) and the `ProcessProfile`-discriminated
// Build branch that produces the configured `IServerHost`. Extracted
// from `compose` for the per-concern subdivision (Phase 15e follow-up
// tail). Takes the exact substrate values the inline definition
// captured and returns the same shape. Zero behaviour change.

/// Phase 16a — construct the builder shape per `ProcessProfile`.
/// Either a `WebApplicationBuilder` bound to `0.0.0.0:<port>` (with
/// optional `MaxRequestBodyBytes` Kestrel cap) for HTTP-serving
/// profiles, or a generic `HostApplicationBuilder` with no Kestrel
/// listener for `WorkerOnly`. The match is exhaustive — exactly one
/// of the returned options is `Some`. The caller binds `.Services` off
/// whichever is populated; the rest of `compose` registers against
/// `IServiceCollection` without knowing which shape backs it.
let buildHostBuilder
    (config: ServerConfig)
    (serverPort: string)
    : WebApplicationBuilder option * HostApplicationBuilder option =
    let useHttpPipeline = ProcessProfileGate.shouldRegisterHttpPipeline config

    let webBuilder =
        if useHttpPipeline then
            let b = WebApplication.CreateBuilder()

            b.WebHost.UseUrls($"http://0.0.0.0:{serverPort}") |> ignore

            // Gap audit pass-2 #3 — apply ServerConfig.MaxRequestBodyBytes
            // to Kestrel so the SDK has a deliberate per-request cap rather
            // than relying on the framework's 30 MB default. `None` (the SDK
            // default) leaves Kestrel's 30 MB in place; `Some bytes` stamps
            // the explicit limit. The MaxRequestBodyBytesValidator warns at
            // preflight if the chosen cap is unwise for the deployment shape.
            match config.MaxRequestBodyBytes with
            | Some bytes ->
                b.WebHost.ConfigureKestrel(fun opts -> opts.Limits.MaxRequestBodySize <- System.Nullable bytes)
                |> ignore
            | None -> ()

            Some b
        else
            None

    let workerBuilder =
        if useHttpPipeline then
            None
        else
            // `ProcessProfile = WorkerOnly`. Generic `IHost` with no
            // Kestrel listener; only the registered `IHostedService`
            // set keeps the process alive. `MaxRequestBodyBytes` has
            // no effect on this branch — there's no inbound HTTP to
            // cap.
            Some(Host.CreateApplicationBuilder())

    webBuilder, workerBuilder

/// Phase 9m — companion config preflight. Runs every registered
/// `IConfigValidator` in parallel (10s global budget); throws
/// `ConfigPreflightFailedException` if any returns `Error` (unless
/// `ServerConfig.SkipPreflight = true`). Warnings log + continue.
/// Must run after every companion has had a chance to call
/// `services.AddSingleton<IConfigValidator>(...)` and before
/// `HealthCheckAggregator.register` so the validator-registration
/// walk sees an immutable picture of `services`.
///
/// Registers a `PreflightSnapshot` singleton so the dev-diagnostics
/// inspector can surface the outcome list at `/dev/inspect`.
let runConfigPreflight (services: IServiceCollection) (config: ServerConfig) (resolvedLogger: ILogger) : unit =
    let preflightOutcomes =
        ConfigValidatorAggregator.validate services (Some resolvedLogger) config.SkipPreflight

    let preflightSnapshot = ConfigValidatorAggregator.PreflightSnapshot()
    preflightSnapshot.Set preflightOutcomes

    services.AddSingleton<ConfigValidatorAggregator.IPreflightSnapshot>(
        preflightSnapshot :> ConfigValidatorAggregator.IPreflightSnapshot
    )
    |> ignore

/// Phase 9j — fold every registered `ICspContributor` (first-party
/// + companion, the latter via `ComposeExtensions.ServiceConfig` /
/// `ServerApp.withCspContributor`) into the resolved CSP header and
/// register it as the singleton `CspMiddleware` resolves. Must run
/// after the `extensions.ServiceConfig` hook (companions registered)
/// and before `builder.Build()` seals the collection — same timing
/// contract as the health-check / config-validator aggregators.
/// `NoSecurityHardening` short-circuits to an empty header so the
/// middleware no-ops and the default deployment is unchanged.
let registerCspAggregate (services: IServiceCollection) (config: ServerConfig) : unit =
    services.AddSingleton<SecurityHardening.ResolvedCspPolicy>(SecurityHardening.aggregate services config)
    |> ignore

/// Giraffe stock-helper DI defaults. The pipeline mounts Giraffe
/// (`app.UseGiraffe` in `configurePipeline`), but until this landed the
/// service composition never registered the services Giraffe's stock
/// response helpers resolve from DI per request: `INegotiationConfig`
/// (the whole negotiating `RequestErrors.*` / `ServerErrors.*` /
/// `Successful.*` family plus `negotiate` / `negotiateWith`),
/// `Giraffe.Json.ISerializer` (the `json` helper) and
/// `Giraffe.Xml.ISerializer` (the `xml` helper). A consumer route
/// handler reaching for any of them threw
/// `Giraffe.MissingDependencyException` at request time, which the
/// exception middleware masked as an opaque 500 — even on the error
/// branches that should have explained the problem (observed in a
/// consumer deployment where `RequestErrors.BAD_REQUEST` itself 500'd).
///
/// Shape: `Json.ISerializer` is pinned FIRST to a Giraffe serializer
/// backed by the SDK's STJ wire options (`FableConverters.shared`) so
/// `json`-helper output matches the platform wire format for F# records
/// / options / DUs — Giraffe's own default (camelCase STJ without the
/// F# converter set) does not handle those shapes the same way. Then
/// `AddGiraffe()` fills in the rest (`RecyclableMemoryStreamManager`,
/// `Xml.ISerializer`, `INegotiationConfig`); its internal
/// `Json.ISerializer` registration no-ops against the pin because every
/// registration `AddGiraffe()` makes is `TryAdd`-semantics.
///
/// Ordering contract: must run AFTER the `extensions.ServiceConfig`
/// hook, so a consumer- or companion-registered `Json.ISerializer` /
/// `INegotiationConfig` / `Xml.ISerializer` makes the SDK's `TryAdd`
/// registrations no-op — the consumer always wins. The SDK's own code
/// paths resolve none of these services, so existing deployments are
/// byte-for-byte unaffected (GP 11); this is purely additive enablement
/// for consumer handlers.
let registerGiraffeDefaults (services: IServiceCollection) : unit =
    services.TryAddSingleton<Json.ISerializer>(fun _ ->
        Json.Serializer(ToolUp.Remoting.Json.SystemTextJson.FableConverters.shared) :> Json.ISerializer)

    services.AddGiraffe() |> ignore

/// Phase 526 — the composed grounding registrations (sorted `metric:` /
/// `subject:` ids) for the config-drift snapshot, resolved from the
/// DI-registered `IMetricRegistry` (Phase 519). Empty when no grounding is
/// composed (`GetService` returns null), so a grounding-free deployment's
/// snapshot is byte-for-byte unchanged.
let internal groundingDriftIds (services: System.IServiceProvider) : string list =
    match services.GetService(typeof<Grounding.IMetricRegistry>) with
    | null -> []
    | resolved ->
        let reg = resolved :?> Grounding.IMetricRegistry

        (reg.Metrics |> List.map (fun m -> "metric:" + m.Id))
        @ (reg.Subjects |> List.map (fun s -> "subject:" + s.Id))
        |> List.sort

/// Phase 16a — branch the `Build()` call by `ProcessProfile`.
/// `WebApplicationBuilder.Build()` returns `WebApplication` (Kestrel
/// + middleware pipeline ready to mount); `HostApplicationBuilder.Build()`
/// returns a generic `IHost` with no Kestrel listener. Both expose
/// `.Services`, so the shared post-Build validation (`ILogger`
/// resolution, `ConfigDriftDetector`) runs against both. Only the
/// Web path mounts the HTTP middleware via `configurePipeline` and
/// returns through `createWebHost`; the worker path skips
/// `configurePipeline` entirely and returns through `createWorkerHost`,
/// whose `Invoke` raises (`InvalidOperationException` — worker silos
/// serve no HTTP).
let buildAndRunHost
    (config: ServerConfig)
    (webBuilder: WebApplicationBuilder option)
    (workerBuilder: HostApplicationBuilder option)
    (resolvedBlobStorage: IBlobStorage)
    (auditLog: IAuditLog)
    (resolvedLogger: ILogger)
    (resolvedMetricsSink: Metrics.IMetricsSink)
    (extensions: ComposeExtensions)
    (router: HttpHandler list -> HttpHandler)
    (devRoutes: HttpHandler list)
    : IServerHost =
    match webBuilder, workerBuilder with
    | Some wb, _ ->
        let app = wb.Build()

        // Startup validation — `logApiError` resolves `ILogger` per request
        // via `GetRequiredService`. Asserting it once here trades a missing-
        // registration silent failure (where every API error vanishes into
        // a request-scoped exception that `Fable.Remoting` re-wraps) for a
        // loud startup throw with a clear message. If you got here via a
        // path that bypasses this validation, register an `ILogger` before
        // serving requests.
        app.Services.GetRequiredService<ILogger>() |> ignore

        // Phase 9q — opt-in startup-time config drift detector. When
        // `ConfigDriftDetection = EnabledConfigDriftDetection`, snapshots
        // the resolved `ServerConfig` (secrets redacted) + a hash of the
        // active companion-assembly set to
        // `_platform/_deploy/last-config.json`, compares against the
        // previous startup's snapshot, and emits a `Warn` log + a
        // `ConfigDrift` audit event when the shape differs. Default
        // `NoConfigDriftDetection` (GP 13) skips the read / compare /
        // write entirely. Runs after `builder.Build()` so the companion-
        // assembly enumeration sees the fully-loaded `AppDomain` and
        // before the middleware pipeline runs so the audit event lands
        // before any request traffic.
        match config.ConfigDriftDetection with
        | NoConfigDriftDetection -> ()
        | EnabledConfigDriftDetection ->
            ConfigDriftDetector.run resolvedBlobStorage auditLog resolvedLogger config (groundingDriftIds app.Services)
            |> Async.RunSynchronously

        // ─── Middleware pipeline → configurePipeline (defined above) ────────
        //
        // Phase 16 — `configurePipeline` no longer calls `app.Run()`.
        // Composition completes when this returns; the caller drives the
        // host through `IServerHost.RunBlocking()` (Kestrel default) or
        // `IServerHost.Invoke` (serverless host adapters).
        configurePipeline app config resolvedLogger resolvedMetricsSink extensions router devRoutes

        // Phase 16 — return `IServerHost` so both Kestrel and serverless
        // host adapters consume the same configured pipeline. The default
        // Kestrel implementation calls `app.Run()` from `RunBlocking()`;
        // graceful shutdown returns process exit code `0` because every
        // process-fatal exception escapes earlier and the runtime sets a
        // non-zero exit code on its own.
        createWebHost app (fun () ->
            app.Run()
            0)
    | _, Some hb ->
        // Phase 16a — `ProcessProfile = WorkerOnly` branch. Generic
        // `IHost` shape; `Host.CreateApplicationBuilder()` mounts no
        // web host, so there's no Kestrel listener at all on this
        // process — `IServerHost.Invoke` throws (`createWorkerHost`
        // raises `InvalidOperationException`). The registered
        // `IHostedService` set (job scheduler, webhook dispatcher,
        // transactional dispatcher, etc.) keeps the silo alive;
        // sibling `WebOnly` silos own the HTTP surface and point at
        // the same persistence stores.
        let host = hb.Build()

        host.Services.GetRequiredService<ILogger>() |> ignore

        match config.ConfigDriftDetection with
        | NoConfigDriftDetection -> ()
        | EnabledConfigDriftDetection ->
            ConfigDriftDetector.run resolvedBlobStorage auditLog resolvedLogger config (groundingDriftIds host.Services)
            |> Async.RunSynchronously

        resolvedLogger.Info(
            "[Phase 16a] ProcessProfile = WorkerOnly — HTTP middleware pipeline NOT registered and no Kestrel listener bound. Background services run; no /api/* routes are served from this silo."
        )

        createWorkerHost host (fun () ->
            host.Run()
            0)
    | None, None -> failwith "unreachable: exactly one of webBuilder / workerBuilder is constructed"