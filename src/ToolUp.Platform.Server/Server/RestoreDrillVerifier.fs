// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 445 — the restore drill: rehearse recovery, unattended, and
/// report whether it would have worked.
///
/// A drill restores the LATEST snapshot side-by-side into a scratch
/// prefix of the live storage (`_restore/{restoreId}/`, never a live
/// key), then checks three things over the restored copy before erasing
/// it:
///
///   1. **Restored hashes** — every restored blob re-hashes to the
///      manifest's digest. Restore preflight hashed the backup COPY; this
///      hashes what was actually WRITTEN, which is the half a preflight
///      cannot see.
///   2. **Event replay folds** — the real `PersistentEventStore`,
///      constructed over a prefixed view of the staged copy, replays
///      every scope (`ReadAll` must deserialise every canonical blob) and
///      its own `IndexConsistencyCheck` finds no orphaned or unindexed
///      entry. The store that will read the data once promoted is the
///      one that verifies it — no re-implementation of its layout.
///   3. **Entity index round-trip** — every `entities/_indexes/…/{id}.ref`
///      in the staged copy resolves to a versioned
///      `objects/_entity__{type}__{id}/` object beside it. Layout per
///      `EntityStore.fs`'s header; the test pack pins it by writing a
///      real entity through `BlobEntityStore` and drilling over it.
///
/// The outcome lands as a `RestoreDrillPassed` / `RestoreDrillFailed`
/// audit row on the reserved `_platform` scope, and as the
/// `backup_restore_drill` readiness probe — `Degraded` (never
/// `Unhealthy`: a failed rehearsal does not make the live service
/// unable to serve) until the next drill passes.
///
/// Scheduling is the composition's opt-in (`BackupSettings.DrillCron`
/// over `IJobScheduler`); `RunDrill` is the on-demand entry point.
module ToolUp.Platform.RestoreDrill

open System
open ToolUp.Platform
open ToolUp.Platform.Backup
open ToolUp.Platform.BackupCoordinator
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.HealthChecks

/// One store-level invariant checked over the restored copy.
type DrillInvariant = {
    /// `"restored-hashes"` / `"event-replay"` / `"entity-index-round-trip"`.
    Name: string
    /// Whether it held.
    Passed: bool
    /// What was checked, and what failed when it did not hold.
    Detail: string
}

/// What one drill found.
type DrillOutcome = {
    /// The snapshot rehearsed, when one was found.
    SnapshotId: string option
    /// The scratch restore's identifier, when the drill got that far.
    RestoreId: string option
    /// When the drill started.
    StartedAt: DateTime
    /// When it finished, passed or not.
    CompletedAt: DateTime
    /// The verdict.
    Passed: bool
    /// Which stage decided it — `"complete"` on a pass; `"no-snapshot"` /
    /// `"preflight"` / `"restore"` / `"invariant"` / `"exception"` on a
    /// failure.
    Stage: string
    /// Blobs whose hash restore preflight verified.
    HashesVerified: int
    /// Every invariant checked, held or not.
    Invariants: DrillInvariant list
    /// Why it failed, in one sentence; `None` on a pass.
    Failure: string option
}

/// Helpers over `DrillOutcome`.
module DrillOutcome =
    /// Invariant names that did not hold.
    let failedInvariants (outcome: DrillOutcome) : string list =
        outcome.Invariants |> List.filter (fun i -> not i.Passed) |> List.map _.Name

/// The layout constants the entity round-trip reads. They mirror
/// `EntityStore.fs` (whose own literals are private to that module); the
/// `BackupCoordinatorTests` drill over a real `BlobEntityStore` write is
/// what keeps them honest.
module private EntityLayout =
    [<Literal>]
    let IndexRoot = "entities/_indexes/"

    [<Literal>]
    let ObjectIdPrefix = "_entity__"

    [<Literal>]
    let Separator = "__"

    /// `entities/_indexes/{type}/{index}/{value}/{entityId}.ref` →
    /// `(type, entityId)`, or `None` for a name that is not an index ref.
    let tryParseIndexRef (blobName: string) : (string * string) option =
        if not (blobName.StartsWith IndexRoot && blobName.EndsWith ".ref") then
            None
        else
            let rest =
                blobName.Substring(IndexRoot.Length, blobName.Length - IndexRoot.Length - ".ref".Length)

            match rest.Split '/' with
            | [| entityType; _index; _value; entityId |] -> Some(entityType, entityId)
            | _ -> None

    /// The object prefix an index ref points at.
    let objectPrefix (entityType: string) (entityId: string) : string =
        $"objects/{ObjectIdPrefix}{entityType}{Separator}{entityId}/"

/// The verifier. One instance per deployment; `LastOutcome` is the
/// state the health probe reads (a documented module-level mutable —
/// the probe must answer without re-running a drill).
///
///  * `coordinator` — the composed `IBackupCoordinator`.
///  * `liveStorage` — the RAW live storage the coordinator restores
///    into; the invariants read the staged copy through a prefixed view
///    of it.
///  * `indexSampleSize` — how many canonical events per scope the event
///    store's consistency check samples (default 200).
type RestoreDrillVerifier
    (
        coordinator: IBackupCoordinator,
        liveStorage: IBlobStorage,
        ?auditLog: IAuditLog,
        ?logger: ILogger,
        ?clock: unit -> DateTime,
        ?indexSampleSize: int
    ) =

    let now = defaultArg clock (fun () -> DateTime.UtcNow)
    let sampleSize = defaultArg indexSampleSize 200
    let mutable lastOutcome: DrillOutcome option = None
    let gate = obj ()

    let audit (event: AuditEvent) = async {
        match auditLog with
        | Some log ->
            try
                do! log.Record(BackupScope.PlatformContainer, event)
            with ex ->
                logger
                |> Option.iter (fun l -> l.Warn(sprintf "[Phase 445] drill audit emission failed: %s" ex.Message))
        | None -> ()
    }

    /// Invariant 1 — what was written re-hashes to the manifest.
    let restoredHashes (staged: IBlobStorage) (manifest: BackupManifest) = async {
        let mismatches = ResizeArray<string>()

        for entry in manifest.Entries do
            match! staged.Download(entry.Container, entry.BlobName) with
            | Error reason -> mismatches.Add $"{entry.Container}/{entry.BlobName}: unreadable ({reason})"
            | Ok bytes ->
                let actual = DeployRecords.digestBytes bytes

                if actual <> entry.Sha256 then
                    mismatches.Add $"{entry.Container}/{entry.BlobName}: expected {entry.Sha256}, restored {actual}"

        return {
            Name = "restored-hashes"
            Passed = mismatches.Count = 0
            Detail =
                if mismatches.Count = 0 then
                    $"{manifest.Entries.Length} restored blob(s) re-hash to the manifest"
                else
                    String.Join("; ", mismatches)
        }
    }

    /// Invariant 2 — the real event store replays every scope over the
    /// staged copy and its indexes are consistent.
    let eventReplay (staged: IBlobStorage) = async {
        let store =
            PersistentEventStore.PersistentEventStore(staged, EventRetentionPolicy.unlimited)

        let! scopes = (store :> IEventStore).ListScopes()
        let problems = ResizeArray<string>()
        let mutable replayed = 0

        for scope in scopes do
            try
                let! events = (store :> IEventStore).ReadAll scope
                replayed <- replayed + events.Length
                let! entries = store.IndexConsistencyCheck(scope, sampleSize)

                for entry in entries do
                    if entry.OrphanedIndexEntries > 0 || entry.UnindexedCanonicals > 0 then
                        problems.Add
                            $"scope {scope} {entry.IndexName}: {entry.OrphanedIndexEntries} orphaned ref(s), {entry.UnindexedCanonicals} unindexed event(s)"
            with ex ->
                problems.Add $"scope {scope}: replay failed ({ex.Message})"

        return {
            Name = "event-replay"
            Passed = problems.Count = 0
            Detail =
                if problems.Count = 0 then
                    $"{replayed} event(s) replayed across {scopes.Length} scope(s); indexes consistent"
                else
                    String.Join("; ", problems)
        }
    }

    /// Invariant 3 — every entity index ref resolves to an object.
    let entityIndexRoundTrip (staged: IBlobStorage) (containers: string list) = async {
        let dangling = ResizeArray<string>()
        let mutable refs = 0

        for container in containers do
            let! names = staged.List(container, EntityLayout.IndexRoot)

            for name in names do
                match EntityLayout.tryParseIndexRef name with
                | None -> ()
                | Some(entityType, entityId) ->
                    refs <- refs + 1
                    let! versions = staged.List(container, EntityLayout.objectPrefix entityType entityId)

                    if versions.IsEmpty then
                        dangling.Add
                            $"{container}/{name} → no object under {EntityLayout.objectPrefix entityType entityId}"

        return {
            Name = "entity-index-round-trip"
            Passed = dangling.Count = 0
            Detail =
                if dangling.Count = 0 then
                    $"{refs} index ref(s) resolve to their entity"
                else
                    String.Join("; ", dangling)
        }
    }

    let record (outcome: DrillOutcome) = async {
        lock gate (fun () -> lastOutcome <- Some outcome)

        if outcome.Passed then
            do!
                audit (
                    RestoreDrillPassed {
                        SnapshotId = defaultArg outcome.SnapshotId ""
                        RestoreId = defaultArg outcome.RestoreId ""
                        HashesVerified = outcome.HashesVerified
                        Invariants = outcome.Invariants |> List.map _.Name
                        StartedAt = outcome.StartedAt
                        CompletedAt = outcome.CompletedAt
                    }
                )
        else
            do!
                audit (
                    RestoreDrillFailed {
                        SnapshotId = outcome.SnapshotId
                        RestoreId = outcome.RestoreId
                        Stage = outcome.Stage
                        Reason = defaultArg outcome.Failure ""
                        FailedInvariants = DrillOutcome.failedInvariants outcome
                        StartedAt = outcome.StartedAt
                        FailedAt = outcome.CompletedAt
                    }
                )

        return outcome
    }

    /// The most recent drill's outcome, or `None` when none has run in
    /// this process.
    member _.LastOutcome: DrillOutcome option = lock gate (fun () -> lastOutcome)

    /// Run one drill end to end: latest snapshot → side-by-side restore
    /// → the three invariants → erase the scratch prefix → record. Never
    /// throws; an exception becomes a `"exception"`-stage failure, and
    /// the scratch prefix is erased on every path that created one.
    member _.RunDrill() : Async<DrillOutcome> = async {
        let startedAt = now ()

        let failed snapshotId restoreId stage hashes invariants (reason: string) = {
            SnapshotId = snapshotId
            RestoreId = restoreId
            StartedAt = startedAt
            CompletedAt = now ()
            Passed = false
            Stage = stage
            HashesVerified = hashes
            Invariants = invariants
            Failure = Some reason
        }

        let! snapshots = coordinator.ListSnapshots()

        match List.tryLast snapshots with
        | None ->
            return! record (failed None None "no-snapshot" 0 [] "no snapshot exists on the backup target to rehearse")
        | Some snapshotId ->
            match! coordinator.ReadManifest snapshotId with
            | Error e -> return! record (failed (Some snapshotId) None "preflight" 0 [] (BackupError.message e))
            | Ok manifest ->
                let scope = Containers manifest.Containers

                match! coordinator.Restore(snapshotId, scope, RestoreOptions.defaults) with
                | Error(PreflightRefused _ as e) ->
                    return! record (failed (Some snapshotId) None "preflight" 0 [] (RestoreError.message e))
                | Error e -> return! record (failed (Some snapshotId) None "restore" 0 [] (RestoreError.message e))
                | Ok report ->
                    let cleanUp () = async {
                        match! coordinator.DiscardStaged(report.RestoreId, scope) with
                        | Ok _ -> ()
                        | Error e ->
                            logger
                            |> Option.iter (fun l ->
                                l.Warn(
                                    sprintf
                                        "[Phase 445] drill scratch prefix %s not fully erased: %s"
                                        (stagingPrefix report.RestoreId)
                                        (RestoreError.message e)
                                ))
                    }

                    try
                        let staged =
                            PrefixedBlobStorage(liveStorage, stagingPrefix report.RestoreId) :> IBlobStorage

                        let! hashes = restoredHashes staged manifest

                        let! replay =
                            if manifest.Containers |> List.contains BackupScope.PlatformContainer then
                                eventReplay staged
                            else
                                async.Return {
                                    Name = "event-replay"
                                    Passed = true
                                    Detail = "scope excludes _platform; no event store to replay"
                                }

                        let! entities = entityIndexRoundTrip staged manifest.Containers
                        let invariants = [ hashes; replay; entities ]
                        do! cleanUp ()

                        match invariants |> List.filter (fun i -> not i.Passed) with
                        | [] ->
                            return!
                                record {
                                    SnapshotId = Some snapshotId
                                    RestoreId = Some report.RestoreId
                                    StartedAt = startedAt
                                    CompletedAt = now ()
                                    Passed = true
                                    Stage = "complete"
                                    HashesVerified = report.HashesVerified
                                    Invariants = invariants
                                    Failure = None
                                }
                        | broken ->
                            let reason =
                                broken |> List.map (fun i -> $"{i.Name}: {i.Detail}") |> String.concat "; "

                            return!
                                record (
                                    failed
                                        (Some snapshotId)
                                        (Some report.RestoreId)
                                        "invariant"
                                        report.HashesVerified
                                        invariants
                                        reason
                                )
                    with ex ->
                        do! cleanUp ()

                        return!
                            record (
                                failed
                                    (Some snapshotId)
                                    (Some report.RestoreId)
                                    "exception"
                                    report.HashesVerified
                                    []
                                    $"drill threw: {ex.Message}"
                            )
    }

/// The `backup_restore_drill` readiness probe: `Healthy` until a drill
/// fails, `Degraded` (with the failure) until the next one passes. A
/// failed rehearsal never reports `Unhealthy` — the live service is
/// still serving; it is the recovery posture that is degraded.
type RestoreDrillHealthCheck(verifier: RestoreDrillVerifier) =
    interface IHealthCheck with
        member _.Name = "backup_restore_drill"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            match verifier.LastOutcome with
            | None -> return Healthy
            | Some outcome when outcome.Passed -> return Healthy
            | Some outcome ->
                let at = outcome.CompletedAt.ToString "o"
                let reason = defaultArg outcome.Failure ""
                return Degraded $"last restore drill failed at {at} ({outcome.Stage}): {reason}"
        }

/// `IJobHandler` names the composition registers.
module BackupJobs =
    /// The scheduled-snapshot handler.
    [<Literal>]
    let SnapshotHandlerName = "_platform.backup.snapshot"

    /// The scheduled-drill handler.
    [<Literal>]
    let DrillHandlerName = "_platform.backup.drill"

/// Takes one snapshot of `scope` per run. A storage failure is
/// `TransientFailure` — the next attempt may well succeed.
type SnapshotJobHandler(coordinator: IBackupCoordinator, scope: BackupScope) =
    interface IJobHandler with
        member _.Execute(_ctx) = async {
            match! coordinator.Snapshot scope with
            | Ok _ -> return JobResult.Success
            | Error e -> return JobResult.TransientFailure(BackupError.message e)
        }

/// Runs one drill per run. A failed drill is `PermanentFailure` — the
/// run is dead-lettered so the job history shows it, and the next
/// scheduled run still fires on its cron; retrying the same snapshot
/// would find the same fault.
type RestoreDrillJobHandler(verifier: RestoreDrillVerifier) =
    interface IJobHandler with
        member _.Execute(_ctx) = async {
            let! outcome = verifier.RunDrill()

            if outcome.Passed then
                return JobResult.Success
            else
                let reason = defaultArg outcome.Failure ""
                return JobResult.PermanentFailure($"{outcome.Stage}: {reason}")
        }