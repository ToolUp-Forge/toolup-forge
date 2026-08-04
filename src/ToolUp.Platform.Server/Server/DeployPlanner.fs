// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── DeployPlanner (Phase 185) ───────────────────────────────────────
//
// The default `plan`-shape dry-run over the Phase 26 deploy plane:
// given a tenant and the container set a manifest requests, compute
// what applying it would change against the scheduler's OBSERVED
// state — and change nothing while doing so.
//
// **Read-only by construction.** The planner touches exactly two
// `IContainerScheduler` members, both reads:
// `ListContainers (Some tenantId)` for the tenant's containers, then
// `GetContainerStatus` per container for live status (`ListContainers`
// documents its `Status` as a list-time snapshot, so a plan that
// trusted it would diff against stale state). It never calls
// `LaunchContainer`, `StopContainer` or `RestartContainer`, writes no
// event, and holds no state between calls. The contract pack asserts
// the zero-mutation property against a recording scheduler, and
// mutation-checks that assertion against a deliberately-applying
// planner.
//
// **The diff is a multiset diff keyed on image reference.** That key
// is not an arbitrary choice: `ContainerInfo` — everything an
// `IContainerScheduler` is obliged to surface about a running
// container — carries `ContainerId`, `TenantId`, `ImageRef`, `Status`
// and `Labels`. The image is the only field a requested
// `ContainerSpec` and an observed container have in common, so it is
// the only honest join. For each image the planner pairs requested
// specs against observed containers:
//
//   * paired, observed running        → `NoChange`
//   * paired, observed transitional   → `NoChange` (converging under
//     (`ContainerCreated` /            the scheduler on its own; apply
//      `ContainerRestarting`)          would only interrupt it)
//   * paired, observed not serving    → `Restart`
//     (`ContainerExited` /
//      `ContainerCrashed` /
//      `ContainerNotFound`)
//   * requested with no observed peer → `Launch`
//   * observed with no requested peer → `Stop`
//
// An image CHANGE therefore reads as `Stop` the old + `Launch` the
// new, which is what apply would genuinely do — a running container's
// image cannot be mutated in place on any scheduler this substrate
// abstracts.
//
// **Known limit, stated rather than implied.** Drift in a spec field
// the scheduler does not surface — an env-var edit, a port change, a
// healthcheck path — is NOT observable through `IContainerScheduler`,
// so it cannot appear in the plan; such a container reads `NoChange`
// while running. Closing that needs either a scheduler surface that
// reports the effective spec or a pipeline that records desired state
// — both pipeline-specific, which is what `IDeployPlanner` exists for.

/// The default dry-run diff. Any `IDeployPipeline` gets it for free
/// via the `PlanDeploy` extension member below; an implementer
/// wanting it under its own `IDeployPlanner` needs one line —
/// `DeployPlanner.plan scheduler tenantId target`.
module DeployPlanner =

    /// Is the observed container serving, or converging towards it
    /// without help? Both answer `NoChange`; the distinction only
    /// changes the reason text.
    let private classifyObserved (status: ContainerStatus) : PlannedChangeKind * string =
        match status with
        | ContainerRunning since -> PlannedChangeKind.NoChange, sprintf "already running since %s" (since.ToString "O")
        | ContainerCreated -> PlannedChangeKind.NoChange, "created and starting — converging without intervention"
        | ContainerRestarting -> PlannedChangeKind.NoChange, "restarting — converging without intervention"
        | ContainerExited(code, at) ->
            PlannedChangeKind.Restart, sprintf "requested but exited with code %d at %s" code (at.ToString "O")
        | ContainerCrashed(reason, at) ->
            PlannedChangeKind.Restart, sprintf "requested but crashed at %s: %s" (at.ToString "O") reason
        | ContainerNotFound -> PlannedChangeKind.Restart, "requested but the scheduler has lost the container"

    /// Rank used to order a plan's changes deterministically:
    /// mutations first, most-consequential first, unchanged last.
    let private kindRank (kind: PlannedChangeKind) : int =
        match kind with
        | PlannedChangeKind.Launch -> 0
        | PlannedChangeKind.Restart -> 1
        | PlannedChangeKind.Stop -> 2
        | PlannedChangeKind.NoChange -> 3

    /// Total, deterministic order over a plan's changes, so two plans
    /// computed from the same observed state are structurally equal
    /// regardless of the order the scheduler enumerated containers in
    /// (`ListContainers` promises only "scheduler-defined" ordering).
    let private orderChanges (changes: PlannedChange list) : PlannedChange list =
        changes
        |> List.sortBy (fun c -> kindRank c.Kind, c.ImageRef, (c.ContainerId |> Option.defaultValue ""))

    /// Pure diff over already-observed state. Separated from the
    /// scheduler read so the classification is testable without any
    /// `IContainerScheduler` at all, and so the read path stays
    /// obviously read-only.
    ///
    /// `observed` is the tenant's containers with LIVE statuses
    /// (`ContainerInfo.Status` re-read per container, not the
    /// list-time snapshot).
    let diff
        (tenantId: TenantId)
        (target: ContainerSpec list)
        (observed: ContainerInfo list)
        (observedAt: DateTime)
        : DeployPlan =
        // Bucket both sides by image, the only common key. Observed
        // containers are sorted by `ContainerId` FIRST, because
        // `ListContainers` promises only "scheduler-defined" ordering
        // — several shipped backends enumerate a hash map. Without
        // this, two containers on the same image would be assigned
        // their roles (paired vs surplus) by enumeration luck, so two
        // plans over identical state could disagree about WHICH
        // container to stop. Sorting the ids makes the pairing total
        // and reproducible; the final `orderChanges` only fixes the
        // order changes are listed in, which is not the same thing.
        let requestedByImage = target |> List.groupBy _.Image |> Map.ofList

        let observedByImage =
            observed |> List.sortBy _.ContainerId |> List.groupBy _.ImageRef |> Map.ofList

        let images =
            (requestedByImage |> Map.toList |> List.map fst)
            @ (observedByImage |> Map.toList |> List.map fst)
            |> List.distinct

        let changes =
            images
            |> List.collect (fun image ->
                let requested = requestedByImage |> Map.tryFind image |> Option.defaultValue []
                // Already id-sorted (see the bucketing above): the
                // lowest ids pair off, so a surplus is taken from the
                // tail deterministically.
                let running = observedByImage |> Map.tryFind image |> Option.defaultValue []

                let paired = min requested.Length running.Length

                // ── Paired: NoChange or Restart, per observed status.
                let pairedChanges =
                    List.zip (requested |> List.truncate paired) (running |> List.truncate paired)
                    |> List.map (fun (spec, info) ->
                        let kind, reason = classifyObserved info.Status

                        {
                            ContainerId = Some info.ContainerId
                            TenantId = tenantId
                            Kind = kind
                            Target = Some spec
                            ImageRef = image
                            Observed = info.Status
                            Reason = reason
                        })

                // ── Surplus requested: nothing running it → Launch.
                let launches =
                    requested
                    |> List.skip paired
                    |> List.map (fun spec -> {
                        ContainerId = None
                        TenantId = tenantId
                        Kind = PlannedChangeKind.Launch
                        Target = Some spec
                        ImageRef = image
                        Observed = ContainerNotFound
                        Reason = "requested by the manifest with no container running it"
                    })

                // ── Surplus observed: no longer requested → Stop.
                let stops =
                    running
                    |> List.skip paired
                    |> List.map (fun info -> {
                        ContainerId = Some info.ContainerId
                        TenantId = tenantId
                        Kind = PlannedChangeKind.Stop
                        Target = None
                        ImageRef = image
                        Observed = info.Status
                        Reason = "running but no longer requested by the manifest"
                    })

                pairedChanges @ launches @ stops)

        {
            TenantId = tenantId
            Changes = orderChanges changes
            ObservedAt = observedAt
        }

    /// Read observed state for `tenantId` and diff `target` against
    /// it. Two scheduler reads, no writes.
    ///
    /// Fails closed with `DeployStorageFailure` when observed state
    /// cannot be read — an under-reported plan is more dangerous than
    /// an absent one, because the missing lines are exactly the ones
    /// an operator would have wanted to see.
    let plan
        (scheduler: IContainerScheduler)
        (tenantId: TenantId)
        (target: ContainerSpec list)
        : Async<Result<DeployPlan, DeployPipelineError>> =
        async {
            let observedAt = DateTime.UtcNow

            try
                let! listed = scheduler.ListContainers(Some tenantId)

                // Re-read live status per container: `ListContainers`
                // documents its `Status` as a list-time snapshot and
                // directs callers to `GetContainerStatus` for live
                // state. Sequential on purpose — a plan is an
                // operator-initiated read over one tenant's
                // containers, and fanning out concurrent reads at a
                // scheduler backend buys nothing at that cardinality.
                let mutable failure: ContainerSchedulerError option = None
                let live = ResizeArray<ContainerInfo>()

                for info in listed do
                    if failure.IsNone then
                        let! status = scheduler.GetContainerStatus info.ContainerId

                        match status with
                        | Ok live' -> live.Add { info with Status = live' }
                        | Error err -> failure <- Some err

                match failure with
                | Some err ->
                    return
                        Error(
                            DeployStorageFailure(
                                sprintf "deploy plan: could not read observed container status: %A" err
                            )
                        )
                | None -> return Ok(diff tenantId target (List.ofSeq live) observedAt)
            with ex ->
                return Error(DeployStorageFailure(sprintf "deploy plan: container scheduler failed: %s" ex.Message))
        }

/// `PlanDeploy` on every `IDeployPipeline`. Auto-opened with the
/// `ToolUp.Platform` namespace, so an existing consumer gains the
/// member without a new `open` and an existing implementer without a
/// recompile (GP 11).
[<AutoOpen>]
module DeployPipelinePlanning =

    type IDeployPipeline with

        /// Compute the apply diff for `target` against `scheduler`'s
        /// observed state for `tenantId` — the dry-run preceding
        /// `BeginDeploy`. Mutates nothing.
        ///
        /// Routes to the pipeline's own `IDeployPlanner` when it
        /// implements one, else to `DeployPlanner.plan`. `target` is
        /// the FULL requested container set for the tenant: a
        /// container running an image absent from it is planned for
        /// `Stop`, so passing a partial set under-reports nothing but
        /// over-reports stops.
        member this.PlanDeploy
            (scheduler: IContainerScheduler, tenantId: TenantId, target: ContainerSpec list)
            : Async<Result<DeployPlan, DeployPipelineError>> =
            match box this with
            | :? IDeployPlanner as planner -> planner.PlanDeploy(tenantId, target)
            | _ -> DeployPlanner.plan scheduler tenantId target