module ToolUp.Platform.ComposeTenantLifecycle

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform

// ─── Phase 54 — tenant-lifecycle compose step ────────────────────────
//
// Registers the four first-party `ITenantLifecycle` hooks + the
// process-local `TenantLifecycleSnapshot` when
// `ServerConfig.TenantLifecycle = EnabledTenantLifecycle`. Each hook is
// registered via the factory overload so it captures the runtime
// `IServiceProvider` and resolves its substrate lazily on every call
// (stateless between invocations, GP 12 rule 4); a hook whose substrate
// is inactive self-`Skipped`s, so registering all four unconditionally
// under the enabled mode is safe regardless of which other subsystems
// the deployment composed.
//
// `NoTenantLifecycle` (the default) is a no-op — no hooks, no snapshot,
// no `/api/_platform/tenants/*` route (the route mount in
// `BuildRouteHandlers` gates on the same field). Zero cost when unused
// (GP 13).
//
// Companion-authored hooks register additively via the same
// `services.AddSingleton<ITenantLifecycle>` surface; the aggregator
// resolves the full `seq<ITenantLifecycle>` at request time.

/// Register the first-party tenant-lifecycle hooks + snapshot holder.
let registerTenantLifecycle (services: IServiceCollection) (config: ServerConfig) : unit =
    match config.TenantLifecycle with
    | NoTenantLifecycle -> ()
    | EnabledTenantLifecycle ->
        services.AddSingleton<PlatformTenantApiHandler.TenantLifecycleSnapshot>(
            PlatformTenantApiHandler.TenantLifecycleSnapshot()
        )
        |> ignore

        services.AddSingleton<ITenantLifecycle>(fun (sp: IServiceProvider) -> EncryptionKeyLifecycle.create sp)
        |> ignore

        services.AddSingleton<ITenantLifecycle>(fun (sp: IServiceProvider) -> MembershipCacheLifecycle.create sp)
        |> ignore

        services.AddSingleton<ITenantLifecycle>(fun (sp: IServiceProvider) -> JobSchedulerLifecycle.create sp)
        |> ignore

        services.AddSingleton<ITenantLifecycle>(fun (sp: IServiceProvider) -> DataSubjectRequestLifecycle.create sp)
        |> ignore