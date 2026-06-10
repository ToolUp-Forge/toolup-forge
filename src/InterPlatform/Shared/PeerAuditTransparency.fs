// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System

// ─── Phase 18a — cross-deployment audit transparency (contract) ──────
//
// The peer foundation (Phase 18) already records one `PeerCallCompleted`
// audit row per *inbound* call on the receiving deployment — keyed by the
// validated caller's peer id. Audit transparency lets a *calling* peer
// read back the receiver's record of its own calls, so it can reconcile
// what it asked for against what the counterpart logged: "I asked for a
// minimum of k=50 and got 47 rows — confirm the seller's gate suppressed
// three." It is the cross-deployment analogue of a customer reading their
// own request log on a SaaS they call.
//
// The defining constraint is **scoping**: a peer may read ONLY the rows
// where it was the call's validated caller. That scope is enforced on the
// receiver from the authenticated `PeerPrincipal` (the same id the
// JSON-RPC host rebuilds the call context from), never from any value the
// caller passes in the query — so the query record carries no caller id
// to forge. These types are the wire surface; the enforcement lives in
// `PeerAuditContractHost`.
//
// Like the rest of the peer substrate this is server-to-server (no Fable
// client surface). The types satisfy the six portability rules (GP 12):
// identity by value, plain immutable records, no framework serialisation
// attributes.

/// Filter for a peer audit-transparency query. Every field is an optional
/// narrowing constraint; the caller-scoping (only the authenticated
/// caller's own calls) is applied server-side from the validated
/// principal and is deliberately NOT expressible here — there is no
/// caller-id field to spoof.
type PeerAuditQuery = {
    /// Restrict to a single hosted contract id. `None` = every contract
    /// the caller has called.
    ContractId: string option
    /// Restrict to a single contract method name. `None` = every method.
    MethodName: string option
    /// Only calls resolved at or after this instant. `None` = no lower
    /// time bound.
    SinceUtc: DateTimeOffset option
    /// When `true`, return only calls that did not succeed
    /// (`Succeeded = false`) — the common "what did the counterpart
    /// reject?" reconciliation path. When `false`, return all calls.
    FailuresOnly: bool
    /// Maximum number of entries to return, newest first. Clamped
    /// server-side to `PeerAudit.maxLimit`; a non-positive value falls
    /// back to `PeerAudit.defaultLimit`.
    Limit: int
}

/// A single counterpart-side audit row visible to the calling peer: the
/// receiver's record of one call the *caller itself* made. Deliberately
/// omits `CallerPeerId` — it is always the querying peer, so echoing it is
/// redundant, and its absence makes cross-peer leakage impossible by
/// construction (there is no field through which another peer's id could
/// ever reach a caller).
type PeerAuditEntry = {
    /// The hosted contract id the call targeted.
    ContractId: string
    /// The contract method dispatched.
    MethodName: string
    /// The cascade-wide correlation id shared across every hop — lets the
    /// caller line a row up against its own outbound call log.
    RootRequestId: string
    /// `true` when the receiver's dispatch returned `Ok`.
    Succeeded: bool
    /// Short outcome label: `"ok"` on success, else the `PeerError` DU
    /// case name (e.g. `"PeerMethodNotFound"`).
    Outcome: string
    /// Wall-clock time the call resolved on the receiver.
    OccurredAt: DateTimeOffset
}

/// The typed audit-transparency contract a peer-enabled deployment hosts
/// when audit transparency is composed in (`withPeerAuditTransparency`).
/// The *calling* deployment builds a typed proxy over this with
/// `JsonRpcPeerClient.create<IPeerAuditApi>`; the receiver answers from
/// its own audit log, scoped to the caller's own calls. A record of
/// functions, like every peer contract — so the same reflection-based
/// proxy / host machinery applies.
type IPeerAuditApi = {
    /// Return the receiver's audit rows for calls the authenticated
    /// caller made, newest first, narrowed by `query`.
    QueryCalls: PeerAuditQuery -> Async<PeerAuditEntry list>
}

/// Reserved identifiers + bounds for the audit-transparency contract.
[<RequireQualifiedAccess>]
module PeerAudit =

    /// The reserved contract id the audit-transparency host registers
    /// under. Namespaced beneath the peer substrate's reserved
    /// `_platform.peer` prefix so it never collides with an author-defined
    /// contract id.
    [<Literal>]
    let contractId = "_platform.peer.audit"

    /// The wire method name — must match the single field of
    /// `IPeerAuditApi` (the typed proxy uses the field name as the method
    /// name).
    [<Literal>]
    let queryMethod = "QueryCalls"

    /// The contract's only wire version.
    let v1: ContractVersion = { Major = 1; Minor = 0 }

    /// Server-side hard cap on `PeerAuditQuery.Limit`, applied regardless
    /// of what the caller requests — bounds both the response size and the
    /// projection cost.
    [<Literal>]
    let maxLimit = 500

    /// The limit applied when a caller passes a non-positive `Limit`.
    [<Literal>]
    let defaultLimit = 100