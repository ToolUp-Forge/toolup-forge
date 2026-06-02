// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── IBuildOrchestrator (Phase 26 substrate) ─────────────────────────
//
// Server-only orchestrator for the deploy-plane build queue. Wraps a
// queueing surface + persistence + retry policy. The substrate ships
// a single-node default (`JobSchedulerBuildOrchestrator`) backed by
// `IJobScheduler`; distributed companions (Akka cluster, Kubernetes
// Jobs, etc.) live downstream and replace this singleton.
//
// **One orchestrator per process.** Like `IJobScheduler`, the SDK
// registers a single `IBuildOrchestrator` singleton. Multi-node
// deployments need a distributed companion to avoid double-build
// dispatch; the in-process default ticks against the local
// `IJobScheduler` and is safe at single-instance scale.
//
// **Six-rule portability audit (Phase 9c, Guiding Principle 12):**
//
//   1. Identity by value      — every method takes/returns `BuildId`
//                                (string) and `AppSlug` (string).
//                                `BuildRequest` is a record with no
//                                live handles. No `IActorRef`-shaped
//                                identity.
//   2. Async at every boundary — every method returns `Async<_>`. No
//                                fire-and-forget `Tell`-shaped
//                                signatures.
//   3. Retry/supervision as data — `BuildRetryPolicy` is a record on
//                                  `BuildRequest`; the orchestrator
//                                  reads it on every retry decision.
//                                  No `OnFailure` callbacks.
//   4. Stateless between calls — implementations may cache request
//                                lookups, but the contract assumes
//                                nothing survives between calls. An
//                                Orleans grain or Akka actor
//                                implementation can deactivate
//                                between any two calls and behave
//                                identically.
//   5. No cross-shard ordering — builds for different `AppSlug`s have
//                                no relative ordering. Builds for the
//                                *same* `AppSlug` are serialised by
//                                per-slug single-actor invariant (or
//                                per-slug `SemaphoreSlim` in the
//                                single-node default).
//   6. Precision at lower bound — submission timestamps round to
//                                  the orchestrator's tick precision.
//                                  The single-node default uses
//                                  `JobPrecision.Minute` (one-shot
//                                  jobs) inherited from
//                                  `IJobScheduler`.

/// Top-level orchestrator for deploy-plane builds. Companions register
/// their own implementation via `services.AddSingleton<IBuildOrchestrator>`;
/// the SDK ships `JobSchedulerBuildOrchestrator` as the single-node
/// default. Consumers (`IDeployPipeline`, admin UIs, the
/// operator's CLI) compose against this interface.
type IBuildOrchestrator =
    /// Enqueue a fresh build. Returns the assigned `BuildId` on
    /// success.
    ///
    /// **Validation chain** (in order):
    ///   1. `AppSlug` is non-empty.
    ///   2. `Source` carries the fields its case requires
    ///      (`GitHubRef.sha` non-empty, `PrebuiltImage.image`
    ///      non-empty, etc.).
    ///   3. `RetryPolicy.MaxAttempts >= 1`.
    /// First failure short-circuits with the appropriate
    /// `BuildOrchestratorError.InvalidRequest`.
    ///
    /// **Idempotency.** When `request.Idempotency` is `Some` and a
    /// live build with the same token exists, returns the existing
    /// `BuildId` without enqueueing a duplicate. The caller cannot
    /// distinguish "brand new" from "recovered" by return value
    /// alone — by design.
    abstract EnqueueBuild: request: BuildRequest -> Async<Result<BuildId, BuildOrchestratorError>>

    /// Read the latest summary for a build. Returns `UnknownBuild`
    /// for ids the orchestrator doesn't recognise.
    abstract GetBuild: buildId: BuildId -> Async<Result<BuildSummary, BuildOrchestratorError>>

    /// Enumerate every build that is not yet terminal (`Queued` or
    /// `Building`). `appSlug` filters to one application's queue;
    /// `None` returns every active build across the orchestrator.
    /// Ordering: `SubmittedAt` ascending (oldest first).
    abstract ListActiveBuilds: appSlug: string option -> Async<BuildSummary list>

    /// Count of pending + running builds across the orchestrator.
    /// Cheap (the orchestrator maintains a counter); polled by
    /// `IHealthCheck` probes + autoscaling decisions.
    abstract GetQueueDepth: unit -> Async<int>

    /// Cancel an in-flight build. Sets `Status = Cancelled` and
    /// stops further retry attempts. Returns
    /// `AlreadyTerminated` when the build already settled (the
    /// caller's mental model is stale — refresh and decide).
    /// Idempotent on the success path (cancelling an already-
    /// cancelled build returns `Ok`).
    abstract CancelBuild: buildId: BuildId * byUserId: string -> Async<Result<unit, BuildOrchestratorError>>

    /// Read the most recent `count` builds for `appSlug`, newest
    /// first. Powers the admin UI's per-app build history panel.
    /// Includes terminal states (`Succeeded` / `Failed` /
    /// `Cancelled`).
    abstract GetBuildHistory: appSlug: string * count: int -> Async<BuildSummary list>