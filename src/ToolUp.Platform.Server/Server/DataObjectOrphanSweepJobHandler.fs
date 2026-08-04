// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.DataObjectOrphanSweep

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// `ToolUp.Platform.ConfigValidation` is deliberately NOT opened: its
// `ValidationResult` DU has `Ok` / `Error` cases that shadow the F# core
// `Result` constructors this module's sweep path uses throughout, and the
// resulting inference failures are reported far from their cause. The one
// validator at the bottom qualifies instead.

// ─── Phase 7c — data-object orphan-blob sweep ────────────────────────
//
// `DataObjectStore.Save` writes the content blob FIRST and the metadata
// blob second — it must, because the metadata names a hash that has to
// already exist. That ordering leaves a window: a process killed between
// the two writes leaves `objects/_content/{hash}.data` behind with
// nothing referencing it, forever. Nothing reclaims it. The in-band GC
// only runs on `Delete` / `Evict` / `Erase`, and the object whose save
// died was never created, so it is never deleted.
//
// Two costs, and the second is the one that matters:
//   * **Storage.** Abandoned content accumulates at the rate of
//     crash-during-save, which is small per event and unbounded over a
//     deployment's life.
//   * **Erasure completeness.** A subject-erasure pass walks *metadata*
//     to find what to redact or remove. Content whose metadata write
//     never landed is invisible to it — so a user's bytes can outlive
//     the erasure that was supposed to remove them, in
//     content-addressable storage, indefinitely. That is a GDPR defect,
//     not a housekeeping one.
//
// **The grace window is the whole design.** A content blob with no
// metadata is indistinguishable from an in-flight `Save` that has not
// reached its metadata write yet. Reclaiming eagerly would delete live
// content out from under a concurrent writer — corruption, not cleanup.
// So the sweep only reclaims blobs whose last write is older than
// `GracePeriod` (default 24h), which is longer than any `Save` can
// plausibly take by orders of magnitude. The floor is deliberate: a
// zero-grace sweep is a data-loss bug, so `withOrphanSweepGracePeriod`
// clamps upward rather than trusting the caller.
//
// **GP 4 — per-scope, never deployment-wide.** One run reaches exactly
// one scope's container, resolved from `JobContext.ScopeId` (the
// scheduler's, never a caller's) through `DataObjectStore.containerFor`.
// The scopes to visit are an EXPLICIT parameter on the policy, for the
// same reason Phase 512's KB retention sweep takes one: `IBlobStorage`
// has no cross-container enumeration and the SDK does not enumerate
// tenants. An empty scope list therefore schedules nothing, which is
// honest — silently defaulting to `_platform` would look composed while
// sweeping a container that holds no data objects.
//
// **GP 11 / GP 13 — a deployment that never composes it pays nothing.**
// No hosted service, no scheduler entry, no blob read. The one visible
// change for an existing deployment is a preflight `Warning` from
// `DataObjectOrphanSweepConfiguredValidator`, which is a signal, not a
// behaviour change: `Warning` never aborts startup.
//
// **GP 12 rule 4 — stateless between invocations.** The handler resolves
// `IBlobStorage` / `IAuditLog` / `ILogger` from the provider on every
// `Execute`; nothing is captured at compose time, so a distributed
// scheduler may deactivate and rehydrate it freely.

/// Phase 7c — per-deployment tuning for the orphan sweep. Registered as
/// a DI singleton by `ServerApp.withDataObjectOrphanSweep`; its presence
/// in the service collection is also what tells the preflight validator
/// the deployment has made a deliberate choice here.
type DataObjectOrphanSweepPolicy = {
    /// Cron expression the sweep runs on. Default `"0 2 * * *"` — daily
    /// at 02:00 UTC, deliberately an hour before the Phase 14w tombstone
    /// vacuum's 03:00 so two storage-reclaim passes do not contend for
    /// the same backing store in the same minute.
    Schedule: string
    /// Minimum age of an orphaned content blob before it may be
    /// reclaimed. Protects in-flight `Save`s whose metadata write has not
    /// landed yet. Default 24h.
    GracePeriod: TimeSpan
    /// Scopes to sweep, one job registration each. Explicit by
    /// necessity — see the GP 4 note above. Empty = inert.
    Scopes: string list
}

module DataObjectOrphanSweepPolicy =

    /// Daily at 02:00 UTC.
    [<Literal>]
    let DefaultSchedule = "0 2 * * *"

    /// 24 hours — comfortably longer than any plausible `Save`.
    let DefaultGracePeriod = TimeSpan.FromHours 24.0

    /// Absolute floor on the grace window. A sweep that reclaims content
    /// younger than this is racing live `Save`s, so the builder clamps
    /// rather than honouring a caller who asked for zero.
    let MinimumGracePeriod = TimeSpan.FromMinutes 5.0

    /// An explicitly-composed no-op: defaults, no scopes. Composing this
    /// is how an operator says "I know about orphaned content and accept
    /// it" — the preflight validator treats a registered policy as an
    /// acknowledgement and stays `Ok`.
    let disabled: DataObjectOrphanSweepPolicy = {
        Schedule = DefaultSchedule
        GracePeriod = DefaultGracePeriod
        Scopes = []
    }

    /// Sweep these scopes on the defaults. Pass the same scope list the
    /// deployment's other per-scope maintenance takes — typically
    /// `ITeamStore` results plus any `_platform` container in use.
    let forScopes (scopes: string list) : DataObjectOrphanSweepPolicy = { disabled with Scopes = scopes }

    /// Override the sweep cadence (cron, 5-field, UTC).
    let withOrphanSweepSchedule (cron: string) (policy: DataObjectOrphanSweepPolicy) : DataObjectOrphanSweepPolicy = {
        policy with
            Schedule = cron
    }

    /// Override the in-flight-`Save` grace window. Clamped up to
    /// `MinimumGracePeriod` — see the field docs for why that is not a
    /// courtesy.
    let withOrphanSweepGracePeriod
        (grace: TimeSpan)
        (policy: DataObjectOrphanSweepPolicy)
        : DataObjectOrphanSweepPolicy =
        {
            policy with
                GracePeriod =
                    (if grace < MinimumGracePeriod then
                         MinimumGracePeriod
                     else
                         grace)
        }

    /// `true` when the policy can never reclaim anything, so nothing is
    /// scheduled and nothing is read.
    let isInert (policy: DataObjectOrphanSweepPolicy) : bool = List.isEmpty policy.Scopes

/// Outcome of one scope's sweep. Returned by `sweepScope` so the job
/// handler, the tests, and an operator-triggered run all read the same
/// evidence rather than inferring it from logs.
type OrphanSweepReport = {
    /// Scope the sweep ran against.
    ScopeId: string
    /// Container it resolved to.
    Container: string
    /// Orphaned content blobs found, before the grace filter.
    OrphansFound: int
    /// Content hashes actually deleted.
    Reclaimed: string list
    /// Total bytes across `Reclaimed`.
    ReclaimedBytes: int64
    /// Orphans left alone because they were younger than the grace
    /// window. Expected to be non-zero on a busy deployment — this is
    /// the in-flight-`Save` protection working.
    DeferredByGrace: int
    /// Operator-legible delete failures, one per blob the backing store
    /// refused. Non-empty means the next run retries them.
    Failures: string list
}

module OrphanSweepReport =
    /// A run that found nothing to reclaim.
    let noOp (scopeId: string) (container: string) (found: int) (deferred: int) : OrphanSweepReport = {
        ScopeId = scopeId
        Container = container
        OrphansFound = found
        Reclaimed = []
        ReclaimedBytes = 0L
        DeferredByGrace = deferred
        Failures = []
    }

    /// `true` when every reclaimable orphan went cleanly.
    let isClean (report: OrphanSweepReport) : bool = report.Failures.IsEmpty

    /// One-line operator summary.
    let summarise (report: OrphanSweepReport) : string =
        sprintf
            "scope %s: %d orphan(s) found, %d reclaimed (%d bytes), %d deferred by grace%s"
            report.ScopeId
            report.OrphansFound
            report.Reclaimed.Length
            report.ReclaimedBytes
            report.DeferredByGrace
            (if isClean report then
                 ""
             else
                 sprintf ", %d failure(s): %s" report.Failures.Length (String.concat "; " report.Failures))

/// Sweep one scope: reclaim every orphaned content blob older than
/// `policy.GracePeriod`, emitting one `OrphanedContentBlobReclaimed`
/// audit row per deletion plus one `OrphanSweepCompleted` summary when
/// the run reclaimed something.
///
/// `now` is a parameter rather than a `DateTime.UtcNow` read so the
/// grace decision is testable without waiting a day.
///
/// Idempotent: a second run finds the reclaimed blobs gone and the
/// deferred ones (if still young) deferred again.
let sweepScope
    (blobStorage: IBlobStorage)
    (auditLog: IAuditLog option)
    (logger: ILogger)
    (now: DateTime)
    (policy: DataObjectOrphanSweepPolicy)
    (scopeId: string)
    : Async<OrphanSweepReport> =
    async {
        let container = DataObjectStore.containerFor scopeId
        let! orphans = DataObjectStore.listOrphanedContent blobStorage container

        // The grace partition. `now - LastModified >= GracePeriod` is the
        // whole reclaim predicate — everything younger is presumed to be
        // an in-flight `Save`, never a crash residue.
        let reclaimable, deferred =
            orphans |> List.partition (fun o -> now - o.LastModified >= policy.GracePeriod)

        if List.isEmpty reclaimable then
            return OrphanSweepReport.noOp scopeId container orphans.Length deferred.Length
        else
            let graceHours = int64 policy.GracePeriod.TotalHours

            // Sequential by design: a scope's orphan set is small (crash
            // residue, not bulk data) and serialising keeps the audit
            // rows in a stable order for the trail.
            let rec reclaimEach
                (remaining: DataObjectStore.OrphanedContentBlob list)
                (acc: Result<DataObjectStore.OrphanedContentBlob, string> list)
                : Async<Result<DataObjectStore.OrphanedContentBlob, string> list> =
                async {
                    match remaining with
                    | [] -> return List.rev acc
                    | blob :: rest ->
                        let! outcome = blobStorage.Delete(container, blob.BlobName)

                        let mapped =
                            match outcome with
                            | Ok() -> Ok blob
                            | Error msg -> Error(sprintf "%s — %s" blob.ContentHash msg)

                        match mapped with
                        | Ok(reclaimed: DataObjectStore.OrphanedContentBlob) ->
                            match auditLog with
                            | Some audit ->
                                do!
                                    audit.Record(
                                        scopeId,
                                        OrphanedContentBlobReclaimed {
                                            ScopeId = scopeId
                                            ContentHash = reclaimed.ContentHash
                                            Bytes = reclaimed.SizeBytes
                                            AgeHours = int64 (now - reclaimed.LastModified).TotalHours
                                        }
                                    )
                            | None -> ()
                        | Error _ -> ()

                        return! reclaimEach rest (mapped :: acc)
                }

            let! outcomes = reclaimEach reclaimable []
            let reclaimedBlobs = outcomes |> List.choose Result.toOption

            let failures =
                outcomes
                |> List.choose (function
                    | Error e -> Some e
                    | Ok _ -> None)

            let reclaimedHashes = reclaimedBlobs |> List.map _.ContentHash
            let reclaimedBytes = reclaimedBlobs |> List.sumBy _.SizeBytes

            if not reclaimedHashes.IsEmpty then
                match auditLog with
                | Some audit ->
                    do!
                        audit.Record(
                            scopeId,
                            OrphanSweepCompleted {
                                ScopeId = scopeId
                                OrphansFound = orphans.Length
                                ReclaimedCount = reclaimedHashes.Length
                                ReclaimedBytes = reclaimedBytes
                                DeferredCount = deferred.Length
                                GracePeriodHours = graceHours
                                FailureCount = failures.Length
                            }
                        )
                | None -> ()

                logger.Info(
                    sprintf
                        "[Phase 7c] Orphan sweep reclaimed %d content blob(s) from scope %s (%d bytes)."
                        reclaimedHashes.Length
                        scopeId
                        reclaimedBytes
                )

            if not failures.IsEmpty then
                logger.Warn(
                    sprintf
                        "[Phase 7c] Orphan sweep left %d blob(s) in scope %s — %s. The next sweep retries them."
                        failures.Length
                        scopeId
                        (String.concat "; " failures)
                )

            return {
                ScopeId = scopeId
                Container = container
                OrphansFound = orphans.Length
                Reclaimed = reclaimedHashes
                ReclaimedBytes = reclaimedBytes
                DeferredByGrace = deferred.Length
                Failures = failures
            }
    }

/// Handler name the sweep job registers under. Namespaced against the
/// platform so it cannot clash with a consumer module's own handlers.
[<Literal>]
let SweepHandlerName = "platform.data-object-orphan-sweep"

/// The `IJobHandler` `withDataObjectOrphanSweep` registers. Resolves its
/// substrate from the provider on every `Execute` — nothing is cached
/// between invocations (GP 12 rule 4). Sweeps `ctx.ScopeId` only (GP 4).
///
/// A run that could not delete every reclaimable orphan returns
/// `TransientFailure`: the shape is retryable by construction (the next
/// attempt re-lists and re-tries exactly the blobs that stayed), which is
/// what `JobRetryPolicy` backoff exists for.
type DataObjectOrphanSweepJobHandler(services: IServiceProvider, policy: DataObjectOrphanSweepPolicy) =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) = async {
            let logger =
                match services.GetService typeof<ILogger> with
                | :? ILogger as l -> l
                | _ ->
                    { new ILogger with
                        member _.Debug _ = ()
                        member _.Info _ = ()
                        member _.Warn _ = ()
                        member _.Error(_, _) = ()
                    }

            match services.GetService typeof<IBlobStorage> with
            | :? IBlobStorage as storage ->
                let auditLog =
                    match services.GetService typeof<IAuditLog> with
                    | :? IAuditLog as a -> Some a
                    | _ -> None

                try
                    let! report = sweepScope storage auditLog logger DateTime.UtcNow policy ctx.ScopeId

                    if OrphanSweepReport.isClean report then
                        return Success
                    else
                        return TransientFailure(OrphanSweepReport.summarise report)
                with ex ->
                    logger.Error(sprintf "[Phase 7c] Orphan sweep failed for scope %s" ctx.ScopeId, Some ex)

                    return TransientFailure ex.Message
            | _ ->
                // No blob storage composed — `IDataObjectStore` writes
                // through `IBlobStorage`, so there is no content pool to
                // sweep. Permanent: a retry cannot conjure a store.
                return
                    PermanentFailure "No IBlobStorage is registered; the data-object orphan sweep has nothing to sweep."
        }

/// Phase 7c / Phase 9m — preflight warning for a deployment whose
/// data-object content pool can accumulate orphans that nothing will
/// ever reclaim.
///
/// Two arms, both `Warning` (never `Error`): the leak is slow, and a
/// short-lived or ephemeral deployment legitimately ignores it.
///
///   1. **Composed but unschedulable** — a policy with scopes is
///      registered while `JobScheduler = NoJobScheduler`. The job is
///      declared and can never fire; that is a config mismatch the
///      operator almost certainly did not intend.
///   2. **Not composed at all**, on a deployment with persistent
///      authenticated storage — the shape where the pool is long-lived
///      enough for crash residue to matter. Deliberately gated on that
///      shape rather than fired at every composition, exactly as
///      `VacuumScheduleValidator` gates its own not-configured arm:
///      warning an anonymous/ephemeral deployment about a decade-scale
///      storage leak is noise, and noise is how a real preflight signal
///      gets ignored.
///
/// Composing `DataObjectOrphanSweepPolicy.disabled` registers a policy
/// with no scopes — an explicit acknowledgement, and `Ok`. There is no
/// separate escape-hatch flag because that composition already is one.
type DataObjectOrphanSweepConfiguredValidator(config: ServerConfig, services: IServiceCollection, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout ConfigValidation.IConfigValidator.defaultTimeout

    /// The composed policy, if any. Probes the service collection the
    /// same way `IdempotencyStoreInstanceValidator` does; keyed
    /// descriptors are skipped (reading their implementation throws, and
    /// forge composes none for this seam) and a factory registration is
    /// not introspectable, so only the instance shape the compose helper
    /// actually uses is recognised.
    let composedPolicy () =
        services
        |> Seq.tryPick (fun d ->
            if
                not (isNull d.ServiceType)
                && d.ServiceType = typeof<DataObjectOrphanSweepPolicy>
                && not d.IsKeyedService
            then
                match d.ImplementationInstance with
                | :? DataObjectOrphanSweepPolicy as p -> Some p
                | _ -> None
            else
                None)

    interface ConfigValidation.IConfigValidator with
        member _.Name = "data-object-orphan-sweep"
        member _.Timeout = timeout

        member _.Validate() = async {
            let schedulerOff = config.JobScheduler = NoJobScheduler
            let persistent = DeploymentConfig.hasPersistentAuthenticatedStorage config

            match composedPolicy () with
            | Some policy when not (DataObjectOrphanSweepPolicy.isInert policy) && schedulerOff ->
                return
                    ConfigValidation.Warning(
                        "ServerApp.withDataObjectOrphanSweep is composed but JobScheduler = NoJobScheduler — the orphan sweep can never run. IDataObjectStore.Save writes its content blob before its metadata blob, so a process killed between the two leaves objects/_content/{hash}.data with nothing referencing it, forever: unbounded storage cost, and content that a subject-erasure pass (which walks metadata) cannot see. Set ServerConfig.JobScheduler = InProcessJobScheduler (or a distributed scheduler companion) so the scheduled sweep can fire. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                    )
            | Some _ -> return ConfigValidation.Ok
            | None when persistent ->
                return
                    ConfigValidation.Warning(
                        "This deployment has persistent authenticated storage and composes no data-object orphan sweep. IDataObjectStore.Save writes its content blob before its metadata blob, so a process killed between the two leaves objects/_content/{hash}.data with nothing referencing it — and nothing reclaims it, because the in-band orphan GC only runs on Delete/Evict/Erase and the object whose save died was never created. The residue accumulates for the life of the deployment, and because a subject-erasure pass walks metadata, orphaned content is invisible to it. Compose ServerApp.withDataObjectOrphanSweep (DataObjectOrphanSweepPolicy.forScopes <your scopes>) together with ServerConfig.JobScheduler = InProcessJobScheduler, or compose DataObjectOrphanSweepPolicy.disabled to record that you accept it. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                    )
            | None -> return ConfigValidation.Ok
        }