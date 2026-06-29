// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ScheduledDeprovisionJobHandler

open System
open ToolUp.Platform

// ─── Phase 54f — scheduled / grace-period offboard poll handler ──────
//
// `IJobHandler` registered under
// `TenantLifecycleAggregator.ScheduledDeprovisionHandlerName`. The
// `ScheduleDeprovision` admin call registers a cron-polled job (top of
// every hour) whose payload carries the target scope, the requesting
// admin, the reason, and the absolute `dueAt`. On each tick this handler:
//
//   * parses the payload;
//   * if `now < dueAt` → returns `Success` (the grace window has not
//     elapsed; wait for the next poll);
//   * if `now >= dueAt` → fires the offboard via
//     `TenantLifecycleAggregator.enqueue` (reusing the Phase 54a/54b
//     resumable background path, attributed to the original requester),
//     then cancels its own poll job so it never fires again.
//
// **Why poll, not fire-at-time.** The job substrate's `Trigger` is
// `Cron` / `OnEvent` / `Manual` — there is no native one-shot-at-future-
// time trigger. A grace window is measured in days, so an hourly poll
// that compares `now` against `dueAt` bounds the fire latency to ≤1h at
// negligible tick cost.
//
// **System actor.** Cron jobs run with no user online — `JobContext`
// carries a scope-only `AccessContext`, not the scheduler's permissions
// (which would leak the original caller's authority). The original
// requesting admin is re-read from the payload and passed as the offboard
// actor so the audit trail attributes the run correctly.
//
// **Stateless between invocations (GP 12 rule 4).** Resolves
// `IJobScheduler` from the injected `IServiceProvider` on every `Execute`.

type ScheduledDeprovisionJobHandler(services: IServiceProvider) =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            match TenantLifecycleAggregator.ScheduledDeprovisionPayload.parse ctx.Payload with
            | Error e ->
                // A malformed payload cannot recover on retry — retiring
                // the poll job here would need a scheduler we may not have,
                // so dead-letter it; an admin re-schedules if needed.
                return PermanentFailure(sprintf "malformed scheduled-deprovision payload: %s" e)
            | Ok(scopeId, actorUserId, _reason, dueAt) ->
                if ctx.RunningAt < dueAt.UtcDateTime then
                    // Grace window has not elapsed — wait for the next tick.
                    return Success
                else
                    match services.GetService(typeof<IJobScheduler>) with
                    | :? IJobScheduler as scheduler ->
                        // Fire the real offboard through the Phase 54a/54b
                        // resumable path, attributed to the original
                        // requester (re-read from the payload, not the
                        // scope-only job AccessContext).
                        let! _ = TenantLifecycleAggregator.enqueue scheduler Deprovisioning scopeId actorUserId

                        // Retire this poll job so it never fires again. The
                        // offboard's own `JobSchedulerLifecycle` hook also
                        // cancels in-scope jobs, but cancel explicitly so
                        // retirement does not depend on hook ordering.
                        do! scheduler.Cancel(ctx.ScopeId, ctx.JobId)
                        return Success
                    | _ ->
                        // The handler only runs because a scheduler
                        // dispatched it, so this is unreachable in practice;
                        // treat a missing scheduler as transient so a
                        // re-dispatch retries rather than dead-lettering.
                        return TransientFailure "scheduled offboard fired but no IJobScheduler resolved"
        }

/// Construct the scheduled-offboard poll handler. Resolves its substrate
/// from `services` on every `Execute` (stateless between invocations).
let create (services: IServiceProvider) : IJobHandler =
    ScheduledDeprovisionJobHandler(services) :> IJobHandler