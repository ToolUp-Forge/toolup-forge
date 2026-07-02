// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Reflection
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open PeerReflection

// ─── Layer 4 — job-substrate fusion ──────────────────────────────────
//
// Long-running contract methods (`… -> Async<PeerJobHandle<'T>>`) do not
// resolve inside the inbound HTTP request. Instead the host schedules a
// `_platform.peer.{contractId}.{methodName}` job on the background-job
// substrate (`IJobScheduler`) and returns the assigned `JobId` to the
// caller, who polls `GET /peer/v1/{contractId}/jobs/{jobId}` until the
// job reaches a terminal state. This file carries the three pieces that
// bridge the peer substrate to the job substrate:
//
//   • `IPeerJobResultStore` / `BlobPeerJobResultStore` — the job
//     substrate's `JobResult` is `Success | …Failure`, with no payload,
//     so a finished peer call parks its *typed* (serialised) result
//     here, keyed by `JobId` and stamped with the scheduling caller's
//     `PeerId`, for the polling *owner* to retrieve (Phase 308).
//   • `PeerJobHandler` — the `IJobHandler` the scheduler dispatches: it
//     unmarshals the call arguments from the job payload, applies the
//     contract implementation, resolves the returned `PeerJobHandle<'T>`
//     to a terminal state, and persists the serialised result.
//   • `PeerJobFusion` — the scheduler + result-store pair the host needs
//     to schedule jobs (dispatch side) and the handlers it registers.
//
// No new runtime — the existing job substrate is the engine.

/// The job payload the dispatch side schedules for a long-running
/// contract method: the validated scheduling caller's `PeerId` plus the
/// method's positional-args JSON exactly as it arrived on the invoke
/// leg. `JobContext` carries no scheduling-caller identity, so the owner
/// rides the payload from `scheduleDispatch` to `PeerJobHandler.Execute`,
/// which stamps it onto the parked result (Phase 308 — caller-ownership
/// scoping). Identity by value (GP 12 rule 1).
type PeerJobPayload = {
    OwnerPeerId: string
    ArgsJson: string
}

/// A parked long-running result together with the `PeerId` of the peer
/// that scheduled it. The poll route compares the recorded owner against
/// the polling principal and refuses a mismatch — isolation enforced
/// structurally at the poll seam, not by `jobId` Guid entropy (GP 4).
type PeerJobRecord = {
    OwnerPeerId: string
    Status: PeerJobStatus<string>
}

/// Persists a long-running peer call's terminal status, keyed by the
/// backing `JobId`. `IJobScheduler`'s `JobResult` carries no result
/// payload, so the typed (serialised) result rides here instead. Async
/// at every boundary + identity by value (GP 12 rules 1, 2); scoped per
/// `scopeId` for isolation (GP 4), mirroring `IJobStore`.
type IPeerJobResultStore =
    /// Record the terminal status (`Completed` json / `Failed` error) of
    /// a finished peer job, stamped with the scheduling caller's
    /// `PeerId`. Never called with `Pending` — the absence of a stored
    /// record *is* the pending signal.
    abstract SaveResult:
        scopeId: string * jobId: PeerJobId * ownerPeerId: string * status: PeerJobStatus<string> -> Async<unit>

    /// Read a peer job's owner-stamped terminal record. `None` means the
    /// job has not yet finished (the caller keeps polling); `Some` is
    /// terminal.
    abstract TryGetResult: scopeId: string * jobId: PeerJobId -> Async<PeerJobRecord option>

/// `IBlobStorage`-backed default. One JSON document per job under the
/// reserved `_platform` container at `peers/jobs/{scopeId}/{jobId}.json`,
/// matching the `BlobPeerRegistry` layout. Stateless between calls
/// (GP 12 rule 4) — every method reads / writes through to the blob
/// store.
type BlobPeerJobResultStore(blobs: IBlobStorage) =
    let container = "_platform"
    let blobNameFor (scopeId: string) (jobId: PeerJobId) = $"peers/jobs/{scopeId}/{jobId}.json"

    interface IPeerJobResultStore with
        member _.SaveResult(scopeId: string, jobId: PeerJobId, ownerPeerId: string, status: PeerJobStatus<string>) = async {
            let record = {
                OwnerPeerId = ownerPeerId
                Status = status
            }

            let payload = Encoding.UTF8.GetBytes(JsonRpc.serialize record)
            let! _ = blobs.Upload(container, blobNameFor scopeId jobId, payload)
            return ()
        }

        member _.TryGetResult(scopeId: string, jobId: PeerJobId) = async {
            let! result = blobs.Download(container, blobNameFor scopeId jobId)

            return
                match result with
                | Ok bytes ->
                    try
                        Some(JsonRpc.deserialize<PeerJobRecord> (Encoding.UTF8.GetString bytes))
                    with _ ->
                        None
                | Error _ -> None
        }

/// The scheduler + result-store pair the host fuses long-running calls
/// onto. Present only when `ServerConfig.PeerSubstrate =
/// EnabledPeerSubstrate` *and* the job substrate is enabled; absent (the
/// `option` is `None`) leaves long-running methods reporting a clear
/// "not enabled" error (GP 13 — zero cost when unused).
type PeerJobFusion = {
    Scheduler: IJobScheduler
    ResultStore: IPeerJobResultStore
}

/// Conventions shared by the dispatch side (which schedules the job) and
/// the execution side (the registered handler) so the two halves agree
/// on the job scope, the audit source module, and the handler name.
[<RequireQualifiedAccess>]
module PeerJob =
    /// The reserved platform scope long-running peer jobs run under —
    /// the same scope the SDK's own internal handlers use. Peer calls are
    /// not tenant-scoped in the foundation.
    [<Literal>]
    let Scope = "_platform"

    /// The audit `SourceModule` peer lifecycle events flow under.
    [<Literal>]
    let SourceModule = "_platform.peer"

    /// Logical job-handler name for a contract method:
    /// `_platform.peer.{contractId}.{methodName}`. Used symmetrically by
    /// the host — it registers the handler under this name and schedules
    /// jobs against it — so dispatch and execution agree.
    let handlerName (contractId: string) (methodName: string) : string =
        $"{SourceModule}.{contractId}.{methodName}"

/// Reflection shim: await the implementation's `Async<PeerJobHandle<'T>>`,
/// resolve the handle to a terminal state, and project it to a
/// serialisable `PeerJobStatus<string>`. Invoked via
/// `MakeGenericMethod('T)` from the handler, which knows `'T` only at
/// runtime.
type private PeerJobInvoker =

    static member ResolveSerialize<'T>(work: Async<PeerJobHandle<'T>>) : Async<PeerJobStatus<string>> = async {
        let! handle = work
        let! resolved = PeerJobHandle.resolve handle

        return
            match resolved with
            | Ok value -> PeerJobStatus.Completed(JsonRpc.serialize value)
            | Error err -> PeerJobStatus.Failed err
    }

/// The `IJobHandler` the scheduler dispatches for one long-running
/// contract method. Closes over the method's implementation function,
/// its argument types, and the `PeerJobHandle<'T>` inner type `'T`, all
/// captured at contract-registration time. Stateless between invocations
/// (GP 12 rule 4): every call's state arrives via `JobContext.Payload`.
/// The handler always finishes the job as `Success` (the job of
/// *capturing the terminal status* succeeded); a peer-side failure is
/// recorded as a `Failed` status, not a job retry — re-running a
/// deterministic peer computation would double-execute it.
type PeerJobHandler(funcValue: obj, argTypes: Type list, innerType: Type, resultStore: IPeerJobResultStore) =

    // NonPublic is load-bearing: `PeerJobInvoker` is a `private` type, so
    // F# emits its (F#-public) static members with non-public IL
    // visibility. Without NonPublic the lookup returns null and the
    // handler ctor NREs.
    static let resolveSerializeMethod =
        typeof<PeerJobInvoker>
            .GetMethod("ResolveSerialize", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)

    let resolveSerialize = resolveSerializeMethod.MakeGenericMethod(innerType)

    interface IJobHandler with
        member _.Execute(ctx: JobContext) = async {
            // The dispatch side schedules the owner-stamped `PeerJobPayload`
            // envelope. A payload that fails to parse as the envelope (a
            // job scheduled before ownership scoping landed) degrades to
            // owner-unknown: the whole payload is treated as the args and
            // the parked record is owned by no peer, so the poll route
            // fails closed rather than guessing an owner.
            let envelope =
                try
                    JsonRpc.deserialize<PeerJobPayload> ctx.Payload
                with _ -> {
                    OwnerPeerId = ""
                    ArgsJson = ctx.Payload
                }

            let! status = async {
                try
                    let args = unmarshalArgs envelope.ArgsJson argTypes
                    let boxedAsyncHandle = applyFunction funcValue args

                    let statusAsync =
                        resolveSerialize.Invoke(null, [| boxedAsyncHandle |]) :?> Async<PeerJobStatus<string>>

                    let! resolved = statusAsync
                    return resolved
                with
                | PeerInvocationException e -> return PeerJobStatus.Failed e
                | ex -> return PeerJobStatus.Failed(PeerHandler ex.Message)
            }

            do! resultStore.SaveResult(ctx.ScopeId, ctx.JobId, envelope.OwnerPeerId, status)
            return Success
        }

/// What `JsonRpcPeerHost.contract` produces: the receiver-facing
/// `PeerContractRegistration` (transport- and job-agnostic) plus the
/// `(handlerName, IJobHandler)` pairs the compose hook registers with
/// `IJobScheduler` for the contract's long-running methods. Immediate-
/// only contracts carry an empty `JobHandlers` list.
type PeerContractHost = {
    Registration: PeerContractRegistration
    JobHandlers: (string * IJobHandler) list
}