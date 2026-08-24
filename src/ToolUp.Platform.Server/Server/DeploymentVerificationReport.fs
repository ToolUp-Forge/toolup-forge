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
// Five at Phase 686; six since Phase 693 added module seam authority.
// The section id set was declared open for exactly this — a literal per
// section rather than a closed union — so the sixth is an addition
// rather than a break, and this header counts them nowhere else.
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
        // default rather than an edge case.
        let seamAuthority = seamAuthorityOf evidence

        { new IDeploymentVerificationEvidence with
            member _.BootSeal = evidence.BootSeal
            member _.GroundingContinuity = continuity
            member _.Ledger = evidence.Ledger
            member _.Certificates = evidence.Certificates
            member _.AnswerJoins = evidence.AnswerJoins

          interface ISeamAuthorityEvidence with
              member _.SeamAuthority = seamAuthority
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
        { new IDeploymentVerificationEvidence with
            member _.BootSeal = evidence.BootSeal
            member _.GroundingContinuity = evidence.GroundingContinuity
            member _.Ledger = evidence.Ledger
            member _.Certificates = evidence.Certificates
            member _.AnswerJoins = evidence.AnswerJoins

          interface ISeamAuthorityEvidence with
              member _.SeamAuthority = seamAuthority
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

            // Phase 693 appends rather than inserting. Adding a section
            // moves every deployment's verdict digest once, which is
            // correct and expected — the report grew. Inserting it among
            // the five would move the SECTION LINES of the ones after it
            // too, so a reader diffing two canonical forms across the
            // upgrade could not tell a re-ordering from a re-verdict.
            let sections = [ bootSeal; continuity; ledger; certificates; answerJoins; seamAuthority ]
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