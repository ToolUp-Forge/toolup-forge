// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.InterPlatform.PeerAuditContractHost

open System
open ToolUp.Platform
open PeerReflection

// ─── Phase 18a — cross-deployment audit transparency (host) ──────────
//
// Author-defined contracts are built with `JsonRpcPeerHost.contract`,
// whose generic dispatch closure DISCARDS the call context — a normal
// contract method only sees its positional arguments. The audit-
// transparency contract cannot use that path: it MUST see the *validated*
// caller identity to scope its answer, because the whole point is "show me
// my own rows and no one else's". So it ships a bespoke
// `PeerContractRegistration` whose dispatch reads `context.Peer.PeerId` —
// the id the JSON-RPC host rebuilt from the authenticated `PeerPrincipal`,
// never the self-asserted wire body — and filters the receiver's audit
// trail to exactly that peer's calls.
//
// The read path is `IAuditLog.GetAuditTrail(_platform scope, …, <event
// type>)`, which returns already-decoded `AuditEvent`s. Every peer
// lifecycle row lands under the `_platform` scope (`PeerJob.Scope`), so
// that scope plus the two peer-call event types is the complete source set
// for this contract.
//
// **Phase 310 — two event types, because a long-running call produces two
// rows.** `PeerCallCompleted` is emitted when the receiver *accepts* a call;
// for a `LongRunning` method that is the moment the job is scheduled, so the
// row says `ok` whatever the computation later does. `PeerJobCompleted` is
// the terminal row the job handler writes when it actually finishes. The
// transparency contract answers with both — the caller asked "what did you
// log for my calls", and half an answer was the defect this phase closes.
//
// The two are not double-counting: they are distinct events about one call,
// joinable on `RootRequestId`, and `FailuresOnly` now returns exactly the
// terminal failures. Before this phase that filter was silently empty for
// every long-running call, however badly it had gone.

/// One caller-scoped audit row, flattened from either peer-call event so
/// the filters and the projection below are written once rather than
/// duplicated per event type. Internal to this projection — not a wire
/// shape.
type private PeerAuditRow = {
    ContractId: string
    MethodName: string
    CallerPeerId: string
    RootRequestId: string
    Succeeded: bool
    Outcome: string
    OccurredAt: DateTimeOffset
}

/// Project the receiver's peer-call audit rows — schedule-time
/// (`PeerCallCompleted`) and terminal (`PeerJobCompleted`) alike — down to
/// the caller-visible `PeerAuditEntry` list, scoped to `callerPeerId` and
/// narrowed by `query`, newest first. Pure and total — separated from the
/// dispatch closure so the scoping + filter logic is unit-testable without
/// an `IPlatformPeer`, an `IAuditLog`, or a transport.
let project (callerPeerId: string) (query: PeerAuditQuery) (events: AuditEvent list) : PeerAuditEntry list =
    let limit =
        if query.Limit <= 0 then
            PeerAudit.defaultLimit
        else
            min query.Limit PeerAudit.maxLimit

    events
    |> List.choose (fun e ->
        match e with
        | PeerCallCompleted p ->
            Some {
                ContractId = p.ContractId
                MethodName = p.MethodName
                CallerPeerId = p.CallerPeerId
                RootRequestId = p.RootRequestId
                Succeeded = p.Succeeded
                Outcome = p.Outcome
                OccurredAt = p.OccurredAt
            }
        | PeerJobCompleted p ->
            Some {
                ContractId = p.ContractId
                MethodName = p.MethodName
                CallerPeerId = p.CallerPeerId
                RootRequestId = p.RootRequestId
                Succeeded = p.Succeeded
                Outcome = p.Outcome
                OccurredAt = p.OccurredAt
            }
        | _ -> None)
    // Scoping crux: a peer sees ONLY rows where it was the validated
    // caller. Enforced here against the authenticated id, never against a
    // value carried in the query — `PeerAuditQuery` has no caller field.
    // The terminal row is scoped by the same field, populated from the
    // owner the dispatch side stamped onto the job payload, so a peer can
    // no more read another's terminal outcomes than its schedule-time ones.
    |> List.filter (fun p -> p.CallerPeerId = callerPeerId)
    |> List.filter (fun p ->
        match query.ContractId with
        | Some c -> p.ContractId = c
        | None -> true)
    |> List.filter (fun p ->
        match query.MethodName with
        | Some m -> p.MethodName = m
        | None -> true)
    |> List.filter (fun p ->
        match query.SinceUtc with
        | Some since -> p.OccurredAt >= since
        | None -> true)
    |> List.filter (fun p -> not query.FailuresOnly || not p.Succeeded)
    |> List.sortByDescending _.OccurredAt
    |> List.truncate limit
    |> List.map (fun p -> {
        ContractId = p.ContractId
        MethodName = p.MethodName
        RootRequestId = p.RootRequestId
        Succeeded = p.Succeeded
        Outcome = p.Outcome
        OccurredAt = p.OccurredAt
    })

/// Build the audit-transparency `PeerContractRegistration` bound to
/// `auditLog`. The dispatch accepts only the `QueryCalls` method (any
/// other name is `PeerMethodNotFound`), unmarshals the single positional
/// `PeerAuditQuery` argument with the same reflection shim the typed proxy
/// marshals with, reads the `PeerCallCompleted` **and** (Phase 310)
/// `PeerJobCompleted` rows from the `_platform` audit trail, and returns
/// the caller-scoped projection over both. A read failure collapses to
/// `PeerHandler`. Registered under `PeerAudit.contractId` at
/// `PeerAudit.v1`.
///
/// Two reads rather than one: `IAuditLog.GetAuditTrail` takes a single
/// event-type filter, and dropping the filter entirely would pull every
/// audit row in the `_platform` scope — every tenant-lifecycle, signing and
/// health event the deployment records — through this projection to discard
/// almost all of it. Two narrow reads are the cheap shape, and the union is
/// re-sorted by `project` anyway.
let registration (auditLog: IAuditLog) : PeerContractRegistration =
    let dispatch: PeerDispatch =
        fun context methodName argsJson -> async {
            if methodName <> PeerAudit.queryMethod then
                return Error(PeerMethodNotFound methodName)
            else
                try
                    // Same wire shape the typed proxy produces: a single
                    // positional `PeerAuditQuery` in a JSON array.
                    let query =
                        unmarshalArgs argsJson [ typeof<PeerAuditQuery> ] |> List.head :?> PeerAuditQuery

                    let! scheduled = auditLog.GetAuditTrail(PeerJob.Scope, None, Some "PeerCallCompleted")
                    let! terminal = auditLog.GetAuditTrail(PeerJob.Scope, None, Some "PeerJobCompleted")
                    let entries = project context.Peer.PeerId query (scheduled @ terminal)
                    return Ok(JsonRpc.serialize entries)
                with ex ->
                    return Error(PeerHandler ex.Message)
        }

    {
        ContractId = PeerAudit.contractId
        Versions = [ PeerAudit.v1 ]
        Dispatch = dispatch
    }