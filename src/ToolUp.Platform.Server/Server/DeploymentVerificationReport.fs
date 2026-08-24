// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Security.Cryptography
open System.Text
open Microsoft.AspNetCore.Http
open ToolUp.Platform.DeploymentVerification

// ─── Phase 686 — composing the five verifiers into one report ────────
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
// them into a different shape, all five are mirrored into tier-neutral
// records and supplied together. `ServerApp.withDeploymentVerification-
// Evidence` — which compiles late enough to see both — derives the
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

/// The deployment-supplied evidence this report composes.
///
/// Every member is optional and absence is honest throughout: a
/// deployment supplying none of it gets a report of five `NotComposed`
/// sections, which is exactly what it should get.
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

[<RequireQualifiedAccess>]
module DeploymentVerificationEvidence =

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
        }

    /// Replace the grounding-continuity member, preserving every other
    /// source. The seam `ServerApp.withDeploymentVerificationEvidence`
    /// uses to fill that section in from the container without the
    /// composition root having to name it.
    let withGroundingContinuity
        (continuity: GroundingContinuityIntegrity option)
        (evidence: IDeploymentVerificationEvidence)
        : IDeploymentVerificationEvidence =
        { new IDeploymentVerificationEvidence with
            member _.BootSeal = evidence.BootSeal
            member _.GroundingContinuity = continuity
            member _.Ledger = evidence.Ledger
            member _.Certificates = evidence.Certificates
            member _.AnswerJoins = evidence.AnswerJoins
        }

/// Phase 686 — gather the five sections, fold them into the report, and
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

            let sections = [ bootSeal; continuity; ledger; certificates; answerJoins ]
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