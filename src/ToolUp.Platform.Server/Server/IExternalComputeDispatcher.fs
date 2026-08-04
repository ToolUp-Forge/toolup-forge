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

// ─── Phase 478 — declaring, and enforcing, the isolation posture ─────────
//
// `ExecutionProfile` (Core) is the requirement a spec carries. This is the
// other half: how a backend DECLARES what it can honour, and where a
// mismatch is refused.
//
// **Why a second interface rather than a member on
// `IExternalComputeDispatcher`.** F# cannot author a default interface
// member, so adding `abstract IsolationPosture` to the dispatcher seam
// would break every implementation that exists — including any a consumer
// has already shipped — for a property the overwhelming majority answer
// `standardOnly` to. A separate interface a backend *also* implements
// keeps the seam additive (GP 11) and keeps the declaration where it
// belongs: on the backends that actually make the guarantee.
//
// It also gives the right default for free. An implementation that does
// not implement `IIsolatedComputeBackend` is read as
// `IsolationPosture.standardOnly` — claiming nothing — so forgetting to
// declare is never mistaken for declaring.
//
// **Where the refusal sits, and why it is BEFORE `Submit`.** `Isolated`
// exists so gated data is computed over by a worker that cannot leak it.
// A check performed after the backend accepted the work is a check on
// something that has already left: the payload is on the backend, and no
// subsequent refusal recalls it. `ExecutionProfileGate.enforce` is
// therefore a decorator whose `Submit` decides on the SPEC and never
// touches the inner dispatcher when the answer is no.

/// Phase 478 — a dispatcher whose backend declares what it guarantees
/// about the environment `Isolated` work runs in.
///
/// A companion implements it **alongside** `IExternalComputeDispatcher`;
/// one that does not is read as `IsolationPosture.standardOnly` and is
/// refused `Isolated` submissions.
type IIsolatedComputeBackend =
    /// The posture this backend enforces. Constant for the life of the
    /// composed dispatcher — it describes how the backend was configured,
    /// not the state of any one job (GP 12 rule 4).
    abstract IsolationPosture: IsolationPosture

/// Phase 478 — the profile↔posture match, and the decorator that enforces
/// it before a submission reaches a backend.
[<RequireQualifiedAccess>]
module ExecutionProfileGate =

    /// The posture `dispatcher` declares. An implementation that does not
    /// implement `IIsolatedComputeBackend` declares nothing, which reads
    /// as `standardOnly` — see the header for why that direction and not
    /// the other.
    let postureOf (dispatcher: IExternalComputeDispatcher) : IsolationPosture =
        match box dispatcher with
        | :? IIsolatedComputeBackend as declared -> declared.IsolationPosture
        | _ -> IsolationPosture.standardOnly

    /// May `dispatcher` be handed `spec`?
    ///
    /// `Ok ()` for every `Standard` spec, whatever the backend — that arm
    /// is Phase 318 exactly. `Error` names the missing clauses, and is
    /// terminal: a backend does not become isolating by being asked twice.
    let check (dispatcher: IExternalComputeDispatcher) (spec: ExternalWorkSpec) : Result<unit, ExternalComputeError> =
        let posture = postureOf dispatcher

        if IsolationPosture.honours spec.Profile posture then
            Ok()
        else
            Error(IsolationPosture.refusal dispatcher.Backend posture)

    /// Wrap `inner` so an `Isolated` spec it cannot honour is refused
    /// **before** it is submitted.
    ///
    /// `Poll` and `Cancel` pass straight through: they operate on a handle
    /// the backend already minted, so there is nothing left to refuse, and
    /// intercepting them would only add a branch to the path a `Standard`
    /// deployment takes (GP 13). `Backend` is the inner backend's own
    /// label — the decorator is not a backend and must not present as one,
    /// or a handle would poll against a name no dispatcher answers to.
    ///
    /// The decorator re-declares the inner posture through
    /// `IIsolatedComputeBackend`, so stacking it (or resolving it through
    /// another decorator) does not silently downgrade a genuinely
    /// isolating backend to `standardOnly`. A wrapper that swallowed the
    /// declaration would turn composing this gate into a reason `Isolated`
    /// stops working, which is the shape of "security control nobody
    /// leaves switched on".
    let enforce (inner: IExternalComputeDispatcher) : IExternalComputeDispatcher =
        let posture = postureOf inner

        { new IExternalComputeDispatcher with
            member _.Backend = inner.Backend

            member _.Submit(scopeId: string, spec: ExternalWorkSpec) = async {
                if IsolationPosture.honours spec.Profile posture then
                    return! inner.Submit(scopeId, spec)
                else
                    // The inner dispatcher is not consulted at all. The
                    // payload never leaves this process.
                    return Error(IsolationPosture.refusal inner.Backend posture)
            }

            member _.Poll(handle: ExternalHandle) = inner.Poll handle

            member _.Cancel(handle: ExternalHandle) = inner.Cancel handle

          interface IIsolatedComputeBackend with
              member _.IsolationPosture = posture
        }

// ─── Phase 320 — declaring that a backend can call back ──────────────
//
// The push path needs one thing the poll path does not: the backend has
// to be TOLD the per-handle secret, or it cannot authenticate its own
// callback. This is the seam that tells it.
//
// **A second interface a backend also implements, not a member on
// `IExternalComputeDispatcher`.** Identical reasoning to
// `IIsolatedComputeBackend` above — F# cannot author a default
// interface member, so a new abstract member on the dispatcher seam
// would break every implementation that exists, including shipped
// consumer ones, for a capability most backends do not have. A backend
// that does not implement this interface is simply never handed a
// credential and is reconciled by polling, which is the behaviour it
// already had (GP 11).
//
// **Why the credential arrives AFTER acceptance rather than as a
// `Submit` parameter.** The credential is keyed by
// `ExternalHandle.HandleId`, and the handle does not exist until
// `Submit` has returned it — so a credential passed INTO `Submit` could
// not name the handle it authenticates, and the platform could not have
// stored its hash against a record it cannot yet key. Threading a
// pre-minted handle id through `Submit` would mean changing the
// dispatcher seam (breaking) and taking handle minting away from the
// backend that owns it.
//
// The honest cost of that ordering is stated rather than hidden: a
// backend fast enough to finish before this call lands cannot call
// back, because it does not yet hold the secret. That run resolves by
// poll on the next tick — the fallback that was always there. The
// window is one blob write wide and only affects work that completed
// faster than the platform could record having submitted it, which is
// not the workload shape this substrate exists for (a GPU training run,
// a batch render). Correctness never depends on the credential
// arriving: nothing is lost, only latency.
type IExternalCallbackCapableBackend =
    /// Accept the completion-callback credential for `handle`. Called
    /// **once** per accepted hand-off, immediately after the platform
    /// has durably registered the handle, and only when the deployment
    /// composed a handle store.
    ///
    /// The implementation's job is to make the credential reachable by
    /// whatever will POST the callback — stamp it onto the backend's own
    /// job record, patch the queued work item's webhook config, hand it
    /// to the worker. It MUST NOT log the secret.
    ///
    /// **Best-effort, and the platform treats it so.** A throw or a
    /// failure here is logged and swallowed: the work is already
    /// accepted and running, the run is already durably
    /// `AwaitingExternal`, and the poll loop resolves it regardless. A
    /// failure to deliver a callback credential is a latency
    /// regression, never a lost job — so it must not be allowed to
    /// become one by failing the hand-off after the payload has left.
    abstract AcceptCallbackCredential: handle: ExternalHandle * credential: ExternalCallbackCredential -> Async<unit>