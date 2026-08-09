// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open ToolUp.Platform

// ─── Phase 644 — registry lifecycle transitions over the peer seam ───
//
// Phase 606's registry surface is QUERY-shaped: a modeller across the
// seam can read what a data host has registered and can read nothing
// into it. That is the right default and it forecloses a topology the
// federated profile exists to serve — the one where the modelling
// judgment and the durable record live in different deployments. A
// consortium's analyst approves a model; the consortium's data host is
// where "approved" has to mean something.
//
// This file is the wire half of that: the invocation a peer sends, the
// attributed record it gets back, and the per-peer GRANT that decides
// whether it may send one at all. The judgment itself is not here — it
// is `ModelTransition.invoke`, the author-agnostic seam in
// `ToolUp.Platform`, which a local admin screen and (Phase 645) a
// promotion policy reach through the same door. A peer invocation is
// not a second state machine; it is a third author.
//
// ── Two orthogonal declarations, deliberately not folded into one ────
//
// Phase 642's `PeerDataVisibilityLevel` governs what a peer may SEE.
// This governs what it may DO. They are separate because they answer
// separate questions and a deployment will routinely want them at
// different settings: a consortium partner that may approve models but
// must never see a row is an entirely ordinary arrangement, and folding
// the two into one ladder would make it inexpressible. A transition
// therefore requires only `AggregatesOnly` on the visibility axis — it
// carries no data — and requires an explicit grant on this one.
//
// ── Fail closed, and the default is not "the obvious transitions" ────
//
// An undeclared grant admits NOTHING, not "the harmless ones". There is
// no such thing as a harmless lifecycle transition across a trust
// boundary: `Retired` withdraws an evidence base other work may be
// citing, and `Approved` is the whole governance gate. A deployment
// that upgrades into this phase and declares nothing therefore refuses
// every peer invocation, which is exactly the behaviour it had before
// the operation existed (GP 11).
//
// ── Cost when unused (GP 13) ─────────────────────────────────────────
//
// Nothing here is composed. A deployment that declares no transition
// grant and supplies no `ModelTransitionDeps` admits no transition
// operation, constructs none of these types, and schedules no job.

// ─── Wire shapes (FEDERATION_WIRE.md §5.7.11) ────────────────────────

/// A requested lifecycle transition, as it crosses the seam.
///
/// **No scope member**, for the reason nothing on this profile has one:
/// the peer binding decides the scope, so an invocation cannot address
/// another tenant's artifacts by construction (GP 4). **No role member**
/// either, and that absence is the sharper one: a peer's actor holds no
/// membership in the receiving deployment's teams, so a role it asserted
/// would be a claim about somebody else's directory. The grant on the
/// binding is what decides, and the receiver owns it.
type PeerTransitionInvocation = {
    /// The artifact's composite-key hash — the id every registry read on
    /// this profile already keys on.
    ArtifactKey: string
    /// The status the artifact is asked to enter, as the stable
    /// `ModelArtifactStatus` label (`"Draft"` / `"Fitted"` /
    /// `"Approved"` / `"Retired"`).
    ///
    /// A string rather than the DU, like every other member on this
    /// contract: the peer wire is language-neutral JSON-RPC and the
    /// caller is very often not an F# deployment. A label naming no
    /// status this profile defines is not a judgment at all, so it is
    /// refused as an unreadable request rather than as an illegal edge —
    /// "that is not a state" and "that is not an edge" are different
    /// facts.
    Target: string
    /// Who, at the CALLING deployment, is asking. The peer's own claim
    /// about its own user, recorded verbatim.
    ///
    /// **The receiver treats it as a claim and never as an authority.**
    /// It is not cross-checked against anything — the receiver has no
    /// view of a counterparty's directory and inventing one would be
    /// worse than useless — and it decides nothing: the grant on the
    /// peer's binding is what admits or refuses. What the field buys is
    /// attribution. "Peer `consortium-north` approved this artifact" is
    /// a weaker record than "peer `consortium-north` approved it, and
    /// says `r.okafor` asked", and the second is the one an
    /// investigation can act on. Same posture as
    /// `ModelExecutionPeerSubmission.SubmitterClass`.
    ActorId: string
    /// The author's stated reason. `null` asserts nothing and is
    /// ordinary — a rationale is evidence, not a gate.
    Rationale: string option
}

/// The attributed record an admitted transition produced.
///
/// Every member is metadata about a state change this deployment has
/// already committed. There is no member an artifact's parameters,
/// diagnostics or any dataset row could ride in — the same argument
/// §5.7.4 makes for the outcome table, which is why a transition needs
/// no visibility level above the floor.
type PeerTransitionRecord = {
    ArtifactKey: string
    /// The status the artifact held. Echoed rather than assumed: a
    /// modeller that invoked on a stale read learns what the artifact
    /// actually moved from.
    FromStatus: string
    ToStatus: string
    /// Where the invocation entered the data host: `"peer"` for
    /// everything on this seam. Carried anyway, and that is not
    /// redundancy — the record a modeller holds and the record the data
    /// host's own trail holds are then the same document, so a
    /// reconciliation between the two sides compares fields rather than
    /// re-deriving one from the transport it happened to arrive on.
    Channel: string
    /// `"peer"` on this seam, for the same reason.
    AuthorKind: string
    /// `{peerId}/{actorId}` — both identities, because either alone is
    /// ambiguous across a federation.
    AuthorId: string
    Rationale: string option
    RecordedAt: DateTimeOffset
    /// The artifact version the transition minted (GP 5 — a transition
    /// appends a version, never mutates one).
    Version: int
}

[<RequireQualifiedAccess>]
module PeerTransition =

    /// The stable refusal classes (§7.2) of the transition family.
    ///
    /// Held here rather than derived at the seam so the three strings a
    /// corpus vector and a refusal log carry live beside the shapes they
    /// describe, and so the profile's `className` cannot spell one of
    /// them differently from the specification table.
    [<Literal>]
    let UnknownArtifactClass = "model-execution-transition-unknown-artifact"

    [<Literal>]
    let InvalidTransitionClass = "model-execution-transition-invalid"

    [<Literal>]
    let InsufficientAuthorityClass = "model-execution-transition-authority"

    /// The refusal class of one seam judgment.
    let className (refusal: ModelTransitionRefusal) : string =
        match refusal with
        | ModelTransitionRefusal.UnknownArtifact _ -> UnknownArtifactClass
        | ModelTransitionRefusal.InvalidTransition _ -> InvalidTransitionClass
        | ModelTransitionRefusal.InsufficientAuthority _ -> InsufficientAuthorityClass

    /// Read the invocation's target label.
    ///
    /// `None` for a label naming no status this profile defines,
    /// including `null` and the empty string. The caller refuses those
    /// as an unreadable request rather than as an illegal transition:
    /// the lifecycle graph has nothing to say about a word that names no
    /// state, and reporting one as a forbidden edge would tell a caller
    /// to try a different edge when what it needs is a different word.
    let target (invocation: PeerTransitionInvocation) : ModelArtifactStatus option =
        if isNull (box invocation.Target) then
            None
        else
            ModelArtifactStatus.parse invocation.Target

    /// Project a validated invocation onto the author-agnostic seam's
    /// own request shape, stamping the PEER as the author.
    ///
    /// The author is built from the binding's peer id and the wire's
    /// actor claim — never from the wire alone — so a caller cannot
    /// author a transition as somebody else by editing a document.
    let toRequest
        (peerId: string)
        (target: ModelArtifactStatus)
        (invocation: PeerTransitionInvocation)
        : ModelTransitionRequest =
        {
            ArtifactKey = invocation.ArtifactKey
            Target = target
            Author = PeerActor(peerId, invocation.ActorId)
            Rationale =
                match invocation.Rationale with
                | Some rationale when not (String.IsNullOrWhiteSpace rationale) -> Some rationale
                | _ -> None
        }

    /// The recorded transition, projected onto the profile's shape.
    let toWireRecord (record: ModelTransitionRecord) : PeerTransitionRecord = {
        ArtifactKey = record.ArtifactKey
        FromStatus = record.FromStatus
        ToStatus = record.ToStatus
        Channel = record.Channel
        AuthorKind = record.AuthorKind
        AuthorId = record.AuthorId
        Rationale = record.Rationale
        RecordedAt = record.RecordedAt
        Version = record.Version
    }

    /// **The fail-closed read of a counterparty's declared transition
    /// grant**, and the only route any consumer should take to it.
    ///
    /// A label that omits the member (every label published before this
    /// phase), carries `null`, or names a status this build does not
    /// know all read as a grant of NOTHING or a grant without that
    /// entry. Each arm is the same claim from a different direction:
    /// silence is not a grant, and a word this deployment cannot enforce
    /// is not one either.
    ///
    /// The result is ordinally sorted, so two readers of one label agree
    /// on the bytes as well as on the set.
    let readDeclaration (declared: string list) : string list =
        if isNull (box declared) then
            []
        else
            declared
            |> List.filter (fun label -> not (isNull (box label)) && (ModelArtifactStatus.parse label).IsSome)
            |> List.distinct
            |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))