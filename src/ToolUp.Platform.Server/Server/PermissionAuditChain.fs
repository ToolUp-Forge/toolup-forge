// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.PermissionAuditChain

open System.Text
open ToolUp.Platform

// ─── Phase 553.A — the in-store permission-event hash chain ──────────
//
// `PermissionChanged` is the audit event a counterparty most wants to
// be able to trust: it is the record of who was granted access to what,
// and it is the record an insider with store access has the clearest
// motive to edit. Append-only storage makes that hard; it does not make
// it EVIDENT. A chain does — each record commits to its predecessor, so
// deleting or editing one breaks every link after it, and the break is
// positioned rather than merely detected.
//
// **Why this exists beside the chained-ledger SINK, not instead of it.**
// `ToolUp.AuditSinks.ChainedLedger` (Phase 677) chains the records it
// REPLICATES, into its own blob store, and ships the counterparty export
// and the DSSE-wrapped verifier that go with them. That is the right
// artefact for a deployment that composes the sink. It is unreachable
// for one that does not — and a deployment composing no audit sink at
// all is the default (GP 13). This module is what such a deployment
// still gets: tamper-evidence over the events as they sit in the
// deployment's OWN `IEventStore`, with no sink, no blob store, no key
// material and no export format. The two chains are deliberately
// independent — same convention, different substrate — because a
// replicated chain proves nothing about the store it was replicated
// FROM.
//
// **Reuse, not a second primitive.** The canonical form is built with
// `ProvenanceFraming` (Core), the estate's one length-prefixed framing
// seam, and the digest with `DeployRecords.digestCanonicalForm`, the
// estate's one server-side "SHA-256 over a canonical form" helper. This
// module contributes the FIELD ORDER a permission record is framed in
// and nothing else; there is no new hashing, no new canonicalisation,
// and no new digest spelling.
//
// **Reading the head is a topology question, not a timestamp one.**
// `IEventStore` promises no cross-method ordering and stores
// `OccurredAt` at `DateTime` resolution, so "the latest permission
// record" is not a well-defined read. The head is instead the record
// whose `ContentHash` nothing else names as its `PrevHash` — a property
// of the chain itself, identical on every reader. Two such records mean
// the chain has FORKED, and a fork makes the head unreadable rather
// than ambiguous: the writer refuses (or degrades) under the Phase 9t
// policy instead of picking one and compounding the damage.

/// The predecessor value a genesis record chains to — 64 hex zeros, the
/// all-zero SHA-256 width. Spelled identically to the chained-ledger
/// sink's `genesisDigest`, and load-bearing for the same reason: a
/// record claiming it asserts it is FIRST, so a chain truncated from the
/// front cannot pass verification by starting later.
[<Literal>]
let genesisHash = "0000000000000000000000000000000000000000000000000000000000000000"

/// The framing version the canonical form opens with. Bump it — and
/// only then — when the FIELD SET or the field ORDER below changes:
/// every stored hash was taken under the version standing at write
/// time, so a silent reordering would read as estate-wide tampering.
/// A version bump makes the change a declared boundary instead.
[<Literal>]
let framingVersion = "toolup.permission-audit-chain.v1"

/// The `AuditEvent` case name the chain covers, matching the codec
/// registry's `EventType` for `PermissionChanged`. Framed into the
/// canonical form so a record cannot be re-labelled as a different
/// event kind without breaking its hash.
[<Literal>]
let chainedEventType = "PermissionChanged"

/// The exact text a permission record's hash is taken over: the framing
/// version, the scope, the event type, the predecessor hash, and every
/// content field of the payload, each length-framed.
///
/// **`PrevHash` is inside the framing**, which is the whole mechanism —
/// it is what makes a record's hash commit to the entire prefix of the
/// chain rather than to its own five fields.
///
/// **The payload's own `Chain` field is deliberately NOT framed.** It
/// carries the two hashes, one of which is the output of this function;
/// framing it would be circular. The predecessor half is framed
/// explicitly, above, so nothing it contributes is lost.
///
/// **Fields are framed explicitly rather than by serialising the record**
/// — matching every other canonical form in this substrate — which
/// means a field added to `PermissionChangedPayload` later is NOT
/// covered until it is added here, under a bumped `framingVersion`.
/// That is a maintenance obligation and is stated so it is not a
/// surprise; the alternative (hashing the serialiser's output) makes the
/// digest depend on a converter's emission order, which is an
/// implementation detail of whichever library produced it.
let canonicalForm (scopeId: string) (prevHash: string) (payload: PermissionChangedPayload) : string =
    let builder = StringBuilder()
    let frame = ProvenanceFraming.frame builder

    frame framingVersion
    frame scopeId
    frame chainedEventType
    frame prevHash
    frame payload.UserId
    frame payload.TeamId
    frame payload.AffectedUserId
    frame payload.ModuleName
    frame payload.Permissions

    builder.ToString()

/// SHA-256, bare lowercase hex, over `canonicalForm`. Deterministic in
/// the strong sense: the same payload under the same predecessor yields
/// the same hash on any machine, in any process, at any time — which is
/// what lets a verifier recompute rather than merely re-read.
let contentHash (scopeId: string) (prevHash: string) (payload: PermissionChangedPayload) : string =
    canonicalForm scopeId prevHash payload |> DeployRecords.digestCanonicalForm

/// Attach a chain link to a payload, chaining it onto `prevHash`. The
/// single place a link is formed.
let link (scopeId: string) (prevHash: string) (payload: PermissionChangedPayload) : PermissionChangedPayload = {
    payload with
        Chain =
            Some {
                PrevHash = prevHash
                ContentHash = contentHash scopeId prevHash payload
            }
}

/// What reading a scope's chain head produced.
///
/// Three of the four are answers; `HeadForked` and `HeadUnanchored` are
/// refusals, and the distinction matters at the write path — an answer
/// is chained onto, a refusal takes the Phase 9t failure policy.
type ChainHeadReading =
    /// No chained record in this scope yet, so the next record is the
    /// genesis one. Covers both a scope that has never recorded a
    /// permission change and a scope whose permission records ALL
    /// predate the chain.
    | HeadEmpty
    /// The unique unreferenced `ContentHash` — the value the next
    /// record chains onto.
    | HeadAt of string
    /// More than one record is unreferenced: the chain has forked, and
    /// there is no single head. Carries every candidate so the operator
    /// sees the shape of the split rather than a bare count.
    | HeadForked of string list
    /// Chained records exist but NONE is unreferenced — every record is
    /// somebody's predecessor. That is a cycle, which an honest writer
    /// cannot produce; it means the stream was constructed rather than
    /// appended. Refused for the same reason a fork is.
    | HeadUnanchored

/// Derive the chain head from a scope's permission records.
///
/// Order-independent by construction: it reads only the link topology,
/// never the order the store returned the records in, so two readers
/// that receive the same set in different orders agree. Unchained
/// (pre-553) records are ignored here — they are outside the chain, and
/// the verifier is where they are accounted for.
let readHead (payloads: PermissionChangedPayload list) : ChainHeadReading =
    let links = payloads |> List.choose _.Chain

    if List.isEmpty links then
        HeadEmpty
    else
        let referenced = links |> List.map _.PrevHash |> Set.ofList

        let unreferenced =
            links
            |> List.map _.ContentHash
            |> List.distinct
            |> List.filter (referenced.Contains >> not)

        match unreferenced with
        | [] -> HeadUnanchored
        | [ single ] -> HeadAt single
        | several -> HeadForked several

/// What a verification walk found wrong. One value per class so a
/// caller branches on the KIND rather than on a diagnostic string.
type PermissionChainBreakKind =
    /// A record's stored `ContentHash` disagrees with the hash
    /// recomputed from its content: it was edited in place.
    | TamperedRecord
    /// Chained records exist but none claims `genesisHash`: the front of
    /// the chain was removed.
    | MissingGenesis
    /// Two records claim the same predecessor. Either the stream forked
    /// (concurrent writers that both read the same head) or a record was
    /// replaced by a re-chained substitute without removing the
    /// original.
    | ForkedChain
    /// The walk reached the end of the links while chained records
    /// remain unvisited: the records after the stopping point no longer
    /// join to anything, which is what DELETING a record in the middle
    /// leaves behind.
    | OrphanedRecords

/// The FIRST break, positioned. The walk stops here: everything after a
/// break is unverifiable in principle, and reporting a hundred
/// downstream consequences of one edit hides the edit.
type PermissionChainBreak = {
    /// Zero-based index along the CHAIN (not the store's read order) at
    /// which the break was observed. Deleting the record at index k
    /// reports index k; editing the record at index k reports index k.
    Position: int
    Kind: PermissionChainBreakKind
    /// Human-readable specifics — the two hashes that disagreed, the
    /// competing successors, or the unvisited count.
    Detail: string
}

/// A clean walk's accounting. Every record the scope holds is in
/// exactly one of the two counts, which is the property that makes the
/// report checkable rather than merely reassuring.
type PermissionChainSummary = {
    /// Records carrying no chain link. For an upgraded deployment this
    /// is its pre-553 history and is expected to be non-zero forever;
    /// for a deployment that has always chained, a non-zero count is a
    /// finding — it means a write took the Phase 9t `LogAndContinue`
    /// branch with the head unreadable.
    UnchainedCount: int
    /// Records verified along the chain, genesis first.
    ChainedCount: int
    /// The verified head hash, or `None` when the scope holds no chained
    /// record.
    Head: string option
}

/// The verdict. Two cases, because "intact" and "broken at position k"
/// are the only two things a caller can act on.
type PermissionChainReport =
    | ChainIntact of PermissionChainSummary
    | ChainBrokenAt of PermissionChainBreak

/// Verify a scope's permission chain from the records themselves.
///
/// **Pure, and total over any input.** It recomputes every hash rather
/// than trusting the stored one, so a store that lies about its own
/// digests is caught; it takes the record set in any order, so it does
/// not depend on the store's read contract; and it is offline, so an
/// auditor holding an export of the rows can run it without reaching
/// the deployment.
///
/// The four break classes and where each lands:
///
///   * **Edit a record's CONTENT** — its recomputed hash no longer
///     matches its stored one: `TamperedRecord` at that record's index.
///   * **Edit content AND restore the stored hash** — the record now
///     hashes consistently but its SUCCESSOR's `PrevHash` no longer
///     names it, so the walk stops early and the tail is stranded:
///     `OrphanedRecords` at the index the walk stopped.
///   * **Delete a record** — identical shape: the walk stops where the
///     link is missing and the tail is stranded.
///   * **Delete the FIRST record** — nothing claims genesis:
///     `MissingGenesis` at index 0.
let verify (scopeId: string) (payloads: PermissionChangedPayload list) : PermissionChainReport =
    let unchained, chained = payloads |> List.partition (fun p -> Option.isNone p.Chain)
    let unchainedCount = List.length unchained

    if List.isEmpty chained then
        ChainIntact {
            UnchainedCount = unchainedCount
            ChainedCount = 0
            Head = None
        }
    else
        // Index by predecessor once, so the walk is a lookup per step
        // rather than a scan, and so a duplicated predecessor is visible
        // as a group of more than one rather than by a second pass.
        let bySuccessor =
            chained
            |> List.groupBy (fun p -> p.Chain |> Option.map _.PrevHash |> Option.defaultValue genesisHash)
            |> Map.ofList

        let chainedCount = List.length chained

        let rec walk (index: int) (previousHash: string) =
            match Map.tryFind previousHash bySuccessor with
            | None
            | Some [] ->
                // End of the links. Either every chained record was
                // visited (intact) or some were stranded by a removed
                // or re-chained predecessor.
                if index = chainedCount then
                    ChainIntact {
                        UnchainedCount = unchainedCount
                        ChainedCount = chainedCount
                        Head = Some previousHash
                    }
                else
                    ChainBrokenAt {
                        Position = index
                        Kind = OrphanedRecords
                        Detail =
                            sprintf
                                "the chain ends after %d record(s) but the scope holds %d chained record(s); %d no longer join to a predecessor (head reached: %s)"
                                index
                                chainedCount
                                (chainedCount - index)
                                previousHash
                    }
            | Some [ record ] ->
                let stored = record.Chain |> Option.map _.ContentHash |> Option.defaultValue ""
                let recomputed = contentHash scopeId previousHash record

                if stored <> recomputed then
                    ChainBrokenAt {
                        Position = index
                        Kind = TamperedRecord
                        Detail =
                            sprintf "record stores content hash %s but its content recomputes to %s" stored recomputed
                    }
                else
                    walk (index + 1) recomputed
            | Some competing ->
                ChainBrokenAt {
                    Position = index
                    Kind = ForkedChain
                    Detail =
                        sprintf
                            "%d records claim predecessor %s: %s"
                            (List.length competing)
                            previousHash
                            (competing
                             |> List.map (fun p -> p.Chain |> Option.map _.ContentHash |> Option.defaultValue "<none>")
                             |> List.sort
                             |> String.concat ", ")
                }

        // A chain with no genesis record is reported as such rather than
        // as an orphan pile: "the front was removed" is a different
        // finding from "the middle was removed", and an auditor acts on
        // them differently.
        if not (Map.containsKey genesisHash bySuccessor) then
            ChainBrokenAt {
                Position = 0
                Kind = MissingGenesis
                Detail =
                    sprintf
                        "%d chained record(s) present but none claims the genesis predecessor — the front of the chain is missing"
                        chainedCount
            }
        else
            walk 0 genesisHash

/// Project an audit trail down to the permission payloads the chain
/// covers. The shape every caller of `verify` needs, kept here so no two
/// callers filter the union differently.
let permissionPayloads (events: AuditEvent list) : PermissionChangedPayload list =
    events
    |> List.choose (fun e ->
        match e with
        | PermissionChanged payload -> Some payload
        | _ -> None)

/// Verify a scope's permission chain through the registered `IAuditLog`.
///
/// The auditor-facing entry point, and deliberately the only one that
/// touches IO: it reads the scope's `PermissionChanged` trail and hands
/// it to the pure `verify` above. A caller holding rows from anywhere
/// else — an export, a replica, a backup — calls `verify` directly.
let verifyScope (auditLog: IAuditLog) (scopeId: string) : Async<PermissionChainReport> = async {
    let! events = auditLog.GetAuditTrail(scopeId, None, Some chainedEventType)
    return verify scopeId (permissionPayloads events)
}