// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.FileProcessor

// ─── MigrationRunner (Phase 10a) ─────────────────────────────────
//
// Walks a team's stored objects for one data type, upgrades every one
// whose stamped schema version lags what the owning module declares,
// and records progress so an admin table can render
// "Migrating Media Optimisation V2→V3: 47/120 objects".
//
// **Idempotent and resumable by construction, not by bookkeeping.**
// The authority on whether an object needs work is the
// `_schemaVersion` stamp in its own metadata, written in the same
// `Save` as the upgraded content. A pass killed halfway leaves
// upgraded objects stamped and the rest not, so the next pass
// recomputes the identical picture and does exactly the outstanding
// work. Nothing is inferred from the status blob — which is why losing
// that blob costs history, not correctness.
//
// **Failure policy — per object, never per pass.** A migrator that
// raises (or hands back a payload the runner cannot write) leaves its
// source object untouched at its old version, logs, emits a
// `MigrationFailed` event into the team's scope, and the pass carries
// on with the remaining objects. One poisonous record does not strand
// the other 999. The operator then either fixes the migrator and runs
// again — the successful 999 are already stamped, so they are skipped
// — or leaves the object quarantined.
//
// **Reads never see a mixed version.** A `Save` publishes the new
// version and its stamp together, so a reader mid-pass sees either the
// old object or the new one, never a half-upgraded payload.
//
// **GP 4.** One pass reaches exactly one scope's container, resolved
// from the team id the caller supplied; `IDataObjectStore` derives the
// container from that id, so another team's objects are structurally
// unreachable.
//
// **GP 12 rule 4.** Every method derives its whole result from its
// parameters plus the injected substrate; nothing is carried between
// invocations, so a distributed scheduler may deactivate and rehydrate
// the runner between any two passes.

/// What a pass did with one object. Aggregated onto `MigrationStatus`.
type internal MigrationObjectOutcome =
    | ObjectUpgraded
    | ObjectAlreadyCurrent
    | ObjectFailed of MigrationFailure

/// Pure helpers shared by the runner and its tests: the chain
/// application and the payload-coercion rule.
module MigrationExecution =

    /// Objects processed between intermediate status writes. Small
    /// enough that a long pass visibly moves in the admin table, large
    /// enough that the status blob is not rewritten per object.
    [<Literal>]
    let statusWriteInterval = 25

    /// Coerce a migrator's boxed return into bytes to store. Anything
    /// else is refused rather than serialised on a guess — writing
    /// bytes the substrate invented is worse than leaving the object
    /// where it was.
    let interpretPayload (payload: obj) : Result<byte[], string> =
        match payload with
        | null -> Error "Migrator returned null; expected byte[] or string."
        | :? (byte[]) as bytes -> Ok bytes
        | :? string as text -> Ok(Encoding.UTF8.GetBytes text)
        | other -> Error $"Migrator returned %s{other.GetType().FullName}; expected byte[] or string."

    /// Apply one step, converting a raise into a typed failure naming
    /// the step that raised. The version pair is in the message
    /// because "the V2→V3 step threw" is the first thing an operator
    /// needs and the last thing a bare exception message carries.
    let private applyStep (step: IDataMigrator) (payload: obj) : Async<Result<obj, string>> = async {
        try
            let! next = step.Migrate payload
            return Ok next
        with ex ->
            return Error $"v%d{step.FromVersion}→v%d{step.ToVersion}: %s{ex.Message}"
    }

    /// Thread one object's content through every step of its chain, in
    /// order. A failure anywhere aborts THIS object only and names the
    /// step.
    let rec private applyChain (steps: IDataMigrator list) (payload: obj) : Async<Result<obj, string>> = async {
        match steps with
        | [] -> return Ok payload
        | step :: rest ->
            let! attempted = applyStep step payload

            match attempted with
            | Error reason -> return Error reason
            | Ok next -> return! applyChain rest next
    }

    /// Run a whole chain over stored content and coerce the result
    /// back to bytes.
    let runChain (chain: IDataMigrator list) (content: byte[]) : Async<Result<byte[], string>> = async {
        let! result = applyChain chain (box content)
        return result |> Result.bind interpretPayload
    }

/// Executes migration passes. Constructed once per deployment and
/// resolved by both the startup sweep and the admin API's manual
/// trigger, so a hand-triggered pass and an automatic one are the same
/// code path.
type MigrationRunner
    (
        registry: MigrationRegistry,
        statusStore: IMigrationStatusStore,
        dataObjectStore: IDataObjectStore,
        eventStore: IEventStore,
        logger: ILogger
    ) =

    let eventJsonOptions = FableConverters.create ()

    let emitFailedEvent
        (teamId: string)
        (dataTypeId: string)
        (targetVersion: SchemaVersion)
        (failure: MigrationFailure)
        =
        async {
            let payload: MigrationFailedEvent = {
                TeamId = teamId
                DataTypeId = dataTypeId
                ObjectId = failure.ObjectId
                AtVersion = failure.AtVersion
                TargetVersion = targetVersion
                Error = failure.Error
            }

            let evt: ModuleEvent = {
                Id = Guid.NewGuid()
                OccurredAt = failure.OccurredAt
                ScopeId = teamId
                SourceModule = MigrationEvents.SourceModule
                EventType = MigrationEvents.MigrationFailedEventType
                Payload = JsonSerializer.Serialize(payload, eventJsonOptions)
            }

            try
                do! eventStore.Write evt
            with ex ->
                // An event-store failure must not promote a per-object
                // migration failure into a pass failure — the log line
                // and the status blob still carry the finding.
                logger.Error("[Migrations] event=migration_failed_event_write_error", Some ex)
        }

    /// Migrate one object. Never raises: a migrator exception, a
    /// storage error, and an unusable payload all become
    /// `ObjectFailed`, which the caller counts and continues past.
    let migrateObject (teamId: string) (target: SchemaVersion) (metadata: DataObject) = async {
        let currentVersion = MigrationMetadata.readVersion metadata.Metadata

        if currentVersion >= target then
            return ObjectAlreadyCurrent
        else
            let failure reason = {
                ObjectId = metadata.ObjectId
                AtVersion = currentVersion
                Error = reason
                OccurredAt = DateTime.UtcNow
            }

            match registry.ResolveChain(metadata.DataType, currentVersion) with
            | Error chainError -> return ObjectFailed(failure (MigrationChainError.describe chainError))
            | Ok [] ->
                // Resolution found nothing to do despite a lagging
                // stamp — unreachable given the guard above, kept
                // explicit so a future chain change cannot turn it into
                // a silent no-op write.
                return ObjectAlreadyCurrent
            | Ok chain ->
                let! contentResult = dataObjectStore.GetContent(teamId, metadata.ContentHash)

                match contentResult with
                | Error storageError -> return ObjectFailed(failure $"Could not read content: %A{storageError}")
                | Ok content ->
                    let! migrated = MigrationExecution.runChain chain content

                    match migrated with
                    | Error reason -> return ObjectFailed(failure reason)
                    | Ok bytes ->
                        let! saved =
                            dataObjectStore.Save(
                                teamId,
                                metadata.ObjectId,
                                bytes,
                                metadata.DataType,
                                MigrationMetadata.MigrationPrincipal,
                                MigrationMetadata.stampVersion target metadata.Metadata,
                                metadata.Policy
                            )

                        match saved with
                        | Error storageError ->
                            return ObjectFailed(failure $"Could not write upgraded object: %A{storageError}")
                        | Ok _ -> return ObjectUpgraded
    }

    /// Run a pass for one (team, data type) and return the status it
    /// persisted. Safe to call concurrently for different teams; two
    /// concurrent passes over the SAME pair race on the status blob's
    /// counters but never on correctness, because the stamps decide the
    /// work and a `Save` that lost the race finds the object already
    /// current next time.
    member _.RunForTeam(teamId: string, dataType: DataType) : Async<MigrationStatus> = async {
        let target = dataType.SchemaVersion
        let startedAt = DateTime.UtcNow
        let baseline = MigrationStatus.idle teamId dataType.Id target

        match registry.ValidateSet dataType.Id with
        | Error chainError ->
            // A structural defect in the migrator set: not one object
            // is touched, and the reason is recorded where an operator
            // will look for it.
            let reason = MigrationChainError.describe chainError
            logger.Warn $"[Migrations] event=pass_blocked team=%s{teamId} dataType=%s{dataType.Id} reason=%s{reason}"

            let blocked = {
                baseline with
                    State = MigrationBlocked reason
                    StartedAt = Some startedAt
                    CompletedAt = Some DateTime.UtcNow
            }

            let! _ = statusStore.Write blocked
            return blocked
        | Ok() ->
            let! allObjects = dataObjectStore.ListObjects teamId
            let objects = allObjects |> List.filter (fun o -> o.DataType = dataType.Id)

            let running = {
                baseline with
                    TotalObjects = List.length objects
                    State = MigrationInProgress
                    StartedAt = Some startedAt
            }

            let! _ = statusStore.Write running

            // Ref cells rather than `let mutable`: the loop body below
            // crosses `let!` boundaries, and F# forbids capturing a
            // mutable local in the closures an async CE builds.
            let migrated = ref 0
            let alreadyCurrent = ref 0
            let failed = ref 0
            let failures = ref List.empty<MigrationFailure>
            let processed = ref 0

            for metadata in objects do
                let! outcome = migrateObject teamId target metadata

                match outcome with
                | ObjectUpgraded -> migrated.Value <- migrated.Value + 1
                | ObjectAlreadyCurrent -> alreadyCurrent.Value <- alreadyCurrent.Value + 1
                | ObjectFailed failure ->
                    failed.Value <- failed.Value + 1

                    failures.Value <- failure :: failures.Value |> List.truncate MigrationStatus.maxRetainedFailures

                    logger.Error(
                        $"[Migrations] event=object_failed team=%s{teamId} dataType=%s{dataType.Id} object=%s{failure.ObjectId} atVersion=%d{failure.AtVersion} target=%d{target} error=%s{failure.Error}",
                        None
                    )

                    do! emitFailedEvent teamId dataType.Id target failure

                processed.Value <- processed.Value + 1

                if processed.Value % MigrationExecution.statusWriteInterval = 0 then
                    let! _ =
                        statusStore.Write {
                            running with
                                MigratedObjects = migrated.Value
                                AlreadyCurrentObjects = alreadyCurrent.Value
                                FailedObjects = failed.Value
                                Failures = failures.Value
                        }

                    ()

            let final = {
                running with
                    MigratedObjects = migrated.Value
                    AlreadyCurrentObjects = alreadyCurrent.Value
                    FailedObjects = failed.Value
                    Failures = failures.Value
                    State =
                        (if failed.Value > 0 then
                             MigrationCompleteWithFailures
                         else
                             MigrationComplete)
                    CompletedAt = Some DateTime.UtcNow
            }

            let! _ = statusStore.Write final

            if migrated.Value > 0 || failed.Value > 0 then
                logger.Info
                    $"[Migrations] event=pass_complete team=%s{teamId} dataType=%s{dataType.Id} target=%d{target} migrated=%d{migrated.Value} alreadyCurrent=%d{alreadyCurrent.Value} failed=%d{failed.Value}"

            return final
    }

    /// Run a pass for one team across every data type that could hold
    /// stale objects. A data type still at the floor version is skipped
    /// without a blob read (GP 13).
    member this.RunForTeam(teamId: string) : Async<MigrationStatus list> = async {
        let results = ref List.empty<MigrationStatus>

        for dataType in registry.MigratableDataTypes do
            let! status = this.RunForTeam(teamId, dataType)
            results.Value <- status :: results.Value

        return List.rev results.Value
    }

    /// Run a pass across every supplied team. Sequential on purpose: a
    /// startup sweep that fanned out across every tenant at once would
    /// contend with the deployment's own first requests for the same
    /// blob backend.
    member this.RunForTeams(teamIds: string list) : Async<MigrationStatus list> = async {
        let results = ref List.empty<MigrationStatus>

        for teamId in teamIds do
            let! statuses = this.RunForTeam teamId
            results.Value <- List.rev statuses @ results.Value

        return List.rev results.Value
    }

// ─── MigrationRunnerService ──────────────────────────────────────

/// One-shot startup sweep. Registered only under
/// `ServerConfig.DataMigrations = EnabledDataMigrations`, and gated on
/// the process-profile matrix like every other background subsystem —
/// a `WebOnly` silo does not migrate, a `WorkerOnly` one does, and a
/// serverless host runs nothing.
///
/// Deliberately a `BackgroundService` rather than a blocking
/// `StartAsync` body: a sweep over a large tenant set can take
/// minutes, and a deployment must not fail its readiness probe waiting
/// for one. Reads during the sweep stay correct throughout — each
/// object is either at its old version or its new one.
type MigrationRunnerService(runner: MigrationRunner, teamStore: TeamManagement.ITeamStore option, logger: ILogger) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            try
                if not stoppingToken.IsCancellationRequested then
                    match teamStore with
                    | None ->
                        // No team concept in this deployment's mode, so
                        // there is no scope set to sweep. The manual
                        // trigger still works against the caller's own
                        // resolved scope.
                        logger.Info
                            "[Migrations] event=startup_sweep_skipped reason=no_team_store — use the admin trigger to migrate a scope."
                    | Some store ->
                        let! teams = store.ListTeams() |> Async.StartAsTask
                        let teamIds = teams |> List.map _.TeamId

                        if not teamIds.IsEmpty then
                            logger.Info $"[Migrations] event=startup_sweep_begin teams=%d{List.length teamIds}"
                            let! statuses = runner.RunForTeams teamIds |> Async.StartAsTask

                            let migrated = statuses |> List.sumBy _.MigratedObjects
                            let failed = statuses |> List.sumBy _.FailedObjects

                            logger.Info
                                $"[Migrations] event=startup_sweep_complete teams=%d{List.length teamIds} migrated=%d{migrated} failed=%d{failed}"
            with
            | :? OperationCanceledException -> ()
            | ex -> logger.Error("[Migrations] event=startup_sweep_error", Some ex)
        }
        :> Task