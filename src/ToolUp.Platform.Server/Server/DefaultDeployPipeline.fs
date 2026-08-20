// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.DefaultDeployPipeline

open System
open System.Collections.Concurrent
open System.Threading
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── DefaultDeployPipeline (Phase 26 substrate default) ──────────────
//
// Single-node default `IDeployPipeline`. Composes `IBuildOrchestrator`
// + `IContainerScheduler` + `IEventStore`. State persisted under
// `SourceModule = "_platform.deploy"` in the `_platform` scope — every
// state transition writes a fresh `DeployStateChanged` event carrying
// the full `DeploySummary`, so reconstruction is "scan + take latest
// by `OccurredAt`" with no in-memory cache (Phase 9c rule 4).
//
// **Scope choice (`_platform`).** Deploy events are
// operator/platform-admin-shaped data; co-locating them with the
// tenant catalog and audit corpus keeps cross-tenant operator queries
// O(1) without piercing tenant scopes. Per-tenant filtering is by
// `TenantId` field on the persisted summary, not by scopeId.
//
// **Pipeline driver runs async.** `BeginDeploy` writes the initial
// `DeployQueued` event, kicks off an `Async.Start` driver, and returns
// immediately with the assigned `DeployId`. The driver polls
// `IBuildOrchestrator.GetBuild` until the build terminates, then
// advances through `DeployPushing → DeployHealthChecking →
// DeploySucceeded` (or `DeployFailed`). Per-tenant ordering is owned
// by `ITenantFleet` (per-tenant SemaphoreSlim) — the pipeline does
// not duplicate it; concurrent deploys for the same tenant are
// expected to be gated upstream.

[<Literal>]
let DeployScopeId = "_platform"

[<Literal>]
let DeploySourceModule = "_platform.deploy"

[<Literal>]
let DeployStateChangedEventType = "DeployStateChanged"

/// Source module sealed deploy records are persisted under.
///
/// **Deliberately NOT `DeploySourceModule`.** Every existing read of
/// the deploy stream deserialises each event as a `DeploySummary`; a
/// record event landing in the same stream would be parsed as one,
/// logged as a parse failure, or — worse — silently produce a summary
/// of nulls that a tenant filter happens to exclude. A separate source
/// module means the pre-existing reads see byte-for-byte what they saw
/// before (GP 11), rather than seeing something new and coping.
[<Literal>]
let DeployRecordSourceModule = "_platform.deploy.record"

[<Literal>]
let DeployRecordSealedEventType = "DeployRecordSealed"

/// Maximum wallclock the substrate driver waits on a build to
/// terminate before declaring the deploy failed. Substrate-default
/// expedient — production-grade pipelines override via a companion.
let private BuildWaitTimeout = TimeSpan.FromMinutes 5.0

/// Polling interval against `IBuildOrchestrator.GetBuild` while the
/// build is in flight. The JobSchedulerBuildOrchestrator's synthetic-
/// success path terminates within milliseconds, so polling rarely
/// iterates more than once.
let private BuildPollInterval = TimeSpan.FromMilliseconds 250.0

// ─── JSON helper ─────────────────────────────────────────────────

module private Json =
    let private options = FableConverters.create ()

    let serialize (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    let deserialize<'T> (json: string) : 'T =
        JsonSerializer.Deserialize<'T>(json, options)

// ─── DefaultDeployPipeline ───────────────────────────────────────

// ─── Phase 656 — opt-in deploy-record sealing ────────────────────────

/// What a deployment supplies to have its deploy records sealed.
///
/// Two halves, because the substrate can supply neither. It does not
/// know what the deployed artifacts are (nothing in the deploy plane
/// enumerates a deployment's own files), and it holds no signing
/// surface (the deploy plane sits below the signing companion). So the
/// deployment provides the provenance and the sealer, and the substrate
/// provides the one thing it *does* own: the canonical bytes, computed
/// identically for the producer and for every later verifier.
type DeploySealingOptions = {
    /// Supplies the provenance recorded for a succeeded deploy — the
    /// deployed artifact digests, the build-transcript digest, and the
    /// opaque upstream slot. Returning `DeployProvenance.none` records
    /// a deploy record that claims nothing beyond its manifest, which
    /// is a legitimate thing to seal.
    Provenance: DeploySummary -> Async<DeployProvenance>
    /// The seal applied to the record's canonical bytes.
    Sealer: IDeployRecordSealer
}

/// Single-node default `IDeployPipeline`. Composes `IBuildOrchestrator`
/// + `IContainerScheduler` + `IEventStore`. Constructed at compose
/// time when `ServerConfig.DeployPlane = SingleNodeDeployPlane`.
///
/// `sealing` is `None` for every deployment that has not opted in, and
/// the four-argument constructor below preserves the pre-Phase-656
/// shape exactly — a deployment that composes no sealer emits the same
/// events, in the same order, with the same payloads (GP 11 / GP 13).
type DefaultDeployPipeline
    (
        buildOrchestrator: IBuildOrchestrator,
        containerScheduler: IContainerScheduler,
        eventStore: IEventStore,
        logger: ILogger,
        sealing: DeploySealingOptions option
    ) =

    let emitState (summary: DeploySummary) = async {
        try
            let evt =
                Events.create DeployScopeId DeploySourceModule DeployStateChangedEventType (Json.serialize summary)

            do! eventStore.Write evt
        with ex ->
            logger.Warn $"[DeployPipeline] event=write_failed deployId={summary.DeployId}: {ex.Message}"
    }

    let emitSealedRecord (signedRecord: SealedDeployRecord) = async {
        try
            let evt =
                Events.create
                    DeployScopeId
                    DeployRecordSourceModule
                    DeployRecordSealedEventType
                    (Json.serialize signedRecord)

            do! eventStore.Write evt
        with ex ->
            logger.Warn
                $"[DeployPipeline] event=record_write_failed deployId={signedRecord.Record.DeployId}: {ex.Message}"
    }

    /// Seal the deploy record for a deploy that has already succeeded.
    ///
    /// **A sealing failure does not fail the deploy**, and that is a
    /// decision rather than an oversight: by the time this runs the
    /// container is up and serving, so turning a signing problem into a
    /// failed deploy would take a healthy deployment down over a record
    /// nobody has read yet. The failure is logged and no record is
    /// written — and the ABSENCE of a record is itself the honest
    /// signal, because a checker asked to verify a deploy with no
    /// sealed record cannot mistake it for a verified one. What would
    /// be indefensible is writing an unsealed record that *looks* like
    /// a sealed one, which is why there is no such path.
    let sealDeployRecord (summary: DeploySummary) = async {
        match sealing with
        | None -> ()
        | Some options ->
            try
                let! provenance = options.Provenance summary

                let record =
                    DeployRecord.create summary.DeployId summary.TenantId summary.BuildId summary.Manifest provenance

                match! options.Sealer.Seal(DeployRecords.canonicalBytes record) with
                | Ok seal -> do! emitSealedRecord { Record = record; Seal = seal }
                | Error reason ->
                    logger.Warn $"[DeployPipeline] event=record_seal_refused deployId={summary.DeployId}: {reason}"
            with ex ->
                logger.Warn $"[DeployPipeline] event=record_seal_failed deployId={summary.DeployId}: {ex.Message}"
    }

    /// Read every persisted `DeploySummary` for a tenant, newest first.
    /// `_platform`-scoped scan filtered to the tenant via the payload's
    /// `TenantId` field.
    let readTenantSummaries (tenantId: TenantId) : Async<DeploySummary list> = async {
        let! events = eventStore.ReadBySource(DeployScopeId, DeploySourceModule)

        let parsed =
            events
            |> List.choose (fun evt ->
                try
                    let summary = Json.deserialize<DeploySummary> evt.Payload

                    if summary.TenantId = tenantId then
                        Some(evt.OccurredAt, summary)
                    else
                        None
                with ex ->
                    logger.Warn $"[DeployPipeline] event=parse_failed eventId={evt.Id}: {ex.Message}"
                    None)
            |> List.sortByDescending fst
            |> List.map snd

        return parsed
    }

    /// Latest summary per `DeployId` from a tenant's history, newest
    /// state-change first. Used by `GetDeployHistory`.
    let groupTenantHistory (tenantId: TenantId) : Async<DeploySummary list> = async {
        let! summaries = readTenantSummaries tenantId
        // Per-DeployId latest summary, then sort by StartedAt desc.
        let perDeploy =
            summaries
            |> List.groupBy _.DeployId
            |> List.map (fun (_, summaries) -> summaries |> List.head) // already newest-first

        return perDeploy |> List.sortByDescending _.StartedAt
    }

    /// Build the `ContainerSpec` the pipeline launches against. Derives
    /// env vars from `manifest.Secrets` (with the secret-source string
    /// passed verbatim as the value — operator companions resolve the
    /// real material via `ISecretStore`); honours
    /// `manifest.Runtime.Healthcheck.Path` as the probe path.
    let buildContainerSpec (manifest: DeployManifest) (imageRef: string) : ContainerSpec =
        let envVars =
            manifest.Secrets
            |> List.fold (fun acc (s: DeployManifestSecret) -> Map.add s.Name s.Source acc) Map.empty

        {
            Image = imageRef
            EnvVars = envVars
            ExposedPort = manifest.Runtime.Healthcheck.Port
            HealthcheckPath = Some manifest.Runtime.Healthcheck.Path
            Labels = Map.empty
        }

    /// Run the full pipeline asynchronously after `BeginDeploy` returns
    /// the `DeployId`. Polls the build, then advances through Pushing
    /// → HealthChecking → Succeeded (or Failed on any branch).
    let runPipeline (summary: DeploySummary) = async {
        let mutable current = summary
        let mutable terminated = false

        // ── Phase 1: wait on the build (or skip if PrebuiltImage). ──

        let prebuiltImage =
            match current.Manifest.Runtime.Image with
            | Some img when not (String.IsNullOrWhiteSpace img) -> Some img
            | _ -> None

        let! buildOutcome = async {
            match prebuiltImage with
            | Some img ->
                // Skip DeployBuilding entirely.
                return Ok img
            | None ->
                let stateBuilding = {
                    current with
                        State = DeployBuilding current.BuildId
                        StateChangedAt = DateTime.UtcNow
                }

                current <- stateBuilding
                do! emitState stateBuilding

                let deadline = DateTime.UtcNow + BuildWaitTimeout
                let mutable result: Result<string, string> option = None

                while result.IsNone && DateTime.UtcNow < deadline do
                    let! build = buildOrchestrator.GetBuild current.BuildId

                    match build with
                    | Ok summary ->
                        match summary.Status with
                        | BuildStatus.Succeeded(_, artefactRef) -> result <- Some(Ok artefactRef)
                        | BuildStatus.Failed(_, reason) -> result <- Some(Error reason)
                        | BuildStatus.Cancelled(_, byUserId) ->
                            result <- Some(Error(sprintf "build cancelled by %s" byUserId))
                        | _ -> do! Async.Sleep BuildPollInterval
                    | Error err -> result <- Some(Error(sprintf "build orchestrator: %A" err))

                return result |> Option.defaultValue (Error "build wait timeout exceeded")
        }

        match buildOutcome with
        | Error reason ->
            let failed = {
                current with
                    State = DeployFailed("build", reason)
                    StateChangedAt = DateTime.UtcNow
            }

            current <- failed
            do! emitState failed
            terminated <- true
        | Ok artefactRef when not terminated ->

            // ── Phase 2: pushing. ──

            let statePushing = {
                current with
                    State = DeployPushing artefactRef
                    StateChangedAt = DateTime.UtcNow
            }

            current <- statePushing
            do! emitState statePushing

            // ── Phase 3: launch + health check. ──

            let spec = buildContainerSpec current.Manifest artefactRef
            let! launched = containerScheduler.LaunchContainer(current.TenantId, spec)

            match launched with
            | Error err ->
                let failed = {
                    current with
                        State = DeployFailed("launch", sprintf "%A" err)
                        StateChangedAt = DateTime.UtcNow
                }

                current <- failed
                do! emitState failed
            | Ok containerId ->
                let stateHealth = {
                    current with
                        State = DeployHealthChecking [ containerId ]
                        StateChangedAt = DateTime.UtcNow
                }

                current <- stateHealth
                do! emitState stateHealth

                // ── Phase 4: succeed. ──
                //
                // Substrate default: trust the container scheduler's
                // launch reporting. Operator companions wanting probed
                // healthcheck enforcement (HTTP GET / TCP-connect /
                // exec-based liveness) wrap a richer pipeline around
                // this one — the substrate is the harness, not the
                // gate.

                let completedAt = DateTime.UtcNow

                let succeeded = {
                    current with
                        State = DeploySucceeded completedAt
                        StateChangedAt = completedAt
                }

                current <- succeeded
                do! emitState succeeded

                // ── Phase 5: seal the deploy record (opt-in). ──
                //
                // After the succeeded transition, never before it: the
                // record describes a deploy that happened, and sealing
                // one that has not yet succeeded would be sealing a
                // prediction.
                do! sealDeployRecord succeeded
        | _ -> ()
    }

    /// The pre-Phase-656 shape: no deploy-record sealing. Kept as an
    /// explicit secondary constructor rather than an optional parameter
    /// — an optional argument folds into one widened constructor and
    /// the four-argument form disappears from the public surface, which
    /// is a break for every existing caller.
    new(buildOrchestrator, containerScheduler, eventStore, logger) =
        DefaultDeployPipeline(buildOrchestrator, containerScheduler, eventStore, logger, None)

    /// Read back the sealed deploy record for `deployId`, if one was
    /// sealed. `None` for a deploy that predates sealing, for a
    /// deployment that composed no sealer, and for a deploy whose
    /// sealing failed — all three are honestly "no record", and none of
    /// them may be reported as verified.
    member _.TryGetSealedRecord(deployId: DeployId) : Async<SealedDeployRecord option> = async {
        let! events = eventStore.ReadBySource(DeployScopeId, DeployRecordSourceModule)

        return
            events
            |> List.filter (fun evt -> evt.EventType = DeployRecordSealedEventType)
            |> List.choose (fun evt ->
                try
                    let signedRecord = Json.deserialize<SealedDeployRecord> evt.Payload

                    if signedRecord.Record.DeployId = deployId then
                        Some(
                            evt.OccurredAt,
                            {
                                signedRecord with
                                    Record = DeployRecord.coerce signedRecord.Record
                            }
                        )
                    else
                        None
                with ex ->
                    logger.Warn $"[DeployPipeline] event=record_parse_failed eventId={evt.Id}: {ex.Message}"
                    None)
            |> List.sortByDescending fst
            |> List.tryHead
            |> Option.map snd
    }

    interface IDeployPipeline with

        member _.BeginDeploy
            (tenantId: TenantId, buildId: BuildId, manifest: DeployManifest, byUserId: string)
            : Async<Result<DeployId, DeployPipelineError>> =
            async {
                let deployId = Guid.NewGuid().ToString("N")
                let startedAt = DateTime.UtcNow

                let summary: DeploySummary = {
                    DeployId = deployId
                    TenantId = tenantId
                    BuildId = buildId
                    Manifest = manifest
                    State = DeployQueued
                    StartedAt = startedAt
                    StateChangedAt = startedAt
                    SubmittedBy = byUserId
                }

                do! emitState summary

                // Fire the pipeline driver. `runPipeline` emits
                // `DeployFailed` for *handled* failures (build / launch),
                // but an *unexpected* throw (orchestrator/scheduler raising,
                // a serialize error) would otherwise escape this
                // `Async.Start` and be swallowed — leaving the deploy stuck
                // in a non-terminal state with no event and no log, i.e. an
                // operator-blind hang. Wrap the fire so any unhandled throw
                // terminates the deploy visibly (DeployFailed + Error log).
                Async.Start(
                    async {
                        try
                            do! runPipeline summary
                        with ex ->
                            let failed = {
                                summary with
                                    State = DeployFailed("driver", ex.Message)
                                    StateChangedAt = DateTime.UtcNow
                            }

                            do! emitState failed

                            logger.Error(
                                $"[DeployPipeline] event=pipeline_unexpected_error deployId={summary.DeployId}",
                                Some ex
                            )
                    }
                )

                return Ok deployId
            }

        member _.GetDeployState(deployId: DeployId) : Async<Result<DeploySummary, DeployPipelineError>> = async {
            let! events = eventStore.ReadBySource(DeployScopeId, DeploySourceModule)

            let candidate =
                events
                |> List.choose (fun evt ->
                    try
                        let summary = Json.deserialize<DeploySummary> evt.Payload

                        if summary.DeployId = deployId then
                            Some(evt.OccurredAt, summary)
                        else
                            None
                    with _ ->
                        None)
                |> List.sortByDescending fst
                |> List.tryHead
                |> Option.map snd

            return
                match candidate with
                | Some s -> Ok s
                | None -> Error(UnknownDeploy deployId)
        }

        member _.Rollback(tenantId: TenantId, byUserId: string) : Async<Result<DeployId, DeployPipelineError>> = async {
            let! perDeploy = groupTenantHistory tenantId

            // Reject when any deploy is mid-flight.
            let inFlight =
                perDeploy
                |> List.tryFind (fun s ->
                    match s.State with
                    | DeployQueued
                    | DeployBuilding _
                    | DeployPushing _
                    | DeployHealthChecking _ -> true
                    | _ -> false)

            match inFlight with
            | Some s -> return Error(DeployStillRunning s.DeployId)
            | None ->
                // Skip the current head (newest by StartedAt) and find
                // the next DeploySucceeded.
                let prevSucceeded =
                    perDeploy
                    |> List.skip (min 1 perDeploy.Length)
                    |> List.tryFind (fun s ->
                        match s.State with
                        | DeploySucceeded _ -> true
                        | _ -> false)

                match prevSucceeded with
                | None -> return Error(NothingToRollbackTo tenantId)
                | Some target ->
                    let rollbackDeployId = Guid.NewGuid().ToString("N")
                    let now = DateTime.UtcNow

                    let rollbackSummary: DeploySummary = {
                        DeployId = rollbackDeployId
                        TenantId = tenantId
                        BuildId = target.BuildId
                        Manifest = target.Manifest
                        State = DeployRolledBack target.DeployId
                        StartedAt = now
                        StateChangedAt = now
                        SubmittedBy = byUserId
                    }

                    // Recover the artefactRef the target deploy actually
                    // ran from its persisted `DeployPushing` transition —
                    // every succeeded deploy emitted one (the prebuilt-
                    // image path included). For a build-sourced deploy
                    // (no `manifest.Runtime.Image`) this is the ONLY
                    // record of the pushed image; the synthetic
                    // `local-build:` fallback below names an image no
                    // registry serves and survives only for histories
                    // whose `DeployPushing` event was lost.
                    let! tenantSummaries = readTenantSummaries tenantId

                    let pushedImageRef =
                        tenantSummaries
                        |> List.tryPick (fun s ->
                            match s.State with
                            | DeployPushing imageRef when s.DeployId = target.DeployId -> Some imageRef
                            | _ -> None)

                    do! emitState rollbackSummary

                    let effectiveImage =
                        match pushedImageRef, target.Manifest.Runtime.Image with
                        | Some r, _ -> r
                        | None, Some img -> img
                        | None, None -> sprintf "local-build:%s:%s" target.Manifest.App.Slug target.BuildId

                    let spec = buildContainerSpec target.Manifest effectiveImage
                    let! launched = containerScheduler.LaunchContainer(tenantId, spec)

                    match launched with
                    | Ok _ -> return Ok rollbackDeployId
                    | Error err ->
                        // The rollback launched nothing — surface it.
                        // The `DeployRolledBack` record above stands as
                        // operator intent; the terminal `DeployFailed`
                        // transition records that the relaunch did not
                        // happen, and the caller gets the failure
                        // rather than a rollback id that looks healthy.
                        let failed = {
                            rollbackSummary with
                                State = DeployFailed("rollback-launch", sprintf "%A" err)
                                StateChangedAt = DateTime.UtcNow
                        }

                        do! emitState failed

                        logger.Error(
                            $"[DeployPipeline] event=rollback_launch_failed deployId={rollbackDeployId}: %A{err}",
                            None
                        )

                        return
                            Error(
                                DeployStorageFailure(
                                    sprintf
                                        "rollback %s launch failed: image %s: %A"
                                        rollbackDeployId
                                        effectiveImage
                                        err
                                )
                            )
        }

        member _.GetDeployHistory(tenantId: TenantId, count: int) : Async<DeploySummary list> = async {
            let! perDeploy = groupTenantHistory tenantId
            return perDeploy |> List.truncate (max 0 count)
        }

    // ─── Phase 185 — the dry-run (read-only) ─────────────────────
    //
    // Declaring `IDeployPlanner` makes the `PlanDeploy` extension
    // member route here rather than to its generic fallback: the
    // pipeline already holds the `IContainerScheduler` it deploys
    // against, so an operator planning through the pipeline need not
    // supply one. The diff itself is `DeployPlanner.plan` — the same
    // read-only computation every other pipeline gets — so there is
    // one diff implementation in the substrate, not two that can
    // drift.
    //
    // Nothing here mutates: no scheduler launch/stop/restart, no
    // `emitState`, no `DeployState` transition. A plan leaves the
    // event stream and the container set exactly as it found them,
    // and `BeginDeploy` above is untouched — a deployment that never
    // plans is byte-for-byte what it was (GP 11 / GP 13).

    interface IDeployPlanner with

        member _.PlanDeploy
            (tenantId: TenantId, target: ContainerSpec list)
            : Async<Result<DeployPlan, DeployPipelineError>> =
            DeployPlanner.plan containerScheduler tenantId target

// ─── Convenience constructor ─────────────────────────────────────

let create
    (buildOrchestrator: IBuildOrchestrator)
    (containerScheduler: IContainerScheduler)
    (eventStore: IEventStore)
    (logger: ILogger)
    : DefaultDeployPipeline =
    DefaultDeployPipeline(buildOrchestrator, containerScheduler, eventStore, logger)

/// The same pipeline, with deploy-record sealing composed in. A
/// separate entry point rather than an optional argument on `create`,
/// for the reason the secondary constructor exists: `create` is public
/// surface and must keep the shape it had.
let createSealed
    (buildOrchestrator: IBuildOrchestrator)
    (containerScheduler: IContainerScheduler)
    (eventStore: IEventStore)
    (logger: ILogger)
    (sealing: DeploySealingOptions)
    : DefaultDeployPipeline =
    DefaultDeployPipeline(buildOrchestrator, containerScheduler, eventStore, logger, Some sealing)