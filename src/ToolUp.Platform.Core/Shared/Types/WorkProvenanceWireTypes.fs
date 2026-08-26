// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Upstream work records as a read-only wire contract ──────────────
//
// The deploy record says WHAT was deployed and, through the build
// transcript, WHAT it was built from. The dependency closure structures
// one half of the upstream question — which resolved packages a build
// stands on. The other half is still a bare digest: what *authoring
// work* produced the sources, and can a reader walk it? A digest can be
// compared and cannot be traversed, so "show me the work behind this
// deployment" is an investigation rather than a lookup.
//
// An **upstream work record** is the walkable half: one record in some
// source system — an authoring step, a review, a verification, a
// release — carrying its own identity, its kind, a content digest, the
// records it descends from, an optional recorded verdict, and the
// opaque label of the system that minted it.
//
// **The platform never learns the producing system's vocabulary.** Every
// value here is a string, an int, or a record of the same. The
// `SourceSystem` label is opaque, the `RecordId` is a join key nothing
// here parses, the `Verdict` is carried verbatim, and `WorkRecordKind`
// keeps an `Other` case precisely so a kind this SDK has never heard of
// crosses INTACT rather than being dropped at the boundary or coerced
// into the nearest known case. A consumer that attaches its own
// authoring system owns that vocabulary end to end; no code in this SDK
// will ever branch on it.
//
// ── The three properties, and why they are these three ───────────────
//
// This is the work/build tier of a contract shape the fact/data tier
// already ships (`IProvenanceQueryApi` and its wire mirrors). Rather
// than invent a second vocabulary for the same idea, the same three
// properties carry this tier, and each is pinned by a test rather than
// asserted in prose:
//
//   1. **Read-only by construction.** Every member of the seam is a
//      query. None takes a mutation and none answers with `unit` — so
//      there is no write surface to forget to gate, because there is no
//      write surface. A deployment cannot expose a work-record write
//      path by composing this seam, whatever it wires behind it.
//   2. **Bounded, never silently truncated.** Depth and record caps are
//      *declared* (`GetCaps`), and a request or an answer that exceeds
//      either is refused with a typed error naming both what was asked
//      and the limit. A shortened ancestor chain is indistinguishable
//      from a complete one, and a reader concluding "this is all the
//      work behind the deployment" from a silently-trimmed walk reaches
//      a confident wrong conclusion.
//   3. **A withheld record is not an absent one.** A record the source
//      system refuses crosses as a typed marker carrying its ref, its
//      kind and the policy that refused it — never as a hole. "This
//      exists and you may not see it" and "there is no work recorded
//      here" are different answers to a reader auditing a deployment,
//      and collapsing them turns a working control into apparent
//      missing data.
//
// **Fable-safe by construction** (GP 10): records, unions and pure
// string handling only, so these types compile on the client tier with
// the rest of `ToolUp.Platform.Core`. The seam itself
// (`IWorkProvenanceSource`) is server-tier; only the answers it speaks
// in live here.
//
// Purely additive (GP 11 / GP 13): every type here is new, nothing
// existing is retyped, and a deployment that composes no work
// provenance source allocates nothing and behaves byte-for-byte as it
// did before this surface existed.

/// What kind of upstream work a record describes.
///
/// A small closed set the platform can reason about structurally, plus
/// `Other` for everything else. The open case is load-bearing rather
/// than defensive: source systems name their steps differently, and a
/// contract that admitted only the four known kinds would either drop an
/// unrecognised record or file it under the nearest wrong case. Both
/// lose information silently. Carrying the label through means a reader
/// sees exactly what the source system said, and the platform still
/// never has to understand it.
[<RequireQualifiedAccess>]
type WorkRecordKind =
    /// Work that produced or changed content.
    | Authored
    /// Work that reviewed other work.
    | Reviewed
    /// Work that checked other work against a stated criterion.
    | Verified
    /// Work that published or cut a release.
    | Released
    /// Any other kind, carried as the source system's own label. Never
    /// interpreted here; never coerced into one of the cases above.
    | Other of label: string

[<RequireQualifiedAccess>]
module WorkRecordKind =

    /// A stable lowercase token per kind, for canonical forms and
    /// operator-facing display. One place, so a diagnostic, a test and a
    /// consumer's rendering all read the same word.
    let label (kind: WorkRecordKind) : string =
        match kind with
        | WorkRecordKind.Authored -> "authored"
        | WorkRecordKind.Reviewed -> "reviewed"
        | WorkRecordKind.Verified -> "verified"
        | WorkRecordKind.Released -> "released"
        | WorkRecordKind.Other other -> other

/// A reference to one upstream work record: its identity, and the
/// system that minted it.
///
/// Identity by value (GP 12 rule 1) — two strings, so the ref crosses
/// any boundary and round-trips through any serialiser. `SourceSystem`
/// is part of the identity rather than metadata beside it: the same
/// `RecordId` may well be minted by two different systems, and a
/// reference that could not say which would be a join key that does not
/// join.
type WorkRecordRef = {
    /// The record's identity in its own source system, exactly as that
    /// system names it. A join key, never parsed here.
    RecordId: string
    /// Opaque label of the system that minted the id. Carried so a
    /// reader knows who to ask; the platform reads it no further.
    SourceSystem: string
}

[<RequireQualifiedAccess>]
module WorkRecordRef =

    /// A ref, as its source system names it.
    let create (sourceSystem: string) (recordId: string) : WorkRecordRef = {
        RecordId = recordId
        SourceSystem = sourceSystem
    }

    /// Render a ref for an operator-facing surface. Deliberately not a
    /// parseable encoding — nothing here reads it back.
    let describe (reference: WorkRecordRef) : string =
        if reference.SourceSystem = "" then
            reference.RecordId
        else
            $"{reference.RecordId} ({reference.SourceSystem})"

/// One upstream work record, as it crosses the seam.
type WorkRecord = {
    /// The record's own reference.
    Ref: WorkRecordRef
    /// What kind of work this was.
    Kind: WorkRecordKind
    /// Content digest of the record as its source system computed it,
    /// lowercase hex, or `""` when the system exposes none. An empty
    /// digest is honest; a fabricated one is not.
    ContentDigest: string
    /// The records this one descends from — the edges an ancestor walk
    /// follows. Empty for a root.
    Parents: WorkRecordRef list
    /// A verdict the source system recorded against this work, carried
    /// verbatim as that system's own label. `None` when it recorded
    /// none; the platform never infers one and never branches on it.
    Verdict: string option
    /// Human label for display. Never an identity.
    Label: string
}

/// A work record the source system refused, as it crosses the seam.
///
/// **Ref and kind, never label, digest, verdict or parents.** The marker
/// exists so a reader can tell a suppressed record from a missing one
/// and can name what it could not read when it says so; the record's
/// *content* is exactly what the refusal withheld.
type WithheldWorkRecord = {
    /// The withheld record's reference — the same ref its descendants'
    /// `Parents` carry, so chain SHAPE survives the refusal.
    Ref: WorkRecordRef
    Kind: WorkRecordKind
    /// Why, as the source system's own policy reference. Opaque here.
    PolicyRef: string
}

/// The answer to a single-record lookup. Three outcomes, deliberately
/// not two: a reader must be able to distinguish "suppressed" from "no
/// work recorded".
[<RequireQualifiedAccess>]
type WorkRecordAnswer =
    /// The record, as the source system resolved it.
    | Found of WorkRecord
    /// The record exists and the source system refused it.
    | Withheld of WithheldWorkRecord
    /// The source system holds no record under this ref.
    | Absent

/// A bounded ancestor request: where to start, and how far back.
type WorkAncestorRequest = {
    /// The record to walk back from.
    Root: WorkRecordRef
    /// Hops to walk. Must be at least 1 and at most the declared
    /// `MaxDepth`; anything else is refused rather than clamped.
    Depth: int
}

/// One materialised ancestor walk, complete by construction.
///
/// "Page" names the unit a caller receives, not a window over a larger
/// result: this contract has no cursor and no `HasMore`, because the
/// alternative to refusing an over-cap walk is handing back a partial
/// chain, and a partial work chain answers the reader's question wrongly
/// rather than incompletely. Every record the walk reached is here,
/// either in `Records` or — where the source system refused it — in
/// `Withheld`.
type WorkAncestorPage = {
    /// The ref the walk was rooted at.
    Root: WorkRecordRef
    /// Records the caller may read.
    Records: WorkRecord list
    /// Records the source system refused, as typed markers.
    Withheld: WithheldWorkRecord list
    /// The depth the walk was bounded to — echoed so a reader holding a
    /// stored answer knows what bound produced it.
    Depth: int
}

[<RequireQualifiedAccess>]
module WorkAncestorPage =

    /// How many records the walk reached, readable plus withheld. This
    /// is the number the record cap is taken against: a walk does not
    /// become smaller by refusing to show you part of it.
    let size (page: WorkAncestorPage) : int =
        List.length page.Records + List.length page.Withheld

/// The bounds a deployment's work provenance surface declares. Read by
/// `GetCaps` so a caller can size its walk instead of discovering the
/// limit as a refusal.
type WorkProvenanceCaps = {
    /// The largest `Depth` an ancestor request may ask for.
    MaxDepth: int
    /// The largest number of records (readable plus withheld) a single
    /// ancestor answer may carry. A walk producing more is refused,
    /// never trimmed.
    MaxRecords: int
}

[<RequireQualifiedAccess>]
module WorkProvenanceCaps =

    /// The shipped defaults, matching the fact-tier surface's bounds so
    /// a deployment reasoning about both does not have to hold two
    /// numbers. A response-size bound, not a modelling statement — a
    /// deployment whose work chains are genuinely larger raises it at
    /// composition.
    let defaults = { MaxDepth = 10; MaxRecords = 2000 }

    /// The bounds a source that answers nothing declares. Distinct from
    /// `defaults` on purpose: a caller reading zeroes learns that this
    /// deployment has no work provenance surface, rather than sizing a
    /// walk that will always come back empty.
    let none = { MaxDepth = 0; MaxRecords = 0 }

/// Why an ancestor request was refused. Every case names both what was
/// asked and what the limit is, so a caller can correct the request
/// without a second round-trip.
type WorkProvenanceError =
    /// `Depth` was below 1. A zero-or-negative walk is a caller bug, not
    /// a request for the root record on its own.
    | WorkDepthInvalid of requested: int
    /// `Depth` exceeded the declared `MaxDepth`.
    | WorkDepthExceedsCap of requested: int * cap: int
    /// The walk completed and reached more records than `MaxRecords`
    /// allows. The answer is refused whole — nothing is truncated.
    | WorkAncestorsExceedRecordCap of records: int * cap: int
    /// The composed source declares a cap and answered with more than it
    /// — a truncation the caller would otherwise never see, reported as
    /// the contract violation it is rather than passed on.
    | WorkAncestorsOverDeclaredCap of records: int * cap: int

[<RequireQualifiedAccess>]
module WorkProvenanceError =

    /// Human-readable refusal text. One place, so a diagnostic, a test
    /// and a consumer's error surface all read the same wording.
    let describe (error: WorkProvenanceError) : string =
        match error with
        | WorkDepthInvalid requested -> $"depth {requested} is invalid — an ancestor walk needs at least 1 hop"
        | WorkDepthExceedsCap(requested, cap) ->
            $"depth {requested} exceeds this deployment's work provenance depth cap of {cap}"
        | WorkAncestorsExceedRecordCap(records, cap) ->
            $"the walk reached {records} work records, above this deployment's cap of {cap} — narrow the walk (a smaller depth, or a nearer root) rather than expecting a partial answer"
        | WorkAncestorsOverDeclaredCap(records, cap) ->
            $"the composed work provenance source returned {records} records against its own declared cap of {cap} — the answer is refused rather than passed on, because a caller cannot tell a trimmed chain from a complete one"

/// How a source system answers "which work record covers these sources".
///
/// Three answers, not two, for the same reason the lookup has three:
/// "no record covers this" and "I do not track this at all" are
/// different facts about a deployment, and a reader deciding what it
/// stands on needs to know which one it is looking at.
[<RequireQualifiedAccess>]
type WorkCoverage =
    /// A work record covers these sources — the head of the chain.
    | Covered of WorkRecordRef
    /// The source system does not track these sources at all.
    | NotTracked
    /// The source system tracks these sources, but holds no record
    /// covering this particular upstream reference.
    | NoCoveringRecord

/// Why a deploy carries no reference to an upstream work record.
///
/// Distinguishable on purpose. "nobody was asked", "there was nothing to
/// ask about", "asked, and the system does not track these sources",
/// "asked, and no record covers them" and "asked, and the answer never
/// arrived" are five different facts. A deployment that reported only
/// the attested case would read as complete and would not be — so no
/// deploy is ever silently dropped from the account, and the reason
/// travels with the gap.
[<RequireQualifiedAccess>]
type WorkUnattestedReason =
    /// No work provenance source was composed — nothing was asked.
    | SourceAbsent
    /// The deploy recorded no upstream provenance reference, so there
    /// was nothing to look the work up by.
    | UpstreamReferenceUnrecorded
    /// The source system does not track these sources at all.
    | NotTracked
    /// The source system tracks these sources, but holds no work record
    /// covering them.
    | NoCoveringRecord
    /// The source was asked and failed; the failure's own reason is
    /// carried. Recorded on the deploy rather than aborting, because
    /// losing the account to one failed lookup would be exactly the
    /// silence this type exists to prevent.
    | LookupFailed of reason: string

[<RequireQualifiedAccess>]
module WorkUnattestedReason =

    /// Render a reason for an operator-facing surface.
    let describe (reason: WorkUnattestedReason) : string =
        match reason with
        | WorkUnattestedReason.SourceAbsent -> "no work provenance source was composed"
        | WorkUnattestedReason.UpstreamReferenceUnrecorded ->
            "the deploy recorded no upstream provenance reference to look the work up by"
        | WorkUnattestedReason.NotTracked -> "the source system does not track these sources"
        | WorkUnattestedReason.NoCoveringRecord -> "the source system holds no work record covering these sources"
        | WorkUnattestedReason.LookupFailed failure -> $"the work provenance source failed: {failure}"

/// Whether a deploy stands on a recorded upstream work chain. Never
/// silent: attested by reference, or unattested with the reason.
[<RequireQualifiedAccess>]
type WorkAttestation =
    | AttestedBy of WorkRecordRef
    | Unattested of WorkUnattestedReason

[<RequireQualifiedAccess>]
module WorkAttestation =

    /// Render an attestation for an operator-facing surface — wherever a
    /// deploy's work provenance renders, this is its vocabulary.
    let describe (attestation: WorkAttestation) : string =
        match attestation with
        | WorkAttestation.AttestedBy reference -> $"attested by upstream work record {WorkRecordRef.describe reference}"
        | WorkAttestation.Unattested reason -> $"unattested — {WorkUnattestedReason.describe reason}"

    /// The head work record, when there is one. `None` carries no reason
    /// and is therefore never the shape anything is RECORDED in — it is
    /// for a caller that has already handled the unattested case and
    /// wants the ref.
    let head (attestation: WorkAttestation) : WorkRecordRef option =
        match attestation with
        | WorkAttestation.AttestedBy reference -> Some reference
        | WorkAttestation.Unattested _ -> None