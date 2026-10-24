module ToolUp.AI.Client.Tests.JobsAdminUITests

open System
open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.Platform
open JobsAdminUI

// ─── Phase 1106 — the background-jobs admin's model ──────────────────
//
// Drives `JobsAdminUI`'s pure decisions and `update` over a dead-lettered
// job and a scheduled one: how each reads, the row order, which jobs are
// offered a re-run, and the re-run round-trip from confirmation to the
// new attempt landing.
//
// Fable-side (not .NET Expecto) for the reason `TenantLifecycleAdminUITests`
// gives: the module declares a module-level `Api.makeProxy<JobApi>`, whose
// reflection-based proxy builder raises under .NET reflection at
// static-init time. The commands `update` returns are not executed here;
// their results are fed back in as messages.

let private created = DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc)
let private failedAt = DateTime(2026, 10, 23, 6, 0, 0, DateTimeKind.Utc)

let private job (handler: string) (minute: int) : JobDefinition = {
    JobId = Guid.NewGuid()
    ScopeId = "team-abc123"
    Handler = handler
    Payload = "{}"
    Trigger = CronTrigger "0 6 * * *"
    Idempotency = None
    RetryPolicy = JobRetryPolicy.defaults
    ShardKey = None
    Precision = JobPrecision.Minute
    Status = JobStatus.Active
    CreatedAt = created.AddMinutes(float minute)
    CreatedBy = "owner-1"
    NextRunAt = Some(failedAt.AddDays 1.0)
    LastRunAt = None
    LastRunStatus = None
    LastRunError = None
    ConsecutiveFailures = 0
    Tags = Map.empty
}

let private unfundedReason =
    "unfunded: no usable AI provider for the run's scope: no provider profile"

/// A job its handler refused outright — recorded as `DeadLettered`.
let private deadLettered = {
    job "daily-narrative" 1 with
        LastRunAt = Some failedAt
        LastRunStatus = Some JobRunStatus.DeadLettered
        LastRunError = Some unfundedReason
        ConsecutiveFailures = 1
}

/// A job on its schedule that has not run yet.
let private scheduled = job "nightly-rollup" 0

let private loadedWith (jobs: JobDefinition list) =
    let m0, _ = init ()
    fst (update (JobsLoaded(Ok jobs)) m0)

let tests =
    testList "JobsAdminUI (Phase 1106)" [

        testCase "a dead-lettered job reads as failed for good, with its error verbatim"
        <| fun _ ->
            Expect.equal (standingOf deadLettered) JobStanding.PermanentlyFailed "dead-lettered ⇒ permanently failed"
            Expect.equal deadLettered.LastRunError (Some unfundedReason) "the recorded reason is the job's own"

            Expect.equal
                (rerunOffer deadLettered)
                RerunOffer.Offered
                "an active, failed-for-good job is offered a re-run"

        testCase "a scheduled job reads differently and is offered nothing"
        <| fun _ ->
            Expect.equal (standingOf scheduled) JobStanding.NotYetRun "never run ⇒ not yet run"
            Expect.equal (rerunOffer scheduled) RerunOffer.NotApplicable "nothing to re-run"

            let retrying = {
                scheduled with
                    LastRunAt = Some failedAt
                    LastRunStatus = Some JobRunStatus.Failed
                    LastRunError = Some "transient"
            }

            Expect.equal (standingOf retrying) JobStanding.FailingWillRetry "failed with retries left ⇒ will retry"
            Expect.equal (rerunOffer retrying) RerunOffer.NotApplicable "the scheduler retries it; no manual re-run"

        testCase "a disabled or cancelled failed job is not offered the re-run, and says why"
        <| fun _ ->
            Expect.equal
                (rerunOffer {
                    deadLettered with
                        Status = JobStatus.Disabled
                })
                RerunOffer.RefusedDisabled
                "disabled refuses a manual trigger"

            Expect.equal
                (rerunOffer {
                    deadLettered with
                        Status = JobStatus.Cancelled
                })
                RerunOffer.RefusedCancelled
                "cancelled never runs again"

            // The model refuses what the view would not show.
            let disabled = {
                deadLettered with
                    Status = JobStatus.Disabled
            }

            let m1, _ = update (RerunRequested disabled.JobId) (loadedWith [ disabled ])
            Expect.equal m1.Confirming None "no confirmation for a disabled job"

        testCase "permanently failed jobs are listed first, other orders kept"
        <| fun _ ->
            let retrying = {
                job "hourly-sync" 2 with
                    LastRunStatus = Some JobRunStatus.Failed
            }

            let succeeded = {
                job "weekly-report" 3 with
                    LastRunStatus = Some JobRunStatus.Succeeded
            }

            let laterDeadLetter = {
                job "monthly-close" 4 with
                    LastRunStatus = Some JobRunStatus.DeadLettered
            }

            let order =
                ordered [ scheduled; deadLettered; retrying; succeeded; laterDeadLetter ]
                |> List.map _.Handler

            Expect.equal
                order
                [
                    "daily-narrative"
                    "monthly-close"
                    "hourly-sync"
                    "nightly-rollup"
                    "weekly-report"
                ]
                "failed for good, then will-retry, then the rest, each band in API order"

        testCase "re-run round-trip: confirm, trigger, poll, the new attempt lands"
        <| fun _ ->
            let m0 = loadedWith [ scheduled; deadLettered ]
            let id = deadLettered.JobId

            // Asking for the re-run only opens the confirmation.
            let m1, _ = update (RerunRequested id) m0
            Expect.equal m1.Confirming (Some id) "confirmation pending"
            Expect.isFalse (m1.Reruns.ContainsKey id) "nothing triggered before confirming"

            // Cancelling closes it without a call.
            let mCancelled, _ = update RerunCancelled m1
            Expect.equal mCancelled.Confirming None "cancel closes the confirmation"
            Expect.isFalse (mCancelled.Reruns.ContainsKey id) "cancel triggers nothing"

            // Confirming issues TriggerOnce.
            let m2, _ = update (RerunConfirmed id) m1
            Expect.equal m2.Confirming None "confirmation closed"
            Expect.equal (m2.Reruns.TryFind id) (Some RerunProgress.Starting) "trigger in flight"

            // Accepted: waiting on an attempt newer than the failed one.
            let m3, _ = update (RerunAccepted(id, Ok())) m2

            Expect.equal
                (m3.Reruns.TryFind id)
                (Some(RerunProgress.Awaiting(Some failedAt, 0)))
                "awaiting an attempt after the recorded one"

            // A poll that still shows the old attempt keeps waiting.
            let m4, _ = update (JobPolled(id, Ok(Some deadLettered))) m3

            Expect.equal
                (m4.Reruns.TryFind id)
                (Some(RerunProgress.Awaiting(Some failedAt, 1)))
                "old attempt ⇒ keep polling"

            // A poll showing the new attempt still running keeps waiting too.
            let running = {
                deadLettered with
                    LastRunAt = Some(failedAt.AddHours 3.0)
                    LastRunStatus = Some JobRunStatus.Running
            }

            Expect.isFalse (attemptLanded (Some failedAt) running) "a running attempt has not landed"

            // The new attempt succeeded: the row and the progress both show it.
            let rerun = {
                deadLettered with
                    LastRunAt = Some(failedAt.AddHours 3.0)
                    LastRunStatus = Some JobRunStatus.Succeeded
                    LastRunError = None
                    ConsecutiveFailures = 0
            }

            let m5, _ = update (JobPolled(id, Ok(Some rerun))) m4

            Expect.equal
                (m5.Reruns.TryFind id)
                (Some(RerunProgress.Landed JobStanding.Succeeded))
                "the new attempt's result is shown"

            match m5.Jobs with
            | Loaded jobs ->
                let row = jobs |> List.find (fun j -> j.JobId = id)
                Expect.equal row.LastRunStatus (Some JobRunStatus.Succeeded) "the row carries the new attempt"
            | _ -> failwith "jobs should stay loaded"

        testCase "re-run refused by the server shows the reason; a long attempt stops polling"
        <| fun _ ->
            let m0 = loadedWith [ deadLettered ]
            let id = deadLettered.JobId
            let m1, _ = update (RerunRequested id) m0
            let m2, _ = update (RerunConfirmed id) m1

            let refused, _ =
                update (RerunAccepted(id, Error "Only team owners and admins can manage scheduled jobs.")) m2

            Expect.equal
                (refused.Reruns.TryFind id)
                (Some(RerunProgress.Refused "Only team owners and admins can manage scheduled jobs."))
                "the refusal is shown verbatim"

            let mutable m = fst (update (RerunAccepted(id, Ok())) m2)

            for _ in 1..maxPolls do
                m <- fst (update (JobPolled(id, Ok(Some deadLettered))) m)

            Expect.equal (m.Reruns.TryFind id) (Some RerunProgress.StillRunning) "bounded: stops waiting"
    ]