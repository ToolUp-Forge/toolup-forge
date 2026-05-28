module ToolUp.Platform.ComposeHealthSmoke

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage

// ─── compose phase: health checks + smoke tests + CSP contributors ───
//
// First-party `IHealthCheck` registrations + companion accumulator
// loop, opt-in first-party `ISmokeTest` registrations + companion
// accumulator loop, first-party `ICspContributor` registrations.
// Extracted from `compose` for the per-concern subdivision (Phase 15e
// follow-up tail). Each helper takes the substrate values its inline
// definition captured and returns `unit`. Zero behaviour change.

/// Phase 9 + 9k — first-party `IHealthCheck` registrations
/// (`BlobStorageHealthCheck`, `AuthProviderHealthCheck`,
/// `EventStoreHealthCheck`) plus the companion-contributed probes
/// accumulator loop. `HealthCheckAggregator.register` (called near
/// end-of-`compose`) walks every `AddSingleton<IHealthCheck>`
/// registration and feeds them into BCL's `MapHealthChecks` pipeline.
let registerHealthChecks
    (services: IServiceCollection)
    (resolvedBlobStorage: IBlobStorage)
    (auth: IAuthProvider)
    (eventStore: IEventStore)
    (healthChecks: HealthChecks.IHealthCheck list)
    : unit =
    services.AddSingleton<HealthChecks.IHealthCheck>(
        HealthCheck.BlobStorageHealthCheck(resolvedBlobStorage) :> HealthChecks.IHealthCheck
    )
    |> ignore

    services.AddSingleton<HealthChecks.IHealthCheck>(
        HealthCheck.AuthProviderHealthCheck(auth) :> HealthChecks.IHealthCheck
    )
    |> ignore

    services.AddSingleton<HealthChecks.IHealthCheck>(
        HealthCheck.EventStoreHealthCheck(eventStore) :> HealthChecks.IHealthCheck
    )
    |> ignore

    // Phase 9k — companion-contributed `IHealthCheck` instances. Each
    // companion provides a probe via `ServerApp.withHealthCheck`; the
    // SDK aggregator walks every `AddSingleton<IHealthCheck>`
    // registration and feeds them all into BCL's `MapHealthChecks`
    // pipeline.
    for check in healthChecks do
        services.AddSingleton<HealthChecks.IHealthCheck>(check) |> ignore

/// Phase 9o — post-deploy smoke tests. Registered only when
/// `ServerConfig.SmokeTest = EnabledSmokeTest`; the default
/// `NoSmokeTest` skips this block entirely. The dispatcher resolves
/// probes via DI at request time so companion-contributed smoke tests
/// can register through any of the standard pathways
/// (`ServerApp.withSmokeTest`, the `Extensions.ServiceConfig` seam,
/// direct DI); the SDK's first-party probes register here alongside.
///
/// `JobSchedulerSmoke` registers only when a real scheduler is
/// available — `NoJobScheduler` deployments cannot exercise the
/// `Schedule + ReadByType + Cancel` path. The sentinel `_smoke`
/// handler registers against the scheduler so the manually-triggered
/// smoke job resolves; the smoke test never dispatches it.
let registerSmokeTests
    (services: IServiceCollection)
    (config: ServerConfig)
    (resolvedBlobStorage: IBlobStorage)
    (resolvedNotificationChannel: INotificationChannel)
    (eventStore: IEventStore)
    (dataObjectStore: IDataObjectStore)
    (auditLog: IAuditLog)
    (jobSchedulerInstance: IJobScheduler option)
    (smokeTests: SmokeTests.ISmokeTest list)
    : unit =
    match config.SmokeTest with
    | NoSmokeTest -> ()
    | EnabledSmokeTest ->
        services.AddSingleton<SmokeTests.ISmokeTest>(
            SmokeTests.Defaults.BlobStorageSmoke(resolvedBlobStorage) :> SmokeTests.ISmokeTest
        )
        |> ignore

        services.AddSingleton<SmokeTests.ISmokeTest>(
            SmokeTests.Defaults.NotificationChannelSmoke(resolvedNotificationChannel) :> SmokeTests.ISmokeTest
        )
        |> ignore

        services.AddSingleton<SmokeTests.ISmokeTest>(
            SmokeTests.Defaults.EventStoreSmoke(eventStore) :> SmokeTests.ISmokeTest
        )
        |> ignore

        services.AddSingleton<SmokeTests.ISmokeTest>(
            SmokeTests.Defaults.DataObjectStoreSmoke(dataObjectStore) :> SmokeTests.ISmokeTest
        )
        |> ignore

        services.AddSingleton<SmokeTests.ISmokeTest>(
            SmokeTests.Defaults.AuditLogSmoke(auditLog) :> SmokeTests.ISmokeTest
        )
        |> ignore

        match jobSchedulerInstance with
        | Some scheduler ->
            scheduler.RegisterHandler("_smoke", SmokeTests.Defaults.SmokeJobHandler() :> IJobHandler)

            services.AddSingleton<SmokeTests.ISmokeTest>(
                SmokeTests.Defaults.JobSchedulerSmoke(scheduler, eventStore) :> SmokeTests.ISmokeTest
            )
            |> ignore
        | None -> ()

        // Phase 9o — companion-contributed `ISmokeTest` instances. Each
        // companion provides a probe via `ServerApp.withSmokeTest`;
        // the dispatcher walks the service collection at request time
        // and runs every registered probe in parallel.
        for test in smokeTests do
            services.AddSingleton<SmokeTests.ISmokeTest>(test) |> ignore

/// Phase 9j — first-party CSP contributors. Registered only when
/// hardening is opted in (GP 13: zero footprint on the default).
/// `SecurityHardening.aggregate` (near end-of-`compose`) walks these
/// plus any companion-registered `ICspContributor` and builds the
/// resolved policy. The OIDC contributor is inert unless
/// `TOOLUP_OIDC_ISSUER` is set; the AI-host contributor widens
/// `connect-src` only. The AG-Grid-CDN contributor is intentionally
/// NOT auto-registered (Vite bundles the grid same-origin) — a
/// CDN-delivered deployment opts in via
/// `ServerApp.withCspContributor`.
let registerFirstPartyCspContributors (services: IServiceCollection) (config: ServerConfig) : unit =
    if config.SecurityHardening <> NoSecurityHardening then
        services.AddSingleton<ICspContributor>(OidcIssuerCspContributor() :> ICspContributor)
        |> ignore

        services.AddSingleton<ICspContributor>(AiProviderCspContributor() :> ICspContributor)
        |> ignore