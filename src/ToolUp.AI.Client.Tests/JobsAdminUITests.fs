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
// new attempt landing. Phase 1110 adds each job's recent attempts — a
// dispatch mid-retry, a succeeded and a dead-lettered job — the re-run on
// any job the scheduler will trigger, and the attempts refresh after one.
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

/// One attempt row for `job`, as `GetRecentRuns` returns it.
let private run
    (job: JobDefinition)
    (attempt: int)
    (startedAt: DateTime)
    (status: JobRunStatus)
    (error: string option)
    (completedAt: DateTime option)
    : JobRun =
    {
        RunId = Guid.NewGuid()
        JobId = job.JobId
        ScopeId = job.ScopeId
        Attempt = attempt
        StartedAt = startedAt
        CompletedAt = completedAt
        Status = status
        Error = error
        DurationMs = completedAt |> Option.map (fun c -> int64 (c - startedAt).TotalMilliseconds)
        ExternalHandle = None
    }

let private loadedWith (jobs: JobDefinition list) =
    let m0, _ = init ()
    fst (update (JobsLoaded(Ok jobs)) m0)

let tests =
    testList "JobsAdminUI (Phases 1106, 1110)" [

        testCase "a dead-lettered job reads as failed for good, with its error verbatim"
        <| fun _ ->
            Expect.equal (standingOf deadLettered) JobStanding.PermanentlyFailed "dead-lettered ⇒ permanently failed"
            Expect.equal deadLettered.LastRunError (Some unfundedReason) "the recorded reason is the job's own"

            Expect.equal
                (rerunOffer deadLettered)
                RerunOffer.Offered
                "an active, failed-for-good job is offered a re-run"

        testCase "a scheduled job reads differently, and is offered the re-run too (Phase 1110)"
        <| fun _ ->
            Expect.equal (standingOf scheduled) JobStanding.NotYetRun "never run ⇒ not yet run"
            Expect.equal (rerunOffer scheduled) RerunOffer.Offered "an active job the scheduler will trigger"

            let retrying = {
                scheduled with
                    LastRunAt = Some failedAt
                    LastRunStatus = Some JobRunStatus.Failed
                    LastRunError = Some "transient"
            }

            Expect.equal (standingOf retrying) JobStanding.FailingWillRetry "failed with retries left ⇒ will retry"
            Expect.equal (rerunOffer retrying) RerunOffer.Offered "a re-run is offered whatever the last run did"

            let succeeded = {
                scheduled with
                    LastRunAt = Some failedAt
                    LastRunStatus = Some JobRunStatus.Succeeded
            }

            Expect.equal (rerunOffer succeeded) RerunOffer.Offered "a succeeded job can be run again now"

            // The model lets the offered job reach the confirmation.
            let m1, _ = update (RerunRequested scheduled.JobId) (loadedWith [ scheduled ])
            Expect.equal m1.Confirming (Some scheduled.JobId) "a scheduled job reaches the confirmation"

        testCase "a disabled or cancelled job is not offered the re-run, whatever its last run, and says why"
        <| fun _ ->
            let disabledScheduled = {
                scheduled with
                    Status = JobStatus.Disabled
            }

            let cancelledScheduled = {
                scheduled with
                    Status = JobStatus.Cancelled
            }

            Expect.equal (rerunOffer disabledScheduled) RerunOffer.RefusedDisabled "disabled refuses a manual trigger"
            Expect.equal (rerunOffer cancelledScheduled) RerunOffer.RefusedCancelled "cancelled never runs again"

            let m0 = loadedWith [ disabledScheduled; cancelledScheduled ]
            let m1, _ = update (RerunRequested disabledScheduled.JobId) m0
            Expect.equal m1.Confirming None "no confirmation for a disabled job"
            let m2, _ = update (RerunRequested cancelledScheduled.JobId) m0
            Expect.equal m2.Confirming None "no confirmation for a cancelled job"

            // A failed-for-good job is refused the same way: the status
            // decides, not the last run.
            Expect.equal
                (rerunOffer {
                    deadLettered with
                        Status = JobStatus.Disabled
                })
                RerunOffer.RefusedDisabled
                "a disabled failed job is refused too"

            Expect.equal
                (rerunOffer {
                    deadLettered with
                        Status = JobStatus.Cancelled
                })
                RerunOffer.RefusedCancelled
                "a cancelled failed job is refused too"

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

        // ─── Phase 1110 — recent attempts ───────────────────────────

        testCase "a dispatch mid-retry: the newest attempt is newer than the record, and reads as in progress"
        <| fun _ ->
            // The record still says the dispatch that dead-lettered on
            // `failedAt`; today's re-trigger has already failed once and
            // is waiting out its retry backoff.
            let today = failedAt.AddDays(1.0).AddMinutes 49.0

            let attempt1 =
                run deadLettered 1 today JobRunStatus.Failed (Some "provider timeout") (Some(today.AddSeconds 50.0))

            let runs = [ attempt1 ]

            Expect.equal (newerThanRecord deadLettered runs) (Some attempt1) "the record is behind this attempt"

            Expect.equal
                (attemptReading deadLettered attempt1)
                AttemptReading.RetryPending
                "failed, dispatch not ended ⇒ retry pending"

            Expect.isTrue (attemptInProgress (attemptReading deadLettered attempt1)) "retry pending reads in progress"
            Expect.equal attempt1.Error (Some "provider timeout") "the attempt's error is its own"

            // The retry starts: the running attempt is the newest.
            let attempt2 =
                run deadLettered 2 (today.AddMinutes 2.0) JobRunStatus.Running None None

            let both = [ attempt1; attempt2 ]

            Expect.equal (newestFirst both |> List.map _.Attempt) [ 2; 1 ] "newest first"
            Expect.equal (newerThanRecord deadLettered both) (Some attempt2) "the running attempt is the newest"

            Expect.equal (attemptReading deadLettered attempt2) AttemptReading.InProgress "running ⇒ in progress"

            Expect.isNone (attemptDuration attempt2) "no duration while it runs"

        testCase "a succeeded job: its attempts belong to the recorded dispatch, with their durations"
        <| fun _ ->
            let started = failedAt

            let succeededJob = {
                scheduled with
                    LastRunAt = Some(started.AddSeconds 10.0)
                    LastRunStatus = Some JobRunStatus.Succeeded
            }

            let first =
                run succeededJob 1 started JobRunStatus.Failed (Some "transient") (Some(started.AddMilliseconds 400.0))

            let second =
                run succeededJob 2 (started.AddSeconds 5.0) JobRunStatus.Succeeded None (Some(started.AddSeconds 6.5))

            let runs = [ first; second ]

            Expect.isNone (newerThanRecord succeededJob runs) "the record covers its newest attempt"
            Expect.equal (attemptReading succeededJob first) AttemptReading.Failed "an earlier failed attempt, ended"
            Expect.isFalse (attemptInProgress (attemptReading succeededJob first)) "not in progress"
            Expect.equal (attemptReading succeededJob second) AttemptReading.Succeeded "succeeded"

            Expect.equal
                (attemptDuration second |> Option.map formatDuration)
                (Some "1.5 s")
                "duration where it has ended"

            Expect.equal (attemptDuration first |> Option.map formatDuration) (Some "400 ms") "sub-second"

        testCase "a dead-lettered job: every attempt has ended, the last one failed for good"
        <| fun _ ->
            let t = failedAt.AddMinutes -3.0

            let attempts = [
                run deadLettered 1 t JobRunStatus.Failed (Some unfundedReason) (Some(t.AddSeconds 1.0))
                run
                    deadLettered
                    2
                    (t.AddMinutes 1.0)
                    JobRunStatus.Failed
                    (Some unfundedReason)
                    (Some(t.AddSeconds 61.0))
                run
                    deadLettered
                    2
                    (t.AddMinutes 2.0)
                    JobRunStatus.DeadLettered
                    (Some unfundedReason)
                    (Some(t.AddMinutes 2.0))
            ]

            Expect.isNone (newerThanRecord deadLettered attempts) "the record moved when the dispatch ended"

            Expect.equal
                (newestFirst attempts |> List.map (attemptReading deadLettered))
                [
                    AttemptReading.PermanentlyFailed
                    AttemptReading.Failed
                    AttemptReading.Failed
                ]
                "failed for good, then the ended failures"

            Expect.isTrue
                (attempts |> List.forall (fun r -> r.Error = Some unfundedReason))
                "each attempt carries its error verbatim"

        testCase "the list reads every job's attempts; expanding re-reads one"
        <| fun _ ->
            let m0, _ = init ()
            let m1, cmd = update (JobsLoaded(Ok [ scheduled; deadLettered ])) m0

            Expect.equal (m1.Attempts.TryFind scheduled.JobId) (Some Loading) "attempts read for the scheduled job"
            Expect.equal (m1.Attempts.TryFind deadLettered.JobId) (Some Loading) "and for the failed one"
            Expect.equal (List.length cmd) 2 "one attempts read per job"

            let older =
                run scheduled 1 failedAt JobRunStatus.Succeeded None (Some(failedAt.AddSeconds 1.0))

            let newer =
                run scheduled 1 (failedAt.AddDays 1.0) JobRunStatus.Succeeded None (Some(failedAt.AddDays 1.0))

            let m2, _ = update (AttemptsLoaded(scheduled.JobId, Ok [ older; newer ])) m1

            Expect.equal (m2.Attempts.TryFind scheduled.JobId) (Some(Loaded [ newer; older ])) "stored newest first"

            let m3, _ = update (AttemptsLoaded(deadLettered.JobId, Error "boom")) m2
            Expect.equal (m3.Attempts.TryFind deadLettered.JobId) (Some(LoadError "boom")) "a failed read says so"

            let m4, expandCmd = update (ToggleAttempts scheduled.JobId) m3
            Expect.isTrue (m4.Expanded.Contains scheduled.JobId) "expanded"
            Expect.isFalse (List.isEmpty expandCmd) "expanding re-reads the attempts"

            let m5, collapseCmd = update (ToggleAttempts scheduled.JobId) m4
            Expect.isFalse (m5.Expanded.Contains scheduled.JobId) "collapsed"
            Expect.isTrue (List.isEmpty collapseCmd) "collapsing reads nothing"

            // A refresh keeps what was on screen while the new read runs.
            let m6, _ = update (JobsLoaded(Ok [ scheduled ])) m5

            Expect.equal (m6.Attempts.TryFind scheduled.JobId) (Some(Loaded [ newer; older ])) "kept while re-read"

            Expect.isFalse (m6.Attempts.ContainsKey deadLettered.JobId) "a job no longer listed is dropped"

        testCase "after a re-run the attempts list opens and refreshes, so the new attempt appears as it starts"
        <| fun _ ->
            let id = scheduled.JobId
            let m0 = loadedWith [ scheduled ]
            let m1, _ = update (RerunRequested id) m0
            let m2, _ = update (RerunConfirmed id) m1
            Expect.isTrue (m2.Expanded.Contains id) "the attempts list opens on confirm"

            let m3, acceptedCmd = update (RerunAccepted(id, Ok())) m2
            Expect.equal (List.length acceptedCmd) 2 "accepted ⇒ the attempts are read, and the poll starts"

            let _, pollCmd = update (PollJob id) m3
            Expect.equal (List.length pollCmd) 2 "each poll reads the job and its attempts"

            // The read lands with the re-run's attempt just started.
            let started = run scheduled 1 (failedAt.AddDays 1.0) JobRunStatus.Running None None
            let m4, _ = update (AttemptsLoaded(id, Ok [ started ])) m3

            match m4.Attempts.TryFind id with
            | Some(Loaded runs) ->
                Expect.equal
                    (newerThanRecord scheduled runs)
                    (Some started)
                    "the new attempt shows, newer than the record"

                Expect.equal (attemptReading scheduled started) AttemptReading.InProgress "in progress"
            | _ -> failwith "attempts should be loaded"

            // When the job reports the attempt landed, one last read follows.
            let landed = {
                scheduled with
                    LastRunAt = Some(failedAt.AddDays(1.0).AddSeconds 5.0)
                    LastRunStatus = Some JobRunStatus.Succeeded
            }

            let m5, landedCmd = update (JobPolled(id, Ok(Some landed))) m4
            Expect.equal (m5.Reruns.TryFind id) (Some(RerunProgress.Landed JobStanding.Succeeded)) "landed"
            Expect.equal (List.length landedCmd) 1 "a final attempts read"
    ]