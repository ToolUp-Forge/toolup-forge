module ToolUp.Platform.Compose.JobSchedulerLifecycle

open System
open ToolUp.Platform
open ToolUp.Platform.Jobs

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

/// The first-party job-scheduler offboard hook, as a value.
let hook: TenantHook<IJobScheduler> = {
    Name = "job-scheduler"
    Requires = "an IJobScheduler (background scheduling)"
    Resolve =
        fun services ->
            match services.GetService(typeof<IJobScheduler>) with
            | :? IJobScheduler as scheduler -> Some scheduler
            | _ -> None
    OnProvisioned = None
    OnDeprovisioned =
        Some(fun scheduler scopeId _ -> async {
            let! jobs = scheduler.ListJobs scopeId
            let errors = ResizeArray<string>()

            for job in jobs do
                match! scheduler.Cancel(scopeId, job.JobId) with
                | Ok() -> ()
                | Error error -> errors.Add(JobTransitionError.message error)

            // A job that could not be cancelled fails the hook, so the
            // resumable sweep re-runs it rather than recording it done.
            if errors.Count = 0 then
                return LifecycleHookResult.Completed
            else
                return LifecycleHookResult.Failed(String.Join("; ", errors))
        })
    // Mutation-free preview: the scope's scheduled jobs (the jobs that
    // WOULD be cancelled), via the read-only ListJobs.
    Preview =
        Some(fun scheduler scopeId _ -> async {
            let! jobs = scheduler.ListJobs scopeId
            return jobs.Length, sprintf "%d scheduled job(s) would be cancelled" jobs.Length
        })
}

/// Construct the first-party job-scheduler lifecycle hook. Resolves the
/// active `IJobScheduler` from `services` on every call.
let create (services: IServiceProvider) : ITenantLifecycle = TenantHook.asLifecycle hook services