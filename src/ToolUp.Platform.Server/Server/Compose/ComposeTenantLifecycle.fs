module ToolUp.Platform.ComposeTenantLifecycle

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

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
// **Phase 54e additions** (also gated on `EnabledTenantLifecycle`):
//   * the blob-backed `ILifecycleSummaryStore` — durable last-summary
//     backing for `GetLifecycleSummary` (survives restart + visible
//     across replicas), in front of which the snapshot is a cache;
//   * a `/dev/inspect` contributor listing every registered
//     `ITenantLifecycle` hook by name, mirroring the way
//     `HealthCheckAggregator` health probes are surfaced.
//
// `NoTenantLifecycle` (the default) is a no-op — no hooks, no snapshot,
// no store, no contributor, no `/api/_platform/tenants/*` route (the
// route mount in `BuildRouteHandlers` gates on the same field). Zero
// cost when unused (GP 13).
//
// Companion-authored hooks register additively via the same
// `services.AddSingleton<ITenantLifecycle>` surface; the aggregator
// resolves the full `seq<ITenantLifecycle>` at request time.

/// Phase 54e — `/dev/inspect` contributor surfacing the registered
/// tenant-lifecycle hook set. Closes over the root `IServiceProvider` and
/// resolves `seq<ITenantLifecycle>` at request time (cheap in-memory
/// enumeration — well inside the contributor budget). Every hook
/// implements both `OnProvisioned` and `OnDeprovisioned`, so each runs in
/// both phases; the panel lists name + phases for operator visibility,
/// the equivalent of the health-check snapshot at `/dev/inspect`.
type private TenantLifecycleDiagnosticsContributor(sp: IServiceProvider) =
    interface IDevDiagnosticsContributor with
        member _.Contribute() = async {
            let hooks = sp.GetServices<ITenantLifecycle>() |> List.ofSeq

            let payload = {|
                HookCount = hooks.Length
                Hooks =
                    hooks
                    |> List.map (fun h -> {|
                        Name = h.Name
                        Phases = [ "Provisioning"; "Deprovisioning" ]
                    |})
            |}

            return "Tenant lifecycle hooks", box payload
        }

/// Register the first-party tenant-lifecycle hooks + snapshot holder +
/// durable summary store + dev-diagnostics contributor.
let registerTenantLifecycle (services: IServiceCollection) (config: ServerConfig) : unit =
    match config.TenantLifecycle with
    | NoTenantLifecycle -> ()
    | EnabledTenantLifecycle ->
        services.AddSingleton<PlatformTenantApiHandler.TenantLifecycleSnapshot>(
            PlatformTenantApiHandler.TenantLifecycleSnapshot()
        )
        |> ignore

        // Phase 54e — durable last-summary store. Factory-registered so it
        // resolves the deployment's `IBlobStorage` (always present — a
        // default in-process/filesystem blob store is registered before
        // this step) lazily; the store is stateless between calls.
        services.AddSingleton<ILifecycleSummaryStore>(fun (sp: IServiceProvider) ->
            BlobBackedLifecycleSummaryStore.create (sp.GetRequiredService<IBlobStorage>()))
        |> ignore

        // Phase 54e — `/dev/inspect` panel listing the registered hook set.
        services.AddSingleton<IDevDiagnosticsContributor>(fun (sp: IServiceProvider) ->
            TenantLifecycleDiagnosticsContributor(sp) :> IDevDiagnosticsContributor)
        |> ignore

        // Phase 54b — completed-step offboard ledger. Factory-registered so
        // it resolves the deployment's `IBlobStorage` lazily; the background
        // sweep (`LifecycleJobHandler`) consults it for resumable recovery,
        // and the inline provision path clears it on re-onboarding.
        services.AddSingleton<ILifecycleLedger>(fun (sp: IServiceProvider) ->
            BlobBackedLifecycleLedger.create (sp.GetRequiredService<IBlobStorage>()))
        |> ignore

        services.AddSingleton<ITenantLifecycle>(fun (sp: IServiceProvider) -> EncryptionKeyLifecycle.create sp)
        |> ignore

        services.AddSingleton<ITenantLifecycle>(fun (sp: IServiceProvider) -> MembershipCacheLifecycle.create sp)
        |> ignore

        services.AddSingleton<ITenantLifecycle>(fun (sp: IServiceProvider) -> JobSchedulerLifecycle.create sp)
        |> ignore

        services.AddSingleton<ITenantLifecycle>(fun (sp: IServiceProvider) -> DataSubjectRequestLifecycle.create sp)
        |> ignore

        // Phase 54d — conversation-store offboard purge (core-tier; the
        // conversation substrate lives in `ToolUp.Platform.Server`, not a
        // companion). Self-`Skipped`s when no `IConversationStore` is
        // composed, so registering it unconditionally under the enabled
        // mode is safe.
        services.AddSingleton<ITenantLifecycle>(fun (sp: IServiceProvider) -> ConversationStoreLifecycle.create sp)
        |> ignore

        // Phase 54a — register the background offboard job handler with the
        // scheduler at startup, so `DeprovisionTenantAsync` can enqueue +
        // dispatch a `LifecycleJobHandler` job. Deferred to an
        // `IHostedService` so the provider is fully built (the scheduler
        // singleton + the `ITenantLifecycle` hooks the handler resolves are
        // present). `RegisterHandler` is idempotent; a no-op when no
        // `IJobScheduler` is composed — `DeprovisionTenantAsync` then
        // returns its clear "requires an IJobScheduler" error and the inline
        // paths are unaffected (GP 13).
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(fun (sp: IServiceProvider) ->
            { new Microsoft.Extensions.Hosting.IHostedService with
                member _.StartAsync(_ct) =
                    match sp.GetService(typeof<IJobScheduler>) with
                    | :? IJobScheduler as scheduler ->
                        scheduler.RegisterHandler(
                            TenantLifecycleAggregator.LifecycleJobHandlerName,
                            LifecycleJobHandler.create sp
                        )

                        // Phase 54f — the scheduled / grace-period offboard
                        // poll handler. Same idempotent registration; a no-op
                        // when no scheduler is composed, so `ScheduleDeprovision`
                        // returns its clear "requires an IJobScheduler" error.
                        scheduler.RegisterHandler(
                            TenantLifecycleAggregator.ScheduledDeprovisionHandlerName,
                            ScheduledDeprovisionJobHandler.create sp
                        )
                    | _ -> ()

                    System.Threading.Tasks.Task.CompletedTask

                member _.StopAsync(_ct) =
                    System.Threading.Tasks.Task.CompletedTask
            })
        |> ignore