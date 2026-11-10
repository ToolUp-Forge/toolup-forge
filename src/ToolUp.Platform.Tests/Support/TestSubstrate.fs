module ToolUp.Platform.Tests.Support.TestSubstrate

// A test that applies a deployment's registrations to a bare service
// collection, without `compose`, registers the substrate its staged
// contributions read. Each substrate value is the INSTANCE the test already
// registered for that service, when it registered one (the last, as in a
// built provider), so a test that hands in a recording event store, audit
// log or notification channel has the composition write through it; any
// other value is an in-memory default.

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Jobs
open ToolUp.Platform.Audit
open ToolUp.Platform.Compose

let private lastInstance<'T when 'T: not struct> (services: IServiceCollection) : 'T option =
    services
    |> Seq.filter (fun d -> d.ServiceType = typeof<'T> && not d.IsKeyedService)
    |> Seq.tryLast
    |> Option.bind (fun d ->
        match d.ImplementationInstance with
        | :? 'T as instance -> Some instance
        | _ -> None)

/// The substrate for `services`: the instances already registered there,
/// in-memory defaults for the rest.
let ofServices (config: ServerConfig) (services: IServiceCollection) : Substrate =
    let logger =
        lastInstance<ILogger> services
        |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

    let orDefault (fallback: unit -> 'T) (found: 'T option) = found |> Option.defaultWith fallback

    {
        Config = config
        Storage =
            lastInstance<IBlobStorage> services
            |> orDefault (fun () ->
                ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage)
        SecretStore =
            lastInstance<Secrets.ISecretStore> services
            |> orDefault (fun () -> EnvironmentSecretStore.EnvironmentSecretStore() :> Secrets.ISecretStore)
        Logger = logger
        EventStore =
            lastInstance<IEventStore> services
            |> orDefault (fun () -> InMemoryEventStore.InMemoryEventStore() :> IEventStore)
        AuditLog =
            lastInstance<IAuditLog> services
            |> orDefault (fun () -> NoOpAuditLog() :> IAuditLog)
        Notifications =
            lastInstance<INotificationChannel> services
            |> orDefault (fun () -> NotificationChannel.NoOpNotificationChannel() :> INotificationChannel)
        MetricsSink =
            lastInstance<Metrics.IMetricsSink> services
            |> orDefault (fun () -> Metrics.NoOpMetricsSink() :> Metrics.IMetricsSink)
        RateLimiter =
            lastInstance<IRateLimiter> services
            |> orDefault (fun () -> NoOpRateLimiter() :> IRateLimiter)
        JobScheduler = lastInstance<IJobScheduler> services
        ActivitySink =
            lastInstance<Tracing.IActivitySink> services
            |> orDefault (fun () -> Tracing.NoOpActivitySink() :> Tracing.IActivitySink)
    }

/// Register the substrate for `services` (see `ofServices`) unless one is
/// registered already.
let ensure (config: ServerConfig) (services: IServiceCollection) : IServiceCollection =
    if services |> Seq.exists (fun d -> d.ServiceType = typeof<Substrate>) then
        services
    else
        Substrate.register (ofServices config services) services

/// Apply `app`'s registrations (`ServerApp.serviceConfig`) to `services`
/// the way `compose` does: the substrate first, then the consumer's
/// service config and every contribution.
let applyServiceConfig (app: ServerApp) (services: IServiceCollection) : IServiceCollection =
    let services = ensure (ServerApp.effectiveConfig app) services

    match ServerApp.serviceConfig app with
    | Some configure -> configure services
    | None -> services