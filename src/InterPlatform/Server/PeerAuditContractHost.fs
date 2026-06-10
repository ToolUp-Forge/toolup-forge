// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.InterPlatform.PeerAuditContractHost

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
// The read path is `IAuditLog.GetAuditTrail(_platform scope, …,
// PeerCallCompleted)`, which returns already-decoded `AuditEvent`s. The
// foundation emits exactly one `PeerCallCompleted` row per inbound call
// under the `_platform` scope (`PeerJob.Scope`), so that scope + event
// type is the complete source set for this contract.

/// Project the receiver's `PeerCallCompleted` audit rows down to the
/// caller-visible `PeerAuditEntry` list, scoped to `callerPeerId` and
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
        | PeerCallCompleted p -> Some p
        | _ -> None)
    // Scoping crux: a peer sees ONLY rows where it was the validated
    // caller. Enforced here against the authenticated id, never against a
    // value carried in the query — `PeerAuditQuery` has no caller field.
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
/// marshals with, reads the `PeerCallCompleted` rows from the `_platform`
/// audit trail, and returns the caller-scoped projection. A read failure
/// collapses to `PeerHandler`. Registered under `PeerAudit.contractId` at
/// `PeerAudit.v1`.
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

                    let! events = auditLog.GetAuditTrail(PeerJob.Scope, None, Some "PeerCallCompleted")
                    let entries = project context.Peer.PeerId query events
                    return Ok(JsonRpc.serialize entries)
                with ex ->
                    return Error(PeerHandler ex.Message)
        }

    {
        ContractId = PeerAudit.contractId
        Versions = [ PeerAudit.v1 ]
        Dispatch = dispatch
    }