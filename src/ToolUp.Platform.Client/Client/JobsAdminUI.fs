// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module JobsAdminUI

open System
open ToolUp.Elmish
open Feliz
open ToolUp.Platform

// ─── Phase 1106 — built-in background-jobs admin ────────────────────
//
// A job that fails for good — dead-lettered after its retries, or
// refused outright by its handler (`JobResult.PermanentFailure`, which
// the scheduler also records as `DeadLettered`) — is recorded on the job
// definition as `LastRunStatus = DeadLettered` with its `LastRunError`.
// Until this module nothing in the admin client showed it: an admin saw
// only a downstream page's empty state, which cannot tell "not yet" from
// "failed, and will never retry".
//
// This module is a view over what `JobApi` already serves, scoped as the
// API is (the caller's resolved scope, server-side): `ListJobs` for the
// table, `GetRecentRuns` for each job's recent attempts, and
// `TriggerOnce` for a confirmed re-run, with `GetJob` and the attempts
// polled until the new attempt lands. It adds no retry policy and no
// server surface.
//
// Phase 1110 — the definition's `LastRun*` fields move only when a
// dispatch ENDS, so while a dispatch is still retrying the record keeps
// the previous dispatch's outcome. The attempts list is what says what a
// job is doing now: an attempt that started after the record's
// `LastRunAt` belongs to a dispatch that has not ended, and the summary
// row says so. And the re-run is offered on any job the scheduler will
// trigger, not only a failed one.
//
// The decisions — how a job's last run and each attempt read, whether a
// re-run is offered, the row order, and when a polled job shows the new
// attempt — are pure functions below the model, so the client test
// harness pins them without rendering anything.
//
// A deployment on `ServerConfig.JobScheduler = NoJobScheduler` mounts no
// `JobApi` route; the `JobsAdmin` shell slot therefore defaults to
// `SlotFill.Empty` and this module is not injected at all.

// ─── The pure decisions ─────────────────────────────────────────────

/// How a job's LAST RUN reads in the table. Derived from
/// `JobDefinition.LastRunStatus` alone; the definition's own `Status`
/// (active / disabled / cancelled) is a separate badge.
[<RequireQualifiedAccess>]
type JobStanding =
    /// `DeadLettered` — failed for good; the scheduler will not retry it.
    | PermanentlyFailed
    /// `Failed` — the last attempt failed and retries remain.
    | FailingWillRetry
    /// `Pending`, `Running` or `AwaitingExternal`.
    | InProgress
    | Succeeded
    /// `ExternallyCancelled` — not a failure.
    | AttemptCancelled
    /// No run recorded yet.
    | NotYetRun

/// Whether the table offers a re-run, and if not, why.
[<RequireQualifiedAccess>]
type RerunOffer =
    /// An active job, whatever its last run did: the action is offered,
    /// and runs the job once in addition to its schedule.
    | Offered
    /// A disabled job. A disabled job refuses a manual trigger (the
    /// `IJobScheduler.TriggerOnce` contract), so the view says so instead
    /// of offering it.
    | RefusedDisabled
    /// A cancelled job — the scheduler refuses to trigger it, ever.
    | RefusedCancelled

/// The standing a job's last run gives it.
let standingOf (job: JobDefinition) : JobStanding =
    match job.LastRunStatus with
    | Some JobRunStatus.DeadLettered -> JobStanding.PermanentlyFailed
    | Some JobRunStatus.Failed -> JobStanding.FailingWillRetry
    | Some JobRunStatus.Pending
    | Some JobRunStatus.Running
    | Some JobRunStatus.AwaitingExternal -> JobStanding.InProgress
    | Some JobRunStatus.Succeeded -> JobStanding.Succeeded
    | Some JobRunStatus.ExternallyCancelled -> JobStanding.AttemptCancelled
    | None -> JobStanding.NotYetRun

/// Whether a re-run is offered for `job`: on every job whose trigger
/// preconditions hold, which is its `Status` alone.
let rerunOffer (job: JobDefinition) : RerunOffer =
    match job.Status with
    | JobStatus.Active -> RerunOffer.Offered
    | JobStatus.Disabled -> RerunOffer.RefusedDisabled
    | JobStatus.Cancelled -> RerunOffer.RefusedCancelled

/// The warning a job's last run SUCCEEDED with, if any
/// (`JobResult.SuccessWithWarning`): the job did its work, and the view
/// shows what it warned of beside its status. A warning on any other
/// standing is not shown — the record clears it on every other ending.
let warningOf (job: JobDefinition) : string option =
    match standingOf job, job.LastRunWarning with
    | JobStanding.Succeeded, Some warning when not (String.IsNullOrWhiteSpace warning) -> Some warning
    | _ -> None

/// Table order: permanently failed jobs first, then jobs failing with
/// retries left, then jobs that succeeded with a warning, then every other
/// job. Within each band the order is the API's own (`CreatedAt`
/// ascending) — `List.sortBy` is stable.
let ordered (jobs: JobDefinition list) : JobDefinition list =
    jobs
    |> List.sortBy (fun job ->
        match standingOf job, warningOf job with
        | JobStanding.PermanentlyFailed, _ -> 0
        | JobStanding.FailingWillRetry, _ -> 1
        | _, Some _ -> 2
        | _ -> 3)

/// Whether `job`, read back after a re-run was accepted, shows the NEW
/// attempt finished: its last run is no longer the one recorded when the
/// re-run was asked for (`previousLastRunAt`), and that attempt is not
/// still in progress.
let attemptLanded (previousLastRunAt: DateTime option) (job: JobDefinition) : bool =
    job.LastRunAt <> previousLastRunAt && standingOf job <> JobStanding.InProgress

/// How many times a re-run polls `GetJob` before it stops waiting.
let maxPolls = 20

/// Delay between polls, in milliseconds.
let pollIntervalMs = 1500

/// How many recent attempts a job's attempts list reads.
let recentAttemptCount = 5

/// How one attempt (`JobRun`) reads in a job's attempts list.
[<RequireQualifiedAccess>]
type AttemptReading =
    /// `Pending`, `Running` or `AwaitingExternal`.
    | InProgress
    /// `Failed`, in a dispatch that has not ended: the scheduler is
    /// waiting out the retry backoff before the next attempt.
    | RetryPending
    /// `Failed`, in a dispatch that has since ended.
    | Failed
    | Succeeded
    /// `DeadLettered` — the dispatch ended and will not be retried.
    | PermanentlyFailed
    /// `ExternallyCancelled`.
    | Cancelled

/// Whether `run` started after the job's record last moved, i.e. it
/// belongs to a dispatch that has not ended. The record's `LastRunAt` is
/// stamped when a dispatch ends, after every row that dispatch wrote.
let startedAfterRecord (job: JobDefinition) (run: JobRun) : bool =
    match job.LastRunAt with
    | Some recorded -> run.StartedAt > recorded
    | None -> true

/// How `run`, one of `job`'s attempts, reads.
let attemptReading (job: JobDefinition) (run: JobRun) : AttemptReading =
    match run.Status with
    | JobRunStatus.Pending
    | JobRunStatus.Running
    | JobRunStatus.AwaitingExternal -> AttemptReading.InProgress
    | JobRunStatus.Failed when startedAfterRecord job run -> AttemptReading.RetryPending
    | JobRunStatus.Failed -> AttemptReading.Failed
    | JobRunStatus.Succeeded -> AttemptReading.Succeeded
    | JobRunStatus.DeadLettered -> AttemptReading.PermanentlyFailed
    | JobRunStatus.ExternallyCancelled -> AttemptReading.Cancelled

/// Whether an attempt reading is still in progress — running, or waiting
/// for its retry.
let attemptInProgress (reading: AttemptReading) : bool =
    match reading with
    | AttemptReading.InProgress
    | AttemptReading.RetryPending -> true
    | _ -> false

/// The attempts newest first, by start time.
let newestFirst (runs: JobRun list) : JobRun list =
    runs |> List.sortByDescending _.StartedAt

/// The newest attempt, when it started after the job's record last
/// moved — the case where `LastRunAt` / `LastRunStatus` still describe an
/// earlier dispatch.
let newerThanRecord (job: JobDefinition) (runs: JobRun list) : JobRun option =
    newestFirst runs |> List.tryHead |> Option.filter (startedAfterRecord job)

/// How long an attempt took, where it has ended.
let attemptDuration (run: JobRun) : TimeSpan option =
    match run.CompletedAt, run.DurationMs with
    | Some _, Some ms -> Some(TimeSpan.FromMilliseconds(float ms))
    | Some completed, None -> Some(completed - run.StartedAt)
    | None, _ -> None

/// A duration as the attempts list shows it.
let formatDuration (span: TimeSpan) : string =
    if span.TotalSeconds < 1.0 then
        $"{int span.TotalMilliseconds} ms"
    elif span.TotalMinutes < 1.0 then
        $"{Math.Round(span.TotalSeconds, 1)} s"
    else
        $"{int span.TotalMinutes} min {span.Seconds} s"

// ─── Model ──────────────────────────────────────────────────────────

type LoadState<'T> =
    | Loading
    | Loaded of 'T
    | LoadError of string

/// Where a re-run asked for in this session stands.
[<RequireQualifiedAccess>]
type RerunProgress =
    /// `TriggerOnce` is in flight.
    | Starting
    /// Accepted; polling for an attempt newer than the one recorded when
    /// the re-run was asked for. `Polls` counts the reads made so far.
    | Awaiting of previousLastRunAt: DateTime option * polls: int
    /// The new attempt finished with this standing.
    | Landed of JobStanding
    /// Accepted, but no new attempt finished within `maxPolls` reads.
    | StillRunning
    /// The server refused the re-run, with its reason.
    | Refused of string

type Model = {
    Jobs: LoadState<JobDefinition list>
    /// The job whose re-run is awaiting confirmation, if any.
    Confirming: Guid option
    /// Re-runs asked for in this session, by job id.
    Reruns: Map<Guid, RerunProgress>
    /// Each listed job's recent attempts, newest first, by job id. Read
    /// for every job when the list loads, so the summary row can say when
    /// the record is behind its newest attempt.
    Attempts: Map<Guid, LoadState<JobRun list>>
    /// The jobs whose attempts list is expanded.
    Expanded: Set<Guid>
    /// Transient error from a poll. Read failures land in `Jobs`.
    LastError: string option
}

type Msg =
    | Refresh
    | JobsLoaded of Result<JobDefinition list, string>
    | ToggleAttempts of Guid
    | AttemptsLoaded of Guid * Result<JobRun list, string>
    | RerunRequested of Guid
    | RerunCancelled
    | RerunConfirmed of Guid
    | RerunAccepted of Guid * Result<unit, string>
    | PollJob of Guid
    | JobPolled of Guid * Result<JobDefinition option, string>
    | DismissError

// ─── API proxy ──────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see
// UserSession.fs + SDK.Client.fs installRequestGuard.
let private jobApi: JobApi =
    Api.makeProxy<JobApi> (customOptions = UserSession.withRequestHeaders)

// ─── Init / update ──────────────────────────────────────────────────

let private loadJobsCmd () =
    Cmd.OfRemoting.call jobApi.ListJobs () (Ok >> JobsLoaded) (fun e -> JobsLoaded(Error e.Message))

let private loadAttemptsCmd (jobId: Guid) : Cmd<Msg> =
    Cmd.OfRemoting.call
        jobApi.GetRecentRuns
        (jobId, recentAttemptCount)
        (fun runs -> AttemptsLoaded(jobId, Ok runs))
        (fun e -> AttemptsLoaded(jobId, Error e.Message))

let private pollCmd (jobId: Guid) : Cmd<Msg> =
    Cmd.OfAsync.perform (fun () -> Async.Sleep pollIntervalMs) () (fun _ -> PollJob jobId)

let init () =
    {
        Jobs = Loading
        Confirming = None
        Reruns = Map.empty
        Attempts = Map.empty
        Expanded = Set.empty
        LastError = None
    },
    loadJobsCmd ()

let private findJob (model: Model) (jobId: Guid) : JobDefinition option =
    match model.Jobs with
    | Loaded jobs -> jobs |> List.tryFind (fun j -> j.JobId = jobId)
    | _ -> None

let private replaceJob (job: JobDefinition) (model: Model) : Model =
    match model.Jobs with
    | Loaded jobs -> {
        model with
            Jobs = Loaded(jobs |> List.map (fun j -> if j.JobId = job.JobId then job else j))
      }
    | _ -> model

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | Refresh -> { model with Jobs = Loading }, loadJobsCmd ()

    | JobsLoaded(Ok jobs) ->
        // A job already showing attempts keeps them on screen while the
        // fresh read is in flight; a job seen for the first time reads
        // as loading.
        let attempts =
            jobs
            |> List.map (fun job -> job.JobId, model.Attempts.TryFind job.JobId |> Option.defaultValue Loading)
            |> Map.ofList

        {
            model with
                Jobs = Loaded jobs
                Attempts = attempts
        },
        Cmd.batch (jobs |> List.map (fun job -> loadAttemptsCmd job.JobId))

    | JobsLoaded(Error message) -> { model with Jobs = LoadError message }, Cmd.none

    | ToggleAttempts jobId ->
        if model.Expanded.Contains jobId then
            {
                model with
                    Expanded = model.Expanded.Remove jobId
            },
            Cmd.none
        else
            // Expanding reads the attempts again: the list is about what
            // the job is doing NOW, not when the table was loaded.
            {
                model with
                    Expanded = model.Expanded.Add jobId
            },
            loadAttemptsCmd jobId

    | AttemptsLoaded(jobId, Ok runs) ->
        {
            model with
                Attempts = model.Attempts |> Map.add jobId (Loaded(newestFirst runs))
        },
        Cmd.none

    | AttemptsLoaded(jobId, Error message) ->
        {
            model with
                Attempts = model.Attempts |> Map.add jobId (LoadError message)
        },
        Cmd.none

    | RerunRequested jobId ->
        // Only a job the table offers the action for can reach the
        // confirmation — the model refuses what the view would not show.
        match findJob model jobId with
        | Some job when rerunOffer job = RerunOffer.Offered -> { model with Confirming = Some jobId }, Cmd.none
        | _ -> model, Cmd.none

    | RerunCancelled -> { model with Confirming = None }, Cmd.none

    | RerunConfirmed jobId ->
        match model.Confirming, findJob model jobId with
        | Some confirming, Some job when confirming = jobId && rerunOffer job = RerunOffer.Offered ->
            let cmd =
                Cmd.OfRemoting.call jobApi.TriggerOnce jobId (fun result -> RerunAccepted(jobId, result)) (fun e ->
                    RerunAccepted(jobId, Error e.Message))

            // The attempts list opens, so the new attempt is seen as it
            // starts.
            {
                model with
                    Confirming = None
                    Reruns = model.Reruns |> Map.add jobId RerunProgress.Starting
                    Expanded = model.Expanded.Add jobId
            },
            cmd
        | _ -> { model with Confirming = None }, Cmd.none

    | RerunAccepted(jobId, Ok()) ->
        let previous = findJob model jobId |> Option.bind _.LastRunAt

        {
            model with
                Reruns = model.Reruns |> Map.add jobId (RerunProgress.Awaiting(previous, 0))
        },
        Cmd.batch [ loadAttemptsCmd jobId; pollCmd jobId ]

    | RerunAccepted(jobId, Error reason) ->
        {
            model with
                Reruns = model.Reruns |> Map.add jobId (RerunProgress.Refused reason)
        },
        Cmd.none

    | PollJob jobId ->
        match model.Reruns.TryFind jobId with
        | Some(RerunProgress.Awaiting _) ->
            model,
            Cmd.batch [
                Cmd.OfRemoting.call jobApi.GetJob jobId (fun job -> JobPolled(jobId, Ok job)) (fun e ->
                    JobPolled(jobId, Error e.Message))
                loadAttemptsCmd jobId
            ]
        | _ -> model, Cmd.none

    | JobPolled(jobId, result) ->
        match model.Reruns.TryFind jobId, result with
        | Some(RerunProgress.Awaiting(previous, _)), Ok(Some job) when attemptLanded previous job ->
            {
                (replaceJob job model) with
                    Reruns = model.Reruns |> Map.add jobId (RerunProgress.Landed(standingOf job))
            },
            // One last read, so the list ends on the attempt that landed.
            loadAttemptsCmd jobId
        | Some(RerunProgress.Awaiting(previous, polls)), Ok _ ->
            let polls = polls + 1

            if polls >= maxPolls then
                {
                    model with
                        Reruns = model.Reruns |> Map.add jobId RerunProgress.StillRunning
                },
                Cmd.none
            else
                {
                    model with
                        Reruns = model.Reruns |> Map.add jobId (RerunProgress.Awaiting(previous, polls))
                },
                pollCmd jobId
        | Some(RerunProgress.Awaiting _), Error message ->
            {
                model with
                    Reruns = model.Reruns |> Map.add jobId RerunProgress.StillRunning
                    LastError = Some message
            },
            Cmd.none
        | _ -> model, Cmd.none

    | DismissError -> { model with LastError = None }, Cmd.none

// ─── View ───────────────────────────────────────────────────────────

let private standingLabel (msgs: JobsAdminMessages) (standing: JobStanding) =
    match standing with
    | JobStanding.PermanentlyFailed -> msgs.StandingPermanentlyFailed
    | JobStanding.FailingWillRetry -> msgs.StandingFailingWillRetry
    | JobStanding.InProgress -> msgs.StandingInProgress
    | JobStanding.Succeeded -> msgs.StandingSucceeded
    | JobStanding.AttemptCancelled -> msgs.StandingAttemptCancelled
    | JobStanding.NotYetRun -> msgs.StandingNotYetRun

let private standingPill (msgs: JobsAdminMessages) (standing: JobStanding) =
    let cls =
        match standing with
        | JobStanding.PermanentlyFailed -> "bg-red-100 text-red-700 border-red-200"
        | JobStanding.FailingWillRetry -> "bg-yellow-100 text-yellow-700 border-yellow-200"
        | JobStanding.InProgress -> "bg-blue-100 text-blue-700 border-blue-200"
        | JobStanding.Succeeded -> "bg-green-100 text-green-700 border-green-200"
        | JobStanding.AttemptCancelled
        | JobStanding.NotYetRun -> "bg-gray-100 text-gray-700 border-gray-200"

    Html.span [
        prop.className $"inline-block px-2 py-0.5 text-xs rounded border {cls}"
        prop.text (standingLabel msgs standing)
    ]

let private errorBanner (msgs: JobsAdminMessages) (message: string) (dispatch: Msg -> unit) =
    Html.div [
        prop.className
            "flex items-start justify-between p-3 mb-4 text-sm border rounded bg-red-50 border-red-200 text-red-800"
        prop.children [
            Html.span [ prop.text message ]
            Html.button [
                prop.className "ml-4 text-xs underline"
                prop.text msgs.Dismiss
                prop.onClick (fun _ -> dispatch DismissError)
            ]
        ]
    ]

let private formatTime (time: DateTime) =
    time.ToString("yyyy-MM-dd HH:mm:ss") + " UTC"

let private rerunCell (msgs: JobsAdminMessages) (model: Model) (dispatch: Msg -> unit) (job: JobDefinition) =
    let progress =
        match model.Reruns.TryFind job.JobId with
        | Some RerunProgress.Starting -> Some msgs.RerunStarting
        | Some(RerunProgress.Awaiting _) -> Some msgs.RerunAwaiting
        | Some(RerunProgress.Landed standing) -> Some(msgs.RerunLanded(standingLabel msgs standing))
        | Some RerunProgress.StillRunning -> Some msgs.RerunStillRunning
        | Some(RerunProgress.Refused reason) -> Some(msgs.RerunRefused reason)
        | None -> None

    let inFlight =
        match model.Reruns.TryFind job.JobId with
        | Some RerunProgress.Starting
        | Some(RerunProgress.Awaiting _) -> true
        | _ -> false

    Html.div [
        prop.className "space-y-1"
        prop.children [
            match rerunOffer job with
            | RerunOffer.Offered when model.Confirming = Some job.JobId ->
                Html.div [
                    prop.className "space-y-1"
                    prop.children [
                        Html.p [
                            prop.className "text-xs text-gray-700"
                            prop.text (msgs.RerunConfirmPrompt job.Handler)
                        ]
                        Html.div [
                            prop.className "space-x-3"
                            prop.children [
                                Html.button [
                                    prop.className "text-xs underline text-red-700"
                                    prop.text msgs.RerunConfirm
                                    prop.onClick (fun _ -> dispatch (RerunConfirmed job.JobId))
                                ]
                                Html.button [
                                    prop.className "text-xs underline"
                                    prop.text msgs.RerunCancel
                                    prop.onClick (fun _ -> dispatch RerunCancelled)
                                ]
                            ]
                        ]
                    ]
                ]
            | RerunOffer.Offered when not inFlight ->
                Html.button [
                    prop.className "text-xs underline"
                    prop.text msgs.RerunButton
                    prop.onClick (fun _ -> dispatch (RerunRequested job.JobId))
                ]
            | RerunOffer.RefusedDisabled ->
                Html.p [
                    prop.className "text-xs text-gray-500"
                    prop.text msgs.RerunUnavailableDisabled
                ]
            | RerunOffer.RefusedCancelled ->
                Html.p [
                    prop.className "text-xs text-gray-500"
                    prop.text msgs.RerunUnavailableCancelled
                ]
            | RerunOffer.Offered -> Html.none
            match progress with
            | Some text -> Html.p [ prop.className "text-xs text-gray-700"; prop.text text ]
            | None -> Html.none
        ]
    ]

let private attemptLabel (msgs: JobsAdminMessages) (reading: AttemptReading) =
    match reading with
    | AttemptReading.InProgress -> msgs.StandingInProgress
    | AttemptReading.RetryPending -> msgs.AttemptRetryPending
    | AttemptReading.Failed -> msgs.AttemptFailed
    | AttemptReading.Succeeded -> msgs.StandingSucceeded
    | AttemptReading.PermanentlyFailed -> msgs.StandingPermanentlyFailed
    | AttemptReading.Cancelled -> msgs.StandingAttemptCancelled

let private attemptPill (msgs: JobsAdminMessages) (reading: AttemptReading) =
    let cls =
        match reading with
        | AttemptReading.PermanentlyFailed -> "bg-red-100 text-red-700 border-red-200"
        | AttemptReading.Failed -> "bg-yellow-100 text-yellow-700 border-yellow-200"
        | AttemptReading.InProgress
        | AttemptReading.RetryPending -> "bg-blue-100 text-blue-700 border-blue-200"
        | AttemptReading.Succeeded -> "bg-green-100 text-green-700 border-green-200"
        | AttemptReading.Cancelled -> "bg-gray-100 text-gray-700 border-gray-200"

    Html.span [
        prop.className $"inline-block px-2 py-0.5 text-xs rounded border {cls}"
        prop.text (attemptLabel msgs reading)
    ]

/// A success's warning, shown in amber beside the status it qualifies.
let private warningNote (warning: string) =
    Html.p [
        prop.className "mt-1 text-xs text-amber-700 font-mono break-all"
        prop.text warning
    ]

let private attemptItem (msgs: JobsAdminMessages) (job: JobDefinition) (run: JobRun) =
    let reading = attemptReading job run

    Html.li [
        prop.key (string run.RunId)
        prop.className "py-1"
        prop.children [
            Html.div [
                prop.className "flex flex-wrap items-center gap-2 text-xs text-gray-700"
                prop.children [
                    Html.span [ prop.className "font-medium"; prop.text (msgs.AttemptNumber run.Attempt) ]
                    attemptPill msgs reading
                    Html.span [ prop.text (msgs.AttemptStarted(formatTime run.StartedAt)) ]
                    match attemptDuration run with
                    | Some span -> Html.span [ prop.text (msgs.AttemptTook(formatDuration span)) ]
                    | None -> Html.none
                ]
            ]
            match run.Error with
            | Some error ->
                Html.p [
                    prop.className "mt-0.5 text-xs text-red-700"
                    prop.children [
                        Html.span [ prop.className "font-medium"; prop.text msgs.ReasonLabel ]
                        Html.span [ prop.className "ml-1 font-mono break-all"; prop.text error ]
                    ]
                ]
            | None -> Html.none
            match run.Status, run.Warning with
            | JobRunStatus.Succeeded, Some warning -> warningNote warning
            | _ -> Html.none
        ]
    ]

let private attemptsRow (msgs: JobsAdminMessages) (model: Model) (job: JobDefinition) =
    Html.tr [
        prop.key (string job.JobId + "-attempts")
        prop.className "border-b border-gray-100 bg-gray-50"
        prop.children [
            Html.td [
                prop.colSpan 4
                prop.className "px-4 py-2"
                prop.children [
                    match model.Attempts.TryFind job.JobId with
                    | None
                    | Some Loading -> Html.p [ prop.className "text-xs text-gray-500"; prop.text msgs.AttemptsLoading ]
                    | Some(LoadError message) ->
                        Html.p [
                            prop.className "text-xs text-red-700"
                            prop.text (msgs.AttemptsLoadFailed message)
                        ]
                    | Some(Loaded []) -> Html.p [ prop.className "text-xs text-gray-500"; prop.text msgs.NoAttempts ]
                    | Some(Loaded runs) -> Html.ul (runs |> List.map (attemptItem msgs job))
                ]
            ]
        ]
    ]

let private summaryRow (msgs: JobsAdminMessages) (model: Model) (dispatch: Msg -> unit) (job: JobDefinition) =
    let standing = standingOf job
    let expanded = model.Expanded.Contains job.JobId

    let newer =
        match model.Attempts.TryFind job.JobId with
        | Some(Loaded runs) -> newerThanRecord job runs
        | _ -> None

    Html.tr [
        prop.key (string job.JobId)
        prop.className (
            if standing = JobStanding.PermanentlyFailed then
                "border-b border-gray-100 bg-red-50"
            else
                "border-b border-gray-100"
        )
        prop.children [
            Html.td [
                prop.className "py-2 pr-4 align-top"
                prop.children [
                    Html.div [ prop.className "text-sm font-medium"; prop.text job.Handler ]
                    Html.div [
                        prop.className "text-xs text-gray-500 font-mono"
                        prop.text (string job.JobId)
                    ]
                    match job.Status with
                    | JobStatus.Disabled ->
                        Html.span [ prop.className "text-xs text-gray-500"; prop.text msgs.JobDisabled ]
                    | JobStatus.Cancelled ->
                        Html.span [ prop.className "text-xs text-gray-500"; prop.text msgs.JobCancelled ]
                    | JobStatus.Active -> Html.none
                    Html.div [
                        Html.button [
                            prop.className "text-xs underline text-gray-600"
                            prop.ariaExpanded expanded
                            prop.text (if expanded then msgs.HideAttempts else msgs.ShowAttempts)
                            prop.onClick (fun _ -> dispatch (ToggleAttempts job.JobId))
                        ]
                    ]
                ]
            ]
            Html.td [
                prop.className "py-2 pr-4 text-sm align-top"
                prop.children [
                    Html.div [
                        prop.text (job.LastRunAt |> Option.map formatTime |> Option.defaultValue msgs.NeverRun)
                    ]
                    match newer with
                    | Some run ->
                        Html.p [
                            prop.className "mt-1 text-xs text-blue-700"
                            prop.text (msgs.NewerAttempt run.Attempt (formatTime run.StartedAt))
                        ]
                    | None -> Html.none
                ]
            ]
            Html.td [
                prop.className "py-2 pr-4 align-top"
                prop.children [
                    standingPill msgs standing
                    match standing, job.LastRunError with
                    | (JobStanding.PermanentlyFailed | JobStanding.FailingWillRetry), Some error ->
                        Html.p [
                            prop.className "mt-1 text-xs text-red-700"
                            prop.children [
                                Html.span [ prop.className "font-medium"; prop.text msgs.ReasonLabel ]
                                Html.span [ prop.className "ml-1 font-mono break-all"; prop.text error ]
                            ]
                        ]
                    | _ -> Html.none
                    match warningOf job with
                    | Some warning -> warningNote warning
                    | None -> Html.none
                ]
            ]
            Html.td [
                prop.className "py-2 align-top"
                prop.children [ rerunCell msgs model dispatch job ]
            ]
        ]
    ]

/// A job's summary row, and its attempts list beneath it when expanded.
let private jobRows (msgs: JobsAdminMessages) (model: Model) (dispatch: Msg -> unit) (job: JobDefinition) = [
    summaryRow msgs model dispatch job
    if model.Expanded.Contains job.JobId then
        attemptsRow msgs model job
]

let private jobsTable (msgs: JobsAdminMessages) (model: Model) (dispatch: Msg -> unit) (jobs: JobDefinition list) =
    if jobs.IsEmpty then
        Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.NoJobs ]
    else
        Html.table [
            prop.className "w-full text-left"
            prop.children [
                Html.thead [
                    Html.tr [
                        prop.className "border-b border-gray-200 text-xs uppercase text-gray-500"
                        prop.children [
                            Html.th [ prop.className "py-2 pr-4 font-medium"; prop.text msgs.ColumnJob ]
                            Html.th [ prop.className "py-2 pr-4 font-medium"; prop.text msgs.ColumnLastRun ]
                            Html.th [ prop.className "py-2 pr-4 font-medium"; prop.text msgs.ColumnStatus ]
                            Html.th [ prop.className "py-2 font-medium"; prop.text msgs.ColumnActions ]
                        ]
                    ]
                ]
                Html.tbody (ordered jobs |> List.collect (jobRows msgs model dispatch))
            ]
        ]

/// The module body as a React COMPONENT, for the reason
/// `MigrationStatusUI`'s is one: a module's `view` is invoked inline by
/// the shell's own render, so a hook there would join the shell's hook
/// order.
[<ReactComponent>]
let private JobsAdminBody (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).JobsAdmin

    let loading =
        match model.Jobs with
        | Loading -> true
        | _ -> false

    Html.div [
        prop.className "flex-1 p-6 overflow-y-auto"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between mb-4"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.h2 [ prop.className "text-lg font-semibold"; prop.text msgs.Heading ]
                            Html.p [ prop.className "text-xs text-gray-500"; prop.text msgs.Subheading ]
                        ]
                    ]
                    Html.button [
                        prop.className "px-3 py-1.5 text-sm border rounded border-gray-300 disabled:opacity-40"
                        prop.disabled loading
                        prop.text (if loading then msgs.Refreshing else msgs.Refresh)
                        prop.onClick (fun _ -> dispatch Refresh)
                    ]
                ]
            ]
            match model.LastError with
            | Some message -> errorBanner msgs message dispatch
            | None -> Html.none
            match model.Jobs with
            | Loading -> Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.Loading ]
            | LoadError message -> errorBanner msgs message dispatch
            | Loaded jobs -> jobsTable msgs model dispatch jobs
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    Html.div [
        prop.className "flex flex-col h-full"
        prop.children [ JobsAdminBody model dispatch ]
    ]

// ─── Module creation ────────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `create` registers the module under it.
let moduleId = "_sdk.Jobs"

let create (config: ModuleLabel option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Background Jobs"

    let icon =
        config
        |> Option.map _.Icon
        |> Option.defaultValue ToolUp.Platform.Icons.dataLoading

    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId moduleId
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Team Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.TeamOwnerAdmin
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register

/// The "Background jobs" administration tile. Like every slot tile it is
/// built for whichever module fills the `JobsAdmin` slot (`owner`), so a
/// replacement keeps the tile and its click-through. The headline key is
/// read from the shared `HomeWidgetContext.Data` bag — supply
/// `"_sdk.admin.jobs"` from an `IHomeWidgetDataProvider` (for example the
/// count of permanently failed jobs) to put a live figure on the tile.
let adminTile (owner: ModuleDefinition) : AdminTile = {
    OwnerModuleId = owner.Id
    Widget = {
        Id = "_sdk.tile.jobs"
        Title = owner.Name
        Icon = owner.Icon
        Weight = 25
        Body =
            AdminTileBody.summary
                "_sdk.admin.jobs"
                "Scheduled background jobs and their recent attempts, with any that failed and will not retry listed first, their reason, and a re-run on any active job."
    }
}