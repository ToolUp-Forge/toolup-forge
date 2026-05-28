// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── JobApi (Fable.Remoting wire surface) ────────────────────────
//
// Client-callable API for scheduling, listing, cancelling, and
// triggering jobs through the SDK's background-job substrate.
// Lives in the shared layer so a Fable client (admin UI or in-app
// scheduler control) can call it via Fable.Remoting.
//
// **Scope discipline.** Every method that takes a `scopeId`
// validates it against the caller's resolved `AccessContext` —
// callers cannot pass an arbitrary scope and read another team's
// jobs. The handler enforces this server-side; the client cannot
// bypass it.
//
// **Permission gating.** Read methods (`ListJobs`, `GetJob`,
// `GetRecentRuns`) are available to any team member. Write methods
// (`Schedule`, `Cancel`, `Disable`, `Enable`, `TriggerOnce`)
// require Owner / Admin in `Team` / `MultiTeam` mode (via
// `TeamRoles.canWriteTeamConfig`); other modes are ungated.

type JobApi = {
    /// List every job in the caller's resolved scope. Order is
    /// `CreatedAt` ascending. Empty when `JobScheduler` is disabled
    /// for the deployment, or when no jobs are scheduled.
    ListJobs: unit -> Async<JobDefinition list>

    /// Read one job by id. `None` for unknown ids — does not throw.
    GetJob: System.Guid -> Async<JobDefinition option>

    /// Read the most recent N run-history rows for a job, newest
    /// first. Capped server-side at 50.
    GetRecentRuns: System.Guid * int -> Async<JobRun list>

    /// Submit a new job. Returns `Ok JobId` on success, `Error
    /// ScheduleError` on validation failure (cron parse, handler
    /// not registered, precision unsupported, storage failure).
    /// The handler stamps `CreatedBy` from the caller's
    /// `AccessContext.UserId` regardless of the value supplied on
    /// the wire (informational only). `ScopeId` is overwritten by
    /// the resolved scope.
    Schedule: JobRegistration -> Async<Result<System.Guid, ScheduleError>>

    /// Cancel a job — sets `Status = Cancelled`. Idempotent.
    Cancel: System.Guid -> Async<Result<unit, string>>

    /// Disable a job — sets `Status = Disabled`. Re-enable via
    /// `Enable`. Stops dispatch without losing the schedule.
    Disable: System.Guid -> Async<Result<unit, string>>

    /// Re-enable a previously-disabled job. No-op when already
    /// `Active`.
    Enable: System.Guid -> Async<Result<unit, string>>

    /// Fire a job immediately, regardless of its `Trigger`.
    /// `Error` when the job is unknown or `Status = Cancelled`.
    TriggerOnce: System.Guid -> Async<Result<unit, string>>
}