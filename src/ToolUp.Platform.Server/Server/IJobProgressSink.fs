// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 321 — IJobProgressSink seam + fan-out default ─────────────────
//
// The scheduler reports terminal outcomes and nothing between them. A
// handler doing eight hours of work is an opaque `Running` row for eight
// hours, which is indistinguishable from a hung one — and the workloads
// that motivated external compute (Phase 318) are exactly the long ones.
// This seam is the missing half: a typed checkpoint API whose default
// implementation fans out to the two consumers that already exist.
//
// **Two legs, two durability postures, one call.**
//
//   transient leg → `INotificationChannel`, every checkpoint, scope-gated,
//                   coalesced under load, best-effort. Drives a progress
//                   bar. Losing one frame costs nothing.
//   durable leg   → `IEventStore` under `_platform.jobs`, only
//                   `Durable = true` and terminal checkpoints. Drives the
//                   audit timeline ("how long did epoch 4 take"). Each
//                   write is a blob, which is why it is opt-in per
//                   checkpoint rather than per deployment.
//
// **Why the sink is `Async` and swallows its own failures.** A progress
// report is observability about work, never part of the work. A channel
// that is down or an event store that refuses a write must not fail the
// job that was merely narrating itself — so every publish is wrapped, and
// the failure is logged at Warn with the job id rather than propagated.
// That posture is the whole reason `ctx.Progress.Report` needs no `try`
// at any handler call site.
//
// **GP 12 conformance.** Rule 1: `JobId` + `scopeId` are values, never a
// live run handle. Rule 2: `Async<unit>` at the boundary. Rule 3: the
// shedding policy is a `ProgressCoalescePolicy` record, not an
// `OnDropped` callback. Rule 4: the sink holds only rate-limit
// bookkeeping — a checkpoint carries everything needed to publish it, so
// a distributed implementation can drop the local cache and merely
// publish more often. Rule 5: checkpoints are ordered within a `JobId`
// only. Rule 6: `At` is a `DateTime` with no sub-second promise, matching
// `INotificationChannel`'s documented precision floor.

/// Sink for long-running job progress checkpoints.
///
/// Handlers do NOT resolve this — they report through the pre-bound
/// `ctx.Progress` reporter (see `JobProgressScope`), which cannot name a
/// job other than the one running (GP 4). This interface is for the
/// scheduler, for the reconciliation poll, and for a companion that wants
/// to read the latest checkpoint back.
type IJobProgressSink =
    /// Record one checkpoint for `jobId` under `scopeId`. Best-effort:
    /// implementations must not throw, and a transport failure is logged
    /// rather than surfaced — a job is never failed by its own progress
    /// reporting.
    abstract Report: jobId: JobId * scopeId: string * checkpoint: ProgressCheckpoint -> Async<unit>

    /// The most recent checkpoint recorded for `jobId`, or `None` when the
    /// job has reported none (or the implementation keeps no cache).
    ///
    /// **This is the read the Phase 69i / 69c bridges need** — a
    /// `JobStatus.Running of progress` arm and an SSE progress frame are
    /// both "latest checkpoint, as a fraction". It is deliberately
    /// `option`-returning and deliberately not durable: a distributed sink
    /// with no local cache answers `None` honestly rather than reaching
    /// into the event store on a poll path.
    abstract Latest: jobId: JobId -> Async<ProgressCheckpoint option>

// ─── JSON helper ─────────────────────────────────────────────────────────

module private ProgressJson =
    let private options = FableConverters.create ()

    let serialize (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

/// The sink a deployment gets when `ServerConfig.JobProgress =
/// NoJobProgress` — and the one every `Report` call reaches in a
/// deployment that never opted in. Publishes nothing, persists nothing,
/// remembers nothing (GP 13).
///
/// Registered nowhere: `compose` registers no `IJobProgressSink` at all
/// when progress is off, and the scheduler hands handlers
/// `JobProgressReporter.noOp`. This type exists so a consumer wiring the
/// seam by hand has an explicit off switch rather than having to invent
/// one.
type NoOpJobProgressSink() =
    interface IJobProgressSink with
        member _.Report(_jobId: JobId, _scopeId: string, _checkpoint: ProgressCheckpoint) = async.Return()
        member _.Latest(_jobId: JobId) = async.Return None

/// The default `IJobProgressSink`: fan out to `INotificationChannel` +
/// `IEventStore`, with the transient leg rate-limited per job.
///
/// **The coalescing invariant, stated because it is the one thing here
/// that must not be got wrong.** A chatty handler can emit thousands of
/// checkpoints a second, and publishing each one floods every SSE
/// connection in the scope. So the transient leg sheds — but
/// `ProgressCoalescer.shouldPublish` evaluates *terminal* and *durable*
/// before it evaluates the interval window, which means the checkpoint
/// that says "done" is never the one dropped. A progress bar that sheds
/// intermediate frames is imperceptible; one that sheds the final frame
/// sits at 94% forever on a job that succeeded, and no later checkpoint
/// arrives to correct it. That asymmetry is why the shedding rule lives in
/// a pure, separately-tested function in the Core tier instead of inline
/// here.
///
/// **The durable leg does not shed at all.** `Durable = true` is the
/// caller declaring this checkpoint worth keeping; a rate limiter that
/// silently dropped some of them would make the audit timeline a sample
/// while still looking like a record.
type FanOutJobProgressSink
    (
        notificationChannel: INotificationChannel,
        eventStore: IEventStore,
        logger: ILogger,
        ?policy: ProgressCoalescePolicy,
        ?handlerFor: JobId -> string option
    ) =

    let policy = defaultArg policy ProgressCoalescePolicy.defaults

    /// Per-job transient-leg bookkeeping: the `At` of the last checkpoint
    /// actually published, and the last checkpoint seen (for `Latest`).
    ///
    /// Bounded implicitly by the live-job count rather than by time — a
    /// terminal checkpoint evicts its own entry, so the steady state is
    /// "one entry per job currently reporting". A job that reports
    /// progress and then dies without a terminal checkpoint leaves one
    /// small entry behind; `Forget` is the explicit sweep for a scheduler
    /// that knows a run ended.
    let published = ConcurrentDictionary<JobId, DateTime>()
    let latest = ConcurrentDictionary<JobId, ProgressCheckpoint>()

    let resolveHandler (jobId: JobId) =
        match handlerFor with
        | Some f ->
            try
                f jobId
            with _ ->
                None
        | None -> None

    interface IJobProgressSink with
        member _.Report(jobId: JobId, scopeId: string, checkpoint: ProgressCheckpoint) = async {
            // Clamp defensively: a caller constructing the record directly
            // bypasses `ProgressCheckpoint.create`, and a fraction of 4.7
            // or NaN would otherwise reach a progress bar.
            let checkpoint = {
                checkpoint with
                    Fraction = ProgressCheckpoint.clampFraction checkpoint.Fraction
            }

            latest[jobId] <- checkpoint
            let terminal = ProgressCheckpoint.isTerminal checkpoint

            // ── transient leg ──
            let lastPublishedAt =
                match published.TryGetValue jobId with
                | true, at -> Some at
                | false, _ -> None

            if ProgressCoalescer.shouldPublish policy lastPublishedAt checkpoint then
                published[jobId] <- checkpoint.At

                try
                    let payload = JobProgress.toPayload jobId scopeId (resolveHandler jobId) checkpoint

                    do!
                        notificationChannel.Publish(
                            scopeId,
                            CustomNotification(JobProgress.NotificationKey, ProgressJson.serialize payload)
                        )
                with ex ->
                    // Best-effort by contract. Warn rather than Error: a
                    // dropped progress frame is a cosmetic loss, and
                    // escalating it would train operators to ignore the
                    // channel's genuine failures.
                    logger.Warn
                        $"[JobProgress] event=progress_publish_failed jobId=%O{jobId} scope=%s{scopeId} terminal=%b{terminal}: {ex.Message}"

            // ── durable leg ──
            //
            // Deliberately independent of the shedding decision above: a
            // durable checkpoint persists whether or not its live twin was
            // published, and a terminal checkpoint always persists so the
            // timeline has an end.
            if checkpoint.Durable || terminal then
                try
                    let payload = JobProgress.toPayload jobId scopeId (resolveHandler jobId) checkpoint

                    let evt =
                        Events.create
                            scopeId
                            JobProgress.SourceModule
                            JobProgress.EventType
                            (ProgressJson.serialize payload)

                    do! eventStore.Write evt
                with ex ->
                    logger.Warn
                        $"[JobProgress] event=progress_event_write_failed jobId=%O{jobId} scope=%s{scopeId} terminal=%b{terminal}: {ex.Message}"

            // A terminal checkpoint is the last one for this job, so its
            // rate-limit slot is dead weight from here on. `latest` is kept
            // — a poll arriving just after completion should read 100%, not
            // `None`.
            if terminal then
                published.TryRemove jobId |> ignore
        }

        member _.Latest(jobId: JobId) =
            match latest.TryGetValue jobId with
            | true, checkpoint -> async.Return(Some checkpoint)
            | false, _ -> async.Return None

    /// Drop all bookkeeping for `jobId`. For a scheduler that knows a run
    /// ended without a terminal checkpoint (dead-lettered, cancelled), so
    /// the cache does not accumulate one entry per abandoned run.
    member _.Forget(jobId: JobId) =
        published.TryRemove jobId |> ignore
        latest.TryRemove jobId |> ignore

    /// Diagnostics: jobs with a cached latest checkpoint.
    member _.TrackedJobs = latest.Count

module JobProgressSink =
    /// Bind a sink to one job, producing the reporter a handler sees as
    /// `ctx.Progress`.
    ///
    /// The binding is the security boundary: a handler holds a reporter
    /// that can only report against the job and scope it was dispatched
    /// for, so no handler can publish progress into another tenant's scope
    /// (GP 4) even by accident.
    let reporterFor (sink: IJobProgressSink) (jobId: JobId) (scopeId: string) : IJobProgressReporter =
        { new IJobProgressReporter with
            member _.Report(checkpoint: ProgressCheckpoint) = sink.Report(jobId, scopeId, checkpoint)
        }

    /// The reporter for an optional sink — `JobProgressReporter.noOp` when
    /// the deployment composed none. The single place the GP 13 "costs
    /// nothing when unconfigured" decision is made.
    let reporterForOption (sink: IJobProgressSink option) (jobId: JobId) (scopeId: string) : IJobProgressReporter =
        match sink with
        | Some s -> reporterFor s jobId scopeId
        | None -> JobProgressReporter.noOp

// ─── ctx.Progress — the handler-facing surface ───────────────────────────
//
// **Why an ambient reporter rather than a `JobContext` field.**
//
// The obvious shape is `JobContext.Progress: IJobProgressReporter`, and it
// was rejected on evidence. `JobContext` is a public record in
// `ToolUp.Platform.Core` whose constructor is pinned in the API baseline,
// and F# records have no field defaults — so adding one field
// source-breaks every construction site in existence: ~18 in this repo
// alone, most of them test doubles and handler harnesses, and an unknown
// number in consumer code that builds a `JobContext` to unit-test a
// handler. That is precisely what GP 11 exists to prevent: a deployment
// upgrading to gain an opt-in observability feature it has not enabled
// should not have to edit code to keep compiling.
//
// So the reporter rides the async chain instead, exactly as
// `LoggerScope`'s correlation ids do (GP 7). The handler-facing call is
// byte-identical to the field version — `ctx.Progress.Report(...)` — while
// `JobContext` keeps its shape and its baseline entry.
//
// **This does not weaken GP 12 rule 4.** The rule forbids handler state
// carried BETWEEN invocations. This is per-dispatch context established by
// the scheduler immediately before `Execute` and torn down immediately
// after, in a `use` scope that restores the prior value even when the
// handler throws. Nothing survives the attempt. A distributed scheduler
// implements the same thing the same way — push before invoke, pop after —
// because `AsyncLocal` flows across continuations and thread-pool hops but
// not across processes, which is the correct boundary for a value that
// describes one attempt.

/// Ambient, dispatch-scoped progress reporter. The scheduler pushes one
/// around each `IJobHandler.Execute` call; handlers read it as
/// `ctx.Progress`.
module JobProgressScope =
    open System.Threading

    let private ambient = AsyncLocal<IJobProgressReporter>()

    /// The reporter currently in scope, or `JobProgressReporter.noOp` when
    /// nothing has been pushed — an unconfigured deployment, a handler
    /// invoked directly by a test, a code path outside a dispatch.
    ///
    /// Never returns null and never throws, so `ctx.Progress.Report` is
    /// safe from anywhere.
    let current () : IJobProgressReporter =
        match box ambient.Value with
        | null -> JobProgressReporter.noOp
        | _ -> ambient.Value

    /// Make `reporter` ambient until the returned scope is disposed,
    /// restoring the exact prior value (so nested pushes pop LIFO).
    let push (reporter: IJobProgressReporter) : IDisposable =
        let prior = ambient.Value
        ambient.Value <- reporter

        { new IDisposable with
            member _.Dispose() = ambient.Value <- prior
        }

[<AutoOpen>]
module JobContextProgressExtensions =
    type JobContext with

        /// Progress reporter for this dispatch. Call
        /// `ctx.Progress.Report(ProgressCheckpoint.create (Some 0.37) "…")`
        /// from a handler; the checkpoint is attributed to this job and
        /// scope automatically.
        ///
        /// A no-op when the deployment has not set
        /// `ServerConfig.JobProgress = EnabledJobProgress`, so a handler
        /// can report unconditionally and a deployment that does not want
        /// the traffic pays one interface dispatch per call (GP 13).
        member _.Progress: IJobProgressReporter = JobProgressScope.current ()