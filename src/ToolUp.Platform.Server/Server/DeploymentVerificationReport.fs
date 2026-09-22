// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Security.Cryptography
open System.Text
open Microsoft.AspNetCore.Http
open ToolUp.Platform.DeploymentVerification

// ─── Phase 686 — composing the verifiers into one report ─────────────
//
// Five at Phase 686; six since Phase 693 added module seam authority;
// eight since Phase 699 added declared-configuration conformance and the
// accepted-acknowledgement record. The section id set was declared open
// for exactly this — a literal per section rather than a closed union —
// so each addition is an addition rather than a break, and this header
// counts them nowhere else.
//
// The verifiers this gathers already exist and are already tested. This
// file mints NO verification logic: it takes each substrate's own verdict
// and maps it onto the typed section verdict declared in
// `Platform.Core`. A section's verdict is therefore that substrate's
// verdict, re-labelled — never a second opinion about it.
//
// **Every section's evidence arrives through one seam, and none of it
// arrives by package reference (GP 1).** The audit ledger lives in
// `ToolUp.AuditSinks.ChainedLedger`, the certificate issuance log in
// `ToolUp.Facts.Server`, the answer-verification join in
// `ToolUp.AI.Server` — all DOWNSTREAM of this assembly. A reference to
// any of them would invert the dependency graph and nail every
// deployment composing this report to those three choices, including
// deployments that compose none of them. Phase 685 settled the shape for
// exactly this reason ("tamper evidence enters as a function, not a
// package reference"): the composition root, the only place holding all
// the pieces, hands over a thunk per section.
//
// The two IN-tier sections — the boot verdict and the grounding-envelope
// continuity walk — arrive the same way, and that uniformity is a
// deliberate second decision rather than a consequence of the first.
// Both of their substrates compile AFTER the route-handler table that
// mounts this report, so neither could be reached from here by reference
// even though both live in this assembly. Rather than special-casing
// them into a different shape, every source is mirrored into a
// tier-neutral record and supplied together — the shape Phase 693's
// seam-authority member follows too, and for the same reason: the seam
// gate, the composition profile and the refusal union all compile after
// this file as well. `ServerApp.withDeploymentVerificationEvidence` —
// which compiles late enough to see the grounding mutator — derives the
// grounding thunk from the container itself, so a deployment that
// composed Phase 684 gets that section without wiring it by hand.
//
// **Why the boot verdict is carried rather than resolved.** Phase 657
// deliberately registers nothing: `ServerApp.verifyComposition` returns a
// value the composition root holds, with no DI singleton and no
// `ServerConfig` field, so a later reader cannot mistake a stale copy for
// the live verdict. That decision stands; the root passes what it holds,
// and a root that never ran the check passes nothing.

// ─── Tier-neutral evidence shapes ────────────────────────────────────
//
// Each mirrors one substrate's own verdict union. They are mirrors, not
// re-derivations: the mapping from the source verdict lives in the
// adapter beside that substrate, and what these add is only the
// discrimination the SECTION verdict needs and the source union does not
// happen to draw.

/// The boot verification verdict (Phase 657), mirrored.
///
/// Four cases where the source has five: `VerificationFailed` and
/// `Drifted` both mean the preflight ran and its answer was adverse, and
/// the report treats them identically — the distinction between them is
/// carried in `detail` and `findings`, which is where an operator reads
/// it. `Unsealed` stays separate because it is NOT adverse.
///
/// **Phase 694's `VerifiedUnrecorded` gets its own case rather than
/// folding into `BootSealVerified`.** That fold is the exact confusion
/// the source verdict was split to prevent: a binding sealed before the
/// manifest recorded canonical-method selectors cannot speak to them, and
/// a report that rendered "matched everything it recorded" as a plain
/// verification would restate the blind spot one tier up.
type BootSealIntegrity =
    /// The running composition is the sealed one.
    | BootSealVerified of profile: string * policy: string * detail: string
    /// The running composition matches everything the sealed binding
    /// recorded, and the binding predates one or more of the declarations
    /// the preflight now compares. Not adverse — an old binding is not a
    /// drifted deployment — and not a verification either. `unrecorded`
    /// names each declaration that could not be compared.
    | BootSealVerifiedUnrecorded of profile: string * policy: string * detail: string * unrecorded: string list
    /// The preflight ran and had nothing to compare against — no sealed
    /// deploy record, or a record with no composition binding. Honest,
    /// legitimate, and emphatically not a verification.
    | BootSealUnsealed of profile: string * policy: string * reason: string
    /// The preflight ran and its answer was adverse: the seal did not
    /// verify, or the running composition drifted from the sealed one.
    | BootSealRejected of profile: string * policy: string * detail: string * findings: string list * refusedStart: bool

/// The grounding-envelope continuity walk (Phase 684), mirrored.
type GroundingContinuityIntegrity =
    /// `boot seal + recorded chain ⇒ live envelope` holds. `declarations`
    /// is the size of the live envelope — load-bearing, because
    /// continuity over an envelope declaring NOTHING holds trivially and
    /// proves only that the deployment has nothing that could drift.
    /// Phase 684's own header draws that distinction and this keeps it.
    | GroundingContinuous of seal: string * declarations: int * steps: int * digest: string
    /// The walk stopped agreeing with the evidence. `detail` is the
    /// source verdict's own account, which names the position.
    | GroundingDiverged of seal: string * declarations: int * detail: string

/// The hash-chained audit ledger walk (Phase 658 / 682), mirrored.
///
/// The one place this adds a distinction the source does not draw: an
/// untrusted head splits into one that was REJECTED (a signature present
/// and not valid, a head pointer disagreeing with the chain — a finding)
/// and one that could not be JUDGED (signed with no verifier supplied,
/// head pointer missing — an incomplete read). Folding those together
/// would let a deployment silence a bad signature by withholding the
/// verifier, which is the cheapest possible attack on this section.
type LedgerIntegrity =
    /// The chain walked clean and the head is trusted: either
    /// signed-and-valid, or honestly unsigned.
    | LedgerChainVerified of records: int64 * headDigest: string * signature: string
    /// The chain walked clean and the head is not trustworthy.
    | LedgerHeadRejected of records: int64 * headDigest: string * detail: string
    /// The chain walked clean and the head's trust could not be
    /// established at all.
    | LedgerHeadUnverifiable of records: int64 * headDigest: string * reason: string
    /// The walk found a break. `position` is the FIRST one; everything
    /// after it is meaningless, which is why only the first is carried.
    | LedgerChainBroken of position: int64 * kind: string * detail: string

/// What the certificate issuance log (Phase 565 / 685) reported.
type CertificateIssuanceIntegrity = {
    /// How many issuances the log enumerates for the scope read.
    Issued: int
    /// Identifiers for the most recent issuances, newest first. Digests
    /// and subjects only — never a certificate body, which the log
    /// deliberately does not hold.
    Recent: string list
    /// Whether the log ran an integrity gate over its own backing trail
    /// before enumerating.
    ///
    /// **Load-bearing, and the reason this is not a bare count.** The
    /// plain audit-trail log claims no integrity and always succeeds;
    /// only the integrity-gated constructor refuses to enumerate over a
    /// broken chain. Enumeration WITHOUT that gate is an observation — a
    /// list the deployment asserts about itself — and calling it a
    /// verification would credit the deployment for a property nobody
    /// checked.
    LogIntegrityChecked: bool
}

/// What the answer-verification provenance join (Phase 680) reported.
///
/// The check is the one Phase 680 made possible: each recorded row
/// carries both the fact ids the answer's verified figures cite AND the
/// provenance head derived from them, so the head recomputes from the ids
/// on the same row. A row where it does not is a row whose join was
/// written by something other than the code that derives it.
type AnswerJoinIntegrity = {
    /// Answer-verification rows read.
    Rows: int
    /// Rows whose recorded provenance head recomputes from their own
    /// cited fact ids.
    Rejoined: int
    /// Rows whose recorded head does not recompute, each described.
    Mismatched: string list
    /// Rows naming no provenance head because the answer verified against
    /// no fact. Honest and expected — a digest over nothing would look
    /// like a chain head and name no chain — so these are reported, never
    /// counted as failures.
    Unanchored: int
}

/// Whether this deployment's composition was actually routed through the
/// seam-authority gate, and what the gate said (Phase 688 / 691).
///
/// **Three cases, and the first one is the whole point of the section.**
/// Phase 691 shipped the gate's production call site, but calling it is
/// per-deployment (GP 13): `SeamAuthorityEnforcement.verify` is a
/// function a composition root invokes, not a hosted service that runs
/// itself. So "the SDK has enforcement" and "this deployment enforces"
/// are different facts, and a report that rendered the first as the
/// second would be asserting a bound nobody applied. `Unenforced` is
/// what a deployment that declared grants and never checked them looks
/// like — declarations, not a bound.
type SeamAuthorityVerification =
    /// No composition in this deployment routed its modules through the
    /// gate. Whatever was declared bounds nothing here.
    | SeamAuthorityUnenforced
    /// The check ran over `components` composed component(s) and admitted
    /// all `seams` derived reach(es). Affirmative ONLY when something was
    /// declared — an admission over an all-unrestricted signature is the
    /// additive floor and the gatherer reads it as such.
    | SeamAuthorityAdmitted of components: int * seams: int
    /// The check ran and refused: a profile that could not be bound, or a
    /// component reaching past its declaration. `detail` is the refusal's
    /// own account and `findings` enumerates it.
    | SeamAuthorityRefused of detail: string * findings: string list

/// One composed unit's declared outbound authority beside the reach its
/// own registrations imply.
///
/// Field names carry the `Authority` / `Declared` / `Derived` prefixes
/// for the Phase 431 field-inference reason recorded on
/// `AuthorizationSurface.Exposed`: F#'s last-declared-wins inference
/// re-points every unannotated construction sharing a full field-name set
/// at whichever record compiled later.
///
/// **Both halves are carried in their own types rather than as strings.**
/// `DeclaredGrant` keeps the `UnrestrictedSeams` / `DeclaredSeams
/// Set.empty` distinction that a name-and-list projection loses — the
/// difference between "reaches everything" and "reaches nothing", which
/// is the security property Phase 688 exists to make expressible.
type ComponentSeamAuthority = {
    /// The component's stable Phase 279 id — the key the grant signature,
    /// the capability signature and the Phase 438 surface all share.
    AuthorityComponent: ComponentId
    /// What it declared it may reach. `UnrestrictedSeams` for a component
    /// absent from the signature (GP 11 — absence is the no-op).
    DeclaredGrant: SeamGrant
    /// The substrate seams its registrations imply, from the Phase
    /// 438/554 `Needs` projection. Empty means the projection derived
    /// nothing from what it can see — never that the component reaches
    /// nothing (see the section's not-proved statement).
    DerivedReach: SeamId list
    /// Whether the component is composed as a module in this deployment.
    /// `false` is a declaration with nothing behind it — reported rather
    /// than dropped, because a grant naming a component that left the
    /// composition is stale review surface that still reads as governance.
    ComposedHere: bool
}

/// The seam-authority posture (Phase 688 / 691), mirrored.
///
/// Tier-neutral for the same reason the boot verdict and the continuity
/// walk are: `CompositionProfile`, `SeamAuthorityRefusal` and
/// `CapabilityDenial` all compile AFTER the route-handler table that
/// mounts this report, even though all three live in this assembly.
/// `ComponentId` / `SeamId` / `SeamGrant` are `Platform.Core` value types
/// and cross that boundary freely, so only the Server-tier verdicts are
/// flattened.
type SeamAuthorityIntegrity = {
    /// The composition profile this deployment declared —
    /// `CompositionProfile.label`, so `"standard"` or `"verified"`.
    Profile: string
    /// Whether declaring a reachable-seam set is MANDATORY under that
    /// profile rather than advisory. Carried as its own field, not
    /// re-derived from `Profile`, because `CompositionProfile` already
    /// keeps `requiresSeamGrants` a separate predicate from the profile
    /// label for exactly this reason: a reader deciding what is demanded
    /// should not have to know the two move together today.
    DeclarationMandatory: bool
    /// Every composed component's declared grant and derived reach, plus
    /// any grant declared for a component this deployment does not
    /// compose. Deterministic order.
    Components: ComponentSeamAuthority list
    /// What the gate said, if this deployment asked it anything.
    Verification: SeamAuthorityVerification
}

/// The deployment-supplied evidence this report composes.
///
/// Every member is optional and absence is honest throughout: a
/// deployment supplying none of it gets a report whose every section
/// reads `NotComposed`, which is exactly what it should get.
///
/// The three downstream members are thunks because reading them is I/O
/// and a report that is never run should not pay for it; the two in-tier
/// members are values because they are already in hand by the time the
/// evidence is built. GP 12 rule 1 — identity by value throughout: every
/// member is a record or a string, never a live handle into the tier
/// that produced it.
type IDeploymentVerificationEvidence =
    /// The boot verification verdict the composition root holds, mapped
    /// from `ServerApp.verifyComposition`. `None` when the deployment
    /// never ran the boot check.
    abstract BootSeal: BootSealIntegrity option

    /// The grounding-envelope continuity walk. `None` when no
    /// grounding-envelope seal is composed. Normally derived from the
    /// container by `ServerApp.withDeploymentVerificationEvidence` rather
    /// than supplied by hand.
    abstract GroundingContinuity: GroundingContinuityIntegrity option

    /// Walk the hash-chained audit ledger. `None` when no chained ledger
    /// is composed. `Error` means the ledger could not be READ at all —
    /// distinct from a walk that found a break, which arrives as
    /// `Ok (LedgerChainBroken …)`.
    abstract Ledger: (unit -> Async<Result<LedgerIntegrity, string>>) option

    /// Enumerate the certificate issuance log. `None` when no certificate
    /// substrate is composed. `Error` carries the integrity gate's
    /// refusal, which is never to be read as "issued nothing".
    abstract Certificates: (unit -> Async<Result<CertificateIssuanceIntegrity, string>>) option

    /// Re-join the answer-verification rows against the provenance they
    /// name. `None` when no answer-verification audit join is composed.
    abstract AnswerJoins: (unit -> Async<Result<AnswerJoinIntegrity, string>>) option

/// Phase 693 — the sixth section's source.
///
/// **A sibling interface rather than a sixth member on
/// `IDeploymentVerificationEvidence`, and that is the rule this estate
/// already recorded rather than a compatibility dodge.** Adding an
/// abstract member to a shipped F# interface is a source break — F#
/// cannot author a default implementation, so every hand-written object
/// expression stops compiling — which is exactly why Phase 688 made
/// `ISeamAuthorityGate` inherit `ICompositionCapabilityGate` instead of
/// growing it. Here the same reasoning lands on a standalone sibling:
/// the report resolves it by type test, so an evidence value that never
/// heard of seam authority still compiles and its sixth section reads
/// `NotComposed` — which is the honest verdict for a deployment that
/// composed nothing to say (GP 11).
///
/// A value, not a thunk: the enforcement result is in the composition
/// root's hand at boot, the same way the boot verdict is, and re-running
/// the gate at report time would answer "would be admitted NOW" rather
/// than "was admitted at composition" — a different and much weaker
/// claim.
type ISeamAuthorityEvidence =
    /// The composition's seam-authority posture. `None` when this
    /// deployment neither declared grants nor ran the check.
    abstract SeamAuthority: SeamAuthorityIntegrity option

/// Phase 713 — the ninth section's source.
///
/// A second standalone sibling rather than a member on either interface
/// above, for the reason Phase 693 recorded when it cut the first: an
/// abstract member added to a shipped F# interface is a source break,
/// because F# cannot author a default implementation and every
/// hand-written object expression stops compiling. The report resolves
/// this one by type test too, so an evidence value that never heard of
/// the evidence chain still compiles and its ninth section reads
/// `NotComposed` — the honest verdict for a deployment that composed
/// nothing to say (GP 11).
///
/// A THUNK rather than a value, unlike the seam-authority member beside
/// it, and the difference is not arbitrary. The seam gate's result is in
/// the composition root's hand at boot and re-running it would answer a
/// weaker question. A chain walk is the opposite: it reads live
/// substrate, so a chain captured at boot would be a snapshot of an
/// evidence posture that has moved, and the report would quote it as
/// current.
/// Phase 785 — the remoting-decoder facet, mirrored.
///
/// Tier-neutral for the reason the seam-authority posture is:
/// `CompositionProfile` and `RemotingDecoderFacet` both compile AFTER
/// the route-handler table that mounts this report, even though both
/// live in this assembly.
type RemotingDecoderRecord = {
    /// The API record's name, as the composition root declared it.
    RecordApiRecord: string
    /// `"algebra"` or `"reflection"`.
    RecordClassification: string
    /// The wire types this record carries with no registered decoder.
    /// Empty exactly when the classification is `"algebra"`.
    RecordUncovered: string list
    /// Whether the wire corpus draws the shapes this record carries.
    /// Carried separately from the classification because the two
    /// together are what the section's `Verified` verdict needs, and
    /// either alone is a claim a deployment makes about itself.
    RecordCorpusCovered: bool
}

/// The remoting decode edge's posture (Phase 785), mirrored.
type RemotingDecoderIntegrity = {
    /// `CompositionProfile.label` — `"standard"` or `"verified"`.
    DecoderProfile: string
    /// Whether a registered algebra decoder is MANDATORY under that
    /// profile rather than advisory.
    DecoderMandatory: bool
    /// One entry per declared API record, in declaration order.
    DecoderRecords: RemotingDecoderRecord list
}

/// Phase 785 — the tenth section's source.
///
/// A third standalone sibling interface rather than a member on any of
/// the three above, for the reason Phase 693 recorded when it cut the
/// first: an abstract member added to a shipped F# interface is a source
/// break, because F# cannot author a default implementation and every
/// hand-written object expression stops compiling. The report resolves
/// this one by type test too, so an evidence value that never heard of
/// the decode edge still compiles and its tenth section reads
/// `NotComposed` (GP 11).
///
/// A VALUE rather than a thunk, like the seam-authority member and
/// unlike the chain walk. The facet is a statement about what the
/// composition root registered at boot; re-reading the registry at
/// report time would answer "what is registered NOW", which on a
/// process that registers once is the same answer and on one that does
/// not is a weaker one.
type IRemotingDecoderEvidence =
    /// The decode edge's posture. `None` when this deployment declared
    /// no API records to the facet.
    abstract RemotingDecoders: RemotingDecoderIntegrity option

/// Phase 772 — one composed component's declared outbound authority, as
/// the egress policy resolved it. Prefixed field names for the Phase 431
/// inference reason recorded on `ComponentSeamAuthority`.
type ComponentEgressGrant = {
    /// The component's stable id — the key the grant signature uses.
    EgressComponent: ComponentId
    /// The grant it resolves to. `UnrestrictedEgress` for a component
    /// absent from a non-mandatory signature (GP 11).
    EgressDeclaredGrant: EgressGrant
}

/// Phase 772 — one outbound call the egress handler refused since boot.
/// Origin and component only — the row mirrors the audit payload and, like
/// it, never carries a path or a query string.
type EgressDenialRecord = {
    DeniedComponent: ComponentId
    DeniedOrigin: string
    DeniedSurface: string
    /// Phase 796 — the payload's disclosure label as
    /// `EgressLabel.render` produced it: `unlabelled`, `labelled{}`, or
    /// `labelled{a,b}`. The refs are policy NAMES, never values, so this
    /// carries no more than the reason beside it already does.
    DeniedLabel: string
    DeniedReason: string
}

/// The outbound-egress posture (Phase 772), mirrored.
///
/// Tier-neutral for the reason `SeamAuthorityIntegrity` is: the
/// `CompositionProfile` that decides whether declaration is mandatory
/// compiles AFTER this file, so the profile arrives as its label and its
/// stance. `EgressPosture`, `EgressGrant` and `ComponentId` are
/// `Platform.Core` value types and cross freely.
type EgressIntegrity = {
    /// `CompositionProfile.label` — `"standard"` or `"verified"`.
    EgressProfile: string
    /// What the installed policy amounts to. `Unenforced` is the state
    /// this section exists to make legible: the permit-all default,
    /// bounding nothing.
    EgressPosture: EgressPosture
    /// Every component the grant signature names, with its grant.
    /// Deterministic order.
    EgressComponents: ComponentEgressGrant list
    /// The most recent refusals the handler recorded, newest last. Bounded
    /// by the handler's own retention; `EgressDenialsSinceBoot` carries the
    /// full count so a long-running process cannot present as quiet.
    EgressDenials: EgressDenialRecord list
    /// Every refusal since boot, retained or not.
    EgressDenialsSinceBoot: int
    /// Phase 796 — the version of the pinned disclosure-policy-ref
    /// vocabulary this deployment's runtime tier holds
    /// (`DisclosurePolicyRefSnapshot.Version`). The composition tier
    /// mirrors that snapshot, so an operator comparing the two reports
    /// can say whether the tiers are speaking one vocabulary or two.
    EgressLabelVocabulary: string
}

/// Phase 772 — the eleventh section's source.
///
/// A fourth standalone sibling interface, for the reason Phase 693
/// recorded when it cut the first: an abstract member added to a shipped
/// F# interface is a source break. Resolved by type test, so an evidence
/// value that never heard of egress still compiles and the section reads
/// `NotComposed` (GP 11). A VALUE rather than a thunk, like the
/// seam-authority member: the posture is what the composition root
/// installed at boot, and the denial ledger is an in-process snapshot
/// the root takes when it builds the evidence.
type IEgressEvidence =
    /// The outbound-egress posture. `None` when this deployment supplies
    /// nothing about it.
    abstract Egress: EgressIntegrity option

type IEvidenceChainEvidence =
    /// Walk the evidence chain. `None` when no walker is composed.
    /// `Error` carries the walk's typed refusal — an over-cap request or
    /// an over-cap closure — which is never to be read as "the chain is
    /// empty".
    abstract EvidenceChain: (unit -> Async<Result<EvidenceChain, EvidenceChainError>>) option

[<RequireQualifiedAccess>]
module DeploymentVerificationEvidence =

    /// Read the seam-authority member off an evidence value that carries
    /// one. `None` for any evidence that does not implement the sibling
    /// interface — which is every value built before Phase 693 and every
    /// hand-written implementation that has not adopted it.
    ///
    /// The single read path: the gatherer and every wither go through
    /// here, so "does this evidence carry seam authority" has one answer
    /// rather than one per call site.
    let seamAuthorityOf (evidence: IDeploymentVerificationEvidence) : SeamAuthorityIntegrity option =
        match box evidence with
        | :? ISeamAuthorityEvidence as source -> source.SeamAuthority
        | _ -> None

    /// Phase 713 — read the evidence-chain member off an evidence value
    /// that carries one. `None` for any evidence that does not implement
    /// the sibling interface, which is every value built before this
    /// phase.
    ///
    /// The same single-read-path discipline `seamAuthorityOf`
    /// established: the gatherer and every wither go through here, so
    /// "does this evidence carry a chain walk" has one answer rather than
    /// one per call site.
    let evidenceChainOf
        (evidence: IDeploymentVerificationEvidence)
        : (unit -> Async<Result<EvidenceChain, EvidenceChainError>>) option =
        match box evidence with
        | :? IEvidenceChainEvidence as source -> source.EvidenceChain
        | _ -> None

    /// Phase 785 — read the remoting-decoder member off an evidence
    /// value that carries one. `None` for any evidence that does not
    /// implement the sibling interface, which is every value built
    /// before this phase. The same single-read-path discipline
    /// `seamAuthorityOf` established.
    let remotingDecodersOf (evidence: IDeploymentVerificationEvidence) : RemotingDecoderIntegrity option =
        match box evidence with
        | :? IRemotingDecoderEvidence as source -> source.RemotingDecoders
        | _ -> None

    /// Phase 772 — read the egress member off an evidence value that
    /// carries one. `None` for any evidence that does not implement the
    /// sibling interface, which is every value built before this phase.
    /// The same single-read-path discipline `seamAuthorityOf` established.
    let egressOf (evidence: IDeploymentVerificationEvidence) : EgressIntegrity option =
        match box evidence with
        | :? IEgressEvidence as source -> source.Egress
        | _ -> None

    /// Evidence naming nothing — every section reads `NotComposed`.
    /// Behaviourally identical to registering no evidence at all; useful
    /// where a value is required rather than an option.
    let none: IDeploymentVerificationEvidence =
        { new IDeploymentVerificationEvidence with
            member _.BootSeal = None
            member _.GroundingContinuity = None
            member _.Ledger = None
            member _.Certificates = None
            member _.AnswerJoins = None

          interface ISeamAuthorityEvidence with
              member _.SeamAuthority = None

          interface IEvidenceChainEvidence with
              member _.EvidenceChain = None

          interface IRemotingDecoderEvidence with
              member _.RemotingDecoders = None

          interface IEgressEvidence with
              member _.Egress = None
        }

    /// Evidence naming whichever sources the composition root holds. Each
    /// argument is independently optional.
    let create
        (bootSeal: BootSealIntegrity option)
        (groundingContinuity: GroundingContinuityIntegrity option)
        (ledger: (unit -> Async<Result<LedgerIntegrity, string>>) option)
        (certificates: (unit -> Async<Result<CertificateIssuanceIntegrity, string>>) option)
        (answerJoins: (unit -> Async<Result<AnswerJoinIntegrity, string>>) option)
        : IDeploymentVerificationEvidence =
        { new IDeploymentVerificationEvidence with
            member _.BootSeal = bootSeal
            member _.GroundingContinuity = groundingContinuity
            member _.Ledger = ledger
            member _.Certificates = certificates
            member _.AnswerJoins = answerJoins

          interface ISeamAuthorityEvidence with
              member _.SeamAuthority = None

          interface IEvidenceChainEvidence with
              member _.EvidenceChain = None

          interface IRemotingDecoderEvidence with
              member _.RemotingDecoders = None

          interface IEgressEvidence with
              member _.Egress = None
        }

    /// Replace the grounding-continuity member, preserving every other
    /// source. The seam `ServerApp.withDeploymentVerificationEvidence`
    /// uses to fill that section in from the container without the
    /// composition root having to name it.
    let withGroundingContinuity
        (continuity: GroundingContinuityIntegrity option)
        (evidence: IDeploymentVerificationEvidence)
        : IDeploymentVerificationEvidence =
        // Phase 693: the seam-authority member is carried THROUGH, not
        // rebuilt as `None`. A wither that dropped a member it does not
        // name would silently delete the sixth section for every root
        // that supplies both — and `withDeploymentVerificationEvidence`
        // calls this one unconditionally, so the loss would be the
        // default rather than an edge case. Phase 713's chain member
        // rides through for exactly the same reason.
        let seamAuthority = seamAuthorityOf evidence
        let evidenceChain = evidenceChainOf evidence
        let remotingDecoders = remotingDecodersOf evidence
        let egress = egressOf evidence

        { new IDeploymentVerificationEvidence with
            member _.BootSeal = evidence.BootSeal
            member _.GroundingContinuity = continuity
            member _.Ledger = evidence.Ledger
            member _.Certificates = evidence.Certificates
            member _.AnswerJoins = evidence.AnswerJoins

          interface ISeamAuthorityEvidence with
              member _.SeamAuthority = seamAuthority

          interface IEvidenceChainEvidence with
              member _.EvidenceChain = evidenceChain

          interface IRemotingDecoderEvidence with
              member _.RemotingDecoders = remotingDecoders

          interface IEgressEvidence with
              member _.Egress = egress
        }

    /// Phase 693 — supply the seam-authority posture, preserving every
    /// other source.
    ///
    /// A wither rather than a sixth argument to `create`: widening that
    /// function's parameter list retypes it, which the public-API
    /// approval gate reads as a REMOVAL of the five-argument form and
    /// which breaks every existing call. The composition root builds its
    /// evidence with `create` exactly as before and pipes it through
    /// here, the same shape `withGroundingContinuity` already
    /// established.
    let withSeamAuthority
        (seamAuthority: SeamAuthorityIntegrity option)
        (evidence: IDeploymentVerificationEvidence)
        : IDeploymentVerificationEvidence =
        let evidenceChain = evidenceChainOf evidence
        let remotingDecoders = remotingDecodersOf evidence
        let egress = egressOf evidence

        { new IDeploymentVerificationEvidence with
            member _.BootSeal = evidence.BootSeal
            member _.GroundingContinuity = evidence.GroundingContinuity
            member _.Ledger = evidence.Ledger
            member _.Certificates = evidence.Certificates
            member _.AnswerJoins = evidence.AnswerJoins

          interface ISeamAuthorityEvidence with
              member _.SeamAuthority = seamAuthority

          interface IEvidenceChainEvidence with
              member _.EvidenceChain = evidenceChain

          interface IRemotingDecoderEvidence with
              member _.RemotingDecoders = remotingDecoders

          interface IEgressEvidence with
              member _.Egress = egress
        }

    /// Phase 713 — supply the evidence-chain walk, preserving every other
    /// source.
    ///
    /// A wither rather than a sixth argument to `create`, for the reason
    /// `withSeamAuthority` is one: widening that function's parameter
    /// list retypes it, which the public-API approval gate reads as a
    /// REMOVAL of the five-argument form and which breaks every existing
    /// call.
    let withEvidenceChain
        (walk: (unit -> Async<Result<EvidenceChain, EvidenceChainError>>) option)
        (evidence: IDeploymentVerificationEvidence)
        : IDeploymentVerificationEvidence =
        let seamAuthority = seamAuthorityOf evidence
        let remotingDecoders = remotingDecodersOf evidence
        let egress = egressOf evidence

        { new IDeploymentVerificationEvidence with
            member _.BootSeal = evidence.BootSeal
            member _.GroundingContinuity = evidence.GroundingContinuity
            member _.Ledger = evidence.Ledger
            member _.Certificates = evidence.Certificates
            member _.AnswerJoins = evidence.AnswerJoins

          interface ISeamAuthorityEvidence with
              member _.SeamAuthority = seamAuthority

          interface IEvidenceChainEvidence with
              member _.EvidenceChain = walk

          interface IRemotingDecoderEvidence with
              member _.RemotingDecoders = remotingDecoders

          interface IEgressEvidence with
              member _.Egress = egress
        }

    /// Phase 785 — supply the remoting-decoder facet, preserving every
    /// other source.
    ///
    /// A wither rather than a sixth argument to `create`, for the reason
    /// `withSeamAuthority` and `withEvidenceChain` are: widening that
    /// function's parameter list retypes it, which the public-API
    /// approval gate reads as a REMOVAL of the five-argument form and
    /// which breaks every existing call.
    let withRemotingDecoders
        (remotingDecoders: RemotingDecoderIntegrity option)
        (evidence: IDeploymentVerificationEvidence)
        : IDeploymentVerificationEvidence =
        let seamAuthority = seamAuthorityOf evidence
        let evidenceChain = evidenceChainOf evidence
        let egress = egressOf evidence

        { new IDeploymentVerificationEvidence with
            member _.BootSeal = evidence.BootSeal
            member _.GroundingContinuity = evidence.GroundingContinuity
            member _.Ledger = evidence.Ledger
            member _.Certificates = evidence.Certificates
            member _.AnswerJoins = evidence.AnswerJoins

          interface ISeamAuthorityEvidence with
              member _.SeamAuthority = seamAuthority

          interface IEvidenceChainEvidence with
              member _.EvidenceChain = evidenceChain

          interface IRemotingDecoderEvidence with
              member _.RemotingDecoders = remotingDecoders

          interface IEgressEvidence with
              member _.Egress = egress
        }

    /// Phase 772 — supply the outbound-egress posture, preserving every
    /// other source.
    ///
    /// A wither rather than a sixth argument to `create`, for the reason
    /// `withSeamAuthority`, `withEvidenceChain` and `withRemotingDecoders`
    /// are: widening that function's parameter list retypes it, which the
    /// public-API approval gate reads as a REMOVAL of the five-argument
    /// form and which breaks every existing call.
    let withEgress
        (egress: EgressIntegrity option)
        (evidence: IDeploymentVerificationEvidence)
        : IDeploymentVerificationEvidence =
        let seamAuthority = seamAuthorityOf evidence
        let evidenceChain = evidenceChainOf evidence
        let remotingDecoders = remotingDecodersOf evidence

        { new IDeploymentVerificationEvidence with
            member _.BootSeal = evidence.BootSeal
            member _.GroundingContinuity = evidence.GroundingContinuity
            member _.Ledger = evidence.Ledger
            member _.Certificates = evidence.Certificates
            member _.AnswerJoins = evidence.AnswerJoins

          interface ISeamAuthorityEvidence with
              member _.SeamAuthority = seamAuthority

          interface IEvidenceChainEvidence with
              member _.EvidenceChain = evidenceChain

          interface IRemotingDecoderEvidence with
              member _.RemotingDecoders = remotingDecoders

          interface IEgressEvidence with
              member _.Egress = egress
        }

/// Phase 686 — gather the sections, fold them into the report, and
/// serve it. Separated from the evidence types above only by F# scoping:
/// the types must sit at namespace level so `ServerApp` names them
/// without opening this module, which would also drag every gatherer into
/// its scope.
module DeploymentVerificationReport =

    // ─── Section gatherers ───────────────────────────────────────────────
    //
    // One per section. Each is total: it never throws, and every failure
    // mode it can reach has a verdict case that names it. A gatherer that
    // could throw would take the whole report down with it, which is the one
    // outcome an assessor cannot act on.

    /// Scope the report's audited-read row is recorded under. The report is
    /// deployment-wide and belongs to no tenant (GP 4).
    [<Literal>]
    let PlatformScopeId = "_platform"

    /// How many recent issuance identifiers the certificate section carries.
    [<Literal>]
    let RecentIssuanceCap = 10

    /// How many per-component seam-authority lines the section carries as
    /// findings. A deployment can compose more modules than an operator
    /// will read in one screen; the verdict already carries the counts,
    /// and `SeamAuthoritySurface.toWire` is the surface for the whole set.
    [<Literal>]
    let SeamAuthorityComponentCap = 20

    let private section id title verdict findings : ReportSection = {
        Id = id
        Title = title
        Verdict = verdict
        Findings = findings
    }

    /// Sealed composition (Phase 657).
    ///
    /// `BootSealUnsealed` is `Observed`, deliberately: the preflight RAN and
    /// found nothing to compare against. It is not a failure — a deployment
    /// may legitimately start unsealed — and it is emphatically not a
    /// verification, which is precisely the confusion a boolean would create.
    let gatherBootSeal (evidence: IDeploymentVerificationEvidence) : ReportSection =
        let title = "Sealed composition (boot verification)"

        match evidence.BootSeal with
        | None ->
            section
                BootSealSection
                title
                (VerificationSectionVerdict.NotComposed
                    "this deployment did not run the boot verification preflight, so there is no verdict to report")
                []
        | Some(BootSealVerified(profile, policy, detail)) ->
            section
                BootSealSection
                title
                (VerificationSectionVerdict.Verified(sprintf "%s (profile %s, policy %s)" detail profile policy))
                []
        | Some(BootSealVerifiedUnrecorded(profile, policy, detail, unrecorded)) ->
            // `Observed`, not `Verified`: the substrate is composed, was
            // read, and there is a part of its check it could not perform.
            // Not `Failed` either — nothing failed, and an upgrade that
            // turned every sealed deployment's report red would be a worse
            // outcome than the blind spot it closed. Non-adverse, so the
            // report still exits zero; visible, so the one-act remedy
            // (re-seal the binding) is legible.
            section
                BootSealSection
                title
                (VerificationSectionVerdict.Observed(sprintf "%s (profile %s, policy %s)" detail profile policy))
                unrecorded
        | Some(BootSealUnsealed(profile, policy, reason)) ->
            section
                BootSealSection
                title
                (VerificationSectionVerdict.Observed(sprintf "%s (profile %s, policy %s)" reason profile policy))
                []
        | Some(BootSealRejected(profile, policy, detail, findings, refusedStart)) ->
            section
                BootSealSection
                title
                (VerificationSectionVerdict.Failed(
                    sprintf
                        "%s (profile %s, policy %s%s)"
                        detail
                        profile
                        policy
                        (if refusedStart then
                             ", start refused"
                         else
                             ", serving under log-and-serve")
                ))
                findings

    /// Grounding-envelope continuity (Phase 684).
    let gatherGroundingContinuity (evidence: IDeploymentVerificationEvidence) : ReportSection =
        let title = "Grounding-envelope continuity"

        match evidence.GroundingContinuity with
        | None ->
            section
                GroundingContinuitySection
                title
                (VerificationSectionVerdict.NotComposed
                    "no grounding-envelope seal is composed, so post-boot movement of the grounding declarations is unrecorded and unbounded")
                []
        | Some(GroundingContinuous(seal, 0, steps, digest)) ->
            // Continuity over an envelope that declares nothing is true and
            // vacuous. Reporting it as `Verified` would credit the deployment
            // for a check that had nothing to check.
            section
                GroundingContinuitySection
                title
                (VerificationSectionVerdict.Observed(
                    sprintf
                        "the sealed envelope declares nothing, so continuity over %d step(s) to '%s' holds trivially — this deployment has no grounding declaration that could drift"
                        steps
                        digest
                ))
                [ sprintf "seal %s" seal ]
        | Some(GroundingContinuous(seal, declarations, steps, digest)) ->
            section
                GroundingContinuitySection
                title
                (VerificationSectionVerdict.Verified(
                    sprintf
                        "the boot seal plus %d recorded mutation(s) accounts for the live envelope '%s'"
                        steps
                        digest
                ))
                [
                    sprintf "seal %s" seal
                    sprintf "%d declaration(s) in the live envelope" declarations
                ]
        | Some(GroundingDiverged(seal, declarations, detail)) ->
            section GroundingContinuitySection title (VerificationSectionVerdict.Failed detail) [
                sprintf "seal %s" seal
                sprintf "%d declaration(s) in the live envelope" declarations
            ]

    /// Hash-chained audit ledger (Phase 658 / 682).
    let gatherLedger (evidence: IDeploymentVerificationEvidence) : Async<ReportSection> = async {
        let title = "Hash-chained audit ledger"

        match evidence.Ledger with
        | None ->
            return
                section
                    AuditLedgerSection
                    title
                    (VerificationSectionVerdict.NotComposed
                        "no hash-chained audit ledger is composed, so the audit trail carries no tamper evidence")
                    []
        | Some walk ->
            let! outcome = walk () |> Async.Catch

            match outcome with
            | Choice2Of2 ex ->
                return
                    section
                        AuditLedgerSection
                        title
                        (VerificationSectionVerdict.Unreadable(sprintf "the ledger walk raised: %s" ex.Message))
                        []
            | Choice1Of2(Error reason) ->
                return
                    section
                        AuditLedgerSection
                        title
                        (VerificationSectionVerdict.Unreadable(sprintf "the ledger could not be read: %s" reason))
                        []
            | Choice1Of2(Ok(LedgerChainVerified(0L, headDigest, signature))) ->
                return
                    section
                        AuditLedgerSection
                        title
                        (VerificationSectionVerdict.Observed(
                            sprintf "the ledger is composed and empty — head '%s', signature %s" headDigest signature
                        ))
                        []
            | Choice1Of2(Ok(LedgerChainVerified(records, headDigest, signature))) ->
                return
                    section
                        AuditLedgerSection
                        title
                        (VerificationSectionVerdict.Verified(
                            sprintf "%d record(s) chain to head '%s'; head signature %s" records headDigest signature
                        ))
                        []
            | Choice1Of2(Ok(LedgerHeadRejected(records, headDigest, detail))) ->
                return
                    section
                        AuditLedgerSection
                        title
                        (VerificationSectionVerdict.Failed(
                            sprintf
                                "the chain of %d record(s) walks clean and its head is not trustworthy: %s"
                                records
                                detail
                        ))
                        [ sprintf "chain head '%s'" headDigest ]
            | Choice1Of2(Ok(LedgerHeadUnverifiable(records, headDigest, reason))) ->
                return
                    section
                        AuditLedgerSection
                        title
                        (VerificationSectionVerdict.Unreadable(
                            sprintf
                                "the chain of %d record(s) walks clean and its head could not be judged: %s"
                                records
                                reason
                        ))
                        [ sprintf "chain head '%s'" headDigest ]
            | Choice1Of2(Ok(LedgerChainBroken(position, kind, detail))) ->
                return
                    section
                        AuditLedgerSection
                        title
                        (VerificationSectionVerdict.Failed(
                            sprintf "the ledger breaks at position %d (%s): %s" position kind detail
                        ))
                        [
                            sprintf
                                "records 0..%d verify; everything after the break is unevidenced"
                                (max 0L (position - 1L))
                        ]
    }

    /// Certificate issuance log (Phase 565 / 685).
    let gatherCertificates (evidence: IDeploymentVerificationEvidence) : Async<ReportSection> = async {
        let title = "Certificate issuance log"

        match evidence.Certificates with
        | None ->
            return
                section
                    CertificateIssuanceSection
                    title
                    (VerificationSectionVerdict.NotComposed
                        "no certificate issuance log is composed, so this deployment's issuances are not enumerable")
                    []
        | Some read ->
            let! outcome = read () |> Async.Catch

            match outcome with
            | Choice2Of2 ex ->
                return
                    section
                        CertificateIssuanceSection
                        title
                        (VerificationSectionVerdict.Unreadable(sprintf "the issuance log read raised: %s" ex.Message))
                        []
            | Choice1Of2(Error reason) ->
                // The Phase 685 discipline at report scope: a log that will
                // not verify is NOT a log that issued nothing. Reading it as
                // absence would make breaking your own ledger the cheapest
                // way to answer an inconvenient question.
                return
                    section
                        CertificateIssuanceSection
                        title
                        (VerificationSectionVerdict.Unreadable(
                            sprintf "the issuance log would not verify, so its contents cannot be relied on: %s" reason
                        ))
                        []
            | Choice1Of2(Ok integrity) when integrity.Issued = 0 ->
                return
                    section
                        CertificateIssuanceSection
                        title
                        (VerificationSectionVerdict.Observed
                            "the issuance log is readable and records no certificate issued by this deployment")
                        []
            | Choice1Of2(Ok integrity) when not integrity.LogIntegrityChecked ->
                return
                    section
                        CertificateIssuanceSection
                        title
                        (VerificationSectionVerdict.Observed(
                            sprintf
                                "%d issuance(s) enumerated from a log that runs no integrity gate — this is the deployment's own assertion, not tamper-evident"
                                integrity.Issued
                        ))
                        (integrity.Recent |> List.truncate RecentIssuanceCap)
            | Choice1Of2(Ok integrity) ->
                return
                    section
                        CertificateIssuanceSection
                        title
                        (VerificationSectionVerdict.Verified(
                            sprintf
                                "%d issuance(s) enumerated behind an integrity gate that verified the backing trail first"
                                integrity.Issued
                        ))
                        (integrity.Recent |> List.truncate RecentIssuanceCap)
    }

    /// Answer-verification provenance join (Phase 680).
    let gatherAnswerJoins (evidence: IDeploymentVerificationEvidence) : Async<ReportSection> = async {
        let title = "Answer-verification provenance join"

        match evidence.AnswerJoins with
        | None ->
            return
                section
                    AnswerJoinSection
                    title
                    (VerificationSectionVerdict.NotComposed
                        "no answer-verification audit join is composed, so served answers carry no recorded link to the facts they stand on")
                    []
        | Some rejoin ->
            let! outcome = rejoin () |> Async.Catch

            match outcome with
            | Choice2Of2 ex ->
                return
                    section
                        AnswerJoinSection
                        title
                        (VerificationSectionVerdict.Unreadable(sprintf "the join read raised: %s" ex.Message))
                        []
            | Choice1Of2(Error reason) ->
                return
                    section
                        AnswerJoinSection
                        title
                        (VerificationSectionVerdict.Unreadable(
                            sprintf "the answer-verification rows could not be read: %s" reason
                        ))
                        []
            | Choice1Of2(Ok join) when join.Rows = 0 ->
                return
                    section
                        AnswerJoinSection
                        title
                        (VerificationSectionVerdict.Observed
                            "the join is composed and no answer-verification row has been recorded yet")
                        []
            | Choice1Of2(Ok join) when not (List.isEmpty join.Mismatched) ->
                return
                    section
                        AnswerJoinSection
                        title
                        (VerificationSectionVerdict.Failed(
                            sprintf
                                "%d of %d row(s) name a provenance head that does not recompute from the fact ids on the same row"
                                join.Mismatched.Length
                                join.Rows
                        ))
                        join.Mismatched
            | Choice1Of2(Ok join) ->
                let findings =
                    if join.Unanchored > 0 then
                        [
                            sprintf
                                "%d row(s) name no provenance head because the answer verified against no fact — expected, and not a failure"
                                join.Unanchored
                        ]
                    else
                        []

                return
                    section
                        AnswerJoinSection
                        title
                        (VerificationSectionVerdict.Verified(
                            sprintf
                                "%d of %d row(s) re-derive their recorded provenance head from their own cited fact ids"
                                join.Rejoined
                                join.Rows
                        ))
                        findings
    }

    /// The seams one component reaches, rendered in the same
    /// `{a,b}` shape `SeamGrant.render` uses for the declared set, so the
    /// two halves of a finding line read against each other rather than
    /// in two notations.
    let private renderReach (reach: SeamId list) : string =
        if List.isEmpty reach then
            "nothing derived"
        else
            reach
            |> List.map SeamId.value
            |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
            |> String.concat ","
            |> sprintf "{%s}"

    /// One finding line per component: what it declared beside what its
    /// registrations imply it reaches.
    ///
    /// Truncated at `SeamAuthorityComponentCap` with an explicit line
    /// saying how many were withheld — a silent truncation would let a
    /// large composition present as a small one, and the count is the
    /// half a reader would otherwise have no way to notice was missing.
    let private componentFindings (components: ComponentSeamAuthority list) : string list =
        let rendered =
            components
            |> List.truncate SeamAuthorityComponentCap
            |> List.map (fun entry ->
                sprintf
                    "%s: declared %s, reaches %s%s"
                    (ComponentId.value entry.AuthorityComponent)
                    (SeamGrant.render entry.DeclaredGrant)
                    (renderReach entry.DerivedReach)
                    (if entry.ComposedHere then
                         ""
                     else
                         " — declared for a component this deployment does not compose"))

        let withheld = components.Length - rendered.Length

        if withheld > 0 then
            rendered @ [ sprintf "(%d further component(s) not listed)" withheld ]
        else
            rendered

    /// Module seam authority (Phase 688 / 691).
    ///
    /// **The section that must not overstate itself, in two independent
    /// ways.** Declaring grants is not enforcing them, and enforcing them
    /// over a composition that declared nothing admits everything by
    /// construction. Either read as `Verified` would credit a deployment
    /// for a bound it does not carry, so both land on `Observed` with the
    /// reason spelled out. `Verified` needs the conjunction: the check
    /// ran, something was declared, and every derived reach was admitted.
    ///
    /// Nothing here is hardcoded from the SDK's own posture. The gatherer
    /// never says "Phase 691 shipped enforcement"; it says what THIS
    /// deployment's composition did, which is the only question an
    /// assessor holding a running deployment is asking.
    let gatherSeamAuthority (evidence: IDeploymentVerificationEvidence) : ReportSection =
        let title = "Module seam authority"

        match DeploymentVerificationEvidence.seamAuthorityOf evidence with
        | None ->
            section
                SeamAuthoritySection
                title
                (VerificationSectionVerdict.NotComposed
                    "no seam-authority declaration or check is composed, so what substrate each module reaches is bounded only by what the container will hand it")
                []
        | Some integrity ->
            let posture =
                if integrity.DeclarationMandatory then
                    "mandatory"
                else
                    "advisory"

            let binding = sprintf "profile %s, seam declaration %s" integrity.Profile posture

            let declared =
                integrity.Components
                |> List.filter (fun entry -> SeamGrant.isDeclared entry.DeclaredGrant)

            let findings = componentFindings integrity.Components

            match integrity.Verification with
            | SeamAuthorityRefused(detail, refusalFindings) ->
                section
                    SeamAuthoritySection
                    title
                    (VerificationSectionVerdict.Failed(sprintf "%s (%s)" detail binding))
                    (refusalFindings @ findings)
            | SeamAuthorityUnenforced when List.isEmpty declared ->
                section
                    SeamAuthoritySection
                    title
                    (VerificationSectionVerdict.Observed(
                        sprintf
                            "no component declares a seam set and no composition in this deployment routes through the seam gate — every module reaches whatever the container will hand it (%s)"
                            binding
                    ))
                    findings
            | SeamAuthorityUnenforced ->
                // The state the phase exists to make legible. The SDK's
                // enforcement is real and this deployment does not call
                // it, so the grants are a statement about intent and
                // nothing holds anything to them.
                section
                    SeamAuthoritySection
                    title
                    (VerificationSectionVerdict.Observed(
                        sprintf
                            "%d of %d component(s) declare a seam set and no composition in this deployment routes through the seam gate, so the declarations bound nothing (%s)"
                            declared.Length
                            integrity.Components.Length
                            binding
                    ))
                    findings
            | SeamAuthorityAdmitted(components, seams) when List.isEmpty declared ->
                // Vacuously true, exactly like continuity over an
                // envelope that declares nothing. Every reach was
                // admitted because every component resolved to
                // `UnrestrictedSeams` — the Phase 688 additive floor —
                // and reporting that as a verification would credit the
                // deployment for a check that could not have refused.
                section
                    SeamAuthoritySection
                    title
                    (VerificationSectionVerdict.Observed(
                        sprintf
                            "the gate admitted all %d derived reach(es) across %d component(s) and no component declared a seam set, so every reach was admitted by the unrestricted default — the additive floor, not a confinement result (%s)"
                            seams
                            components
                            binding
                    ))
                    findings
            | SeamAuthorityAdmitted(components, seams) ->
                section
                    SeamAuthoritySection
                    title
                    (VerificationSectionVerdict.Verified(
                        sprintf
                            "the gate admitted all %d derived reach(es) across %d composed component(s); %d of them declare a seam set (%s)"
                            seams
                            components
                            declared.Length
                            binding
                    ))
                    findings

    // ─── Phase 785 — the tenth section: the decode edge ──────────────────

    /// How many per-record lines the remoting-decoder section carries.
    /// A composition can mount more API records than an operator will
    /// read in one screen; the verdict already carries the counts.
    [<Literal>]
    let RemotingDecoderRecordCap = 20

    /// One finding line per API record: how it decodes, what it carries
    /// that has no decoder, and whether the corpus draws its shapes.
    ///
    /// Truncated with an explicit count of what was withheld, for the
    /// reason the seam-authority section truncates that way: a silent
    /// truncation would let a large composition present as a small one.
    let private decoderFindings (records: RemotingDecoderRecord list) : string list =
        let rendered =
            records
            |> List.truncate RemotingDecoderRecordCap
            |> List.map (fun record ->
                let corpus =
                    if record.RecordCorpusCovered then
                        "corpus covers its shapes"
                    else
                        "corpus coverage NOT declared"

                match record.RecordUncovered with
                | [] -> sprintf "%s: %s, %s" record.RecordApiRecord record.RecordClassification corpus
                | uncovered ->
                    sprintf
                        "%s: %s, %s — no decoder for %s"
                        record.RecordApiRecord
                        record.RecordClassification
                        corpus
                        (String.concat ", " uncovered))

        let withheld = records.Length - rendered.Length

        if withheld > 0 then
            rendered @ [ sprintf "(%d further API record(s) not listed)" withheld ]
        else
            rendered

    /// The remoting decode edge (Phase 785).
    ///
    /// **`Verified` needs the CONJUNCTION, and the section is the place
    /// that will not take half of it.** A record decodes through the
    /// closed algebra AND the wire corpus draws the shapes it carries —
    /// either alone is a deployment's assertion about itself. A record
    /// on the algebra path whose corpus coverage is undeclared reads
    /// `Observed`: the decoder is there and nothing has shown it decodes
    /// what this record actually carries, which is honest and
    /// emphatically not a verification.
    ///
    /// A `Reflection` record is `Observed` too and never `Failed`,
    /// whatever the profile. Reflection decoding is the SDK's shipped
    /// default and the path every deployment ran before this phase; a
    /// section that reddened on it would report the platform's own
    /// baseline as a defect, and would be turned off. The profile's
    /// requirement is enforced where the profile enforces everything
    /// else — at the boot preflight, which refuses the process a start
    /// rather than colouring a report line.
    let gatherRemotingDecoders (evidence: IDeploymentVerificationEvidence) : ReportSection =
        let title = "Remoting decode edge"

        match DeploymentVerificationEvidence.remotingDecodersOf evidence with
        | None ->
            section
                RemotingDecoderSection
                title
                (VerificationSectionVerdict.NotComposed
                    "no API record is declared to the remoting-decoder facet, so nothing here says how this deployment's client responses are decoded")
                []
        | Some integrity ->
            let posture =
                if integrity.DecoderMandatory then
                    "mandatory"
                else
                    "advisory"

            let binding =
                sprintf "profile %s, algebra decoders %s" integrity.DecoderProfile posture

            let findings = decoderFindings integrity.DecoderRecords
            let total = List.length integrity.DecoderRecords

            let verified =
                integrity.DecoderRecords
                |> List.filter (fun record -> record.RecordClassification = "algebra" && record.RecordCorpusCovered)

            let reflection =
                integrity.DecoderRecords
                |> List.filter (fun record -> record.RecordClassification <> "algebra")

            if total = 0 then
                section
                    RemotingDecoderSection
                    title
                    (VerificationSectionVerdict.Observed(
                        sprintf "the facet is composed and declares no API record, so it bounds nothing (%s)" binding
                    ))
                    findings
            elif List.length verified = total then
                section
                    RemotingDecoderSection
                    title
                    (VerificationSectionVerdict.Verified(
                        sprintf
                            "all %d declared API record(s) decode through the closed algebra and the wire corpus draws the shapes each carries (%s)"
                            total
                            binding
                    ))
                    findings
            else
                section
                    RemotingDecoderSection
                    title
                    (VerificationSectionVerdict.Observed(
                        sprintf
                            "%d of %d declared API record(s) decode through the closed algebra with corpus-covered shapes; %d still decode by reflection over an open type graph (%s)"
                            (List.length verified)
                            total
                            (List.length reflection)
                            binding
                    ))
                    findings

    // ─── Phase 772 — the eleventh section: what leaves the process ───────

    /// How many per-component grant lines the egress section carries.
    [<Literal>]
    let EgressComponentCap = 20

    /// How many refusal lines the egress section carries. The verdict
    /// already carries the full since-boot count.
    [<Literal>]
    let EgressDenialCap = 20

    /// One finding line per declared component, then one per retained
    /// refusal, each list truncated with an explicit count of what was
    /// withheld — for the reason the seam-authority section truncates
    /// that way: a silent truncation would let a large composition
    /// present as a small one, or a noisy one as quiet.
    let private egressFindings (integrity: EgressIntegrity) : string list =
        let components =
            integrity.EgressComponents
            |> List.truncate EgressComponentCap
            |> List.map (fun entry ->
                sprintf
                    "%s: %s"
                    (ComponentId.value entry.EgressComponent)
                    (EgressGrant.render entry.EgressDeclaredGrant))

        let withheldComponents = integrity.EgressComponents.Length - components.Length

        let denials =
            integrity.EgressDenials
            |> List.truncate EgressDenialCap
            |> List.map (fun denial ->
                sprintf
                    "refused: %s -> %s (%s surface, %s)"
                    (ComponentId.value denial.DeniedComponent)
                    denial.DeniedOrigin
                    denial.DeniedSurface
                    denial.DeniedLabel)

        let withheldDenials = integrity.EgressDenialsSinceBoot - denials.Length

        [
            yield! components

            if withheldComponents > 0 then
                yield sprintf "(%d further component(s) not listed)" withheldComponents

            yield! denials

            if withheldDenials > 0 then
                yield sprintf "(%d further refusal(s) not listed)" withheldDenials
        ]

    /// Outbound egress through the platform client factory (Phase 772).
    ///
    /// **The section that must say when it bounds nothing.** The SDK ships
    /// the handler in front of every in-tree client, but the policy it
    /// consults is the permit-all default until a composition installs
    /// another — so "the SDK has an egress seam" and "this deployment
    /// bounds its egress" are different facts, and `Unenforced` reads as
    /// `Observed` with the reason spelled out rather than borrowing the
    /// SDK's posture. `Verified` needs a bound that can actually refuse:
    /// declaration mandatory (an undeclared component is denied) — with
    /// grants, or with none, where every call is refused and the section
    /// says so. Declared grants under a profile that leaves undeclared
    /// components unrestricted are a partial bound and read `Observed`.
    ///
    /// Every line is derived from the installed binding and the handler's
    /// own ledger, never reported by the root: a root cannot overstate its
    /// coverage by passing a flattering number.
    let gatherEgress (evidence: IDeploymentVerificationEvidence) : ReportSection =
        let title = "Outbound egress"

        match DeploymentVerificationEvidence.egressOf evidence with
        | None ->
            section
                EgressSection
                title
                (VerificationSectionVerdict.NotComposed
                    "no egress posture is composed, so which origins each component may reach through the platform client factory is bounded only by what the network will accept")
                []
        | Some integrity ->
            let mandatory =
                match integrity.EgressPosture with
                | EgressPosture.Unenforced -> false
                | EgressPosture.DenyAll -> true
                | EgressPosture.Declared(_, undeclaredDenied) -> undeclaredDenied

            // Phase 796 — the binding line names the ref vocabulary the
            // runtime tier holds, and whether an unlabelled payload is
            // refused. Both travel with the posture rather than as their
            // own section: they qualify the SAME bound, and a reader
            // asking "what may leave here" needs the two facts together.
            let binding =
                sprintf
                    "profile %s, destination declaration %s, payload label %s, ref vocabulary %s"
                    integrity.EgressProfile
                    (if mandatory then "mandatory" else "advisory")
                    (if mandatory then "must be cleared" else "advisory")
                    integrity.EgressLabelVocabulary

            let findings = egressFindings integrity
            let refusals = integrity.EgressDenialsSinceBoot

            match integrity.EgressPosture with
            | EgressPosture.Unenforced ->
                section
                    EgressSection
                    title
                    (VerificationSectionVerdict.Observed(
                        sprintf
                            "the permit-all default is composed: every outbound call from every component through the platform client factory is permitted and nothing is bounded (%s)"
                            binding
                    ))
                    findings
            | EgressPosture.DenyAll ->
                section
                    EgressSection
                    title
                    (VerificationSectionVerdict.Verified(
                        sprintf
                            "declaration is mandatory and no component declares a destination, so every outbound call through the platform client factory is refused before its socket opens; %d refusal(s) recorded since boot (%s)"
                            refusals
                            binding
                    ))
                    findings
            | EgressPosture.Declared(components, true) ->
                section
                    EgressSection
                    title
                    (VerificationSectionVerdict.Verified(
                        sprintf
                            "declared destinations bind %d component(s) and an undeclared component is refused; %d refusal(s) recorded since boot (%s)"
                            components
                            refusals
                            binding
                    ))
                    findings
            | EgressPosture.Declared(components, false) ->
                section
                    EgressSection
                    title
                    (VerificationSectionVerdict.Observed(
                        sprintf
                            "declared destinations bind %d component(s) but an undeclared component remains unrestricted, so the bound is partial; %d refusal(s) recorded since boot (%s)"
                            components
                            refusals
                            binding
                    ))
                    findings

    // ─── Phase 713 — the ninth section: the join ─────────────────────────

    /// How many hop lines the evidence-chain section carries.
    ///
    /// The chain's hop count is fixed and small by construction, so this
    /// is not a truncation guard for the hops themselves — it bounds the
    /// per-hop findings the section quotes beneath them. The verdict's
    /// own counts are always over the whole walk.
    [<Literal>]
    let EvidenceChainFindingCap = 40

    /// One line per hop, in walk order, whatever each hop said.
    ///
    /// **Every hop renders, including the absent ones.** A section that
    /// listed only the resolved hops would read as a complete chain and
    /// would not be — which is the single failure this whole surface
    /// exists to prevent, reproduced at the last possible moment inside
    /// the artefact meant to expose it.
    let private hopFindings (chain: EvidenceChain) : string list =
        let lines =
            chain.Hops
            |> List.map (fun hop ->
                sprintf
                    "%d. %s — %s: %s"
                    hop.Ordinal
                    hop.Title
                    ((EvidenceLink.label hop.Link).ToUpperInvariant())
                    (EvidenceLink.detail hop.Link))

        let shown = lines |> List.truncate EvidenceChainFindingCap
        let withheld = lines.Length - shown.Length

        if withheld > 0 then
            shown @ [ sprintf "(%d further hop line(s) not listed)" withheld ]
        else
            shown

    /// Evidence chain (Phase 713).
    ///
    /// **Four verdicts for four chain outcomes, and the two
    /// non-affirmative ones are not the same.** A chain in which nothing
    /// resolved is `Observed` and never `NotComposed`: the walker IS
    /// composed, it ran, and it found no join — which is a read with
    /// nothing to affirm, not an absent substrate. A chain the walker
    /// REFUSED is `Unreadable` and never `Observed`, for the reason the
    /// ledger section refuses to read a failed integrity gate as an empty
    /// log: a deployment that can end an inconvenient walk by exceeding
    /// its own cap must not be rewarded with a quieter verdict.
    ///
    /// A withheld hop never reddens the section. A refusal is a working
    /// access control, and a report that failed on one would teach a
    /// reader to route around the control rather than ask its holder.
    let gatherEvidenceChain (evidence: IDeploymentVerificationEvidence) : Async<ReportSection> = async {
        let title = "Evidence chain"

        match DeploymentVerificationEvidence.evidenceChainOf evidence with
        | None ->
            return
                section
                    EvidenceChainSection
                    title
                    (VerificationSectionVerdict.NotComposed
                        "no evidence chain walker is composed, so tracing this deployment back to the work that authored it is an investigation rather than a query")
                    []
        | Some walk ->
            let! outcome = walk () |> Async.Catch

            match outcome with
            | Choice2Of2 ex ->
                return
                    section
                        EvidenceChainSection
                        title
                        (VerificationSectionVerdict.Unreadable(sprintf "the chain walk raised: %s" ex.Message))
                        []
            | Choice1Of2(Error error) ->
                return
                    section
                        EvidenceChainSection
                        title
                        (VerificationSectionVerdict.Unreadable(
                            sprintf "the chain walk was refused: %s" (EvidenceChainError.describe error)
                        ))
                        []
            | Choice1Of2(Ok chain) ->
                let hops = List.length chain.Hops

                let linked =
                    chain.Hops
                    |> List.filter (fun hop -> EvidenceLink.isLinked hop.Link)
                    |> List.length

                let broken =
                    chain.Hops
                    |> List.filter (fun hop -> EvidenceLink.isBroken hop.Link)
                    |> List.length

                let verdict =
                    match chain.Outcome with
                    | EvidenceChainOutcome.ChainBroken ->
                        VerificationSectionVerdict.Failed(
                            sprintf
                                "%d of %d hop(s) in the chain are broken, so the walk from the authoring work to the ledger position does not hold (chain digest %s)"
                                broken
                                hops
                                chain.VerdictDigest
                        )
                    | EvidenceChainOutcome.ChainUnrecorded ->
                        // Composed, walked, and not one join resolved.
                        // `Observed` rather than `NotComposed` — the
                        // walker answered, and what it answered is that
                        // this deployment records no chain at all.
                        VerificationSectionVerdict.Observed(
                            sprintf
                                "the chain was walked and not one of its %d hop(s) resolves, so this deployment records no traversable evidence at all (chain digest %s)"
                                hops
                                chain.VerdictDigest
                        )
                    | EvidenceChainOutcome.ChainPartial ->
                        VerificationSectionVerdict.Observed(
                            sprintf
                                "%d of %d hop(s) resolve and none is broken, so the chain is unbroken where it exists and does not reach end to end (chain digest %s)"
                                linked
                                hops
                                chain.VerdictDigest
                        )
                    | EvidenceChainOutcome.ChainComplete ->
                        VerificationSectionVerdict.Verified(
                            sprintf
                                "all %d hop(s) resolve, so this deployment traverses from the upstream work record that authored its sources to its position in the audit ledger (chain digest %s)"
                                hops
                                chain.VerdictDigest
                        )

                return section EvidenceChainSection title verdict (hopFindings chain)
    }

    // ─── Phase 699 — declared intent, and accepted risk ──────────────────
    //
    // Six sections describe what this deployment IS. Neither of the two
    // below does: one states what the operator SAID it should be and where
    // reality differs, the other states which safety refusals were waived
    // to let it start. Together they close the assessor triangle — a
    // capability manifest says what the composition may do, the six
    // sections above say what it is, and until now nothing said what it
    // was meant to be.
    //
    // **The claims discipline, stated once and honoured in every string
    // below.** The conformance section attests: *this file, this hash, was
    // the declared intent at this boot, and here is where reality
    // differed.* It never attests that the deployment BEHAVED per the
    // file. The distinction is not pedantry — the section compares
    // declared values against what the resolution seam reports as
    // effective, which is one layer below any reader's use of the value,
    // and the corresponding not-proved statement says so rather than
    // leaving a reader to infer it.
    //
    // **Why these two read the resolution seam DIRECTLY rather than
    // arriving through `IDeploymentVerificationEvidence`.** The header at
    // the top of this file gives two reasons for the evidence seam, and
    // neither applies here. The first is dependency direction: the ledger,
    // the issuance log and the answer join live in assemblies DOWNSTREAM
    // of this one. The second is compile order: the boot verdict, the
    // continuity walk and the seam-authority posture live in this assembly
    // but compile after the route table that mounts this report.
    // `ConfigResolution` is neither — it is `Platform.Core`, upstream of
    // everything, and it is ambient process state installed at boot in the
    // same way the environment itself is.
    //
    // Routing it through the evidence seam would therefore buy nothing and
    // cost the one property that matters most here: a composition root
    // that forgot to hand its manifest over would produce a report reading
    // "this deployment declares no configuration manifest" for a
    // deployment that declares one — which is precisely the
    // declared-but-not-applied failure the whole declared layer exists to
    // make impossible, reproduced inside the artefact that exists to
    // detect it. Reading the seam directly cannot be forgotten.

    /// How many per-key conformance lines the section carries. Same
    /// reasoning as `SeamAuthorityComponentCap`: a manifest may legitimately
    /// declare more keys than an operator reads in one screen, the verdict
    /// already carries the counts, and the truncation is stated rather than
    /// silent. Findings are ordered so that nothing an assessor must act on
    /// is ever the part that gets withheld.
    [<Literal>]
    let ConfigConformanceKeyCap = 40

    /// The marker a set secret is shown as, spelled exactly as
    /// `--print-config` spells it so an operator comparing the two reads
    /// one vocabulary. Restated rather than shared because the module that
    /// owns it compiles after this file.
    [<Literal>]
    let private RedactedValue = "<redacted>"

    /// How one key the manifest declares actually fared at this boot.
    ///
    /// The two `Ignored*` cases are separate because their remedies are:
    /// one waits on a reader migrating to the seam, the other is a line in
    /// the file that states nothing. Folding them would produce a finding
    /// an operator could not act on.
    type private DeclaredKeyOutcome =
        /// The declared value is the effective value, supplied by the
        /// manifest itself. The conforming case.
        | HonouredFromManifest of effective: string
        /// The environment supplies a different value. Legitimate and
        /// documented — env sits ABOVE the manifest by design — so it is
        /// reported with both values and never counted as a finding.
        | OverriddenByEnv of effective: string
        /// No reader resolves this key through the seam, so the declared
        /// value reaches nothing.
        | IgnoredUnbindable
        /// The key is bindable and the manifest still supplies no value
        /// for it — an empty declaration, which states nothing — so the
        /// key falls through to whatever layer is named.
        | IgnoredNoLayer of ConfigResolution.ConfigSource

    /// Whether the registry marks `key` secret. A secret key cannot reach
    /// a manifest through the shipped loader, which refuses it outright —
    /// so this is defence for the paths that bypass the loader (a
    /// hand-installed snapshot in a test, a future loader) rather than a
    /// case the ordinary flow reaches. Redacting something that cannot be
    /// there costs one comparison; printing something that can be is
    /// unrecoverable.
    let private isSecretKey (key: string) : bool =
        ConfigKeys.all |> List.exists (fun d -> d.EnvVar = key && d.IsSecret)

    let private renderValue (secret: bool) (value: string) : string = if secret then RedactedValue else value

    /// Classify one declared key.
    ///
    /// **Bindability is checked FIRST and that ordering is load-bearing.**
    /// The seam reads the manifest's value table without consulting
    /// bindability, so an unbindable key resolves with source `manifest`
    /// and would classify as honoured — reporting a key nothing reads as
    /// the conforming case, which is the single most misleading line this
    /// section could emit.
    let private classifyDeclared (key: string) : DeclaredKeyOutcome =
        if not (ConfigKeys.isManifestBindable key) then
            IgnoredUnbindable
        else
            match ConfigResolution.tryResolve key with
            | Some(effective, ConfigResolution.ManifestConfigSource) -> HonouredFromManifest effective
            | Some(effective, ConfigResolution.EnvConfigSource) -> OverriddenByEnv effective
            | Some(_, source) -> IgnoredNoLayer source
            | None -> IgnoredNoLayer ConfigResolution.DefaultConfigSource

    /// The profile context lines, if a profile is in force. Reported in
    /// this section rather than a seventh of its own because a profile IS
    /// declared intent — the rung directly below the manifest — and an
    /// operator reading which of their declared keys took effect needs the
    /// bundle that supplied the rest in the same place.
    let private profileFindings () : string list =
        match ConfigResolution.profile () with
        | None -> []
        | Some p ->
            let header =
                sprintf
                    "profile '%s' is in force, selected by %s, supplying %d key(s) one rung below the manifest"
                    p.Name
                    (ConfigResolution.ProfileSelection.describe p.SelectedBy)
                    (Map.count p.Values)

            match ConfigResolution.profileShadowedKeys () with
            | [] -> [ header ]
            | taken -> [
                header
                sprintf
                    "%d profile key(s) are taken back by a higher layer and do not take effect from the profile: %s"
                    taken.Length
                    (String.concat ", " taken)
              ]

    /// Declared configuration conformance (Phase 696 / 700 declared, 699
    /// reported).
    ///
    /// Three shapes of absence, and none of them is silence. A deployment
    /// with no manifest and no profile has declared nothing and the section
    /// says so; one with a profile but no manifest has declared a posture
    /// with no hashable artefact behind it and the section says THAT; and a
    /// manifest declaring no key is a committed file stating no intent,
    /// which is `Observed` for the same reason continuity over an empty
    /// envelope is — a comparison with nothing to compare has verified
    /// nothing.
    let gatherConfigConformance () : ReportSection =
        let title = "Declared configuration conformance"

        match ConfigResolution.snapshot () with
        | None ->
            match ConfigResolution.profile () with
            | None ->
                section
                    ConfigConformanceSection
                    title
                    (VerificationSectionVerdict.NotComposed
                        "this deployment declares no configuration manifest, so there is no stated intent to compare the effective configuration against — every value came from the environment, an imported profile, or a declared default")
                    []
            | Some p ->
                section
                    ConfigConformanceSection
                    title
                    (VerificationSectionVerdict.Observed(
                        sprintf
                            "no configuration manifest is declared and profile '%s' is in force, so this deployment's stated intent is a profile name rather than a file — there are no declared key/value lines to compare against, and no manifest hash to quote"
                            p.Name
                    ))
                    (profileFindings ())
        | Some manifest ->
            let declared = manifest.Values |> Map.toList |> List.sortBy fst

            let classified =
                declared |> List.map (fun (key, value) -> key, value, classifyDeclared key)

            let pick chooser =
                classified |> List.filter (fun (_, _, outcome) -> chooser outcome)

            let ignored =
                pick (function
                    | IgnoredUnbindable
                    | IgnoredNoLayer _ -> true
                    | _ -> false)

            let overridden =
                pick (function
                    | OverriddenByEnv _ -> true
                    | _ -> false)

            let honoured =
                pick (function
                    | HonouredFromManifest _ -> true
                    | _ -> false)

            let line (key: string, value: string, outcome: DeclaredKeyOutcome) =
                let show = renderValue (isSecretKey key)

                match outcome with
                | HonouredFromManifest effective ->
                    sprintf "%s: declared %s, effective %s [manifest]" key (show value) (show effective)
                | OverriddenByEnv effective ->
                    sprintf
                        "%s: declared %s, effective %s [env] — the environment overrides the manifest, which is the documented precedence and is not a finding"
                        key
                        (show value)
                        (show effective)
                | IgnoredUnbindable ->
                    sprintf
                        "%s: declared %s — IGNORED: no reader resolves this key through the configuration seam, so the declared value takes effect nowhere. Set the %s environment variable instead until its reader migrates."
                        key
                        (show value)
                        key
                | IgnoredNoLayer source ->
                    sprintf
                        "%s: declared %s — IGNORED: the declaration supplies no value (an empty entry states nothing), so the key resolves from [%s] instead."
                        key
                        (show value)
                        (ConfigResolution.ConfigSource.label source)

            // Findings an assessor must act on lead, so the cap can only
            // ever withhold conforming lines.
            let keyLines = (ignored @ overridden @ honoured) |> List.map line
            let shown = keyLines |> List.truncate ConfigConformanceKeyCap
            let withheld = keyLines.Length - shown.Length

            let findings =
                profileFindings ()
                @ shown
                @ (if withheld > 0 then
                       [
                           sprintf
                               "(%d further declared key(s) not listed; the verdict's counts are over all %d)"
                               withheld
                               keyLines.Length
                       ]
                   else
                       [])

            let header = sprintf "the manifest at %s (sha256:%s)" manifest.Path manifest.Hash

            let verdict =
                match declared, ignored with
                | [], _ ->
                    VerificationSectionVerdict.Observed(
                        sprintf
                            "%s declares no configuration key, so conformance over it holds trivially — this deployment has committed a manifest and stated no intent in it"
                            header
                    )
                | _, [] ->
                    VerificationSectionVerdict.Verified(
                        sprintf
                            "%s declares %d key(s) and every one is accounted for at this boot: %d resolve from the manifest and %d are overridden by the environment, which is the documented precedence"
                            header
                            declared.Length
                            honoured.Length
                            overridden.Length
                    )
                | _, _ ->
                    // The finding the section exists for. A declared value
                    // that reaches nothing is worse than an undeclared one:
                    // the file reads as the configuration and is not.
                    VerificationSectionVerdict.Failed(
                        sprintf
                            "%s declares %d key(s) and %d of them take effect nowhere, so this deployment is not running the configuration it declares"
                            header
                            declared.Length
                            ignored.Length
                    )

            section ConfigConformanceSection title verdict findings

    /// The values a boolean acknowledgement reads as ON — exactly the set
    /// every `*FromEnv` boolean reader recognises, case-insensitively.
    ///
    /// Matching the readers is the whole requirement. A section that
    /// applied a more generous rule would list a hatch as accepted that
    /// the preflight does not honour, and one that applied a stricter rule
    /// would omit a hatch that is genuinely lowering a refusal. Either way
    /// the inventory would describe a deployment other than this one.
    let private isOnValue (value: string) : bool =
        match value.ToLowerInvariant() with
        | "1"
        | "true"
        | "yes"
        | "on" -> true
        | _ -> false

    /// What one escape hatch is doing at this boot.
    type private HatchState =
        /// Set, and reads as on — the refusal it names is lowered.
        | HatchActive of ConfigResolution.ConfigSource
        /// Set to a value that does NOT read as on. Reported rather than
        /// dropped: silence here is the operator's trap — they set the
        /// hatch, the deployment still refuses, and nothing anywhere says
        /// the value was the problem.
        | HatchSetNotInForce of source: ConfigResolution.ConfigSource * value: string
        /// No layer supplies a value. The ordinary state.
        | HatchInactive

    let private hatchState (d: ConfigKeys.ConfigKeyDescriptor) : HatchState =
        match ConfigResolution.tryResolve d.EnvVar with
        | None -> HatchInactive
        | Some(value, source) ->
            match d.Type with
            | ConfigKeys.BoolKey when not (isOnValue value) -> HatchSetNotInForce(source, value)
            // A non-boolean member of the category is in force by virtue
            // of being supplied at all; there is no "on" to read.
            | _ -> HatchActive source

    /// Accepted acknowledgements (Phase 699).
    ///
    /// **Never `Verified` and never `Failed`, and both halves are
    /// deliberate.** An inventory is not a verification — nothing here is
    /// checked, the section enumerates what the deployment has accepted.
    /// And an acknowledged hatch is an operator's decision, legitimately
    /// taken: a report that reddened on one would redden on every
    /// deployment that made a considered trade-off, and a gate that is red
    /// for making considered trade-offs is a gate that gets turned off.
    /// The section's whole job is to make the set READABLE in one place,
    /// which is what an assessor could not previously get at all.
    ///
    /// The enumerated set is the registry's own escape-hatch category, not
    /// a name-prefix match — see `ConfigKeys.EscapeHatchCategory` for why
    /// the two differ and why the category is the authority.
    let gatherAcceptedAcknowledgements () : ReportSection =
        let title = "Accepted acknowledgements"
        let hatches = ConfigKeys.escapeHatchKeys
        let states = hatches |> List.map (fun d -> d, hatchState d)

        let active =
            states
            |> List.choose (fun (d, state) ->
                match state with
                | HatchActive source -> Some(d, source)
                | _ -> None)

        let misset =
            states
            |> List.choose (fun (d, state) ->
                match state with
                | HatchSetNotInForce(source, value) -> Some(d, source, value)
                | _ -> None)

        let activeLines =
            active
            |> List.map (fun (d, source) ->
                sprintf "%s [%s]: %s" d.EnvVar (ConfigResolution.ConfigSource.label source) d.Description)

        let missetLines =
            misset
            |> List.map (fun (d, source, value) ->
                sprintf
                    "%s [%s]: SET AND NOT IN FORCE — the value %s does not read as on (recognised: 1, true, yes, on), so the refusal it would lower still stands. %s"
                    d.EnvVar
                    (ConfigResolution.ConfigSource.label source)
                    (renderValue d.IsSecret value)
                    d.Description)

        match active, misset with
        | [], [] ->
            section
                AcceptedAcknowledgementSection
                title
                (VerificationSectionVerdict.NotComposed(
                    sprintf
                        "none of the %d escape hatch(es) the configuration registry declares is set in this deployment, so no preflight refusal has been acknowledged and lowered — every refusal the preflight can raise still stands"
                        hatches.Length
                ))
                []
        | [], _ ->
            section
                AcceptedAcknowledgementSection
                title
                (VerificationSectionVerdict.Observed(
                    sprintf
                        "no escape hatch is in force at this boot, and %d of the %d the configuration registry declares are set to a value that does not read as on — each of those refusals still stands, which is unlikely to be what the operator who set them intended"
                        misset.Length
                        hatches.Length
                ))
                missetLines
        | _, _ ->
            let missetNote =
                if List.isEmpty misset then
                    ""
                else
                    sprintf ", and %d further are set to a value that does not read as on" misset.Length

            section
                AcceptedAcknowledgementSection
                title
                (VerificationSectionVerdict.Observed(
                    sprintf
                        "%d of the %d escape hatch(es) the configuration registry declares are in force at this boot%s; each names one preflight refusal this deployment has accepted rather than resolved"
                        active.Length
                        hatches.Length
                        missetNote
                ))
                (missetLines @ activeLines)

    // ─── What the report does not prove ──────────────────────────────────

    /// The not-proved statements, with the two that a composed substrate
    /// NARROWS resolved against the sections actually gathered.
    ///
    /// Narrowing is never closing. Composing Phase 684 does not delete the
    /// post-boot-mutation caveat — it shrinks it to "everything except the
    /// five enumerated grounding facets", which is a smaller and still
    /// entirely real bound, and the statement says so in the same breath.
    let notProvedFor (sections: ReportSection list) : NotProvedStatement list =
        let isComposed id =
            match sections |> List.tryFind (fun s -> s.Id = id) with
            | Some {
                       Verdict = VerificationSectionVerdict.NotComposed _
                   }
            | None -> false
            | Some _ -> true

        [
            {
                Id = "post-boot-mutation"
                Statement =
                    "The boot seal is a statement about the composition as it stood at boot. Nothing in this report proves the composition did not change afterwards; the profile does not freeze it."
                Narrowing =
                    if isComposed GroundingContinuitySection then
                        Some
                            "the five enumerated grounding facets (metric registration, subject registration, purpose declaration, canonical method, disclosure policy) are covered post-boot by the continuity chain above. The rest of the composition is not."
                    else
                        None
            }
            {
                // Phase 694. Before it, the honest statement here would
                // have been that the boot comparison was structurally
                // blind to a canonical-method flip — and the report did
                // not make it, which is the more instructive half of why
                // this entry exists. The manifest now records the selector
                // under a versioned schema, so the comparison sees it; the
                // residual bound is the age of the binding being compared
                // against, and the verdict names that itself rather than
                // resolving it as a match.
                Id = "boot-seal-covers-what-it-recorded"
                Statement =
                    "The boot comparison proves the running composition matches what the sealed binding RECORDED. A binding sealed before a declaration joined the recorded manifest cannot speak to that declaration — most consequentially a metric's canonical-method selector, which changes what an already enumerated number means without changing anything else."
                Narrowing =
                    if isComposed BootSealSection then
                        Some
                            "the manifest records canonical-method selectors under a versioned schema, so a flip between two recorded boots is a named difference; a binding too old to carry them reports 'verified-unrecorded' and names each metric, never a match. Re-sealing the binding from the running composition closes the gap for good."
                    else
                        None
            }
            {
                Id = "recorded-input-truth"
                Statement =
                    "Every check above establishes that recorded evidence is internally consistent and has not been altered since it was recorded. None of them establishes that what was recorded was true at the moment of recording."
                Narrowing = None
            }
            {
                Id = "code-never-composed"
                Statement =
                    "This report covers the substrates this deployment wired. A capability that was never composed produces no evidence and no absence anywhere except the NotComposed sections above — read those as the report's own boundary, not as a clean bill."
                Narrowing = None
            }
            {
                Id = "gate-is-a-decision-point"
                Statement =
                    "The composition gates are decision points, not a sandbox. They refuse a composition at the moment it is presented; they do not confine code that is already running, and nothing here observes runtime behaviour."
                Narrowing = None
            }
            {
                Id = "certificate-bodies-not-retained"
                Statement =
                    "An issuance log proves that a document with a given digest was issued. No certificate bodies are retained, so this report cannot re-verify the documents themselves — that requires the holder's own copy, checked against the log by digest."
                Narrowing =
                    if isComposed CertificateIssuanceSection then
                        Some
                            "the issuances above are enumerable and their digests are quotable, so a holder can prove inclusion of a document they already have."
                    else
                        None
            }
            {
                // Phase 693. `SeamAuthorityEnforcement`'s own header
                // states this bound and the statement is lifted from it
                // deliberately rather than softened: the derivation reads
                // the registrations a module DECLARES, and a Giraffe
                // handler is a closure whose reach is not enumerable. A
                // refusal is therefore sound and an admission is a subset
                // claim — and an enforcement layer believed to be a
                // sandbox would be worse than none.
                Id = "seam-reach-is-a-subset-claim"
                Statement =
                    "The seam-authority section reports the substrate each module's own registrations IMPLY it reaches. A module can still resolve substrate from the container by hand, and route handlers are closures whose reach is not enumerable, so a seam refusal is sound while an admission is a subset claim and never a proof of confinement."
                Narrowing =
                    if isComposed SeamAuthoritySection then
                        Some
                            "the section names each component's declared seam set beside the reach derived from its registrations, so the distance between what was declared and what is observable is visible rather than inferred. It does not shrink the bound: substrate resolved by hand sits outside both halves."
                    else
                        None
            }
            {
                Id = "ledger-covers-what-reached-it"
                Statement =
                    "A verified audit ledger proves that the records it holds are the records it was given, in order. It does not prove that every event which occurred reached it — a sink that was never composed, or an event emitted before the ledger was, leaves no gap the chain can see."
                Narrowing = None
            }
            {
                // Phase 699 appends, for the reason `buildReport` appends
                // its sections: the canonical form is order-sensitive, so
                // inserting among the existing statements would move the
                // lines of every statement after it and a reader diffing
                // two canonical forms across the upgrade could not tell a
                // re-ordering from a re-statement.
                //
                // The claims-discipline statement, and the one this phase
                // would be dishonest without. The conformance
                // section compares two things the resolution seam can see;
                // a reader's USE of a value is a third thing it cannot,
                // and the distance between "the declared value is the
                // effective value" and "the deployment behaved per the
                // file" is exactly that third thing.
                Id = "declared-config-is-not-observed-behaviour"
                Statement =
                    "The configuration conformance section compares what the manifest DECLARES against what the resolution seam reports as EFFECTIVE at this boot. It observes no reader consuming a value: a subsystem that read a key once at startup and cached it, one that was composed with a literal above the seam, or one that was never composed at all, is outside what this comparison can see. A conforming manifest is a statement about declared intent reaching the seam, never a statement that the deployment behaved per the file."
                Narrowing =
                    if isComposed ConfigConformanceSection then
                        Some
                            "every declared key that no reader resolves through the seam is named above as ignored, so the gap between declaration and consumption is enumerated key by key rather than assumed away. What remains unobserved is what each reader then did with the value it was handed."
                    else
                        None
            }
            {
                // Phase 699. An inventory of accepted risk invites exactly
                // one wrong reading — that the list is what was waived —
                // and the section cannot tell the difference from where it
                // stands, so the bound is stated rather than guessed at.
                Id = "hatches-are-an-inventory-not-a-waiver-record"
                Statement =
                    "The accepted-acknowledgement section names the escape hatches that are IN FORCE, not the refusals they actually lowered. A hatch set against a condition this deployment does not meet is inert and is still listed, and the section cannot say which preflight refusals would have fired without them. Read it as the set of risks this deployment has pre-accepted, not as a record of what was waived."
                Narrowing =
                    if isComposed AcceptedAcknowledgementSection then
                        Some
                            "each hatch above carries the registry's own description of the refusal it lowers and the configuration layer that set it, so what was accepted and by which lane is legible without reading the source. Whether the corresponding refusal would have fired is still not established here."
                    else
                        None
            }
            {
                // Phase 713 appends, for the reason every statement since
                // 693 has: the canonical form is order-sensitive, so
                // inserting among the existing statements would move the
                // lines of every statement after it.
                //
                // A chain invites one specific over-reading — that a
                // complete traversal proves the endpoints are TRUE — and
                // the artefact would be dishonest without saying
                // otherwise. Each hop proves that two recorded facts
                // reference each other as recorded. What produced either
                // fact is outside every link in the walk.
                Id = "chain-joins-records-not-reality"
                Statement =
                    "The evidence chain proves that recorded facts reference each other as recorded: that the deploy record names this transcript, that this closure is the one it binds, that a work record covers its sources. It does not establish that any of those records was true when it was written. A complete chain over fabricated inputs is complete, and the hop that would catch that does not exist — no traversal can."
                Narrowing =
                    if isComposed EvidenceChainSection then
                        Some
                            "every hop names the join key it resolved on, so a reader can re-derive each link from the records themselves rather than take the chain's word for it. The bound is unchanged: re-deriving a link confirms the reference, never the fact."
                    else
                        None
            }
            {
                // Phase 785 appends, for the reason every statement since
                // 693 has: the canonical form is order-sensitive.
                //
                // The decode edge invites the over-reading that a total
                // decoder makes a decoded value SAFE. It does not. The
                // combinators establish that bytes either become the
                // declared type or become a named refusal; what the
                // declared type then means — whose tenant it belongs to,
                // whether the caller may have it, whether it satisfies
                // the domain's own rules — is every other control in this
                // deployment, and none of them is narrowed by this one.
                Id = "decode-is-not-authorisation"
                Statement =
                    "The remoting decode edge establishes that client bytes become a value of the declared type or a named refusal, with bounded length and depth. It establishes nothing about that value's authorisation, tenancy or session integrity, nothing about its semantic validity, and nothing about the bytes-to-value pass itself, which is BOUNDED rather than proved. Records on the reflection fallback, and argument types with no JSON decoder registered, are outside it entirely."
                Narrowing =
                    if isComposed RemotingDecoderSection then
                        Some
                            "the section names every API record this deployment SERVES (recorded at `Api.make`, not declared by a root) and which of them decode through the closed algebra over corpus-covered shapes, so the boundary is enumerated record by record and its coverage is a ratio over the served set rather than a claim for the deployment as a whole. What it does not narrow is any of the four clauses above."
                    else
                        None
            }
            {
                // Phase 772. The egress seam sits in front of every client
                // the platform factory hands out, and only those. A client
                // constructed by hand — `new HttpClient()` in a module or a
                // companion that cannot reach the Server tier — never meets
                // the handler, and no report line can see it. Making that
                // construction a finding is the compile-time analyser's
                // subject (Phase 776), not this report's.
                Id = "egress-covers-the-platform-factory"
                Statement =
                    "The egress section governs outbound HTTP made through the platform client factory — the named IHttpClientFactory client and PlatformHttpClient — and only that. A client a module or companion constructs by hand is outside it, as is every non-HTTP egress: SMTP, message brokers, database drivers, raw sockets. A permitted call is a call the policy did not refuse; nothing here inspects what it carried."
                Narrowing =
                    if isComposed EgressSection then
                        Some
                            "the section names each component's declared origins and every refusal the handler recorded since boot, so what the factory bounds is enumerated origin by origin. What bypasses the factory is not visible here."
                    else
                        None
            }
        ]

    // ─── Assembly ────────────────────────────────────────────────────────

    /// Lowercase-hex SHA-256 over the report's canonical form. Server-side
    /// because `System.Security.Cryptography` is not Fable-compilable; the
    /// canonical form it hashes is declared in `Platform.Core`, so any host
    /// recomputes the same digest from the same report.
    let verdictDigest (sections: ReportSection list) (notProved: NotProvedStatement list) : string =
        let bytes = Encoding.UTF8.GetBytes(canonicalForm sections notProved)
        Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    /// Gather every section and assemble the report. The single composition
    /// point — the endpoint, the CI entry and the tests all reach the report
    /// through here, so there is exactly one definition of what it contains.
    let buildReport
        (evidence: IDeploymentVerificationEvidence)
        (actor: string)
        (generatedAt: DateTime)
        : Async<DeploymentVerificationReport> =
        async {
            let bootSeal = gatherBootSeal evidence
            let continuity = gatherGroundingContinuity evidence
            let! ledger = gatherLedger evidence
            let! certificates = gatherCertificates evidence
            let! answerJoins = gatherAnswerJoins evidence
            let seamAuthority = gatherSeamAuthority evidence

            // Phase 699. The two sections that take no evidence argument —
            // they read the configuration resolution seam, which is
            // ambient `Platform.Core` state installed at boot rather than
            // a substrate a composition root hands over. The header above
            // `gatherConfigConformance` gives the reasoning; the short
            // version is that a root which forgot to pass its manifest
            // would produce a report claiming the deployment declares
            // none, which is the exact failure the declared layer exists
            // to prevent.
            let configConformance = gatherConfigConformance ()
            let acknowledgements = gatherAcceptedAcknowledgements ()

            // Phase 713. The one section whose subject is a join across
            // the substrates the others read one at a time.
            let! evidenceChain = gatherEvidenceChain evidence

            // Phase 785. The one section whose subject is the decode
            // EDGE — where client bytes become typed values the rest of
            // the deployment takes as given.
            let remotingDecoders = gatherRemotingDecoders evidence

            // Phase 772. The one section whose subject is what LEAVES the
            // process — the origins the platform client factory permits
            // each component to reach, and what it refused.
            let egress = gatherEgress evidence

            // Phase 693 appends rather than inserting. Adding a section
            // moves every deployment's verdict digest once, which is
            // correct and expected — the report grew. Inserting it among
            // the five would move the SECTION LINES of the ones after it
            // too, so a reader diffing two canonical forms across the
            // upgrade could not tell a re-ordering from a re-verdict.
            // Phase 699 appends its two for the same reason, and Phase
            // 713 its one.
            let sections = [
                bootSeal
                continuity
                ledger
                certificates
                answerJoins
                seamAuthority
                configConformance
                acknowledgements
                evidenceChain
                // Phase 785 appends for the reason 693, 699 and 713 did:
                // adding a section moves every deployment's verdict digest
                // once, which is correct and expected — the report grew.
                // Inserting it among the nine would move the SECTION LINES
                // of the ones after it too, so a reader diffing two
                // canonical forms across the upgrade could not tell a
                // re-ordering from a re-verdict.
                remotingDecoders
                // Phase 772 appends for the same reason: the report grew
                // by one section, and every earlier line keeps its place.
                egress
            ]

            let notProved = notProvedFor sections

            return {
                SchemaVersion = SchemaVersion
                Actor = actor
                GeneratedAt = generatedAt
                Sections = sections
                NotProved = notProved
                Outcome = outcomeOf sections
                VerdictDigest = verdictDigest sections notProved
            }
        }

    /// Resolve the registered evidence, or `none` when the deployment
    /// registered none at all — the bare-deployment path, which must produce
    /// an honest empty report rather than an error.
    let resolveEvidence (services: IServiceProvider) : IDeploymentVerificationEvidence =
        match services.GetService(typeof<IDeploymentVerificationEvidence>) with
        | :? IDeploymentVerificationEvidence as evidence -> evidence
        | _ -> DeploymentVerificationEvidence.none

    /// The audited-read record.
    ///
    /// Awaited rather than fire-and-forget: `IAuditLog.Record` is documented
    /// best-effort and swallows its own failures, so awaiting costs nothing
    /// in the failure case — and the CI entry exits the process immediately
    /// afterwards, which would otherwise race the write. A verification that
    /// left no trace because the process was faster than its own audit sink
    /// is the one outcome this row exists to prevent.
    let recordRead (services: IServiceProvider) (report: DeploymentVerificationReport) : Async<unit> = async {
        match services.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as auditLog ->
            let payload: DeploymentVerifiedPayload = {
                Actor = report.Actor
                Outcome = DeploymentVerificationOutcome.label report.Outcome
                VerdictDigest = report.VerdictDigest
                Sections =
                    report.Sections
                    |> List.map (fun s -> sprintf "%s=%s" s.Id (VerificationSectionVerdict.label s.Verdict))
                ExitCode = exitCode report
                OccurredAt = DateTimeOffset.UtcNow
            }

            do! auditLog.Record(PlatformScopeId, DeploymentVerified payload)
        | _ -> ()
    }

    /// Build the report AND record the read. The entry both surfaces use.
    let run (services: IServiceProvider) (actor: string) : Async<DeploymentVerificationReport> = async {
        let evidence = resolveEvidence services
        let! report = buildReport evidence actor DateTime.UtcNow
        do! recordRead services report
        return report
    }

    // ─── Platform-Admin endpoint ─────────────────────────────────────────

    let private resolveAccessContext (ctx: HttpContext) : AccessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac
        | _ ->
            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            AccessContext.unrestricted (AnonymousSession userId)

    /// The actor recorded on the audited read: the resolved user id where one
    /// is present. The read is Platform-Admin-gated, so by the time the
    /// gatherer runs there always is.
    let private resolveActor (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) when not (String.IsNullOrWhiteSpace id) -> id
        | _ -> "unknown"

    /// Build the `IDeploymentVerificationApi` handler. Mirrors
    /// `DeploymentReadinessReport.deploymentReadinessApi`: the gate is
    /// `canModifyPlatformConfig`, anonymous and non-admin callers receive
    /// `Error`, and the read carries no tenant-scoped data (GP 4).
    ///
    /// **The audited row is written only past the gate.** A refused caller
    /// produces no `DeploymentVerified` row, because nothing was verified —
    /// recording refusals here would fill the trail this report's own ledger
    /// section walks with rows about reads that never happened.
    let deploymentVerificationApi (ctx: HttpContext) : IDeploymentVerificationApi =
        let accessContext = resolveAccessContext ctx

        {
            GetVerificationReport =
                fun () -> async {
                    if not (AccessContext.canModifyPlatformConfig accessContext) then
                        return Error "platform admin role required"
                    else
                        let! report = run ctx.RequestServices (resolveActor ctx)
                        return Ok report
                }
        }