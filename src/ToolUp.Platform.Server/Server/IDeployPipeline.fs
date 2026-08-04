// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── IDeployPipeline (Phase 26 substrate) ────────────────────────────
//
// Server-only orchestrator for the per-tenant deploy lifecycle.
// Bridges `IBuildOrchestrator` (which produced the artefact) with
// `IContainerScheduler` (which runs containers). Persists every
// state transition as a `ModuleEvent` under
// `_platform.deploy` so the deploy history is queryable through the
// standard `IEventStore` surface.
//
// **One pipeline per process.** Like the other substrate
// orchestrators, the SDK registers a singleton `IDeployPipeline`.
// The single-node default (`DefaultDeployPipeline`) orchestrates
// build → push → launch → healthcheck → traffic-flip phases against
// the four substrate interfaces.
//
// **Linear state machine per deploy.** A deploy progresses through
// `DeployState` cases monotonically. Mid-flight transitions
// (`DeployQueued` → `DeployPushing`, etc.) emit one audit event each;
// terminal transitions (`DeploySucceeded`, `DeployFailed`,
// `DeployRolledBack`) emit a final summary event the operator's
// dashboard subscribes to via `IEventStore.ReadBySource`.
//
// **Six-rule portability audit (Phase 9c, Guiding Principle 12):**
//
//   1. Identity by value      — every method takes/returns
//                                `DeployId`, `TenantId`, `BuildId`
//                                (all string aliases). `DeploySummary`
//                                is a record.
//   2. Async at every boundary — every method returns `Async<_>`.
//   3. Retry/supervision as data — failure flows through
//                                  `DeployPipelineError`; no
//                                  `OnFailure`-shaped callbacks.
//                                  Pipeline-internal retries (e.g.
//                                  re-pull on transient registry
//                                  failure) are encoded as part of
//                                  the pipeline's state machine, not
//                                  callbacks.
//   4. Stateless between calls — pipeline state is persisted to
//                                `IEventStore`; an Orleans grain
//                                deactivation or Akka.Persistence
//                                restart re-reads from the store and
//                                resumes.
//   5. No cross-shard ordering — deploys for different tenants have
//                                no relative ordering. Deploys for
//                                the same tenant are serialised by
//                                `ITenantFleet`'s per-tenant single-
//                                actor invariant.
//   6. Precision at lower bound — `StateChangedAt` precision matches
//                                  the underlying `IEventStore` write
//                                  precision (millisecond — the
//                                  default `DateTime.UtcNow`).

/// Server-side orchestrator for the deploy lifecycle. Companions
/// register their own implementation via
/// `services.AddSingleton<IDeployPipeline>`; the SDK ships
/// `DefaultDeployPipeline` as the single-node default. State is
/// persisted to `IEventStore` under
/// `SourceModule = "_platform.deploy"` — the deploy history is
/// queryable through the standard event-store read methods.
type IDeployPipeline =
    /// Start a fresh deploy. Persists the initial `DeployQueued`
    /// state event and returns the assigned `DeployId`. The caller
    /// polls `GetDeployState` (or subscribes to the deploy events
    /// via `IEventStore`) to follow progress.
    ///
    /// The pipeline internally chooses the right next state based
    /// on the build's outcome: a `Succeeded` build with a
    /// `PrebuiltImage` source skips `DeployBuilding`; a build that
    /// is still in-flight transitions to `DeployBuilding buildId`.
    abstract BeginDeploy:
        tenantId: TenantId * buildId: BuildId * manifest: DeployManifest * byUserId: string ->
            Async<Result<DeployId, DeployPipelineError>>

    /// Read the current state of a deploy. The returned summary
    /// reflects the latest `StateChangedAt` event in
    /// `IEventStore`.
    abstract GetDeployState: deployId: DeployId -> Async<Result<DeploySummary, DeployPipelineError>>

    /// Roll a tenant back to its previous successful deploy. The
    /// pipeline finds the most recent `DeploySucceeded` deploy
    /// prior to the current head, re-issues a `DeployRolledBack`
    /// event tagged with that deploy's id, and re-launches the
    /// containers against the rollback target's manifest+image.
    ///
    /// Returns `DeployStillRunning` when a deploy for this tenant
    /// is mid-flight (the caller cancels it first or waits for it
    /// to settle); `NothingToRollbackTo` when no prior successful
    /// deploy exists for the tenant.
    abstract Rollback: tenantId: TenantId * byUserId: string -> Async<Result<DeployId, DeployPipelineError>>

    /// Read the most recent `count` deploys for a tenant, newest
    /// first. Includes every terminal state (`DeploySucceeded`,
    /// `DeployFailed`, `DeployRolledBack`). Powers the admin UI's
    /// per-tenant deploy history panel.
    abstract GetDeployHistory: tenantId: TenantId * count: int -> Async<DeploySummary list>

// ─── IDeployPlanner (Phase 185 — the dry-run seam) ───────────────────
//
// `PlanDeploy` — the `plan`-shape preview `BeginDeploy` shipped
// without — is reachable on EVERY `IDeployPipeline` as
// `pipeline.PlanDeploy(scheduler, tenantId, target)`, an extension
// member declared in `DeployPlanner.fs` alongside the default
// implementation it falls back to. This interface is the opt-in
// override: a pipeline that can compute a better plan than the
// generic one — because it knows its scheduler's native diff, or
// tracks desired state of its own — implements `IDeployPlanner` and
// the extension routes to it instead.
//
// **Why an optional seam and not a fifth `abstract` on
// `IDeployPipeline` (GP 11).** F# 10 cannot author a default
// implementation for an interface member: adding
// `abstract PlanDeploy` to `IDeployPipeline` would break the compile
// of every existing implementer, in-tree and downstream, which is
// precisely what GP 11 forbids. Splitting the member into an opt-in
// interface plus an extension member on the base interface keeps the
// intended call site (`pipeline.PlanDeploy …` works on any pipeline)
// while leaving every implementer byte-for-byte unchanged. A
// deployment that never plans pays nothing (GP 13) — no member is
// invoked and no scheduler call is made.
//
// **Read-only, by contract.** An `IDeployPlanner` implementation MUST
// NOT mutate: no `LaunchContainer` / `StopContainer` /
// `RestartContainer`, no event write, no state transition. The
// contract pack asserts zero scheduler mutation calls fire during a
// plan, so an implementation that applies while planning fails the
// conformance bar.
//
// **Six-rule portability audit (GP 12):** rule 1 — `DeployPlan` /
// `PlannedChange` are records of string aliases, specs and statuses,
// no live handles; rule 2 — the member returns `Async<_>`; rule 3 —
// failure is `DeployPipelineError` data, never a callback; rule 4 —
// the plan is computed wholly from parameters plus a fresh read of
// observed state, so nothing survives between calls.

/// Opt-in companion seam to `IDeployPipeline`: a native dry-run
/// implementation. Implement it on a pipeline that can plan better
/// than `DeployPlanner.plan`; leave it unimplemented and the
/// `PlanDeploy` extension member falls back to the generic
/// scheduler-observed diff.
type IDeployPlanner =
    /// Compute what deploying `target` to `tenantId` would change,
    /// without changing anything. `target` is the full requested
    /// container set for the tenant — a spec absent from it is a
    /// container the plan proposes to stop.
    ///
    /// Returns `DeployStorageFailure` when observed state could not
    /// be read; a plan computed from partial state is worse than no
    /// plan, so the planner fails closed rather than under-reporting
    /// changes.
    abstract PlanDeploy:
        tenantId: TenantId * target: ContainerSpec list -> Async<Result<DeployPlan, DeployPipelineError>>