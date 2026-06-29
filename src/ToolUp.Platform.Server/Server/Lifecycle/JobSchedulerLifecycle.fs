module ToolUp.Platform.JobSchedulerLifecycle

open System
open ToolUp.Platform

// ─── Phase 54 — first-party job-scheduler lifecycle hook ─────────────
//
// On `OnDeprovisioned`, cancels every scheduled job in the offboarded
// scope so no recurring / event-driven job keeps firing against a dead
// tenant. Resolves the active `IJobScheduler` from DI per call (GP 12
// rule 4) and uses only the portable surface — `ListJobs` + `Cancel` —
// so it works against the in-process default and any distributed
// companion (Akka / Orleans / Hangfire) without an interface extension:
//   * `IJobScheduler` present — enumerate the scope's jobs and `Cancel`
//     each. `Cancel` is idempotent (cancelling an already-cancelled /
//     unknown job is a no-op), so a partially-cancelled scope re-runs
//     cleanly.
//   * no scheduler registered — `Skipped` (scheduling not enabled).
//
// Deliberately avoids adding a `CancelByScope` method to `IJobScheduler`
// (which would break every existing implementor + contract test for a
// convenience the enumerate-and-cancel loop already covers portably).
//
// `OnProvisioned` is a no-op `Skipped`: modules schedule jobs on demand,
// so provisioning has nothing to do.

type JobSchedulerLifecycle(services: IServiceProvider) =
    interface ITenantLifecycle with
        member _.Name = "job-scheduler"

        member _.OnProvisioned(_scopeId, _actorUserId) = async {
            return LifecycleHookResult.Skipped "no provisioning action — modules schedule jobs on demand"
        }

        member _.OnDeprovisioned(scopeId, _actorUserId) = async {
            match services.GetService(typeof<IJobScheduler>) with
            | :? IJobScheduler as scheduler ->
                let! jobs = scheduler.ListJobs scopeId

                for job in jobs do
                    do! scheduler.Cancel(scopeId, job.JobId)

                return LifecycleHookResult.Completed
            | _ -> return LifecycleHookResult.Skipped "no IJobScheduler registered (background scheduling not enabled)"
        }

    // Phase 54c — mutation-free preview: count the scope's scheduled jobs
    // (the jobs that WOULD be cancelled) via the read-only ListJobs,
    // without calling Cancel.
    interface ITenantLifecyclePreview with
        member _.OnDeprovisionPreview(scopeId, _actorUserId) = async {
            match services.GetService(typeof<IJobScheduler>) with
            | :? IJobScheduler as scheduler ->
                let! jobs = scheduler.ListJobs scopeId

                return
                    LifecyclePreviewItem.affecting
                        "job-scheduler"
                        jobs.Length
                        (sprintf "%d scheduled job(s) would be cancelled" jobs.Length)
            | _ ->
                return
                    LifecyclePreviewItem.affecting "job-scheduler" 0 "no IJobScheduler registered — no jobs to cancel"
        }

/// Construct the first-party job-scheduler lifecycle hook. Resolves the
/// active `IJobScheduler` from `services` on every call.
let create (services: IServiceProvider) : ITenantLifecycle =
    JobSchedulerLifecycle(services) :> ITenantLifecycle