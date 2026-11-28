// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The background-jobs admin as a Fuaran-UI tree, built and drawn in a file of
/// its own, which `JobsAdminUI`'s view mounts through `TreeSurface.deferred`
/// (the shell reads that module at boot; see `TeamManagerTree` for the trade).
/// Nothing imports this file statically.
///
/// A job's row shows the job and its own entries in the model's maps (its
/// recent attempts, its re-run progress, whether its attempts are open), so a
/// row is kept on those (`KeepScoped.RowInputs`): a poll, or one job's attempts
/// arriving, builds that job's row alone. Row ids come from the job's id and
/// every row message carries it. The poll is the declared effect value
/// `update` returns; the tree only dispatches.
module internal JobsAdminTree

open System
open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open JobsAdminUI
open KeepScoped

/// An instant as the table shows it, from its components.
let private formatTime (time: DateTime) =
    sprintf "%04d-%02d-%02d %02d:%02d:%02d UTC" time.Year time.Month time.Day time.Hour time.Minute time.Second

let private badge (id: string) (variant: BadgeVariant) (label: string) : Node<'Msg> =
    Fuaran.badge id {
        Label = Nodes.literal label
        Variant = variant
    }

let private standingLabel (msgs: JobsAdminMessages) (standing: JobStanding) =
    match standing with
    | JobStanding.PermanentlyFailed -> msgs.StandingPermanentlyFailed
    | JobStanding.FailingWillRetry -> msgs.StandingFailingWillRetry
    | JobStanding.InProgress -> msgs.StandingInProgress
    | JobStanding.Succeeded -> msgs.StandingSucceeded
    | JobStanding.AttemptCancelled -> msgs.StandingAttemptCancelled
    | JobStanding.NotYetRun -> msgs.StandingNotYetRun

let private standingBadge (id: string) (msgs: JobsAdminMessages) (standing: JobStanding) =
    let variant =
        match standing with
        | JobStanding.PermanentlyFailed -> BadgeVariant.Critical
        | JobStanding.FailingWillRetry -> BadgeVariant.Warning
        | JobStanding.InProgress -> BadgeVariant.Brand
        | JobStanding.Succeeded -> BadgeVariant.Success
        | JobStanding.AttemptCancelled
        | JobStanding.NotYetRun -> BadgeVariant.Neutral

    badge id variant (standingLabel msgs standing)

let private attemptBadge (id: string) (msgs: JobsAdminMessages) (reading: AttemptReading) =
    let variant, label =
        match reading with
        | AttemptReading.PermanentlyFailed -> BadgeVariant.Critical, msgs.StandingPermanentlyFailed
        | AttemptReading.Failed -> BadgeVariant.Warning, msgs.AttemptFailed
        | AttemptReading.InProgress -> BadgeVariant.Brand, msgs.StandingInProgress
        | AttemptReading.RetryPending -> BadgeVariant.Brand, msgs.AttemptRetryPending
        | AttemptReading.Succeeded -> BadgeVariant.Success, msgs.StandingSucceeded
        | AttemptReading.Cancelled -> BadgeVariant.Neutral, msgs.StandingAttemptCancelled

    badge id variant label

/// A failure's reason: the label, then what the scheduler recorded.
let private reason (id: string) (msgs: JobsAdminMessages) (error: string) : Node<'Msg> =
    Nodes.Layout.row id [
        Nodes.text (Nodes.Ids.child id "label") msgs.ReasonLabel
        Nodes.text (Nodes.Ids.child id "text") error
    ]

// ─── A job's row ─────────────────────────────────────────────────────

/// What one job's row reads: the job and its own entries in the model.
type private JobRow = {
    Msgs: JobsAdminMessages
    Job: JobDefinition
    Attempts: Remote<JobRun list, string> option
    Rerun: RerunProgress option
    Expanded: bool
}

let private sameEntry (a: 'T option) (b: 'T option) =
    match a, b with
    | Some a, Some b -> sameObject a b
    | None, None -> true
    | _ -> false

let private inputs =
    RowInputs(
        (fun (row: JobRow) -> row.Job.JobId),
        fun a b ->
            sameObject a.Msgs b.Msgs
            && sameObject a.Job b.Job
            && sameEntry a.Attempts b.Attempts
            && sameEntry a.Rerun b.Rerun
            && a.Expanded = b.Expanded
    )

let private columns (msgs: JobsAdminMessages) : Nodes.Table.Column list = [
    Nodes.Table.column "job" msgs.ColumnJob 4
    Nodes.Table.column "last-run" msgs.ColumnLastRun 3
    Nodes.Table.column "status" msgs.ColumnStatus 3
    Nodes.Table.column "actions" msgs.ColumnActions 2
]

let private attemptItem (id: string) (msgs: JobsAdminMessages) (job: JobDefinition) (run: JobRun) =
    Nodes.Layout.column id [
        Nodes.Layout.row (Nodes.Ids.child id "line") [
            Nodes.text (Nodes.Ids.child id "number") (msgs.AttemptNumber run.Attempt)
            attemptBadge (Nodes.Ids.child id "reading") msgs (attemptReading job run)
            Nodes.text (Nodes.Ids.child id "started") (msgs.AttemptStarted(formatTime run.StartedAt))
            match attemptDuration run with
            | Some span -> Nodes.text (Nodes.Ids.child id "took") (msgs.AttemptTook(formatDuration span))
            | None -> ()
        ]
        match run.Error with
        | Some error -> reason (Nodes.Ids.child id "reason") msgs error
        | None -> ()
        match run.Status, run.Warning with
        | JobRunStatus.Succeeded, Some warning -> Nodes.text (Nodes.Ids.child id "warning") warning
        | _ -> ()
    ]

let private attempts (id: string) (row: JobRow) : Node<Msg> =
    let msgs = row.Msgs

    Nodes.Layout.column id [
        match row.Attempts with
        | None
        | Some Remote.NotAsked
        | Some Remote.Loading -> Nodes.text (Nodes.Ids.child id "loading") msgs.AttemptsLoading
        | Some(Remote.Failed message) -> Nodes.text (Nodes.Ids.child id "failed") (msgs.AttemptsLoadFailed message)
        | Some(Remote.Reloading [])
        | Some(Remote.Loaded []) -> Nodes.text (Nodes.Ids.child id "none") msgs.NoAttempts
        | Some(Remote.Reloading runs)
        | Some(Remote.Loaded runs) ->
            for run in runs do
                attemptItem (Nodes.Ids.keyed (Nodes.Ids.child id "run") (string run.RunId)) msgs row.Job run
    ]

let private jobRows =
    Keep.create (fun (row: JobRow) ->
        let msgs = row.Msgs
        let job = row.Job
        let id = Nodes.Ids.keyed "jobs-job" (string job.JobId)

        let cell part children =
            Nodes.Table.cell (Nodes.Ids.child id part) children

        let standing = standingOf job

        let newer =
            match row.Attempts with
            | Some(Remote.Loaded runs) -> newerThanRecord job runs
            | _ -> None

        let inFlight =
            match row.Rerun with
            | Some RerunProgress.Starting
            | Some(RerunProgress.Awaiting _) -> true
            | _ -> false

        let progress =
            match row.Rerun with
            | Some RerunProgress.Starting -> Some msgs.RerunStarting
            | Some(RerunProgress.Awaiting _) -> Some msgs.RerunAwaiting
            | Some(RerunProgress.Landed standing) -> Some(msgs.RerunLanded(standingLabel msgs standing))
            | Some RerunProgress.StillRunning -> Some msgs.RerunStillRunning
            | Some(RerunProgress.Refused reason) -> Some(msgs.RerunRefused reason)
            | None -> None

        Nodes.Table.row id (columns msgs) [
            cell "job" [
                Nodes.text (Nodes.Ids.child id "handler") job.Handler
                Nodes.text (Nodes.Ids.child id "id") (string job.JobId)
                match job.Status with
                | JobStatus.Disabled -> Nodes.text (Nodes.Ids.child id "disabled") msgs.JobDisabled
                | JobStatus.Cancelled -> Nodes.text (Nodes.Ids.child id "cancelled") msgs.JobCancelled
                | JobStatus.Active -> ()
                Nodes.Actions.button
                    (Nodes.Ids.child id "toggle")
                    ButtonVariant.Tertiary
                    (if row.Expanded then
                         msgs.HideAttempts
                     else
                         msgs.ShowAttempts)
                    false
                    (ToggleAttempts job.JobId)
                |> Node.withExtraAttribute "aria-expanded" (if row.Expanded then "true" else "false")
                if row.Expanded then
                    attempts (Nodes.Ids.child id "attempts") row
            ]
            cell "last-run" [
                Nodes.text
                    (Nodes.Ids.child id "last-run-at")
                    (job.LastRunAt |> Option.map formatTime |> Option.defaultValue msgs.NeverRun)
                match newer with
                | Some run ->
                    Nodes.text (Nodes.Ids.child id "newer") (msgs.NewerAttempt run.Attempt (formatTime run.StartedAt))
                | None -> ()
            ]
            cell "status" [
                standingBadge (Nodes.Ids.child id "standing") msgs standing
                match standing, job.LastRunError with
                | (JobStanding.PermanentlyFailed | JobStanding.FailingWillRetry), Some error ->
                    reason (Nodes.Ids.child id "reason") msgs error
                | _ -> ()
                match warningOf job with
                | Some warning -> Nodes.text (Nodes.Ids.child id "warning") warning
                | None -> ()
            ]
            cell "actions" [
                match rerunOffer job with
                | RerunOffer.Offered ->
                    Nodes.Actions.button
                        (Nodes.Ids.child id "rerun")
                        ButtonVariant.Secondary
                        msgs.RerunButton
                        inFlight
                        (RerunRequested job.JobId)
                | RerunOffer.RefusedDisabled ->
                    Nodes.text (Nodes.Ids.child id "rerun-refused") msgs.RerunUnavailableDisabled
                | RerunOffer.RefusedCancelled ->
                    Nodes.text (Nodes.Ids.child id "rerun-refused") msgs.RerunUnavailableCancelled
                match progress with
                | Some text -> Nodes.text (Nodes.Ids.child id "rerun-progress") text
                | None -> ()
            ]
        ])

let private jobsTable (msgs: JobsAdminMessages) (model: Model) (jobs: JobDefinition list) : Node<Msg> =
    if jobs.IsEmpty then
        Nodes.text "jobs-none" msgs.NoJobs
    else
        let rows =
            ordered jobs
            |> List.map (fun job -> {
                Msgs = msgs
                Job = job
                Attempts = model.Attempts.TryFind job.JobId
                Rerun = model.Reruns.TryFind job.JobId
                Expanded = model.Expanded.Contains job.JobId
            })
            |> inputs.Of

        Nodes.Table.table "jobs-table" msgs.Heading (columns msgs) (Keep.list jobRows rows)

/// The re-run confirmation, while one is pending.
let private rerunConfirm (msgs: JobsAdminMessages) (job: JobDefinition) : Node<Msg> =
    Fuaran.modal "jobs-rerun" {
        Defaults.modal with
            Open = Binding.Static(Some true)
            Heading = Some(Nodes.literal msgs.RerunButton)
            OnDismiss = Some(Action.dispatch RerunCancelled)
            Children = [
                Nodes.text "jobs-rerun-prompt" (msgs.RerunConfirmPrompt job.Handler)
                Nodes.Layout.row "jobs-rerun-actions" [
                    Nodes.Actions.secondary "jobs-rerun-cancel" msgs.RerunCancel RerunCancelled
                    Nodes.Actions.primary "jobs-rerun-confirm" msgs.RerunConfirm (RerunConfirmed job.JobId)
                ]
            ]
    }

// ─── The page ────────────────────────────────────────────────────────

/// The page: the heading and its refresh, the notice, the jobs or the state
/// they are in, and an open confirmation. Every node sends its message
/// through the tree.
let tree (msgs: JobsAdminMessages) (model: Model) : Node<Msg> =
    let loading = Remote.isLoading model.Jobs

    Nodes.Layout.column "jobs" [
        Fuaran.box "jobs-header" {
            Defaults.box with
                Layout = BoxLayout.Grid(2, Some "minmax(0, 1fr) auto", Some 16)
                Children = [
                    Nodes.Layout.column "jobs-titles" [
                        Nodes.heading "jobs-heading" 2 msgs.Heading
                        Nodes.text "jobs-subheading" msgs.Subheading
                    ]
                    Nodes.Actions.button
                        "jobs-refresh"
                        ButtonVariant.Secondary
                        (if loading then msgs.Refreshing else msgs.Refresh)
                        loading
                        Refresh
                ]
        }
        match model.LastError with
        | Some(Notice.Problem text) ->
            Nodes.Actions.banner "jobs-banner" ToneVariant.Critical text msgs.Dismiss DismissError
        | Some(Notice.Info text) -> Nodes.Actions.banner "jobs-banner" ToneVariant.Info text msgs.Dismiss DismissError
        | None -> ()
        match model.Jobs with
        | Remote.NotAsked
        | Remote.Loading -> Nodes.text "jobs-loading" msgs.Loading
        | Remote.Failed message ->
            Fuaran.callout "jobs-failed" {
                Defaults.callout with
                    Body = Nodes.literal message
                    Tone = ToneVariant.Critical
            }
        | Remote.Reloading jobs
        | Remote.Loaded jobs -> jobsTable msgs model jobs
        match model.Confirming |> Option.bind (findJob model) with
        | Some job -> rerunConfirm msgs job
        | None -> ()
    ]

/// The deferred component `JobsAdminUI`'s view mounts.
let Surface (props: JobsAdminUI.SurfaceProps) : Fable.React.ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(TreeLowering.treeProps host props.Dispatch (tree props.Messages props.Model))