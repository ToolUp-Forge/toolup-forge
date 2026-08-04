# Phase 185 — Deploy-manifest plan/diff (dry-run apply)

**What changes.** The deploy plane gains a `plan`-shape preview. `IDeployPipeline.BeginDeploy` applies a
deploy; there was no machine-checkable way to see what it would change first. `PlanDeploy` is that step:

```fsharp
open ToolUp.Platform

let! result = pipeline.PlanDeploy(containerScheduler, tenantId, targetSpecs)

match result with
| Ok plan when DeployPlan.isNoOp plan -> printfn "already converged — nothing to deploy"
| Ok plan ->
    printfn "%s" (DeployPlan.summarise plan) // "1 to launch, 0 to restart, 1 to stop, 2 unchanged"

    for change in DeployPlan.mutating plan do
        printfn "  %A %s (%A)" change.Kind change.ImageRef change.ContainerId
| Error e -> eprintfn "could not plan: %A" e
```

Computing a plan **mutates nothing** — no `LaunchContainer` / `StopContainer` / `RestartContainer`, no
event write, no deploy-state transition. `BeginDeploy` and the rest of the Phase 26 surface are
untouched, so a deployment that never plans is byte-for-byte what it was (GP 11 / GP 13).

**ADDITIVE — nothing to do.** No existing consumer needs a change to keep compiling or to keep
behaving identically. Adopt `PlanDeploy` when you want the preview.

## Why this is not a fifth `abstract` member on `IDeployPipeline`

The obvious shape — `abstract PlanDeploy` on `IDeployPipeline` with a default implementation — **is not
expressible in F# 10**: the language can *consume* default interface members but cannot *author* them
(adding a concrete or `default` member to an interface declaration makes it a class:
`FS0887: The type 'IDeployPipeline' is not an interface type`). Adding a plain `abstract` member would
therefore have broken the compile of every existing implementer, in-tree and downstream, which is
exactly what GP 11 forbids. The surface is split in three instead:

| Piece | Where | Who touches it |
|---|---|---|
| `DeployPlan` / `PlannedChange` / `PlannedChangeKind` + the `DeployPlan` helpers | `ToolUp.Platform.Core`, `Shared/Types/DeployPlaneTypes.fs` (Fable-safe) | read by everyone |
| `PlanDeploy` **extension member** on `IDeployPipeline` | `ToolUp.Platform.Server`, `Server/DeployPlanner.fs` (`[<AutoOpen>]`) | nobody — it is free |
| `DeployPlanner.plan` — the default read-only diff | same file | an implementer opting in natively |
| `IDeployPlanner` — the optional native-override seam | `Server/IDeployPipeline.fs` | only a pipeline that wants to override |

`pipeline.PlanDeploy(scheduler, tenantId, target)` resolves on **any** `IDeployPipeline`. It routes to
the pipeline's own `IDeployPlanner` when it declares one, and to `DeployPlanner.plan` otherwise.
`IDeployPipeline` itself gained **no member** — visible in the regenerated
`api-baselines/ToolUp.Platform.Server.approved.txt`, whose diff is 7 additions and 0 removals.

## Adopting a native `PlanDeploy` (optional)

Only worth doing when your pipeline can plan better than the generic diff — it knows its scheduler's
native diff, or it records desired state of its own. One interface, one member:

```fsharp
type MyDeployPipeline(scheduler: IContainerScheduler, (* … *)) =
    interface IDeployPipeline with
        member _.BeginDeploy(tenantId, buildId, manifest, byUserId) = (* unchanged *)
    // … the rest unchanged …

    interface IDeployPlanner with
        member _.PlanDeploy(tenantId, target) =
            // Delegating to the substrate default is a legitimate
            // implementation — it saves callers passing a scheduler.
            DeployPlanner.plan scheduler tenantId target
```

An `IDeployPlanner` implementation **must be read-only**. The contract pack
(`IDeployPipelineContract.planTests`) asserts zero scheduler mutation calls fire during a plan, against a
recording scheduler; a planner that applies while planning fails the conformance bar.

## How the diff is computed

A multiset diff per tenant, keyed on **image reference** — the only field a requested `ContainerSpec` and
an observed `ContainerInfo` have in common, so the only honest join. Observed state comes from
`IContainerScheduler.ListContainers (Some tenantId)` plus a `GetContainerStatus` re-read per container
(`ListContainers` documents its `Status` as a list-time snapshot).

| Situation | `PlannedChangeKind` |
|---|---|
| requested, observed `ContainerRunning` | `NoChange` |
| requested, observed `ContainerCreated` / `ContainerRestarting` | `NoChange` — converging without intervention |
| requested, observed `ContainerExited` / `ContainerCrashed` / `ContainerNotFound` | `Restart` |
| requested, nothing observed running that image | `Launch` |
| observed, image absent from the requested set | `Stop` |

Two consequences worth knowing before you read a plan:

- **`target` is the FULL requested container set for the tenant.** A container running an image absent
  from it is planned for `Stop`. Passing a partial set over-reports stops.
- **An image change reads as `Stop` the old + `Launch` the new**, because that is what apply genuinely
  does — no scheduler this substrate abstracts can mutate a running container's image in place.
- **Drift in a field the scheduler does not surface is invisible.** An env-var edit, a port change or a
  healthcheck-path change cannot appear in the plan (`ContainerInfo` carries only id / tenant / image /
  status / labels), so such a container reads `NoChange` while running. Closing that needs a scheduler
  that reports the effective spec, or a pipeline that records desired state — which is what
  `IDeployPlanner` exists for.

`Changes` is in a deterministic order (`Launch`, `Restart`, `Stop`, `NoChange`, then image, then
container id), so two plans over the same observed state compare equal by value despite
`ListContainers` promising only scheduler-defined ordering. `ObservedAt` is a wallclock snapshot — a
plan is a snapshot, and state can move between plan and apply exactly as with any plan/apply split.

A failure to read observed state fails the plan **closed** (`DeployPipelineError.DeployStorageFailure`,
message prefixed `deploy plan:`) rather than emitting a plan built from partial state: the lines that
would be missing are precisely the ones an operator wanted to see. No new `DeployPipelineError` case was
added, so consumer matches on that DU stay exhaustive.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — green.
- 37 new Expecto cases: `IDeployPipelineContract.planTests` bound twice (15 each) — once against
  `DefaultDeployPipeline` (native `IDeployPlanner` route) and once against the pre-Phase-185
  `InMemoryDeployPipeline`, left byte-for-byte untouched, exercising the fallback route (that binding
  *is* the GP 11 evidence); 6 pure-diff classification cases; and 1 mutation check.
- The mutation check exists because "no mutation happened" is the shape that passes vacuously when the
  probe saw nothing. It drives the same recording probe with a planner that deliberately applies while
  planning and asserts the probe reports it — and the read-only assertion was additionally demonstrated
  red by temporarily binding the pack to that planner
  (`a plan mutates nothing: no Launch/Stop/Restart call fired. Should be empty.`).
- Every zero-mutation case also asserts the reads it expected *did* fire, so it cannot certify a no-op.

## Rollback

Revert the phase's commit. Nothing persists a `DeployPlan`, no configuration selects it, and no existing
code path invokes it, so removal is a pure surface subtraction with no data or behavioural residue.

## See also

- `src/ToolUp.Platform.Server/Server/DeployPlanner.fs` — the default diff, fully commented.
- `src/ToolUp.Platform.Server/Server/IDeployPipeline.fs` — the `IDeployPlanner` seam + the GP 11 rationale.
- `src/ToolUp.Platform.Core/Shared/Types/DeployPlaneTypes.fs` — the plan records (Fable-safe).
- `src/ToolUp.Platform.Tests/Contracts/IDeployPipelineContract.fs` — `planTests` + the recording scheduler.
