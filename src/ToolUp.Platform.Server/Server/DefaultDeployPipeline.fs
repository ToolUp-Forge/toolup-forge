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

/// Single-node default `IDeployPipeline`. Composes `IBuildOrchestrator`
/// + `IContainerScheduler` + `IEventStore`. Constructed at compose
/// time when `ServerConfig.DeployPlane = SingleNodeDeployPlane`.
type DefaultDeployPipeline
    (
        buildOrchestrator: IBuildOrchestrator,
        containerScheduler: IContainerScheduler,
        eventStore: IEventStore,
        logger: ILogger
    ) =

    let emitState (summary: DeploySummary) = async {
        try
            let evt =
                Events.create DeployScopeId DeploySourceModule DeployStateChangedEventType (Json.serialize summary)

            do! eventStore.Write evt
        with ex ->
            logger.Warn $"[DeployPipeline] event=write_failed deployId={summary.DeployId}: {ex.Message}"
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
        | _ -> ()
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

                    do! emitState rollbackSummary

                    // Re-launch the rollback target's container. Best-
                    // effort; failure here is logged but the rollback
                    // record stands (the event chain reflects operator
                    // intent regardless of whether the new container
                    // launches).
                    let imageRef =
                        match target.State with
                        | DeploySucceeded _ ->
                            // Recover the artefactRef from the matching
                            // DeployPushing transition for this deploy.
                            None
                        | _ -> None
                    // Fall back: pull manifest.Runtime.Image when set.
                    let effectiveImage =
                        match imageRef, target.Manifest.Runtime.Image with
                        | Some r, _ -> r
                        | None, Some img -> img
                        | None, None -> sprintf "local-build:%s:%s" target.Manifest.App.Slug target.BuildId

                    let spec = buildContainerSpec target.Manifest effectiveImage
                    let! launched = containerScheduler.LaunchContainer(tenantId, spec)

                    match launched with
                    | Ok _ -> return Ok rollbackDeployId
                    | Error err ->
                        logger.Warn
                            $"[DeployPipeline] event=rollback_launch_failed deployId={rollbackDeployId}: %A{err}"

                        return Ok rollbackDeployId
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