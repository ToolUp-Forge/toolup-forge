// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 318 — IExternalComputeDispatcher seam + no-op default ─────────
//
// The seam for handing a unit of work to compute that is **not this
// process**: a GPU box, a batch / training service, a worker pool. Phase 9b
// job handlers run in-process (`IJobHandler.Execute` is an F# `async`
// invoked on the server), so before this seam a consumer with an intensive
// workload had to reach out to an external service by hand from inside a
// handler, invent its own submit/poll/result protocol, and hold a scheduler
// slot for the duration.
//
// **The dispatcher brokers; it does not run.** `Submit` accepts and returns
// an `ExternalHandle` immediately; `Poll` maps a handle to a typed
// `ExternalOutcome`; `Cancel` requests teardown. No method blocks for the
// duration of the work, and no implementation of this interface is expected
// to execute the payload itself.
//
// **Relationship to `IJobScheduler`.** Orthogonal, and composable: the job
// scheduler owns *when* something runs on this deployment, the dispatcher
// owns *where* the heavy part runs. A handler submits, records the handle,
// and returns — a later poll (its own scheduled job) advances it. Making
// that hand-off non-blocking end-to-end is Phase 319's job; progress
// reporting is Phase 321's. This phase is the substrate both build on.
//
// **Six portability rules (GP 12) — audited (Phase 9c).**
// 1. *Identity by value* — `Submit` returns an `ExternalHandle` record of
//    `Guid` + strings + `DateTime`; `Poll` / `Cancel` take that same record.
//    No `IActorRef`, no `IGrainReference`, no backend SDK client, no live
//    job object crosses the surface. The backend's own token rides as the
//    opaque `NativeRef` string.
// 2. *Async at every boundary* — all three methods return `Async<_>`. There
//    is no sync method and no fire-and-forget `Tell`-shaped signature, so
//    no carve-out (`IMetricsSink`-style) is claimed or needed.
// 3. *Retry + supervision as data* — timeout and idempotency are fields on
//    `ExternalWorkSpec`; failure is an `ExternalComputeError` record whose
//    `Retriable` flag is the retry decision, returned as data. No
//    `OnFailure: exn -> unit` parameter, no supervision-strategy object,
//    and a backend-reported failure is a `Result`/`ExternalOutcome`, never
//    an exception across the boundary.
// 4. *Stateless between invocations* — a dispatcher receives the whole spec
//    per `Submit` and the whole handle per `Poll` / `Cancel`; it caches
//    nothing between calls, so a recycled worker or a re-activated grain
//    answers identically.
// 5. *No cross-shard ordering* — two handles are independent. Ordering is
//    promised only within one handle's own state progression
//    (`Pending`/`Running` → a terminal case), never across submissions.
// 6. *Precision at the lower bound* — `ExternalWorkSpec.Timeout` is
//    advisory with the backend's own scheduling granularity as its floor,
//    and `Running of progress: float option` lets a backend that cannot
//    report progress say `None` rather than fabricate a figure.
//
// **No framework types on the surface** — no `Microsoft.Extensions.*`, no
// `HttpContext`, no `CancellationToken`, no `Task`. The whole interface is
// expressible from `ToolUp.Platform` value types alone.

/// Brokers a unit of work to an external compute backend and reports its
/// outcome. Implemented by a companion under `src/ExternalCompute/` (GP 1 —
/// the backend SDK / transport never reaches `ToolUp.Platform.*`) and
/// composed as an `IExternalComputeDispatcher` DI singleton. Selected by
/// `ServerConfig.ExternalCompute`; the default `NoExternalCompute` registers
/// `NoExternalComputeDispatcher` (GP 13).
type IExternalComputeDispatcher =
    /// Stable backend identifier stamped onto every `ExternalHandle.Backend`
    /// this dispatcher mints (e.g. `"http-worker-pool"`). Diagnostics read
    /// it; a poll uses it to confirm the handle belongs to this backend.
    abstract Backend: string

    /// Hand `spec` to the backend under `scopeId` and return a handle
    /// **immediately** — this call MUST NOT wait for the work to finish.
    ///
    /// `Ok handle` means accepted, not completed: the caller persists the
    /// handle and polls. `Error` is a refusal to accept (unknown `Kind`,
    /// unhonourable resource hint, backend unreachable, no backend
    /// configured) with retriability carried as data on the error.
    ///
    /// When `spec.Idempotency` is `Some key` and this scope has already
    /// submitted that key, an implementation SHOULD return the existing
    /// handle rather than start the work a second time.
    abstract Submit: scopeId: string * spec: ExternalWorkSpec -> Async<Result<ExternalHandle, ExternalComputeError>>

    /// Read the current outcome of `handle`. Non-destructive and
    /// idempotent: polling a terminal handle returns the same terminal
    /// outcome. A handle the backend no longer recognises is reported as
    /// `ExternalOutcome.Failed` with a terminal error — never an exception,
    /// and never an invented `Cancelled`.
    abstract Poll: handle: ExternalHandle -> Async<ExternalOutcome>

    /// Request teardown of `handle`. Best-effort and **idempotent** —
    /// cancelling an already-terminal or already-cancelled handle is not an
    /// error, and completes without throwing. Returns when the request has
    /// been lodged, not when the backend has finished tearing down; the
    /// caller confirms via `Poll`.
    abstract Cancel: handle: ExternalHandle -> Async<unit>

/// The default dispatcher for a deployment that composes no external-compute
/// backend. Registered when `ServerConfig.ExternalCompute =
/// NoExternalCompute` (the default), so `IExternalComputeDispatcher` always
/// resolves and a handler's submit path is a typed refusal rather than a DI
/// resolution failure.
///
/// Costs nothing (GP 13): no background service, no connection, no vendor
/// dependency, no allocation beyond the returned `Async`. `Submit` returns
/// `Error ExternalComputeError.notConfigured` — terminal, because no retry
/// composes a backend. `Poll` reports the same refusal as
/// `ExternalOutcome.Failed` (a handle cannot exist without a submit having
/// succeeded, so this arm is reachable only for a handle minted elsewhere),
/// and `Cancel` is a no-op, honouring the idempotent-cancel contract.
type NoExternalComputeDispatcher() =
    /// The backend label a not-configured deployment reports.
    static member BackendName = "none"

    interface IExternalComputeDispatcher with
        member _.Backend = NoExternalComputeDispatcher.BackendName

        member _.Submit(_scopeId: string, _spec: ExternalWorkSpec) = async {
            return Error ExternalComputeError.notConfigured
        }

        member _.Poll(_handle: ExternalHandle) = async {
            return ExternalOutcome.Failed ExternalComputeError.notConfigured
        }

        member _.Cancel(_handle: ExternalHandle) = async { return () }