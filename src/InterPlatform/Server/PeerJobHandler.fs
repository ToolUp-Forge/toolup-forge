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
//     `PeerId`, for the polling *owner* to retrieve (Phase 308), under
//     a `PeerJobRetentionPolicy` that bounds how long it stays there
//     (Phase 316).
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
    /// Phase 310 — the cascade-wide correlation id the receiver *derived*
    /// for the scheduling request (Phase 331), carried across the job
    /// boundary for the same reason the owner is: `JobContext` has no
    /// request, no principal and no ambient context, so anything the
    /// execution side must attribute has to arrive as data (GP 12 rule 4).
    /// Without it the terminal audit row would either mint a fresh id — and
    /// correlate to nothing — or re-derive one from a request that ended
    /// minutes earlier.
    ///
    /// A job scheduled before this phase carries no such field; it
    /// deserialises to `null` and is normalised to `""` at the read site,
    /// which records an uncorrelated terminal row rather than losing it.
    RootRequestId: string
}

/// A parked long-running result together with the `PeerId` of the peer
/// that scheduled it. The poll route compares the recorded owner against
/// the polling principal and refuses a mismatch — isolation enforced
/// structurally at the poll seam, not by `jobId` Guid entropy (GP 4).
type PeerJobRecord = {
    OwnerPeerId: string
    Status: PeerJobStatus<string>
}

/// How long a parked peer job result is retained (Phase 316). Expressed
/// as a value so a distributed `IPeerJobResultStore` honours the same
/// contract rather than re-deriving its own — policy as data, never a
/// callback (GP 12 rule 3).
///
/// Retention here is not only a storage-growth question. These documents
/// hold the *typed* federated results of a long-running peer call, so an
/// unbounded store is a data-retention exposure for a clean-room or
/// privacy-sensitive federation (GP 4), and it compounds the Phase 308
/// ownership posture: without a bound, a stale result stays readable by
/// its owner forever.
type PeerJobRetentionPolicy = {
    /// Time from the terminal write to expiry. An expired record reads as
    /// absent and its blob is reclaimed on that read. `None` keeps the
    /// record until an external lifecycle removes it — the behaviour
    /// before retention existed.
    Ttl: TimeSpan option
    /// Reclaim a record once it has been read, rather than waiting out
    /// `Ttl`. The first read stamps a deadline `GraceWindow` ahead of
    /// itself; reads inside that window still resolve, so a retried or
    /// duplicated poll is not punished for the first one having landed.
    DeleteOnRead: bool
    /// How long a delete-on-read record stays readable after its first
    /// read. Ignored when `DeleteOnRead` is `false`. `TimeSpan.Zero`
    /// means the record is absent from the next read onwards.
    GraceWindow: TimeSpan
}

[<RequireQualifiedAccess>]
module PeerJobRetentionPolicy =
    /// No TTL, no delete-on-read: byte-for-byte the behaviour before
    /// retention existed. The escape hatch for a deployment whose own
    /// lifecycle already owns these blobs.
    let keepForever: PeerJobRetentionPolicy = {
        Ttl = None
        DeleteOnRead = false
        GraceWindow = TimeSpan.Zero
    }

    /// What a deployment gets without configuring anything: a
    /// deliberately generous 30-day TTL, no delete-on-read (GP 11). A
    /// poll cycle is a minutes-scale conversation between two peers, so
    /// 30 days is several orders of magnitude of headroom — a deployment
    /// that never thinks about retention keeps every result far longer
    /// than any caller could still be polling for it, and stops
    /// accumulating them forever.
    let default': PeerJobRetentionPolicy = {
        keepForever with
            Ttl = Some(TimeSpan.FromDays 30.0)
            GraceWindow = TimeSpan.FromMinutes 5.0
    }

    /// Bound the record's lifetime to `ttl` from the terminal write.
    let withTtl (ttl: TimeSpan) (policy: PeerJobRetentionPolicy) : PeerJobRetentionPolicy = {
        policy with
            Ttl = Some ttl
    }

    /// Drop the TTL, keeping whatever delete-on-read behaviour the policy
    /// already carries.
    let withoutTtl (policy: PeerJobRetentionPolicy) : PeerJobRetentionPolicy = { policy with Ttl = None }

    /// Reclaim a record `grace` after its first read. `Ttl` stays as the
    /// backstop for a result nobody ever polls — delete-on-read is lazy
    /// by construction (a stateless store has no timer of its own), so a
    /// never-read record is reclaimed by the TTL, not by this.
    let withDeleteOnRead (grace: TimeSpan) (policy: PeerJobRetentionPolicy) : PeerJobRetentionPolicy = {
        policy with
            DeleteOnRead = true
            GraceWindow = grace
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
    ///
    /// A record retired by `Retention` — expired past its TTL, or swept
    /// after a delete-on-read grace window — reads as `None` too, so a
    /// caller cannot distinguish *expired* from *never existed*. That
    /// indistinguishability is deliberate and matches the Phase 308
    /// non-disclosure posture, where an unknown `jobId` is answered
    /// `Pending` precisely so possession of an id discloses nothing. The
    /// cost is that a caller who polls too late sees `Pending` forever
    /// rather than a "gone" signal; the TTL is generous enough
    /// (`PeerJobRetentionPolicy.default'`) that reaching it means the
    /// caller had already abandoned the call.
    abstract TryGetResult: scopeId: string * jobId: PeerJobId -> Async<PeerJobRecord option>

    /// The retention policy this store applies (Phase 316). Data, not
    /// behaviour: a distributed implementation reports the policy it
    /// honours, and an admin / diagnostic surface can show a deployment
    /// what its federated results' lifetime actually is without knowing
    /// which store is registered.
    abstract Retention: PeerJobRetentionPolicy

/// The document `BlobPeerJobResultStore` writes per job: the fields a
/// poll returns (`PeerJobRecord`) plus the retention stamps that decide
/// when it stops being returned.
///
/// Deliberately a separate type from `PeerJobRecord` rather than two more
/// fields on it: the record is the value every caller — and every
/// alternative `IPeerJobResultStore` — constructs, so widening it would
/// be a compile break at every construction site (GP 11). The stamps are
/// this store's own bookkeeping, not part of what a poll answers.
///
/// A document written before retention existed carries neither stamp;
/// both absent fields deserialise to `None`, so a pre-316 blob is kept
/// forever and no deployment loses a result to the upgrade.
type PeerJobDocument = {
    OwnerPeerId: string
    Status: PeerJobStatus<string>
    /// Absolute expiry derived from `PeerJobRetentionPolicy.Ttl` at the
    /// terminal write. `None` = no TTL applied.
    ExpiresAt: DateTimeOffset option
    /// Absolute expiry stamped by the first read under a delete-on-read
    /// policy. `None` = never read (or delete-on-read is off).
    ReadExpiresAt: DateTimeOffset option
}

/// `IBlobStorage`-backed default. One JSON document per job under the
/// reserved `_platform` container at `peers/jobs/{scopeId}/{jobId}.json`,
/// matching the `BlobPeerRegistry` layout. Stateless between calls
/// (GP 12 rule 4) — every method reads / writes through to the blob
/// store, including the retention stamps, so nothing is held in memory
/// across a poll and a second instance behaves identically.
///
/// Retention is enforced **lazily, on read**: an expired document is
/// reported absent and its blob deleted in the same call. That keeps the
/// store free of a background sweeper (no hosted service, no scheduler
/// dependency — GP 13) at the cost of a never-polled result occupying
/// storage until something reads it. A deployment that wants eager
/// reclamation fuses its own sweep onto `IJobScheduler` over the
/// `peers/jobs/` prefix; the stamps it needs are in the document.
///
/// The policy and the clock arrive as **overloads**, not optional
/// arguments: F# compiles `?retention` / `?now` into one widened
/// constructor, which erases the pre-316 `(IBlobStorage)` signature from
/// the emitted surface — source-compatible, but a binary break the
/// public-API baseline correctly reports as a removal. Explicit
/// secondary constructors keep the original signature intact, so this
/// phase's surface diff is purely additive (GP 11).
type BlobPeerJobResultStore(blobs: IBlobStorage, retention: PeerJobRetentionPolicy, now: unit -> DateTimeOffset) =
    let container = "_platform"
    let policy = retention
    let clock = now
    let blobNameFor (scopeId: string) (jobId: PeerJobId) = $"peers/jobs/{scopeId}/{jobId}.json"

    /// The instant this document stops being readable — the earlier of
    /// the TTL stamp and the delete-on-read stamp, since either alone is
    /// sufficient to retire it.
    let deadlineOf (doc: PeerJobDocument) =
        match doc.ExpiresAt, doc.ReadExpiresAt with
        | Some ttl, Some read -> Some(min ttl read)
        | Some ttl, None -> Some ttl
        | None, Some read -> Some read
        | None, None -> None

    let writeDocument (scopeId: string) (jobId: PeerJobId) (doc: PeerJobDocument) = async {
        let payload = Encoding.UTF8.GetBytes(JsonRpc.serialize doc)
        let! _ = blobs.Upload(container, blobNameFor scopeId jobId, payload)
        return ()
    }

    /// The pre-316 call shape, unchanged: the generous default policy on
    /// the system clock.
    new(blobs: IBlobStorage) =
        BlobPeerJobResultStore(blobs, PeerJobRetentionPolicy.default', (fun () -> DateTimeOffset.UtcNow))

    /// A deployment-chosen retention policy on the system clock. The
    /// three-argument form takes the clock too, for tests that need to
    /// cross a TTL horizon without waiting one out.
    new(blobs: IBlobStorage, retention: PeerJobRetentionPolicy) =
        BlobPeerJobResultStore(blobs, retention, (fun () -> DateTimeOffset.UtcNow))

    interface IPeerJobResultStore with
        member _.Retention = policy

        member _.SaveResult(scopeId: string, jobId: PeerJobId, ownerPeerId: string, status: PeerJobStatus<string>) = async {
            let writtenAt = clock ()

            let doc = {
                OwnerPeerId = ownerPeerId
                Status = status
                ExpiresAt = policy.Ttl |> Option.map (fun ttl -> writtenAt.Add ttl)
                ReadExpiresAt = None
            }

            do! writeDocument scopeId jobId doc
        }

        member _.TryGetResult(scopeId: string, jobId: PeerJobId) = async {
            let! result = blobs.Download(container, blobNameFor scopeId jobId)

            let decoded =
                match result with
                | Ok bytes ->
                    try
                        Some(JsonRpc.deserialize<PeerJobDocument> (Encoding.UTF8.GetString bytes))
                    with _ ->
                        None
                | Error _ -> None

            match decoded with
            | None -> return None
            | Some doc ->
                let readAt = clock ()

                match deadlineOf doc with
                | Some deadline when readAt >= deadline ->
                    // Retired. Reclaim the blob on the way past — this is
                    // the whole sweep, and it runs on the read that would
                    // otherwise have served a stale federated result.
                    let! _ = blobs.Delete(container, blobNameFor scopeId jobId)
                    return None
                | _ ->
                    // A delete-on-read record starts its grace clock on
                    // the FIRST read only; a later read must not slide the
                    // window forward, or a polling loop would keep the
                    // result alive indefinitely.
                    if policy.DeleteOnRead && Option.isNone doc.ReadExpiresAt then
                        do!
                            writeDocument scopeId jobId {
                                doc with
                                    ReadExpiresAt = Some(readAt.Add policy.GraceWindow)
                            }

                    return
                        Some {
                            OwnerPeerId = doc.OwnerPeerId
                            Status = doc.Status
                        }
        }

/// The scheduler + result-store pair the host fuses long-running calls
/// onto. Present only when `ServerConfig.PeerSubstrate =
/// EnabledPeerSubstrate` *and* the job substrate is enabled; absent (the
/// `option` is `None`) leaves long-running methods reporting a clear
/// "not enabled" error (GP 13 — zero cost when unused).
type PeerJobFusion = {
    Scheduler: IJobScheduler
    ResultStore: IPeerJobResultStore
    /// Phase 310 — the audit log the execution side records a long-running
    /// call's terminal outcome on. `None` in a partial host with no
    /// `IAuditLog` registered, which records nothing — matching the
    /// immediate path, where `auditPeerCall` resolves the log per-request
    /// and no-ops when it is absent.
    ///
    /// It rides here because this record is the ONLY thing the compose hook
    /// hands a contract builder, and the builder is what constructs the job
    /// handlers. The execution side has no other route to it: a job handler
    /// is built once at contract-registration time and `JobContext` carries
    /// no service provider, so a per-invocation DI resolve is not available
    /// the way it is on the request path. Consumers receive this record from
    /// `withContract` and pass it through — they do not construct it.
    AuditLog: IAuditLog option
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
///
/// **Phase 310 — the terminal audit row.** The receiver's audit trail used
/// to stop at dispatch for a long-running call: the host emits
/// `PeerCallCompleted` when `peer.Handle` returns the assigned `JobId`, so
/// every such call was logged `ok` whatever the background computation went
/// on to do, and the Phase 18a transparency contract reported that back to
/// the caller as the truth. This handler now records the real outcome as
/// `PeerJobCompleted`, correlated to the schedule-time row by the
/// `RootRequestId` carried on the payload.
///
/// The identity, contract id and method name are all constructor-captured
/// or payload-carried rather than resolved here, because the execution side
/// has neither a request nor a principal — which is the same reason the
/// audit log itself is captured at construction (see `PeerJobFusion`).
type PeerJobHandler
    (
        funcValue: obj,
        argTypes: Type list,
        innerType: Type,
        resultStore: IPeerJobResultStore,
        auditLog: IAuditLog option,
        contractId: string,
        methodName: string
    ) =

    // NonPublic is load-bearing: `PeerJobInvoker` is a `private` type, so
    // F# emits its (F#-public) static members with non-public IL
    // visibility. Without NonPublic the lookup returns null and the
    // handler ctor NREs.
    static let resolveSerializeMethod =
        typeof<PeerJobInvoker>
            .GetMethod("ResolveSerialize", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)

    let resolveSerialize = resolveSerializeMethod.MakeGenericMethod(innerType)

    /// Record the terminal outcome, best-effort. `IAuditLog.Record` is
    /// already documented as swallowing its own write failures, but the
    /// resolution and the projection in front of it are not, and a handler
    /// that threw here would fail a job whose result is already durably
    /// parked — a flaky audit store must never change the job's outcome.
    ///
    /// Runs AFTER `SaveResult` deliberately: the parked result is what the
    /// caller is polling for, so it is the primary write, and the audit row
    /// is the observation of it. There is no post-response hazard on this
    /// path — a job handler runs on the scheduler's own async, with no HTTP
    /// response started and no per-request resource to read after disposal
    /// — but the ordering keeps the primary write first regardless.
    let recordTerminalOutcome (envelope: PeerJobPayload) (jobId: PeerJobId) (status: PeerJobStatus<string>) = async {
        match auditLog with
        | None -> ()
        | Some log ->
            try
                let succeeded =
                    match status with
                    | PeerJobStatus.Completed _ -> true
                    | _ -> false

                let payload: PeerJobCompletedPayload = {
                    ContractId = contractId
                    MethodName = methodName
                    CallerPeerId = envelope.OwnerPeerId
                    RootRequestId = envelope.RootRequestId
                    JobId = jobId
                    Succeeded = succeeded
                    Outcome =
                        match status with
                        | PeerJobStatus.Completed _ -> "ok"
                        | PeerJobStatus.Failed e -> JsonRpc.errorCaseName e
                        // Unreachable: `ResolveSerialize` resolves the
                        // handle before returning, so the handler never
                        // sees `Pending`. Labelled rather than ignored so
                        // a future non-terminal status is visible in the
                        // trail instead of silently reading as success.
                        | PeerJobStatus.Pending -> "pending"
                    OccurredAt = DateTimeOffset.UtcNow
                }

                do! log.Record(PeerJob.Scope, PeerJobCompleted payload)
            with _ ->
                ()
    }

    /// The pre-310 call shape, unchanged: no audit emission and no contract
    /// / method attribution. Kept as an explicit secondary constructor
    /// (rather than widening the original with optional arguments, which F#
    /// compiles into one replacement signature) so this phase's public
    /// surface diff is purely additive — the same reasoning
    /// `BlobPeerJobResultStore` records for its Phase 316 overloads.
    new(funcValue: obj, argTypes: Type list, innerType: Type, resultStore: IPeerJobResultStore) =
        PeerJobHandler(funcValue, argTypes, innerType, resultStore, None, "", "")

    interface IJobHandler with
        member _.Execute(ctx: JobContext) = async {
            // The dispatch side schedules the owner-stamped `PeerJobPayload`
            // envelope. A payload that fails to parse as the envelope (a
            // job scheduled before ownership scoping landed) degrades to
            // owner-unknown: the whole payload is treated as the args and
            // the parked record is owned by no peer, so the poll route
            // fails closed rather than guessing an owner.
            let parsed =
                try
                    JsonRpc.deserialize<PeerJobPayload> ctx.Payload
                with _ -> {
                    OwnerPeerId = ""
                    ArgsJson = ctx.Payload
                    RootRequestId = ""
                }

            // A job scheduled before Phase 310 parses cleanly and leaves
            // `RootRequestId` null — a missing field is absence, not a
            // parse failure — so normalise rather than trusting the shape.
            let envelope = {
                parsed with
                    RootRequestId =
                        if isNull (box parsed.RootRequestId) then
                            ""
                        else
                            parsed.RootRequestId
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
            do! recordTerminalOutcome envelope ctx.JobId status
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

// ─── Phase 630 — the gateway's group job-handle map ──────────────────
//
// Phase 595's `PeerGateway` presents a group of deployments as one peer,
// but only for **immediate** methods: a member's long-running result
// parks in that member's own `IPeerJobResultStore`, and the group's
// `GET /peer/v1/{contractId}/jobs/{jobId}` route cannot read it. The
// missing piece is **handle translation** — the gateway mints a handle of
// its own, remembers what it stands for, and resolves a poll against it
// by forwarding to the owning member.
//
// Three properties the binding has to hold, and each one is a constraint
// on the shape rather than on the caller:
//
//   * **Content-free.** The handle an external caller receives is a fresh
//     v4 `Guid` and nothing else. It is deliberately NOT the member's own
//     job id, and it encodes no member identity, no route, and no
//     ordinal — otherwise the id itself becomes a topology oracle,
//     letting a counterparty partition a group's traffic by member (or
//     correlate a group handle with a job it can see on a member it also
//     talks to directly). Everything that identifies the member lives
//     HERE, receiver-side, and never crosses the wire.
//   * **Durable.** The gateway may restart while a member job runs, and a
//     handle the gateway can no longer resolve is a call the caller can
//     never collect. The shipped default is blob-backed for the same
//     reason `BlobRoundStateStore` is: the in-memory alternative loses
//     exactly the state the poll leg exists to serve.
//   * **Lifetime-bounded.** A binding retained longer than the member
//     record it points at is a routing tuple nothing stands behind. The
//     bound is expressed in the *same* `PeerJobRetentionPolicy`
//     vocabulary the member's own store honours (Phase 316) and defaults
//     to the same value, so a binding written under the default can never
//     outlive a member record written under the default.
//
// **Two levels of ownership, and both are load-bearing.** The member's
// parked record is owned by *the gateway* — the gateway is the peer that
// authenticated to the member and scheduled the work, so it is the only
// peer the member's own Phase 308 check will serve. The group binding is
// owned by *the external caller* — the peer that dispatched through the
// group face. Neither check substitutes for the other: without the
// member-side one any peer could poll the member directly, and without
// this one any peer holding a group handle could collect another
// caller's federated result.

/// What one group-minted job handle stands for: the member the gateway
/// routed the dispatch to, that member's own job id, and the external
/// caller entitled to collect it. Receiver-side only — a caller sees the
/// handle, never this. Identity by value (GP 12 rule 1).
type PeerGroupJobBinding = {
    /// `PeerId` of the peer that dispatched the long-running call
    /// *through the group*, taken from the validated inbound
    /// `PeerCallContext` at dispatch time. The poll route compares it
    /// against the polling principal and refuses a mismatch — the group
    /// edge's dual of the Phase 308 check the member applies to its own
    /// parked record.
    OwnerPeerId: string
    /// The member the gateway delegated to, exactly as the resolved route
    /// named it — the gateway polls this peer, at this base URL.
    MemberPeer: TargetPeer
    /// The contract the call was made against. The member's poll route
    /// carries it in the path, so the binding has to remember it; the
    /// group's own route carries the same id, but a binding that trusted
    /// the *inbound* path would resolve a handle against whichever
    /// contract the caller chose to name.
    ContractId: string
    /// The contract method whose job this is. Not needed to route the
    /// poll — it is what makes the group-edge terminal audit row
    /// attributable to a method rather than only to a contract.
    MethodName: string
    /// The member's own `PeerJobId`, as the member returned it from the
    /// invoke leg. Never disclosed to the caller.
    MemberJobId: PeerJobId
    /// The cascade-wide correlation id the gateway *derived* for the
    /// dispatching request (Phase 331), carried so the group-edge
    /// `PeerJobCompleted` row joins the schedule-time `PeerCallCompleted`
    /// row the gateway already wrote for the same call (Phase 310).
    RootRequestId: string
}

/// The gateway's durable map from a group-minted handle to the member job
/// it fronts. Async at every boundary + identity by value (GP 12 rules 1,
/// 2); scoped per `scopeId` for isolation (GP 4), mirroring
/// `IPeerJobResultStore`.
type IPeerGroupJobMap =
    /// Record what a freshly-minted group handle stands for. Called once,
    /// on the dispatch that minted it, under a key no other writer can
    /// hold — so this is a write-once insert and never a read-modify-write.
    abstract Bind: scopeId: string * groupJobId: PeerJobId * binding: PeerGroupJobBinding -> Async<unit>

    /// Resolve a group handle. `None` for a handle this gateway never
    /// minted **and** for one whose binding has been retired — the same
    /// indistinguishability `IPeerJobResultStore.TryGetResult` keeps, and
    /// for the same Phase 308 reason: possession of an id must disclose
    /// nothing.
    abstract TryGet: scopeId: string * groupJobId: PeerJobId -> Async<PeerGroupJobBinding option>

    /// Claim the right to emit the group-edge terminal audit row for this
    /// handle. `true` exactly once per binding under sequential polling;
    /// `false` on every later poll, so a caller that keeps polling after
    /// completion does not multiply the trail.
    ///
    /// Best-effort de-duplication, deliberately: two *concurrent* polls
    /// can both observe an unmarked binding and both emit. That is the
    /// right trade here — audit sinks are already required to be
    /// batch-idempotent (`IAuditSink`), a duplicated row is recoverable
    /// where a missing one is not, and the alternative (a compare-and-swap
    /// against `IConditionalBlobStorage`) would make a durable-map backend
    /// a hard requirement for a property that is only cosmetic.
    abstract MarkTerminalObserved: scopeId: string * groupJobId: PeerJobId -> Async<bool>

    /// The retention policy this map applies. Data, not behaviour — the
    /// same contract `IPeerJobResultStore.Retention` declares, so an admin
    /// surface can report a group handle's lifetime beside the member
    /// record's without knowing which implementation is registered.
    abstract Retention: PeerJobRetentionPolicy

/// The document `BlobPeerGroupJobMap` writes per handle: the binding a
/// poll resolves, plus this store's own bookkeeping.
///
/// Separate from `PeerGroupJobBinding` for the reason `PeerJobDocument` is
/// separate from `PeerJobRecord`: the binding is the value every caller —
/// and every alternative `IPeerGroupJobMap` — constructs, so widening it
/// would be a compile break at every construction site (GP 11).
type PeerGroupJobDocument = {
    Binding: PeerGroupJobBinding
    /// Absolute expiry derived from `PeerJobRetentionPolicy.Ttl` at bind
    /// time. `None` = no TTL applied.
    ExpiresAt: DateTimeOffset option
    /// When the group-edge terminal audit row was first claimed. `None` =
    /// not yet observed terminal.
    TerminalObservedAt: DateTimeOffset option
}

/// `IBlobStorage`-backed default. One JSON document per group handle under
/// the reserved `_platform` container at
/// `peers/groups/jobs/{scopeId}/{groupJobId}.json`, beside the member-side
/// `peers/jobs/` layout. Stateless between calls (GP 12 rule 4) — every
/// method reads / writes through to the blob store.
///
/// **Plain `IBlobStorage`, not `IConditionalBlobStorage`, and that is a
/// judgement rather than an omission.** `BlobPeerReplayGuard` and
/// `BlobPrivacyBudgetLedger` refuse a non-conditional backend at
/// construction because in both the atomic compare-and-swap *is* the
/// defence — a lost update there is a spent replay or a double epsilon
/// spend. Nothing here has that shape: `Bind` is a write-once insert under
/// a freshly-minted `Guid` that no concurrent writer can name, and the
/// only read-modify-write (`MarkTerminalObserved`) guards an audit-row
/// duplicate, which the sink contract already absorbs. Requiring
/// conditional writes would cost every deployment a backend constraint to
/// buy a property none of them needs.
///
/// Retention is enforced **lazily, on read**, exactly as
/// `BlobPeerJobResultStore` does it: an expired document is reported
/// absent and its blob deleted in the same call, so the map needs no
/// background sweeper (GP 13).
///
/// **`DeleteOnRead` is deliberately not honoured for a binding**, though
/// `Ttl` is. Delete-on-read exists to retire a *result* shortly after the
/// caller has collected it; a binding carries no result, only a routing
/// tuple, and it is read once per poll by construction — reclaiming it on
/// the first read would break the very poll loop it exists to serve, and
/// would do so precisely while the member job is still `Pending`. The
/// member's own store still applies whatever delete-on-read policy the
/// member composed to the thing that policy is about.
///
/// The policy and the clock arrive as **overloads**, not optional
/// arguments — F# compiles `?retention` / `?now` into one widened
/// constructor, which would erase the simple `(IBlobStorage)` signature
/// from the emitted surface (the same reasoning `BlobPeerJobResultStore`
/// records).
type BlobPeerGroupJobMap(blobs: IBlobStorage, retention: PeerJobRetentionPolicy, now: unit -> DateTimeOffset) =
    let container = "_platform"
    let policy = retention
    let clock = now

    let blobNameFor (scopeId: string) (groupJobId: PeerJobId) =
        $"peers/groups/jobs/{scopeId}/{groupJobId}.json"

    let writeDocument (scopeId: string) (groupJobId: PeerJobId) (doc: PeerGroupJobDocument) = async {
        let payload = Encoding.UTF8.GetBytes(JsonRpc.serialize doc)
        let! _ = blobs.Upload(container, blobNameFor scopeId groupJobId, payload)
        return ()
    }

    let readDocument (scopeId: string) (groupJobId: PeerJobId) = async {
        let! result = blobs.Download(container, blobNameFor scopeId groupJobId)

        match result with
        | Ok bytes ->
            try
                return Some(JsonRpc.deserialize<PeerGroupJobDocument> (Encoding.UTF8.GetString bytes))
            with _ ->
                return None
        | Error _ -> return None
    }

    /// Has this document stopped being resolvable? Only the TTL retires a
    /// binding — see the `DeleteOnRead` note in the type doc.
    let isExpired (readAt: DateTimeOffset) (doc: PeerGroupJobDocument) =
        match doc.ExpiresAt with
        | Some deadline -> readAt >= deadline
        | None -> false

    /// The default retention on the system clock — the same generous
    /// 30-day bound `BlobPeerJobResultStore(blobs)` selects, so a binding
    /// and the member record it points at expire together when neither is
    /// configured.
    new(blobs: IBlobStorage) =
        BlobPeerGroupJobMap(blobs, PeerJobRetentionPolicy.default', (fun () -> DateTimeOffset.UtcNow))

    /// A deployment-chosen retention on the system clock. The
    /// three-argument form takes the clock too, for tests that need to
    /// cross a TTL horizon without waiting one out.
    new(blobs: IBlobStorage, retention: PeerJobRetentionPolicy) =
        BlobPeerGroupJobMap(blobs, retention, (fun () -> DateTimeOffset.UtcNow))

    interface IPeerGroupJobMap with
        member _.Retention = policy

        member _.Bind(scopeId: string, groupJobId: PeerJobId, binding: PeerGroupJobBinding) = async {
            let boundAt = clock ()

            do!
                writeDocument scopeId groupJobId {
                    Binding = binding
                    ExpiresAt = policy.Ttl |> Option.map (fun ttl -> boundAt.Add ttl)
                    TerminalObservedAt = None
                }
        }

        member _.TryGet(scopeId: string, groupJobId: PeerJobId) = async {
            let! decoded = readDocument scopeId groupJobId

            match decoded with
            | None -> return None
            | Some doc ->
                if isExpired (clock ()) doc then
                    // Retired. Reclaim the blob on the way past — the whole
                    // sweep, running on the read that would otherwise have
                    // routed a poll at a member record that is itself gone.
                    let! _ = blobs.Delete(container, blobNameFor scopeId groupJobId)
                    return None
                else
                    return Some doc.Binding
        }

        member _.MarkTerminalObserved(scopeId: string, groupJobId: PeerJobId) = async {
            let! decoded = readDocument scopeId groupJobId

            match decoded with
            | None -> return false
            | Some doc ->
                let readAt = clock ()

                if isExpired readAt doc || doc.TerminalObservedAt.IsSome then
                    return false
                else
                    do!
                        writeDocument scopeId groupJobId {
                            doc with
                                TerminalObservedAt = Some readAt
                        }

                    return true
        }

/// What the host's job-poll route needs to resolve a group-minted handle:
/// the map that says what the handle stands for, and the transport the
/// gateway polls the owning member over.
///
/// A single DI singleton rather than two, and registered **only** by a
/// gateway composition that was given a map (`PeerServerApp.withGroupJobMap`
/// + `PeerGateway.withAggregate`). An ordinary receiver registers nothing,
/// the poll route resolves `None`, and its path through the handler is
/// byte-for-byte the pre-630 one (GP 11 / GP 13).
///
/// The client rides here rather than being resolved independently because
/// the gateway's transport is the one `withAggregate` was handed — a
/// deployment may pass a thin forwarder over the composed `IPeerClient`
/// singleton, or its own `HttpPeerClient` — and the poll leg must delegate
/// over exactly the transport the invoke leg used.
type PeerGroupJobFronting = {
    Map: IPeerGroupJobMap
    Client: IPeerClient
}