// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Security.Cryptography
open System.Text
open ToolUp.Platform

// ─── Phase 490 — the governed cohort-activation seam (vocabulary) ─────
//
// Activation is the step where a cohort stops being an analytical
// artefact and starts being USED: a deployment holds a set of opaque
// ids, a counterparty holds another, and the overlap is handed to some
// downstream destination that will act on it. Every privacy control the
// roadmap has built so far bears on the ANSWER — the clean-room floor
// (Phases 18b / 311), the bilateral approval of the template being
// enforced (Phase 480), the cumulative epsilon budget (Phase 190), the
// calibrated noise (Phase 481). None of them bears on the ACT.
//
// The governance question activation asks is a different one, and it is
// not answerable by a floor: *who authorised this specific cohort, for
// this specific purpose, to this specific destination, and is that
// authorisation still valid?* A k-floor cleared by an activation nobody
// agreed to is a correctly-computed number crossing a boundary it had
// no permission to cross.
//
// This file is the vocabulary; `CohortActivation.fs` is the enforced
// pipeline. The split follows `CleanRoomBroker.fs` / `CleanRoomGate.fs`:
// the types + the canonical encoding are the part a counterparty, an
// auditor, and a test all read, and keeping them clear of the dispatch
// machinery is what let Phase 480's evaluation be tested exhaustively
// without a blob backend.
//
// ── Where activation sits in the gate's invariant chain ──
//
// `CleanRoomGate` already runs a layered chain on every gated answer:
// 0 approval → 0.5 budget → 1 surface → 2 checkability → 3 release
// post-condition → 4 calibrated noise. Activation does NOT build a
// second enforcement path beside it. It binds at exactly two points:
//
//   * **Invariant 0, reused verbatim.** An activation authorisation IS a
//     clean-room template approval — over a template this file DERIVES
//     from the authorisation (`ActivationCanonical.template`). The
//     derived template's id carries the SHA-256 of the whole
//     authorisation (destination + its permitted shapes + its floor +
//     the purpose + the cohort definition), so Phase 480's existing
//     content-hash version transitively binds to all of it. Nothing new
//     signs, nothing new revokes, nothing new stores: an approval is
//     issued, shipped, verified, evaluated and withdrawn by exactly the
//     code path that already does it for a query template. A second
//     approval mechanism would be a second thing to get wrong, and the
//     one that already exists is the one with the mutation defence.
//
//   * **Invariant 5, new here.** The egress projection. Invariants 0–4
//     decide whether a COUNT may be released; invariant 5 decides what
//     SHAPE that release takes as it leaves the deployment, and refuses
//     any shape the destination's approval does not name. It runs last,
//     on what invariant 4 produced, for the same reason noise runs after
//     invariant 3: each layer binds on what the layer below it has
//     already cleared, and a projection that ran earlier would be
//     projecting an answer nobody had yet agreed to release.
//
// ── Why the raw id list is unreachable rather than forbidden ──
//
// `ActivationRelease` has no case that carries a member id. That is the
// whole of the structural guarantee, and it is deliberately a fact about
// the type rather than a check in the pipeline: there is no value to
// construct, so there is no branch to forget. It is reinforced from
// underneath by the gate's invariant 2 — a handler that answered with
// rows instead of a `CohortResult` is withheld as uncheckable, so even a
// substituted cohort producer cannot smuggle a list through the pipe the
// projection reads from.
//
// Tokens are the sanctioned way a destination acts on individuals it
// matched: an opaque per-member pseudonym, keyed per authorisation, so
// two activations of the same person for two different purposes produce
// unlinkable tokens. Purpose limitation is not only a check at the front
// of the pipeline — it reaches into the token space itself.
//
// ── Cost when unused (GP 13) ──
//
// Nothing in this file or `CohortActivation.fs` is composed by
// `PeerServerApp.run`. A deployment that never calls
// `CohortActivation.activate` never constructs any of these types, and
// its peer substrate is byte-for-byte what it was before this phase —
// the same posture `RoundOrchestrator` takes.

/// What an activation may release. **There is deliberately no case
/// carrying the cohort's own member ids**, and that absence is the
/// structural half of the phase: a raw-list release is not refused by a
/// check, it is unrepresentable.
///
/// The three cases are not ordered by revealingness in a way the
/// substrate can exploit — `ReleaseTokens` discloses per-member
/// pseudonyms that `ReleaseCount` does not, and a destination's approval
/// names an explicit SET, never a ceiling.
type ActivationShape =
    /// The matched cohort size, as one number.
    | ReleaseCount
    /// One opaque, per-authorisation pseudonym per matched member. See
    /// `IActivationTokenMinter` for what makes a token unlinkable.
    | ReleaseTokens
    /// A bucketed distribution of the matched cohort, by caller-declared
    /// label.
    | ReleaseHistogram

/// One labelled bucket of a histogram release.
type ActivationBucket = { Label: string; Count: int }

/// What actually crossed the boundary. Value-typed and immutable (GP 5 /
/// GP 12 rule 1) — it is handed to an `IActivationDestination` that may
/// be a remote peer, a queue, or a file, so it carries no live handle.
type ActivationRelease =
    /// `ReleaseCount` — the matched cohort size, after suppression, the
    /// k-floor, and any calibrated noise.
    | ActivatedCount of matched: int
    /// `ReleaseTokens` — one token per matched member, in a canonical
    /// (ordinal) order so two runs of the same activation produce the
    /// same list and a re-delivery is idempotent.
    | ActivatedTokens of tokens: string list
    /// `ReleaseHistogram` — the released buckets, sub-threshold buckets
    /// already suppressed by the gate.
    | ActivatedHistogram of buckets: ActivationBucket list

/// One caller-declared constraint on a cohort. Opaque `(name, value)`
/// data, NOT a query language: forge has no opinion about what
/// "recency > 30d" or "value >= 500" mean, and inventing a vocabulary
/// for them would put a domain model in the substrate (GP 1). What the
/// substrate does with a predicate is hash it — so a cohort narrowed
/// from one predicate set to another is a different cohort, and its
/// approval does not carry over.
type CohortPredicate = { Name: string; Value: string }

/// The shape constraints a cohort declares about itself.
type CohortConstraints = {
    /// The smallest cohort this definition is willing to be activated
    /// at. Composed with — never able to loosen — the destination's own
    /// floor, on exactly `PrivacyGate.compose`'s rule.
    MinCohortSize: int
    /// Caller-declared value / recency / eligibility predicates. Order
    /// is irrelevant to the hash; content is not.
    Predicates: CohortPredicate list
}

/// How the cohort's member set is defined. Two cases because both are
/// real: a materialised list is the honest shape for an uploaded
/// audience, and a query expression is the honest shape for one derived
/// on demand — and an approval must be able to bind to either.
///
/// **A query's EXPRESSION is what the approval binds to, not its
/// result.** That is the only coherent reading: a result varies with the
/// data underneath it, so an approval bound to a result would either
/// expire on every insert or bind to nothing at all. The expression is
/// uninterpreted by the substrate (GP 1) and resolved by a composed
/// `ICohortResolver`.
type CohortDefinition =
    /// The literal opaque id set.
    | CohortMembers of ids: string list
    /// An uninterpreted expression a composed `ICohortResolver` turns
    /// into an id set.
    | CohortQuery of expression: string

/// A cohort: what it is called, how its members are defined, and the
/// shape it constrains itself to. **Every field is covered by the
/// authorisation hash**, which is what makes an edited cohort a cohort
/// nobody has approved.
type CohortSpec = {
    /// Stable, operator-facing id for diagnostics and audit.
    CohortId: string
    Definition: CohortDefinition
    Constraints: CohortConstraints
}

/// The declared purpose an activation is authorised FOR. Purpose
/// limitation is the control that stops an approval obtained for
/// measurement being reused for targeting, and it is enforced the only
/// way it can be enforced structurally: the purpose is inside the hash,
/// so an approval for purpose A produces a version that an activation
/// for purpose B does not match.
///
/// **The description is signed too.** It is part of what the parties
/// agreed to — a purpose whose text is rewritten after approval is a
/// different agreement wearing the same id, which is precisely the
/// substitution the content hash exists to prevent.
type ActivationPurpose = {
    /// Stable id, e.g. `"reach-measurement"`.
    PurposeId: string
    /// The agreed prose. Operator-facing; signed.
    Description: string
}

/// The approvable description of a destination — pure data, because this
/// is what the authorisation hash covers and hashing an interface's
/// properties would let an implementation answer differently on the run
/// that computed the hash and the run that used it.
type ActivationDestination = {
    /// Stable id for diagnostics, audit, and token keying.
    DestinationId: string
    /// The peer that owns this destination, and therefore the other half
    /// of the bilateral approval.
    CounterpartyPeerId: string
    /// The shapes this destination may receive. Widening the set changes
    /// the hash, so it invalidates every approval already issued — which
    /// is the correct behaviour, not an inconvenience: "you may also
    /// receive per-member tokens" is a new agreement.
    PermittedShapes: Set<ActivationShape>
    /// The privacy floor every release to this destination must clear.
    /// Composed with the cohort's own `MinCohortSize` at activation.
    Floor: PrivacyGate
}

/// One authorisation: this cohort, for this purpose, to this
/// destination. The unit the parties sign, the unit an audit row names,
/// and the unit revocation withdraws.
type ActivationAuthorisation = {
    Cohort: CohortSpec
    Purpose: ActivationPurpose
    Destination: ActivationDestination
}

/// Why an activation did not release. Failure as data (GP 12 rule 3),
/// one case per invariant so an operator reads WHICH control bound
/// without parsing prose.
type ActivationRefusal =
    /// Invariant 0 — no live bilateral approval for this exact
    /// authorisation. Covers unapproved, pending, revoked, expired, and
    /// every edit to the cohort, the purpose, or the destination, since
    /// all of them produce a version nobody has approved. The reason is
    /// audit-facing and is never sent to a counterparty.
    | ActivationUnauthorised of reason: string
    /// The cohort's definition could not be turned into an id set.
    | ActivationCohortUnresolved of reason: string
    /// The private match failed on the wire or in the protocol.
    | ActivationMatchFailed of error: PsiError
    /// Invariants 0.5–4 — the clean-room gate withheld the answer: a
    /// sub-floor cohort, an exhausted budget, a shape off the
    /// destination's approved surface, or an uncheckable answer. Carries
    /// the template id and nothing else, on exactly the
    /// `PeerCleanRoomWithheld` argument: a quantitative refusal a caller
    /// can vary its request against is a counting oracle.
    | ActivationWithheld of templateId: string
    /// Invariant 5 — the egress projection refused. The released answer
    /// cleared the floor but could not be projected into the requested
    /// shape without disclosing something the shape does not carry.
    | ActivationEgressRefused of reason: string
    /// The destination itself refused or failed the delivery. The
    /// release had already cleared every invariant — this is a transport
    /// or downstream failure, not a governance decision.
    | ActivationDeliveryFailed of reason: string

/// Where a cleared release goes. The delivery seam, separate from the
/// `ActivationDestination` descriptor it carries, so the approvable
/// description stays pure data while the act of delivering stays a
/// substitutable implementation (GP 1 — a deployment shipping an
/// in-house destination should not have to fork the type its approval
/// hashes over).
///
/// `Deliver` is reached ONLY from `CohortActivation.activate`, after
/// every invariant has passed. Satisfies GP 12: identity by value
/// (rule 1), async at the boundary (rule 2), failure as data (rule 3),
/// and stateless between calls (rule 4) — the authorisation id is passed
/// per delivery rather than held from a previous one.
type IActivationDestination =
    /// The approvable description. Read at activation time and hashed,
    /// so an implementation that varied it between calls would simply
    /// stop matching its own approvals.
    abstract Descriptor: ActivationDestination

    /// Accept a cleared release under a named authorisation. The
    /// authorisation id is the join key between this delivery and both
    /// parties' signed approval records.
    abstract Deliver: authorisationId: string * release: ActivationRelease -> Async<Result<unit, string>>

/// Turns a `CohortDefinition` into an id set. A seam because a query
/// expression is the deployment's language, never forge's (GP 1).
type ICohortResolver =
    /// `Error` carries an operator-facing reason; it is recorded
    /// receiver-side and never sent to a counterparty.
    abstract Resolve: definition: CohortDefinition -> Async<Result<string list, string>>

/// The private match, split by what it reveals.
///
/// **The split is the design, not an API convenience.** `MatchSize` is
/// the Phase 479 `CardinalityOnly` preflight — it learns the overlap
/// SIZE and no membership — and it is what the clean-room floor binds
/// on. `MatchMembers` is a `Members` run, which reveals membership, and
/// it is performed only for a `ReleaseTokens` activation and only AFTER
/// the preflight's size has cleared the floor. A sub-floor cohort
/// therefore never causes a membership-revealing exchange at all, which
/// is a stronger statement than withholding its answer afterwards.
type IActivationMatcher =
    /// The `CardinalityOnly` preflight: `|cohort ∩ destination|`.
    abstract MatchSize: cohort: string list * destination: ActivationDestination -> Async<Result<int, PsiError>>

    /// The `Members` run, in the initiator's own pre-images. Called only
    /// after `MatchSize` cleared the floor, and only for a token
    /// release.
    abstract MatchMembers:
        cohort: string list * destination: ActivationDestination -> Async<Result<string list, PsiError>>

/// Mints and verifies the opaque per-member tokens a `ReleaseTokens`
/// activation carries.
///
/// A seam on the same argument `ITemplateApprovalSigner` is one: a
/// deployment whose key custody is an HSM or a KMS substitutes an
/// implementation without touching the pipeline. Async at every boundary
/// and stateless between calls (GP 12 rules 2 + 4) — the shipped default
/// reads key material per call, so a rotation takes effect immediately.
type IActivationTokenMinter =
    /// The token for `memberId` under `authorisationId`. Deterministic
    /// for a given pair, so a re-delivery of the same activation is
    /// idempotent rather than a second, differently-named audience.
    abstract Mint: authorisationId: string * memberId: string -> Async<Result<string, string>>

    /// Whether `token` is the token for `memberId` under
    /// `authorisationId`. The destination's redemption path; a
    /// constant-time comparison, so a redemption endpoint is not a
    /// timing oracle over the token space.
    abstract Verify: authorisationId: string * memberId: string * token: string -> Async<Result<bool, string>>

/// Stable wire names + the `OutputShape` each release shape
/// materialises as.
[<RequireQualifiedAccess>]
module ActivationShape =

    /// The stable name of a release shape. Written out rather than
    /// derived from the DU case name, on exactly
    /// `TemplateCanonical.shapeName`'s argument: renaming a case in F#
    /// source must not silently invalidate every signed authorisation in
    /// a federation.
    let name (shape: ActivationShape) : string =
        match shape with
        | ReleaseCount -> "ReleaseCount"
        | ReleaseTokens -> "ReleaseTokens"
        | ReleaseHistogram -> "ReleaseHistogram"

    /// Parse a stable name back. `None` for anything unrecognised — a
    /// destination approved for a shape this SDK does not know is a
    /// destination this SDK does not release to.
    let tryParse (value: string) : ActivationShape option =
        match value with
        | "ReleaseCount" -> Some ReleaseCount
        | "ReleaseTokens" -> Some ReleaseTokens
        | "ReleaseHistogram" -> Some ReleaseHistogram
        | _ -> None

    /// The gate-checkable `OutputShape` a release shape's underlying
    /// answer takes. A token release is metered as a `Count`, because
    /// the number the floor binds on is the cohort size — the tokens are
    /// a projection of it, decided at invariant 5.
    let outputShape (shape: ActivationShape) : OutputShape =
        match shape with
        | ReleaseCount
        | ReleaseTokens -> Count
        | ReleaseHistogram -> Histogram

/// The canonical, length-prefixed encoding of an activation
/// authorisation, and the `CleanRoomTemplate` derived from it.
///
/// **This is the whole of the binding between an approval and the
/// activation it approves** — read the file header before changing
/// anything here, because a change to the encoding invalidates every
/// authorisation already signed across a federation. The construction is
/// `TemplateCanonical`'s, deliberately: same length-prefixed field
/// discipline, same count-first ordinal set ordering, same domain
/// separator convention, so a reader who has understood one has
/// understood both.
[<RequireQualifiedAccess>]
module ActivationCanonical =

    /// Domain separator, so an activation encoding can never be confused
    /// for a template or an approval-record encoding even if their field
    /// sequences coincided.
    ///
    /// **Phase 654 — taken from `SignedShape`, not written out here.** A
    /// `let` rather than a `[<Literal>]` because a literal cannot hold a
    /// function call; the value is used only as a string, so nothing
    /// downstream needed a constant.
    let domain = SignedShape.separator SignedShape.ActivationAuthorisation

    /// The reserved prefix of a derived template's id, namespaced
    /// beneath `_platform` on the convention `PeerAudit.contractId` and
    /// `TemplateApprovalContract.contractId` already follow — an author
    /// cannot collide with it, and the id is a well-formed identifier so
    /// it survives `IdentitySanitiser.sanitiseScopeId` on its way to
    /// becoming an approval-store folder name.
    [<Literal>]
    let templateIdPrefix = "_platform.activation."

    /// Emit one field as `{utf8ByteLength}:{value}` and a newline. The
    /// length prefix is what makes the encoding injection-proof: a
    /// cohort id or a predicate value containing the delimiter cannot
    /// shift a field boundary, because the reader is told how many bytes
    /// the value occupies before it starts.
    let private field (sb: StringBuilder) (value: string) : unit =
        sb.Append(Encoding.UTF8.GetByteCount value).Append(':').Append(value).Append('\n')
        |> ignore

    /// Emit a sequence count-first in ordinal order, so an
    /// authorisation's hash does not depend on the order an author wrote
    /// its members in — nor on the machine's culture, which is why the
    /// comparison is `String.CompareOrdinal` and not the ambient one.
    let private sortedSeq (sb: StringBuilder) (values: string seq) : unit =
        let ordered =
            values |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> List.ofSeq

        field sb (string (List.length ordered))
        ordered |> List.iter (field sb)

    /// The canonical UTF-8 encoding of an authorisation. Every field of
    /// the cohort, the purpose and the destination is covered — that is
    /// the mutation defence, and it is a property of this function
    /// rather than a rule enforced elsewhere.
    let bytes (auth: ActivationAuthorisation) : byte[] =
        let sb = StringBuilder()
        field sb domain

        // Destination.
        field sb auth.Destination.DestinationId
        field sb auth.Destination.CounterpartyPeerId
        sortedSeq sb (auth.Destination.PermittedShapes |> Seq.map ActivationShape.name)
        field sb (string auth.Destination.Floor.MinCohortSize)
        field sb (string auth.Destination.Floor.SuppressionThreshold)
        sortedSeq sb (auth.Destination.Floor.PermittedShapes |> Seq.map TemplateCanonical.shapeName)

        // Purpose — id AND prose, per the type's own doc comment.
        field sb auth.Purpose.PurposeId
        field sb auth.Purpose.Description

        // Cohort.
        field sb auth.Cohort.CohortId

        match auth.Cohort.Definition with
        | CohortMembers ids ->
            field sb "members"
            sortedSeq sb ids
        | CohortQuery expression ->
            field sb "query"
            field sb expression

        field sb (string auth.Cohort.Constraints.MinCohortSize)

        // Predicates: count-first, ordered by the encoded pair so the
        // ordering is over the same bytes the hash covers rather than
        // over a tuple whose comparison is F#'s and not the encoding's.
        let predicates =
            auth.Cohort.Constraints.Predicates
            |> List.map (fun p ->
                let inner = StringBuilder()
                field inner p.Name
                field inner p.Value
                inner.ToString())

        sortedSeq sb predicates

        Encoding.UTF8.GetBytes(sb.ToString())

    /// Lowercase hex SHA-256 — the estate's existing digest
    /// presentation.
    let private sha256Hex (value: byte[]) : string =
        SHA256.HashData value |> Convert.ToHexString |> _.ToLowerInvariant()

    /// The raw digest of an authorisation's canonical encoding. Exposed
    /// because it is the message a token is derived under: a token keyed
    /// by the whole authorisation is unlinkable across purposes and
    /// destinations by construction.
    let digest (auth: ActivationAuthorisation) : byte[] = SHA256.HashData(bytes auth)

    /// The authorisation's version: `sha256:{lowercase hex}`. The
    /// algorithm is named in the value so a future digest change is a
    /// visible discontinuity rather than a silent one.
    let version (auth: ActivationAuthorisation) : string = $"sha256:{sha256Hex (bytes auth)}"

    /// The derived template's id. Deterministic, well-formed, and
    /// carrying the whole authorisation digest — which is what makes
    /// Phase 480's content-hash approval bind to the cohort, the purpose
    /// and the destination without Phase 480 knowing any of them exist.
    let id (auth: ActivationAuthorisation) : string =
        $"{templateIdPrefix}{sha256Hex (bytes auth)}"

    /// The `CleanRoomTemplate` an activation authorisation approves as.
    ///
    /// Three fields, each load-bearing:
    ///
    ///   * `TemplateId` carries the authorisation digest, so
    ///     `TemplateCanonical.version` over this template transitively
    ///     covers every field of the authorisation.
    ///   * `AllowedMethods` is the destination's permitted SHAPES, so
    ///     the gate's invariant 1 refuses a shape off the approved
    ///     surface before the handler computes anything.
    ///   * `Floor` is the destination's floor composed with the cohort's
    ///     own `MinCohortSize` — `PrivacyGate.compose`, so the pair can
    ///     only ever tighten. A cohort declaring a floor of 500 into a
    ///     destination whose floor is 50 is activated at 500.
    let template (auth: ActivationAuthorisation) : CleanRoomTemplate = {
        TemplateId = id auth
        AllowedMethods = auth.Destination.PermittedShapes |> Set.map ActivationShape.name
        Floor =
            PrivacyGate.compose auth.Destination.Floor {
                auth.Destination.Floor with
                    MinCohortSize = auth.Cohort.Constraints.MinCohortSize
            }
    }